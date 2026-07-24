using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;
using SynthiaCode.App.Views;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Settings;

internal static class ContextWindowIndicatorTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("context usage notifications are enabled by default", ContextUsageNotificationsAreEnabledByDefaultAsync),
        ("context usage is calculated and routed per chat", ContextUsageIsCalculatedAndRoutedPerChatAsync),
        ("context usage handles reasoning token edge cases", ContextUsageHandlesReasoningTokenEdgeCasesAsync),
        ("context compactions count current and legacy notifications once", ContextCompactionsCountCurrentAndLegacyNotificationsOnceAsync),
        ("app-server compaction notifications render as transcript activity", AppServerCompactionNotificationsRenderAsTranscriptActivityAsync),
        ("token pressure does not trigger client-side summarization", TokenPressureDoesNotTriggerClientSideSummarizationAsync),
        ("context usage and compactions survive chat persistence", ContextUsageAndCompactionsSurviveChatPersistenceAsync),
        ("composer shows context used beside send with usage tooltip", ComposerShowsContextUsedBesideSendAsync)
    ];

    private static Task ContextUsageNotificationsAreEnabledByDefaultAsync()
    {
        Assert(
            CodexInitializeOptions.Default.OptOutNotificationMethods?.Contains(
                "thread/tokenUsage/updated",
                StringComparer.Ordinal) != true,
            "default initialization subscribes to context token usage");
        return Task.CompletedTask;
    }

    private static Task ContextUsageIsCalculatedAndRoutedPerChatAsync()
    {
        var workspace = new CodexThreadWorkspace();
        var first = workspace.GetOrCreate("thread-a");
        var second = workspace.GetOrCreate("thread-b");

        workspace.ApplyNotification(Notification(
            "thread/tokenUsage/updated",
            """
            {
              "threadId":"thread-a",
              "turnId":"turn-a",
              "tokenUsage":{
                "total":{"inputTokens":140000,"cachedInputTokens":80000,"outputTokens":9000,"reasoningOutputTokens":1000,"totalTokens":150000},
                "last":{"inputTokens":40000,"cachedInputTokens":20000,"outputTokens":2500,"reasoningOutputTokens":500,"totalTokens":43000},
                "modelContextWindow":252000
              }
            }
            """));

        Assert(first.ContextTokensUsed == 42500, "reasoning output tokens are excluded from latest context usage");
        Assert(first.ContextWindowTokens == 252000, "model context window is retained");
        Assert(first.ContextUsedPercent == 17, "used percentage is rounded from the protocol values");
        Assert(first.ContextRemainingPercent == 83, "remaining percentage complements used percentage");
        Assert(first.HasContextWindowUsage, "valid token usage makes context information available");
        Assert(!second.HasContextWindowUsage, "usage stays isolated from other chats");
        return Task.CompletedTask;
    }

    private static Task ContextUsageHandlesReasoningTokenEdgeCasesAsync()
    {
        var missingReasoning = new CodexThreadService();
        missingReasoning.ApplyNotification(Notification(
            "thread/tokenUsage/updated",
            """{"tokenUsage":{"last":{"totalTokens":43000},"modelContextWindow":252000}}"""));

        var zeroReasoning = new CodexThreadService();
        zeroReasoning.ApplyNotification(Notification(
            "thread/tokenUsage/updated",
            """{"tokenUsage":{"last":{"reasoningOutputTokens":0,"totalTokens":43000},"modelContextWindow":252000}}"""));

        var oversizedReasoning = new CodexThreadService();
        oversizedReasoning.ApplyNotification(Notification(
            "thread/tokenUsage/updated",
            """{"tokenUsage":{"last":{"reasoningOutputTokens":44000,"totalTokens":43000},"modelContextWindow":252000}}"""));

        Assert(missingReasoning.ContextTokensUsed == 43000, "missing reasoning tokens default to zero");
        Assert(zeroReasoning.ContextTokensUsed == 43000, "zero reasoning tokens preserve total usage");
        Assert(oversizedReasoning.ContextTokensUsed == 0, "oversized reasoning tokens clamp context usage to zero");
        Assert(oversizedReasoning.ContextWindowTokens == 252000, "valid context window is retained for clamped usage");
        return Task.CompletedTask;
    }

    private static Task ContextCompactionsCountCurrentAndLegacyNotificationsOnceAsync()
    {
        var service = new CodexThreadService();

        service.ApplyNotification(Notification(
            "item/completed",
            """{"threadId":"thread-a","turnId":"turn-a","item":{"id":"compact-1","type":"contextCompaction"}}"""));
        service.ApplyNotification(Notification(
            "item/completed",
            """{"threadId":"thread-a","turnId":"turn-a","item":{"id":"compact-1","type":"contextCompaction"}}"""));
        service.ApplyNotification(Notification(
            "thread/compacted",
            """{"threadId":"thread-a","turnId":"turn-b"}"""));

        Assert(service.ContextCompactionCount == 2, "current and legacy compactions are counted without duplicate items");
        return Task.CompletedTask;
    }

    private static Task AppServerCompactionNotificationsRenderAsTranscriptActivityAsync()
    {
        var current = new CodexThreadService();
        current.BeginTurn("Continue the task.");
        current.BindPendingTurn("turn-a");

        current.ApplyNotification(Notification(
            "item/started",
            """{"threadId":"thread-a","turnId":"turn-a","item":{"id":"compact-1","type":"contextCompaction"}}"""));
        current.ApplyNotification(Notification(
            "item/completed",
            """{"threadId":"thread-a","turnId":"turn-a","item":{"id":"compact-1","type":"contextCompaction"}}"""));

        var currentActivity = current.ConversationTurns.Single().Activity.Single();
        Assert(currentActivity.Title == "Compacted context", "current compaction lifecycle is rendered as one completed activity");
        Assert(currentActivity.Detail.Contains("Codex app-server", StringComparison.Ordinal), "activity identifies app-server as the compaction owner");

        var legacy = new CodexThreadService();
        legacy.BeginTurn("Continue the task.");
        legacy.BindPendingTurn("turn-b");
        legacy.ApplyNotification(Notification(
            "thread/compacted",
            """{"threadId":"thread-b","turnId":"turn-b"}"""));

        var legacyActivity = legacy.ConversationTurns.Single().Activity.Single();
        Assert(legacyActivity.Title == "Compacted context", "legacy app-server notification is rendered as transcript activity");
        Assert(legacyActivity.Method == "thread/compacted", "legacy activity retains notification provenance");
        return Task.CompletedTask;
    }

    private static Task TokenPressureDoesNotTriggerClientSideSummarizationAsync()
    {
        var service = new CodexThreadService();
        service.BeginTurn("Keep working without rewriting this prompt.");
        service.BindPendingTurn("turn-a");

        service.ApplyNotification(Notification(
            "thread/tokenUsage/updated",
            """{"threadId":"thread-a","turnId":"turn-a","tokenUsage":{"last":{"totalTokens":251999},"modelContextWindow":252000}}"""));

        var turn = service.ConversationTurns.Single();
        Assert(service.ContextRemainingPercent == 0, "near-full context is observed and rounded for display");
        Assert(service.ContextCompactionCount == 0, "token pressure alone does not fabricate a compaction");
        Assert(turn.Activity.Count == 0, "token pressure alone does not fabricate compaction activity");
        Assert(turn.UserPrompt == "Keep working without rewriting this prompt.", "the client does not replace conversation content with a local summary");
        Assert(string.IsNullOrEmpty(turn.AssistantResponse), "the client does not generate an automatic summary response");
        return Task.CompletedTask;
    }

    private static Task ContextUsageAndCompactionsSurviveChatPersistenceAsync()
    {
        var store = new ThreadStore();
        var settings = new AppSettings();
        store.Upsert(settings, new ProjectThreadState
        {
            ProjectPath = @"C:\Repo",
            ThreadId = "thread-a",
            ContextTokensUsed = 43000,
            ContextWindowTokens = 252000,
            ContextCompactionCount = 3
        });

        var snapshot = AppSettingsSnapshot.Create(settings);
        var restoredState = store.GetProjectThreads(snapshot, @"C:\Repo").Single();
        var workspace = new CodexThreadWorkspace();
        var restoredService = workspace.Restore(restoredState);

        Assert(restoredService.ContextTokensUsed == 43000, "persisted context tokens restore into the chat service");
        Assert(restoredService.ContextWindowTokens == 252000, "persisted context window restores into the chat service");
        Assert(restoredService.ContextCompactionCount == 3, "persisted compaction count restores into the chat service");
        return Task.CompletedTask;
    }

    private static Task ComposerShowsContextUsedBesideSendAsync() => WpfTestHost.RunAsync(() =>
    {
        ConfigureTestResources(Application.Current.Resources);
        var service = new CodexThreadService();
        service.ApplyNotification(Notification(
            "thread/tokenUsage/updated",
            """{"threadId":"thread-a","turnId":"turn-a","tokenUsage":{"total":{"inputTokens":140000,"cachedInputTokens":80000,"outputTokens":9000,"reasoningOutputTokens":1000,"totalTokens":150000},"last":{"inputTokens":40000,"cachedInputTokens":20000,"outputTokens":2500,"reasoningOutputTokens":500,"totalTokens":43000},"modelContextWindow":252000}}"""));
        service.ApplyNotification(Notification(
            "item/completed",
            """{"threadId":"thread-a","turnId":"turn-a","item":{"id":"compact-1","type":"contextCompaction"}}"""));

        var viewModel = CreateTaskViewModel();
        viewModel.UseThreadService(service);
        var view = new TaskView { DataContext = new TaskContext(viewModel) };
        view.ApplyTemplate();
        view.Measure(new Size(900, 700));
        view.Arrange(new Rect(0, 0, 900, 700));
        view.UpdateLayout();

        var indicator = view.FindName("ContextWindowIndicator") as Border
            ?? throw new InvalidOperationException("context window indicator was not found");
        var label = indicator.Child as TextBlock
            ?? throw new InvalidOperationException("context window indicator label was not found");
        var send = FindSiblingSendButton(indicator);

        label.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
        indicator.GetBindingExpression(FrameworkElement.ToolTipProperty)?.UpdateTarget();

        Assert(label.Text == "17%", "indicator shows context percentage used");
        Assert(indicator.ToolTip?.ToString() ==
            string.Join(Environment.NewLine, "Context window", "17% used, 83% remaining", "42.5k/252k tokens used", "Compactions: 1"),
            $"tooltip includes used, remaining, token totals, and compactions; actual: {indicator.ToolTip}");
        Assert(ReferenceEquals(indicator.Parent, send.Parent), "indicator is in the bottom action row beside send");
        Assert(AutomationProperties.GetName(indicator) == "Context window usage", "indicator has an accessible name");
    });

    private static Button FindSiblingSendButton(Border indicator)
    {
        var panel = indicator.Parent as Panel
            ?? throw new InvalidOperationException("context indicator was not placed in the composer action panel");
        var indicatorIndex = panel.Children.IndexOf(indicator);
        return indicatorIndex >= 0 && indicatorIndex + 1 < panel.Children.Count &&
            panel.Children[indicatorIndex + 1] is Button send
                ? send
                : throw new InvalidOperationException("composer send button was not adjacent to the context indicator");
    }

    private static TaskViewModel CreateTaskViewModel() => new(
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => false,
        () => false);

    private static AppServerNotification Notification(string method, string json) =>
        new(method, JsonNode.Parse(json)!.AsObject());

    private static void ConfigureTestResources(ResourceDictionary resources)
    {
        resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
        resources["InverseBooleanToVisibilityConverter"] = new InverseBooleanToVisibilityConverter();
        resources["Card"] = BorderStyle(new Thickness(16));
        resources["ConversationTurnCard"] = BorderStyle(new Thickness(14));
        resources["ConversationUserSurface"] = BorderStyle(new Thickness(12));
        resources["ConversationAssistantSurface"] = BorderStyle(new Thickness(12));
        resources["StatePill"] = BorderStyle(new Thickness(8, 3, 8, 3));
        resources["CompactButton"] = new Style(typeof(Button));
        resources["RunTaskButton"] = new Style(typeof(Button));
        resources["ConversationBodyText"] = new Style(typeof(TextBlock));
        resources["ConversationRoleText"] = new Style(typeof(TextBlock));
        resources["ConversationMetadataText"] = new Style(typeof(TextBlock));
        resources["ConversationActivityTitleText"] = new Style(typeof(TextBlock));
        resources["ConversationActivityDetailText"] = new Style(typeof(TextBlock));
    }

    private static Style BorderStyle(Thickness padding)
    {
        var style = new Style(typeof(Border));
        style.Setters.Add(new Setter(Border.PaddingProperty, padding));
        return style;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record TaskContext(TaskViewModel TaskWorkspace);
}
