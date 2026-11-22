
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
    private readonly RoomAvailabilityService _availability;
    private readonly BookingCancellationService _cancellation;

    public HomeController(
        GoodHotelDbContext db,
        ILogger<HomeController> logger,
        IPasswordHasher<User> passwordHasher,
        IWebHostEnvironment environment,
        BookingService bookingService,
        RoomAvailabilityService availability,
        BookingCancellationService cancellation)
    {
        _cancellation = cancellation;
        _db = db;
        _logger = logger;
        _passwordHasher = passwordHasher;
        _environment = environment;
        _bookingService = bookingService;
        _availability = availability;
    }

    public async Task<IActionResult> Index([FromServices] ParallelReader reader)
    {
        // โปรโมชันดึงพร้อมกันกับห้องพัก (คนละ connection)
        var promotionsTask = reader.Run(LoadOpenPromotionsAsync);

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

        var model = new HomeViewModel
        {
            FeaturedRooms = featuredRooms,
            Promotions = await promotionsTask
        };

        return View(model);
    }

    /// <summary>โปรที่เปิดอยู่และยังไม่หมดเขต สำหรับแสดงให้ลูกค้าเห็น</summary>
    private Task<List<PromotionListViewModel>> LoadOpenPromotionsAsync() => LoadOpenPromotionsAsync(_db);

    private async Task<List<PromotionListViewModel>> LoadOpenPromotionsAsync(GoodHotelDbContext db)
    {
        var today = _availability.HotelToday();
        var promotions = await db.Promotions.AsNoTracking()
            .Where(p => p.IsActive && p.RuleType != null && (p.EndDate == null || p.EndDate >= today))
            .OrderBy(p => p.PromotionId)
            .ToListAsync();
        return promotions.Select(PromotionListViewModel.From).ToList();
    }

    [HttpGet]
    public async Task<IActionResult> Rooms(AvailabilitySearchViewModel search)
    {
        ViewData["HotelToday"] = _availability.HotelToday();
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
            else if (search.CheckIn.Value < _availability.HotelToday())
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
                var guests = search.Guests!.Value;

                roomsQuery = roomsQuery
                    .Where(room =>
                        room.Capacity.HasValue &&
                        room.Capacity.Value >= guests);

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

        if (search.HasSearched)
        {
            var blocked = await _availability.GetBlockedPeriodsAsync(
                search.Rooms.Select(room => room.RoomId).ToArray(),
                search.CheckIn!.Value, search.CheckOut!.Value, HttpContext.RequestAborted);
            var blockedIds = blocked.Select(period => period.RoomId).ToHashSet();
            search.Rooms = search.Rooms.Where(room => !blockedIds.Contains(room.RoomId)).ToList();
        }

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
        var today = _availability.HotelToday();

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
                booking.Status != BookingStatuses.Cancelled &&
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

        var blockedPeriods = (await _availability.GetBlockedPeriodsAsync(
            new[] { room.RoomId }, monthStart, monthEnd, cancellationToken))
            .Select(period => new
            {
                CheckIn = !period.Start.HasValue || period.Start < monthStart ? monthStart : period.Start.Value,
                CheckOut = !period.End.HasValue || period.End > monthEnd ? monthEnd : period.End.Value
            }).ToList();

        var days = new List<object>(DateTime.DaysInMonth(
            monthStart.Year,
            monthStart.Month));
        for (var date = monthStart; date < monthEnd; date = date.AddDays(1))
        {
            var occupied = blockedPeriods.Any(period =>
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
            BlockedPeriods = blockedPeriods.Select(period => new
            {
                CheckIn = period.CheckIn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                CheckOut = period.CheckOut.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            }),
            Days = days
        });
    }

    public async Task<IActionResult> Promotion()
    {
        return View(await LoadOpenPromotionsAsync());
    }

    [HttpGet]
    [Authorize(Roles = "Customer")]
    public async Task<IActionResult> MyBookings(
        [FromServices] RefundNotices notices,
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
                Amount = booking.Amount,
                BookingStatus = booking.Status,
                CreatedAtUtc = booking.CreatedAtUtc,
                CancelReason = booking.CancelReason,
                StayStatus = _db.BookingStays
                    .Where(stay => stay.BookingId == booking.BookingId)
                    .Select(stay => stay.Status)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        var today = _availability.HotelToday();
        foreach (var booking in bookings)
        {
            booking.IsHistory = booking.BookingStatus == BookingStatuses.Cancelled ||
                                booking.StayStatus == "CheckedOut" ||
                                (booking.StayStatus == null && booking.CheckOut <= today);
        }

        var paymentRows = await _db.Payments.AsNoTracking()
            .Where(p => p.Booking != null && p.Booking.UserId == userId)
            .Select(p => new { p.BookingId, p.PaymentId, p.PaymentMethod, p.PaymentStatus, p.Amount,
                p.SubmittedAtUtc, p.VerifiedAtUtc, p.LegacyDisposition })
            .ToListAsync(cancellationToken);
        var allRefunds = await _db.PaymentRefunds.AsNoTracking()
            .Where(r => r.Payment.Booking != null && r.Payment.Booking.UserId == userId)
            .OrderByDescending(r => r.RefundId)
            .Select(r => new { r.Payment.BookingId, r.PaymentId, r.Amount, r.Status }).ToListAsync(cancellationToken);
        var refunds = allRefunds.Where(r => r.Status == PaymentService.RefundApproved).ToList();

        foreach (var booking in bookings)
        {
            var records = paymentRows.Where(p => p.BookingId == booking.BookingId).ToList();
            var needsReview = records.Any(p => p.SubmittedAtUtc == null && p.VerifiedAtUtc == null &&
                p.LegacyDisposition == null);
            var confirmedPayment = records.Where(p => p.VerifiedAtUtc != null)
                .OrderBy(p => p.PaymentId).FirstOrDefault();
            var paid = confirmedPayment?.Amount ?? 0m;
            var bookingRefunds = allRefunds.Where(r => r.BookingId == booking.BookingId).ToList();
            booking.RefundStatus = bookingRefunds.FirstOrDefault()?.Status;
            booking.CanCancel = BookingCancellationService.CanCancel(booking.BookingStatus, booking.CheckOut,
                booking.StayStatus != null, booking.RefundStatus == PaymentService.RefundRequested, today);
            booking.CanRequestRefund = PaymentService.CanRequestRefund(booking.BookingStatus, booking.CheckOut,
                booking.StayStatus != null, confirmedPayment != null, bookingRefunds.Count > 0, today);
            var refunded = refunds.Where(r => r.BookingId == booking.BookingId &&
                r.PaymentId == confirmedPayment?.PaymentId).Sum(r => r.Amount);
            // การคืนเงินไม่ทำให้ลูกค้าค้างจ่ายเพิ่ม (เดิมบวกยอดคืนกลับเข้าไป)
            booking.OutstandingAmount = needsReview || !booking.Amount.HasValue
                ? null : Math.Max(0m, booking.Amount.Value - paid);
            var hasPending = records.Any(p => p.SubmittedAtUtc != null && p.VerifiedAtUtc == null &&
                (p.PaymentStatus == "Pending" || p.PaymentStatus == "Submitted"));
            booking.PaymentDueAtUtc = booking.BookingStatus == BookingStatuses.Booked && booking.StayStatus == null &&
                confirmedPayment == null && !hasPending
                    ? PaymentDeadline.DueAtUtc(booking.CreatedAtUtc) : null;
            booking.IsMockPayment = confirmedPayment != null && PaymentService.IsMockMethod(confirmedPayment.PaymentMethod);
            booking.PaymentStatus = needsReview ? "Requires reconciliation" :
                confirmedPayment != null ? (paid > 0 && refunded >= paid ? "Refunded" : "Paid") :
                records.Where(p => p.SubmittedAtUtc != null || p.VerifiedAtUtc != null)
                    .OrderByDescending(p => p.VerifiedAtUtc != null)
                    .ThenByDescending(p => p.PaymentId).Select(p => p.PaymentStatus).FirstOrDefault() ?? "Not submitted";
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

        // ผลคำขอคืนเงินใหม่ที่ยังไม่เคยเห็น: แจ้งบนหน้านี้ แล้วนับว่าเห็นแล้ว (ตัวเลขที่เมนูหายไป)
        ViewData["RefundNotices"] = await notices.UnseenAsync(HttpContext, _db, userId, cancellationToken);
        notices.MarkSeen(HttpContext, userId);
        return View(bookings);
    }

    [HttpPost]
    [Authorize(Roles = "Customer")]
    [ValidateAntiForgeryToken]
    /// <param name="findAnotherRoom">กดจากหน้าชำระเงิน "ยกเลิกแล้วเลือกห้องอื่น": ยกเลิกสำเร็จแล้วพาไปค้นห้องวันเดิม</param>
    public async Task<IActionResult> CancelBooking(int bookingId, bool findAnotherRoom, CancellationToken cancellationToken)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) || userId <= 0)
        {
            return Forbid();
        }

        var stay = await _db.Bookings.AsNoTracking()
            .Where(b => b.BookingId == bookingId && b.UserId == userId)
            .Select(b => new { b.CheckIn, b.CheckOut, b.Guests })
            .FirstOrDefaultAsync(cancellationToken);

        var result = await _cancellation.CancelAsync(bookingId, userId, cancellationToken);
        TempData[result.Succeeded ? "BookingSuccess" : "BookingError"] = result.Message;

        if (result.Succeeded && findAnotherRoom && stay?.CheckIn != null && stay.CheckOut != null)
        {
            var iso = System.Globalization.CultureInfo.InvariantCulture;
            return RedirectToAction(nameof(Rooms), new
            {
                CheckIn = stay.CheckIn.Value.ToString("yyyy-MM-dd", iso),
                CheckOut = stay.CheckOut.Value.ToString("yyyy-MM-dd", iso),
                Guests = stay.Guests ?? 1
            });
        }
        return result.Succeeded || !findAnotherRoom
            ? RedirectToAction(nameof(MyBookings))
            : RedirectToAction("Details", "Payment", new { id = bookingId });
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
            CurrentCustomerId(),
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
            return BadRequest(new ApplyPromotionResponseViewModel(false, "ข้อมูลการเข้าพักไม่ถูกต้อง"));
        }

        // ใช้ logic เดียวกับหน้ายืนยันการจอง: ตรวจห้อง + คำนวณราคา + เลือกโปรที่ดีที่สุด
        var quote = await _bookingService.PrepareConfirmationAsync(
            new CreateBookingViewModel
            {
                RoomId = request.RoomId,
                CheckIn = checkIn,
                CheckOut = checkOut,
                Guests = request.Guests,
                ExtraBedCount = request.ExtraBedCount,
                PromotionId = request.PromotionId
            },
            CurrentCustomerId(),
            cancellationToken);

        if (!quote.CanConfirm)
        {
            return BadRequest(new ApplyPromotionResponseViewModel(false, quote.StatusMessage ?? "ข้อมูลไม่ถูกต้อง"));
        }

        return Json(new ApplyPromotionResponseViewModel(
            true,
            PromotionMessage(quote),
            quote.PromotionName,
            quote.NightCount,
            quote.RoomSubtotal,
            quote.DiscountAmount,
            quote.ExtraBedAmount,
            quote.TotalAmount,
            quote.PromotionId));
    }

    // ข้อความใต้ dropdown: บอกว่าใช้โปรอะไร และถ้าไม่ใช่โปรที่ลดได้มากที่สุดให้บอกด้วย
    private static string PromotionMessage(BookingConfirmationViewModel quote)
    {
        if (quote.PromotionName == null) return "การจองนี้ยังไม่เข้าเงื่อนไขโปรโมชัน";
        var best = quote.AvailablePromotions.FirstOrDefault();
        var chosenDiscount = quote.DiscountAmount ?? 0m;
        // บอกว่ามีโปรที่ลดได้มากกว่า เฉพาะเมื่อมากกว่าจริง (ลดเท่ากันถือว่าคุ้มที่สุดเหมือนกัน)
        return best != null && best.DiscountAmount > chosenDiscount
            ? $"ใช้โปร \"{quote.PromotionName}\" ลด ฿{chosenDiscount:N0} (โปร \"{best.Name}\" ลดได้ ฿{best.DiscountAmount:N0})"
            : $"ใช้โปร \"{quote.PromotionName}\" ลด ฿{chosenDiscount:N0} ซึ่งคุ้มที่สุดแล้ว";
    }

    private int? CurrentCustomerId() =>
        User.IsInRole("Customer") && int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id > 0
            ? id
            : null;

    [HttpPost]
    [Authorize(Roles = "Customer")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Booking(
        [FromForm] int? roomId,
        [FromForm] string? checkIn,
        [FromForm] string? checkOut,
        [FromForm] int? guests,
        [FromForm] int extraBedCount,
        [FromForm] int? promotionId,
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
            PromotionId = promotionId
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
                userId,
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
            var paymentRows = await _db.Payments.AsNoTracking()
                .Where(payment => payment.BookingId == result.BookingId)
                .Select(payment => new
                {
                    payment.PaymentId, payment.PaymentMethod, payment.PaymentStatus, payment.Amount, payment.SubmittedAtUtc,
                    payment.VerifiedAtUtc, payment.LegacyDisposition,
                    Refunded = payment.Refunds.Where(refund => refund.Status == "Approved")
                        .Sum(refund => (decimal?)refund.Amount) ?? 0m
                }).ToListAsync(cancellationToken);
            var needsReview = paymentRows.Any(payment => payment.SubmittedAtUtc == null &&
                payment.VerifiedAtUtc == null && payment.LegacyDisposition == null);
            var confirmed = paymentRows.Where(payment => payment.VerifiedAtUtc != null)
                .OrderBy(payment => payment.PaymentId).FirstOrDefault();
            var pending = paymentRows.Any(payment => payment.SubmittedAtUtc != null &&
                payment.VerifiedAtUtc == null && (payment.PaymentStatus == "Pending" || payment.PaymentStatus == "Submitted"));
            var paymentStatus = needsReview ? "Requires reconciliation" :
                confirmed != null
                    ? (confirmed.Amount > 0 && confirmed.Refunded >= confirmed.Amount
                        ? "Refunded" : "Paid")
                    : paymentRows.Where(payment => payment.SubmittedAtUtc != null)
                        .OrderByDescending(payment => payment.PaymentId)
                        .Select(payment => payment.PaymentStatus).FirstOrDefault() ?? "Not submitted";
            var createdAtUtc = await _db.Bookings.AsNoTracking().Where(b => b.BookingId == result.BookingId)
                .Select(b => b.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
            var dueAtUtc = confirmed == null && !pending ? PaymentDeadline.DueAtUtc(createdAtUtc) : null;
            return View(new BookingConfirmationViewModel
            {
                IsSaved = true,
                SavedBookingId = result.BookingId,
                PaymentStatus = paymentStatus,
                IsMockPayment = confirmed != null && PaymentService.IsMockMethod(confirmed.PaymentMethod),
                CanSubmitPayment = result.BookingId.HasValue && result.Amount > 0 &&
                    !needsReview && confirmed == null && !pending &&
                    !(dueAtUtc <= DateTime.UtcNow),
                PaymentDueAtUtc = dueAtUtc,
                EstimatedAmount = result.Amount,
                RoomSubtotal = result.RoomSubtotal,
                DiscountAmount = result.DiscountAmount,
                ExtraBedAmount = result.ExtraBedAmount,
                TotalAmount = result.Amount,
                ExtraBedCount = result.ExtraBedCount,
                PromotionId = result.PromotionId,
                PromotionName = result.PromotionName,
                StatusMessage = result.Message
            });
        }

        var model = await _bookingService.PrepareConfirmationAsync(
            request,
            userId,
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

            if (User.IsInRole("Accountant"))
                return RedirectToAction("Index", "Accountant");

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

        if (user == null || !user.IsActive || string.IsNullOrEmpty(user.Password))
        {
            ViewBag.Status = "อีเมลหรือรหัสผ่านไม่ถูกต้อง";
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
                ViewBag.Status = "อีเมลหรือรหัสผ่านไม่ถูกต้อง";
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
            "Accountant",
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
            "Accountant" => RedirectToAction("Index", "Accountant"),
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

        // เคยมาแบบ Walk-in: พนักงานสร้างบัญชีให้แล้ว (ยังไม่มีรหัสผ่าน) → ผูกเข้าบัญชีเดิมแทนการสร้างบัญชีซ้ำ
        // ประวัติการเข้าพักเดิมจะโผล่ในหน้าการจองของฉัน และ Walk-in ครั้งหน้าก็เข้าบัญชีเดียวกัน
        // ต้องตรงทั้งเบอร์และชื่อ-นามสกุล และต้องเป็นบัญชีที่ยังไม่มีรหัสผ่านเท่านั้น
        // (กันคนอื่นกรอกเบอร์ของลูกค้าคนอื่นมาสมัครเพื่อดูประวัติ หรือสมัครทับบัญชีที่มีเจ้าของแล้ว)
        var firstName = data.FirstName!.Trim();
        var lastName = data.LastName!.Trim();
        var walkInCandidates = _db.Users
            .Where(u => u.Role == "Customer" && u.Password == null && u.Phone == data.Phone)
            .ToList();
        var walkInAccount = walkInCandidates.FirstOrDefault(u =>
            string.Equals(u.FirstName?.Trim(), firstName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(u.LastName?.Trim(), lastName, StringComparison.OrdinalIgnoreCase));

        User user;
        if (walkInAccount != null)
        {
            user = walkInAccount;
            user.IsActive = true;
        }
        else
        {
            var lastUser = _db.Users
                .OrderByDescending(u => u.UserId)
                .FirstOrDefault();
            user = new User { UserId = lastUser == null ? 1 : lastUser.UserId + 1, Role = "Customer" };
            _db.Add(user);
        }

        user.FirstName = firstName;
        user.LastName = lastName;
        user.Email = email;
        user.Phone = data.Phone!;
        user.BirthDate = data.BirthDate;

        // บัญชีใหม่ใช้ Password Hash ตั้งแต่แรก
        user.Password = _passwordHasher.HashPassword(
            user,
            data.Password!
        );

        try
        {
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

        TempData["RegisterSuccessMessage"] = walkInAccount != null
            ? "สมัครสมาชิกสำเร็จ ระบบเชื่อมประวัติการเข้าพักที่เคาน์เตอร์ (Walk-in) เข้ากับบัญชีนี้แล้ว กรุณาเข้าสู่ระบบ"
            : "สมัครสมาชิกสำเร็จ กรุณาเข้าสู่ระบบ";

        return RedirectToAction("Login");
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
