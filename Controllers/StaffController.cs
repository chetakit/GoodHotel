
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GoodHotel.Models;
using GoodHotel.Models.ViewModels;
using GoodHotel.Services;
using System.Security.Claims;

namespace GoodHotel.Controllers
{
    [Authorize(Roles = "Staff")]
    public class StaffController : Controller
    {
        private readonly GoodHotelDbContext _db;
        private readonly StayService _stays;
        private readonly HousekeepingService _housekeeping;
        private readonly RoomStatusService _roomStatus;
        private readonly RoomAvailabilityService _availability;
        private readonly WalkInService _walkIn;
        private readonly BookingCancellationService _cancellation;

        public StaffController(
            GoodHotelDbContext db,
            StayService stays,
            HousekeepingService housekeeping,
            RoomStatusService roomStatus,
            RoomAvailabilityService availability,
            WalkInService walkIn,
            BookingCancellationService cancellation)
        {
            _cancellation = cancellation;
            _db = db;
            _stays = stays;
            _housekeeping = housekeeping;
            _roomStatus = roomStatus;
            _availability = availability;
            _walkIn = walkIn;
        }

        public sealed record BookingListItem(
            int BookingId,
            string? FirstName,
            string? LastName,
            string? RoomNumber,
            DateOnly? CheckIn,
            DateOnly? CheckOut,
            int? Guests,
            decimal? Amount,
            string BookingStatus,
            string? StayStatus,
            string? CancelReason,
            DateTime? CreatedAtUtc)
        {
            public string PaymentLabel { get; init; } = "";
        }

        public sealed record BookingDetailsModel(
            int BookingId,
            string? FirstName,
            string? LastName,
            string? RoomNumber,
            DateOnly? CheckIn,
            DateOnly? CheckOut,
            int? Guests,
            int? ExtraBed,
            int? PromotionId,
            string? PromotionName,
            decimal? Amount,
            string? StayStatus,
            string BookingStatus)
        {
            public string PaymentLabel { get; init; } = "";
        }

        public sealed record GuestDetailsModel(
            int BookingId,
            bool HasGuest,
            string? FirstName,
            string? LastName,
            string? Email,
            string? Phone,
            DateOnly? BirthDate);

        [HttpGet]
        public async Task<IActionResult> Index(CancellationToken cancellationToken)
        {
            var (start, end) = _stays.HotelDayUtcBounds();
            // นับทุกตัวเลขใน query เดียว (แต่ละ query ไปฐานข้อมูลใช้ ~200ms)
            // อาศัยแถวของผู้ใช้ที่ล็อกอินอยู่เป็นตัวยึด เพราะแถวนี้มีอยู่แน่นอน
            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var counts = await _db.Users.Where(u => u.UserId == userId).Select(_ => new
            {
                Bookings = _db.Bookings.Count(b => b.Status != BookingStatuses.Cancelled),
                Staying = _db.BookingStays.Count(s => s.Status == "CheckedIn"),
                CheckedInToday = _db.BookingStays.Count(s => s.CheckedInAtUtc >= start && s.CheckedInAtUtc < end),
                CheckedOutToday = _db.BookingStays.Count(s => s.CheckedOutAtUtc >= start && s.CheckedOutAtUtc < end),
                ToVerify = _db.HousekeepingTasks.Count(t => t.Status == "Completed"),
                OpenMaintenance = _db.MaintenanceReports.Count(r => r.Status != MaintenanceReport.Resolved)
            }).SingleAsync(cancellationToken);
            var model = new StaffDashboardViewModel(counts.Bookings, counts.Staying, counts.CheckedInToday,
                counts.CheckedOutToday, counts.ToVerify, counts.OpenMaintenance);

            var today = _availability.HotelToday();

            // แขกที่รอเช็คอิน = ไม่ได้ยกเลิก ยังไม่เช็คอิน และวันนี้อยู่ในช่วงที่จองไว้
            // รวมแขกที่มาช้ากว่าวันเข้าพัก (ระบบยังให้เช็คอินได้จนถึงวันออก) ไม่งั้นจะหายไปจากหน้านี้
            var arrivals = await _db.Bookings.AsNoTracking()
                .Where(b => b.Status != BookingStatuses.Cancelled && b.CheckIn <= today && b.CheckOut > today &&
                    !_db.BookingStays.Any(s => s.BookingId == b.BookingId))
                .OrderBy(b => b.Room!.Room1)
                .Select(FrontDeskRowProjection)
                .ToListAsync(cancellationToken);
            var arrivalLabels = await PaymentService.PaymentLabelsAsync(
                _db, arrivals.Select(a => a.BookingId).ToList(), cancellationToken);
            var arrivalsWithPayment = arrivals
                .Select(row => row with { IsPaid = arrivalLabels[row.BookingId] == PaymentService.PaidLabel })
                .ToList();

            // แขกที่ต้องออกวันนี้ = ยังพักอยู่ และวันออกคือวันนี้หรือเลยมาแล้ว
            var departures = await _db.BookingStays.AsNoTracking()
                .Where(s => s.Status == "CheckedIn" && s.Booking.CheckOut <= today)
                .OrderBy(s => s.Booking.Room!.Room1)
                .Select(s => s.Booking).Select(FrontDeskRowProjection)
                .ToListAsync(cancellationToken);

            ViewBag.Today = today;
            return View(model with { Arrivals = arrivalsWithPayment, Departures = departures });
        }

