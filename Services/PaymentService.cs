using System.Data;
using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GoodHotel.Services;

// ผลการทำรายการเงิน
public sealed record PaymentResult(bool Succeeded, string Message, int? PaymentId = null);

// กติกาเรื่องเงินทั้งหมด: จ่าย ยืนยัน และคืนเงิน ล็อกแถวการจองก่อนทุกครั้ง
public sealed class PaymentService(GoodHotelDbContext db, TimeProvider time, ParallelReader? reader = null)
{
    // ชื่อล็อกที่กันเลขชำระเงินชนกัน
    internal const string IdLock = "GoodHotel.PaymentId";

    // ล็อกแถวการจองของ DbContext นี้
    private Task<bool> LockBookingAsync(int bookingId, CancellationToken ct) => LockBookingAsync(db, bookingId, ct);

    /// <summary>ล็อกแถวการจอง ใช้ร่วมกันทุกที่ที่เปลี่ยนการจ่ายหรือยกเลิก (ต้องอยู่ใน transaction)</summary>
    internal static async Task<bool> LockBookingAsync(GoodHotelDbContext db, int bookingId, CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Booking locking requires a transaction.");
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "SELECT `BookingId` FROM `bookings` WHERE `BookingId` = @id FOR UPDATE";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = bookingId;
        command.Parameters.Add(parameter);
        var value = await command.ExecuteScalarAsync(ct);
        return value != null && value != DBNull.Value;
    }

    /// <summary>ล็อกการจองและอ่านข้อมูลในรอบเดียว (null = ไม่มีการจองนี้)</summary>
    internal static async Task<Booking?> LockAndLoadBookingAsync(GoodHotelDbContext db, int bookingId, CancellationToken ct,
        bool track = false)
    {
        if (db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Booking locking requires a transaction.");
        // ห้ามต่อ LINQ หลัง FromSql ไม่งั้น FOR UPDATE ไปอยู่ใน subquery
        var query = db.Bookings.FromSqlInterpolated($"SELECT * FROM `bookings` WHERE `BookingId` = {bookingId} FOR UPDATE");
        return (await (track ? query : query.AsNoTracking()).ToListAsync(ct)).SingleOrDefault();
    }

    // ผลตรวจที่อ่านมาในครั้งเดียว
    private sealed record BookingChecks(bool KeyUsed, bool HasLegacy, bool HasConfirmed, bool HasPending, bool HasStay, bool HasRefund);

    /// <summary>
    /// ตรวจทุกอย่างของการจองในรอบเดียว เรียกหลังล็อกการจองแล้วเท่านั้น
    /// (ทุกคนที่เปลี่ยนการจ่าย คืนเงิน หรือเช็คอินต้องล็อกการจองก่อน ผลจึงไม่เปลี่ยนระหว่างนี้)
    /// </summary>
    private async Task<(bool ActorAllowed, BookingChecks Checks)> CheckAsync(int bookingId, int actorId, string[] roles,
        string key, CancellationToken ct)
    {
        var actorTask = reader!.Run(c => c.Users.AsNoTracking().Where(u => u.UserId == actorId)
            .Select(u => new { u.Role, u.IsActive }).SingleOrDefaultAsync(ct));
        var checksTask = reader.Run(c => c.Bookings.Where(b => b.BookingId == bookingId).Select(b => new BookingChecks(
            c.PaymentEvents.Any(e => e.IdempotencyKey == key),
            c.Payments.Any(p => p.BookingId == b.BookingId && p.SubmittedAtUtc == null &&
                p.VerifiedAtUtc == null && p.LegacyDisposition == null),
            c.Payments.Any(p => p.BookingId == b.BookingId && p.VerifiedAtUtc != null),
            c.Payments.Any(p => p.BookingId == b.BookingId && p.SubmittedAtUtc != null &&
                (p.PaymentStatus == "Pending" || p.PaymentStatus == "Submitted")),
            c.BookingStays.Any(s => s.BookingId == b.BookingId),
            c.PaymentRefunds.Any(r => r.Payment.BookingId == b.BookingId))).SingleAsync(ct));
        await Task.WhenAll(actorTask, checksTask);
        var actor = actorTask.Result;
        return (actor is { IsActive: true } && roles.Contains(actor.Role, StringComparer.Ordinal), checksTask.Result);
    }

    // ตรวจรหัสกันกดซ้ำ
    private static string? KeyError(string key, bool used) =>
        !ValidKey(key) ? "คำขอไม่ถูกต้อง กรุณาโหลดหน้าใหม่" : used ? "คำขอนี้ถูกดำเนินการไปแล้ว" : null;

    // รหัสกันกดซ้ำต้องเป็น GUID
    private static bool ValidKey(string key) => Guid.TryParseExact(key, "N", out _);
    // สร้างผลไม่สำเร็จ
    private static PaymentResult Error(string message) => new(false, message);
    // สร้างผลสำเร็จ
    private static PaymentResult Success(int id, string message) => new(true, message, id);

    // คนทำมี role ที่อนุญาตไหม
    private async Task<bool> ActorHasRoleAsync(int actorId, string[] roles, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.UserId == actorId)
            .Select(u => new { u.Role, u.IsActive }).SingleOrDefaultAsync(ct);
        return user != null && user.IsActive && roles.Contains(user.Role, StringComparer.Ordinal);
    }

