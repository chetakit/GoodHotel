using System.Data;
using System.Data.Common;
using GoodHotel.Models;
using GoodHotel.Models.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GoodHotel.Services;

public class HousekeepingService(GoodHotelDbContext db, TimeProvider timeProvider)
{
    // Lock users before updating tasks; multiple user locks must be taken in ID order.
    public static async Task<bool> LockUserAsync(GoodHotelDbContext db, int userId, CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("User locking requires a transaction.");
        await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "SELECT `UserId` FROM `users` WHERE `UserId` = @userId FOR UPDATE";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@userId";
        parameter.Value = userId;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync(ct);
        return result != null && result != DBNull.Value;
    }

    public IQueryable<AdminHousekeepingTaskItem> AdminTasks(string? status, int? roomId, int? assigneeId)
    {
        var tasks = db.HousekeepingTasks.AsNoTracking();
        if (!string.IsNullOrEmpty(status)) tasks = tasks.Where(t => t.Status == status);
        if (roomId.HasValue) tasks = tasks.Where(t => t.RoomId == roomId);
        if (assigneeId.HasValue) tasks = tasks.Where(t => t.AssignedToUserId == assigneeId);
        return tasks.OrderByDescending(t => t.TaskId).Select(t => new AdminHousekeepingTaskItem(
            t.TaskId, t.RoomId, t.Room.Room1, t.TaskType, t.Status, t.AssignedToUserId,
            t.AssignedToUser == null ? null : t.AssignedToUser.FirstName,
            t.AssignedToUser == null ? null : t.AssignedToUser.LastName, t.CreatedAtUtc));
    }

