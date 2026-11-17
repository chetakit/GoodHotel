
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using GoodHotel.Models;
using GoodHotel.Models.ViewModels;
using GoodHotel.Services;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Data;

namespace GoodHotel.Controllers
{
    [Authorize(Roles = "Admin")]
    public class AdminController : Controller
    {
        private static readonly string[] AllowedRoomStatuses =
        {
            "Available",
            "Occupied",
            "Reserved",
            "Maintenance"
        };

        private readonly GoodHotelDbContext _db;
        private readonly IPasswordHasher<User> _passwordHasher;
        private readonly ILogger<AdminController> _logger;
        private readonly IWebHostEnvironment _environment;
        private readonly HousekeepingService _housekeeping;
        private readonly RoomAvailabilityService _availability;
        private readonly RoomStatusService _roomStatus;

        public AdminController(
            GoodHotelDbContext db,
            IPasswordHasher<User> passwordHasher,
            ILogger<AdminController> logger,
            IWebHostEnvironment environment,
            HousekeepingService housekeeping,
            RoomAvailabilityService availability,
            RoomStatusService roomStatus)
        {
            _roomStatus = roomStatus;
            _db = db;
            _passwordHasher = passwordHasher;
            _logger = logger;
            _environment = environment;
            _housekeeping = housekeeping;
            _availability = availability;
        }

        public async Task<IActionResult> Dashboard(CancellationToken ct)
        {
            // สถานะห้องจริง (ดูจากการเข้าพัก/งานทำความสะอาด/งานซ่อม) ให้ตรงกับหน้า Room Status ของ Staff
            var roomBoard = await _roomStatus.GetRoomBoardAsync(ct);
            var liveRoomCounts = roomBoard
                .GroupBy(room => room.Status)
                .OrderBy(group => group.Key)
                .Select(group => new RoomStatusCountViewModel
                {
                    Status = RoomStatusService.Label(group.Key),
                    Count = group.Count()
                })
                .ToList();

            var confirmedPayments = PaymentService.ConfirmedForRevenue(_db.Payments);
            var model = new AdminDashboardViewModel
            {
                RoomCount = _db.Rooms.Count(),
                UserCount = _db.Users.Count(),
                BookingCount = _db.Bookings.Count(b => b.Status != BookingStatuses.Cancelled),
                Revenue = (confirmedPayments.Sum(p => p.Amount) ?? 0m) -
                    (_db.PaymentRefunds.Where(r => r.Status == "Approved" &&
                        confirmedPayments.Any(payment => payment.PaymentId == r.PaymentId))
                        .Sum(r => (decimal?)r.Amount) ?? 0m),
                OpenMaintenanceCount = _db.MaintenanceReports.Count(r => r.Status == "Open"),
                PendingHousekeepingCount = _db.HousekeepingTasks.Count(t => t.Status == "Pending"),
                AwaitingVerificationCount = _db.HousekeepingTasks.Count(t => t.Status == "Completed"),
                RoomStatusCounts = liveRoomCounts,
                BookingsByCheckInDate = _db.Bookings
                    .Where(booking => booking.CheckIn.HasValue && booking.Status != BookingStatuses.Cancelled)
                    .OrderByDescending(booking => booking.CheckIn)
                    .ThenByDescending(booking => booking.BookingId)
                    .Take(5)
                    .Select(booking => new DashboardBookingViewModel
                    {
                        BookingId = booking.BookingId,
                        GuestName = booking.User == null
                            ? null
                            : (booking.User.FirstName ?? "") + " " +
                              (booking.User.LastName ?? ""),
                        RoomNumber = booking.Room == null
                            ? null
                            : booking.Room.Room1,
                        CheckIn = booking.CheckIn,
                        Amount = booking.Amount
                    })
                    .ToList()
            };

            return View(model);
        }

        public async Task<IActionResult> Rooms(CancellationToken ct)
        {
            // สถานะจริงตอนนี้ (รวมการเข้าพัก งานทำความสะอาด งานซ่อม) แสดงคู่กับสถานะที่ Admin ตั้งไว้
            ViewBag.LiveStatus = (await _roomStatus.GetRoomBoardAsync(ct)).ToDictionary(r => r.RoomId);

            var rooms = _db.Rooms
                .OrderBy(room => room.Room1)
                .Select(room => new RoomListViewModel
                {
                    RoomId = room.RoomId,
                    RoomNumber = room.Room1,
                    Type = room.Type,
                    Capacity = room.Capacity,
                    Status = room.Status,
                    Rate = room.Rate,
                    ImagePath = room.ImagePath,
                    Description = room.Description
                })
                .ToList();

            foreach (var room in rooms)
            {
                room.ImagePath = RoomImageStorage.GetDisplayPath(
                    _environment.WebRootPath,
                    room.ImagePath);
            }

            return View(rooms);
        }

