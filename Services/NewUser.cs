using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace GoodHotel.Services;

/// <summary>เพิ่มผู้ใช้ใหม่ ออกรหัสสมาชิกให้ และลองใหม่ถ้ามีคนอื่นได้เลขเดียวกันพร้อมกัน</summary>
public static class NewUser
{
    private const int Attempts = 12;

    /// <returns>false = อีเมลนี้มีบัญชีอยู่แล้ว</returns>
    public static async Task<bool> AddAsync(GoodHotelDbContext db, User user, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            // ลำดับสำคัญ: อ่านเลขล่าสุดก่อน แล้วค่อยเช็คอีเมล
            // ถ้ามีคนเพิ่มอีเมลนี้หลังอ่านเลข เลขจะชนและวนกลับมาเช็คใหม่ จึงไม่มีอีเมลซ้ำ
            var lastId = await db.Users.MaxAsync(u => (int?)u.UserId, ct) ?? 0;
            if (user.Email != null && await db.Users.AnyAsync(u => u.Email == user.Email, ct)) return false;

            user.UserId = lastId + 1;
            db.Users.Add(user);
            try
            {
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateException exception) when (attempt < Attempts &&
                exception.InnerException is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry })
            {
                db.Entry(user).State = EntityState.Detached;
                await Task.Delay(Random.Shared.Next(20, 150), ct);
            }
        }
    }
}
