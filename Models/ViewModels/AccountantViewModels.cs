namespace GoodHotel.Models.ViewModels;

/// <summary>ยอดเงินของช่วงเวลาหนึ่ง: รับเข้า - คืนออก = สุทธิ</summary>
public sealed record MoneyTotal(decimal Received, decimal Refunded)
{
    public decimal Net => Received - Refunded;
}

public sealed record MethodTotal(string Method, int Count, decimal Amount);

/// <summary>
/// หนึ่งรายการในสมุดรายวัน: เงินรับ (การชำระที่ยืนยันแล้ว) หรือเงินคืน (คำขอคืนที่อนุมัติแล้ว)
/// </summary>
public sealed record LedgerEntry(
    DateTime OccurredAtUtc,
    bool IsRefund,
    int PaymentId,
    int? BookingId,
    string? Method,
    decimal Amount,
    string? ActorName,
    string? GuestName);

public sealed record AccountantDashboardViewModel(
    MoneyTotal Today,
    MoneyTotal ThisMonth,
    MoneyTotal AllTime,
    int AwaitingReview,
    int LegacyToReconcile,
    int RefundRequests,
    IReadOnlyList<MethodTotal> MonthByMethod,
    IReadOnlyList<LedgerEntry> TodayLedger);

public sealed record DailyCloseViewModel(
    DateOnly Date,
    MoneyTotal Total,
    IReadOnlyList<MethodTotal> ByMethod,
    IReadOnlyList<LedgerEntry> Entries);

public sealed record FinanceReportViewModel(
    DateOnly From,
    DateOnly To,
    MoneyTotal Total,
    decimal BookedAmount,
    int BookingCount,
    IReadOnlyList<MethodTotal> ByMethod,
    IReadOnlyList<DailyRow> Daily,
    IReadOnlyList<PromotionUsageViewModel> Promotions);

/// <summary>หนึ่งเหตุการณ์ในหน้า Audit: การกระทำเรื่องเงิน (payment_events) หรือการยกเลิกการจอง</summary>
public sealed record AuditEntry(
    DateTime OccurredAtUtc,
    string Type,
    string TypeLabel,
    int? BookingId,
    int? PaymentId,
    string? Actor,
    string? ActorRole,
    decimal? Amount,
    string? Method,
    string? Detail);

public sealed record AuditViewModel(
    DateOnly From,
    DateOnly To,
    string? Type,
    int? BookingId,
    string? Actor,
    IReadOnlyList<AuditEntry> Entries,
    IReadOnlyList<KeyValuePair<string, string>> Types,
    bool Truncated);

public sealed record DailyRow(DateOnly Date, decimal Received, decimal Refunded)
{
    public decimal Net => Received - Refunded;
}
