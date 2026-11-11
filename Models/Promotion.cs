using System;
using System.Collections.Generic;

namespace GoodHotel.Models;

public partial class Promotion
{
    public int PromotionId { get; set; }

    public string? PromotionName { get; set; }

    public string? Description { get; set; }

    public string? Condition { get; set; }

    public string? Benefit { get; set; }

    public virtual ICollection<Booking> Bookings { get; set; } = new List<Booking>();
}
