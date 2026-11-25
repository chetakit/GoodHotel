using System.Data;
using System.Data.Common;
using GoodHotel.Models;
using GoodHotel.Models.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GoodHotel.Services;

// ผลลัพธ์ที่เป็นไปได้ตอนบันทึกการจอง
public enum BookingSaveStatus
{
    Created,
    AlreadyExists,
    InvalidRequest,
    UserNotFound,
    RoomNotFound,
    RoomUnavailable,
    CapacityExceeded,
    InvalidRate,
    LockTimeout
}

// ข้อมูลที่ส่งกลับไปให้ Controller
public sealed record BookingSaveResult(
    BookingSaveStatus Status,
    int? BookingId = null,
    decimal? Amount = null,
    string? Message = null,
    int ExtraBedCount = 0,
    int? PromotionId = null,
    string? PromotionName = null,
    decimal? RoomSubtotal = null,
    decimal? DiscountAmount = null,
    decimal? ExtraBedAmount = null);

// กติกาการจองห้องทั้งหมด: ตรวจข้อมูล คิดราคา กันจองซ้ำ และบันทึก
public sealed class BookingService
{
    private const string BookingIdLockName = "GoodHotel.BookingId";
    // ลำดับล็อกทั้งระบบ: ห้อง → เลขการจอง → เลขชำระเงิน (ตอนจ่ายออนไลน์ใช้เฉพาะเลขชำระเงิน จึงไม่ล็อกไขว้กัน)
    private const int BookingIdLockTimeoutSeconds = 15;

    private readonly GoodHotelDbContext _db;
    private readonly ILogger<BookingService> _logger;
    private readonly PromotionService _promotions;
    private readonly RoomAvailabilityService _availability;
    private readonly TimeProvider _time;
    private readonly ParallelReader _reader;

    // รับของที่ต้องใช้จากระบบ DI
    public BookingService(
        GoodHotelDbContext db,
        ILogger<BookingService> logger,
        PromotionService promotions,
        RoomAvailabilityService availability,
        TimeProvider time,
        ParallelReader reader)
    {
        _reader = reader;
        _time = time;
        _db = db;
        _logger = logger;
        _promotions = promotions;
        _availability = availability;
    }

    /// <param name="customerId">ลูกค้าที่กำลังจอง (null = ยังไม่ login) ใช้ตรวจโปรที่ขึ้นกับประวัติลูกค้า</param>
    // เตรียมหน้ายืนยัน: ตรวจห้องและคิดราคา (ยังไม่บันทึก)
    public async Task<BookingConfirmationViewModel> PrepareConfirmationAsync(
        CreateBookingViewModel request,
        int? customerId,
        CancellationToken cancellationToken = default,
        bool isNewCustomer = false)
    {
        // ขั้น 1 ตรวจข้อมูลที่ส่งมา
        var model = CopyRequest(request);
        var validationMessage = ValidateRequest(request);
        if (validationMessage != null)
        {
            model.StatusMessage = validationMessage;
            return model;
        }

        // เช็ควันว่างและโหลดข้อมูลโปรพร้อมกับดึงข้อมูลห้อง (อ่านผ่าน connection แยก ไม่ชนกับ _db)
        var promotionsTask = _promotions.LoadAsync(customerId, isNewCustomer, cancellationToken);
        var availableTask = _availability.IsAvailableAsync(
            request.RoomId,
            request.CheckIn!.Value,
            request.CheckOut!.Value,
            cancellationToken);
        var room = await _db.Rooms
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.RoomId == request.RoomId,
                cancellationToken);
        await Task.WhenAll(availableTask, promotionsTask);
        var available = availableTask.Result;

        if (room == null)
        {
            model.StatusMessage = "ไม่พบห้องพักที่เลือก";
            return model;
        }

        // ขั้น 2 ใส่ข้อมูลห้องและราคา = ราคาต่อคืน x จำนวนคืน แล้วตรวจว่าห้องจองได้
        SetRoomDetails(model, room, request);

