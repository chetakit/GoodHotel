namespace GoodHotel.Models;

public class PaymentRefund
{
    public int RefundId { get; set; }
    public int PaymentId { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; } = "Requested";
    public string Reason { get; set; } = string.Empty;
    public int RequestedByUserId { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public int? ApprovedByUserId { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }

    public Payment Payment { get; set; } = null!;
    public User RequestedByUser { get; set; } = null!;
    public User? ApprovedByUser { get; set; }
}
