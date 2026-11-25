using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace GoodHotel.Models.ViewModels;

// ฟอร์มแจ้งซ่อม
public class MaintenanceCreateViewModel
{
    [Required(ErrorMessage = "กรุณาเลือกห้อง")]
    [Range(1, int.MaxValue, ErrorMessage = "กรุณาเลือกห้อง")]
    public int RoomId { get; set; }

    [Required(ErrorMessage = "กรุณาอธิบายปัญหาที่พบ")]
    [StringLength(2000)]
    public string? Description { get; set; }

    [BindNever]
    public List<SelectListItem> Rooms { get; set; } = new();
}

// งานซ่อมหนึ่งแถว
public sealed record MaintenanceListItem(
    int ReportId,
    string? RoomNumber,
    string Status,
    bool BlocksRoom,
    DateOnly? BlockFrom,
    DateOnly? BlockUntil,
    string Description,
    DateTime ReportedAtUtc,
    string? ReportedBy);

// รายละเอียดงานซ่อม
public sealed record MaintenanceDetailsViewModel(
    int ReportId,
    string? RoomNumber,
    string Description,
    string Status,
    bool BlocksRoom,
    DateOnly? BlockFrom,
    DateOnly? BlockUntil,
    int ReportedByUserId,
    int? ResolvedByUserId,
    string? ResolutionNote,
    DateTime? ResolvedAtUtc,
    IReadOnlyList<MaintenanceEventItem> Events);

// ประวัติงานซ่อมหนึ่งรายการ
public sealed record MaintenanceEventItem(
    int ActorUserId,
    string? FromStatus,
    string ToStatus,
    DateTime OccurredAtUtc,
    string? Note);
