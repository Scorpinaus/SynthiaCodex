using SynthiaCode.Core.Attachments;

namespace SynthiaCode.Core.Harnesses;

public enum WorkspaceAccessMode
{
    ReadOnly,
    WorkspaceWrite,
    Unrestricted
}

public enum ApprovalInteractionMode
{
    Prompt,
    AutomaticReview,
    NeverPrompt
}

public sealed record HarnessExecutionPolicy(
    WorkspaceAccessMode WorkspaceAccess,
    ApprovalInteractionMode ApprovalMode,
    string? ProfileId = null);

public sealed record HarnessTurnOptions(
    string? ModelId = null,
    string? ReasoningEffortId = null,
    string? ServiceTierId = null,
    HarnessExecutionPolicy? ExecutionPolicy = null)
{
    public static HarnessTurnOptions Default { get; } = new();
}

public abstract record HarnessContentPart;

public sealed record TextContentPart(string Text) : HarnessContentPart;

public sealed record DataImageContentPart(string DataUrl) : HarnessContentPart;

public sealed record LocalImageContentPart(string Path) : HarnessContentPart;

public sealed record WorkspaceReferenceContentPart(string Name, string Path) : HarnessContentPart;

public sealed record SkillReferenceContentPart(string Name, string Path) : HarnessContentPart;

public sealed record StartConversationCommand(
    ConversationId LocalConversationId,
    string? WorkspacePath,
    HarnessTurnOptions Options,
    string? DeveloperInstructions = null,
    string? BaseInstructions = null);

public sealed record StartConversationResult(ConversationAddress Address);

public sealed record ResumeConversationCommand(
    ConversationAddress Address,
    string? WorkspacePath,
    HarnessTurnOptions Options,
    string? DeveloperInstructions = null,
    string? BaseInstructions = null);

public sealed record ResumeConversationResult(
    ConversationAddress Address,
    IReadOnlyList<ConversationTurnSnapshot> Turns);

public sealed record ReadConversationCommand(
    ConversationAddress Address,
    bool IncludeTurns = true);

public sealed record ReadConversationResult(
    ConversationAddress Address,
    IReadOnlyList<ConversationTurnSnapshot> Turns);

public sealed record ForkConversationCommand(
    ConversationId LocalConversationId,
    ConversationAddress Source,
    string? WorkspacePath,
    HarnessTurnOptions Options,
    string? DeveloperInstructions = null,
    string? BaseInstructions = null);

public sealed record ForkConversationResult(ConversationAddress Address);

public sealed record RollbackConversationCommand(
    ConversationAddress Address,
    int TurnCount);

public sealed record RollbackConversationResult(
    ConversationAddress Address,
    IReadOnlyList<ConversationTurnSnapshot> Turns);

public sealed record StartTurnCommand(
    ConversationAddress Address,
    IReadOnlyList<HarnessContentPart> Inputs,
    string? WorkspacePath,
    HarnessTurnOptions Options)
{
    public string Prompt => string.Join(
        Environment.NewLine,
        Inputs.OfType<TextContentPart>().Select(input => input.Text));
}

public sealed record StartTurnResult(string RemoteTurnId);

public sealed record SteerTurnCommand(
    ConversationAddress Address,
    string ExpectedRemoteTurnId,
    IReadOnlyList<HarnessContentPart> Inputs)
{
    public string Prompt => string.Join(
        Environment.NewLine,
        Inputs.OfType<TextContentPart>().Select(input => input.Text));
}

public sealed record SteerTurnResult(string RemoteTurnId);

public enum ConversationTurnStatus
{
    Idle,
    Running,
    Completed,
    Failed,
    Cancelled
}

public enum ActivityKind
{
    Information,
    Plan,
    Reasoning,
    Command,
    FileChange,
    Tool,
    WebSearch,
    ImageGeneration,
    ContextCompaction,
    Collaboration,
    Error
}

public sealed record ActivityItem(
    string Id,
    ActivityKind Kind,
    string Title,
    string Detail,
    DateTimeOffset Timestamp,
    bool IsCompleted = false,
    bool IsError = false);

public sealed record ConversationTurnSnapshot(
    string? RemoteTurnId,
    string UserPrompt,
    string AssistantResponse,
    ConversationTurnStatus Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    bool IsSuperseded,
    IReadOnlyList<ActivityItem> Activity,
    IReadOnlyList<AttachmentReference> UserAttachments,
    IReadOnlyList<string> GeneratedImagePaths);

public enum HarnessInputModality
{
    Text,
    Image,
    WorkspaceReference
}

public sealed record HarnessOptionChoice(
    string Id,
    string DisplayName,
    string Description,
    bool IsDefault = false);

public sealed record HarnessOptionDescriptor(
    string Id,
    string DisplayName,
    string Description,
    IReadOnlyList<HarnessOptionChoice> Choices);

public sealed record HarnessModelDescriptor(
    string Id,
    string DisplayName,
    string Description,
    bool IsDefault,
    bool IsHidden,
    IReadOnlyList<HarnessInputModality> InputModalities,
    IReadOnlyList<HarnessOptionDescriptor> Options,
    string? AvailabilityMessage = null)
{
    public bool Supports(HarnessInputModality modality) => InputModalities.Contains(modality);
}
