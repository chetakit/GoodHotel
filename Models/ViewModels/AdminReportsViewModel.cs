namespace GoodHotel.Models.ViewModels;

public class AdminReportsViewModel
{
    public int BookingCount { get; set; }

    public decimal? BookingAmount { get; set; }

    public int BookingsWithValidStayDates { get; set; }

    public decimal? AverageStayNights { get; set; }

    public int PaymentCount { get; set; }

    public decimal? RecordedPaymentAmount { get; set; }

    public List<PaymentStatusSummaryViewModel> PaymentsByStatus { get; set; } = new();
}

public class PaymentStatusSummaryViewModel
{
    public string? Status { get; set; }

    public int Count { get; set; }

    public decimal? Amount { get; set; }
}
