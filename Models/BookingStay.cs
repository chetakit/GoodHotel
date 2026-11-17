namespace GoodHotel.Models;

public class BookingStay
{
    public int BookingId { get; set; }
    public string Status { get; set; } = "CheckedIn";
    public DateTime CheckedInAtUtc { get; set; }
    public int CheckedInByUserId { get; set; }
    public DateTime? CheckedOutAtUtc { get; set; }
    public int? CheckedOutByUserId { get; set; }
    public bool IsEarlyCheckIn { get; set; }
    public string? CheckInNote { get; set; }

    public Booking Booking { get; set; } = null!;
    public User CheckedInByUser { get; set; } = null!;
    public User? CheckedOutByUser { get; set; }
}
