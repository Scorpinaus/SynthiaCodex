namespace SynthiaCode.Core.Harnesses;

[Flags]
public enum HarnessCapability
{
    None = 0,
    CreateConversation = 1 << 0,
    ResumeConversation = 1 << 1,
    ReadConversation = 1 << 2,
    RenameConversation = 1 << 3,
    ArchiveConversation = 1 << 4,
    ForkConversation = 1 << 5,
    RollbackConversation = 1 << 6,
    StartTurn = 1 << 7,
    CancelTurn = 1 << 8,
    SteerTurn = 1 << 9,
    Streaming = 1 << 10,
    ImageInput = 1 << 11,
    WorkspaceReferences = 1 << 12,
    ModelCatalog = 1 << 13,
    ModelOptions = 1 << 14,
    Approvals = 1 << 15,
    PermissionProfiles = 1 << 16,
    Skills = 1 << 17,
    Account = 1 << 18,
    RateLimits = 1 << 19,
    Configuration = 1 << 20,
    ContextUsage = 1 << 21,
    GeneratedImages = 1 << 22,
    AgentCollaboration = 1 << 23
}

public sealed record HarnessCapabilities(HarnessCapability Supported)
{
    public static HarnessCapabilities None { get; } = new(HarnessCapability.None);

    public bool Supports(HarnessCapability capability) =>
        capability != HarnessCapability.None && (Supported & capability) == capability;

    public HarnessCapabilities With(HarnessCapability capability) => new(Supported | capability);
}

public sealed record HarnessDescriptor(
    HarnessId Id,
    string DisplayName,
    string Description,
    HarnessCapabilities Capabilities);

public enum HarnessAvailabilityState
{
    Available,
    Unavailable,
    Misconfigured
}

public sealed record HarnessAvailability(
    HarnessAvailabilityState State,
    string Summary,
    string? Detail = null)
{
    public bool IsAvailable => State == HarnessAvailabilityState.Available;
}

public enum HarnessSessionState
{
    Idle,
    Connecting,
    Connected,
    Reconnecting,
    Unavailable,
    Disposed
}
