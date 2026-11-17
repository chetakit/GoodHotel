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
        if (note?.Length > 2000) return "Check-in note must be at most 2000 characters.";
        var roomId = await db.Bookings.AsNoTracking().Where(b => b.BookingId == bookingId)
            .Select(b => b.RoomId).SingleOrDefaultAsync(ct);
        if (!roomId.HasValue) return "Booking or room not found.";

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (!await RoomAvailabilityService.LockRoomAsync(db, tx, roomId.Value, ct)) return "Room not found.";
        var booking = await db.Bookings.AsNoTracking()
            .Where(b => b.BookingId == bookingId)
            .Select(b => new { b.RoomId, b.CheckIn, b.CheckOut, b.Status, RoomStatus = b.Room == null ? null : b.Room.Status })
            .SingleOrDefaultAsync(ct);
        if (booking == null || booking.RoomId != roomId) return "Booking or room changed. Please retry.";
        if (booking.Status == BookingStatuses.Cancelled) return "This booking was cancelled.";
        var today = HotelToday();
        if (!booking.CheckIn.HasValue || !booking.CheckOut.HasValue ||
            booking.CheckOut <= booking.CheckIn || today >= booking.CheckOut)
            return "The booking dates are invalid or the check-out date has already arrived.";
        if (booking.RoomStatus != "Available") return "The room is not available.";
        var early = today < booking.CheckIn;
        if (early) return "Early check-in is unavailable until booking availability accounts for active stays.";
        if (!await PaymentService.IsPaidAsync(db, bookingId, ct))
            return "ยังไม่ได้รับชำระเงิน กรุณายืนยันการชำระเงินก่อนเช็คอิน";
        if (await db.BookingStays.AnyAsync(s => s.BookingId == bookingId, ct))
            return "This booking already has a stay.";
        if (await db.BookingStays.AnyAsync(s => s.Status == "CheckedIn" && s.Booking.RoomId == roomId, ct))
            return "The room already has an active stay.";
        if (await db.HousekeepingTasks.AnyAsync(t => t.RoomId == roomId &&
            t.TaskType == "CheckoutCleaning" && t.Status != "Verified", ct))
            return "The room has a checkout cleaning task awaiting verification.";
        if (await db.MaintenanceReports.AnyAsync(r =>
            r.RoomId == roomId && r.Status != MaintenanceReport.Resolved, ct))
            return "The room is blocked by maintenance during this stay.";

        db.BookingStays.Add(new BookingStay
        {
            BookingId = bookingId, Status = "CheckedIn", CheckedInAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            CheckedInByUserId = actorId, IsEarlyCheckIn = early, CheckInNote = note?.Trim()
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return null;
    }

    public async Task<string?> CheckOutAsync(int bookingId, int actorId, CancellationToken ct)
    {
        var roomId = await db.Bookings.AsNoTracking().Where(b => b.BookingId == bookingId)
            .Select(b => b.RoomId).SingleOrDefaultAsync(ct);
        if (!roomId.HasValue) return "Booking or room not found.";

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (!await RoomAvailabilityService.LockRoomAsync(db, tx, roomId.Value, ct)) return "Room not found.";
        var booking = await db.Bookings.AsNoTracking().Where(b => b.BookingId == bookingId)
            .Select(b => new { b.RoomId }).SingleOrDefaultAsync(ct);
        if (booking == null || booking.RoomId != roomId) return "Booking or room changed. Please retry.";
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var updated = await db.BookingStays.Where(s => s.BookingId == bookingId && s.Status == "CheckedIn")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "CheckedOut")
                .SetProperty(x => x.CheckedOutAtUtc, now)
                .SetProperty(x => x.CheckedOutByUserId, actorId), ct);
        if (updated != 1) return "No active stay is available for check-out.";

        await housekeeping.CreateCheckoutCleaningAsync(roomId.Value, bookingId, actorId, now, ct);
        await tx.CommitAsync(ct);
        return null;
    }

}
