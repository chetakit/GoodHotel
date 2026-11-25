using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace GoodHotel.Models.ViewModels;

// ฟอร์มเพิ่ม/แก้ห้อง
public class RoomFormViewModel
{
    public int? RoomId { get; set; }

    [Required(ErrorMessage = "กรุณากรอกหมายเลขห้อง")]
    [StringLength(20)]
    public string? RoomNumber { get; set; }

    [Required(ErrorMessage = "กรุณากรอกประเภทห้อง")]
    [StringLength(50)]
    public string? Type { get; set; }

    [StringLength(500)]
    public string? Description { get; set; }

    [Required(ErrorMessage = "กรุณากรอกจำนวนคนที่รับได้")]
    [Range(1, int.MaxValue, ErrorMessage = "ต้องรับได้อย่างน้อย 1 คน")]
    public int? Capacity { get; set; }

    [Required(ErrorMessage = "กรุณาเลือกสถานะห้อง")]
    [StringLength(20)]
    public string? Status { get; set; } = "Available";

    public string? OriginalStatus { get; set; }

    public string? OriginalPreMaintenanceStatus { get; set; }

    public int? OriginalMaintenanceEventId { get; set; }

    // สถานะห้องเปลี่ยนไปหลังเปิดฟอร์มหรือไม่
    public bool HasStaleRoomStatus(string? status, string? preMaintenanceStatus, int latestEventId) =>
        !OriginalMaintenanceEventId.HasValue ||
        !string.Equals(OriginalStatus ?? "", status ?? "", StringComparison.Ordinal) ||
        !string.Equals(OriginalPreMaintenanceStatus ?? "", preMaintenanceStatus ?? "", StringComparison.Ordinal) ||
        OriginalMaintenanceEventId.Value != latestEventId;

    [BindNever]
    public bool RequiresMaintenanceReview { get; set; }

    public bool ConfirmMaintenanceReview { get; set; }

    [Required(ErrorMessage = "กรุณากรอกราคาต่อคืน")]
    [Range(typeof(decimal), "0.01", "99999999.99", ErrorMessage = "ราคาต่อคืนต้องมากกว่า 0")]
    public decimal? Rate { get; set; }

    public string? ImagePath { get; set; }

    public IFormFile? Image { get; set; }
}
