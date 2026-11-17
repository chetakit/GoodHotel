using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace GoodHotel.Models.ViewModels;

public class RoomFormViewModel
{
    public int? RoomId { get; set; }

    [Required]
    [StringLength(20)]
    public string? RoomNumber { get; set; }

    [Required]
    [StringLength(50)]
    public string? Type { get; set; }

    [StringLength(500)]
    public string? Description { get; set; }

    [Required]
    [Range(1, int.MaxValue)]
    public int? Capacity { get; set; }

    [Required]
    [StringLength(20)]
    public string? Status { get; set; } = "Available";

    public string? OriginalStatus { get; set; }

    public string? OriginalPreMaintenanceStatus { get; set; }

    public int? OriginalMaintenanceEventId { get; set; }

    public bool HasStaleRoomStatus(string? status, string? preMaintenanceStatus, int latestEventId) =>
        !OriginalMaintenanceEventId.HasValue ||
        !string.Equals(OriginalStatus ?? "", status ?? "", StringComparison.Ordinal) ||
        !string.Equals(OriginalPreMaintenanceStatus ?? "", preMaintenanceStatus ?? "", StringComparison.Ordinal) ||
        OriginalMaintenanceEventId.Value != latestEventId;

    [BindNever]
    public bool RequiresMaintenanceReview { get; set; }

    public bool ConfirmMaintenanceReview { get; set; }

    [Required]
    [Range(typeof(decimal), "0.01", "99999999.99")]
    public decimal? Rate { get; set; }

    public string? ImagePath { get; set; }

    public IFormFile? Image { get; set; }
}
