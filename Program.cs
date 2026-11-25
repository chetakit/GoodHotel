
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

builder.Services.AddControllersWithViews(ThaiValidation.Configure);
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
// เลือก pool ตามคำขอ (ดูเหตุผลใน DatabaseConnections)
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<DatabaseConnections>();
void ConfigureDatabase(IServiceProvider services, DbContextOptionsBuilder options) => options.UseMySql(
    services.GetRequiredService<DatabaseConnections>()
        .For(services.GetRequiredService<IHttpContextAccessor>().HttpContext),
    DatabaseConnections.ServerVersion);
builder.Services.AddDbContext<GoodHotelDbContext>(ConfigureDatabase);
builder.Services.AddHostedService<ReadConnectionWarmer>();
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
            return;
        }

        // ชื่อที่แสดงบนเมนู ใช้ค่าปัจจุบันจากฐานข้อมูล (แก้ชื่อแล้วเปลี่ยนทันที ไม่ต้องล็อกอินใหม่)
        if (context.Principal!.Identity is ClaimsIdentity identity)
        {
            foreach (var old in identity.FindAll(ClaimTypes.GivenName).ToList()) identity.RemoveClaim(old);
            identity.AddClaim(new Claim(ClaimTypes.GivenName, $"{currentUser.FirstName} {currentUser.LastName}".Trim()));
        }
    };
});

builder.Services.AddAuthorization();

// กำหนดเวลาชำระเงินหลังจอง (ค่าเริ่มต้น 15 นาที) และตัวยกเลิกการจองที่หมดเวลาทุก 30 วินาที
PaymentDeadline.Configure(builder.Configuration);
builder.Services.AddHostedService<PaymentDeadlineSweeper>();

var app = builder.Build();

// ตั้งข้อมูลตัวอย่างสำหรับสาธิต แล้วจบการทำงาน (ไม่เปิดเว็บ)
if (args.Contains("seed-demo"))
{
    Environment.ExitCode = await DemoData.RunAsync(app.Services, args);
    return;
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// ลิงก์ผิด/ข้อมูลไม่มีอยู่: แสดงหน้าบอกผู้ใช้แทนหน้าขาวเปล่า (คำตอบที่มีเนื้อหาอยู่แล้ว เช่น JSON ไม่เปลี่ยน)
app.UseStatusCodePagesWithReExecute("/Home/StatusPage", "?code={0}");

app.UseHttpsRedirection();
// รูปห้องตั้งชื่อไฟล์ใหม่ทุกครั้งที่อัปโหลด และ css/js มีเลขรุ่น (?v=) ต่อท้าย เบราว์เซอร์จึงเก็บไว้ใช้ซ้ำได้
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = file =>
    {
        var request = file.Context.Request;
        if (request.Query.ContainsKey("v") || request.Path.StartsWithSegments("/uploads"))
            file.Context.Response.Headers.CacheControl = "public,max-age=2592000";
        else if (request.Path.StartsWithSegments("/images") || request.Path.StartsWithSegments("/lib"))
            file.Context.Response.Headers.CacheControl = "public,max-age=86400";
    }
});

// เปิด connection ฐานข้อมูลค้างไว้ตลอด 1 request
// ปกติ EF เปิด/ปิด connection ทุก query และทุกครั้งที่หยิบจาก pool ต้อง reset
// ทำให้แต่ละ query ใช้ ~370ms (ไปกลับ Railway 2 รอบ) เปิดค้างไว้เหลือ ~200ms ต่อ query
// (ลองใช้ interceptor กันไม่ให้ EF ปิดแล้ว ช้ากว่าเดิม จึงใช้วิธีนี้)
app.Use(async (context, next) =>
{
    context.RequestServices.GetRequiredService<DatabaseConnections>().DropIfStale();
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
