namespace GoodHotel.Models.ViewModels;

public class MyBookingViewModel
{
    public int BookingId { get; set; }

    public string? ImagePath { get; set; }

    public string? RoomNumber { get; set; }

    public string? RoomType { get; set; }

    public DateOnly? CheckIn { get; set; }

    public DateOnly? CheckOut { get; set; }

    public int? Guests { get; set; }

    public int? NightCount { get; set; }

    public decimal? Amount { get; set; }
}
