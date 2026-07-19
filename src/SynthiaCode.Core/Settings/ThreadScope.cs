using System.Text.Json.Serialization;

namespace SynthiaCode.Core.Settings;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ThreadScopeKind
{
    Project,
    General
}

public readonly record struct ThreadScopeKey
{
    private ThreadScopeKey(ThreadScopeKind kind, string? projectPath)
    {
        Kind = kind;
        ProjectPath = projectPath;
    }

    public ThreadScopeKind Kind { get; }

    public string? ProjectPath { get; }

    public static ThreadScopeKey General { get; } = new(ThreadScopeKind.General, null);

    public static ThreadScopeKey ForProject(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new ArgumentException("Project path is required for a project thread scope.", nameof(projectPath));
        }

        return new ThreadScopeKey(ThreadScopeKind.Project, Path.GetFullPath(projectPath));
    }

    public static ThreadScopeKey From(ThreadScopeKind kind, string? projectPath) =>
        kind == ThreadScopeKind.General ? General : ForProject(projectPath ?? string.Empty);

    public bool Matches(ThreadScopeKind kind, string? projectPath)
    {
        if (Kind != kind)
        {
            return false;
        }

        return Kind == ThreadScopeKind.General || PathsEqual(ProjectPath, projectPath);
    }

    private static bool PathsEqual(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
