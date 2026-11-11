
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using GoodHotel.Models;
using GoodHotel.Models.ViewModels;
using GoodHotel.Services;

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
        private readonly PromotionMetadataService _promotionMetadata;
        private readonly PromotionManagementService _promotionManagement;

        public AdminController(
            GoodHotelDbContext db,
            IPasswordHasher<User> passwordHasher,
            ILogger<AdminController> logger,
            IWebHostEnvironment environment,
            PromotionMetadataService promotionMetadata,
            PromotionManagementService promotionManagement)
        {
            _db = db;
            _passwordHasher = passwordHasher;
            _logger = logger;
            _environment = environment;
            _promotionMetadata = promotionMetadata;
            _promotionManagement = promotionManagement;
        }

        public IActionResult Dashboard()
        {
            var model = new AdminDashboardViewModel
            {
                RoomCount = _db.Rooms.Count(),
                UserCount = _db.Users.Count(),
                BookingCount = _db.Bookings.Count(),
                RecordedPaymentAmount = _db.Payments.Sum(payment => payment.Amount),
                RoomStatusCounts = _db.Rooms
                    .GroupBy(room => room.Status)
                    .Select(group => new RoomStatusCountViewModel
                    {
                        Status = group.Key,
                        Count = group.Count()
                    })
                    .OrderBy(item => item.Status)
                    .ToList(),
                BookingsByCheckInDate = _db.Bookings
                    .Where(booking => booking.CheckIn.HasValue)
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

        public IActionResult Rooms()
        {
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
                    "A room with this number already exists.");
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
                    "The image could not be uploaded. Please try again.");
                return View("RoomForm", data);
            }
            catch (DbUpdateException exception)
            {
                _logger.LogError(exception, "Could not save a new room.");
                DeleteNewUpload(uploadedImagePath);
                ModelState.AddModelError(
                    "",
                    "The room could not be saved. Please try again.");
                return View("RoomForm", data);
            }

            TempData["RoomSuccessMessage"] = "Room added successfully.";
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
                    "A room with this number already exists.");
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

                room.Room1 = data.RoomNumber;
                room.Type = data.Type;
                room.Description = data.Description;
                room.Capacity = data.Capacity;
                room.Status = data.Status;
                room.Rate = data.Rate;
                if (uploadedImagePath != null)
                {
                    room.ImagePath = uploadedImagePath;
                }

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
                _logger.LogError(exception, "Could not update the room image.");
                DeleteNewUpload(uploadedImagePath);
                ModelState.AddModelError(
                    nameof(data.Image),
                    "The image could not be uploaded. Please try again.");
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
                    "The room could not be updated. Please try again.");
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

            TempData["RoomSuccessMessage"] = "Room updated successfully.";
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
                TempData["RoomErrorMessage"] = "The selected room was not found.";
                return RedirectToAction(nameof(Rooms));
            }

            if (_db.Bookings.Any(booking => booking.RoomId == id))
            {
                TempData["RoomErrorMessage"] =
                    "This room cannot be deleted because it has booking records.";
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
                    "This room could not be deleted because it is referenced by another record.";
                return RedirectToAction(nameof(Rooms));
            }

            TempData["RoomSuccessMessage"] = "Room deleted successfully.";
            return RedirectToAction(nameof(Rooms));
        }

        private void ValidateRoom(RoomFormViewModel data)
        {
            if (string.IsNullOrWhiteSpace(data.RoomNumber))
            {
                ModelState.AddModelError(
                    nameof(data.RoomNumber),
                    "Room number is required.");
            }

            if (string.IsNullOrWhiteSpace(data.Type))
            {
                ModelState.AddModelError(
                    nameof(data.Type),
                    "Room type is required.");
            }

            if (string.IsNullOrWhiteSpace(data.Status))
            {
                ModelState.AddModelError(
                    nameof(data.Status),
                    "Room status is required.");
            }
            else if (!AllowedRoomStatuses.Contains(
                         data.Status,
                         StringComparer.Ordinal))
            {
                ModelState.AddModelError(
                    nameof(data.Status),
                    "Select a valid room status.");
            }
        }

        public async Task<IActionResult> Promotion(
            CancellationToken cancellationToken)
        {
            var entities = await _db.Promotions
                .AsNoTracking()
                .OrderBy(promotion => promotion.PromotionId)
                .ToListAsync(cancellationToken);

            var promotions = entities.Select(promotion =>
            {
                var parsed = _promotionMetadata.Parse(promotion.Condition);
                return new PromotionListViewModel
                {
                    PromotionId = promotion.PromotionId,
                    PromotionName = promotion.PromotionName,
                    Description = promotion.Description,
                    Condition = parsed.IsLegacy || parsed.Error != null
                        ? promotion.Condition
                        : null,
                    Benefit = promotion.Benefit,
                    PromotionCode = parsed.Metadata?.Code,
                    DiscountType = parsed.Metadata?.DiscountType,
                    DiscountValue = parsed.Metadata?.DiscountValue,
                    StartDate = parsed.Metadata?.StartDate,
                    EndDate = parsed.Metadata?.EndDate,
                    IsActive = parsed.Metadata?.IsActive,
                    IsLegacyPromotion = parsed.IsLegacy,
                    MetadataError = parsed.Error
                };
            }).ToList();

            return View(promotions);
        }

        public IActionResult AddPromotion()
        {
            return View("PromotionForm", new PromotionFormViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddPromotion(
            PromotionFormViewModel data,
            CancellationToken cancellationToken)
        {
            ValidatePromotionForm(data);

            if (!ModelState.IsValid)
            {
                return View("PromotionForm", data);
            }

            var result = await _promotionManagement.SaveAsync(data, cancellationToken);
            if (result.Status != PromotionWriteStatus.Saved)
            {
                ModelState.AddModelError(string.Empty, result.Message ?? "Could not save promotion.");
                return View("PromotionForm", data);
            }

            TempData["PromotionSuccessMessage"] = "Promotion added successfully.";
            return RedirectToAction(nameof(Promotion));
        }

        public async Task<IActionResult> EditPromotion(
            int id,
            CancellationToken cancellationToken)
        {
            var promotion = await _db.Promotions
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    item => item.PromotionId == id,
                    cancellationToken);
            return promotion == null
                ? NotFound()
                : View("PromotionForm", CreatePromotionForm(promotion));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditPromotion(
            PromotionFormViewModel data,
            CancellationToken cancellationToken)
        {
            if (data.PromotionId is null or <= 0)
            {
                return NotFound();
            }

            ValidatePromotionForm(data);

            if (!ModelState.IsValid)
            {
                await PopulateLegacyConditionAsync(data, cancellationToken);
                return View("PromotionForm", data);
            }

            var result = await _promotionManagement.SaveAsync(data, cancellationToken);
            if (result.Status == PromotionWriteStatus.NotFound)
            {
                return NotFound();
            }
            if (result.Status != PromotionWriteStatus.Saved)
            {
                ModelState.AddModelError(string.Empty, result.Message ?? "Could not update promotion.");
                await PopulateLegacyConditionAsync(data, cancellationToken);
                return View("PromotionForm", data);
            }

            TempData["PromotionSuccessMessage"] = "Promotion updated successfully.";
            return RedirectToAction(nameof(Promotion));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult DeletePromotion(int id)
        {
            var promotion = _db.Promotions
                .FirstOrDefault(item => item.PromotionId == id);

            if (promotion == null)
            {
                TempData["PromotionErrorMessage"] =
                    "The selected promotion was not found.";
                return RedirectToAction(nameof(Promotion));
            }

            if (_db.Bookings.Any(booking => booking.PromotionId == id))
            {
                TempData["PromotionErrorMessage"] =
                    "This promotion cannot be deleted because it has booking records.";
                return RedirectToAction(nameof(Promotion));
            }

            _db.Promotions.Remove(promotion);

            try
            {
                _db.SaveChanges();
            }
            catch (DbUpdateException exception)
            {
                _logger.LogWarning(
                    exception,
                    "Could not delete promotion {PromotionId}; it may have a related booking.",
                    id);
                TempData["PromotionErrorMessage"] =
                    "This promotion could not be deleted because it is referenced by a booking.";
                return RedirectToAction(nameof(Promotion));
            }

            TempData["PromotionSuccessMessage"] = "Promotion deleted successfully.";
            return RedirectToAction(nameof(Promotion));
        }

        private void ValidatePromotionForm(PromotionFormViewModel data)
        {
            if (string.IsNullOrWhiteSpace(data.PromotionName))
            {
                ModelState.AddModelError(
                    nameof(data.PromotionName),
                    "Promotion name is required.");
            }

            if (data.UseDiscountCode)
            {
                if (!_promotionMetadata.TryParseDate(data.StartDate, out var startDate))
                {
                    ModelState.AddModelError(
                        nameof(data.StartDate),
                        "กรุณาระบุวันเริ่มต้นในรูปแบบวันที่ที่ถูกต้อง");
                }

                if (!_promotionMetadata.TryParseDate(data.EndDate, out var endDate))
                {
                    ModelState.AddModelError(
                        nameof(data.EndDate),
                        "กรุณาระบุวันสิ้นสุดในรูปแบบวันที่ที่ถูกต้อง");
                }

                if (data.DiscountValue == null)
                {
                    ModelState.AddModelError(
                        nameof(data.DiscountValue),
                        "กรุณาระบุมูลค่าส่วนลด");
                }
                else if (_promotionMetadata.TryParseDate(data.StartDate, out startDate) &&
                         _promotionMetadata.TryParseDate(data.EndDate, out endDate))
                {
                    var error = _promotionMetadata.Validate(
                        new PromotionMetadata(
                            data.PromotionCode ?? string.Empty,
                            data.DiscountType ?? string.Empty,
                            data.DiscountValue.Value,
                            startDate,
                            endDate,
                            data.IsActive),
                        out _);
                    if (error != null)
                    {
                        ModelState.AddModelError(string.Empty, error);
                    }
                }
            }

            if (data.Benefit?.Length > 255)
            {
                ModelState.AddModelError(
                    nameof(data.Benefit),
                    "Benefit ต้องมีความยาวไม่เกิน 255 ตัวอักษร");
            }
        }

        private PromotionFormViewModel CreatePromotionForm(Promotion promotion)
        {
            var form = new PromotionFormViewModel
            {
                PromotionId = promotion.PromotionId,
                PromotionName = promotion.PromotionName,
                Description = promotion.Description,
                Benefit = promotion.Benefit
            };
            var parsed = _promotionMetadata.Parse(promotion.Condition);
            if (parsed.IsLegacy)
            {
                form.UseDiscountCode = false;
                form.IsLegacyPromotion = true;
                form.LegacyCondition = promotion.Condition;
            }
            else if (parsed.Metadata != null)
            {
                form.UseDiscountCode = true;
                form.PromotionCode = parsed.Metadata.Code;
                form.DiscountType = parsed.Metadata.DiscountType;
                form.DiscountValue = parsed.Metadata.DiscountValue;
                form.StartDate = parsed.Metadata.StartDate.ToString(
                    "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture);
                form.EndDate = parsed.Metadata.EndDate.ToString(
                    "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture);
                form.IsActive = parsed.Metadata.IsActive;
            }
            else
            {
                form.UseDiscountCode = false;
                form.MetadataError = parsed.Error;
            }

            return form;
        }

        private async Task PopulateLegacyConditionAsync(
            PromotionFormViewModel form,
            CancellationToken cancellationToken)
        {
            var promotion = await _db.Promotions
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    item => item.PromotionId == form.PromotionId,
                    cancellationToken);
            if (promotion == null)
            {
                return;
            }

            var parsed = _promotionMetadata.Parse(promotion.Condition);
            if (parsed.IsLegacy)
            {
                form.IsLegacyPromotion = true;
                form.LegacyCondition = promotion.Condition;
            }
            else if (parsed.Error != null)
            {
                form.MetadataError = parsed.Error;
            }
        }

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
            var user = (from u in _db.Users
                        where u.UserId == data.UserId
                        select u).FirstOrDefault();

            if (user != null)
            {
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

                user.FirstName = data.FirstName;
                user.LastName = data.LastName;
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
            var user = (from u in _db.Users
                        where u.UserId == id
                        select u).FirstOrDefault();

            if (user != null)
            {
                _db.Remove(user);
                _db.SaveChanges();

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
                    Role = user.Role
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
                    Role = user.Role
                })
                .FirstOrDefault();

            if (employee == null)
            {
                return NotFound();
            }

            return View(employee);
        }

        [HttpGet]
        public IActionResult HousekeepingAssignments(int? taskId = null)
        {
            if (taskId is <= 0)
            {
                return BadRequest();
            }

            return OperationsContextPage(
                taskId.HasValue ? "Assign Housekeeping Task" : "Housekeeping Task Assignment",
                taskId.HasValue
                    ? $"Task #{taskId.Value}: assignment data has not been verified."
                    : "Review unassigned housekeeping work. Task records are not loaded.",
                taskId,
                null,
                new OperationsSectionViewModel
                {
                    Title = "Assign task",
                    Description = "Assignment is unavailable until housekeeping persistence is implemented.",
                    Fields = new[]
                    {
                        "Task",
                        "Housekeeper"
                    },
                    ActionLabel = "Assign task (not implemented)",
                    DisabledActionMessage = "Assignment is disabled until task persistence and authorization checks are implemented.",
                    EmptyState = taskId.HasValue
                        ? null
                        : "Task records have not been queried. Whether unassigned tasks exist is not known."
                });
        }

        [HttpGet]
        public IActionResult HousekeepingReassignments(int? taskId = null)
        {
            if (taskId is <= 0)
            {
                return BadRequest();
            }

            return OperationsContextPage(
                taskId.HasValue ? "Reassign Housekeeping Task" : "Housekeeping Task Reassignment",
                taskId.HasValue
                    ? $"Task #{taskId.Value}: assignment data has not been verified."
                    : "Review assigned housekeeping work. Task records are not loaded.",
                taskId,
                null,
                new OperationsSectionViewModel
                {
                    Title = "Reassign task",
                    Description = "Reassignment is unavailable until housekeeping persistence is implemented.",
                    Fields = new[]
                    {
                        "Task",
                        "Current housekeeper",
                        "New housekeeper"
                    },
                    ActionLabel = "Reassign task (not implemented)",
                    DisabledActionMessage = "Reassignment is disabled until task persistence and authorization checks are implemented.",
                    EmptyState = taskId.HasValue
                        ? null
                        : "Task records have not been queried. Whether assigned tasks exist is not known."
                });
        }

        [HttpGet]
        public IActionResult MaintenanceReports()
        {
            return OperationsPage(
                "Maintenance Reports",
                "Review room issues and approve report resolution.",
                new OperationsSectionViewModel
                {
                    Title = "Open reports",
                    Description = "Maintenance reports are not loaded.",
                    Fields = new[]
                    {
                        "Room",
                        "Issue details",
                        "Room blocked",
                        "Reported by",
                        "Reported at"
                    },
                    EmptyState = "Maintenance reports have not been queried. Whether open reports exist is not known."
                },
                new OperationsSectionViewModel
                {
                    Title = "Report history",
                    Description = "Resolved report history is not loaded.",
                    EmptyState = "Maintenance history has not been queried."
                });
        }

        [HttpGet]
        public IActionResult MaintenanceResolution(int? reportId = null)
        {
            if (reportId is <= 0)
            {
                return BadRequest();
            }

            return OperationsContextPage(
                reportId.HasValue ? "Resolve Maintenance Report" : "Maintenance Resolution",
                reportId.HasValue
                    ? $"Report #{reportId.Value}: report data has not been verified."
                    : "Review blocking maintenance reports before approving resolution.",
                null,
                reportId,
                new OperationsSectionViewModel
                {
                    Title = "Resolution review",
                    Description = "Only Admin can approve release of a room block.",
                    Fields = new[]
                    {
                        "Room",
                        "Issue details",
                        "Blocking status",
                        "Reported by",
                        "Resolution notes"
                    },
                    ActionLabel = "Resolve report (not implemented)",
                    DisabledActionMessage = "Resolution is disabled until maintenance persistence and server-side authorization are implemented.",
                    EmptyState = reportId.HasValue
                        ? null
                        : "Maintenance reports have not been queried. Whether blocking reports exist is not known."
                });
        }

        public IActionResult Reports()
        {
            var stayDates = _db.Bookings
                .Where(booking =>
                    booking.CheckIn.HasValue &&
                    booking.CheckOut.HasValue &&
                    booking.CheckOut > booking.CheckIn)
                .Select(booking => new
                {
                    booking.CheckIn,
                    booking.CheckOut
                })
                .ToList();

            var stayNights = stayDates
                .Select(stay => stay.CheckOut!.Value.DayNumber -
                                stay.CheckIn!.Value.DayNumber)
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
                BookingCount = _db.Bookings.Count(),
                BookingAmount = _db.Bookings.Sum(booking => booking.Amount),
                BookingsWithValidStayDates = stayNights.Count,
                AverageStayNights = stayNights.Count == 0
                    ? null
                    : (decimal)stayNights.Average(),
                PaymentCount = _db.Payments.Count(),
                RecordedPaymentAmount = _db.Payments.Sum(payment => payment.Amount),
                PaymentsByStatus = paymentStatusSummaries
            };

            return View(model);
        }

        private ViewResult OperationsPage(
            string title,
            string description,
            params OperationsSectionViewModel[] sections)
        {
            return OperationsContextPage(title, description, null, null, sections);
        }

        private ViewResult OperationsContextPage(
            string title,
            string description,
            int? taskId = null,
            int? reportId = null,
            params OperationsSectionViewModel[] sections)
        {
            return View(
                "~/Views/Shared/OperationsPage.cshtml",
                new OperationsPageViewModel
                {
                    Title = title,
                    Description = description,
                    BackController = "Admin",
                    BackAction = nameof(Dashboard),
                    BackLabel = "Admin Dashboard",
                    Sections = sections,
                    TaskId = taskId,
                    ReportId = reportId
                });
        }
    }
}
