using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using SynthiaCode.App;
using SynthiaCode.App.Controls;
using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;
using SynthiaCode.App.Views;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Projects;
using SynthiaCode.Core.Settings;

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
        VerifyWindowCloseIsDeferred();
        VerifyCollapsibleNavigationSections();
        VerifyProjectNavigationWraps();
        VerifyTranscriptWrapsAndScrolls();
        VerifyQueuedFollowUpControls();
        VerifyInstructionSettingsSurface();
    });

    private static void VerifyInstructionSettingsSurface()
    {
        var view = new DetailsView
        {
            Width = 340,
            Height = 760
        };

        PumpLayout(view);

        var developerEditor = view.FindName("DeveloperInstructionsEditor") as TextBox;
        var baseEditor = view.FindName("BaseInstructionsEditor") as TextBox;
        var saveButton = view.FindName("SaveInstructionSettingsButton") as Button;
        var resetButton = view.FindName("ResetInstructionSettingsButton") as Button;
        Assert(developerEditor is { AcceptsReturn: true, TextWrapping: TextWrapping.Wrap },
            "settings expose a multiline developer instructions editor");
        Assert(baseEditor is { AcceptsReturn: true, TextWrapping: TextWrapping.Wrap },
            "settings expose a multiline base instructions editor");
        Assert(saveButton is not null && resetButton is not null,
            "settings expose explicit save and reset instruction actions");
        Assert(AutomationProperties.GetName(developerEditor) == "Developer instructions",
            "developer instructions editor has an accessible name");
        Assert(AutomationProperties.GetName(baseEditor) == "Base instructions override",
            "base instructions editor has an accessible name");
    }

    private static void ApplyDarkThemeForTest(ResourceDictionary resources)
    {
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "/SynthiaCode.App;component/Themes/DarkTheme.xaml",
                UriKind.Relative)
        });
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "/SynthiaCode.App;component/Themes/TransientSurfaces.xaml",
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
        resources["ValueText"] = TextStyle(fontSize: 11, wraps: true);
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
        var implicitMenuItemStyle = resources[typeof(MenuItem)] as Style
            ?? throw new InvalidOperationException("implicit context-menu item style was not registered");
        Assert(
            ReferenceEquals(implicitMenuItemStyle.BasedOn, itemStyle),
            "menu items inherit the text-only item template without styling separators");
        Assert(
            contextMenuStyle.Setters.OfType<Setter>().All(setter => setter.Property != ItemsControl.ItemContainerStyleProperty),
            "context menus do not force a MenuItem style onto mixed item containers");

        var contextMenu = new ContextMenu { Style = contextMenuStyle };
        var hostedItem = new MenuItem { Header = "Fork" };
        var separator = new Separator();
        contextMenu.Items.Add(hostedItem);
        contextMenu.Items.Add(separator);
        contextMenu.Items.Add(new MenuItem { Header = "Delete" });
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
        Assert(ReferenceEquals(hostedItem.Style, implicitMenuItemStyle), "hosted menu items receive the text-only style");
        Assert(
            separator.Style is null || separator.Style.TargetType == typeof(Separator),
            "separators never receive a MenuItem-targeted style");

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

    private static void VerifyCollapsibleNavigationSections()
    {
        var workspace = CreateProjectViewModel();
        var generalChat = new ProjectThreadState
        {
            ScopeKind = ThreadScopeKind.General,
            ThreadId = "general-chat",
            Title = "General chat"
        };
        workspace.RefreshProjectNavigation(
            [new RecentProject(@"C:\Work\CollapsibleProject", "Collapsible project", DateTimeOffset.UtcNow)],
            [generalChat]);

        var view = new ProjectThreadView
        {
            DataContext = new ProjectContext(workspace),
            Width = 268,
            Height = 420
        };

        PumpLayout(view);
        var searchBox = view.FindName("ChatSearchBox") as TextBox
            ?? throw new InvalidOperationException("cross-chat search box was not created");
        Assert(
            AutomationProperties.GetName(searchBox) == "Search all chats",
            "cross-chat search is exposed to automation");
        var text = FindVisualDescendants<TextBlock>(view).Select(block => block.Text).ToList();
        Assert(text.Contains("Chats"), "General navigation is labelled Chats");
        Assert(!text.Contains("General"), "the legacy General section label is absent");

        var chatsToggle = FindVisualDescendants<Button>(view)
            .Single(button => AutomationProperties.GetName(button) == "Toggle Chats");
        var projectsToggle = FindVisualDescendants<Button>(view)
            .Single(button => AutomationProperties.GetName(button) == "Toggle Projects");
        var chatsList = FindVisualDescendants<ListBox>(view)
            .Single(listBox => ReferenceEquals(listBox.ItemsSource, workspace.GeneralThreads));
        var projectsList = FindVisualDescendants<ListBox>(view)
            .Single(listBox => ReferenceEquals(listBox.ItemsSource, workspace.Projects));

        Assert(chatsList.Visibility == Visibility.Visible, "Chats content starts visible");
        Assert(projectsList.Visibility == Visibility.Visible, "Projects content starts visible");
        Assert(ReferenceEquals(chatsToggle.Command, workspace.ToggleChatsCommand), "Chats header is wired to the Chats toggle command");
        Assert(ReferenceEquals(projectsToggle.Command, workspace.ToggleProjectsCommand), "Projects header is wired to the Projects toggle command");

        chatsToggle.Command.Execute(null);
        PumpLayout(view);
        Assert(chatsList.Visibility == Visibility.Collapsed, "Chats header collapses its chat list");
        Assert(projectsList.Visibility == Visibility.Visible, "Chats collapse leaves Projects visible");

        projectsToggle.Command.Execute(null);
        PumpLayout(view);
        Assert(projectsList.Visibility == Visibility.Collapsed, "Projects header collapses its project list");
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

        var projectList = FindVisualDescendants<ListBox>(view)
            .First(listBox => ReferenceEquals(listBox.ItemsSource, workspace.Projects));
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
        var longActivity = string.Join(' ', Enumerable.Repeat("Complete activity details must remain visible inside the assistant message.", 20));
        var table = "| Model | Availability | Best suited for |\n|---|---|---|\n| **Qwen3.7-Max** | Hosted/API | Long-running agents |";
        var tallResponse = $"{table}{Environment.NewLine}{Environment.NewLine}{string.Join(Environment.NewLine, Enumerable.Repeat(longLine, 12))}";
        var turn = new CodexConversationTurn
        {
            TurnId = "responsive-turn",
            UserPrompt = longLine,
            AssistantResponse = tallResponse,
            Status = CodexTurnStatus.Completed,
            StartedAt = DateTimeOffset.Parse("2026-07-20T12:43:00+08:00"),
            CompletedAt = DateTimeOffset.Parse("2026-07-20T12:45:00+08:00")
        };
        turn.Activity.Add(new CodexTimelineItem(
            CodexTimelineItemKind.WebSearch,
            "Searched the web",
            longActivity,
            "item/webSearch",
            DateTimeOffset.UtcNow));
        turn.IsActivityExpanded = true;
        var turns = new ObservableCollection<CodexConversationTurn> { turn };
        var view = new TaskView { Width = 620, Height = 520 };
        var conversationList = (ListBox?)view.FindName("ConversationList")
            ?? throw new InvalidOperationException("conversation list was not found");
        var findBox = view.FindName("FindInChatBox") as TextBox
            ?? throw new InvalidOperationException("find-in-chat box was not created");
        Assert(
            AutomationProperties.GetName(findBox) == "Find text in current chat",
            "find-in-chat is exposed to automation");
        conversationList.ItemsSource = turns;
        typeof(TaskView)
            .GetField("observedTurns", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(view, turns);

        PumpLayout(view);
        var scroller = FindVisualDescendant<ScrollViewer>(conversationList)
            ?? throw new InvalidOperationException("conversation scroll viewer was not created");
        var responseText = FindVisualDescendants<MarkdownTextBlock>(conversationList)
            .Single(block => block.Markdown == tallResponse);
        var assistantSurface = FindVisualDescendants<Border>(conversationList)
            .Single(border => AutomationProperties.GetName(border) == "Assistant message");
        var activityExpander = FindVisualDescendants<Expander>(conversationList)
            .Single(expander => AutomationProperties.GetName(expander) == "Turn activity");
        var activityDetail = FindVisualDescendants<TextBlock>(activityExpander)
            .Single(block => block.Text == longActivity);

        AssertNear(0, scroller.ScrollableWidth, "transcript has no horizontal scroll extent");
        Assert(IsVisualDescendantOf(activityExpander, assistantSurface), "turn activity is contained by the assistant message surface");
        Assert(IsVisualDescendantOf(responseText, assistantSurface), "assistant response shares the activity message surface");
        Assert(activityDetail.TextTrimming == TextTrimming.None, "activity details are not visually trimmed");
        Assert(activityDetail.TextWrapping != TextWrapping.NoWrap, "activity details wrap within the assistant message");
        Assert(activityDetail.ActualHeight > activityDetail.FontSize * 3, "complete activity detail occupies multiple wrapped lines");
        Assert(activityDetail.ActualWidth <= assistantSurface.ActualWidth + 0.5, "activity detail stays within the assistant message width");
        Assert(responseText.ActualWidth <= scroller.ViewportWidth + 0.5, "assistant response stays within transcript viewport");
        Assert(responseText.ActualHeight > responseText.FontSize * 3, "assistant response wraps to multiple lines");
        Assert(responseText.Inlines.OfType<Hyperlink>().Any(), "assistant response renders a clickable markdown link");
        var markdownTable = (Grid)responseText.Inlines.OfType<InlineUIContainer>().Single().Child;
        Assert(markdownTable.ActualWidth > 0, "assistant markdown table participates in layout");
        Assert(markdownTable.ActualWidth <= responseText.ActualWidth + 0.5, "assistant markdown table stays within the message width");
        var transcriptText = FindVisualDescendants<TextBlock>(conversationList).ToList();
        var userTimestamp = transcriptText.Single(block =>
            AutomationProperties.GetName(block) == "User message date and time");
        var assistantTimestamp = transcriptText.Single(block =>
            AutomationProperties.GetName(block) == "Assistant message date and time");
        Assert(!string.IsNullOrWhiteSpace(userTimestamp.Text), "user message has its own timestamp footer");
        Assert(!string.IsNullOrWhiteSpace(assistantTimestamp.Text), "assistant message has its own timestamp footer");
        Assert(!transcriptText.Any(block => block.Text is "Turn" or "Completed"), "turn-level metadata is removed");

        var copyButtons = FindVisualDescendants<Button>(conversationList)
            .Where(button => button.Content?.ToString() == "Copy")
            .ToDictionary(AutomationProperties.GetName);
        Assert(copyButtons.Count == 2, "each message has a copy action");
        Assert(Equals(copyButtons["Copy user message"].CommandParameter, longLine), "user copy action targets the user message");
        Assert(Equals(copyButtons["Copy assistant message"].CommandParameter, tallResponse), "assistant copy action targets the assistant message");
        Assert(
            VirtualizingPanel.GetScrollUnit(conversationList) == ScrollUnit.Pixel,
            "transcript uses pixel scrolling for variable-height turns");

        Assert(
            !FindVisualDescendants<Expander>(view).Any(expander => Equals(expander.Header, "Run settings")),
            "Run settings expander is removed");
        var modelOptionsButton = (Button?)view.FindName("ModelOptionsButton")
            ?? throw new InvalidOperationException("compact model options button was not found");
        var executionPolicySelector = (ComboBox?)view.FindName("ExecutionPolicySelector")
            ?? throw new InvalidOperationException("composer execution-policy selector was not found");
        var modelOptionsPopup = (Popup?)view.FindName("ModelOptionsPopup")
            ?? throw new InvalidOperationException("model options popup was not found");
        Assert(modelOptionsButton.MinHeight >= 32, "compact model selector has an accessible target size");
        Assert(executionPolicySelector.MinHeight >= 32, "execution-policy selector has an accessible target size");
        var modelRight = modelOptionsButton.TranslatePoint(new Point(modelOptionsButton.ActualWidth, 0), view).X;
        var policyLeft = executionPolicySelector.TranslatePoint(new Point(0, 0), view).X;
        Assert(policyLeft >= modelRight, "execution-policy selector is positioned to the right of the model selector");
        Assert(modelOptionsPopup.Placement == PlacementMode.Top, "model options open above the composer");
        Assert(!modelOptionsPopup.StaysOpen, "model options close when focus moves away");
        VerifyModelPickerRowsStayCompact(modelOptionsPopup);

        scroller.ScrollToEnd();
        PumpLayout(view);
        Assert(scroller.ScrollableHeight > 0, "compact composer leaves an accessible vertical transcript range");
        AssertNear(scroller.ScrollableHeight, scroller.VerticalOffset, "compact composer preserves the latest response");

        scroller.ScrollToTop();
        PumpLayout(view);
        AssertNear(0, scroller.VerticalOffset, "compact composer preserves a user-scrolled-up position");
    }

    private static void VerifyModelPickerRowsStayCompact(Popup popup)
    {
        var popupContent = popup.Child as FrameworkElement
            ?? throw new InvalidOperationException("model options popup content was not created");
        var modelList = FindVisualDescendants<ListBox>(popupContent)
            .Single(list => AutomationProperties.GetName(list) == "Available models");
        var row = modelList.ItemTemplate.LoadContent() as FrameworkElement
            ?? throw new InvalidOperationException("model picker row template was not created");
        var labels = FindVisualDescendants<TextBlock>(row).ToList();

        Assert(row.MaxHeight <= 48, "model rows remain short enough to expose the complete supported catalog");
        Assert(labels.Count == 2, "model rows contain only a name and concise description");
        Assert(labels.All(label => label.TextWrapping == TextWrapping.NoWrap), "model row text stays on one line");
    }

    private static void VerifyQueuedFollowUpControls()
    {
        var taskView = new TaskView();
        var queuePanel = taskView.FindName("QueuedFollowUpPanel") as FrameworkElement
            ?? throw new InvalidOperationException("queued follow-up panel was not found");
        var queueList = taskView.FindName("QueuedFollowUpList") as ItemsControl
            ?? throw new InvalidOperationException("queued follow-up list was not found");
        var alternateButton = taskView.FindName("AlternateFollowUpButton") as Button
            ?? throw new InvalidOperationException("alternate follow-up action was not found");
        Assert(AutomationProperties.GetName(queuePanel) == "Queued follow-ups", "queue panel has an accessible name");
        Assert(AutomationProperties.GetName(queueList) == "Queued follow-up messages", "queue list has an accessible name");
        Assert(alternateButton.MinHeight >= 32, "alternate follow-up action has an accessible target size");

        var details = new DetailsView();
        var selector = details.FindName("FollowUpBehaviorSelector") as ComboBox
            ?? throw new InvalidOperationException("follow-up behavior setting was not found");
        Assert(AutomationProperties.GetName(selector) == "Follow-up behavior", "follow-up behavior setting has an accessible name");
    }

    private static void VerifyWindowCloseIsDeferred()
    {
        var scheduleClose = typeof(MainWindow).GetMethod(
            "ScheduleClose",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("window close scheduler was not found");
        var invoked = false;
        var operation = scheduleClose.Invoke(
            null,
            [Dispatcher.CurrentDispatcher, new Action(() => invoked = true)]) as DispatcherOperation
            ?? throw new InvalidOperationException("window close scheduler did not return a dispatcher operation");

        Assert(!invoked, "window close is not re-entered from inside the Closing handler");
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        Assert(operation.Status == DispatcherOperationStatus.Completed && invoked, "deferred window close runs on the dispatcher");
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
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => true,
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

    private static bool IsVisualDescendantOf(DependencyObject descendant, DependencyObject ancestor)
    {
        for (var current = VisualTreeHelper.GetParent(descendant); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
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
