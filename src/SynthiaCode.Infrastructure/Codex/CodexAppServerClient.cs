using System.Text.Json;
using System.Text.Json.Nodes;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Codex.Configuration;
using SynthiaCode.Infrastructure.Codex.Codecs;

namespace SynthiaCode.Infrastructure.Codex;

/// <summary>
/// Provides typed Codex app-server operations over one JSON-RPC connection.
/// </summary>
public class CodexClient : IAsyncDisposable
{
    private readonly JsonRpcConnection connection;
    private readonly SessionCodexCodec sessionCodec;
    private readonly ThreadCodexCodec threadCodec = new();
    private readonly AccountCodexCodec accountCodec = new();
    private readonly SkillsCodexCodec skillsCodec = new();
    private readonly TurnCodexCodec turnCodec = new();
    private readonly CodexServerRequestParser serverRequestParser = new();
    private readonly CodexNotificationParser notificationParser;
    private readonly HashSet<CodexRequestId> pendingIncomingRequests = [];
    private readonly HashSet<CodexRequestId> respondingIncomingRequests = [];
    private readonly object gate = new();

    public CodexClient(IAppServerTransport transport, CodexAppServerClientMetadata metadata)
    {
        sessionCodec = new SessionCodexCodec(metadata);
        notificationParser = new CodexNotificationParser(serverRequestParser);
        connection = new JsonRpcConnection(transport, ProcessMessageAsync);
        connection.ConnectionFailed += OnConnectionFailed;
    }

    public event EventHandler<AppServerNotification>? NotificationReceived;

    public event EventHandler<CodexServerRequest>? ServerRequestReceived;

    public event EventHandler<AppServerConnectionFailedEventArgs>? ConnectionFailed;

    public bool IsHealthy { get; private set; }

    public Task<CodexAppServerSession> InitializeAsync(CancellationToken cancellationToken = default) =>
        InitializeAsync(CodexInitializeOptions.Default, cancellationToken);

    public async Task<CodexAppServerSession> InitializeAsync(
        CodexInitializeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        var call = sessionCodec.EncodeInitialize(options);
        var response = await SendRequestForResponseAsync(call.Method, call.Parameters, cancellationToken).ConfigureAwait(false);
        await SendNotificationAsync("initialized", new JsonObject(), cancellationToken).ConfigureAwait(false);

        await using var registration = cancellationToken.Register(() => connection.CancelPendingResponse(response, cancellationToken));
        var result = await response.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        IsHealthy = true;
        return sessionCodec.DecodeInitialize(result);
    }

    public async Task<CodexThreadStartResult> StartThreadAsync(
        CodexThreadStartOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        var call = threadCodec.EncodeStart(options);
        var result = await SendThreadLifecycleRequestAsync(
            call.Method,
            call.Parameters!,
            cancellationToken).ConfigureAwait(false);
        return threadCodec.DecodeStart(result);
    }

