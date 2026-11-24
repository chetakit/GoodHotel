using System.Data;
using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Services;

public sealed class StayService(GoodHotelDbContext db, HousekeepingService housekeeping, TimeProvider timeProvider, ParallelReader reader)
{
    private static readonly TimeZoneInfo HotelTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok");

    public DateOnly HotelToday() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), HotelTimeZone).DateTime);

    public (DateTime StartUtc, DateTime EndUtc) HotelDayUtcBounds()
    {
        var start = HotelToday().ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return (TimeZoneInfo.ConvertTimeToUtc(start, HotelTimeZone),
            TimeZoneInfo.ConvertTimeToUtc(start.AddDays(1), HotelTimeZone));
    }

    public async Task<string?> CheckInAsync(int bookingId, int actorId, string? note, bool closeRefund,
        CancellationToken ct)
    {
        if (note?.Length > 2000) return "หมายเหตุต้องไม่เกิน 2000 ตัวอักษร";
        var roomId = await db.Bookings.AsNoTracking().Where(b => b.BookingId == bookingId)
            .Select(b => b.RoomId).SingleOrDefaultAsync(ct);
        if (!roomId.HasValue) return "ไม่พบการจองหรือห้อง";

        // ล็อกห้องแล้วล็อกการจอง (ลำดับเดียวกับที่อื่น) จากนั้นตรวจทุกอย่างพร้อมกัน
        // จ่ายเงิน ยกเลิก ขอคืนเงิน ย้ายห้อง แจ้งซ่อม เช็คเอาท์ ต้องล็อกแถวใดแถวหนึ่งนี้ก่อน ผลจึงไม่เปลี่ยนระหว่างนี้
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var room = await RoomAvailabilityService.LockAndLoadRoomAsync(db, roomId.Value, ct);
        if (room == null) return "ไม่พบห้อง";
        var booking = await PaymentService.LockAndLoadBookingAsync(db, bookingId, ct);
        if (booking == null || booking.RoomId != roomId) return "ข้อมูลการจองหรือห้องเปลี่ยนไป กรุณาลองใหม่";
        if (booking.Status == BookingStatuses.Cancelled) return "การจองนี้ถูกยกเลิกแล้ว";
        var today = HotelToday();
        if (!booking.CheckIn.HasValue || !booking.CheckOut.HasValue ||
            booking.CheckOut <= booking.CheckIn || today >= booking.CheckOut)
            return "วันที่การจองไม่ถูกต้อง หรือเลยวันออกไปแล้ว";
        if (room.Status != "Available") return "ห้องนี้ไม่พร้อมใช้งาน";
        var early = today < booking.CheckIn;
        if (early) return "ยังเช็คอินก่อนวันเข้าพักไม่ได้";
        var paidTask = reader.Run(c => PaymentService.IsPaidAsync(c, bookingId, ct));
        var checksTask = reader.Run(c => c.Rooms.Where(r => r.RoomId == roomId).Select(r => new
        {
            AlreadyIn = c.BookingStays.Any(s => s.BookingId == bookingId),
            Occupied = c.BookingStays.Any(s => s.Status == "CheckedIn" && s.Booking.RoomId == roomId),
            Cleaning = c.HousekeepingTasks.Any(t => t.RoomId == roomId &&
                t.TaskType == "CheckoutCleaning" && t.Status != "Verified"),
            Repair = c.MaintenanceReports.Any(m => m.RoomId == roomId && m.Status != MaintenanceReport.Resolved),
            RefundPending = c.PaymentRefunds.Any(f => f.Payment.BookingId == bookingId &&
                f.Status == PaymentService.RefundRequested)
        }).SingleAsync(ct));
        await Task.WhenAll(paidTask, checksTask);
        var checks = checksTask.Result;
        if (!paidTask.Result) return "ยังไม่ได้ชำระเงิน ให้ลูกค้าชำระออนไลน์ก่อนเช็คอิน";
        if (checks.AlreadyIn) return "การจองนี้เช็คอินไปแล้ว";
        if (checks.Occupied) return "ห้องนี้มีแขกเข้าพักอยู่แล้ว";
        if (checks.Cleaning) return "ห้องนี้ยังรอทำความสะอาด/รอตรวจ";
        if (checks.Repair) return "ห้องนี้ติดงานซ่อม";

        // มีคำขอคืนเงินค้าง: พนักงานต้องยืนยันก่อน ไม่งั้นคำขอของลูกค้าถูกปิดโดยไม่มีใครรู้
        // (ตรวจฝั่ง server ด้วย เผื่อลูกค้าเพิ่งส่งคำขอหลังจากพนักงานเปิดหน้าค้างไว้)
        if (!closeRefund && checks.RefundPending)
            return "การจองนี้มีคำขอคืนเงินรอฝ่ายบัญชีพิจารณา ถ้าเช็คอินคำขอจะถูกปิด กรุณาแจ้งลูกค้าแล้วกดเช็คอินอีกครั้งเพื่อยืนยัน";

        db.BookingStays.Add(new BookingStay
        {
            BookingId = bookingId, Status = "CheckedIn", CheckedInAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            CheckedInByUserId = actorId, IsEarlyCheckIn = early, CheckInNote = note?.Trim()
        });
        // มาพักแล้ว: คำขอคืนเงินที่ยังรออยู่ถูกปิดอัตโนมัติ (บัญชีจะอนุมัติคืนเงินหลังเข้าพักไม่ได้)
        if (checks.RefundPending)
            await PaymentService.CloseOpenRefundsAsync(db, bookingId, actorId, timeProvider.GetUtcNow().UtcDateTime, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return null;
    }

    public async Task<string?> CheckOutAsync(int bookingId, int actorId, CancellationToken ct)
    {
        var roomId = await db.Bookings.AsNoTracking().Where(b => b.BookingId == bookingId)
            .Select(b => b.RoomId).SingleOrDefaultAsync(ct);
        if (!roomId.HasValue) return "ไม่พบการจองหรือห้อง";

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (!await RoomAvailabilityService.LockRoomAsync(db, tx, roomId.Value, ct)) return "ไม่พบห้อง";
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var updated = await db.BookingStays.Where(s => s.BookingId == bookingId && s.Status == "CheckedIn" &&
                db.Bookings.Any(b => b.BookingId == bookingId && b.RoomId == roomId))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "CheckedOut")
                .SetProperty(x => x.CheckedOutAtUtc, now)
                .SetProperty(x => x.CheckedOutByUserId, actorId), ct);
        if (updated != 1) return "ไม่มีแขกที่เข้าพักอยู่ให้เช็คเอาท์";

        await housekeeping.CreateCheckoutCleaningAsync(roomId.Value, bookingId, actorId, now, ct);
        await tx.CommitAsync(ct);
        return null;
    }

}
