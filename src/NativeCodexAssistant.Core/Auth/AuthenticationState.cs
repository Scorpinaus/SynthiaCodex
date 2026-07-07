namespace NativeCodexAssistant.Core.Auth;

public enum AuthReadiness
{
    Unavailable,
    LikelySignedIn,
    NotSignedIn,
    Unknown
}

public enum LoginMethod
{
    ChatGpt,
    ApiKey,
    DeviceCode
}

public sealed record AuthenticationState(
    AuthReadiness Readiness,
    string Summary,
    string Detail,
    string? CodexHome);
