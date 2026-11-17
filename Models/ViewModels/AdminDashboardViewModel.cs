namespace GoodHotel.Models.ViewModels;

public class AdminDashboardViewModel
{
    public int RoomCount { get; set; }

    public int UserCount { get; set; }

    public int BookingCount { get; set; }

    public decimal? Revenue { get; set; }

    public int OpenMaintenanceCount { get; set; }
    public int PendingHousekeepingCount { get; set; }
    public int AwaitingVerificationCount { get; set; }

    public List<DashboardBookingViewModel> BookingsByCheckInDate { get; set; } = new();

    public List<RoomStatusCountViewModel> RoomStatusCounts { get; set; } = new();
}

public class DashboardBookingViewModel
{
    public int BookingId { get; set; }

    public string? GuestName { get; set; }

    public string? RoomNumber { get; set; }

    public DateOnly? CheckIn { get; set; }

    public decimal? Amount { get; set; }
}

public class RoomStatusCountViewModel
{
    public string? Status { get; set; }

    public int Count { get; set; }
}
