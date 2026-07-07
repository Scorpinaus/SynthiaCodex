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
    private int nextRequestId;

    public CodexAppServerClient(IAppServerTransport transport, CodexAppServerClientMetadata metadata)
    {
        this.transport = transport;
        this.metadata = metadata;
    }

    public event EventHandler<AppServerNotification>? NotificationReceived;

    public async Task<CodexAppServerSession> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        var parameters = new JsonObject
        {
            ["clientInfo"] = new JsonObject
            {
                ["name"] = metadata.Name,
                ["title"] = metadata.Title,
                ["version"] = metadata.Version
            }
        };

        var response = await SendRequestForResponseAsync("initialize", parameters, cancellationToken).ConfigureAwait(false);
        await SendNotificationAsync("initialized", new JsonObject(), cancellationToken).ConfigureAwait(false);

        await using var registration = cancellationToken.Register(() => CancelPendingResponse(response, cancellationToken));
        var result = await response.Task.WaitAsync(cancellationToken).ConfigureAwait(false) as JsonObject;
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
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            CompleteAllPending(ex);
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
            CompleteAllPending(new CodexAppServerProtocolException("App-server emitted invalid JSON.", ex));
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