        // แปลง Booking เป็นแถวหน้าแดชบอร์ด (เขียนเป็น Expression ให้ EF แปลงเป็น SQL ได้)
        private static readonly System.Linq.Expressions.Expression<Func<Booking, FrontDeskRow>> FrontDeskRowProjection = b => new FrontDeskRow(
            b.BookingId,
            b.User == null ? null : b.User.FirstName,
            b.User == null ? null : b.User.LastName,
            b.User == null ? null : b.User.Phone,
            b.Room == null ? null : b.Room.Room1,
            b.CheckIn, b.CheckOut, b.Guests);

        [HttpGet]
        public async Task<IActionResult> Bookings(
            string? q,
            string? when,
            CancellationToken cancellationToken)
        {
            var query = _db.Bookings.AsNoTracking();

            // ช่องค้นหาเดียว: พิมพ์เลขที่การจอง ชื่อ นามสกุล หรือเบอร์โทรก็ได้
            // ขึ้นต้นด้วย # (เช่น #12) = ค้นเฉพาะเลขที่การจอง
            var keyword = q?.Trim();
            if (keyword?.StartsWith('#') == true)
            {
                query = int.TryParse(keyword[1..], out var id)
                    ? query.Where(b => b.BookingId == id)
                    : query.Where(b => false);
            }
            else if (!string.IsNullOrEmpty(keyword))
            {
                var isNumber = int.TryParse(keyword, out var number);
                query = query.Where(b =>
                    (isNumber && b.BookingId == number) ||
                    (b.User != null && (
                        (b.User.FirstName + " " + b.User.LastName).Contains(keyword) ||
                        (b.User.Phone != null && b.User.Phone.Contains(keyword)))));
            }

            // ตัวกรองช่วงเวลา: today = เข้าหรือออกวันนี้, upcoming = ยังไม่ถึงวันเข้าพัก, staying = พักอยู่ตอนนี้
            var today = _availability.HotelToday();
            query = when switch
            {
                "today" => query.Where(b => b.CheckIn == today || b.CheckOut == today),
                "upcoming" => query.Where(b => b.Status != BookingStatuses.Cancelled && b.CheckIn > today),
                "staying" => query.Where(b => _db.BookingStays.Any(s => s.BookingId == b.BookingId && s.Status == "CheckedIn")),
                _ => query
            };

            var bookings = await query
                .OrderByDescending(booking => booking.CheckIn)
                .ThenByDescending(booking => booking.BookingId)
                .Select(booking => new BookingListItem(
                    booking.BookingId,
                    booking.User == null ? null : booking.User.FirstName,
                    booking.User == null ? null : booking.User.LastName,
                    booking.Room == null ? null : booking.Room.Room1,
                    booking.CheckIn,
                    booking.CheckOut,
                    booking.Guests,
                    booking.Amount,
                    booking.Status,
                    _db.BookingStays.Where(s => s.BookingId == booking.BookingId)
                        .Select(s => s.Status).FirstOrDefault(),
                    booking.CancelReason,
                    booking.CreatedAtUtc))
                .ToListAsync(cancellationToken);

            // เติมสถานะการจ่ายเงินให้ทุกแถวด้วย query เดียว
            var labels = await PaymentService.PaymentLabelsAsync(
                _db, bookings.Select(b => b.BookingId).ToList(), cancellationToken);

            return View(bookings.Select(b => b with { PaymentLabel = labels[b.BookingId] }).ToList());
        }

