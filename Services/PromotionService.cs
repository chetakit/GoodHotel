using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Services;

/// <summary>ข้อมูลการจองที่ใช้ตรวจเงื่อนไขโปร</summary>
public sealed record PromotionContext(
    int? CustomerId,
    DateOnly CheckIn,
    DateOnly CheckOut,
    decimal RoomSubtotal);

public sealed record AppliedPromotion(int PromotionId, string Name, decimal DiscountAmount);

/// <summary>
/// หาโปรที่ลูกค้าได้รับอัตโนมัติ
/// กติกา: 1 การจองใช้ได้ 1 โปร และระบบเลือกโปรที่ลดได้มากที่สุดให้
/// ส่วนลดคิดจากค่าห้องเท่านั้น (ไม่รวมเตียงเสริม)
/// </summary>
public sealed class PromotionService(GoodHotelDbContext db, RoomAvailabilityService availability)
{
    public async Task<AppliedPromotion?> FindBestAsync(PromotionContext booking, CancellationToken ct)
    {
        var candidates = await db.Promotions.AsNoTracking()
            .Where(p => p.IsActive && p.RuleType != null && p.DiscountType != null && p.DiscountValue > 0)
            .ToListAsync(ct);

        var customer = await LoadCustomerHistoryAsync(booking.CustomerId, ct);
        var today = availability.HotelToday();

        return candidates
            .Where(p => IsEligible(p, booking, customer, today))
            .Select(p => new AppliedPromotion(p.PromotionId, p.PromotionName ?? "Promotion", CalculateDiscount(p, booking.RoomSubtotal)))
            .Where(p => p.DiscountAmount > 0)
            .OrderByDescending(p => p.DiscountAmount)
            .FirstOrDefault();
    }

    public static decimal CalculateDiscount(Promotion promotion, decimal roomSubtotal)
    {
        var value = promotion.DiscountValue ?? 0m;
        var discount = promotion.DiscountType == PromotionRules.Percent
            ? Math.Round(roomSubtotal * value / 100m, 2, MidpointRounding.AwayFromZero)
            : value;

        if (promotion.DiscountType == PromotionRules.Percent && promotion.MaxDiscount > 0)
        {
            discount = Math.Min(discount, promotion.MaxDiscount.Value);
        }

        // ลดไม่เกินค่าห้อง
        return Math.Min(discount, roomSubtotal);
    }

    private static bool IsEligible(Promotion promotion, PromotionContext booking, CustomerHistory customer, DateOnly today)
    {
        // เงื่อนไขส่วนกลาง: ช่วงวันที่เข้าพัก + ยอดขั้นต่ำ
        if (promotion.StartDate.HasValue && booking.CheckIn < promotion.StartDate.Value) return false;
        if (promotion.EndDate.HasValue && booking.CheckIn > promotion.EndDate.Value) return false;
        if (promotion.MinSpend.HasValue && booking.RoomSubtotal < promotion.MinSpend.Value) return false;

        var x = promotion.RuleValue ?? 0;
        var daysBeforeCheckIn = booking.CheckIn.DayNumber - today.DayNumber;
        var nights = booking.CheckOut.DayNumber - booking.CheckIn.DayNumber;

        return promotion.RuleType switch
        {
            PromotionRules.EarlyBird => daysBeforeCheckIn >= x,
            PromotionRules.LastMinute => daysBeforeCheckIn <= x,
            PromotionRules.LongStay => nights >= x,
            PromotionRules.Weekday => AllNightsAreWeekdays(booking.CheckIn, nights),
            PromotionRules.BirthdayMonth => customer.BirthMonth == booking.CheckIn.Month,
            PromotionRules.FirstStay => customer.IsKnown && customer.ActiveBookingCount == 0,
            PromotionRules.LoyalGuest => customer.CompletedStayCount >= x,
            PromotionRules.Seasonal => true,
            _ => false
        };
    }

    // วันที่ของ "คืน" คือวันที่เข้าพักคืนนั้น เช่น เข้าพักคืนวันศุกร์ = ไม่ใช่วันธรรมดา
    private static bool AllNightsAreWeekdays(DateOnly checkIn, int nights) =>
        Enumerable.Range(0, nights)
            .Select(i => checkIn.AddDays(i).DayOfWeek)
            .All(day => day is not (DayOfWeek.Friday or DayOfWeek.Saturday));

    private sealed record CustomerHistory(bool IsKnown, int? BirthMonth, int ActiveBookingCount, int CompletedStayCount);

    private async Task<CustomerHistory> LoadCustomerHistoryAsync(int? customerId, CancellationToken ct)
    {
        if (customerId is not > 0)
        {
            return new CustomerHistory(false, null, 0, 0);
        }

        var birthDate = await db.Users.AsNoTracking()
            .Where(u => u.UserId == customerId)
            .Select(u => u.BirthDate)
            .FirstOrDefaultAsync(ct);

        var activeBookings = await db.Bookings.AsNoTracking()
            .CountAsync(b => b.UserId == customerId && b.Status != BookingStatuses.Cancelled, ct);

        var completedStays = await db.BookingStays.AsNoTracking()
            .CountAsync(s => s.Booking.UserId == customerId && s.Status == "CheckedOut", ct);

        return new CustomerHistory(true, birthDate?.Month, activeBookings, completedStays);
    }
}