        if (room.Status != "Available")
        {
            model.StatusMessage = "ห้องพักนี้ไม่พร้อมให้จอง";
            return model;
        }

        if (!room.Capacity.HasValue ||
            (long)request.Guests!.Value >
                (long)room.Capacity.Value + request.ExtraBedCount)
        {
            model.StatusMessage = "จำนวนผู้เข้าพักเกินความจุของห้อง";
            return model;
        }

        if (!room.Rate.HasValue || room.Rate.Value <= 0)
        {
            model.StatusMessage = "ไม่สามารถยืนยันราคาให้ห้องนี้ได้";
            return model;
        }

        if (model.EstimatedAmount.HasValue &&
            model.EstimatedAmount.Value > 99999999.99m)
        {
            model.StatusMessage = "ยอดรวมเกินขอบเขตที่ระบบรองรับ";
            return model;
        }

        if (!available)
        {
            model.StatusMessage = "ห้องพักไม่พร้อมให้จองในช่วงวันที่เลือก";
            return model;
        }

        var subtotal = model.EstimatedAmount.GetValueOrDefault();
        var nights = model.NightCount.GetValueOrDefault();
        model.RoomSubtotal = subtotal;
        model.ExtraBedCount = request.ExtraBedCount;

        // โปรที่ลูกค้าเลือก (ถ้าเลือกโปรที่ใช้ไม่ได้ หรือยังไม่เลือก ใช้โปรที่ลดได้มากที่สุด)
        var context = new PromotionContext(request.CheckIn!.Value, request.CheckOut!.Value, subtotal);
        var (promotion, eligible) = _promotions.Resolve(promotionsTask.Result, context, request.PromotionId);
        model.PromotionChecks = _promotions.CheckAll(promotionsTask.Result, context);
        model.Customer = promotionsTask.Result.Customer;
        model.AvailablePromotions = eligible;
        model.PromotionId = promotion?.PromotionId;
        model.PromotionName = promotion?.Name;
        model.DiscountAmount = promotion?.DiscountAmount ?? 0m;
        // เตียงเสริม 500 บาท x จำนวนเตียง x จำนวนคืน
        model.ExtraBedAmount = 500m * request.ExtraBedCount * nights;
        // ยอดสุทธิ = ค่าห้อง - ส่วนลด + เตียงเสริม
        model.TotalAmount = subtotal - model.DiscountAmount + model.ExtraBedAmount;

        if (model.TotalAmount > 99999999.99m)
        {
            model.StatusMessage = "ยอดรวมเกินขอบเขตที่ระบบรองรับ";
            return model;
        }

