namespace GoodHotel.Models;

// ตาราง promotions: โปรโมชั่น
public partial class Promotion
{
    public int PromotionId { get; set; } // เลขโปร

    public string? PromotionName { get; set; } // ชื่อโปร

    public string? Description { get; set; }

    // ---------- เงื่อนไข ----------

    // ประเภทโปร ดูค่าใน PromotionRules (EarlyBird, LongStay, ...)
    public string? RuleType { get; set; }

    // ตัวเลข X ของเงื่อนไข เช่น จองล่วงหน้า X วัน / พัก X คืน / เคยพัก X ครั้ง
    public int? RuleValue { get; set; }

    // ยอดค่าห้องขั้นต่ำ (ไม่รวมเตียงเสริม)
    public decimal? MinSpend { get; set; }

    // ช่วงวันที่เข้าพักที่ใช้โปรได้ (ว่าง = ไม่จำกัด)
    public DateOnly? StartDate { get; set; }

    public DateOnly? EndDate { get; set; }

    public bool IsActive { get; set; } = true; // เปิดใช้อยู่ไหม

    // ---------- ส่วนลด ----------

    // "Percent" หรือ "Fixed" (บาท)
    public string? DiscountType { get; set; }

    public decimal? DiscountValue { get; set; }

    // ลดได้สูงสุดกี่บาท (ใช้กับแบบ % เท่านั้น)
    public decimal? MaxDiscount { get; set; }

    // ---------- ช่องเก่าจากระบบ Promotion Code เดิม (ไม่ได้ใช้แล้ว เก็บไว้เพื่อไม่ให้ข้อมูลเดิมหาย) ----------

    public string? Condition { get; set; }

    public string? Benefit { get; set; }

    public virtual ICollection<Booking> Bookings { get; set; } = new List<Booking>();
}
