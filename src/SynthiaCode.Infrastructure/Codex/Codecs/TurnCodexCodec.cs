using System.Text.Json.Nodes;
using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.Infrastructure.Codex.Codecs;

internal sealed class TurnCodexCodec
{
    public CodexRpcCall EncodeSteer(CodexTurnSteerRequest request)
    {
        ValidateUserInputs(request.Inputs, nameof(request));
        return new CodexRpcCall(
            "turn/steer",
            new JsonObject
            {
                ["threadId"] = request.ThreadId,
                ["expectedTurnId"] = request.ExpectedTurnId,
                ["input"] = WriteUserInputs(request.Inputs)
            });
    }

    public CodexTurnSteerResult DecodeSteer(JsonNode? response)
    {
        var turnId = ReadString(response as JsonObject, "turnId");
        if (string.IsNullOrWhiteSpace(turnId))
        {
            throw new CodexAppServerProtocolException("turn/steer response did not include result.turnId.");
        }

        return new CodexTurnSteerResult(turnId);
    }

    public CodexRpcCall EncodeStart(CodexTurnStartRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ThreadId))
        {
            throw new ArgumentException("Thread ID is required.", nameof(request));
        }

        ValidateUserInputs(request.Inputs, nameof(request));
        ValidatePermissionBoundary(request.Sandbox, request.PermissionProfileId);

        var parameters = new JsonObject
        {
            ["threadId"] = request.ThreadId,
            ["input"] = WriteUserInputs(request.Inputs),
            ["cwd"] = request.Cwd
        };

        if (request.Sandbox is not null)
        {
            parameters["sandboxPolicy"] = request.Sandbox.Value.ToTurnSandboxPolicy(request.WorkspaceRoots);
        }

        AddPermissionProfile(parameters, request.PermissionProfileId);
        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            parameters["model"] = request.Model;
        }

        if (request.ReasoningEffort is not null)
        {
            parameters["effort"] = request.ReasoningEffort.Value.ToProtocolValue();
        }

        AddApprovalPolicyOverrides(parameters, request.ApprovalPolicy, request.ApprovalsReviewer);
        switch (request.ServiceTier)
        {
            case CodexServiceTierSelection.Inherit:
                break;
            case CodexServiceTierSelection.Standard:
                parameters["serviceTier"] = null;
                break;
            case CodexServiceTierSelection.Fast:
                parameters["serviceTier"] = "fast";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.ServiceTier, "Unknown service tier selection.");
        }

        return new CodexRpcCall("turn/start", parameters);
    }

    public CodexTurnStartResult DecodeStart(JsonNode? response)
    {
        var turnId = ReadString(response as JsonObject, "turn.id");
        if (string.IsNullOrWhiteSpace(turnId))
        {
            throw new CodexAppServerProtocolException("turn/start response did not include result.turn.id.");
        }

        return new CodexTurnStartResult(turnId);
    }

    public CodexRpcCall EncodeReviewStart(CodexReviewStartRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ThreadId))
        {
            throw new ArgumentException("Thread ID is required.", nameof(request));
        }

        ArgumentNullException.ThrowIfNull(request.Target);
        return new CodexRpcCall(
            "review/start",
            new JsonObject
            {
                ["threadId"] = request.ThreadId,
                ["delivery"] = request.Delivery switch
                {
                    CodexReviewDelivery.Inline => "inline",
                    CodexReviewDelivery.Detached => "detached",
                    _ => throw new ArgumentOutOfRangeException(nameof(request), request.Delivery, "Unknown review delivery.")
                },
                ["target"] = WriteReviewTarget(request.Target)
            });
    }

    public CodexReviewStartResult DecodeReviewStart(JsonNode? response)
    {
        var result = response as JsonObject;
        var turnId = ReadString(result, "turn.id");
        var reviewThreadId = ReadString(result, "reviewThreadId");
        if (string.IsNullOrWhiteSpace(turnId))
        {
            throw new CodexAppServerProtocolException("review/start response did not include result.turn.id.");
        }

        if (string.IsNullOrWhiteSpace(reviewThreadId))
        {
            throw new CodexAppServerProtocolException("review/start response did not include result.reviewThreadId.");
        }

        return new CodexReviewStartResult(turnId, reviewThreadId);
    }

    public CodexRpcCall EncodeInterrupt(string threadId, string turnId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("Thread ID is required.", nameof(threadId));
        }

        if (string.IsNullOrWhiteSpace(turnId))
        {
            throw new ArgumentException("Turn ID is required.", nameof(turnId));
        }

        return new CodexRpcCall(
            "turn/interrupt",
            new JsonObject
            {
                ["threadId"] = threadId,
                ["turnId"] = turnId
            });
    }

    private static JsonObject WriteReviewTarget(CodexReviewTarget target) => target.Kind switch
    {
        CodexReviewTargetKind.UncommittedChanges => new JsonObject
        {
            ["type"] = "uncommittedChanges"
        },
        CodexReviewTargetKind.BaseBranch => new JsonObject
        {
            ["type"] = "baseBranch",
            ["branch"] = target.Branch
        },
        CodexReviewTargetKind.Commit => new JsonObject
        {
            ["type"] = "commit",
            ["sha"] = target.Sha,
            ["title"] = target.Title
        },
        CodexReviewTargetKind.Custom => new JsonObject
        {
            ["type"] = "custom",
            ["instructions"] = target.Instructions
        },
        _ => throw new ArgumentOutOfRangeException(nameof(target), target.Kind, "Unknown review target.")
    };

    private static void ValidateUserInputs(IReadOnlyList<CodexUserInput>? inputs, string parameterName)
    {
        if (inputs is null || inputs.Count == 0)
        {
            throw new ArgumentException("At least one prompt input is required.", parameterName);
        }

        var hasContent = false;
        foreach (var input in inputs)
        {
            switch (input)
            {
                case CodexTextInput text when !string.IsNullOrWhiteSpace(text.Text):
                case CodexLocalImageInput localImage when !string.IsNullOrWhiteSpace(localImage.Path):
                case CodexImageInput dataImage when dataImage.DataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase):
                    hasContent = true;
                    break;
                case CodexMentionInput mention when
                    !string.IsNullOrWhiteSpace(mention.Name) &&
                    !string.IsNullOrWhiteSpace(mention.Path) &&
                    Path.IsPathRooted(mention.Path):
                    hasContent = true;
                    break;
                case CodexSkillInput skill when
                    !string.IsNullOrWhiteSpace(skill.Name) &&
                    !string.IsNullOrWhiteSpace(skill.Path) &&
                    Path.IsPathRooted(skill.Path) &&
                    Path.GetFileName(skill.Path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase):
                    hasContent = true;
                    break;
                case CodexSkillInput:
                    throw new ArgumentException(
                        "Skill inputs require a name and an absolute SKILL.md path.",
                        parameterName);
                case CodexTextInput or CodexLocalImageInput or CodexImageInput or CodexMentionInput:
                    break;
                default:
                    throw new ArgumentException("The prompt contains an unsupported input part.", parameterName);
            }
        }

        if (!hasContent)
        {
            throw new ArgumentException("At least one non-empty text or attachment input is required.", parameterName);
        }
    }

    private static JsonArray WriteUserInputs(IReadOnlyList<CodexUserInput> inputs)
    {
        var result = new JsonArray();
        foreach (var input in inputs)
        {
            JsonObject? item = input switch
            {
                CodexTextInput text when !string.IsNullOrWhiteSpace(text.Text) => new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text.Text
                },
                CodexLocalImageInput image when !string.IsNullOrWhiteSpace(image.Path) => new JsonObject
                {
                    ["type"] = "localImage",
                    ["path"] = image.Path
                },
                CodexImageInput image when image.DataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) => new JsonObject
                {
                    ["type"] = "image",
                    ["url"] = image.DataUrl
                },
                CodexMentionInput mention when
                    !string.IsNullOrWhiteSpace(mention.Name) &&
                    !string.IsNullOrWhiteSpace(mention.Path) &&
                    Path.IsPathRooted(mention.Path) => new JsonObject
                {
                    ["type"] = "mention",
                    ["name"] = mention.Name,
                    ["path"] = mention.Path
                },
                CodexSkillInput skill when
                    !string.IsNullOrWhiteSpace(skill.Name) &&
                    !string.IsNullOrWhiteSpace(skill.Path) &&
                    Path.IsPathRooted(skill.Path) &&
                    Path.GetFileName(skill.Path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) => new JsonObject
                {
                    ["type"] = "skill",
                    ["name"] = skill.Name,
                    ["path"] = skill.Path
                },
                _ => null
            };
            if (item is not null)
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static void AddApprovalPolicyOverrides(
        JsonObject parameters,
        CodexApprovalPolicy? approvalPolicy,
        CodexApprovalsReviewer? approvalsReviewer)
    {
        if (approvalPolicy is not null)
        {
            parameters["approvalPolicy"] = approvalPolicy.Value.ToProtocolValue();
        }

        if (approvalsReviewer is not null)
        {
            parameters["approvalsReviewer"] = approvalsReviewer.Value.ToProtocolValue();
        }
    }

    private static void AddPermissionProfile(JsonObject parameters, string? permissionProfileId)
    {
        if (!string.IsNullOrWhiteSpace(permissionProfileId))
        {
            parameters["permissionProfile"] = permissionProfileId;
        }
    }

    private static void ValidatePermissionBoundary(CodexSandbox? sandbox, string? permissionProfileId)
    {
        if (sandbox is not null && !string.IsNullOrWhiteSpace(permissionProfileId))
        {
            throw new InvalidOperationException("A permission profile and a legacy sandbox cannot be sent together.");
        }
    }

    private static string? ReadString(JsonObject? source, string path)
    {
        JsonNode? current = source;
        foreach (var segment in path.Split('.'))
        {
            current = current?[segment];
        }

        return current is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;
    }
}
