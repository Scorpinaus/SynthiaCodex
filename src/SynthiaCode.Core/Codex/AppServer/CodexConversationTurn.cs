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
    private string editedPrompt = string.Empty;

    public CodexConversationTurn()
    {
        Activity.CollectionChanged += OnActivityChanged;
        UserAttachments.CollectionChanged += OnUserAttachmentsChanged;
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
            }
        }
    }

    public bool HasAssistantResponse => !string.IsNullOrWhiteSpace(AssistantResponse);

    public string AssistantResponseDisplay => string.IsNullOrWhiteSpace(AssistantResponse)
        ? Status == CodexTurnStatus.Running ? "Working…" : "No assistant response"
        : AssistantResponse;

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
            }
        }
    }

    public DateTime? CompletedAtLocalTime => CompletedAt?.LocalDateTime;

    public ObservableCollection<CodexTimelineItem> Activity { get; } = [];

    public ObservableCollection<AttachmentReference> UserAttachments { get; } = [];

    public ObservableCollection<AttachmentReference> UserImages => UserAttachments;

    public bool HasUserAttachments => UserAttachments.Count > 0;

    public bool HasUserImages => HasUserAttachments;

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

    public string ActivitySummary => Activity.Count == 1 ? "1 activity item" : $"{Activity.Count} activity items";

    public CodexConversationTurnSnapshot ToSnapshot() => new()
    {
        TurnId = TurnId,
        UserPrompt = UserPrompt,
        AssistantResponse = AssistantResponse,
        Status = Status,
        StartedAt = StartedAt,
        CompletedAt = CompletedAt,
        IsSuperseded = IsSuperseded,
        Activity = [.. Activity],
        UserAttachments = [.. UserAttachments.Select(attachment => attachment.Clone())]
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
            IsSuperseded = snapshot.IsSuperseded
        };
        foreach (var item in snapshot.Activity)
        {
            turn.Activity.Add(UnicodeTextNormalizer.RepairLegacyMojibake(item));
        }
        foreach (var attachment in snapshot.UserAttachments)
        {
            turn.UserAttachments.Add(attachment.Clone());
        }

        return turn;
    }

    private void OnActivityChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasActivity));
        OnPropertyChanged(nameof(ActivitySummary));
    }

    private void OnUserAttachmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasUserAttachments));
        OnPropertyChanged(nameof(HasUserImages));
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
    public List<CodexTimelineItem> Activity { get; set; } = [];
    public List<AttachmentReference> UserAttachments { get; set; } = [];

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
