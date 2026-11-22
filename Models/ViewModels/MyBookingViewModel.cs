namespace GoodHotel.Models.ViewModels;

public class MyBookingViewModel
{
    public int BookingId { get; set; }

    public string? ImagePath { get; set; }

    public string? RoomNumber { get; set; }

    public string? RoomType { get; set; }

    public DateOnly? CheckIn { get; set; }

    public DateOnly? CheckOut { get; set; }

    public int? Guests { get; set; }

    public int? NightCount { get; set; }

    public decimal? Amount { get; set; }

    public decimal? OutstandingAmount { get; set; }

    public string PaymentStatus { get; set; } = "Not submitted";

    public bool IsMockPayment { get; set; }

    // "Booked" / "Cancelled"
    public string BookingStatus { get; set; } = BookingStatuses.Booked;

    // null = ยังไม่ check-in, "CheckedIn", "CheckedOut"
    public string? StayStatus { get; set; }

    public bool CanCancel { get; set; }

    public bool CanRequestRefund { get; set; }

    // คำขอคืนเงินล่าสุด: null = ไม่เคยขอ, Requested / Approved / Rejected / Closed
    public string? RefundStatus { get; set; }

    public DateTime? CreatedAtUtc { get; set; }

    public string? CancelReason { get; set; }

    // เส้นตายชำระเงิน (null = ไม่มี เช่น จ่ายแล้ว มีการชำระที่รอยืนยัน หรือการจองเก่า)
    public DateTime? PaymentDueAtUtc { get; set; }

    // true = อยู่ในแท็บ "ประวัติการเข้าพัก" (ยกเลิกแล้ว / เช็คเอาท์แล้ว / เลยวันแล้ว)
    public bool IsHistory { get; set; }

    public string StayLabel => BookingStatus == BookingStatuses.Cancelled
        ? (CancelReason == CancelReasons.PaymentTimeout ? "ยกเลิก · ไม่ชำระเงินตามกำหนด" : "ยกเลิกแล้ว")
        : StayStatus switch
        {
            "CheckedIn" => "กำลังเข้าพัก",
            "CheckedOut" => "เข้าพักเสร็จแล้ว",
            _ when IsHistory => "ไม่ได้เข้าพัก",
            _ => "รอเข้าพัก"
        };

    public string StayBadgeClass => StayLabel switch
    {
        "ยกเลิกแล้ว" => "text-bg-secondary",
        "กำลังเข้าพัก" => "text-bg-success",
        "เข้าพักเสร็จแล้ว" => "text-bg-dark",
        "ไม่ได้เข้าพัก" => "text-bg-warning",
        _ => "text-bg-primary"
    };
}
