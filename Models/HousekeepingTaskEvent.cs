namespace GoodHotel.Models;

// ตาราง housekeeping_task_events: ประวัติว่าใครทำอะไรกับงาน
public class HousekeepingTaskEvent
{
    public int EventId { get; set; }
    public int TaskId { get; set; }
    public int ActorUserId { get; set; }
    public string EventType { get; set; } = string.Empty; // ประเภทเหตุการณ์ เช่น Claim, Start, Complete
    public DateTime OccurredAtUtc { get; set; }
    public string? PreviousStatus { get; set; } // สถานะก่อน
    public string? NewStatus { get; set; } // สถานะหลัง
    public int? PreviousAssignee { get; set; }
    public int? NewAssignee { get; set; }
    public string? Reason { get; set; }

    public HousekeepingTask Task { get; set; } = null!;
    public User ActorUser { get; set; } = null!;
    public User? PreviousAssigneeUser { get; set; }
    public User? NewAssigneeUser { get; set; }
}
