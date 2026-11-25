namespace GoodHotel.Services;

/// <summary>
/// รหัสผ่านเก่าที่ยังเก็บเป็นข้อความธรรมดา (บัญชีก่อนมีระบบ hash) จะถูกเปลี่ยนเป็น hash ตอนล็อกอินครั้งแรก
/// ต้องไม่เทียบตรงๆ กับค่าที่เป็น hash อยู่แล้ว ไม่งั้นคนที่ได้ค่า hash จากฐานข้อมูลใช้มันแทนรหัสผ่านได้
/// </summary>
public static class LegacyPassword
{
    // เทียบรหัสผ่านเก่าที่ยังไม่ถูก hash
    public static bool Matches(string stored, string entered) =>
        !IsHash(stored) && string.Equals(stored, entered, StringComparison.Ordinal);

    /// <summary>รูปแบบ hash ของ ASP.NET Identity: base64, v2 = 0x00 ยาว 49 ไบต์, v3 = 0x01 ยาวอย่างน้อย 45 ไบต์</summary>
    public static bool IsHash(string stored)
    {
        var bytes = new byte[stored.Length];
        return Convert.TryFromBase64String(stored, bytes, out var length) && length > 0 &&
               ((bytes[0] == 0x00 && length == 49) || (bytes[0] == 0x01 && length >= 45));
    }
}
