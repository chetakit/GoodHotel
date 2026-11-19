using System.Globalization;
using GoodHotel.Models;
using GoodHotel.Models.ViewModels;
using GoodHotel.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Controllers;

/// <summary>
/// ฝ่ายบัญชี: ดูรายได้ ปิดยอดรายวัน อนุมัติคืนเงิน และออกรายงาน/ไฟล์ CSV
/// (พนักงานต้อนรับยังรับเงินและยืนยันสลิปได้ตามปกติ ฝ่ายบัญชีมาตรวจซ้ำจากสมุดรายวัน)
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
        var refundsTask = reader.Run(db => db.PaymentRefunds.CountAsync(r => r.Status == "Requested", ct));
        await Task.WhenAll(monthTask, allTimeTask, countsTask, refundsTask);

        var month = monthTask.Result;
        var todayEntries = month.Where(e => FinanceLedger.HotelDate(e.OccurredAtUtc) == today).ToList();

        return View(new AccountantDashboardViewModel(
            FinanceLedger.Total(todayEntries),
            FinanceLedger.Total(month),
            allTimeTask.Result,
            countsTask.Result?.AwaitingReview ?? 0,
            refundsTask.Result,
            countsTask.Result?.Legacy ?? 0,
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
