namespace GoodHotel.Models.ViewModels;

// ข้อมูลหน้าภาพรวมของ Admin
public class AdminDashboardViewModel
{
    public int RoomCount { get; set; }

    public int UserCount { get; set; }

    public int BookingCount { get; set; }

    public decimal? Revenue { get; set; }

    public int OpenMaintenanceCount { get; set; }
    public int PendingHousekeepingCount { get; set; }
    public int AwaitingVerificationCount { get; set; }

    public List<DashboardBookingViewModel> BookingsByCheckInDate { get; set; } = new();

    public List<RoomStatusCountViewModel> RoomStatusCounts { get; set; } = new();

    // กราฟ: จำนวนการจองตามวันเข้าพัก (ย้อนหลัง 13 วัน ถึงล่วงหน้า 14 วัน)
    public List<ChartPoint> BookingsByDay { get; set; } = new();

    // กราฟ: จำนวนห้องที่มีแขกพักแต่ละคืน (14 คืนข้างหน้า)
    public List<ChartPoint> NightlyOccupancy { get; set; } = new();
}

/// <summary>หนึ่งแท่งในกราฟ: Label = ป้ายใต้แท่ง, Tip = ข้อความตอนชี้, IsToday = เน้นป้ายวันนี้</summary>
public record ChartPoint(string Label, int Value, string Tip, bool IsToday = false);

/// <summary>ข้อมูลให้ partial _ColumnChart วาดกราฟแท่ง (AxisMax null = ปัดจากค่าสูงสุดให้เอง)</summary>
public record ColumnChartModel(string Id, IReadOnlyList<ChartPoint> Points, string ValueHeader, int? AxisMax = null);

// การจองหนึ่งรายการบนหน้าภาพรวม
public class DashboardBookingViewModel
{
    public int BookingId { get; set; }

    public string? GuestName { get; set; }

    public string? RoomNumber { get; set; }

    public DateOnly? CheckIn { get; set; }

    public decimal? Amount { get; set; }
}

// จำนวนห้องของแต่ละสถานะ
public class RoomStatusCountViewModel
{
    public string? Status { get; set; }

    public int Count { get; set; }
}
