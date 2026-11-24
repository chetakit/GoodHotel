using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Services;

/// <summary>
/// ยิง query อ่านอย่างเดียวหลายอันพร้อมกัน (แต่ละอันใช้ DbContext ของตัวเอง)
/// ฐานข้อมูลอยู่บน Railway ไป-กลับรอบละ ~200ms ถ้ายิงทีละอัน 5 query = 1 วินาที ยิงพร้อมกันเหลือ ~200ms
/// ใช้ใน transaction ได้เฉพาะหลังล็อกแถวที่เกี่ยวข้องแล้ว และ transaction นั้นยังไม่ได้เขียนอะไร
/// (อ่านจากคนละ connection จะเห็นเฉพาะข้อมูลที่ commit แล้ว ซึ่งล็อกกันไม่ให้ใครเปลี่ยนระหว่างนั้น)
/// </summary>
public sealed class ParallelReader(DatabaseConnections connections)
{
    public async Task<T> Run<T>(Func<GoodHotelDbContext, Task<T>> query)
    {
        await using var context = new GoodHotelDbContext(connections.ReadOptions);
        return await query(context);
    }
}