    public async Task<CodexThreadResumeResult> ResumeThreadAsync(
        CodexThreadResumeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ThreadId))
        {
            throw new ArgumentException("Thread ID is required.", nameof(request));
        }

        ValidatePermissionBoundary(request.Sandbox, request.PermissionProfileId);

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        var parameters = new JsonObject
        {
            ["threadId"] = request.ThreadId,
            ["cwd"] = request.Cwd
        };

        if (request.Sandbox is not null)
        {
            parameters["sandbox"] = request.Sandbox.Value.ToProtocolValue();
        }

        AddPermissionProfile(parameters, request.PermissionProfileId);

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            parameters["model"] = request.Model;
        }

        AddApprovalPolicyOverrides(parameters, request.ApprovalPolicy, request.ApprovalsReviewer);
        AddInstructionOverrides(parameters, request.DeveloperInstructions, request.BaseInstructions);

        var result = await SendThreadLifecycleRequestAsync("thread/resume", parameters, cancellationToken).ConfigureAwait(false) as JsonObject;
        var threadId = ReadString(result, "thread.id");
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new CodexAppServerProtocolException("thread/resume response did not include result.thread.id.");
        }

        return new CodexThreadResumeResult(
            threadId,
            ParseConversationTurns(result?["thread"]?["turns"] as JsonArray),
            ParseActivePermissionProfile(result));
    }

    public async Task<CodexThreadRollbackResult> RollbackThreadAsync(
        CodexThreadRollbackRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ThreadId))
        {
            throw new ArgumentException("Thread ID is required.", nameof(request));
        }
        if (request.NumTurns < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "At least one turn must be rolled back.");
        }

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var result = await SendRequestAsync(
            "thread/rollback",
            new JsonObject
            {
                ["threadId"] = request.ThreadId,
                ["numTurns"] = request.NumTurns
            },
            cancellationToken).ConfigureAwait(false) as JsonObject;
        var threadId = ReadString(result, "thread.id");
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new CodexAppServerProtocolException("thread/rollback response did not include result.thread.id.");
        }

        return new CodexThreadRollbackResult(
            threadId,
            ParseConversationTurns(result?["thread"]?["turns"] as JsonArray));
    }

    public async Task<CodexThreadReadResult> ReadThreadAsync(
        CodexThreadReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ThreadId))
        {
            throw new ArgumentException("Thread ID is required.", nameof(request));
        }

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var result = await SendRequestAsync(
            "thread/read",
            new JsonObject
            {
                ["threadId"] = request.ThreadId,
                ["includeTurns"] = request.IncludeTurns
            },
            cancellationToken).ConfigureAwait(false) as JsonObject;

        var threadId = ReadString(result, "thread.id");
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new CodexAppServerProtocolException("thread/read response did not include result.thread.id.");
        }

        var turns = result?["thread"]?["turns"] as JsonArray;
        return new CodexThreadReadResult(threadId, ParseConversationTurns(turns));
    }

    public async Task<IReadOnlyList<CodexModelOption>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        var models = new List<CodexModelOption>();
        var seenModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? cursor = null;
        do
        {
            var parameters = new JsonObject
            {
                ["includeHidden"] = false,
                ["limit"] = 100
            };
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                parameters["cursor"] = cursor;
            }

            var result = await SendRequestAsync(
                "model/list",
                parameters,
                cancellationToken).ConfigureAwait(false) as JsonObject;

            if (result?["data"] is JsonArray data)
            {
                foreach (var item in data.OfType<JsonObject>())
                {
                    var model = ReadString(item, "model") ?? ReadString(item, "id");
                    if (string.IsNullOrWhiteSpace(model) || !seenModels.Add(model))
                    {
                        continue;
                    }

                    models.Add(new CodexModelOption(
                        ReadString(item, "id") ?? model,
                        model,
                        ReadString(item, "displayName") ?? model,
                        ReadString(item, "description") ?? string.Empty,
                        ReadBool(item, "isDefault") ?? false,
                        ReadBool(item, "hidden") ?? false,
                        ParseReasoningEffort(ReadString(item, "defaultReasoningEffort")),
                        ReadReasoningEfforts(item),
                        ReadServiceTiers(item),
                        ReadString(item, "availabilityNux.message"),
                        ReadStringArray(item, "additionalSpeedTiers"),
                        ReadInputModalities(item)));
                }
            }

            cursor = ReadString(result, "nextCursor");
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return models;
    }

    public async Task<CodexPermissionProfileListResult> ListPermissionProfilesAsync(
        string cwd,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            throw new ArgumentException("A project working directory is required.", nameof(cwd));
        }

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var profiles = new List<CodexPermissionProfileSummary>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        try
        {
            do
            {
                var parameters = new JsonObject
                {
                    ["cwd"] = cwd,
                    ["limit"] = 100
                };
                if (!string.IsNullOrWhiteSpace(cursor))
                {
                    parameters["cursor"] = cursor;
                }

                var result = await SendRequestAsync(
                    "permissionProfile/list",
                    parameters,
                    cancellationToken).ConfigureAwait(false) as JsonObject;
                if (result?["data"] is JsonArray data)
                {
                    foreach (var item in data.OfType<JsonObject>())
                    {
                        var id = ReadString(item, "id");
                        if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                        {
                            continue;
                        }

                        profiles.Add(new CodexPermissionProfileSummary(
                            id,
                            ReadString(item, "description"),
                            ReadBool(item, "allowed") ?? true));
                    }
                }

                cursor = ReadString(result, "nextCursor");
            }
            while (!string.IsNullOrWhiteSpace(cursor));
        }
        catch (CodexAppServerProtocolException ex) when (ex.Code == -32601)
        {
            return new CodexPermissionProfileListResult([], null, IsSupported: false);
        }

        return new CodexPermissionProfileListResult(profiles, null);
    }

    public async Task<CodexSkillListResult> ListSkillsAsync(
        CodexSkillListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var call = skillsCodec.EncodeList(request);
        try
        {
            var result = await SendRequestAsync(call.Method, call.Parameters, cancellationToken).ConfigureAwait(false);
            return skillsCodec.DecodeList(result);
        }
        catch (CodexAppServerProtocolException ex) when (ex.Code == -32601)
        {
            return new CodexSkillListResult([], IsSupported: false);
        }
    }

    public async Task<CodexSkillConfigWriteResult> WriteSkillConfigAsync(
        CodexSkillConfigWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var call = skillsCodec.EncodeConfigWrite(request);
        try
        {
            var result = await SendRequestAsync(call.Method, call.Parameters, cancellationToken).ConfigureAwait(false);
            return skillsCodec.DecodeConfigWrite(result);
        }
        catch (CodexAppServerProtocolException ex) when (ex.Code == -32601)
        {
            return new CodexSkillConfigWriteResult(request.Enabled, IsSupported: false);
        }
    }

    public async Task<CodexAccountReadResult> ReadAccountAsync(
        bool refreshToken = false,
        CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var call = accountCodec.EncodeRead(refreshToken);
        var result = await SendRequestAsync(call.Method, call.Parameters, cancellationToken).ConfigureAwait(false);
        return accountCodec.DecodeRead(result);
    }

    public async Task<CodexAccountRateLimitsResult> ReadAccountRateLimitsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var call = accountCodec.EncodeReadRateLimits();
        var result = await SendRequestAsync(call.Method, call.Parameters, cancellationToken).ConfigureAwait(false);
        return accountCodec.DecodeReadRateLimits(result);
    }

    public async Task<CodexExecutionPolicyConfig> ReadExecutionPolicyConfigAsync(
        string? cwd,
        CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var result = await SendRequestAsync(
            "config/read",
            new JsonObject
            {
                ["cwd"] = string.IsNullOrWhiteSpace(cwd) ? null : cwd,
                ["includeLayers"] = false
            },
            cancellationToken).ConfigureAwait(false) as JsonObject;
        var config = result?["config"] as JsonObject;
        var origins = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (result?["origins"] is JsonObject originValues)
        {
            foreach (var (key, value) in originValues)
            {
                var origin = value as JsonObject;
                origins[key] = ReadString(origin, "path") ?? ReadString(origin, "name");
            }
        }

        return new CodexExecutionPolicyConfig(
            ParseSandbox(ReadString(config, "sandbox_mode")),
            ParseApprovalPolicy(config?["approval_policy"]),
            ParseApprovalsReviewer(ReadString(config, "approvals_reviewer")),
            ReadBool(config, "sandbox_workspace_write.network_access"),
            origins);
    }

    public async Task<CodexEffectiveConfiguration> ReadEffectiveConfigurationAsync(
        string? cwd,
        CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        JsonObject? result;
        try
        {
            result = await SendRequestAsync(
                "config/read",
                new JsonObject
                {
                    ["cwd"] = string.IsNullOrWhiteSpace(cwd) ? null : Path.GetFullPath(cwd),
                    ["includeLayers"] = true
                },
                cancellationToken).ConfigureAwait(false) as JsonObject;
        }
        catch (CodexAppServerProtocolException ex) when (ex.Code == -32601)
        {
            return CodexEffectiveConfiguration.Unsupported;
        }

        var config = result?["config"] as JsonObject;
        var allowedOriginKeys = new HashSet<string>(
            [
                "model",
                "model_provider",
                "model_reasoning_effort",
                "service_tier",
                "profile",
                "sandbox_mode",
                "approval_policy",
                "approvals_reviewer",
                "web_search",
                "sandbox_workspace_write.network_access"
            ],
            StringComparer.Ordinal);
        var origins = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (result?["origins"] is JsonObject originValues)
        {
            foreach (var (key, value) in originValues)
            {
                if (allowedOriginKeys.Contains(key))
                {
                    origins[key] = FormatConfigOrigin(value as JsonObject);
                }
            }
        }

        return new CodexEffectiveConfiguration(
            ReadString(config, "model"),
            ReadString(config, "model_provider"),
            ReadString(config, "model_reasoning_effort"),
            ReadString(config, "service_tier"),
            ReadString(config, "profile"),
            ReadString(config, "sandbox_mode"),
            FormatApprovalPolicy(config?["approval_policy"]),
            ReadString(config, "approvals_reviewer"),
            ReadString(config, "web_search"),
            ReadBool(config, "sandbox_workspace_write.network_access"),
            origins);
    }

    public async Task<CodexProjectTrustLevel> ReadProjectTrustAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeProjectTrustPath(projectPath);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var result = await SendRequestAsync(
            "config/read",
            new JsonObject
            {
                ["cwd"] = null,
                ["includeLayers"] = false
            },
            cancellationToken).ConfigureAwait(false) as JsonObject;
        var config = result?["config"] as JsonObject
            ?? throw new CodexAppServerProtocolException(
                "config/read response did not include result.config.");
        if (!config.TryGetPropertyValue("projects", out var projectsNode) || projectsNode is null)
        {
            return CodexProjectTrustLevel.Unknown;
        }
        if (projectsNode is not JsonObject projects)
        {
            throw new CodexAppServerProtocolException(
                "config/read response included a non-object config.projects value.");
        }

        foreach (var (configuredPath, configuredValue) in projects)
        {
            if (!ProjectTrustPathsEqual(configuredPath, normalizedPath))
            {
                continue;
            }
            if (configuredValue is not JsonObject project)
            {
                throw new CodexAppServerProtocolException(
                    "config/read response included an invalid project trust entry.");
            }
            if (!project.TryGetPropertyValue("trust_level", out var trustNode) || trustNode is null)
            {
                return CodexProjectTrustLevel.Unknown;
            }
            if (trustNode is not JsonValue trustValue ||
                !trustValue.TryGetValue<string>(out var trustLevel))
            {
                throw new CodexAppServerProtocolException(
                    "config/read response included a non-string project trust level.");
            }

            return trustLevel switch
            {
                "trusted" => CodexProjectTrustLevel.Trusted,
                "untrusted" => CodexProjectTrustLevel.Untrusted,
                _ => throw new CodexAppServerProtocolException(
                    $"config/read response included unsupported project trust level '{trustLevel}'.")
            };
        }

        return CodexProjectTrustLevel.Unknown;
    }

    public async Task WriteProjectTrustAsync(
        string projectPath,
        CodexProjectTrustLevel trustLevel,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeProjectTrustPath(projectPath);
        var wireValue = trustLevel switch
        {
            CodexProjectTrustLevel.Trusted => "trusted",
            CodexProjectTrustLevel.Untrusted => "untrusted",
            _ => throw new ArgumentOutOfRangeException(
                nameof(trustLevel),
                trustLevel,
                "Only explicit project trust decisions can be persisted.")
        };
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var result = await SendRequestAsync(
            "config/value/write",
            new JsonObject
            {
                ["keyPath"] = $"projects.{JsonSerializer.Serialize(normalizedPath)}.trust_level",
                ["value"] = wireValue,
                ["mergeStrategy"] = "upsert"
            },
            cancellationToken).ConfigureAwait(false) as JsonObject;
        if (ReadString(result, "status") is not ("ok" or "okOverridden"))
        {
            throw new CodexAppServerProtocolException(
                "config/value/write response did not include a supported result.status.");
        }
    }

    public async Task<CodexExecutionPolicyRequirements> ReadExecutionPolicyRequirementsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var result = await SendRequestAsync(
            "configRequirements/read",
            new JsonObject(),
            cancellationToken).ConfigureAwait(false) as JsonObject;
        var requirements = result?["requirements"] as JsonObject;
        if (requirements is null)
        {
            return CodexExecutionPolicyRequirements.Unrestricted;
        }

        return new CodexExecutionPolicyRequirements(
            ParseSandboxArray(requirements["allowedSandboxModes"] as JsonArray),
            ParseApprovalPolicyArray(requirements["allowedApprovalPolicies"] as JsonArray),
            ParseApprovalsReviewerArray(requirements["allowedApprovalsReviewers"] as JsonArray),
            ReadStringArray(requirements, "allowedPermissionProfiles"));
    }

    public async Task<CodexThreadListResult> ListThreadsAsync(
        CodexThreadListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        var parameters = new JsonObject();
        if (!string.IsNullOrWhiteSpace(request.Cwd))
        {
            parameters["cwd"] = request.Cwd;
        }

        if (request.Archived is not null)
        {
            parameters["archived"] = request.Archived.Value;
        }

        if (request.Limit is not null)
        {
            parameters["limit"] = request.Limit.Value;
        }

        if (!string.IsNullOrWhiteSpace(request.Cursor))
        {
            parameters["cursor"] = request.Cursor;
        }

        var result = await SendRequestAsync("thread/list", parameters, cancellationToken).ConfigureAwait(false) as JsonObject;
        var threads = new List<CodexThreadSummary>();
        if (result?["data"] is JsonArray data)
        {
            foreach (var item in data.OfType<JsonObject>())
            {
                var threadId = ReadString(item, "id");
                if (string.IsNullOrWhiteSpace(threadId))
                {
                    continue;
                }

                var preview = ReadString(item, "preview") ?? string.Empty;
                threads.Add(new CodexThreadSummary(
                    threadId,
                    ReadString(item, "name") ?? preview,
                    preview,
                    ReadString(item, "cwd"),
                    ReadUnixTimestamp(item, "createdAt"),
                    ReadUnixTimestamp(item, "updatedAt"),
                    ReadString(item, "status.type")));
            }
        }

        return new CodexThreadListResult(threads, ReadString(result, "nextCursor"));
    }

    public async Task<CodexThreadForkResult> ForkThreadAsync(
        CodexThreadForkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePermissionBoundary(request.Sandbox, request.PermissionProfileId);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var parameters = new JsonObject
        {
            ["threadId"] = request.ThreadId,
            ["cwd"] = request.Cwd
        };
        if (!string.IsNullOrWhiteSpace(request.LastTurnId))
        {
            parameters["lastTurnId"] = request.LastTurnId;
        }
        if (request.Sandbox is not null)
        {
            parameters["sandbox"] = request.Sandbox.Value.ToProtocolValue();
        }
        AddPermissionProfile(parameters, request.PermissionProfileId);
        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            parameters["model"] = request.Model;
        }

        AddApprovalPolicyOverrides(parameters, request.ApprovalPolicy, request.ApprovalsReviewer);
        AddInstructionOverrides(parameters, request.DeveloperInstructions, request.BaseInstructions);

        var result = await SendThreadLifecycleRequestAsync("thread/fork", parameters, cancellationToken).ConfigureAwait(false) as JsonObject;
        var threadId = ReadString(result, "thread.id");
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new CodexAppServerProtocolException("thread/fork response did not include result.thread.id.");
        }

        return new CodexThreadForkResult(threadId, ParseActivePermissionProfile(result));
    }

    public Task ArchiveThreadAsync(string threadId, CancellationToken cancellationToken = default) =>
        SendThreadIdRequestAsync("thread/archive", threadId, cancellationToken);

    public Task UnarchiveThreadAsync(string threadId, CancellationToken cancellationToken = default) =>
        SendThreadIdRequestAsync("thread/unarchive", threadId, cancellationToken);

    public async Task SetThreadNameAsync(
        string threadId,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("Thread ID is required.", nameof(threadId));
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Thread name is required.", nameof(name));
        }

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        await SendRequestAsync(
            "thread/name/set",
            new JsonObject
            {
                ["threadId"] = threadId,
                ["name"] = name
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CodexThreadGoal> SetThreadGoalAsync(
        CodexThreadGoalSetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ThreadId))
        {
            throw new ArgumentException("Thread ID is required.", nameof(request));
        }
        if (request.Objective is not null &&
            (string.IsNullOrWhiteSpace(request.Objective) || request.Objective.Length > 4_000))
        {
            throw new ArgumentException("A goal objective must contain 1 through 4,000 characters.", nameof(request));
        }
        if (request.IncludeTokenBudget && request.TokenBudget is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "A goal token budget must be positive or null.");
        }
        if (request.Objective is null && request.Status is null && !request.IncludeTokenBudget)
        {
            throw new ArgumentException("A goal update must change the objective, status, or token budget.", nameof(request));
        }

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var parameters = new JsonObject { ["threadId"] = request.ThreadId };
        if (request.Objective is not null)
        {
            parameters["objective"] = request.Objective;
        }
        if (request.Status is not null)
        {
            parameters["status"] = request.Status.Value.ToProtocolValue();
        }
        if (request.IncludeTokenBudget)
        {
            parameters["tokenBudget"] = request.TokenBudget is null
                ? null
                : JsonValue.Create(request.TokenBudget.Value);
        }

        var result = await SendRequestAsync("thread/goal/set", parameters, cancellationToken).ConfigureAwait(false) as JsonObject;
        if (result?["goal"] is not JsonObject goal)
        {
            throw new CodexAppServerProtocolException("thread/goal/set response did not include result.goal.");
        }

        var saved = CodexThreadGoalJson.Parse(goal);
        return string.Equals(saved.ThreadId, request.ThreadId, StringComparison.Ordinal)
            ? saved
            : throw new CodexAppServerProtocolException("thread/goal/set returned a goal for a different thread.");
    }

    public async Task<CodexThreadGoal?> GetThreadGoalAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("Thread ID is required.", nameof(threadId));
        }

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var result = await SendRequestAsync(
            "thread/goal/get",
            new JsonObject { ["threadId"] = threadId },
            cancellationToken).ConfigureAwait(false) as JsonObject;
        if (result?["goal"] is not JsonObject goal)
        {
            return null;
        }

        var loaded = CodexThreadGoalJson.Parse(goal);
        return string.Equals(loaded.ThreadId, threadId, StringComparison.Ordinal)
            ? loaded
            : throw new CodexAppServerProtocolException("thread/goal/get returned a goal for a different thread.");
    }

    public async Task<bool> ClearThreadGoalAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("Thread ID is required.", nameof(threadId));
        }

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var result = await SendRequestAsync(
            "thread/goal/clear",
            new JsonObject { ["threadId"] = threadId },
            cancellationToken).ConfigureAwait(false) as JsonObject;
        return result?["cleared"] is JsonValue clearedValue && clearedValue.TryGetValue<bool>(out var cleared)
            ? cleared
            : throw new CodexAppServerProtocolException("thread/goal/clear response did not include result.cleared.");
    }

    public async Task<CodexTurnSteerResult> SteerTurnAsync(
        CodexTurnSteerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var call = turnCodec.EncodeSteer(request);
        var result = await SendRequestAsync(call.Method, call.Parameters, cancellationToken).ConfigureAwait(false);
        return turnCodec.DecodeSteer(result);
    }

    public async Task<CodexTurnStartResult> StartTurnAsync(
        CodexTurnStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var call = turnCodec.EncodeStart(request);
        var result = await SendRequestAsync(call.Method, call.Parameters, cancellationToken).ConfigureAwait(false);
        return turnCodec.DecodeStart(result);
    }

    public async Task<CodexReviewStartResult> StartReviewAsync(
        CodexReviewStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var call = turnCodec.EncodeReviewStart(request);
        var result = await SendRequestAsync(call.Method, call.Parameters, cancellationToken).ConfigureAwait(false);
        return turnCodec.DecodeReviewStart(result);
    }

    public async Task CancelTurnAsync(string threadId, string turnId, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var call = turnCodec.EncodeInterrupt(threadId, turnId);
        await SendRequestAsync(call.Method, call.Parameters, cancellationToken).ConfigureAwait(false);
    }

    public Task RespondToServerRequestAsync(
        CodexServerRequest request,
        CodexServerRequestResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        return RespondToServerRequestCoreAsync(
            request.RequestId,
            new JsonObject
            {
                ["id"] = request.RequestId.ToJsonNode(),
                ["result"] = response.Result.DeepClone()
            },
            cancellationToken);
    }

    private async Task SendThreadIdRequestAsync(
        string method,
        string threadId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("Thread ID is required.", nameof(threadId));
        }

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        await SendRequestAsync(
            method,
            new JsonObject { ["threadId"] = threadId },
            cancellationToken).ConfigureAwait(false);
    }

    private Task EnsureStartedAsync(CancellationToken cancellationToken) =>
        connection.EnsureStartedAsync(cancellationToken);

    private async Task<JsonNode?> SendRequestAsync(
        string method,
        JsonObject? parameters,
        CancellationToken cancellationToken)
    {
        return await connection.SendRequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonNode?> SendThreadLifecycleRequestAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        var hasInstructionOverrides =
            parameters.ContainsKey("developerInstructions") ||
            parameters.ContainsKey("baseInstructions");
        try
        {
            return await SendRequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (CodexAppServerProtocolException ex) when (
            hasInstructionOverrides &&
            ex.Code == -32602 &&
            (ex.Message.Contains("developerInstructions", StringComparison.OrdinalIgnoreCase) ||
             ex.Message.Contains("baseInstructions", StringComparison.OrdinalIgnoreCase)))
        {
            throw new CodexAppServerProtocolException(
                "The installed Codex runtime does not support custom instruction overrides. " +
                "Update Codex or disable custom instructions in SynthiaCode Settings.",
                ex);
        }
    }

    private Task<JsonRpcPendingResponse> SendRequestForResponseAsync(
        string method,
        JsonObject? parameters,
        CancellationToken cancellationToken) =>
        connection.BeginRequestAsync(method, parameters, cancellationToken);

    private Task SendNotificationAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken) =>
        connection.SendNotificationAsync(method, parameters, cancellationToken);

    private Task WriteMessageAsync(JsonObject message, CancellationToken cancellationToken) =>
        connection.SendMessageAsync(message, cancellationToken);

    private void OnConnectionFailed(object? sender, AppServerConnectionFailedEventArgs args)
    {
        IsHealthy = false;
        lock (gate)
        {
            pendingIncomingRequests.Clear();
            respondingIncomingRequests.Clear();
        }

        ConnectionFailed?.Invoke(this, args);
    }

    private async Task ProcessMessageAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var method = ReadString(message, "method");
        if (message["id"] is not null && !string.IsNullOrWhiteSpace(method))
        {
            if (!serverRequestParser.TryReadRequestId(message["id"], out var requestId))
            {
                return;
            }

            var serverParams = message["params"] as JsonObject;
            RegisterIncomingRequest(requestId);
            string? parseError = null;
            if (serverParams is null || !serverRequestParser.TryParse(method, serverParams, requestId, out var request, out parseError))
            {
                await RespondToServerRequestErrorAsync(
                    requestId,
                    -32602,
                    parseError ?? $"Invalid parameters for {method}.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (request.Payload is CodexUnsupportedServerRequest)
            {
                await RespondToServerRequestErrorAsync(
                    requestId,
                    -32601,
                    $"Server request method is not supported: {method}",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            ServerRequestReceived?.Invoke(this, request);
            return;
        }

        if (!notificationParser.TryParse(message, out var parsedNotification))
        {
            return;
        }

        if (parsedNotification.ResolvedRequestId is { } resolvedRequestId)
        {
            lock (gate)
            {
                pendingIncomingRequests.Remove(resolvedRequestId);
            }
        }
        NotificationReceived?.Invoke(this, parsedNotification.Notification);
    }

    private void RegisterIncomingRequest(CodexRequestId requestId)
    {
        lock (gate)
        {
            if (!pendingIncomingRequests.Add(requestId) || respondingIncomingRequests.Contains(requestId))
            {
                throw new CodexAppServerProtocolException($"Duplicate server request id {requestId}.");
            }
        }
    }

    private Task RespondToServerRequestErrorAsync(
        CodexRequestId requestId,
        int code,
        string message,
        CancellationToken cancellationToken) =>
        RespondToServerRequestCoreAsync(
            requestId,
            new JsonObject
            {
                ["id"] = requestId.ToJsonNode(),
                ["error"] = new JsonObject
                {
                    ["code"] = code,
                    ["message"] = message
                }
            },
            cancellationToken);

    private async Task RespondToServerRequestCoreAsync(
        CodexRequestId requestId,
        JsonObject message,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!pendingIncomingRequests.Remove(requestId) || !respondingIncomingRequests.Add(requestId))
            {
                throw new InvalidOperationException($"Server request {requestId} is no longer pending.");
            }
        }

        try
        {
            await WriteMessageAsync(message, cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                respondingIncomingRequests.Remove(requestId);
            }
        }
        catch
        {
            lock (gate)
            {
                respondingIncomingRequests.Remove(requestId);
                if (IsHealthy)
                {
                    pendingIncomingRequests.Add(requestId);
                }
            }

            throw;
        }
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

    private static void ValidatePermissionBoundary(CodexSandbox? sandbox, string? permissionProfileId)
    {
        if (sandbox is not null && !string.IsNullOrWhiteSpace(permissionProfileId))
        {
            throw new InvalidOperationException("A permission profile and a legacy sandbox cannot be sent together.");
        }
    }

    private static void AddPermissionProfile(JsonObject parameters, string? permissionProfileId)
    {
        if (!string.IsNullOrWhiteSpace(permissionProfileId))
        {
            parameters["permissionProfile"] = permissionProfileId;
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

    private static long? ReadLong(JsonObject source, string propertyName) =>
        source[propertyName] is JsonValue value && value.TryGetValue<long>(out var result)
            ? result
            : null;

    private static int? ReadInt(JsonObject source, string propertyName) =>
        source[propertyName] is JsonValue value && value.TryGetValue<int>(out var result)
            ? result
            : null;

    private static string? ReadString(JsonObject? obj, string path)
    {
        if (obj is null)
        {
            return null;
        }

        JsonNode? current = obj;
        foreach (var segment in path.Split('.'))
        {
            if (current is JsonObject currentObject)
            {
                current = currentObject[segment];
                continue;
            }

            if (current is JsonArray currentArray && int.TryParse(segment, out var index))
            {
                current = index >= 0 && index < currentArray.Count ? currentArray[index] : null;
                continue;
            }

            return null;
        }

        return current?.GetValue<string>();
    }

    private static bool? ReadBool(JsonObject? obj, string path)
    {
        if (obj is null)
        {
            return null;
        }

        JsonNode? current = obj;
        foreach (var segment in path.Split('.'))
        {
            if (current is JsonObject currentObject)
            {
                current = currentObject[segment];
                continue;
            }

            if (current is JsonArray currentArray && int.TryParse(segment, out var index))
            {
                current = index >= 0 && index < currentArray.Count ? currentArray[index] : null;
                continue;
            }

            return null;
        }

        return current is JsonValue value && value.TryGetValue<bool>(out var boolValue)
            ? boolValue
            : null;
    }

    private static string? FormatConfigOrigin(JsonObject? origin)
    {
        if (origin is null)
        {
            return null;
        }

        if (origin["name"] is JsonValue nameValue &&
            nameValue.TryGetValue<string>(out var nameText))
        {
            return nameText;
        }

        if (origin["name"] is JsonObject source)
        {
            return ReadString(source, "file") ??
                   ReadString(source, "path") ??
                   ReadString(source, "type");
        }

        return ReadString(origin, "path") ?? ReadString(origin, "type");
    }

    private static string? FormatApprovalPolicy(JsonNode? value)
    {
        if (value is JsonObject)
        {
            return "granular";
        }

        return value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
            ? text
            : null;
    }

    private static DateTimeOffset? ReadUnixTimestamp(JsonObject obj, string path)
    {
        JsonNode? current = obj;
        foreach (var segment in path.Split('.'))
        {
            current = current is JsonObject currentObject ? currentObject[segment] : null;
        }

        if (current is JsonValue value && value.TryGetValue<long>(out var seconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        return null;
    }

    private static IReadOnlyList<CodexReasoningOption> ReadReasoningEfforts(JsonObject model)
    {
        if (model["supportedReasoningEfforts"] is not JsonArray efforts)
        {
            return [];
        }

        var values = new List<CodexReasoningOption>();
        foreach (var item in efforts.OfType<JsonObject>())
        {
            var effort = ParseReasoningEffort(ReadString(item, "reasoningEffort"));
            if (effort is not null)
            {
                values.Add(new CodexReasoningOption(
                    effort.Value,
                    ReadString(item, "description") ?? string.Empty));
            }
        }

        return values;
    }

    private static IReadOnlyList<CodexServiceTierOption> ReadServiceTiers(JsonObject model)
    {
        if (model["serviceTiers"] is not JsonArray tiers)
        {
            return [];
        }

        var values = new List<CodexServiceTierOption>();
        foreach (var item in tiers.OfType<JsonObject>())
        {
            var id = ReadString(item, "id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                values.Add(new CodexServiceTierOption(
                    id,
                    ReadString(item, "name") ?? id,
                    ReadString(item, "description") ?? string.Empty));
            }
        }

        return values;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonObject source, string propertyName)
    {
        if (source[propertyName] is not JsonArray items)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in items.OfType<JsonValue>())
        {
            if (item.TryGetValue<string>(out var value) && !string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static IReadOnlyList<CodexInputModality>? ReadInputModalities(JsonObject source)
    {
        if (source["inputModalities"] is not JsonArray items)
        {
            return null;
        }

        var values = new List<CodexInputModality>();
        foreach (var item in items.OfType<JsonValue>())
        {
            if (!item.TryGetValue<string>(out var value))
            {
                continue;
            }
            var modality = value.ToLowerInvariant() switch
            {
                "text" => CodexInputModality.Text,
                "image" => CodexInputModality.Image,
                _ => (CodexInputModality?)null
            };
            if (modality is { } known && !values.Contains(known))
            {
                values.Add(known);
            }
        }
        return values;
    }

    private static CodexSandbox? ParseSandbox(string? value) => value?.ToLowerInvariant() switch
    {
        "read-only" or "readonly" => CodexSandbox.ReadOnly,
        "workspace-write" or "workspacewrite" => CodexSandbox.WorkspaceWrite,
        "danger-full-access" or "dangerfullaccess" => CodexSandbox.DangerFullAccess,
        _ => null
    };

    private static CodexApprovalPolicy? ParseApprovalPolicy(JsonNode? value)
    {
        if (value is JsonObject)
        {
            return CodexApprovalPolicy.Granular;
        }

        if (value is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var text))
        {
            return null;
        }

        return text.ToLowerInvariant() switch
        {
            "untrusted" or "unlesstrusted" => CodexApprovalPolicy.Untrusted,
            "on-request" or "onrequest" => CodexApprovalPolicy.OnRequest,
            "never" => CodexApprovalPolicy.Never,
            "on-failure" or "onfailure" => CodexApprovalPolicy.OnFailureDeprecated,
            _ => null
        };
    }

    private static CodexApprovalsReviewer? ParseApprovalsReviewer(string? value) => value?.ToLowerInvariant() switch
    {
        "user" => CodexApprovalsReviewer.User,
        "auto_review" or "autoreview" => CodexApprovalsReviewer.AutoReview,
        "guardian_subagent" or "guardiansubagent" => CodexApprovalsReviewer.GuardianSubagentLegacy,
        _ => null
    };

    private static string NormalizeProjectTrustPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A project path is required.", nameof(path));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool ProjectTrustPathsEqual(string configuredPath, string normalizedPath)
    {
        if (string.Equals(configuredPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            return string.Equals(
                NormalizeProjectTrustPath(configuredPath),
                normalizedPath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static IReadOnlyList<CodexSandbox> ParseSandboxArray(JsonArray? values)
    {
        if (values is null)
        {
            return [];
        }

        return values
            .Select(value => value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) ? ParseSandbox(text) : null)
            .OfType<CodexSandbox>()
            .Distinct()
            .ToList();
    }

    private static IReadOnlyList<CodexApprovalPolicy> ParseApprovalPolicyArray(JsonArray? values)
    {
        if (values is null)
        {
            return [];
        }

        return values
            .Select(ParseApprovalPolicy)
            .OfType<CodexApprovalPolicy>()
            .Distinct()
            .ToList();
    }

    private static IReadOnlyList<CodexApprovalsReviewer> ParseApprovalsReviewerArray(JsonArray? values)
    {
        if (values is null)
        {
            return [];
        }

        return values
            .Select(value => value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
                ? ParseApprovalsReviewer(text)
                : null)
            .OfType<CodexApprovalsReviewer>()
            .Distinct()
            .ToList();
    }

    private static CodexReasoningEffort? ParseReasoningEffort(string? value) => value?.ToLowerInvariant() switch
    {
        "none" => CodexReasoningEffort.None,
        "minimal" => CodexReasoningEffort.Minimal,
        "low" => CodexReasoningEffort.Low,
        "medium" => CodexReasoningEffort.Medium,
        "high" => CodexReasoningEffort.High,
        "xhigh" => CodexReasoningEffort.XHigh,
        _ => null
    };

    private static IReadOnlyList<CodexConversationTurnSnapshot> ParseConversationTurns(JsonArray? turns)
    {
        if (turns is null)
        {
            return [];
        }

        var parsed = new List<CodexConversationTurnSnapshot>();
        foreach (var turn in turns.OfType<JsonObject>())
        {
            var turnId = ReadString(turn, "id");
            if (string.IsNullOrWhiteSpace(turnId))
            {
                continue;
            }

            var prompts = new List<string>();
            var assistantMessages = new List<string>();
            var generatedImagePaths = new List<string>();
            var activity = new List<CodexTimelineItem>();
            var isCodeReview = false;
            var reviewScope = string.Empty;
            if (turn["items"] is JsonArray items)
            {
                foreach (var item in items.OfType<JsonObject>())
                {
                    switch (ReadString(item, "type"))
                    {
                        case "userMessage" when item["content"] is JsonArray content:
                            prompts.AddRange(content
                                .OfType<JsonObject>()
                                .Where(input => string.Equals(ReadString(input, "type"), "text", StringComparison.Ordinal))
                                .Select(input => ReadString(input, "text"))
                                .Where(text => !string.IsNullOrWhiteSpace(text))!);
                            break;
                        case "agentMessage":
                            var message = ReadString(item, "text");
                            if (!string.IsNullOrWhiteSpace(message))
                            {
                                assistantMessages.Add(UnicodeTextNormalizer.RepairLegacyMojibake(message));
                            }
                            break;
                        case "imageGeneration":
                            var savedPath = ReadString(item, "savedPath");
                            if (string.Equals(ReadString(item, "status"), "completed", StringComparison.Ordinal) &&
                                !string.IsNullOrWhiteSpace(savedPath) &&
                                !generatedImagePaths.Contains(savedPath, StringComparer.OrdinalIgnoreCase))
                            {
                                generatedImagePaths.Add(savedPath);
                            }
                            break;
                        case "collabAgentToolCall":
                            activity.Add(ParseCollaborationActivity(item));
                            break;
                        case "enteredReviewMode":
                            isCodeReview = true;
                            reviewScope = ReadString(item, "review") ?? reviewScope;
                            activity.Add(ParseReviewActivity(item, completed: false));
                            break;
                        case "exitedReviewMode":
                            isCodeReview = true;
                            var review = ReadString(item, "review");
                            if (!string.IsNullOrWhiteSpace(review))
                            {
                                assistantMessages.Add(UnicodeTextNormalizer.RepairLegacyMojibake(review));
                            }
                            activity.Add(ParseReviewActivity(item, completed: true));
                            break;
                    }
                }
            }

            parsed.Add(new CodexConversationTurnSnapshot
            {
                TurnId = turnId,
                UserPrompt = string.Join(Environment.NewLine, prompts),
                AssistantResponse = assistantMessages.LastOrDefault() ?? string.Empty,
                Diff = ReadString(turn, "diff") ?? string.Empty,
                Status = ParseTurnStatus(ReadString(turn, "status")),
                StartedAt = ReadUnixTimestamp(turn, "startedAt") ?? DateTimeOffset.UtcNow,
                CompletedAt = ReadUnixTimestamp(turn, "completedAt"),
                Activity = activity,
                GeneratedImagePaths = generatedImagePaths,
                IsCodeReview = isCodeReview,
                ReviewScope = reviewScope
            });
        }

        return parsed;
    }

    private static CodexTimelineItem ParseReviewActivity(JsonObject item, bool completed)
    {
        var itemId = ReadString(item, "id") ?? $"review:{Guid.NewGuid():N}";
        var detail = UnicodeTextNormalizer.RepairLegacyMojibake(ReadString(item, "review") ?? string.Empty);
        return new CodexTimelineItem(
            CodexTimelineItemKind.CodeReview,
            completed ? "Code review completed" : "Code review started",
            detail,
            "item/codeReview",
            DateTimeOffset.UtcNow)
        {
            ItemId = itemId,
            ActivityKey = $"review:{itemId}"
        };
    }

    private static CodexTimelineItem ParseCollaborationActivity(JsonObject item)
    {
        var itemId = ReadString(item, "id") ?? $"restored:{Guid.NewGuid():N}";
        var tool = ReadString(item, "tool") ?? "agent task";
        var status = ReadString(item, "status");
        var prompt = ReadString(item, "prompt");
        var detail = string.IsNullOrWhiteSpace(prompt) ? tool : $"{tool}: {prompt}";
        var title = string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
            ? "Delegated work failed"
            : "Delegated work";
        var receiverThreadIds = item["receiverThreadIds"] is JsonArray receivers
            ? receivers
                .OfType<JsonValue>()
                .Select(value => value.TryGetValue<string>(out var threadId) ? threadId : null)
                .Where(threadId => !string.IsNullOrWhiteSpace(threadId))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : [];
        var agentStates = item["agentsStates"] is JsonObject states
            ? states
                .Where(pair => pair.Value is JsonObject)
                .Select(pair =>
                {
                    var state = (JsonObject)pair.Value!;
                    return new CodexCollaborationAgentState(
                        pair.Key,
                        ReadString(state, "status") ?? "notFound",
                        ReadString(state, "message"));
                })
                .ToArray()
            : [];

        return new CodexTimelineItem(
            CodexTimelineItemKind.Collaboration,
            title,
            detail,
            "item/collaboration",
            DateTimeOffset.UtcNow)
        {
            ItemId = itemId,
            ActivityKey = $"collaboration:{itemId}",
            CollaborationTool = tool,
            CollaborationStatus = status,
            CollaborationPrompt = prompt,
            CollaborationModel = ReadString(item, "model"),
            CollaborationSenderThreadId = ReadString(item, "senderThreadId"),
            CollaborationReceiverThreadIds = receiverThreadIds,
            CollaborationAgentStates = agentStates
        };
    }

    private static CodexTurnStatus ParseTurnStatus(string? status) => status switch
    {
        "inProgress" => CodexTurnStatus.Running,
        "completed" => CodexTurnStatus.Completed,
        "interrupted" => CodexTurnStatus.Cancelled,
        "failed" => CodexTurnStatus.Failed,
        _ => CodexTurnStatus.Idle
    };

    public async ValueTask DisposeAsync()
    {
        connection.ConnectionFailed -= OnConnectionFailed;
        lock (gate)
        {
            pendingIncomingRequests.Clear();
            respondingIncomingRequests.Clear();
        }

        await connection.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class CodexAppServerProtocolException : Exception
{
    public CodexAppServerProtocolException(string message, int? code)
        : base(message)
    {
        Code = code;
    }

    public CodexAppServerProtocolException(string message)
        : base(message)
    {
    }

    public CodexAppServerProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public int? Code { get; }
}