        model.CanConfirm = true;
        return model;
    }

    // บันทึกการจองจริง ทุกอย่างอยู่ใน transaction เดียว: สำเร็จทั้งหมดหรือไม่เกิดอะไรเลย
    public async Task<BookingSaveResult> CreateAsync(
        int userId,
        CreateBookingViewModel request,
        CancellationToken cancellationToken = default,
        decimal? expectedAmount = null,
        bool requireSelectedPromotion = false,
        int? deskStaffId = null)
    {
        var validationMessage = ValidateRequest(request);
        if (validationMessage != null)
        {
            return new BookingSaveResult(
                BookingSaveStatus.InvalidRequest,
                Message: validationMessage);
        }

        if (userId <= 0)
        {
            return new BookingSaveResult(
                BookingSaveStatus.UserNotFound,
                Message: "ไม่พบบัญชีลูกค้าที่กำลังใช้งาน");
        }

        // ขั้น 1 เปิดการเชื่อมต่อและเริ่ม transaction
        await _db.Database.OpenConnectionAsync(cancellationToken);
        var connection = _db.Database.GetDbConnection();
        IDbContextTransaction? transaction = null;
        var transactionCompleted = false;
        var lockAcquired = false;
        var paymentLockAcquired = false;

        try
        {
            var activeTransaction =
                await _db.Database.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted,
                    cancellationToken);
            transaction = activeTransaction;
            var dbTransaction = activeTransaction.GetDbTransaction();

            // ไม่ผ่านเงื่อนไข: ยกเลิก transaction แล้วส่งเหตุผลกลับ
            async Task<BookingSaveResult> RollbackWithResultAsync(
                BookingSaveResult result)
            {
                await activeTransaction.RollbackAsync(cancellationToken);
                transactionCompleted = true;
                return result;
            }

            // ล็อกห้องก่อน แล้วยิงทุกการตรวจสอบพร้อมกัน (~200ms แทนที่จะต่อคิวเกือบ 2 วินาที)
            // ใครจะจอง เช็คอิน ย้ายห้อง หรือแจ้งซ่อมห้องนี้ต้องล็อกห้องก่อน ข้อมูลที่อ่านจึงไม่เปลี่ยนจนกว่าจะบันทึกเสร็จ
            // ขั้น 2 ล็อกแถวห้อง (SELECT ... FOR UPDATE) คนอื่นที่จองห้องเดียวกันต้องรอ จึงจองซ้ำไม่ได้
            var room = await RoomAvailabilityService.LockAndLoadRoomAsync(_db, request.RoomId, cancellationToken);
            var userTask = _reader.Run(db => db.Users
                .AsNoTracking()
                .AnyAsync(
                    user => user.UserId == userId &&
                            user.Role == "Customer",
                    cancellationToken));
            var existingTask = _reader.Run(db => db.Bookings
                .AsNoTracking()
                .Where(booking =>
                    booking.Status != BookingStatuses.Cancelled &&
                    booking.UserId == userId &&
                    booking.RoomId == request.RoomId &&
                    booking.CheckIn == request.CheckIn &&
                    booking.CheckOut == request.CheckOut &&
                    // เช็คเอาท์ไปแล้ว (เช่น ออกก่อนกำหนด) ถือว่าจบ จองห้องเดิมวันเดิมใหม่ได้
                    !db.BookingStays.Any(stay => stay.BookingId == booking.BookingId && stay.Status == "CheckedOut") &&
                    booking.Guests == request.Guests &&
                    (booking.ExtraBed == request.ExtraBedCount ||
                     (request.ExtraBedCount == 0 && booking.ExtraBed == null)))
                .Select(booking => new
                {
                    booking.BookingId,
                    booking.Amount,
                    booking.PromotionId,
                    PromotionName = booking.Promotion == null ? null : booking.Promotion.PromotionName
                })
                .FirstOrDefaultAsync(cancellationToken));
            var availableTask = _availability.IsAvailableAsync(
                request.RoomId,
                request.CheckIn!.Value,
                request.CheckOut!.Value,
                cancellationToken);
            var promotionsTask = _promotions.LoadAsync(userId, false, cancellationToken);
            var staffTask = deskStaffId == null ? Task.FromResult(true) : _reader.Run(db => db.Users.AsNoTracking()
                .AnyAsync(user => user.UserId == deskStaffId && user.Role == "Staff" && user.IsActive, cancellationToken));
            await Task.WhenAll(userTask, existingTask, availableTask, promotionsTask, staffTask);
            var userIsCustomer = userTask.Result;
            var existingRequest = existingTask.Result;

            // ขั้น 3 ตรวจเงื่อนไขทีละข้อ ไม่ผ่านข้อไหนให้ rollback
            if (!staffTask.Result)
            {
                return await RollbackWithResultAsync(new BookingSaveResult(
                    BookingSaveStatus.InvalidRequest,
                    Message: "เฉพาะพนักงานต้อนรับที่รับเงินที่เคาน์เตอร์ได้"));
            }

            if (!userIsCustomer)
            {
                return await RollbackWithResultAsync(new BookingSaveResult(
                    BookingSaveStatus.UserNotFound,
                    Message: "ไม่พบบัญชีลูกค้าที่กำลังใช้งาน"));
            }

            if (room == null)
            {
                return await RollbackWithResultAsync(new BookingSaveResult(
                    BookingSaveStatus.RoomNotFound,
                    Message: "ไม่พบห้องพักที่เลือก"));
            }

            // กดยืนยันซ้ำ (เช่น refresh หน้า) ให้คืนการจองเดิม ไม่สร้างใหม่
            if (existingRequest != null)
            {
                await activeTransaction.CommitAsync(cancellationToken);
                transactionCompleted = true;
                return new BookingSaveResult(
                    BookingSaveStatus.AlreadyExists,
                    existingRequest.BookingId,
                    existingRequest.Amount,
                    "รายการจองนี้ถูกบันทึกไว้แล้ว",
                    request.ExtraBedCount,
                    existingRequest.PromotionId,
                    existingRequest.PromotionName);
            }

            if (room.Status != "Available")
            {
                return await RollbackWithResultAsync(new BookingSaveResult(
                    BookingSaveStatus.RoomUnavailable,
                    Message: "ห้องพักนี้ไม่พร้อมให้จอง"));
            }

            if (!room.Capacity.HasValue ||
                (long)request.Guests!.Value >
                    (long)room.Capacity.Value + request.ExtraBedCount)
            {
                return await RollbackWithResultAsync(new BookingSaveResult(
                    BookingSaveStatus.CapacityExceeded,
                    Message: "จำนวนผู้เข้าพักเกินความจุของห้อง"));
            }

            if (!room.Rate.HasValue || room.Rate.Value <= 0)
            {
                return await RollbackWithResultAsync(new BookingSaveResult(
                    BookingSaveStatus.InvalidRate,
                    Message: "ไม่สามารถยืนยันราคาให้ห้องนี้ได้"));
            }

            if (!availableTask.Result)
            {
                return await RollbackWithResultAsync(new BookingSaveResult(
                    BookingSaveStatus.RoomUnavailable,
                    Message: "ห้องพักไม่พร้อมให้จองในช่วงวันที่เลือก"));
            }

            // ขั้น 4 คิดราคาใหม่จากฐานข้อมูล ไม่เชื่อราคาที่ส่งมาจากหน้าเว็บ
            var nightCount =
                request.CheckOut.Value.DayNumber -
                request.CheckIn.Value.DayNumber;
            decimal roomSubtotal;
            try
            {
                roomSubtotal = checked(room.Rate.Value * nightCount);
            }
            catch (OverflowException)
            {
                return await RollbackWithResultAsync(new BookingSaveResult(
                    BookingSaveStatus.InvalidRate,
                    Message: "ยอดรวมเกินขอบเขตที่ระบบรองรับ"));
            }

            if (roomSubtotal > 99999999.99m)
            {
                return await RollbackWithResultAsync(new BookingSaveResult(
                    BookingSaveStatus.InvalidRate,
                    Message: "ยอดรวมเกินขอบเขตที่ระบบรองรับ"));
            }

            // คำนวณโปรใหม่ตอนบันทึกจริง: ตรวจว่าโปรที่ลูกค้าเลือกยังใช้ได้ (ไม่เชื่อส่วนลดจากหน้าเว็บ)
            var (promotion, _) = _promotions.Resolve(
                promotionsTask.Result,
                new PromotionContext(request.CheckIn.Value, request.CheckOut.Value, roomSubtotal),
                request.PromotionId);
            var promotionId = promotion?.PromotionId;
            // พนักงานเลือกโปรเอง: ถ้าโปรนั้นใช้ไม่ได้ให้บอก ไม่เปลี่ยนเป็นโปรอื่นเงียบๆ
            if (requireSelectedPromotion && request.PromotionId.HasValue && promotionId != request.PromotionId)
            {
                return await RollbackWithResultAsync(new BookingSaveResult(
                    BookingSaveStatus.InvalidRequest,
                    Message: WalkInService.PromotionRefused));
            }
            var discountAmount = promotion?.DiscountAmount ?? 0m;

            decimal extraBedAmount;
            decimal amount;
            try
            {
                extraBedAmount = checked(
                    500m * request.ExtraBedCount * nightCount);
                amount = checked(
                    roomSubtotal - discountAmount + extraBedAmount);
            }
            catch (OverflowException)
            {
                return await RollbackWithResultAsync(new BookingSaveResult(
                    BookingSaveStatus.InvalidRate,
                    Message: "ยอดรวมเกินขอบเขตที่ระบบรองรับ"));
            }

            if (amount > 99999999.99m)
            {
                return await RollbackWithResultAsync(new BookingSaveResult(
                    BookingSaveStatus.InvalidRate,
                    Message: "ยอดรวมเกินขอบเขตที่ระบบรองรับ"));
            }

            // ราคาไม่ตรงกับที่พนักงานเห็น (Walk-in): ให้ตรวจยอดอีกครั้ง
            if (expectedAmount.HasValue && expectedAmount != amount)
            {
                return await RollbackWithResultAsync(new BookingSaveResult(
                    BookingSaveStatus.InvalidRate,
                    Message: $"ยอดเปลี่ยนเป็น ฿{amount:N2} กรุณาตรวจยอดแล้วกดยืนยันอีกครั้ง"));
            }

            if (deskStaffId != null && amount <= 0)
            {
                return await RollbackWithResultAsync(new BookingSaveResult(
                    BookingSaveStatus.InvalidRate,
                    Message: "ยอดเงินของการจองนี้ต้องตรวจสอบก่อน"));
            }

            // ขอล็อกเลขที่การจอง (และเลขชำระเงินถ้ารับเงินสดเลย) พร้อมอ่านเลขล่าสุดในรอบเดียว
            int maxId;
            var maxPaymentId = 0;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = dbTransaction;
                command.CommandText = "SELECT GET_LOCK(@lockName, @timeoutSeconds); " +
                    "SELECT MAX(`BookingId`) FROM `bookings`" +
                    (deskStaffId == null ? "" : "; SELECT GET_LOCK(@paymentLock, @timeoutSeconds); SELECT COALESCE(MAX(`PaymentId`), 0) FROM `payments`");
                AddParameter(command, "@lockName", BookingIdLockName);
                AddParameter(command, "@timeoutSeconds", BookingIdLockTimeoutSeconds);
                if (deskStaffId != null) AddParameter(command, "@paymentLock", PaymentService.IdLock);
                await using var result = await command.ExecuteReaderAsync(CancellationToken.None);
                await result.ReadAsync(CancellationToken.None);
                int? lockResult = result.IsDBNull(0) ? null : Convert.ToInt32(result.GetValue(0));
                lockAcquired = lockResult == 1;
                await result.NextResultAsync(CancellationToken.None);
                // ล็อกชื่อถือไว้จนหลัง commit อ่านธรรมดาก็เห็นทุกเลข ไม่ต้องล็อกแถวล่าสุด (ซึ่งต้องรอคนที่กำลังจ่ายการจองนั้น)
                maxId = await result.ReadAsync(CancellationToken.None) && !result.IsDBNull(0) ? Convert.ToInt32(result.GetValue(0)) : 0;
                if (deskStaffId != null)
                {
                    await result.NextResultAsync(CancellationToken.None);
                    await result.ReadAsync(CancellationToken.None);
                    paymentLockAcquired = !result.IsDBNull(0) && Convert.ToInt32(result.GetValue(0)) == 1;
                    await result.NextResultAsync(CancellationToken.None);
                    await result.ReadAsync(CancellationToken.None);
                    maxPaymentId = Convert.ToInt32(result.GetValue(0));
                }
                if (lockResult == null)
                {
                    throw new InvalidOperationException(
                        "MySQL did not return a valid result for GET_LOCK.");
                }
            }

            if (!lockAcquired || (deskStaffId != null && !paymentLockAcquired))
            {
                return await RollbackWithResultAsync(new BookingSaveResult(
                    BookingSaveStatus.LockTimeout,
                    Message: "ระบบกำลังบันทึกการจองอื่น กรุณาลองอีกครั้ง"));
            }

            if (maxId == int.MaxValue)
            {
                throw new InvalidOperationException("No more BookingId values are available.");
            }

            // ขั้น 5 เลขการจองใหม่ = เลขมากสุด + 1 (ล็อกไว้แล้ว เลขจึงไม่ชน)
            var bookingId = maxId + 1;

            // ขั้น 6 เพิ่มแถวลงตาราง bookings
            _db.Bookings.Add(new Booking
            {
                BookingId = bookingId,
                UserId = userId,
                RoomId = room.RoomId,
                CheckIn = request.CheckIn,
                CheckOut = request.CheckOut,
                Guests = request.Guests,
                ExtraBed = request.ExtraBedCount,
                PromotionId = promotionId,
                Amount = amount,
                CreatedAtUtc = _time.GetUtcNow().UtcDateTime
            });

            // Walk-in: รับเงินสดและเช็คอินใน transaction เดียวกับการจอง (สำเร็จทั้งหมดหรือไม่เกิดอะไรเลย)
            // ห้องถูกล็อกและเพิ่งตรวจแล้วว่าวันนี้ไม่มีแขก ไม่รอทำความสะอาด และไม่ติดซ่อม
            if (deskStaffId is { } staffId)
            {
                var now = _time.GetUtcNow().UtcDateTime;
                _db.Payments.Add(PaymentService.DeskCash(maxPaymentId + 1, bookingId, staffId, amount, now));
                _db.BookingStays.Add(new BookingStay
                {
                    BookingId = bookingId, Status = "CheckedIn", CheckedInAtUtc = now,
                    CheckedInByUserId = staffId, CheckInNote = "Walk-in"
                });
            }

            // ขั้น 7 SaveChanges = ส่ง INSERT, Commit = ยืนยันให้ข้อมูลมีผลจริง
            await _db.SaveChangesAsync(cancellationToken);
            await activeTransaction.CommitAsync(cancellationToken);
            transactionCompleted = true;

            return new BookingSaveResult(
                BookingSaveStatus.Created,
                bookingId,
                amount,
                "บันทึกการจองสำเร็จ",
                request.ExtraBedCount,
                promotionId,
                promotion?.Name,
                roomSubtotal,
                discountAmount,
                extraBedAmount);
        }
        // เกิดข้อผิดพลาด: ย้อนกลับทั้งหมด ไม่ให้ข้อมูลค้างครึ่งทาง
        catch
        {
            if (transaction != null && !transactionCompleted)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    transactionCompleted = true;
                }
                catch (Exception rollbackException)
                {
                    _logger.LogError(
                        rollbackException,
                        "Could not roll back the booking transaction after an error.");
                }
            }

            throw;
        }
        // ไม่ว่าสำเร็จหรือไม่ ต้องปลดล็อกและปิดการเชื่อมต่อเสมอ
        finally
        {
            if (lockAcquired || paymentLockAcquired)
            {
                try
                {
                    if (connection.State == ConnectionState.Open)
                    {
                        await ReleaseNamedLockAsync(
                            connection,
                            transactionCompleted
                                ? null
                                : transaction?.GetDbTransaction(),
                            lockAcquired,
                            paymentLockAcquired,
                            CancellationToken.None);
                    }
                    else
                    {
                        _logger.LogError(
                            "The MySQL connection closed before the BookingId named lock could be released; closing the session releases the lock.");
                    }
                }
                catch (Exception releaseException)
                {
                    _logger.LogError(
                        releaseException,
                        "Could not release the BookingId named lock. Closing the MySQL session will release it.");
                }
            }

            if (transaction != null)
            {
                try
                {
                    await transaction.DisposeAsync();
                }
                catch (Exception disposeException)
                {
                    _logger.LogError(
                        disposeException,
                        "Could not dispose the booking transaction cleanly.");
                }
            }

            try
            {
                await _db.Database.CloseConnectionAsync();
            }
            catch (Exception closeException)
            {
                _logger.LogError(
                    closeException,
                    "Could not close the booking database connection cleanly.");
            }
        }
    }

    // ปลดล็อกเลขที่การจอง/เลขชำระเงิน
    private async Task ReleaseNamedLockAsync(
        DbConnection connection,
        DbTransaction? transaction,
        bool bookingLock,
        bool paymentLock,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // ปล่อยทุกล็อกที่ถืออยู่ในรอบเดียว
        command.CommandText = (bookingLock, paymentLock) switch
        {
            (true, true) => "SELECT RELEASE_LOCK(@paymentLock) + RELEASE_LOCK(@lockName)",
            (true, false) => "SELECT RELEASE_LOCK(@lockName)",
            _ => "SELECT RELEASE_LOCK(@paymentLock)"
        };
        if (bookingLock) AddParameter(command, "@lockName", BookingIdLockName);
        if (paymentLock) AddParameter(command, "@paymentLock", PaymentService.IdLock);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result == null || result == DBNull.Value ||
            Convert.ToInt32(
                result,
                System.Globalization.CultureInfo.InvariantCulture) != (bookingLock ? 1 : 0) + (paymentLock ? 1 : 0))
        {
            _logger.LogError(
                "MySQL did not confirm release of the booking named locks.");
            throw new InvalidOperationException(
                "Could not confirm release of the booking named locks.");
        }
    }

    // ใส่ค่าเป็น parameter ไม่ต่อข้อความ SQL เอง (กัน SQL Injection)
    private static void AddParameter(
        DbCommand command,
        string name,
        object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    // ตรวจข้อมูลที่ส่งมา คืนข้อความเตือน (null = ผ่าน)
    private string? ValidateRequest(CreateBookingViewModel request)
    {
        if (request.RoomId <= 0)
        {
            return "กรุณาเลือกห้องพัก";
        }

        if (!request.CheckIn.HasValue || !request.CheckOut.HasValue)
        {
            return "กรุณาระบุวันที่เข้าพักและวันที่ออก";
        }

        if (request.CheckIn.Value < _availability.HotelToday())
        {
            return "วันที่เข้าพักต้องไม่เป็นวันที่ย้อนหลัง";
        }

        if (request.CheckOut.Value <= request.CheckIn.Value)
        {
            return "วันที่ออกต้องอยู่หลังวันที่เข้าพัก";
        }

        if (!request.Guests.HasValue || request.Guests.Value <= 0)
        {
            return "จำนวนผู้เข้าพักต้องมากกว่า 0";
        }

        if (request.ExtraBedCount is < 0 or > 1)
        {
            return "จำนวนเตียงเสริมต้องเป็น 0 หรือ 1";
        }

        return null;
    }

    // คัดลอกข้อมูลที่กรอกไปใส่หน้ายืนยัน
    private static BookingConfirmationViewModel CopyRequest(
        CreateBookingViewModel request)
    {
        return new BookingConfirmationViewModel
        {
            RoomId = request.RoomId,
            CheckIn = request.CheckIn,
            CheckOut = request.CheckOut,
            Guests = request.Guests,
            ExtraBedCount = request.ExtraBedCount
        };
    }

    // ใส่ข้อมูลห้อง จำนวนคืน และราคา
    private static void SetRoomDetails(
        BookingConfirmationViewModel model,
        Room room,
        CreateBookingViewModel request)
    {
        model.RoomNumber = room.Room1;
        model.RoomType = room.Type;
        model.Rate = room.Rate;
        model.Capacity = room.Capacity;

        if (room.Rate.HasValue &&
            request.CheckIn.HasValue &&
            request.CheckOut.HasValue)
        {
            model.NightCount =
                request.CheckOut.Value.DayNumber -
                request.CheckIn.Value.DayNumber;
            model.EstimatedAmount = room.Rate.Value * model.NightCount;
        }
    }
}
