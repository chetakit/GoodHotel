namespace GoodHotel.Models;

// ตาราง rooms: ห้องพัก
public partial class Room
{
    public int RoomId { get; set; } // รหัสห้อง (primary key)

    public string? Room1 { get; set; } // เลขห้องที่แสดง

    public string? Type { get; set; } // ประเภทห้อง

    public string? Description { get; set; }

    public int? Capacity { get; set; } // พักได้กี่คน

    public string? Status { get; set; } // เปิดขาย/ปิดขาย/ซ่อม (Admin ตั้งเอง)

    public string? PreMaintenanceStatus { get; set; } // สถานะก่อนซ่อม

    public decimal? Rate { get; set; } // ราคาต่อคืน

    public string? ImagePath { get; set; } // ที่อยู่รูป

    public virtual ICollection<Booking> Bookings { get; set; } = new List<Booking>();
}
