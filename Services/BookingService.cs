using System.Data;
using System.Data.Common;
using GoodHotel.Models;
using GoodHotel.Models.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GoodHotel.Services;

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

public sealed class BookingService
{
    private const string BookingIdLockName = "GoodHotel.BookingId";
    private const int BookingIdLockTimeoutSeconds = 5;

    private readonly GoodHotelDbContext _db;
    private readonly ILogger<BookingService> _logger;
    private readonly PromotionService _promotions;
    private readonly RoomAvailabilityService _availability;

    public BookingService(
        GoodHotelDbContext db,
        ILogger<BookingService> logger,
        PromotionService promotions,
        RoomAvailabilityService availability)
    {
        _db = db;
        _logger = logger;
        _promotions = promotions;
        _availability = availability;
    }

    /// <param name="customerId">ลูกค้าที่กำลังจอง (null = ยังไม่ login) ใช้ตรวจโปรที่ขึ้นกับประวัติลูกค้า</param>
    public async Task<BookingConfirmationViewModel> PrepareConfirmationAsync(
        CreateBookingViewModel request,
        int? customerId,
        CancellationToken cancellationToken = default)
    {
        var model = CopyRequest(request);
        var validationMessage = ValidateRequest(request);
        if (validationMessage != null)
        {
            model.StatusMessage = validationMessage;
            return model;
        }

        var room = await _db.Rooms
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.RoomId == request.RoomId,
                cancellationToken);

        if (room == null)
        {
            model.StatusMessage = "ไม่พบห้องพักที่เลือก";
            return model;
        }

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

        var available = await _availability.IsAvailableAsync(
            room.RoomId,
            request.CheckIn!.Value,
            request.CheckOut!.Value,
            cancellationToken);

        if (!available)
        {
            model.StatusMessage = "ห้องพักไม่พร้อมให้จองในช่วงวันที่เลือก";
            return model;
        }

        var subtotal = model.EstimatedAmount.GetValueOrDefault();
        var nights = model.NightCount.GetValueOrDefault();
        model.RoomSubtotal = subtotal;
        model.ExtraBedCount = request.ExtraBedCount;

        // ระบบเลือกโปรที่ดีที่สุดให้อัตโนมัติ
        var promotion = await _promotions.FindBestAsync(
            new PromotionContext(customerId, request.CheckIn!.Value, request.CheckOut!.Value, subtotal),
            cancellationToken);
        model.PromotionId = promotion?.PromotionId;
        model.PromotionName = promotion?.Name;
        model.DiscountAmount = promotion?.DiscountAmount ?? 0m;
        model.ExtraBedAmount = 500m * request.ExtraBedCount * nights;
        model.TotalAmount = subtotal - model.DiscountAmount + model.ExtraBedAmount;

        if (model.TotalAmount > 99999999.99m)
        {
            model.StatusMessage = "ยอดรวมเกินขอบเขตที่ระบบรองรับ";
            return model;
        }

