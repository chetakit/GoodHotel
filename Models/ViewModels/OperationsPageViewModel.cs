namespace GoodHotel.Models.ViewModels;

public sealed class OperationsPageViewModel
{
    public required string Title { get; init; }

    public required string Description { get; init; }

    public required string BackController { get; init; }

    public required string BackAction { get; init; }

    public string? BackLabel { get; init; }

    public int? BookingId { get; init; }

    public int? TaskId { get; init; }

    public int? ReportId { get; init; }

    public IReadOnlyList<OperationsSectionViewModel> Sections { get; init; } =
        Array.Empty<OperationsSectionViewModel>();

    public IReadOnlyList<OperationsLinkViewModel> Links { get; init; } =
        Array.Empty<OperationsLinkViewModel>();
}

public sealed class OperationsSectionViewModel
{
    public required string Title { get; init; }

    public required string Description { get; init; }

    public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> InputFields { get; init; } = Array.Empty<string>();

    public string? ActionLabel { get; init; }

    public IReadOnlyList<string> Actions { get; init; } = Array.Empty<string>();

    public string? EmptyState { get; init; }

    public string? DisabledActionMessage { get; init; }
}

public sealed class OperationsLinkViewModel
{
    public required string Label { get; init; }

    public required string Controller { get; init; }

    public required string Action { get; init; }

    public object? RouteValues { get; init; }
}
