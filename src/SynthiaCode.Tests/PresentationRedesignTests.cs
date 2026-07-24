using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

internal static partial class PresentationRedesignTests
{
    private static readonly string[] RequiredThemeKeys =
    [
        "AppCanvasBrush",
        "SurfaceBrush",
        "SurfaceRaisedBrush",
        "SurfaceSunkenBrush",
        "RailBrush",
        "InspectorBrush",
        "TextPrimaryBrush",
        "TextSecondaryBrush",
        "TextTertiaryBrush",
        "TextOnAccentBrush",
        "BorderSubtleBrush",
        "BorderStrongBrush",
        "FocusRingBrush",
        "DividerBrush",
        "SelectionBrush",
        "SelectionIndicatorBrush",
        "ActionAccentBrush",
        "ActionAccentHoverBrush",
        "ActionAccentPressedBrush",
        "ActionAccentSubtleBrush",
        "SuccessBrush",
        "WarningBrush",
        "DangerBrush",
        "InfoBrush",
        "SuccessSubtleBrush",
        "WarningSubtleBrush",
        "DangerSubtleBrush",
        "InfoSubtleBrush"
    ];

    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("redesign themes expose complete semantic resources", ThemesExposeSemanticResourcesAsync),
        ("application resources are composed by design-system concern", ApplicationResourcesAreComposedAsync),
        ("shared controls expose modern reusable primitives", SharedControlsExposeReusablePrimitivesAsync),
        ("window shell uses custom chrome and adaptive docked regions", WindowShellUsesAdaptiveRegionsAsync),
        ("feature surfaces follow the presentation accessibility contract", FeatureSurfacesFollowContractAsync),
        ("redesign preserves presentation performance guardrails", PerformanceGuardrailsAsync),
        ("redesign phase ledger records completed implementation slices", PhaseLedgerRecordsCompletedSlicesAsync)
    ];

    private static Task ThemesExposeSemanticResourcesAsync() => WpfTestHost.RunAsync(() =>
    {
        var dark = LoadDictionary("Themes/DarkTheme.xaml");
        var light = LoadDictionary("Themes/LightTheme.xaml");
        var highContrast = LoadDictionary("Themes/HighContrastTheme.xaml");

        foreach (var key in RequiredThemeKeys)
        {
            Assert(dark.Contains(key), $"dark theme contains {key}");
            Assert(light.Contains(key), $"light theme contains {key}");
            Assert(highContrast.Contains(key), $"high contrast theme contains {key}");
            Assert(dark[key] is Brush, $"dark {key} resolves to a brush");
            Assert(light[key] is Brush, $"light {key} resolves to a brush");
            Assert(highContrast[key] is Brush, $"high contrast {key} resolves to a brush");
        }

        AssertColor(dark, "AppCanvasBrush", "#0D0F12");
        AssertColor(dark, "RailBrush", "#111318");
        AssertColor(dark, "SurfaceBrush", "#16191F");
        AssertColor(dark, "SurfaceRaisedBrush", "#1D2129");
        AssertColor(dark, "SelectionBrush", "#252A33");
        AssertColor(dark, "TextPrimaryBrush", "#F1F3F5");
        AssertColor(dark, "TextSecondaryBrush", "#9DA5B1");
        AssertColor(dark, "BorderSubtleBrush", "#2C323C");
        AssertColor(dark, "SelectionIndicatorBrush", "#D4D8DE");
        AssertColor(dark, "ActionAccentBrush", "#18A77B");
        AssertColor(dark, "FocusRingBrush", "#45C997");

        foreach (var compatibilityKey in new[] { "CanvasBrush", "PanelBrush", "InkBrush", "MutedInkBrush", "LineBrush", "SignalBrush" })
        {
            Assert(dark.Contains(compatibilityKey), $"dark theme retains owned compatibility alias {compatibilityKey}");
            Assert(light.Contains(compatibilityKey), $"light theme retains owned compatibility alias {compatibilityKey}");
        }
    });

    private static Task ApplicationResourcesAreComposedAsync()
    {
        var app = ReadAppFile("App.xaml");
        var expectedSources = new[]
        {
            "Themes/LightTheme.xaml",
            "Themes/Foundations.xaml",
            "Themes/Typography.xaml",
            "Themes/Icons.xaml",
            "Themes/Controls.Buttons.xaml",
            "Themes/Controls.Inputs.xaml",
            "Themes/Controls.Navigation.xaml",
            "Themes/Controls.Transient.xaml"
        };

        foreach (var source in expectedSources)
        {
            Assert(app.Contains($"Source=\"{source}\"", StringComparison.Ordinal), $"App composes {source}");
        }

        Assert(!app.Contains("<ControlTemplate", StringComparison.Ordinal), "App.xaml no longer owns control templates");
        Assert(!HexColorRegex().IsMatch(app), "App.xaml contains no literal palette colors");
        return Task.CompletedTask;
    }

    private static Task SharedControlsExposeReusablePrimitivesAsync()
    {
        var foundations = ReadAppFile("Themes", "Foundations.xaml");
        var buttons = ReadAppFile("Themes", "Controls.Buttons.xaml");
        var inputs = ReadAppFile("Themes", "Controls.Inputs.xaml");
        var navigation = ReadAppFile("Themes", "Controls.Navigation.xaml");
        var icons = ReadAppFile("Themes", "Icons.xaml");
        var transient = ReadAppFile("Themes", "Controls.Transient.xaml");
        var themeService = ReadAppFile("Services", "WpfThemeService.cs");

        foreach (var token in new[] { "Space2", "Space4", "Space8", "Space12", "Space16", "Radius4", "Radius8", "ControlHeightCompact", "ControlHeightStandard", "MotionFast", "MotionStandard" })
        {
            Assert(foundations.Contains($"x:Key=\"{token}\"", StringComparison.Ordinal), $"foundation token {token} exists");
        }

        foreach (var style in new[] { "IconButton", "PrimaryButton", "SecondaryButton", "SubtleButton", "DangerButton", "CompactButton", "FocusVisual" })
        {
            Assert(buttons.Contains($"x:Key=\"{style}\"", StringComparison.Ordinal), $"button primitive {style} exists");
        }

        foreach (var primitive in new[] { "SearchBox", "PaneHeader", "PaneSurface", "NavigationRow", "StatusPill", "DrawerSurface", "EmptyStatePresenter" })
        {
            Assert(
                inputs.Contains($"x:Key=\"{primitive}\"", StringComparison.Ordinal) ||
                navigation.Contains($"x:Key=\"{primitive}\"", StringComparison.Ordinal),
                $"shared primitive {primitive} exists");
        }

        foreach (var icon in new[] { "IconMenu", "IconAdd", "IconSearch", "IconTerminal", "IconChanges", "IconSettings", "IconClose", "IconMinimize", "IconMaximize", "IconSend" })
        {
            Assert(icons.Contains($"x:Key=\"{icon}\"", StringComparison.Ordinal), $"geometry resource {icon} exists");
        }

        Assert(transient.Contains("TargetType=\"{x:Type ContextMenu}\"", StringComparison.Ordinal), "context menus own themed chrome");
        Assert(transient.Contains("TargetType=\"{x:Type ToolTip}\"", StringComparison.Ordinal), "tooltips own themed chrome");
        Assert(transient.Contains("TargetType=\"{x:Type Popup}\"", StringComparison.Ordinal), "popup defaults are theme aware");
        Assert(themeService.Contains("SystemParameters.HighContrast", StringComparison.Ordinal), "system high contrast selects the system-color palette");
        Assert(themeService.Contains("SystemEvents.UserPreferenceChanged", StringComparison.Ordinal), "system theme changes are observed while the app is running");
        return Task.CompletedTask;
    }

    private static Task WindowShellUsesAdaptiveRegionsAsync()
    {
        var shell = ReadAppFile("MainWindow.xaml");
        var codeBehind = ReadAppFile("MainWindow.xaml.cs");

        Assert(shell.Contains("<WindowChrome", StringComparison.Ordinal), "shell configures WindowChrome");
        Assert(shell.Contains("WindowStyle=\"None\"", StringComparison.Ordinal), "stock window chrome is replaced");
        Assert(shell.Contains("x:Name=\"ProjectRailRegion\"", StringComparison.Ordinal), "persistent project rail region exists");
        Assert(shell.Contains("x:Name=\"ConversationWorkspaceRegion\"", StringComparison.Ordinal), "conversation workspace region exists");
        Assert(shell.Contains("x:Name=\"TerminalDockRegion\"", StringComparison.Ordinal), "terminal is a lower dock");
        Assert(shell.Contains("x:Name=\"InspectorRegion\"", StringComparison.Ordinal), "persistent inspector region exists");
        Assert(shell.Contains("x:Name=\"CompactProjectRailDrawer\"", StringComparison.Ordinal), "compact rail drawer exists");
        Assert(shell.Contains("x:Name=\"CompactInspectorDrawer\"", StringComparison.Ordinal), "compact inspector drawer exists");
        Assert(shell.Contains("Panel.ZIndex=\"1000\"", StringComparison.Ordinal), "approval overlay stays above drawers and popups");
        Assert(!shell.Contains("SelectedWorkspaceTabIndex", StringComparison.Ordinal), "shell no longer navigates primary workspaces through tabs");
        Assert(codeBehind.Contains("WM_NCHITTEST", StringComparison.Ordinal), "custom caption preserves native hit testing");
        Assert(codeBehind.Contains("SystemCommands.ShowSystemMenu", StringComparison.Ordinal), "title bar exposes the native system menu");
        return Task.CompletedTask;
    }

    private static Task FeatureSurfacesFollowContractAsync()
    {
        var featureFiles = new[]
        {
            "MainWindow.xaml",
            Path.Combine("Views", "ProjectThreadView.xaml"),
            Path.Combine("Views", "TaskView.xaml"),
            Path.Combine("Views", "ApprovalPromptView.xaml"),
            Path.Combine("Views", "TerminalView.xaml"),
            Path.Combine("Views", "GitView.xaml"),
            Path.Combine("Views", "DetailsView.xaml"),
            Path.Combine("Views", "UserAccountView.xaml")
        };

        foreach (var relativePath in featureFiles)
        {
            var source = ReadAppFile(relativePath.Split(Path.DirectorySeparatorChar));
            Assert(!HexColorRegex().IsMatch(source), $"{relativePath} contains no literal theme color");
        }

        var navigation = ReadAppFile("Views", "ProjectThreadView.xaml");
        Assert(navigation.Contains("AutomationProperties.Name=\"Search chats and projects\"", StringComparison.Ordinal), "navigation search is accessible");
        Assert(navigation.Contains("VirtualizingStackPanel.VirtualizationMode=\"Recycling\"", StringComparison.Ordinal), "navigation retains recycling virtualization");

        var task = ReadAppFile("Views", "TaskView.xaml");
        Assert(task.Contains("AutomationProperties.Name=\"Conversation transcript\"", StringComparison.Ordinal), "transcript has an automation name");
        Assert(task.Contains("AutomationProperties.Name=\"Message composer\"", StringComparison.Ordinal), "composer has an automation name");
        Assert(task.Contains("VirtualizingStackPanel.IsVirtualizing=\"True\"", StringComparison.Ordinal), "conversation virtualization remains enabled");

        var approval = ReadAppFile("Views", "ApprovalPromptView.xaml");
        Assert(approval.Contains("KeyboardNavigation.TabNavigation=\"Cycle\"", StringComparison.Ordinal), "approval content contains keyboard focus");
        Assert(approval.Contains("IsDefault=\"False\"", StringComparison.Ordinal), "approval actions do not accept accidental default Enter");

        var terminal = ReadAppFile("Views", "TerminalView.xaml");
        Assert(terminal.Contains("AutomationProperties.Name=\"Terminal output\"", StringComparison.Ordinal), "terminal output is accessible");
        Assert(terminal.Contains("Background=\"{DynamicResource TerminalBrush}\"", StringComparison.Ordinal), "terminal uses its dedicated dark surface");
        Assert(terminal.Contains("Terminal.ToggleMaximizeCommand", StringComparison.Ordinal), "terminal exposes maximize-within-workspace");
        Assert(terminal.Contains("Terminal.ToggleCommand", StringComparison.Ordinal), "terminal dock exposes a close action");
        return Task.CompletedTask;
    }

    private static Task PhaseLedgerRecordsCompletedSlicesAsync()
    {
        var parity = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "feature_parity.md"));
        Assert(parity.Contains("## Modern WPF redesign implementation parity", StringComparison.Ordinal), "feature parity contains redesign ledger");
        for (var phase = 0; phase <= 11; phase++)
        {
            Assert(
                Regex.IsMatch(parity, $@"\|\s*Phase {phase}\s*\|[^\r\n]*\|\s*\*\*Complete\*\*\s*\|"),
                $"feature parity marks Phase {phase} complete");
        }

        return Task.CompletedTask;
    }

    private static Task PerformanceGuardrailsAsync()
    {
        var task = ReadAppFile("Views", "TaskView.xaml");
        var navigation = ReadAppFile("Views", "ProjectThreadView.xaml");
        var shell = ReadAppFile("MainWindow.xaml");
        var terminalViewModel = ReadAppFile("ViewModels", "TerminalViewModel.cs");
        var combined = string.Concat(task, navigation, shell);

        Assert(!combined.Contains("DropShadowEffect", StringComparison.Ordinal), "large scrolling surfaces avoid drop shadows");
        Assert(!combined.Contains("LayoutTransform", StringComparison.Ordinal), "scrolling surfaces avoid layout transforms");
        Assert(task.Contains("VirtualizingStackPanel.VirtualizationMode=\"Recycling\"", StringComparison.Ordinal), "transcript keeps recycling virtualization");
        Assert(navigation.Contains("VirtualizingStackPanel.VirtualizationMode=\"Recycling\"", StringComparison.Ordinal), "navigation keeps recycling virtualization");
        Assert(shell.Contains("ResizeDirection=\"Rows\"", StringComparison.Ordinal), "terminal resize stays native rather than animated");
        Assert(terminalViewModel.Contains("TimeSpan.FromMilliseconds(50)", StringComparison.Ordinal), "terminal presentation batching remains bounded");
        return Task.CompletedTask;
    }

    private static ResourceDictionary LoadDictionary(string relativeUri) => new()
    {
        Source = new Uri($"/SynthiaCode.App;component/{relativeUri}", UriKind.Relative)
    };

    private static void AssertColor(ResourceDictionary dictionary, string key, string expected)
    {
        var brush = dictionary[key] as SolidColorBrush
            ?? throw new InvalidOperationException($"{key} is not a solid color brush.");
        var expectedColor = (Color)ColorConverter.ConvertFromString(expected);
        Assert(brush.Color == expectedColor, $"{key} is {expected}, actual {brush.Color}");
    }

    private static string ReadAppFile(params string[] relativeSegments) =>
        File.ReadAllText(Path.Combine([FindRepositoryRoot(), "src", "SynthiaCode.App", .. relativeSegments]));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SynthiaCode.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    [GeneratedRegex("""#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?""")]
    private static partial Regex HexColorRegex();
}
