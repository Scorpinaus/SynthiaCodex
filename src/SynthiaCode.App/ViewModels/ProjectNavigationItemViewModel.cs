using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using SynthiaCode.Core.Projects;
using SynthiaCode.Core.Settings;

namespace SynthiaCode.App.ViewModels;

public sealed class ProjectNavigationItemViewModel : ObservableObject, IDisposable
{
    private bool isExpanded;
    private bool isSelected;

    public ProjectNavigationItemViewModel(RecentProject project)
    {
        Project = project;
    }

    public RecentProject Project { get; private set; }

    public string Path => Project.Path;

    public string Name => Project.Name;

    public ObservableCollection<ProjectThreadState> Threads { get; } = [];

    public int ThreadCount => Threads.Count;

    public bool HasThreads => Threads.Count > 0;

    public bool HasRunningThreads => Threads.Any(thread => thread.IsRunning);

    public string RunningSummary
    {
        get
        {
            var count = Threads.Count(thread => thread.IsRunning);
            return count == 1 ? "1 running" : $"{count} running";
        }
    }

    public string Chevron => IsExpanded ? "▾" : "▸";

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (SetProperty(ref isExpanded, value))
            {
                OnPropertyChanged(nameof(Chevron));
            }
        }
    }

    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }

    public void Update(RecentProject project, IEnumerable<ProjectThreadState> threads)
    {
        Project = project;
        OnPropertyChanged(nameof(Project));
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(Name));

        foreach (var thread in Threads)
        {
            thread.PropertyChanged -= OnThreadPropertyChanged;
        }

        Threads.Clear();
        foreach (var thread in threads.OrderByDescending(thread => thread.IsPinned).ThenByDescending(thread => thread.UpdatedAt))
        {
            Threads.Add(thread);
            thread.PropertyChanged += OnThreadPropertyChanged;
        }

        OnPropertyChanged(nameof(ThreadCount));
        OnPropertyChanged(nameof(HasThreads));
        OnPropertyChanged(nameof(HasRunningThreads));
        OnPropertyChanged(nameof(RunningSummary));
    }

    public void Dispose()
    {
        foreach (var thread in Threads)
        {
            thread.PropertyChanged -= OnThreadPropertyChanged;
        }
    }

    public static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(System.IO.Path.GetFullPath(left), System.IO.Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private void OnThreadPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ProjectThreadState.IsRunning) or nameof(ProjectThreadState.TurnStatus) or nameof(ProjectThreadState.IsArchived))
        {
            OnPropertyChanged(nameof(HasRunningThreads));
            OnPropertyChanged(nameof(RunningSummary));
        }
    }
}
