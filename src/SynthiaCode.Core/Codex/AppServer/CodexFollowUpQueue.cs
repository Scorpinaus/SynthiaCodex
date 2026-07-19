using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using SynthiaCode.Core.Attachments;

namespace SynthiaCode.Core.Codex.AppServer;

public enum FollowUpBehavior
{
    Queue,
    Steer
}

public static class FollowUpBehaviorExtensions
{
    public static string ToSettingsValue(this FollowUpBehavior behavior) => behavior switch
    {
        FollowUpBehavior.Queue => "queue",
        FollowUpBehavior.Steer => "steer",
        _ => throw new ArgumentOutOfRangeException(nameof(behavior), behavior, "Unknown follow-up behavior.")
    };

    public static FollowUpBehavior ParseFollowUpBehavior(this string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "steer" or "interrupt" => FollowUpBehavior.Steer,
        _ => FollowUpBehavior.Queue
    };
}

public enum QueuedFollowUpState
{
    Pending,
    Starting,
    NeedsAttention
}

public sealed class QueuedTurnOptionsSnapshot
{
    public string WorkspacePath { get; set; } = string.Empty;
    public string? Model { get; set; }
    public CodexReasoningEffort? ReasoningEffort { get; set; }
    public CodexServiceTierSelection ServiceTier { get; set; }
    public CodexSandbox? Sandbox { get; set; }
    public CodexApprovalPolicy? ApprovalPolicy { get; set; }
    public CodexApprovalsReviewer? ApprovalsReviewer { get; set; }
    public string? PermissionProfileId { get; set; }

    public QueuedTurnOptionsSnapshot Clone() => new()
    {
        WorkspacePath = WorkspacePath,
        Model = Model,
        ReasoningEffort = ReasoningEffort,
        ServiceTier = ServiceTier,
        Sandbox = Sandbox,
        ApprovalPolicy = ApprovalPolicy,
        ApprovalsReviewer = ApprovalsReviewer,
        PermissionProfileId = PermissionProfileId
    };
}

public sealed class QueuedFollowUpSnapshot
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public QueuedFollowUpState State { get; set; }
    public string? LastError { get; set; }
    public QueuedTurnOptionsSnapshot Options { get; set; } = new();
    public List<AttachmentReference> Images { get; set; } = [];

    public QueuedFollowUpSnapshot Clone() => new()
    {
        Id = Id,
        Text = Text,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        State = State,
        LastError = LastError,
        Options = Options.Clone(),
        Images = [.. Images.Select(image => image.Clone())]
    };
}

