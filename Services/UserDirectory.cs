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

    public void Remember(int userId, string? firstName, string? lastName, string? role) =>
        _cache[userId] = Label(firstName, lastName, role);

    /// <summary>ชื่อ + ตำแหน่ง เช่น "สมศรี รักสะอาด (แม่บ้าน)"</summary>
    public static string Label(string? firstName, string? lastName, string? role)
    {
        var name = $"{firstName} {lastName}".Trim();
        if (string.IsNullOrWhiteSpace(role)) return name;
        return $"{name} ({ThaiText.Status(role)})".Trim();
    }

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

    /// <summary>ดึงชื่อหลายคนใน query เดียวล่วงหน้า (หน้าที่มีประวัติหลายแถว จะได้ไม่ query ทีละคน)</summary>
    public async Task PreloadAsync(IEnumerable<int?> userIds, CancellationToken ct = default)
    {
        var missing = userIds.OfType<int>().Distinct().Where(id => !_cache.ContainsKey(id)).ToList();
        if (missing.Count == 0) return;

        var users = await db.Users.AsNoTracking()
            .Where(u => missing.Contains(u.UserId))
            .Select(u => new { u.UserId, u.FirstName, u.LastName, u.Role })
            .ToListAsync(ct);
        foreach (var id in missing)
        {
            var user = users.FirstOrDefault(u => u.UserId == id);
            _cache[id] = user == null ? $"ผู้ใช้ #{id}" : Label(user.FirstName, user.LastName, user.Role);
        }
    }
}
