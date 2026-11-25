namespace GoodHotel.Services;

/// <summary>
/// แปลงค่าสถานะที่เก็บในฐานข้อมูล (ภาษาอังกฤษ) เป็นข้อความภาษาไทยสำหรับแสดงผล
/// ค่าในฐานข้อมูลยังเป็นภาษาอังกฤษเหมือนเดิม เปลี่ยนแค่ตอนแสดงบนหน้าเว็บ
/// </summary>
public static class ThaiText
{
    private static readonly Dictionary<string, string> Statuses = new(StringComparer.OrdinalIgnoreCase)
    {
        // การจอง / การเข้าพัก
        ["Booked"] = "จองแล้ว",
        ["Cancelled"] = "ยกเลิกแล้ว",
        ["CheckedIn"] = "เข้าพักอยู่",
        ["CheckedOut"] = "เช็คเอาท์แล้ว",

        // การชำระเงิน / คืนเงิน
        ["Not submitted"] = "ยังไม่ชำระ",
        ["Pending"] = "รอดำเนินการ",
        ["Submitted"] = "ส่งหลักฐานแล้ว",
        ["Paid"] = "ชำระแล้ว",
        ["Rejected"] = "ถูกปฏิเสธ",
        ["Refunded"] = "คืนเงินแล้ว",
        ["Requested"] = "รออนุมัติ",
        ["Approved"] = "อนุมัติแล้ว",
        ["Requires reconciliation"] = "ต้องตรวจสอบข้อมูลเก่า",
        ["Excluded"] = "ไม่นับรวม",
        ["Confirmed"] = "ยืนยันแล้ว",

        // งานแม่บ้าน
        ["Accepted"] = "รับงานแล้ว",
        ["InProgress"] = "กำลังทำ",
        ["In Progress"] = "กำลังซ่อม",
        ["Completed"] = "ทำเสร็จแล้ว",
        ["Verified"] = "ตรวจผ่านแล้ว",
        ["CheckoutCleaning"] = "ทำความสะอาดหลังเช็คเอาท์",
        ["Created"] = "สร้างงาน",
        ["Claim"] = "รับงาน",
        ["Start"] = "เริ่มทำ",
        ["Complete"] = "ทำเสร็จ",
        ["Verify"] = "ตรวจผ่าน",
        ["Assign"] = "มอบหมายงาน",
        ["Reassign"] = "เปลี่ยนผู้รับงาน",

        // งานซ่อม
        ["Open"] = "แจ้งแล้ว",
        ["Resolved"] = "ซ่อมเสร็จแล้ว",

        // ห้องพัก (ค่าที่ Admin ตั้ง)
        ["Available"] = "ว่าง",
        ["Occupied"] = "มีผู้เข้าพัก",
        ["Reserved"] = "ปิดขาย",
        ["Maintenance"] = "ซ่อมบำรุง",

        // ประวัติการชำระเงิน
        ["Submission"] = "ส่งการชำระเงิน",
        ["MockConfirmed"] = "ชำระแบบจำลอง (ยืนยันอัตโนมัติ)",
        ["Confirmation"] = "ยืนยันรับเงิน",
        ["Rejection"] = "ปฏิเสธการชำระเงิน",
        ["DeskCashReceived"] = "รับเงินสดที่เคาน์เตอร์",
        ["RefundRequested"] = "ขอคืนเงิน",
        ["RefundApproved"] = "อนุมัติคืนเงิน",
        ["RefundRejected"] = "ปฏิเสธการคืนเงิน",
        ["RoomMoved"] = "ย้ายห้อง",
        ["Rework"] = "ตรวจไม่ผ่าน ให้ทำใหม่",
        ["RefundClosed"] = "ปิดคำขอคืนเงิน (เช็คอินแล้ว)",
        ["Closed"] = "ปิดแล้ว",
        ["LegacyConfirmed"] = "ยืนยันข้อมูลเก่า",
        ["LegacyExcluded"] = "ไม่นับข้อมูลเก่า",

        // บทบาทผู้ใช้
        ["Customer"] = "ลูกค้า",
        ["Staff"] = "พนักงานต้อนรับ",
        ["Housekeeper"] = "แม่บ้าน",
        ["Accountant"] = "ฝ่ายบัญชี",
        ["Admin"] = "ผู้ดูแลระบบ"
    };

    /// <summary>ค่าสถานะ → ภาษาไทย (ถ้าไม่รู้จักจะคืนค่าเดิม, ถ้าว่างคืน "-")</summary>
    public static string Status(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : Statuses.GetValueOrDefault(value, value);

    /// <summary>สีป้ายของสถานะงานแม่บ้าน งานซ่อม และการจอง (ใช้สีเดียวกันทุกหน้า)</summary>
    public static string Badge(string? status) => status switch
    {
        "Verified" or "Resolved" or "CheckedIn" => "text-bg-success",
        "Pending" or "Open" => "text-bg-warning",
        "Accepted" or "InProgress" or "In Progress" => "text-bg-info",
        "Completed" => "live-status tone-arrive", // ทำเสร็จแล้ว รอพนักงานต้อนรับตรวจ
        "Booked" => "text-bg-primary",
        "CheckedOut" => "text-bg-dark",
        _ => "text-bg-secondary"
    };

    /// <summary>วิธีชำระเงิน (ค่าที่เก็บในฐานข้อมูล) → ชื่อที่แสดง</summary>
    public static string PaymentMethod(string? method) => method switch
    {
        "Cash" => "เงินสดที่โรงแรม",
        "Bank Transfer (Mock)" => "โอนผ่านธนาคาร (จำลอง)",
        "PromptPay (Mock)" => "พร้อมเพย์ (จำลอง)",
        "PayPal (Mock)" => "PayPal (จำลอง)",
        "Bank Transfer" => "โอนผ่านธนาคาร",
        "PromptPay" => "พร้อมเพย์",
        null or "" => "-",
        _ => method
    };

    private static readonly TimeZoneInfo HotelTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok");

    /// <summary>เวลา UTC ในฐานข้อมูล → เวลาไทย เช่น "17/11/2026 10:25"</summary>
    public static string Time(DateTime? utc) =>
        utc.HasValue
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc), HotelTimeZone)
                .ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : "-";

    /// <summary>วันที่ เช่น "25/11/2026"</summary>
    public static string Date(DateOnly? date) =>
        date?.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture) ?? "-";

    /// <summary>หมายเหตุที่ระบบบันทึกเป็นภาษาอังกฤษ → ไทย (ข้อมูลในฐานข้อมูลไม่เปลี่ยน)</summary>
    public static string? Reason(string? value) =>
        value == "DEMO only; no real payment was transferred." ? "การชำระเงินจำลอง ไม่มีการโอนเงินจริง" : value;

    /// <summary>ข้อความว่าง → "-"</summary>
    public static string OrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    /// <summary>จำนวนเงิน เช่น "฿9,000.00" หรือ "−฿9,000.00" (ติดลบใส่เครื่องหมายไว้หน้า ฿)</summary>
    public static string Baht(decimal amount, string format = "N2") =>
        (amount < 0 ? "−฿" : "฿") + Math.Abs(amount).ToString(format, System.Globalization.CultureInfo.InvariantCulture);
}
