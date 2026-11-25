namespace GoodHotel.Models;

// ตาราง users: ผู้ใช้ทุก role
public partial class User
{
    public int UserId { get; set; } // รหัสผู้ใช้ (primary key)

    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public string? Email { get; set; } // ใช้ล็อกอิน

    public string? Phone { get; set; }

    public DateOnly? BirthDate { get; set; } // ใช้กับโปรเดือนเกิด

    public string? Password { get; set; } // เก็บแบบ hash

    public string? Role { get; set; } // Customer, Staff, Housekeeper, Accountant, Admin

    public bool IsActive { get; set; } = true; // false = เข้าระบบไม่ได้

    public virtual ICollection<Booking> Bookings { get; set; } = new List<Booking>();
}
