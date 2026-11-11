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

    public string? PromotionStatusMessage { get; set; }

    public decimal? DiscountAmount { get; set; }

    public decimal? ExtraBedAmount { get; set; }

    public decimal? TotalAmount { get; set; }

    public int? PromotionId { get; set; }

    public bool CanConfirm { get; set; }

    public bool IsSaved { get; set; }

    public int? SavedBookingId { get; set; }

    public string? StatusMessage { get; set; }
}
