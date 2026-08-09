using System.Text.Json.Nodes;
using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.Infrastructure.Codex.Codecs;

internal sealed class ThreadCodexCodec
{
    public CodexRpcCall EncodeStart(CodexThreadStartOptions options)
    {
        ValidatePermissionBoundary(options.Sandbox, options.PermissionProfileId);

        var parameters = new JsonObject();
        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            parameters["model"] = options.Model;
        }

        if (!string.IsNullOrWhiteSpace(options.Cwd))
        {
            parameters["cwd"] = options.Cwd;
        }

        if (options.Sandbox is not null)
        {
            parameters["sandbox"] = options.Sandbox.Value.ToProtocolValue();
        }

        AddPermissionProfile(parameters, options.PermissionProfileId);
        AddApprovalPolicyOverrides(parameters, options.ApprovalPolicy, options.ApprovalsReviewer);
        AddInstructionOverrides(parameters, options.DeveloperInstructions, options.BaseInstructions);
        return new CodexRpcCall("thread/start", parameters);
    }

    public CodexThreadStartResult DecodeStart(JsonNode? response)
    {
        var result = response as JsonObject;
        var threadId = ReadString(result, "thread.id");
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new CodexAppServerProtocolException("thread/start response did not include result.thread.id.");
        }

        return new CodexThreadStartResult(threadId, ParseActivePermissionProfile(result));
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

    private static void AddInstructionOverrides(
        JsonObject parameters,
        string? developerInstructions,
        string? baseInstructions)
    {
        if (!string.IsNullOrWhiteSpace(developerInstructions))
        {
            parameters["developerInstructions"] = developerInstructions;
        }

        if (!string.IsNullOrWhiteSpace(baseInstructions))
        {
            parameters["baseInstructions"] = baseInstructions;
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

    private static CodexActivePermissionProfile? ParseActivePermissionProfile(JsonObject? result)
    {
        var node = result?["thread"]?["activePermissionProfile"] ?? result?["activePermissionProfile"];
        if (node is JsonValue value && value.TryGetValue<string>(out var stringId) && !string.IsNullOrWhiteSpace(stringId))
        {
            return new CodexActivePermissionProfile(stringId);
        }

        if (node is not JsonObject profile)
        {
            return null;
        }

        var id = ReadString(profile, "id");
        return string.IsNullOrWhiteSpace(id)
            ? null
            : new CodexActivePermissionProfile(id, ReadString(profile, "description"));
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
