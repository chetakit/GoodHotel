using System;
using System.Collections.Generic;

namespace GoodHotel.Models;

public partial class Booking
{
    public int BookingId { get; set; }

    public int? UserId { get; set; }

    public int? RoomId { get; set; }

    public DateOnly? CheckIn { get; set; }

    public DateOnly? CheckOut { get; set; }

    public int? Guests { get; set; }

    public int? ExtraBed { get; set; }

    public int? PromotionId { get; set; }

    public decimal? Amount { get; set; }

    // "Booked" หรือ "Cancelled" (ดูค่าคงที่ใน BookingStatuses)
    public string Status { get; set; } = BookingStatuses.Booked;

    public DateTime? CancelledAtUtc { get; set; }

    // เวลาที่กดจอง ใช้นับเวลาชำระเงิน (การจองก่อนมีระบบนี้เป็น null และไม่ถูกยกเลิกอัตโนมัติ)
    public DateTime? CreatedAtUtc { get; set; }

    // เหตุผลที่ยกเลิก (ดู CancelReasons) การยกเลิกก่อนมีระบบนี้เป็น null
    public string? CancelReason { get; set; }

    public virtual ICollection<Payment> Payments { get; set; } = new List<Payment>();

    public virtual Promotion? Promotion { get; set; }

    public virtual Room? Room { get; set; }

    public virtual User? User { get; set; }
}

public static class BookingStatuses
{
    public const string Booked = "Booked";
    public const string Cancelled = "Cancelled";
}

public static class CancelReasons
{
    public const string Customer = "Customer";
    public const string Staff = "Staff";
    public const string PaymentTimeout = "PaymentTimeout";

    /// <summary>ข้อความสั้นสำหรับป้ายสถานะ</summary>
    public static string Label(string? reason) => reason switch
    {
        Customer => "ลูกค้ายกเลิก",
        Staff => "พนักงานยกเลิก",
        PaymentTimeout => "หมดเวลาชำระเงิน",
        _ => "ยกเลิกแล้ว"
    };
}
