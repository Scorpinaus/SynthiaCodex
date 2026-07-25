using System.IO;

namespace SynthiaCode.App.Services;

public static class LocalImageResourcePolicy
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".webp",
        ".gif"
    };

    public static bool IsSupported(Uri? uri, out string path)
    {
        path = string.Empty;
        return uri is { IsAbsoluteUri: true, IsFile: true } &&
               !uri.IsUnc &&
               TryResolve(uri.LocalPath, out path, out _);
    }

    public static bool TryCreateSupportedUri(string? value, out Uri uri, out string path)
    {
        uri = null!;
        path = string.Empty;
        if (!TryResolve(value, out path, out uri))
        {
            return false;
        }

        return true;
    }

    private static bool TryResolve(string? value, out string path, out Uri uri)
    {
        path = string.Empty;
        uri = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var candidate = value.Trim();
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) && parsed.IsFile)
            {
                if (parsed.IsUnc)
                {
                    return false;
                }

                candidate = parsed.LocalPath;
            }

            if (!Path.IsPathFullyQualified(candidate) ||
                candidate.StartsWith(@"\\", StringComparison.Ordinal) ||
                !SupportedExtensions.Contains(Path.GetExtension(candidate)))
            {
                return false;
            }

            var fullPath = Path.GetFullPath(candidate);
            if (!File.Exists(fullPath))
            {
                return false;
            }

            path = fullPath;
            uri = new Uri(fullPath, UriKind.Absolute);
            return uri.IsFile && !uri.IsUnc;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException or
            UriFormatException)
        {
            return false;
        }
    }
}
