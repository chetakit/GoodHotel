
using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using GoodHotel.Services;
using System.Globalization;
using System.Security.Claims;

// ใช้ปี ค.ศ. ทั้งแอป: ถ้าเครื่องตั้งภาษาไทย .NET จะแสดง/อ่านวันที่เป็นปี พ.ศ. (เช่น 2569)
// ทำให้วันที่ในหน้าเว็บผิด และ <input type="date"> ส่งค่าผิด
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddScoped<BookingService>();
builder.Services.AddScoped<RoomAvailabilityService>();
builder.Services.AddScoped<PaymentService>();
builder.Services.AddScoped<PaymentEvidenceStorage>();
builder.Services.AddScoped<HousekeepingService>();
builder.Services.AddScoped<StayService>();
builder.Services.AddScoped<BookingCancellationService>();
builder.Services.AddScoped<RoomStatusService>();
builder.Services.AddScoped<WalkInService>();
builder.Services.AddScoped<RoomMoveService>();
builder.Services.AddScoped<UserDirectory>();
builder.Services.AddScoped<PromotionService>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<UserAccessCache>();
builder.Services.AddSingleton<CustomerNotices>();
builder.Services.AddSingleton(TimeProvider.System);


// Database Connection
var connectionString = builder.Configuration
    .GetConnectionString("DefaultConnection");

var serverVersion = new MySqlServerVersion(
    new Version(8, 0, 0)
);

builder.Services.AddDbContext<GoodHotelDbContext>(
    options => options.UseMySql(
        connectionString,
        serverVersion
    )
);
// factory สำหรับ ParallelReader (สร้าง DbContext แยกเพื่อยิง query พร้อมกัน)
builder.Services.AddDbContextFactory<GoodHotelDbContext>(
    options => options.UseMySql(connectionString, serverVersion),
    ServiceLifetime.Scoped);
builder.Services.AddScoped<ParallelReader>();
builder.Services.AddScoped<FinanceLedger>();


// Password Hashing
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();

// Authentication
builder.Services.AddAuthentication(
    CookieAuthenticationDefaults.AuthenticationScheme
)
.AddCookie(options =>
{
    options.LoginPath = "/Home/Login";
    options.AccessDeniedPath = "/Home/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Events.OnValidatePrincipal = async context =>
    {
        var idValue = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var role = context.Principal?.FindFirstValue(ClaimTypes.Role);
        if (!int.TryParse(idValue, out var userId) || userId <= 0 || string.IsNullOrEmpty(role))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        var services = context.HttpContext.RequestServices;
        var currentUser = await services.GetRequiredService<UserAccessCache>().GetAsync(
            services.GetRequiredService<GoodHotelDbContext>(), userId, context.HttpContext.RequestAborted);
        if (currentUser == null || !currentUser.IsActive ||
            !string.Equals(currentUser.Role, role, StringComparison.Ordinal))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    };
});

builder.Services.AddAuthorization();

// กำหนดเวลาชำระเงินหลังจอง (ค่าเริ่มต้น 15 นาที) และตัวยกเลิกการจองที่หมดเวลาทุก 30 วินาที
PaymentDeadline.Configure(builder.Configuration);
builder.Services.AddHostedService<PaymentDeadlineSweeper>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

// เปิด connection ฐานข้อมูลค้างไว้ตลอด 1 request
// ปกติ EF เปิด/ปิด connection ทุก query และทุกครั้งที่หยิบจาก pool ต้อง reset
// ทำให้แต่ละ query ใช้ ~370ms (ไปกลับ Railway 2 รอบ) เปิดค้างไว้เหลือ ~200ms ต่อ query
// (ลองใช้ interceptor กันไม่ให้ EF ปิดแล้ว ช้ากว่าเดิม จึงใช้วิธีนี้)
app.Use(async (context, next) =>
{
    var db = context.RequestServices.GetRequiredService<GoodHotelDbContext>();
    await db.Database.OpenConnectionAsync(context.RequestAborted);
    try { await next(); }
    finally { await db.Database.CloseConnectionAsync(); }
});

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
