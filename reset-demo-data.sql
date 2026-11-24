-- ============================================================
-- ล้างข้อมูลทดสอบ ก่อนสาธิตระบบ
-- ลบแล้วกู้คืนไม่ได้: ก่อนรัน ให้กด Backups → Create backup ใน Railway ก่อน
--
-- ช่อง Query ของ Railway รันได้ครั้งละ 1 คำสั่ง: รันทีละข้อตามลำดับ (ลำดับสำคัญ เพราะตารางอ้างอิงกัน)
--
-- สิ่งที่จะเหลือหลังรัน:
--   ห้องพัก 5 ห้อง (ทุกห้องกลับเป็น "เปิดขาย"), โปรโมชันที่ตั้งเงื่อนไขแล้ว 8 โปร
--   บัญชี: Admin (#7), พนักงานต้อนรับ (#10), แม่บ้าน (#11), ลูกค้าทดสอบ (#17), ฝ่ายบัญชี (#20, #21)
--   รหัสผ่านและอีเมลของบัญชีที่เหลือไม่เปลี่ยน
-- สิ่งที่จะหายทั้งหมด: การจอง การเข้าพัก การชำระเงิน การคืนเงิน งานทำความสะอาด งานซ่อม
-- ============================================================


-- ---------- ส่วนที่ 1: ลบงานทั้งหมด (ลูก → แม่) ----------

-- 1
DELETE FROM `payment_events` WHERE `EventId` > 0

-- 2
DELETE FROM `payment_refunds` WHERE `RefundId` > 0

-- 3
DELETE FROM `payments` WHERE `PaymentId` > 0

-- 4
DELETE FROM `housekeeping_task_events` WHERE `EventId` > 0

-- 5
DELETE FROM `housekeeping_tasks` WHERE `TaskId` > 0

-- 6
DELETE FROM `booking_stays` WHERE `BookingId` > 0

-- 7
DELETE FROM `bookings` WHERE `BookingId` > 0

-- 8
DELETE FROM `maintenance_report_events` WHERE `EventId` > 0

-- 9
DELETE FROM `maintenance_reports` WHERE `ReportId` > 0


-- ---------- ส่วนที่ 2: บัญชีผู้ใช้ ----------

-- 10 ลบบัญชีทดสอบชื่อมั่ว (แก้รายการเลขได้ถ้าอยากเก็บบางบัญชีไว้)
--    3 fdsfsd, 4 san nas, 5 ooo iiiii, 6 asdasd asda, 8 sdadas asdadas, 9 asdasd jujy,
--    12 df adfadfa (พนักงานต้อนรับสำรอง), 13 gsr fad (แม่บ้านสำรอง), 14 san test,
--    15 op as, 16 asdww weff, 18 QA WalkIn, 19 หมู เติ้ล
DELETE FROM `users` WHERE `UserId` IN (3, 4, 5, 6, 8, 9, 12, 13, 14, 15, 16, 18, 19)

-- 10.1 ลบบัญชีที่สร้างตอนทดสอบ: 22 ทดสอบ วอล์กอิน, 23 มานี ทดสอบสมัคร, 24 คนอื่น แอบอ้าง, 25 เลือก ทดสอบ, 26 แข่ง ทดสอบ
--      (บัญชี #21 ACC count ฝ่ายบัญชี เก็บไว้)
DELETE FROM `users` WHERE `UserId` IN (22, 23, 24, 25, 26)

-- 11 ตั้งชื่อพนักงานให้ดูเป็นคนจริงตอนสาธิต (อีเมล asd@gmail.com เดิม)
UPDATE `users` SET `FirstName` = 'สมชาย', `LastName` = 'ใจดี' WHERE `UserId` = 10

-- 12 (อีเมล ds@gmail.com เดิม)
UPDATE `users` SET `FirstName` = 'สมศรี', `LastName` = 'รักสะอาด' WHERE `UserId` = 11

-- 13 ลูกค้าทดสอบ (อีเมล qa.customer...@goodhotel.test เดิม)
UPDATE `users` SET `FirstName` = 'มานี', `LastName` = 'มีสุข' WHERE `UserId` = 17

-- 14 ฝ่ายบัญชี (อีเมล qa.accountant@goodhotel.test เดิม)
UPDATE `users` SET `FirstName` = 'วิภา', `LastName` = 'บัญชีดี' WHERE `UserId` = 20


-- ---------- ส่วนที่ 3: ห้องพักและโปรโมชัน ----------

-- 15 ทุกห้องกลับเป็น "เปิดขาย" (ห้อง 102, 201, 202 ถูกปิดขาย/ซ่อมบำรุงค้างไว้)
UPDATE `rooms` SET `Status` = 'Available', `PreMaintenanceStatus` = NULL WHERE `RoomId` > 0

-- 16 ลบโปรเก่าที่ยังไม่ได้ตั้งเงื่อนไข (ลูกค้าใช้ไม่ได้อยู่แล้ว): Ninth Day Special, Birthday Celebration,
--    Mother's Day Delight, New Year Gift, Valentine's Romance, asassss, wdsss, sssssssssss
DELETE FROM `promotions` WHERE `RuleType` IS NULL
