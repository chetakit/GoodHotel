using System.Globalization;
using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Services;

/// <summary>สถานะจริงของห้อง ณ ตอนนี้ (ใช้ในหน้า Room Status ของพนักงานต้อนรับ)</summary>
public enum RoomLiveStatus
{
    Available,      // ว่าง พร้อมขาย
    ArrivingToday,  // ว่าง แต่มีลูกค้าจองเข้าพักวันนี้
    Occupied,       // มีผู้เข้าพัก
    Cleaning,       // เช็คเอาท์แล้ว รอแม่บ้านทำความสะอาด / รอตรวจ
    Maintenance,    // ซ่อมบำรุง
    Closed          // Admin ตั้งไว้ว่าไม่พร้อมขาย (เช่น Reserved)
}

public sealed record RoomStatusItem(
    int RoomId,
    string? RoomNumber,
    string? RoomType,
    int? Capacity,
    RoomLiveStatus Status,
    string? Detail,
    int? BookingId);

/// <summary>
/// ช่อง Room.Status ในฐานข้อมูลถูกแก้โดย Admin/งานซ่อมเท่านั้น
/// สถานะจริงจึงต้องรวมจาก การเข้าพัก + งานทำความสะอาด + งานซ่อม + การจองของวันนี้
/// </summary>
public sealed class RoomStatusService(GoodHotelDbContext db, RoomAvailabilityService availability)
{
    /// <summary>ชื่อสถานะภาษาไทย (ใช้ทั้งหน้า Staff Room Status และ Admin Dashboard)</summary>
    public static string Label(RoomLiveStatus status) => status switch
    {
        RoomLiveStatus.Available => "ว่าง",
        RoomLiveStatus.ArrivingToday => "รอเช็คอินวันนี้",
        RoomLiveStatus.Occupied => "มีผู้เข้าพัก",
        RoomLiveStatus.Cleaning => "รอทำความสะอาด",
        RoomLiveStatus.Maintenance => "ซ่อมบำรุง",
        _ => "ไม่พร้อมขาย"
    };

    public async Task<IReadOnlyList<RoomStatusItem>> GetRoomBoardAsync(CancellationToken ct)
    {
        var today = availability.HotelToday();

        var rooms = await db.Rooms.AsNoTracking()
            .OrderBy(r => r.Room1)
            .ToListAsync(ct);

        var activeStays = await db.BookingStays.AsNoTracking()
            .Where(s => s.Status == "CheckedIn" && s.Booking.RoomId != null)
            .Select(s => new
            {
                RoomId = s.Booking.RoomId!.Value,
                s.BookingId,
                s.Booking.CheckOut,
                GuestName = s.Booking.User == null ? null : s.Booking.User.FirstName + " " + s.Booking.User.LastName
            })
            .ToListAsync(ct);

        var cleaningTasks = await db.HousekeepingTasks.AsNoTracking()
            .Where(t => t.TaskType == "CheckoutCleaning" && t.Status != "Verified")
            .Select(t => new { t.RoomId, t.Status })
            .ToListAsync(ct);

        var maintenanceRoomIds = await db.MaintenanceReports.AsNoTracking()
            .Where(r => r.Status != MaintenanceReport.Resolved)
            .Select(r => r.RoomId)
            .Distinct()
            .ToListAsync(ct);

        var arrivalsToday = await db.Bookings.AsNoTracking()
            .Where(b => b.Status == BookingStatuses.Booked &&
                        b.RoomId != null &&
                        b.CheckIn == today &&
                        !db.BookingStays.Any(s => s.BookingId == b.BookingId))
            .Select(b => new
            {
                RoomId = b.RoomId!.Value,
                b.BookingId,
                GuestName = b.User == null ? null : b.User.FirstName + " " + b.User.LastName
            })
            .ToListAsync(ct);

        return rooms.Select(room =>
        {
            var stay = activeStays.FirstOrDefault(s => s.RoomId == room.RoomId);
            var cleaning = cleaningTasks.FirstOrDefault(t => t.RoomId == room.RoomId);
            var arrival = arrivalsToday.FirstOrDefault(a => a.RoomId == room.RoomId);

            // ลำดับความสำคัญ: มีคนพัก > ซ่อม > รอทำความสะอาด > ปิดห้อง > มีคนจะเข้าวันนี้ > ว่าง
            if (stay != null)
                return Item(room, RoomLiveStatus.Occupied,
                    $"{stay.GuestName} · ออก {stay.CheckOut?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}",
                    stay.BookingId);

            if (room.Status == "Maintenance" || maintenanceRoomIds.Contains(room.RoomId))
                return Item(room, RoomLiveStatus.Maintenance, "มีรายการแจ้งซ่อมที่ยังไม่เสร็จ");

            if (cleaning != null)
                return Item(room, RoomLiveStatus.Cleaning, $"งานทำความสะอาด: {cleaning.Status}");

            // Admin ตั้งสถานะห้องเองในหน้าจัดการห้อง
            if (room.Status == "Occupied")
                return Item(room, RoomLiveStatus.Occupied, "Admin ตั้งสถานะว่ามีผู้เข้าพัก");

            if (room.Status != "Available")
                return Item(room, RoomLiveStatus.Closed, $"Admin ตั้งสถานะเป็น {room.Status}");

            if (arrival != null)
                return Item(room, RoomLiveStatus.ArrivingToday, $"{arrival.GuestName} · รอเช็คอิน", arrival.BookingId);

            return Item(room, RoomLiveStatus.Available, null);
        }).ToList();
    }

    private static RoomStatusItem Item(Room room, RoomLiveStatus status, string? detail, int? bookingId = null) =>
        new(room.RoomId, room.Room1, room.Type, room.Capacity, status, detail, bookingId);
}
