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
    IConfiguration configuration,
    ILogger<PaymentController> logger) : Controller
{
    private bool TryActor(out int id) => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier),
        NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;

    private string? QrPath => configuration["Payments:PromptPayQrImagePath"];
    private bool QrAvailable => !string.IsNullOrWhiteSpace(QrPath) && Path.IsPathFullyQualified(QrPath) &&
        Path.GetExtension(QrPath).Equals(".png", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(QrPath);

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
            TempData["PaymentError"] = "The request was already processed or conflicts with another payment.";
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
    [Authorize(Roles = "Customer,Staff,Admin")]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        if (id <= 0 || !TryActor(out var actorId)) return BadRequest();
        var booking = await db.Bookings.AsNoTracking().Where(b => b.BookingId == id)
            .Select(b => new { b.UserId, b.Amount }).SingleOrDefaultAsync(ct);
        if (booking == null) return NotFound();
        if (User.IsInRole("Customer") && booking.UserId != actorId) return Forbid();
        var list = await PaymentItems(db.Payments.AsNoTracking().Where(p => p.BookingId == id)
            .OrderByDescending(p => p.PaymentId)).ToListAsync(ct);
        var history = await db.PaymentEvents.AsNoTracking().Where(e => e.Payment.BookingId == id)
            .OrderByDescending(e => e.EventId)
            .Select(e => new PaymentHistoryItem(e.EventId, e.PaymentId, e.ActorUserId, e.EventType, e.OccurredAtUtc,
                e.Amount, e.PaymentMethod, e.Reason)).ToListAsync(ct);
        var refunds = await db.PaymentRefunds.AsNoTracking().Where(r => r.Payment.BookingId == id)
            .OrderByDescending(r => r.RefundId)
            .Select(r => new PaymentRefundItem(r.RefundId, r.PaymentId, r.Payment.BookingId, r.Amount, r.Status, r.Reason,
                r.RequestedByUserId, r.RequestedAtUtc, r.ApprovedByUserId, r.ApprovedAtUtc)).ToListAsync(ct);
        var confirmedPayment = list.Where(p => p.IsVerified).OrderBy(p => p.PaymentId).FirstOrDefault();
        var confirmed = confirmedPayment?.Amount ?? 0m;
        var refunded = refunds.Where(r => r.Status == "Approved" && r.PaymentId == confirmedPayment?.PaymentId)
            .Sum(r => r.Amount);
        return View(new PaymentDetailsViewModel(id, booking.Amount, confirmed, refunded,
            list.Any(p => p.IsLegacy), list, history, refunds));
    }

    [HttpGet]
    [Authorize(Roles = "Staff,Admin")]
    public async Task<IActionResult> Queue(string? search, string? status, string? method, CancellationToken ct)
    {
        search = search?.Trim();
        status = status?.Trim();
        method = method?.Trim();
        if (search?.Length > 100 || status?.Length > 30 || method?.Length > 50) return BadRequest();
        var query = FilterHistory(db.Payments.AsNoTracking(), search, method);
        var list = await ManagementItems(query.OrderByDescending(p => p.PaymentId)).ToListAsync(ct);
        if (!string.IsNullOrEmpty(status))
            list = list.Where(p => p.Payment.DisplayStatus == status).ToList();
        var refunds = await db.PaymentRefunds.AsNoTracking().Where(r => r.Status == "Requested")
            .OrderBy(r => r.RefundId)
            .Select(r => new PaymentRefundItem(r.RefundId, r.PaymentId, r.Payment.BookingId, r.Amount, r.Status, r.Reason,
                r.RequestedByUserId, r.RequestedAtUtc, r.ApprovedByUserId, r.ApprovedAtUtc)).ToListAsync(ct);
        return View(new PaymentQueueViewModel(list, refunds, search, status, method));
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
    [Authorize(Roles = "Staff,Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Review(int bookingId, int paymentId, bool? confirm,
        decimal? received, string? reason, string key, CancellationToken ct)
    {
        if (!ModelState.IsValid || bookingId <= 0 || paymentId <= 0 || !confirm.HasValue || !TryActor(out var actor))
            return BadRequest();
        return await FinishAsync(bookingId, () => payments.ReviewAsync(paymentId, actor, confirm.Value, received, reason, key, ct));
    }

    [HttpPost]
    [Authorize(Roles = "Staff,Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reconcile(int bookingId, int paymentId, bool? confirmed,
        string? reason, string key, CancellationToken ct)
    {
        if (!ModelState.IsValid || bookingId <= 0 || paymentId <= 0 || !confirmed.HasValue || !TryActor(out var actor))
            return BadRequest();
        return await FinishAsync(bookingId, () => payments.ReconcileAsync(paymentId, actor, confirmed.Value, reason, key, ct));
    }

    [HttpPost]
    [Authorize(Roles = "Customer")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestRefund(int bookingId, int paymentId, decimal amount,
        string? reason, string key, CancellationToken ct)
    {
        if (!ModelState.IsValid || bookingId <= 0 || paymentId <= 0 || !TryActor(out var actor)) return BadRequest();
        return await FinishAsync(bookingId, () => payments.RequestRefundAsync(paymentId, actor, amount, reason, key, ct));
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveRefund(int bookingId, int refundId, bool actualRefundSent,
        string key, CancellationToken ct)
    {
        if (!ModelState.IsValid || bookingId <= 0 || refundId <= 0 || !actualRefundSent || !TryActor(out var actor))
            return BadRequest();
        return await FinishAsync(bookingId, () => payments.ApproveRefundAsync(refundId, actor, key, ct));
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectRefund(int bookingId, int refundId, string? reason,
        string key, CancellationToken ct)
    {
        if (!ModelState.IsValid || bookingId <= 0 || refundId <= 0 || !TryActor(out var actor))
            return BadRequest();
        return await FinishAsync(bookingId, () => payments.RejectRefundAsync(refundId, actor, reason, key, ct));
    }

    [HttpGet]
    [Authorize(Roles = "Customer,Staff,Admin")]
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

    [HttpGet]
    [Authorize(Roles = "Customer")]
    public IActionResult PromptPayQr()
    {
        if (!QrAvailable) return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "private, no-store";
        return PhysicalFile(QrPath!, "image/png");
    }
}
