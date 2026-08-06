using System.Security.Cryptography;
using System.Text;
using SynthiaCode.Core.Harnesses;

namespace SynthiaCode.Core.Settings;

public static class AppSettingsHarnessMigration
{
    public const int CurrentSchemaVersion = 1;

    public static bool Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var changed = false;
        var isLegacySettings = settings.HarnessSchemaVersion < CurrentSchemaVersion;

        var defaultHarnessId = NormalizeHarnessId(settings.DefaultHarnessId);
        if (!string.Equals(settings.DefaultHarnessId, defaultHarnessId, StringComparison.Ordinal))
        {
            settings.DefaultHarnessId = defaultHarnessId;
            changed = true;
        }

        foreach (var thread in settings.ProjectThreads)
        {
            var harnessId = NormalizeHarnessId(thread.HarnessId);
            var remoteId = string.IsNullOrWhiteSpace(thread.RemoteConversationId) && isLegacySettings
                ? NormalizeRemoteId(thread.ThreadId)
                : NormalizeRemoteId(thread.RemoteConversationId);
            var conversationId = ResolveConversationId(
                thread.ConversationId,
                harnessId,
                remoteId,
                thread.ThreadId);

            if (!string.Equals(thread.HarnessId, harnessId, StringComparison.Ordinal))
            {
                thread.HarnessId = harnessId;
                changed = true;
            }
            if (!string.Equals(thread.RemoteConversationId, remoteId, StringComparison.Ordinal))
            {
                thread.RemoteConversationId = remoteId;
                changed = true;
            }
            if (thread.ConversationId != conversationId)
            {
                thread.ConversationId = conversationId;
                changed = true;
            }
        }

        if (settings.HarnessSchemaVersion != CurrentSchemaVersion)
        {
            settings.HarnessSchemaVersion = CurrentSchemaVersion;
            changed = true;
        }

        return changed;
    }

    internal static string NormalizeHarnessId(string? harnessId) =>
        string.IsNullOrWhiteSpace(harnessId)
            ? KnownHarnessIds.Codex
            : harnessId.Trim().ToLowerInvariant();

    internal static string? NormalizeRemoteId(string? remoteId) =>
        string.IsNullOrWhiteSpace(remoteId) ? null : remoteId.Trim();

    internal static Guid ResolveConversationId(
        Guid conversationId,
        string? harnessId,
        string? remoteConversationId,
        string legacyThreadId)
    {
        if (conversationId != Guid.Empty)
        {
            return conversationId;
        }

        var stableRemoteId = NormalizeRemoteId(remoteConversationId) ?? NormalizeRemoteId(legacyThreadId);
        return stableRemoteId is null
            ? Guid.NewGuid()
            : CreateDeterministicConversationId(NormalizeHarnessId(harnessId), stableRemoteId);
    }

    public static Guid CreateDeterministicConversationId(string harnessId, string remoteConversationId)
    {
        if (string.IsNullOrWhiteSpace(remoteConversationId))
        {
            throw new ArgumentException("A remote conversation ID is required.", nameof(remoteConversationId));
        }

        var input = Encoding.UTF8.GetBytes(
            $"synthiacode-conversation\0{NormalizeHarnessId(harnessId)}\0{remoteConversationId.Trim()}");
        var hash = SHA256.HashData(input);
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }
}
