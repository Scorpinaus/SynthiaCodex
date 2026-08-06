using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using SynthiaCode.Core.Attachments;

namespace SynthiaCode.Core.Codex.AppServer;

public sealed class CodexConversationTurn : INotifyPropertyChanged
{
    private string turnId = string.Empty;
    private string userPrompt = string.Empty;
    private string assistantResponse = string.Empty;
    private CodexTurnStatus status = CodexTurnStatus.Idle;
    private DateTimeOffset? completedAt;
    private bool isActivityExpanded;
    private bool isSuperseded;
    private bool isPromptEditing;
    private bool isFindMatch;
    private bool isCurrentFindMatch;
    private bool isCodeReview;
    private string reviewScope = string.Empty;
    private string editedPrompt = string.Empty;

    public CodexConversationTurn()
    {
        Activity.CollectionChanged += OnActivityChanged;
        UserAttachments.CollectionChanged += OnUserAttachmentsChanged;
        GeneratedImagePaths.CollectionChanged += OnGeneratedImagePathsChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string TurnId
    {
        get => turnId;
        set
        {
            if (SetProperty(ref turnId, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanEditPrompt));
            }
        }
    }

    public string UserPrompt
    {
        get => userPrompt;
        set => SetProperty(ref userPrompt, value ?? string.Empty);
    }

    public string AssistantResponse
    {
        get => assistantResponse;
        set
        {
            if (SetProperty(ref assistantResponse, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(AssistantResponseDisplay));
                OnPropertyChanged(nameof(HasAssistantResponse));
                OnPropertyChanged(nameof(HasAssistantContent));
                OnPropertyChanged(nameof(ShowsCommentaryChannel));
                OnPropertyChanged(nameof(ShowsAssistantChannel));
            }
        }
    }

    public bool HasAssistantResponse => !string.IsNullOrWhiteSpace(AssistantResponse);

    public bool HasGeneratedImages => GeneratedImagePaths.Count > 0;

    public bool HasAssistantContent => HasAssistantResponse || HasGeneratedImages;

    public string AssistantResponseDisplay
    {
        get
        {
            var parts = GeneratedImagePaths
                .Select(CreateGeneratedImageMarkdown)
                .Where(markdown => !string.IsNullOrWhiteSpace(markdown))
                .ToList();
            if (HasAssistantResponse)
            {
                parts.Add(AssistantResponse);
            }

            return parts.Count > 0
                ? string.Join($"{Environment.NewLine}{Environment.NewLine}", parts)
                : Status == CodexTurnStatus.Running ? "Working…" : "No assistant response";
        }
    }

