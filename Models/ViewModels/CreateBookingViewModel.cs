using System.ComponentModel.DataAnnotations;

namespace GoodHotel.Models.ViewModels;

public class CreateBookingViewModel
{
    [Range(1, int.MaxValue, ErrorMessage = "กรุณาเลือกห้องพัก")]
    public int RoomId { get; set; }

    [Required(ErrorMessage = "กรุณาเลือกวันที่เข้าพัก")]
    public DateOnly? CheckIn { get; set; }

    [Required(ErrorMessage = "กรุณาเลือกวันที่ออก")]
    public DateOnly? CheckOut { get; set; }

    [Required(ErrorMessage = "กรุณาระบุจำนวนผู้เข้าพัก")]
    [Range(1, int.MaxValue, ErrorMessage = "จำนวนผู้เข้าพักต้องมากกว่า 0")]
    public int? Guests { get; set; }

    [Range(0, 1, ErrorMessage = "จำนวนเตียงเสริมต้องเป็น 0 หรือ 1")]
    public int ExtraBedCount { get; set; }

    [StringLength(64)]
    public string? PromotionCode { get; set; }
}
