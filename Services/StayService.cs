using System.Data;
using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Services;

public sealed class StayService(GoodHotelDbContext db, HousekeepingService housekeeping, TimeProvider timeProvider)
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

    public async Task<string?> CheckInAsync(int bookingId, int actorId, string? note, CancellationToken ct)
    {
        if (note?.Length > 2000) return "หมายเหตุต้องไม่เกิน 2000 ตัวอักษร";
        var roomId = await db.Bookings.AsNoTracking().Where(b => b.BookingId == bookingId)
            .Select(b => b.RoomId).SingleOrDefaultAsync(ct);
        if (!roomId.HasValue) return "ไม่พบการจองหรือห้อง";

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (!await RoomAvailabilityService.LockRoomAsync(db, tx, roomId.Value, ct)) return "ไม่พบห้อง";
        var booking = await db.Bookings.AsNoTracking()
            .Where(b => b.BookingId == bookingId)
            .Select(b => new { b.RoomId, b.CheckIn, b.CheckOut, b.Status, RoomStatus = b.Room == null ? null : b.Room.Status })
            .SingleOrDefaultAsync(ct);
        if (booking == null || booking.RoomId != roomId) return "ข้อมูลการจองหรือห้องเปลี่ยนไป กรุณาลองใหม่";
        if (booking.Status == BookingStatuses.Cancelled) return "การจองนี้ถูกยกเลิกแล้ว";
        var today = HotelToday();
        if (!booking.CheckIn.HasValue || !booking.CheckOut.HasValue ||
            booking.CheckOut <= booking.CheckIn || today >= booking.CheckOut)
            return "วันที่การจองไม่ถูกต้อง หรือเลยวันออกไปแล้ว";
        if (booking.RoomStatus != "Available") return "ห้องนี้ไม่พร้อมใช้งาน";
        var early = today < booking.CheckIn;
        if (early) return "ยังเช็คอินก่อนวันเข้าพักไม่ได้";
        if (!await PaymentService.IsPaidAsync(db, bookingId, ct))
            return "ยังไม่ได้รับชำระเงิน กรุณายืนยันการชำระเงินก่อนเช็คอิน";
        if (await db.BookingStays.AnyAsync(s => s.BookingId == bookingId, ct))
            return "การจองนี้เช็คอินไปแล้ว";
        if (await db.BookingStays.AnyAsync(s => s.Status == "CheckedIn" && s.Booking.RoomId == roomId, ct))
            return "ห้องนี้มีแขกเข้าพักอยู่แล้ว";
        if (await db.HousekeepingTasks.AnyAsync(t => t.RoomId == roomId &&
            t.TaskType == "CheckoutCleaning" && t.Status != "Verified", ct))
            return "ห้องนี้ยังรอทำความสะอาด/รอตรวจ";
        if (await db.MaintenanceReports.AnyAsync(r =>
            r.RoomId == roomId && r.Status != MaintenanceReport.Resolved, ct))
            return "ห้องนี้ติดงานซ่อม";

        db.BookingStays.Add(new BookingStay
        {
            BookingId = bookingId, Status = "CheckedIn", CheckedInAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            CheckedInByUserId = actorId, IsEarlyCheckIn = early, CheckInNote = note?.Trim()
        });
        // มาพักแล้ว: คำขอคืนเงินที่ยังรออยู่ถูกปิดอัตโนมัติ (บัญชีจะอนุมัติคืนเงินหลังเข้าพักไม่ได้)
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

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (!await RoomAvailabilityService.LockRoomAsync(db, tx, roomId.Value, ct)) return "ไม่พบห้อง";
        var booking = await db.Bookings.AsNoTracking().Where(b => b.BookingId == bookingId)
            .Select(b => new { b.RoomId }).SingleOrDefaultAsync(ct);
        if (booking == null || booking.RoomId != roomId) return "ข้อมูลการจองหรือห้องเปลี่ยนไป กรุณาลองใหม่";
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var updated = await db.BookingStays.Where(s => s.BookingId == bookingId && s.Status == "CheckedIn")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "CheckedOut")
                .SetProperty(x => x.CheckedOutAtUtc, now)
                .SetProperty(x => x.CheckedOutByUserId, actorId), ct);
        if (updated != 1) return "ไม่มีแขกที่เข้าพักอยู่ให้เช็คเอาท์";

        await housekeeping.CreateCheckoutCleaningAsync(roomId.Value, bookingId, actorId, now, ct);
        await tx.CommitAsync(ct);
        return null;
    }

}
