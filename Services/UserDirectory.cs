using System.Security.Claims;
using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Services;

/// <summary>
/// แปลงรหัสผู้ใช้เป็นชื่อสำหรับแสดงบนหน้าเว็บ (เช่น "ทำโดย: สมชาย ใจดี (พนักงานต้อนรับ)" แทน "ผู้ใช้ #11")
/// จำชื่อที่เคยค้นแล้วไว้ในคำขอเดียวกัน จะได้ไม่ query ซ้ำ
/// </summary>
public sealed class UserDirectory(GoodHotelDbContext db)
{
    private readonly Dictionary<int, string> _cache = new();

    // จำชื่อที่อ่านมาแล้ว ไม่ต้องถามซ้ำ
    public void Remember(int userId, string? firstName, string? lastName, string? role) =>
        _cache[userId] = Label(firstName, lastName, role);

    /// <summary>ชื่อ + ตำแหน่ง เช่น "สมศรี รักสะอาด (แม่บ้าน)"</summary>
    public static string Label(string? firstName, string? lastName, string? role)
    {
        var name = $"{firstName} {lastName}".Trim();
        if (string.IsNullOrWhiteSpace(role)) return name;
        return $"{name} ({ThaiText.Status(role)})".Trim();
    }

    /// <summary>ชื่อของคนที่ล็อกอินอยู่ (ไม่มีก็ใช้อีเมล)</summary>
    public static string DisplayName(ClaimsPrincipal user)
    {
        var name = user.FindFirst(ClaimTypes.GivenName)?.Value;
        return string.IsNullOrWhiteSpace(name) ? user.Identity?.Name ?? "" : name;
    }

    /// <summary>ตัวอักษรแรกของชื่อ ข้ามสระหน้าของไทย (เ แ โ ใ ไ)</summary>
    public static string Initial(string name) =>
        name.TrimStart('เ', 'แ', 'โ', 'ใ', 'ไ', ' ') is { Length: > 0 } rest ? rest[..1].ToUpperInvariant() : "?";

    // ชื่อพร้อมตำแหน่งของผู้ใช้
    public async Task<string> NameAsync(int? userId)
    {
        if (userId is not int id) return "-";
        if (_cache.TryGetValue(id, out var cached)) return cached;

        var user = await db.Users.AsNoTracking()
            .Where(u => u.UserId == id)
            .Select(u => new { u.FirstName, u.LastName, u.Role })
            .FirstOrDefaultAsync();
        var name = user == null ? $"ผู้ใช้ #{id}" : Label(user.FirstName, user.LastName, user.Role);
        _cache[id] = name;
        return name;
    }
}
