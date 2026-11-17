-- ระบบ Promotion ใหม่: Admin กำหนดเงื่อนไข + ลดเป็น % หรือบาทเอง
-- รันครั้งเดียวกับฐานข้อมูลของโปรเจค (ข้อมูลเดิมไม่หาย)

ALTER TABLE `promotions`
    ADD `RuleType` varchar(30) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    ADD `RuleValue` int NULL,
    ADD `MinSpend` decimal(10,2) NULL,
    ADD `StartDate` date NULL,
    ADD `EndDate` date NULL,
    ADD `IsActive` tinyint(1) NOT NULL DEFAULT 1,
    ADD `DiscountType` varchar(10) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    ADD `DiscountValue` decimal(10,2) NULL,
    ADD `MaxDiscount` decimal(10,2) NULL;

-- โปรตัวอย่าง 8 อัน (เปิดใช้งานเลย Admin แก้ตัวเลขหรือปิดได้ที่หน้า Promotions)
-- ใช้ PromotionId ต่อจากค่ามากสุดที่มีอยู่
SET @id := (SELECT COALESCE(MAX(`PromotionId`), 0) FROM `promotions`);

INSERT INTO `promotions`
    (`PromotionId`, `PromotionName`, `Description`, `RuleType`, `RuleValue`, `MinSpend`,
     `DiscountType`, `DiscountValue`, `MaxDiscount`, `IsActive`)
VALUES
    (@id + 1, 'Early Bird',     'จองล่วงหน้าก่อนใคร ลดทันที',            'EarlyBird',     30,   NULL, 'Percent', 15,   2000, 1),
    (@id + 2, 'Long Stay',      'พักยาวคุ้มกว่า',                        'LongStay',      3,    NULL, 'Fixed',   1000, NULL, 1),
    (@id + 3, 'Weekday Deal',   'พักวันธรรมดา อาทิตย์-พฤหัส ราคาพิเศษ',   'Weekday',       NULL, NULL, 'Percent', 10,   1500, 1),
    (@id + 4, 'Last Minute',    'จองด่วนก่อนเข้าพัก',                     'LastMinute',    3,    NULL, 'Percent', 10,   1000, 1),
    (@id + 5, 'Birthday Month', 'เข้าพักในเดือนเกิด รับส่วนลดพิเศษ',       'BirthdayMonth', NULL, NULL, 'Percent', 20,   1500, 1),
    (@id + 6, 'First Stay',     'ลูกค้าใหม่ จองครั้งแรก',                  'FirstStay',     NULL, NULL, 'Fixed',   500,  NULL, 1),
    (@id + 7, 'Loyal Guest',    'ขอบคุณลูกค้าประจำ',                      'LoyalGuest',    3,    NULL, 'Percent', 12,   2000, 1),
    (@id + 8, 'New Year',       'เข้าพักช่วงปีใหม่',                       'Seasonal',      NULL, 3000, 'Fixed',   800,  NULL, 1);

UPDATE `promotions` SET `StartDate` = '2026-12-28', `EndDate` = '2027-01-05' WHERE `PromotionId` = @id + 8;
