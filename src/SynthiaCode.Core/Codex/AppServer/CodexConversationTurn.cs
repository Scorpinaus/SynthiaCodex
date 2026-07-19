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

    public CodexConversationTurn()
    {
        Activity.CollectionChanged += OnActivityChanged;
        UserAttachments.CollectionChanged += OnUserAttachmentsChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string TurnId
    {
        get => turnId;
        set => SetProperty(ref turnId, value ?? string.Empty);
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
            }
        }
    }

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
            }
        }
    }

    public string StatusLabel => Status.ToString();

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt
    {
        get => completedAt;
        set => SetProperty(ref completedAt, value);
    }

    public ObservableCollection<CodexTimelineItem> Activity { get; } = [];

    public ObservableCollection<AttachmentReference> UserAttachments { get; } = [];

    public ObservableCollection<AttachmentReference> UserImages => UserAttachments;

    public bool HasUserAttachments => UserAttachments.Count > 0;

    public bool HasUserImages => HasUserAttachments;

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
            CompletedAt = snapshot.CompletedAt
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
