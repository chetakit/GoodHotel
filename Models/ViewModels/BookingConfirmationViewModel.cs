namespace GoodHotel.Models.ViewModels;

public class BookingConfirmationViewModel : CreateBookingViewModel
{
    public string? RoomNumber { get; set; }

    public string? RoomType { get; set; }

    public decimal? Rate { get; set; }

    public int? Capacity { get; set; }

    public int? NightCount { get; set; }

    public decimal? EstimatedAmount { get; set; }

    public decimal? RoomSubtotal { get; set; }

    // โปรที่ใช้กับการจองนี้ (ลูกค้าเลือก หรือโปรที่ดีที่สุด; null = ไม่เข้าเงื่อนไขโปรไหนเลย)
    public string? PromotionName { get; set; }

    // โปรทั้งหมดที่การจองนี้ใช้ได้ (ตัวเลือกใน dropdown) เรียงจากลดมากไปน้อย
    public IReadOnlyList<GoodHotel.Services.AppliedPromotion> AvailablePromotions { get; set; } = [];

    public decimal? DiscountAmount { get; set; }

    public decimal? ExtraBedAmount { get; set; }

    public decimal? TotalAmount { get; set; }

    // PromotionId (โปรที่ใช้จริง) สืบทอดมาจาก CreateBookingViewModel

    public bool CanConfirm { get; set; }

    public bool IsSaved { get; set; }

    public int? SavedBookingId { get; set; }

    public string PaymentStatus { get; set; } = "Not submitted";

    public bool IsMockPayment { get; set; }

    public bool CanSubmitPayment { get; set; }

    // เส้นตายชำระเงินของการจองที่เพิ่งบันทึก (null = ไม่ต้องแสดง)
    public DateTime? PaymentDueAtUtc { get; set; }

    public string? StatusMessage { get; set; }
}
