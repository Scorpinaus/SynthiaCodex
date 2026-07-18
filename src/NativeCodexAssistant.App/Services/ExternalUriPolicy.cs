namespace NativeCodexAssistant.App.Services;

public static class ExternalUriPolicy
{
    public static bool IsSupported(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    public static bool TryCreateSupportedUri(string? value, out Uri uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri!) && IsSupported(uri);
}