        public IActionResult AddRoom()
        {
            return View("RoomForm", new RoomFormViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddRoom(RoomFormViewModel data)
        {
            ValidateRoom(data);
            ValidateRoomImage(data.Image);

            if (!ModelState.IsValid)
            {
                return View("RoomForm", data);
            }

            data.RoomNumber = data.RoomNumber!.Trim();
            data.Type = data.Type!.Trim();
            data.Description = data.Description?.Trim();
            data.Status = data.Status!.Trim();

            if (_db.Rooms.Any(room => room.Room1 == data.RoomNumber))
            {
                ModelState.AddModelError(
                    nameof(data.RoomNumber),
                    "มีห้องหมายเลขนี้อยู่แล้ว");
                return View("RoomForm", data);
            }

            var maxRoomId = _db.Rooms
                .Select(room => (int?)room.RoomId)
                .Max() ?? 0;

            string? uploadedImagePath = null;

            try
            {
                if (data.Image != null)
                {
                    uploadedImagePath = await RoomImageStorage.SaveAsync(
                        data.Image,
                        _environment.WebRootPath,
                        _logger,
                        HttpContext.RequestAborted);
                }

                var room = new Room
                {
                    RoomId = maxRoomId + 1,
                    Room1 = data.RoomNumber,
                    Type = data.Type,
                    Description = data.Description,
                    Capacity = data.Capacity,
                    Status = data.Status,
                    Rate = data.Rate,
                    ImagePath = uploadedImagePath
                };

                _db.Rooms.Add(room);
                await _db.SaveChangesAsync(HttpContext.RequestAborted);
            }
            catch (InvalidDataException exception)
            {
                DeleteNewUpload(uploadedImagePath);
                ModelState.AddModelError(nameof(data.Image), exception.Message);
                return View("RoomForm", data);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(exception, "Could not save an uploaded room image.");
                DeleteNewUpload(uploadedImagePath);
                ModelState.AddModelError(
                    nameof(data.Image),
                    "อัปโหลดรูปไม่สำเร็จ กรุณาลองใหม่");
                return View("RoomForm", data);
            }
            catch (DbUpdateException exception)
            {
                _logger.LogError(exception, "Could not save a new room.");
                DeleteNewUpload(uploadedImagePath);
                ModelState.AddModelError(
                    "",
                    "บันทึกห้องไม่สำเร็จ กรุณาลองใหม่");
                return View("RoomForm", data);
            }

            TempData["RoomSuccessMessage"] = "เพิ่มห้องแล้ว";
            return RedirectToAction(nameof(Rooms));
        }

        public IActionResult EditRoom(int id)
        {
            var room = _db.Rooms.FirstOrDefault(room => room.RoomId == id);
            if (room == null)
            {
                return NotFound();
            }

            var data = new RoomFormViewModel
            {
                RoomId = room.RoomId,
                RoomNumber = room.Room1,
                Type = room.Type,
                Description = room.Description,
                Capacity = room.Capacity,
                Status = room.Status,
                OriginalStatus = room.Status,
                OriginalPreMaintenanceStatus = room.PreMaintenanceStatus,
                OriginalMaintenanceEventId = _db.MaintenanceReportEvents
                    .Where(item => item.Report.RoomId == room.RoomId)
                    .Max(item => (int?)item.EventId) ?? 0,
                RequiresMaintenanceReview = room.Status == "Maintenance" &&
                    !_db.MaintenanceReports.Any(report =>
                        report.RoomId == room.RoomId && report.Status != MaintenanceReport.Resolved),
                Rate = room.Rate,
                ImagePath = RoomImageStorage.GetDisplayPath(
                    _environment.WebRootPath,
                    room.ImagePath)
            };

            return View("RoomForm", data);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditRoom(RoomFormViewModel data)
        {
            if (data.RoomId is null or <= 0)
            {
                return NotFound();
            }

            var room = _db.Rooms
                .FirstOrDefault(existingRoom => existingRoom.RoomId == data.RoomId);

            if (room == null)
            {
                return NotFound();
            }

            var previousImagePath = room.ImagePath;
            data.RequiresMaintenanceReview = room.Status == "Maintenance" &&
                !_db.MaintenanceReports.Any(report =>
                    report.RoomId == room.RoomId && report.Status != MaintenanceReport.Resolved);
            data.ImagePath = RoomImageStorage.GetDisplayPath(
                _environment.WebRootPath,
                previousImagePath);
            ValidateRoom(data);
            ValidateRoomImage(data.Image);

            if (!ModelState.IsValid)
            {
                return View("RoomForm", data);
            }

            data.RoomNumber = data.RoomNumber!.Trim();
            data.Type = data.Type!.Trim();
            data.Description = data.Description?.Trim();
            data.Status = data.Status!.Trim();

            if (_db.Rooms.Any(existingRoom =>
                    existingRoom.RoomId != data.RoomId &&
                    existingRoom.Room1 == data.RoomNumber))
            {
                ModelState.AddModelError(
                    nameof(data.RoomNumber),
                    "มีห้องหมายเลขนี้อยู่แล้ว");
                return View("RoomForm", data);
            }

            string? uploadedImagePath = null;

            try
            {
                if (data.Image != null)
                {
                    uploadedImagePath = await RoomImageStorage.SaveAsync(
                        data.Image,
                        _environment.WebRootPath,
                        _logger,
                        HttpContext.RequestAborted);
                }

                await using var transaction = await _db.Database.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted, HttpContext.RequestAborted);
                if (!await RoomAvailabilityService.LockRoomAsync(_db, transaction, data.RoomId.Value, HttpContext.RequestAborted))
                {
                    DeleteNewUpload(uploadedImagePath);
                    return NotFound();
                }
                await _db.Entry(room).ReloadAsync(HttpContext.RequestAborted);
                previousImagePath = room.ImagePath;
                data.ImagePath = RoomImageStorage.GetDisplayPath(_environment.WebRootPath, previousImagePath);
                var latestEventId = await _db.MaintenanceReportEvents
                    .Where(item => item.Report.RoomId == room.RoomId)
                    .MaxAsync(item => (int?)item.EventId, HttpContext.RequestAborted) ?? 0;
                if (data.HasStaleRoomStatus(room.Status, room.PreMaintenanceStatus, latestEventId))
                {
                    DeleteNewUpload(uploadedImagePath);
                    ModelState.AddModelError(string.Empty,
                        "สถานะห้องเปลี่ยนไประหว่างที่เปิดหน้านี้ กรุณาโหลดหน้าใหม่");
                    return View("RoomForm", data);
                }
                var hasUnresolved = await _db.MaintenanceReports.AnyAsync(
                    report => report.RoomId == data.RoomId && report.Status != MaintenanceReport.Resolved,
                    HttpContext.RequestAborted);
                if (data.Status != "Maintenance" && hasUnresolved)
                {
                    DeleteNewUpload(uploadedImagePath);
                    ModelState.AddModelError(nameof(data.Status),
                        "ห้องต้องเป็นสถานะซ่อมบำรุงจนกว่างานซ่อมจะเสร็จ");
                    return View("RoomForm", data);
                }
                data.RequiresMaintenanceReview = room.Status == "Maintenance" && !hasUnresolved;
                if (data.RequiresMaintenanceReview && data.Status != "Maintenance" && !data.ConfirmMaintenanceReview)
                {
                    DeleteNewUpload(uploadedImagePath);
                    ModelState.AddModelError(nameof(data.ConfirmMaintenanceReview),
                        "กรุณาตรวจสอบเงื่อนไขของห้องก่อนเปลี่ยนสถานะกลับ");
                    return View("RoomForm", data);
                }
                if (((room.Status == "Maintenance" && data.Status != "Maintenance") ||
                     (room.Status != "Available" && data.Status == "Available")) &&
                    await _availability.HasCurrentRestrictionsAsync(room.RoomId, HttpContext.RequestAborted))
                {
                    DeleteNewUpload(uploadedImagePath);
                    ModelState.AddModelError(nameof(data.Status),
                        "ห้องนี้ยังมีแขก การจอง หรืองานทำความสะอาดค้างอยู่ กรุณาลองใหม่ภายหลัง");
                    return View("RoomForm", data);
                }

                room.Room1 = data.RoomNumber;
                room.Type = data.Type;
                room.Description = data.Description;
                room.Capacity = data.Capacity;
                room.Status = data.Status;
                if (data.Status != "Maintenance") room.PreMaintenanceStatus = null;
                room.Rate = data.Rate;
                if (uploadedImagePath != null)
                {
                    room.ImagePath = uploadedImagePath;
                }

                await _db.SaveChangesAsync(HttpContext.RequestAborted);
                await transaction.CommitAsync(HttpContext.RequestAborted);
            }
            catch (InvalidDataException exception)
            {
                DeleteNewUpload(uploadedImagePath);
                ModelState.AddModelError(nameof(data.Image), exception.Message);
                return View("RoomForm", data);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(exception, "Could not update the room image.");
                DeleteNewUpload(uploadedImagePath);
                ModelState.AddModelError(
                    nameof(data.Image),
                    "อัปโหลดรูปไม่สำเร็จ กรุณาลองใหม่");
                return View("RoomForm", data);
            }
            catch (DbUpdateException exception)
            {
                _logger.LogError(
                    exception,
                    "Could not update room {RoomId}.",
                    data.RoomId);
                DeleteNewUpload(uploadedImagePath);
                ModelState.AddModelError(
                    "",
                    "แก้ไขห้องไม่สำเร็จ กรุณาลองใหม่");
                return View("RoomForm", data);
            }

            if (uploadedImagePath != null &&
                !string.Equals(
                    previousImagePath,
                    uploadedImagePath,
                    StringComparison.Ordinal))
            {
                DeletePreviousImageIfUnused(previousImagePath, data.RoomId.Value);
            }

            TempData["RoomSuccessMessage"] = "บันทึกข้อมูลห้องแล้ว";
            return RedirectToAction(nameof(Rooms));
        }

        private void ValidateRoomImage(IFormFile? image)
        {
            var validationError = RoomImageStorage.GetUploadValidationError(image);
            if (validationError != null)
            {
                ModelState.AddModelError(nameof(RoomFormViewModel.Image), validationError);
            }
        }

        private void DeleteNewUpload(string? imagePath)
        {
            if (imagePath == null ||
                !RoomImageStorage.TryGetPhysicalPath(
                    _environment.WebRootPath,
                    imagePath,
                    out var physicalPath))
            {
                return;
            }

            try
            {
                if (System.IO.File.Exists(physicalPath))
                {
                    System.IO.File.Delete(physicalPath);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(exception, "Could not remove an unused uploaded room image.");
            }
        }

        private void DeletePreviousImageIfUnused(string? imagePath, int roomId)
        {
            if (imagePath == null ||
                _db.Rooms.Any(room =>
                    room.RoomId != roomId &&
                    room.ImagePath == imagePath) ||
                !RoomImageStorage.TryGetPhysicalPath(
                    _environment.WebRootPath,
                    imagePath,
                    out var physicalPath))
            {
                return;
            }

            try
            {
                if (System.IO.File.Exists(physicalPath))
                {
                    System.IO.File.Delete(physicalPath);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    exception,
                    "Could not remove the previous image for room {RoomId}.",
                    roomId);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult DeleteRoom(int id)
        {
            var room = _db.Rooms.FirstOrDefault(existingRoom => existingRoom.RoomId == id);

            if (room == null)
            {
                TempData["RoomErrorMessage"] = "ไม่พบห้องที่เลือก";
                return RedirectToAction(nameof(Rooms));
            }

            if (_db.Bookings.Any(booking => booking.RoomId == id))
            {
                TempData["RoomErrorMessage"] =
                    "ลบห้องนี้ไม่ได้ เพราะมีประวัติการจอง";
                return RedirectToAction(nameof(Rooms));
            }

            _db.Rooms.Remove(room);

            try
            {
                _db.SaveChanges();
            }
            catch (DbUpdateException exception)
            {
                _logger.LogWarning(
                    exception,
                    "Could not delete room {RoomId}; it may have a related booking.",
                    id);
                TempData["RoomErrorMessage"] =
                    "ลบห้องนี้ไม่ได้ เพราะมีข้อมูลอื่นอ้างอิงอยู่";
                return RedirectToAction(nameof(Rooms));
            }

            TempData["RoomSuccessMessage"] = "ลบห้องแล้ว";
            return RedirectToAction(nameof(Rooms));
        }

        private void ValidateRoom(RoomFormViewModel data)
        {
            if (string.IsNullOrWhiteSpace(data.RoomNumber))
            {
                ModelState.AddModelError(
                    nameof(data.RoomNumber),
                    "กรุณากรอกหมายเลขห้อง");
            }

            if (string.IsNullOrWhiteSpace(data.Type))
            {
                ModelState.AddModelError(
                    nameof(data.Type),
                    "กรุณากรอกประเภทห้อง");
            }

            if (string.IsNullOrWhiteSpace(data.Status))
            {
                ModelState.AddModelError(
                    nameof(data.Status),
                    "กรุณาเลือกสถานะห้อง");
            }
            else if (!AllowedRoomStatuses.Contains(
                         data.Status,
                         StringComparer.Ordinal))
            {
                ModelState.AddModelError(
                    nameof(data.Status),
                    "กรุณาเลือกสถานะห้องให้ถูกต้อง");
            }
        }

        // ---------- Promotions ----------

        public async Task<IActionResult> Promotion(CancellationToken ct)
        {
            // โปรที่ตั้งเงื่อนไขแล้วขึ้นก่อน → เปิดอยู่ก่อนปิด → ตามลำดับที่สร้าง
            var promotions = await _db.Promotions.AsNoTracking()
                .OrderBy(p => p.RuleType == null)
                .ThenByDescending(p => p.IsActive)
                .ThenBy(p => p.PromotionId)
                .ToListAsync(ct);
            return View(promotions.Select(PromotionListViewModel.From).ToList());
        }

        public IActionResult AddPromotion()
        {
            return View("PromotionForm", new PromotionFormViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddPromotion(PromotionFormViewModel form, CancellationToken ct)
        {
            ValidatePromotionForm(form);
            if (!ModelState.IsValid)
            {
                return View("PromotionForm", form);
            }

            var lastId = await _db.Promotions.MaxAsync(p => (int?)p.PromotionId, ct) ?? 0;
            var promotion = new Promotion { PromotionId = lastId + 1 };
            CopyFormToPromotion(form, promotion);
            _db.Promotions.Add(promotion);
            await _db.SaveChangesAsync(ct);

            TempData["PromotionSuccessMessage"] = $"เพิ่มโปร \"{promotion.PromotionName}\" แล้ว";
            return RedirectToAction(nameof(Promotion));
        }

        public async Task<IActionResult> EditPromotion(int id, CancellationToken ct)
        {
            var promotion = await _db.Promotions.AsNoTracking()
                .FirstOrDefaultAsync(p => p.PromotionId == id, ct);
            return promotion == null ? NotFound() : View("PromotionForm", CreatePromotionForm(promotion));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditPromotion(PromotionFormViewModel form, CancellationToken ct)
        {
            var promotion = await _db.Promotions.FirstOrDefaultAsync(p => p.PromotionId == form.PromotionId, ct);
            if (promotion == null)
            {
                return NotFound();
            }

            ValidatePromotionForm(form);
            if (!ModelState.IsValid)
            {
                return View("PromotionForm", form);
            }

            CopyFormToPromotion(form, promotion);
            await _db.SaveChangesAsync(ct);

            TempData["PromotionSuccessMessage"] = $"บันทึกโปร \"{promotion.PromotionName}\" แล้ว";
            return RedirectToAction(nameof(Promotion));
        }

        /// <summary>เปิด/ปิดโปรแบบกดปุ่มเดียว</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> TogglePromotion(int id, CancellationToken ct)
        {
            var promotion = await _db.Promotions.FirstOrDefaultAsync(p => p.PromotionId == id, ct);
            if (promotion == null)
            {
                return NotFound();
            }

            promotion.IsActive = !promotion.IsActive;
            await _db.SaveChangesAsync(ct);

            var action = promotion.IsActive ? "เปิด" : "ปิด";
            TempData["PromotionSuccessMessage"] = $"{action}โปร \"{promotion.PromotionName}\" แล้ว";
            return RedirectToAction(nameof(Promotion));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeletePromotion(int id, CancellationToken ct)
        {
            var promotion = await _db.Promotions.FirstOrDefaultAsync(p => p.PromotionId == id, ct);
            if (promotion == null)
            {
                TempData["PromotionErrorMessage"] = "ไม่พบโปรโมชั่นนี้";
                return RedirectToAction(nameof(Promotion));
            }

            // โปรที่เคยถูกใช้กับการจองแล้วลบไม่ได้ (ประวัติการจองจะเสีย) ให้ปิดแทน
            if (await _db.Bookings.AnyAsync(b => b.PromotionId == id, ct))
            {
                TempData["PromotionErrorMessage"] = "โปรนี้เคยถูกใช้กับการจองแล้ว ลบไม่ได้ ให้กดปิดแทน";
                return RedirectToAction(nameof(Promotion));
            }

            _db.Promotions.Remove(promotion);
            await _db.SaveChangesAsync(ct);

            TempData["PromotionSuccessMessage"] = "ลบโปรแล้ว";
            return RedirectToAction(nameof(Promotion));
        }

        private void ValidatePromotionForm(PromotionFormViewModel form)
        {
            if (form.RuleType == null || !PromotionRules.All.ContainsKey(form.RuleType))
            {
                ModelState.AddModelError(nameof(form.RuleType), "กรุณาเลือกประเภทโปร");
            }
            else if (PromotionRules.NeedsValue(form.RuleType) && form.RuleValue is not > 0)
            {
                ModelState.AddModelError(nameof(form.RuleValue), "โปรประเภทนี้ต้องกรอกตัวเลขเงื่อนไข (มากกว่า 0)");
            }

            if (form.DiscountType is not (PromotionRules.Percent or PromotionRules.Fixed))
            {
                ModelState.AddModelError(nameof(form.DiscountType), "กรุณาเลือกลดเป็น % หรือบาท");
            }
            else if (form.DiscountType == PromotionRules.Percent && form.DiscountValue > 100)
            {
                ModelState.AddModelError(nameof(form.DiscountValue), "ลดเป็น % ได้ไม่เกิน 100");
            }

            var start = ParseFormDate(form.StartDate, nameof(form.StartDate));
            var end = ParseFormDate(form.EndDate, nameof(form.EndDate));
            if (start.HasValue && end.HasValue && end < start)
            {
                ModelState.AddModelError(nameof(form.EndDate), "วันสิ้นสุดต้องไม่อยู่ก่อนวันเริ่ม");
            }
        }

        private DateOnly? ParseFormDate(string? value, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var date = ParseIsoDate(value);
            if (date == null)
            {
                ModelState.AddModelError(field, "รูปแบบวันที่ไม่ถูกต้อง");
            }

            return date;
        }

        private static void CopyFormToPromotion(PromotionFormViewModel form, Promotion promotion)
        {
            promotion.PromotionName = form.PromotionName!.Trim();
            promotion.Description = form.Description?.Trim();
            promotion.RuleType = form.RuleType;
            promotion.RuleValue = PromotionRules.NeedsValue(form.RuleType) ? form.RuleValue : null;
            promotion.DiscountType = form.DiscountType;
            promotion.DiscountValue = form.DiscountValue;
            promotion.MaxDiscount = form.DiscountType == PromotionRules.Percent && form.MaxDiscount > 0 ? form.MaxDiscount : null;
            promotion.MinSpend = form.MinSpend > 0 ? form.MinSpend : null;
            promotion.StartDate = ParseIsoDate(form.StartDate);
            promotion.EndDate = ParseIsoDate(form.EndDate);
            promotion.IsActive = form.IsActive;
        }

        // ใช้ InvariantCulture เพราะเครื่องตั้งภาษาไทยจะตีความปีเป็น พ.ศ.
        private static DateOnly? ParseIsoDate(string? value) =>
            DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : null;

        private static PromotionFormViewModel CreatePromotionForm(Promotion promotion) => new()
        {
            PromotionId = promotion.PromotionId,
            PromotionName = promotion.PromotionName,
            Description = promotion.Description,
            RuleType = promotion.RuleType,
            RuleValue = promotion.RuleValue,
            DiscountType = promotion.DiscountType ?? PromotionRules.Percent,
            DiscountValue = promotion.DiscountValue,
            MaxDiscount = promotion.MaxDiscount,
            MinSpend = promotion.MinSpend,
            StartDate = promotion.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            EndDate = promotion.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            IsActive = promotion.IsActive
        };

        public IActionResult Members()
        {
            var userList = _db.Users
                .Select(user => new MemberListViewModel
                {
                    UserId = user.UserId,
                    FirstName = user.FirstName,
                    LastName = user.LastName,
                    Email = user.Email,
                    Phone = user.Phone,
                    Role = user.Role
                })
                .ToList();

            return View(userList);
        }

        public IActionResult EditUser(int id)
        {
            if (id <= 0) return BadRequest();
            var user = _db.Users
                .Where(user => user.UserId == id)
                .Select(user => new EditUserViewModel
                {
                    UserId = user.UserId,
                    FirstName = user.FirstName,
                    LastName = user.LastName,
                    Email = user.Email,
                    Phone = user.Phone,
                    Role = user.Role
                })
                .FirstOrDefault();

            if (user != null)
            {
                return View(user);
            }

            return NotFound();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult EditUser(EditUserViewModel data)
        {
            if (data.UserId <= 0) return BadRequest();
            var user = (from u in _db.Users
                        where u.UserId == data.UserId
                        select u).FirstOrDefault();

            if (user != null)
            {
                if (string.IsNullOrWhiteSpace(data.FirstName))
                    ModelState.AddModelError(nameof(data.FirstName), "กรุณากรอกชื่อ");
                if (string.IsNullOrWhiteSpace(data.LastName))
                    ModelState.AddModelError(nameof(data.LastName), "กรุณากรอกนามสกุล");
                if (string.IsNullOrWhiteSpace(data.Email))
                    ModelState.AddModelError(nameof(data.Email), "กรุณากรอกอีเมล");
                else
                {
                    data.Email = data.Email.Trim();
                    if (!new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(data.Email))
                        ModelState.AddModelError(nameof(data.Email), "กรุณากรอกอีเมลให้ถูกต้อง");
                    else if (_db.Users.Any(u => u.UserId != data.UserId && u.Email == data.Email))
                        ModelState.AddModelError(nameof(data.Email), "อีเมลนี้ถูกใช้งานแล้ว");
                }
                if (data.Role != "Customer" && data.Role != "Staff" &&
                    data.Role != "Housekeeper" && data.Role != "Admin")
                    ModelState.AddModelError(nameof(data.Role), "กรุณาเลือก Role ที่ถูกต้อง");
                if (string.IsNullOrWhiteSpace(data.Phone))
                {
                    ModelState.AddModelError(
                        "Phone",
                        "กรุณากรอกเบอร์โทรศัพท์");
                }
                else
                {
                    var phone = data.Phone.Trim();
                    var digits = new string(
                        phone.Where(char.IsDigit).ToArray());

                    if (phone.Any(c => !char.IsDigit(c) && c != ' '))
                    {
                        ModelState.AddModelError(
                            "Phone",
                            "กรุณากรอกเฉพาะตัวเลข");
                    }
                    else if (digits.Length != 10 ||
                             (!digits.StartsWith("06") &&
                              !digits.StartsWith("08") &&
                              !digits.StartsWith("09")))
                    {
                        ModelState.AddModelError(
                            "Phone",
                            "กรุณากรอกเบอร์มือถือ 10 หลัก");
                    }
                }

                if (!ModelState.IsValid)
                {
                    return View(data);
                }

                user.FirstName = data.FirstName!.Trim();
                user.LastName = data.LastName!.Trim();
                user.Email = data.Email;
                user.Phone = new string(
                    data.Phone!.Where(char.IsDigit).ToArray());

                user.Role = data.Role;

                _db.SaveChanges();

                return RedirectToAction(
                    "Members",
                    new { status = "EditSuccess" });
            }

            return RedirectToAction(
                "Members",
                new { status = "EditFailed" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult DeleteUser(int id)
        {
            if (id <= 0) return BadRequest();
            if (User.FindFirstValue(ClaimTypes.NameIdentifier) == id.ToString())
            {
                TempData["MemberError"] = "ลบบัญชีของตัวเองไม่ได้";
                return RedirectToAction(nameof(Members));
            }
            using var transaction = _db.Database.BeginTransaction(IsolationLevel.Serializable);
            var user = (from u in _db.Users
                        where u.UserId == id
                        select u).FirstOrDefault();

            if (user != null)
            {
                if (_db.Bookings.Any(b => b.UserId == id) ||
                    _db.BookingStays.Any(s => s.CheckedInByUserId == id || s.CheckedOutByUserId == id) ||
                    _db.MaintenanceReports.Any(r => r.ReportedByUserId == id || r.ResolvedByUserId == id) ||
                    _db.MaintenanceReportEvents.Any(e => e.ActorUserId == id) ||
                    _db.HousekeepingTasks.Any(t => t.AssignedToUserId == id || t.CreatedByUserId == id ||
                        t.CompletedByUserId == id || t.VerifiedByUserId == id) ||
                    _db.HousekeepingTaskEvents.Any(e => e.ActorUserId == id || e.PreviousAssignee == id || e.NewAssignee == id))
                {
                    TempData["MemberError"] = "ไม่สามารถลบบัญชีที่มีประวัติการจอง การเข้าพัก งาน หรือรายงานซ่อม";
                    return RedirectToAction(nameof(Members));
                }
                _db.Remove(user);
                try
                {
                    _db.SaveChanges();
                    transaction.Commit();
                }
                catch (DbUpdateException exception)
                {
                    _logger.LogWarning(exception, "Could not delete user {UserId}; related records may exist.", id);
                    TempData["MemberError"] = "ไม่สามารถลบบัญชีได้ เนื่องจากมีข้อมูลที่เกี่ยวข้องหรือเกิดข้อผิดพลาดในการบันทึก";
                    return RedirectToAction(nameof(Members));
                }

                return RedirectToAction(
                    "Members",
                    new { status = "DeleteSuccess" });
            }

            return RedirectToAction(
                "Members",
                new { status = "DeleteFailed" });
        }

        public IActionResult AddUser()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult AddUser(User data)
        {
            if (string.IsNullOrWhiteSpace(data.FirstName) ||
                string.IsNullOrWhiteSpace(data.LastName) ||
                string.IsNullOrWhiteSpace(data.Email) ||
                string.IsNullOrWhiteSpace(data.Password) ||
                data.BirthDate == null ||
                string.IsNullOrWhiteSpace(data.Phone) ||
                string.IsNullOrWhiteSpace(data.Role))
            {
                ModelState.AddModelError(
                    "",
                    "กรุณากรอกข้อมูลให้ครบ");
            }

            if (!string.IsNullOrWhiteSpace(data.Email))
            {
                var email = data.Email.Trim();

                var emailCheck = (from u in _db.Users
                                  where u.Email == email
                                  select u).FirstOrDefault();

                if (emailCheck != null)
                {
                    ModelState.AddModelError(
                        "Email",
                        "อีเมลนี้ถูกใช้งานแล้ว");
                }
            }

            if (!string.IsNullOrWhiteSpace(data.Phone))
            {
                var phone = data.Phone.Trim();
                var digits = new string(
                    phone.Where(char.IsDigit).ToArray());

                if (phone.Any(c => !char.IsDigit(c) && c != ' '))
                {
                    ModelState.AddModelError(
                        "Phone",
                        "กรุณากรอกเฉพาะตัวเลข");
                }
                else if (digits.Length != 10 ||
                         (!digits.StartsWith("06") &&
                          !digits.StartsWith("08") &&
                          !digits.StartsWith("09")))
                {
                    ModelState.AddModelError(
                        "Phone",
                        "กรุณากรอกเบอร์มือถือ 10 หลัก");
                }
            }

            if (string.IsNullOrWhiteSpace(data.Role) ||
                !(data.Role == "Customer" ||
                  data.Role == "Staff" ||
                  data.Role == "Housekeeper" ||
                  data.Role == "Admin"))
            {
                ModelState.AddModelError(
                    "Role",
                    "กรุณาเลือก Role ที่ถูกต้อง");
            }

            if (!ModelState.IsValid)
            {
                return View(data);
            }

            data.FirstName = data.FirstName!.Trim();
            data.LastName = data.LastName!.Trim();
            data.Email = data.Email!.Trim();
            data.Phone = new string(
                data.Phone!.Where(char.IsDigit).ToArray());
            data.IsActive = true;

            var lastUser = _db.Users
                .OrderByDescending(u => u.UserId)
                .FirstOrDefault();

            data.UserId = lastUser == null
                ? 1
                : lastUser.UserId + 1;

            // เข้ารหัส Password ก่อนบันทึกลงฐานข้อมูล
            data.Password = _passwordHasher.HashPassword(
                data,
                data.Password!
            );

            _db.Users.Add(data);
            _db.SaveChanges();

            TempData["SuccessMessage"] = "เพิ่มบัญชีสมาชิกสำเร็จ";
            return RedirectToAction("Members");
        }

        public IActionResult Employees()
        {
            var employees = _db.Users
                .Where(user => user.Role == "Staff" ||
                               user.Role == "Housekeeper")
                .OrderBy(user => user.UserId)
                .Select(user => new EmployeeListViewModel
                {
                    UserId = user.UserId,
                    FirstName = user.FirstName,
                    LastName = user.LastName,
                    Email = user.Email,
                    Phone = user.Phone,
                    Role = user.Role,
                    IsActive = user.IsActive
                })
                .ToList();

            return View(employees);
        }

        public IActionResult EmployeeProfile(int id)
        {
            var employee = _db.Users
                .Where(user => user.UserId == id &&
                               (user.Role == "Staff" ||
                                user.Role == "Housekeeper"))
                .Select(user => new EmployeeProfileViewModel
                {
                    UserId = user.UserId,
                    FirstName = user.FirstName,
                    LastName = user.LastName,
                    Email = user.Email,
                    Phone = user.Phone,
                    BirthDate = user.BirthDate,
                    Role = user.Role,
                    IsActive = user.IsActive
                })
                .FirstOrDefault();

            if (employee == null)
            {
                return NotFound();
            }

            return View(employee);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetEmployeeActive(
            int id, bool? isActive, CancellationToken ct)
        {
            if (!ModelState.IsValid || id <= 0 || !isActive.HasValue) return BadRequest();

            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            if (!await HousekeepingService.LockUserAsync(_db, id, ct)) return NotFound();

            var user = await _db.Users.SingleOrDefaultAsync(u => u.UserId == id, ct);
            if (user == null || (user.Role != "Staff" && user.Role != "Housekeeper")) return NotFound();

            if (!isActive.Value && user.IsActive &&
                await _db.HousekeepingTasks.AnyAsync(t => t.AssignedToUserId == id &&
                    (t.Status == "Accepted" || t.Status == "InProgress"), ct))
            {
                TempData["EmployeeError"] =
                    "ปิดการใช้งานพนักงานคนนี้ไม่ได้ เพราะยังมีงานทำความสะอาดค้างอยู่";
                return RedirectToAction(nameof(EmployeeProfile), new { id });
            }

            user.IsActive = isActive.Value;
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            TempData["EmployeeSuccess"] = isActive.Value ? "เปิดการใช้งานพนักงานแล้ว" : "ปิดการใช้งานพนักงานแล้ว";
            return RedirectToAction(nameof(EmployeeProfile), new { id });
        }

        [HttpGet]
        public async Task<IActionResult> HousekeepingAssignments(
            int? taskId, string? status, int? roomId, int? assigneeId, CancellationToken ct)
        {
            if (!ValidTaskFilters(status, roomId, assigneeId) || taskId is <= 0) return BadRequest();
            if (taskId.HasValue) return RedirectToAction(nameof(HousekeepingDetails), new { id = taskId.Value });
            return View(await _housekeeping.AdminTasks(status, roomId, assigneeId).ToListAsync(ct));
        }

        [HttpGet]
        public IActionResult HousekeepingReassignments(int? taskId = null)
        {
            if (!ModelState.IsValid || taskId is <= 0) return BadRequest();
            return taskId.HasValue
                ? RedirectToAction(nameof(HousekeepingDetails), new { id = taskId.Value })
                : RedirectToAction(nameof(HousekeepingAssignments));
        }

        [HttpGet]
        public async Task<IActionResult> HousekeepingDetails(int id, CancellationToken ct)
        {
            if (!ModelState.IsValid || id <= 0) return BadRequest();
            var task = await _housekeeping.AdminDetailsAsync(id, ct);
            return task == null ? NotFound() : View(task);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AssignHousekeepingTask(
            int id, int assigneeId, string? expectedStatus, int? expectedAssigneeId,
            int? expectedEventId, string? reason, CancellationToken ct)
        {
            if (!ModelState.IsValid || id <= 0 || assigneeId <= 0 || expectedAssigneeId is <= 0 ||
                !expectedEventId.HasValue || expectedEventId < 0 ||
                expectedStatus is not ("Pending" or "Accepted" or "InProgress"))
                return BadRequest();
            if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var actorId) || actorId <= 0)
                return Forbid();

            var error = await _housekeeping.AssignAsync(id, assigneeId, actorId,
                expectedStatus, expectedAssigneeId, expectedEventId.Value, reason, ct);
            if (error == null)
                TempData["HousekeepingSuccess"] = "บันทึกการมอบหมายงานแล้ว";
            else
                TempData["HousekeepingError"] = error;
            return RedirectToAction(nameof(HousekeepingDetails), new { id });
        }

        private bool ValidTaskFilters(string? status, int? roomId, int? assigneeId) =>
            ModelState.IsValid && roomId is not <= 0 && assigneeId is not <= 0 &&
            (string.IsNullOrEmpty(status) || status is "Pending" or "Accepted" or "InProgress" or "Completed" or "Verified");

        [HttpGet]
        public IActionResult MaintenanceReports()
        {
            return RedirectToAction("Index", "Maintenance");
        }

        [HttpGet]
        public IActionResult MaintenanceResolution(int? reportId = null)
        {
            if (reportId is <= 0)
            {
                return BadRequest();
            }

            return reportId.HasValue
                ? RedirectToAction("Details", "Maintenance", new { id = reportId.Value })
                : RedirectToAction("Index", "Maintenance");
        }

        public IActionResult Reports()
        {
            var confirmedPayments = PaymentService.ConfirmedForRevenue(_db.Payments);
            var stayDates = _db.BookingStays
                .Where(stay => stay.Status == "CheckedOut" && stay.CheckedOutAtUtc.HasValue &&
                    stay.CheckedOutAtUtc >= stay.CheckedInAtUtc)
                .Select(stay => new
                {
                    stay.CheckedInAtUtc,
                    stay.CheckedOutAtUtc
                })
                .ToList();

            var hotelTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok");
            var stayNights = stayDates
                .Select(stay =>
                    (TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(stay.CheckedOutAtUtc!.Value, DateTimeKind.Utc), hotelTimeZone).Date -
                     TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(stay.CheckedInAtUtc, DateTimeKind.Utc), hotelTimeZone).Date).Days)
                .ToList();

            var paymentStatusSummaries = _db.Payments
                .GroupBy(payment => payment.PaymentStatus)
                .Select(group => new PaymentStatusSummaryViewModel
                {
                    Status = group.Key,
                    Count = group.Count(),
                    Amount = group.Sum(payment => payment.Amount)
                })
                .OrderBy(summary => summary.Status)
                .ToList();

            var model = new AdminReportsViewModel
            {
                BookingCount = _db.Bookings.Count(b => b.Status != BookingStatuses.Cancelled),
                CancelledBookingCount = _db.Bookings.Count(b => b.Status == BookingStatuses.Cancelled),
                PromotionUsage = _db.Bookings
                    .Where(b => b.Status != BookingStatuses.Cancelled && b.Promotion != null)
                    .GroupBy(b => b.Promotion!.PromotionName)
                    .Select(g => new PromotionUsageViewModel(
                        g.Key ?? "-", g.Count(), g.Sum(b => b.Amount) ?? 0m))
                    .ToList()
                    .OrderByDescending(p => p.BookingCount)
                    .ToList(),
                BookingAmount = _db.Bookings
                    .Where(booking => booking.Status != BookingStatuses.Cancelled)
                    .Sum(booking => booking.Amount),
                CompletedStaysWithValidDates = stayNights.Count,
                AverageActualStayNights = stayNights.Count == 0
                    ? null
                    : (decimal)stayNights.Average(),
                PaymentCount = _db.Payments.Count(),
                Revenue = (confirmedPayments.Sum(p => p.Amount) ?? 0m) -
                    (_db.PaymentRefunds.Where(r => r.Status == "Approved" &&
                        confirmedPayments.Any(payment => payment.PaymentId == r.PaymentId))
                        .Sum(r => (decimal?)r.Amount) ?? 0m),
                PaymentsByStatus = paymentStatusSummaries,
                HousekeepingByStatus = _db.HousekeepingTasks.GroupBy(t => t.Status)
                    .Select(g => new AdminOperationStatusCount(g.Key, g.Count())).ToList(),
                MaintenanceByStatus = _db.MaintenanceReports.GroupBy(r => r.Status)
                    .Select(g => new AdminOperationStatusCount(g.Key, g.Count())).ToList()
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> ExportBookings(CancellationToken ct)
        {
            var rows = await _db.Bookings.AsNoTracking().OrderBy(b => b.BookingId)
                .Select(b => new
                {
                    b.BookingId, b.UserId, b.RoomId, b.CheckIn, b.CheckOut, b.Guests, b.Amount, b.Status,
                    Promotion = b.Promotion == null ? null : b.Promotion.PromotionName
                })
                .ToListAsync(ct);
            return Csv("bookings.csv",
                new[] { "BookingId", "UserId", "RoomId", "CheckIn", "CheckOut", "Guests", "Amount", "Status", "Promotion" },
                rows.Select(b => new[] { Format(b.BookingId), Format(b.UserId), Format(b.RoomId),
                    Format(b.CheckIn), Format(b.CheckOut), Format(b.Guests), Format(b.Amount),
                    b.Status, b.Promotion }));
        }

        [HttpGet]
        public async Task<IActionResult> ExportPayments(CancellationToken ct)
        {
            var rows = await _db.Payments.AsNoTracking().OrderBy(p => p.PaymentId)
                .Select(p => new { p.PaymentId, p.BookingId, p.PaymentDate, p.PaymentMethod, p.PaymentStatus, p.Amount,
                    p.SubmittedAtUtc, p.VerifiedAtUtc, p.LegacyDisposition })
                .ToListAsync(ct);
            return Csv("payments.csv", new[] { "PaymentId", "BookingId", "PaymentDate", "PaymentMethod", "PaymentStatus", "Amount", "VerifiedAtUtc", "LegacyDisposition" },
                rows.Select(p => new[] { Format(p.PaymentId), Format(p.BookingId), Format(p.PaymentDate),
                    p.PaymentMethod, p.PaymentStatus, Format(p.Amount), Format(p.VerifiedAtUtc),
                    p.SubmittedAtUtc == null ? p.LegacyDisposition ?? "Requires reconciliation" : null }));
        }

        [HttpGet]
        public async Task<IActionResult> ExportMaintenanceReports(CancellationToken ct)
        {
            var rows = await _db.MaintenanceReports.AsNoTracking().OrderBy(r => r.ReportId)
                .Select(r => new { r.ReportId, r.RoomId, r.Description, r.Status, r.BlocksRoom,
                    r.BlockFrom, r.BlockUntil, r.ReportedAtUtc, r.ReportedByUserId, r.ResolvedAtUtc,
                    r.ResolvedByUserId, r.ResolutionNote }).ToListAsync(ct);
            return Csv("maintenance-reports.csv",
                new[] { "ReportId", "RoomId", "Description", "Status", "BlocksRoom", "BlockFrom", "BlockUntilExclusive",
                    "ReportedAtUtc", "ReportedByUserId", "ResolvedAtUtc", "ResolvedByUserId", "ResolutionNote" },
                rows.Select(r => new[] { Format(r.ReportId), Format(r.RoomId), r.Description, r.Status,
                    Format(r.BlocksRoom), Format(r.BlockFrom), Format(r.BlockUntil), Format(r.ReportedAtUtc),
                    Format(r.ReportedByUserId), Format(r.ResolvedAtUtc), Format(r.ResolvedByUserId), r.ResolutionNote }));
        }

        [HttpGet]
        public async Task<IActionResult> ExportHousekeepingTasks(string? status, int? roomId, int? assigneeId, CancellationToken ct)
        {
            if (!ValidTaskFilters(status, roomId, assigneeId)) return BadRequest();
            var rows = await _housekeeping.AdminTasks(status, roomId, assigneeId).ToListAsync(ct);
            return Csv("housekeeping-tasks.csv",
                new[] { "TaskId", "RoomId", "RoomNumber", "TaskType", "Status", "AssignedToUserId", "CreatedAtUtc" },
                rows.Select(t => new[] { Format(t.TaskId), Format(t.RoomId), t.RoomNumber, t.TaskType,
                    t.Status, Format(t.AssignedToUserId), Format(t.CreatedAtUtc) }));
        }

        private FileContentResult Csv(string filename, string[] headers, IEnumerable<string?[]> rows)
        {
            var builder = new StringBuilder();
            void AppendRow(IEnumerable<string?> values) =>
                builder.AppendJoin(",", values.Select(EscapeCsv)).Append("\r\n");
            AppendRow(headers);
            foreach (var row in rows) AppendRow(row);
            var encoding = new UTF8Encoding(true);
            var content = encoding.GetPreamble().Concat(encoding.GetBytes(builder.ToString())).ToArray();
            return File(content, "text/csv; charset=utf-8", filename);
        }

        private static string EscapeCsv(string? value)
        {
            value ??= string.Empty;
            var trimmed = value.TrimStart();
            if (value.StartsWith('\t') || value.StartsWith('\r') || value.StartsWith('\n') ||
                (trimmed.Length > 0 && "=+-@".Contains(trimmed[0])))
                value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string? Format(object? value) => value switch
        {
            null => null,
            bool boolean => boolean ? "true" : "false",
            DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => throw new ArgumentException("Unsupported CSV value.", nameof(value))
        };

    }
}
