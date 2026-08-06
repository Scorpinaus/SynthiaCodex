namespace SynthiaCode.Core.Harnesses;

public static class KnownHarnessIds
{
    public const string Codex = "codex";
    public const string InMemory = "in-memory";
}

public readonly record struct HarnessId
{
    public HarnessId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A harness ID is required.", nameof(value));
        }

        Value = value.Trim().ToLowerInvariant();
    }

    public string Value { get; }

    public static HarnessId Codex { get; } = new(KnownHarnessIds.Codex);

    public static HarnessId InMemory { get; } = new(KnownHarnessIds.InMemory);

    public override string ToString() => Value;
}

public readonly record struct ConversationId
{
    public ConversationId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A conversation ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static ConversationId New() => new(Guid.NewGuid());

    public static bool TryParse(string? value, out ConversationId conversationId)
    {
        if (Guid.TryParse(value, out var parsed) && parsed != Guid.Empty)
        {
            conversationId = new ConversationId(parsed);
            return true;
        }

        conversationId = default;
        return false;
    }

    public override string ToString() => Value.ToString("D");
}

public sealed record ConversationAddress(
    ConversationId LocalId,
    HarnessId HarnessId,
    string? RemoteId)
{
    public ConversationAddress WithRemoteId(string remoteId)
    {
        if (string.IsNullOrWhiteSpace(remoteId))
        {
            throw new ArgumentException("A remote conversation ID is required.", nameof(remoteId));
        }

        return this with { RemoteId = remoteId.Trim() };
    }
}
