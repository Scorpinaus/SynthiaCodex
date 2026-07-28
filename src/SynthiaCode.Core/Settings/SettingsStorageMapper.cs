using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.Core.Settings;

// Keeps the storage DTO projections and deep-copy rules in one place. Settings
// saves run asynchronously, so every mutable collection must be copied here.
public static class SettingsStorageMapper
{
    public static AppSettings Clone(AppSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new AppSettings
        {
            Theme = source.Theme,
            PreferredCodexPath = source.PreferredCodexPath,
            LastModelOverride = source.LastModelOverride,
            LastReasoningEffortOverride = source.LastReasoningEffortOverride,
            LastServiceTierOverride = source.LastServiceTierOverride,
            FollowUpBehavior = source.FollowUpBehavior,
            CustomDeveloperInstructionsEnabled = source.CustomDeveloperInstructionsEnabled,
            CustomDeveloperInstructions = source.CustomDeveloperInstructions,
            CustomBaseInstructionsEnabled = source.CustomBaseInstructionsEnabled,
            CustomBaseInstructions = source.CustomBaseInstructions,
            SandboxModeOverride = source.SandboxModeOverride,
            ApprovalPolicyOverride = source.ApprovalPolicyOverride,
            PermissionMode = source.PermissionMode,
            CustomPermissionProfileId = source.CustomPermissionProfileId,
            ExecutionPolicySchemaVersion = source.ExecutionPolicySchemaVersion,
            AttachmentSchemaVersion = source.AttachmentSchemaVersion,
            IsProjectRailOpen = source.IsProjectRailOpen,
            IsDetailsPaneOpen = source.IsDetailsPaneOpen,
            RecentProjects = [.. source.RecentProjects],
            ProjectThreads = [.. source.ProjectThreads.Select(CloneThread)],
            ComposerAttachmentDrafts = [.. source.ComposerAttachmentDrafts.Select(CloneDraft)]
        };
    }

    public static PersistedProjectThread ToPersisted(ProjectThreadState source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var target = new PersistedProjectThread();
        Copy(source, target);
        return target;
    }

    public static void CopyToPersisted(ProjectThreadState source, PersistedProjectThread target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        Copy(source, target);
    }

    public static ProjectThreadState ToPresentation(PersistedProjectThread source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var target = new ProjectThreadState();
        Copy(source, target);
        return target;
    }

    public static PersistedProjectThread CloneThread(PersistedProjectThread source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var target = new PersistedProjectThread();
        Copy(source, target);
        return target;
    }

    private static void Copy(IThreadStorageState source, IThreadStorageState target)
    {
        target.ScopeKind = source.ScopeKind;
        target.ProjectPath = source.ProjectPath;
        target.ThreadId = source.ThreadId;
        target.Title = source.Title;
        target.IsTitlePlaceholder = source.IsTitlePlaceholder;
        target.Preview = source.Preview;
        target.IsArchived = source.IsArchived;
        target.IsPinned = source.IsPinned;
        target.IsActive = source.IsActive;
        target.IsRunning = source.IsRunning;
        target.TurnStatus = source.TurnStatus;
        target.Mode = source.Mode;
        target.WorkspacePath = source.WorkspacePath;
        target.WorktreeBranch = source.WorktreeBranch;
        target.AppliedDeveloperInstructions = source.AppliedDeveloperInstructions;
        target.AppliedBaseInstructions = source.AppliedBaseInstructions;
        target.CreatedAt = source.CreatedAt;
        target.FinalResponse = source.FinalResponse;
        target.TimelineItems = [.. source.TimelineItems];
        target.RawEvents = [.. source.RawEvents];
        target.ConversationTurns = [.. source.ConversationTurns.Select(CloneTurn)];
        target.QueuedFollowUps = [.. source.QueuedFollowUps.Select(CloneQueuedFollowUp)];
        target.ContextTokensUsed = source.ContextTokensUsed;
        target.ContextWindowTokens = source.ContextWindowTokens;
        target.ContextCompactionCount = source.ContextCompactionCount;
        target.UpdatedAt = source.UpdatedAt;
    }

    private static ComposerAttachmentDraftSnapshot CloneDraft(ComposerAttachmentDraftSnapshot source) => new()
    {
        ScopeKind = source.ScopeKind,
        ProjectPath = source.ProjectPath,
        ThreadId = source.ThreadId,
        Attachments = [.. source.Attachments.Select(CloneAttachment)],
        UpdatedAt = source.UpdatedAt
    };

    private static CodexConversationTurnSnapshot CloneTurn(CodexConversationTurnSnapshot source) => new()
    {
        TurnId = source.TurnId,
        UserPrompt = source.UserPrompt,
        AssistantResponse = source.AssistantResponse,
        Status = source.Status,
        StartedAt = source.StartedAt,
        CompletedAt = source.CompletedAt,
        IsSuperseded = source.IsSuperseded,
        Activity = [.. source.Activity],
        UserAttachments = [.. source.UserAttachments.Select(CloneAttachment)],
        GeneratedImagePaths = [.. source.GeneratedImagePaths]
    };

    private static QueuedFollowUpSnapshot CloneQueuedFollowUp(QueuedFollowUpSnapshot source) => new()
    {
        Id = source.Id,
        Text = source.Text,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
        State = source.State,
        LastError = source.LastError,
        Options = new QueuedTurnOptionsSnapshot
        {
            WorkspacePath = source.Options.WorkspacePath,
            Model = source.Options.Model,
            ReasoningEffort = source.Options.ReasoningEffort,
            ServiceTier = source.Options.ServiceTier,
            Sandbox = source.Options.Sandbox,
            ApprovalPolicy = source.Options.ApprovalPolicy,
            ApprovalsReviewer = source.Options.ApprovalsReviewer,
            PermissionProfileId = source.Options.PermissionProfileId
        },
        Attachments = [.. source.Attachments.Select(CloneAttachment)],
        SkillInputs = [.. source.SkillInputs]
    };

    private static AttachmentReference CloneAttachment(AttachmentReference source) => new()
    {
        Id = source.Id,
        Kind = source.Kind,
        SourceKind = source.SourceKind,
        StorageKey = source.StorageKey,
        WorkspaceRelativePath = source.WorkspaceRelativePath,
        DisplayName = source.DisplayName,
        MediaType = source.MediaType,
        ByteLength = source.ByteLength,
        PixelWidth = source.PixelWidth,
        PixelHeight = source.PixelHeight,
        SnapshotFileCount = source.SnapshotFileCount,
        SnapshotByteLength = source.SnapshotByteLength,
        ContentSha256 = source.ContentSha256,
        ManagedPath = source.ManagedPath
    };
}
