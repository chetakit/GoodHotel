using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GoodHotel.Services;

/// <summary>
/// จำ role ชื่อ และสถานะเปิด/ปิดบัญชีของผู้ใช้ไว้ชั่วคราว
/// เดิมทุกหน้าต้องถามฐานข้อมูลก่อน 1 รอบ (~200ms เพราะฐานข้อมูลอยู่บน Railway)
/// ถ้าแก้ชื่อ/role/ปิดบัญชี/ลบผู้ใช้ ต้องเรียก Forget ให้มีผลทันที
/// </summary>
public sealed class UserAccessCache(IMemoryCache cache)
{
    // สิทธิ์และชื่อของผู้ใช้
    public sealed record Access(string? Role, bool IsActive, string? FirstName, string? LastName);

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    // ชื่อที่ใช้เก็บใน cache
    private static string Key(int userId) => $"user-access:{userId}";

    // อ่านจาก cache ไม่มีค่อยถามฐานข้อมูล
    public async Task<Access?> GetAsync(GoodHotelDbContext db, int userId, CancellationToken ct)
    {
        if (cache.TryGetValue(Key(userId), out Access? access)) return access;

        access = await db.Users.AsNoTracking().Where(u => u.UserId == userId)
            .Select(u => new Access(u.Role, u.IsActive, u.FirstName, u.LastName)).SingleOrDefaultAsync(ct);
        if (access != null) cache.Set(Key(userId), access, Lifetime);
        return access;
    }

    // ลบออกจาก cache เมื่อข้อมูลเปลี่ยน
    public void Forget(int userId) => cache.Remove(Key(userId));
}
