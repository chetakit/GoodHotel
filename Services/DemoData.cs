using System.Text.Json;
using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Services;

/// <summary>
/// ตั้งข้อมูลตัวอย่างสำหรับสาธิต (ย้อนหลัง 21 วัน ถึงล่วงหน้า 9 วัน นับจากวันที่รัน)
///   dotnet run -- seed-demo --yes
/// ลบการจอง/การเงิน/งานทั้งหมดเดิมแล้วสร้างใหม่ใน transaction เดียว และสำรองของเดิมเป็นไฟล์ใน backups/ ก่อนทุกครั้ง
/// ห้องพัก โปรโมชัน อีเมล และรหัสผ่านไม่เปลี่ยน รันซ้ำเช้าวันสาธิตได้ วันที่จะเลื่อนตามเอง
/// </summary>
public static class DemoData
{
    private const int Admin = 7, Staff = 10, Maid = 11, Accountant = 21;
    private const int Kitti = 3, Thana = 4, Pim = 5, Anucha = 6, Nicha = 9;

    // บัญชีที่เก็บไว้ (#20 ฝ่ายบัญชีสำรอง)
    private static readonly int[] Keep = [Kitti, Thana, Pim, Anucha, Admin, Nicha, Staff, Maid, 20, Accountant];

    // ชื่อสำหรับสาธิต
    private static readonly (int Id, string First, string Last, string Phone)[] People =
    [
        (Kitti, "กิตติ", "พงษ์ไทย", "0831231233"), (Thana, "ธนา", "ศรีสุข", "0861112201"),
        (Pim, "พิมพ์ชนก", "แก้วใส", "0812312312"), (Anucha, "อนุชา", "ทองดี", "0891112203"),
        (Nicha, "ณิชา", "วัฒนกุล", "0832423423"), (Staff, "สมชาย", "ใจดี", "0812345678"),
        (Maid, "สมศรี", "รักสะอาด", "0912323423"), (Accountant, "วิภา", "บัญชีดี", "0953635462"), (20, "นภา", "รุ่งเรือง", "0891112220")
    ];

    // ลูกค้า Walk-in (ไม่มีอีเมลและรหัสผ่าน)
    private static readonly (int Id, string First, string Last, string Phone)[] WalkIns =
    [
        (101, "วรพล", "มั่นคง", "0811110101"), (102, "จิราพร", "สายทอง", "0811110102"),
        (103, "ปรีชา", "วงศ์ดี", "0811110103"), (104, "สุดา", "บุญมาก", "0811110104"),
        (105, "ชัยวัฒน์", "ศิริพงษ์", "0811110105"), (106, "อรทัย", "แสงจันทร์", "0811110106")
    ];

    // ประเภทการจองตัวอย่าง
    private enum Kind { Finished, InHouse, Upcoming, NoShow, Timeout, SelfCancelUnpaid, SelfCancelPaid, StaffCancel, Refunded }

    // แผนของการจองตัวอย่าง 1 รายการ
    private sealed record Spec(int Customer, string Room, DateOnly In, DateOnly Out, DateTime CreatedUtc, Kind Kind,
        bool WalkIn = false, int ExtraBed = 0, DateTime? CheckedOutUtc = null, DateTime? CancelledUtc = null, string? Tag = null)
    {
        // การจองที่กันห้องจริง (ใช้กันข้อมูลตัวอย่างซ้อนกัน)
        public bool HoldsRoom => Kind is Kind.Finished or Kind.InHouse or Kind.Upcoming or Kind.NoShow;
        // วันชนกันไหม
        public bool Overlaps(DateOnly from, DateOnly to) => In < to && Out > from;
    }

