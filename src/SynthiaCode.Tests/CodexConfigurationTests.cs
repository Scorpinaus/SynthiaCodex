using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SynthiaCode.App.ViewModels;
using SynthiaCode.App.Views;
using SynthiaCode.Core.Codex.Configuration;
using SynthiaCode.Infrastructure.Codex.Configuration;

internal static class CodexConfigurationTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("shared Codex files save atomically and reject stale edits", SharedFilesSaveAndRejectStaleEditsAsync),
        ("Codex configuration provenance follows workspace precedence", ProvenanceFollowsWorkspacePrecedenceAsync),
        ("Codex configuration view model edits and deep links shared files", ViewModelEditsAndDeepLinksAsync),
        ("settings expose shared Codex editors and provenance", SettingsExposeEditorsAndProvenanceAsync)
    ];

    private static async Task SharedFilesSaveAndRejectStaleEditsAsync()
    {
        using var temp = TempWorkspace.Create();
        var codexHome = temp.CreateDirectory("codex-home");
        var service = new SharedCodexConfigurationService(codexHome);

        var initial = await service.LoadAsync(workspacePath: null);
        Assert(!initial.SharedInstructions.Exists, "shared AGENTS.md starts missing");
        Assert(!initial.SharedConfiguration.Exists, "shared config.toml starts missing");

        var saved = await service.SaveAsync(
            CodexConfigurationFileKind.SharedInstructions,
            "# Shared instructions\nKeep tests first.\n",
            initial.SharedInstructions.Revision);
        Assert(saved.Exists, "saving creates shared AGENTS.md");
        AssertEqual(
            "# Shared instructions\nKeep tests first.\n",
            await File.ReadAllTextAsync(saved.Path),
            "saved AGENTS.md content");

        await File.WriteAllTextAsync(saved.Path, "# Changed elsewhere\n");
        await AssertThrowsAsync<CodexConfigurationConflictException>(
            () => service.SaveAsync(
                CodexConfigurationFileKind.SharedInstructions,
                "# Stale editor content\n",
                saved.Revision),
            "stale shared AGENTS.md save");
        AssertEqual("# Changed elsewhere\n", await File.ReadAllTextAsync(saved.Path), "external change is preserved");
        Assert(
            !Directory.EnumerateFiles(codexHome, "*.tmp", SearchOption.TopDirectoryOnly).Any(),
            "atomic save leaves no temporary file");
    }

    private static async Task ProvenanceFollowsWorkspacePrecedenceAsync()
    {
        using var temp = TempWorkspace.Create();
        var codexHome = temp.CreateDirectory("codex-home");
        await File.WriteAllTextAsync(Path.Combine(codexHome, "AGENTS.md"), "# Shared\n");
        await File.WriteAllTextAsync(Path.Combine(codexHome, "config.toml"), "model = \"shared\"\n");

        var repository = temp.CreateDirectory("repo");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        await File.WriteAllTextAsync(Path.Combine(repository, "AGENTS.md"), "# Root\n");
        Directory.CreateDirectory(Path.Combine(repository, ".codex"));
        await File.WriteAllTextAsync(Path.Combine(repository, ".codex", "config.toml"), "model = \"root\"\n");
        var child = Directory.CreateDirectory(Path.Combine(repository, "src", "feature")).FullName;
        await File.WriteAllTextAsync(Path.Combine(repository, "src", "AGENTS.md"), "# Src\n");
        Directory.CreateDirectory(Path.Combine(repository, "src", ".codex"));
        await File.WriteAllTextAsync(Path.Combine(repository, "src", ".codex", "config.toml"), "model = \"src\"\n");

        var snapshot = await new SharedCodexConfigurationService(codexHome).LoadAsync(child);
        var existing = snapshot.Provenance.Where(source => source.Exists).ToList();

        AssertEqual(CodexConfigurationFileKind.SharedInstructions, existing[0].Kind, "shared instructions precedence");
        AssertEqual(CodexConfigurationFileKind.SharedConfiguration, existing[1].Kind, "shared configuration precedence");
        AssertSequenceEqual(
            [
                Path.Combine(repository, "AGENTS.md"),
                Path.Combine(repository, ".codex", "config.toml"),
                Path.Combine(repository, "src", "AGENTS.md"),
                Path.Combine(repository, "src", ".codex", "config.toml")
            ],
            existing.Skip(2).Select(source => source.Path),
            "workspace provenance root-to-leaf order");
        Assert(existing.Skip(2).All(source => !source.IsEditable), "workspace sources are provenance-only");
        Assert(existing.Select(source => source.Precedence).SequenceEqual(existing.Select(source => source.Precedence).Order()),
            "provenance precedence increases in display order");
    }

    private static async Task ViewModelEditsAndDeepLinksAsync()
    {
        using var temp = TempWorkspace.Create();
        var codexHome = temp.CreateDirectory("codex-home");
        var service = new SharedCodexConfigurationService(codexHome);
        var opened = new List<string>();
        var revealed = new List<string>();
        var statuses = new List<string>();
        var viewModel = new CodexConfigurationViewModel(
            service,
            () => temp.Root,
            opened.Add,
            revealed.Add,
            () => false,
            statuses.Add,
            new TestLogger());

        await viewModel.RefreshAsync();
        viewModel.SharedInstructionsText = "# Team guidance\n";
        viewModel.SharedConfigurationText = "model = \"gpt-5.6\"\n";
        Assert(viewModel.SaveSharedInstructionsCommand.CanExecute(null), "changed AGENTS.md can save");
        Assert(viewModel.SaveSharedConfigurationCommand.CanExecute(null), "changed config.toml can save");

        await viewModel.SaveSharedInstructionsAsync();
        await viewModel.SaveSharedConfigurationAsync();
        AssertEqual("# Team guidance\n", await File.ReadAllTextAsync(viewModel.SharedInstructionsPath), "view model saves AGENTS.md");
        AssertEqual("model = \"gpt-5.6\"\n", await File.ReadAllTextAsync(viewModel.SharedConfigurationPath), "view model saves config.toml");
        Assert(!viewModel.HasSharedInstructionsChanges, "AGENTS.md is clean after save");
        Assert(!viewModel.HasSharedConfigurationChanges, "config.toml is clean after save");

        await viewModel.OpenSharedInstructionsAsync();
        await viewModel.OpenSharedConfigurationAsync();
        viewModel.RevealSourceCommand.Execute(viewModel.Provenance.Single(source =>
            source.Kind == CodexConfigurationFileKind.SharedInstructions));
        AssertSequenceEqual(
            [viewModel.SharedInstructionsPath, viewModel.SharedConfigurationPath],
            opened,
            "editor deep links");
        AssertEqual(viewModel.SharedInstructionsPath, revealed.Single(), "provenance reveal deep link");

        await File.WriteAllTextAsync(viewModel.SharedInstructionsPath, "# External edit\n");
        viewModel.SharedInstructionsText = "# Conflicting edit\n";
        await viewModel.SaveSharedInstructionsAsync();
        Assert(
            viewModel.ConfigurationMessage.Contains("changed outside", StringComparison.OrdinalIgnoreCase),
            "stale view-model save gives an actionable conflict");
        AssertEqual("# External edit\n", await File.ReadAllTextAsync(viewModel.SharedInstructionsPath), "conflict does not overwrite");
    }

    private static Task SettingsExposeEditorsAndProvenanceAsync() => WpfTestHost.RunAsync(() =>
    {
        var view = new DetailsView
        {
            Width = 340,
            Height = 760
        };
        var available = new Size(view.Width, view.Height);
        view.Measure(available);
        view.Arrange(new Rect(available));
        view.UpdateLayout();

        var agentsEditor = view.FindName("SharedAgentsEditor") as TextBox;
        var configEditor = view.FindName("SharedConfigEditor") as TextBox;
        var provenance = view.FindName("CodexConfigurationProvenance") as ItemsControl;
        var saveAgents = view.FindName("SaveSharedAgentsButton") as Button;
        var saveConfig = view.FindName("SaveSharedConfigButton") as Button;

        Assert(agentsEditor is { AcceptsReturn: true }, "settings expose multiline shared AGENTS.md editor");
        Assert(configEditor is { AcceptsReturn: true }, "settings expose multiline shared config.toml editor");
        Assert(provenance is not null, "settings expose configuration provenance");
        Assert(saveAgents is not null && saveConfig is not null, "settings expose explicit shared-file save actions");
        AssertEqual("Shared AGENTS.md", AutomationProperties.GetName(agentsEditor), "AGENTS.md editor accessible name");
        AssertEqual("Shared Codex config.toml", AutomationProperties.GetName(configEditor), "config.toml editor accessible name");
    });

    private static async Task AssertThrowsAsync<TException>(Func<Task> action, string label)
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

        throw new InvalidOperationException($"{label}: expected {typeof(TException).Name}.");
    }

    private static void AssertSequenceEqual<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string label)
    {
        var expectedValues = expected.ToList();
        var actualValues = actual.ToList();
        if (!expectedValues.SequenceEqual(actualValues))
        {
            throw new InvalidOperationException(
                $"{label}: expected [{string.Join(", ", expectedValues)}], actual [{string.Join(", ", actualValues)}].");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{label}: expected '{expected}', actual '{actual}'.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
