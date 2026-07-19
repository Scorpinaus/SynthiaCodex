using SynthiaCode.App.ViewModels;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Settings;

internal static class QueuedFollowUpTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("queued follow-up preference defaults and parses compatibly", PreferenceDefaultsAndParsesCompatiblyAsync),
        ("queued follow-up domain edits and reorders without identity loss", DomainEditsAndReordersAsync),
        ("queued follow-up restore makes interrupted delivery explicit", RestoreMakesInterruptedDeliveryExplicitAsync),
        ("queued follow-up workspace isolates threads", WorkspaceIsolatesThreadsAsync),
        ("queued follow-ups survive deep settings snapshots", QueueSurvivesDeepSettingsSnapshotAsync),
        ("queued follow-up composer labels active behavior", ComposerLabelsActiveBehaviorAsync)
    ];

    private static Task PreferenceDefaultsAndParsesCompatiblyAsync()
    {
        Assert(((string?)null).ParseFollowUpBehavior() == FollowUpBehavior.Queue, "missing setting defaults to queue");
        Assert("queue".ParseFollowUpBehavior() == FollowUpBehavior.Queue, "queue setting parses");
        Assert("steer".ParseFollowUpBehavior() == FollowUpBehavior.Steer, "steer setting parses");
        Assert("interrupt".ParseFollowUpBehavior() == FollowUpBehavior.Steer, "legacy interrupt maps to steer");
        Assert("unknown".ParseFollowUpBehavior() == FollowUpBehavior.Queue, "unknown setting fails safe to queue");
        Assert(FollowUpBehavior.Queue.ToSettingsValue() == "queue", "queue serializes stably");
        Assert(FollowUpBehavior.Steer.ToSettingsValue() == "steer", "steer serializes stably");
        return Task.CompletedTask;
    }

    private static Task DomainEditsAndReordersAsync()
    {
        var queue = new CodexFollowUpQueue();
        var options = Options(@"D:\Repo");
        var first = queue.Enqueue("  First follow-up  ", options);
        var second = queue.Enqueue("Second follow-up", options);
        var third = queue.Enqueue("Third follow-up", options);

        Assert(queue.Items.Select(item => item.Text).SequenceEqual(["First follow-up", "Second follow-up", "Third follow-up"]), "enqueue trims and preserves FIFO order");
        queue.BeginEdit(second.Id);
        second.EditText = "  Edited second  ";
        queue.CommitEdit(second.Id);
        queue.MoveUp(third.Id);

        Assert(queue.Items.Select(item => item.Text).SequenceEqual(["First follow-up", "Third follow-up", "Edited second"]), "edit and move update queue order");
        Assert(queue.Items[2].Id == second.Id, "edit preserves item identity");
        Assert(queue.Items[2].Options.Model == "gpt-test", "edit preserves captured options");
        queue.BeginEdit(second.Id);
        second.EditText = "Discard this edit";
        queue.CancelEdit(second.Id);
        Assert(second.EditText == second.Text && !second.IsEditing, "cancel restores committed text");

        queue.MarkStarting(first.Id);
        Assert(queue.Items[0].State == QueuedFollowUpState.Starting, "starting transition is explicit");
        queue.MarkPending(first.Id, "definite rejection");
        Assert(queue.Items[0].State == QueuedFollowUpState.Pending, "definite rejection is retryable");
        Assert(queue.Items[0].LastError == "definite rejection", "definite rejection detail is retained");
        queue.MarkNeedsAttention(first.Id, "delivery unknown");
        Assert(queue.Items[0].State == QueuedFollowUpState.NeedsAttention, "ambiguous delivery needs attention");
        queue.Remove(first.Id);
        Assert(queue.Items.Count == 2 && queue.Items.All(item => item.Id != first.Id), "remove deletes only the acknowledged item");
        return Task.CompletedTask;
    }

    private static Task RestoreMakesInterruptedDeliveryExplicitAsync()
    {
        var queue = new CodexFollowUpQueue();
        queue.Restore(
        [
            new QueuedFollowUpSnapshot
            {
                Id = "starting",
                Text = "Was this delivered?",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                UpdatedAt = DateTimeOffset.UtcNow,
                State = QueuedFollowUpState.Starting,
                Options = Options(@"D:\Repo")
            }
        ]);

        var restored = queue.Items.Single();
        Assert(restored.State == QueuedFollowUpState.NeedsAttention, "restored starting item is never retried automatically");
        Assert(!string.IsNullOrWhiteSpace(restored.LastError), "restored ambiguous item explains why it is paused");
        return Task.CompletedTask;
    }

    private static Task WorkspaceIsolatesThreadsAsync()
    {
        var workspace = new CodexFollowUpQueueWorkspace();
        workspace.GetOrCreate("thread-a").Enqueue("A", Options(@"D:\A"));
        workspace.GetOrCreate("thread-b").Enqueue("B", Options(@"D:\B"));

        Assert(workspace.GetRequired("thread-a").Items.Single().Text == "A", "thread A queue is isolated");
        Assert(workspace.GetRequired("thread-b").Items.Single().Text == "B", "thread B queue is isolated");
        Assert(workspace.ThreadIds.Count == 2, "workspace tracks both queues");
        return Task.CompletedTask;
    }

    private static Task QueueSurvivesDeepSettingsSnapshotAsync()
    {
        var queued = new QueuedFollowUpSnapshot
        {
            Id = "queued-1",
            Text = "Run the integration tests next",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            State = QueuedFollowUpState.Pending,
            Options = Options(@"D:\Repo")
        };
        var settings = new AppSettings
        {
            FollowUpBehavior = "steer",
            ProjectThreads =
            [
                new PersistedProjectThread
                {
                    ProjectPath = @"D:\Repo",
                    ThreadId = "thread-1",
                    QueuedFollowUps = [queued]
                }
            ]
        };

        var snapshot = AppSettingsSnapshot.Create(settings);
        queued.Text = "mutated";
        queued.Options.Model = "mutated-model";
        settings.FollowUpBehavior = "queue";

        Assert(snapshot.FollowUpBehavior == "steer", "preference snapshot is independent");
        var saved = snapshot.ProjectThreads.Single().QueuedFollowUps.Single();
        Assert(saved.Text == "Run the integration tests next", "queued prompt snapshot is independent");
        Assert(saved.Options.Model == "gpt-test", "nested turn options snapshot is independent");

        var store = new ThreadStore();
        var presentation = store.GetProjectThreads(snapshot, @"D:\Repo").Single();
        presentation.QueuedFollowUps[0].Text = "presentation mutation";
        Assert(snapshot.ProjectThreads[0].QueuedFollowUps[0].Text == "Run the integration tests next", "thread store presentation clone is independent");
        return Task.CompletedTask;
    }

    private static Task ComposerLabelsActiveBehaviorAsync()
    {
        var viewModel = new TaskViewModel(
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => false,
            () => false);

        viewModel.IsTurnRunning = true;
        viewModel.FollowUpBehavior = FollowUpBehavior.Queue;
        Assert(viewModel.ComposerActionLabel == "Queue follow-up", "active queue default is visible");
        Assert(viewModel.AlternateFollowUpActionLabel == "Steer current turn", "queue default exposes steer alternate");

        viewModel.FollowUpBehavior = FollowUpBehavior.Steer;
        Assert(viewModel.ComposerActionLabel == "Steer task", "active steer default is visible");
        Assert(viewModel.AlternateFollowUpActionLabel == "Queue for next turn", "steer default exposes queue alternate");
        return Task.CompletedTask;
    }

    private static QueuedTurnOptionsSnapshot Options(string workspacePath) => new()
    {
        WorkspacePath = workspacePath,
        Model = "gpt-test",
        ReasoningEffort = CodexReasoningEffort.High,
        ServiceTier = CodexServiceTierSelection.Fast,
        Sandbox = CodexSandbox.WorkspaceWrite,
        ApprovalPolicy = CodexApprovalPolicy.OnRequest,
        ApprovalsReviewer = CodexApprovalsReviewer.User
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
