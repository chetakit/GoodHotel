using System.Security.Claims;
using GoodHotel.Models;
using GoodHotel.Models.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Controllers;

/// <summary>บัญชีของตัวเอง: เปลี่ยนรหัสผ่าน (ทุกบทบาท) และข้อมูลส่วนตัว (ลูกค้า)</summary>
[Authorize]
public class AccountController(GoodHotelDbContext db, IPasswordHasher<User> hasher) : Controller
{
    /// <summary>ลูกค้าแก้ชื่อและเบอร์ของตัวเอง</summary>
    [HttpGet]
    [Authorize(Roles = "Customer")]
    public async Task<IActionResult> Profile(CancellationToken ct)
    {
        var user = await CurrentCustomerAsync(ct);
        return user == null ? Forbid() : View(new ProfileViewModel
        {
            FirstName = user.FirstName, LastName = user.LastName, Phone = user.Phone,
            Email = user.Email, BirthDate = user.BirthDate
        });
    }

    [HttpPost]
    [Authorize(Roles = "Customer")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Profile([Bind("FirstName,LastName,Phone")] ProfileViewModel form, CancellationToken ct)
    {
        var user = await CurrentCustomerAsync(ct);
        if (user == null) return Forbid();
        form.Email = user.Email;
        form.BirthDate = user.BirthDate;

        var phone = form.Phone?.Trim();
        if (phone != user.Phone && ModelState[nameof(form.Phone)]?.Errors.Count == 0)
        {
            phone = ProfileViewModel.NormalizePhone(phone);
            if (phone == null)
                ModelState.AddModelError(nameof(form.Phone), "กรุณากรอกเบอร์มือถือ 10 หลัก ขึ้นต้นด้วย 06, 08 หรือ 09");
            // เบอร์ใช้จับคู่ลูกค้า Walk-in ถ้าใช้เบอร์คนอื่นได้ การเข้าพักอาจไปอยู่ผิดบัญชี
            else if (await db.Users.AnyAsync(u => u.Phone == phone && u.UserId != user.UserId && u.Role == "Customer", ct))
                ModelState.AddModelError(nameof(form.Phone), "เบอร์นี้ใช้ไม่ได้ กรุณาติดต่อพนักงานต้อนรับ");
        }
        if (!ModelState.IsValid) return View(form);

        user.FirstName = form.FirstName!.Trim();
        user.LastName = form.LastName!.Trim();
        user.Phone = phone;
        await db.SaveChangesAsync(ct);
        TempData["ProfileSuccess"] = "บันทึกข้อมูลแล้ว";
        return RedirectToAction(nameof(Profile));
    }

    private async Task<User?> CurrentCustomerAsync(CancellationToken ct) =>
        int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? await db.Users.SingleOrDefaultAsync(u => u.UserId == id && u.IsActive && u.Role == "Customer", ct)
            : null;

    [HttpGet]
    public IActionResult Password() => View(new ChangePasswordViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Password(ChangePasswordViewModel form, CancellationToken ct)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Forbid();
        if (!ModelState.IsValid) return View(form);

        var user = await db.Users.SingleOrDefaultAsync(u => u.UserId == userId && u.IsActive, ct);
        if (user == null || string.IsNullOrEmpty(user.Password)) return Forbid();

        // ตอนล็อกอินรหัสเก่าถูกเปลี่ยนเป็น hash แล้ว จึงเทียบแบบ hash อย่างเดียว
        PasswordVerificationResult result;
        try { result = hasher.VerifyHashedPassword(user, user.Password, form.CurrentPassword!); }
        catch (FormatException) { result = PasswordVerificationResult.Failed; }
        if (result == PasswordVerificationResult.Failed)
        {
            ModelState.AddModelError(nameof(form.CurrentPassword), "รหัสผ่านปัจจุบันไม่ถูกต้อง");
            return View(form);
        }
        if (form.NewPassword == form.CurrentPassword)
        {
            ModelState.AddModelError(nameof(form.NewPassword), "รหัสผ่านใหม่ต้องไม่ซ้ำกับรหัสเดิม");
            return View(form);
        }

        user.Password = hasher.HashPassword(user, form.NewPassword!);
        await db.SaveChangesAsync(ct);
        TempData["PasswordSuccess"] = "เปลี่ยนรหัสผ่านแล้ว ครั้งหน้าให้เข้าสู่ระบบด้วยรหัสใหม่";
        return RedirectToAction(nameof(Password));
    }
}
