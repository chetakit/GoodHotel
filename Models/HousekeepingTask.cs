namespace GoodHotel.Models;

// ตาราง housekeeping_tasks: งานทำความสะอาด
public class HousekeepingTask
{
    public int TaskId { get; set; } // เลขงาน
    public int RoomId { get; set; } // ห้อง
    public int? StayBookingId { get; set; } // มาจากการจองไหน
    public string TaskType { get; set; } = string.Empty; // ประเภทงาน
    public string Status { get; set; } = "Pending"; // Pending, Accepted, InProgress, Completed, Verified
    public int? AssignedToUserId { get; set; } // แม่บ้านที่รับงาน
    public int CreatedByUserId { get; set; } // คนที่ทำให้เกิดงาน (คนเช็คเอาท์)
    public DateTime CreatedAtUtc { get; set; } // เวลาที่งานเกิด
    public DateTime? AcceptedAtUtc { get; set; } // เวลารับงาน
    public DateTime? StartedAtUtc { get; set; } // เวลาเริ่มทำ
    public DateTime? CompletedAtUtc { get; set; } // เวลาทำเสร็จ
    public int? CompletedByUserId { get; set; }
    public int? VerifiedByUserId { get; set; } // พนักงานที่ตรวจ
    public DateTime? VerifiedAtUtc { get; set; } // เวลาตรวจผ่าน

    public Room Room { get; set; } = null!;
    public User? AssignedToUser { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public User? CompletedByUser { get; set; }
    public User? VerifiedByUser { get; set; }
    public ICollection<HousekeepingTaskEvent> Events { get; set; } = new List<HousekeepingTaskEvent>();
}
