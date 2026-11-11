using System.ComponentModel.DataAnnotations;

namespace GoodHotel.Models.ViewModels;

public sealed class ApplyPromotionRequestViewModel
{
    [Range(1, int.MaxValue)]
    public int RoomId { get; set; }

    [Required]
    [StringLength(10)]
    public string? CheckIn { get; set; }

    [Required]
    [StringLength(10)]
    public string? CheckOut { get; set; }

    [Range(1, int.MaxValue)]
    public int Guests { get; set; }

    [Range(0, 1)]
    public int ExtraBedCount { get; set; }

    [StringLength(64)]
    public string? PromotionCode { get; set; }
}

public sealed record ApplyPromotionResponseViewModel(
    bool IsValidRequest,
    string PromotionStatus,
    string Message,
    int? NightCount = null,
    decimal? RoomSubtotal = null,
    decimal? DiscountAmount = null,
    decimal? ExtraBedAmount = null,
    decimal? TotalAmount = null);
