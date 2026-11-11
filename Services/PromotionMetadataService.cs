using System.Globalization;
using System.Text.RegularExpressions;
using GoodHotel.Models;

namespace GoodHotel.Services;

public sealed record PromotionMetadata(
    string Code,
    string DiscountType,
    decimal DiscountValue,
    DateOnly StartDate,
    DateOnly EndDate,
    bool IsActive);

public sealed record PromotionMetadataParseResult(
    bool IsLegacy,
    PromotionMetadata? Metadata,
    string? Error);

public sealed class PromotionMetadataService
{
    public const string VersionPrefix = "GH_PROMO_V1";
    public const int MaximumConditionLength = 255;
    private const decimal MaximumDiscountValue = 99999999.99m;
    private static readonly Regex CodePattern = new(
        "^[A-Z0-9_-]{1,32}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public PromotionMetadataParseResult Parse(string? condition)
    {
        if (condition == null ||
            !condition.StartsWith(VersionPrefix + "|", StringComparison.Ordinal))
        {
            return new PromotionMetadataParseResult(true, null, null);
        }

        var fields = condition.Split('|');
        if (fields.Length != 7 ||
            !string.Equals(fields[0], VersionPrefix, StringComparison.Ordinal))
        {
            return InvalidMetadata("Promotion metadata has an invalid field count.");
        }

        if (!string.Equals(fields[1], NormalizeCode(fields[1]), StringComparison.Ordinal) ||
            !IsValidCode(fields[1]))
        {
            return InvalidMetadata("Promotion metadata contains an invalid code.");
        }

        if (fields[2] is not ("PERCENT" or "FIXED"))
        {
            return InvalidMetadata("Promotion metadata contains an invalid discount type.");
        }

        if (!decimal.TryParse(
                fields[3],
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return InvalidMetadata("Promotion metadata contains an invalid discount value.");
        }

        if (!DateOnly.TryParseExact(
                fields[4],
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var startDate) ||
            !DateOnly.TryParseExact(
                fields[5],
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var endDate))
        {
            return InvalidMetadata("Promotion metadata contains an invalid date.");
        }

        if (fields[6] is not ("0" or "1"))
        {
            return InvalidMetadata("Promotion metadata contains an invalid active status.");
        }

        var metadata = new PromotionMetadata(
            fields[1],
            fields[2],
            value,
            startDate,
            endDate,
            fields[6] == "1");
        var validationError = Validate(metadata, out _);
        return validationError == null
            ? new PromotionMetadataParseResult(false, metadata, null)
            : InvalidMetadata(validationError);
    }

    public string? Validate(
        PromotionMetadata metadata,
        out PromotionMetadata normalized)
    {
        var code = NormalizeCode(metadata.Code);
        normalized = metadata with { Code = code };

        if (!IsValidCode(code))
        {
            return "Code ต้องมีความยาว 1–32 ตัว และใช้ได้เฉพาะ A–Z, 0–9, _ หรือ -";
        }

        if (metadata.DiscountType is not ("PERCENT" or "FIXED"))
        {
            return "เลือกประเภทส่วนลดไม่ถูกต้อง";
        }

        if (metadata.DiscountValue < 0 ||
            metadata.DiscountValue > MaximumDiscountValue ||
            decimal.Round(metadata.DiscountValue, 2) != metadata.DiscountValue)
        {
            return "มูลค่าส่วนลดต้องมีทศนิยมไม่เกิน 2 ตำแหน่งและอยู่ในขอบเขตที่รองรับ";
        }

        if (metadata.DiscountType == "PERCENT" &&
            (metadata.DiscountValue < 0 || metadata.DiscountValue > 100))
        {
            return "ส่วนลดเปอร์เซ็นต์ต้องอยู่ระหว่าง 0 ถึง 100";
        }

        if (metadata.DiscountType == "FIXED" &&
            metadata.DiscountValue <= 0)
        {
            return "ส่วนลดแบบจำนวนเงินต้องมากกว่า 0";
        }

        if (metadata.StartDate > metadata.EndDate)
        {
            return "วันเริ่มต้นต้องไม่อยู่หลังวันสิ้นสุด";
        }

        var serialized = SerializeUnchecked(normalized);
        if (serialized.Length > MaximumConditionLength)
        {
            return $"ข้อมูลโปรโมชั่นยาวเกิน {MaximumConditionLength} ตัวอักษร";
        }

        return null;
    }

    public string Serialize(PromotionMetadata metadata)
    {
        var error = Validate(metadata, out var normalized);
        if (error != null)
        {
            throw new ArgumentException(error, nameof(metadata));
        }

        return SerializeUnchecked(normalized);
    }

    public string NormalizeCode(string? code) =>
        (code ?? string.Empty).Trim().ToUpperInvariant();

    public bool TryParseDate(string? value, out DateOnly date)
    {
        date = default;
        return DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    public bool IsActiveOn(PromotionMetadata metadata, DateOnly bookingDate) =>
        metadata.IsActive &&
        bookingDate >= metadata.StartDate &&
        bookingDate <= metadata.EndDate;

    public decimal CalculateDiscount(
        PromotionMetadata metadata,
        decimal roomAmount,
        DateOnly bookingDate)
    {
        if (roomAmount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(roomAmount),
                "Room amount cannot be negative.");
        }

        var validationError = Validate(metadata, out var normalized);
        if (validationError != null)
        {
            throw new ArgumentException(validationError, nameof(metadata));
        }

        if (!IsActiveOn(normalized, bookingDate))
        {
            throw new InvalidOperationException(
                "The promotion is not active on the booking date.");
        }

        var discount = normalized.DiscountType == "PERCENT"
            ? decimal.Round(
                roomAmount * normalized.DiscountValue / 100m,
                2,
                MidpointRounding.AwayFromZero)
            : normalized.DiscountValue;

        return Math.Min(discount, roomAmount);
    }

    public bool IsValidCode(string code) => CodePattern.IsMatch(code);

    private static string SerializeUnchecked(PromotionMetadata metadata) =>
        string.Join(
            '|',
            VersionPrefix,
            metadata.Code,
            metadata.DiscountType,
            metadata.DiscountValue.ToString("0.##", CultureInfo.InvariantCulture),
            metadata.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            metadata.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            metadata.IsActive ? "1" : "0");

    private static PromotionMetadataParseResult InvalidMetadata(string error) =>
        new(false, null, error);
}
