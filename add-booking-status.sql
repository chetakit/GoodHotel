-- เพิ่มสถานะการจอง (Booked / Cancelled) ให้ตาราง bookings
-- รันครั้งเดียวกับฐานข้อมูลของโปรเจค ข้อมูลเดิมทุกแถวจะเป็น 'Booked'

ALTER TABLE `bookings`
    ADD `Status` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL DEFAULT 'Booked',
    ADD `CancelledAtUtc` datetime(6) NULL;
