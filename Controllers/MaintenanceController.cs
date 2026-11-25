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

// แจ้งซ่อมและติดตามงานซ่อม
[Authorize(Roles = "Admin,Staff,Housekeeper")]
public class MaintenanceController : Controller
{
    private readonly GoodHotelDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly ParallelReader _reader;
    private readonly UserDirectory _users;

    // รับ Service ที่ต้องใช้จากระบบ DI
    public MaintenanceController(GoodHotelDbContext db, TimeProvider timeProvider, ParallelReader reader, UserDirectory users)
    {
        _reader = reader;
        _users = users;
        _db = db;
        _timeProvider = timeProvider;
    }

    // รายการงานซ่อม
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
                report.BlockUntil,
                report.Description,
                report.ReportedAtUtc,
                UserDirectory.Label(report.ReportedByUser.FirstName, report.ReportedByUser.LastName, report.ReportedByUser.Role)))
            .ToListAsync(cancellationToken);

        return View(reports);
    }

    // [GET] ฟอร์มแจ้งซ่อม
    [HttpGet]
    [Authorize(Roles = "Staff,Housekeeper")]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        var model = new MaintenanceCreateViewModel();
        await PopulateRoomsAsync(model, cancellationToken);
        return View(model);
    }

    // [POST] บันทึกแจ้งซ่อม และปิดขายห้อง
    [HttpPost]
    [Authorize(Roles = "Staff,Housekeeper")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        MaintenanceCreateViewModel model,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            await PopulateRoomsAsync(model, cancellationToken);
            return View(model);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var room = await RoomAvailabilityService.LockAndLoadRoomAsync(_db, model.RoomId, cancellationToken, track: true);
        if (room == null)
        {
            ModelState.AddModelError(nameof(model.RoomId), "ไม่พบห้องที่เลือก");
            await PopulateRoomsAsync(model, cancellationToken);
            return View(model);
        }
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
        TempData["MaintenanceSuccess"] = "แจ้งซ่อมแล้ว ห้องถูกปิดไม่ให้จอง (การจองเดิมไม่ถูกยกเลิก) กรุณาแจ้งผู้ดูแลระบบ";
        return RedirectToAction(nameof(Index));
    }

    // รายละเอียดและประวัติงานซ่อม
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

    // [POST] เริ่มซ่อม
    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> StartProgress(int id, CancellationToken cancellationToken)
        => TransitionAsync(id, MaintenanceReport.InProgress, null, cancellationToken);

    // [POST] ซ่อมเสร็จ เปิดขายห้องกลับถ้าไม่มีงานซ่อมอื่น
    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Resolve(int id, string? resolutionNote, CancellationToken cancellationToken)
        => TransitionAsync(id, MaintenanceReport.Resolved, resolutionNote, cancellationToken);

    // เปลี่ยนสถานะงานซ่อม: Open → In Progress → Resolved (ข้ามขั้นไม่ได้)
    private async Task<IActionResult> TransitionAsync(
        int id, string nextStatus, string? resolutionNote, CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            return BadRequest();
        }

        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        if (!ModelState.IsValid || (nextStatus == MaintenanceReport.Resolved &&
            (string.IsNullOrWhiteSpace(resolutionNote) || resolutionNote.Length > 2000)))
        {
            TempData["MaintenanceError"] = "กรุณาระบุบันทึกการซ่อม (ไม่เกิน 2000 ตัวอักษร)";
            return RedirectToAction(nameof(Details), new { id });
        }

        var roomId = await _db.MaintenanceReports.AsNoTracking().Where(item => item.ReportId == id)
            .Select(item => (int?)item.RoomId).SingleOrDefaultAsync(cancellationToken);
        if (!roomId.HasValue) return NotFound();
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var room = await RoomAvailabilityService.LockAndLoadRoomAsync(_db, roomId.Value, cancellationToken, track: true);
        if (room == null) return NotFound();

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
            TempData["MaintenanceError"] = "สถานะงานซ่อมเปลี่ยนไปแล้ว หรือทำรายการนี้ไม่ได้";
            return RedirectToAction(nameof(Details), new { id });
        }

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
            room.Status = MaintenanceReport.RoomStatusAfterResolution(
                room.PreMaintenanceStatus, otherUnresolved);
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
            TempData["MaintenanceError"] = "งานซ่อมนี้ถูกเปลี่ยนไปแล้ว กรุณาตรวจสอบอีกครั้ง";
            return RedirectToAction(nameof(Details), new { id });
        }

        TempData["MaintenanceSuccess"] = nextStatus == MaintenanceReport.Resolved
            ? (requiresReview
                ? "ปิดงานซ่อมแล้ว ห้องยังเป็นสถานะซ่อมบำรุงจนกว่าผู้ดูแลระบบจะตรวจสอบ"
                : "ปิดงานซ่อมแล้ว")
            : "เริ่มซ่อมแล้ว";
        return RedirectToAction(nameof(Details), new { id });
    }

    // รหัสผู้ใช้ที่ล็อกอินอยู่
    private bool TryGetUserId(out int userId)
    {
        return int.TryParse(
                   User.FindFirstValue(ClaimTypes.NameIdentifier),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out userId) && userId > 0;
    }

    // อ่านข้อมูลงานซ่อมหนึ่งงาน
    private async Task<MaintenanceDetailsViewModel?> GetDetailsAsync(int id, CancellationToken cancellationToken)
    {
        // งานซ่อม ประวัติ และชื่อคนที่เกี่ยวข้อง ไม่ขึ้นต่อกัน อ่านพร้อมกัน
        var reportTask = _reader.Run(db => db.MaintenanceReports.AsNoTracking()
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
            .SingleOrDefaultAsync(cancellationToken));
        var namesTask = _reader.Run(db => db.Users.AsNoTracking()
            .Where(u => db.MaintenanceReports.Any(r => r.ReportId == id &&
                            (r.ReportedByUserId == u.UserId || r.ResolvedByUserId == u.UserId)) ||
                        db.MaintenanceReportEvents.Any(e => e.ReportId == id && e.ActorUserId == u.UserId))
            .Select(u => new { u.UserId, u.FirstName, u.LastName, u.Role }).ToListAsync(cancellationToken));
        var eventsTask = _reader.Run(db => db.MaintenanceReportEvents.AsNoTracking()
            .Where(item => item.ReportId == id)
            .OrderBy(item => item.EventId)
            .Select(item => new MaintenanceEventItem(item.ActorUserId, item.FromStatus,
                item.ToStatus, item.OccurredAtUtc, item.Note))
            .ToListAsync(cancellationToken));
        await Task.WhenAll(reportTask, namesTask, eventsTask);

        var report = reportTask.Result;
        if (report == null) return null;
        foreach (var person in namesTask.Result)
            _users.Remember(person.UserId, person.FirstName, person.LastName, person.Role);
        var events = eventsTask.Result;
        return new MaintenanceDetailsViewModel(report.ReportId, report.RoomNumber, report.Description,
            report.Status, report.BlocksRoom, report.BlockFrom, report.BlockUntil, report.ReportedByUserId,
            report.ResolvedByUserId, report.ResolutionNote, report.ResolvedAtUtc, events);
    }

    // ใส่รายการห้องใน dropdown
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
