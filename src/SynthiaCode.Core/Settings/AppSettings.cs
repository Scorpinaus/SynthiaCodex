using SynthiaCode.Core.Projects;
using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Codex.AppServer;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace SynthiaCode.Core.Settings;

public sealed class AppSettings
{
    public string Theme { get; set; } = "System";

    public string? PreferredCodexPath { get; set; }

    public string? LastModelOverride { get; set; }

    public string? LastReasoningEffortOverride { get; set; }

    public string? LastServiceTierOverride { get; set; }

    public string? FollowUpBehavior { get; set; }

    public string? SandboxModeOverride { get; set; } = "workspace-write";

    public string? ApprovalPolicyOverride { get; set; } = "on-request";

    public string? PermissionMode { get; set; }

    public string? CustomPermissionProfileId { get; set; }

    public int ExecutionPolicySchemaVersion { get; set; }

    public int AttachmentSchemaVersion { get; set; } = 3;

    public bool IsProjectRailOpen { get; set; } = true;

    public bool IsDetailsPaneOpen { get; set; }

    public List<RecentProject> RecentProjects { get; set; } = [];

    public List<PersistedProjectThread> ProjectThreads { get; set; } = [];

    public List<ComposerAttachmentDraftSnapshot> ComposerAttachmentDrafts { get; set; } = [];
}

public sealed class ComposerAttachmentDraftSnapshot
{
    public ThreadScopeKind ScopeKind { get; set; } = ThreadScopeKind.Project;
    public string ProjectPath { get; set; } = string.Empty;
    public string? ThreadId { get; set; }
    public List<AttachmentReference> Attachments { get; set; } = [];

    [JsonIgnore]
    public List<AttachmentReference> Images
    {
        get => Attachments;
        set => Attachments = value ?? [];
    }

    [JsonPropertyName("Images")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AttachmentReference>? LegacyImages
    {
        get => null;
        set
        {
            if (Attachments.Count == 0 && value is not null)
            {
                Attachments = value;
            }
        }
    }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ComposerAttachmentDraftSnapshot Clone() => new()
    {
        ScopeKind = ScopeKind,
        ProjectPath = ProjectPath,
        ThreadId = ThreadId,
        Attachments = [.. Attachments.Select(attachment => attachment.Clone())],
        UpdatedAt = UpdatedAt
    };
}

// Storage-only DTO. Keep property names stable so Phase 3-5A settings.json files
// deserialize without a migration or schema rewrite.
public sealed class PersistedProjectThread
{
    public ThreadScopeKind ScopeKind { get; set; } = ThreadScopeKind.Project;
    public string ProjectPath { get; set; } = string.Empty;
    public string ThreadId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Preview { get; set; } = string.Empty;
    public bool IsArchived { get; set; }
    public bool IsPinned { get; set; }
    public bool IsActive { get; set; }
    public bool IsRunning { get; set; }
    public string TurnStatus { get; set; } = "Idle";
    public string Mode { get; set; } = "local";
    public string? WorkspacePath { get; set; }
    public string? WorktreeBranch { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string FinalResponse { get; set; } = string.Empty;
    public List<CodexTimelineItem> TimelineItems { get; set; } = [];
    public List<string> RawEvents { get; set; } = [];
    public List<CodexConversationTurnSnapshot> ConversationTurns { get; set; } = [];
    public List<QueuedFollowUpSnapshot> QueuedFollowUps { get; set; } = [];
    public long ContextTokensUsed { get; set; }
    public long ContextWindowTokens { get; set; }
    public int ContextCompactionCount { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ProjectThreadState : INotifyPropertyChanged
{
    private bool isArchived;
    private bool isPinned;
    private bool isRunning;
    private string mode = "local";
    private string? worktreeBranch;
    private string turnStatus = "Idle";

    public event PropertyChangedEventHandler? PropertyChanged;
    public ThreadScopeKind ScopeKind { get; set; } = ThreadScopeKind.Project;
    public string ProjectPath { get; set; } = string.Empty;

    [JsonIgnore]
    public ThreadScopeKey ScopeKey => ThreadScopeKey.From(ScopeKind, ProjectPath);

    [JsonIgnore]
    public bool IsGeneral => ScopeKind == ThreadScopeKind.General;

    public string ThreadId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Preview { get; set; } = string.Empty;

    public bool IsArchived
    {
        get => isArchived;
        set
        {
            if (isArchived == value)
            {
                return;
            }

            isArchived = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActivityLabel));
            OnPropertyChanged(nameof(HasActionableStatus));
        }
    }

    public bool IsPinned
    {
        get => isPinned;
        set
        {
            if (isPinned == value)
            {
                return;
            }

            isPinned = value;
            OnPropertyChanged();
        }
    }

    public bool IsActive { get; set; }

    public bool IsRunning
    {
        get => isRunning;
        set
        {
            if (isRunning == value)
            {
                return;
            }

            isRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActivityLabel));
            OnPropertyChanged(nameof(HasActionableStatus));
        }
    }

    public string TurnStatus
    {
        get => turnStatus;
        set
        {
            if (string.Equals(turnStatus, value, StringComparison.Ordinal))
            {
                return;
            }

            turnStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActivityLabel));
            OnPropertyChanged(nameof(HasActionableStatus));
        }
    }

    public string Mode
    {
        get => mode;
        set
        {
            if (string.Equals(mode, value, StringComparison.Ordinal))
            {
                return;
            }

            mode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WorkspaceModeLabel));
        }
    }

    public string? WorkspacePath { get; set; }

    public string? WorktreeBranch
    {
        get => worktreeBranch;
        set
        {
            if (string.Equals(worktreeBranch, value, StringComparison.Ordinal))
            {
                return;
            }

            worktreeBranch = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WorkspaceModeLabel));
        }
    }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string FinalResponse { get; set; } = string.Empty;

    public List<CodexTimelineItem> TimelineItems { get; set; } = [];

    public List<string> RawEvents { get; set; } = [];

    public List<CodexConversationTurnSnapshot> ConversationTurns { get; set; } = [];

    public List<QueuedFollowUpSnapshot> QueuedFollowUps { get; set; } = [];

    public long ContextTokensUsed { get; set; }

    public long ContextWindowTokens { get; set; }

    public int ContextCompactionCount { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title)
        ? string.IsNullOrWhiteSpace(Preview) ? ThreadId : Preview
        : Title;

    public string ActivityLabel => IsArchived ? "Archived" : TurnStatus;

    public bool HasActionableStatus =>
        IsArchived ||
        IsRunning ||
        TurnStatus is "Failed" or "Cancelled" or "Canceled";

    public string WorkspaceModeLabel => Mode.ToLowerInvariant() switch
    {
        "general" => "General workspace",
        "worktree" => string.IsNullOrWhiteSpace(WorktreeBranch) ? "Worktree" : $"Worktree · {WorktreeBranch}",
        "worktree-removed" => "Worktree removed",
        _ => "Current checkout"
    };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
