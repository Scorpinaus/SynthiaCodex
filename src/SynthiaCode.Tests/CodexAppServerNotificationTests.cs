using System.Text.Json.Nodes;
using SynthiaCode.Core.Codex.AppServer;

[Trait("Category", TestCategories.ProtocolContract)]
public sealed class CodexAppServerNotificationTests
{


    [Fact(DisplayName = "typed app-server decoder maps every supported notification kind")]
    public Task MapsEverySupportedKindAsync()
    {
        var cases = new (string Method, CodexAppServerNotificationKind Kind)[]
        {
            ("thread/started", CodexAppServerNotificationKind.ThreadStarted),
            ("thread/archived", CodexAppServerNotificationKind.ThreadArchived),
            ("thread/unarchived", CodexAppServerNotificationKind.ThreadUnarchived),
            ("thread/goal/updated", CodexAppServerNotificationKind.ThreadGoalUpdated),
            ("thread/goal/cleared", CodexAppServerNotificationKind.ThreadGoalCleared),
            ("thread/tokenUsage/updated", CodexAppServerNotificationKind.ThreadTokenUsageUpdated),
            ("thread/compacted", CodexAppServerNotificationKind.ThreadCompacted),
            ("turn/started", CodexAppServerNotificationKind.TurnStarted),
            ("turn/completed", CodexAppServerNotificationKind.TurnCompleted),
            ("turn/plan/updated", CodexAppServerNotificationKind.TurnPlanUpdated),
            ("item/started", CodexAppServerNotificationKind.ItemStarted),
            ("item/completed", CodexAppServerNotificationKind.ItemCompleted),
            ("item/agentMessage/delta", CodexAppServerNotificationKind.AgentMessageDelta),
            ("item/mcpToolCall/progress", CodexAppServerNotificationKind.McpToolCallProgress),
            ("account/rateLimits/updated", CodexAppServerNotificationKind.AccountRateLimitsUpdated),
            ("account/updated", CodexAppServerNotificationKind.AccountUpdated),
            ("account/login/completed", CodexAppServerNotificationKind.AccountLoginCompleted),
            ("skills/changed", CodexAppServerNotificationKind.SkillsChanged),
            ("serverRequest/resolved", CodexAppServerNotificationKind.ServerRequestResolved)
        };

        foreach (var (method, expectedKind) in cases)
        {
            var notification = Decode(method, "{}");
            Assert(notification.Kind == expectedKind, $"{method} maps to {expectedKind}");
        }

        Assert(
            Decode("account/future", "{}").Kind == CodexAppServerNotificationKind.AccountNotification,
            "unrecognized account notifications retain the account family");
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "typed app-server decoder routes top-level and nested identifiers")]
    public Task DecodesRoutingShapesAsync()
    {
        var topLevel = Decode(
            CodexAppServerNotificationMethods.TurnCompleted,
            """{"threadId":"thread-top","turnId":"turn-top","itemId":"item-top","status":"completed"}""");
        var nested = Decode(
            CodexAppServerNotificationMethods.TurnCompleted,
            """{"turn":{"id":"turn-1","threadId":"thread-1","status":"cancelled"},"item":{"id":"item-1"}}""");

        Assert(topLevel.ThreadId == "thread-top" && topLevel.TurnId == "turn-top" && topLevel.ItemId == "item-top", "top-level routing identifiers are decoded");
        Assert(topLevel.TurnStatus == "completed", "top-level turn status is decoded");
        Assert(nested.ThreadId == "thread-1", "turn thread identifier alternate shape is routed");
        Assert(nested.TurnId == "turn-1", "nested turn identifier is routed");
        Assert(nested.ItemId == "item-1", "nested item identifier is routed");
        Assert(nested.TurnStatus == "cancelled", "nested turn status is decoded");
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "typed app-server decoder exposes archive lifecycle transitions")]
    public Task DecodesArchiveLifecycleAsync()
    {
        var archived = Decode(CodexAppServerNotificationMethods.ThreadArchived, """{"thread":{"id":"thread-1"}}""");
        var unarchived = Decode(CodexAppServerNotificationMethods.ThreadUnarchived, """{"threadId":"thread-1"}""");

        Assert(archived.IsArchived == true && archived.ThreadId == "thread-1", "archive lifecycle state is decoded");
        Assert(unarchived.IsArchived == false && unarchived.ThreadId == "thread-1", "unarchive lifecycle state is decoded");
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "typed app-server decoder handles account skills and approval lifecycle")]
    public Task DecodesAccountSkillsAndApprovalLifecycleAsync()
    {
        var account = Decode(
            CodexAppServerNotificationMethods.AccountRateLimitsUpdated,
            """{"rateLimits":{"limitId":"codex"}}""");
        var accountUpdated = Decode(CodexAppServerNotificationMethods.AccountUpdated, "{}");
        var loginCompleted = Decode(CodexAppServerNotificationMethods.AccountLoginCompleted, "{}");
        var skillsChanged = Decode(CodexAppServerNotificationMethods.SkillsChanged, "{}");
        var stringRequest = Decode(
            CodexAppServerNotificationMethods.ServerRequestResolved,
            """{"id":"approval-1"}""");
        var integerRequest = Decode(
            CodexAppServerNotificationMethods.ServerRequestResolved,
            """{"requestId":42}""");

        Assert(account.Kind == CodexAppServerNotificationKind.AccountRateLimitsUpdated, "rate-limit notification is decoded");
        Assert(account.RateLimits?["limitId"]?.GetValue<string>() == "codex", "rate-limit payload is exposed without reparsing");
        Assert(accountUpdated.Kind == CodexAppServerNotificationKind.AccountUpdated, "account refresh notification is decoded");
        Assert(loginCompleted.Kind == CodexAppServerNotificationKind.AccountLoginCompleted, "account login notification is decoded");
        Assert(skillsChanged.Kind == CodexAppServerNotificationKind.SkillsChanged, "skills invalidation notification is decoded");
        Assert(stringRequest.RequestId == CodexRequestId.FromString("approval-1"), "string approval request identifier is decoded");
        Assert(integerRequest.RequestId == CodexRequestId.FromInteger(42), "integer approval request identifier is decoded");
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "typed app-server decoder drives workspace and thread projections")]
    public Task DrivesTypedThreadProjectionsAsync()
    {
        var workspace = new CodexThreadWorkspace();
        workspace.Restore(new SynthiaCode.Core.Settings.ProjectThreadState { ProjectPath = @"C:\Repo", ThreadId = "thread-1" });
        var delta = Decode(
            CodexAppServerNotificationMethods.AgentMessageDelta,
            """{"threadId":"thread-1","turnId":"turn-1","itemId":"item-1","delta":"typed response"}""");
        var routedThreadId = workspace.ApplyNotification(delta);

        var service = workspace.GetRequired("thread-1");
        service.ApplyNotification(Decode(
            CodexAppServerNotificationMethods.TurnCompleted,
            """{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed"}}"""));

        Assert(routedThreadId == "thread-1", "typed workspace routes by decoded thread identifier");
        Assert(service.FinalResponse == "typed response", "typed thread service projects streamed response");
        Assert(service.ActiveTurnStatus == CodexTurnStatus.Completed, "typed thread service projects decoded completion status");
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "typed app-server decoder preserves unknown wire notifications")]
    public Task PreservesUnknownNotificationAsync()
    {
        var notification = Decode("future/notification", """{"threadId":"thread-1","value":7}""");

        Assert(notification.Kind == CodexAppServerNotificationKind.Unknown, "unknown methods remain forward compatible");
        Assert(notification.Method == "future/notification", "unknown raw method is preserved");
        Assert(notification.Params["value"]?.GetValue<int>() == 7, "unknown raw payload is preserved");
        Assert(notification.ThreadId == "thread-1", "known routing fields are still available for unknown methods");
        return Task.CompletedTask;
    }

    private static CodexAppServerNotification Decode(string method, string parameters) =>
        CodexAppServerNotification.Decode(new AppServerNotification(method, JsonNode.Parse(parameters)!.AsObject()));

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
