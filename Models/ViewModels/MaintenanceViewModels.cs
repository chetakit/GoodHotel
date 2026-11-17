using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace GoodHotel.Models.ViewModels;

public class MaintenanceCreateViewModel
{
    [Range(1, int.MaxValue, ErrorMessage = "Select a room.")]
    public int RoomId { get; set; }

    [Required]
    [StringLength(2000)]
    public string? Description { get; set; }

    [BindNever]
    public List<SelectListItem> Rooms { get; set; } = new();
}

public sealed record MaintenanceListItem(
    int ReportId,
    string? RoomNumber,
    string Status,
    bool BlocksRoom,
    DateOnly? BlockFrom,
    DateOnly? BlockUntil);

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

public sealed record MaintenanceEventItem(
    int ActorUserId,
    string? FromStatus,
    string ToStatus,
    DateTime OccurredAtUtc,
    string? Note);
