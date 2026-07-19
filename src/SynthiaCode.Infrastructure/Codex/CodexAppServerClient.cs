using System.Text.Json;
using System.Text.Json.Nodes;
using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.Infrastructure.Codex;

public sealed class CodexAppServerClient : IAsyncDisposable
{
    private readonly IAppServerTransport transport;
    private readonly CodexAppServerClientMetadata metadata;
    private readonly CancellationTokenSource readLoopCancellation = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly Dictionary<int, TaskCompletionSource<JsonNode?>> pendingRequests = [];
    private readonly HashSet<CodexRequestId> pendingIncomingRequests = [];
    private readonly HashSet<CodexRequestId> respondingIncomingRequests = [];
    private readonly object gate = new();
    private Task? readLoop;
    private bool started;
    private int connectionFailureReported;
    private int nextRequestId;

    public CodexAppServerClient(IAppServerTransport transport, CodexAppServerClientMetadata metadata)
    {
        this.transport = transport;
        this.metadata = metadata;
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

        var parameters = new JsonObject
        {
            ["clientInfo"] = new JsonObject
            {
                ["name"] = metadata.Name,
                ["title"] = metadata.Title,
                ["version"] = metadata.Version
            },
            ["capabilities"] = new JsonObject
            {
                ["experimentalApi"] = options.ExperimentalApi,
                ["optOutNotificationMethods"] = options.OptOutNotificationMethods is null
                    ? null
                    : new JsonArray(options.OptOutNotificationMethods.Select(method => JsonValue.Create(method)).ToArray())
            }
        };

        var response = await SendRequestForResponseAsync("initialize", parameters, cancellationToken).ConfigureAwait(false);
        await SendNotificationAsync("initialized", new JsonObject(), cancellationToken).ConfigureAwait(false);

        await using var registration = cancellationToken.Register(() => CancelPendingResponse(response, cancellationToken));
        var result = await response.Task.WaitAsync(cancellationToken).ConfigureAwait(false) as JsonObject;
        IsHealthy = true;
        return new CodexAppServerSession(
            ReadString(result, "userAgent"),
            ReadString(result, "platformFamily"),
            ReadString(result, "platformOs"));
    }

    public async Task<CodexThreadStartResult> StartThreadAsync(
        CodexThreadStartOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidatePermissionBoundary(options.Sandbox, options.PermissionProfileId);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        var parameters = new JsonObject();
        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            parameters["model"] = options.Model;
        }

        if (options.Sandbox is not null)
        {
            parameters["sandbox"] = options.Sandbox.Value.ToProtocolValue();
        }

        AddPermissionProfile(parameters, options.PermissionProfileId);

        AddApprovalPolicyOverrides(parameters, options.ApprovalPolicy, options.ApprovalsReviewer);

