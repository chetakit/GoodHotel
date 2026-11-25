namespace GoodHotel.Models.ViewModels;

// งานหนึ่งรายการ
public record HousekeepingTaskItem(
    int TaskId, string? RoomNumber, string? RoomType, string TaskType,
    string Status, int? AssignedToUserId, DateTime CreatedAtUtc,
    DateTime? AcceptedAtUtc, DateTime? StartedAtUtc, DateTime? CompletedAtUtc,
    string? ReworkReason = null);

// ข้อมูลหน้าหลักแม่บ้าน
public record HousekeepingDashboardViewModel(
    int AvailableCount, int AcceptedCount, int InProgressCount, int CompletedTodayCount,
    IReadOnlyList<HousekeepingTaskItem> RecentTasks);

// งานว่าง + งานของฉัน
public record HousekeepingTasksViewModel(
    IReadOnlyList<HousekeepingTaskItem> Available,
    IReadOnlyList<HousekeepingTaskItem> Assigned);

// รายละเอียดงาน
public record HousekeepingDetailsViewModel(HousekeepingTaskItem Task, bool IsMine);

// ประวัติหนึ่งรายการ
public record HousekeepingEventItem(
    int TaskId, string? RoomNumber, string EventType, DateTime OccurredAtUtc,
    string? PreviousStatus, string? NewStatus, string? Reason);

// ข้อมูลหน้าประวัติ
public record HousekeepingHistoryViewModel(
    IReadOnlyList<HousekeepingTaskItem> Completed,
    IReadOnlyList<HousekeepingEventItem> Events);
