using System.Security.Claims;
using GoodHotel.Models;
using GoodHotel.Models.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Controllers;

/// <summary>เปลี่ยนรหัสผ่านของตัวเอง ใช้ได้ทุกบทบาท</summary>
[Authorize]
public class AccountController(GoodHotelDbContext db, IPasswordHasher<User> hasher) : Controller
{
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
