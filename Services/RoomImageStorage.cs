using Microsoft.Extensions.Logging;

namespace GoodHotel.Services;

public static class RoomImageStorage
{
    public const string DefaultImagePath = "/images/default-room.svg";

    private const string UploadPathPrefix = "/uploads/rooms/";
    private const long MaximumFileSize = 5 * 1024 * 1024;

    public static string? GetUploadValidationError(IFormFile? file)
    {
        if (file == null)
        {
            return null;
        }

        if (file.Length == 0 || file.Length > MaximumFileSize)
        {
            return "Image must be larger than 0 bytes and no more than 5 MB.";
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var expectedContentType = extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => null
        };

        if (expectedContentType == null ||
            !string.Equals(
                file.ContentType,
                expectedContentType,
                StringComparison.OrdinalIgnoreCase))
        {
            return "Only valid JPG, JPEG, PNG, or WEBP images are allowed.";
        }

        return null;
    }

    public static async Task<string> SaveAsync(
        IFormFile file,
        string webRootPath,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var validationError = GetUploadValidationError(file);
        if (validationError != null)
        {
            throw new InvalidDataException(validationError);
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var uploadDirectory = Path.Combine(webRootPath, "uploads", "rooms");
        Directory.CreateDirectory(uploadDirectory);

        var fileName = $"{Guid.NewGuid():N}{extension}";
        var physicalPath = Path.GetFullPath(Path.Combine(uploadDirectory, fileName));
        EnsurePathIsWithinDirectory(physicalPath, uploadDirectory);

        await using var input = file.OpenReadStream();
        if (!await HasValidImageSignatureAsync(input, extension, cancellationToken))
        {
            throw new InvalidDataException(
                "The uploaded file content does not match its image type.");
        }

        input.Position = 0;

        try
        {
            await using var output = new FileStream(
                physicalPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);
            await input.CopyToAsync(output, cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            try
            {
                if (File.Exists(physicalPath))
                {
                    File.Delete(physicalPath);
                }
            }
            catch (Exception cleanupException) when (
                cleanupException is IOException or UnauthorizedAccessException)
            {
                logger.LogError(
                    cleanupException,
                    "Could not remove a partial room image upload.");
            }

            throw;
        }

        return $"{UploadPathPrefix}{fileName}";
    }

    public static string GetDisplayPath(string? webRootPath, string? imagePath)
    {
        if (webRootPath == null ||
            !TryGetPhysicalPath(webRootPath, imagePath, out var physicalPath) ||
            !File.Exists(physicalPath))
        {
            return DefaultImagePath;
        }

        return imagePath!;
    }

    public static bool TryGetPhysicalPath(
        string webRootPath,
        string? imagePath,
        out string physicalPath)
    {
        physicalPath = string.Empty;

        if (string.IsNullOrWhiteSpace(imagePath) ||
            !imagePath.StartsWith(UploadPathPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var fileName = imagePath[UploadPathPrefix.Length..];
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName != Path.GetFileName(fileName) ||
            fileName.Contains('/') ||
            fileName.Contains('\\'))
        {
            return false;
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp"))
        {
            return false;
        }

        var uploadDirectory = Path.GetFullPath(
            Path.Combine(webRootPath, "uploads", "rooms"));
        physicalPath = Path.GetFullPath(Path.Combine(uploadDirectory, fileName));

        try
        {
            EnsurePathIsWithinDirectory(physicalPath, uploadDirectory);
            return true;
        }
        catch (InvalidDataException)
        {
            physicalPath = string.Empty;
            return false;
        }
    }

    private static async Task<bool> HasValidImageSignatureAsync(
        Stream stream,
        string extension,
        CancellationToken cancellationToken)
    {
        var header = new byte[12];
        var bytesRead = await stream.ReadAsync(
            header.AsMemory(),
            cancellationToken);

        return extension switch
        {
            ".jpg" or ".jpeg" =>
                bytesRead >= 3 &&
                header[0] == 0xFF &&
                header[1] == 0xD8 &&
                header[2] == 0xFF,
            ".png" =>
                bytesRead >= 8 &&
                header.AsSpan(0, 8).SequenceEqual(
                    new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ".webp" =>
                bytesRead >= 12 &&
                header.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
                header.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false
        };
    }

    private static void EnsurePathIsWithinDirectory(
        string physicalPath,
        string directoryPath)
    {
        var fullDirectory = Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(physicalPath);

        if (!fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Invalid room image path.");
        }
    }
}
