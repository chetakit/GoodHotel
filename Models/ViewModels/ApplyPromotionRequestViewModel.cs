using System.ComponentModel.DataAnnotations;

namespace GoodHotel.Models.ViewModels;

/// <summary>คำขอคำนวณราคาใหม่ในหน้าจอง (เช่น ตอนเปลี่ยนจำนวนเตียงเสริม)</summary>
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

    // โปรที่ลูกค้าเลือก (null = โปรที่ลดได้มากที่สุด)
    public int? PromotionId { get; set; }
}

// คำตอบราคาที่ส่งกลับให้ site.js เป็น JSON
public sealed record ApplyPromotionResponseViewModel(
    bool IsValidRequest,
    string Message,
    string? PromotionName = null,
    int? NightCount = null,
    decimal? RoomSubtotal = null,
    decimal? DiscountAmount = null,
    decimal? ExtraBedAmount = null,
    decimal? TotalAmount = null,
    int? PromotionId = null);
