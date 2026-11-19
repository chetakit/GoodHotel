using System.ComponentModel.DataAnnotations;
using GoodHotel.Services;

namespace GoodHotel.Models.ViewModels;

/// <summary>ฟอร์มสร้าง/แก้ไขโปรของ Admin</summary>
public class PromotionFormViewModel
{
    public int? PromotionId { get; set; }

    [Required(ErrorMessage = "กรุณากรอกชื่อโปรโมชัน")]
    [StringLength(100)]
    public string? PromotionName { get; set; }

    [StringLength(255)]
    public string? Description { get; set; }

    [Required(ErrorMessage = "กรุณาเลือกประเภทโปร")]
    public string? RuleType { get; set; }

    [Range(0, 365, ErrorMessage = "ตัวเลขเงื่อนไขต้องอยู่ระหว่าง 0-365")]
    public int? RuleValue { get; set; }

    [Required]
    public string DiscountType { get; set; } = PromotionRules.Percent;

    [Required(ErrorMessage = "กรุณากรอกมูลค่าส่วนลด")]
    [Range(0.01, 99999999, ErrorMessage = "ส่วนลดต้องมากกว่า 0")]
    public decimal? DiscountValue { get; set; }

    [Range(0, 99999999, ErrorMessage = "ลดสูงสุดต้องไม่ติดลบ")]
    public decimal? MaxDiscount { get; set; }

    [Range(0, 99999999, ErrorMessage = "ยอดขั้นต่ำต้องไม่ติดลบ")]
    public decimal? MinSpend { get; set; }

    // รับเป็นข้อความ yyyy-MM-dd เพราะเครื่องตั้งภาษาไทยจะแปลงวันที่เป็นปี พ.ศ.
    [StringLength(10)]
    public string? StartDate { get; set; }

    [StringLength(10)]
    public string? EndDate { get; set; }

    public bool IsActive { get; set; } = true;
}
