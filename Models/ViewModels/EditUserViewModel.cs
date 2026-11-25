using System.ComponentModel.DataAnnotations;

namespace GoodHotel.Models.ViewModels;

// ฟอร์มแก้บัญชี
public class EditUserViewModel
{
    public int UserId { get; set; }

    [Required, StringLength(50)]
    public string? FirstName { get; set; }

    [Required, StringLength(50)]
    public string? LastName { get; set; }

    // ลูกค้า Walk-in อาจไม่มีอีเมล จึงเช็คว่าต้องกรอกหรือไม่ใน controller
    [StringLength(100), EmailAddress(ErrorMessage = "กรุณากรอกอีเมลให้ถูกต้อง")]
    public string? Email { get; set; }

    [Required, StringLength(20)]
    public string? Phone { get; set; }

    [Required, StringLength(20)]
    public string? Role { get; set; }

    // เว้นว่าง = ไม่เปลี่ยนรหัสผ่าน
    [StringLength(255, MinimumLength = 8, ErrorMessage = "รหัสผ่านใหม่ต้องยาว 8 ตัวอักษรขึ้นไป")]
    public string? NewPassword { get; set; }

    [Compare(nameof(NewPassword), ErrorMessage = "รหัสผ่านใหม่ทั้งสองช่องไม่ตรงกัน")]
    public string? ConfirmPassword { get; set; }
}
