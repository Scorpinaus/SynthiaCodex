using System.IO;
using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Infrastructure.Attachments;

namespace SynthiaCode.App.Services;

public sealed class AttachmentPromptInputBuilder
{
    private readonly IAttachmentStore? attachmentStore;
    private readonly WorkspaceAttachmentResolver workspaceAttachmentResolver;

    public AttachmentPromptInputBuilder(
        IAttachmentStore? attachmentStore,
        WorkspaceAttachmentResolver workspaceAttachmentResolver)
    {
        this.attachmentStore = attachmentStore;
        this.workspaceAttachmentResolver = workspaceAttachmentResolver ??
            throw new ArgumentNullException(nameof(workspaceAttachmentResolver));
    }

    public IReadOnlyList<CodexUserInput> Build(
        string text,
        IReadOnlyList<AttachmentReference> attachments,
        string workspacePath)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        var inputs = new List<CodexUserInput>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            inputs.Add(new CodexTextInput(text));
        }

        foreach (var attachment in attachments)
        {
            if (attachment.SourceKind == AttachmentSourceKind.WorkspaceReference)
            {
                var resolved = workspaceAttachmentResolver.Revalidate(workspacePath, attachment);
                attachment.ManagedPath = resolved.ManagedPath;
                inputs.Add(new CodexMentionInput(resolved.WorkspaceRelativePath!, resolved.ManagedPath!));
                continue;
            }

            var path = attachmentStore is not null
                ? attachmentStore.ResolvePath(attachment)
                : attachment.ManagedPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new FileNotFoundException($"Attachment '{attachment.DisplayName}' is unavailable.");
            }
            var exists = attachment.IsFolder ? Directory.Exists(path) : File.Exists(path);
            if (!exists)
            {
                throw attachment.IsFolder
                    ? new DirectoryNotFoundException($"Attachment '{attachment.DisplayName}' is unavailable.")
                    : new FileNotFoundException($"Attachment '{attachment.DisplayName}' is unavailable.", path);
            }

            attachment.ManagedPath = path;
            inputs.Add(attachment.IsImage
                ? new CodexLocalImageInput(path)
                : new CodexMentionInput(attachment.DisplayName, path));
        }

        if (inputs.Count == 0)
        {
            throw new InvalidOperationException("Enter a prompt or attach a file, folder, or image before sending.");
        }
        return inputs;
    }
}
