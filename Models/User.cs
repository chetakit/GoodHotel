using System;
using System.Collections.Generic;

namespace GoodHotel.Models;

public partial class User
{
    public int UserId { get; set; }

    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public string? Email { get; set; }

    public string? Phone { get; set; }

    public DateOnly? BirthDate { get; set; }

    public string? Password { get; set; }

    public string? Role { get; set; }

    public virtual ICollection<Booking> Bookings { get; set; } = new List<Booking>();
}
