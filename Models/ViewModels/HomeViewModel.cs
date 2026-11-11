namespace GoodHotel.Models.ViewModels;

public class HomeViewModel
{
    public List<RoomListViewModel> FeaturedRooms { get; set; } = new();

    public List<PromotionListViewModel> Promotions { get; set; } = new();
}
