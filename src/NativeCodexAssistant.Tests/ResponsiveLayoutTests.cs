using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using NativeCodexAssistant.App.Controls;
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

    private static Task ResponsiveViewsConstrainLongContentAsync() => WpfTestHost.RunAsync(() =>
    {
        ConfigureTestResources(Application.Current.Resources);
        ApplyDarkThemeForTest(Application.Current.Resources);
        VerifyDarkLinkToolTip(Application.Current.Resources);
        VerifyTextOnlyContextMenu(Application.Current.Resources);
        VerifyProjectNavigationWraps();
        VerifyTranscriptWrapsAndScrolls();
    });

    private static void ApplyDarkThemeForTest(ResourceDictionary resources)
    {
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "/NativeCodexAssistant.App;component/Themes/DarkTheme.xaml",
                UriKind.Relative)
        });
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "/NativeCodexAssistant.App;component/Themes/TransientSurfaces.xaml",
                UriKind.Relative)
        });
    }

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

    private static void VerifyDarkLinkToolTip(ResourceDictionary resources)
    {
        var renderer = new MarkdownTextBlock { Markdown = "[Open documentation](https://example.com/docs)" };
        var link = renderer.Inlines.OfType<Hyperlink>().Single();
        var toolTip = link.ToolTip as ToolTip
            ?? throw new InvalidOperationException("link URL is not hosted in a ToolTip control");
        var style = resources[typeof(ToolTip)] as Style
            ?? throw new InvalidOperationException("themed ToolTip style was not registered");

        toolTip.Style = style;
        toolTip.ApplyTemplate();

        var foreground = toolTip.Foreground as SolidColorBrush
            ?? throw new InvalidOperationException("tooltip foreground is not a solid theme brush");
        var background = toolTip.Background as SolidColorBrush
            ?? throw new InvalidOperationException("tooltip background is not a solid theme brush");
        Assert(ContrastRatio(foreground.Color, background.Color) >= 4.5, "dark tooltip text has readable contrast");
        var text = toolTip.Template.FindName("ToolTipText", toolTip) as TextBlock
            ?? throw new InvalidOperationException("tooltip text presenter was not created");
        Assert(text.TextWrapping == TextWrapping.Wrap, "long tooltip URLs wrap");
    }

    private static void VerifyTextOnlyContextMenu(ResourceDictionary resources)
    {
        var itemStyle = resources["TextOnlyContextMenuItem"] as Style
            ?? throw new InvalidOperationException("text-only menu item style was not registered");
        var contextMenuStyle = resources[typeof(ContextMenu)] as Style
            ?? throw new InvalidOperationException("context menu style was not registered");
        var itemContainerSetter = contextMenuStyle.Setters
            .OfType<Setter>()
            .SingleOrDefault(setter => setter.Property == ItemsControl.ItemContainerStyleProperty);

        Assert(ReferenceEquals(itemContainerSetter?.Value, itemStyle), "context menus select the text-only item template");

        var contextMenu = new ContextMenu { Style = contextMenuStyle };
        var hostedItem = new MenuItem { Header = "Fork" };
        contextMenu.Items.Add(hostedItem);
        contextMenu.ApplyTemplate();
        contextMenu.Measure(new Size(260, 120));
        contextMenu.Arrange(new Rect(contextMenu.DesiredSize));
        contextMenu.UpdateLayout();

        Assert(contextMenu.OverridesDefaultStyle, "context menus do not use the stock icon-gutter template");
        var contextMenuChrome = contextMenu.Template.FindName("ContextMenuChrome", contextMenu) as Border
            ?? throw new InvalidOperationException("context menu themed surface was not created");
        Assert(
            ReferenceEquals(contextMenuChrome.Background, contextMenu.Background),
            "context menu chrome paints the complete popup background");
        Assert(
            contextMenu.Template.FindName("ContextMenuItemsHost", contextMenu) is ItemsPresenter,
            "context menu owns its complete items host");
        Assert(ReferenceEquals(hostedItem.Style, itemStyle), "hosted menu items receive the text-only style");

        var item = new MenuItem { Header = "Resume", Style = itemStyle };
        item.ApplyTemplate();
        item.Measure(new Size(240, 80));
        item.Arrange(new Rect(item.DesiredSize));
        item.UpdateLayout();

        Assert(item.Template.FindName("MenuItemChrome", item) is Border, "menu item themed surface is present");
        Assert(item.Template.FindName("MenuItemHeader", item) is ContentPresenter, "menu item header is present");
        Assert(!FindVisualDescendants<Grid>(item).Any(), "text-only menu item has no icon gutter grid");
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var lighter = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
        var darker = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color) =>
        0.2126 * LinearChannel(color.R) + 0.7152 * LinearChannel(color.G) + 0.0722 * LinearChannel(color.B);

    private static double LinearChannel(byte channel)
    {
        var value = channel / 255d;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
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
        var actionMenus = FindVisualDescendants<Button>(view)
            .Select(button => button.ContextMenu)
            .OfType<ContextMenu>()
            .ToList();
        Assert(actionMenus.Count >= 2, "project navigation creates project and thread action menus");
        foreach (var actionMenu in actionMenus)
        {
            actionMenu.ApplyTemplate();
            Assert(actionMenu.OverridesDefaultStyle, "navigation action menus use the owned popup chrome");
            Assert(
                actionMenu.Template.FindName("ContextMenuChrome", actionMenu) is Border,
                "navigation action menus paint their complete popup background");
        }

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
        var longLine = string.Join(' ', Enumerable.Repeat("A responsive assistant response with [release notes](https://example.com/releases) must stay inside the transcript column.", 18));
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
        var responseText = FindVisualDescendants<MarkdownTextBlock>(conversationList)
            .Single(block => block.Markdown == tallResponse);

        AssertNear(0, scroller.ScrollableWidth, "transcript has no horizontal scroll extent");
        Assert(responseText.ActualWidth <= scroller.ViewportWidth + 0.5, "assistant response stays within transcript viewport");
        Assert(responseText.ActualHeight > responseText.FontSize * 3, "assistant response wraps to multiple lines");
        Assert(responseText.Inlines.OfType<Hyperlink>().Any(), "assistant response renders a clickable markdown link");
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