    public async Task<AdminHousekeepingDetailsViewModel?> AdminDetailsAsync(int id, CancellationToken ct)
    {
        var task = await AdminTasks(null, null, null).Where(t => t.TaskId == id).SingleOrDefaultAsync(ct);
        if (task == null) return null;
        var events = await db.HousekeepingTaskEvents.AsNoTracking().Where(e => e.TaskId == id)
            .OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => e.EventId)
            .Select(e => new AdminHousekeepingEventItem(e.EventId, e.ActorUserId, e.EventType,
                e.OccurredAtUtc, e.PreviousStatus, e.NewStatus, e.PreviousAssignee, e.NewAssignee, e.Reason))
            .ToListAsync(ct);
        var users = await db.Users.AsNoTracking()
            .Where(u => u.Role == "Housekeeper" && u.IsActive)
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName).ThenBy(u => u.UserId)
            .Select(u => new { u.UserId, u.FirstName, u.LastName, u.Role })
            .ToListAsync(ct);
        var housekeepers = users.Where(u => u.Role == "Housekeeper")
            .Select(u => new AdminHousekeeperItem(u.UserId, u.FirstName, u.LastName)).ToList();
        return new AdminHousekeepingDetailsViewModel(task, events, housekeepers,
            events.Count == 0 ? 0 : events.Max(e => e.EventId));
    }

    private static string? ValidateAssignment(string status, int? previousAssignee, int newAssignee, string? reason)
    {
        if (reason?.Length > 2000) return "เหตุผลต้องไม่เกิน 2000 ตัวอักษร";
        if (status == "Pending" && previousAssignee == null) return null;
        if ((status == "Accepted" || status == "InProgress") && previousAssignee.HasValue)
        {
            if (previousAssignee == newAssignee) return "กรุณาเลือกแม่บ้านคนอื่น";
            if (string.IsNullOrWhiteSpace(reason)) return "กรุณาระบุเหตุผลในการเปลี่ยนผู้รับงาน";
            return null;
        }
        return "มอบหมายงานนี้ไม่ได้ในสถานะปัจจุบัน";
    }

    public async Task<string?> AssignAsync(int taskId, int newAssignee, int actorId,
        string expectedStatus, int? expectedAssignee, int expectedEventId, string? reason, CancellationToken ct)
    {
        if (taskId <= 0 || newAssignee <= 0 || actorId <= 0 || expectedAssignee is <= 0 || expectedEventId < 0)
            return "ไม่พบงานหรือผู้ใช้";

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var snapshot = await db.HousekeepingTasks.AsNoTracking().Where(t => t.TaskId == taskId)
            .Select(t => new { t.Status, t.AssignedToUserId }).SingleOrDefaultAsync(ct);
        if (snapshot == null) return "ไม่พบงาน";
        if (snapshot.Status != expectedStatus || snapshot.AssignedToUserId != expectedAssignee)
            return "งานนี้ถูกเปลี่ยนไปแล้ว กรุณาตรวจสอบอีกครั้ง";

        var error = ValidateAssignment(snapshot.Status, snapshot.AssignedToUserId, newAssignee, reason);
        if (error != null) return error;

        var userIds = new[] { actorId, newAssignee, snapshot.AssignedToUserId ?? 0 }
            .Where(id => id > 0).Distinct().OrderBy(id => id);
        foreach (var userId in userIds)
            if (!await LockUserAsync(db, userId, ct)) return "ไม่พบผู้ใช้ที่เกี่ยวข้อง";

        var actor = await db.Users.AsNoTracking().Where(u => u.UserId == actorId)
            .Select(u => new { u.Role, u.IsActive }).SingleOrDefaultAsync(ct);
        if (actor == null || actor.Role != "Admin" || !actor.IsActive)
            return "บัญชีผู้ดูแลระบบนี้ไม่มีสิทธิ์แล้ว";
        var assignee = await db.Users.AsNoTracking().Where(u => u.UserId == newAssignee)
            .Select(u => new { u.Role, u.IsActive }).SingleOrDefaultAsync(ct);
        if (assignee == null || assignee.Role != "Housekeeper" || !assignee.IsActive)
            return "กรุณาเลือกแม่บ้านที่ยังใช้งานอยู่";

        var current = await db.HousekeepingTasks.AsNoTracking().Where(t => t.TaskId == taskId)
            .Select(t => new { t.Status, t.AssignedToUserId }).SingleOrDefaultAsync(ct);
        if (current == null || current.Status != snapshot.Status ||
            current.AssignedToUserId != snapshot.AssignedToUserId)
            return "งานนี้ถูกเปลี่ยนไปแล้ว กรุณาตรวจสอบอีกครั้ง";
        var latestEventId = await db.HousekeepingTaskEvents.AsNoTracking()
            .Where(e => e.TaskId == taskId).MaxAsync(e => (int?)e.EventId, ct) ?? 0;
        if (latestEventId != expectedEventId)
            return "งานนี้ถูกเปลี่ยนไปแล้ว กรุณาตรวจสอบอีกครั้ง";

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var changed = await db.HousekeepingTasks
            .Where(t => t.TaskId == taskId && t.Status == snapshot.Status &&
                t.AssignedToUserId == snapshot.AssignedToUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "Accepted")
                .SetProperty(t => t.AssignedToUserId, newAssignee)
                .SetProperty(t => t.AcceptedAtUtc, now)
                .SetProperty(t => t.StartedAtUtc, (DateTime?)null), ct);
        if (changed != 1) return "งานนี้ถูกเปลี่ยนไปแล้ว กรุณาตรวจสอบอีกครั้ง";

        db.HousekeepingTaskEvents.Add(new HousekeepingTaskEvent
        {
            TaskId = taskId, ActorUserId = actorId,
            EventType = snapshot.AssignedToUserId.HasValue ? "Reassign" : "Assign",
            OccurredAtUtc = now, PreviousStatus = snapshot.Status, NewStatus = "Accepted",
            PreviousAssignee = snapshot.AssignedToUserId, NewAssignee = newAssignee,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return null;
    }

    public async Task CreateCheckoutCleaningAsync(int roomId, int bookingId, int actorId, DateTime now, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Checkout cleaning must be created within the checkout transaction.");

        var task = new HousekeepingTask
        {
            RoomId = roomId, StayBookingId = bookingId, TaskType = "CheckoutCleaning",
            Status = "Pending", CreatedByUserId = actorId, CreatedAtUtc = now
        };
        task.Events.Add(new HousekeepingTaskEvent
        {
            ActorUserId = actorId, EventType = "Created", OccurredAtUtc = now, NewStatus = "Pending"
        });
        db.HousekeepingTasks.Add(task);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<StaffTaskViewModel>> AwaitingVerificationAsync(CancellationToken ct) =>
        await db.HousekeepingTasks.AsNoTracking().Where(t => t.Status == "Completed")
            .OrderBy(t => t.CompletedAtUtc)
            .Select(t => new StaffTaskViewModel(t.TaskId, t.Room.Room1, t.Room.Type, t.TaskType,
                t.Status, t.AssignedToUserId, t.CompletedByUserId, t.CompletedAtUtc))
            .ToListAsync(ct);

    public async Task<StaffTaskViewModel?> StaffDetailsAsync(int id, CancellationToken ct) =>
        await db.HousekeepingTasks.AsNoTracking().Where(t => t.TaskId == id)
            .Select(t => new StaffTaskViewModel(t.TaskId, t.Room.Room1, t.Room.Type, t.TaskType,
                t.Status, t.AssignedToUserId, t.CompletedByUserId, t.CompletedAtUtc))
            .SingleOrDefaultAsync(ct);

    public async Task<bool> VerifyAsync(int taskId, int actorId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.HousekeepingTasks
            .Where(t => t.TaskId == taskId && t.Status == "Completed" &&
                t.AssignedToUserId != actorId && t.CompletedByUserId != actorId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "Verified")
                .SetProperty(t => t.VerifiedAtUtc, now)
                .SetProperty(t => t.VerifiedByUserId, actorId), ct);
        if (changed != 1) return false;
        var assignee = await db.HousekeepingTasks.Where(t => t.TaskId == taskId)
            .Select(t => t.AssignedToUserId).SingleAsync(ct);
        db.HousekeepingTaskEvents.Add(new HousekeepingTaskEvent
        {
            TaskId = taskId, ActorUserId = actorId, EventType = "Verify", OccurredAtUtc = now,
            PreviousStatus = "Completed", NewStatus = "Verified",
            PreviousAssignee = assignee, NewAssignee = assignee
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// ตรวจไม่ผ่าน: ส่งกลับให้แม่บ้านคนเดิมทำใหม่ (ถ้าคนเดิมใช้งานไม่ได้แล้ว ส่งกลับเข้ากองให้คนอื่นรับ)
    /// ห้องยังเช็คอินไม่ได้ เพราะงานทำความสะอาดยังไม่ Verified
    /// </summary>
    public async Task<string?> ReworkAsync(int taskId, int actorId, string? reason, CancellationToken ct)
    {
        reason = reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 500) return "กรุณาระบุสิ่งที่ต้องแก้ (ไม่เกิน 500 ตัวอักษร)";
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var assignee = await db.HousekeepingTasks.AsNoTracking().Where(t => t.TaskId == taskId)
            .Select(t => t.AssignedToUserId).SingleOrDefaultAsync(ct);
        var keep = assignee.HasValue && await LockUserAsync(db, assignee.Value, ct) &&
            await db.Users.AsNoTracking().AnyAsync(u => u.UserId == assignee && u.IsActive && u.Role == "Housekeeper", ct);
        var next = keep ? "InProgress" : "Pending";
        var nextAssignee = keep ? assignee : null;
        var changed = await db.HousekeepingTasks
            .Where(t => t.TaskId == taskId && t.Status == "Completed" && t.AssignedToUserId == assignee &&
                t.AssignedToUserId != actorId && t.CompletedByUserId != actorId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, next)
                .SetProperty(t => t.AssignedToUserId, nextAssignee)
                .SetProperty(t => t.CompletedAtUtc, (DateTime?)null)
                .SetProperty(t => t.CompletedByUserId, (int?)null), ct);
        if (changed != 1) return "ส่งงานนี้กลับไม่ได้ (งานถูกเปลี่ยนไปแล้ว หรือคุณเป็นคนทำงานนี้เอง)";
        db.HousekeepingTaskEvents.Add(new HousekeepingTaskEvent
        {
            TaskId = taskId, ActorUserId = actorId, EventType = "Rework", OccurredAtUtc = now,
            PreviousStatus = "Completed", NewStatus = next,
            PreviousAssignee = assignee, NewAssignee = nextAssignee, Reason = reason
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return null;
    }

    private IQueryable<HousekeepingTaskItem> TaskItems(IQueryable<HousekeepingTask> tasks) =>
        tasks.Select(t => new HousekeepingTaskItem(
            t.TaskId, t.Room.Room1, t.Room.Type, t.TaskType, t.Status, t.AssignedToUserId,
            t.CreatedAtUtc, t.AcceptedAtUtc, t.StartedAtUtc, t.CompletedAtUtc,
            t.Status == "Completed" || t.Status == "Verified" ? null
                : t.Events.Where(e => e.EventType == "Rework").OrderByDescending(e => e.EventId)
                    .Select(e => e.Reason).FirstOrDefault()));

    public async Task<HousekeepingDashboardViewModel> DashboardAsync(int userId, CancellationToken ct)
    {
        var tasks = db.HousekeepingTasks.AsNoTracking();
        var today = timeProvider.GetUtcNow().UtcDateTime.Date;
        var tomorrow = today.AddDays(1);
        var available = await tasks.CountAsync(t => t.Status == "Pending" && t.AssignedToUserId == null, ct);
        var accepted = await tasks.CountAsync(t => t.AssignedToUserId == userId && t.Status == "Accepted", ct);
        var inProgress = await tasks.CountAsync(t => t.AssignedToUserId == userId && t.Status == "InProgress", ct);
        var completed = await tasks.CountAsync(t => t.CompletedByUserId == userId && t.CompletedAtUtc >= today && t.CompletedAtUtc < tomorrow, ct);
        var recent = await TaskItems(tasks.Where(t => t.AssignedToUserId == userId)
            .OrderByDescending(t => t.TaskId).Take(5)).ToListAsync(ct);
        return new HousekeepingDashboardViewModel(available, accepted, inProgress, completed, recent);
    }

    public async Task<HousekeepingTasksViewModel> TasksAsync(int userId, CancellationToken ct)
    {
        var tasks = db.HousekeepingTasks.AsNoTracking();
        var available = await TaskItems(tasks.Where(t => t.Status == "Pending" && t.AssignedToUserId == null)
            .OrderBy(t => t.CreatedAtUtc)).ToListAsync(ct);
        var assigned = await TaskItems(tasks.Where(t => t.AssignedToUserId == userId &&
            (t.Status == "Accepted" || t.Status == "InProgress"))
            .OrderBy(t => t.CreatedAtUtc)).ToListAsync(ct);
        return new HousekeepingTasksViewModel(available, assigned);
    }

    public async Task<HousekeepingDetailsViewModel?> DetailsAsync(int id, int userId, CancellationToken ct)
    {
        var item = await TaskItems(db.HousekeepingTasks.AsNoTracking().Where(t => t.TaskId == id &&
            ((t.Status == "Pending" && t.AssignedToUserId == null) ||
             t.AssignedToUserId == userId ||
             (t.CompletedByUserId == userId && (t.Status == "Completed" || t.Status == "Verified")))))
            .SingleOrDefaultAsync(ct);
        return item == null ? null : new HousekeepingDetailsViewModel(item, item.AssignedToUserId == userId);
    }

    public async Task<HousekeepingHistoryViewModel> HistoryAsync(int userId, CancellationToken ct)
    {
        var completed = await TaskItems(db.HousekeepingTasks.AsNoTracking()
            .Where(t => t.CompletedByUserId == userId && (t.Status == "Completed" || t.Status == "Verified"))
            .OrderByDescending(t => t.CompletedAtUtc)).ToListAsync(ct);
        var events = await db.HousekeepingTaskEvents.AsNoTracking()
            .Where(e => e.ActorUserId == userId)
            .OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => e.EventId)
            .Select(e => new HousekeepingEventItem(e.TaskId, e.Task.Room.Room1, e.EventType,
                e.OccurredAtUtc, e.PreviousStatus, e.NewStatus, e.Reason))
            .ToListAsync(ct);
        return new HousekeepingHistoryViewModel(completed, events);
    }

    public async Task<bool> TransitionAsync(int taskId, int userId, string action, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var transaction = action == "Claim"
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
            : await db.Database.BeginTransactionAsync(ct);
        if (!await LockUserAsync(db, userId, ct)) return false;
        int changed;
        string previous;
        string next;
        int? previousAssignee;
        switch (action)
        {
            case "Claim":
                if (!await db.Users.AsNoTracking().AnyAsync(u =>
                        u.UserId == userId && u.Role == "Housekeeper" && u.IsActive, ct))
                    return false;
                previous = "Pending";
                next = "Accepted";
                previousAssignee = null;
                changed = await db.HousekeepingTasks
                    .Where(t => t.TaskId == taskId && t.Status == previous && t.AssignedToUserId == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, next)
                        .SetProperty(t => t.AssignedToUserId, userId)
                        .SetProperty(t => t.AcceptedAtUtc, now), ct);
                break;
            case "Start":
                previous = "Accepted";
                next = "InProgress";
                previousAssignee = userId;
                changed = await db.HousekeepingTasks
                    .Where(t => t.TaskId == taskId && t.Status == previous && t.AssignedToUserId == userId)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, next)
                        .SetProperty(t => t.StartedAtUtc, now), ct);
                break;
            case "Complete":
                previous = "InProgress";
                next = "Completed";
                previousAssignee = userId;
                changed = await db.HousekeepingTasks
                    .Where(t => t.TaskId == taskId && t.Status == previous && t.AssignedToUserId == userId)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, next)
                        .SetProperty(t => t.CompletedAtUtc, now)
                        .SetProperty(t => t.CompletedByUserId, userId), ct);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }

        if (changed != 1)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        db.HousekeepingTaskEvents.Add(new HousekeepingTaskEvent
        {
            TaskId = taskId, ActorUserId = userId, EventType = action, OccurredAtUtc = now,
            PreviousStatus = previous, NewStatus = next, PreviousAssignee = previousAssignee,
            NewAssignee = userId
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
