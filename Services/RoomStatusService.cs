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
    Cleaning,       // เช็คเอาท์แล้ว รอแม่บ้านทำความสะอาด
    Inspection,     // แม่บ้านทำเสร็จแล้ว รอพนักงานต้อนรับตรวจ
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
public sealed class RoomStatusService(RoomAvailabilityService availability, ParallelReader reader)
{
    /// <summary>ชื่อสถานะภาษาไทย (ใช้ทั้งหน้า Staff Room Status และ Admin Dashboard)</summary>
    public static string Label(RoomLiveStatus status) => status switch
    {
        RoomLiveStatus.Available => "ว่าง",
        RoomLiveStatus.ArrivingToday => "รอเช็คอิน",
        RoomLiveStatus.Occupied => "มีผู้เข้าพัก",
        RoomLiveStatus.Cleaning => "รอทำความสะอาด",
        RoomLiveStatus.Inspection => "รอตรวจห้อง",
        RoomLiveStatus.Maintenance => "ซ่อมบำรุง",
        _ => "ไม่พร้อมขาย"
    };

    public async Task<IReadOnlyList<RoomStatusItem>> GetRoomBoardAsync(CancellationToken ct)
    {
        var today = availability.HotelToday();

        // 5 query นี้ไม่ขึ้นต่อกัน ยิงพร้อมกันได้
        var roomsTask = reader.Run(db => db.Rooms.AsNoTracking()
            .OrderBy(r => r.Room1)
            .ToListAsync(ct));

        var staysTask = reader.Run(db => db.BookingStays.AsNoTracking()
            .Where(s => s.Status == "CheckedIn" && s.Booking.RoomId != null)
            .Select(s => new
            {
                RoomId = s.Booking.RoomId!.Value,
                s.BookingId,
                s.Booking.CheckOut,
                GuestName = s.Booking.User == null ? null : s.Booking.User.FirstName + " " + s.Booking.User.LastName
            })
            .ToListAsync(ct));

        var cleaningTask = reader.Run(db => db.HousekeepingTasks.AsNoTracking()
            .Where(t => t.TaskType == "CheckoutCleaning" && t.Status != "Verified")
            .Select(t => new { t.RoomId, t.Status })
            .ToListAsync(ct));

        var maintenanceTask = reader.Run(db => db.MaintenanceReports.AsNoTracking()
            .Where(r => r.Status != MaintenanceReport.Resolved)
            .Select(r => r.RoomId)
            .Distinct()
            .ToListAsync(ct));

        var arrivalsTask = reader.Run(db => db.Bookings.AsNoTracking()
            .Where(b => b.Status == BookingStatuses.Booked &&
                        b.RoomId != null &&
                        // รวมแขกที่มาช้ากว่าวันเข้าพัก: ห้องยังถูกจองอยู่ ห้ามขึ้นว่าว่าง
                        b.CheckIn <= today && b.CheckOut > today &&
                        !db.BookingStays.Any(s => s.BookingId == b.BookingId))
            .Select(b => new
            {
                RoomId = b.RoomId!.Value,
                b.BookingId,
                IsLate = b.CheckIn < today,
                GuestName = b.User == null ? null : b.User.FirstName + " " + b.User.LastName
            })
            .ToListAsync(ct));

        await Task.WhenAll(roomsTask, staysTask, cleaningTask, maintenanceTask, arrivalsTask);
        var rooms = roomsTask.Result;
        var activeStays = staysTask.Result;
        var cleaningTasks = cleaningTask.Result;
        var maintenanceRoomIds = maintenanceTask.Result;
        var arrivalsToday = arrivalsTask.Result;

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

            if (maintenanceRoomIds.Contains(room.RoomId))
                return Item(room, RoomLiveStatus.Maintenance, "มีรายการแจ้งซ่อมที่ยังไม่เสร็จ");

            // ไม่มีงานซ่อมค้าง แต่ Admin ตั้งห้องเป็นซ่อมบำรุงไว้ (หรือปิดงานซ่อมแล้วยังไม่ได้เปิดห้อง)
            if (room.Status == "Maintenance")
                return Item(room, RoomLiveStatus.Maintenance, "Admin ตั้งเป็นซ่อมบำรุงไว้ (ไม่มีงานซ่อมค้าง) เปิดห้องได้ที่หน้าจัดการห้อง");

            if (cleaning != null)
            {
                // มีแขกจองเข้าวันนี้ด้วย: บอกพนักงานให้รีบ เพราะเช็คอินไม่ได้จนกว่าจะตรวจผ่าน
                var waiting = arrival == null ? "" : $" · {arrival.GuestName} รอเช็คอินวันนี้";
                return cleaning.Status == "Completed"
                    ? Item(room, RoomLiveStatus.Inspection, "แม่บ้านทำเสร็จแล้ว รอพนักงานต้อนรับตรวจ" + waiting, arrival?.BookingId)
                    : Item(room, RoomLiveStatus.Cleaning, $"งานทำความสะอาด: {ThaiText.Status(cleaning.Status)}{waiting}", arrival?.BookingId);
            }

            // Admin ปิดขายห้องไว้ (รวมค่า Occupied เก่าที่เคยให้ Admin ตั้งเอง: ไม่มีแขกจริง จึงถือเป็นปิดขาย)
            if (room.Status != "Available")
                return Item(room, RoomLiveStatus.Closed, "Admin ปิดขายไว้");

            if (arrival != null)
                return Item(room, RoomLiveStatus.ArrivingToday,
                    $"{arrival.GuestName} · {(arrival.IsLate ? "มาช้ากว่าวันเข้าพัก ยังรอเช็คอิน" : "รอเช็คอิน")}", arrival.BookingId);

            return Item(room, RoomLiveStatus.Available, null);
        }).ToList();
    }

    private static RoomStatusItem Item(Room room, RoomLiveStatus status, string? detail, int? bookingId = null) =>
        new(room.RoomId, room.Room1, room.Type, room.Capacity, status, detail, bookingId);
}
