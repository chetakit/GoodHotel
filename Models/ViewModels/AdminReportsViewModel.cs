namespace GoodHotel.Models.ViewModels;

/// <summary>รายงานงานโรงแรมของ Admin (ไม่มีตัวเลขเงิน)</summary>
public class AdminReportsViewModel
{
    // ว่าง = ไม่จำกัดช่วงวันที่
    public DateOnly? From { get; set; }

    public DateOnly? To { get; set; }

    // การจองที่ยังไม่ถูกยกเลิก
    public int BookingCount { get; set; }

    public int CancelledBookingCount { get; set; }

    // ในจำนวนที่ยกเลิก: ยกเลิกอัตโนมัติเพราะไม่ชำระเงินตามกำหนด
    public int PaymentTimeoutCount { get; set; }

    public List<PromotionUsageViewModel> PromotionUsage { get; set; } = new();

    public int CompletedStaysWithValidDates { get; set; }

    public decimal? AverageActualStayNights { get; set; }

    public List<AdminOperationStatusCount> HousekeepingByStatus { get; set; } = new();
    public List<AdminOperationStatusCount> MaintenanceByStatus { get; set; } = new();
}

public record AdminOperationStatusCount(string? Status, int Count);

public record PromotionUsageViewModel(string PromotionName, int BookingCount, decimal BookingAmount);
