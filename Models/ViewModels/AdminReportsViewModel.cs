namespace GoodHotel.Models.ViewModels;

public class AdminReportsViewModel
{
    // การจองที่ยังไม่ถูกยกเลิก
    public int BookingCount { get; set; }

    public int CancelledBookingCount { get; set; }

    public List<PromotionUsageViewModel> PromotionUsage { get; set; } = new();

    public decimal? BookingAmount { get; set; }

    public int CompletedStaysWithValidDates { get; set; }

    public decimal? AverageActualStayNights { get; set; }

    public int PaymentCount { get; set; }

    public decimal? Revenue { get; set; }

    public List<PaymentStatusSummaryViewModel> PaymentsByStatus { get; set; } = new();
    public List<AdminOperationStatusCount> HousekeepingByStatus { get; set; } = new();
    public List<AdminOperationStatusCount> MaintenanceByStatus { get; set; } = new();
}

public record AdminOperationStatusCount(string? Status, int Count);

public record PromotionUsageViewModel(string PromotionName, int BookingCount, decimal BookingAmount);

public class PaymentStatusSummaryViewModel
{
    public string? Status { get; set; }

    public int Count { get; set; }

    public decimal? Amount { get; set; }
}
