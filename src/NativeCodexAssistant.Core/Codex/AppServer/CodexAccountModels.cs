using System.Text.Json.Nodes;

namespace NativeCodexAssistant.Core.Codex.AppServer;

public sealed record CodexAccountInfo(
    string Type,
    string? Email,
    string? PlanType,
    string? CredentialSource);

public sealed record CodexAccountReadResult(
    CodexAccountInfo? Account,
    bool RequiresOpenAiAuth);

public sealed record CodexRateLimitWindow(
    int UsedPercent,
    long? WindowDurationMins,
    DateTimeOffset? ResetsAt);

public sealed record CodexCreditsSnapshot(
    bool HasCredits,
    bool Unlimited,
    string? Balance);

public sealed record CodexRateLimitSnapshot(
    string? LimitId,
    string? LimitName,
    string? PlanType,
    CodexRateLimitWindow? Primary,
    CodexRateLimitWindow? Secondary,
    CodexCreditsSnapshot? Credits,
    string? RateLimitReachedType);

public sealed record CodexAccountRateLimitsResult(
    IReadOnlyList<CodexRateLimitSnapshot> Limits,
    int? ResetCreditsAvailable);

public static class CodexAccountProtocolParser
{
    public static CodexAccountReadResult ParseAccount(JsonObject? result)
    {
        var account = result?["account"] as JsonObject;
        CodexAccountInfo? parsedAccount = null;
        if (account is not null)
        {
            var type = ReadString(account, "type") ?? "unknown";
            parsedAccount = new CodexAccountInfo(
                type,
                ReadString(account, "email"),
                ReadString(account, "planType"),
                ReadString(account, "credentialSource"));
        }

        return new CodexAccountReadResult(
            parsedAccount,
            ReadBoolean(result, "requiresOpenaiAuth") ?? false);
    }

    public static CodexAccountRateLimitsResult ParseRateLimits(JsonObject? result)
    {
        var limits = new List<CodexRateLimitSnapshot>();
        if (result?["rateLimitsByLimitId"] is JsonObject byLimitId)
        {
            foreach (var entry in byLimitId)
            {
                if (entry.Value is JsonObject value)
                {
                    limits.Add(ParseRateLimitSnapshot(value, entry.Key));
                }
            }
        }

        if (result?["rateLimits"] is JsonObject legacy)
        {
            var parsedLegacy = ParseRateLimitSnapshot(legacy);
            var isDuplicate = limits.Any(item =>
                !string.IsNullOrWhiteSpace(parsedLegacy.LimitId) &&
                string.Equals(item.LimitId, parsedLegacy.LimitId, StringComparison.OrdinalIgnoreCase));
            if (!isDuplicate)
            {
                limits.Add(parsedLegacy);
            }
        }

        limits = limits
            .OrderBy(item => string.Equals(item.LimitId, "codex", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.LimitName ?? item.LimitId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CodexAccountRateLimitsResult(
            limits,
            ReadInteger(result?["rateLimitResetCredits"] as JsonObject, "availableCount"));
    }

    public static CodexRateLimitSnapshot ParseRateLimitSnapshot(JsonObject value, string? fallbackLimitId = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new CodexRateLimitSnapshot(
            ReadString(value, "limitId") ?? fallbackLimitId,
            ReadString(value, "limitName"),
            ReadString(value, "planType"),
            ParseWindow(value["primary"] as JsonObject),
            ParseWindow(value["secondary"] as JsonObject),
            ParseCredits(value["credits"] as JsonObject),
            ReadString(value, "rateLimitReachedType"));
    }

    private static CodexRateLimitWindow? ParseWindow(JsonObject? value)
    {
        if (value is null || ReadInteger(value, "usedPercent") is not { } usedPercent)
        {
            return null;
        }

        return new CodexRateLimitWindow(
            usedPercent,
            ReadLong(value, "windowDurationMins"),
            ReadUnixTimestamp(value, "resetsAt"));
    }

    private static CodexCreditsSnapshot? ParseCredits(JsonObject? value)
    {
        if (value is null)
        {
            return null;
        }

        return new CodexCreditsSnapshot(
            ReadBoolean(value, "hasCredits") ?? false,
            ReadBoolean(value, "unlimited") ?? false,
            ReadString(value, "balance"));
    }

    private static string? ReadString(JsonObject? value, string propertyName) =>
        value?[propertyName] is JsonValue node && node.TryGetValue<string>(out var result)
            ? result
            : null;

    private static bool? ReadBoolean(JsonObject? value, string propertyName) =>
        value?[propertyName] is JsonValue node && node.TryGetValue<bool>(out var result)
            ? result
            : null;

    private static int? ReadInteger(JsonObject? value, string propertyName)
    {
        if (value?[propertyName] is not JsonValue node)
        {
            return null;
        }

        if (node.TryGetValue<int>(out var intValue))
        {
            return intValue;
        }

        return node.TryGetValue<long>(out var longValue) && longValue is >= int.MinValue and <= int.MaxValue
            ? (int)longValue
            : null;
    }

    private static long? ReadLong(JsonObject? value, string propertyName)
    {
        if (value?[propertyName] is not JsonValue node)
        {
            return null;
        }

        if (node.TryGetValue<long>(out var longValue))
        {
            return longValue;
        }

        return node.TryGetValue<int>(out var intValue) ? intValue : null;
    }

    private static DateTimeOffset? ReadUnixTimestamp(JsonObject value, string propertyName)
    {
        var seconds = ReadLong(value, propertyName);
        if (seconds is null)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
