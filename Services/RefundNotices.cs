using System.Globalization;
using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GoodHotel.Services;

/// <summary>
/// แจ้งลูกค้าว่าฝ่ายบัญชีพิจารณาคำขอคืนเงินแล้ว (อนุมัติ/ปฏิเสธ) ด้วยตัวเลขที่เมนู "การจองของฉัน"
/// - "เห็นแล้ว" = เวลาที่เปิดหน้าการจองของฉันครั้งล่าสุด เก็บใน cookie (ไม่ต้องเพิ่มคอลัมน์ในฐานข้อมูล)
/// - จำตัวเลขไว้ 1 นาทีต่อผู้ใช้ ไม่ให้ทุกหน้าของลูกค้าต้องถามฐานข้อมูลเพิ่ม (~200ms ต่อ query บน Railway)
/// </summary>
public sealed class RefundNotices(IMemoryCache cache)
{
    // แยก cookie ตามผู้ใช้: เบราว์เซอร์เดียวกันอาจมีลูกค้าหลายคนสลับกันล็อกอิน
    private static string SeenCookie(int userId) => $"gh_refund_seen_{userId}";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LookBack = TimeSpan.FromDays(14);

    private static string Key(int userId) => $"refund-notices:{userId}";

    private static DateTime SeenAtUtc(HttpContext context, int userId)
    {
        var floor = DateTime.UtcNow - LookBack;
        return context.Request.Cookies.TryGetValue(SeenCookie(userId), out var raw) &&
               long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) &&
               ticks > floor.Ticks && ticks <= DateTime.UtcNow.Ticks
            ? new DateTime(ticks, DateTimeKind.Utc)
            : floor;
    }

    /// <summary>จำนวนผลการพิจารณาคำขอคืนเงินที่ลูกค้ายังไม่ได้เปิดดู</summary>
    public async Task<int> UnseenAsync(HttpContext context, GoodHotelDbContext db, int userId, CancellationToken ct)
    {
        if (cache.TryGetValue(Key(userId), out int count)) return count;
        var seen = SeenAtUtc(context, userId);
        count = await db.PaymentEvents.AsNoTracking()
            // เขียน == ตรงๆ: array.Contains ใน .NET 10 ผูกกับ Span ทำให้ EF แปลงเป็น SQL ไม่ได้
            .CountAsync(e => (e.EventType == "RefundApproved" || e.EventType == "RefundRejected") && e.OccurredAtUtc > seen &&
                             e.Payment.Booking != null && e.Payment.Booking.UserId == userId, ct);
        cache.Set(Key(userId), count, Lifetime);
        return count;
    }

    /// <summary>เปิดหน้าการจองของฉันแล้ว = เห็นแล้ว</summary>
    public void MarkSeen(HttpContext context, int userId)
    {
        context.Response.Cookies.Append(SeenCookie(userId), DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture),
            new CookieOptions { HttpOnly = true, IsEssential = true, SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps, Expires = DateTimeOffset.UtcNow.Add(LookBack) });
        cache.Set(Key(userId), 0, Lifetime);
    }
}
