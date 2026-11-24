using System.Reflection;
using GoodHotel.Controllers;
using GoodHotel.Models;
using GoodHotel.Models.ViewModels;
using GoodHotel.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var options = new DbContextOptionsBuilder<GoodHotelDbContext>()
    .UseMySql("Server=localhost;Database=unused;User=unused;Password=unused",
        new MySqlServerVersion(new Version(8, 0, 0)))
    .Options;
using var db = new GoodHotelDbContext(options);
var service = new PaymentService(db, TimeProvider.System);
var createSubmission = typeof(PaymentService).GetMethod("CreateSubmission", BindingFlags.Instance | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("Submission factory not found.");
var key = Guid.NewGuid().ToString("N");
foreach (var method in new[] { "Bank Transfer (Mock)", "PromptPay (Mock)", "PayPal (Mock)", "Cash" })
{
    var payment = (Payment)createSubmission.Invoke(service, [1, 2, 3, method, 500m, key])!;
    var isMock = method != "Cash";
    Check(PaymentService.IsMockMethod(method) == isMock, $"{method}: method classification.");
    Check(payment.PaymentStatus == (isMock ? "Paid" : "Pending"), $"{method}: payment status.");
    Check(payment.SubmittedAtUtc.HasValue && payment.SubmittedByUserId == 3, $"{method}: submission metadata.");
    Check(payment.VerifiedAtUtc.HasValue == isMock && payment.VerifiedByUserId == (isMock ? 3 : null),
        $"{method}: confirmation metadata.");
    Check(payment.PaymentDate.HasValue == isMock && payment.Amount == 500m, $"{method}: date and amount.");
    var paymentEvent = payment.Events.Single();
    Check(paymentEvent.EventType == (isMock ? "MockConfirmed" : "Submission") &&
        paymentEvent.ActorUserId == 3 && paymentEvent.IdempotencyKey == key &&
        paymentEvent.Amount == 500m && paymentEvent.PaymentMethod == method, $"{method}: event metadata.");
    Check(!isMock || paymentEvent.Reason?.Contains("no real payment", StringComparison.OrdinalIgnoreCase) == true,
        $"{method}: event must describe the simulation.");
}
Check(!PaymentService.IsMockMethod("Bank Transfer") && !PaymentService.IsMockMethod(null),
    "Historical or unknown methods are not mock methods.");
Check(!(await service.SubmitAsync(1, 1, "Bank Transfer", key, CancellationToken.None)).Succeeded &&
    !(await service.SubmitAsync(1, 1, "PayPal", key, CancellationToken.None)).Succeeded,
    "Unlabelled historical or unknown methods cannot be submitted as mock payments.");
Check(!(await service.SubmitAsync(1, 1, "Cash", key, CancellationToken.None)).Succeeded,
    "Online bookings cannot be paid in cash (cash is desk/walk-in only).");
var review = typeof(PaymentController).GetMethod(nameof(PaymentController.Review))!;
Check(review.GetCustomAttribute<AuthorizeAttribute>()?.Roles == "Staff,Accountant" &&
    review.IsDefined(typeof(HttpPostAttribute)) && review.IsDefined(typeof(ValidateAntiForgeryTokenAttribute)),
    "Cash confirmation must require Staff/Accountant, POST, and antiforgery protection.");

var now = DateTime.UtcNow;
var payments = new[]
{
    new Payment { PaymentId = 1, BookingId = 10, Amount = 100m, VerifiedAtUtc = now },
    new Payment { PaymentId = 2, BookingId = 10, Amount = 100m, VerifiedAtUtc = now },
    new Payment { PaymentId = 3, BookingId = 11, Amount = 200m, PaymentStatus = "Pending", SubmittedAtUtc = now },
    new Payment { PaymentId = 4, BookingId = 12, Amount = 300m },
    new Payment { PaymentId = 5, BookingId = null, Amount = 40m, VerifiedAtUtc = now },
    new Payment { PaymentId = 6, BookingId = null, Amount = 60m, VerifiedAtUtc = now }
}.AsQueryable();
var confirmed = PaymentService.ConfirmedForRevenue(payments);
Check(confirmed.Select(p => p.PaymentId).SequenceEqual(new[] { 1, 5, 6 }),
    "Revenue must count one confirmed payment per booking, but retain separate orphan payments.");
var refunds = new[]
{
    new PaymentRefund { PaymentId = 1, Status = "Approved", Amount = 25m },
    new PaymentRefund { PaymentId = 1, Status = "Requested", Amount = 10m },
    new PaymentRefund { PaymentId = 2, Status = "Approved", Amount = 90m },
    new PaymentRefund { PaymentId = 3, Status = "Approved", Amount = 30m }
}.AsQueryable();
var net = (confirmed.Sum(p => p.Amount) ?? 0m) -
    (refunds.Where(r => r.Status == "Approved" && confirmed.Any(p => p.PaymentId == r.PaymentId))
        .Sum(r => (decimal?)r.Amount) ?? 0m);
Check(net == 175m, "Revenue must deduct approved refunds only for counted payments.");

PaymentListItem Item(bool legacy = false, bool excluded = false, bool verified = false,
    string? status = "Pending", decimal amount = 100m, decimal refunded = 0m) =>
    new(1, 10, "Cash", status, amount, legacy, excluded, !legacy && !excluded, verified, false, refunded);
Check(Item(legacy: true).DisplayStatus == "Requires reconciliation", "Unreviewed legacy status.");
Check(Item(excluded: true).DisplayStatus == "Excluded", "Excluded legacy status.");
Check(Item().DisplayStatus == "Pending", "Pending cash status.");
Check(Item(verified: true, status: "Completed", refunded: 50m).DisplayStatus == "Paid", "Partial refund status.");
Check(Item(verified: true, refunded: 100m).DisplayStatus == "Refunded", "Full refund status.");

var sample = new Payment
{
    PaymentId = 9, BookingId = 10, PaymentMethod = "PayPal (Mock)", Amount = 100m,
    PaymentStatus = "Paid", SubmittedAtUtc = now, VerifiedAtUtc = now,
    Booking = new Booking { User = new User { FirstName = "Test", LastName = "Customer" } },
    Refunds = new List<PaymentRefund>
    {
        new() { Status = "Approved", Amount = 100m },
        new() { Status = "Requested", Amount = 5m }
    }
};
var historyItem = PaymentController.ManagementItems(new[] { sample }.AsQueryable()).Single();
Check(historyItem.Customer == "Test Customer" && historyItem.Payment.DisplayStatus == "Refunded" &&
    historyItem.Payment.ApprovedRefundAmount == 100m && historyItem.TransactionAtUtc == now,
    "Payment history must project the customer, date, and approved refunds.");
var historyData = new[] { sample }.AsQueryable();
Check(PaymentController.FilterHistory(historyData, "10", null).Single().PaymentId == 9 &&
    PaymentController.FilterHistory(historyData, "9", null).Single().PaymentId == 9 &&
    PaymentController.FilterHistory(historyData, "Customer", "PayPal (Mock)").Single().PaymentId == 9 &&
    PaymentController.FilterHistory(historyData, "Test Customer", null).Single().PaymentId == 9 &&
    !PaymentController.FilterHistory(historyData, "Customer", "Cash").Any(),
    "History search must match booking ID, payment ID and customer and combine with method filtering.");

var searchPayments = new[]
{
    new Payment { PaymentId = 42, BookingId = 7, PaymentMethod = "Cash", PaymentStatus = "Pending",
        SubmittedAtUtc = now, Booking = new Booking { User = new User { FirstName = "Ada", LastName = "Lovelace" } } },
    new Payment { PaymentId = 7, BookingId = 8, PaymentMethod = "PayPal (Mock)", PaymentStatus = "Paid",
        SubmittedAtUtc = now, VerifiedAtUtc = now, Amount = 100m,
        Booking = new Booking { User = new User { FirstName = "Seven", LastName = "Guest" } } },
    new Payment { PaymentId = 43, BookingId = 7, PaymentMethod = "PayPal (Mock)", PaymentStatus = "Paid",
        SubmittedAtUtc = now, VerifiedAtUtc = now, Amount = 100m }
}.AsQueryable();
var sevenMatches = PaymentController.FilterHistory(searchPayments, "7", null).Select(p => p.PaymentId).ToArray();
Check(sevenMatches.SequenceEqual(new[] { 42, 7, 43 }), "Searching 7 must match both booking and payment IDs.");
var combined = PaymentController.ManagementItems(PaymentController.FilterHistory(searchPayments, "7", "Cash"))
    .Where(p => p.Payment.DisplayStatus == "Pending").ToList();
Check(combined.Count == 1 && combined[0].Payment.PaymentId == 42,
    "Booking ID search, method, and displayed status must work together.");
Check(!PaymentController.ManagementItems(PaymentController.FilterHistory(searchPayments, "7", "Cash"))
    .Any(p => p.Payment.DisplayStatus == "Paid"), "A mismatching status must exclude the result.");
Check(PaymentController.FilterHistory(searchPayments, "Ada Lovelace", null).Single().PaymentId == 42,
    "A full customer name must match the name displayed in the history.");

var dbConfirmed = PaymentService.ConfirmedForRevenue(db.Payments);
Check(dbConfirmed.ToQueryString().Contains("NOT EXISTS", StringComparison.OrdinalIgnoreCase),
    "Revenue deduplication must translate to MySQL.");
Check(db.PaymentRefunds.Where(r => r.Status == "Approved" && dbConfirmed.Any(p => p.PaymentId == r.PaymentId))
    .Select(r => r.Amount).ToQueryString().Contains("EXISTS", StringComparison.OrdinalIgnoreCase),
    "Approved refund matching must translate to MySQL.");
var query = PaymentController.FilterHistory(db.Payments, "Test", "PayPal (Mock)")
    .OrderByDescending(p => p.PaymentId);
var historySql = PaymentController.ManagementItems(query).ToQueryString();
Check(historySql.Contains("payment_refunds", StringComparison.OrdinalIgnoreCase) &&
    historySql.Contains("PaymentMethod", StringComparison.OrdinalIgnoreCase),
    "Filtered payment history and approved refund projection must translate to MySQL.");
_ = PaymentController.ManagementItems(PaymentController.FilterHistory(db.Payments, "Alice", "Cash"))
    .ToQueryString();
var numericSql = PaymentController.ManagementItems(PaymentController.FilterHistory(db.Payments, "7", "Cash"))
    .ToQueryString();
var numericWhere = numericSql[numericSql.LastIndexOf("WHERE", StringComparison.OrdinalIgnoreCase)..];
Check(numericSql.Contains("= 7;", StringComparison.Ordinal) &&
    numericWhere.Contains("`p`.`BookingId` =", StringComparison.Ordinal) &&
    numericWhere.Contains("`p`.`PaymentId` =", StringComparison.Ordinal) &&
    numericWhere.Contains("`p`.`PaymentMethod` =", StringComparison.Ordinal),
    "Numeric search and method filtering must translate together to MySQL with ID 7.");

// กติกาคำขอคืนเงิน / ยกเลิกเอง
var day = new DateOnly(2026, 11, 22);
Check(PaymentService.CanRequestRefund(BookingStatuses.Booked, day.AddDays(1), false, true, false, day),
    "Paid booking before check-out without a stay may request a refund (check-in day included).");
Check(!PaymentService.CanRequestRefund(BookingStatuses.Booked, day.AddDays(5), false, false, false, day),
    "Unpaid booking cannot request a refund.");
Check(!PaymentService.CanRequestRefund(BookingStatuses.Booked, day.AddDays(5), true, true, false, day),
    "Checked-in booking cannot request a refund.");
Check(!PaymentService.CanRequestRefund(BookingStatuses.Booked, day.AddDays(5), false, true, true, day),
    "Only one refund request per booking (rejected cannot re-request).");
Check(!PaymentService.CanRequestRefund(BookingStatuses.Cancelled, day.AddDays(5), false, true, false, day),
    "Cancelled booking cannot request a refund.");
Check(!PaymentService.CanRequestRefund(BookingStatuses.Booked, day, false, true, false, day),
    "Past check-out cannot request a refund.");
Check(BookingCancellationService.CanCancel(BookingStatuses.Booked, day.AddDays(1), false, false, day),
    "Booking can be self-cancelled until check-in.");
Check(!BookingCancellationService.CanCancel(BookingStatuses.Booked, day.AddDays(1), false, true, day),
    "Self-cancel is blocked while a refund request is pending.");
Check(!BookingCancellationService.CanCancel(BookingStatuses.Booked, day.AddDays(1), true, false, day),
    "Checked-in booking cannot be cancelled.");

// Walk-in: เบอร์ซ้ำต้องไม่เข้าผิดบัญชี
WalkInCustomer[] samePhone = [new(1, "สมชาย", "ใจดี", false), new(2, "คนอื่น", "แอบอ้าง", true)];
Check(WalkInService.MatchCustomer([], "ก", "ข", null) == (null, false), "Unknown phone creates a new customer.");
Check(WalkInService.MatchCustomer(samePhone, " คนอื่น ", "แอบอ้าง", null) == (2, false), "Same phone must match by name, not by first row.");
Check(WalkInService.MatchCustomer(samePhone, "มานี", "มีสุข", null) == (null, true), "Same phone with a different name needs a staff choice.");
Check(WalkInService.MatchCustomer(samePhone, "มานี", "มีสุข", 1) == (1, false), "Staff may pick a listed customer.");
Check(WalkInService.MatchCustomer(samePhone, "มานี", "มีสุข", 0) == (null, false), "Staff may create a new customer.");
Check(WalkInService.MatchCustomer(samePhone, "มานี", "มีสุข", 99) == (null, true), "A customer outside the phone's list is not accepted.");
WalkInCustomer[] twins = [new(1, "ก", "ข", false), new(2, "ก", "ข", true)];
Check(WalkInService.MatchCustomer(twins, "ก", "ข", null) == (null, true), "Duplicate names need a staff choice.");

// รหัสผ่านเก่า: ค่า hash ในฐานข้อมูลต้องใช้แทนรหัสผ่านไม่ได้
var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<User>();
var hash = hasher.HashPassword(new User(), "secret");
Check(LegacyPassword.IsHash(hash) && !LegacyPassword.Matches(hash, hash), "A stored hash must not work as a password.");
var v2 = new Microsoft.AspNetCore.Identity.PasswordHasher<User>(Microsoft.Extensions.Options.Options.Create(
    new Microsoft.AspNetCore.Identity.PasswordHasherOptions
    { CompatibilityMode = Microsoft.AspNetCore.Identity.PasswordHasherCompatibilityMode.IdentityV2 })).HashPassword(new User(), "secret");
Check(!LegacyPassword.Matches(v2, v2), "An Identity V2 hash must not work as a password.");
Check(LegacyPassword.Matches("plain123", "plain123") && LegacyPassword.Matches("AAAA", "AAAA"), "Old plain-text passwords still migrate.");
Check(!LegacyPassword.Matches("plain123", "other"), "A wrong plain-text password is refused.");

// ย้ายห้อง
Check(RoomMoveService.CanMove(BookingStatuses.Booked, day.AddDays(1), false, true, false, day), "Paid booking before check-in can move.");
Check(!RoomMoveService.CanMove(BookingStatuses.Booked, day.AddDays(1), false, false, false, day), "Unpaid booking cannot move.");
Check(!RoomMoveService.CanMove(BookingStatuses.Booked, day.AddDays(1), true, true, false, day), "Checked-in booking cannot move.");
Check(!RoomMoveService.CanMove(BookingStatuses.Booked, day.AddDays(1), false, true, true, day), "Pending refund blocks a move.");
Check(!RoomMoveService.CanMove(BookingStatuses.Booked, day, false, true, false, day), "No-show cannot move.");
Check(!RoomMoveService.CanMove(BookingStatuses.Cancelled, day.AddDays(1), false, true, false, day), "Cancelled booking cannot move.");
Check(RoomMoveService.IsSuitable(2500m, 2500m, 2, 3, 1) && !RoomMoveService.IsSuitable(2500m, 2400m, 2, 1, 0) &&
    !RoomMoveService.IsSuitable(2500m, 3000m, 2, 3, 0), "A move keeps or raises the room rate and must fit the guests.");
Check(ThaiText.Status("RoomMoved") != "RoomMoved" && "RoomMoved".Length <= 30, "Room moves are shown in Thai and fit the column.");

Check(RoomMoveService.BlockReason(BookingStatuses.Booked, day.AddDays(1), false, true, true, day)!.Contains("คืนเงิน") &&
    RoomMoveService.BlockReason(BookingStatuses.Booked, day.AddDays(1), false, false, false, day)!.Contains("ชำระเงิน"),
    "Staff are told why a room cannot be moved.");
Check(typeof(AuditController).GetCustomAttribute<AuthorizeAttribute>()?.Roles == "Accountant,Admin" &&
    typeof(AccountantController).GetCustomAttribute<AuthorizeAttribute>()?.Roles == "Accountant" &&
    !typeof(AuditController).GetMethods().Any(m => m.IsDefined(typeof(HttpPostAttribute))),
    "Audit is read-only for Accountant and Admin; other accounting pages stay Accountant-only.");

Check(ProfileViewModel.NormalizePhone("089 111 2233") == "0891112233" && ProfileViewModel.NormalizePhone("021112233") == null &&
    ProfileViewModel.NormalizePhone("9911122334") == null && ProfileViewModel.NormalizePhone("08911x2233") == null &&
    ProfileViewModel.NormalizePhone(null) == null, "Profile phone must be a Thai mobile number.");

var csvText = System.Text.Encoding.UTF8.GetString(CsvExport.Build(["h"],
    [["-7650.00"], ["-5"], ["=1+1"], ["-1+cmd"], ["-2+3"], ["+66811112222"], ["@x"], [" -5"], ["--5"]]));
Check(csvText.Contains("\"-7650.00\"") && csvText.Contains("\"-5\""), "Negative amounts must stay numbers in CSV.");
foreach (var risky in new[] { "=1+1", "-1+cmd", "-2+3", "+66811112222", "@x", " -5", "--5" })
    Check(csvText.Contains($"\"'{risky}\""), $"CSV must still neutralise {risky}.");

var required = new System.ComponentModel.DataAnnotations.RequiredAttribute(); ThaiValidation.Translate(required);
var custom = new System.ComponentModel.DataAnnotations.RequiredAttribute { ErrorMessage = "เอง" }; ThaiValidation.Translate(custom);
Check(required.FormatErrorMessage("X") == "กรุณากรอกข้อมูลช่องนี้" && custom.ErrorMessage == "เอง",
    "Default validation messages are Thai and custom ones are kept.");

Console.WriteLine("Payment workflow regression checks passed.");
