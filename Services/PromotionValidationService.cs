using GoodHotel.Models;
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Services;

public enum PromotionValidationStatus
{
    Success,
    CodeNotFound,
    Inactive,
    NotStarted,
    Expired,
    InvalidMetadata,
    InvalidCode,
    DuplicateCode,
    InvalidRoomSubtotal
}

public sealed record PromotionValidationResult(
    PromotionValidationStatus Status,
    string Message,
    int? PromotionId = null,
    string? NormalizedCode = null,
    string? DiscountType = null,
    decimal? DiscountValue = null,
    decimal? RoomSubtotal = null,
    decimal? DiscountAmount = null,
    decimal? FinalRoomSubtotal = null)
{
    public bool Succeeded => Status == PromotionValidationStatus.Success;
}

public sealed class PromotionValidationService
{
    private const decimal MaximumRoomSubtotal = 99999999.99m;

    private readonly GoodHotelDbContext _db;
    private readonly PromotionMetadataService _metadataService;
    private readonly TimeProvider _timeProvider;

    public PromotionValidationService(
        GoodHotelDbContext db,
        PromotionMetadataService metadataService,
        TimeProvider timeProvider)
    {
        _db = db;
        _metadataService = metadataService;
        _timeProvider = timeProvider;
    }

    public async Task<PromotionValidationResult> ValidateAsync(
        string? code,
        decimal roomSubtotal,
        CancellationToken cancellationToken = default)
    {
        var normalizedCode = _metadataService.NormalizeCode(code);
        if (!_metadataService.IsValidCode(normalizedCode))
        {
            return Failure(
                PromotionValidationStatus.InvalidCode,
                "รูปแบบ Promotion Code ไม่ถูกต้อง");
        }

        if (roomSubtotal <= 0 ||
            roomSubtotal > MaximumRoomSubtotal ||
            decimal.Round(roomSubtotal, 2) != roomSubtotal)
        {
            return Failure(
                PromotionValidationStatus.InvalidRoomSubtotal,
                "ยอดค่าห้องต้องมากกว่า 0 และอยู่ในขอบเขตที่ระบบรองรับ");
        }

        var promotions = await _db.Promotions
            .AsNoTracking()
            .Select(promotion => new
            {
                promotion.PromotionId,
                promotion.Condition
            })
            .ToListAsync(cancellationToken);

        var matchingPromotions = new List<(int PromotionId, PromotionMetadata Metadata)>();
        var matchingInvalidMetadata = false;

        foreach (var promotion in promotions)
        {
            var parsed = _metadataService.Parse(promotion.Condition);
            if (parsed.Metadata is { } metadata &&
                string.Equals(
                    metadata.Code,
                    normalizedCode,
                    StringComparison.OrdinalIgnoreCase))
            {
                matchingPromotions.Add((promotion.PromotionId, metadata));
            }
            else if (parsed.Error != null &&
                     CouldContainCode(promotion.Condition, normalizedCode))
            {
                matchingInvalidMetadata = true;
            }
        }

        if (matchingInvalidMetadata)
        {
            return Failure(
                PromotionValidationStatus.InvalidMetadata,
                "ข้อมูลของ Promotion Code นี้ไม่ถูกต้อง");
        }

        if (matchingPromotions.Count > 1)
        {
            return Failure(
                PromotionValidationStatus.DuplicateCode,
                "พบ Promotion Code ซ้ำ จึงไม่สามารถใช้ Code นี้ได้");
        }

        if (matchingPromotions.Count == 0)
        {
            return Failure(
                PromotionValidationStatus.CodeNotFound,
                "ไม่พบ Promotion Code นี้");
        }

        var (promotionId, matchingMetadata) = matchingPromotions[0];
        var bookingDate = DateOnly.FromDateTime(
            _timeProvider.GetLocalNow().DateTime);

        if (!matchingMetadata.IsActive)
        {
            return Failure(
                PromotionValidationStatus.Inactive,
                "Promotion Code นี้ไม่ได้เปิดใช้งาน");
        }

        if (bookingDate < matchingMetadata.StartDate)
        {
            return Failure(
                PromotionValidationStatus.NotStarted,
                "Promotion Code นี้ยังไม่ถึงวันเริ่มใช้งาน");
        }

        if (bookingDate > matchingMetadata.EndDate)
        {
            return Failure(
                PromotionValidationStatus.Expired,
                "Promotion Code นี้หมดอายุแล้ว");
        }

        var discountAmount = _metadataService.CalculateDiscount(
            matchingMetadata,
            roomSubtotal,
            bookingDate);

        return new PromotionValidationResult(
            PromotionValidationStatus.Success,
            "ใช้ Promotion Code สำเร็จ",
            promotionId,
            matchingMetadata.Code,
            matchingMetadata.DiscountType,
            matchingMetadata.DiscountValue,
            roomSubtotal,
            discountAmount,
            roomSubtotal - discountAmount);
    }

    private bool CouldContainCode(string? condition, string normalizedCode)
    {
        if (condition == null ||
            !condition.StartsWith(
                PromotionMetadataService.VersionPrefix + "|",
                StringComparison.Ordinal))
        {
            return false;
        }

        var fields = condition.Split('|');
        return fields.Length > 1 &&
               string.Equals(
                   _metadataService.NormalizeCode(fields[1]),
                   normalizedCode,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static PromotionValidationResult Failure(
        PromotionValidationStatus status,
        string message) =>
        new(status, message);
}
