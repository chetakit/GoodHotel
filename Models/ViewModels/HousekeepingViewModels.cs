namespace GoodHotel.Models.ViewModels;

public record HousekeepingTaskItem(
    int TaskId, string? RoomNumber, string? RoomType, string TaskType,
    string Status, int? AssignedToUserId, DateTime CreatedAtUtc,
    DateTime? AcceptedAtUtc, DateTime? StartedAtUtc, DateTime? CompletedAtUtc);

public record HousekeepingDashboardViewModel(
    int AvailableCount, int AcceptedCount, int InProgressCount, int CompletedTodayCount,
    IReadOnlyList<HousekeepingTaskItem> RecentTasks);

public record HousekeepingTasksViewModel(
    IReadOnlyList<HousekeepingTaskItem> Available,
    IReadOnlyList<HousekeepingTaskItem> Assigned);

public record HousekeepingDetailsViewModel(HousekeepingTaskItem Task, bool IsMine);

public record HousekeepingEventItem(
    int TaskId, string? RoomNumber, string EventType, DateTime OccurredAtUtc,
    string? PreviousStatus, string? NewStatus, string? Reason);

public record HousekeepingHistoryViewModel(
    IReadOnlyList<HousekeepingTaskItem> Completed,
    IReadOnlyList<HousekeepingEventItem> Events);
