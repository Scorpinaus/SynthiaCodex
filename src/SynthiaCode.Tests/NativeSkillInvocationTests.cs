using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;
using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;
using SynthiaCode.App.Views;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Infrastructure.Codex;

[Trait("Category", TestCategories.Wpf)]
[Collection(TestCategories.WpfCollection)]
public sealed class NativeSkillInvocationTests
{


    [Fact(DisplayName = "native skill selector projects enabled workspace skills")]
    public async Task SelectorProjectsEnabledSkillsAsync()
    {
        var loadCount = 0;
        var viewModel = CreateTaskViewModel(_ =>
        {
            loadCount++;
            return Task.FromResult(new ComposerSkillLoadResult(
                [
                    Skill("review", @"C:\Repo\.agents\skills\review\SKILL.md", CodexSkillScope.Repository),
                    Skill("review", @"C:\Users\Test\.codex\skills\review\SKILL.md", CodexSkillScope.User),
                    Skill("disabled", @"C:\Repo\.agents\skills\disabled\SKILL.md", CodexSkillScope.Repository, enabled: false)
                ],
                IsSupported: true));
        });

        await viewModel.SkillSelector.OpenAsync();

        AssertEqual(1, loadCount, "selector load count");
        Assert(viewModel.SkillSelector.IsOpen, "selector opens");
        AssertEqual(2, viewModel.SkillSelector.AvailableSkills.Count, "enabled candidates only");
        AssertEqual(2, viewModel.SkillSelector.FilteredSkills.Count, "duplicate names remain distinct");
        Assert(
            viewModel.SkillSelector.FilteredSkills.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
            "absolute path remains selector identity");

        viewModel.SkillSelector.SearchText = "Repository";
        AssertEqual(1, viewModel.SkillSelector.FilteredSkills.Count, "scope participates in search");
        AssertEqual(CodexSkillScope.Repository, viewModel.SkillSelector.FilteredSkills[0].Scope, "repository match");

        viewModel.SkillSelector.SearchText = "$review";
        AssertEqual(2, viewModel.SkillSelector.FilteredSkills.Count, "dollar prefix is ignored when filtering");
    }

    [Fact(DisplayName = "native skill selector replaces dollar tokens and removes bindings")]
    public async Task SelectorReplacesTokensAsync()
    {
        var partialToken = ComposerSkillToken.Find("Run $review later", "Run $rev".Length);
        Assert(partialToken is not null, "partial caret token is found");
        AssertEqual("rev", partialToken!.Query, "partial caret query");
        AssertEqual("$review".Length, partialToken.Length, "replacement spans the complete token");

        var repositoryPath = @"C:\Repo\.agents\skills\review\SKILL.md";
        var viewModel = CreateTaskViewModel(_ => Task.FromResult(new ComposerSkillLoadResult(
            [Skill("review", repositoryPath, CodexSkillScope.Repository)],
            IsSupported: true)));
        viewModel.Prompt = "Please $rev";

        var token = ComposerSkillToken.Find(viewModel.Prompt, viewModel.Prompt.Length);
        Assert(token is not null, "active dollar token is found");
        AssertEqual("rev", token!.Query, "active dollar query");

        await viewModel.SkillSelector.OpenAsync(token);
        viewModel.SkillSelector.SelectCommand.Execute(viewModel.SkillSelector.FilteredSkills.Single());

        AssertEqual("Please $review ", viewModel.Prompt, "query token replaced");
        AssertEqual(1, viewModel.SkillSelector.SelectedSkills.Count, "skill binding selected");
        AssertEqual(repositoryPath, viewModel.SkillSelector.SelectedSkills[0].Path, "selected absolute path");
        Assert(!viewModel.SkillSelector.IsOpen, "selector closes after selection");

        viewModel.SkillSelector.RemoveCommand.Execute(viewModel.SkillSelector.SelectedSkills.Single());
        AssertEqual("Please", viewModel.Prompt, "removing binding removes marker");
        AssertEqual(0, viewModel.SkillSelector.SelectedSkills.Count, "binding removed");
    }

