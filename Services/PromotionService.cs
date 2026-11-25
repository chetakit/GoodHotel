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

/// <summary>ผลเช็คสิทธิ์ของโปรหนึ่งตัว Reason = เหตุผลที่ใช้ไม่ได้ (null = ใช้ได้)</summary>
public sealed record PromotionCheck(int PromotionId, string Name, string DiscountText, string Condition, decimal Discount, string? Reason);

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
    public static IReadOnlyList<AppliedPromotion> FindEligible(PromotionData data, PromotionContext booking, DateOnly today)
    {
        var (candidates, customer) = data;

        return candidates
            .Where(p => IneligibleReason(p, booking, customer, today) == null)
            .Select(p => new AppliedPromotion(p.PromotionId, p.PromotionName ?? "Promotion", CalculateDiscount(p, booking.RoomSubtotal)))
            .Where(p => p.DiscountAmount > 0)
            .OrderByDescending(p => p.DiscountAmount)
            .ThenBy(p => p.PromotionId)
            .ToList();
    }

    /// <summary>เช็คสิทธิ์ทุกโปรที่เปิดอยู่ พร้อมเหตุผลที่ใช้ไม่ได้ (ใช้ได้ขึ้นก่อน ลดมากขึ้นก่อน)</summary>
    public IReadOnlyList<PromotionCheck> CheckAll(PromotionData data, PromotionContext booking)
    {
        var today = availability.HotelToday();
        return data.Candidates
            .Select(p => new PromotionCheck(p.PromotionId, p.PromotionName ?? "Promotion",
                PromotionRules.DescribeDiscount(p), PromotionRules.DescribeCondition(p),
                CalculateDiscount(p, booking.RoomSubtotal), IneligibleReason(p, booking, data.Customer, today)))
            .OrderBy(c => c.Reason != null)
            .ThenByDescending(c => c.Discount)
            .ThenBy(c => c.PromotionId)
            .ToList();
    }

    /// <summary>
    /// โปรที่จะใช้จริง: ถ้าลูกค้าเลือกโปรที่ใช้ได้ ใช้โปรนั้น ไม่งั้น (ไม่ได้เลือก/เลือกโปรที่ใช้ไม่ได้) ใช้โปรที่ดีที่สุด
    /// ตรวจจากฝั่ง server ทุกครั้ง ไม่เชื่อค่าที่ส่งมาจากหน้าเว็บ
    /// </summary>
    public (AppliedPromotion? Chosen, IReadOnlyList<AppliedPromotion> Eligible) Resolve(
        PromotionData data, PromotionContext booking, int? selectedPromotionId)
    {
        var eligible = FindEligible(data, booking, availability.HotelToday());
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

    /// <summary>เหตุผลที่โปรนี้ใช้กับการจองไม่ได้ (null = ใช้ได้) เป็นกติกาเดียวที่ใช้ทั้งระบบ</summary>
    public static string? IneligibleReason(Promotion promotion, PromotionContext booking, CustomerHistory customer, DateOnly today)
    {
        // เงื่อนไขส่วนกลาง: ช่วงวันที่เข้าพัก + ยอดขั้นต่ำ
        if (promotion.StartDate.HasValue && booking.CheckIn < promotion.StartDate.Value)
            return $"ใช้ได้เมื่อเข้าพักตั้งแต่ {ThaiText.Date(promotion.StartDate)}";
        if (promotion.EndDate.HasValue && booking.CheckIn > promotion.EndDate.Value)
            return $"หมดช่วงเข้าพักแล้ว ({ThaiText.Date(promotion.EndDate)})";
        if (promotion.MinSpend.HasValue && booking.RoomSubtotal < promotion.MinSpend.Value)
            return $"ค่าห้องต้องถึง ฿{promotion.MinSpend:N0} (ตอนนี้ ฿{booking.RoomSubtotal:N0})";

        var x = promotion.RuleValue ?? 0;
        var daysBeforeCheckIn = booking.CheckIn.DayNumber - today.DayNumber;
        var nights = booking.CheckOut.DayNumber - booking.CheckIn.DayNumber;
        const string needCustomer = "กรอกชื่อ นามสกุล และเบอร์เพื่อเช็คสิทธิ์";

        return promotion.RuleType switch
        {
            PromotionRules.EarlyBird => daysBeforeCheckIn >= x ? null
                : $"ต้องจองล่วงหน้า {x} วัน (ตอนนี้ {daysBeforeCheckIn} วัน)",
            PromotionRules.LastMinute => daysBeforeCheckIn <= x ? null
                : $"ต้องจองก่อนเข้าพักไม่เกิน {x} วัน (ตอนนี้ {daysBeforeCheckIn} วัน)",
            PromotionRules.LongStay => nights >= x ? null : $"ต้องพัก {x} คืนขึ้นไป (ตอนนี้ {nights} คืน)",
            PromotionRules.Weekday => AllNightsAreWeekdays(booking.CheckIn, nights) ? null : "มีคืนวันศุกร์หรือเสาร์",
            PromotionRules.BirthdayMonth => customer.BirthMonth == booking.CheckIn.Month ? null
                : !customer.IsKnown ? needCustomer
                : customer.BirthMonth == null ? "ไม่มีวันเกิดของลูกค้าในระบบ" : "ไม่ใช่เดือนเกิดของลูกค้า",
            PromotionRules.FirstStay => !customer.IsKnown ? needCustomer
                : customer.ActiveBookingCount == 0 ? null : "ลูกค้าเคยจองแล้ว",
            PromotionRules.LoyalGuest => customer.CompletedStayCount >= x ? null
                : !customer.IsKnown ? needCustomer : $"เข้าพักจบแล้ว {customer.CompletedStayCount} จาก {x} ครั้ง",
            PromotionRules.Seasonal => null,
            _ => "ยังไม่ได้ตั้งเงื่อนไข"
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
