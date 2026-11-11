using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

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

    [Required]
    [Range(typeof(decimal), "0.01", "99999999.99")]
    public decimal? Rate { get; set; }

    public string? ImagePath { get; set; }

    public IFormFile? Image { get; set; }
}
