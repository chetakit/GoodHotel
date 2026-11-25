namespace GoodHotel.Models.ViewModels;

public record StaffDashboardViewModel(
    int CheckedInStays, int TodayCheckIns, int TodayCheckOuts,
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
    DateOnly? CheckIn, DateOnly? CheckOut, int? Guests, bool RefundPending, int? RoomId)
{
    /// <summary>ห้องยังเช็คอินไม่ได้เพราะอะไร เช่น "รอทำความสะอาด" (null = พร้อม)</summary>
    public string? RoomNotReady { get; init; }

    /// <summary>ติดแค่รอพนักงานตรวจห้อง กดไปตรวจได้เลย</summary>
    public bool AwaitingInspection { get; init; }

    public string GuestName => $"{FirstName} {LastName}".Trim();
    public bool IsPaid { get; init; }
}

public record StaffTaskViewModel(
    int TaskId, string? RoomNumber, string? RoomType, string TaskType, string Status,
    int? AssignedToUserId, int? CompletedByUserId, DateTime? CompletedAtUtc);
