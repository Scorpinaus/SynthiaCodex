using System.Text.Json.Nodes;
using SynthiaCode.App.ViewModels;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Settings;
using SynthiaCode.Infrastructure.Codex;

internal static class Phase5GModelControlsTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("phase 5g model catalog retains picker capabilities", ModelCatalogRetainsPickerCapabilitiesAsync),
        ("phase 5g turn service tier supports inherit fast and off", TurnServiceTierSupportsInheritFastAndOffAsync),
        ("phase 5g selection reconciles model reasoning and fast", SelectionReconcilesModelReasoningAndFastAsync),
        ("phase 5g service tier preference survives settings snapshot", ServiceTierPreferenceSurvivesSettingsSnapshotAsync)
    ];

    private static async Task ModelCatalogRetainsPickerCapabilitiesAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(
            transport,
            new CodexAppServerClientMetadata("phase_5g_tests", "Phase 5G Tests", "1.0.0"));
        await CompleteInitializeAsync(client, transport);

        var modelsTask = client.ListModelsAsync();
        await transport.WaitForClientMessageCountAsync(3);
        var firstPageRequest = ParseMessage(transport.ClientMessages[2]);
        Assert(firstPageRequest["params"]?["limit"]?.GetValue<int>() == 100, "catalog requests a complete model page");
        Assert(firstPageRequest["params"]?["includeHidden"]?.GetValue<bool>() == false, "picker excludes hidden models");
        transport.ServerSend(
            """
            {"id":1,"result":{"data":[{"id":"sol","model":"gpt-5.6-sol","displayName":"GPT-5.6 Sol","description":"Strong coding model","isDefault":true,"hidden":false,"defaultReasoningEffort":"high","availabilityNux":{"message":"Available for this workspace"},"supportedReasoningEfforts":[{"reasoningEffort":"medium","description":"Balanced"},{"reasoningEffort":"high","description":"Deeper reasoning"}],"additionalSpeedTiers":["fast"],"serviceTiers":[{"id":"priority","name":"Fast","description":"Faster responses at higher credit use"}]}],"nextCursor":"page-2"}}
            """);

        await transport.WaitForClientMessageCountAsync(4);
        var secondPageRequest = ParseMessage(transport.ClientMessages[3]);
        Assert(secondPageRequest["params"]?["cursor"]?.GetValue<string>() == "page-2", "catalog follows pagination cursor");
        transport.ServerSend(
            """
            {"id":2,"result":{"data":[{"id":"sol-duplicate","model":"gpt-5.6-sol","displayName":"Duplicate Sol","description":"Duplicate","isDefault":false,"hidden":false,"defaultReasoningEffort":"medium","supportedReasoningEfforts":[],"serviceTiers":[]},{"id":"luna","model":"gpt-5.6-luna","displayName":"GPT-5.6 Luna","description":"Lighter model","isDefault":false,"hidden":false,"defaultReasoningEffort":"low","supportedReasoningEfforts":[{"reasoningEffort":"low","description":"Focused"}],"serviceTiers":[]}]}}
            """);

        var models = await modelsTask;
        Assert(models.Count == 2, "catalog merges pages and removes duplicate protocol models");
        var model = models[0];
        Assert(model.Id == "sol", "catalog retains model ID");
        Assert(model.Model == "gpt-5.6-sol", "catalog retains protocol model");
        Assert(model.DisplayName == "GPT-5.6 Sol", "catalog retains display name");
        Assert(model.Description == "Strong coding model", "catalog retains description");
        Assert(model.IsDefault && !model.Hidden, "catalog retains default and hidden state");
        Assert(model.DefaultReasoningEffort == CodexReasoningEffort.High, "catalog retains default effort");
        Assert(model.SupportedReasoningEfforts.Count == 2, "catalog retains supported efforts");
        Assert(model.SupportedReasoningEfforts[1].Effort == CodexReasoningEffort.High, "catalog parses typed effort");
        Assert(model.SupportedReasoningEfforts[1].Description == "Deeper reasoning", "catalog retains effort description");
        Assert(model.AdditionalSpeedTiers?.Single() == "fast", "catalog retains Fast speed capability");
        Assert(model.ServiceTiers.Single().Id == "priority", "catalog retains service tier ID");
        Assert(model.ServiceTiers.Single().Description.Contains("higher credit", StringComparison.Ordinal), "catalog retains service tier description");
        Assert(model.AvailabilityMessage == "Available for this workspace", "catalog retains availability message");
    }

    private static async Task TurnServiceTierSupportsInheritFastAndOffAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(
            transport,
            new CodexAppServerClientMetadata("phase_5g_tests", "Phase 5G Tests", "1.0.0"));
        await CompleteInitializeAsync(client, transport);

        var inherit = client.StartTurnAsync(Request(CodexServiceTierSelection.Inherit));
        await transport.WaitForClientMessageCountAsync(3);
        var inheritRequest = ParseMessage(transport.ClientMessages[2]);
        Assert(!inheritRequest["params"]!.AsObject().ContainsKey("serviceTier"), "inherit omits service tier");
        transport.ServerSend("""{"id":1,"result":{"turn":{"id":"turn_inherit"}}}""");
        await inherit;

        var fast = client.StartTurnAsync(Request(CodexServiceTierSelection.Fast));
        await transport.WaitForClientMessageCountAsync(4);
        var fastRequest = ParseMessage(transport.ClientMessages[3]);
        Assert(fastRequest["params"]?["serviceTier"]?.GetValue<string>() == "fast", "fast sends service tier");
        transport.ServerSend("""{"id":2,"result":{"turn":{"id":"turn_fast"}}}""");
        await fast;

        var standard = client.StartTurnAsync(Request(CodexServiceTierSelection.Standard));
        await transport.WaitForClientMessageCountAsync(5);
        var standardRequest = ParseMessage(transport.ClientMessages[4]);
        var standardParams = standardRequest["params"]!.AsObject();
        Assert(standardParams.ContainsKey("serviceTier"), "explicit off includes service tier property");
        Assert(standardParams["serviceTier"] is null, "explicit off clears service tier with null");
        transport.ServerSend("""{"id":3,"result":{"turn":{"id":"turn_standard"}}}""");
        await standard;
    }

    private static Task SelectionReconcilesModelReasoningAndFastAsync()
    {
        var viewModel = new TaskViewModel(
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => false,
            () => false);
        viewModel.ModelOverride = "removed-model";
        viewModel.ReasoningEffortOverride = "xhigh";
        viewModel.ServiceTierSelection = CodexServiceTierSelection.Fast;

        var sol = Model(
            "sol",
            "gpt-5.6-sol",
            "GPT-5.6 Sol",
            true,
            CodexReasoningEffort.High,
            [CodexReasoningEffort.Medium, CodexReasoningEffort.High],
            supportsFast: true);
        var luna = Model(
            "luna",
            "gpt-5.6-luna",
            "GPT-5.6 Luna",
            false,
            CodexReasoningEffort.Low,
            [CodexReasoningEffort.Minimal, CodexReasoningEffort.Low],
            supportsFast: false);

        viewModel.ApplyModelCatalog(
            [sol, luna],
            new CodexAccountInfo("chatgpt", "developer@example.com", "plus", null));

        Assert(ReferenceEquals(viewModel.SelectedModel, sol), "missing saved model falls back to catalog default");
        Assert(viewModel.SelectedReasoning?.Effort == CodexReasoningEffort.High, "unsupported effort falls back to model default");
        Assert(viewModel.IsFastModeAvailable && viewModel.IsFastModeEnabled, "supported model preserves Fast selection");
        Assert(viewModel.AccountPlanLabel == "ChatGPT Plus", "ChatGPT plan is displayed as context");
        Assert(viewModel.ModelSelectionSummary.Contains("GPT-5.6 Sol", StringComparison.Ordinal), "summary uses display name");
        Assert(viewModel.ModelSelectionSummary.Contains("High", StringComparison.Ordinal), "summary uses reasoning label");
        Assert(viewModel.ModelSelectionSummary.Contains("Fast", StringComparison.Ordinal), "summary includes Fast state");

        viewModel.SelectedModel = luna;

        Assert(viewModel.ReasoningOptions.Count == 2, "model change rebuilds reasoning list");
        Assert(viewModel.SelectedReasoning?.Effort == CodexReasoningEffort.Low, "model change selects new default effort");
        Assert(!viewModel.IsFastModeAvailable && !viewModel.IsFastModeEnabled, "unsupported model turns Fast off");
        Assert(viewModel.ServiceTierSelection == CodexServiceTierSelection.Standard, "unsupported model records explicit standard tier");

        viewModel.ApplyModelCatalog(
            [sol],
            new CodexAccountInfo("apiKey", null, null, "env"));
        Assert(string.IsNullOrEmpty(viewModel.AccountPlanLabel), "API-key account omits ChatGPT plan label");

        viewModel.ReasoningEffortOverride = "medium";
        viewModel.ApplyModelCatalog(
            [sol],
            new CodexAccountInfo("apiKey", null, null, "env"));
        Assert(viewModel.SelectedReasoning?.Effort == CodexReasoningEffort.Medium, "refresh reconciles reasoning even when model records are reused");
        return Task.CompletedTask;
    }

    private static Task ServiceTierPreferenceSurvivesSettingsSnapshotAsync()
    {
        var settings = new AppSettings
        {
            LastModelOverride = "gpt-5.6-sol",
            LastReasoningEffortOverride = "high",
            LastServiceTierOverride = "fast"
        };

        var snapshot = AppSettingsSnapshot.Create(settings);
        settings.LastServiceTierOverride = "standard";

        Assert(snapshot.LastServiceTierOverride == "fast", "snapshot retains independent service tier preference");
        return Task.CompletedTask;
    }

    private static CodexTurnStartRequest Request(CodexServiceTierSelection serviceTier) => new(
        "thread_phase5g",
        "Test service tier.",
        @"D:\Repo",
        CodexSandbox.WorkspaceWrite,
        "gpt-5.6-sol",
        CodexReasoningEffort.High,
        serviceTier);

    private static CodexModelOption Model(
        string id,
        string model,
        string displayName,
        bool isDefault,
        CodexReasoningEffort defaultEffort,
        IReadOnlyList<CodexReasoningEffort> efforts,
        bool supportsFast) => new(
            id,
            model,
            displayName,
            $"Description for {displayName}",
            isDefault,
            false,
            defaultEffort,
            efforts.Select(effort => new CodexReasoningOption(effort, $"{effort} description")).ToList(),
            supportsFast ? [new CodexServiceTierOption("priority", "Fast", "Faster responses")] : [],
            null,
            supportsFast ? ["fast"] : []);

    private static async Task CompleteInitializeAsync(CodexAppServerClient client, FakeAppServerTransport transport)
    {
        var initialize = client.InitializeAsync();
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"test","platformFamily":"windows","platformOs":"windows"}}""");
        await initialize;
    }

    private static JsonObject ParseMessage(string value) =>
        JsonNode.Parse(value)?.AsObject() ?? throw new InvalidOperationException("Expected a JSON object.");

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
