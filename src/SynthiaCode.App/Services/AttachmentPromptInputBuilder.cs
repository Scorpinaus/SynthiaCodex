using System.IO;
using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Harnesses;
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
        string workspacePath) =>
        BuildHarness(text, attachments, workspacePath).Select(ToCodex).ToArray();

    public IReadOnlyList<HarnessContentPart> BuildHarness(
        string text,
        IReadOnlyList<AttachmentReference> attachments,
        string workspacePath)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        var inputs = new List<HarnessContentPart>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            inputs.Add(new TextContentPart(text));
        }

        foreach (var attachment in attachments)
        {
            if (attachment.SourceKind == AttachmentSourceKind.WorkspaceReference)
            {
                var resolved = workspaceAttachmentResolver.Revalidate(workspacePath, attachment);
                attachment.ManagedPath = resolved.ManagedPath;
                inputs.Add(new WorkspaceReferenceContentPart(resolved.WorkspaceRelativePath!, resolved.ManagedPath!));
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
                ? new LocalImageContentPart(path)
                : new WorkspaceReferenceContentPart(attachment.DisplayName, path));
        }

        if (inputs.Count == 0)
        {
            throw new InvalidOperationException("Enter a prompt or attach a file, folder, or image before sending.");
        }
        return inputs;
    }

    private static CodexUserInput ToCodex(HarnessContentPart input) => input switch
    {
        TextContentPart text => new CodexTextInput(text.Text),
        DataImageContentPart image => new CodexImageInput(image.DataUrl),
        LocalImageContentPart image => new CodexLocalImageInput(image.Path),
        WorkspaceReferenceContentPart mention => new CodexMentionInput(mention.Name, mention.Path),
        SkillReferenceContentPart skill => new CodexSkillInput(skill.Name, skill.Path),
        _ => throw new NotSupportedException($"Unsupported attachment input {input.GetType().Name}.")
    };
}
