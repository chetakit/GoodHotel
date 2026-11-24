using System.ComponentModel.DataAnnotations;

namespace GoodHotel.Models.ViewModels;

public class ChangePasswordViewModel
{
    [Required(ErrorMessage = "กรุณากรอกรหัสผ่านปัจจุบัน")]
    public string? CurrentPassword { get; set; }

    [Required(ErrorMessage = "กรุณากรอกรหัสผ่านใหม่")]
    [StringLength(255, MinimumLength = 8, ErrorMessage = "รหัสผ่านใหม่ต้องยาว 8 ตัวอักษรขึ้นไป")]
    public string? NewPassword { get; set; }

    [Required(ErrorMessage = "กรุณายืนยันรหัสผ่านใหม่")]
    [Compare(nameof(NewPassword), ErrorMessage = "รหัสผ่านใหม่ทั้งสองช่องไม่ตรงกัน")]
    public string? ConfirmPassword { get; set; }
}
