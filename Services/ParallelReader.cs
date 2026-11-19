using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Services;

/// <summary>
/// ยิง query อ่านอย่างเดียวหลายอันพร้อมกัน (แต่ละอันใช้ DbContext ของตัวเอง)
/// ฐานข้อมูลอยู่บน Railway ไป-กลับรอบละ ~200ms ถ้ายิงทีละอัน 5 query = 1 วินาที ยิงพร้อมกันเหลือ ~200ms
/// ห้ามใช้กับงานที่ต้องอยู่ใน transaction เดียวกัน (เช่น ตอนล็อกห้องเพื่อจอง)
/// </summary>
public sealed class ParallelReader(IDbContextFactory<GoodHotelDbContext> factory)
{
    public async Task<T> Run<T>(Func<GoodHotelDbContext, Task<T>> query)
    {
        await using var context = await factory.CreateDbContextAsync();
        return await query(context);
    }
}
