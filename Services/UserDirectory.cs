using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Services;

/// <summary>
/// แปลงรหัสผู้ใช้เป็นชื่อสำหรับแสดงบนหน้าเว็บ (เช่น "ทำโดย: สมชาย ใจดี" แทน "ผู้ใช้ #11")
/// จำชื่อที่เคยค้นแล้วไว้ในคำขอเดียวกัน จะได้ไม่ query ซ้ำ
/// </summary>
public sealed class UserDirectory(GoodHotelDbContext db)
{
    private readonly Dictionary<int, string> _cache = new();

    public async Task<string> NameAsync(int? userId)
    {
        if (userId is not int id) return "-";
        if (_cache.TryGetValue(id, out var cached)) return cached;

        var user = await db.Users.AsNoTracking()
            .Where(u => u.UserId == id)
            .Select(u => new { u.FirstName, u.LastName })
            .FirstOrDefaultAsync();
        var name = user == null ? $"ผู้ใช้ #{id}" : $"{user.FirstName} {user.LastName}".Trim();
        _cache[id] = name;
        return name;
    }
}
