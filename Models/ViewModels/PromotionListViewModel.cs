namespace GoodHotel.Models.ViewModels;

public class PromotionListViewModel
{
    public int PromotionId { get; set; }

    public string? PromotionName { get; set; }

    public string? Description { get; set; }

    public string? Condition { get; set; }

    public string? Benefit { get; set; }

    public string? PromotionCode { get; set; }

    public string? DiscountType { get; set; }

    public decimal? DiscountValue { get; set; }

    public DateOnly? StartDate { get; set; }

    public DateOnly? EndDate { get; set; }

    public bool? IsActive { get; set; }

    public bool IsLegacyPromotion { get; set; }

    public string? MetadataError { get; set; }
}