        model.CanConfirm = true;
        return model;
    }

    public async Task<BookingSaveResult> CreateAsync(
        int userId,
        CreateBookingViewModel request,
        CancellationToken cancellationToken = default)
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

        await _db.Database.OpenConnectionAsync(cancellationToken);
        var connection = _db.Database.GetDbConnection();
        IDbContextTransaction? transaction = null;
        var transactionCompleted = false;
        var lockAcquired = false;

        try
        {
            var activeTransaction =
                await _db.Database.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted,
                    cancellationToken);
            transaction = activeTransaction;
            var dbTransaction = activeTransaction.GetDbTransaction();

            async Task<BookingSaveResult> RollbackWithResultAsync(
                BookingSaveResult result)
            {
                await activeTransaction.RollbackAsync(cancellationToken);
                transactionCompleted = true;
                return result;
            }

            var userIsCustomer = await _db.Users
                .AsNoTracking()
                .AnyAsync(
                    user => user.UserId == userId &&
                            user.Role == "Customer",
                    cancellationToken);

            if (!userIsCustomer)
            {
                return await RollbackWithResultAsync(new BookingSaveResult(
                    BookingSaveStatus.UserNotFound,
                    Message: "ไม่พบบัญชีลูกค้าที่กำลังใช้งาน"));
            }

            var roomExists = await RoomAvailabilityService.LockRoomAsync(
                _db,
                activeTransaction,
                request.RoomId,
                cancellationToken);

            if (!roomExists)
            {
                return await RollbackWithResultAsync(new BookingSaveResult(
                    BookingSaveStatus.RoomNotFound,
                    Message: "ไม่พบห้องพักที่เลือก"));
            }

            var existingRequest = await _db.Bookings
                .AsNoTracking()
                .Where(booking =>
                    booking.Status != BookingStatuses.Cancelled &&
                    booking.UserId == userId &&
                    booking.RoomId == request.RoomId &&
                    booking.CheckIn == request.CheckIn &&
                    booking.CheckOut == request.CheckOut &&
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
                .FirstOrDefaultAsync(cancellationToken);

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

            var room = await _db.Rooms
                .SingleOrDefaultAsync(
                    item => item.RoomId == request.RoomId,
                    cancellationToken);

            if (room == null)
            {
                return await RollbackWithResultAsync(new BookingSaveResult(
                    BookingSaveStatus.RoomNotFound,
                    Message: "ไม่พบห้องพักที่เลือก"));
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

            if (!await _availability.IsAvailableAsync(
                    room.RoomId,
                    request.CheckIn!.Value,
                    request.CheckOut!.Value,
                    cancellationToken))
            {
                return await RollbackWithResultAsync(new BookingSaveResult(
                    BookingSaveStatus.RoomUnavailable,
                    Message: "ห้องพักไม่พร้อมให้จองในช่วงวันที่เลือก"));
            }

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

            // คำนวณโปรใหม่ตอนบันทึกจริง (ไม่เชื่อค่าจากหน้าเว็บ)
            var promotion = await _promotions.FindBestAsync(
                new PromotionContext(userId, request.CheckIn.Value, request.CheckOut.Value, roomSubtotal),
                cancellationToken);
            var promotionId = promotion?.PromotionId;
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

            var lockResult = await GetNamedLockAsync(
                connection,
                dbTransaction,
                CancellationToken.None);

            if (lockResult != 1)
            {
                if (lockResult == 0)
                {
                    return await RollbackWithResultAsync(new BookingSaveResult(
                        BookingSaveStatus.LockTimeout,
                        Message: "ระบบกำลังบันทึกการจองอื่น กรุณาลองอีกครั้ง"));
                }

                throw new InvalidOperationException(
                    "MySQL did not return a valid result for GET_LOCK.");
            }

            lockAcquired = true;
            var bookingId = await GetNextBookingIdAsync(
                connection,
                dbTransaction,
                cancellationToken);

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
                Amount = amount
            });

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
        finally
        {
            if (lockAcquired)
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

    private async Task<int?> GetNamedLockAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT GET_LOCK(@lockName, @timeoutSeconds)";
        AddParameter(command, "@lockName", BookingIdLockName);
        AddParameter(command, "@timeoutSeconds", BookingIdLockTimeoutSeconds);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result == null || result == DBNull.Value)
        {
            return null;
        }

        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<int> GetNextBookingIdAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT `BookingId` FROM `bookings` ORDER BY `BookingId` DESC LIMIT 1 FOR UPDATE";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        var maxId = result == null || result == DBNull.Value
            ? 0
            : Convert.ToInt32(
                result,
                System.Globalization.CultureInfo.InvariantCulture);

        if (maxId == int.MaxValue)
        {
            throw new InvalidOperationException(
                "No more BookingId values are available.");
        }

        return maxId + 1;
    }

    private async Task ReleaseNamedLockAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT RELEASE_LOCK(@lockName)";
        AddParameter(command, "@lockName", BookingIdLockName);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result == null || result == DBNull.Value ||
            Convert.ToInt32(
                result,
                System.Globalization.CultureInfo.InvariantCulture) != 1)
        {
            _logger.LogError(
                "MySQL did not confirm release of the BookingId named lock.");
            throw new InvalidOperationException(
                "Could not confirm release of the BookingId named lock.");
        }
    }

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
