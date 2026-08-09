using System.Text.Json;
using System.Text.Json.Nodes;
using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.Infrastructure.Codex;

internal sealed class JsonRpcConnection : IAsyncDisposable
{
    private readonly IAppServerTransport transport;
    private readonly Func<JsonObject, CancellationToken, Task> messageHandler;
    private readonly CancellationTokenSource readLoopCancellation = new();
    private readonly SemaphoreSlim startGate = new(1, 1);
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly Dictionary<int, TaskCompletionSource<JsonNode?>> pendingRequests = [];
    private readonly object gate = new();
    private Task? readLoop;
    private volatile bool started;
    private Exception? connectionFailure;
    private int connectionFailureReported;
    private int nextRequestId;

    public JsonRpcConnection(
        IAppServerTransport transport,
        Func<JsonObject, CancellationToken, Task> messageHandler)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.messageHandler = messageHandler ?? throw new ArgumentNullException(nameof(messageHandler));
    }

    public event EventHandler<AppServerConnectionFailedEventArgs>? ConnectionFailed;

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        if (started)
        {
            return;
        }

        await startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (started)
            {
                return;
            }

            await transport.StartAsync(cancellationToken).ConfigureAwait(false);
            readLoop = Task.Run(() => ReadLoopAsync(readLoopCancellation.Token), CancellationToken.None);
            started = true;
        }
        finally
        {
            startGate.Release();
        }
    }

    public async Task<JsonNode?> SendRequestAsync(
        string method,
        JsonObject? parameters,
        CancellationToken cancellationToken = default)
    {
        var response = await BeginRequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        await using var registration = cancellationToken.Register(() => CancelPendingResponse(response, cancellationToken));
        return await response.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonRpcPendingResponse> BeginRequestAsync(
        string method,
        JsonObject? parameters,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ArgumentException("A JSON-RPC method is required.", nameof(method));
        }

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var id = AllocateRequestId();
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            ThrowIfFailedLocked();
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
            return new JsonRpcPendingResponse(id, completion.Task, completion);
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

    public Task SendNotificationAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ArgumentException("A JSON-RPC method is required.", nameof(method));
        }

        ArgumentNullException.ThrowIfNull(parameters);
        return WriteMessageAsync(
            new JsonObject
            {
                ["method"] = method,
                ["params"] = parameters
            },
            cancellationToken);
    }

    public Task SendMessageAsync(JsonObject message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return WriteMessageAsync(message, cancellationToken);
    }

    public void CancelPendingResponse(
        JsonRpcPendingResponse response,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            pendingRequests.Remove(response.Id);
        }

        response.Completion.TrySetCanceled(cancellationToken);
    }

    private async Task WriteMessageAsync(JsonObject message, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (gate)
            {
                ThrowIfFailedLocked();
            }

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
                var message = ParseMessage(line);
                if (IsResponse(message))
                {
                    CompletePendingRequest(message);
                    continue;
                }

                await messageHandler(message, cancellationToken).ConfigureAwait(false);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                ReportConnectionFailure(new EndOfStreamException("Codex app-server closed its output stream."));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportConnectionFailure(ex);
        }
    }

    private static JsonObject ParseMessage(string line)
    {
        try
        {
            return JsonNode.Parse(line) as JsonObject ??
                throw new CodexAppServerProtocolException("App-server message was not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new CodexAppServerProtocolException("App-server emitted invalid JSON.", ex);
        }
    }

    private static bool IsResponse(JsonObject message) =>
        message["id"] is not null &&
        string.IsNullOrWhiteSpace(message["method"]?.GetValue<string>());

    private void CompletePendingRequest(JsonObject message)
    {
        if (message["id"] is not JsonValue idValue || !idValue.TryGetValue<int>(out var id))
        {
            return;
        }

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
            completion.TrySetException(
                new CodexAppServerProtocolException($"App-server error {code}: {errorMessage}", code));
            return;
        }

        completion.TrySetResult(message["result"]?.DeepClone());
    }

    private void ReportConnectionFailure(Exception exception)
    {
        CompleteAllPending(exception);
        if (Interlocked.Exchange(ref connectionFailureReported, 1) == 0)
        {
            ConnectionFailed?.Invoke(this, new AppServerConnectionFailedEventArgs(exception));
        }
    }

    private void CompleteAllPending(Exception exception)
    {
        List<TaskCompletionSource<JsonNode?>> completions;
        lock (gate)
        {
            connectionFailure ??= exception;
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

    private void ThrowIfFailedLocked()
    {
        if (connectionFailure is not null)
        {
            throw new CodexAppServerProtocolException(
                "The JSON-RPC connection is not available.",
                connectionFailure);
        }
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

        CompleteAllPending(new ObjectDisposedException(nameof(JsonRpcConnection)));
        readLoopCancellation.Dispose();
        startGate.Dispose();
        writeGate.Dispose();
        await transport.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed record JsonRpcPendingResponse(
    int Id,
    Task<JsonNode?> Task,
    TaskCompletionSource<JsonNode?> Completion);
