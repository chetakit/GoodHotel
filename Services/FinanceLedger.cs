using GoodHotel.Models;
using GoodHotel.Models.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Services;

/// <summary>
/// ตัวเลขเงินของฝ่ายบัญชี ใช้นิยามเดียวกับรายได้ทั้งระบบ:
/// เงินรับ = การชำระที่ยืนยันแล้ว (นับเฉพาะรายการแรกของแต่ละการจอง) ตามเวลาที่ยืนยัน
/// เงินคืน = คำขอคืนเงินที่อนุมัติแล้ว ตามเวลาที่อนุมัติ
/// </summary>
public sealed class FinanceLedger(ParallelReader reader)
{
    private static readonly TimeZoneInfo HotelTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok");

    /// <summary>ช่วงวันที่ไทย [from, to] (รวมวันสุดท้าย) → ช่วงเวลา UTC [start, end)</summary>
    public static (DateTime StartUtc, DateTime EndUtc) UtcBounds(DateOnly from, DateOnly to) =>
        (TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), HotelTimeZone),
         TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), HotelTimeZone));

    /// <summary>รายการเงินเข้า/ออกทั้งหมดในช่วงเวลา เรียงตามเวลา</summary>
    public async Task<IReadOnlyList<LedgerEntry>> EntriesAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        var receivedTask = reader.Run(db => PaymentService.ConfirmedForRevenue(db.Payments)
            .Where(p => p.VerifiedAtUtc >= startUtc && p.VerifiedAtUtc < endUtc)
            .Select(p => new LedgerEntry(
                p.VerifiedAtUtc!.Value, false, p.PaymentId, p.BookingId, p.PaymentMethod, p.Amount ?? 0m,
                p.VerifiedByUser == null ? null : p.VerifiedByUser.FirstName + " " + p.VerifiedByUser.LastName,
                p.Booking == null || p.Booking.User == null ? null
                    : p.Booking.User.FirstName + " " + p.Booking.User.LastName))
            .ToListAsync(ct));

        var refundedTask = reader.Run(db => db.PaymentRefunds
            .Where(r => r.Status == "Approved" && r.ApprovedAtUtc >= startUtc && r.ApprovedAtUtc < endUtc &&
                PaymentService.ConfirmedForRevenue(db.Payments).Any(p => p.PaymentId == r.PaymentId))
            .Select(r => new LedgerEntry(
                r.ApprovedAtUtc!.Value, true, r.PaymentId, r.Payment.BookingId, r.Payment.PaymentMethod, r.Amount,
                r.ApprovedByUser == null ? null : r.ApprovedByUser.FirstName + " " + r.ApprovedByUser.LastName,
                r.Payment.Booking == null || r.Payment.Booking.User == null ? null
                    : r.Payment.Booking.User.FirstName + " " + r.Payment.Booking.User.LastName))
            .ToListAsync(ct));

        await Task.WhenAll(receivedTask, refundedTask);
        return receivedTask.Result.Concat(refundedTask.Result).OrderBy(e => e.OccurredAtUtc).ToList();
    }

    /// <summary>ยอดรวมตลอดเวลา คำนวณในฐานข้อมูล (ไม่ต้องดึงทุกแถวมา)</summary>
    public async Task<MoneyTotal> AllTimeAsync(CancellationToken ct)
    {
        var receivedTask = reader.Run(db => PaymentService.ConfirmedForRevenue(db.Payments)
            .SumAsync(p => p.Amount ?? 0m, ct));
        var refundedTask = reader.Run(db => db.PaymentRefunds
            .Where(r => r.Status == "Approved" &&
                PaymentService.ConfirmedForRevenue(db.Payments).Any(p => p.PaymentId == r.PaymentId))
            .SumAsync(r => r.Amount, ct));
        await Task.WhenAll(receivedTask, refundedTask);
        return new MoneyTotal(receivedTask.Result, refundedTask.Result);
    }

    public static MoneyTotal Total(IEnumerable<LedgerEntry> entries)
    {
        var list = entries as IReadOnlyCollection<LedgerEntry> ?? entries.ToList();
        return new MoneyTotal(list.Where(e => !e.IsRefund).Sum(e => e.Amount), list.Where(e => e.IsRefund).Sum(e => e.Amount));
    }

    /// <summary>เงินรับแยกตามช่องทาง (ไม่รวมเงินคืน)</summary>
    public static IReadOnlyList<MethodTotal> ByMethod(IEnumerable<LedgerEntry> entries) =>
        entries.Where(e => !e.IsRefund)
            .GroupBy(e => ThaiText.PaymentMethod(e.Method))
            .Select(g => new MethodTotal(g.Key, g.Count(), g.Sum(e => e.Amount)))
            .OrderByDescending(m => m.Amount)
            .ToList();

    public static DateOnly HotelDate(DateTime utc) => RoomAvailabilityService.HotelDate(utc);
}
