using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Infrastructure.Attachments;

namespace SynthiaCode.App.Services;

/// <summary>
/// Converts a stable application-level turn configuration into app-server request DTOs.
/// This keeps protocol-shape construction out of the shell view model.
/// </summary>
public sealed class CodexTurnRequestFactory
{
    private readonly IAttachmentStore? attachmentStore;
    private readonly WorkspaceAttachmentResolver workspaceAttachmentResolver;

    public CodexTurnRequestFactory(IAttachmentStore? attachmentStore, WorkspaceAttachmentResolver workspaceAttachmentResolver)
    {
        this.attachmentStore = attachmentStore;
        this.workspaceAttachmentResolver = workspaceAttachmentResolver;
    }

    public CodexThreadStartOptions CreateThreadStart(
        CodexResolvedPermissionMode permissions,
        string? model,
        string cwd,
        string? developerInstructions,
        string? baseInstructions) => new(
            NormalizeOverride(model), permissions.Sandbox, permissions.ApprovalPolicy,
            permissions.ApprovalsReviewer, permissions.PermissionProfileId, cwd,
            developerInstructions, baseInstructions);

    public CodexThreadResumeRequest CreateThreadResume(
        CodexResolvedPermissionMode permissions, string? model, string threadId, string cwd,
        string? developerInstructions, string? baseInstructions) => new(
            threadId, cwd, permissions.Sandbox, NormalizeOverride(model), permissions.ApprovalPolicy,
            permissions.ApprovalsReviewer, permissions.PermissionProfileId,
            developerInstructions, baseInstructions);

    public CodexThreadForkRequest CreateThreadFork(
        CodexResolvedPermissionMode permissions, string? model, string threadId, string cwd,
        string? developerInstructions, string? baseInstructions) => new(
            threadId, cwd, permissions.Sandbox, NormalizeOverride(model), permissions.ApprovalPolicy,
            permissions.ApprovalsReviewer, permissions.PermissionProfileId,
            developerInstructions, baseInstructions);

    public CodexTurnStartRequest CreateTurnStart(TurnRequestComposition composition)
    {
        var inputs = BuildInputs(
            composition.Prompt, composition.Attachments, composition.WorkspacePath,
            composition.SelectedModel, composition.SkillInputs);
        return new CodexTurnStartRequest(
            composition.ThreadId, inputs, composition.WorkspacePath, composition.Permissions.Sandbox,
            NormalizeOverride(composition.Model), ParseReasoningEffort(composition.ReasoningEffort), composition.ServiceTier,
            composition.Permissions.ApprovalPolicy, composition.Permissions.ApprovalsReviewer,
            composition.Permissions.PermissionProfileId);
    }

    public IReadOnlyList<CodexUserInput> BuildInputs(
        string prompt, IReadOnlyList<AttachmentReference> attachments, string workspacePath,
        CodexModelOption? selectedModel, IReadOnlyList<CodexSkillInput> skillInputs)
    {
        ValidateImageSupport(attachments, selectedModel);
        var inputs = new AttachmentPromptInputBuilder(attachmentStore, workspaceAttachmentResolver)
            .Build(prompt, attachments, workspacePath)
            .ToList();
        inputs.AddRange(skillInputs);
        return inputs;
    }

    public QueuedTurnOptionsSnapshot CaptureQueuedOptions(
        CodexResolvedPermissionMode permissions, CodexPermissionMode permissionMode,
        string workspacePath, string? model, string? reasoningEffort, CodexServiceTierSelection serviceTier) => new()
        {
            WorkspacePath = workspacePath,
            Model = NormalizeOverride(model),
            ReasoningEffort = ParseReasoningEffort(reasoningEffort),
            ServiceTier = serviceTier,
            PermissionMode = permissionMode,
            Sandbox = permissions.Sandbox,
            ApprovalPolicy = permissions.ApprovalPolicy,
            ApprovalsReviewer = permissions.ApprovalsReviewer,
            PermissionProfileId = permissions.PermissionProfileId
        };

    public static string? NormalizeOverride(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static CodexReasoningEffort? ParseReasoningEffort(string? value) => NormalizeOverride(value)?.ToLowerInvariant() switch
    {
        "none" => CodexReasoningEffort.None,
        "minimal" => CodexReasoningEffort.Minimal,
        "low" => CodexReasoningEffort.Low,
        "medium" => CodexReasoningEffort.Medium,
        "high" => CodexReasoningEffort.High,
        "xhigh" => CodexReasoningEffort.XHigh,
        _ => null
    };

    private static void ValidateImageSupport(IReadOnlyList<AttachmentReference> attachments, CodexModelOption? model)
    {
        if (attachments.Any(attachment => attachment.IsImage) && model?.SupportsImageInput == false)
        {
            throw new InvalidOperationException(
                $"{model.DisplayName} does not accept image input. Remove the images or choose an image-capable model.");
        }
    }
}

public sealed record TurnRequestComposition(
    string ThreadId,
    string Prompt,
    IReadOnlyList<AttachmentReference> Attachments,
    string WorkspacePath,
    CodexResolvedPermissionMode Permissions,
    string? Model,
    string? ReasoningEffort,
    CodexServiceTierSelection ServiceTier,
    CodexModelOption? SelectedModel,
    IReadOnlyList<CodexSkillInput> SkillInputs);
