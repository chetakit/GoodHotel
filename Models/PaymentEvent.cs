namespace GoodHotel.Models;

public class PaymentEvent
{
    public int EventId { get; set; }
    public int PaymentId { get; set; }
    public int ActorUserId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public decimal? Amount { get; set; }
    public string? PaymentMethod { get; set; }
    public string? Reason { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;

    public Payment Payment { get; set; } = null!;
    public User ActorUser { get; set; } = null!;
}