public sealed class QueuedFollowUp : INotifyPropertyChanged
{
    private string text = string.Empty;
    private string editText = string.Empty;
    private bool isEditing;
    private QueuedFollowUpState state;
    private string? lastError;
    private DateTimeOffset updatedAt;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; init; } = string.Empty;

    public string Text
    {
        get => text;
        internal set => SetProperty(ref text, value);
    }

    public string EditText
    {
        get => editText;
        set => SetProperty(ref editText, value);
    }

    public bool IsEditing
    {
        get => isEditing;
        internal set => SetProperty(ref isEditing, value);
    }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt
    {
        get => updatedAt;
        internal set => SetProperty(ref updatedAt, value);
    }

    public QueuedFollowUpState State
    {
        get => state;
        internal set
        {
            if (SetProperty(ref state, value))
            {
                OnPropertyChanged(nameof(StateLabel));
                OnPropertyChanged(nameof(IsPending));
                OnPropertyChanged(nameof(IsStarting));
                OnPropertyChanged(nameof(NeedsAttention));
            }
        }
    }

    public string? LastError
    {
        get => lastError;
        internal set
        {
            if (SetProperty(ref lastError, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public QueuedTurnOptionsSnapshot Options { get; init; } = new();

    public IReadOnlyList<AttachmentReference> Images { get; init; } = [];

    public bool HasImages => Images.Count > 0;

    public string StateLabel => State switch
    {
        QueuedFollowUpState.Starting => "Starting",
        QueuedFollowUpState.NeedsAttention => "Needs attention",
        _ => "Queued"
    };

    public bool IsPending => State == QueuedFollowUpState.Pending;
    public bool IsStarting => State == QueuedFollowUpState.Starting;
    public bool NeedsAttention => State == QueuedFollowUpState.NeedsAttention;
    public bool HasError => !string.IsNullOrWhiteSpace(LastError);

    public QueuedFollowUpSnapshot Snapshot() => new()
    {
        Id = Id,
        Text = Text,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        State = State,
        LastError = LastError,
        Options = Options.Clone(),
        Images = [.. Images.Select(image => image.Clone())]
    };

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

public sealed class CodexFollowUpQueue
{
    public const int MaximumItems = 50;
    public const int MaximumItemBytes = 64 * 1024;
    public const int MaximumAggregateBytes = 256 * 1024;
    private const string InterruptedDeliveryMessage =
        "SynthiaCode closed or disconnected while this follow-up was starting. Review it before retrying.";

    public ObservableCollection<QueuedFollowUp> Items { get; } = [];

    public QueuedFollowUp Enqueue(
        string text,
        QueuedTurnOptionsSnapshot options,
        IEnumerable<AttachmentReference>? images = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var imageList = (images ?? []).Select(image => image.Clone()).ToList();
        var normalized = ValidateContent(text, imageList, replacingItemId: null);
        if (Items.Count >= MaximumItems)
        {
            throw new InvalidOperationException($"A thread can queue at most {MaximumItems} follow-ups.");
        }

        var now = DateTimeOffset.UtcNow;
        var item = new QueuedFollowUp
        {
            Id = Guid.NewGuid().ToString("N"),
            Text = normalized,
            EditText = normalized,
            CreatedAt = now,
            UpdatedAt = now,
            State = QueuedFollowUpState.Pending,
            Options = options.Clone(),
            Images = imageList
        };
        Items.Add(item);
        return item;
    }

    public void Restore(IEnumerable<QueuedFollowUpSnapshot>? snapshots)
    {
        Items.Clear();
        foreach (var snapshot in (snapshots ?? []).Take(MaximumItems))
        {
            if (string.IsNullOrWhiteSpace(snapshot.Text) && snapshot.Images.Count == 0)
            {
                continue;
            }

            var state = snapshot.State == QueuedFollowUpState.Starting
                ? QueuedFollowUpState.NeedsAttention
                : snapshot.State;
            Items.Add(new QueuedFollowUp
            {
                Id = string.IsNullOrWhiteSpace(snapshot.Id) ? Guid.NewGuid().ToString("N") : snapshot.Id,
                Text = snapshot.Text.Trim(),
                EditText = snapshot.Text.Trim(),
                CreatedAt = snapshot.CreatedAt == default ? DateTimeOffset.UtcNow : snapshot.CreatedAt,
                UpdatedAt = snapshot.UpdatedAt == default ? DateTimeOffset.UtcNow : snapshot.UpdatedAt,
                State = state,
                LastError = snapshot.State == QueuedFollowUpState.Starting
                    ? InterruptedDeliveryMessage
                    : snapshot.LastError,
                Options = (snapshot.Options ?? new QueuedTurnOptionsSnapshot()).Clone(),
                Images = [.. snapshot.Images.Select(image => image.Clone())]
            });
        }
    }

    public IReadOnlyList<QueuedFollowUpSnapshot> Snapshot() =>
        Items.Select(item => item.Snapshot()).ToList();

    public void Edit(string id, string text)
    {
        var item = GetRequired(id);
        EnsureMutable(item);
        item.Text = ValidateContent(text, item.Images, id);
        item.EditText = item.Text;
        item.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void BeginEdit(string id)
    {
        var item = GetRequired(id);
        EnsureMutable(item);
        item.EditText = item.Text;
        item.IsEditing = true;
    }

    public void CommitEdit(string id)
    {
        var item = GetRequired(id);
        EnsureMutable(item);
        Edit(id, item.EditText);
        item.IsEditing = false;
    }

    public void CancelEdit(string id)
    {
        var item = GetRequired(id);
        item.EditText = item.Text;
        item.IsEditing = false;
    }

    public void MoveUp(string id)
    {
        var index = IndexOf(id);
        if (index <= 0)
        {
            return;
        }

        EnsureMutable(Items[index]);
        Items.Move(index, index - 1);
        Items[index].UpdatedAt = DateTimeOffset.UtcNow;
        Items[index - 1].UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void MoveDown(string id)
    {
        var index = IndexOf(id);
        if (index < 0 || index >= Items.Count - 1)
        {
            return;
        }

        EnsureMutable(Items[index]);
        Items.Move(index, index + 1);
        Items[index].UpdatedAt = DateTimeOffset.UtcNow;
        Items[index + 1].UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void MarkStarting(string id) => SetState(id, QueuedFollowUpState.Starting, null);

    public void MarkPending(string id, string? error = null) => SetState(id, QueuedFollowUpState.Pending, error);

    public void MarkNeedsAttention(string id, string error) =>
        SetState(id, QueuedFollowUpState.NeedsAttention, string.IsNullOrWhiteSpace(error) ? "Review this follow-up before retrying." : error.Trim());

    public void Remove(string id)
    {
        var item = GetRequired(id);
        Items.Remove(item);
    }

    public int IndexOf(string id)
    {
        for (var index = 0; index < Items.Count; index++)
        {
            if (string.Equals(Items[index].Id, id, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    public QueuedFollowUp GetRequired(string id) =>
        Items.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Queued follow-up '{id}' was not found.");

    private string ValidateContent(
        string? text,
        IReadOnlyCollection<AttachmentReference> images,
        string? replacingItemId)
    {
        var normalized = text?.Trim() ?? string.Empty;
        if (normalized.Length == 0 && images.Count == 0)
        {
            throw new InvalidOperationException("A queued follow-up cannot be empty.");
        }

        if (images.Count > AttachmentLimits.MaximumImagesPerInput)
        {
            throw new InvalidOperationException($"A queued follow-up can contain at most {AttachmentLimits.MaximumImagesPerInput} images.");
        }
        if (images.Any(image => string.IsNullOrWhiteSpace(image.StorageKey) || image.ByteLength <= 0))
        {
            throw new InvalidOperationException("A queued follow-up contains an invalid image reference.");
        }
        var imageBytes = images.Sum(image => image.ByteLength);
        if (imageBytes > AttachmentLimits.MaximumBytesPerInput)
        {
            throw new InvalidOperationException($"Queued images cannot exceed {AttachmentLimits.MaximumBytesPerInput / (1024 * 1024)} MiB per follow-up.");
        }

        var byteCount = Encoding.UTF8.GetByteCount(normalized);
        if (byteCount > MaximumItemBytes)
        {
            throw new InvalidOperationException($"A queued follow-up cannot exceed {MaximumItemBytes / 1024} KiB.");
        }

        var aggregate = Items
            .Where(item => !string.Equals(item.Id, replacingItemId, StringComparison.Ordinal))
            .Sum(item => Encoding.UTF8.GetByteCount(item.Text));
        if (aggregate + byteCount > MaximumAggregateBytes)
        {
            throw new InvalidOperationException($"Queued follow-ups cannot exceed {MaximumAggregateBytes / 1024} KiB per thread.");
        }

        return normalized;
    }

    private void SetState(string id, QueuedFollowUpState state, string? error)
    {
        var item = GetRequired(id);
        item.State = state;
        item.LastError = error;
        item.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static void EnsureMutable(QueuedFollowUp item)
    {
        if (item.State == QueuedFollowUpState.Starting)
        {
            throw new InvalidOperationException("A follow-up cannot be changed while it is starting.");
        }
    }
}

public sealed class CodexFollowUpQueueWorkspace
{
    private readonly Dictionary<string, CodexFollowUpQueue> queues = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> ThreadIds => queues.Keys;

    public CodexFollowUpQueue Restore(string threadId, IEnumerable<QueuedFollowUpSnapshot>? snapshots)
    {
        var queue = GetOrCreate(threadId);
        queue.Restore(snapshots);
        return queue;
    }

    public CodexFollowUpQueue GetOrCreate(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("Thread ID is required.", nameof(threadId));
        }

        if (!queues.TryGetValue(threadId, out var queue))
        {
            queue = new CodexFollowUpQueue();
            queues.Add(threadId, queue);
        }

        return queue;
    }

    public CodexFollowUpQueue GetRequired(string threadId) =>
        queues.TryGetValue(threadId, out var queue)
            ? queue
            : throw new KeyNotFoundException($"Follow-up queue for thread '{threadId}' is not loaded.");
}
