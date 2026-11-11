
using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using GoodHotel.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddScoped<BookingService>();
builder.Services.AddSingleton<PromotionMetadataService>();
builder.Services.AddScoped<PromotionManagementService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<PromotionValidationService>();

// Database Connection
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

builder.Services.AddDbContext<GoodHotelDbContext>(
    options => options.UseMySql(
        connectionString,
        ServerVersion.AutoDetect(connectionString)
    )
);

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
});

builder.Services.AddAuthorization();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
