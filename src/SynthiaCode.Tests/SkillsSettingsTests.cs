using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;
using SynthiaCode.App.Views;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Infrastructure.Codex;

internal static class SkillsSettingsTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("skills protocol lists metadata errors and duplicate names", ListsMetadataErrorsAndDuplicateNamesAsync),
        ("skills protocol writes path enablement and degrades when unsupported", WritesEnablementAndDegradesAsync),
        ("effective settings protocol allowlists values and origins", ReadsAllowlistedEffectiveSettingsAsync),
        ("skills view model filters toggles and reacts to invalidation", ViewModelFiltersTogglesAndInvalidatesAsync),
        ("skills settings surface is accessible and virtualized", SettingsSurfaceIsAccessibleAndVirtualizedAsync)
    ];

    private static async Task ListsMetadataErrorsAndDuplicateNamesAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = CreateClient(transport);
        await InitializeAsync(client, transport);

        var cwd = Path.GetFullPath(@"C:\Work\Skill Repo");
        var listTask = client.ListSkillsAsync(new CodexSkillListRequest([cwd], ForceReload: true));
        await transport.WaitForClientMessageCountAsync(3);
        var request = ParseMessage(transport.ClientMessages[2]);
        AssertEqual("skills/list", request["method"]?.GetValue<string>(), "skills list method");
        AssertEqual(cwd, request["params"]?["cwds"]?[0]?.GetValue<string>(), "skills list cwd");
        AssertEqual(true, request["params"]?["forceReload"]?.GetValue<bool>(), "skills list forced reload");

        SendResult(
            transport,
            request,
            new JsonObject
            {
                ["data"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["cwd"] = cwd,
                        ["errors"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["path"] = Path.Combine(cwd, ".agents", "skills", "broken", "SKILL.md"),
                                ["message"] = "Missing required description"
                            }
                        },
                        ["skills"] = new JsonArray
                        {
                            Skill(
                                "review",
                                "Review the current change.",
                                Path.Combine(cwd, ".agents", "skills", "review", "SKILL.md"),
                                "repo",
                                enabled: true,
                                displayName: "Repository review"),
                            Skill(
                                "review",
                                "Review any repository.",
                                @"C:\Users\Test\.agents\skills\review\SKILL.md",
                                "user",
                                enabled: false,
                                displayName: "Personal review")
                        }
                    }
                }
            });

        var result = await listTask;
        Assert(result.IsSupported, "skills list is supported");
        AssertEqual(1, result.Contexts.Count, "skills context count");
        var context = result.Contexts.Single();
        AssertEqual(cwd, context.Cwd, "skills response cwd");
        AssertEqual(2, context.Skills.Count, "duplicate skill names are preserved");
        AssertEqual(2, context.Skills.Select(skill => skill.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            "skill path is unique identity");
        AssertEqual(CodexSkillScope.Repository, context.Skills[0].Scope, "repository scope");
        AssertEqual("Repository review", context.Skills[0].Interface?.DisplayName, "skill interface display name");
        AssertEqual("mcp", context.Skills[0].Dependencies?.Tools.Single().Type, "skill dependency type");
        AssertEqual(1, context.Errors.Count, "per-cwd skill errors");
        Assert(context.Errors[0].Message.Contains("description", StringComparison.Ordinal), "skill error message");
    }

    private static async Task WritesEnablementAndDegradesAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = CreateClient(transport);
        await InitializeAsync(client, transport);

        var skillPath = Path.GetFullPath(@"C:\Users\Test\.agents\skills\review\SKILL.md");
        var writeTask = client.WriteSkillConfigAsync(new CodexSkillConfigWriteRequest(skillPath, Enabled: false));
        await transport.WaitForClientMessageCountAsync(3);
        var request = ParseMessage(transport.ClientMessages[2]);
        AssertEqual("skills/config/write", request["method"]?.GetValue<string>(), "skills write method");
        AssertEqual(skillPath, request["params"]?["path"]?.GetValue<string>(), "skills write path");
        Assert(request["params"]?["name"] is null, "skills write does not use ambiguous name identity");
        AssertEqual(false, request["params"]?["enabled"]?.GetValue<bool>(), "skills write enabled value");
        SendResult(transport, request, new JsonObject { ["effectiveEnabled"] = false });
        var write = await writeTask;
        Assert(write.IsSupported && !write.EffectiveEnabled, "effective skill state is authoritative");

        var listTask = client.ListSkillsAsync(new CodexSkillListRequest([@"C:\Work"]));
        await transport.WaitForClientMessageCountAsync(4);
        var unsupported = ParseMessage(transport.ClientMessages[3]);
        SendError(transport, unsupported, -32601, "Method not found");
        var list = await listTask;
        Assert(!list.IsSupported && list.Contexts.Count == 0, "unsupported skills list is nonfatal");
        Assert(client.IsHealthy, "unsupported skills method keeps app-server healthy");
    }

    private static async Task ReadsAllowlistedEffectiveSettingsAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = CreateClient(transport);
        await InitializeAsync(client, transport);

        var cwd = Path.GetFullPath(@"C:\Work\Settings Repo");
        var readTask = client.ReadEffectiveConfigurationAsync(cwd);
        await transport.WaitForClientMessageCountAsync(3);
        var request = ParseMessage(transport.ClientMessages[2]);
        AssertEqual("config/read", request["method"]?.GetValue<string>(), "effective settings method");
        AssertEqual(cwd, request["params"]?["cwd"]?.GetValue<string>(), "effective settings cwd");
        AssertEqual(true, request["params"]?["includeLayers"]?.GetValue<bool>(), "effective settings include layers");

        SendResult(
            transport,
            request,
            new JsonObject
            {
                ["config"] = new JsonObject
                {
                    ["model"] = "gpt-test",
                    ["model_provider"] = "openai",
                    ["model_reasoning_effort"] = "high",
                    ["service_tier"] = "fast",
                    ["profile"] = "team",
                    ["sandbox_mode"] = "workspace-write",
                    ["approval_policy"] = "on-request",
                    ["approvals_reviewer"] = "user",
                    ["web_search"] = "cached",
                    ["sandbox_workspace_write"] = new JsonObject { ["network_access"] = false },
                    ["mcp_servers"] = new JsonObject
                    {
                        ["private"] = new JsonObject { ["env_http_headers"] = new JsonObject { ["TOKEN"] = "secret" } }
                    }
                },
                ["origins"] = new JsonObject
                {
                    ["model"] = Origin("user", @"C:\Codex\config.toml"),
                    ["sandbox_mode"] = Origin("project", Path.Combine(cwd, ".codex", "config.toml"))
                },
                ["layers"] = new JsonArray()
            });

        var result = await readTask;
        Assert(result.IsSupported, "effective settings are supported");
        AssertEqual("gpt-test", result.Model, "effective model");
        AssertEqual("openai", result.ModelProvider, "effective model provider");
        AssertEqual("high", result.ReasoningEffort, "effective reasoning");
        AssertEqual("fast", result.ServiceTier, "effective service tier");
        AssertEqual("team", result.Profile, "effective profile");
        AssertEqual("workspace-write", result.SandboxMode, "effective sandbox");
        AssertEqual("on-request", result.ApprovalPolicy, "effective approval policy");
        AssertEqual("user", result.ApprovalsReviewer, "effective approval reviewer");
        AssertEqual("cached", result.WebSearchMode, "effective web search");
        AssertEqual(false, result.SandboxNetworkAccess, "effective network access");
        AssertEqual(@"C:\Codex\config.toml", result.Origins["model"], "model origin");
        Assert(!result.Origins.ContainsKey("mcp_servers"), "unallowlisted origin is discarded");
        Assert(!result.ToString()!.Contains("secret", StringComparison.Ordinal), "sensitive config is not retained");
    }

    private static async Task ViewModelFiltersTogglesAndInvalidatesAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var coordinator = CreateCoordinator(transport);
        await ConnectAsync(coordinator, transport);
        var cwd = Path.GetFullPath(@"C:\Work\Skill Repo");
        var opened = new List<string>();
        var revealed = new List<string>();
        var viewModel = new SkillsViewModel(
            coordinator,
            () => cwd,
            () => "Project",
            opened.Add,
            revealed.Add,
            () => false,
            _ => { },
            new TestLogger());

        var refreshTask = viewModel.RefreshAsync(forceReload: false);
        await transport.WaitForClientMessageCountAsync(3);
        var listRequest = ParseMessage(transport.ClientMessages[2]);
        SendResult(transport, listRequest, SkillsResult(cwd, enabled: true));
        await refreshTask;

        AssertEqual(2, viewModel.Skills.Count, "view model skill count");
        AssertEqual(1, viewModel.EnabledCount, "view model enabled count");
        AssertEqual("Project", viewModel.ContextLabel, "view model context label");
        viewModel.SearchText = "personal";
        AssertEqual(1, viewModel.Skills.Count, "skill search");
        viewModel.SearchText = string.Empty;
        viewModel.SelectedScopeFilter = "Repository";
        AssertEqual(1, viewModel.Skills.Count, "skill scope filter");

        var repositorySkill = viewModel.Skills.Single();
        var toggleTask = viewModel.ToggleSkillAsync(repositorySkill);
        await transport.WaitForClientMessageCountAsync(4);
        var toggleRequest = ParseMessage(transport.ClientMessages[3]);
        AssertEqual(repositorySkill.Path, toggleRequest["params"]?["path"]?.GetValue<string>(), "toggle uses row path");
        SendResult(transport, toggleRequest, new JsonObject { ["effectiveEnabled"] = false });
        await transport.WaitForClientMessageCountAsync(5);
        var forcedRefresh = ParseMessage(transport.ClientMessages[4]);
        AssertEqual(true, forcedRefresh["params"]?["forceReload"]?.GetValue<bool>(), "toggle forces refresh");
        SendResult(transport, forcedRefresh, SkillsResult(cwd, enabled: false));
        await toggleTask;
        Assert(!repositorySkill.IsEnabled, "toggle applies effective disabled state");

        viewModel.SelectedScopeFilter = "All";
        var first = viewModel.Skills[0];
        viewModel.OpenSkillCommand.Execute(first);
        viewModel.RevealSkillCommand.Execute(first);
        AssertEqual(first.Path, opened.Single(), "skill editor action");
        AssertEqual(first.Path, revealed.Single(), "skill Explorer action");

        viewModel.IsActive = true;
        transport.ServerSend("""{"method":"skills/changed","params":{}}""");
        await transport.WaitForClientMessageCountAsync(6);
        var invalidationRefresh = ParseMessage(transport.ClientMessages[5]);
        AssertEqual("skills/list", invalidationRefresh["method"]?.GetValue<string>(), "skills invalidation refresh");
        SendResult(transport, invalidationRefresh, SkillsResult(cwd, enabled: false));
        await WaitUntilAsync(() => !viewModel.IsBusy, "skills invalidation refresh completes");
        Assert(!viewModel.IsStale, "skills invalidation clears stale state");

        await viewModel.DisposeAsync();
    }

    private static Task SettingsSurfaceIsAccessibleAndVirtualizedAsync() => WpfTestHost.RunAsync(() =>
    {
        var view = new SkillsSettingsView
        {
            Width = 340,
            Height = 680
        };
        var available = new Size(view.Width, view.Height);
        view.Measure(available);
        view.Arrange(new Rect(available));
        view.UpdateLayout();

        var search = view.FindName("SkillsSearchBox") as TextBox;
        var filter = view.FindName("SkillScopeFilter") as ComboBox;
        var refresh = view.FindName("RefreshSkillsButton") as Button;
        var list = view.FindName("SkillsList") as ListBox;
        var effective = view.FindName("EffectiveSettingsList") as ItemsControl;

        Assert(search is not null, "skills search exists");
        AssertEqual("Search Codex skills", AutomationProperties.GetName(search), "skills search accessible name");
        Assert(filter is not null, "skills scope filter exists");
        AssertEqual("Filter Codex skills by scope", AutomationProperties.GetName(filter), "scope filter accessible name");
        Assert(refresh is not null, "skills refresh exists");
        Assert(list is not null, "skills list exists");
        AssertEqual(true, VirtualizingStackPanel.GetIsVirtualizing(list), "skills list virtualization");
        AssertEqual(VirtualizationMode.Recycling, VirtualizingStackPanel.GetVirtualizationMode(list),
            "skills list recycling");
        Assert(effective is not null, "effective settings list exists");

        var countBindingPaths = new HashSet<string>(
            ["Skills.EnabledCount", "Skills.DisabledCount", "Skills.ErrorCount"],
            StringComparer.Ordinal);
        var countBindings = LogicalDescendants<TextBlock>(view)
            .SelectMany(textBlock => textBlock.Inlines.OfType<Run>())
            .Select(run => BindingOperations.GetBinding(run, Run.TextProperty))
            .Where(binding => binding is not null && countBindingPaths.Contains(binding.Path.Path))
            .ToList();
        AssertEqual(3, countBindings.Count, "skills summary count bindings");
        Assert(
            countBindings.All(binding => binding?.Mode == BindingMode.OneWay),
            "read-only skills summary properties use explicit one-way bindings");

        var details = new DetailsView();
        Assert(details.FindName("SkillsSettingsSurface") is SkillsSettingsView, "settings integrates skills surface");
        Assert(details.FindName("SharedAgentsEditor") is TextBox, "shared AGENTS editor remains available");
        Assert(details.FindName("SharedConfigEditor") is TextBox, "shared config editor remains available");
    });

    private static JsonObject Skill(
        string name,
        string description,
        string path,
        string scope,
        bool enabled,
        string displayName) =>
        new()
        {
            ["name"] = name,
            ["description"] = description,
            ["path"] = path,
            ["scope"] = scope,
            ["enabled"] = enabled,
            ["shortDescription"] = $"Short {description}",
            ["interface"] = new JsonObject
            {
                ["displayName"] = displayName,
                ["shortDescription"] = $"Use {displayName}",
                ["brandColor"] = "#336699",
                ["defaultPrompt"] = $"Run {name}"
            },
            ["dependencies"] = new JsonObject
            {
                ["tools"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "mcp",
                        ["value"] = "docs",
                        ["description"] = "Documentation",
                        ["transport"] = "streamable_http",
                        ["url"] = "https://example.test/mcp"
                    }
                }
            }
        };

    private static JsonObject SkillsResult(string cwd, bool enabled) =>
        new()
        {
            ["data"] = new JsonArray
            {
                new JsonObject
                {
                    ["cwd"] = cwd,
                    ["errors"] = new JsonArray(),
                    ["skills"] = new JsonArray
                    {
                        Skill(
                            "review",
                            "Review this repository.",
                            Path.Combine(cwd, ".agents", "skills", "review", "SKILL.md"),
                            "repo",
                            enabled,
                            "Repository review"),
                        Skill(
                            "personal",
                            "Personal workflow.",
                            @"C:\Users\Test\.agents\skills\personal\SKILL.md",
                            "user",
                            enabled: false,
                            displayName: "Personal workflow")
                    }
                }
            }
        };

    private static JsonObject Origin(string type, string file) =>
        new()
        {
            ["name"] = new JsonObject
            {
                ["type"] = type,
                ["file"] = file
            },
            ["version"] = "1"
        };

    private static CodexAppServerClient CreateClient(FakeAppServerTransport transport) =>
        new(transport, new CodexAppServerClientMetadata("skills_tests", "Skills Tests", "1.0.0"));

    private static AppServerSessionCoordinator CreateCoordinator(FakeAppServerTransport transport) =>
        new(
            new FakeCodexProcessService(transport),
            new TestLogger(),
            new CodexAppServerClientMetadata("skills_tests", "Skills Tests", "1.0.0"));

    private static async Task InitializeAsync(CodexAppServerClient client, FakeAppServerTransport transport)
    {
        var task = client.InitializeAsync();
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"codex-test","platformFamily":"windows","platformOs":"windows"}}""");
        await task;
    }

    private static async Task ConnectAsync(
        AppServerSessionCoordinator coordinator,
        FakeAppServerTransport transport)
    {
        var task = coordinator.EnsureConnectedAsync(
            new CodexInstallation(true, @"C:\Tools\codex.exe", "codex test", "Codex test", "Test"));
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"codex-test","platformFamily":"windows","platformOs":"windows"}}""");
        await task;
    }

    private static void SendResult(FakeAppServerTransport transport, JsonObject request, JsonObject result)
    {
        var id = request["id"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Request does not have a numeric id.");
        transport.ServerSend(new JsonObject { ["id"] = id, ["result"] = result }.ToJsonString());
    }

    private static void SendError(
        FakeAppServerTransport transport,
        JsonObject request,
        int code,
        string message)
    {
        var id = request["id"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Request does not have a numeric id.");
        transport.ServerSend(
            new JsonObject
            {
                ["id"] = id,
                ["error"] = new JsonObject
                {
                    ["code"] = code,
                    ["message"] = message
                }
            }.ToJsonString());
    }

    private static JsonObject ParseMessage(string value) =>
        JsonNode.Parse(value)?.AsObject()
        ?? throw new InvalidOperationException("Expected a JSON object.");

    private static async Task WaitUntilAsync(Func<bool> condition, string label)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in LogicalDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{label}: expected '{expected}', actual '{actual}'.");
        }
    }
}
