
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GoodHotel.Models.ViewModels;

namespace GoodHotel.Controllers
{
    [Authorize(Roles = "Staff")]
    public class StaffController : Controller
    {
        [HttpGet]
        public IActionResult Index()
        {
            return Page(
                "Staff Dashboard",
                "Reception overview and daily hotel operations.",
                new OperationsSectionViewModel
                {
                    Title = "Today's overview",
                    Description = "Metrics will appear when the stay workflow is available.",
                    Fields = new[]
                    {
                        "Total rooms",
                        "Available rooms",
                        "Occupied rooms",
                        "Today's check-ins",
                        "Today's check-outs",
                        "Upcoming bookings"
                    }
                });
        }

        [HttpGet]
        public IActionResult Bookings()
        {
            return Page(
                "Booking Overview",
                "Search and review booking details for reception operations.",
                new OperationsSectionViewModel
                {
                    Title = "Booking search",
                    Description = "Search is unavailable until a read-only booking query is connected.",
                    InputFields = new[]
                    {
                        "Booking ID",
                        "Customer name",
                        "Room number"
                    },
                    ActionLabel = "Search (not implemented)",
                    DisabledActionMessage = "Search is disabled until a read-only booking query is implemented."
                },
                new OperationsSectionViewModel
                {
                    Title = "Bookings",
                    Description = "Booking records are not loaded on this screen.",
                    EmptyState = "Booking records have not been queried. Search results and record links require backend integration."
                });
        }

        [HttpGet]
        public IActionResult BookingDetails(int id)
        {
            if (id <= 0)
            {
                return BadRequest();
            }

            return BookingPage(
                "Booking Details",
                "Booking data is not loaded. The supplied identifier has not been verified against a booking record.",
                id,
                new[]
                {
                    new OperationsLinkViewModel
                    {
                        Label = "Guest Details",
                        Controller = "Staff",
                        Action = nameof(GuestDetails),
                        RouteValues = new { bookingId = id }
                    },
                    new OperationsLinkViewModel
                    {
                        Label = "Check-in",
                        Controller = "Staff",
                        Action = nameof(CheckIn),
                        RouteValues = new { bookingId = id }
                    },
                    new OperationsLinkViewModel
                    {
                        Label = "Check-out",
                        Controller = "Staff",
                        Action = nameof(CheckOut),
                        RouteValues = new { bookingId = id }
                    }
                },
                new OperationsSectionViewModel
                {
                    Title = "Booking information",
                    Description = "No booking data has been loaded.",
                    EmptyState = "Booking details require backend integration."
                },
                new OperationsSectionViewModel
                {
                    Title = "Guest information",
                    Description = "Only guest details required for reception duties will be displayed.",
                    EmptyState = "Guest details require backend integration."
                });
        }

        [HttpGet]
        public IActionResult GuestDetails(int bookingId)
        {
            if (bookingId <= 0)
            {
                return BadRequest();
            }

            return BookingPage(
                "Guest Details",
                "Guest details are not loaded. The supplied identifier has not been verified against a booking record.",
                bookingId,
                new[]
                {
                    new OperationsLinkViewModel
                    {
                        Label = "Booking Details",
                        Controller = "Staff",
                        Action = nameof(BookingDetails),
                        RouteValues = new { id = bookingId }
                    }
                },
                new OperationsSectionViewModel
                {
                    Title = "Guest information",
                    Description = "No customer data is displayed on this preparation screen.",
                    EmptyState = "Guest information requires backend integration."
                });
        }

        [HttpGet]
        public IActionResult CheckIn(int bookingId)
        {
            if (bookingId <= 0)
            {
                return BadRequest();
            }

            return BookingPage(
                "Check-in",
                $"Booking #{bookingId}: review and room-readiness data are not loaded.",
                bookingId,
                BookingWorkflowLinks(bookingId),
                new OperationsSectionViewModel
                {
                    Title = "Check-in review",
                    Description = "Room readiness, cleaning tasks, and blocking maintenance must be verified by the future workflow.",
                    Fields = new[]
                    {
                        "Booking and guest summary",
                        "Room readiness",
                        "Early check-in confirmation"
                    },
                    ActionLabel = "Confirm check-in (not implemented)",
                    DisabledActionMessage = "Check-in is disabled until stay persistence and readiness checks are implemented."
                });
        }

        [HttpGet]
        public IActionResult CheckOut(int bookingId)
        {
            if (bookingId <= 0)
            {
                return BadRequest();
            }

            return BookingPage(
                "Check-out",
                $"Booking #{bookingId}: active-stay data is not loaded.",
                bookingId,
                BookingWorkflowLinks(bookingId),
                new OperationsSectionViewModel
                {
                    Title = "Check-out review",
                    Description = "A cleaning task must be created atomically when check-out is implemented.",
                    Fields = new[]
                    {
                        "Active stay summary",
                        "Check-out confirmation",
                        "Checkout cleaning task"
                    },
                    ActionLabel = "Confirm check-out (not implemented)",
                    DisabledActionMessage = "Check-out and cleaning-task creation are disabled until transactional backend integration is implemented."
                });
        }

        [HttpGet]
        public IActionResult MaintenanceReports()
        {
            return Page(
                "Maintenance Reports",
                "Report a room issue if this role is authorized by the final operational policy.",
                new OperationsSectionViewModel
                {
                    Title = "New report",
                    Description = "Staff report submission remains disabled pending role-policy confirmation.",
                    Fields = new[]
                    {
                        "Room",
                        "Issue details",
                        "Blocks room use"
                    },
                    ActionLabel = "Submit report (not implemented)",
                    DisabledActionMessage = "Report submission is disabled until the Staff reporting policy and maintenance persistence are implemented."
                });
        }

        private ViewResult Page(
            string title,
            string description,
            params OperationsSectionViewModel[] sections)
        {
            return BuildPage(title, description, null, null, sections);
        }

        private ViewResult BookingPage(
            string title,
            string description,
            int bookingId,
            OperationsLinkViewModel[] links,
            params OperationsSectionViewModel[] sections)
        {
            return BuildPage(title, description, bookingId, links, sections);
        }

        private ViewResult BuildPage(
            string title,
            string description,
            int? bookingId,
            OperationsLinkViewModel[]? links,
            OperationsSectionViewModel[] sections)
        {
            return View(
                "~/Views/Shared/OperationsPage.cshtml",
                new OperationsPageViewModel
                {
                    Title = title,
                    Description = description,
                    BackController = "Staff",
                    BackAction = nameof(Index),
                    BackLabel = "Staff Dashboard",
                    Sections = sections,
                    BookingId = bookingId,
                    Links = links ?? Array.Empty<OperationsLinkViewModel>()
                });
        }

        private static OperationsLinkViewModel[] BookingWorkflowLinks(int bookingId)
        {
            return new[]
            {
                new OperationsLinkViewModel
                {
                    Label = "Booking Details",
                    Controller = "Staff",
                    Action = nameof(BookingDetails),
                    RouteValues = new { id = bookingId }
                },
                new OperationsLinkViewModel
                {
                    Label = "Guest Details",
                    Controller = "Staff",
                    Action = nameof(GuestDetails),
                    RouteValues = new { bookingId }
                }
            };
        }
    }
}
