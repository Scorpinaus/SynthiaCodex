using System.Text.Json.Nodes;
using NativeCodexAssistant.Core.Codex.AppServer;
using NativeCodexAssistant.Infrastructure.Codex;

internal static class ApprovalProtocolTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("approval protocol classifies string-id server requests", ClassifiesStringIdServerRequestsAsync),
        ("approval protocol responds exactly once with original numeric id", RespondsExactlyOnceWithOriginalNumericIdAsync),
        ("approval protocol rejects unsupported server requests", RejectsUnsupportedServerRequestsAsync),
        ("approval protocol invalidates resolved server requests", InvalidatesResolvedServerRequestsAsync),
        ("execution policies serialize on thread and turn requests", ExecutionPoliciesSerializeAsync),
        ("execution policy config and requirements are read", ReadsExecutionPolicyConfigAndRequirementsAsync)
    ];

    private static async Task ClassifiesStringIdServerRequestsAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = CreateClient(transport);
        await CompleteInitializeAsync(client, transport);
        var received = new TaskCompletionSource<CodexServerRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ServerRequestReceived += (_, request) => received.TrySetResult(request);

        transport.ServerSend(
            """
            {"id":"approval-7","method":"item/commandExecution/requestApproval","params":{"threadId":"thr_1","turnId":"turn_1","itemId":"item_1","startedAtMs":1784420000000,"command":"dotnet test","cwd":"D:\\Repo","reason":"Run verification","networkApprovalContext":{"host":"api.example.com","protocol":"https"}}}
            """);

        var request = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(request.RequestId.StringValue == "approval-7", "string request id is retained");
        Assert(request.Method == "item/commandExecution/requestApproval", "request method is retained");
        var command = request.Payload as CodexCommandApprovalRequest ??
            throw new InvalidOperationException("command request is parsed to a typed payload");
        Assert(command.ThreadId == "thr_1" && command.TurnId == "turn_1" && command.ItemId == "item_1", "command correlation is retained");
        Assert(command.Command == "dotnet test" && command.Cwd == @"D:\Repo", "command display fields are retained");
        Assert(command.NetworkContext?.Host == "api.example.com" && command.NetworkContext.Protocol == "https", "network context is retained");
        Assert(transport.ClientMessages.Count == 2, "server request is not mistaken for an outgoing response");
    }

    private static async Task RespondsExactlyOnceWithOriginalNumericIdAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = CreateClient(transport);
        await CompleteInitializeAsync(client, transport);
        var received = new TaskCompletionSource<CodexServerRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ServerRequestReceived += (_, request) => received.TrySetResult(request);
        transport.ServerSend(
            """
            {"id":9223372036854770000,"method":"item/fileChange/requestApproval","params":{"threadId":"thr_2","turnId":"turn_2","itemId":"item_2","startedAtMs":1784420000000,"reason":"Write generated files","grantRoot":"D:\\Repo\\generated"}}
            """);

        var request = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(request.RequestId.IntegerValue == 9223372036854770000L, "large numeric request id is retained");
        var fileChange = request.Payload as CodexFileChangeApprovalRequest;
        Assert(fileChange?.GrantRoot == @"D:\Repo\generated", "file grant root is parsed");

        await client.RespondToServerRequestAsync(
            request,
            CodexServerRequestResponse.FileChange(CodexApprovalDecision.AcceptForSession));
        await transport.WaitForClientMessageCountAsync(3);
        var response = ParseMessage(transport.ClientMessages[2]);
        Assert(response["id"]?.GetValue<long>() == 9223372036854770000L, "response uses original numeric id");
        Assert(response["result"]?["decision"]?.GetValue<string>() == "acceptForSession", "response serializes decision");

        await AssertThrowsAsync<InvalidOperationException>(
            () => client.RespondToServerRequestAsync(
                request,
                CodexServerRequestResponse.FileChange(CodexApprovalDecision.Decline)),
            "a server request cannot be answered twice");
        Assert(transport.ClientMessages.Count == 3, "duplicate response is not written");
    }

    private static async Task RejectsUnsupportedServerRequestsAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = CreateClient(transport);
        await CompleteInitializeAsync(client, transport);

        transport.ServerSend("""{"id":"future-1","method":"future/approval","params":{"threadId":"thr_future"}}""");
        await transport.WaitForClientMessageCountAsync(3);
        var response = ParseMessage(transport.ClientMessages[2]);
        Assert(response["id"]?.GetValue<string>() == "future-1", "unsupported response retains request id");
        Assert(response["error"]?["code"]?.GetValue<int>() == -32601, "unsupported request fails with method-not-found");
        Assert(response["error"]?["message"]?.GetValue<string>().Contains("future/approval", StringComparison.Ordinal) == true, "unsupported response identifies method");
    }

    private static async Task InvalidatesResolvedServerRequestsAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = CreateClient(transport);
        await CompleteInitializeAsync(client, transport);
        var received = new TaskCompletionSource<CodexServerRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ServerRequestReceived += (_, request) => received.TrySetResult(request);
        client.NotificationReceived += (_, notification) =>
        {
            if (notification.Method == "serverRequest/resolved")
            {
                resolved.TrySetResult();
            }
        };
        transport.ServerSend(
            """
            {"id":"approval-resolved","method":"item/fileChange/requestApproval","params":{"threadId":"thr","turnId":"turn","itemId":"item","startedAtMs":1,"reason":"Write"}}
            """);
        var request = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));

        transport.ServerSend(
            """
            {"method":"serverRequest/resolved","params":{"requestId":"approval-resolved"}}
            """);
        await resolved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await AssertThrowsAsync<InvalidOperationException>(() =>
            client.RespondToServerRequestAsync(request, CodexServerRequestResponse.FileChange(CodexApprovalDecision.Accept)),
            "a resolved request rejects late responses");
        Assert(transport.ClientMessages.Count == 2, "a resolved request cannot write a late response");
    }

    private static async Task ExecutionPoliciesSerializeAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = CreateClient(transport);
        await CompleteInitializeAsync(client, transport);

        var startThread = client.StartThreadAsync(new CodexThreadStartOptions(
            Model: "gpt-test",
            Sandbox: CodexSandbox.ReadOnly,
            ApprovalPolicy: CodexApprovalPolicy.Untrusted,
            ApprovalsReviewer: CodexApprovalsReviewer.User));
        await transport.WaitForClientMessageCountAsync(3);
        var threadRequest = ParseMessage(transport.ClientMessages[2]);
        Assert(threadRequest["params"]?["sandbox"]?.GetValue<string>() == "read-only", "thread sandbox is serialized");
        Assert(threadRequest["params"]?["approvalPolicy"]?.GetValue<string>() == "untrusted", "thread approval policy is serialized");
        Assert(threadRequest["params"]?["approvalsReviewer"]?.GetValue<string>() == "user", "thread reviewer is serialized");
        transport.ServerSend("""{"id":1,"result":{"thread":{"id":"thr_policy"}}}""");
        await startThread;

        var startTurn = client.StartTurnAsync(new CodexTurnStartRequest(
            "thr_policy",
            "Verify policy.",
            @"D:\Repo",
            CodexSandbox.WorkspaceWrite,
            ApprovalPolicy: CodexApprovalPolicy.OnRequest,
            ApprovalsReviewer: CodexApprovalsReviewer.User));
        await transport.WaitForClientMessageCountAsync(4);
        var turnRequest = ParseMessage(transport.ClientMessages[3]);
        Assert(turnRequest["params"]?["sandboxPolicy"]?["type"]?.GetValue<string>() == "workspaceWrite", "turn sandbox policy is structured");
        Assert(turnRequest["params"]?["approvalPolicy"]?.GetValue<string>() == "on-request", "turn approval policy is serialized");
        Assert(turnRequest["params"]?["approvalsReviewer"]?.GetValue<string>() == "user", "turn reviewer is serialized");
        transport.ServerSend("""{"id":2,"result":{"turn":{"id":"turn_policy"}}}""");
        await startTurn;

        var inherited = client.StartThreadAsync(CodexThreadStartOptions.Default);
        await transport.WaitForClientMessageCountAsync(5);
        var inheritedParams = ParseMessage(transport.ClientMessages[4])["params"]!.AsObject();
        Assert(!inheritedParams.ContainsKey("sandbox") && !inheritedParams.ContainsKey("approvalPolicy") && !inheritedParams.ContainsKey("approvalsReviewer"), "inherit omits policy overrides");
        transport.ServerSend("""{"id":3,"result":{"thread":{"id":"thr_inherit"}}}""");
        await inherited;
    }

    private static async Task ReadsExecutionPolicyConfigAndRequirementsAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = CreateClient(transport);
        await CompleteInitializeAsync(client, transport);

        var configTask = client.ReadExecutionPolicyConfigAsync(@"D:\Repo");
        await transport.WaitForClientMessageCountAsync(3);
        var configRequest = ParseMessage(transport.ClientMessages[2]);
        Assert(configRequest["method"]?.GetValue<string>() == "config/read", "effective config uses config/read");
        Assert(configRequest["params"]?["cwd"]?.GetValue<string>() == @"D:\Repo", "effective config is project scoped");
        transport.ServerSend(
            """
            {"id":1,"result":{"config":{"sandbox_mode":"workspace-write","approval_policy":"on-request","approvals_reviewer":"user","sandbox_workspace_write":{"network_access":false}},"origins":{"sandbox_mode":{"name":"user","path":"C:\\Users\\me\\.codex\\config.toml"}}}}
            """);
        var config = await configTask;
        Assert(config.Sandbox == CodexSandbox.WorkspaceWrite, "effective sandbox is parsed");
        Assert(config.ApprovalPolicy == CodexApprovalPolicy.OnRequest, "effective approval policy is parsed");
        Assert(config.ApprovalsReviewer == CodexApprovalsReviewer.User, "effective reviewer is parsed");
        Assert(config.WorkspaceWriteNetworkAccess == false, "effective network setting is parsed");

        var requirementsTask = client.ReadExecutionPolicyRequirementsAsync();
        await transport.WaitForClientMessageCountAsync(4);
        Assert(ParseMessage(transport.ClientMessages[3])["method"]?.GetValue<string>() == "configRequirements/read", "requirements use configRequirements/read");
        transport.ServerSend(
            """
            {"id":2,"result":{"requirements":{"allowedSandboxModes":["read-only","workspace-write"],"allowedApprovalPolicies":["untrusted","on-request"]}}}
            """);
        var requirements = await requirementsTask;
        Assert(requirements.AllowedSandboxes.SequenceEqual([CodexSandbox.ReadOnly, CodexSandbox.WorkspaceWrite]), "allowed sandboxes are parsed");
        Assert(requirements.AllowedApprovalPolicies.SequenceEqual([CodexApprovalPolicy.Untrusted, CodexApprovalPolicy.OnRequest]), "allowed approval policies are parsed");
    }

    private static CodexAppServerClient CreateClient(FakeAppServerTransport transport) => new(
        transport,
        new CodexAppServerClientMetadata("approval_tests", "Approval Tests", "1.0.0"));

    private static async Task CompleteInitializeAsync(CodexAppServerClient client, FakeAppServerTransport transport)
    {
        var initialize = client.InitializeAsync();
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"test","platformFamily":"windows","platformOs":"windows"}}""");
        await initialize;
    }

    private static JsonObject ParseMessage(string value) =>
        JsonNode.Parse(value)?.AsObject() ?? throw new InvalidOperationException("Expected a JSON object.");

    private static async Task AssertThrowsAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
