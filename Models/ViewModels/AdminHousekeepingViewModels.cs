namespace GoodHotel.Models.ViewModels;

public record AdminHousekeepingTaskItem(
    int TaskId, int RoomId, string? RoomNumber, string TaskType, string Status,
    int? AssignedToUserId, string? AssigneeFirstName, string? AssigneeLastName,
    DateTime CreatedAtUtc);

public record AdminHousekeepingEventItem(
    int EventId, int ActorUserId, string EventType, DateTime OccurredAtUtc,
    string? PreviousStatus, string? NewStatus, int? PreviousAssignee, int? NewAssignee, string? Reason);

public record AdminHousekeepingDetailsViewModel(
    AdminHousekeepingTaskItem Task, IReadOnlyList<AdminHousekeepingEventItem> Events,
    IReadOnlyList<AdminHousekeeperItem> ActiveHousekeepers, int LatestEventId);

public record AdminHousekeeperItem(int UserId, string? FirstName, string? LastName);
