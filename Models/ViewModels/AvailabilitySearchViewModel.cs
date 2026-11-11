namespace GoodHotel.Models.ViewModels;

public class AvailabilitySearchViewModel
{
    public DateOnly? CheckIn { get; set; }

    public DateOnly? CheckOut { get; set; }

    public int? Guests { get; set; }

    public bool HasSearchCriteria { get; set; }

    public bool HasSearched { get; set; }

    public List<RoomListViewModel> Rooms { get; set; } = new();
}
