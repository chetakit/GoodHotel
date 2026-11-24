namespace GoodHotel.Models.ViewModels;

public record StaffDashboardViewModel(
    int TotalBookings, int CheckedInStays, int TodayCheckIns, int TodayCheckOuts,
    int AwaitingVerification, int OpenMaintenanceReports)
{
    /// <summary>แขกที่จะเข้าพักวันนี้ (ยังไม่เช็คอิน)</summary>
    public IReadOnlyList<FrontDeskRow> Arrivals { get; init; } = [];

    /// <summary>แขกที่ถึงกำหนดออกวันนี้ (หรือเลยกำหนดแล้ว) แต่ยังไม่เช็คเอาท์</summary>
    public IReadOnlyList<FrontDeskRow> Departures { get; init; } = [];
}

/// <summary>หนึ่งแถวในรายการเข้า/ออกวันนี้ของหน้าแดชบอร์ด Staff</summary>
public record FrontDeskRow(
    int BookingId, string? FirstName, string? LastName, string? Phone, string? RoomNumber,
    DateOnly? CheckIn, DateOnly? CheckOut, int? Guests, bool RefundPending)
{
    public string GuestName => $"{FirstName} {LastName}".Trim();
    public bool IsPaid { get; init; }
}

public record StaffTaskViewModel(
    int TaskId, string? RoomNumber, string? RoomType, string TaskType, string Status,
    int? AssignedToUserId, int? CompletedByUserId, DateTime? CompletedAtUtc);
