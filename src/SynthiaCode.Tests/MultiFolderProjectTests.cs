using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;
using SynthiaCode.App.Views;
using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Harnesses;
using SynthiaCode.Core.Projects;
using SynthiaCode.Core.Settings;
using SynthiaCode.Harnesses.Codex;
using SynthiaCode.Infrastructure.Attachments;
using SynthiaCode.Infrastructure.Codex;
using SynthiaCode.Infrastructure.Projects;
using SynthiaCode.Infrastructure.Settings;

internal static class MultiFolderProjectTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("multi-folder project migration preserves scoped state", ProjectMigrationPreservesState),
        ("multi-folder Codex turns send bounded writable roots", CodexTurnSendsWritableRootsAsync),
        ("multi-folder attachments preserve their owning root", AttachmentsPreserveOwningRoot),
        ("multi-folder project navigation exposes edit management", ProjectNavigationExposesEditAsync),
        ("multi-folder Git selection routes repository actions", GitSelectionRoutesActionsAsync),
        ("multi-folder editor renders accessible folder controls", ProjectEditorRendersAccessibleControlsAsync)
    ];

    private static async Task ProjectMigrationPreservesState()
    {
        using var temp = TempWorkspace.Create();
        var primary = temp.CreateDirectory("App");
        var secondary = temp.CreateDirectory("Docs");
        var worktree = temp.CreateDirectory("App-worktree");
        var settings = new AppSettings
        {
            RecentProjects =
            [
                new RecentProject(primary, "App", DateTimeOffset.UtcNow, [secondary])
            ],
            ProjectThreads =
            [
                new PersistedProjectThread
                {
                    ProjectPath = primary,
                    ThreadId = "local-thread",
                    WorkspacePath = primary,
                    Mode = "local",
                    ConversationTurns =
                    [
                        new CodexConversationTurnSnapshot
                        {
                            UserAttachments =
                            [
                                new AttachmentReference
                                {
                                    Id = "local-file",
                                    Kind = AttachmentKind.File,
                                    SourceKind = AttachmentSourceKind.WorkspaceReference,
                                    WorkspaceRelativePath = "src/file.cs"
                                }
                            ]
                        }
                    ],
                    QueuedFollowUps =
                    [
                        new QueuedFollowUpSnapshot
                        {
                            Id = "queued",
                            Text = "Continue",
                            Options = new QueuedTurnOptionsSnapshot { WorkspacePath = primary }
                        }
                    ]
                },
                new PersistedProjectThread
                {
                    ProjectPath = primary,
                    ThreadId = "worktree-thread",
                    WorkspacePath = worktree,
                    Mode = "worktree"
                }
            ],
            ComposerAttachmentDrafts =
            [
                new ComposerAttachmentDraftSnapshot
                {
                    ProjectPath = primary,
                    Attachments =
                    [
                        new AttachmentReference
                        {
                            Id = "draft-file",
                            Kind = AttachmentKind.File,
                            SourceKind = AttachmentSourceKind.WorkspaceReference,
                            WorkspaceRelativePath = "README.md"
                        }
                    ]
                }
            ]
        };

        var result = new RecentProjectService().UpdateProjectFolders(
            settings,
            new ProjectFolderUpdateRequest(primary, secondary, [secondary, primary]));

        Assert(PathsEqual(result.Project.Path, secondary), "the selected primary becomes the project path");
        Assert(result.Project.FolderPaths.Count == 2 && PathsEqual(result.Project.FolderPaths[0], secondary), "folder paths are primary first");
        var local = settings.ProjectThreads.Single(thread => thread.ThreadId == "local-thread");
        var worktreeThread = settings.ProjectThreads.Single(thread => thread.ThreadId == "worktree-thread");
        Assert(PathsEqual(local.ProjectPath, secondary) && PathsEqual(local.WorkspacePath, secondary), "local chat scope and cwd migrate");
        Assert(PathsEqual(worktreeThread.ProjectPath, secondary) && PathsEqual(worktreeThread.WorkspacePath, worktree), "worktree cwd is preserved");
        Assert(PathsEqual(local.ConversationTurns[0].UserAttachments[0].WorkspaceRootPath, primary), "legacy turn attachment is stamped with its old root");
        Assert(local.QueuedFollowUps[0].Options.WorkspaceRoots.Any(path => PathsEqual(path, primary)), "queued access retains both attached roots");
        Assert(PathsEqual(settings.ComposerAttachmentDrafts[0].ProjectPath, secondary), "composer draft scope migrates");
        Assert(PathsEqual(settings.ComposerAttachmentDrafts[0].Attachments[0].WorkspaceRootPath, primary), "draft attachment keeps its owning root");
        var clone = SettingsStorageMapper.Clone(settings);
        Assert(clone.RecentProjects[0].FolderPaths.Count == 2, "deep copies retain the attached folder set");
        Assert(!ReferenceEquals(
            clone.RecentProjects[0].AdditionalFolderPaths,
            settings.RecentProjects[0].AdditionalFolderPaths), "deep copies isolate the persisted folder collection");

        var store = new JsonSettingsStore(temp.CreateDirectory("Settings"), new TestLogger());
        await store.SaveAsync(settings);
        var restored = await store.LoadAsync();
        Assert(restored.RecentProjects[0].FolderPaths.Count == 2, "settings round trips all attached folders");
        Assert(PathsEqual(restored.RecentProjects[0].FolderPaths[0], secondary), "settings round trip preserves the primary folder");
    }

    private static async Task CodexTurnSendsWritableRootsAsync()
    {
        using var temp = TempWorkspace.Create();
        var primary = temp.CreateDirectory("Primary");
        var secondary = temp.CreateDirectory("Secondary");
        var roots = new[] { primary, secondary };
        var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(
            transport,
            new CodexAppServerClientMetadata("multi_root_tests", "Multi-root tests", "1.0"));
        await InitializeAsync(client, transport);

        var start = client.StartTurnAsync(new CodexTurnStartRequest(
            "thread-roots",
            [new CodexTextInput("Inspect both folders")],
            primary,
            CodexSandbox.WorkspaceWrite,
            WorkspaceRoots: roots));
        await transport.WaitForClientMessageCountAsync(3);
        var request = Parse(transport.ClientMessages[2]);
        Assert(ReadString(request, "method") == "turn/start", "turn start is used");
        Assert(PathsEqual(ReadString(request, "params.cwd"), primary), "primary remains cwd");
        var writableRoots = ReadNode(request, "params.sandboxPolicy.writableRoots") as JsonArray;
        Assert(writableRoots?.Count == 2, "both attached roots are sent");
        Assert(writableRoots!.Select(node => node!.GetValue<string>()).Any(path => PathsEqual(path, secondary)), "secondary is writable");
        transport.ServerSend("""{"id":1,"result":{"turn":{"id":"turn-roots"}}}""");
        await start;

        var lifecycle = new StartConversationCommand(
            ConversationId.New(),
            primary,
            new HarnessTurnOptions(ExecutionPolicy: new HarnessExecutionPolicy(
                WorkspaceAccessMode.WorkspaceWrite,
                ApprovalInteractionMode.Prompt)),
            "Keep tests green.",
            WorkspaceRoots: roots).ToCodex();
        Assert(lifecycle.DeveloperInstructions?.Contains(secondary, StringComparison.OrdinalIgnoreCase) == true, "secondary roots are described to Codex");
        Assert(CodexSandbox.ReadOnly.ToTurnSandboxPolicy(roots)["writableRoots"] is null, "read-only is not broadened by attached roots");
    }

    private static Task AttachmentsPreserveOwningRoot()
    {
        using var temp = TempWorkspace.Create();
        var primary = temp.CreateDirectory("Primary");
        var secondary = temp.CreateDirectory("Secondary");
        var file = Path.Combine(secondary, "guide.md");
        File.WriteAllText(file, "guide");
        var resolver = new WorkspaceAttachmentResolver();

        var attachment = resolver.Resolve([primary, secondary], file, AttachmentKind.File);
        Assert(PathsEqual(attachment.WorkspaceRootPath, secondary), "secondary root identity is persisted");
        Assert(PathsEqual(resolver.Revalidate([primary, secondary], attachment).ManagedPath, file), "attached secondary reference revalidates");
        AssertThrows<InvalidDataException>(
            () => resolver.Revalidate([primary], attachment),
            "detached roots invalidate old workspace references");
        return Task.CompletedTask;
    }

    private static async Task ProjectNavigationExposesEditAsync()
    {
        object? edited = null;
        var actions = new ProjectThreadActionStub
        {
            EditProject = parameter =>
            {
                edited = parameter;
                return Task.CompletedTask;
            },
            CanEditProject = _ => true
        };
        var viewModel = WorkspaceActionStubs.CreateProjectThreadViewModel(actions);
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "multi-folder-edit"));
        viewModel.EditProjectCommand.Execute(path);
        await WaitUntilAsync(() => edited is not null, "edit command dispatched");
        Assert(PathsEqual(edited as string, path), "edit command targets the owning project");
    }

    private static async Task GitSelectionRoutesActionsAsync()
    {
        using var temp = TempWorkspace.Create();
        var primary = temp.CreateDirectory("App");
        var secondary = temp.CreateDirectory("Docs");
        var git = new MultiRootGitService(primary, secondary);
        var viewModel = new GitViewModel(
            git,
            new FakeUserInteractionService(),
            new TestLogger(),
            () => new GitContext(primary, primary, [primary, secondary]),
            () => false,
            _ => { });

        await viewModel.RefreshAsync();
        Assert(viewModel.Repositories.Count == 2, "both repositories are discovered");
        Assert(PathsEqual(viewModel.SelectedRepository?.RootPath, primary), "primary repository is selected first");
        viewModel.SelectedRepository = viewModel.Repositories.Single(repository => PathsEqual(repository.RootPath, secondary));
        Assert(viewModel.SelectedFile?.Path == "docs.md", "selection projects the secondary changed files");
        viewModel.StageCommand.Execute(null);
        await WaitUntilAsync(() => git.StageRoots.Count == 1, "stage action completed");
        Assert(PathsEqual(git.StageRoots[0], secondary), "Git mutation targets the selected repository");
    }

    private static Task ProjectEditorRendersAccessibleControlsAsync() => WpfTestHost.RunAsync(() =>
    {
        var resources = Application.Current.Resources;
        resources["CompactButton"] = new Style(typeof(Button));
        resources["PrimaryButton"] = new Style(typeof(Button));
        resources["DangerButton"] = new Style(typeof(Button));
        resources["NavigationRow"] = new Style(typeof(ListBoxItem));
        using var temp = TempWorkspace.Create();
        var primary = temp.CreateDirectory("App");
        var secondary = temp.CreateDirectory("Docs");
        var window = new ProjectFoldersWindow(
            new RecentProject(primary, "App", DateTimeOffset.UtcNow, [secondary]),
            new FakeFolderPicker(secondary));

        Assert(AutomationProperties.GetName(window) == "Edit project folders", "dialog has an accessible name");
        Assert(window.FindName("ProjectFolderList") is ListBox, "folder list is rendered");
        Assert(window.FindName("AddProjectFolderButton") is Button, "add action is rendered");
        Assert(window.FindName("MakePrimaryFolderButton") is Button, "primary action is rendered");
        Assert(window.FindName("RemoveProjectFolderButton") is Button, "remove action is rendered");
        Assert(window.FindName("SaveProjectFoldersButton") is Button, "save action is rendered");
        window.Close();
    });

    private static async Task InitializeAsync(CodexAppServerClient client, FakeAppServerTransport transport)
    {
        var initialize = client.InitializeAsync();
        await transport.WaitForClientMessageCountAsync(1);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"multi-root-tests","platformFamily":"windows","platformOs":"windows"}}""");
        await initialize;
        await transport.WaitForClientMessageCountAsync(2);
    }

    private static JsonObject Parse(string value) => JsonNode.Parse(value)!.AsObject();

    private static string? ReadString(JsonObject value, string path) => ReadNode(value, path)?.GetValue<string>();

    private static JsonNode? ReadNode(JsonObject value, string path)
    {
        JsonNode? current = value;
        foreach (var segment in path.Split('.'))
        {
            current = current switch
            {
                JsonObject currentObject => currentObject[segment],
                JsonArray currentArray when int.TryParse(segment, out var index) && index >= 0 && index < currentArray.Count => currentArray[index],
                _ => null
            };
        }
        return current;
    }

    private static bool PathsEqual(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static async Task WaitUntilAsync(Func<bool> condition, string label)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
        Assert(condition(), label);
    }

    private static void AssertThrows<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
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

    private sealed class MultiRootGitService(string primary, string secondary) : IGitService
    {
        public List<string> StageRoots { get; } = [];

        public Task<GitRepositoryState> GetRepositoryStateAsync(string workingDirectory, CancellationToken cancellationToken = default)
        {
            var root = PathsEqual(workingDirectory, secondary) ? secondary : primary;
            var file = PathsEqual(root, secondary)
                ? new GitChangedFile("docs.md", null, ' ', 'M')
                : new GitChangedFile("app.cs", null, ' ', 'M');
            return Task.FromResult(new GitRepositoryState(true, root, PathsEqual(root, secondary) ? "docs" : "main", [file], null));
        }

        public Task<GitReviewCatalog> GetReviewCatalogAsync(string workingDirectory, CancellationToken cancellationToken = default)
        {
            var root = PathsEqual(workingDirectory, secondary) ? secondary : primary;
            return Task.FromResult(new GitReviewCatalog(root, PathsEqual(root, secondary) ? "docs" : "main", [], []));
        }

        public Task<string> GetDiffAsync(string repositoryRoot, GitChangedFile file, bool staged, CancellationToken cancellationToken = default) =>
            Task.FromResult($"{repositoryRoot}:{file.Path}");

        public Task StageAsync(string repositoryRoot, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
        {
            StageRoots.Add(repositoryRoot);
            return Task.CompletedTask;
        }

        public Task UnstageAsync(string repositoryRoot, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RevertAsync(string repositoryRoot, IReadOnlyCollection<GitChangedFile> files, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<GitCommitResult> CommitAsync(string repositoryRoot, string message, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitCommitResult("abc1234", message));
    }
}