    // รหัสนี้เคยใช้แล้วหรือยัง
    private async Task<string?> ValidateKeyAsync(string key, CancellationToken ct)
    {
        if (!ValidKey(key)) return "คำขอไม่ถูกต้อง กรุณาโหลดหน้าใหม่";
        return await db.PaymentEvents.AnyAsync(e => e.IdempotencyKey == key, ct)
            ? "คำขอนี้ถูกดำเนินการไปแล้ว" : null;
    }

    // บันทึกประวัติว่าใครทำอะไร
    private void AddEvent(Payment payment, int actorId, string eventType, string key,
        decimal? amount, string? reason = null)
    {
        payment.Events.Add(new PaymentEvent
        {
            ActorUserId = actorId, EventType = eventType, OccurredAtUtc = time.GetUtcNow().UtcDateTime,
            Amount = amount, PaymentMethod = payment.PaymentMethod, Reason = reason, IdempotencyKey = key
        });
    }

    // มีข้อมูลเก่าที่ยังไม่ตรวจไหม
    private async Task<bool> HasUnreconciledLegacyAsync(int bookingId, CancellationToken ct) =>
        await db.Payments.AnyAsync(p => p.BookingId == bookingId && p.SubmittedAtUtc == null &&
            p.VerifiedAtUtc == null && p.LegacyDisposition == null, ct);

    // จ่ายแล้วหรือยัง
    private async Task<bool> HasConfirmedAsync(int bookingId, CancellationToken ct) =>
        await db.Payments.AnyAsync(p => p.BookingId == bookingId && p.VerifiedAtUtc != null, ct);

    // ยอดเงินถูกต้องไหม
    private static bool ValidAmount(decimal? amount) =>
        amount.HasValue && amount > 0 && amount <= 99999999.99m && decimal.Round(amount.Value, 2) == amount;

    // จ่ายครบเต็มจำนวนไหม
    private static bool IsFullPayment(decimal? bookingAmount, decimal? recordedAmount, decimal? received) =>
        ValidAmount(bookingAmount) && recordedAmount == bookingAmount && received == bookingAmount;

    /// <summary>ขอล็อกเลขที่ชำระเงินและอ่านเลขล่าสุดในรอบเดียว (null = รอล็อกไม่ไหว) onLocked เรียกทันทีที่ได้ล็อก</summary>
    private async Task<int?> LockPaymentIdAsync(Action onLocked, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "SELECT GET_LOCK(@name, 15); SELECT COALESCE(MAX(`PaymentId`), 0) FROM `payments`";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.Value = IdLock;
        command.Parameters.Add(parameter);
        // ไม่ใช้ ct: ถ้ายกเลิกกลางทางจะไม่รู้ว่าได้ล็อกแล้วหรือยัง
        await using var result = await command.ExecuteReaderAsync(CancellationToken.None);
        await result.ReadAsync(CancellationToken.None);
        int? locked = result.IsDBNull(0) ? null : Convert.ToInt32(result.GetValue(0));
        if (locked == 1) onLocked();
        else if (locked != 0) throw new InvalidOperationException("Could not acquire the payment ID lock.");
        await result.NextResultAsync(CancellationToken.None);
        await result.ReadAsync(CancellationToken.None);
        var lastId = Math.Max(0, Convert.ToInt32(result.GetValue(0)));
        ct.ThrowIfCancellationRequested();
        if (locked == 0) return null;
        if (lastId == int.MaxValue) throw new InvalidOperationException("Payment IDs are exhausted.");
        return lastId;
    }

