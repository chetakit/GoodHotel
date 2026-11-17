namespace GoodHotel.Models.ViewModels;

public record PaymentListItem(int PaymentId, int? BookingId, string? Method, string? Status,
    decimal? Amount, bool IsLegacy, bool IsExcludedLegacy, bool IsNew, bool IsVerified, bool HasEvidence,
    decimal ApprovedRefundAmount)
{
    public string DisplayStatus => IsLegacy ? "Requires reconciliation" :
        IsExcludedLegacy ? "Excluded" :
        IsVerified ? (Amount > 0 && ApprovedRefundAmount >= Amount ? "Refunded" : "Paid") :
        Status ?? "Unknown";
}

public record PaymentHistoryItem(int EventId, int PaymentId, int ActorUserId, string EventType, DateTime OccurredAtUtc,
    decimal? Amount, string? Method, string? Reason);

public record PaymentRefundItem(int RefundId, int PaymentId, int? BookingId, decimal Amount, string Status, string Reason,
    int RequestedByUserId, DateTime RequestedAtUtc, int? ApprovedByUserId, DateTime? ApprovedAtUtc);

public record PaymentDetailsViewModel(int BookingId, decimal? BookingAmount, decimal ConfirmedAmount,
    decimal RefundedAmount, bool RequiresReconciliation, IReadOnlyList<PaymentListItem> Payments,
    IReadOnlyList<PaymentHistoryItem> Events, IReadOnlyList<PaymentRefundItem> Refunds);

public record PaymentManagementItem(PaymentListItem Payment, string? Customer, DateTime? TransactionAtUtc,
    DateOnly? PaymentDate);

public record PaymentQueueViewModel(IReadOnlyList<PaymentManagementItem> Payments,
    IReadOnlyList<PaymentRefundItem> Refunds, string? Search, string? Status, string? Method);
