using System.Text.Json;
using System.Text.Json.Nodes;
using NativeCodexAssistant.Core.Codex.AppServer;

namespace NativeCodexAssistant.Infrastructure.Codex;

public sealed class CodexAppServerClient : IAsyncDisposable
{
    private readonly IAppServerTransport transport;
    private readonly CodexAppServerClientMetadata metadata;
    private readonly CancellationTokenSource readLoopCancellation = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly Dictionary<int, TaskCompletionSource<JsonNode?>> pendingRequests = [];
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

        var result = await SendRequestAsync("thread/start", parameters, cancellationToken).ConfigureAwait(false) as JsonObject;
        var threadId = ReadString(result, "thread.id");
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new CodexAppServerProtocolException("thread/start response did not include result.thread.id.");
        }

        return new CodexThreadStartResult(threadId);
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

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        var parameters = new JsonObject
        {
            ["threadId"] = request.ThreadId,
            ["cwd"] = request.Cwd,
            ["sandbox"] = request.Sandbox.ToProtocolValue()
        };

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            parameters["model"] = request.Model;
        }

        var result = await SendRequestAsync("thread/resume", parameters, cancellationToken).ConfigureAwait(false) as JsonObject;
        var threadId = ReadString(result, "thread.id");
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new CodexAppServerProtocolException("thread/resume response did not include result.thread.id.");
        }

        return new CodexThreadResumeResult(
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

        var result = await SendRequestAsync(
            "model/list",
            new JsonObject
            {
                ["includeHidden"] = false
            },
            cancellationToken).ConfigureAwait(false) as JsonObject;

        var data = result?["data"] as JsonArray;
        if (data is null)
        {
            return [];
        }

        var models = new List<CodexModelOption>();
        foreach (var item in data.OfType<JsonObject>())
        {
            var model = ReadString(item, "model") ?? ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(model))
            {
                continue;
            }

            models.Add(new CodexModelOption(
                ReadString(item, "id") ?? model,
                model,
                ReadString(item, "displayName") ?? model,
                ReadBool(item, "isDefault") ?? false,
                ReadReasoningEfforts(item)));
        }

        return models;
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
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var parameters = new JsonObject
        {
            ["threadId"] = request.ThreadId,
            ["cwd"] = request.Cwd,
            ["sandbox"] = request.Sandbox.ToProtocolValue()
        };
        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            parameters["model"] = request.Model;
        }

        var result = await SendRequestAsync("thread/fork", parameters, cancellationToken).ConfigureAwait(false) as JsonObject;
        var threadId = ReadString(result, "thread.id");
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new CodexAppServerProtocolException("thread/fork response did not include result.thread.id.");
        }

        return new CodexThreadForkResult(threadId);
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
            ["cwd"] = request.Cwd,
            ["sandboxPolicy"] = request.Sandbox.ToTurnSandboxPolicy()
        };

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            parameters["model"] = request.Model;
        }

        if (request.ReasoningEffort is not null)
        {
            parameters["effort"] = request.ReasoningEffort.Value.ToProtocolValue();
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
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestForResponseAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        await using var registration = cancellationToken.Register(() => CancelPendingResponse(response, cancellationToken));
        return await response.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<PendingResponse> SendRequestForResponseAsync(
        string method,
        JsonObject parameters,
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
            ["id"] = id,
            ["params"] = parameters
        };

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
                ProcessLine(line);
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

    private void ProcessLine(string line)
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

        if (message["id"] is not null)
        {
            CompletePendingRequest(message);
            return;
        }

        var method = ReadString(message, "method");
        if (string.IsNullOrWhiteSpace(method))
        {
            return;
        }

        var parameters = message["params"] as JsonObject ?? new JsonObject();
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
            completion.TrySetException(new CodexAppServerProtocolException($"App-server error {code}: {errorMessage}"));
            return;
        }

        completion.TrySetResult(message["result"]?.DeepClone());
    }

    private void CompleteAllPending(Exception exception)
    {
        List<TaskCompletionSource<JsonNode?>> completions;
        lock (gate)
        {
            completions = [.. pendingRequests.Values];
            pendingRequests.Clear();
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

    private static IReadOnlyList<string> ReadReasoningEfforts(JsonObject model)
    {
        if (model["supportedReasoningEfforts"] is not JsonArray efforts)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in efforts.OfType<JsonObject>())
        {
            var effort = ReadString(item, "reasoningEffort");
            if (!string.IsNullOrWhiteSpace(effort))
            {
                values.Add(effort);
            }
        }

        return values;
    }

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
    public CodexAppServerProtocolException(string message)
        : base(message)
    {
    }

    public CodexAppServerProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
