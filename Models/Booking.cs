namespace GoodHotel.Models;

// ตาราง bookings: การจองห้อง
public partial class Booking
{
    public int BookingId { get; set; } // เลขที่การจอง (primary key)

    public int? UserId { get; set; } // ลูกค้าที่จอง (foreign key ไป users)

    public int? RoomId { get; set; } // ห้องที่จอง (foreign key ไป rooms)

    public DateOnly? CheckIn { get; set; } // วันเข้าพัก

    public DateOnly? CheckOut { get; set; } // วันออก

    public int? Guests { get; set; } // จำนวนผู้เข้าพัก

    public int? ExtraBed { get; set; } // จำนวนเตียงเสริม

    public int? PromotionId { get; set; } // โปรที่ใช้ (ไม่ใช้ = null)

    public decimal? Amount { get; set; } // ยอดเงินรวมหลังหักส่วนลด

    // "Booked" หรือ "Cancelled" (ดูค่าคงที่ใน BookingStatuses)
    public string Status { get; set; } = BookingStatuses.Booked;

    public DateTime? CancelledAtUtc { get; set; } // เวลาที่ยกเลิก

    // เวลาที่กดจอง ใช้นับเวลาชำระเงิน (การจองก่อนมีระบบนี้เป็น null และไม่ถูกยกเลิกอัตโนมัติ)
    public DateTime? CreatedAtUtc { get; set; }

    // เหตุผลที่ยกเลิก (ดู CancelReasons) การยกเลิกก่อนมีระบบนี้เป็น null
    public string? CancelReason { get; set; }

    // ข้อมูลที่เชื่อมกัน (navigation property) ใช้เดินไปตารางอื่น เช่น booking.Room.Type
    public virtual ICollection<Payment> Payments { get; set; } = new List<Payment>();

    public virtual Promotion? Promotion { get; set; }

    public virtual Room? Room { get; set; }

    public virtual User? User { get; set; }
}

// ค่าสถานะการจอง
public static class BookingStatuses
{
    public const string Booked = "Booked";
    public const string Cancelled = "Cancelled";
}

// เหตุผลที่ยกเลิก
public static class CancelReasons
{
    public const string Customer = "Customer";
    public const string Staff = "Staff";
    public const string PaymentTimeout = "PaymentTimeout";
    public const string Refund = "RefundApproved";

    /// <summary>ข้อความสั้นสำหรับป้ายสถานะ</summary>
    public static string Label(string? reason) => reason switch
    {
        Customer => "ลูกค้ายกเลิก",
        Staff => "พนักงานยกเลิก",
        PaymentTimeout => "หมดเวลาชำระเงิน",
        Refund => "ยกเลิกเพราะอนุมัติคืนเงิน",
        _ => "ยกเลิกแล้ว"
    };
}
