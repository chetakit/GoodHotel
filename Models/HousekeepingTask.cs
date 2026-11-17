namespace GoodHotel.Models;

public class HousekeepingTask
{
    public int TaskId { get; set; }
    public int RoomId { get; set; }
    public int? StayBookingId { get; set; }
    public string TaskType { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public int? AssignedToUserId { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? AcceptedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int? CompletedByUserId { get; set; }
    public int? VerifiedByUserId { get; set; }
    public DateTime? VerifiedAtUtc { get; set; }

    public Room Room { get; set; } = null!;
    public User? AssignedToUser { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public User? CompletedByUser { get; set; }
    public User? VerifiedByUser { get; set; }
    public ICollection<HousekeepingTaskEvent> Events { get; set; } = new List<HousekeepingTaskEvent>();
}
