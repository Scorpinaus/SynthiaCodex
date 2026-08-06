namespace SynthiaCode.Core.Codex.AppServer;

public enum CodexReviewTargetKind
{
    UncommittedChanges,
    BaseBranch,
    Commit,
    Custom
}

public enum CodexReviewDelivery
{
    Inline,
    Detached
}

public sealed class CodexReviewTarget
{
    private CodexReviewTarget(
        CodexReviewTargetKind kind,
        string? branch = null,
        string? sha = null,
        string? title = null,
        string? instructions = null)
    {
        Kind = kind;
        Branch = branch;
        Sha = sha;
        Title = title;
        Instructions = instructions;
    }

    public CodexReviewTargetKind Kind { get; }

    public string? Branch { get; }

    public string? Sha { get; }

    public string? Title { get; }

    public string? Instructions { get; }

    public string DisplayLabel => Kind switch
    {
        CodexReviewTargetKind.UncommittedChanges => "Review uncommitted changes",
        CodexReviewTargetKind.BaseBranch => $"Review changes against '{Branch}'",
        CodexReviewTargetKind.Commit => string.IsNullOrWhiteSpace(Title)
            ? $"Review commit {ShortSha(Sha)}"
            : $"Review commit {ShortSha(Sha)}: {Title}",
        CodexReviewTargetKind.Custom => "Custom code review",
        _ => "Code review"
    };

    public static CodexReviewTarget UncommittedChanges() =>
        new(CodexReviewTargetKind.UncommittedChanges);

    public static CodexReviewTarget BaseBranch(string branch) =>
        new(CodexReviewTargetKind.BaseBranch, branch: Required(branch, nameof(branch)));

    public static CodexReviewTarget Commit(string sha, string? title = null) =>
        new(
            CodexReviewTargetKind.Commit,
            sha: Required(sha, nameof(sha)),
            title: NormalizeOptional(title));

    public static CodexReviewTarget Custom(string instructions) =>
        new(CodexReviewTargetKind.Custom, instructions: Required(instructions, nameof(instructions)));

    private static string Required(string value, string parameterName) =>
        !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException("A review target value is required.", parameterName);

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ShortSha(string? sha) =>
        string.IsNullOrWhiteSpace(sha) || sha.Length <= 7 ? sha ?? string.Empty : sha[..7];
}

public sealed record CodexReviewStartRequest(
    string ThreadId,
    CodexReviewTarget Target,
    CodexReviewDelivery Delivery = CodexReviewDelivery.Inline);

public sealed record CodexReviewStartResult(string TurnId, string ReviewThreadId);
