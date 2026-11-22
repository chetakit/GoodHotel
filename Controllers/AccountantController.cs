using System.Globalization;
using GoodHotel.Models;
using GoodHotel.Models.ViewModels;
using GoodHotel.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Controllers;

/// <summary>
/// ฝ่ายบัญชี: ดูรายได้ ปิดยอดรายวัน พิจารณาคำขอคืนเงิน ตรวจสอบย้อนหลัง (Audit) และออกรายงาน/ไฟล์ CSV
/// (ลูกค้าจองออนไลน์ชำระออนไลน์เท่านั้น เงินสดมีเฉพาะ Walk-in ที่พนักงานต้อนรับรับที่เคาน์เตอร์ ฝ่ายบัญชีตรวจซ้ำจากสมุดรายวัน/Audit)
/// </summary>
[Authorize(Roles = "Accountant")]
public class AccountantController(FinanceLedger ledger, ParallelReader reader, RoomAvailabilityService availability)
    : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var today = availability.HotelToday();
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var (startUtc, endUtc) = FinanceLedger.UtcBounds(monthStart, today);

        var monthTask = ledger.EntriesAsync(startUtc, endUtc, ct);
        var allTimeTask = ledger.AllTimeAsync(ct);
        var countsTask = reader.Run(db => db.Payments.GroupBy(_ => 1).Select(g => new
        {
            AwaitingReview = g.Count(p => p.SubmittedAtUtc != null && p.VerifiedAtUtc == null &&
                (p.PaymentStatus == "Pending" || p.PaymentStatus == "Submitted")),
            Legacy = g.Count(p => p.SubmittedAtUtc == null && p.VerifiedAtUtc == null && p.LegacyDisposition == null)
        }).FirstOrDefaultAsync(ct));
        var refundsTask = reader.Run(db => db.PaymentRefunds.CountAsync(r => r.Status == PaymentService.RefundRequested, ct));
        await Task.WhenAll(monthTask, allTimeTask, countsTask, refundsTask);

        var month = monthTask.Result;
        var todayEntries = month.Where(e => FinanceLedger.HotelDate(e.OccurredAtUtc) == today).ToList();

        return View(new AccountantDashboardViewModel(
            FinanceLedger.Total(todayEntries),
            FinanceLedger.Total(month),
            allTimeTask.Result,
            countsTask.Result?.AwaitingReview ?? 0,
            countsTask.Result?.Legacy ?? 0,
            refundsTask.Result,
            FinanceLedger.ByMethod(month),
            todayEntries.OrderByDescending(e => e.OccurredAtUtc).ToList()));
    }

    /// <summary>ปิดยอดรายวัน: ทุกรายการเงินเข้า/ออกของวันนั้น พร้อมชื่อคนที่ยืนยัน</summary>
    [HttpGet]
    public async Task<IActionResult> Daily(DateOnly? date, CancellationToken ct)
    {
        var day = date ?? availability.HotelToday();
        var (startUtc, endUtc) = FinanceLedger.UtcBounds(day, day);
        var entries = await ledger.EntriesAsync(startUtc, endUtc, ct);
        return View(new DailyCloseViewModel(day, FinanceLedger.Total(entries), FinanceLedger.ByMethod(entries), entries));
    }

    /// <summary>รายงานการเงินตามช่วงวันที่ (ค่าเริ่มต้น: ต้นเดือนถึงวันนี้)</summary>
    [HttpGet]
    public async Task<IActionResult> Reports(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var (start, end) = Range(from, to);
        var (startUtc, endUtc) = FinanceLedger.UtcBounds(start, end);

        var entriesTask = ledger.EntriesAsync(startUtc, endUtc, ct);
        var bookingsTask = reader.Run(db => db.Bookings
            .Where(b => b.Status != BookingStatuses.Cancelled && b.CheckIn >= start && b.CheckIn <= end)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Amount = g.Sum(b => b.Amount) ?? 0m })
            .FirstOrDefaultAsync(ct));
        var promotionsTask = reader.Run(db => db.Bookings
            .Where(b => b.Status != BookingStatuses.Cancelled && b.Promotion != null &&
                b.CheckIn >= start && b.CheckIn <= end)
            .GroupBy(b => b.Promotion!.PromotionName)
            .Select(g => new PromotionUsageViewModel(g.Key ?? "-", g.Count(), g.Sum(b => b.Amount) ?? 0m))
            .ToListAsync(ct));
        await Task.WhenAll(entriesTask, bookingsTask, promotionsTask);

        var entries = entriesTask.Result;
        var daily = entries.GroupBy(e => FinanceLedger.HotelDate(e.OccurredAtUtc))
            .Select(g => new DailyRow(g.Key, g.Where(e => !e.IsRefund).Sum(e => e.Amount), g.Where(e => e.IsRefund).Sum(e => e.Amount)))
            .OrderBy(d => d.Date)
            .ToList();

        return View(new FinanceReportViewModel(start, end, FinanceLedger.Total(entries),
            bookingsTask.Result?.Amount ?? 0m, bookingsTask.Result?.Count ?? 0,
            FinanceLedger.ByMethod(entries), daily,
            promotionsTask.Result.OrderByDescending(p => p.BookingAmount).ToList()));
    }

    /// <summary>สมุดรายวันเป็นไฟล์ CSV ตามช่วงวันที่</summary>
    [HttpGet]
    public async Task<IActionResult> ExportLedger(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var (start, end) = Range(from, to);
        var (startUtc, endUtc) = FinanceLedger.UtcBounds(start, end);
        var entries = await ledger.EntriesAsync(startUtc, endUtc, ct);
        var content = CsvExport.Build(
            ["วันเวลา (ไทย)", "ประเภท", "เลขที่ชำระเงิน", "เลขที่การจอง", "ลูกค้า", "ช่องทาง", "จำนวนเงิน", "ผู้ยืนยัน/อนุมัติ"],
            entries.Select(e => new[]
            {
                ThaiText.Time(e.OccurredAtUtc), e.IsRefund ? "คืนเงิน" : "รับเงิน",
                CsvExport.Format(e.PaymentId), CsvExport.Format(e.BookingId), e.GuestName,
                ThaiText.PaymentMethod(e.Method), CsvExport.Format(e.IsRefund ? -e.Amount : e.Amount), e.ActorName
            }));
        return File(content, "text/csv; charset=utf-8",
            $"ledger-{start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}-to-{end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.csv");
    }

    // ---------------- Audit: ตรวจสอบย้อนหลังทุกการกระทำเรื่องเงินและการยกเลิก ----------------

    private const int AuditLimit = 1000;
    private const string CancelType = "BookingCancelled";

    private static readonly string[] AuditEventTypes =
    [
        "Submission", "MockConfirmed", "Confirmation", "Rejection", "DeskCashReceived",
        "RefundRequested", "RefundApproved", "RefundRejected", "RefundClosed", "LegacyConfirmed", "LegacyExcluded"
    ];

    private static IReadOnlyList<KeyValuePair<string, string>> AuditTypes() =>
        AuditEventTypes.Select(t => new KeyValuePair<string, string>(t, ThaiText.Status(t)))
            .Append(new KeyValuePair<string, string>(CancelType, "ยกเลิกการจอง"))
            .ToList();

    /// <summary>
    /// รวมเหตุการณ์จาก payment_events (ใครทำ อะไร เมื่อไร เหตุผล) กับการยกเลิกการจอง (ตาราง bookings)
    /// กรองในฐานข้อมูลก่อน แล้วค่อยกรองชื่อผู้ทำรายการในหน่วยความจำ
    /// </summary>
    private async Task<(List<AuditEntry> Entries, bool Truncated)> AuditEntriesAsync(DateOnly start, DateOnly end,
        string? type, int? bookingId, string? actor, CancellationToken ct)
    {
        var (startUtc, endUtc) = FinanceLedger.UtcBounds(start, end);
        var wantEvents = string.IsNullOrEmpty(type) || type != CancelType;
        var wantCancels = string.IsNullOrEmpty(type) || type == CancelType;

        var eventsTask = !wantEvents ? Task.FromResult(new List<AuditEntry>()) : reader.Run(db =>
        {
            var q = db.PaymentEvents.AsNoTracking()
                .Where(e => e.OccurredAtUtc >= startUtc && e.OccurredAtUtc < endUtc);
            if (!string.IsNullOrEmpty(type)) q = q.Where(e => e.EventType == type);
            if (bookingId.HasValue) q = q.Where(e => e.Payment.BookingId == bookingId);
            return q.OrderByDescending(e => e.OccurredAtUtc).Take(AuditLimit + 1)
                .Select(e => new AuditEntry(e.OccurredAtUtc, e.EventType, e.EventType, e.Payment.BookingId, e.PaymentId,
                    e.ActorUser.FirstName + " " + e.ActorUser.LastName, e.ActorUser.Role, e.Amount, e.PaymentMethod, e.Reason))
                .ToListAsync(ct);
        });

        var cancelsTask = !wantCancels ? Task.FromResult(new List<AuditEntry>()) : reader.Run(db =>
        {
            var q = db.Bookings.AsNoTracking().Where(b => b.Status == BookingStatuses.Cancelled &&
                b.CancelledAtUtc >= startUtc && b.CancelledAtUtc < endUtc);
            if (bookingId.HasValue) q = q.Where(b => b.BookingId == bookingId);
            return q.OrderByDescending(b => b.CancelledAtUtc).Take(AuditLimit + 1)
                .Select(b => new AuditEntry(b.CancelledAtUtc!.Value, CancelType, b.CancelReason ?? "", b.BookingId, null,
                    b.CancelReason == CancelReasons.Customer && b.User != null ? b.User.FirstName + " " + b.User.LastName : null,
                    b.CancelReason, b.Amount, null, null))
                .ToListAsync(ct);
        });

        await Task.WhenAll(eventsTask, cancelsTask);

        var entries = eventsTask.Result
            .Select(e => e with { TypeLabel = ThaiText.Status(e.Type), ActorRole = ThaiText.Status(e.ActorRole) })
            .Concat(cancelsTask.Result.Select(c => c with
            {
                TypeLabel = "ยกเลิกการจอง · " + CancelReasons.Label(c.TypeLabel),
                Actor = c.ActorRole switch
                {
                    CancelReasons.Customer => c.Actor,
                    CancelReasons.Staff => "พนักงานต้อนรับ",
                    CancelReasons.Refund => "ฝ่ายบัญชี",
                    CancelReasons.PaymentTimeout => "ระบบ",
                    _ => null
                },
                ActorRole = c.ActorRole switch
                {
                    CancelReasons.Customer => "ลูกค้า",
                    CancelReasons.PaymentTimeout => "อัตโนมัติ",
                    CancelReasons.Refund => "อนุมัติคืนเงิน",
                    _ => null
                },
                Detail = c.ActorRole == CancelReasons.PaymentTimeout ? "ไม่ชำระเงินภายในเวลาที่กำหนด" : null
            }))
            .Where(e => string.IsNullOrWhiteSpace(actor) ||
                (e.Actor?.Contains(actor.Trim(), StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderByDescending(e => e.OccurredAtUtc)
            .ToList();

        var truncated = entries.Count > AuditLimit;
        return (truncated ? entries.Take(AuditLimit).ToList() : entries, truncated);
    }

    [HttpGet]
    public async Task<IActionResult> Audit(DateOnly? from, DateOnly? to, string? type, int? bookingId, string? actor,
        CancellationToken ct)
    {
        var (start, end) = Range(from, to);
        if (!string.IsNullOrEmpty(type) && AuditTypes().All(t => t.Key != type)) type = null;
        var (entries, truncated) = await AuditEntriesAsync(start, end, type, bookingId, actor, ct);
        return View(new AuditViewModel(start, end, type, bookingId, actor, entries, AuditTypes(), truncated));
    }

    [HttpGet]
    public async Task<IActionResult> ExportAudit(DateOnly? from, DateOnly? to, string? type, int? bookingId, string? actor,
        CancellationToken ct)
    {
        var (start, end) = Range(from, to);
        if (!string.IsNullOrEmpty(type) && AuditTypes().All(t => t.Key != type)) type = null;
        var (entries, _) = await AuditEntriesAsync(start, end, type, bookingId, actor, ct);
        var content = CsvExport.Build(
            ["วันเวลา (ไทย)", "เหตุการณ์", "เลขที่การจอง", "เลขที่ชำระเงิน", "ผู้ทำรายการ", "บทบาท", "ช่องทาง", "จำนวนเงิน", "หมายเหตุ"],
            entries.Select(e => new[]
            {
                ThaiText.Time(e.OccurredAtUtc), e.TypeLabel, CsvExport.Format(e.BookingId), CsvExport.Format(e.PaymentId),
                e.Actor, e.ActorRole, e.Method == null ? null : ThaiText.PaymentMethod(e.Method), CsvExport.Format(e.Amount), e.Detail
            }));
        return File(content, "text/csv; charset=utf-8",
            $"audit-{start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}-to-{end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.csv");
    }

    [HttpGet]
    public async Task<IActionResult> ExportPayments(CancellationToken ct)
    {
        var rows = await reader.Run(db => db.Payments.AsNoTracking().OrderBy(p => p.PaymentId)
            .Select(p => new { p.PaymentId, p.BookingId, p.PaymentDate, p.PaymentMethod, p.PaymentStatus, p.Amount,
                p.SubmittedAtUtc, p.VerifiedAtUtc, p.LegacyDisposition })
            .ToListAsync(ct));
        var content = CsvExport.Build(
            ["PaymentId", "BookingId", "PaymentDate", "PaymentMethod", "PaymentStatus", "Amount", "VerifiedAtUtc", "LegacyDisposition"],
            rows.Select(p => new[] { CsvExport.Format(p.PaymentId), CsvExport.Format(p.BookingId), CsvExport.Format(p.PaymentDate),
                p.PaymentMethod, p.PaymentStatus, CsvExport.Format(p.Amount), CsvExport.Format(p.VerifiedAtUtc),
                p.SubmittedAtUtc == null ? p.LegacyDisposition ?? "Requires reconciliation" : null }));
        return File(content, "text/csv; charset=utf-8", "payments.csv");
    }

    // ช่วงวันที่ที่ใช้ได้: ไม่ระบุ = ต้นเดือนถึงวันนี้, กลับด้านให้อัตโนมัติ, ไม่เกิน 1 ปี
    private (DateOnly Start, DateOnly End) Range(DateOnly? from, DateOnly? to)
    {
        var today = availability.HotelToday();
        var end = to ?? today;
        var start = from ?? new DateOnly(end.Year, end.Month, 1);
        if (start > end) (start, end) = (end, start);
        if (end.DayNumber - start.DayNumber > 366) start = end.AddDays(-366);
        return (start, end);
    }
}
