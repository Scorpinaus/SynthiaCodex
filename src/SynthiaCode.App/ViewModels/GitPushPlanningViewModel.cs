using System.IO;

namespace SynthiaCode.App.ViewModels;

public sealed class GitPushPlanningViewModel
{
    public bool CanPush(
        bool isShuttingDown,
        bool isBusy,
        string? repositoryRoot,
        GitRepositoryOption? selectedRepository) =>
        !isShuttingDown &&
        !isBusy &&
        !string.IsNullOrWhiteSpace(repositoryRoot) &&
        selectedRepository?.State.HasNamedBranch == true;

    public bool TargetMatchesDisplay(
        GitRepositoryOption? selectedRepository,
        string root,
        string branchName) =>
        selectedRepository is { State.HasNamedBranch: true } selected &&
        PathsEqual(selected.RootPath, root) &&
        string.Equals(selected.State.Branch, branchName, StringComparison.Ordinal);

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
