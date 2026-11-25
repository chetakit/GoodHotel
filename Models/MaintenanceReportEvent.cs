namespace GoodHotel.Models;

// ตารางประวัติงานซ่อม
public class MaintenanceReportEvent
{
    public int EventId { get; set; }
    public int ReportId { get; set; }
    public int ActorUserId { get; set; }
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public string? Note { get; set; }

    public MaintenanceReport Report { get; set; } = null!;
    public User ActorUser { get; set; } = null!;
}
