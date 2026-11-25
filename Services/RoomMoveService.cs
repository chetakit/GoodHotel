using System.Data;
using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Services;

public sealed record RoomMoveOption(int RoomId, string? RoomNumber, string? Type, int? Capacity, decimal? Rate);

public sealed record RoomMoveInfo(int BookingId, string? GuestName, int RoomId, string? RoomNumber, string? RoomType,
    decimal? Rate, DateOnly CheckIn, DateOnly CheckOut, int Guests, int ExtraBed);

/// <summary>
/// ย้ายห้องให้การจองที่จ่ายแล้วแต่ยังไม่เช็คอิน (เช่น ห้องเดิมเสีย แขกเดิมยังไม่ออก)
/// - ย้ายได้เฉพาะห้องที่ราคาไม่ต่ำกว่าห้องเดิม และไม่เก็บเงินเพิ่ม เพราะเป็นเหตุจากโรงแรม
/// - มีคำขอคืนเงินค้างอยู่ย้ายไม่ได้ ต้องรอฝ่ายบัญชีก่อน
/// - บันทึกใน payment_events (RoomMoved) เห็นในประวัติการชำระเงินและหน้า Audit พร้อมชื่อพนักงาน
/// </summary>
public sealed class RoomMoveService(GoodHotelDbContext db, RoomAvailabilityService availability, TimeProvider time,
    ParallelReader reader)
{
    public static bool CanMove(string? status, DateOnly? checkOut, bool hasStay, bool isPaid, bool refundPending,
        DateOnly today) =>
        BlockReason(status, checkOut, hasStay, isPaid, refundPending, today) == null;

    /// <summary>ย้ายห้องไม่ได้เพราะอะไร (null = ย้ายได้)</summary>
    public static string? BlockReason(string? status, DateOnly? checkOut, bool hasStay, bool isPaid, bool refundPending,
        DateOnly today) =>
        status != BookingStatuses.Booked ? "การจองนี้ถูกยกเลิกแล้ว"
        : hasStay ? "เช็คอินแล้ว ย้ายห้องได้เฉพาะก่อนเช็คอิน"
        : !checkOut.HasValue || checkOut.Value <= today ? "เลยวันออกแล้ว"
        : refundPending ? "มีคำขอคืนเงินรอฝ่ายบัญชีพิจารณา ต้องรอผลก่อน"
        : !isPaid ? "ลูกค้ายังไม่ได้ชำระเงิน"
        : null;

    public static bool IsSuitable(decimal? oldRate, decimal? newRate, int? capacity, int guests, int extraBed) =>
        oldRate.HasValue && newRate >= oldRate && capacity.HasValue && guests <= capacity + extraBed;

    /// <summary>คืน null เมื่อการจองนี้ย้ายห้องไม่ได้</summary>
    public async Task<RoomMoveInfo?> InfoAsync(int bookingId, CancellationToken ct)
    {
        // อ่านพร้อมกันได้: ตอนย้ายจริงห้องและการจองถูกล็อกไว้แล้ว ผลจึงไม่เปลี่ยนระหว่างนี้
        var paidTask = reader.Run(c => PaymentService.IsPaidAsync(c, bookingId, ct));
        var bookingTask = reader.Run(c => c.Bookings.AsNoTracking().Where(x => x.BookingId == bookingId && x.RoomId != null)
            .Select(x => new
            {
                x.Status, x.CheckIn, x.CheckOut, x.Guests, x.ExtraBed, RoomId = x.RoomId!.Value,
                RoomNumber = x.Room!.Room1, RoomType = x.Room.Type, x.Room.Rate,
                GuestName = x.User == null ? null : x.User.FirstName + " " + x.User.LastName,
                HasStay = c.BookingStays.Any(s => s.BookingId == x.BookingId),
                RefundPending = x.Payments.Any(p => p.Refunds.Any(r => r.Status == PaymentService.RefundRequested))
            }).SingleOrDefaultAsync(ct));
        await Task.WhenAll(paidTask, bookingTask);
        var b = bookingTask.Result;
        if (b?.CheckIn == null || b.CheckOut == null || b.Guests == null ||
            !CanMove(b.Status, b.CheckOut, b.HasStay, paidTask.Result,
                b.RefundPending, availability.HotelToday()))
            return null;
        return new RoomMoveInfo(bookingId, b.GuestName, b.RoomId, b.RoomNumber, b.RoomType, b.Rate,
            b.CheckIn.Value, b.CheckOut.Value, b.Guests.Value, b.ExtraBed ?? 0);
    }

    // แขกที่มาช้า: คืนที่ผ่านไปแล้วไม่ต้องเช็ค
    private DateOnly From(RoomMoveInfo info)
    {
        var today = availability.HotelToday();
        return info.CheckIn > today ? info.CheckIn : today;
    }

    public async Task<IReadOnlyList<RoomMoveOption>> OptionsAsync(RoomMoveInfo info, CancellationToken ct)
    {
        var roomsTask = reader.Run(c => c.Rooms.AsNoTracking()
            .Where(r => r.RoomId != info.RoomId && r.Status == "Available")
            .OrderBy(r => r.Rate).ThenBy(r => r.Room1)
            .Select(r => new RoomMoveOption(r.RoomId, r.Room1, r.Type, r.Capacity, r.Rate)).ToListAsync(ct));
        var blockedTask = availability.GetBlockedPeriodsAsync(null, From(info), info.CheckOut, ct);
        await Task.WhenAll(roomsTask, blockedTask);
        var rooms = roomsTask.Result.Where(r => IsSuitable(info.Rate, r.Rate, r.Capacity, info.Guests, info.ExtraBed)).ToList();
        var blocked = blockedTask.Result.Select(p => p.RoomId).ToHashSet();
        return rooms.Where(r => !blocked.Contains(r.RoomId)).ToList();
    }

    /// <summary>คืน null เมื่อสำเร็จ ไม่งั้นคืนข้อความบอกเหตุผล</summary>
    public async Task<string?> MoveAsync(int bookingId, int newRoomId, int staffId, string? reason, CancellationToken ct)
    {
        reason = reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 500) return "กรุณาระบุเหตุผลที่ย้ายห้อง (ไม่เกิน 500 ตัวอักษร)";
        var oldRoomId = await db.Bookings.AsNoTracking().Where(b => b.BookingId == bookingId)
            .Select(b => b.RoomId).SingleOrDefaultAsync(ct);
        if (!oldRoomId.HasValue) return "ไม่พบการจองหรือห้อง";
        if (oldRoomId == newRoomId) return "กรุณาเลือกห้องอื่น";

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        // ล็อกทั้งสองห้องตามลำดับเลข กันจองหรือเช็คอินชนกันระหว่างย้าย
        foreach (var id in new[] { oldRoomId.Value, newRoomId }.Order())
            if (!await RoomAvailabilityService.LockRoomAsync(db, tx, id, ct)) return "ไม่พบห้อง";

        if (!await PaymentService.LockBookingAsync(db, bookingId, ct)) return "ไม่พบการจองหรือห้อง";

        var info = await InfoAsync(bookingId, ct);
        if (info == null || info.RoomId != oldRoomId) return "การจองนี้ย้ายห้องไม่ได้แล้ว (เช็คอิน ยกเลิก หรือมีคำขอคืนเงินอยู่)";
        var target = await db.Rooms.AsNoTracking().SingleAsync(r => r.RoomId == newRoomId, ct);
        if (target.Status != "Available" ||
            !IsSuitable(info.Rate, target.Rate, target.Capacity, info.Guests, info.ExtraBed) ||
            !await availability.IsAvailableAsync(newRoomId, From(info), info.CheckOut, ct))
            return "ห้องที่เลือกไม่ว่างหรือรับการจองนี้ไม่ได้ กรุณาเลือกใหม่";

        // เงื่อนไขอยู่ใน WHERE อีกชั้น: ลูกค้ายกเลิก/ขอคืนเงิน หรือบัญชีอนุมัติคืนเงินพร้อมกันพอดี
        var moved = await db.Bookings.Where(b => b.BookingId == bookingId && b.RoomId == oldRoomId &&
                b.Status == BookingStatuses.Booked &&
                !db.PaymentRefunds.Any(r => r.Payment.BookingId == b.BookingId && r.Status == PaymentService.RefundRequested))
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.RoomId, newRoomId), ct);
        if (moved != 1) return "การจองนี้ถูกเปลี่ยนไปแล้ว กรุณาลองใหม่";

        var payment = await db.Payments.Where(p => p.BookingId == bookingId && p.VerifiedAtUtc != null)
            .OrderBy(p => p.PaymentId).FirstAsync(ct);
        db.PaymentEvents.Add(new PaymentEvent
        {
            PaymentId = payment.PaymentId, ActorUserId = staffId, EventType = "RoomMoved",
            OccurredAtUtc = time.GetUtcNow().UtcDateTime, PaymentMethod = payment.PaymentMethod,
            Reason = $"ห้อง {info.RoomNumber} → ห้อง {target.Room1}: {reason}", IdempotencyKey = Guid.NewGuid().ToString("N")
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return null;
    }
}
