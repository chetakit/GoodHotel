using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Services;

/// <summary>
/// กำหนดเวลาชำระเงินหลังจอง: จองออนไลน์แล้วต้องชำระออนไลน์ภายในเวลาที่กำหนด
/// ไม่งั้นระบบยกเลิกการจองให้เองแล้วปล่อยห้องคืน กันคนจองกันห้องไว้โดยไม่จ่าย
///
/// ไม่ถูกยกเลิกเมื่อ:
///   - ชำระแล้ว (ยืนยันแล้ว) หรือส่งการชำระที่รอยืนยันอยู่ (รายการเงินสดเก่าจากก่อนเปลี่ยนเป็นชำระออนไลน์อย่างเดียว)
///   - เช็คอินแล้ว
///   - การจองก่อนมีระบบนี้ (ไม่มีเวลาที่จอง)
///
/// ตั้งเวลาได้ใน appsettings.json: "Booking": { "PaymentWindowMinutes": 15 } (ไม่ใส่ = 15 นาที)
/// </summary>
public static class PaymentDeadline
{
    public const int DefaultMinutes = 15;

    public static TimeSpan Window { get; private set; } = TimeSpan.FromMinutes(DefaultMinutes);

    public static void Configure(IConfiguration configuration)
    {
        var minutes = configuration.GetValue<int?>("Booking:PaymentWindowMinutes") ?? DefaultMinutes;
        Window = TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 24 * 60));
    }

    public static DateTime? DueAtUtc(DateTime? createdAtUtc) => createdAtUtc + Window;

    /// <summary>
    /// ยกเลิกการจองที่หมดเวลาชำระ คืนจำนวนที่ยกเลิก
    /// หารายการแบบไม่ล็อกก่อน แล้วล็อกทีละการจองและตรวจซ้ำ
    /// (เดิมใช้ UPDATE เดียวทั้งตาราง ล็อกกว้างจนชนกับคนที่กำลังจ่ายเงิน เกิด deadlock ฝั่งที่แพ้จ่ายไม่สำเร็จ)
    /// </summary>
    public static async Task<int> ExpireOverdueAsync(GoodHotelDbContext db, DateTime nowUtc, CancellationToken ct)
    {
        var overdue = await Overdue(db, nowUtc).Select(b => b.BookingId).ToListAsync(ct);
        var expired = 0;
        foreach (var id in overdue)
        {
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            // ถ้าลูกค้ากำลังจ่ายอยู่ (ล็อกการจองไว้) จะรอ แล้วเห็นการชำระนั้น จึงไม่ยกเลิกการจองที่เพิ่งจ่าย
            if (!await PaymentService.LockBookingAsync(db, id, ct)) continue;
            expired += await Overdue(db, nowUtc).Where(b => b.BookingId == id)
                .ExecuteUpdateAsync(b => b
                    .SetProperty(x => x.Status, BookingStatuses.Cancelled)
                    .SetProperty(x => x.CancelledAtUtc, nowUtc)
                    .SetProperty(x => x.CancelReason, CancelReasons.PaymentTimeout), ct);
            await tx.CommitAsync(ct);
        }
        return expired;
    }

    private static IQueryable<Booking> Overdue(GoodHotelDbContext db, DateTime nowUtc)
    {
        var cutoff = nowUtc - Window;
        return db.Bookings
            .Where(b => b.Status == BookingStatuses.Booked &&
                        b.CreatedAtUtc != null && b.CreatedAtUtc < cutoff &&
                        !db.BookingStays.Any(s => s.BookingId == b.BookingId) &&
                        !db.Payments.Any(p => p.BookingId == b.BookingId &&
                            (p.VerifiedAtUtc != null ||
                             (p.SubmittedAtUtc != null && (p.PaymentStatus == "Pending" || p.PaymentStatus == "Submitted")))));
    }
}

/// <summary>เช็คการจองที่หมดเวลาชำระทุก 30 วินาที (ทำงานเบื้องหลังตลอดที่เว็บเปิดอยู่)</summary>
public sealed class PaymentDeadlineSweeper(
    IServiceScopeFactory scopes, TimeProvider time, ILogger<PaymentDeadlineSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<GoodHotelDbContext>();
                var expired = await PaymentDeadline.ExpireOverdueAsync(db, time.GetUtcNow().UtcDateTime, stoppingToken);
                if (expired > 0)
                    logger.LogInformation("Cancelled {Count} unpaid booking(s) past the payment deadline.", expired);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // ฐานข้อมูลหลุดชั่วคราว ไม่ให้เว็บล่ม รอบหน้าลองใหม่
                logger.LogWarning(ex, "Payment deadline sweep failed; will retry.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