        var result = await SendRequestAsync("thread/start", parameters, cancellationToken).ConfigureAwait(false) as JsonObject;
        var threadId = ReadString(result, "thread.id");
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new CodexAppServerProtocolException("thread/start response did not include result.thread.id.");
        }

        return new CodexThreadStartResult(threadId, ParseActivePermissionProfile(result));
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

        var result = await SendRequestAsync("thread/resume", parameters, cancellationToken).ConfigureAwait(false) as JsonObject;
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
                        ReadStringArray(item, "additionalSpeedTiers")));
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

    public async Task<CodexAccountReadResult> ReadAccountAsync(
        bool refreshToken = false,
        CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var result = await SendRequestAsync(
            "account/read",
            new JsonObject
            {
                ["refreshToken"] = refreshToken
            },
            cancellationToken).ConfigureAwait(false) as JsonObject;
        return CodexAccountProtocolParser.ParseAccount(result);
    }

    public async Task<CodexAccountRateLimitsResult> ReadAccountRateLimitsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var result = await SendRequestAsync(
            "account/rateLimits/read",
            parameters: null,
            cancellationToken).ConfigureAwait(false) as JsonObject;
        return CodexAccountProtocolParser.ParseRateLimits(result);
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

        var result = await SendRequestAsync("thread/fork", parameters, cancellationToken).ConfigureAwait(false) as JsonObject;
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

    public async Task<CodexTurnSteerResult> SteerTurnAsync(
        CodexTurnSteerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new ArgumentException("Steering prompt is required.", nameof(request));
        }

        var result = await SendRequestAsync(
            "turn/steer",
            new JsonObject
            {
                ["threadId"] = request.ThreadId,
                ["expectedTurnId"] = request.ExpectedTurnId,
                ["input"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = request.Prompt
                    }
                }
            },
            cancellationToken).ConfigureAwait(false) as JsonObject;
        var turnId = ReadString(result, "turnId");
        if (string.IsNullOrWhiteSpace(turnId))
        {
            throw new CodexAppServerProtocolException("turn/steer response did not include result.turnId.");
        }

        return new CodexTurnSteerResult(turnId);
    }

    public async Task<CodexTurnStartResult> StartTurnAsync(
        CodexTurnStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ThreadId))
        {
            throw new ArgumentException("Thread ID is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new ArgumentException("Prompt is required.", nameof(request));
        }

        ValidatePermissionBoundary(request.Sandbox, request.PermissionProfileId);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        var parameters = new JsonObject
        {
            ["threadId"] = request.ThreadId,
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = request.Prompt
                }
            },
            ["cwd"] = request.Cwd
        };

        if (request.Sandbox is not null)
        {
            parameters["sandboxPolicy"] = request.Sandbox.Value.ToTurnSandboxPolicy();
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

        var result = await SendRequestAsync("turn/start", parameters, cancellationToken).ConfigureAwait(false) as JsonObject;
        var turnId = ReadString(result, "turn.id");
        if (string.IsNullOrWhiteSpace(turnId))
        {
            throw new CodexAppServerProtocolException("turn/start response did not include result.turn.id.");
        }

        return new CodexTurnStartResult(turnId);
    }

    public async Task CancelTurnAsync(string threadId, string turnId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("Thread ID is required.", nameof(threadId));
        }

        if (string.IsNullOrWhiteSpace(turnId))
        {
            throw new ArgumentException("Turn ID is required.", nameof(turnId));
        }

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        await SendRequestAsync(
            "turn/interrupt",
            new JsonObject
            {
                ["threadId"] = threadId,
                ["turnId"] = turnId
            },
            cancellationToken).ConfigureAwait(false);
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

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (started)
        {
            return;
        }

        await transport.StartAsync(cancellationToken).ConfigureAwait(false);
        readLoop = Task.Run(() => ReadLoopAsync(readLoopCancellation.Token), CancellationToken.None);
        started = true;
    }

    private async Task<JsonNode?> SendRequestAsync(
        string method,
        JsonObject? parameters,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestForResponseAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        await using var registration = cancellationToken.Register(() => CancelPendingResponse(response, cancellationToken));
        return await response.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<PendingResponse> SendRequestForResponseAsync(
        string method,
        JsonObject? parameters,
        CancellationToken cancellationToken)
    {
        var id = AllocateRequestId();
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            pendingRequests[id] = completion;
        }

        var message = new JsonObject
        {
            ["method"] = method,
            ["id"] = id
        };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        try
        {
            await WriteMessageAsync(message, cancellationToken).ConfigureAwait(false);
            return new PendingResponse(id, completion.Task, completion);
        }
        catch
        {
            lock (gate)
            {
                pendingRequests.Remove(id);
            }

            throw;
        }
    }

    private Task SendNotificationAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        var message = new JsonObject
        {
            ["method"] = method,
            ["params"] = parameters
        };

        return WriteMessageAsync(message, cancellationToken);
    }

    private async Task WriteMessageAsync(JsonObject message, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await transport.WriteLineAsync(message.ToJsonString(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var line in transport.ReadLinesAsync(cancellationToken).ConfigureAwait(false))
            {
                await ProcessLineAsync(line, cancellationToken).ConfigureAwait(false);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                ReportConnectionFailure(new EndOfStreamException("Codex app-server closed its output stream."));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ReportConnectionFailure(ex);
        }
    }

    private void ReportConnectionFailure(Exception exception)
    {
        IsHealthy = false;
        CompleteAllPending(exception);
        if (Interlocked.Exchange(ref connectionFailureReported, 1) == 0)
        {
            ConnectionFailed?.Invoke(this, new AppServerConnectionFailedEventArgs(exception));
        }
    }

    private async Task ProcessLineAsync(string line, CancellationToken cancellationToken)
    {
        JsonObject message;
        try
        {
            message = JsonNode.Parse(line) as JsonObject ??
                throw new CodexAppServerProtocolException("App-server message was not a JSON object.");
        }
        catch (JsonException ex)
        {
            ReportConnectionFailure(new CodexAppServerProtocolException("App-server emitted invalid JSON.", ex));
            return;
        }

        var method = ReadString(message, "method");
        if (message["id"] is not null && !string.IsNullOrWhiteSpace(method))
        {
            if (!TryReadRequestId(message["id"], out var requestId))
            {
                return;
            }

            var serverParams = message["params"] as JsonObject;
            RegisterIncomingRequest(requestId);
            string? parseError = null;
            if (serverParams is null || !TryParseServerRequest(method, serverParams, requestId, out var request, out parseError))
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

        if (message["id"] is not null)
        {
            CompletePendingRequest(message);
            return;
        }

        if (string.IsNullOrWhiteSpace(method))
        {
            return;
        }

        var parameters = message["params"] as JsonObject ?? new JsonObject();
        if (method == "serverRequest/resolved" &&
            TryReadRequestId(parameters["requestId"] ?? parameters["id"], out var resolvedRequestId))
        {
            lock (gate)
            {
                pendingIncomingRequests.Remove(resolvedRequestId);
            }
        }
        NotificationReceived?.Invoke(this, new AppServerNotification(method, parameters));
    }

    private void CompletePendingRequest(JsonObject message)
    {
        var id = message["id"]!.GetValue<int>();
        TaskCompletionSource<JsonNode?>? completion;
        lock (gate)
        {
            if (!pendingRequests.Remove(id, out completion))
            {
                return;
            }
        }

        if (message["error"] is JsonObject error)
        {
            var code = error["code"]?.GetValue<int?>() ?? 0;
            var errorMessage = error["message"]?.GetValue<string>() ?? "App-server request failed.";
            completion.TrySetException(new CodexAppServerProtocolException($"App-server error {code}: {errorMessage}", code));
            return;
        }

        completion.TrySetResult(message["result"]?.DeepClone());
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

    private static bool TryParseServerRequest(
        string method,
        JsonObject parameters,
        CodexRequestId requestId,
        out CodexServerRequest request,
        out string? error)
    {
        error = null;
        CodexServerRequestPayload payload;
        switch (method)
        {
            case "item/commandExecution/requestApproval":
                if (!TryReadApprovalCorrelation(parameters, out var commandThreadId, out var commandTurnId, out var commandItemId, out var commandStartedAt, out error))
                {
                    request = null!;
                    return false;
                }

                CodexNetworkApprovalContext? networkContext = null;
                if (parameters["networkApprovalContext"] is JsonObject network)
                {
                    var host = ReadString(network, "host");
                    var protocol = ReadString(network, "protocol");
                    if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(protocol))
                    {
                        networkContext = new CodexNetworkApprovalContext(host, protocol, ReadInt(network, "port"));
                    }
                }

                payload = new CodexCommandApprovalRequest(
                    commandThreadId,
                    commandTurnId,
                    commandItemId,
                    commandStartedAt,
                    ReadString(parameters, "command"),
                    ReadString(parameters, "cwd"),
                    ReadString(parameters, "reason"),
                    networkContext,
                    ReadStringArray(parameters, "proposedExecpolicyAmendment"),
                    ReadStringArray(parameters, "availableDecisions"),
                    ReadString(parameters, "approvalId"));
                break;

            case "item/fileChange/requestApproval":
                if (!TryReadApprovalCorrelation(parameters, out var fileThreadId, out var fileTurnId, out var fileItemId, out var fileStartedAt, out error))
                {
                    request = null!;
                    return false;
                }

                payload = new CodexFileChangeApprovalRequest(
                    fileThreadId,
                    fileTurnId,
                    fileItemId,
                    fileStartedAt,
                    ReadString(parameters, "reason"),
                    ReadString(parameters, "grantRoot"));
                break;

            case "item/permissions/requestApproval":
                if (!TryReadApprovalCorrelation(parameters, out var permissionThreadId, out var permissionTurnId, out var permissionItemId, out var permissionStartedAt, out error))
                {
                    request = null!;
                    return false;
                }

                var cwd = ReadString(parameters, "cwd");
                var permissions = parameters["permissions"] as JsonObject;
                if (string.IsNullOrWhiteSpace(cwd) || permissions is null)
                {
                    request = null!;
                    error = "Permission approval requires cwd and permissions.";
                    return false;
                }

                payload = new CodexPermissionApprovalRequest(
                    permissionThreadId,
                    permissionTurnId,
                    permissionItemId,
                    permissionStartedAt,
                    cwd,
                    ReadString(parameters, "reason"),
                    (JsonObject)permissions.DeepClone());
                break;

            default:
                payload = new CodexUnsupportedServerRequest(method);
                break;
        }

        request = new CodexServerRequest(
            requestId,
            method,
            (JsonObject)parameters.DeepClone(),
            payload);
        return true;
    }

    private static bool TryReadApprovalCorrelation(
        JsonObject parameters,
        out string threadId,
        out string turnId,
        out string itemId,
        out long startedAtMs,
        out string? error)
    {
        threadId = ReadString(parameters, "threadId") ?? string.Empty;
        turnId = ReadString(parameters, "turnId") ?? string.Empty;
        itemId = ReadString(parameters, "itemId") ?? string.Empty;
        startedAtMs = ReadLong(parameters, "startedAtMs") ?? 0;
        if (string.IsNullOrWhiteSpace(threadId) ||
            string.IsNullOrWhiteSpace(turnId) ||
            string.IsNullOrWhiteSpace(itemId) ||
            ReadLong(parameters, "startedAtMs") is null)
        {
            error = "Approval request is missing threadId, turnId, itemId, or startedAtMs.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryReadRequestId(JsonNode? value, out CodexRequestId requestId)
    {
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<long>(out var integer))
        {
            requestId = CodexRequestId.FromInteger(integer);
            return true;
        }

        if (value is JsonValue stringValue && stringValue.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text))
        {
            requestId = CodexRequestId.FromString(text);
            return true;
        }

        requestId = default;
        return false;
    }

    private void CompleteAllPending(Exception exception)
    {
        List<TaskCompletionSource<JsonNode?>> completions;
        lock (gate)
        {
            completions = [.. pendingRequests.Values];
            pendingRequests.Clear();
            pendingIncomingRequests.Clear();
            respondingIncomingRequests.Clear();
        }

        foreach (var completion in completions)
        {
            completion.TrySetException(exception);
        }
    }

    private int AllocateRequestId()
    {
        lock (gate)
        {
            return nextRequestId++;
        }
    }

    private void CancelPendingResponse(PendingResponse response, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            pendingRequests.Remove(response.Id);
        }

        response.Completion.TrySetCanceled(cancellationToken);
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
                    }
                }
            }

            parsed.Add(new CodexConversationTurnSnapshot
            {
                TurnId = turnId,
                UserPrompt = string.Join(Environment.NewLine, prompts),
                AssistantResponse = assistantMessages.LastOrDefault() ?? string.Empty,
                Status = ParseTurnStatus(ReadString(turn, "status")),
                StartedAt = ReadUnixTimestamp(turn, "startedAt") ?? DateTimeOffset.UtcNow,
                CompletedAt = ReadUnixTimestamp(turn, "completedAt")
            });
        }

        return parsed;
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
        readLoopCancellation.Cancel();
        await transport.StopAsync().ConfigureAwait(false);
        if (readLoop is not null)
        {
            try
            {
                await readLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        readLoopCancellation.Dispose();
        writeGate.Dispose();
        await transport.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed record PendingResponse(
    int Id,
    Task<JsonNode?> Task,
    TaskCompletionSource<JsonNode?> Completion);

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
