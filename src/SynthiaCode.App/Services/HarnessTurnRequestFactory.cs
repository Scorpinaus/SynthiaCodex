using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Harnesses;
using SynthiaCode.Infrastructure.Attachments;

namespace SynthiaCode.App.Services;

/// <summary>
/// Converts presentation selections and attachment drafts into harness-neutral
/// conversation commands. Provider-specific protocol mapping belongs to the selected
/// harness adapter.
/// </summary>
public sealed class HarnessTurnRequestFactory
{
    private readonly IAttachmentStore? attachmentStore;
    private readonly WorkspaceAttachmentResolver workspaceAttachmentResolver;

    public HarnessTurnRequestFactory(
        IAttachmentStore? attachmentStore,
        WorkspaceAttachmentResolver workspaceAttachmentResolver)
    {
        this.attachmentStore = attachmentStore;
        this.workspaceAttachmentResolver = workspaceAttachmentResolver;
    }

    public StartConversationCommand CreateConversationStart(
        ConversationId conversationId,
        CodexResolvedPermissionMode permissions,
        string? model,
        string workspacePath,
        string? developerInstructions,
        string? baseInstructions,
        IReadOnlyList<string>? workspaceRoots = null) => new(
            conversationId,
            workspacePath,
            CreateOptions(permissions, model, null, CodexServiceTierSelection.Inherit),
            developerInstructions,
            baseInstructions,
            workspaceRoots);

    public ResumeConversationCommand CreateConversationResume(
        ConversationAddress address,
        CodexResolvedPermissionMode permissions,
        string? model,
        string workspacePath,
        string? developerInstructions,
        string? baseInstructions,
        IReadOnlyList<string>? workspaceRoots = null) => new(
            address,
            workspacePath,
            CreateOptions(permissions, model, null, CodexServiceTierSelection.Inherit),
            developerInstructions,
            baseInstructions,
            workspaceRoots);

    public ForkConversationCommand CreateConversationFork(
        ConversationId conversationId,
        ConversationAddress source,
        CodexResolvedPermissionMode permissions,
        string? model,
        string workspacePath,
        string? developerInstructions,
        string? baseInstructions,
        IReadOnlyList<string>? workspaceRoots = null) => new(
            conversationId,
            source,
            workspacePath,
            CreateOptions(permissions, model, null, CodexServiceTierSelection.Inherit),
            developerInstructions,
            baseInstructions,
            workspaceRoots);

    public StartTurnCommand CreateTurnStart(HarnessTurnRequestComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        var inputs = BuildInputs(
            composition.Prompt,
            composition.Attachments,
            composition.WorkspacePath,
            composition.SelectedModel,
            composition.SkillInputs,
            composition.WorkspaceRoots);
        return new StartTurnCommand(
            composition.Address,
            inputs,
            composition.WorkspacePath,
            CreateOptions(
                composition.Permissions,
                composition.Model,
                composition.ReasoningEffort,
                composition.ServiceTier),
            composition.WorkspaceRoots);
    }

    public IReadOnlyList<HarnessContentPart> BuildInputs(
        string prompt,
        IReadOnlyList<AttachmentReference> attachments,
        string workspacePath,
        CodexModelOption? selectedModel,
        IReadOnlyList<CodexSkillInput> skillInputs,
        IReadOnlyList<string>? workspaceRoots = null)
    {
        ValidateImageSupport(attachments, selectedModel);
        var inputs = new AttachmentPromptInputBuilder(attachmentStore, workspaceAttachmentResolver)
            .BuildHarness(prompt, attachments, workspacePath, workspaceRoots)
            .ToList();
        inputs.AddRange(skillInputs.Select(skill => new SkillReferenceContentPart(skill.Name, skill.Path)));
        return inputs;
    }

    private static HarnessTurnOptions CreateOptions(
        CodexResolvedPermissionMode permissions,
        string? model,
        string? reasoningEffort,
        CodexServiceTierSelection serviceTier) => new(
            CodexTurnRequestFactory.NormalizeOverride(model),
            CodexTurnRequestFactory.NormalizeOverride(reasoningEffort),
            serviceTier switch
            {
                CodexServiceTierSelection.Standard => "standard",
                CodexServiceTierSelection.Fast => "fast",
                _ => null
            },
            new HarnessExecutionPolicy(
                permissions.Sandbox switch
                {
                    CodexSandbox.ReadOnly => WorkspaceAccessMode.ReadOnly,
                    CodexSandbox.DangerFullAccess => WorkspaceAccessMode.Unrestricted,
                    _ => WorkspaceAccessMode.WorkspaceWrite
                },
                permissions.ApprovalPolicy == CodexApprovalPolicy.Never
                    ? ApprovalInteractionMode.NeverPrompt
                    : permissions.ApprovalsReviewer == CodexApprovalsReviewer.AutoReview
                        ? ApprovalInteractionMode.AutomaticReview
                        : ApprovalInteractionMode.Prompt,
                permissions.PermissionProfileId));

    private static void ValidateImageSupport(
        IReadOnlyList<AttachmentReference> attachments,
        CodexModelOption? model)
    {
        if (attachments.Any(attachment => attachment.IsImage) && model?.SupportsImageInput == false)
        {
            throw new InvalidOperationException(
                $"{model.DisplayName} does not accept image input. Remove the images or choose an image-capable model.");
        }
    }
}

public sealed record HarnessTurnRequestComposition(
    ConversationAddress Address,
    string Prompt,
    IReadOnlyList<AttachmentReference> Attachments,
    string WorkspacePath,
    CodexResolvedPermissionMode Permissions,
    string? Model,
    string? ReasoningEffort,
    CodexServiceTierSelection ServiceTier,
    CodexModelOption? SelectedModel,
    IReadOnlyList<CodexSkillInput> SkillInputs,
    IReadOnlyList<string>? WorkspaceRoots = null);
