using System.Buffers.Binary;
using System.Security.Cryptography;
using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Logging;

namespace SynthiaCode.Infrastructure.Attachments;

public sealed class LocalAttachmentStore : IAttachmentStore
{
    private static readonly TimeSpan StagingRetention = TimeSpan.FromHours(24);
    private static readonly TimeSpan OrphanRetention = TimeSpan.FromDays(7);
    private readonly string rootPath;
    private readonly string objectPath;
    private readonly string stagingPath;
    private readonly IAppLogger logger;
    private readonly SemaphoreSlim importGate = new(1, 1);

    public LocalAttachmentStore(string rootPath, IAppLogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        this.rootPath = Path.GetFullPath(rootPath);
        objectPath = Path.Combine(this.rootPath, "objects");
        stagingPath = Path.Combine(this.rootPath, "staging");
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Directory.CreateDirectory(objectPath);
        Directory.CreateDirectory(stagingPath);
    }

    public async Task<AttachmentReference> ImportFileAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The selected image no longer exists.", fullPath);
        }
        if (info.Length > AttachmentLimits.MaximumBytesPerImage)
        {
            throw new InvalidDataException($"The image exceeds the {AttachmentLimits.MaximumBytesPerImage / (1024 * 1024)} MiB limit.");
        }

        await using var source = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ImportStreamAsync(source, info.Name, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AttachmentReference> ImportStreamAsync(
        Stream source,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var stagingFile = Path.Combine(stagingPath, $"{Guid.NewGuid():N}.tmp");
        await importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            await using (var target = new FileStream(
                             stagingFile,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }
                    length += read;
                    if (length > AttachmentLimits.MaximumBytesPerImage)
                    {
                        throw new InvalidDataException($"The image exceeds the {AttachmentLimits.MaximumBytesPerImage / (1024 * 1024)} MiB limit.");
                    }
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (length == 0)
            {
                throw new InvalidDataException("The selected image is empty.");
            }

            var bytes = await File.ReadAllBytesAsync(stagingFile, cancellationToken).ConfigureAwait(false);
            var metadata = ImageMetadata.Read(bytes);
            ValidateDimensions(metadata.Width, metadata.Height);
            var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            var storageKey = $"objects/{sha256[..2]}/{sha256}{metadata.Extension}";
            var finalPath = ResolveStorageKey(storageKey);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

            if (!File.Exists(finalPath))
            {
                var currentBytes = Directory.EnumerateFiles(objectPath, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length);
                if (currentBytes + length > AttachmentLimits.MaximumStoreBytes)
                {
                    throw new IOException("Attachment storage is full. Remove unused attachment images before trying again.");
                }
                File.Move(stagingFile, finalPath);
            }
            else
            {
                File.Delete(stagingFile);
            }

            return new AttachmentReference
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = AttachmentKind.Image,
                SourceKind = AttachmentSourceKind.ManagedCopy,
                StorageKey = storageKey,
                DisplayName = SanitizeDisplayName(displayName, metadata.Extension),
                MediaType = metadata.MediaType,
                ByteLength = length,
                PixelWidth = metadata.Width,
                PixelHeight = metadata.Height,
                ContentSha256 = sha256,
                ManagedPath = finalPath
            };
        }
        catch
        {
            TryDelete(stagingFile);
            throw;
        }
        finally
        {
            importGate.Release();
        }
    }

    public string ResolvePath(AttachmentReference attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        var path = ResolveStorageKey(attachment.StorageKey);
        attachment.ManagedPath = path;
        return path;
    }

    public async Task CleanupAsync(
        IEnumerable<string> referencedStorageKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(referencedStorageKeys);
        var referenced = referencedStorageKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(NormalizeStorageKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        await importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var file in Directory.EnumerateFiles(stagingPath, "*.tmp", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (now - File.GetLastWriteTimeUtc(file) >= StagingRetention)
                {
                    TryDelete(file);
                }
            }

            foreach (var file in Directory.EnumerateFiles(objectPath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = NormalizeStorageKey(Path.GetRelativePath(rootPath, file));
                if (!referenced.Contains(key) && now - File.GetLastWriteTimeUtc(file) >= OrphanRetention)
                {
                    TryDelete(file);
                }
            }
        }
        finally
        {
            importGate.Release();
        }
    }

    private string ResolveStorageKey(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey) || Path.IsPathRooted(storageKey))
        {
            throw new InvalidDataException("The attachment storage reference is invalid.");
        }
        var normalized = NormalizeStorageKey(storageKey);
        if (!normalized.StartsWith("objects/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Split('/').Any(segment => segment is ".." or "." or ""))
        {
            throw new InvalidDataException("The attachment storage reference is invalid.");
        }
        var candidate = Path.GetFullPath(Path.Combine(rootPath, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The attachment storage reference escapes the managed store.");
        }
        return candidate;
    }

    private static string NormalizeStorageKey(string value) => value.Replace('\\', '/').Trim('/');

    private static string SanitizeDisplayName(string value, string extension)
    {
        var leaf = Path.GetFileName(value ?? string.Empty);
        var cleaned = new string(leaf.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = $"image{extension}";
        }
        if (cleaned.Length > 120)
        {
            cleaned = cleaned[..Math.Min(110, cleaned.Length)] + extension;
        }
        return cleaned;
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > AttachmentLimits.MaximumPixelEdge ||
            height > AttachmentLimits.MaximumPixelEdge || (long)width * height > AttachmentLimits.MaximumPixelCount)
        {
            throw new InvalidDataException("The image dimensions exceed the safe preview limit.");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            logger.Log(AppLogLevel.Warning, "attachment_cleanup_failed", "An attachment cache file could not be removed.", exception: ex);
        }
    }

    private sealed record ImageMetadata(string MediaType, string Extension, int Width, int Height)
    {
        public static ImageMetadata Read(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length >= 24 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                return new("image/png", ".png", BinaryPrimitives.ReadInt32BigEndian(bytes[16..20]), BinaryPrimitives.ReadInt32BigEndian(bytes[20..24]));
            }
            if (bytes.Length >= 10 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
            {
                if (CountByte(bytes, 0x2C) > 1)
                {
                    throw new InvalidDataException("Animated GIF images are not supported.");
                }
                return new("image/gif", ".gif", BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..8]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..10]));
            }
            if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
            {
                return ReadJpeg(bytes);
            }
            if (bytes.Length >= 30 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
            {
                return ReadWebP(bytes);
            }
            throw new InvalidDataException("The selected file is not a supported PNG, JPEG, WebP, or GIF image.");
        }

        private static ImageMetadata ReadJpeg(ReadOnlySpan<byte> bytes)
        {
            var index = 2;
            while (index + 8 < bytes.Length)
            {
                if (bytes[index] != 0xFF)
                {
                    index++;
                    continue;
                }
                var marker = bytes[index + 1];
                if (marker is 0xD8 or 0xD9)
                {
                    index += 2;
                    continue;
                }
                if (index + 4 > bytes.Length)
                {
                    break;
                }
                var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes[(index + 2)..(index + 4)]);
                if (segmentLength < 2 || index + 2 + segmentLength > bytes.Length)
                {
                    break;
                }
                if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
                {
                    var height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(index + 5)..(index + 7)]);
                    var width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(index + 7)..(index + 9)]);
                    return new("image/jpeg", ".jpg", width, height);
                }
                index += 2 + segmentLength;
            }
            throw new InvalidDataException("The JPEG image does not contain valid dimensions.");
        }

        private static ImageMetadata ReadWebP(ReadOnlySpan<byte> bytes)
        {
            var kind = bytes[12..16];
            if (kind.SequenceEqual("VP8X"u8) && bytes.Length >= 30)
            {
                var width = 1 + bytes[24] + (bytes[25] << 8) + (bytes[26] << 16);
                var height = 1 + bytes[27] + (bytes[28] << 8) + (bytes[29] << 16);
                return new("image/webp", ".webp", width, height);
            }
            if (kind.SequenceEqual("VP8L"u8) && bytes.Length >= 25 && bytes[20] == 0x2F)
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..25]);
                var width = (int)(bits & 0x3FFF) + 1;
                var height = (int)((bits >> 14) & 0x3FFF) + 1;
                return new("image/webp", ".webp", width, height);
            }
            if (kind.SequenceEqual("VP8 "u8) && bytes.Length >= 30 && bytes[23] == 0x9D && bytes[24] == 0x01 && bytes[25] == 0x2A)
            {
                var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..28]) & 0x3FFF;
                var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..30]) & 0x3FFF;
                return new("image/webp", ".webp", width, height);
            }
            throw new InvalidDataException("The WebP image header is invalid.");
        }

        private static int CountByte(ReadOnlySpan<byte> bytes, byte value)
        {
            var count = 0;
            foreach (var item in bytes)
            {
                if (item == value)
                {
                    count++;
                }
            }
            return count;
        }
    }
}
