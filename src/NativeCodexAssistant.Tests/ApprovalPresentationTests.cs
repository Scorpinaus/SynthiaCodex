using System.Text.Json.Nodes;
using NativeCodexAssistant.App.ViewModels;
using NativeCodexAssistant.Core.Codex.AppServer;
using NativeCodexAssistant.Core.Settings;
using NativeCodexAssistant.Infrastructure.Settings;

internal static class ApprovalPresentationTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("approval queue serializes prompts and responses", SerializesPromptsAndResponsesAsync),
        ("approval queue resolves stale prompts", ResolvesStalePromptsAsync),
        ("permission approvals preserve requested permissions and scope", PreservesPermissionScopeAsync),
        ("execution policy defaults and explicit inheritance persist", PersistsExecutionPolicyAsync),
        ("execution policy enforces confirmations and requirements", EnforcesPolicySafetyAsync),
        ("approval and execution-policy controls are present in WPF", PresentsApprovalControlsAsync)
    ];

    private static async Task SerializesPromptsAndResponsesAsync()
    {
        var responses = new List<(CodexServerRequest Request, CodexServerRequestResponse Response)>();
        var queue = new ApprovalQueueViewModel((request, response, _) =>
        {
            responses.Add((request, response));
            return Task.CompletedTask;
        });
        var first = CommandRequest("request-1");
        var second = FileRequest("request-2");

        queue.Enqueue(first);
        queue.Enqueue(second);

        Assert(queue.PendingCount == 2, "both requests are retained");
        Assert(queue.ActivePrompt?.Request.RequestId == first.RequestId, "the oldest request is shown first");
        await queue.RespondAsync(CodexApprovalDecision.Accept);
        Assert(responses.Count == 1, "one response is sent");
        Assert(responses[0].Response.Result["decision"]?.GetValue<string>() == "accept", "allow once is serialized");
        Assert(queue.ActivePrompt?.Request.RequestId == second.RequestId, "the next request becomes active");
    }

    private static Task ResolvesStalePromptsAsync()
    {
        var queue = new ApprovalQueueViewModel((_, _, _) => Task.CompletedTask);
        var first = CommandRequest("request-3");
        var second = FileRequest("request-4");
        queue.Enqueue(first);
        queue.Enqueue(second);

        Assert(queue.Resolve(first.RequestId), "the active request can be invalidated");
        Assert(queue.ActivePrompt?.Request.RequestId == second.RequestId, "invalidation advances the queue");
        Assert(!queue.Resolve(first.RequestId), "an already removed request is ignored");
        return Task.CompletedTask;
    }

    private static async Task PreservesPermissionScopeAsync()
    {
        CodexServerRequestResponse? captured = null;
        var queue = new ApprovalQueueViewModel((_, response, _) =>
        {
            captured = response;
            return Task.CompletedTask;
        });
        var permissions = new JsonObject
        {
            ["network"] = new JsonObject { ["enabled"] = true },
            ["fileSystem"] = new JsonObject { ["read"] = new JsonArray("D:\\Repo") }
        };
        queue.Enqueue(new CodexServerRequest(
            CodexRequestId.FromInteger(77),
            "item/permissions/requestApproval",
            new JsonObject(),
            new CodexPermissionApprovalRequest("thr", "turn", "item", 1, "D:\\Repo", "Need access", permissions)));

        var permissionOptions = queue.ActivePrompt?.PermissionOptions ??
            throw new InvalidOperationException("permission controls are available");
        Assert(permissionOptions.Count == 2, "each requested top-level permission is selectable");
        permissionOptions.Single(option => option.Name == "network").IsGranted = false;

        await queue.ApprovePermissionsAsync(CodexPermissionGrantScope.Session);

        Assert(captured?.Result["scope"]?.GetValue<string>() == "session", "session permission scope is serialized");
        Assert(captured?.Result["permissions"]?["network"] is null, "deselected permissions are omitted");
        Assert(captured?.Result["permissions"]?["fileSystem"]?.ToJsonString() == permissions["fileSystem"]?.ToJsonString(), "selected permissions retain their requested scope");
    }

    private static async Task PersistsExecutionPolicyAsync()
    {
        var settings = new AppSettings();
        Assert(settings.SandboxModeOverride == "workspace-write", "new installations default to workspace-write");
        Assert(settings.ApprovalPolicyOverride == "on-request", "new installations default to on-request approvals");

        settings.SandboxModeOverride = null;
        settings.ApprovalPolicyOverride = null;
        var snapshot = AppSettingsSnapshot.Create(settings);
        Assert(snapshot.SandboxModeOverride is null && snapshot.ApprovalPolicyOverride is null, "explicit inheritance survives snapshots");

        using var temp = TempWorkspace.Create();
        var store = new JsonSettingsStore(temp.Root, new TestLogger());
        await store.SaveAsync(settings);
        var reloaded = await store.LoadAsync();
        Assert(reloaded.SandboxModeOverride is null && reloaded.ApprovalPolicyOverride is null, "explicit inheritance survives a settings round trip");
    }

    private static Task EnforcesPolicySafetyAsync()
    {
        var confirmations = 0;
        var policy = new ExecutionPolicyViewModel((_, _) =>
        {
            confirmations++;
            return false;
        });
        policy.Initialize("workspace-write", "on-request");

        Assert(!policy.TrySelectSandbox(CodexSandbox.DangerFullAccess), "full access is rejected without confirmation");
        Assert(!policy.TrySelectApprovalPolicy(CodexApprovalPolicy.Never), "never approve is rejected without confirmation");
        Assert(confirmations == 2, "both dangerous settings require confirmation");
        Assert(policy.SandboxOverride == CodexSandbox.WorkspaceWrite && policy.ApprovalPolicyOverride == CodexApprovalPolicy.OnRequest, "rejected changes retain safe values");

        policy.ApplyRequirements(new CodexExecutionPolicyRequirements(
            [CodexSandbox.ReadOnly],
            [CodexApprovalPolicy.Untrusted]));
        Assert(!policy.TrySelectSandbox(CodexSandbox.WorkspaceWrite), "managed sandbox restrictions are enforced");
        Assert(!policy.TrySelectApprovalPolicy(CodexApprovalPolicy.OnRequest), "managed approval restrictions are enforced");
        Assert(policy.IsManaged, "managed state is exposed to the UI");
        return Task.CompletedTask;
    }

    private static Task PresentsApprovalControlsAsync()
    {
        var root = FindRepositoryRoot();
        var mainWindow = File.ReadAllText(Path.Combine(root, "src", "NativeCodexAssistant.App", "MainWindow.xaml"));
        var details = File.ReadAllText(Path.Combine(root, "src", "NativeCodexAssistant.App", "Views", "DetailsView.xaml"));
        var approval = File.ReadAllText(Path.Combine(root, "src", "NativeCodexAssistant.App", "Views", "ApprovalPromptView.xaml"));

        Assert(mainWindow.Contains("views:ApprovalPromptView", StringComparison.Ordinal), "the approval overlay is hosted by the main window");
        Assert(details.Contains("ExecutionPolicy.SelectedSandboxMode", StringComparison.Ordinal), "sandbox settings are configurable");
        Assert(details.Contains("ExecutionPolicy.SelectedApprovalPolicy", StringComparison.Ordinal), "approval settings are configurable");
        Assert(approval.Contains("ApprovalQueue.AllowOnceCommand", StringComparison.Ordinal), "allow-once action is presented");
        Assert(approval.Contains("ApprovalQueue.AllowSessionCommand", StringComparison.Ordinal), "allow-for-session action is presented");
        Assert(approval.Contains("ApprovalQueue.DeclineCommand", StringComparison.Ordinal), "decline action is presented");
        return Task.CompletedTask;
    }

    private static CodexServerRequest CommandRequest(string id) => new(
        CodexRequestId.FromString(id),
        "item/commandExecution/requestApproval",
        new JsonObject(),
        new CodexCommandApprovalRequest("thr", "turn", "item", 1, "dotnet test", "D:\\Repo", "Verify", null, [], [], null));

    private static CodexServerRequest FileRequest(string id) => new(
        CodexRequestId.FromString(id),
        "item/fileChange/requestApproval",
        new JsonObject(),
        new CodexFileChangeApprovalRequest("thr", "turn", "item", 1, "Write files", "D:\\Repo"));

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "NativeCodexAssistant.App")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
