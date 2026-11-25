namespace GoodHotel.Models;

// ข้อมูลหน้าข้อผิดพลาด (ไม่ใช่ตาราง)
public class ErrorViewModel
{
    public string? RequestId { get; set; }

    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
}
