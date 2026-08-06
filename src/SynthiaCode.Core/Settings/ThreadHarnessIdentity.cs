using SynthiaCode.Core.Harnesses;

namespace SynthiaCode.Core.Settings;

public static class ThreadHarnessIdentity
{
    public static ConversationAddress GetConversationAddress(this ProjectThreadState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return CreateAddress(
            state.ConversationId,
            state.HarnessId,
            state.RemoteConversationId,
            state.ThreadId);
    }

    public static ConversationAddress GetConversationAddress(this PersistedProjectThread state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return CreateAddress(
            state.ConversationId,
            state.HarnessId,
            state.RemoteConversationId,
            state.ThreadId);
    }

    public static void ApplyConversationAddress(
        this ProjectThreadState state,
        ConversationAddress address,
        bool useLocalIdAsThreadId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(address);
        state.ConversationId = address.LocalId.Value;
        state.HarnessId = address.HarnessId.Value;
        state.RemoteConversationId = address.RemoteId;
        if (useLocalIdAsThreadId)
        {
            state.ThreadId = address.LocalId.ToString();
        }
    }

    private static ConversationAddress CreateAddress(
        Guid conversationId,
        string? harnessId,
        string? remoteConversationId,
        string legacyThreadId)
    {
        var normalizedHarnessId = AppSettingsHarnessMigration.NormalizeHarnessId(harnessId);
        var normalizedRemoteId = AppSettingsHarnessMigration.NormalizeRemoteId(remoteConversationId)
            ?? AppSettingsHarnessMigration.NormalizeRemoteId(legacyThreadId);
        var normalizedConversationId = AppSettingsHarnessMigration.ResolveConversationId(
            conversationId,
            normalizedHarnessId,
            normalizedRemoteId,
            legacyThreadId);
        return new ConversationAddress(
            new ConversationId(normalizedConversationId),
            new HarnessId(normalizedHarnessId),
            normalizedRemoteId);
    }
}
