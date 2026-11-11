namespace GoodHotel.Models.ViewModels;

public class RoomListViewModel
{
    public int RoomId { get; set; }

    public string? RoomNumber { get; set; }

    public string? Type { get; set; }

    public int? Capacity { get; set; }

    public string? Status { get; set; }

    public decimal? Rate { get; set; }

    public string? ImagePath { get; set; }

    public string? Description { get; set; }
}
