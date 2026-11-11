using System.ComponentModel.DataAnnotations;

namespace GoodHotel.Models.ViewModels;

public class PromotionFormViewModel
{
    public int? PromotionId { get; set; }

    public bool UseDiscountCode { get; set; } = true;

    public bool IsLegacyPromotion { get; set; }

    public string? LegacyCondition { get; set; }

    public string? MetadataError { get; set; }

    [Required]
    [StringLength(100)]
    public string? PromotionName { get; set; }

    [StringLength(255)]
    public string? Description { get; set; }

    [StringLength(32)]
    public string? PromotionCode { get; set; }

    public string? DiscountType { get; set; } = "PERCENT";

    public decimal? DiscountValue { get; set; }

    [StringLength(10)]
    public string? StartDate { get; set; }

    [StringLength(10)]
    public string? EndDate { get; set; }

    public bool IsActive { get; set; } = true;

    [StringLength(255)]
    public string? Benefit { get; set; }
}
