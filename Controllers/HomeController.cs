
using System.Diagnostics;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GoodHotel.Models;
using GoodHotel.Models.ViewModels;
using GoodHotel.Services;

namespace GoodHotel.Controllers;

public class HomeController : Controller
{
    private readonly GoodHotelDbContext _db;
    private readonly ILogger<HomeController> _logger;
    private readonly IPasswordHasher<User> _passwordHasher;
    private readonly IWebHostEnvironment _environment;
    private readonly BookingService _bookingService;
    private readonly PromotionMetadataService _promotionMetadata;
    private readonly PromotionValidationService _promotionValidation;
    private readonly TimeProvider _timeProvider;

    public HomeController(
        GoodHotelDbContext db,
        ILogger<HomeController> logger,
        IPasswordHasher<User> passwordHasher,
        IWebHostEnvironment environment,
        BookingService bookingService,
        PromotionMetadataService promotionMetadata,
        PromotionValidationService promotionValidation,
        TimeProvider timeProvider)
    {
        _db = db;
        _logger = logger;
        _passwordHasher = passwordHasher;
        _environment = environment;
        _bookingService = bookingService;
        _promotionMetadata = promotionMetadata;
        _promotionValidation = promotionValidation;
        _timeProvider = timeProvider;
    }

    public async Task<IActionResult> Index()
    {
        var featuredRooms = await _db.Rooms
            .AsNoTracking()
            .Where(room => room.Status == "Available")
            .OrderBy(room => room.Room1)
            .Select(room => new RoomListViewModel
            {
                RoomId = room.RoomId,
                RoomNumber = room.Room1,
                Type = room.Type,
                Rate = room.Rate,
                Capacity = room.Capacity,
                Status = room.Status,
                ImagePath = room.ImagePath
            })
            .ToListAsync();

        foreach (var room in featuredRooms)
        {
            room.ImagePath = RoomImageStorage.GetDisplayPath(
                _environment.WebRootPath,
                room.ImagePath);
        }

        var promotionEntities = await _db.Promotions
            .AsNoTracking()
            .OrderBy(promotion => promotion.PromotionId)
            .ToListAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var promotions = promotionEntities
            .Select(promotion =>
            {
                var parsed = _promotionMetadata.Parse(promotion.Condition);
                var metadata = parsed.Metadata;
                return new PromotionListViewModel
                {
                    PromotionId = promotion.PromotionId,
                    PromotionName = promotion.PromotionName,
                    Description = promotion.Description,
                    Condition = parsed.IsLegacy ? promotion.Condition : null,
                    Benefit = promotion.Benefit,
                    PromotionCode = metadata?.Code,
                    DiscountType = metadata?.DiscountType,
                    DiscountValue = metadata?.DiscountValue,
                    StartDate = metadata?.StartDate,
                    EndDate = metadata?.EndDate,
                    IsActive = metadata?.IsActive,
                    IsLegacyPromotion = parsed.IsLegacy,
                    MetadataError = parsed.Error
                };
            })
            .Where(promotion =>
                promotion.IsLegacyPromotion ||
                (promotion.IsActive == true &&
                 promotion.StartDate <= today &&
                 promotion.EndDate >= today))
            .ToList();

        var model = new HomeViewModel
        {
            FeaturedRooms = featuredRooms,
            Promotions = promotions
        };

        return View(model);
    }

    public IActionResult UserList()
    {
        var userList = _db.Users.ToList();
        return View(userList);
    }

