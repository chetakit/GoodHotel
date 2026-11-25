using System.Security.Claims;
using GoodHotel.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GoodHotel.Controllers;

// หน้าของแม่บ้าน: กติกาทั้งหมดอยู่ใน HousekeepingService
[Authorize(Roles = "Housekeeper")]
public class HousekeeperController(HousekeepingService housekeeping) : Controller
{
    // หน้าหลัก: จำนวนงานและงานล่าสุด
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Forbid();
        return View(await housekeeping.DashboardAsync(userId, ct));
    }

    // งานว่างที่รับได้ และงานที่ตัวเองรับไว้
    [HttpGet]
    public async Task<IActionResult> Tasks(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Forbid();
        return View(await housekeeping.TasksAsync(userId, ct));
    }

    // รายละเอียดงาน
    [HttpGet]
    public async Task<IActionResult> TaskDetails(int id, CancellationToken ct)
    {
        if (!ModelState.IsValid || id <= 0) return BadRequest();
        if (!TryGetUserId(out var userId)) return Forbid();
        var task = await housekeeping.DetailsAsync(id, userId, ct);
        return task == null ? NotFound() : View(task);
    }

    // ประวัติงานของตัวเอง
    [HttpGet]
    public async Task<IActionResult> History(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Forbid();
        return View(await housekeeping.HistoryAsync(userId, ct));
    }

    // [POST] รับงาน
    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Claim(int id, CancellationToken ct) => Transition(id, "Claim", ct);

    // [POST] เริ่มทำ
    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Start(int id, CancellationToken ct) => Transition(id, "Start", ct);

    /// <summary>ปุ่มเดียว: รับงานแล้วเริ่มทำทันที (ประวัติยังบันทึกแยก 2 ขั้นเหมือนเดิม)</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClaimAndStart(int id, CancellationToken ct)
    {
        if (!ModelState.IsValid || id <= 0) return BadRequest();
        // คุกกี้ถูกตรวจกับตารางผู้ใช้ทุกคำขอแล้ว (UserAccessCache)
        if (!TryGetUserId(out var userId)) return Forbid();

        if (await housekeeping.TransitionAsync(id, userId, "ClaimAndStart", ct))
            TempData["TaskSuccess"] = "รับงานแล้ว เริ่มทำได้เลย";
        else
            TempData["TaskError"] = "ทำรายการไม่ได้ งานนี้อาจมีคนรับไปแล้ว";

        return RedirectToAction(nameof(Tasks));
    }

    // [POST] ทำเสร็จ ส่งให้พนักงานต้อนรับตรวจ
    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Complete(int id, CancellationToken ct) => Transition(id, "Complete", ct);

    // ใช้ร่วมกันทุกปุ่ม: เปลี่ยนสถานะงาน แล้วกลับพร้อมข้อความ
    private async Task<IActionResult> Transition(int id, string action, CancellationToken ct)
    {
        if (!ModelState.IsValid || id <= 0) return BadRequest();
        // คุกกี้ถูกตรวจกับตารางผู้ใช้ทุกคำขอแล้ว (UserAccessCache)
        if (!TryGetUserId(out var userId)) return Forbid();

        if (await housekeeping.TransitionAsync(id, userId, action, ct))
            TempData["TaskSuccess"] = "อัปเดตงานแล้ว";
        else
            TempData["TaskError"] = "ทำรายการไม่ได้ งานนี้ถูกเปลี่ยนไปแล้ว";

        return RedirectToAction(nameof(Tasks));
    }

    // รหัสแม่บ้านที่ล็อกอินอยู่
    private bool TryGetUserId(out int userId) =>
        int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId) && userId > 0;
}
