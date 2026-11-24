using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Services;

/// <summary>ข้อมูลการจองที่ใช้ตรวจเงื่อนไขโปร</summary>
public sealed record PromotionContext(
    int? CustomerId,
    DateOnly CheckIn,
    DateOnly CheckOut,
    decimal RoomSubtotal,
    bool IsNewCustomer = false);

public sealed record AppliedPromotion(int PromotionId, string Name, decimal DiscountAmount);

/// <summary>
/// โปรโมชันของการจอง
/// กติกา: 1 การจองใช้ได้ 1 โปร ลูกค้าเลือกเองจากโปรที่เข้าเงื่อนไข (ค่าเริ่มต้น = โปรที่ลดได้มากที่สุด)
/// ส่วนลดคิดจากค่าห้องเท่านั้น (ไม่รวมเตียงเสริม)
/// </summary>
public sealed class PromotionService(GoodHotelDbContext db, RoomAvailabilityService availability, ParallelReader reader)
{
    /// <summary>โปรทั้งหมดที่การจองนี้ใช้ได้ เรียงจากลดมากไปน้อย</summary>
    public async Task<IReadOnlyList<AppliedPromotion>> FindEligibleAsync(PromotionContext booking, CancellationToken ct)
    {
        Task<List<Promotion>> Candidates(GoodHotelDbContext c) => c.Promotions.AsNoTracking()
            .Where(p => p.IsActive && p.RuleType != null && p.DiscountType != null && p.DiscountValue > 0)
            .ToListAsync(ct);

        List<Promotion> candidates;
        CustomerHistory customer;
        if (db.Database.CurrentTransaction != null)
        {
            // กำลังจองอยู่ใน transaction ต้องอ่านผ่าน connection เดิมทีละ query
            candidates = await Candidates(db);
            customer = await LoadCustomerHistoryAsync(db, booking, ct);
        }
        else
        {
            var candidatesTask = reader.Run(Candidates);
            var customerTask = reader.Run(c => LoadCustomerHistoryAsync(c, booking, ct));
            await Task.WhenAll(candidatesTask, customerTask);
            candidates = candidatesTask.Result;
            customer = customerTask.Result;
        }
        var today = availability.HotelToday();

        return candidates
            .Where(p => IsEligible(p, booking, customer, today))
            .Select(p => new AppliedPromotion(p.PromotionId, p.PromotionName ?? "Promotion", CalculateDiscount(p, booking.RoomSubtotal)))
            .Where(p => p.DiscountAmount > 0)
            .OrderByDescending(p => p.DiscountAmount)
            .ThenBy(p => p.PromotionId)
            .ToList();
    }

    public async Task<AppliedPromotion?> FindBestAsync(PromotionContext booking, CancellationToken ct) =>
        (await FindEligibleAsync(booking, ct)).FirstOrDefault();

    /// <summary>
    /// โปรที่จะใช้จริง: ถ้าลูกค้าเลือกโปรที่ใช้ได้ ใช้โปรนั้น ไม่งั้น (ไม่ได้เลือก/เลือกโปรที่ใช้ไม่ได้) ใช้โปรที่ดีที่สุด
    /// ตรวจจากฝั่ง server ทุกครั้ง ไม่เชื่อค่าที่ส่งมาจากหน้าเว็บ
    /// </summary>
    public async Task<(AppliedPromotion? Chosen, IReadOnlyList<AppliedPromotion> Eligible)> ResolveAsync(
        PromotionContext booking, int? selectedPromotionId, CancellationToken ct)
    {
        var eligible = await FindEligibleAsync(booking, ct);
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

    private sealed record CustomerHistory(bool IsKnown, int? BirthMonth, int ActiveBookingCount, int CompletedStayCount);

    // ทุกตัวเลขอยู่ใน query เดียว (แต่ละ query ไปฐานข้อมูลใช้ ~200ms)
    private static async Task<CustomerHistory> LoadCustomerHistoryAsync(
        GoodHotelDbContext c, PromotionContext booking, CancellationToken ct)
    {
        var customerId = booking.CustomerId;
        if (customerId is not > 0)
        {
            // ลูกค้าใหม่ที่กำลังจะถูกสร้าง (Walk-in) = รู้แน่ว่ายังไม่เคยจอง
            return new CustomerHistory(booking.IsNewCustomer, null, 0, 0);
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
