using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;
using SynthiaCode.App.Views;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Projects;
using SynthiaCode.Infrastructure.Codex;

internal static class AccountFeatureTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("app-server client reads account and rate limits", ClientReadsAccountAndRateLimitsAsync),
        ("account view model projects identity and remaining usage", AccountViewModelProjectsIdentityAndUsageAsync),
        ("account notifications update usage without entering a thread", AccountNotificationUpdatesUsageAsync),
        ("account footer remains fixed while project navigation scrolls", AccountFooterRemainsFixedAsync)
    ];

    private static async Task ClientReadsAccountAndRateLimitsAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(
            transport,
            new CodexAppServerClientMetadata("account_tests", "Account tests", "1.0.0"));
        await CompleteInitializeAsync(client, transport);

        var accountTask = client.ReadAccountAsync();
        await transport.WaitForClientMessageCountAsync(3);
        var accountRequest = ParseMessage(transport.ClientMessages[2]);
        AssertJsonString("account/read", accountRequest, "method", "account method");
        AssertJsonBoolean(false, accountRequest, "params.refreshToken", "account refresh flag");
        transport.ServerSend(
            """
            {"id":1,"result":{"account":{"type":"chatgpt","email":"jane.doe@example.com","planType":"pro"},"requiresOpenaiAuth":true}}
            """);

        var account = await accountTask;
        AssertEqual("chatgpt", account.Account?.Type, "account type");
        AssertEqual("jane.doe@example.com", account.Account?.Email, "account email");
        AssertEqual("pro", account.Account?.PlanType, "account plan");
        Assert(account.RequiresOpenAiAuth, "account requires OpenAI auth");

        var limitsTask = client.ReadAccountRateLimitsAsync();
        await transport.WaitForClientMessageCountAsync(4);
        var limitsRequest = ParseMessage(transport.ClientMessages[3]);
        AssertJsonString("account/rateLimits/read", limitsRequest, "method", "rate-limit method");
        transport.ServerSend(
            """
            {"id":2,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":1784383200}},"rateLimitsByLimitId":{"codex":{"limitId":"codex","primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":1784383200},"secondary":{"usedPercent":40,"windowDurationMins":10080,"resetsAt":1784901600}},"codex_other":{"limitId":"codex_other","limitName":"Other Codex","primary":{"usedPercent":42,"windowDurationMins":60,"resetsAt":1784386800}}},"rateLimitResetCredits":{"availableCount":2}}}
            """);

        var limits = await limitsTask;
        AssertEqual(2, limits.Limits.Count, "deduplicated limit count");
        AssertEqual("codex", limits.Limits[0].LimitId, "Codex limit first");
        AssertEqual(25, limits.Limits[0].Primary?.UsedPercent, "primary used percent");
        AssertEqual(40, limits.Limits[0].Secondary?.UsedPercent, "secondary used percent");
        AssertEqual(2, limits.ResetCreditsAvailable, "reset credits");
    }

    private static Task AccountViewModelProjectsIdentityAndUsageAsync()
    {
        var settingsOpened = 0;
        var viewModel = CreateAccountViewModel(() => settingsOpened++);
        viewModel.ApplyAccount(new CodexAccountReadResult(
            new CodexAccountInfo("chatgpt", "jane.doe@example.com", "pro", null),
            RequiresOpenAiAuth: true));
        viewModel.ApplyRateLimits(new CodexAccountRateLimitsResult(
            [new CodexRateLimitSnapshot(
                "codex",
                null,
                "pro",
                new CodexRateLimitWindow(25, 300, DateTimeOffset.Parse("2026-07-18T18:00:00+08:00")),
                new CodexRateLimitWindow(40, 10080, DateTimeOffset.Parse("2026-07-24T18:00:00+08:00")),
                null,
                null)],
            ResetCreditsAvailable: 2));

        AssertEqual("jane.doe", viewModel.DisplayName, "derived display name");
        AssertEqual("JD", viewModel.Initials, "derived initials");
        AssertEqual("jane.doe@example.com · Pro", viewModel.IdentityDetail, "identity detail");
        AssertEqual(2, viewModel.UsageBuckets.Count, "usage window count");
        AssertEqual("5-hour limit", viewModel.UsageBuckets[0].Label, "primary duration label");
        AssertEqual(75, viewModel.UsageBuckets[0].RemainingPercent, "primary remaining percent");
        AssertEqual("Weekly limit", viewModel.UsageBuckets[1].Label, "secondary duration label");
        AssertEqual(60, viewModel.UsageBuckets[1].RemainingPercent, "secondary remaining percent");
        AssertEqual("2 limit resets available", viewModel.ResetCreditsLabel, "reset credit label");

        viewModel.IsFlyoutOpen = true;
        viewModel.OpenSettingsCommand.Execute(null);
        AssertEqual(1, settingsOpened, "settings action count");
        Assert(!viewModel.IsFlyoutOpen, "settings action closes flyout");
        return Task.CompletedTask;
    }

    private static Task AccountNotificationUpdatesUsageAsync()
    {
        var viewModel = CreateAccountViewModel(() => { });
        viewModel.ApplyAccount(new CodexAccountReadResult(
            new CodexAccountInfo("chatgpt", "jane@example.com", "plus", null),
            RequiresOpenAiAuth: true));

        var handled = viewModel.TryApplyNotification(new AppServerNotification(
            "account/rateLimits/updated",
            JsonNode.Parse(
                """
                {"rateLimits":{"limitId":"codex","primary":{"usedPercent":58,"windowDurationMins":300,"resetsAt":1784383200}}}
                """)!.AsObject()));

        Assert(handled, "account notification handled");
        AssertEqual(1, viewModel.UsageBuckets.Count, "notification usage count");
        AssertEqual(42, viewModel.UsageBuckets[0].RemainingPercent, "notification remaining percent");
        return Task.CompletedTask;
    }

    private static Task AccountFooterRemainsFixedAsync() => WpfTestHost.RunAsync(() =>
    {
        ConfigureTestResources(Application.Current.Resources);
        var projectWorkspace = CreateProjectViewModel();
        projectWorkspace.RefreshProjectNavigation(
            Enumerable.Range(1, 18)
                .Select(index => new RecentProject(
                    $@"C:\Work\Project{index}",
                    $"Project {index}",
                    DateTimeOffset.UtcNow.AddMinutes(-index)))
                .ToList(),
            []);
        var account = CreateAccountViewModel(() => { });
        account.ApplyAccount(new CodexAccountReadResult(
            new CodexAccountInfo("chatgpt", "jane.doe@example.com", "pro", null),
            RequiresOpenAiAuth: true));

        var view = new ProjectThreadView
        {
            DataContext = new ProjectContext(projectWorkspace, account),
            Width = 268,
            Height = 320
        };
        var host = new Window
        {
            Content = view,
            Width = 268,
            Height = 320,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Opacity = 0
        };
        host.Show();
        try
        {
            PumpLayout(view);

            var footer = FindVisualDescendants<FrameworkElement>(view)
                .Single(element => element.Name == "AccountFooter");
            var projectList = FindVisualDescendants<ListBox>(view)
                .First(listBox => ReferenceEquals(listBox.ItemsSource, projectWorkspace.Projects));
            var scroller = FindVisualDescendant<ScrollViewer>(projectList)
                ?? throw new InvalidOperationException("project navigation scroll viewer was not created");
            var footerTop = footer.TransformToAncestor(view).Transform(new Point()).Y;

            Assert(scroller.ScrollableHeight > 0, "project list has an independent vertical scroll range");
            Assert(footerTop + footer.ActualHeight <= view.ActualHeight + 0.5, "account footer stays within navigation bounds");
            Assert(footerTop > view.ActualHeight / 2, "account footer is anchored in the lower half of the rail");

            var accountView = FindVisualDescendants<UserAccountView>(view).Single();
            var popup = accountView.FindName("AccountPopup") as Popup
                ?? throw new InvalidOperationException("account popup was not created");
            account.IsFlyoutOpen = true;
            PumpLayout(view);
            Assert(popup.IsOpen, "account flyout opens from view-model state");
            Assert(ReferenceEquals(popup.DataContext, account), "account flyout retains the account data context");
            account.IsFlyoutOpen = false;

            scroller.ScrollToEnd();
            PumpLayout(view);
            var footerAfterScroll = footer.TransformToAncestor(view).Transform(new Point()).Y;
            AssertNear(footerTop, footerAfterScroll, "project scrolling does not move account footer");
        }
        finally
        {
            host.Close();
        }
    });

    private static AccountViewModel CreateAccountViewModel(Action openSettings) => new(
        _ => Task.FromResult(new CodexAccountReadResult(null, RequiresOpenAiAuth: true)),
        _ => Task.FromResult(new CodexAccountRateLimitsResult([], null)),
        openSettings,
        new RelayCommand(() => { }),
        new RelayCommand(() => { }),
        new TestLogger());

    private static ProjectThreadViewModel CreateProjectViewModel() => new(
        () => Task.CompletedTask,
        _ => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => true,
        () => true,
        () => true,
        () => true,
        () => true,
        () => true,
        _ => { });

    private static async Task CompleteInitializeAsync(CodexAppServerClient client, FakeAppServerTransport transport)
    {
        var initializeTask = client.InitializeAsync();
        await transport.WaitForClientMessageCountAsync(1);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"codex-test"}}""");
        await initializeTask;
        await transport.WaitForClientMessageCountAsync(2);
    }

    private static JsonObject ParseMessage(string line) =>
        JsonNode.Parse(line) as JsonObject ?? throw new InvalidOperationException("Message was not a JSON object.");

    private static JsonNode? ResolvePath(JsonNode node, string path)
    {
        JsonNode? current = node;
        foreach (var segment in path.Split('.'))
        {
            current = current is JsonObject obj ? obj[segment] : null;
        }

        return current;
    }

    private static void AssertJsonString(string expected, JsonNode node, string path, string label) =>
        AssertEqual(expected, ResolvePath(node, path)?.GetValue<string>(), label);

    private static void AssertJsonBoolean(bool expected, JsonNode node, string path, string label) =>
        AssertEqual(expected, ResolvePath(node, path)?.GetValue<bool>(), label);

    private static void ConfigureTestResources(ResourceDictionary resources)
    {
        resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
        resources["InverseBooleanToVisibilityConverter"] = new InverseBooleanToVisibilityConverter();
        resources["CompactButton"] = new Style(typeof(Button));
        resources["CreateThreadButton"] = new Style(typeof(Button));
        resources["ProjectActionButton"] = new Style(typeof(Button));
        resources["SectionLabel"] = new Style(typeof(TextBlock));
    }

    private static void PumpLayout(FrameworkElement element)
    {
        var available = new Size(element.Width, element.Height);
        element.Measure(available);
        element.Arrange(new Rect(available));
        element.UpdateLayout();
        element.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        element.Measure(available);
        element.Arrange(new Rect(available));
        element.UpdateLayout();
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject => FindVisualDescendants<T>(root).FirstOrDefault();

    private static void AssertNear(double expected, double actual, string message)
    {
        if (Math.Abs(expected - actual) > 0.5)
        {
            throw new InvalidOperationException($"{message}: expected {expected:0.##}, actual {actual:0.##}");
        }
    }

    private static void Assert<T>(bool condition, T message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message?.ToString());
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{label}: expected {expected}, actual {actual}");
        }
    }

    private sealed record ProjectContext(ProjectThreadViewModel ProjectWorkspace, AccountViewModel Account);
}
