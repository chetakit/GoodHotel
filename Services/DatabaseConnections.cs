using MySqlConnector;

namespace GoodHotel.Services;

/// <summary>
/// ฐานข้อมูลอยู่ไกล (ไปกลับ ~200ms) ปกติทุกครั้งที่หยิบ connection จาก pool จะ reset ก่อน เสียฟรี 1 รอบทุกหน้า
/// - Read: ใช้กับการเปิดหน้า (GET) ปิดการ reset เพราะอ่านอย่างเดียว ไม่มีอะไรค้างใน session
/// - Write: ใช้กับการบันทึก (POST) และงานเบื้องหลัง ยัง reset ทุกครั้งเหมือนเดิม
///   เพราะการจอง/ชำระเงินใช้ GET_LOCK ซึ่งผูกกับ session ถ้าค้างต้องถูกล้าง
/// connection string คนละค่า = คนละ pool จึงไม่ปนกัน
/// </summary>
public sealed class DatabaseConnections(IConfiguration configuration)
{
    public const int WarmReaders = 6;

    // เงียบเกินนี้แปลว่าเครื่องเพิ่ง sleep หรือเน็ตหลุด connection ที่ค้างอยู่อาจตายแล้ว
    private const long StaleMilliseconds = 100_000;
    private long _lastAlive = Environment.TickCount64;

    public string Write { get; } = configuration.GetConnectionString("DefaultConnection") ?? "";

    public string Read { get; } = new MySqlConnectionStringBuilder(
        configuration.GetConnectionString("DefaultConnection") ?? "")
    {
        ConnectionReset = false,
        MinimumPoolSize = WarmReaders,
        Keepalive = 30
    }.ConnectionString;

    public string For(HttpContext? context) =>
        context != null && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            ? Read : Write;

    public void MarkAlive() => Interlocked.Exchange(ref _lastAlive, Environment.TickCount64);

    /// <summary>เพิ่งตื่นจาก sleep: ทิ้ง connection เก่าก่อน ไม่ให้หน้าแรกที่เปิดเจอ error</summary>
    public void DropIfStale()
    {
        if (Environment.TickCount64 - Interlocked.Read(ref _lastAlive) < StaleMilliseconds) return;
        MarkAlive();
        ClearRead();
    }

    public void ClearRead()
    {
        using var connection = new MySqlConnection(Read);
        MySqlConnection.ClearPool(connection);
    }
}

/// <summary>เปิด connection สำหรับอ่านค้างไว้และเช็คว่ายังใช้ได้ทุก 45 วินาที (เปิดหน้าแรกจะไม่ต้องรอต่อใหม่)</summary>
public sealed class ReadConnectionWarmer(DatabaseConnections connections, ILogger<ReadConnectionWarmer> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(45));
        do
        {
            try
            {
                connections.DropIfStale();
                // เปิดพร้อมกันเพื่อให้ได้คนละเส้น แล้ว ping ทุกเส้น
                await Task.WhenAll(Enumerable.Range(0, DatabaseConnections.WarmReaders).Select(async _ =>
                {
                    await using var connection = new MySqlConnection(connections.Read);
                    await connection.OpenAsync(stoppingToken);
                    if (!await connection.PingAsync(stoppingToken))
                        throw new InvalidOperationException("Read connection did not answer ping.");
                }));
                connections.MarkAlive();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                connections.ClearRead();
                logger.LogWarning(ex, "Read connection check failed; the pool was cleared.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
