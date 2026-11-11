
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GoodHotel.Models.ViewModels;

namespace GoodHotel.Controllers
{
    [Authorize(Roles = "Housekeeper")]
    public class HousekeeperController : Controller
    {
        [HttpGet]
        public IActionResult Index()
        {
            return Page(
                "Housekeeper Dashboard",
                "Cleaning and maintenance overview for assigned hotel work.",
                new OperationsSectionViewModel
                {
                    Title = "Today's overview",
                    Description = "Metrics will appear when the housekeeping workflow is available.",
                    Fields = new[]
                    {
                        "Pending cleaning",
                        "In progress",
                        "Completed today",
                        "Rooms requiring maintenance"
                    }
                });
        }

        [HttpGet]
        public IActionResult Tasks()
        {
            return Page(
                "Cleaning Tasks",
                "Review assigned tasks and tasks available to claim.",
                new OperationsSectionViewModel
                {
                    Title = "Assigned tasks",
                    Description = "Assigned task records are not loaded.",
                    EmptyState = "Task records have not been queried. Whether assigned tasks exist is not known."
                },
                new OperationsSectionViewModel
                {
                    Title = "Available tasks",
                    Description = "Claimable tasks are not loaded.",
                    ActionLabel = "Claim task (not implemented)",
                    DisabledActionMessage = "Claiming is disabled until task persistence and concurrency checks are implemented.",
                    EmptyState = "Task records have not been queried. Whether claimable tasks exist is not known."
                });
        }

        [HttpGet]
        public IActionResult TaskDetails(int id)
        {
            if (id <= 0)
            {
                return BadRequest();
            }

            return Page(
                "Task Details",
                $"Task #{id}: details have not been verified against a task record.",
                new OperationsSectionViewModel
                {
                    Title = "Task",
                    Description = "No task data has been loaded.",
                    EmptyState = "Task details require housekeeping backend integration.",
                    Fields = new[]
                    {
                        "Room number and type",
                        "Task status",
                        "Assignment history"
                    },
                    Actions = new[]
                    {
                        "Start task (not implemented)",
                        "Complete task (not implemented)"
                    },
                    DisabledActionMessage = "Task transitions are disabled until task persistence and concurrency checks are implemented."
                },
                taskId: id,
                links: new[]
                {
                    new OperationsLinkViewModel
                    {
                        Label = "Cleaning Tasks",
                        Controller = "Housekeeper",
                        Action = nameof(Tasks)
                    }
                });
        }

        [HttpGet]
        public IActionResult MaintenanceReports()
        {
            return Page(
                "Maintenance Reports",
                "Report room issues and indicate whether they affect safe room use.",
                new OperationsSectionViewModel
                {
                    Title = "New report",
                    Description = "Submitting a report is not enabled on this screen.",
                    Fields = new[]
                    {
                        "Room",
                        "Issue details",
                        "Blocks room use"
                    },
                    ActionLabel = "Submit report (not implemented)",
                    DisabledActionMessage = "Report submission is disabled until maintenance persistence and authorization checks are implemented."
                });
        }

        [HttpGet]
        public IActionResult MaintenanceHistory()
        {
            return Page(
                "Maintenance History",
                "Review maintenance reports relevant to housekeeping duties.",
                new OperationsSectionViewModel
                {
                    Title = "Report history",
                    Description = "Maintenance history is not loaded.",
                    EmptyState = "No report history is available. Backend integration is pending."
                });
        }

        private ViewResult Page(
            string title,
            string description,
            params OperationsSectionViewModel[] sections)
        {
            return BuildPage(title, description, null, null, sections);
        }

        private ViewResult Page(
            string title,
            string description,
            OperationsSectionViewModel section,
            int? taskId,
            OperationsLinkViewModel[] links)
        {
            return BuildPage(
                title,
                description,
                taskId,
                links,
                new[] { section });
        }

        private ViewResult BuildPage(
            string title,
            string description,
            int? taskId,
            OperationsLinkViewModel[]? links,
            OperationsSectionViewModel[] sections)
        {
            return View(
                "~/Views/Shared/OperationsPage.cshtml",
                new OperationsPageViewModel
                {
                    Title = title,
                    Description = description,
                    BackController = "Housekeeper",
                    BackAction = nameof(Index),
                    BackLabel = "Housekeeper Dashboard",
                    Sections = sections,
                    TaskId = taskId,
                    Links = links ?? Array.Empty<OperationsLinkViewModel>()
                });
        }
    }
}
