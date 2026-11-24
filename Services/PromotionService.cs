using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Services;

/// <summary>ข้อมูลการจองที่ใช้ตรวจเงื่อนไขโปร</summary>
public sealed record PromotionContext(
    DateOnly CheckIn,
    DateOnly CheckOut,
    decimal RoomSubtotal);

public sealed record CustomerHistory(bool IsKnown, int? BirthMonth, int ActiveBookingCount, int CompletedStayCount);

public sealed record PromotionData(IReadOnlyList<Promotion> Candidates, CustomerHistory Customer);

public sealed record AppliedPromotion(int PromotionId, string Name, decimal DiscountAmount);

/// <summary>
/// โปรโมชันของการจอง
/// กติกา: 1 การจองใช้ได้ 1 โปร ลูกค้าเลือกเองจากโปรที่เข้าเงื่อนไข (ค่าเริ่มต้น = โปรที่ลดได้มากที่สุด)
/// ส่วนลดคิดจากค่าห้องเท่านั้น (ไม่รวมเตียงเสริม)
/// </summary>
public sealed class PromotionService(RoomAvailabilityService availability, ParallelReader reader)
{
    /// <summary>ข้อมูลที่ใช้ตัดสินโปร (โปรที่เปิดอยู่ + ประวัติลูกค้า) โหลดไว้ก่อนได้โดยยังไม่ต้องรู้ราคา</summary>
    public async Task<PromotionData> LoadAsync(int? customerId, bool isNewCustomer, CancellationToken ct)
    {
        var candidatesTask = reader.Run(c => c.Promotions.AsNoTracking()
            .Where(p => p.IsActive && p.RuleType != null && p.DiscountType != null && p.DiscountValue > 0)
            .ToListAsync(ct));
        var customerTask = reader.Run(c => LoadCustomerHistoryAsync(c, customerId, isNewCustomer, ct));
        await Task.WhenAll(candidatesTask, customerTask);
        return new PromotionData(candidatesTask.Result, customerTask.Result);
    }

    /// <summary>โปรทั้งหมดที่การจองนี้ใช้ได้ เรียงจากลดมากไปน้อย</summary>
    private IReadOnlyList<AppliedPromotion> FindEligible(PromotionData data, PromotionContext booking)
    {
        var (candidates, customer) = data;
        var today = availability.HotelToday();

        return candidates
            .Where(p => IsEligible(p, booking, customer, today))
            .Select(p => new AppliedPromotion(p.PromotionId, p.PromotionName ?? "Promotion", CalculateDiscount(p, booking.RoomSubtotal)))
            .Where(p => p.DiscountAmount > 0)
            .OrderByDescending(p => p.DiscountAmount)
            .ThenBy(p => p.PromotionId)
            .ToList();
    }

    /// <summary>
    /// โปรที่จะใช้จริง: ถ้าลูกค้าเลือกโปรที่ใช้ได้ ใช้โปรนั้น ไม่งั้น (ไม่ได้เลือก/เลือกโปรที่ใช้ไม่ได้) ใช้โปรที่ดีที่สุด
    /// ตรวจจากฝั่ง server ทุกครั้ง ไม่เชื่อค่าที่ส่งมาจากหน้าเว็บ
    /// </summary>
    public (AppliedPromotion? Chosen, IReadOnlyList<AppliedPromotion> Eligible) Resolve(
        PromotionData data, PromotionContext booking, int? selectedPromotionId)
    {
        var eligible = FindEligible(data, booking);
        var chosen = (selectedPromotionId.HasValue
            ? eligible.FirstOrDefault(p => p.PromotionId == selectedPromotionId.Value)
            : null) ?? eligible.FirstOrDefault();
        return (chosen, eligible);
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


    // ทุกตัวเลขอยู่ใน query เดียว (แต่ละ query ไปฐานข้อมูลใช้ ~200ms)
    private static async Task<CustomerHistory> LoadCustomerHistoryAsync(
        GoodHotelDbContext c, int? customerId, bool isNewCustomer, CancellationToken ct)
    {
        if (customerId is not > 0)
        {
            // ลูกค้าใหม่ที่กำลังจะถูกสร้าง (Walk-in) = รู้แน่ว่ายังไม่เคยจอง
            return new CustomerHistory(isNewCustomer, null, 0, 0);
        }

        var history = await c.Users.AsNoTracking()
            .Where(u => u.UserId == customerId)
            .Select(u => new
            {
                u.BirthDate,
                Active = c.Bookings.Count(b => b.UserId == u.UserId && b.Status != BookingStatuses.Cancelled),
                Completed = c.BookingStays.Count(s => s.Booking.UserId == u.UserId && s.Status == "CheckedOut")
            })
            .FirstOrDefaultAsync(ct);

        return new CustomerHistory(true, history?.BirthDate?.Month, history?.Active ?? 0, history?.Completed ?? 0);
    }
}
