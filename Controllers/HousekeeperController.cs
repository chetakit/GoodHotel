using System.Security.Claims;
using GoodHotel.Models;
using GoodHotel.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Controllers;

[Authorize(Roles = "Housekeeper")]
public class HousekeeperController(HousekeepingService housekeeping, GoodHotelDbContext db) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Forbid();
        return View(await housekeeping.DashboardAsync(userId, ct));
    }

    [HttpGet]
    public async Task<IActionResult> Tasks(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Forbid();
        return View(await housekeeping.TasksAsync(userId, ct));
    }

    [HttpGet]
    public async Task<IActionResult> TaskDetails(int id, CancellationToken ct)
    {
        if (!ModelState.IsValid || id <= 0) return BadRequest();
        if (!TryGetUserId(out var userId)) return Forbid();
        var task = await housekeeping.DetailsAsync(id, userId, ct);
        return task == null ? NotFound() : View(task);
    }

    [HttpGet]
    public async Task<IActionResult> History(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Forbid();
        return View(await housekeeping.HistoryAsync(userId, ct));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Claim(int id, CancellationToken ct) => Transition(id, "Claim", ct);

    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Start(int id, CancellationToken ct) => Transition(id, "Start", ct);

    /// <summary>ปุ่มเดียว: รับงานแล้วเริ่มทำทันที (ประวัติยังบันทึกแยก 2 ขั้นเหมือนเดิม)</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClaimAndStart(int id, CancellationToken ct)
    {
        if (!ModelState.IsValid || id <= 0) return BadRequest();
        if (!TryGetUserId(out var userId) ||
            !await db.Users.AsNoTracking().AnyAsync(u => u.UserId == userId, ct))
            return Forbid();

        if (await housekeeping.TransitionAsync(id, userId, "Claim", ct) &&
            await housekeeping.TransitionAsync(id, userId, "Start", ct))
            TempData["TaskSuccess"] = "รับงานแล้ว เริ่มทำได้เลย";
        else
            TempData["TaskError"] = "ทำรายการไม่ได้ งานนี้อาจมีคนรับไปแล้ว";

        return RedirectToAction(nameof(Tasks));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Complete(int id, CancellationToken ct) => Transition(id, "Complete", ct);

    [HttpGet]
    public IActionResult MaintenanceReports() => RedirectToAction("Index", "Maintenance");

    [HttpGet]
    public IActionResult MaintenanceHistory() => RedirectToAction("Index", "Maintenance");

    private async Task<IActionResult> Transition(int id, string action, CancellationToken ct)
    {
        if (!ModelState.IsValid || id <= 0) return BadRequest();
        if (!TryGetUserId(out var userId) ||
            !await db.Users.AsNoTracking().AnyAsync(u => u.UserId == userId, ct))
            return Forbid();

        if (await housekeeping.TransitionAsync(id, userId, action, ct))
            TempData["TaskSuccess"] = "อัปเดตงานแล้ว";
        else
            TempData["TaskError"] = "ทำรายการไม่ได้ งานนี้ถูกเปลี่ยนไปแล้ว";

        return RedirectToAction(nameof(Tasks));
    }

    private bool TryGetUserId(out int userId) =>
        int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId) && userId > 0;
}
