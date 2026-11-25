using System.Globalization;
using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GoodHotel.Services;

/// <summary>
/// แจ้งลูกค้าด้วยตัวเลขที่เมนู "การจองของฉัน" เมื่อฝ่ายบัญชีพิจารณาคำขอคืนเงินแล้ว หรือพนักงานย้ายห้องให้
/// - "เห็นแล้ว" = เวลาที่เปิดหน้าการจองของฉันครั้งล่าสุด เก็บใน cookie (ไม่ต้องเพิ่มคอลัมน์ในฐานข้อมูล)
/// - จำตัวเลขไว้ 1 นาทีต่อผู้ใช้ ไม่ให้ทุกหน้าของลูกค้าต้องถามฐานข้อมูลเพิ่ม (~200ms ต่อ query บน Railway)
/// </summary>
public sealed record NoticeCount(int Refunds, int Moves)
{
    public int Total => Refunds + Moves;
}

// แจ้งเตือนผลคำขอคืนเงินให้ลูกค้า
public sealed class CustomerNotices(IMemoryCache cache)
{
    // แยก cookie ตามผู้ใช้: เบราว์เซอร์เดียวกันอาจมีลูกค้าหลายคนสลับกันล็อกอิน
    private static string SeenCookie(int userId) => $"gh_refund_seen_{userId}";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LookBack = TimeSpan.FromDays(14);

    // ชื่อที่ใช้เก็บใน cache ของแต่ละคน
    private static string Key(int userId) => $"customer-notices:{userId}";

    // เวลาล่าสุดที่ลูกค้าเปิดดู (เก็บใน cookie)
    private static DateTime SeenAtUtc(HttpContext context, int userId)
    {
        var floor = DateTime.UtcNow - LookBack;
        return context.Request.Cookies.TryGetValue(SeenCookie(userId), out var raw) &&
               long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) &&
               ticks > floor.Ticks && ticks <= DateTime.UtcNow.Ticks
            ? new DateTime(ticks, DateTimeKind.Utc)
            : floor;
    }

    /// <summary>จำนวนเรื่องที่ลูกค้ายังไม่ได้เปิดดู</summary>
    /// <param name="fresh">true = ไม่ใช้ค่าที่จำไว้ (หน้าที่กำลังจะนับว่า "เห็นแล้ว" ต้องใช้ค่าล่าสุด ไม่งั้นเรื่องที่เพิ่งเกิดจะหายไปโดยไม่เคยแจ้ง)</param>
    public async Task<NoticeCount> UnseenAsync(HttpContext context, GoodHotelDbContext db, int userId,
        CancellationToken ct, bool fresh = false)
    {
        if (!fresh && cache.TryGetValue(Key(userId), out NoticeCount? known) && known != null) return known;
        var seen = SeenAtUtc(context, userId);
        var types = await db.PaymentEvents.AsNoTracking()
            // เขียน == ตรงๆ: array.Contains ใน .NET 10 ผูกกับ Span ทำให้ EF แปลงเป็น SQL ไม่ได้
            .Where(e => (e.EventType == "RefundApproved" || e.EventType == "RefundRejected" || e.EventType == "RoomMoved") &&
                        e.OccurredAtUtc > seen && e.Payment.Booking != null && e.Payment.Booking.UserId == userId)
            .Select(e => e.EventType).ToListAsync(ct);
        var moves = types.Count(t => t == "RoomMoved");
        var count = new NoticeCount(types.Count - moves, moves);
        cache.Set(Key(userId), count, Lifetime);
        return count;
    }

    /// <summary>เปิดหน้าการจองของฉันแล้ว = เห็นแล้ว</summary>
    public void MarkSeen(HttpContext context, int userId)
    {
        context.Response.Cookies.Append(SeenCookie(userId), DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture),
            new CookieOptions { HttpOnly = true, IsEssential = true, SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps, Expires = DateTimeOffset.UtcNow.Add(LookBack) });
        cache.Set(Key(userId), new NoticeCount(0, 0), Lifetime);
    }
}
