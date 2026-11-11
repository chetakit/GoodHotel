using System;
using System.Collections.Generic;

namespace GoodHotel.Models;

public partial class Room
{
    public int RoomId { get; set; }

    public string? Room1 { get; set; }

    public string? Type { get; set; }

    public string? Description { get; set; }

    public int? Capacity { get; set; }

    public string? Status { get; set; }

    public decimal? Rate { get; set; }

    public string? ImagePath { get; set; }

    public virtual ICollection<Booking> Bookings { get; set; } = new List<Booking>();
}
