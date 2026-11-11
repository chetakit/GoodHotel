using System.Data;
using System.Data.Common;
using GoodHotel.Models;
using GoodHotel.Models.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MySqlConnector;

namespace GoodHotel.Services;

public enum PromotionWriteStatus
{
    Saved,
    NotFound,
    DuplicateCode,
    InvalidMetadata,
    LockTimeout
}

public sealed record PromotionWriteResult(
    PromotionWriteStatus Status,
    string? Message = null);

public sealed class PromotionManagementService
{
    private const string PromotionWriteLockName = "GoodHotel.PromotionWrite";
    private const int LockTimeoutSeconds = 5;

    private readonly GoodHotelDbContext _db;
    private readonly PromotionMetadataService _metadataService;
    private readonly ILogger<PromotionManagementService> _logger;

    public PromotionManagementService(
        GoodHotelDbContext db,
        PromotionMetadataService metadataService,
        ILogger<PromotionManagementService> logger)
    {
        _db = db;
        _metadataService = metadataService;
        _logger = logger;
    }

    public async Task<PromotionWriteResult> SaveAsync(
        PromotionFormViewModel form,
        CancellationToken cancellationToken = default)
    {
        var connection = _db.Database.GetDbConnection();
        var connectionWasOpen = connection.State == ConnectionState.Open;
        var openedHere = !connectionWasOpen;
        if (openedHere)
        {
            await _db.Database.OpenConnectionAsync(cancellationToken);
        }

        IDbContextTransaction? transaction = null;
        var transactionCompleted = false;
        var lockAcquired = false;

        try
        {
            transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
            var dbTransaction = transaction.GetDbTransaction();
            var lockResult = await GetWriteLockAsync(
                connection,
                dbTransaction,
                cancellationToken);
            if (lockResult != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                transactionCompleted = true;
                return lockResult == 0
                    ? new PromotionWriteResult(
                        PromotionWriteStatus.LockTimeout,
                        "ระบบกำลังบันทึกโปรโมชั่นอื่น กรุณาลองอีกครั้ง")
                    : throw new InvalidOperationException(
                        "MySQL did not return a valid result for the promotion write lock.");
            }

            lockAcquired = true;
            var promotions = await _db.Promotions
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            var promotion = form.PromotionId.HasValue
                ? await _db.Promotions.FirstOrDefaultAsync(
                    item => item.PromotionId == form.PromotionId.Value,
                    cancellationToken)
                : null;

            if (form.PromotionId.HasValue && promotion == null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                transactionCompleted = true;
                return new PromotionWriteResult(
                    PromotionWriteStatus.NotFound,
                    "ไม่พบโปรโมชั่นที่ต้องการแก้ไข");
            }

            var currentParse = promotion == null
                ? new PromotionMetadataParseResult(true, null, null)
                : _metadataService.Parse(promotion.Condition);

            if (currentParse.Error != null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                transactionCompleted = true;
                return new PromotionWriteResult(
                    PromotionWriteStatus.InvalidMetadata,
                    "Metadata ของโปรโมชั่นเดิมไม่ถูกต้อง จึงไม่สามารถแก้ไขทับได้");
            }

            if (promotion == null && !form.UseDiscountCode)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                transactionCompleted = true;
                return new PromotionWriteResult(
                    PromotionWriteStatus.InvalidMetadata,
                    "โปรโมชั่นใหม่ต้องกำหนด Discount Code");
            }

            if (promotion != null &&
                !currentParse.IsLegacy &&
                !form.UseDiscountCode)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                transactionCompleted = true;
                return new PromotionWriteResult(
                    PromotionWriteStatus.InvalidMetadata,
                    "โปรโมชั่นที่มี Discount Code ต้องคง Metadata ไว้");
            }

            string? serializedMetadata = null;
            if (form.UseDiscountCode)
            {
                if (!_metadataService.TryParseDate(form.StartDate, out var startDate) ||
                    !_metadataService.TryParseDate(form.EndDate, out var endDate))
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    transactionCompleted = true;
                    return new PromotionWriteResult(
                        PromotionWriteStatus.InvalidMetadata,
                        "กรุณาระบุวันที่ในรูปแบบ yyyy-MM-dd");
                }

                var metadata = new PromotionMetadata(
                    form.PromotionCode ?? string.Empty,
                    form.DiscountType ?? string.Empty,
                    form.DiscountValue ?? 0,
                    startDate,
                    endDate,
                    form.IsActive);
                var validationError = _metadataService.Validate(
                    metadata,
                    out var normalizedMetadata);
                if (validationError != null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    transactionCompleted = true;
                    return new PromotionWriteResult(
                        PromotionWriteStatus.InvalidMetadata,
                        validationError);
                }

                foreach (var other in promotions)
                {
                    if (other.PromotionId == form.PromotionId)
                    {
                        continue;
                    }

                    var parsed = _metadataService.Parse(other.Condition);
                    if (parsed.Error != null)
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                        transactionCompleted = true;
                        return new PromotionWriteResult(
                            PromotionWriteStatus.InvalidMetadata,
                            "พบ Metadata ของโปรโมชั่นเดิมที่ไม่ถูกต้อง จึงไม่สามารถยืนยัน Code ว่าไม่ซ้ำได้");
                    }

