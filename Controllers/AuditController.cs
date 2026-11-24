using System.Globalization;
using GoodHotel.Models;
using GoodHotel.Models.ViewModels;
using GoodHotel.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Controllers;

/// <summary>
/// ตรวจสอบย้อนหลัง: อ่านอย่างเดียว ไม่มีการแก้ไขข้อมูลในคลาสนี้
/// ฝ่ายบัญชีใช้ตรวจเงิน ผู้ดูแลระบบใช้ตรวจฝ่ายบัญชีอีกชั้น (ฝ่ายบัญชีเป็นคนอนุมัติคืนเงินเอง)
/// </summary>
[Authorize(Roles = "Accountant,Admin")]
public class AuditController(ParallelReader reader, RoomAvailabilityService availability) : Controller
{
    // ---------------- Audit: ตรวจสอบย้อนหลังทุกการกระทำเรื่องเงินและการยกเลิก ----------------

    private const int AuditLimit = 1000;
    private const string CancelType = "BookingCancelled";

    private static readonly string[] AuditEventTypes =
    [
        "Submission", "MockConfirmed", "Confirmation", "Rejection", "DeskCashReceived",
        "RefundRequested", "RefundApproved", "RefundRejected", "RefundClosed", "RoomMoved", "LegacyConfirmed", "LegacyExcluded"
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
    public async Task<IActionResult> Index(DateOnly? from, DateOnly? to, string? type, int? bookingId, string? actor,
        CancellationToken ct)
    {
        var (start, end) = FinanceLedger.Range(from, to, availability.HotelToday());
        if (!string.IsNullOrEmpty(type) && AuditTypes().All(t => t.Key != type)) type = null;
        var (entries, truncated) = await AuditEntriesAsync(start, end, type, bookingId, actor, ct);
        return View(new AuditViewModel(start, end, type, bookingId, actor, entries, AuditTypes(), truncated));
    }

    [HttpGet]
    public async Task<IActionResult> Export(DateOnly? from, DateOnly? to, string? type, int? bookingId, string? actor,
        CancellationToken ct)
    {
        var (start, end) = FinanceLedger.Range(from, to, availability.HotelToday());
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
}