        [HttpGet]
        public async Task<IActionResult> BookingDetails(int id, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid || id <= 0)
            {
                return BadRequest();
            }

            var booking = await _db.Bookings
                .AsNoTracking()
                .Where(item => item.BookingId == id)
                .Select(item => new BookingDetailsModel(
                    item.BookingId,
                    item.User == null ? null : item.User.FirstName,
                    item.User == null ? null : item.User.LastName,
                    item.Room == null ? null : item.Room.Room1,
                    item.CheckIn,
                    item.CheckOut,
                    item.Guests,
                    item.ExtraBed,
                    item.PromotionId,
                    item.Promotion == null ? null : item.Promotion.PromotionName,
                    item.Amount,
                    _db.BookingStays.Where(s => s.BookingId == item.BookingId)
                        .Select(s => s.Status).SingleOrDefault(),
                    item.Status))
                .SingleOrDefaultAsync(cancellationToken);

            if (booking == null)
            {
                return NotFound();
            }

            return View(booking with
            {
                PaymentLabel = await PaymentService.PaymentLabelAsync(_db, booking.BookingId, cancellationToken)
            });
        }

        [HttpGet]
        public async Task<IActionResult> GuestDetails(int bookingId, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid || bookingId <= 0)
            {
                return BadRequest();
            }

            var guest = await _db.Bookings
                .AsNoTracking()
                .Where(item => item.BookingId == bookingId)
                .Select(item => new GuestDetailsModel(
                    item.BookingId,
                    item.User != null,
                    item.User == null ? null : item.User.FirstName,
                    item.User == null ? null : item.User.LastName,
                    item.User == null ? null : item.User.Email,
                    item.User == null ? null : item.User.Phone,
                    item.User == null ? null : item.User.BirthDate))
                .SingleOrDefaultAsync(cancellationToken);

