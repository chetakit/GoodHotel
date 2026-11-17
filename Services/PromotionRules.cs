using System.Globalization;
using GoodHotel.Models;

namespace GoodHotel.Services;

/// <summary>ประเภทโปรทั้งหมด + ข้อความภาษาไทยสำหรับแสดงผล</summary>
public static class PromotionRules
{
    public const string EarlyBird = "EarlyBird";
    public const string LongStay = "LongStay";
    public const string Weekday = "Weekday";
    public const string LastMinute = "LastMinute";
    public const string BirthdayMonth = "BirthdayMonth";
    public const string FirstStay = "FirstStay";
    public const string LoyalGuest = "LoyalGuest";
    public const string Seasonal = "Seasonal";

    public const string Percent = "Percent";
    public const string Fixed = "Fixed";

    /// <summary>ประเภทโปร → (ชื่อที่แสดง, คำอธิบายช่อง X หรือ null ถ้าไม่ต้องกรอก X)</summary>
    public static readonly IReadOnlyDictionary<string, (string Label, string? ValueLabel)> All =
        new Dictionary<string, (string, string?)>
        {
            [EarlyBird] = ("Early Bird – จองล่วงหน้า", "จองก่อนวันเข้าพักอย่างน้อย (วัน)"),
            [LongStay] = ("Long Stay – พักยาว", "พักอย่างน้อย (คืน)"),
            [Weekday] = ("Weekday – พักวันธรรมดา (อา.–พฤ.)", null),
            [LastMinute] = ("Last Minute – จองด่วน", "จองก่อนวันเข้าพักไม่เกิน (วัน)"),
            [BirthdayMonth] = ("Birthday Month – เข้าพักในเดือนเกิด", null),
            [FirstStay] = ("First Stay – จองครั้งแรก", null),
            [LoyalGuest] = ("Loyal Guest – ลูกค้าประจำ", "เคยเข้าพักจบแล้วอย่างน้อย (ครั้ง)"),
            [Seasonal] = ("Seasonal – ช่วงเทศกาล (ใช้ช่วงวันที่อย่างเดียว)", null)
        };

    public static bool NeedsValue(string? ruleType) =>
        ruleType != null && All.TryGetValue(ruleType, out var rule) && rule.ValueLabel != null;

    /// <summary>เงื่อนไขแบบอ่านง่าย เช่น "จองล่วงหน้าอย่างน้อย 30 วัน · เข้าพัก 28/12/2026–05/01/2027"</summary>
    public static string DescribeCondition(Promotion promotion)
    {
        var x = promotion.RuleValue;
        var parts = new List<string>
        {
            promotion.RuleType switch
            {
                EarlyBird => $"จองล่วงหน้าอย่างน้อย {x} วัน",
                LongStay => $"พักตั้งแต่ {x} คืนขึ้นไป",
                Weekday => "ทุกคืนที่พักเป็นวันอาทิตย์–พฤหัสบดี",
                LastMinute => $"จองภายใน {x} วันก่อนเข้าพัก",
                BirthdayMonth => "เข้าพักในเดือนเกิดของคุณ",
                FirstStay => "การจองครั้งแรกของบัญชี",
                LoyalGuest => $"เคยเข้าพักกับเรามาแล้ว {x} ครั้งขึ้นไป",
                Seasonal => "เข้าพักในช่วงเทศกาล",
                _ => "ยังไม่ได้ตั้งเงื่อนไข"
            }
        };

        if (promotion.StartDate.HasValue || promotion.EndDate.HasValue)
        {
            parts.Add($"เข้าพัก {FormatDate(promotion.StartDate) ?? "วันนี้"}–{FormatDate(promotion.EndDate) ?? "ไม่จำกัด"}");
        }

        if (promotion.MinSpend > 0)
        {
            parts.Add($"ค่าห้องขั้นต่ำ ฿{promotion.MinSpend:N0}");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>ส่วนลดแบบอ่านง่าย เช่น "ลด 15% (สูงสุด ฿2,000)" หรือ "ลด ฿1,000"</summary>
    public static string DescribeDiscount(Promotion promotion) =>
        promotion.DiscountType == Percent
            ? $"ลด {promotion.DiscountValue:0.##}%" + (promotion.MaxDiscount > 0 ? $" (สูงสุด ฿{promotion.MaxDiscount:N0})" : "")
            : $"ลด ฿{promotion.DiscountValue:N0}";

    private static string? FormatDate(DateOnly? date) =>
        date?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}
