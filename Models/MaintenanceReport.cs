namespace GoodHotel.Models;

public class MaintenanceReport
{
    public const string Open = "Open";
    public const string InProgress = "In Progress";
    public const string Resolved = "Resolved";

    public static bool CanTransition(string current, string next) =>
        (current == Open && next == InProgress) ||
        (current == InProgress && next == Resolved);

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
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = Open;
    public bool BlocksRoom { get; set; }
    public DateOnly? BlockFrom { get; set; }
    public DateOnly? BlockUntil { get; set; }
    public DateTime ReportedAtUtc { get; set; }
    public int ReportedByUserId { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public int? ResolvedByUserId { get; set; }
    public string? ResolutionNote { get; set; }

    public Room Room { get; set; } = null!;
    public User ReportedByUser { get; set; } = null!;
    public User? ResolvedByUser { get; set; }
    public ICollection<MaintenanceReportEvent> Events { get; set; } = new List<MaintenanceReportEvent>();
}
