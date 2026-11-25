using System.Globalization;
using System.Security.Claims;
using GoodHotel.Models;
using GoodHotel.Models.ViewModels;
using GoodHotel.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace GoodHotel.Controllers;

[Authorize]
public class PaymentController(
    GoodHotel.Models.GoodHotelDbContext db,
    PaymentService payments,
    PaymentEvidenceStorage evidence,
    ILogger<PaymentController> logger,
    ParallelReader reader,
    UserDirectory users,
    RoomAvailabilityService availability) : Controller
{
    private bool TryActor(out int id) => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier),
        NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;

    private async Task<IActionResult> FinishAsync(int bookingId, Func<Task<PaymentResult>> operation)
    {
        try
        {
            var result = await operation();
            TempData[result.Succeeded ? "PaymentSuccess" : "PaymentError"] = result.Message;
        }
        catch (DbUpdateException ex) when (ex.InnerException is MySqlException { Number: 1062 })
        {
            logger.LogWarning(ex, "Duplicate or conflicting payment operation for booking {BookingId}.", bookingId);
            TempData["PaymentError"] = "คำขอนี้ถูกดำเนินการไปแล้ว";
        }
        return RedirectToAction(nameof(Details), new { id = bookingId });
    }

    private static IQueryable<PaymentListItem> PaymentItems(IQueryable<GoodHotel.Models.Payment> query) => query.Select(p =>
        new PaymentListItem(p.PaymentId, p.BookingId, p.PaymentMethod, p.PaymentStatus, p.Amount,
            p.SubmittedAtUtc == null && p.VerifiedAtUtc == null && p.LegacyDisposition == null,
            p.SubmittedAtUtc == null && p.LegacyDisposition == "Excluded", p.SubmittedAtUtc != null,
            p.VerifiedAtUtc != null, p.EvidenceFileName != null,
            p.Refunds.Where(r => r.Status == "Approved").Sum(r => (decimal?)r.Amount) ?? 0m));

    public static IQueryable<PaymentManagementItem> ManagementItems(IQueryable<Payment> query) =>
        query.Select(p => new PaymentManagementItem(
            new PaymentListItem(p.PaymentId, p.BookingId, p.PaymentMethod, p.PaymentStatus, p.Amount,
                p.SubmittedAtUtc == null && p.VerifiedAtUtc == null && p.LegacyDisposition == null,
                p.SubmittedAtUtc == null && p.LegacyDisposition == "Excluded", p.SubmittedAtUtc != null,
                p.VerifiedAtUtc != null, p.EvidenceFileName != null,
                p.Refunds.Where(r => r.Status == "Approved").Sum(r => (decimal?)r.Amount) ?? 0m),
            p.Booking == null || p.Booking.User == null ? null :
                (p.Booking.User.FirstName ?? "") + " " + (p.Booking.User.LastName ?? ""),
            p.SubmittedAtUtc ?? (p.PaymentDate == null ? p.VerifiedAtUtc : null), p.PaymentDate));

    public static IQueryable<Payment> FilterHistory(IQueryable<Payment> query, string? search, string? method)
    {
        if (!string.IsNullOrEmpty(search))
        {
            int? id = int.TryParse(search, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedId)
                ? parsedId : null;
            query = query.Where(p => (id.HasValue && (p.BookingId == id || p.PaymentId == id)) ||
                (p.Booking != null && p.Booking.User != null &&
                 ((p.Booking.User.FirstName != null && p.Booking.User.FirstName.Contains(search)) ||
                  (p.Booking.User.LastName != null && p.Booking.User.LastName.Contains(search)) ||
                  ((p.Booking.User.FirstName ?? "") + " " + (p.Booking.User.LastName ?? "")).Contains(search) ||
                  (p.Booking.User.Email != null && p.Booking.User.Email.Contains(search)))));
        }
        return string.IsNullOrEmpty(method) ? query : query.Where(p => p.PaymentMethod == method);
    }

    [HttpGet]
    [Authorize(Roles = "Customer,Staff,Accountant")]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        if (id <= 0 || !TryActor(out var actorId)) return BadRequest();

        // 4 query ไม่ขึ้นต่อกัน ยิงพร้อมกัน แล้วค่อยเช็คสิทธิ์จากผลของ query แรก
        var bookingTask = reader.Run(c => c.Bookings.AsNoTracking().Where(b => b.BookingId == id)
            .Select(b => new { b.UserId, b.Amount, b.Status,
                GuestName = b.User == null ? null : b.User.FirstName + " " + b.User.LastName,
                RoomNumber = b.Room == null ? null : b.Room.Room1, b.CreatedAtUtc, b.CancelReason, b.CheckOut,
                HasStay = c.BookingStays.Any(s => s.BookingId == b.BookingId) }).SingleOrDefaultAsync(ct));
        var listTask = reader.Run(c => PaymentItems(c.Payments.AsNoTracking().Where(p => p.BookingId == id)
            .OrderByDescending(p => p.PaymentId)).ToListAsync(ct));
        var historyTask = reader.Run(c => c.PaymentEvents.AsNoTracking().Where(e => e.Payment.BookingId == id)
            .OrderByDescending(e => e.EventId)
            .Select(e => new PaymentHistoryItem(e.EventId, e.PaymentId, e.ActorUserId, e.EventType, e.OccurredAtUtc,
                e.Amount, e.PaymentMethod, e.Reason)).ToListAsync(ct));
        var refundsTask = reader.Run(c => c.PaymentRefunds.AsNoTracking().Where(r => r.Payment.BookingId == id)
            .OrderByDescending(r => r.RefundId)
            .Select(r => new PaymentRefundItem(r.RefundId, r.PaymentId, r.Payment.BookingId, r.Amount, r.Status, r.Reason,
                r.RequestedByUserId, r.RequestedAtUtc, r.ApprovedByUserId, r.ApprovedAtUtc)).ToListAsync(ct));
        var namesTask = reader.Run(c => c.Users.AsNoTracking()
            .Where(u => c.PaymentEvents.Any(e => e.ActorUserId == u.UserId && e.Payment.BookingId == id) ||
                        c.PaymentRefunds.Any(r => r.RequestedByUserId == u.UserId && r.Payment.BookingId == id))
            .Select(u => new { u.UserId, u.FirstName, u.LastName, u.Role }).ToListAsync(ct));
        await Task.WhenAll(bookingTask, listTask, historyTask, refundsTask, namesTask);

        var booking = bookingTask.Result;
        if (booking == null) return NotFound();
        if (User.IsInRole("Customer") && booking.UserId != actorId) return Forbid();
        var list = listTask.Result;
        var history = historyTask.Result;
        var refunds = refundsTask.Result;
        foreach (var person in namesTask.Result) users.Remember(person.UserId, person.FirstName, person.LastName, person.Role);
        var confirmedPayment = list.Where(p => p.IsVerified).OrderBy(p => p.PaymentId).FirstOrDefault();
        var confirmed = confirmedPayment?.Amount ?? 0m;
        var refunded = refunds.Where(r => r.Status == "Approved" && r.PaymentId == confirmedPayment?.PaymentId)
            .Sum(r => r.Amount);
        var today = availability.HotelToday();
        return View(new PaymentDetailsViewModel(id, booking.Amount, confirmed, refunded,
            list.Any(p => p.IsLegacy), list, history, refunds, booking.Status, booking.GuestName, booking.RoomNumber,
            booking.CreatedAtUtc, booking.CancelReason,
            BookingCancellationService.CanCancel(booking.Status ?? "", booking.CheckOut, booking.HasStay,
                refunds.Any(r => r.Status == PaymentService.RefundRequested), today),
            PaymentService.CanRequestRefund(booking.Status, booking.CheckOut, booking.HasStay,
                confirmedPayment != null && refunded < confirmed, refunds.Count > 0, today),
            booking.Status != BookingStatuses.Cancelled && !booking.HasStay && booking.CheckOut <= today));
    }

    /// <summary>
    /// ใบเสร็จรับเงิน (พิมพ์ได้) ออกจากการชำระที่ยืนยันแล้วรายการแรกของการจอง
    /// ลูกค้าเปิดได้เฉพาะของตัวเอง พนักงานต้อนรับและฝ่ายบัญชีเปิดได้ทุกใบ
    /// </summary>
    [HttpGet]
    [Authorize(Roles = "Customer,Staff,Accountant")]
    public async Task<IActionResult> Receipt(int id, CancellationToken ct)
    {
        if (id <= 0 || !TryActor(out var actorId)) return BadRequest();

        var receipt = await PaymentService.ConfirmedForRevenue(db.Payments).AsNoTracking()
            .Where(p => p.BookingId == id)
            .OrderBy(p => p.PaymentId)
            .Select(p => new
            {
                OwnerId = p.Booking!.UserId,
                Model = new ReceiptViewModel(
                    p.PaymentId, id, p.VerifiedAtUtc!.Value, p.PaymentMethod, p.Amount ?? 0m,
                    p.Refunds.Where(r => r.Status == "Approved").Sum(r => (decimal?)r.Amount) ?? 0m,
                    p.VerifiedByUser == null ? null : p.VerifiedByUser.FirstName + " " + p.VerifiedByUser.LastName,
                    p.Booking.User == null ? null : p.Booking.User.FirstName + " " + p.Booking.User.LastName,
                    p.Booking.User == null ? null : p.Booking.User.Email,
                    p.Booking.User == null ? null : p.Booking.User.Phone,
                    p.Booking.Room == null ? null : p.Booking.Room.Room1,
                    p.Booking.Room == null ? null : p.Booking.Room.Type,
                    p.Booking.CheckIn, p.Booking.CheckOut, p.Booking.Guests, p.Booking.ExtraBed ?? 0,
                    p.Booking.Promotion == null ? null : p.Booking.Promotion.PromotionName,
                    p.Booking.Amount ?? 0m,
                    p.Booking.Status == BookingStatuses.Cancelled)
            })
            .FirstOrDefaultAsync(ct);

        if (receipt == null)
        {
            TempData["PaymentError"] = "การจองนี้ยังไม่มีการชำระเงินที่ยืนยันแล้ว จึงยังออกใบเสร็จไม่ได้";
            return RedirectToAction(nameof(Details), new { id });
        }
        if (User.IsInRole("Customer") && receipt.OwnerId != actorId) return Forbid();
        return View(receipt.Model);
    }

    [HttpGet]
    [Authorize(Roles = "Staff,Accountant")]
    public async Task<IActionResult> Queue(string? search, string? status, string? method, CancellationToken ct)
    {
        search = search?.Trim();
        status = status?.Trim();
        method = method?.Trim();
        if (search?.Length > 100 || status?.Length > 30 || method?.Length > 50) return BadRequest();
        var query = FilterHistory(db.Payments.AsNoTracking(), search, method);
        var refundsTask = reader.Run(c => c.PaymentRefunds.AsNoTracking().Where(r => r.Status == PaymentService.RefundRequested)
            .OrderBy(r => r.RefundId)
            .Select(r => new PaymentRefundItem(r.RefundId, r.PaymentId, r.Payment.BookingId, r.Amount, r.Status, r.Reason,
                r.RequestedByUserId, r.RequestedAtUtc, r.ApprovedByUserId, r.ApprovedAtUtc)).ToListAsync(ct));
        var list = await ManagementItems(query.OrderByDescending(p => p.PaymentId)).ToListAsync(ct);
        if (!string.IsNullOrEmpty(status))
            list = list.Where(p => p.Payment.DisplayStatus == status).ToList();
        var refunds = await refundsTask;
        return View(new PaymentQueueViewModel(list, refunds, search, status, method));
    }

    /// <summary>ลูกค้าขอคืนเงิน: เลือกเหตุผลจากรายการ + รายละเอียดเพิ่มเติม ("อื่นๆ" ต้องกรอกรายละเอียด)</summary>
    [HttpPost]
    [Authorize(Roles = "Customer")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestRefund(int bookingId, string? reasonChoice, string? detail,
        string key, CancellationToken ct)
    {
        if (!ModelState.IsValid || bookingId <= 0 || !TryActor(out var actor)) return BadRequest();
        reasonChoice = reasonChoice?.Trim();
        detail = detail?.Trim();
        if (string.IsNullOrEmpty(reasonChoice) || !PaymentService.RefundReasons.Contains(reasonChoice) ||
            (reasonChoice == "อื่นๆ" && string.IsNullOrEmpty(detail)) || detail?.Length > 500)
        {
            TempData["PaymentError"] = "กรุณาเลือกเหตุผล (ถ้าเลือก \"อื่นๆ\" ต้องกรอกรายละเอียด)";
            return Redirect(Url.Action(nameof(Details), new { id = bookingId }) + "#refund");
        }
        var reason = string.IsNullOrEmpty(detail) ? reasonChoice : $"{reasonChoice}: {detail}";
        return await FinishAsync(bookingId, () => payments.RequestRefundAsync(bookingId, actor, reason, key, ct));
    }

    [HttpPost]
    [Authorize(Roles = "Accountant")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveRefund(int bookingId, int refundId, string key, CancellationToken ct)
    {
        if (!ModelState.IsValid || bookingId <= 0 || refundId <= 0 || !TryActor(out var actor)) return BadRequest();
        return await FinishAsync(bookingId, () => payments.ApproveRefundAsync(refundId, actor, key, ct));
    }

    [HttpPost]
    [Authorize(Roles = "Accountant")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectRefund(int bookingId, int refundId, string? reason,
        string key, CancellationToken ct)
    {
        if (!ModelState.IsValid || bookingId <= 0 || refundId <= 0 || !TryActor(out var actor)) return BadRequest();
        return await FinishAsync(bookingId, () => payments.RejectRefundAsync(refundId, actor, reason, key, ct));
    }

    [HttpPost]
    [Authorize(Roles = "Customer")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(int bookingId, string method, string key, CancellationToken ct)
    {
        if (!ModelState.IsValid || bookingId <= 0 || !TryActor(out var actorId)) return BadRequest();
        if (!await db.Bookings.AsNoTracking().AnyAsync(b => b.BookingId == bookingId && b.UserId == actorId, ct))
            return Forbid();
        return await FinishAsync(bookingId, () => payments.SubmitAsync(bookingId, actorId, method, key, ct));
    }

    [HttpPost]
    [Authorize(Roles = "Staff,Accountant")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Review(int bookingId, int paymentId, bool? confirm,
        decimal? received, string? reason, string key, CancellationToken ct)
    {
        if (!ModelState.IsValid || bookingId <= 0 || paymentId <= 0 || !confirm.HasValue || !TryActor(out var actor))
            return BadRequest();
        return await FinishAsync(bookingId, () => payments.ReviewAsync(paymentId, actor, confirm.Value, received, reason, key, ct));
    }

    [HttpPost]
    [Authorize(Roles = "Accountant")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reconcile(int bookingId, int paymentId, bool? confirmed,
        string? reason, string key, CancellationToken ct)
    {
        if (!ModelState.IsValid || bookingId <= 0 || paymentId <= 0 || !confirmed.HasValue || !TryActor(out var actor))
            return BadRequest();
        return await FinishAsync(bookingId, () => payments.ReconcileAsync(paymentId, actor, confirmed.Value, reason, key, ct));
    }

    [HttpGet]
    [Authorize(Roles = "Customer,Staff,Accountant")]
    public async Task<IActionResult> Evidence(int id, CancellationToken ct)
    {
        if (id <= 0 || !TryActor(out var actor)) return BadRequest();
        var item = await db.Payments.AsNoTracking().Where(p => p.PaymentId == id)
            .Select(p => new { p.EvidenceFileName, Owner = p.Booking == null ? null : p.Booking.UserId })
            .SingleOrDefaultAsync(ct);
        if (item?.EvidenceFileName == null) return NotFound();
        if (User.IsInRole("Customer") && item.Owner != actor) return Forbid();
        var path = evidence.GetPath(item.EvidenceFileName);
        if (!System.IO.File.Exists(path)) return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "private, no-store";
        var type = Path.GetExtension(path) switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
        return PhysicalFile(path, type);
    }
}