    // คำสั่ง dotnet run -- seed-demo: สำรอง → ล้าง → สร้างข้อมูล
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        if (!args.Contains("--yes"))
        {
            Console.WriteLine("คำสั่งนี้ลบการจอง การเงิน งานทำความสะอาด และงานซ่อมทั้งหมด แล้วใส่ข้อมูลตัวอย่างใหม่");
            Console.WriteLine("(สำรองของเดิมไว้ใน backups/ ก่อน) ถ้าต้องการทำจริง: dotnet run -- seed-demo --yes");
            return 1;
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GoodHotelDbContext>();
        var root = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
        db.Database.SetCommandTimeout(120);

        var file = await BackupAsync(db, Path.Combine(root, "backups"));
        Console.WriteLine($"สำรองข้อมูลเดิมแล้ว: {file}");

        await using var tx = await db.Database.BeginTransactionAsync();
        await ClearAsync(db);
        var summary = await SeedAsync(db);
        await tx.CommitAsync();
        Console.WriteLine(summary);
        return 0;
    }

    // สำรองข้อมูลเดิมเป็นไฟล์ JSON
    private static async Task<string> BackupAsync(GoodHotelDbContext db, string folder)
    {
        var data = new
        {
            Users = await db.Users.AsNoTracking().ToListAsync(),
            Rooms = await db.Rooms.AsNoTracking().ToListAsync(),
            Promotions = await db.Promotions.AsNoTracking().ToListAsync(),
            Bookings = await db.Bookings.AsNoTracking().ToListAsync(),
            BookingStays = await db.BookingStays.AsNoTracking().ToListAsync(),
            Payments = await db.Payments.AsNoTracking().ToListAsync(),
            PaymentEvents = await db.PaymentEvents.AsNoTracking().ToListAsync(),
            PaymentRefunds = await db.PaymentRefunds.AsNoTracking().ToListAsync(),
            HousekeepingTasks = await db.HousekeepingTasks.AsNoTracking().ToListAsync(),
            HousekeepingTaskEvents = await db.HousekeepingTaskEvents.AsNoTracking().ToListAsync(),
            MaintenanceReports = await db.MaintenanceReports.AsNoTracking().ToListAsync(),
            MaintenanceReportEvents = await db.MaintenanceReportEvents.AsNoTracking().ToListAsync()
        };
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"backup-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(data));
        return path;
    }

    // ล้างข้อมูลเดิม
    private static async Task ClearAsync(GoodHotelDbContext db)
    {
        // ลูกก่อนแม่
        await db.PaymentEvents.ExecuteDeleteAsync();
        await db.PaymentRefunds.ExecuteDeleteAsync();
        await db.Payments.ExecuteDeleteAsync();
        await db.HousekeepingTaskEvents.ExecuteDeleteAsync();
        await db.HousekeepingTasks.ExecuteDeleteAsync();
        await db.BookingStays.ExecuteDeleteAsync();
        await db.Bookings.ExecuteDeleteAsync();
        await db.MaintenanceReportEvents.ExecuteDeleteAsync();
        await db.MaintenanceReports.ExecuteDeleteAsync();

        var keep = Keep.ToList();
        await db.Users.Where(u => !keep.Contains(u.UserId)).ExecuteDeleteAsync();
        await db.Rooms.ExecuteUpdateAsync(r => r
            .SetProperty(x => x.Status, "Available")
            .SetProperty(x => x.PreMaintenanceStatus, (string?)null));
        // โปรเก่าที่ไม่มีเงื่อนไข ลูกค้าใช้ไม่ได้อยู่แล้ว ปิดไว้ไม่ให้ขึ้นว่าเปิดใช้งาน
        await db.Promotions.Where(p => p.RuleType == null)
            .ExecuteUpdateAsync(p => p.SetProperty(x => x.IsActive, false));
    }