    [Fact(DisplayName = "native skill selector surface is accessible and virtualized")]
    public Task SelectorSurfaceIsAccessibleAsync() => WpfTestHost.RunAsync(() =>
    {
        var resources = Application.Current.Resources;
        resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
        resources["InverseBooleanToVisibilityConverter"] = new InverseBooleanToVisibilityConverter();
        resources["Card"] = new Style(typeof(Border));
        resources["StatePill"] = new Style(typeof(Border));
        resources["ConversationTurnCard"] = new Style(typeof(Border));
        resources["ConversationUserSurface"] = new Style(typeof(Border));
        resources["ConversationAssistantSurface"] = new Style(typeof(Border));
        resources["CompactButton"] = new Style(typeof(Button));
        resources["RunTaskButton"] = new Style(typeof(Button));
        resources["SectionLabel"] = new Style(typeof(TextBlock));
        resources["ConversationBodyText"] = new Style(typeof(TextBlock));
        resources["ConversationRoleText"] = new Style(typeof(TextBlock));
        resources["ConversationMetadataText"] = new Style(typeof(TextBlock));
        resources["ConversationActivityTitleText"] = new Style(typeof(TextBlock));
        resources["ConversationActivityDetailText"] = new Style(typeof(TextBlock));
        var view = new TaskView
        {
            Width = 900,
            Height = 720
        };
        var available = new Size(view.Width, view.Height);
        view.Measure(available);
        view.Arrange(new Rect(available));
        view.UpdateLayout();

        var button = WpfTestHost.FindNamedDescendant<Button>(view, "SkillsButton");
        var popup = WpfTestHost.FindNamedDescendant<Popup>(view, "SkillsPopup");
        var search = WpfTestHost.FindNamedDescendant<TextBox>(view, "SkillsSearchBox");
        var list = WpfTestHost.FindNamedDescendant<ListBox>(view, "ComposerSkillsList");
        var selected = WpfTestHost.FindNamedDescendant<ItemsControl>(view, "SelectedSkillsList");

        Assert(button is not null, "skills button exists");
        AssertEqual("Select skills", AutomationProperties.GetName(button), "skills button accessible name");
        Assert(popup is not null, "skills popup exists");
        Assert(search is not null, "skills search exists");
        AssertEqual("Search enabled skills", AutomationProperties.GetName(search), "skills search accessible name");
        Assert(list is not null, "skills list exists");
        AssertEqual(true, VirtualizingStackPanel.GetIsVirtualizing(list), "skills list virtualization");
        AssertEqual(VirtualizationMode.Recycling, VirtualizingStackPanel.GetVirtualizationMode(list),
            "skills list recycling");
        Assert(selected is not null, "selected skill chips exist");
    });

    [Fact(DisplayName = "explicit skill inputs serialize on start and steer")]
    public async Task ExplicitInputsSerializeAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(
            transport,
            new CodexAppServerClientMetadata("skill_input_tests", "Skill Input Tests", "1.0"));
        await InitializeAsync(client, transport);

        var skillPath = Path.GetFullPath(@"C:\Repo\.agents\skills\review\SKILL.md");
        var startTask = client.StartTurnAsync(new CodexTurnStartRequest(
            "thr_skills",
            [
                new CodexTextInput("$review Check the change."),
                new CodexSkillInput("review", skillPath)
            ],
            @"C:\Repo",
            CodexSandbox.WorkspaceWrite));

        await transport.WaitForClientMessageCountAsync(3);
        var start = JsonNode.Parse(transport.ClientMessages[2])!.AsObject();
        AssertEqual("text", ReadString(start, "params.input.0.type"), "visible marker text type");
        AssertEqual("$review Check the change.", ReadString(start, "params.input.0.text"), "visible marker retained");
        AssertEqual("skill", ReadString(start, "params.input.1.type"), "skill input type");
        AssertEqual("review", ReadString(start, "params.input.1.name"), "skill input name");
        AssertEqual(skillPath, ReadString(start, "params.input.1.path"), "skill input absolute path");
        transport.ServerSend("""{"id":1,"result":{"turn":{"id":"turn_skills"}}}""");
        await startTask;

        var steerTask = client.SteerTurnAsync(new CodexTurnSteerRequest(
            "thr_skills",
            "turn_skills",
            [
                new CodexTextInput("$review Check again."),
                new CodexSkillInput("review", skillPath)
            ]));
        await transport.WaitForClientMessageCountAsync(4);
        var steer = JsonNode.Parse(transport.ClientMessages[3])!.AsObject();
        AssertEqual("skill", ReadString(steer, "params.input.1.type"), "steer skill input type");
        AssertEqual(skillPath, ReadString(steer, "params.input.1.path"), "steer skill path");
        transport.ServerSend("""{"id":2,"result":{"turnId":"turn_skills"}}""");
        await steerTask;

