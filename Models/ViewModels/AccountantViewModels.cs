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
    int RefundRequests,
    int LegacyToReconcile,
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

public sealed record DailyRow(DateOnly Date, decimal Received, decimal Refunded)
{
    public decimal Net => Received - Refunded;
}
