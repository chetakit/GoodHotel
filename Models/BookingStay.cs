namespace GoodHotel.Models;

// ตาราง booking_stays: การเข้าพักจริง (มีแถว = เช็คอินแล้ว)
public class BookingStay
{
    public int BookingId { get; set; } // เลขที่การจอง (1 การจองมีการเข้าพัก 1 ครั้ง)
    public string Status { get; set; } = "CheckedIn"; // CheckedIn หรือ CheckedOut
    public DateTime CheckedInAtUtc { get; set; } // เวลาเช็คอิน
    public int CheckedInByUserId { get; set; } // พนักงานที่เช็คอิน
    public DateTime? CheckedOutAtUtc { get; set; } // เวลาเช็คเอาท์
    public int? CheckedOutByUserId { get; set; } // พนักงานที่เช็คเอาท์
    public bool IsEarlyCheckIn { get; set; }
    public string? CheckInNote { get; set; } // หมายเหตุ

    public Booking Booking { get; set; } = null!;
    public User CheckedInByUser { get; set; } = null!;
    public User? CheckedOutByUser { get; set; }
}
