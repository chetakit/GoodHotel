namespace GoodHotel.Models;

// ตาราง payment_refunds: คำขอคืนเงิน
public class PaymentRefund
{
    public int RefundId { get; set; }
    public int PaymentId { get; set; }
    public decimal Amount { get; set; } // จำนวนเงินที่คืน
    public string Status { get; set; } = "Requested"; // Requested, Approved, Rejected, Closed
    public string Reason { get; set; } = string.Empty; // เหตุผลของลูกค้า
    public int RequestedByUserId { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public int? ApprovedByUserId { get; set; } // บัญชีที่อนุมัติ
    public DateTime? ApprovedAtUtc { get; set; }

    public Payment Payment { get; set; } = null!;
    public User RequestedByUser { get; set; } = null!;
    public User? ApprovedByUser { get; set; }
}
