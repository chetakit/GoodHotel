using System;
using System.Collections.Generic;

namespace GoodHotel.Models;

public partial class Booking
{
    public int BookingId { get; set; }

    public int? UserId { get; set; }

    public int? RoomId { get; set; }

    public DateOnly? CheckIn { get; set; }

    public DateOnly? CheckOut { get; set; }

    public int? Guests { get; set; }

    public int? ExtraBed { get; set; }

    public int? PromotionId { get; set; }

    public decimal? Amount { get; set; }

    public virtual ICollection<Payment> Payments { get; set; } = new List<Payment>();

    public virtual Promotion? Promotion { get; set; }

    public virtual Room? Room { get; set; }

    public virtual User? User { get; set; }
}
