using System.ComponentModel.DataAnnotations;
using GoodHotel.Services;

namespace GoodHotel.Models.ViewModels;

// ฟอร์ม Walk-in
public class WalkInViewModel
{
    [Required(ErrorMessage = "กรุณากรอกชื่อ")]
    [StringLength(50)]
    public string? FirstName { get; set; }

    [Required(ErrorMessage = "กรุณากรอกนามสกุล")]
    [StringLength(50)]
    public string? LastName { get; set; }

    [Required(ErrorMessage = "กรุณากรอกเบอร์โทร")]
    [RegularExpression(@"^0\d{8,9}$", ErrorMessage = "เบอร์โทรต้องเป็นตัวเลข 9-10 หลัก ขึ้นต้นด้วย 0")]
    public string? Phone { get; set; }

    [EmailAddress(ErrorMessage = "รูปแบบอีเมลไม่ถูกต้อง")]
    [StringLength(100)]
    public string? Email { get; set; }

    [Required(ErrorMessage = "กรุณาเลือกวันที่ออก")]
    public DateOnly? CheckOut { get; set; }

    [Required(ErrorMessage = "กรุณาระบุจำนวนผู้เข้าพัก")]
    [Range(1, 20, ErrorMessage = "จำนวนผู้เข้าพักต้องอยู่ระหว่าง 1-20")]
    public int? Guests { get; set; } = 1;

    public bool ExtraBed { get; set; }

    [Required(ErrorMessage = "กรุณาเลือกห้อง")]
    public int? RoomId { get; set; }

    // เบอร์ซ้ำและระบบตัดสินเองไม่ได้: พนักงานเลือกลูกค้า (0 = ลูกค้าใหม่)
    public int? CustomerChoice { get; set; }

    // เบอร์+ชื่อตอนที่เลือก ถ้าแก้เบอร์หรือชื่อหลังเลือก ตัวเลือกเดิมใช้ไม่ได้ ต้องเลือกใหม่
    public string? CustomerChoiceFor { get; set; }

    public string CustomerKey => $"{Phone?.Trim()}|{FirstName?.Trim()}|{LastName?.Trim()}";

    // โปรที่พนักงานเลือก (null = ให้ระบบเลือกโปรที่ลดได้มากที่สุด)
    public int? PromotionId { get; set; }

    // ยอดที่พนักงานเห็นบนหน้าจอก่อนกดยืนยัน ถ้าไม่ตรงกับยอดจริงต้องยืนยันใหม่ (กันเก็บเงินผิด)
    public decimal? ExpectedTotal { get; set; }

    // ข้อมูลสำหรับแสดงผล
    public IReadOnlyList<WalkInCustomer> Candidates { get; set; } = [];

    public DateOnly Today { get; set; }

    public IReadOnlyList<RoomStatusItem> AvailableRooms { get; set; } = [];

    // ว่างวันนี้ แต่มีคนจองต่อก่อนวันออกที่เลือก
    public IReadOnlyList<RoomStatusItem> ShorterStayRooms { get; set; } = [];

    // รหัสห้อง → วันออกช้าสุดที่พักได้ (ไม่มี = ยังไม่มีคนจองต่อ)
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public Dictionary<int, DateOnly> FreeUntil { get; set; } = [];
}
