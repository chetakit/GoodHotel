namespace GoodHotel.Models;

public class HousekeepingTaskEvent
{
    public int EventId { get; set; }
    public int TaskId { get; set; }
    public int ActorUserId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public string? PreviousStatus { get; set; }
    public string? NewStatus { get; set; }
    public int? PreviousAssignee { get; set; }
    public int? NewAssignee { get; set; }
    public string? Reason { get; set; }

    public HousekeepingTask Task { get; set; } = null!;
    public User ActorUser { get; set; } = null!;
    public User? PreviousAssigneeUser { get; set; }
    public User? NewAssigneeUser { get; set; }
}