    public CodexTurnStatus Status
    {
        get => status;
        set
        {
            if (SetProperty(ref status, value))
            {
                IsActivityExpanded = value == CodexTurnStatus.Running;
                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(AssistantResponseDisplay));
                OnPropertyChanged(nameof(WorkSummary));
                OnPropertyChanged(nameof(CanEditPrompt));
            }
        }
    }

    public string StatusLabel => Status.ToString();

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTime StartedAtLocalTime => StartedAt.LocalDateTime;

    public DateTimeOffset? CompletedAt
    {
        get => completedAt;
        set
        {
            if (SetProperty(ref completedAt, value))
            {
                OnPropertyChanged(nameof(CompletedAtLocalTime));
                OnPropertyChanged(nameof(WorkSummary));
            }
        }
    }

    public DateTime? CompletedAtLocalTime => CompletedAt?.LocalDateTime;

    public ObservableCollection<CodexTimelineItem> Activity { get; } = [];

    public ObservableCollection<AttachmentReference> UserAttachments { get; } = [];

    public ObservableCollection<AttachmentReference> UserImages => UserAttachments;

    public ObservableCollection<string> GeneratedImagePaths { get; } = [];

    public bool HasUserAttachments => UserAttachments.Count > 0;

    public bool HasUserImages => HasUserAttachments;

    public bool IsCodeReview
    {
        get => isCodeReview;
        set
        {
            if (SetProperty(ref isCodeReview, value))
            {
                OnPropertyChanged(nameof(CanEditPrompt));
                OnPropertyChanged(nameof(ReviewBadgeLabel));
            }
        }
    }

    public string ReviewScope
    {
        get => reviewScope;
        set
        {
            if (SetProperty(ref reviewScope, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasReviewScope));
                OnPropertyChanged(nameof(ReviewScopeDisplay));
            }
        }
    }

    [JsonIgnore]
    public bool HasReviewScope => !string.IsNullOrWhiteSpace(ReviewScope);

    [JsonIgnore]
    public string ReviewBadgeLabel => "Code review";

    [JsonIgnore]
    public string ReviewScopeDisplay => HasReviewScope ? $"Scope: {ReviewScope}" : string.Empty;

    public bool IsSuperseded
    {
        get => isSuperseded;
        set
        {
            if (SetProperty(ref isSuperseded, value))
            {
                if (value)
                {
                    CancelPromptEdit();
                }
                OnPropertyChanged(nameof(CanEditPrompt));
            }
        }
    }

    [JsonIgnore]
    public bool IsFindMatch
    {
        get => isFindMatch;
        set => SetProperty(ref isFindMatch, value);
    }

    [JsonIgnore]
    public bool IsCurrentFindMatch
    {
        get => isCurrentFindMatch;
        set => SetProperty(ref isCurrentFindMatch, value);
    }

    [JsonIgnore]
    public bool IsPromptEditing
    {
        get => isPromptEditing;
        private set
        {
            if (SetProperty(ref isPromptEditing, value))
            {
                OnPropertyChanged(nameof(CanEditPrompt));
                OnPropertyChanged(nameof(CanSubmitPromptEdit));
            }
        }
    }

    [JsonIgnore]
    public string EditedPrompt
    {
        get => editedPrompt;
        set
        {
            if (SetProperty(ref editedPrompt, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanSubmitPromptEdit));
            }
        }
    }

    [JsonIgnore]
    public bool CanEditPrompt =>
        !IsCodeReview &&
        !IsSuperseded &&
        !IsPromptEditing &&
        Status != CodexTurnStatus.Running &&
        !string.IsNullOrWhiteSpace(TurnId);

    [JsonIgnore]
    public bool CanSubmitPromptEdit =>
        IsPromptEditing &&
        !string.IsNullOrWhiteSpace(EditedPrompt) &&
        !string.Equals(EditedPrompt.Trim(), UserPrompt, StringComparison.Ordinal);

    public void BeginPromptEdit()
    {
        if (!CanEditPrompt)
        {
            return;
        }

        EditedPrompt = UserPrompt;
        IsPromptEditing = true;
    }

    public void CancelPromptEdit()
    {
        EditedPrompt = UserPrompt;
        IsPromptEditing = false;
    }

    public bool IsActivityExpanded
    {
        get => isActivityExpanded;
        set => SetProperty(ref isActivityExpanded, value);
    }

    public bool HasActivity => Activity.Count > 0;

    public bool ShowsCommentaryChannel => HasActivity;

    public bool ShowsAssistantChannel => !HasActivity || HasAssistantContent;

    public string ActivitySummary => Activity.Count == 1 ? "1 activity item" : $"{Activity.Count} activity items";

    public string WorkSummary
    {
        get
        {
            if (Status == CodexTurnStatus.Running)
            {
                return "Working\u2026";
            }

            return CompletedAt is { } completed
                ? $"Worked for {FormatWorkDuration(completed - StartedAt)}"
                : "Work details";
        }
    }

    public CodexConversationTurnSnapshot ToSnapshot() => new()
    {
        TurnId = TurnId,
        UserPrompt = UserPrompt,
        AssistantResponse = AssistantResponse,
        Status = Status,
        StartedAt = StartedAt,
        CompletedAt = CompletedAt,
        IsSuperseded = IsSuperseded,
        IsCodeReview = IsCodeReview,
        ReviewScope = ReviewScope,
        Activity = [.. Activity],
        UserAttachments = [.. UserAttachments.Select(attachment => attachment.Clone())],
        GeneratedImagePaths = [.. GeneratedImagePaths]
    };

    public static CodexConversationTurn FromSnapshot(CodexConversationTurnSnapshot snapshot)
    {
        var turn = new CodexConversationTurn
        {
            TurnId = snapshot.TurnId,
            UserPrompt = snapshot.UserPrompt,
            AssistantResponse = UnicodeTextNormalizer.RepairLegacyMojibake(snapshot.AssistantResponse),
            Status = snapshot.Status,
            StartedAt = snapshot.StartedAt,
            CompletedAt = snapshot.CompletedAt,
            IsSuperseded = snapshot.IsSuperseded,
            IsCodeReview = snapshot.IsCodeReview,
            ReviewScope = UnicodeTextNormalizer.RepairLegacyMojibake(snapshot.ReviewScope)
        };
        foreach (var item in snapshot.Activity)
        {
            turn.Activity.Add(UnicodeTextNormalizer.RepairLegacyMojibake(item));
        }
        foreach (var attachment in snapshot.UserAttachments)
        {
            turn.UserAttachments.Add(attachment.Clone());
        }
        foreach (var path in snapshot.GeneratedImagePaths)
        {
            turn.GeneratedImagePaths.Add(path);
        }

        return turn;
    }

    private void OnActivityChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasActivity));
        OnPropertyChanged(nameof(ShowsCommentaryChannel));
        OnPropertyChanged(nameof(ShowsAssistantChannel));
        OnPropertyChanged(nameof(ActivitySummary));
    }

    private void OnUserAttachmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasUserAttachments));
        OnPropertyChanged(nameof(HasUserImages));
    }

    private void OnGeneratedImagePathsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasGeneratedImages));
        OnPropertyChanged(nameof(HasAssistantContent));
        OnPropertyChanged(nameof(AssistantResponseDisplay));
        OnPropertyChanged(nameof(ShowsAssistantChannel));
    }

    private static string? CreateGeneratedImageMarkdown(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var candidate = Uri.TryCreate(path, UriKind.Absolute, out var parsed) && parsed.IsFile
                ? parsed.LocalPath
                : path;
            if (!Path.IsPathFullyQualified(candidate) ||
                candidate.StartsWith(@"\\", StringComparison.Ordinal) ||
                Path.GetExtension(candidate).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".gif"))
            {
                return null;
            }

            var target = new Uri(Path.GetFullPath(candidate), UriKind.Absolute)
                .AbsoluteUri
                .Replace("(", "%28", StringComparison.Ordinal)
                .Replace(")", "%29", StringComparison.Ordinal);
            return $"[Generated image]({target})";
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException or
            UriFormatException)
        {
            return null;
        }
    }

    private static string FormatWorkDuration(TimeSpan duration)
    {
        var totalSeconds = Math.Max(0, (long)Math.Floor(duration.TotalSeconds));
        if (totalSeconds == 0)
        {
            return "<1s";
        }

        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;
        return hours > 0
            ? $"{hours}h {minutes}m {seconds}s"
            : minutes > 0
                ? $"{minutes}m {seconds}s"
                : $"{seconds}s";
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class CodexConversationTurnSnapshot
{
    public string TurnId { get; set; } = string.Empty;
    public string UserPrompt { get; set; } = string.Empty;
    public string AssistantResponse { get; set; } = string.Empty;
    public CodexTurnStatus Status { get; set; } = CodexTurnStatus.Idle;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public bool IsSuperseded { get; set; }
    public bool IsCodeReview { get; set; }
    public string ReviewScope { get; set; } = string.Empty;
    public List<CodexTimelineItem> Activity { get; set; } = [];
    public List<AttachmentReference> UserAttachments { get; set; } = [];
    public List<string> GeneratedImagePaths { get; set; } = [];

    [JsonIgnore]
    public List<AttachmentReference> UserImages
    {
        get => UserAttachments;
        set => UserAttachments = value ?? [];
    }

    [JsonPropertyName("UserImages")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AttachmentReference>? LegacyUserImages
    {
        get => null;
        set
        {
            if (UserAttachments.Count == 0 && value is not null)
            {
                UserAttachments = value;
            }
        }
    }
}