    [HttpGet]
    public async Task<IActionResult> Rooms(AvailabilitySearchViewModel search)
    {
        search.HasSearched = false;
        search.HasSearchCriteria =
            Request.Query.ContainsKey(nameof(search.CheckIn)) ||
            Request.Query.ContainsKey(nameof(search.CheckOut)) ||
            Request.Query.ContainsKey(nameof(search.Guests));

        var roomsQuery = _db.Rooms.AsNoTracking();

        if (search.HasSearchCriteria)
        {
            if (!search.CheckIn.HasValue)
            {
                ModelState.AddModelError(
                    nameof(search.CheckIn),
                    "กรุณาเลือกวันที่เข้าพัก");
            }
            else if (search.CheckIn.Value < DateOnly.FromDateTime(DateTime.Today))
            {
                ModelState.AddModelError(
                    nameof(search.CheckIn),
                    "วันที่เข้าพักต้องไม่เป็นวันที่ย้อนหลัง");
            }

            if (!search.CheckOut.HasValue)
            {
                ModelState.AddModelError(
                    nameof(search.CheckOut),
                    "กรุณาเลือกวันที่ออก");
            }
            else if (search.CheckIn.HasValue &&
                     search.CheckOut.Value <= search.CheckIn.Value)
            {
                ModelState.AddModelError(
                    nameof(search.CheckOut),
                    "วันที่ออกต้องอยู่หลังวันที่เข้าพัก");
            }

            if (!search.Guests.HasValue || search.Guests.Value <= 0)
            {
                ModelState.AddModelError(
                    nameof(search.Guests),
                    "จำนวนผู้เข้าพักต้องมากกว่า 0");
            }

            if (ModelState.IsValid)
            {
                var checkIn = search.CheckIn!.Value;
                var checkOut = search.CheckOut!.Value;
                var guests = search.Guests!.Value;

                roomsQuery = roomsQuery
                    .Where(room =>
                        room.Status == "Available" &&
                        room.Capacity.HasValue &&
                        room.Capacity.Value >= guests &&
                        !room.Bookings.Any(booking =>
                            booking.CheckIn.HasValue &&
                            booking.CheckOut.HasValue &&
                            booking.CheckIn.Value < checkOut &&
                            booking.CheckOut.Value > checkIn));

                search.HasSearched = true;
            }
        }

        search.Rooms = await roomsQuery
            .OrderBy(room => room.Room1)
            .Select(room => new RoomListViewModel
            {
                RoomId = room.RoomId,
                RoomNumber = room.Room1,
                Type = room.Type,
                Rate = room.Rate,
                Capacity = room.Capacity,
                Status = room.Status,
                ImagePath = room.ImagePath,
                Description = room.Description
            })
            .ToListAsync();

        foreach (var room in search.Rooms)
        {
            room.ImagePath = RoomImageStorage.GetDisplayPath(
                _environment.WebRootPath,
                room.ImagePath);
        }

        return View(search);
    }

