
// จุดเริ่มต้นของโปรแกรม: รันไฟล์นี้ก่อนเสมอ แบ่งเป็น 2 ส่วน
// ส่วนที่ 1 (ก่อน builder.Build) ลงทะเบียนสิ่งที่ระบบต้องใช้ ส่วนที่ 2 (หลัง Build) กำหนดลำดับที่คำขอแต่ละครั้งต้องผ่าน
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

// สร้างตัวตั้งค่าเว็บ (อ่าน appsettings.json ให้ด้วย)
var builder = WebApplication.CreateBuilder(args);

// เปิดใช้ MVC: Controller + View และแปลข้อความเตือนของฟอร์มเป็นไทย
builder.Services.AddControllersWithViews(ThaiValidation.Configure);
// ลงทะเบียน Service (Dependency Injection) Controller ขอใช้ได้ผ่าน constructor โดยไม่ต้อง new เอง
// AddScoped = สร้างใหม่ทุกคำขอ, AddSingleton = มีตัวเดียวทั้งโปรแกรม
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
// ให้ class อื่นรู้ว่าคำขอปัจจุบันคืออะไร
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<DatabaseConnections>();
// เชื่อมฐานข้อมูล MySQL: อ่าน ConnectionStrings:DefaultConnection จาก appsettings.json
void ConfigureDatabase(IServiceProvider services, DbContextOptionsBuilder options) => options.UseMySql(
    services.GetRequiredService<DatabaseConnections>()
        .For(services.GetRequiredService<IHttpContextAccessor>().HttpContext),
    DatabaseConnections.ServerVersion);
// GoodHotelDbContext คือตัวกลางระหว่างโค้ด C# กับ MySQL
builder.Services.AddDbContext<GoodHotelDbContext>(ConfigureDatabase);
// งานเบื้องหลัง: เปิดสายฐานข้อมูลค้างไว้ให้หน้าแรกเร็ว
builder.Services.AddHostedService<ReadConnectionWarmer>();
builder.Services.AddScoped<ParallelReader>();
builder.Services.AddScoped<FinanceLedger>();


// Password Hashing
// ตัวแปลงรหัสผ่านเป็น hash (ย้อนกลับเป็นรหัสเดิมไม่ได้)
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();

// Authentication
// ล็อกอินด้วย cookie: ล็อกอินผ่านแล้วเบราว์เซอร์ส่ง cookie มายืนยันตัวทุกครั้ง
builder.Services.AddAuthentication(
    CookieAuthenticationDefaults.AuthenticationScheme
)
.AddCookie(options =>
{
    // ยังไม่ล็อกอิน → ส่งไปหน้า Login, ล็อกอินแล้วแต่ role ไม่ตรง → หน้า AccessDenied
    options.LoginPath = "/Home/Login";
    options.AccessDeniedPath = "/Home/AccessDenied";
    // อยู่ได้ 8 ชั่วโมง และต่ออายุให้ถ้ายังใช้งานอยู่
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    // ตรวจ cookie ทุกคำขอ: ถ้าบัญชีถูกปิดหรือถูกเปลี่ยน role ให้ออกจากระบบทันที
    options.Events.OnValidatePrincipal = async context =>
    {
        // อ่านรหัสผู้ใช้และ role ที่เก็บไว้ใน cookie
        var idValue = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var role = context.Principal?.FindFirstValue(ClaimTypes.Role);
        if (!int.TryParse(idValue, out var userId) || userId <= 0 || string.IsNullOrEmpty(role))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        var services = context.HttpContext.RequestServices;
        // เทียบกับข้อมูลล่าสุดในฐานข้อมูล (จำไว้ 1 นาที จะได้ไม่ถามทุกหน้า)
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

// เปิดใช้ [Authorize(Roles = "...")] ที่อยู่บน Controller
builder.Services.AddAuthorization();

// กำหนดเวลาชำระเงินหลังจอง (ค่าเริ่มต้น 15 นาที) และตัวยกเลิกการจองที่หมดเวลาทุก 30 วินาที
PaymentDeadline.Configure(builder.Configuration);
builder.Services.AddHostedService<PaymentDeadlineSweeper>();

// จบส่วนลงทะเบียน ต่อจากนี้คือ middleware: ทุกคำขอผ่านตามลำดับบนลงล่าง
var app = builder.Build();

// ตั้งข้อมูลตัวอย่างสำหรับสาธิต แล้วจบการทำงาน (ไม่เปิดเว็บ)
if (args.Contains("seed-demo"))
{
    Environment.ExitCode = await DemoData.RunAsync(app.Services, args);
    return;
}

// ตอนใช้งานจริง: เกิดข้อผิดพลาดให้แสดงหน้า Error แทนรายละเอียดโค้ด
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// ลิงก์ผิด/ข้อมูลไม่มีอยู่: แสดงหน้าบอกผู้ใช้แทนหน้าขาวเปล่า (คำตอบที่มีเนื้อหาอยู่แล้ว เช่น JSON ไม่เปลี่ยน)
app.UseStatusCodePagesWithReExecute("/Home/StatusPage", "?code={0}");

// เปลี่ยน http เป็น https (เมื่อเครื่องตั้ง https ไว้)
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

// หาว่า URL นี้ต้องไป Controller/Action ไหน
app.UseRouting();

// เช็คว่า "คุณเป็นใคร" (อ่าน cookie) แล้วบรรทัดถัดไปเช็คว่า "มีสิทธิ์ไหม" ต้องเรียงตามนี้
app.UseAuthentication();
app.UseAuthorization();

// รูปแบบ URL: /Controller/Action/id เช่น /Home/RoomDetails/5 → HomeController.RoomDetails(id = 5)
// ไม่ใส่อะไรเลย → HomeController.Index
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// เริ่มเปิดเว็บ รอรับคำขอ
app.Run();
