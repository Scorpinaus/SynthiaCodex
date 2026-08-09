using System.Collections.ObjectModel;
using SynthiaCode.Core.Git;

namespace SynthiaCode.App.ViewModels;

public sealed class GitRepositorySelectionViewModel : ObservableObject
{
    private readonly GitViewModel owner;

    internal GitRepositorySelectionViewModel(GitViewModel owner)
    {
        this.owner = owner;
        owner.PropertyChanged += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.PropertyName))
            {
                OnPropertyChanged(args.PropertyName);
            }
        };
    }

    public ObservableCollection<GitRepositoryOption> Repositories => owner.Repositories;
    public GitRepositoryOption? SelectedRepository
    {
        get => owner.SelectedRepository;
        set => owner.SelectedRepository = value;
    }
    public string? RepositoryRoot => owner.RepositoryRoot;
    public string Branch => owner.Branch;
    public string StatusMessage => owner.StatusMessage;
    public bool IsRepository => owner.IsRepository;
    public bool HasMultipleRepositories => owner.HasMultipleRepositories;
    public bool ShowsRepositorySelector => owner.ShowsRepositorySelector;
}
