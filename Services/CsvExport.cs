using System.Globalization;
using System.Text;

namespace GoodHotel.Services;

/// <summary>สร้างไฟล์ CSV (UTF-8 มี BOM ให้ Excel อ่านภาษาไทยได้) ใช้ร่วมกันระหว่าง Admin และฝ่ายบัญชี</summary>
public static class CsvExport
{
    public static byte[] Build(string[] headers, IEnumerable<string?[]> rows)
    {
        var builder = new StringBuilder();
        void AppendRow(IEnumerable<string?> values) =>
            builder.AppendJoin(",", values.Select(Escape)).Append("\r\n");
        AppendRow(headers);
        foreach (var row in rows) AppendRow(row);
        var encoding = new UTF8Encoding(true);
        return encoding.GetPreamble().Concat(encoding.GetBytes(builder.ToString())).ToArray();
    }

    // กันสูตร Excel (ค่าที่ขึ้นต้นด้วย = + - @) ไม่ให้ถูกรันตอนเปิดไฟล์
    // ยกเว้นจำนวนติดลบ เช่น -7650.00 (ยอดคืนเงิน) ไม่งั้น Excel มองเป็นข้อความแล้ว SUM ไม่หักให้
    private static string Escape(string? value)
    {
        value ??= string.Empty;
        var trimmed = value.TrimStart();
        if (value.StartsWith('\t') || value.StartsWith('\r') || value.StartsWith('\n') ||
            (trimmed.Length > 0 && value != "-" && "=+-@".Contains(trimmed[0]) && !IsNegativeNumber(value)))
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static bool IsNegativeNumber(string value) => value.StartsWith('-') &&
        decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _);

    /// <summary>แปลงตัวเลข/วันที่เป็นข้อความรูปแบบกลาง (ข้อความธรรมดาส่งตรงได้เลย ไม่ต้องผ่านตัวนี้)</summary>
    public static string? Format(object? value) => value switch
    {
        null => null,
        bool boolean => boolean ? "true" : "false",
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new ArgumentException("Unsupported CSV value.", nameof(value))
    };
}