    // ปลดล็อกเลขชำระเงิน
    private async Task ReleasePaymentIdAsync()
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT RELEASE_LOCK(@name)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.Value = IdLock;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync(CancellationToken.None);
        if (result == null || result == DBNull.Value || Convert.ToInt32(result) != 1)
            throw new InvalidOperationException("Could not release the payment ID lock.");
    }

    /// <summary>
    /// การจองนี้จ่ายเงินแล้วหรือยัง: มีการชำระที่ยืนยันแล้ว และยังไม่ได้คืนเงินเต็มจำนวน
    /// ใช้เป็นเงื่อนไขก่อนเช็คอิน และตัดสินว่า Staff ยกเลิกการจองแทนลูกค้าได้หรือไม่
    /// </summary>
    public static async Task<bool> IsPaidAsync(GoodHotelDbContext db, int bookingId, CancellationToken ct)
    {
        var payment = await db.Payments.AsNoTracking()
            .Where(p => p.BookingId == bookingId && p.VerifiedAtUtc != null)
            .OrderBy(p => p.PaymentId)
            .Select(p => new
            {
                p.Amount,
                Refunded = p.Refunds.Where(r => r.Status == "Approved").Sum(r => (decimal?)r.Amount) ?? 0m
            })
            .FirstOrDefaultAsync(ct);

        return payment != null && payment.Amount > payment.Refunded;
    }

    public const string PaidLabel = "จ่ายแล้ว";
    public const string RefundedLabel = "คืนเงินแล้ว";

    /// <summary>
    /// สถานะการจ่ายเงินของหลายการจองในครั้งเดียว (query เดียว)
    /// ใช้ในหน้ารายการ แทนการถามทีละแถว ซึ่งช้ามากเพราะฐานข้อมูลอยู่ไกล
    /// กติกาเหมือน IsPaidAsync ทุกอย่าง
    /// </summary>
    public static async Task<Dictionary<int, string>> PaymentLabelsAsync(GoodHotelDbContext db,
        IReadOnlyCollection<int> bookingIds, CancellationToken ct)
    {
        if (bookingIds.Count == 0) return [];

        var label = await PaymentLabelLookupAsync(db.Payments.AsNoTracking()
            .Where(p => p.BookingId != null && bookingIds.Contains(p.BookingId.Value)), ct);
        return bookingIds.Distinct().ToDictionary(id => id, label);
    }

    /// <summary>
    /// สถานะการจ่ายจากรายการชำระที่กรองมาแล้ว (กรองด้วย subquery ได้ จึงไม่ต้องรอรายการจองก่อน)
    /// การจองที่ไม่มีรายการชำระ = ยังไม่จ่าย
    /// </summary>
    public static async Task<Func<int, string>> PaymentLabelLookupAsync(IQueryable<Payment> payments, CancellationToken ct)
    {
        var rows = await payments
            .Where(p => p.BookingId != null)
            .Select(p => new
            {
                BookingId = p.BookingId!.Value,
                p.PaymentId,
                Verified = p.VerifiedAtUtc != null,
                p.Amount,
                Refunded = p.Refunds.Where(r => r.Status == "Approved").Sum(r => (decimal?)r.Amount) ?? 0m,
                Pending = p.SubmittedAtUtc != null && p.VerifiedAtUtc == null &&
                    (p.PaymentStatus == "Pending" || p.PaymentStatus == "Submitted")
            })
            .ToListAsync(ct);

        var byBooking = rows.ToLookup(r => r.BookingId);
        return id =>
        {
            var found = byBooking[id].ToList();
            var firstVerified = found.Where(p => p.Verified).MinBy(p => p.PaymentId);
            if (firstVerified != null && firstVerified.Amount > firstVerified.Refunded) return PaidLabel;
            if (firstVerified != null && firstVerified.Refunded > 0) return RefundedLabel;
            return found.Any(p => p.Pending) ? "รอตรวจสอบการชำระเงิน" : "ยังไม่จ่าย";
        };
    }

    // เป็นวิธีจ่ายแบบจำลองไหม (โปรเจคนี้ไม่มีการโอนเงินจริง)
    public static bool IsMockMethod(string? method) =>
        method is "Bank Transfer (Mock)" or "PromptPay (Mock)" or "PayPal (Mock)";

    // รายการที่นับเป็นรายได้ (การจองละ 1 รายการ)
    public static IQueryable<Payment> ConfirmedForRevenue(IQueryable<Payment> payments) =>
        payments.Where(payment => payment.VerifiedAtUtc != null &&
            (payment.BookingId == null || !payments.Any(other =>
                other.BookingId == payment.BookingId && other.VerifiedAtUtc != null &&
                other.PaymentId < payment.PaymentId)));

    // สร้างแถวชำระเงินใหม่
    private Payment CreateSubmission(int paymentId, int bookingId, int actorId, string method, decimal? amount, string key)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var isMock = IsMockMethod(method);
        var payment = new Payment
        {
            PaymentId = paymentId, BookingId = bookingId, Amount = amount,
            PaymentMethod = method, PaymentStatus = isMock ? "Paid" : "Pending",
            SubmittedAtUtc = now, SubmittedByUserId = actorId,
            VerifiedAtUtc = isMock ? now : null,
            VerifiedByUserId = isMock ? actorId : null,
            PaymentDate = isMock ? RoomAvailabilityService.HotelDate(now) : null
        };
        AddEvent(payment, actorId, isMock ? "MockConfirmed" : "Submission", key, amount,
            isMock ? "DEMO only; no real payment was transferred." : null);
        return payment;
    }

    // ลูกค้าชำระเงิน: ล็อกการจอง → ตรวจเงื่อนไข → บันทึก
    public async Task<PaymentResult> SubmitAsync(int bookingId, int actorId, string method, string key,
        CancellationToken ct)
    {
        // จองออนไลน์: ลูกค้าชำระผ่านช่องทางออนไลน์ (จำลอง) เท่านั้น ไม่มีจ่ายเงินสดที่โรงแรม
        // (เงินสดมีเฉพาะลูกค้า Walk-in ที่พนักงานรับที่เคาน์เตอร์ ดู BookingService.CreateAsync)
        if (bookingId <= 0 || actorId <= 0 || !IsMockMethod(method))
            return Error("กรุณาเลือกวิธีชำระเงินออนไลน์");

        await db.Database.OpenConnectionAsync(ct);
        var idLocked = false;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var booking = await LockAndLoadBookingAsync(db, bookingId, ct);
            if (booking == null) return Error("ไม่พบการจอง");
            var (allowed, checks) = await CheckAsync(bookingId, actorId, ["Customer"], key, ct);
            if (!allowed || booking.UserId != actorId)
                return Error("คุณชำระเงินการจองนี้ไม่ได้");
            if (booking.Status == BookingStatuses.Cancelled)
                return Error("การจองนี้ถูกยกเลิกแล้ว");
            // เลยวันออกแล้ว (แขกไม่ได้มาพัก): ไม่รับชำระ ไม่งั้นลูกค้าจ่ายเงินให้คืนที่ผ่านไปแล้ว
            if (booking.CheckOut <= RoomAvailabilityService.HotelDate(time.GetUtcNow().UtcDateTime))
                return Error("เลยวันเข้าพักของการจองนี้แล้ว ชำระเงินไม่ได้");
            var keyError = KeyError(key, checks.KeyUsed);
            if (keyError != null) return Error(keyError);
            if (!ValidAmount(booking.Amount)) return Error("ยอดเงินของการจองนี้ต้องตรวจสอบก่อน");
            if (checks.HasLegacy)
                return Error("มีข้อมูลการชำระเงินเก่าที่ต้องตรวจสอบก่อนชำระเงิน");
            if (checks.HasConfirmed) return Error("การจองนี้ชำระเงินแล้ว");
            if (checks.HasPending)
                return Error("มีการชำระเงินที่รอยืนยันอยู่แล้ว");

            // เลยกำหนดเวลาชำระแล้ว (ระบบยังไม่ทันยกเลิกให้): ยกเลิกตอนนี้เลย ไม่รับชำระ
            var now = time.GetUtcNow().UtcDateTime;
            if (PaymentDeadline.DueAtUtc(booking.CreatedAtUtc) < now)
            {
                await db.Bookings.Where(b => b.BookingId == bookingId && b.Status == BookingStatuses.Booked)
                    .ExecuteUpdateAsync(b => b
                        .SetProperty(x => x.Status, BookingStatuses.Cancelled)
                        .SetProperty(x => x.CancelledAtUtc, now)
                        .SetProperty(x => x.CancelReason, CancelReasons.PaymentTimeout), ct);
                await tx.CommitAsync(ct);
                return Error("หมดเวลาชำระเงินแล้ว การจองนี้ถูกยกเลิก กรุณาจองใหม่");
            }

            if (await LockPaymentIdAsync(() => idLocked = true, ct) is not { } lastId)
                return Error("ระบบกำลังบันทึกการชำระเงินอื่น กรุณาลองใหม่");
            var payment = CreateSubmission(lastId + 1, bookingId, actorId, method, booking.Amount, key);
            db.Payments.Add(payment);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Success(payment.PaymentId, "ชำระเงินจำลองสำเร็จ (ไม่มีการโอนเงินจริง)");
        }
        finally
        {
            try
            {
                if (idLocked) await ReleasePaymentIdAsync();
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    /// <summary>
    /// เงินสดที่พนักงานต้อนรับรับที่เคาน์เตอร์ (ลูกค้า Walk-in) ยืนยันแล้วทันทีเพราะพนักงานรับเงินเอง
    /// </summary>
    internal static Payment DeskCash(int paymentId, int bookingId, int staffId, decimal amount, DateTime now)
    {
        var payment = new Payment
        {
            PaymentId = paymentId, BookingId = bookingId, Amount = amount, PaymentMethod = "Cash", PaymentStatus = "Paid",
            PaymentDate = RoomAvailabilityService.HotelDate(now), SubmittedAtUtc = now, SubmittedByUserId = staffId,
            VerifiedAtUtc = now, VerifiedByUserId = staffId
        };
        payment.Events.Add(new PaymentEvent
        {
            ActorUserId = staffId, EventType = "DeskCashReceived", OccurredAtUtc = now, Amount = amount,
            PaymentMethod = payment.PaymentMethod, Reason = "รับเงินสดที่เคาน์เตอร์",
            IdempotencyKey = Guid.NewGuid().ToString("N")
        });
        return payment;
    }

    // ยืนยันหรือปฏิเสธการชำระที่รอตรวจ
    public async Task<PaymentResult> ReviewAsync(int paymentId, int actorId, bool confirm, decimal? received,
        string? reason, string key, CancellationToken ct)
    {
        if (paymentId <= 0 || actorId <= 0) return Error("ไม่พบการชำระเงิน");
        if (reason?.Length > 2000 || (!confirm && string.IsNullOrWhiteSpace(reason)))
            return Error("กรุณาระบุเหตุผลที่ปฏิเสธ (ไม่เกิน 2000 ตัวอักษร)");
        var bookingId = await db.Payments.AsNoTracking().Where(p => p.PaymentId == paymentId)
            .Select(p => p.BookingId).SingleOrDefaultAsync(ct);
        if (!bookingId.HasValue) return Error("การชำระเงินนี้ไม่มีการจองที่เกี่ยวข้อง");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (!await ActorHasRoleAsync(actorId, ["Staff", "Accountant"], ct))
            return Error("คุณตรวจสอบการชำระเงินนี้ไม่ได้");
        if (!await LockBookingAsync(bookingId.Value, ct)) return Error("ไม่พบการจอง");
        var booking = await db.Bookings.AsNoTracking().SingleAsync(b => b.BookingId == bookingId, ct);
        var payment = await db.Payments.SingleOrDefaultAsync(p => p.PaymentId == paymentId && p.BookingId == bookingId, ct);
        if (payment == null || payment.SubmittedAtUtc == null || payment.VerifiedAtUtc != null ||
            payment.PaymentStatus is not ("Pending" or "Submitted"))
            return Error("การชำระเงินนี้ไม่ได้รอตรวจสอบแล้ว");
        if (!((payment.PaymentMethod == "Cash" && payment.PaymentStatus == "Pending") ||
              (payment.PaymentMethod is "Bank Transfer" or "PromptPay" && payment.PaymentStatus == "Submitted" &&
               payment.EvidenceFileName != null && payment.EvidenceSha256 != null)))
            return Error("ต้องตรวจสอบวิธีชำระเงินหรือหลักฐานก่อน");
        var keyError = await ValidateKeyAsync(key, ct);
        if (keyError != null) return Error(keyError);
        if (await HasUnreconciledLegacyAsync(bookingId.Value, ct))
            return Error("มีข้อมูลการชำระเงินเก่าที่ต้องตรวจสอบก่อน");
        if (confirm)
        {
            if (booking.Status == BookingStatuses.Cancelled)
                return Error("การจองนี้ถูกยกเลิกแล้ว ให้กดปฏิเสธการชำระเงินแทน");
            if (!IsFullPayment(booking.Amount, payment.Amount, received))
                return Error("จำนวนเงินที่ได้รับต้องเท่ากับยอดการจองเต็มจำนวน");
            if (await HasConfirmedAsync(bookingId.Value, ct)) return Error("การจองนี้ชำระเงินแล้ว");
            payment.PaymentStatus = "Paid";
            payment.VerifiedAtUtc = time.GetUtcNow().UtcDateTime;
            payment.VerifiedByUserId = actorId;
            payment.PaymentDate = RoomAvailabilityService.HotelDate(payment.VerifiedAtUtc.Value);
        }
        else
        {
            payment.PaymentStatus = "Rejected";
        }
        AddEvent(payment, actorId, confirm ? "Confirmation" : "Rejection", key,
            confirm ? received : payment.Amount, reason?.Trim());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Success(paymentId, confirm ? "ยืนยันการชำระเงินแล้ว" : "ปฏิเสธการชำระเงินแล้ว");
    }

    // บัญชีตรวจข้อมูลเก่า
    public async Task<PaymentResult> ReconcileAsync(int paymentId, int actorId, bool confirmed,
        string? reason, string key, CancellationToken ct)
    {
        if (paymentId <= 0 || actorId <= 0 || string.IsNullOrWhiteSpace(reason) || reason.Length > 2000)
            return Error("กรุณาระบุเหตุผลในการตรวจสอบ");
        var bookingId = await db.Payments.AsNoTracking().Where(p => p.PaymentId == paymentId)
            .Select(p => p.BookingId).SingleOrDefaultAsync(ct);
        if (!bookingId.HasValue) return Error("ข้อมูลเก่านี้ไม่มีการจอง ต้องตรวจสอบเอง");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (!await ActorHasRoleAsync(actorId, ["Accountant"], ct)) return Error("เฉพาะฝ่ายบัญชีที่ตรวจข้อมูลเก่าได้");
        if (!await LockBookingAsync(bookingId.Value, ct)) return Error("ไม่พบการจอง");
        var booking = await db.Bookings.AsNoTracking().SingleAsync(b => b.BookingId == bookingId, ct);
        var payment = await db.Payments.SingleOrDefaultAsync(p => p.PaymentId == paymentId && p.BookingId == bookingId, ct);
        if (payment == null || payment.SubmittedAtUtc != null || payment.VerifiedAtUtc != null ||
            payment.LegacyDisposition != null) return Error("ข้อมูลเก่านี้ตรวจสอบไปแล้ว");
        var keyError = await ValidateKeyAsync(key, ct);
        if (keyError != null) return Error(keyError);
        if (confirmed)
        {
            if (!ValidAmount(booking.Amount) || payment.Amount != booking.Amount)
                return Error("ยอดเงินในข้อมูลเก่าต้องเท่ากับยอดการจองจึงจะยืนยันได้");
            if (await HasConfirmedAsync(bookingId.Value, ct)) return Error("การจองนี้มีการชำระเงินที่ยืนยันแล้ว");
            payment.VerifiedAtUtc = time.GetUtcNow().UtcDateTime;
            payment.VerifiedByUserId = actorId;
        }
        payment.LegacyDisposition = confirmed ? "Confirmed" : "Excluded";
        AddEvent(payment, actorId, confirmed ? "LegacyConfirmed" : "LegacyExcluded", key, payment.Amount, reason.Trim());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Success(paymentId, "ตรวจสอบข้อมูลเก่าแล้ว");
    }

    // ---------------- คำขอคืนเงิน ----------------
    // กติกา:
    // - ลูกค้าขอได้เฉพาะการจองของตัวเองที่จ่ายแล้ว ยังไม่เช็คอิน ยังไม่เลยวันออก ขอได้ครั้งเดียว (ถูกปฏิเสธแล้วขอใหม่ไม่ได้)
    // - คืนเต็มจำนวนที่จ่ายมา ไม่มีคืนบางส่วน
    // - ฝ่ายบัญชีอนุมัติ = บันทึกการคืนเงิน + ยกเลิกการจองให้อัตโนมัติ (ห้องว่างทันที) ในธุรกรรมเดียว
    // - ฝ่ายบัญชีปฏิเสธ = ต้องใส่เหตุผล การจองยังอยู่ ลูกค้าเลือกมาพักตามปกติ หรือยกเลิกเองแบบไม่คืนเงิน
    // - ถ้าเช็คอินระหว่างรออนุมัติ คำขอจะถูกปิดอัตโนมัติ (CloseOpenRefundsAsync)

    public const string RefundRequested = "Requested";
    public const string RefundApproved = "Approved";
    public const string RefundRejected = "Rejected";
    public const string RefundClosed = "Closed";

    public static readonly string[] RefundReasons =
    [
        "เปลี่ยนแผนการเดินทาง",
        "ติดธุระ/เหตุฉุกเฉิน",
        "เจ็บป่วย",
        "จองผิดวันหรือผิดห้อง",
        "อื่นๆ"
    ];

    /// <summary>ลูกค้าขอคืนเงินได้หรือไม่ (ใช้ทั้งตอนแสดงปุ่มและตอนตรวจซ้ำฝั่ง server)</summary>
    public static bool CanRequestRefund(string? bookingStatus, DateOnly? checkOut, bool hasStay, bool isPaid,
        bool hasAnyRefundRecord, DateOnly today) =>
        bookingStatus == BookingStatuses.Booked && !hasStay && isPaid && !hasAnyRefundRecord &&
        checkOut.HasValue && checkOut.Value > today;

    // ลูกค้าขอคืนเงิน
    public async Task<PaymentResult> RequestRefundAsync(int bookingId, int actorId, string? reason,
        string key, CancellationToken ct)
    {
        reason = reason?.Trim();
        if (bookingId <= 0 || actorId <= 0 || string.IsNullOrWhiteSpace(reason) || reason.Length > 2000)
            return Error("กรุณาเลือกหรือกรอกเหตุผลที่ขอคืนเงิน");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var booking = await LockAndLoadBookingAsync(db, bookingId, ct);
        if (booking == null) return Error("ไม่พบการจอง");
        var checkTask = CheckAsync(bookingId, actorId, ["Customer"], key, ct);
        var payment = await db.Payments.Where(p => p.BookingId == bookingId && p.VerifiedAtUtc != null)
            .OrderBy(p => p.PaymentId).FirstOrDefaultAsync(ct);
        var (allowed, checks) = await checkTask;
        if (!allowed || booking.UserId != actorId) return Error("คุณขอคืนเงินการจองนี้ไม่ได้");
        if (payment == null || !ValidAmount(payment.Amount)) return Error("การจองนี้ยังไม่มีการชำระเงินที่ยืนยันแล้ว");
        var today = RoomAvailabilityService.HotelDate(time.GetUtcNow().UtcDateTime);
        if (!CanRequestRefund(booking.Status, booking.CheckOut, checks.HasStay, true, checks.HasRefund, today))
        {
            return Error(checks.HasRefund ? "การจองนี้เคยขอคืนเงินไปแล้ว ขอซ้ำไม่ได้"
                : checks.HasStay ? "เช็คอินแล้ว ขอคืนเงินไม่ได้"
                : "การจองนี้ขอคืนเงินไม่ได้แล้ว");
        }
        var keyError = KeyError(key, checks.KeyUsed);
        if (keyError != null) return Error(keyError);
        db.PaymentRefunds.Add(new PaymentRefund
        {
            PaymentId = payment.PaymentId, Amount = payment.Amount!.Value, Reason = reason, Status = RefundRequested,
            RequestedByUserId = actorId, RequestedAtUtc = time.GetUtcNow().UtcDateTime
        });
        AddEvent(payment, actorId, "RefundRequested", key, payment.Amount, reason);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Success(payment.PaymentId, "ส่งคำขอคืนเงินแล้ว รอฝ่ายบัญชีพิจารณา ระหว่างนี้การจองยังอยู่ตามเดิม");
    }

    // บัญชีอนุมัติ: คืนเงิน + ยกเลิกการจองพร้อมกัน
    public async Task<PaymentResult> ApproveRefundAsync(int refundId, int actorId, string key, CancellationToken ct)
    {
        if (refundId <= 0 || actorId <= 0) return Error("ไม่พบคำขอคืนเงิน");
        var bookingId = await db.PaymentRefunds.AsNoTracking().Where(r => r.RefundId == refundId)
            .Select(r => r.Payment.BookingId).SingleOrDefaultAsync(ct);
        if (!bookingId.HasValue) return Error("ไม่พบคำขอคืนเงินหรือการจอง");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var booking = await LockAndLoadBookingAsync(db, bookingId.Value, ct, track: true);
        if (booking == null) return Error("ไม่พบการจอง");
        var checkTask = CheckAsync(bookingId.Value, actorId, ["Accountant"], key, ct);
        var refund = await db.PaymentRefunds.Include(r => r.Payment)
            .SingleOrDefaultAsync(r => r.RefundId == refundId && r.Payment.BookingId == bookingId, ct);
        var (allowed, checks) = await checkTask;
        if (!allowed) return Error("เฉพาะฝ่ายบัญชีที่อนุมัติการคืนเงินได้");
        if (refund == null || refund.Status != RefundRequested || refund.Payment.VerifiedAtUtc == null ||
            !ValidAmount(refund.Payment.Amount)) return Error("คำขอคืนเงินนี้ไม่ได้รออนุมัติแล้ว");
        if (booking.Status != BookingStatuses.Booked || checks.HasStay)
            return Error("การจองนี้เช็คอินหรือถูกยกเลิกไปแล้ว อนุมัติคืนเงินไม่ได้");
        var keyError = KeyError(key, checks.KeyUsed);
        if (keyError != null) return Error(keyError);

        var now = time.GetUtcNow().UtcDateTime;
        refund.Status = RefundApproved;
        refund.ApprovedAtUtc = now;
        refund.ApprovedByUserId = actorId;
        if (refund.Amount == refund.Payment.Amount && refund.Payment.SubmittedAtUtc != null)
            refund.Payment.PaymentStatus = "Refunded";
        booking.Status = BookingStatuses.Cancelled;
        booking.CancelledAtUtc = now;
        booking.CancelReason = CancelReasons.Refund;
        AddEvent(refund.Payment, actorId, "RefundApproved", key, refund.Amount, refund.Reason);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Success(refund.PaymentId, (IsMockMethod(refund.Payment.PaymentMethod)
            ? "อนุมัติคืนเงินแล้ว (จำลอง ไม่มีการโอนเงินจริง)" : "อนุมัติคืนเงินแล้ว") +
            $" การจอง #{bookingId} ถูกยกเลิกและห้องว่างให้จองแล้ว");
    }

    // บัญชีปฏิเสธ พร้อมเหตุผล
    public async Task<PaymentResult> RejectRefundAsync(int refundId, int actorId, string? reason,
        string key, CancellationToken ct)
    {
        reason = reason?.Trim();
        if (refundId <= 0 || actorId <= 0 || string.IsNullOrWhiteSpace(reason) || reason.Length > 2000)
            return Error("กรุณาระบุเหตุผลที่ปฏิเสธ ลูกค้าจะเห็นเหตุผลนี้");
        var bookingId = await db.PaymentRefunds.AsNoTracking().Where(r => r.RefundId == refundId)
            .Select(r => r.Payment.BookingId).SingleOrDefaultAsync(ct);
        if (!bookingId.HasValue) return Error("ไม่พบคำขอคืนเงินหรือการจอง");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (!await LockBookingAsync(bookingId.Value, ct)) return Error("ไม่พบการจอง");
        var checkTask = CheckAsync(bookingId.Value, actorId, ["Accountant"], key, ct);
        var refund = await db.PaymentRefunds.Include(r => r.Payment)
            .SingleOrDefaultAsync(r => r.RefundId == refundId && r.Payment.BookingId == bookingId, ct);
        var (allowed, checks) = await checkTask;
        if (!allowed) return Error("เฉพาะฝ่ายบัญชีที่ตรวจคำขอคืนเงินได้");
        if (refund == null || refund.Status != RefundRequested) return Error("คำขอคืนเงินนี้ไม่ได้รออนุมัติแล้ว");
        var keyError = KeyError(key, checks.KeyUsed);
        if (keyError != null) return Error(keyError);
        refund.Status = RefundRejected;
        // เหตุผลที่ปฏิเสธเก็บใน payment_events (RefundRejected) ไม่ต้องเพิ่มคอลัมน์ใหม่
        AddEvent(refund.Payment, actorId, "RefundRejected", key, refund.Amount, reason);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Success(refund.PaymentId, "ปฏิเสธคำขอคืนเงินแล้ว");
    }

    /// <summary>
    /// เช็คอินแล้ว: ปิดคำขอคืนเงินที่ยังรออยู่ (มาพักแล้วไม่ควรได้เงินคืน) และบันทึกในประวัติการชำระเงิน
    /// เรียกภายในธุรกรรมเช็คอิน
    /// </summary>
    public static async Task CloseOpenRefundsAsync(GoodHotelDbContext db, int bookingId, int actorId, DateTime nowUtc,
        CancellationToken ct)
    {
        var open = await db.PaymentRefunds.Include(r => r.Payment)
            .Where(r => r.Payment.BookingId == bookingId && r.Status == RefundRequested).ToListAsync(ct);
        foreach (var refund in open)
        {
            refund.Status = RefundClosed;
            refund.Payment.Events.Add(new PaymentEvent
            {
                ActorUserId = actorId, EventType = "RefundClosed", OccurredAtUtc = nowUtc, Amount = refund.Amount,
                PaymentMethod = refund.Payment.PaymentMethod, Reason = "ลูกค้าเช็คอินแล้ว คำขอคืนเงินถูกปิดอัตโนมัติ",
                IdempotencyKey = Guid.NewGuid().ToString("N")
            });
        }
    }
}
