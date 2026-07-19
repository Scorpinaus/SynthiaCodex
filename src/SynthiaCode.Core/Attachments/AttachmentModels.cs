using System.Text.Json.Serialization;

namespace SynthiaCode.Core.Attachments;

public static class AttachmentLimits
{
    public const int MaximumImagesPerInput = 10;
    public const long MaximumBytesPerImage = 20L * 1024 * 1024;
    public const long MaximumBytesPerInput = 50L * 1024 * 1024;
    public const long MaximumPixelCount = 50_000_000;
    public const int MaximumPixelEdge = 32_768;
    public const long MaximumStoreBytes = 1024L * 1024 * 1024;
}

public sealed class AttachmentReference
{
    public string Id { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string MediaType { get; set; } = string.Empty;
    public long ByteLength { get; set; }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    public string ContentSha256 { get; set; } = string.Empty;

    [JsonIgnore]
    public string? ManagedPath { get; set; }

    [JsonIgnore]
    public bool IsAvailable => !string.IsNullOrWhiteSpace(ManagedPath) && File.Exists(ManagedPath);

    [JsonIgnore]
    public string DimensionsLabel => PixelWidth > 0 && PixelHeight > 0
        ? $"{PixelWidth} x {PixelHeight}"
        : "Unknown dimensions";

    [JsonIgnore]
    public string SizeLabel => ByteLength < 1024 * 1024
        ? $"{Math.Max(1, ByteLength / 1024d):0.#} KiB"
        : $"{ByteLength / (1024d * 1024d):0.#} MiB";

    public AttachmentReference Clone() => new()
    {
        Id = Id,
        StorageKey = StorageKey,
        DisplayName = DisplayName,
        MediaType = MediaType,
        ByteLength = ByteLength,
        PixelWidth = PixelWidth,
        PixelHeight = PixelHeight,
        ContentSha256 = ContentSha256,
        ManagedPath = ManagedPath
    };
}

public interface IAttachmentStore
{
    Task<AttachmentReference> ImportFileAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);

    Task<AttachmentReference> ImportStreamAsync(
        Stream source,
        string displayName,
        CancellationToken cancellationToken = default);

    string ResolvePath(AttachmentReference attachment);

    Task CleanupAsync(
        IEnumerable<string> referencedStorageKeys,
        CancellationToken cancellationToken = default);
}
