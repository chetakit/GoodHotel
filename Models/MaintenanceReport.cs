namespace GoodHotel.Models;

// ตาราง maintenance_reports: งานซ่อม
public class MaintenanceReport
{
    public const string Open = "Open";
    public const string InProgress = "In Progress";
    public const string Resolved = "Resolved";

    // เปลี่ยนสถานะได้ทีละขั้นเท่านั้น
    public static bool CanTransition(string current, string next) =>
        (current == Open && next == InProgress) ||
        (current == InProgress && next == Resolved);

    // จำสถานะห้องก่อนซ่อม ไว้คืนตอนซ่อมเสร็จ
    public static string? CapturePreviousRoomStatus(string? current, string? previous, bool hasUnresolved) =>
        hasUnresolved ? previous : current is "Available" or "Occupied" or "Reserved" ? current : null;

    /// <summary>
    /// ซ่อมเสร็จ: คืนสถานะขายเดิมทันที แม้ยังมีแขกหรือรอทำความสะอาด
    /// (สองอย่างนั้นกันการจองและเช็คอินเองอยู่แล้ว) ห้องที่ Admin ปิดซ่อมไว้เองไม่มีสถานะเดิม จึงยังปิดอยู่
    /// </summary>
    public static string RoomStatusAfterResolution(
        string? previous, bool hasOtherUnresolved) =>
        !hasOtherUnresolved && previous is "Available" or "Occupied" or "Reserved"
            ? previous : "Maintenance";

    public int ReportId { get; set; }
    public int RoomId { get; set; }
    public string Description { get; set; } = string.Empty; // อาการที่แจ้ง
    public string Status { get; set; } = Open;
    public bool BlocksRoom { get; set; }
    public DateOnly? BlockFrom { get; set; }
    public DateOnly? BlockUntil { get; set; }
    public DateTime ReportedAtUtc { get; set; } // เวลาแจ้ง
    public int ReportedByUserId { get; set; } // คนแจ้ง
    public DateTime? ResolvedAtUtc { get; set; } // เวลาซ่อมเสร็จ
    public int? ResolvedByUserId { get; set; } // คนปิดงาน
    public string? ResolutionNote { get; set; } // บันทึกการซ่อม

    public Room Room { get; set; } = null!;
    public User ReportedByUser { get; set; } = null!;
    public User? ResolvedByUser { get; set; }
    public ICollection<MaintenanceReportEvent> Events { get; set; } = new List<MaintenanceReportEvent>();
}
