using GoodHotel.Models;
using GoodHotel.Models.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Services;

public sealed record WalkInResult(bool Succeeded, string Message, int? BookingId = null);

/// <summary>
/// ลูกค้า Walk-in: พนักงานต้อนรับจองแทนลูกค้าที่เคาน์เตอร์ แล้วรับเงินสดและ check-in ให้ในขั้นตอนเดียว
/// 1) หา/สร้างบัญชีลูกค้าจากเบอร์โทร  2) จองห้องคืนนี้  3) รับเงินสด  4) check-in
/// </summary>
public sealed class WalkInService(
    GoodHotelDbContext db,
    BookingService bookings,
    PaymentService payments,
    StayService stays,
    RoomAvailabilityService availability)
{
    public async Task<WalkInResult> CreateAsync(WalkInViewModel form, int staffId, CancellationToken ct)
    {
        var customerId = await FindOrCreateCustomerAsync(form, ct);

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

        // ถ้าขั้นตอนหลังจากนี้ไม่ผ่าน การจองยังอยู่ พนักงานไปทำต่อที่หน้า Booking Details ได้
        var payment = await payments.RecordDeskCashAsync(bookingId, staffId, ct);
        if (!payment.Succeeded)
        {
            return new WalkInResult(false, $"จองแล้ว แต่บันทึกการชำระเงินไม่สำเร็จ: {payment.Message}", bookingId);
        }

        var checkInError = await stays.CheckInAsync(bookingId, staffId, "Walk-in", ct);
        if (checkInError != null)
        {
            return new WalkInResult(false, $"จองและรับเงินแล้ว แต่ check-in ไม่สำเร็จ: {checkInError}", bookingId);
        }

        return new WalkInResult(true,
            $"Walk-in สำเร็จ: การจอง #{bookingId} ห้องพร้อมเข้าพัก รับเงินสด ฿{booking.Amount:N2}", bookingId);
    }

    /// <summary>ใช้เบอร์โทรเป็นตัวระบุลูกค้า ถ้าเคยมาแล้วจะใช้บัญชีเดิม</summary>
    private async Task<int> FindOrCreateCustomerAsync(WalkInViewModel form, CancellationToken ct)
    {
        var phone = form.Phone!.Trim();
        var existing = await db.Users
            .Where(u => u.Role == "Customer" && u.Phone == phone)
            .Select(u => (int?)u.UserId)
            .FirstOrDefaultAsync(ct);
        if (existing.HasValue)
        {
            return existing.Value;
        }

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
