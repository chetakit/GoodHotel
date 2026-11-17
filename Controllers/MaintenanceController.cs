using System.Globalization;
using System.Security.Claims;
using GoodHotel.Models;
using GoodHotel.Models.ViewModels;
using GoodHotel.Services;
using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Controllers;

[Authorize(Roles = "Admin,Staff,Housekeeper")]
public class MaintenanceController : Controller
{
    private readonly GoodHotelDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly RoomAvailabilityService _availability;

    public MaintenanceController(GoodHotelDbContext db, TimeProvider timeProvider, RoomAvailabilityService availability)
    {
        _db = db;
        _timeProvider = timeProvider;
        _availability = availability;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var reports = await _db.MaintenanceReports
            .AsNoTracking()
            .OrderByDescending(report => report.ReportId)
            .Select(report => new MaintenanceListItem(
                report.ReportId,
                report.Room.Room1,
                report.Status,
                report.BlocksRoom,
                report.BlockFrom,
                report.BlockUntil))
            .ToListAsync(cancellationToken);

        return View(reports);
    }

    [HttpGet]
    [Authorize(Roles = "Staff,Housekeeper")]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        var model = new MaintenanceCreateViewModel();
        await PopulateRoomsAsync(model, cancellationToken);
        return View(model);
    }

    [HttpPost]
    [Authorize(Roles = "Staff,Housekeeper")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        MaintenanceCreateViewModel model,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId) ||
            !await _db.Users.AsNoTracking().AnyAsync(user => user.UserId == userId, cancellationToken))
        {
            return Forbid();
        }

        if (ModelState.IsValid &&
            !await _db.Rooms.AsNoTracking().AnyAsync(room => room.RoomId == model.RoomId, cancellationToken))
        {
            ModelState.AddModelError(nameof(model.RoomId), "The selected room was not found.");
        }

        if (!ModelState.IsValid)
        {
            await PopulateRoomsAsync(model, cancellationToken);
            return View(model);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await RoomAvailabilityService.LockRoomAsync(_db, transaction, model.RoomId, cancellationToken))
            return NotFound();

        var room = await _db.Rooms.SingleAsync(item => item.RoomId == model.RoomId, cancellationToken);
        var hasUnresolved = await _db.MaintenanceReports.AnyAsync(
            item => item.RoomId == model.RoomId && item.Status != MaintenanceReport.Resolved, cancellationToken);
        room.PreMaintenanceStatus = MaintenanceReport.CapturePreviousRoomStatus(
            room.Status, room.PreMaintenanceStatus, hasUnresolved);
        room.Status = "Maintenance";
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var report = new MaintenanceReport
        {
            RoomId = model.RoomId,
            Description = model.Description!.Trim(),
            Status = MaintenanceReport.Open,
            BlocksRoom = true,
            ReportedByUserId = userId,
            ReportedAtUtc = now
        };
        report.Events.Add(new MaintenanceReportEvent
        {
            ActorUserId = userId,
            ToStatus = MaintenanceReport.Open,
            OccurredAtUtc = now
        });
        _db.MaintenanceReports.Add(report);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        TempData["MaintenanceSuccess"] = "Report created and room blocked. Existing bookings are unchanged; notify an Admin.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || id <= 0)
        {
            return BadRequest();
        }

        var report = await GetDetailsAsync(id, cancellationToken);
        return report == null ? NotFound() : View(report);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> StartProgress(int id, CancellationToken cancellationToken)
        => TransitionAsync(id, MaintenanceReport.InProgress, null, cancellationToken);

    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Resolve(int id, string? resolutionNote, CancellationToken cancellationToken)
        => TransitionAsync(id, MaintenanceReport.Resolved, resolutionNote, cancellationToken);

    private async Task<IActionResult> TransitionAsync(
        int id, string nextStatus, string? resolutionNote, CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            return BadRequest();
        }

        if (!TryGetUserId(out var userId) ||
            !await _db.Users.AsNoTracking().AnyAsync(user => user.UserId == userId, cancellationToken))
        {
            return Forbid();
        }

        if (!ModelState.IsValid || (nextStatus == MaintenanceReport.Resolved &&
            (string.IsNullOrWhiteSpace(resolutionNote) || resolutionNote.Length > 2000)))
        {
            TempData["MaintenanceError"] = "A resolution note of at most 2000 characters is required.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var roomId = await _db.MaintenanceReports.AsNoTracking().Where(item => item.ReportId == id)
            .Select(item => (int?)item.RoomId).SingleOrDefaultAsync(cancellationToken);
        if (!roomId.HasValue) return NotFound();
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await RoomAvailabilityService.LockRoomAsync(_db, transaction, roomId.Value, cancellationToken))
            return NotFound();

        var report = await _db.MaintenanceReports
            .SingleOrDefaultAsync(item => item.ReportId == id, cancellationToken);
        if (report == null || report.RoomId != roomId.Value)
        {
            return NotFound();
        }

        var expectedStatus = nextStatus == MaintenanceReport.InProgress
            ? MaintenanceReport.Open : MaintenanceReport.InProgress;
        if (!MaintenanceReport.CanTransition(report.Status, nextStatus))
        {
            TempData["MaintenanceError"] = "The report status changed or this transition is not allowed.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var room = await _db.Rooms.SingleAsync(item => item.RoomId == roomId.Value, cancellationToken);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var requiresReview = false;
        report.Status = nextStatus;
        if (nextStatus == MaintenanceReport.Resolved)
        {
            report.ResolvedAtUtc = now;
            report.ResolvedByUserId = userId;
            report.ResolutionNote = resolutionNote!.Trim();
            var otherUnresolved = await _db.MaintenanceReports.AnyAsync(item =>
                item.RoomId == roomId.Value && item.ReportId != id &&
                item.Status != MaintenanceReport.Resolved, cancellationToken);
            var hasCurrentRestrictions = !otherUnresolved &&
                await _availability.HasCurrentRestrictionsAsync(roomId.Value, cancellationToken);
            room.Status = MaintenanceReport.RoomStatusAfterResolution(
                room.PreMaintenanceStatus, otherUnresolved, hasCurrentRestrictions);
            if (room.Status != "Maintenance")
            {
                room.PreMaintenanceStatus = null;
            }
            else
            {
                requiresReview = !otherUnresolved;
            }
        }
        else
        {
            room.Status = "Maintenance";
        }
        _db.MaintenanceReportEvents.Add(new MaintenanceReportEvent
        {
            ReportId = id,
            ActorUserId = userId,
            FromStatus = expectedStatus,
            ToStatus = nextStatus,
            OccurredAtUtc = now,
            Note = nextStatus == MaintenanceReport.Resolved ? report.ResolutionNote : null
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            TempData["MaintenanceError"] = "This report changed. Review it before trying again.";
            return RedirectToAction(nameof(Details), new { id });
        }

        TempData["MaintenanceSuccess"] = nextStatus == MaintenanceReport.Resolved
            ? (requiresReview
                ? "Report resolved. The room remains in Maintenance for Admin review."
                : "Maintenance report resolved.")
            : "Maintenance work is in progress.";
        return RedirectToAction(nameof(Details), new { id });
    }

    private bool TryGetUserId(out int userId)
    {
        return int.TryParse(
                   User.FindFirstValue(ClaimTypes.NameIdentifier),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out userId) && userId > 0;
    }

    private async Task<MaintenanceDetailsViewModel?> GetDetailsAsync(int id, CancellationToken cancellationToken)
    {
        var report = await _db.MaintenanceReports.AsNoTracking()
            .Where(item => item.ReportId == id)
            .Select(item => new
            {
                item.ReportId,
                RoomNumber = item.Room.Room1,
                item.Description,
                item.Status,
                item.BlocksRoom,
                item.BlockFrom,
                item.BlockUntil,
                item.ReportedByUserId,
                item.ResolvedByUserId,
                item.ResolutionNote,
                item.ResolvedAtUtc
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (report == null) return null;
        var events = await _db.MaintenanceReportEvents.AsNoTracking()
            .Where(item => item.ReportId == id)
            .OrderBy(item => item.EventId)
            .Select(item => new MaintenanceEventItem(item.ActorUserId, item.FromStatus,
                item.ToStatus, item.OccurredAtUtc, item.Note))
            .ToListAsync(cancellationToken);
        return new MaintenanceDetailsViewModel(report.ReportId, report.RoomNumber, report.Description,
            report.Status, report.BlocksRoom, report.BlockFrom, report.BlockUntil, report.ReportedByUserId,
            report.ResolvedByUserId, report.ResolutionNote, report.ResolvedAtUtc, events);
    }

    private async Task PopulateRoomsAsync(MaintenanceCreateViewModel model, CancellationToken cancellationToken)
    {
        model.Rooms = await _db.Rooms
            .AsNoTracking()
            .OrderBy(room => room.Room1)
            .Select(room => new SelectListItem
            {
                Value = room.RoomId.ToString(),
                Text = room.Room1 ?? ("Room ID " + room.RoomId)
            })
            .ToListAsync(cancellationToken);
    }
}
