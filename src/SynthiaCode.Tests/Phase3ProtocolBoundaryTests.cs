using System.Reflection;
using System.Text.Json.Nodes;
using SynthiaCode.App.Services;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Infrastructure.Codex;
using Xunit;

public sealed class Phase3ProtocolBoundaryTests
{
    [Fact]
    public void Protocol_boundary_has_one_public_facade_and_internal_parts()
    {
        var assembly = typeof(CodexClient).Assembly;

        Assert.True(typeof(CodexClient).IsPublic);
        Assert.Equal(typeof(CodexClient), typeof(CodexAppServerClient).BaseType);
        var coordinatorClient = typeof(AppServerSessionCoordinator)
            .GetField("client", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Equal(typeof(CodexClient), coordinatorClient?.FieldType);
        Assert.NotNull(assembly.GetType("SynthiaCode.Infrastructure.Codex.JsonRpcConnection"));
        Assert.NotNull(assembly.GetType("SynthiaCode.Infrastructure.Codex.CodexNotificationParser"));
        Assert.NotNull(assembly.GetType("SynthiaCode.Infrastructure.Codex.CodexServerRequestParser"));

        var codecTypes = assembly.GetTypes()
            .Where(type => type.Namespace == "SynthiaCode.Infrastructure.Codex.Codecs")
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("SessionCodexCodec", codecTypes);
        Assert.Contains("ThreadCodexCodec", codecTypes);
        Assert.Contains("TurnCodexCodec", codecTypes);
        Assert.Contains("SkillsCodexCodec", codecTypes);
        Assert.Contains("AccountCodexCodec", codecTypes);
    }

    [Fact]
    public async Task Json_rpc_connection_correlates_out_of_order_responses()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = new CodexClient(transport, TestMetadata());
        await CompleteInitializeAsync(client, transport);

        var accountTask = client.ReadAccountAsync();
        var rateLimitsTask = client.ReadAccountRateLimitsAsync();
        await transport.WaitForClientMessageCountAsync(4);

        var accountRequest = ParseMessage(transport.ClientMessages[2]);
        var rateLimitsRequest = ParseMessage(transport.ClientMessages[3]);
        var accountId = accountRequest["id"]!.GetValue<int>();
        var rateLimitsId = rateLimitsRequest["id"]!.GetValue<int>();

        transport.ServerSend(new JsonObject
        {
            ["id"] = rateLimitsId,
            ["result"] = new JsonObject
            {
                ["rateLimits"] = new JsonObject
                {
                    ["primary"] = new JsonObject { ["usedPercent"] = 25 }
                }
            }
        }.ToJsonString());
        transport.ServerSend(new JsonObject
        {
            ["id"] = accountId,
            ["result"] = new JsonObject
            {
                ["account"] = new JsonObject { ["type"] = "apiKey" }
            }
        }.ToJsonString());

        var account = await accountTask;
        var rateLimits = await rateLimitsTask;
        Assert.Equal("apiKey", account.Account?.Type);
        Assert.Equal(25, rateLimits.Limits[0].Primary?.UsedPercent);
    }

    [Fact]
    public async Task Parsers_route_notifications_and_typed_server_requests()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = new CodexClient(transport, TestMetadata());
        await CompleteInitializeAsync(client, transport);

        var notification = new TaskCompletionSource<AppServerNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverRequest = new TaskCompletionSource<CodexServerRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.NotificationReceived += (_, value) => notification.TrySetResult(value);
        client.ServerRequestReceived += (_, value) => serverRequest.TrySetResult(value);

        transport.ServerSend("""{"method":"turn/started","params":{"threadId":"thr_3","turn":{"id":"turn_3"}}}""");
        transport.ServerSend("""{"id":"approval-3","method":"item/fileChange/requestApproval","params":{"threadId":"thr_3","turnId":"turn_3","itemId":"item_3","startedAtMs":3,"reason":"Write files"}}""");

        var parsedNotification = await notification.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var parsedRequest = await serverRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("turn/started", parsedNotification.Method);
        Assert.IsType<CodexFileChangeApprovalRequest>(parsedRequest.Payload);
        Assert.Equal("approval-3", parsedRequest.RequestId.StringValue);
    }

    [Fact]
    public async Task Invalid_json_fails_the_connection_and_pending_request()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = new CodexClient(transport, TestMetadata());
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionFailed += (_, args) => failure.TrySetResult(args.Exception);

        var initializeTask = client.InitializeAsync();
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("{invalid-json");

        var exception = await failure.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsType<CodexAppServerProtocolException>(exception);
        await Assert.ThrowsAsync<CodexAppServerProtocolException>(() => initializeTask);
        await Assert.ThrowsAsync<CodexAppServerProtocolException>(() => client.ReadAccountAsync());
        Assert.Equal(2, transport.ClientMessages.Count);
        Assert.False(client.IsHealthy);
    }

    private static async Task CompleteInitializeAsync(CodexClient client, FakeAppServerTransport transport)
    {
        var initializeTask = client.InitializeAsync();
        await transport.WaitForClientMessageCountAsync(2);
        var request = ParseMessage(transport.ClientMessages[0]);
        var id = request["id"]!.GetValue<int>();
        transport.ServerSend(new JsonObject
        {
            ["id"] = id,
            ["result"] = new JsonObject { ["userAgent"] = "phase3" }
        }.ToJsonString());
        await initializeTask;
    }

    private static CodexAppServerClientMetadata TestMetadata() =>
        new("phase3_protocol", "Phase 3 Protocol", "1.0");

    private static JsonObject ParseMessage(string value) =>
        JsonNode.Parse(value) as JsonObject ?? throw new InvalidDataException("Expected a JSON object.");
}
