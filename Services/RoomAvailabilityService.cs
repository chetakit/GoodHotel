using System.Data.Common;
using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GoodHotel.Services;

public sealed record RoomBlockedPeriod(int RoomId, DateOnly? Start, DateOnly? End)
{
    public bool Overlaps(DateOnly start, DateOnly end) =>
        (!Start.HasValue || Start < end) && (!End.HasValue || End > start);
}

public sealed class RoomAvailabilityService(GoodHotelDbContext db, TimeProvider timeProvider)
{
    private static readonly TimeZoneInfo HotelTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok");

    public DateOnly HotelToday() => HotelDate(timeProvider.GetUtcNow().UtcDateTime);

    public static DateOnly HotelDate(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), HotelTimeZone));

    public static RoomBlockedPeriod ActiveStayPeriod(int roomId, DateTime checkedInAtUtc,
        DateOnly? scheduledCheckout, DateOnly today) =>
        new(roomId, HotelDate(checkedInAtUtc),
            scheduledCheckout.HasValue && scheduledCheckout.Value > today ? scheduledCheckout : null);

    /// <summary>
    /// วันที่การจองนี้ปล่อยห้องจริง: ถ้าแขกเช็คเอาท์ก่อนกำหนด ใช้วันที่เช็คเอาท์จริง ไม่ใช่วันที่จองไว้
    /// (ห้องยังติดงานทำความสะอาดจนกว่า Staff จะตรวจผ่าน ซึ่งถูกกันไว้แยกใน CleaningPeriod)
    /// </summary>
    public static DateOnly? EffectiveCheckOut(DateOnly? scheduledCheckOut, DateTime? checkedOutAtUtc)
    {
        if (checkedOutAtUtc == null) return scheduledCheckOut;
        var actual = HotelDate(checkedOutAtUtc.Value);
        return scheduledCheckOut.HasValue && scheduledCheckOut.Value < actual ? scheduledCheckOut : actual;
    }

    public static RoomBlockedPeriod CleaningPeriod(int roomId, DateTime? checkedOutAtUtc) =>
        new(roomId, checkedOutAtUtc.HasValue ? HotelDate(checkedOutAtUtc.Value) : null, null);

    public async Task<IReadOnlyList<RoomBlockedPeriod>> GetBlockedPeriodsAsync(
        IReadOnlyCollection<int> roomIds, DateOnly start, DateOnly end, CancellationToken ct)
    {
        if (start >= end) throw new ArgumentException("The end date must be after the start date.");
        if (roomIds.Count == 0) return Array.Empty<RoomBlockedPeriod>();
        var ids = roomIds.Distinct().ToList();
        var rooms = await db.Rooms.AsNoTracking().Where(r => ids.Contains(r.RoomId))
            .Select(r => new { r.RoomId, r.Status }).ToListAsync(ct);
        var periods = new List<RoomBlockedPeriod>();
        var found = rooms.Select(r => r.RoomId).ToHashSet();
        periods.AddRange(ids.Where(id => !found.Contains(id)).Select(id => new RoomBlockedPeriod(id, null, null)));
        periods.AddRange(rooms.Where(r => !string.Equals(r.Status, "Available", StringComparison.Ordinal))
            .Select(r => new RoomBlockedPeriod(r.RoomId, null, null)));

        var bookings = await db.Bookings.AsNoTracking()
            .Where(b => b.Status != BookingStatuses.Cancelled &&
                b.RoomId.HasValue && ids.Contains(b.RoomId.Value) &&
                b.CheckIn.HasValue && b.CheckOut.HasValue && b.CheckIn < end && b.CheckOut > start)
            .Select(b => new
            {
                RoomId = b.RoomId!.Value,
                b.CheckIn,
                b.CheckOut,
                CheckedOutAtUtc = db.BookingStays
                    .Where(s => s.BookingId == b.BookingId && s.Status == "CheckedOut")
                    .Select(s => s.CheckedOutAtUtc)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);
        periods.AddRange(bookings.Select(b =>
            new RoomBlockedPeriod(b.RoomId, b.CheckIn, EffectiveCheckOut(b.CheckOut, b.CheckedOutAtUtc))));

        var activeStays = await db.BookingStays.AsNoTracking()
            .Where(s => s.Status == "CheckedIn" && s.Booking.RoomId.HasValue && ids.Contains(s.Booking.RoomId.Value))
            .Select(s => new { RoomId = s.Booking.RoomId!.Value, s.CheckedInAtUtc, s.Booking.CheckOut })
            .ToListAsync(ct);
        var today = HotelToday();
        periods.AddRange(activeStays.Select(s => ActiveStayPeriod(s.RoomId, s.CheckedInAtUtc, s.CheckOut, today)));

        var reports = await db.MaintenanceReports.AsNoTracking()
            .Where(r => ids.Contains(r.RoomId) && r.Status != MaintenanceReport.Resolved)
            .Select(r => r.RoomId).Distinct().ToListAsync(ct);
        periods.AddRange(reports.Select(roomId => new RoomBlockedPeriod(roomId, null, null)));

        var cleaning = await (from task in db.HousekeepingTasks.AsNoTracking()
            join stay in db.BookingStays.AsNoTracking() on task.StayBookingId equals (int?)stay.BookingId into stays
            from stay in stays.DefaultIfEmpty()
            where ids.Contains(task.RoomId) && task.TaskType == "CheckoutCleaning" && task.Status != "Verified"
            select new
            {
                task.RoomId,
                CheckedOutAtUtc = stay == null || stay.Status != "CheckedOut" || stay.Booking.RoomId != task.RoomId
                    ? null : stay.CheckedOutAtUtc
            }).ToListAsync(ct);
        periods.AddRange(cleaning.Select(t => CleaningPeriod(t.RoomId, t.CheckedOutAtUtc)));
        return periods.Where(p => p.Overlaps(start, end)).ToList();
    }

    public async Task<bool> IsAvailableAsync(int roomId, DateOnly start, DateOnly end, CancellationToken ct) =>
        (await GetBlockedPeriodsAsync(new[] { roomId }, start, end, ct)).Count == 0;

    public static IQueryable<BookingStay> CurrentStays(IQueryable<BookingStay> stays, int roomId) =>
        stays.Where(stay => stay.Status == "CheckedIn" && stay.Booking.RoomId == roomId);

    public static IQueryable<Booking> CurrentBookings(IQueryable<Booking> bookings, int roomId, DateOnly today) =>
        bookings.Where(booking => booking.Status != BookingStatuses.Cancelled &&
            booking.RoomId == roomId && booking.CheckIn.HasValue &&
            booking.CheckOut.HasValue && booking.CheckIn <= today && booking.CheckOut > today);

    public static IQueryable<HousekeepingTask> PendingCheckoutCleaning(
        IQueryable<HousekeepingTask> tasks, int roomId) =>
        tasks.Where(task => task.RoomId == roomId && task.TaskType == "CheckoutCleaning" &&
            task.Status != "Verified");

    public async Task<bool> HasCurrentRestrictionsAsync(int roomId, CancellationToken ct)
    {
        var today = HotelToday();
        return await CurrentStays(db.BookingStays.AsNoTracking(), roomId).AnyAsync(ct) ||
               await CurrentBookings(db.Bookings.AsNoTracking(), roomId, today)
                   // แขกที่เช็คเอาท์ไปแล้ว (แม้ก่อนกำหนด) ไม่ถือว่ายังใช้ห้องอยู่
                   .Where(b => !db.BookingStays.Any(s => s.BookingId == b.BookingId && s.Status == "CheckedOut"))
                   .AnyAsync(ct) ||
               await PendingCheckoutCleaning(db.HousekeepingTasks.AsNoTracking(), roomId).AnyAsync(ct);
    }

    public static async Task<bool> LockRoomAsync(GoodHotelDbContext db, IDbContextTransaction transaction,
        int roomId, CancellationToken ct)
    {
        await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "SELECT `RoomId` FROM `rooms` WHERE `RoomId` = @roomId FOR UPDATE";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@roomId";
        parameter.Value = roomId;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync(ct);
        return result != null && result != DBNull.Value;
    }
}
