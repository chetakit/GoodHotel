using GoodHotel.Services;

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
    IReadOnlyList<PaymentHistoryItem> Events, IReadOnlyList<PaymentRefundItem> Refunds,
    string? BookingStatus = null, string? GuestName = null, string? RoomNumber = null,
    DateTime? CreatedAtUtc = null, string? CancelReason = null)
{
    public bool IsCancelled => BookingStatus == BookingStatuses.Cancelled;

    // เส้นตายชำระเงิน: แสดงเฉพาะการจองที่ยังไม่ยกเลิก ยังไม่จ่าย และยังไม่ได้เลือกวิธีที่รอยืนยัน (เช่นเงินสด)
    public DateTime? PaymentDueAtUtc => IsCancelled || Payments.Any(p => p.IsVerified ||
        (p.IsNew && (p.Status == "Pending" || p.Status == "Submitted")))
        ? null : PaymentDeadline.DueAtUtc(CreatedAtUtc);

    /// <summary>เงินที่โรงแรมถือไว้จริง = รับแล้ว - คืนแล้ว</summary>
    public decimal NetReceived => ConfirmedAmount - RefundedAmount;

    /// <summary>
    /// ยอดที่ลูกค้ายังต้องจ่าย: การจองที่ยกเลิกแล้วไม่มียอดค้าง
    /// การคืนเงินไม่ทำให้เกิดหนี้ใหม่ (เดิมบวกยอดคืนกลับเข้าไป ทำให้ยกเลิก+คืนเงินแล้วยังขึ้นว่าค้างจ่ายเต็มจำนวน)
    /// </summary>
    public decimal? Outstanding => RequiresReconciliation || !BookingAmount.HasValue ? null
        : IsCancelled ? 0m
        : Math.Max(0m, BookingAmount.Value - ConfirmedAmount);
}

public record PaymentManagementItem(PaymentListItem Payment, string? Customer, DateTime? TransactionAtUtc,
    DateOnly? PaymentDate);

public record PaymentQueueViewModel(IReadOnlyList<PaymentManagementItem> Payments,
    IReadOnlyList<PaymentRefundItem> Refunds, string? Search, string? Status, string? Method);
