using NativeCodexAssistant.Core.Projects;
using NativeCodexAssistant.Core.Codex.AppServer;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NativeCodexAssistant.Core.Settings;

public sealed class AppSettings
{
    public string Theme { get; set; } = "System";

    public string? PreferredCodexPath { get; set; }

    public string? LastModelOverride { get; set; }

    public string? LastReasoningEffortOverride { get; set; }

    public bool IsProjectRailOpen { get; set; } = true;

    public bool IsDetailsPaneOpen { get; set; }

    public List<RecentProject> RecentProjects { get; set; } = [];

    public List<PersistedProjectThread> ProjectThreads { get; set; } = [];
}

// Storage-only DTO. Keep property names stable so Phase 3-5A settings.json files
// deserialize without a migration or schema rewrite.
public sealed class PersistedProjectThread
{
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
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ProjectThreadState : INotifyPropertyChanged
{
    private bool isArchived;
    private bool isRunning;
    private string mode = "local";
    private string? worktreeBranch;
    private string turnStatus = "Idle";

    public event PropertyChangedEventHandler? PropertyChanged;
    public string ProjectPath { get; set; } = string.Empty;

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
        }
    }

    public bool IsPinned { get; set; }

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

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title)
        ? string.IsNullOrWhiteSpace(Preview) ? ThreadId : Preview
        : Title;

    public string ActivityLabel => IsArchived ? "Archived" : TurnStatus;

    public string WorkspaceModeLabel => Mode.ToLowerInvariant() switch
    {
        "worktree" => string.IsNullOrWhiteSpace(WorktreeBranch) ? "Worktree" : $"Worktree · {WorktreeBranch}",
        "worktree-removed" => "Worktree removed",
        _ => "Current checkout"
    };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
