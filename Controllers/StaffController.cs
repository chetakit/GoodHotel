
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GoodHotel.Models;
using GoodHotel.Models.ViewModels;
using GoodHotel.Services;
using System.Security.Claims;

namespace GoodHotel.Controllers
{
    // หน้าของพนักงานต้อนรับ: การจอง เช็คอิน/เอาท์ Walk-in ย้ายห้อง ตรวจห้อง
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

        // รับ Service ที่ต้องใช้จากระบบ DI
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

        // หนึ่งแถวในรายการจอง
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
            DateTime? CreatedAtUtc,
            bool RefundPending)
        {
            public string PaymentLabel { get; init; } = "";
        }

        // ข้อมูลหน้ารายละเอียดการจอง
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
            public bool RefundPending { get; init; }
        }

        // ข้อมูลหน้าแขก
        public sealed record GuestDetailsModel(
            int BookingId,
            bool HasGuest,
            string? FirstName,
            string? LastName,
            string? Email,
            string? Phone,
            DateOnly? BirthDate);

        // หน้าหลัก: แขกเข้า/ออกวันนี้
        [HttpGet]
        public async Task<IActionResult> Index([FromServices] ParallelReader reader, CancellationToken cancellationToken)
        {
            var (start, end) = _stays.HotelDayUtcBounds();
            // นับทุกตัวเลขใน query เดียว (แต่ละ query ไปฐานข้อมูลใช้ ~200ms)
            // อาศัยแถวของผู้ใช้ที่ล็อกอินอยู่เป็นตัวยึด เพราะแถวนี้มีอยู่แน่นอน
            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var today = _availability.HotelToday();
            var countsTask = reader.Run(db => db.Users.Where(u => u.UserId == userId).Select(_ => new
            {
                Staying = db.BookingStays.Count(s => s.Status == "CheckedIn"),
                CheckedInToday = db.BookingStays.Count(s => s.CheckedInAtUtc >= start && s.CheckedInAtUtc < end),
                CheckedOutToday = db.BookingStays.Count(s => s.CheckedOutAtUtc >= start && s.CheckedOutAtUtc < end),
                ToVerify = db.HousekeepingTasks.Count(t => t.Status == "Completed"),
                OpenMaintenance = db.MaintenanceReports.Count(r => r.Status != MaintenanceReport.Resolved)
            }).SingleAsync(cancellationToken));
            // แขกที่ต้องออกวันนี้ = ยังพักอยู่ และวันออกคือวันนี้หรือเลยมาแล้ว
            var departuresTask = reader.Run(db => db.BookingStays.AsNoTracking()
                .Where(s => s.Status == "CheckedIn" && s.Booking.CheckOut <= today)
                .OrderBy(s => s.Booking.Room!.Room1)
                .Select(s => s.Booking).Select(FrontDeskRowProjection)
                .ToListAsync(cancellationToken));

            // แขกที่รอเช็คอิน = ไม่ได้ยกเลิก ยังไม่เช็คอิน และวันนี้อยู่ในช่วงที่จองไว้
            // รวมแขกที่มาช้ากว่าวันเข้าพัก (ระบบยังให้เช็คอินได้จนถึงวันออก) ไม่งั้นจะหายไปจากหน้านี้
            IQueryable<Booking> Waiting(GoodHotelDbContext db) => db.Bookings.AsNoTracking()
                .Where(b => b.Status != BookingStatuses.Cancelled && b.CheckIn <= today && b.CheckOut > today &&
                    !db.BookingStays.Any(s => s.BookingId == b.BookingId));
            // การจ่ายเงินและสถานะห้องยิงพร้อมกับรายการแขก ไม่ต้องรอกัน
            var labelTask = reader.Run(db =>
            {
                var listed = Waiting(db);
                return PaymentService.PaymentLabelLookupAsync(db.Payments.AsNoTracking()
                    .Where(p => listed.Any(b => b.BookingId == p.BookingId)), cancellationToken);
            });
            var boardTask = _roomStatus.GetRoomBoardAsync(cancellationToken);
            var arrivals = await Waiting(_db)
                .OrderBy(b => b.Room!.Room1)
                .Select(FrontDeskRowProjection)
                .ToListAsync(cancellationToken);
            var arrivalLabel = await labelTask;
            // ห้องพร้อมให้เช็คอินหรือยัง ใช้สถานะห้องชุดเดียวกับหน้าสถานะห้อง
            var board = (await boardTask).ToDictionary(r => r.RoomId);
            string? NotReady(int? roomId) => roomId.HasValue && board.TryGetValue(roomId.Value, out var room) &&
                room.Status is not (RoomLiveStatus.ArrivingToday or RoomLiveStatus.Available)
                    ? RoomStatusService.Label(room.Status) : null;
            var arrivalsWithPayment = arrivals
                .Select(row => row with
                {
                    IsPaid = arrivalLabel(row.BookingId) == PaymentService.PaidLabel,
                    RoomNotReady = NotReady(row.RoomId),
                    AwaitingInspection = row.RoomId.HasValue && board.TryGetValue(row.RoomId.Value, out var live) &&
                        live.Status == RoomLiveStatus.Inspection
                })
                .ToList();

            await Task.WhenAll(countsTask, departuresTask);
            var counts = countsTask.Result;
            var departures = departuresTask.Result;
            var model = new StaffDashboardViewModel(counts.Staying, counts.CheckedInToday,
                counts.CheckedOutToday, counts.ToVerify, counts.OpenMaintenance);

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
            b.CheckIn, b.CheckOut, b.Guests,
            b.Payments.Any(p => p.Refunds.Any(r => r.Status == PaymentService.RefundRequested)),
            b.RoomId);

        // รายการจอง: ค้นหา กรอง เรียง และแบ่งหน้า
        [HttpGet]
        public async Task<IActionResult> Bookings(
            string? q,
            string? when,
            DateOnly? from,
            DateOnly? to,
            string? sort,
            int? page,
            [FromServices] ParallelReader reader,
            CancellationToken cancellationToken)
        {
            const int pageSize = 20;
            if (from > to) (from, to) = (to, from);
            // สร้าง query จาก context ที่ส่งมา เพื่อนับจำนวนกับดึงรายการพร้อมกันได้
            var today = _availability.HotelToday();
            IQueryable<Booking> Filter(GoodHotelDbContext db)
            {
                var query = db.Bookings.AsNoTracking();

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

                // ช่วงวันเข้าพัก (ใส่ด้านเดียวก็ได้)
                if (from.HasValue) query = query.Where(b => b.CheckIn >= from);
                if (to.HasValue) query = query.Where(b => b.CheckIn <= to);

                // ตัวกรองช่วงเวลา: today = เข้าหรือออกวันนี้, upcoming = ยังไม่ถึงวันเข้าพัก, staying = พักอยู่ตอนนี้
                query = when switch
                {
                    "today" => query.Where(b => b.CheckIn == today || b.CheckOut == today),
                    "upcoming" => query.Where(b => b.Status != BookingStatuses.Cancelled && b.CheckIn > today),
                    "noshow" => query.Where(b => b.Status != BookingStatuses.Cancelled && b.CheckOut <= today &&
                        !db.BookingStays.Any(s => s.BookingId == b.BookingId)),
                    "staying" => query.Where(b => db.BookingStays.Any(s => s.BookingId == b.BookingId && s.Status == "CheckedIn")),
                    "cancelled" => query.Where(b => b.Status == BookingStatuses.Cancelled),
                    "all" => query,
                    // ค่าเริ่มต้นไม่แสดงที่ยกเลิก แต่ถ้ากำลังค้นหา ต้องหาเจอทุกการจอง
                    _ => string.IsNullOrEmpty(keyword) ? query.Where(b => b.Status != BookingStatuses.Cancelled) : query
                };

                return query;
            }

            var countTask = reader.Run(db => Filter(db).CountAsync(cancellationToken));
            var labelTask = reader.Run(db =>
            {
                var listed = Filter(db);
                return PaymentService.PaymentLabelLookupAsync(db.Payments.AsNoTracking()
                    .Where(p => listed.Any(b => b.BookingId == p.BookingId)), cancellationToken);
            });
            var currentPage = Math.Clamp(page ?? 1, 1, 100_000);
            var bookings = await LoadPageAsync(currentPage);
            var total = await countTask;
            var totalPages = Math.Max(1, (total + pageSize - 1) / pageSize);
            if (currentPage > totalPages)
            {
                currentPage = totalPages;
                bookings = await LoadPageAsync(currentPage);
            }
            ViewBag.Total = total;
            ViewBag.Page = currentPage;
            ViewBag.TotalPages = totalPages;

            ViewBag.Today = today;

            ViewBag.From = from;
            ViewBag.To = to;

            // ค่าเริ่มต้น: การจองล่าสุดก่อน (เลขที่มาก → น้อย)
            static IQueryable<Booking> Ordered(IQueryable<Booking> query, string? sort) => sort switch
            {
                "oldest" => query.OrderBy(b => b.BookingId),
                "checkin" => query.OrderBy(b => b.CheckIn).ThenBy(b => b.BookingId),
                "checkin-desc" => query.OrderByDescending(b => b.CheckIn).ThenByDescending(b => b.BookingId),
                _ => query.OrderByDescending(b => b.BookingId)
            };

            Task<List<BookingListItem>> LoadPageAsync(int number) => Ordered(Filter(_db), sort)
                .Skip((number - 1) * pageSize)
                .Take(pageSize)
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
                    booking.CreatedAtUtc,
                    booking.Payments.Any(p => p.Refunds.Any(r => r.Status == PaymentService.RefundRequested))))
                .ToListAsync(cancellationToken);

            var label = await labelTask;
            return View(bookings.Select(b => b with { PaymentLabel = label(b.BookingId) }).ToList());
        }

        // รายละเอียดการจอง
        [HttpGet]
        public async Task<IActionResult> BookingDetails(int id, [FromServices] ParallelReader reader, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid || id <= 0)
            {
                return BadRequest();
            }

            var labelsTask = reader.Run(db => PaymentService.PaymentLabelsAsync(db, new[] { id }, cancellationToken));
            var refundPendingTask = reader.Run(db => db.PaymentRefunds.AnyAsync(r => r.Payment.BookingId == id &&
                r.Status == PaymentService.RefundRequested, cancellationToken));
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
            await Task.WhenAll(labelsTask, refundPendingTask);

            if (booking == null)
            {
                return NotFound();
            }

            ViewBag.Today = _availability.HotelToday();
            return View(booking with
            {
                PaymentLabel = labelsTask.Result[id],
                RefundPending = refundPendingTask.Result
            });
        }

        // ข้อมูลติดต่อของแขก
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

        // [POST] เช็คอิน (เงื่อนไขอยู่ใน StayService) ข้อความที่ได้กลับมา = เหตุผลที่ไม่ผ่าน, null = สำเร็จ
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CheckIn(int bookingId, string? note, bool fromDashboard,
            bool closeRefund, CancellationToken ct)
        {
            if (!ModelState.IsValid || bookingId <= 0) return BadRequest();
            var actorId = await GetActorIdAsync();
            if (!actorId.HasValue) return Forbid();
            var error = await _stays.CheckInAsync(bookingId, actorId.Value, note, closeRefund, ct);
            TempData[error == null ? "StaffSuccess" : "StaffError"] = error ?? "เช็คอินเรียบร้อย";
            return BackToDeskOrBooking(fromDashboard, bookingId);
        }

        // [POST] พนักงานยกเลิกการจอง
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelBooking(int bookingId, CancellationToken ct)
        {
            if (bookingId <= 0) return BadRequest();
            var result = await _cancellation.CancelByStaffAsync(bookingId, ct);
            TempData[result.Succeeded ? "StaffSuccess" : "StaffError"] = result.Message;
            return RedirectToAction(nameof(BookingDetails), new { id = bookingId });
        }

        // [POST] เช็คเอาท์ ระบบสร้างงานทำความสะอาดให้เอง
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CheckOut(int bookingId, bool fromDashboard, CancellationToken ct)
        {
            if (!ModelState.IsValid || bookingId <= 0) return BadRequest();
            var actorId = await GetActorIdAsync();
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

        // กระดานสถานะห้องทั้งหมด
        [HttpGet]
        public async Task<IActionResult> RoomStatus(CancellationToken ct)
        {
            ViewBag.Today = _availability.HotelToday();
            return View(await _roomStatus.GetRoomBoardAsync(ct));
        }

        // ---------- ลูกค้า Walk-in ----------

        // [GET] ฟอร์มรับลูกค้า Walk-in
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

        // [POST] จอง รับเงินสด และเช็คอินในครั้งเดียว
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

            var actorId = await GetActorIdAsync();
            if (!actorId.HasValue) return Forbid();

            if (ModelState.IsValid)
            {
                var result = await _walkIn.CreateAsync(form, actorId.Value, ct);
                if (result.BookingId.HasValue)
                {
                    TempData[result.Succeeded ? "StaffSuccess" : "StaffError"] = result.Message;
                    return RedirectToAction(nameof(BookingDetails), new { id = result.BookingId });
                }

                form.Candidates = result.Candidates ?? [];
                ModelState.AddModelError(string.Empty, result.Message);
            }

            return View(await FillWalkInRoomsAsync(form, ct));
        }

        /// <summary>ยอดเงินสดที่ต้องเก็บ แสดงบนหน้า Walk-in ก่อนกดยืนยัน</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> WalkInQuote(WalkInViewModel form, CancellationToken ct)
        {
            // ฟอร์มยังกรอกไม่ครบก็คำนวณได้ จึงไม่ดู ModelState ที่นี่ (ตอนยืนยันตรวจครบ)
            form.CheckOut = DateOnly.TryParseExact(Request.Form["CheckOut"], "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var checkOut) ? checkOut : null;
            return Json(await _walkIn.QuoteAsync(form, ct));
        }

        /// <summary>ห้องที่ขายได้ตั้งแต่วันนี้ถึงวันออก และรับจำนวนคนได้ (รวมเตียงเสริม)</summary>
        private async Task<WalkInViewModel> FillWalkInRoomsAsync(WalkInViewModel form, CancellationToken ct)
        {
            form.Today = _availability.HotelToday();
            var guests = form.Guests ?? 1;

            // ความจุ + เตียงเสริม 1 เตียง ต้องรับจำนวนคนได้
            var boardTask = _roomStatus.GetRoomBoardAsync(ct);
            // ดูล่วงหน้า 1 ปี เพื่อบอกได้ว่าแต่ละห้องพักได้ถึงวันไหน
            var horizon = form.Today.AddYears(1);
            if (form.CheckOut > horizon) horizon = form.CheckOut.Value;
            var blockedTask = _availability.GetBlockedPeriodsAsync(null, form.Today, horizon, ct);
            var freeNow = (await boardTask)
                .Where(r => r.Status == RoomLiveStatus.Available && (r.Capacity ?? 0) + 1 >= guests)
                .ToList();

            // วันที่ห้องเริ่มไม่ว่าง = วันออกช้าสุดที่พักได้ (ไม่มีในรายการ = ยังไม่มีคนจองต่อ)
            form.FreeUntil = (await blockedTask).GroupBy(b => b.RoomId)
                .ToDictionary(g => g.Key, g => g.Min(b => b.Start ?? form.Today));
            var wanted = form.CheckOut > form.Today ? form.CheckOut.Value : form.Today.AddDays(1);
            bool FreeFor(RoomStatusItem r, DateOnly checkOut) =>
                !form.FreeUntil.TryGetValue(r.RoomId, out var until) || until >= checkOut;

            form.AvailableRooms = freeNow.Where(r => FreeFor(r, wanted)).ToList();
            form.ShorterStayRooms = freeNow.Where(r => !FreeFor(r, wanted) && FreeFor(r, form.Today.AddDays(1)))
                .OrderByDescending(r => form.FreeUntil[r.RoomId]).ToList();
            return form;
        }

        /// <summary>ย้ายห้องให้การจองที่จ่ายแล้วแต่ยังไม่เช็คอิน</summary>
        [HttpGet]
        public async Task<IActionResult> MoveRoom(int id, [FromServices] RoomMoveService moves, CancellationToken ct)
        {
            var info = await moves.InfoAsync(id, ct);
            if (info == null)
            {
                TempData["StaffError"] = "การจองนี้ย้ายห้องไม่ได้ (ต้องจ่ายแล้ว ยังไม่เช็คอิน และไม่มีคำขอคืนเงินค้าง)";
                return RedirectToAction(nameof(BookingDetails), new { id });
            }
            ViewBag.Options = await moves.OptionsAsync(info, ct);
            return View(info);
        }

        // [POST] ย้ายห้อง
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MoveRoom(int bookingId, int newRoomId, string? reason,
            [FromServices] RoomMoveService moves, CancellationToken ct)
        {
            if (bookingId <= 0 || newRoomId <= 0) return BadRequest();
            var actorId = await GetActorIdAsync();
            if (!actorId.HasValue) return Forbid();
            var error = await moves.MoveAsync(bookingId, newRoomId, actorId.Value, reason, ct);
            TempData[error == null ? "StaffSuccess" : "StaffError"] = error ?? "ย้ายห้องเรียบร้อย ลูกค้าจ่ายเท่าเดิม";
            return error == null
                ? RedirectToAction(nameof(BookingDetails), new { id = bookingId })
                : RedirectToAction(nameof(MoveRoom), new { id = bookingId });
        }

        // [POST] ตรวจห้องไม่ผ่าน ส่งให้แม่บ้านทำใหม่
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReworkTask(int id, string? reason, CancellationToken ct)
        {
            if (id <= 0) return BadRequest();
            var actorId = await GetActorIdAsync();
            if (!actorId.HasValue) return Forbid();
            var error = await _housekeeping.ReworkAsync(id, actorId.Value, reason, ct);
            TempData[error == null ? "StaffSuccess" : "StaffError"] = error ?? "ส่งงานกลับให้แม่บ้านทำใหม่แล้ว ห้องยังเช็คอินไม่ได้จนกว่าจะตรวจผ่าน";
            return RedirectToAction(nameof(VerificationTasks));
        }

        // ห้องที่รอตรวจ
        [HttpGet]
        public async Task<IActionResult> VerificationTasks(CancellationToken ct) =>
            View(await _housekeeping.AwaitingVerificationAsync(ct));

        // รายละเอียดห้องที่จะตรวจ
        [HttpGet]
        public async Task<IActionResult> VerificationDetails(int id, CancellationToken ct)
        {
            if (!ModelState.IsValid || id <= 0) return BadRequest();
            var task = await _housekeeping.StaffDetailsAsync(id, ct);
            return task == null ? NotFound() : View(task);
        }

        // [POST] ตรวจห้องผ่าน ห้องพร้อมเข้าพัก
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VerifyTask(int id, CancellationToken ct)
        {
            if (!ModelState.IsValid || id <= 0) return BadRequest();
            var actorId = await GetActorIdAsync();
            if (!actorId.HasValue) return Forbid();
            var verified = await _housekeeping.VerifyAsync(id, actorId.Value, ct);
            // ตรวจผ่านแล้วแต่ห้องอาจยังปิดอยู่ (งานซ่อมค้าง หรือ Admin ปิดไว้) อย่าบอกว่าพร้อมเข้าพัก
            var roomOpen = verified && await _db.HousekeepingTasks.AsNoTracking().AnyAsync(t => t.TaskId == id &&
                t.Room.Status == "Available" &&
                !_db.MaintenanceReports.Any(r => r.RoomId == t.RoomId && r.Status != MaintenanceReport.Resolved), ct);
            TempData[verified ? "StaffSuccess" : "StaffError"] = verified
                ? (roomOpen ? "ตรวจผ่านแล้ว ห้องพร้อมให้เข้าพัก" : "ตรวจผ่านแล้ว แต่ห้องยังปิดอยู่ (ติดงานซ่อมหรือ Admin ปิดไว้)")
                : "ตรวจงานนี้ไม่ได้ (งานถูกเปลี่ยนไปแล้ว หรือคุณเป็นคนทำงานนี้เอง)";
            return RedirectToAction(nameof(VerificationTasks));
        }

        // คุกกี้ถูกตรวจกับตารางผู้ใช้ทุกคำขอแล้ว (UserAccessCache) ไม่ต้องถามซ้ำ
        private Task<int?> GetActorIdAsync() => Task.FromResult<int?>(
            int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id > 0 ? id : null);
    }
}
