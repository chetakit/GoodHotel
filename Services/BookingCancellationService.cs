using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Services;

public sealed record CancelBookingResult(bool Succeeded, string Message);

/// <summary>
/// ลูกค้ายกเลิกการจองของตัวเอง
/// เงื่อนไข: ต้องเป็นเจ้าของการจอง, ยังไม่ถูกยกเลิก, ยังไม่ check-in และต้องยกเลิกก่อนวันเข้าพัก
/// ถ้าจ่ายเงินแล้ว ระบบจะสร้างคำขอคืนเงินให้ฝ่ายบัญชีอนุมัติต่ออัตโนมัติ
/// </summary>
public sealed class BookingCancellationService(
    GoodHotelDbContext db,
    PaymentService payments,
    TimeProvider time)
{
    public static bool CanCancel(string status, DateOnly? checkIn, bool hasStay, DateOnly today) =>
        status == BookingStatuses.Booked &&
        !hasStay &&
        checkIn.HasValue &&
        checkIn.Value > today;

    public async Task<CancelBookingResult> CancelAsync(int bookingId, int customerId, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var today = RoomAvailabilityService.HotelDate(now);

        // อัปเดตด้วยคำสั่งเดียว เงื่อนไขทั้งหมดอยู่ใน WHERE จึงไม่เกิดปัญหากดยกเลิกซ้อนกัน
        var updated = await db.Bookings
            .Where(b => b.BookingId == bookingId &&
                        b.UserId == customerId &&
                        b.Status == BookingStatuses.Booked &&
                        b.CheckIn > today &&
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
        var refundMessage = await RequestRefundIfPaidAsync(bookingId, customerId, ct);
        return new CancelBookingResult(true, $"ยกเลิกการจอง #{bookingId} เรียบร้อยแล้ว {refundMessage}".Trim());
    }

    /// <summary>
    /// การจองถูกยกเลิกแล้ว: การชำระที่ยังรอยืนยัน (เช่น เลือกจ่ายเงินสดไว้) ไม่ต้องรอพนักงานหรือบัญชีตรวจอีก
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
    /// ถ้าจ่ายแล้ว ลูกค้าต้องยกเลิกเองเพื่อให้ระบบส่งคำขอคืนเงิน
    /// </summary>
    public async Task<CancelBookingResult> CancelByStaffAsync(int bookingId, CancellationToken ct)
    {
        if (await PaymentService.IsPaidAsync(db, bookingId, ct))
        {
            return new CancelBookingResult(false, "การจองนี้จ่ายเงินแล้ว ให้ลูกค้ายกเลิกเองเพื่อขอคืนเงิน");
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

        return updated == 1
            ? new CancelBookingResult(true, $"ยกเลิกการจอง #{bookingId} แล้ว ห้องกลับมาว่างให้จองได้")
            : new CancelBookingResult(false, "ยกเลิกไม่ได้: การจองนี้ถูกยกเลิกแล้วหรือเช็คอินไปแล้ว");
    }

    private async Task<string> RequestRefundIfPaidAsync(int bookingId, int customerId, CancellationToken ct)
    {
        var confirmedPayment = await db.Payments.AsNoTracking()
            .Where(p => p.BookingId == bookingId && p.VerifiedAtUtc != null)
            .OrderBy(p => p.PaymentId)
            .Select(p => new { p.PaymentId, p.Amount })
            .FirstOrDefaultAsync(ct);

        if (confirmedPayment?.Amount is not > 0)
        {
            return "";
        }

        var alreadyRefunded = await db.PaymentRefunds
            .Where(r => r.PaymentId == confirmedPayment.PaymentId && r.Status == "Approved")
            .SumAsync(r => (decimal?)r.Amount, ct) ?? 0m;
        var remaining = confirmedPayment.Amount.Value - alreadyRefunded;
        if (remaining <= 0)
        {
            return "";
        }

        var refund = await payments.RequestRefundAsync(
            confirmedPayment.PaymentId,
            customerId,
            remaining,
            $"ลูกค้ายกเลิกการจอง #{bookingId}",
            Guid.NewGuid().ToString("N"),
            ct);

        return refund.Succeeded
            ? $"ส่งคำขอคืนเงิน ฿{remaining:N2} ให้ฝ่ายบัญชีอนุมัติแล้ว"
            : $"แต่ส่งคำขอคืนเงินอัตโนมัติไม่สำเร็จ ({refund.Message}) กรุณาขอคืนเงินที่หน้า Payment details";
    }

    private async Task<string> ExplainWhyNotCancelledAsync(
        int bookingId, int customerId, DateOnly today, CancellationToken ct)
    {
        var booking = await db.Bookings.AsNoTracking()
            .Where(b => b.BookingId == bookingId && b.UserId == customerId)
            .Select(b => new
            {
                b.Status,
                b.CheckIn,
                HasStay = db.BookingStays.Any(s => s.BookingId == b.BookingId)
            })
            .SingleOrDefaultAsync(ct);

        if (booking == null) return "ไม่พบการจองนี้";
        if (booking.Status == BookingStatuses.Cancelled) return "การจองนี้ถูกยกเลิกไปแล้ว";
        if (booking.HasStay) return "เช็คอินแล้ว ไม่สามารถยกเลิกได้";
        if (booking.CheckIn <= today) return "ยกเลิกได้ก่อนวันเข้าพักเท่านั้น กรุณาติดต่อพนักงานต้อนรับ";
        return "ไม่สามารถยกเลิกการจองนี้ได้";
    }
}