    // สร้างข้อมูลตัวอย่างใหม่
    private static async Task<string> SeedAsync(GoodHotelDbContext db)
    {
        var now = DateTime.UtcNow;
        var today = RoomAvailabilityService.HotelDate(now);
        DateOnly D(int days) => today.AddDays(days);
        // เวลาไทยของวันนั้น → UTC และไม่เกินเวลาปัจจุบัน
        DateTime At(DateOnly day, int hour, int minute = 0)
        {
            var utc = FinanceLedger.UtcBounds(day, day).StartUtc.AddHours(hour).AddMinutes(minute);
            return utc < now ? utc : now.AddMinutes(-1);
        }

        var rooms = await db.Rooms.ToDictionaryAsync(r => r.Room1!);
        var promotions = await db.Promotions.AsNoTracking()
            .Where(p => p.IsActive && p.RuleType != null && p.DiscountType != null && p.DiscountValue > 0).ToListAsync();

        foreach (var (id, first, last, phone) in People)
        {
            var user = await db.Users.SingleAsync(u => u.UserId == id);
            (user.FirstName, user.LastName, user.Phone, user.IsActive) = (first, last, phone, true);
            user.BirthDate = id switch
            {
                Kitti => new DateOnly(1995, 4, 18),
                Thana => new DateOnly(1988, 8, 9),
                Pim => new DateOnly(1999, today.AddMonths(1).Month, 5),
                Anucha => new DateOnly(1992, 6, 21),
                Nicha => new DateOnly(2000, today.Month, 15), // เดือนนี้ ให้เห็นโปรเดือนเกิด
                _ => user.BirthDate is { } birth && birth <= today ? birth : new DateOnly(1990, 1, 10)
            };
        }
        db.Users.AddRange(WalkIns.Select(w => new User
        {
            UserId = w.Id, FirstName = w.First, LastName = w.Last, Phone = w.Phone, Role = "Customer", IsActive = true
        }));
        await db.SaveChangesAsync();
        var birthMonths = await db.Users.AsNoTracking().Where(u => u.BirthDate != null)
            .ToDictionaryAsync(u => u.UserId, u => u.BirthDate!.Value.Month);

        // ---------- สถานการณ์ที่ตั้งใจให้เห็นตอนสาธิต ----------
        var specs = new List<Spec>
        {
            // ธนา: ลูกค้าประจำ พักมาแล้ว 3 ครั้ง จองครั้งใหม่ได้โปร Loyal Guest
            new(Thana, "103", D(-18), D(-16), At(D(-20), 10), Kind.Finished),
            new(Thana, "304", D(-13), D(-11), At(D(-15), 20), Kind.Finished),
            new(Thana, "201", D(-7), D(-5), At(D(-9), 9), Kind.Finished),
            new(Thana, "201", D(3), D(5), At(D(-1), 11), Kind.Upcoming),
            // กิตติ: กำลังเข้าพัก
            new(Kitti, "102", D(-12), D(-10), At(D(-14), 15), Kind.Finished),
            new(Kitti, "101", D(-1), D(1), At(D(-3), 19), Kind.InHouse),
            new(Kitti, "401", D(7), D(9), At(D(-1), 21), Kind.Upcoming),
            // พิมพ์ชนก: มาถึงวันนี้ และถูกย้ายห้องเพราะแอร์ห้องเดิมเสีย
            new(Pim, "301", D(-16), D(-15), At(D(-18), 12), Kind.Finished),
            new(Pim, "102", D(0), D(2), At(D(-2), 13), Kind.Upcoming, Tag: "moved"),
            new(Pim, "303", D(8), D(9), At(D(-1), 16), Kind.Upcoming),
            // อนุชา: มาถึงวันนี้แต่ห้องยังรอตรวจ และมีคำขอคืนเงินรอพิจารณา
            new(Anucha, "401", D(-15), D(-13), At(D(-17), 8), Kind.Finished),
            new(Anucha, "203", D(0), D(1), At(D(-2), 18), Kind.Upcoming),
            new(Anucha, "301", D(4), D(6), At(D(-2), 20), Kind.Upcoming, Tag: "refund-requested"),
            new(Anucha, "201", D(-4), D(-2), At(D(-8), 14), Kind.Refunded, CancelledUtc: At(D(-6), 10)),
            // ณิชา: ไม่มาเข้าพัก ยกเลิกเองแบบไม่คืนเงิน ถูกปฏิเสธคืนเงิน และมาถึงวันนี้
            new(Nicha, "401", D(-10), D(-9), At(D(-12), 17), Kind.NoShow),
            new(Nicha, "303", D(-3), D(-2), At(D(-6), 12), Kind.SelfCancelPaid, CancelledUtc: At(D(-5), 9)),
            new(Nicha, "304", D(0), D(1), At(D(-1), 10), Kind.Upcoming),
            new(Nicha, "103", D(6), D(8), At(D(-3), 11), Kind.Upcoming, Tag: "refund-rejected"),
            // จองล่วงหน้าและรายการเงินของวันนี้ ให้หน้าบัญชีกับกราฟมีข้อมูล
            new(Nicha, "202", D(2), D(4), At(D(-4), 15), Kind.Upcoming),
            new(Kitti, "304", D(3), D(5), At(D(-2), 9), Kind.Upcoming),
            new(Pim, "203", D(5), D(7), At(D(-1), 8), Kind.Upcoming),
            new(Thana, "302", D(8), D(9), now.AddMinutes(-180), Kind.Upcoming),
            new(106, "303", D(0), D(1), now.AddMinutes(-60), Kind.InHouse, WalkIn: true),
            // ยกเลิกแบบอื่น
            new(Pim, "302", D(-5), D(-4), At(D(-8), 21), Kind.Timeout, CancelledUtc: At(D(-8), 21, 15)),
            new(Thana, "403", D(-9), D(-8), At(D(-12), 22), Kind.Timeout, CancelledUtc: At(D(-12), 22, 15)),
            new(Kitti, "204", D(-11), D(-10), At(D(-15), 13), Kind.StaffCancel, CancelledUtc: At(D(-15), 13, 8)),
            new(Anucha, "104", D(-6), D(-5), At(D(-10), 18), Kind.SelfCancelUnpaid, CancelledUtc: At(D(-10), 18, 5)),
            // Walk-in ที่ยังพักอยู่ และที่เช็คเอาท์วันนี้ (ห้องอยู่ในขั้นทำความสะอาดต่างกัน)
            new(101, "202", D(-2), D(0), At(D(-2), 14), Kind.InHouse, WalkIn: true),
            new(102, "403", D(-1), D(2), At(D(-1), 15), Kind.InHouse, WalkIn: true),
            new(103, "104", D(-1), D(0), At(D(-1), 13), Kind.Finished, WalkIn: true, CheckedOutUtc: now.AddMinutes(-90), Tag: "clean-pending"),
            new(104, "302", D(-2), D(0), At(D(-2), 16), Kind.Finished, WalkIn: true, CheckedOutUtc: now.AddMinutes(-120), Tag: "clean-progress"),
            new(105, "203", D(-1), D(0), At(D(-1), 17), Kind.Finished, WalkIn: true, CheckedOutUtc: now.AddMinutes(-150), Tag: "clean-done")
        };

        // ---------- ประวัติทั่วไปให้กราฟและรายงานมีข้อมูล (ผลเหมือนเดิมทุกครั้งที่รัน) ----------
        var random = new Random(402);
        foreach (var number in rooms.Keys.Order())
        {
            for (var day = D(-18 + random.Next(0, 4)); day < D(-1); day = day.AddDays(1))
            {
                var nights = random.Next(1, 4);
                var (from, to) = (day, day.AddDays(nights));
                if (to > D(-1)) break;
                // 204 เสียตั้งแต่เมื่อวาน ไม่กระทบประวัติ
                if (specs.Any(s => s.HoldsRoom && s.Room == number && s.Overlaps(from, to))) continue;
                var walkIn = random.NextDouble() < 0.6;
                var people = (walkIn ? WalkIns.Select(w => w.Id) : new[] { Kitti, Pim, Anucha })
                    .Where(id => !specs.Any(s => s.HoldsRoom && s.Customer == id && s.Overlaps(from, to))).ToArray();
                if (people.Length == 0) continue;
                var created = walkIn ? At(from, 13 + random.Next(0, 6), random.Next(0, 60))
                    : At(from.AddDays(-random.Next(1, 4)), 8 + random.Next(0, 14), random.Next(0, 60));
                specs.Add(new Spec(people[random.Next(people.Length)], number, from, to, created, Kind.Finished,
                    walkIn, random.NextDouble() < 0.12 ? 1 : 0));
                day = to.AddDays(random.Next(0, 4));
            }
        }

        // ---------- สร้างการจองตามลำดับเวลาที่จอง ----------
        string[] methods = ["PromptPay (Mock)", "Bank Transfer (Mock)", "PayPal (Mock)"];
        int paymentId = 0, taskId = 0, taskEventId = 0, paymentEventId = 0, refundId = 0;
        var bookingId = 0;
        var done = new List<(Spec Spec, int Id)>();
        foreach (var spec in specs.OrderBy(s => s.CreatedUtc))
        {
            var room = rooms[spec.Room];
            var nights = spec.Out.DayNumber - spec.In.DayNumber;
            var subtotal = room.Rate!.Value * nights;
            var createdDay = RoomAvailabilityService.HotelDate(spec.CreatedUtc);
            var history = new CustomerHistory(true,
                birthMonths.TryGetValue(spec.Customer, out var month) ? month : null,
                done.Count(x => x.Spec.Customer == spec.Customer && !(x.Spec.CancelledUtc < spec.CreatedUtc)),
                done.Count(x => x.Spec.Customer == spec.Customer && x.Spec.Kind == Kind.Finished && x.Spec.Out <= createdDay));
            var promotion = PromotionService.FindEligible(new PromotionData(promotions, history),
                new PromotionContext(spec.In, spec.Out, subtotal), createdDay).FirstOrDefault();
            var amount = subtotal - (promotion?.DiscountAmount ?? 0m) + 500m * spec.ExtraBed * nights;
            var id = ++bookingId;
            done.Add((spec, id));

            var cancelled = spec.CancelledUtc.HasValue;
            db.Bookings.Add(new Booking
            {
                BookingId = id, UserId = spec.Customer, RoomId = room.RoomId, CheckIn = spec.In, CheckOut = spec.Out,
                Guests = spec.ExtraBed == 1 ? room.Capacity + 1 : Math.Max(1, room.Capacity!.Value - id % 2),
                ExtraBed = spec.ExtraBed, PromotionId = promotion?.PromotionId, Amount = amount, CreatedAtUtc = spec.CreatedUtc,
                Status = cancelled ? BookingStatuses.Cancelled : BookingStatuses.Booked, CancelledAtUtc = spec.CancelledUtc,
                CancelReason = spec.Kind switch
                {
                    Kind.Timeout => CancelReasons.PaymentTimeout,
                    Kind.StaffCancel => CancelReasons.Staff,
                    Kind.SelfCancelPaid or Kind.SelfCancelUnpaid => CancelReasons.Customer,
                    Kind.Refunded => CancelReasons.Refund,
                    _ => null
                }
            });

            if (spec.Kind is Kind.Timeout or Kind.StaffCancel or Kind.SelfCancelUnpaid) continue;

            // ---------- การชำระเงิน ----------
            var paidAt = spec.CreatedUtc.AddMinutes(spec.WalkIn ? 2 : 3 + id % 7);
            var actor = spec.WalkIn ? Staff : spec.Customer;
            var method = spec.WalkIn ? "Cash" : methods[id % methods.Length];
            var payment = new Payment
            {
                PaymentId = ++paymentId, BookingId = id, Amount = amount, PaymentMethod = method,
                PaymentStatus = spec.Kind == Kind.Refunded ? "Refunded" : "Paid",
                PaymentDate = RoomAvailabilityService.HotelDate(paidAt), SubmittedAtUtc = paidAt, SubmittedByUserId = actor,
                VerifiedAtUtc = paidAt, VerifiedByUserId = actor
            };
            db.Payments.Add(payment);
            void PayEvent(string type, int who, DateTime when, decimal? value, string? reason) => db.PaymentEvents.Add(new PaymentEvent
            {
                EventId = ++paymentEventId, PaymentId = payment.PaymentId, ActorUserId = who, EventType = type, OccurredAtUtc = when,
                Amount = value, PaymentMethod = method, Reason = reason, IdempotencyKey = Guid.NewGuid().ToString("N")
            });
            PayEvent(spec.WalkIn ? "DeskCashReceived" : "MockConfirmed", actor, paidAt, amount,
                spec.WalkIn ? "รับเงินสดที่เคาน์เตอร์" : "DEMO only; no real payment was transferred.");

            void Refund(string status, DateTime requested, string reason, DateTime? decided = null, string? note = null)
            {
                db.PaymentRefunds.Add(new PaymentRefund
                {
                    RefundId = ++refundId, PaymentId = payment.PaymentId, Amount = amount, Status = status, Reason = reason,
                    RequestedByUserId = spec.Customer, RequestedAtUtc = requested,
                    ApprovedByUserId = status == "Approved" ? Accountant : null, ApprovedAtUtc = status == "Approved" ? decided : null
                });
                PayEvent("RefundRequested", spec.Customer, requested, amount, reason);
                if (decided.HasValue)
                    PayEvent(status == "Approved" ? "RefundApproved" : "RefundRejected", Accountant, decided.Value, amount, note ?? reason);
            }
            if (spec.Kind == Kind.Refunded) Refund("Approved", spec.CancelledUtc!.Value.AddHours(-5), "เจ็บป่วย", spec.CancelledUtc);
            if (spec.Tag == "refund-requested") Refund("Requested", now.AddMinutes(-50), "เปลี่ยนแผนการเดินทาง");
            if (spec.Tag == "refund-rejected")
                Refund("Rejected", At(D(-2), 9), "เปลี่ยนแผนการเดินทาง", At(D(-2), 13), "ไม่มีเอกสารประกอบตามเงื่อนไข");
            if (spec.Tag == "moved") PayEvent("RoomMoved", Staff, At(D(-1), 16), null, "ห้อง 204 → ห้อง 102: แอร์ห้อง 204 เสีย");

            // ---------- การเข้าพักและงานทำความสะอาด ----------
            if (spec.Kind is not (Kind.Finished or Kind.InHouse)) continue;
            var checkedIn = spec.WalkIn ? paidAt.AddMinutes(3) : At(spec.In, 14, id * 7 % 60);
            var checkedOut = spec.Kind == Kind.Finished ? spec.CheckedOutUtc ?? At(spec.Out, 10, id * 11 % 60) : (DateTime?)null;
            db.BookingStays.Add(new BookingStay
            {
                BookingId = id, Status = checkedOut.HasValue ? "CheckedOut" : "CheckedIn", CheckedInAtUtc = checkedIn,
                CheckedInByUserId = Staff, CheckedOutAtUtc = checkedOut, CheckedOutByUserId = checkedOut.HasValue ? Staff : null,
                CheckInNote = spec.WalkIn ? "Walk-in" : null
            });
            if (checkedOut is not { } left) continue;

            // ขั้นของงาน: 0 รอคนรับ, 1 กำลังทำ, 2 รอตรวจ, 3 ตรวจผ่าน
            var stage = spec.Tag switch { "clean-pending" => 0, "clean-progress" => 1, "clean-done" => 2, _ => 3 };
            var (claimed, finished, verified) = (left.AddMinutes(15), left.AddMinutes(55), left.AddMinutes(75));
            var task = new HousekeepingTask
            {
                TaskId = ++taskId, RoomId = room.RoomId, StayBookingId = id, TaskType = "CheckoutCleaning", CreatedByUserId = Staff,
                CreatedAtUtc = left, Status = stage switch { 0 => "Pending", 1 => "InProgress", 2 => "Completed", _ => "Verified" },
                AssignedToUserId = stage > 0 ? Maid : null, AcceptedAtUtc = stage > 0 ? claimed : null,
                StartedAtUtc = stage > 0 ? claimed : null, CompletedAtUtc = stage > 1 ? finished : null,
                CompletedByUserId = stage > 1 ? Maid : null, VerifiedAtUtc = stage > 2 ? verified : null,
                VerifiedByUserId = stage > 2 ? Staff : null
            };
            db.HousekeepingTasks.Add(task);
            void TaskEvent(string type, int who, DateTime when, string? before, string after) =>
                db.HousekeepingTaskEvents.Add(new HousekeepingTaskEvent
                {
                    EventId = ++taskEventId, TaskId = task.TaskId, ActorUserId = who, EventType = type, OccurredAtUtc = when,
                    PreviousStatus = before, NewStatus = after,
                    PreviousAssignee = type is "Created" or "Claim" ? null : Maid, NewAssignee = type == "Created" ? null : Maid
                });
            TaskEvent("Created", Staff, left, null, "Pending");
            if (stage > 0) { TaskEvent("Claim", Maid, claimed, "Pending", "Accepted"); TaskEvent("Start", Maid, claimed, "Accepted", "InProgress"); }
            if (stage > 1) TaskEvent("Complete", Maid, finished, "InProgress", "Completed");
            if (stage > 2) TaskEvent("Verify", Staff, verified, "Completed", "Verified");
        }

        // ---------- งานซ่อม ----------
        int reportId = 0, reportEventId = 0;
        void Repair(string number, string problem, int by, DateTime reported, DateTime? started = null, DateTime? resolved = null, string? note = null)
        {
            var room = rooms[number];
            var status = resolved.HasValue ? MaintenanceReport.Resolved : started.HasValue ? MaintenanceReport.InProgress : MaintenanceReport.Open;
            var report = new MaintenanceReport
            {
                ReportId = ++reportId, RoomId = room.RoomId, Description = problem, Status = status, BlocksRoom = true,
                ReportedByUserId = by, ReportedAtUtc = reported, ResolvedAtUtc = resolved,
                ResolvedByUserId = resolved.HasValue ? Admin : null, ResolutionNote = note
            };
            db.MaintenanceReports.Add(report);
            void Step(int who, DateTime when, string? before, string after, string? text = null) =>
                db.MaintenanceReportEvents.Add(new MaintenanceReportEvent
                {
                    EventId = ++reportEventId, ReportId = report.ReportId, ActorUserId = who, OccurredAtUtc = when,
                    FromStatus = before, ToStatus = after, Note = text
                });
            Step(by, reported, null, MaintenanceReport.Open);
            if (started.HasValue) Step(Admin, started.Value, MaintenanceReport.Open, MaintenanceReport.InProgress);
            if (resolved.HasValue) Step(Admin, resolved.Value, MaintenanceReport.InProgress, MaintenanceReport.Resolved, note);
            if (!resolved.HasValue) (room.Status, room.PreMaintenanceStatus) = ("Maintenance", "Available");
        }
        Repair("303", "ฝักบัวน้ำไหลเบา", Maid, At(D(-17), 11), At(D(-17), 13), At(D(-17), 15), "ล้างหัวฝักบัวและเปลี่ยนสาย");
        Repair("101", "รีโมตทีวีเสีย", Staff, At(D(-13), 9), At(D(-13), 9, 30), At(D(-13), 10), "เปลี่ยนรีโมตใหม่");
        Repair("402", "หลอดไฟห้องน้ำขาด", Maid, At(D(-8), 12), At(D(-8), 13), At(D(-8), 13, 30), "เปลี่ยนหลอดไฟ");
        Repair("204", "แอร์ไม่เย็น มีน้ำหยด", Maid, At(D(-1), 10), At(D(-1), 15));
        Repair("402", "ก๊อกน้ำอ่างล้างหน้ารั่ว", Maid, now.AddMinutes(-40));

        await db.SaveChangesAsync();

        var held = done.Where(x => x.Spec.HoldsRoom).ToList();
        foreach (var a in held)
            if (held.Any(b => b.Id != a.Id && b.Spec.Room == a.Spec.Room && b.Spec.Overlaps(a.Spec.In, a.Spec.Out)))
                throw new InvalidOperationException($"ข้อมูลตัวอย่างซ้อนกัน: ห้อง {a.Spec.Room} {a.Spec.In:yyyy-MM-dd}");

        return $"ตั้งข้อมูลตัวอย่างแล้ว (วันนี้ = {today:dd/MM/yyyy}): การจอง {done.Count}, การชำระ {paymentId}, " +
               $"คำขอคืนเงิน {refundId}, งานทำความสะอาด {taskId}, งานซ่อม {reportId}";
    }
}
