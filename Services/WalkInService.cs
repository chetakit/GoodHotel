using GoodHotel.Models;
using GoodHotel.Models.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Services;

/// <param name="Candidates">ลูกค้าที่ใช้เบอร์นี้แต่ระบบตัดสินเองไม่ได้ว่าเป็นคนไหน ให้พนักงานเลือก</param>
public sealed record WalkInResult(bool Succeeded, string Message, int? BookingId = null,
    IReadOnlyList<WalkInCustomer>? Candidates = null);

/// <param name="IsFinal">false = ยังไม่รู้ว่าเป็นลูกค้าคนไหน ส่วนลดอาจเปลี่ยน</param>
public sealed record WalkInQuote(bool Succeeded, string? Message, int? Nights = null, decimal? RoomSubtotal = null,
    string? PromotionName = null, decimal? Discount = null, decimal? ExtraBed = null, decimal? Total = null, bool IsFinal = false);

public sealed record WalkInCustomer(int UserId, string? FirstName, string? LastName, bool CanLogin);

/// <summary>
/// ลูกค้า Walk-in: พนักงานต้อนรับจองแทนลูกค้าที่เคาน์เตอร์ แล้วรับเงินสดและ check-in ให้ในขั้นตอนเดียว
/// 1) หา/สร้างบัญชีลูกค้าจากเบอร์โทรและชื่อ  2) จองห้องคืนนี้  3) รับเงินสด  4) check-in
/// </summary>
public sealed class WalkInService(
    GoodHotelDbContext db,
    BookingService bookings,
    PaymentService payments,
    StayService stays,
    BookingCancellationService cancellation,
    RoomAvailabilityService availability)
{
    public async Task<WalkInResult> CreateAsync(WalkInViewModel form, int staffId, CancellationToken ct)
    {
        var phone = form.Phone!.Trim();
        var candidates = await db.Users.AsNoTracking()
            .Where(u => u.Role == "Customer" && u.Phone == phone)
            .OrderBy(u => u.UserId)
            .Select(u => new WalkInCustomer(u.UserId, u.FirstName, u.LastName, u.Password != null))
            .ToListAsync(ct);
        var match = MatchCustomer(candidates, form.FirstName!, form.LastName!,
            form.CustomerChoiceFor == form.CustomerKey ? form.CustomerChoice : null);
        if (match.NeedsChoice)
        {
            return new WalkInResult(false, "เบอร์นี้มีลูกค้าอยู่ในระบบแล้ว กรุณาเลือกว่าเป็นลูกค้าคนไหน", Candidates: candidates);
        }

        // ยอดที่พนักงานเห็นต้องตรงกับยอดที่จะบันทึก ตรวจก่อนสร้างลูกค้าและการจอง
        if (form.ExpectedTotal.HasValue)
        {
            var quote = await PriceAsync(form, match.UserId, isNewCustomer: match.UserId == null, ct);
            if (!quote.CanConfirm) return new WalkInResult(false, quote.StatusMessage ?? "จองห้องไม่สำเร็จ");
            if (quote.TotalAmount != form.ExpectedTotal)
                return new WalkInResult(false, $"ยอดเปลี่ยนเป็น ฿{quote.TotalAmount:N2} กรุณาตรวจยอดแล้วกดยืนยันอีกครั้ง");
        }

        var customerId = match.UserId ?? await CreateCustomerAsync(form, ct);

        var booking = await bookings.CreateAsync(customerId, new CreateBookingViewModel
        {
            RoomId = form.RoomId!.Value,
            CheckIn = availability.HotelToday(),
            CheckOut = form.CheckOut,
            Guests = form.Guests,
            ExtraBedCount = form.ExtraBed ? 1 : 0
        }, ct);

        if (booking.Status != BookingSaveStatus.Created || booking.BookingId is not int bookingId)
        {
            return new WalkInResult(false, booking.Message ?? "จองห้องไม่สำเร็จ");
        }

        var payment = await payments.RecordDeskCashAsync(bookingId, staffId, ct);
        if (!payment.Succeeded)
        {
            // รับเงินสดทีหลังไม่ได้ (การจองทั่วไปชำระออนไลน์เท่านั้น) จึงยกเลิกให้แล้วทำใหม่ ไม่ปล่อยการจองค้าง
            // ถ้ายกเลิกไม่ได้เพราะเงินถูกบันทึกไปแล้วจริงๆ การจองยังอยู่ ให้พนักงานเปิดดู
            var cancelled = await cancellation.CancelByStaffAsync(bookingId, ct);
            return cancelled.Succeeded
                ? new WalkInResult(false, $"บันทึกเงินสดไม่สำเร็จ ({payment.Message}) ยกเลิกการจอง #{bookingId} แล้ว กรุณาทำรายการใหม่")
                : new WalkInResult(false, $"บันทึกเงินสดไม่สำเร็จ ({payment.Message}) กรุณาตรวจสอบการจองนี้", bookingId);
        }

        // เช็คอินไม่ผ่าน: จ่ายเงินแล้ว การจองยังอยู่ พนักงานกดเช็คอินต่อที่หน้ารายละเอียดการจองได้
        var checkInError = await stays.CheckInAsync(bookingId, staffId, "Walk-in", closeRefund: false, ct);
        if (checkInError != null)
        {
            return new WalkInResult(false, $"จองและรับเงินแล้ว แต่ check-in ไม่สำเร็จ: {checkInError}", bookingId);
        }

        return new WalkInResult(true,
            $"Walk-in สำเร็จ: การจอง #{bookingId} ห้องพร้อมเข้าพัก รับเงินสด ฿{booking.Amount:N2}", bookingId);
    }

    /// <summary>ยอดที่ต้องเก็บ คำนวณก่อนยืนยัน (ไม่บันทึกอะไร) ใช้สูตรเดียวกับตอนจองจริง</summary>
    public async Task<WalkInQuote> QuoteAsync(WalkInViewModel form, CancellationToken ct)
    {
        if (form.RoomId is not > 0 || form.CheckOut == null) return new WalkInQuote(false, "เลือกห้องเพื่อดูยอดที่ต้องเก็บ");

        int? customerId = null;
        var isFinal = false;
        var phone = form.Phone?.Trim();
        if (!string.IsNullOrEmpty(phone) && !string.IsNullOrWhiteSpace(form.FirstName) && !string.IsNullOrWhiteSpace(form.LastName))
        {
            var candidates = await db.Users.AsNoTracking()
                .Where(u => u.Role == "Customer" && u.Phone == phone)
                .Select(u => new WalkInCustomer(u.UserId, u.FirstName, u.LastName, u.Password != null))
                .ToListAsync(ct);
            var match = MatchCustomer(candidates, form.FirstName, form.LastName,
                form.CustomerChoiceFor == form.CustomerKey ? form.CustomerChoice : null);
            customerId = match.UserId;
            isFinal = !match.NeedsChoice;
        }

        var quote = await PriceAsync(form, customerId, isNewCustomer: isFinal && customerId == null, ct);
        return quote.CanConfirm
            ? new WalkInQuote(true, null, quote.NightCount, quote.RoomSubtotal, quote.PromotionName,
                quote.DiscountAmount, quote.ExtraBedAmount, quote.TotalAmount, isFinal)
            : new WalkInQuote(false, quote.StatusMessage ?? "คำนวณยอดไม่ได้");
    }

    private Task<BookingConfirmationViewModel> PriceAsync(WalkInViewModel form, int? customerId, bool isNewCustomer,
        CancellationToken ct) =>
        bookings.PrepareConfirmationAsync(new CreateBookingViewModel
        {
            RoomId = form.RoomId!.Value,
            CheckIn = availability.HotelToday(),
            CheckOut = form.CheckOut,
            Guests = form.Guests,
            ExtraBedCount = form.ExtraBed ? 1 : 0
        }, customerId, ct, isNewCustomer);

    /// <summary>
    /// เบอร์โทรซ้ำกันได้หลายบัญชี จึงต้องดูชื่อด้วย ไม่งั้นการเข้าพักไปอยู่ในบัญชีคนอื่น
    /// - ไม่มีใครใช้เบอร์นี้ = ลูกค้าใหม่, ชื่อตรงคนเดียว = คนนั้น, นอกนั้นพนักงานต้องเลือก
    /// </summary>
    /// <param name="choice">ที่พนักงานเลือก: null = ยังไม่เลือก, 0 = ลูกค้าใหม่, อื่นๆ = UserId (ต้องอยู่ในรายการเบอร์นี้)</param>
    public static (int? UserId, bool NeedsChoice) MatchCustomer(IReadOnlyList<WalkInCustomer> candidates,
        string firstName, string lastName, int? choice)
    {
        if (candidates.Count == 0 || choice == 0) return (null, false);
        if (choice.HasValue && candidates.Any(c => c.UserId == choice)) return (choice, false);
        var sameName = candidates.Where(c => SameName(c.FirstName, firstName) && SameName(c.LastName, lastName)).ToList();
        return sameName.Count == 1 ? (sameName[0].UserId, false) : (null, true);
    }

    private static bool SameName(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private async Task<int> CreateCustomerAsync(WalkInViewModel form, CancellationToken ct)
    {
        var phone = form.Phone!.Trim();
        // อีเมลไม่บังคับ ถ้าซ้ำกับบัญชีอื่นจะไม่บันทึก เพื่อไม่ให้ชนกับการ login ของคนอื่น
        var email = string.IsNullOrWhiteSpace(form.Email) ? null : form.Email.Trim();
        if (email != null && await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            email = null;
        }

        var lastId = await db.Users.MaxAsync(u => (int?)u.UserId, ct) ?? 0;
        var customer = new User
        {
            UserId = lastId + 1,
            FirstName = form.FirstName!.Trim(),
            LastName = form.LastName!.Trim(),
            Phone = phone,
            Email = email,
            Role = "Customer",
            Password = null, // ลูกค้า Walk-in ยังไม่มีรหัสผ่าน จึง login ไม่ได้จนกว่าจะสมัครเอง
            IsActive = true
        };
        db.Users.Add(customer);
        await db.SaveChangesAsync(ct);
        return customer.UserId;
    }
}
