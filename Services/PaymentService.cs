using System.Data;
using System.Data.Common;
using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GoodHotel.Services;

public sealed record PaymentResult(bool Succeeded, string Message, int? PaymentId = null);

public sealed class PaymentService(GoodHotelDbContext db, TimeProvider time)
{
    private const string IdLock = "GoodHotel.PaymentId";

    private async Task<bool> LockBookingAsync(int bookingId, CancellationToken ct)
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

    private static bool ValidKey(string key) => Guid.TryParseExact(key, "N", out _);
    private static PaymentResult Error(string message) => new(false, message);
    private static PaymentResult Success(int id, string message) => new(true, message, id);

    private async Task<bool> ActorHasRoleAsync(int actorId, string[] roles, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.UserId == actorId)
            .Select(u => new { u.Role, u.IsActive }).SingleOrDefaultAsync(ct);
        return user != null && user.IsActive && roles.Contains(user.Role, StringComparer.Ordinal);
    }

    private async Task<string?> ValidateKeyAsync(string key, CancellationToken ct)
    {
        if (!ValidKey(key)) return "คำขอไม่ถูกต้อง กรุณาโหลดหน้าใหม่";
        return await db.PaymentEvents.AnyAsync(e => e.IdempotencyKey == key, ct)
            ? "คำขอนี้ถูกดำเนินการไปแล้ว" : null;
    }

    private void AddEvent(Payment payment, int actorId, string eventType, string key,
        decimal? amount, string? reason = null)
    {
        payment.Events.Add(new PaymentEvent
        {
            ActorUserId = actorId, EventType = eventType, OccurredAtUtc = time.GetUtcNow().UtcDateTime,
            Amount = amount, PaymentMethod = payment.PaymentMethod, Reason = reason, IdempotencyKey = key
        });
    }

    private async Task<bool> HasUnreconciledLegacyAsync(int bookingId, CancellationToken ct) =>
        await db.Payments.AnyAsync(p => p.BookingId == bookingId && p.SubmittedAtUtc == null &&
            p.VerifiedAtUtc == null && p.LegacyDisposition == null, ct);

    private async Task<bool> HasConfirmedAsync(int bookingId, CancellationToken ct) =>
        await db.Payments.AnyAsync(p => p.BookingId == bookingId && p.VerifiedAtUtc != null, ct);

    private static bool ValidAmount(decimal? amount) =>
        amount.HasValue && amount > 0 && amount <= 99999999.99m && decimal.Round(amount.Value, 2) == amount;

    private static bool IsFullPayment(decimal? bookingAmount, decimal? recordedAmount, decimal? received) =>
        ValidAmount(bookingAmount) && recordedAmount == bookingAmount && received == bookingAmount;

    private static bool RefundWithinLimit(decimal? paid, decimal approved, decimal requested) =>
        ValidAmount(paid) && approved >= 0 && approved <= paid &&
        ValidAmount(requested) && requested <= paid - approved;

    private async Task<int?> LockPaymentIdAsync(CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "SELECT GET_LOCK(@name, 5)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.Value = IdLock;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync(ct);
        return result == null || result == DBNull.Value ? null : Convert.ToInt32(result);
    }

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

    /// <summary>สถานะการจ่ายเงินแบบสั้นๆ สำหรับหน้า Staff</summary>
    public static async Task<string> PaymentLabelAsync(GoodHotelDbContext db, int bookingId, CancellationToken ct)
    {
        if (await IsPaidAsync(db, bookingId, ct)) return PaidLabel;

        var hasPending = await db.Payments.AnyAsync(p => p.BookingId == bookingId && p.SubmittedAtUtc != null &&
            p.VerifiedAtUtc == null && (p.PaymentStatus == "Pending" || p.PaymentStatus == "Submitted"), ct);
        return hasPending ? "รอตรวจสอบการชำระเงิน" : "ยังไม่จ่าย";
    }

    public static bool IsMockMethod(string? method) =>
        method is "Bank Transfer (Mock)" or "PromptPay (Mock)" or "PayPal (Mock)";

    public static IQueryable<Payment> ConfirmedForRevenue(IQueryable<Payment> payments) =>
        payments.Where(payment => payment.VerifiedAtUtc != null &&
            (payment.BookingId == null || !payments.Any(other =>
                other.BookingId == payment.BookingId && other.VerifiedAtUtc != null &&
                other.PaymentId < payment.PaymentId)));

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

    public async Task<PaymentResult> SubmitAsync(int bookingId, int actorId, string method, string key,
        CancellationToken ct)
    {
        if (bookingId <= 0 || actorId <= 0 || (method != "Cash" && !IsMockMethod(method)))
            return Error("คำขอชำระเงินไม่ถูกต้อง");

        await db.Database.OpenConnectionAsync(ct);
        var idLocked = false;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            if (!await ActorHasRoleAsync(actorId, ["Customer"], ct))
                return Error("คุณชำระเงินการจองนี้ไม่ได้");
            if (!await LockBookingAsync(bookingId, ct)) return Error("ไม่พบการจอง");
            var booking = await db.Bookings.AsNoTracking().SingleAsync(b => b.BookingId == bookingId, ct);
            if (booking.UserId != actorId)
                return Error("คุณชำระเงินการจองนี้ไม่ได้");
            if (booking.Status == BookingStatuses.Cancelled)
                return Error("การจองนี้ถูกยกเลิกแล้ว");
            var keyError = await ValidateKeyAsync(key, ct);
            if (keyError != null) return Error(keyError);
            if (!ValidAmount(booking.Amount)) return Error("ยอดเงินของการจองนี้ต้องตรวจสอบก่อน");
            if (await HasUnreconciledLegacyAsync(bookingId, ct))
                return Error("มีข้อมูลการชำระเงินเก่าที่ต้องตรวจสอบก่อนชำระเงิน");
            if (await HasConfirmedAsync(bookingId, ct)) return Error("การจองนี้ชำระเงินแล้ว");
            if (await db.Payments.AnyAsync(p => p.BookingId == bookingId && p.SubmittedAtUtc != null &&
                    (p.PaymentStatus == "Pending" || p.PaymentStatus == "Submitted"), ct))
                return Error("มีการชำระเงินที่รอยืนยันอยู่แล้ว");

            var lockResult = await LockPaymentIdAsync(ct);
            if (lockResult == 0) return Error("ระบบกำลังบันทึกการชำระเงินอื่น กรุณาลองใหม่");
            if (lockResult != 1) throw new InvalidOperationException("Could not acquire the payment ID lock.");
            idLocked = true;
            var lastId = Math.Max(0, await db.Payments.Select(p => (int?)p.PaymentId).MaxAsync(ct) ?? 0);
            if (lastId == int.MaxValue) throw new InvalidOperationException("Payment IDs are exhausted.");
            var payment = CreateSubmission(lastId + 1, bookingId, actorId, method, booking.Amount, key);
            db.Payments.Add(payment);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Success(payment.PaymentId, IsMockMethod(method)
                ? "ชำระเงินจำลองสำเร็จ (ไม่มีการโอนเงินจริง)"
                : "บันทึกการชำระเงินสดแล้ว รอพนักงานยืนยันเมื่อได้รับเงิน");
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
    /// พนักงานต้อนรับรับเงินสดที่เคาน์เตอร์ (ใช้กับลูกค้า Walk-in)
    /// บันทึกเป็นยืนยันแล้วทันที เพราะพนักงานรับเงินเอง
    /// </summary>
    public async Task<PaymentResult> RecordDeskCashAsync(int bookingId, int staffId, CancellationToken ct)
    {
        if (bookingId <= 0 || staffId <= 0) return Error("คำขอชำระเงินไม่ถูกต้อง");

        await db.Database.OpenConnectionAsync(ct);
        var idLocked = false;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            if (!await ActorHasRoleAsync(staffId, ["Staff"], ct)) return Error("เฉพาะพนักงานต้อนรับที่รับเงินที่เคาน์เตอร์ได้");
            if (!await LockBookingAsync(bookingId, ct)) return Error("ไม่พบการจอง");

            var booking = await db.Bookings.AsNoTracking().SingleAsync(b => b.BookingId == bookingId, ct);
            if (booking.Status == BookingStatuses.Cancelled) return Error("การจองนี้ถูกยกเลิกแล้ว");
            if (!ValidAmount(booking.Amount)) return Error("ยอดเงินของการจองนี้ต้องตรวจสอบก่อน");
            if (await HasConfirmedAsync(bookingId, ct)) return Error("การจองนี้ชำระเงินแล้ว");

            var lockResult = await LockPaymentIdAsync(ct);
            if (lockResult != 1) return Error("ระบบกำลังบันทึกการชำระเงินอื่น กรุณาลองใหม่");
            idLocked = true;

            var now = time.GetUtcNow().UtcDateTime;
            var lastId = await db.Payments.Select(p => (int?)p.PaymentId).MaxAsync(ct) ?? 0;
            var payment = new Payment
            {
                PaymentId = lastId + 1,
                BookingId = bookingId,
                Amount = booking.Amount,
                PaymentMethod = "Cash",
                PaymentStatus = "Paid",
                PaymentDate = RoomAvailabilityService.HotelDate(now),
                SubmittedAtUtc = now,
                SubmittedByUserId = staffId,
                VerifiedAtUtc = now,
                VerifiedByUserId = staffId
            };
            AddEvent(payment, staffId, "DeskCashReceived", Guid.NewGuid().ToString("N"), booking.Amount,
                "รับเงินสดที่เคาน์เตอร์");

            db.Payments.Add(payment);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Success(payment.PaymentId, "บันทึกรับเงินสดแล้ว");
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
        if (!await ActorHasRoleAsync(actorId, ["Staff", "Admin"], ct))
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

    public async Task<PaymentResult> ReconcileAsync(int paymentId, int actorId, bool confirmed,
        string? reason, string key, CancellationToken ct)
    {
        if (paymentId <= 0 || actorId <= 0 || string.IsNullOrWhiteSpace(reason) || reason.Length > 2000)
            return Error("กรุณาระบุเหตุผลในการตรวจสอบ");
        var bookingId = await db.Payments.AsNoTracking().Where(p => p.PaymentId == paymentId)
            .Select(p => p.BookingId).SingleOrDefaultAsync(ct);
        if (!bookingId.HasValue) return Error("ข้อมูลเก่านี้ไม่มีการจอง ต้องตรวจสอบเอง");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (!await ActorHasRoleAsync(actorId, ["Staff", "Admin"], ct)) return Error("ไม่มีสิทธิ์ทำรายการนี้");
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

    public async Task<PaymentResult> RequestRefundAsync(int paymentId, int actorId, decimal amount,
        string? reason, string key, CancellationToken ct)
    {
        if (paymentId <= 0 || actorId <= 0 || !ValidAmount(amount) ||
            string.IsNullOrWhiteSpace(reason) || reason.Length > 2000)
            return Error("กรุณากรอกจำนวนเงินและเหตุผลที่ขอคืนเงินให้ถูกต้อง");
        var bookingId = await db.Payments.AsNoTracking().Where(p => p.PaymentId == paymentId)
            .Select(p => p.BookingId).SingleOrDefaultAsync(ct);
        if (!bookingId.HasValue) return Error("ไม่พบการชำระเงิน");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (!await ActorHasRoleAsync(actorId, ["Customer"], ct))
            return Error("คุณขอคืนเงินการจองนี้ไม่ได้");
        if (!await LockBookingAsync(bookingId.Value, ct)) return Error("ไม่พบการจอง");
        var booking = await db.Bookings.AsNoTracking().SingleAsync(b => b.BookingId == bookingId, ct);
        var payment = await db.Payments.SingleOrDefaultAsync(p => p.PaymentId == paymentId && p.BookingId == bookingId, ct);
        if (booking.UserId != actorId)
            return Error("คุณขอคืนเงินการจองนี้ไม่ได้");
        if (payment?.VerifiedAtUtc == null || !ValidAmount(payment.Amount))
            return Error("ต้องมีการชำระเงินที่ยืนยันแล้วก่อน");
        var keyError = await ValidateKeyAsync(key, ct);
        if (keyError != null) return Error(keyError);
        if (await db.PaymentRefunds.AnyAsync(r => r.PaymentId == paymentId && r.Status == "Requested", ct))
            return Error("มีคำขอคืนเงินที่รออนุมัติอยู่แล้ว");
        var approved = await db.PaymentRefunds.Where(r => r.PaymentId == paymentId && r.Status == "Approved")
            .SumAsync(r => (decimal?)r.Amount, ct) ?? 0m;
        if (!RefundWithinLimit(payment.Amount, approved, amount)) return Error("ยอดคืนเงินเกินจำนวนที่ชำระมา");
        db.PaymentRefunds.Add(new PaymentRefund
        {
            PaymentId = paymentId, Amount = amount, Reason = reason.Trim(), Status = "Requested",
            RequestedByUserId = actorId, RequestedAtUtc = time.GetUtcNow().UtcDateTime
        });
        AddEvent(payment, actorId, "RefundRequested", key, amount, reason.Trim());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Success(paymentId, "ส่งคำขอคืนเงินแล้ว");
    }

    public async Task<PaymentResult> ApproveRefundAsync(int refundId, int actorId, string key, CancellationToken ct)
    {
        if (refundId <= 0 || actorId <= 0) return Error("ไม่พบคำขอคืนเงิน");
        var bookingId = await db.PaymentRefunds.AsNoTracking().Where(r => r.RefundId == refundId)
            .Select(r => r.Payment.BookingId).SingleOrDefaultAsync(ct);
        if (!bookingId.HasValue) return Error("ไม่พบคำขอคืนเงินหรือการจอง");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (!await ActorHasRoleAsync(actorId, ["Admin"], ct)) return Error("เฉพาะผู้ดูแลระบบที่อนุมัติการคืนเงินได้");
        if (!await LockBookingAsync(bookingId.Value, ct)) return Error("ไม่พบการจอง");
        var refund = await db.PaymentRefunds.Include(r => r.Payment)
            .SingleOrDefaultAsync(r => r.RefundId == refundId && r.Payment.BookingId == bookingId, ct);
        if (refund == null || refund.Status != "Requested" || refund.Payment.VerifiedAtUtc == null ||
            !ValidAmount(refund.Payment.Amount)) return Error("คำขอคืนเงินนี้อนุมัติไม่ได้แล้ว");
        var keyError = await ValidateKeyAsync(key, ct);
        if (keyError != null) return Error(keyError);
        var approved = await db.PaymentRefunds.Where(r => r.PaymentId == refund.PaymentId && r.Status == "Approved")
            .SumAsync(r => (decimal?)r.Amount, ct) ?? 0m;
        if (!RefundWithinLimit(refund.Payment.Amount, approved, refund.Amount))
            return Error("ยอดคืนเงินเกินจำนวนที่ชำระมา");
        refund.Status = "Approved";
        refund.ApprovedAtUtc = time.GetUtcNow().UtcDateTime;
        refund.ApprovedByUserId = actorId;
        if (approved + refund.Amount == refund.Payment.Amount && refund.Payment.SubmittedAtUtc != null)
            refund.Payment.PaymentStatus = "Refunded";
        AddEvent(refund.Payment, actorId, "RefundApproved", key, refund.Amount, refund.Reason);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Success(refund.PaymentId, IsMockMethod(refund.Payment.PaymentMethod)
            ? "บันทึกการคืนเงินจำลองแล้ว (ไม่มีการโอนเงินจริง)"
            : "บันทึกการคืนเงินแล้ว");
    }

    public async Task<PaymentResult> RejectRefundAsync(int refundId, int actorId, string? reason,
        string key, CancellationToken ct)
    {
        if (refundId <= 0 || actorId <= 0 || string.IsNullOrWhiteSpace(reason) || reason.Length > 2000)
            return Error("กรุณาระบุเหตุผลที่ปฏิเสธ");
        var bookingId = await db.PaymentRefunds.AsNoTracking().Where(r => r.RefundId == refundId)
            .Select(r => r.Payment.BookingId).SingleOrDefaultAsync(ct);
        if (!bookingId.HasValue) return Error("ไม่พบคำขอคืนเงินหรือการจอง");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (!await ActorHasRoleAsync(actorId, ["Admin"], ct)) return Error("เฉพาะผู้ดูแลระบบที่ตรวจคำขอคืนเงินได้");
        if (!await LockBookingAsync(bookingId.Value, ct)) return Error("ไม่พบการจอง");
        var refund = await db.PaymentRefunds.Include(r => r.Payment)
            .SingleOrDefaultAsync(r => r.RefundId == refundId && r.Payment.BookingId == bookingId, ct);
        if (refund == null || refund.Status != "Requested") return Error("คำขอคืนเงินนี้ไม่ได้รอตรวจสอบแล้ว");
        var keyError = await ValidateKeyAsync(key, ct);
        if (keyError != null) return Error(keyError);
        refund.Status = "Rejected";
        AddEvent(refund.Payment, actorId, "RefundRejected", key, refund.Amount, reason.Trim());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Success(refund.PaymentId, "ปฏิเสธคำขอคืนเงินแล้ว");
    }
}