    [HttpGet]
    public async Task<IActionResult> RoomAvailability(
        int? roomId,
        int? year,
        int? month,
        CancellationToken cancellationToken)
    {
        if (!roomId.HasValue || roomId.Value <= 0 ||
            !year.HasValue || !month.HasValue ||
            year.Value < 1 || year.Value > 9999 ||
            month.Value < 1 || month.Value > 12)
        {
            return BadRequest(new { message = "ข้อมูลห้องหรือเดือนไม่ถูกต้อง" });
        }

        var monthStart = new DateOnly(year.Value, month.Value, 1);
        var today = DateOnly.FromDateTime(DateTime.Today);

        DateOnly monthEnd;
        try
        {
            monthEnd = monthStart.AddMonths(1);
        }
        catch (ArgumentOutOfRangeException)
        {
            return BadRequest(new { message = "เดือนที่ระบุไม่ถูกต้อง" });
        }

        var room = await _db.Rooms
            .AsNoTracking()
            .Where(item => item.RoomId == roomId.Value)
            .Select(item => new
            {
                item.RoomId,
                RoomNumber = item.Room1,
                RoomType = item.Type,
                item.Rate,
                item.Capacity,
                item.Status
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (room == null)
        {
            return NotFound(new { message = "ไม่พบห้องพักที่เลือก" });
        }

        var bookingPeriods = await _db.Bookings
            .AsNoTracking()
            .Where(booking =>
                booking.RoomId == room.RoomId &&
                booking.CheckIn.HasValue &&
                booking.CheckOut.HasValue &&
                booking.CheckIn.Value < monthEnd &&
                booking.CheckOut.Value > monthStart)
            .Select(booking => new
            {
                CheckIn = booking.CheckIn!.Value,
                CheckOut = booking.CheckOut!.Value
            })
            .ToListAsync(cancellationToken);

        var days = new List<object>(DateTime.DaysInMonth(
            monthStart.Year,
            monthStart.Month));
        for (var date = monthStart; date < monthEnd; date = date.AddDays(1))
        {
            var occupied = !string.Equals(
                               room.Status,
                               "Available",
                               StringComparison.OrdinalIgnoreCase) ||
                           bookingPeriods.Any(period =>
                               period.CheckIn <= date &&
                               period.CheckOut > date);
            days.Add(new
            {
                Date = date.ToString(
                    "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture),
                IsOccupied = occupied,
                CanCheckIn = !occupied && date >= today
            });
        }

        return Json(new
        {
            room.RoomId,
            room.RoomNumber,
            room.RoomType,
            room.Rate,
            room.Capacity,
            room.Status,
            Today = today.ToString(
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture),
            BookingPeriods = bookingPeriods.Select(period => new
            {
                CheckIn = period.CheckIn.ToString(
                    "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture),
                CheckOut = period.CheckOut.ToString(
                    "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture)
            }),
            Days = days
        });
    }

    public IActionResult Promotion()
    {
        return View();
    }

    [HttpGet]
    [Authorize(Roles = "Customer")]
    public async Task<IActionResult> MyBookings(
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(
                User.FindFirstValue(ClaimTypes.NameIdentifier),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var userId) ||
            userId <= 0)
        {
            return Forbid();
        }

        var bookings = await (
            from booking in _db.Bookings.AsNoTracking()
            join room in _db.Rooms.AsNoTracking()
                on booking.RoomId equals (int?)room.RoomId
            where booking.UserId == userId
            orderby booking.CheckIn descending, booking.BookingId descending
            select new MyBookingViewModel
            {
                BookingId = booking.BookingId,
                ImagePath = room.ImagePath,
                RoomNumber = room.Room1,
                RoomType = room.Type,
                CheckIn = booking.CheckIn,
                CheckOut = booking.CheckOut,
                Guests = booking.Guests,
                Amount = booking.Amount
            })
            .ToListAsync(cancellationToken);

        foreach (var booking in bookings)
        {
            booking.ImagePath = RoomImageStorage.GetDisplayPath(
                _environment.WebRootPath,
                booking.ImagePath);

            if (booking.CheckIn.HasValue && booking.CheckOut.HasValue)
            {
                booking.NightCount =
                    booking.CheckOut.Value.DayNumber -
                    booking.CheckIn.Value.DayNumber;
            }
        }

        return View(bookings);
    }

    [HttpGet]
    public async Task<IActionResult> Booking(
        int? roomId,
        DateOnly? checkIn,
        DateOnly? checkOut,
        int? guests,
        CancellationToken cancellationToken)
    {
        LogBookingDateDiagnostics(
            "GET",
            Request.Query["CheckIn"].ToString(),
            Request.Query["CheckOut"].ToString(),
            checkIn,
            checkOut);

        var request = new CreateBookingViewModel
        {
            RoomId = roomId ?? 0,
            CheckIn = checkIn,
            CheckOut = checkOut,
            Guests = guests
        };
        var model = await _bookingService.PrepareConfirmationAsync(
            request,
            cancellationToken);
        return View(model);
    }

    [HttpPost]
    [Authorize(Roles = "Customer")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyPromotion(
        [FromForm] ApplyPromotionRequestViewModel request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid ||
            !TryParseBookingDate(request.CheckIn, out var checkIn) ||
            !TryParseBookingDate(request.CheckOut, out var checkOut))
        {
            return BadRequest(new ApplyPromotionResponseViewModel(
                false,
                "InvalidRequest",
                "ข้อมูลการเข้าพักไม่ถูกต้อง"));
        }

        var today = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
        if (checkIn < today || checkOut <= checkIn)
        {
            return BadRequest(new ApplyPromotionResponseViewModel(
                false,
                "InvalidRequest",
                "กรุณาตรวจสอบวันที่เข้าพักและวันที่ออก"));
        }

        var room = await _db.Rooms
            .AsNoTracking()
            .Where(item => item.RoomId == request.RoomId)
            .Select(item => new
            {
                item.RoomId,
                item.Status,
                item.Rate,
                item.Capacity
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (room == null ||
            room.Status != "Available" ||
            !room.Capacity.HasValue ||
            (long)request.Guests >
                (long)room.Capacity.Value + request.ExtraBedCount ||
            !room.Rate.HasValue ||
            room.Rate.Value <= 0)
        {
            return BadRequest(new ApplyPromotionResponseViewModel(
                false,
                "InvalidRequest",
                room == null
                    ? "ไม่พบห้องพักที่เลือก"
                    : room.Status != "Available"
                        ? "ห้องพักนี้ไม่พร้อมให้จอง"
                        : room.Capacity.HasValue &&
                      (long)request.Guests >
                          (long)room.Capacity.Value + request.ExtraBedCount
                        ? "จำนวนผู้เข้าพักเกินความจุของห้องรวมเตียงเสริม"
                        : "ไม่สามารถยืนยันราคาให้ห้องนี้ได้"));
        }

        var hasOverlap = await _db.Bookings
            .AsNoTracking()
            .AnyAsync(
                booking =>
                    booking.RoomId == room.RoomId &&
                    booking.CheckIn.HasValue &&
                    booking.CheckOut.HasValue &&
                    booking.CheckIn.Value < checkOut &&
                    booking.CheckOut.Value > checkIn,
                cancellationToken);

        if (hasOverlap)
        {
            return BadRequest(new ApplyPromotionResponseViewModel(
                false,
                "RoomUnavailable",
                "ห้องพักถูกจองแล้วในช่วงวันที่เลือก"));
        }

        var nightCount = checkOut.DayNumber - checkIn.DayNumber;
        decimal roomSubtotal;
        decimal extraBedAmount;
        try
        {
            roomSubtotal = checked(room.Rate.Value * nightCount);
            extraBedAmount = checked(500m * request.ExtraBedCount * nightCount);
        }
        catch (OverflowException)
        {
            return BadRequest(new ApplyPromotionResponseViewModel(
                false,
                "InvalidRequest",
                "ยอดรวมเกินขอบเขตที่ระบบรองรับ"));
        }

        if (roomSubtotal <= 0 || roomSubtotal > 99999999.99m)
        {
            return BadRequest(new ApplyPromotionResponseViewModel(
                false,
                "InvalidRequest",
                "ยอดรวมเกินขอบเขตที่ระบบรองรับ"));
        }

        var promotionCode = request.PromotionCode?.Trim();
        var discountAmount = 0m;
        var promotionStatus = "NoCode";
        var message = "ยังไม่ได้ใช้ Promotion Code";

        if (!string.IsNullOrEmpty(promotionCode))
        {
            var validation = await _promotionValidation.ValidateAsync(
                promotionCode,
                roomSubtotal,
                cancellationToken);
            promotionStatus = validation.Status.ToString();
            message = validation.Message;
            if (validation.Succeeded)
            {
                discountAmount = validation.DiscountAmount!.Value;
            }
        }
        decimal totalAmount;
        try
        {
            totalAmount = checked(roomSubtotal - discountAmount + extraBedAmount);
        }
        catch (OverflowException)
        {
            return BadRequest(new ApplyPromotionResponseViewModel(
                false,
                "InvalidRequest",
                "ยอดรวมเกินขอบเขตที่ระบบรองรับ"));
        }

        if (totalAmount > 99999999.99m)
        {
            return BadRequest(new ApplyPromotionResponseViewModel(
                false,
                "InvalidRequest",
                "ยอดรวมเกินขอบเขตที่ระบบรองรับ"));
        }

        return Json(new ApplyPromotionResponseViewModel(
            true,
            promotionStatus,
            message,
            nightCount,
            roomSubtotal,
            discountAmount,
            extraBedAmount,
            totalAmount));
    }

    [HttpPost]
    [Authorize(Roles = "Customer")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Booking(
        [FromForm] int? roomId,
        [FromForm] string? checkIn,
        [FromForm] string? checkOut,
        [FromForm] int? guests,
        [FromForm] int extraBedCount,
        [FromForm] string? promotionCode,
        CancellationToken cancellationToken)
    {
        var parsedCheckIn = TryParseBookingDate(
            checkIn,
            out var checkInDate);
        var parsedCheckOut = TryParseBookingDate(
            checkOut,
            out var checkOutDate);

        var request = new CreateBookingViewModel
        {
            RoomId = roomId ?? 0,
            CheckIn = parsedCheckIn ? checkInDate : null,
            CheckOut = parsedCheckOut ? checkOutDate : null,
            Guests = guests,
            ExtraBedCount = extraBedCount,
            PromotionCode = promotionCode
        };

        if (!parsedCheckIn)
        {
            ModelState.AddModelError(
                nameof(CreateBookingViewModel.CheckIn),
                string.IsNullOrWhiteSpace(checkIn)
                    ? "กรุณาเลือกวันที่เข้าพัก"
                    : "รูปแบบวันที่เข้าพักไม่ถูกต้อง");
        }

        if (!parsedCheckOut)
        {
            ModelState.AddModelError(
                nameof(CreateBookingViewModel.CheckOut),
                string.IsNullOrWhiteSpace(checkOut)
                    ? "กรุณาเลือกวันที่ออก"
                    : "รูปแบบวันที่ออกไม่ถูกต้อง");
        }

        TryValidateModel(request);

        LogBookingDateDiagnostics(
            "POST",
            checkIn ?? "<no form>",
            checkOut ?? "<no form>",
            parsedCheckIn ? checkInDate : null,
            parsedCheckOut ? checkOutDate : null);

        if (!int.TryParse(
                User.FindFirstValue(ClaimTypes.NameIdentifier),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var userId) ||
            userId <= 0)
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            var invalidModel = await _bookingService.PrepareConfirmationAsync(
                request,
                cancellationToken);
            return View(invalidModel);
        }

        var result = await _bookingService.CreateAsync(
            userId,
            request,
            cancellationToken);

        if (result.Status is BookingSaveStatus.Created or
            BookingSaveStatus.AlreadyExists)
        {
            return View(new BookingConfirmationViewModel
            {
                IsSaved = true,
                SavedBookingId = result.BookingId,
                EstimatedAmount = result.Amount,
                RoomSubtotal = result.RoomSubtotal,
                DiscountAmount = result.DiscountAmount,
                ExtraBedAmount = result.ExtraBedAmount,
                TotalAmount = result.Amount,
                ExtraBedCount = result.ExtraBedCount,
                PromotionId = result.PromotionId,
                PromotionCode = result.PromotionCode,
                StatusMessage = result.Message,
                PromotionStatusMessage = result.PromotionCode == null
                    ? null
                    : "ใช้ Promotion Code สำเร็จ"
            });
        }

        var model = await _bookingService.PrepareConfirmationAsync(
            request,
            cancellationToken);
        model.StatusMessage = result.Message;
        return View(model);
    }

    private bool TryParseBookingDate(
        string? rawValue,
        out DateOnly parsedDate)
    {
        parsedDate = default;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        return DateOnly.TryParseExact(
            rawValue,
            "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out parsedDate);
    }

    private void LogBookingDateDiagnostics(
        string source,
        string rawCheckIn,
        string rawCheckOut,
        DateOnly? checkIn,
        DateOnly? checkOut)
    {
        var modelStateErrors = string.Join(
            " | ",
            ModelState
                .Where(entry =>
                    entry.Key is nameof(CreateBookingViewModel.CheckIn) or
                        nameof(CreateBookingViewModel.CheckOut) or
                        nameof(CreateBookingViewModel.RoomId) or
                        nameof(CreateBookingViewModel.Guests))
                .SelectMany(entry => entry.Value?.Errors.Select(error =>
                    $"{entry.Key}: {SanitizeDiagnosticValue(error.ErrorMessage)}") ??
                    Enumerable.Empty<string>()));

        _logger.LogInformation(
            "Booking date diagnostic. Method={Method} Path={Path} Source={Source} RawCheckIn={RawCheckIn} RawCheckOut={RawCheckOut} BoundCheckIn={BoundCheckIn} BoundCheckOut={BoundCheckOut} ServerToday={ServerToday} ModelStateErrors={ModelStateErrors}",
            Request.Method,
            Request.Path,
            source,
            SanitizeDiagnosticValue(rawCheckIn),
            SanitizeDiagnosticValue(rawCheckOut),
            checkIn?.ToString(
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture) ?? "<null>",
            checkOut?.ToString(
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture) ?? "<null>",
            DateOnly.FromDateTime(DateTime.Today).ToString(
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture),
            string.IsNullOrEmpty(modelStateErrors)
                ? "<none>"
                : SanitizeDiagnosticValue(modelStateErrors));
    }

    private static string SanitizeDiagnosticValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "<empty>";
        }

        var sanitized = new string(value
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray());

        return sanitized.Length <= 128
            ? sanitized
            : sanitized[..128];
    }

    // ถ้า Login อยู่แล้ว ห้ามกลับมาหน้า Login
    public IActionResult Login()
    {
        if (User.Identity != null && User.Identity.IsAuthenticated)
        {
            if (User.IsInRole("Admin"))
                return RedirectToAction("Dashboard", "Admin");

            if (User.IsInRole("Staff"))
                return RedirectToAction("Index", "Staff");

            if (User.IsInRole("Housekeeper"))
                return RedirectToAction("Index", "Housekeeper");

            if (User.IsInRole("Customer"))
                return RedirectToAction("Index", "Home");
        }

        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(User data)
    {
        if (string.IsNullOrWhiteSpace(data.Email) ||
            string.IsNullOrWhiteSpace(data.Password))
        {
            ViewBag.Status = "กรุณากรอก Email และ Password";
            return View(data);
        }

        var email = data.Email.Trim();

        var user = _db.Users.FirstOrDefault(u => u.Email == email);

        if (user == null || string.IsNullOrEmpty(user.Password))
        {
            ViewBag.Status = "Email หรือ Password ไม่ถูกต้อง";
            return View(data);
        }

        PasswordVerificationResult passwordResult;

        // ตรวจสอบรหัสผ่านแบบ Hash
        try
        {
            passwordResult = _passwordHasher.VerifyHashedPassword(
                user,
                user.Password,
                data.Password
            );
        }
        catch (FormatException)
        {
            // รองรับรหัสผ่านเก่าที่เก็บเป็น Plain Text
            passwordResult = PasswordVerificationResult.Failed;
        }

        if (passwordResult == PasswordVerificationResult.Failed)
        {
            // ตรวจสอบรหัสผ่านเก่า
            if (user.Password != data.Password)
            {
                ViewBag.Status = "Email หรือ Password ไม่ถูกต้อง";
                return View(data);
            }

            // ถ้าถูกต้อง เปลี่ยนเป็น Hash
            user.Password = _passwordHasher.HashPassword(
                user,
                data.Password
            );

            _db.SaveChanges();
        }
        else if (passwordResult ==
                 PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.Password = _passwordHasher.HashPassword(
                user,
                data.Password
            );

            _db.SaveChanges();
        }

        var allowedRoles = new[]
        {
            "Customer",
            "Staff",
            "Housekeeper",
            "Admin"
        };

        if (string.IsNullOrWhiteSpace(user.Role) ||
            !allowedRoles.Contains(user.Role))
        {
            ViewBag.Status = "บัญชีนี้ยังไม่ได้กำหนด Role ที่ถูกต้อง";
            return View(data);
        }

        var claims = new List<Claim>
        {
            new Claim(
                ClaimTypes.NameIdentifier,
                user.UserId.ToString()
            ),
            new Claim(
                ClaimTypes.Name,
                user.Email ?? ""
            ),
            new Claim(
                ClaimTypes.Role,
                user.Role
            )
        };

        var identity = new ClaimsIdentity(
            claims,
            CookieAuthenticationDefaults.AuthenticationScheme
        );

        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal
        );

        return user.Role switch
        {
            "Admin" => RedirectToAction("Dashboard", "Admin"),
            "Customer" => RedirectToAction("Index", "Home"),
            "Staff" => RedirectToAction("Index", "Staff"),
            "Housekeeper" => RedirectToAction("Index", "Housekeeper"),
            _ => RedirectToAction("Login")
        };
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(
            CookieAuthenticationDefaults.AuthenticationScheme
        );

        return RedirectToAction("Index", "Home");
    }

    public IActionResult AccessDenied()
    {
        return View();
    }

    public IActionResult Register()
    {
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Register(
        User data,
        string? ConfirmPassword,
        bool AgreeToTerms)
    {
        if (string.IsNullOrWhiteSpace(data.FirstName))
        {
            ModelState.AddModelError("FirstName", "กรุณากรอกชื่อ");
        }
        else if (data.FirstName.Length > 50)
        {
            ModelState.AddModelError(
                "FirstName",
                "ชื่อต้องมีความยาวไม่เกิน 50 ตัวอักษร"
            );
        }

        if (string.IsNullOrWhiteSpace(data.LastName))
        {
            ModelState.AddModelError("LastName", "กรุณากรอกนามสกุล");
        }
        else if (data.LastName.Length > 50)
        {
            ModelState.AddModelError(
                "LastName",
                "นามสกุลต้องมีความยาวไม่เกิน 50 ตัวอักษร"
            );
        }

        if (string.IsNullOrWhiteSpace(data.Email))
        {
            ModelState.AddModelError("Email", "กรุณากรอกอีเมล");
        }
        else if (data.Email.Length > 100 ||
                 !new EmailAddressAttribute().IsValid(data.Email.Trim()))
        {
            ModelState.AddModelError(
                "Email",
                "กรุณากรอกอีเมลให้ถูกต้อง"
            );
        }

        if (string.IsNullOrWhiteSpace(data.Phone))
        {
            ModelState.AddModelError("Phone", "กรุณากรอกหมายเลขโทรศัพท์");
        }
        else
        {
            var phone = data.Phone.Trim();

            if (phone.Any(c => !char.IsDigit(c) && c != ' '))
            {
                ModelState.AddModelError("Phone", "กรุณากรอกเฉพาะตัวเลข");
            }
            else
            {
                var phoneDigits = new string(
                    phone.Where(char.IsDigit).ToArray()
                );

                if (phoneDigits.Length != 10 ||
                    !(phoneDigits.StartsWith("06") ||
                      phoneDigits.StartsWith("08") ||
                      phoneDigits.StartsWith("09")))
                {
                    ModelState.AddModelError(
                        "Phone",
                        "กรุณากรอกหมายเลขโทรศัพท์มือถือไทย 10 หลัก โดยขึ้นต้นด้วย 06, 08 หรือ 09"
                    );
                }
                else
                {
                    data.Phone = phoneDigits;
                }
            }
        }

        if (data.BirthDate == null)
        {
            ModelState.AddModelError("BirthDate", "กรุณาเลือกวันเกิด");
        }

        if (string.IsNullOrWhiteSpace(data.Password))
        {
            ModelState.AddModelError("Password", "กรุณากรอกรหัสผ่าน");
        }
        else if (data.Password.Length > 255)
        {
            ModelState.AddModelError(
                "Password",
                "รหัสผ่านต้องมีความยาวไม่เกิน 255 ตัวอักษร"
            );
        }

        if (data.Password != ConfirmPassword)
        {
            ModelState.AddModelError(
                "ConfirmPassword",
                "รหัสผ่านและการยืนยันรหัสผ่านไม่ตรงกัน"
            );
        }

        if (!AgreeToTerms)
        {
            ModelState.AddModelError(
                "AgreeToTerms",
                "กรุณายอมรับข้อกำหนดและนโยบายความเป็นส่วนตัวก่อนสมัครสมาชิก"
            );
        }

        if (!ModelState.IsValid)
        {
            ViewBag.ConfirmPassword = ConfirmPassword;
            ViewBag.AgreeToTerms = AgreeToTerms;
            return View(data);
        }

        var email = data.Email!.Trim();

        var userCheck = _db.Users.FirstOrDefault(u => u.Email == email);

        if (userCheck != null)
        {
            ViewBag.Status =
                "อีเมลนี้ได้ลงทะเบียนไว้แล้ว กรุณาใช้อีเมลอื่นหรือเข้าสู่ระบบ";

            ViewBag.ConfirmPassword = ConfirmPassword;
            ViewBag.AgreeToTerms = AgreeToTerms;
            return View(data);
        }

        var lastUser = _db.Users
            .OrderByDescending(u => u.UserId)
            .FirstOrDefault();

        User user = new();

        user.UserId = lastUser == null ? 1 : lastUser.UserId + 1;
        user.FirstName = data.FirstName!.Trim();
        user.LastName = data.LastName!.Trim();
        user.Email = email;
        user.Phone = data.Phone!;
        user.BirthDate = data.BirthDate;

        // บัญชีใหม่ใช้ Password Hash ตั้งแต่แรก
        user.Password = _passwordHasher.HashPassword(
            user,
            data.Password!
        );

        user.Role = "Customer";

        try
        {
            _db.Add(user);
            _db.SaveChanges();
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException exception)
        {
            _logger.LogError(
                exception,
                "Could not save a new registered user."
            );

            ViewBag.Status =
                "เกิดข้อผิดพลาดในการบันทึกข้อมูล กรุณาลองอีกครั้ง";

            ViewBag.ConfirmPassword = ConfirmPassword;
            ViewBag.AgreeToTerms = AgreeToTerms;
            return View(data);
        }

        TempData["RegisterSuccessMessage"] =
            "สมัครสมาชิกสำเร็จ กรุณาเข้าสู่ระบบ";

        return RedirectToAction("Login");
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(
        Duration = 0,
        Location = ResponseCacheLocation.None,
        NoStore = true
    )]
    public IActionResult Error()
    {
        return View(new ErrorViewModel
        {
            RequestId = Activity.Current?.Id
                ?? HttpContext.TraceIdentifier
        });
    }
}