            return guest == null ? NotFound() : View(guest);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CheckIn(int bookingId, string? note, bool fromDashboard, CancellationToken ct)
        {
            if (!ModelState.IsValid || bookingId <= 0) return BadRequest();
            var actorId = await GetActorIdAsync(ct);
            if (!actorId.HasValue) return Forbid();
            var error = await _stays.CheckInAsync(bookingId, actorId.Value, note, ct);
            TempData[error == null ? "StaffSuccess" : "StaffError"] = error ?? "เช็คอินเรียบร้อย";
            return BackToDeskOrBooking(fromDashboard, bookingId);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelBooking(int bookingId, CancellationToken ct)
        {
            if (bookingId <= 0) return BadRequest();
            var result = await _cancellation.CancelByStaffAsync(bookingId, ct);
            TempData[result.Succeeded ? "StaffSuccess" : "StaffError"] = result.Message;
            return RedirectToAction(nameof(BookingDetails), new { id = bookingId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CheckOut(int bookingId, bool fromDashboard, CancellationToken ct)
        {
            if (!ModelState.IsValid || bookingId <= 0) return BadRequest();
            var actorId = await GetActorIdAsync(ct);
            if (!actorId.HasValue) return Forbid();
            var error = await _stays.CheckOutAsync(bookingId, actorId.Value, ct);
            TempData[error == null ? "StaffSuccess" : "StaffError"] = error ?? "เช็คเอาท์เรียบร้อย ส่งงานทำความสะอาดให้แม่บ้านแล้ว";
            return BackToDeskOrBooking(fromDashboard, bookingId);
        }

        /// <summary>กดจากแดชบอร์ด → กลับแดชบอร์ด, กดจากหน้ารายละเอียด → กลับหน้ารายละเอียด</summary>
        private IActionResult BackToDeskOrBooking(bool fromDashboard, int bookingId) =>
            fromDashboard
                ? RedirectToAction(nameof(Index))
                : RedirectToAction(nameof(BookingDetails), new { id = bookingId });

        // ---------- สถานะห้องพัก ----------

        [HttpGet]
        public async Task<IActionResult> RoomStatus(CancellationToken ct)
        {
            ViewBag.Today = _availability.HotelToday();
            return View(await _roomStatus.GetRoomBoardAsync(ct));
        }

        // ---------- ลูกค้า Walk-in ----------

        [HttpGet]
        public async Task<IActionResult> WalkIn(DateOnly? checkOut, int? guests, CancellationToken ct)
        {
            var today = _availability.HotelToday();
            var form = new WalkInViewModel
            {
                CheckOut = checkOut ?? today.AddDays(1),
                Guests = guests ?? 1
            };
            return View(await FillWalkInRoomsAsync(form, ct));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> WalkIn(WalkInViewModel form, CancellationToken ct)
        {
            // ค่าในฟอร์ม POST ถูกแปลงตาม culture ของเครื่อง (ภาษาไทย = ปี พ.ศ.) จึงแปลงวันที่ ค.ศ. เอง
            ModelState.Remove(nameof(form.CheckOut));
            form.CheckOut = DateOnly.TryParseExact(Request.Form["CheckOut"], "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var checkOut) ? checkOut : null;

            if (form.CheckOut == null)
            {
                ModelState.AddModelError(nameof(form.CheckOut), "กรุณาเลือกวันที่ออก");
            }
            else if (form.CheckOut <= _availability.HotelToday())
            {
                ModelState.AddModelError(nameof(form.CheckOut), "วันที่ออกต้องอยู่หลังวันนี้");
            }

            var actorId = await GetActorIdAsync(ct);
            if (!actorId.HasValue) return Forbid();

            if (ModelState.IsValid)
            {
                var result = await _walkIn.CreateAsync(form, actorId.Value, ct);
                if (result.BookingId.HasValue)
                {
                    TempData[result.Succeeded ? "StaffSuccess" : "StaffError"] = result.Message;
                    return RedirectToAction(nameof(BookingDetails), new { id = result.BookingId });
                }

                ModelState.AddModelError(string.Empty, result.Message);
            }

            return View(await FillWalkInRoomsAsync(form, ct));
        }

        /// <summary>ห้องที่ขายได้ตั้งแต่วันนี้ถึงวันออก และรับจำนวนคนได้ (รวมเตียงเสริม)</summary>
        private async Task<WalkInViewModel> FillWalkInRoomsAsync(WalkInViewModel form, CancellationToken ct)
        {
            form.Today = _availability.HotelToday();
            var guests = form.Guests ?? 1;

            // ความจุ + เตียงเสริม 1 เตียง ต้องรับจำนวนคนได้
            var freeNow = (await _roomStatus.GetRoomBoardAsync(ct))
                .Where(r => r.Status == RoomLiveStatus.Available && (r.Capacity ?? 0) + 1 >= guests)
                .ToList();

            if (form.CheckOut > form.Today && freeNow.Count > 0)
            {
                var blocked = await _availability.GetBlockedPeriodsAsync(
                    freeNow.Select(r => r.RoomId).ToArray(), form.Today, form.CheckOut.Value, ct);
                var blockedIds = blocked.Select(b => b.RoomId).ToHashSet();
                freeNow = freeNow.Where(r => !blockedIds.Contains(r.RoomId)).ToList();
            }

            form.AvailableRooms = freeNow;
            return form;
        }

        [HttpGet]
        public async Task<IActionResult> VerificationTasks(CancellationToken ct) =>
            View(await _housekeeping.AwaitingVerificationAsync(ct));

        [HttpGet]
        public async Task<IActionResult> VerificationDetails(int id, CancellationToken ct)
        {
            if (!ModelState.IsValid || id <= 0) return BadRequest();
            var task = await _housekeeping.StaffDetailsAsync(id, ct);
            return task == null ? NotFound() : View(task);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VerifyTask(int id, CancellationToken ct)
        {
            if (!ModelState.IsValid || id <= 0) return BadRequest();
            var actorId = await GetActorIdAsync(ct);
            if (!actorId.HasValue) return Forbid();
            var verified = await _housekeeping.VerifyAsync(id, actorId.Value, ct);
            TempData[verified ? "StaffSuccess" : "StaffError"] = verified
                ? "ตรวจผ่านแล้ว ห้องกลับมาว่างให้จองได้"
                : "ตรวจงานนี้ไม่ได้ (งานถูกเปลี่ยนไปแล้ว หรือคุณเป็นคนทำงานนี้เอง)";
            return RedirectToAction(nameof(VerificationTasks));
        }

        [HttpGet]
        public IActionResult MaintenanceReports()
        {
            return RedirectToAction("Index", "Maintenance");
        }

        private async Task<int?> GetActorIdAsync(CancellationToken ct)
        {
            if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) || id <= 0 ||
                !await _db.Users.AsNoTracking().AnyAsync(u => u.UserId == id, ct))
                return null;
            return id;
        }
    }
}
