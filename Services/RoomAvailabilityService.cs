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

public sealed class RoomAvailabilityService(GoodHotelDbContext db, TimeProvider timeProvider, ParallelReader reader)
{
    private static readonly TimeZoneInfo HotelTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok");

    public DateOnly HotelToday() => HotelDate(timeProvider.GetUtcNow().UtcDateTime);

    public static DateOnly HotelDate(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), HotelTimeZone));

    /// <summary>
    /// แขกที่พักอยู่: กันถึงวันออก ถ้าถึงกำหนดหรือเลยกำหนดแล้วยังไม่เช็คเอาท์ กันถึงวันนี้ (เลื่อนไปทีละวันจนกว่าจะเช็คเอาท์)
    /// ห้องจึงยังจองล่วงหน้าได้ และเช็คอินซ้อนไม่ได้เพราะตรวจแขกที่พักอยู่เสมอ
    /// </summary>
    public static RoomBlockedPeriod ActiveStayPeriod(int roomId, DateTime checkedInAtUtc,
        DateOnly? scheduledCheckout, DateOnly today) =>
        new(roomId, HotelDate(checkedInAtUtc),
            !scheduledCheckout.HasValue ? null
                : scheduledCheckout.Value > today ? scheduledCheckout : today.AddDays(1));

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

    /// <summary>
    /// รอทำความสะอาด/รอตรวจ: กันถึงวันนี้เท่านั้น จองล่วงหน้าตั้งแต่พรุ่งนี้ได้
    /// (เช็คอินยังต้องตรวจห้องผ่านก่อนเสมอ ตรวจใน StayService)
    /// </summary>
    public static RoomBlockedPeriod CleaningPeriod(int roomId, DateTime? checkedOutAtUtc, DateOnly today) =>
        new(roomId, checkedOutAtUtc.HasValue ? HotelDate(checkedOutAtUtc.Value) : null, today.AddDays(1));

    /// <param name="roomIds">null = ทุกห้อง</param>
    /// <remarks>เรียกใน transaction ได้เมื่อล็อกห้องแล้ว: ทุกคนที่ทำให้ห้องไม่ว่างต้องล็อกห้องก่อน ผลจึงไม่เปลี่ยนระหว่างนี้</remarks>
    public async Task<IReadOnlyList<RoomBlockedPeriod>> GetBlockedPeriodsAsync(
        IReadOnlyCollection<int>? roomIds, DateOnly start, DateOnly end, CancellationToken ct)
    {
        if (start >= end) throw new ArgumentException("The end date must be after the start date.");
        if (roomIds is { Count: 0 }) return Array.Empty<RoomBlockedPeriod>();
        var all = roomIds == null;
        var ids = roomIds?.Distinct().ToList() ?? [];
        var today = HotelToday();

        // แต่ละ query คืนช่วงเวลาที่ห้องไม่ว่าง (query ไม่ขึ้นต่อกัน)
        var queries = new Func<GoodHotelDbContext, Task<IEnumerable<RoomBlockedPeriod>>>[]
        {
            async c =>
            {
                var rooms = await c.Rooms.AsNoTracking().Where(r => all || ids.Contains(r.RoomId))
                    .Select(r => new { r.RoomId, r.Status }).ToListAsync(ct);
                var found = rooms.Select(r => r.RoomId).ToHashSet();
                return ids.Where(id => !found.Contains(id)).Select(id => new RoomBlockedPeriod(id, null, null))
                    .Concat(rooms.Where(r => !string.Equals(r.Status, "Available", StringComparison.Ordinal))
                        .Select(r => new RoomBlockedPeriod(r.RoomId, null, null)))
                    .ToList();
            },
            async c => (await c.Bookings.AsNoTracking()
                .Where(b => b.Status != BookingStatuses.Cancelled &&
                    b.RoomId.HasValue && (all || ids.Contains(b.RoomId.Value)) &&
                    b.CheckIn.HasValue && b.CheckOut.HasValue && b.CheckIn < end && b.CheckOut > start)
                .Select(b => new
                {
                    RoomId = b.RoomId!.Value,
                    b.CheckIn,
                    b.CheckOut,
                    CheckedOutAtUtc = c.BookingStays
                        .Where(s => s.BookingId == b.BookingId && s.Status == "CheckedOut")
                        .Select(s => s.CheckedOutAtUtc)
                        .FirstOrDefault()
                })
                .ToListAsync(ct))
                .Select(b => new RoomBlockedPeriod(b.RoomId, b.CheckIn, EffectiveCheckOut(b.CheckOut, b.CheckedOutAtUtc))),
            async c => (await c.BookingStays.AsNoTracking()
                .Where(s => s.Status == "CheckedIn" && s.Booking.RoomId.HasValue && (all || ids.Contains(s.Booking.RoomId.Value)))
                .Select(s => new { RoomId = s.Booking.RoomId!.Value, s.CheckedInAtUtc, s.Booking.CheckOut })
                .ToListAsync(ct))
                .Select(s => ActiveStayPeriod(s.RoomId, s.CheckedInAtUtc, s.CheckOut, today)),
            async c => (await c.MaintenanceReports.AsNoTracking()
                .Where(r => (all || ids.Contains(r.RoomId)) && r.Status != MaintenanceReport.Resolved)
                .Select(r => r.RoomId).Distinct().ToListAsync(ct))
                .Select(roomId => new RoomBlockedPeriod(roomId, null, null)),
            async c => (await (from task in c.HousekeepingTasks.AsNoTracking()
                join stay in c.BookingStays.AsNoTracking() on task.StayBookingId equals (int?)stay.BookingId into stays
                from stay in stays.DefaultIfEmpty()
                where (all || ids.Contains(task.RoomId)) && task.TaskType == "CheckoutCleaning" && task.Status != "Verified"
                select new
                {
                    task.RoomId,
                    CheckedOutAtUtc = stay == null || stay.Status != "CheckedOut" || stay.Booking.RoomId != task.RoomId
                        ? null : stay.CheckedOutAtUtc
                }).ToListAsync(ct))
                .Select(t => CleaningPeriod(t.RoomId, t.CheckedOutAtUtc, today))
        };

        var periods = new List<RoomBlockedPeriod>();
        foreach (var result in await Task.WhenAll(queries.Select(reader.Run))) periods.AddRange(result);
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
        // แค่มีคนจองวันนี้ (ยังไม่เช็คอิน) ไม่กันการเปิดห้อง ไม่งั้นซ่อมเสร็จแล้วแขกเช็คอินไม่ได้ และ Admin ก็เปิดห้องไม่ได้
        return await CurrentStays(db.BookingStays.AsNoTracking(), roomId).AnyAsync(ct) ||
               await PendingCheckoutCleaning(db.HousekeepingTasks.AsNoTracking(), roomId).AnyAsync(ct);
    }

    /// <summary>ล็อกห้องและอ่านข้อมูลห้องในรอบเดียว (null = ไม่มีห้องนี้)</summary>
    public static async Task<Room?> LockAndLoadRoomAsync(GoodHotelDbContext db, int roomId, CancellationToken ct, bool track = false)
    {
        if (db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Room locking requires a transaction.");
        // ห้ามต่อ LINQ หลัง FromSql ไม่งั้น FOR UPDATE ไปอยู่ใน subquery
        var query = db.Rooms.FromSqlInterpolated($"SELECT * FROM `rooms` WHERE `RoomId` = {roomId} FOR UPDATE");
        return (await (track ? query : query.AsNoTracking()).ToListAsync(ct)).SingleOrDefault();
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
