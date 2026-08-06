using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Git;

namespace SynthiaCode.App.Services;

/// <summary>Native picker for the four review targets supported by Codex app-server.</summary>
public sealed class CodeReviewWindow : Window
{
    private readonly GitReviewCatalog catalog;
    private readonly RadioButton uncommittedTarget;
    private readonly RadioButton baseBranchTarget;
    private readonly RadioButton commitTarget;
    private readonly RadioButton customTarget;
    private readonly ComboBox baseBranchSelector;
    private readonly ComboBox commitSelector;
    private readonly TextBox customInstructions;
    private readonly TextBlock validationMessage;

    public CodeReviewWindow(GitReviewCatalog catalog)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

        Title = "Start code review";
        Width = 640;
        Height = 570;
        MinWidth = 520;
        MinHeight = 470;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetName(this, "Start code review");
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "InkBrush");
        NameScope.SetNameScope(this, new NameScope());

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock
        {
            Text = "Review code changes",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold
        });

        var description = new TextBlock
        {
            Text = $"Choose what the dedicated Codex reviewer should inspect in {catalog.RepositoryRoot}.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 7, 0, 16)
        };
        description.SetResourceReference(ForegroundProperty, "MutedInkBrush");
        Grid.SetRow(description, 1);
        root.Children.Add(description);

        var targets = new StackPanel();
        uncommittedTarget = CreateRadio(
            "Uncommitted changes",
            "UncommittedReviewTarget",
            "Review staged, unstaged, and untracked changes");
        uncommittedTarget.IsChecked = true;
        targets.Children.Add(CreateTargetCard(
            uncommittedTarget,
            "Includes staged, unstaged, and untracked files.",
            null));

        baseBranchSelector = new ComboBox
        {
            Name = "BaseBranchSelector",
            ItemsSource = catalog.BaseBranches,
            SelectedIndex = catalog.BaseBranches.Count > 0 ? 0 : -1,
            MinHeight = 32,
            Margin = new Thickness(0, 7, 0, 0),
            IsEnabled = false
        };
        AutomationProperties.SetName(baseBranchSelector, "Base branch");
        RegisterName(baseBranchSelector.Name, baseBranchSelector);
        baseBranchTarget = CreateRadio(
            "Changes against a base branch",
            "BaseBranchReviewTarget",
            "Review changes against a base branch");
        targets.Children.Add(CreateTargetCard(
            baseBranchTarget,
            "Codex finds the merge base and reviews the current branch diff.",
            baseBranchSelector));

        commitSelector = new ComboBox
        {
            Name = "CommitSelector",
            ItemsSource = catalog.Commits,
            DisplayMemberPath = nameof(GitReviewCommit.DisplayLabel),
            SelectedIndex = catalog.Commits.Count > 0 ? 0 : -1,
            MinHeight = 32,
            Margin = new Thickness(0, 7, 0, 0),
            IsEnabled = false
        };
        AutomationProperties.SetName(commitSelector, "Commit to review");
        RegisterName(commitSelector.Name, commitSelector);
        commitTarget = CreateRadio(
            "A specific commit",
            "CommitReviewTarget",
            "Review a specific commit");
        targets.Children.Add(CreateTargetCard(
            commitTarget,
            "Reviews the exact change set introduced by the selected commit.",
            commitSelector));

        customInstructions = new TextBox
        {
            Name = "CustomReviewInstructions",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 72,
            MaxHeight = 120,
            Margin = new Thickness(0, 7, 0, 0),
            Padding = new Thickness(8, 6, 8, 6),
            IsEnabled = false
        };
        AutomationProperties.SetName(customInstructions, "Custom review instructions");
        RegisterName(customInstructions.Name, customInstructions);
        customTarget = CreateRadio(
            "Custom review instructions",
            "CustomReviewTarget",
            "Use custom review instructions");
        targets.Children.Add(CreateTargetCard(
            customTarget,
            "Focus the reviewer on criteria you provide.",
            customInstructions));

        foreach (var target in new[] { uncommittedTarget, baseBranchTarget, commitTarget, customTarget })
        {
            target.Checked += OnTargetChanged;
        }

        var scroll = new ScrollViewer
        {
            Content = targets,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        validationMessage = new TextBlock
        {
            Foreground = Brushes.IndianRed,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetLiveSetting(validationMessage, AutomationLiveSetting.Assertive);
        Grid.SetRow(validationMessage, 3);
        root.Children.Add(validationMessage);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 84,
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancel.SetResourceReference(StyleProperty, "CompactButton");
        var start = new Button
        {
            Name = "StartCodeReviewButton",
            Content = "Start review",
            IsDefault = true,
            MinWidth = 104
        };
        start.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetName(start, "Start code review");
        start.Click += StartReview;
        RegisterName(start.Name, start);
        actions.Children.Add(cancel);
        actions.Children.Add(start);
        Grid.SetRow(actions, 4);
        root.Children.Add(actions);

        Content = root;
        Loaded += (_, _) => uncommittedTarget.Focus();
        UpdateTargetControls(moveFocus: false);
    }

    public CodexReviewTarget? Selection { get; private set; }

    private RadioButton CreateRadio(string content, string name, string automationName)
    {
        var radio = new RadioButton
        {
            Name = name,
            Content = content,
            GroupName = "ReviewTarget",
            FontWeight = FontWeights.SemiBold
        };
        AutomationProperties.SetName(radio, automationName);
        RegisterName(name, radio);
        return radio;
    }

    private static Border CreateTargetCard(RadioButton target, string description, Control? input)
    {
        var content = new StackPanel();
        content.Children.Add(target);
        var detail = new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(22, 4, 0, 0)
        };
        detail.SetResourceReference(ForegroundProperty, "MutedInkBrush");
        content.Children.Add(detail);
        if (input is not null)
        {
            content.Children.Add(input);
        }

        var card = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 9),
            Child = content
        };
        card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        card.SetResourceReference(Border.BackgroundProperty, "FieldBrush");
        return card;
    }

    private void OnTargetChanged(object sender, RoutedEventArgs e)
    {
        ClearValidation();
        UpdateTargetControls(moveFocus: IsLoaded);
    }

    private void UpdateTargetControls(bool moveFocus)
    {
        baseBranchSelector.IsEnabled = baseBranchTarget.IsChecked == true;
        commitSelector.IsEnabled = commitTarget.IsChecked == true;
        customInstructions.IsEnabled = customTarget.IsChecked == true;
        if (!moveFocus)
        {
            return;
        }

        if (baseBranchSelector.IsEnabled)
        {
            baseBranchSelector.Focus();
        }
        else if (commitSelector.IsEnabled)
        {
            commitSelector.Focus();
        }
        else if (customInstructions.IsEnabled)
        {
            customInstructions.Focus();
        }
    }

    private void StartReview(object sender, RoutedEventArgs args)
    {
        if (uncommittedTarget.IsChecked == true)
        {
            Selection = CodexReviewTarget.UncommittedChanges();
        }
        else if (baseBranchTarget.IsChecked == true)
        {
            if (baseBranchSelector.SelectedItem is not string branch)
            {
                ShowValidation("No alternate base branch is available in this repository.");
                return;
            }
            Selection = CodexReviewTarget.BaseBranch(branch);
        }
        else if (commitTarget.IsChecked == true)
        {
            if (commitSelector.SelectedItem is not GitReviewCommit commit)
            {
                ShowValidation("No commit is available to review.");
                return;
            }
            Selection = CodexReviewTarget.Commit(commit.Sha, commit.Title);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(customInstructions.Text))
            {
                ShowValidation("Enter custom review instructions.");
                customInstructions.Focus();
                return;
            }
            Selection = CodexReviewTarget.Custom(customInstructions.Text);
        }

        DialogResult = true;
    }

    private void ShowValidation(string message)
    {
        validationMessage.Text = message;
        validationMessage.Visibility = Visibility.Visible;
    }

    private void ClearValidation()
    {
        validationMessage.Text = string.Empty;
        validationMessage.Visibility = Visibility.Collapsed;
    }
}
