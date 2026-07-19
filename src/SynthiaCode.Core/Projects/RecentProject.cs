namespace SynthiaCode.Core.Projects;

public sealed record RecentProject(
    string Path,
    string Name,
    DateTimeOffset LastOpenedUtc);
