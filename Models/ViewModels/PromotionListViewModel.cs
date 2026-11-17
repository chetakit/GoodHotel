using GoodHotel.Services;

namespace GoodHotel.Models.ViewModels;

/// <summary>การ์ดโปร 1 ใบ (ใช้ทั้งหน้า Admin และหน้าลูกค้า)</summary>
public class PromotionListViewModel
{
    public int PromotionId { get; set; }

    public string? PromotionName { get; set; }

    public string? Description { get; set; }

    public string RuleLabel { get; set; } = "";

    public string ConditionText { get; set; } = "";

    public string DiscountText { get; set; } = "";

    public bool IsActive { get; set; }

    // โปรเก่าจากระบบ Promotion Code ที่ยังไม่ได้ตั้งเงื่อนไขใหม่
    public bool NeedsSetup { get; set; }

    public static PromotionListViewModel From(Promotion promotion) => new()
    {
        PromotionId = promotion.PromotionId,
        PromotionName = promotion.PromotionName,
        Description = promotion.Description,
        RuleLabel = promotion.RuleType != null && PromotionRules.All.TryGetValue(promotion.RuleType, out var rule)
            ? rule.Label
            : "ยังไม่ได้ตั้งเงื่อนไข",
        ConditionText = PromotionRules.DescribeCondition(promotion),
        DiscountText = promotion.DiscountValue > 0 ? PromotionRules.DescribeDiscount(promotion) : "-",
        IsActive = promotion.IsActive,
        NeedsSetup = promotion.RuleType == null || promotion.DiscountType == null
    };
}
