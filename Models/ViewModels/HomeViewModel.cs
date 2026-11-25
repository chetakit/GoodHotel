namespace GoodHotel.Models.ViewModels;

// ข้อมูลหน้าแรก
// ViewModel = ข้อมูลเฉพาะที่หน้านั้นต้องใช้ ไม่ส่งทั้งตาราง
public class HomeViewModel
{
    public List<RoomListViewModel> FeaturedRooms { get; set; } = new();

    public List<PromotionListViewModel> Promotions { get; set; } = new();
}
