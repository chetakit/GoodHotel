namespace GoodHotel.Models.ViewModels;

// งานแม่บ้านหนึ่งแถว (Admin)
public record AdminHousekeepingTaskItem(
    int TaskId, int RoomId, string? RoomNumber, string TaskType, string Status,
    int? AssignedToUserId, string? AssigneeFirstName, string? AssigneeLastName,
    DateTime CreatedAtUtc);

// ประวัติงานหนึ่งรายการ
public record AdminHousekeepingEventItem(
    int EventId, int ActorUserId, string EventType, DateTime OccurredAtUtc,
    string? PreviousStatus, string? NewStatus, int? PreviousAssignee, int? NewAssignee, string? Reason);

// ข้อมูลหน้ารายละเอียดงาน
public record AdminHousekeepingDetailsViewModel(
    AdminHousekeepingTaskItem Task, IReadOnlyList<AdminHousekeepingEventItem> Events,
    IReadOnlyList<AdminHousekeeperItem> ActiveHousekeepers, int LatestEventId);

// แม่บ้านที่เลือกได้
public record AdminHousekeeperItem(int UserId, string? FirstName, string? LastName);
