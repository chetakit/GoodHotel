namespace GoodHotel.Models.ViewModels;

public record StaffDashboardViewModel(
    int TotalBookings, int CheckedInStays, int TodayCheckIns, int TodayCheckOuts,
    int AwaitingVerification, int OpenMaintenanceReports);

public record StaffTaskViewModel(
    int TaskId, string? RoomNumber, string? RoomType, string TaskType, string Status,
    int? AssignedToUserId, int? CompletedByUserId, DateTime? CompletedAtUtc);
