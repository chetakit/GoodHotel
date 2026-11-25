namespace GoodHotel.Models;

public partial class Payment
{
    public int PaymentId { get; set; }

    public int? BookingId { get; set; }

    public string? PaymentMethod { get; set; }

    public string? PaymentStatus { get; set; }

    public DateOnly? PaymentDate { get; set; }

    public decimal? Amount { get; set; }

    public DateTime? SubmittedAtUtc { get; set; }
    public int? SubmittedByUserId { get; set; }
    public int? VerifiedByUserId { get; set; }
    public DateTime? VerifiedAtUtc { get; set; }
    public string? EvidenceFileName { get; set; }
    public string? EvidenceSha256 { get; set; }
    public string? LegacyDisposition { get; set; }

    public virtual Booking? Booking { get; set; }
    public User? SubmittedByUser { get; set; }
    public User? VerifiedByUser { get; set; }
    public ICollection<PaymentEvent> Events { get; set; } = new List<PaymentEvent>();
    public ICollection<PaymentRefund> Refunds { get; set; } = new List<PaymentRefund>();
}
