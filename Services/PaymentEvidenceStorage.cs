using System.Security.Cryptography;

namespace GoodHotel.Services;

public sealed class PaymentEvidenceStorage(
    IWebHostEnvironment environment, IConfiguration configuration, ILogger<PaymentEvidenceStorage> logger)
{
    private const long MaximumBytes = 5 * 1024 * 1024;
    private string DirectoryPath
    {
        get
        {
            var configured = configuration["Payments:EvidenceDirectory"];
            if (!string.IsNullOrWhiteSpace(configured) && !Path.IsPathFullyQualified(configured))
                throw new InvalidOperationException("The payment evidence directory must be an absolute path.");
            var directory = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(environment.ContentRootPath, "App_Data", "payment-evidence") : configured);
            if (!string.IsNullOrWhiteSpace(environment.WebRootPath))
            {
                var webRoot = Path.GetFullPath(environment.WebRootPath).TrimEnd(Path.DirectorySeparatorChar);
                if (directory.Equals(webRoot, StringComparison.OrdinalIgnoreCase) ||
                    directory.StartsWith(webRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Payment evidence must be stored outside the web root.");
            }
            return directory;
        }
    }

    public async Task<(string FileName, string Sha256)> SaveAsync(IFormFile file, CancellationToken ct)
    {
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var contentType = extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => null
        };
        if (file.Length <= 0 || file.Length > MaximumBytes || contentType == null ||
            !string.Equals(file.ContentType, contentType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("หลักฐานต้องเป็นรูป JPG, PNG หรือ WEBP ขนาดไม่เกิน 5 MB");

        await using var input = file.OpenReadStream();
        var header = new byte[12];
        var read = await input.ReadAsync(header.AsMemory(), ct);
        var valid = extension switch
        {
            ".jpg" or ".jpeg" => read >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff,
            ".png" => read >= 8 && header.AsSpan(0, 8).SequenceEqual(
                new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
            ".webp" => read >= 12 && header.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
                header.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false
        };
        if (!valid) throw new InvalidDataException("ไฟล์หลักฐานไม่ตรงกับชนิดรูปภาพ");
        input.Position = 0;
        Directory.CreateDirectory(DirectoryPath);
        var fileName = Guid.NewGuid().ToString("N") + extension;
        var path = GetPath(fileName);
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, useAsync: true))
                await input.CopyToAsync(output, ct);
            await using var saved = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(saved, ct);
            return (fileName, Convert.ToHexString(hash));
        }
        catch
        {
            Delete(fileName);
            throw;
        }
    }

    public string GetPath(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(fileName), "N", out _) ||
            extension is not (".jpg" or ".jpeg" or ".png" or ".webp") ||
            fileName != Path.GetFileName(fileName))
            throw new InvalidDataException("ไม่พบหลักฐาน");
        return Path.Combine(DirectoryPath, fileName);
    }

    public void Delete(string fileName)
    {
        try
        {
            File.Delete(GetPath(fileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not delete payment evidence {EvidenceFileName}.", fileName);
        }
    }
}
