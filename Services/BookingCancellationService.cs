using System.Data;
using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Services;

public sealed record CancelBookingResult(bool Succeeded, string Message);

/// <summary>
/// ลูกค้ายกเลิกการจองของตัวเอง
/// เงื่อนไข: ต้องเป็นเจ้าของการจอง, ยังไม่ถูกยกเลิก, ยังไม่ check-in, ยังไม่เลยวันออก
/// และไม่มีคำขอคืนเงินที่รอฝ่ายบัญชีพิจารณา (กันกรณียกเลิกไปแล้วบัญชีมาอนุมัติทีหลัง)
/// ยกเลิกเอง = ไม่คืนเงิน ถ้าต้องการเงินคืนต้องส่งคำขอคืนเงินให้ฝ่ายบัญชีอนุมัติ (อนุมัติแล้วระบบยกเลิกการจองให้เอง)
/// </summary>
public sealed class BookingCancellationService(
    GoodHotelDbContext db,
    TimeProvider time)
{
    public static bool CanCancel(string status, DateOnly? checkOut, bool hasStay, bool refundPending, DateOnly today) =>
        status == BookingStatuses.Booked &&
        !hasStay &&
        !refundPending &&
        checkOut.HasValue &&
        checkOut.Value > today;

    public async Task<CancelBookingResult> CancelAsync(int bookingId, int customerId, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var today = RoomAvailabilityService.HotelDate(now);

        // ล็อกการจองก่อน แล้วค่อยอัปเดตโดยมีเงื่อนไขทั้งหมดใน WHERE
        // (ต้องล็อกเป็นคำสั่งแยก ไม่งั้นเงื่อนไขอาจอ่านข้อมูลก่อนที่การเช็คอินที่กำลังทำอยู่จะบันทึกเสร็จ)
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await PaymentService.LockBookingAsync(db, bookingId, ct);
        var updated = await db.Bookings
            .Where(b => b.BookingId == bookingId &&
                        b.UserId == customerId &&
                        b.Status == BookingStatuses.Booked &&
                        b.CheckOut > today &&
                        !db.PaymentRefunds.Any(r => r.Payment.BookingId == b.BookingId &&
                                                    r.Status == PaymentService.RefundRequested) &&
                        !db.BookingStays.Any(s => s.BookingId == b.BookingId))
            .ExecuteUpdateAsync(b => b
                .SetProperty(x => x.Status, BookingStatuses.Cancelled)
                .SetProperty(x => x.CancelledAtUtc, now)
                .SetProperty(x => x.CancelReason, CancelReasons.Customer), ct);

        if (updated == 0)
        {
            return new CancelBookingResult(false, await ExplainWhyNotCancelledAsync(bookingId, customerId, today, ct));
        }

        await CloseOpenPaymentsAsync(bookingId, ct);
        await tx.CommitAsync(ct);
        var wasPaid = await PaymentService.IsPaidAsync(db, bookingId, ct);
        return new CancelBookingResult(true, wasPaid
            ? $"ยกเลิกการจอง #{bookingId} เรียบร้อยแล้ว (ยกเลิกเองไม่คืนเงิน)"
            : $"ยกเลิกการจอง #{bookingId} เรียบร้อยแล้ว");
    }

    /// <summary>
    /// การจองถูกยกเลิกแล้ว: การชำระที่ยังรอยืนยัน (รายการเงินสดเก่าที่ค้าง) ไม่ต้องรอพนักงานหรือบัญชีตรวจอีก
    /// ไม่งั้นจะค้างอยู่ในคิว "สลิปรอตรวจ" ทั้งที่การจองไม่มีแล้ว
    /// </summary>
    private Task<int> CloseOpenPaymentsAsync(int bookingId, CancellationToken ct) =>
        db.Payments
            .Where(p => p.BookingId == bookingId && p.VerifiedAtUtc == null &&
                        (p.PaymentStatus == "Pending" || p.PaymentStatus == "Submitted"))
            .ExecuteUpdateAsync(p => p.SetProperty(x => x.PaymentStatus, "Cancelled"), ct);

    /// <summary>
    /// พนักงานต้อนรับยกเลิกการจองแทนลูกค้า (เช่น ลูกค้าไม่มาเข้าพัก / จองแล้วไม่จ่ายเงิน)
    /// ยกเลิกได้เฉพาะการจองที่ยังไม่จ่ายเงินและยังไม่เช็คอิน
    /// ถ้าจ่ายแล้ว ให้ลูกค้าขอคืนเงิน (ฝ่ายบัญชีอนุมัติ) หรือยกเลิกเองแบบไม่คืนเงิน
    /// </summary>
    public async Task<CancelBookingResult> CancelByStaffAsync(int bookingId, CancellationToken ct)
    {
        // ล็อกการจองก่อนเช็คการจ่าย (ตอนลูกค้าจ่ายก็ล็อกแถวเดียวกัน)
        // ไม่งั้นลูกค้าจ่ายแทรกระหว่างเช็คกับยกเลิก การจองที่จ่ายแล้วจะถูกยกเลิก
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (!await PaymentService.LockBookingAsync(db, bookingId, ct))
            return new CancelBookingResult(false, "ไม่พบการจองนี้");
        if (await PaymentService.IsPaidAsync(db, bookingId, ct))
        {
            return new CancelBookingResult(false,
                "การจองนี้ชำระเงินแล้ว ลูกค้าขอคืนเงินหรือยกเลิกเองได้ที่หน้าการจองของฉัน");
        }

        var updated = await db.Bookings
            .Where(b => b.BookingId == bookingId &&
                        b.Status == BookingStatuses.Booked &&
                        !db.BookingStays.Any(s => s.BookingId == b.BookingId))
            .ExecuteUpdateAsync(b => b
                .SetProperty(x => x.Status, BookingStatuses.Cancelled)
                .SetProperty(x => x.CancelledAtUtc, time.GetUtcNow().UtcDateTime)
                .SetProperty(x => x.CancelReason, CancelReasons.Staff), ct);

        if (updated == 1) await CloseOpenPaymentsAsync(bookingId, ct);
        await tx.CommitAsync(ct);

        return updated == 1
            ? new CancelBookingResult(true, $"ยกเลิกการจอง #{bookingId} แล้ว ห้องกลับมาว่างให้จองได้")
            : new CancelBookingResult(false, "ยกเลิกไม่ได้: การจองนี้ถูกยกเลิกแล้วหรือเช็คอินไปแล้ว");
    }

    private async Task<string> ExplainWhyNotCancelledAsync(
        int bookingId, int customerId, DateOnly today, CancellationToken ct)
    {
        var booking = await db.Bookings.AsNoTracking()
            .Where(b => b.BookingId == bookingId && b.UserId == customerId)
            .Select(b => new
            {
                b.Status,
                b.CheckOut,
                HasStay = db.BookingStays.Any(s => s.BookingId == b.BookingId),
                RefundPending = db.PaymentRefunds.Any(r => r.Payment.BookingId == b.BookingId &&
                                                           r.Status == PaymentService.RefundRequested)
            })
            .SingleOrDefaultAsync(ct);

        if (booking == null) return "ไม่พบการจองนี้";
        if (booking.Status == BookingStatuses.Cancelled) return "การจองนี้ถูกยกเลิกไปแล้ว";
        if (booking.HasStay) return "เช็คอินแล้ว ไม่สามารถยกเลิกได้";
        if (booking.RefundPending) return "มีคำขอคืนเงินรอฝ่ายบัญชีพิจารณาอยู่ กรุณารอผลก่อน";
        if (booking.CheckOut <= today) return "เลยวันออกของการจองนี้แล้ว";
        return "ไม่สามารถยกเลิกการจองนี้ได้";
    }
}
