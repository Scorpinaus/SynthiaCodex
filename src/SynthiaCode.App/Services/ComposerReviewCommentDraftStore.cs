using SynthiaCode.Core.Git;
using SynthiaCode.Core.Settings;

namespace SynthiaCode.App.Services;

public static class ComposerReviewCommentDraftStore
{
    public static void Capture(
        AppSettings settings,
        string? projectPath,
        string? threadId,
        IEnumerable<GitInlineComment> comments)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(comments);
        var scope = ResolveScope(projectPath);
        var draft = Find(settings, scope, threadId);
        var snapshot = GitInlineComment.NormalizeForSubmission(comments)
            .Select(comment => comment.Clone())
            .ToList();
        if (snapshot.Count == 0)
        {
            if (draft is not null)
            {
                draft.ReviewComments = [];
                draft.UpdatedAt = DateTimeOffset.UtcNow;
                if (draft.Attachments.Count == 0)
                {
                    settings.ComposerAttachmentDrafts.Remove(draft);
                }
            }
            return;
        }

        draft ??= new ComposerAttachmentDraftSnapshot
        {
            ScopeKind = scope.Kind,
            ProjectPath = scope.ProjectPath ?? string.Empty,
            ThreadId = threadId
        };
        if (!settings.ComposerAttachmentDrafts.Contains(draft))
        {
            settings.ComposerAttachmentDrafts.Add(draft);
        }
        draft.ReviewComments = snapshot;
        draft.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static IReadOnlyList<GitInlineComment> Restore(
        AppSettings settings,
        string? projectPath,
        string? threadId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var draft = Find(settings, ResolveScope(projectPath), threadId);
        return GitInlineComment.NormalizeRestored(draft?.ReviewComments)
            .Select(comment => comment.Clone())
            .ToArray();
    }

    public static void Remove(
        AppSettings settings,
        string? projectPath,
        string? threadId,
        IEnumerable<string> commentIds)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(commentIds);
        var ids = commentIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return;
        }

        Capture(
            settings,
            projectPath,
            threadId,
            Restore(settings, projectPath, threadId)
                .Where(comment => !ids.Contains(comment.Id)));
    }

    private static ComposerAttachmentDraftSnapshot? Find(
        AppSettings settings,
        ThreadScopeKey scope,
        string? threadId) => settings.ComposerAttachmentDrafts.FirstOrDefault(item =>
            scope.Matches(item.ScopeKind, item.ProjectPath) &&
            string.Equals(item.ThreadId, threadId, StringComparison.Ordinal));

    private static ThreadScopeKey ResolveScope(string? projectPath) =>
        string.IsNullOrWhiteSpace(projectPath)
            ? ThreadScopeKey.General
            : ThreadScopeKey.ForProject(projectPath);
}
