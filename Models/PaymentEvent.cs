namespace GoodHotel.Models;

// ตาราง payment_events: ประวัติเรื่องเงินทุกครั้ง
public class PaymentEvent
{
    public int EventId { get; set; }
    public int PaymentId { get; set; }
    public int ActorUserId { get; set; } // คนทำ
    public string EventType { get; set; } = string.Empty; // ทำอะไร
    public DateTime OccurredAtUtc { get; set; }
    public decimal? Amount { get; set; }
    public string? PaymentMethod { get; set; }
    public string? Reason { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty; // รหัสกันกดซ้ำ ใช้ได้ครั้งเดียว

    public Payment Payment { get; set; } = null!;
    public User ActorUser { get; set; } = null!;
}
