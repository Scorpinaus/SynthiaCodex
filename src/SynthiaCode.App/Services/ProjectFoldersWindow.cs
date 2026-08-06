using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SynthiaCode.Core.Projects;

namespace SynthiaCode.App.Services;

/// <summary>
/// Native project-folder editor. The first folder is the primary Codex working
/// directory; the remaining folders are additional bounded workspace roots.
/// </summary>
public sealed class ProjectFoldersWindow : Window
{
    private readonly IFolderPicker folderPicker;
    private readonly ObservableCollection<ProjectFolderRow> folders;
    private readonly ListBox folderList;
    private readonly Button makePrimaryButton;
    private readonly Button removeButton;
    private readonly TextBlock validationMessage;
    private string primaryPath;

    public ProjectFoldersWindow(RecentProject project, IFolderPicker folderPicker)
    {
        ArgumentNullException.ThrowIfNull(project);
        this.folderPicker = folderPicker ?? throw new ArgumentNullException(nameof(folderPicker));
        primaryPath = Path.GetFullPath(project.Path);
        folders = new ObservableCollection<ProjectFolderRow>(
            project.FolderPaths.Select(path => new ProjectFolderRow(path, ProjectFolderSet.PathsEqual(path, primaryPath))));

        Title = "Edit project folders";
        Width = 700;
        Height = 460;
        MinWidth = 540;
        MinHeight = 360;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetName(this, "Edit project folders");
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "InkBrush");
        NameScope.SetNameScope(this, new NameScope());

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = "Project folders",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold
        };
        root.Children.Add(heading);

        var description = new TextBlock
        {
            Text = "Codex starts in the primary folder. Additional folders are available for file search, reading, and editing.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 16)
        };
        description.SetResourceReference(ForegroundProperty, "MutedInkBrush");
        Grid.SetRow(description, 1);
        root.Children.Add(description);

        folderList = new ListBox
        {
            Name = "ProjectFolderList",
            ItemsSource = folders,
            DisplayMemberPath = nameof(ProjectFolderRow.DisplayText),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = 160
        };
        folderList.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "NavigationRow");
        AutomationProperties.SetName(folderList, "Project folders");
        folderList.SelectionChanged += (_, _) => UpdateActionStates();
        RegisterName(folderList.Name, folderList);
        Grid.SetRow(folderList, 2);
        root.Children.Add(folderList);

        var folderActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var addButton = CreateButton("Add folder", "AddProjectFolderButton", "Add project folder", AddFolder);
        makePrimaryButton = CreateButton("Make primary", "MakePrimaryFolderButton", "Make selected folder primary", MakePrimary);
        removeButton = CreateButton("Remove", "RemoveProjectFolderButton", "Remove selected project folder", RemoveFolder);
        makePrimaryButton.Margin = new Thickness(8, 0, 0, 0);
        removeButton.Margin = new Thickness(8, 0, 0, 0);
        removeButton.SetResourceReference(StyleProperty, "DangerButton");
        folderActions.Children.Add(addButton);
        folderActions.Children.Add(makePrimaryButton);
        folderActions.Children.Add(removeButton);
        Grid.SetRow(folderActions, 3);
        root.Children.Add(folderActions);

        validationMessage = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.IndianRed,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetLiveSetting(validationMessage, AutomationLiveSetting.Assertive);
        Grid.SetRow(validationMessage, 4);
        root.Children.Add(validationMessage);

        var dialogActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 84,
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancelButton.SetResourceReference(StyleProperty, "CompactButton");
        var saveButton = CreateButton("Save", "SaveProjectFoldersButton", "Save project folders", Save);
        saveButton.IsDefault = true;
        saveButton.MinWidth = 88;
        saveButton.SetResourceReference(StyleProperty, "PrimaryButton");
        dialogActions.Children.Add(cancelButton);
        dialogActions.Children.Add(saveButton);
        Grid.SetRow(dialogActions, 5);
        root.Children.Add(dialogActions);

        Content = root;
        folderList.SelectedIndex = 0;
        UpdateActionStates();
    }

    public ProjectFolderEditSelection? Selection { get; private set; }

    private Button CreateButton(string content, string name, string automationName, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Name = name,
            Content = content,
            MinWidth = 92
        };
        button.SetResourceReference(StyleProperty, "CompactButton");
        AutomationProperties.SetName(button, automationName);
        button.Click += handler;
        RegisterName(name, button);
        return button;
    }

    private void AddFolder(object sender, RoutedEventArgs args)
    {
        var path = folderPicker.PickFolder(primaryPath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        path = Path.GetFullPath(path);
        if (folders.Any(folder => ProjectFolderSet.PathsEqual(folder.Path, path)))
        {
            ShowValidation("That folder is already attached to this project.");
            return;
        }

        var added = new ProjectFolderRow(path, false);
        folders.Add(added);
        folderList.SelectedItem = added;
        ClearValidation();
    }

    private void MakePrimary(object sender, RoutedEventArgs args)
    {
        if (folderList.SelectedItem is not ProjectFolderRow selected)
        {
            return;
        }

        primaryPath = selected.Path;
        RefreshRows(selected.Path);
        ClearValidation();
    }

    private void RemoveFolder(object sender, RoutedEventArgs args)
    {
        if (folderList.SelectedItem is not ProjectFolderRow selected || selected.IsPrimary)
        {
            return;
        }

        var index = folderList.SelectedIndex;
        folders.Remove(selected);
        folderList.SelectedIndex = Math.Min(index, folders.Count - 1);
        ClearValidation();
    }

    private void Save(object sender, RoutedEventArgs args)
    {
        var unavailable = folders.FirstOrDefault(folder => !Directory.Exists(folder.Path));
        if (unavailable is not null)
        {
            ShowValidation($"This folder is unavailable: {unavailable.Path}");
            folderList.SelectedItem = unavailable;
            return;
        }

        var paths = ProjectFolderSet.NormalizePersisted(primaryPath, folders.Select(folder => folder.Path));
        Selection = new ProjectFolderEditSelection(primaryPath, paths);
        DialogResult = true;
    }

    private void RefreshRows(string selectedPath)
    {
        var paths = folders.Select(folder => folder.Path).ToList();
        folders.Clear();
        foreach (var path in ProjectFolderSet.NormalizePersisted(primaryPath, paths))
        {
            folders.Add(new ProjectFolderRow(path, ProjectFolderSet.PathsEqual(path, primaryPath)));
        }
        folderList.SelectedItem = folders.First(folder => ProjectFolderSet.PathsEqual(folder.Path, selectedPath));
    }

    private void UpdateActionStates()
    {
        var selected = folderList.SelectedItem as ProjectFolderRow;
        makePrimaryButton.IsEnabled = selected is not null && !selected.IsPrimary;
        removeButton.IsEnabled = selected is not null && !selected.IsPrimary && folders.Count > 1;
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

    private sealed record ProjectFolderRow(string Path, bool IsPrimary)
    {
        public string DisplayText => IsPrimary ? $"{Path}  (Primary)" : Path;
    }
}
