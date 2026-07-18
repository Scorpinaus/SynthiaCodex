using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NativeCodexAssistant.App.Services;
using NativeCodexAssistant.App.ViewModels;
using NativeCodexAssistant.App.Views;
using NativeCodexAssistant.Core.Codex.AppServer;
using NativeCodexAssistant.Core.Projects;
using NativeCodexAssistant.Core.Settings;

internal static class ResponsiveLayoutTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("responsive navigation and transcript constrain long content", ResponsiveViewsConstrainLongContentAsync)
    ];

    private static Task ResponsiveViewsConstrainLongContentAsync() => RunOnStaAsync(() =>
    {
        var app = new Application();
        ConfigureTestResources(app.Resources);
        try
        {
            VerifyProjectNavigationWraps();
            VerifyTranscriptWrapsAndScrolls();
        }
        finally
        {
            app.Shutdown();
        }
    });

    private static void ConfigureTestResources(ResourceDictionary resources)
    {
        resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
        resources["InverseBooleanToVisibilityConverter"] = new InverseBooleanToVisibilityConverter();
        resources["Card"] = BorderStyle(new Thickness(16));
        resources["StatePill"] = BorderStyle(new Thickness(8, 3, 8, 3));
        resources["ConversationTurnCard"] = BorderStyle(new Thickness(14));
        resources["ConversationUserSurface"] = BorderStyle(new Thickness(12));
        resources["ConversationAssistantSurface"] = BorderStyle(new Thickness(12));
        resources["ConversationActivitySurface"] = BorderStyle(new Thickness(10, 8, 10, 8));
        resources["CompactButton"] = new Style(typeof(Button));
        resources["CreateThreadButton"] = new Style(typeof(Button));
        resources["ProjectActionButton"] = new Style(typeof(Button));
        resources["RunTaskButton"] = new Style(typeof(Button));
        resources["SectionLabel"] = TextStyle(fontSize: 12);
        resources["ConversationBodyText"] = TextStyle(fontSize: 14, lineHeight: 22, wraps: true);
        resources["ConversationRoleText"] = TextStyle(fontSize: 12);
        resources["ConversationMetadataText"] = TextStyle(fontSize: 11);
        resources["ConversationActivityTitleText"] = TextStyle(fontSize: 12);
        resources["ConversationActivityDetailText"] = TextStyle(fontSize: 12, lineHeight: 18, wraps: true);
    }

    private static Style BorderStyle(Thickness padding)
    {
        var style = new Style(typeof(Border));
        style.Setters.Add(new Setter(Border.PaddingProperty, padding));
        return style;
    }

    private static Style TextStyle(double fontSize, double? lineHeight = null, bool wraps = false)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.FontSizeProperty, fontSize));
        if (lineHeight is not null)
        {
            style.Setters.Add(new Setter(TextBlock.LineHeightProperty, lineHeight.Value));
        }
        if (wraps)
        {
            style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        }
        return style;
    }

    private static void VerifyProjectNavigationWraps()
    {
        const string longProjectName = "ProjectNameThatMustWrapBecauseItIsMuchWiderThanTheNavigationColumn";
        const string longThreadTitle = "019f3d42-5a99-78d2-b449-5affcbbc3d65-thread-title-that-must-wrap";
        const string projectPath = @"C:\Work\ResponsiveProject";
        var workspace = CreateProjectViewModel();
        var thread = new ProjectThreadState
        {
            ProjectPath = projectPath,
            ThreadId = "responsive-thread",
            Title = longThreadTitle,
            WorkspacePath = projectPath,
            IsRunning = true,
            TurnStatus = "Running",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        workspace.RefreshProjectNavigation(
            [new RecentProject(projectPath, longProjectName, DateTimeOffset.UtcNow)],
            [thread]);
        workspace.SetSelectedProjectPath(projectPath);
        workspace.SelectedThread = thread;

        var view = new ProjectThreadView
        {
            DataContext = new ProjectContext(workspace),
            Width = 268,
            Height = 420
        };

        PumpLayout(view);
        var projectList = FindVisualDescendants<ListBox>(view).First();
        var scroller = FindVisualDescendant<ScrollViewer>(projectList)
            ?? throw new InvalidOperationException("project navigation scroll viewer was not created");
        var projectText = FindVisualDescendants<TextBlock>(view).Single(block => block.Text == longProjectName);
        var threadText = FindVisualDescendants<TextBlock>(view).Single(block => block.Text == longThreadTitle);

        AssertNear(0, scroller.ScrollableWidth, "project navigation has no horizontal scroll extent");
        Assert(projectText.ActualHeight > projectText.FontSize * 1.5, "long project name wraps to multiple lines");
        Assert(threadText.ActualHeight > threadText.FontSize * 1.5, "long thread title wraps to multiple lines");
    }

    private static void VerifyTranscriptWrapsAndScrolls()
    {
        var longLine = string.Join(' ', Enumerable.Repeat("A responsive assistant response must stay inside the transcript column.", 18));
        var tallResponse = string.Join(Environment.NewLine, Enumerable.Repeat(longLine, 12));
        var turns = new ObservableCollection<CodexConversationTurn>
        {
            new()
            {
                TurnId = "responsive-turn",
                UserPrompt = longLine,
                AssistantResponse = tallResponse,
                Status = CodexTurnStatus.Completed
            }
        };
        var view = new TaskView { Width = 620, Height = 520 };
        var conversationList = (ListBox?)view.FindName("ConversationList")
            ?? throw new InvalidOperationException("conversation list was not found");
        conversationList.ItemsSource = turns;
        typeof(TaskView)
            .GetField("observedTurns", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(view, turns);

        PumpLayout(view);
        var scroller = FindVisualDescendant<ScrollViewer>(conversationList)
            ?? throw new InvalidOperationException("conversation scroll viewer was not created");
        var responseText = FindVisualDescendants<TextBlock>(conversationList)
            .Single(block => block.Text == tallResponse);

        AssertNear(0, scroller.ScrollableWidth, "transcript has no horizontal scroll extent");
        Assert(responseText.ActualWidth <= scroller.ViewportWidth + 0.5, "assistant response stays within transcript viewport");
        Assert(responseText.ActualHeight > responseText.FontSize * 3, "assistant response wraps to multiple lines");
        Assert(
            VirtualizingPanel.GetScrollUnit(conversationList) == ScrollUnit.Pixel,
            "transcript uses pixel scrolling for variable-height turns");

        var settings = FindVisualDescendants<Expander>(view)
            .Single(expander => Equals(expander.Header, "Run settings"));
        scroller.ScrollToEnd();
        PumpLayout(view);
        settings.IsExpanded = true;
        PumpLayout(view);

        Assert(scroller.ScrollableHeight > 0, "expanded settings leave an accessible vertical transcript range");
        AssertNear(scroller.ScrollableHeight, scroller.VerticalOffset, "expanding settings preserves the latest response");

        scroller.ScrollToTop();
        PumpLayout(view);
        settings.IsExpanded = false;
        PumpLayout(view);
        AssertNear(0, scroller.VerticalOffset, "viewport changes preserve a user-scrolled-up position");
    }

    private static ProjectThreadViewModel CreateProjectViewModel() => new(
        () => Task.CompletedTask,
        _ => Task.CompletedTask,
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
        _ => { });

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

    private static Task RunOnStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void AssertNear(double expected, double actual, string message)
    {
        if (Math.Abs(expected - actual) > 0.5)
        {
            throw new InvalidOperationException($"{message}: expected {expected:0.##}, actual {actual:0.##}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record ProjectContext(ProjectThreadViewModel ProjectWorkspace);

}
