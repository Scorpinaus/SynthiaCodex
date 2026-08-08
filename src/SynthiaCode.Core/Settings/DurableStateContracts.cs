using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Harnesses;
using SynthiaCode.Core.Projects;

namespace SynthiaCode.Core.Settings;

public static class DurableStateSchema
{
    public const int Release010 = 0;
    public const int Current = 1;
}

public interface ILegacySettingsRepository : ISettingsStore
{
    string BackupPath { get; }

    bool Exists { get; }

    bool BackupExists { get; }

    Task CreateBackupAsync(CancellationToken cancellationToken = default);

    Task<AppSettings> LoadBackupAsync(CancellationToken cancellationToken = default);
}

public interface IPreferencesRepository
{
    Task<DurablePreferencesDocument?> LoadAsync(long generation, CancellationToken cancellationToken = default);

    Task SaveAsync(DurablePreferencesDocument document, CancellationToken cancellationToken = default);
}

public interface IProjectThreadCatalogRepository
{
    Task<ProjectThreadCatalogDocument?> LoadAsync(long generation, CancellationToken cancellationToken = default);

    Task SaveAsync(ProjectThreadCatalogDocument document, CancellationToken cancellationToken = default);
}

public interface IConversationRepository
{
    Task<IReadOnlyList<ConversationDocument>> LoadAsync(
        IEnumerable<string> threadIds,
        long generation,
        CancellationToken cancellationToken = default);

    Task SaveAsync(IEnumerable<ConversationDocument> documents, CancellationToken cancellationToken = default);
}

public interface IQueueStateRepository
{
    Task<QueueStateDocument?> LoadAsync(long generation, CancellationToken cancellationToken = default);

    Task SaveAsync(QueueStateDocument document, CancellationToken cancellationToken = default);
}

public interface IDraftStateRepository
{
    Task<DraftStateDocument?> LoadAsync(long generation, CancellationToken cancellationToken = default);

    Task SaveAsync(DraftStateDocument document, CancellationToken cancellationToken = default);
}

public interface IDurableStateManifestRepository
{
    Task<DurableStateManifest?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(DurableStateManifest manifest, CancellationToken cancellationToken = default);
}

public interface IDurableStateMigration
{
    int FromVersion { get; }

    int ToVersion { get; }

    void Apply(AppSettings settings);
}

public interface IDurableStateMigrator
{
    AppSettings Migrate(AppSettings settings, int fromVersion, int toVersion);
}

public sealed class DurableStateManifest
{
    public int SchemaVersion { get; set; } = DurableStateSchema.Current;

    public long Generation { get; set; }

    public string? ImportedFromRelease { get; set; }

    public DateTimeOffset? ImportedAtUtc { get; set; }
}

public sealed class DurablePreferencesDocument
{
    public int SchemaVersion { get; set; } = DurableStateSchema.Current;
    public long Generation { get; set; }
    public string Theme { get; set; } = "System";
    public string? PreferredCodexPath { get; set; }
    public string? LastModelOverride { get; set; }
    public string? LastReasoningEffortOverride { get; set; }
    public string? LastServiceTierOverride { get; set; }
    public string? FollowUpBehavior { get; set; }
    public bool CustomDeveloperInstructionsEnabled { get; set; }
    public string CustomDeveloperInstructions { get; set; } = string.Empty;
    public bool CustomBaseInstructionsEnabled { get; set; }
    public string CustomBaseInstructions { get; set; } = string.Empty;
    public string? SandboxModeOverride { get; set; } = "workspace-write";
    public string? ApprovalPolicyOverride { get; set; } = "on-request";
    public string? PermissionMode { get; set; }
    public string? CustomPermissionProfileId { get; set; }
    public int ExecutionPolicySchemaVersion { get; set; }
    public int AttachmentSchemaVersion { get; set; } = 3;
    public int HarnessSchemaVersion { get; set; }
    public string DefaultHarnessId { get; set; } = KnownHarnessIds.Codex;
    public bool IsProjectRailOpen { get; set; } = true;
    public bool IsDetailsPaneOpen { get; set; }
    public string? LastSelectedProjectPath { get; set; }
}

public sealed class ProjectThreadCatalogDocument
{
    public int SchemaVersion { get; set; } = DurableStateSchema.Current;
    public long Generation { get; set; }
    public List<RecentProject> Projects { get; set; } = [];
    public List<ProjectThreadCatalogEntry> Threads { get; set; } = [];
}

public sealed class ProjectThreadCatalogEntry
{
    public ThreadScopeKind ScopeKind { get; set; } = ThreadScopeKind.Project;
    public string ProjectPath { get; set; } = string.Empty;
    public string ThreadId { get; set; } = string.Empty;
    public Guid ConversationId { get; set; }
    public string HarnessId { get; set; } = KnownHarnessIds.Codex;
    public string? RemoteConversationId { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool IsTitlePlaceholder { get; set; }
    public string Preview { get; set; } = string.Empty;
    public bool IsArchived { get; set; }
    public bool IsPinned { get; set; }
    public bool IsActive { get; set; }
    public bool IsRunning { get; set; }
    public string TurnStatus { get; set; } = "Idle";
    public string Mode { get; set; } = "local";
    public string? WorkspacePath { get; set; }
    public string? WorktreeBranch { get; set; }
    public string? AppliedDeveloperInstructions { get; set; }
    public string? AppliedBaseInstructions { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ConversationDocument
{
    public int SchemaVersion { get; set; } = DurableStateSchema.Current;
    public long Generation { get; set; }
    public string ThreadId { get; set; } = string.Empty;
    public string FinalResponse { get; set; } = string.Empty;
    public List<CodexTimelineItem> TimelineItems { get; set; } = [];
    public List<string> RawEvents { get; set; } = [];
    public List<CodexConversationTurnSnapshot> ConversationTurns { get; set; } = [];
    public long ContextTokensUsed { get; set; }
    public long ContextWindowTokens { get; set; }
    public int ContextCompactionCount { get; set; }
}

public sealed class QueueStateDocument
{
    public int SchemaVersion { get; set; } = DurableStateSchema.Current;
    public long Generation { get; set; }
    public List<ThreadQueueState> Threads { get; set; } = [];
}

public sealed class ThreadQueueState
{
    public string ThreadId { get; set; } = string.Empty;
    public List<QueuedFollowUpSnapshot> QueuedFollowUps { get; set; } = [];
}

public sealed class DraftStateDocument
{
    public int SchemaVersion { get; set; } = DurableStateSchema.Current;
    public long Generation { get; set; }
    public List<ComposerAttachmentDraftSnapshot> Drafts { get; set; } = [];
}
