-- กำหนดเวลาชำระเงินหลังจอง (ค่าเริ่มต้น 15 นาที)
-- รันครั้งเดียวกับฐานข้อมูลของโปรเจค
--   CreatedAtUtc : เวลาที่กดจอง ใช้นับเวลาชำระเงิน (การจองเก่าเป็น NULL = ไม่ถูกยกเลิกอัตโนมัติ)
--   CancelReason : เหตุผลที่ยกเลิก Customer / Staff / PaymentTimeout (การยกเลิกเก่าเป็น NULL)

ALTER TABLE `bookings`
    ADD `CreatedAtUtc` datetime(6) NULL,
    ADD `CancelReason` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL;

-- ระบบค้นหาการจองที่หมดเวลาทุกนาที index นี้ทำให้ค้นเร็ว
CREATE INDEX `IX_bookings_Status_CreatedAtUtc` ON `bookings` (`Status`, `CreatedAtUtc`);
