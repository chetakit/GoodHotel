using System.ComponentModel.DataAnnotations;

namespace GoodHotel.Models.ViewModels;

// ฟอร์มโปรไฟล์ลูกค้า
public class ProfileViewModel
{
    [Required(ErrorMessage = "กรุณากรอกชื่อ")]
    [StringLength(50, ErrorMessage = "ชื่อต้องยาวไม่เกิน 50 ตัวอักษร")]
    public string? FirstName { get; set; }

    [Required(ErrorMessage = "กรุณากรอกนามสกุล")]
    [StringLength(50, ErrorMessage = "นามสกุลต้องยาวไม่เกิน 50 ตัวอักษร")]
    public string? LastName { get; set; }

    [Required(ErrorMessage = "กรุณากรอกเบอร์โทร")]
    [StringLength(20, ErrorMessage = "กรุณากรอกเบอร์มือถือ 10 หลัก ขึ้นต้นด้วย 06, 08 หรือ 09")]
    public string? Phone { get; set; }

    // แสดงอย่างเดียว: อีเมลใช้ล็อกอิน วันเกิดใช้กับโปรเดือนเกิด
    public string? Email { get; set; }
    public DateOnly? BirthDate { get; set; }

    /// <summary>"089 111 2233" → "0891112233" (ไม่ใช่เบอร์มือถือไทย = null)</summary>
    /// <summary>วันเกิดต้องเป็น ค.ศ. ไม่อยู่ในอนาคต และไม่เกิน 120 ปี (กันกรอกปี พ.ศ. ซึ่งทำให้วันเกิดเพี้ยน)</summary>
    public static bool IsValidBirthDate(DateOnly date, DateOnly today) => date <= today && date > today.AddYears(-120);

    // แปลงเบอร์ให้เหลือแต่ตัวเลข
    public static string? NormalizePhone(string? phone)
    {
        var digits = phone?.Replace(" ", "");
        return digits?.Length == 10 && digits.All(char.IsAsciiDigit) && digits[0] == '0' && (digits[1] is '6' or '8' or '9')
            ? digits : null;
    }
}
