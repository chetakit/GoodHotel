using GoodHotel.Models;
using GoodHotel.Models.ViewModels;
using GoodHotel.Services;
using Microsoft.EntityFrameworkCore;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var today = new DateOnly(2026, 11, 14);
var bookings = new[]
{
    new Booking { RoomId = 1, CheckIn = today.AddDays(-1), CheckOut = today.AddDays(1) },
    new Booking { RoomId = 2, CheckIn = today.AddDays(1), CheckOut = today.AddDays(2) },
    new Booking { RoomId = 3, CheckIn = today.AddDays(-2), CheckOut = today },
    new Booking { RoomId = 4, CheckIn = null, CheckOut = today.AddDays(1) }
}.AsQueryable();
Check(RoomAvailabilityService.CurrentBookings(bookings, 1, today).Any(), "Current booking must restrict restoration.");
Check(!RoomAvailabilityService.CurrentBookings(bookings, 2, today).Any(), "Future booking must remain date-specific.");
Check(!RoomAvailabilityService.CurrentBookings(bookings, 3, today).Any(), "Checkout date is exclusive.");
Check(!RoomAvailabilityService.CurrentBookings(bookings, 4, today).Any(), "Undated bookings must follow existing availability rules.");

var stays = new[]
{
    new BookingStay { Booking = new Booking { RoomId = 1 }, Status = "CheckedIn" },
    new BookingStay { Booking = new Booking { RoomId = 2 }, Status = "CheckedOut" }
}.AsQueryable();
Check(RoomAvailabilityService.CurrentStays(stays, 1).Any(), "Active stay must restrict restoration.");
Check(!RoomAvailabilityService.CurrentStays(stays, 2).Any(), "Checked-out stay is not active.");

var tasks = new[]
{
    new HousekeepingTask { RoomId = 1, TaskType = "CheckoutCleaning", Status = "Completed" },
    new HousekeepingTask { RoomId = 2, TaskType = "CheckoutCleaning", Status = "Verified" },
    new HousekeepingTask { RoomId = 3, TaskType = "Other", Status = "Pending" }
}.AsQueryable();
Check(RoomAvailabilityService.PendingCheckoutCleaning(tasks, 1).Any(), "Unverified cleaning must restrict restoration.");
Check(!RoomAvailabilityService.PendingCheckoutCleaning(tasks, 2).Any(), "Verified cleaning must not restrict restoration.");
Check(!RoomAvailabilityService.PendingCheckoutCleaning(tasks, 3).Any(), "Other tasks must retain existing behavior.");

Check(MaintenanceReport.RoomStatusAfterResolution("Available", false, true) == "Maintenance",
    "A current restriction must prevent restoration to Available.");
Check(MaintenanceReport.RoomStatusAfterResolution("Available", false, false) == "Available",
    "Unrestricted rooms must restore their saved status.");
Check(MaintenanceReport.RoomStatusAfterResolution("Available", true, false) == "Maintenance",
    "Another unresolved report must keep the room in Maintenance.");
Check(MaintenanceReport.RoomStatusAfterResolution(null, false, false) == "Maintenance",
    "Unknown previous status must remain in Maintenance for review.");

var form = new RoomFormViewModel
{
    OriginalStatus = "Maintenance",
    OriginalPreMaintenanceStatus = "Available",
    OriginalMaintenanceEventId = 10
};
Check(!form.HasStaleRoomStatus("Maintenance", "Available", 10), "Unchanged room state must be accepted.");
Check(form.HasStaleRoomStatus("Available", null, 11), "A resolved report must invalidate an old form.");
Check(form.HasStaleRoomStatus("Maintenance", "Available", 12), "A new maintenance cycle must invalidate an old form.");
Check(form.HasStaleRoomStatus("Maintenance", null, 10), "A changed saved status must invalidate an old form.");
form.OriginalMaintenanceEventId = null;
Check(form.HasStaleRoomStatus("Maintenance", "Available", 10), "A missing snapshot must not be accepted.");

var options = new DbContextOptionsBuilder<GoodHotelDbContext>()
    .UseMySql("Server=localhost;Database=unused;User=unused;Password=unused",
        new MySqlServerVersion(new Version(8, 0, 0)))
    .Options;
using var db = new GoodHotelDbContext(options);
Check(RoomAvailabilityService.CurrentBookings(db.Bookings, 1, today).ToQueryString().Contains("CheckOut"),
    "The current-booking filter must translate for MySQL.");
Check(RoomAvailabilityService.CurrentStays(db.BookingStays, 1).ToQueryString().Contains("CheckedIn"),
    "The active-stay filter must translate for MySQL.");
Check(RoomAvailabilityService.PendingCheckoutCleaning(db.HousekeepingTasks, 1).ToQueryString().Contains("CheckoutCleaning"),
    "The cleaning filter must translate for MySQL.");

Console.WriteLine("Maintenance workflow regression checks passed.");
