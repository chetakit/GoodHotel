namespace GoodHotel.Models;

// ตาราง payments: การชำระเงิน
public partial class Payment
{
    public int PaymentId { get; set; } // เลขชำระเงิน

    public int? BookingId { get; set; } // จ่ายของการจองไหน

    public string? PaymentMethod { get; set; } // วิธีจ่าย

    public string? PaymentStatus { get; set; } // สถานะ เช่น Paid, Refunded

    public DateOnly? PaymentDate { get; set; } // วันที่จ่าย

    public decimal? Amount { get; set; } // จำนวนเงิน

    public DateTime? SubmittedAtUtc { get; set; } // เวลาส่งรายการ
    public int? SubmittedByUserId { get; set; } // คนส่งรายการ
    public int? VerifiedByUserId { get; set; } // คนยืนยัน
    public DateTime? VerifiedAtUtc { get; set; } // เวลายืนยัน (มีค่า = จ่ายแล้ว)
    public string? EvidenceFileName { get; set; } // รูปหลักฐาน (ระบบเก่า)
    public string? EvidenceSha256 { get; set; }
    public string? LegacyDisposition { get; set; } // ผลตรวจข้อมูลเก่า

    public virtual Booking? Booking { get; set; }
    public User? SubmittedByUser { get; set; }
    public User? VerifiedByUser { get; set; }
    public ICollection<PaymentEvent> Events { get; set; } = new List<PaymentEvent>();
    public ICollection<PaymentRefund> Refunds { get; set; } = new List<PaymentRefund>();
}