        await AssertThrowsAsync<ArgumentException>(() => client.StartTurnAsync(new CodexTurnStartRequest(
            "thr_invalid_skill",
            [new CodexTextInput("$review"), new CodexSkillInput("review", @"relative\SKILL.md")],
            @"C:\Repo",
            CodexSandbox.WorkspaceWrite)));
    }

    [Fact(DisplayName = "skill invocation resolves unique names and rejects ambiguity")]
    public async Task InvocationResolutionIsExactAsync()
    {
        var repositoryPath = @"C:\Repo\.agents\skills\review\SKILL.md";
        var userPath = @"C:\Users\Test\.codex\skills\review\SKILL.md";
        var viewModel = CreateTaskViewModel(_ => Task.FromResult(new ComposerSkillLoadResult(
            [
                Skill("review", repositoryPath, CodexSkillScope.Repository),
                Skill("review", userPath, CodexSkillScope.User),
                Skill("format", @"C:\Repo\.agents\skills\format\SKILL.md", CodexSkillScope.Repository)
            ],
            IsSupported: true)));
        await viewModel.SkillSelector.OpenAsync();

        var unique = viewModel.SkillSelector.ResolveSkillInputs("$format Apply formatting.");
        AssertEqual(1, unique.Count, "unique manual marker resolves");
        AssertEqual("format", unique[0].Name, "unique skill name");

        AssertThrows<InvalidOperationException>(
            () => viewModel.SkillSelector.ResolveSkillInputs("$review Inspect this."),
            "duplicate marker requires picker");

        viewModel.Prompt = "$rev";
        await viewModel.SkillSelector.OpenAsync(ComposerSkillToken.Find(viewModel.Prompt, viewModel.Prompt.Length));
        var repository = viewModel.SkillSelector.FilteredSkills.Single(item =>
            item.Path.Equals(repositoryPath, StringComparison.OrdinalIgnoreCase));
        viewModel.SkillSelector.SelectCommand.Execute(repository);
        var selected = viewModel.SkillSelector.ResolveSkillInputs(viewModel.Prompt);
        AssertEqual(repositoryPath, selected.Single().Path, "selected duplicate binds exact path");

        viewModel.Prompt = "Marker removed";
        AssertEqual(0, viewModel.SkillSelector.ResolveSkillInputs(viewModel.Prompt).Count, "removed marker drops stale binding");
    }

    [Fact(DisplayName = "queued follow-ups preserve explicit skill bindings")]
    public Task QueuedFollowUpsPreserveBindingsAsync()
    {
        var path = @"C:\Repo\.agents\skills\review\SKILL.md";
        var input = new CodexSkillInput("review", path);
        var queue = new CodexFollowUpQueue();
        queue.Enqueue(
            "$review Inspect after this turn.",
            new QueuedTurnOptionsSnapshot { WorkspacePath = @"C:\Repo" },
            skillInputs: [input]);

        var snapshot = queue.Snapshot().Single();
        AssertEqual(path, snapshot.SkillInputs.Single().Path, "queue snapshot skill path");

        var serialized = JsonSerializer.Serialize(snapshot);
        var roundTripped = JsonSerializer.Deserialize<QueuedFollowUpSnapshot>(serialized)
            ?? throw new InvalidOperationException("queued skill snapshot did not deserialize");
        var restored = new CodexFollowUpQueue();
        restored.Restore([roundTripped]);
        AssertEqual(path, restored.Items.Single().SkillInputs.Single().Path, "restored queue skill path");
        AssertEqual("review", restored.Items.Single().SkillInputs.Single().Name, "restored queue skill name");

        restored.Edit(restored.Items.Single().Id, "Marker removed.");
        var selector = CreateTaskViewModel(_ => Task.FromResult(
            new ComposerSkillLoadResult([], IsSupported: true))).SkillSelector;
        AssertEqual(
            0,
            selector.ResolveSkillInputs(
                restored.Items.Single().Text,
                restored.Items.Single().SkillInputs).Count,
            "queued edit cannot retain a stale native binding");
        return Task.CompletedTask;
    }

    private static TaskViewModel CreateTaskViewModel(
        Func<CancellationToken, Task<ComposerSkillLoadResult>> loadSkills) =>
        WorkspaceActionStubs.CreateTaskViewModel(WorkspaceActionStubs.Task(
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => false,
            () => false,
            loadComposerSkills: loadSkills));

    private static CodexSkillMetadata Skill(
        string name,
        string path,
        CodexSkillScope scope,
        bool enabled = true) =>
        new(
            name,
            $"Use {name} for repository work.",
            path,
            scope,
            enabled,
            $"Short {name}",
            new CodexSkillInterface(
                $"{scope} {name}",
                $"Use {name}",
                null,
                null,
                null,
                null),
            null);

    private static async Task InitializeAsync(
        CodexAppServerClient client,
        FakeAppServerTransport transport)
    {
        var initialize = client.InitializeAsync();
        await transport.WaitForClientMessageCountAsync(1);
        transport.ServerSend(
            """{"id":0,"result":{"userAgent":"skill-input-test","platformFamily":"windows","platformOs":"windows"}}""");
        await initialize;
        await transport.WaitForClientMessageCountAsync(2);
    }

    private static string? ReadString(JsonObject source, string path)
    {
        JsonNode? current = source;
        foreach (var segment in path.Split('.'))
        {
            current = current switch
            {
                JsonObject value => value[segment],
                JsonArray value when int.TryParse(segment, out var index) && index >= 0 && index < value.Count =>
                    value[index],
                _ => null
            };
        }
        return current?.GetValue<string>();
    }

    private static void AssertThrows<T>(Action action, string label)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"{label}: expected {typeof(T).Name}.");
    }

    private static async Task AssertThrowsAsync<T>(Func<Task> action)
        where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
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