                    if (parsed.Metadata != null &&
                        string.Equals(
                            parsed.Metadata.Code,
                            normalizedMetadata.Code,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                        transactionCompleted = true;
                        return new PromotionWriteResult(
                            PromotionWriteStatus.DuplicateCode,
                            "Promotion Code นี้ถูกใช้งานแล้ว");
                    }
                }

                serializedMetadata = _metadataService.Serialize(normalizedMetadata);
            }

            if (promotion == null)
            {
                var maxPromotionId = promotions.Count == 0
                    ? 0
                    : promotions.Max(item => item.PromotionId);
                if (maxPromotionId == int.MaxValue)
                {
                    throw new InvalidOperationException(
                        "No more PromotionId values are available.");
                }

                promotion = new Promotion
                {
                    PromotionId = maxPromotionId + 1,
                    Condition = serializedMetadata
                };
                _db.Promotions.Add(promotion);
            }
            else if (serializedMetadata != null)
            {
                promotion.Condition = serializedMetadata;
            }

            promotion.PromotionName = form.PromotionName!.Trim();
            promotion.Description = form.Description?.Trim();
            promotion.Benefit = form.Benefit?.Trim();

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            transactionCompleted = true;
            return new PromotionWriteResult(PromotionWriteStatus.Saved);
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
                        "Could not roll back a promotion write after an error ({ExceptionType}).",
                        rollbackException.GetType().Name);
                }
            }

            throw;
        }
        finally
        {
            if (lockAcquired)
            {
                var lockReleased = false;
                try
                {
                    if (connection.State == ConnectionState.Open)
                    {
                        await ReleaseWriteLockAsync(
                            connection,
                            CancellationToken.None);
                        lockReleased = true;
                    }
                    else
                    {
                        _logger.LogError(
                            "The MySQL connection closed before the promotion write lock was released; invalidating its pool entry.");
                    }
                }
                catch (Exception releaseException)
                {
                    _logger.LogError(
                        "Could not release the promotion write lock ({ExceptionType}); invalidating its MySQL session.",
                        releaseException.GetType().Name);
                }

                if (!lockReleased)
                {
                    await InvalidateLockHoldingConnectionAsync(
                        connection,
                        connectionWasOpen);
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
                        "Could not dispose the promotion transaction cleanly ({ExceptionType}).",
                        disposeException.GetType().Name);
                }
            }

            if (openedHere)
            {
                try
                {
                    await _db.Database.CloseConnectionAsync();
                }
                catch (Exception closeException)
                {
                    _logger.LogError(
                        "Could not close the promotion database connection cleanly ({ExceptionType}).",
                        closeException.GetType().Name);
                }
            }
        }
    }

    private async Task InvalidateLockHoldingConnectionAsync(
        DbConnection connection,
        bool connectionWasOpen)
    {
        if (connection is MySqlConnection mySqlConnection)
        {
            try
            {
                MySqlConnection.ClearPool(mySqlConnection);
            }
            catch (Exception clearPoolException)
            {
                _logger.LogError(
                    "Could not clear the MySQL connection pool after a named-lock release failure ({ExceptionType}).",
                    clearPoolException.GetType().Name);
                try
                {
                    MySqlConnection.ClearAllPools();
                }
                catch (Exception clearAllPoolsException)
                {
                    _logger.LogCritical(
                        "Could not clear MySQL connection pools after a named-lock release failure ({ExceptionType}).",
                        clearAllPoolsException.GetType().Name);
                }
            }
        }
        else
        {
            _logger.LogCritical(
                "The lock-holding connection is not a MySqlConnection; its provider pool cannot be explicitly invalidated.");
        }

        if (connectionWasOpen)
        {
            _logger.LogWarning(
                "The database connection was already open when the promotion save began; it is being closed because the named-lock release could not be confirmed.");
        }

        try
        {
            // Named locks belong to the physical MySQL session. Discarding that session is
            // required even when the DbContext connection was already open on entry.
            await connection.CloseAsync();
        }
        catch (Exception closeException)
        {
            _logger.LogError(
                "Could not close the lock-holding database connection ({ExceptionType}); disposing it as a final cleanup attempt.",
                closeException.GetType().Name);
            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception disposeException)
            {
                _logger.LogCritical(
                    "Could not dispose the lock-holding database connection ({ExceptionType}).",
                    disposeException.GetType().Name);
            }
        }
    }

    private async Task<int?> GetWriteLockAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT GET_LOCK(@lockName, @timeoutSeconds)";
        AddParameter(command, "@lockName", PromotionWriteLockName);
        AddParameter(command, "@timeoutSeconds", LockTimeoutSeconds);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result == null || result == DBNull.Value
            ? null
            : Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task ReleaseWriteLockAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT RELEASE_LOCK(@lockName)";
        AddParameter(command, "@lockName", PromotionWriteLockName);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result == null ||
            result == DBNull.Value ||
            Convert.ToInt32(
                result,
                System.Globalization.CultureInfo.InvariantCulture) != 1)
        {
            throw new InvalidOperationException(
                "Could not confirm release of the promotion write lock.");
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
}
