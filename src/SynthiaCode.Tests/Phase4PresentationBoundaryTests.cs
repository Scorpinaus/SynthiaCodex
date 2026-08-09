using System.Xml.Linq;
using SynthiaCode.Presentation.Markdown;
using Xunit;

[Trait("Category", TestCategories.Unit)]
public sealed class Phase4PresentationBoundaryTests
{
    [Fact]
    public void Presentation_project_is_WPF_free_and_has_no_product_project_references()
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(root, "src", "SynthiaCode.Presentation", "SynthiaCode.Presentation.csproj");
        var project = XDocument.Load(projectPath);

        Assert.DoesNotContain(project.Descendants("UseWPF"), element =>
            string.Equals(element.Value, "true", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(project.Descendants("TargetFramework"), element =>
            element.Value.Contains("-windows", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(project.Descendants("ProjectReference"));
    }

    [Fact]
    public void Markdown_parser_keeps_code_span_pipes_inside_one_table_cell()
    {
        var document = MarkdownDocumentParser.Parse(
            "| Value | State |" + Environment.NewLine +
            "| --- | --- |" + Environment.NewLine +
            "| `left|right` | kept |");

        var table = Assert.IsType<MarkdownTableBlock>(Assert.Single(document.Blocks));
        Assert.Equal(2, table.Rows[0].Count);
        Assert.Equal("`left|right`", table.Rows[0][0]);
        Assert.Equal("kept", table.Rows[0][1]);
    }

    [Fact]
    public void Task_presentation_uses_five_feature_controls_and_view_models()
    {
        var root = FindRepositoryRoot();
        var views = Path.Combine(root, "src", "SynthiaCode.App", "Views");
        var viewModels = Path.Combine(root, "src", "SynthiaCode.App", "ViewModels");
        var shell = File.ReadAllText(Path.Combine(views, "TaskView.xaml"));
        var conversation = File.ReadAllText(Path.Combine(views, "TaskConversationView.xaml"));
        var composer = File.ReadAllText(Path.Combine(views, "TaskComposerView.xaml"));
        var taskViewModel = File.ReadAllText(Path.Combine(viewModels, "TaskViewModel.cs"));

        Assert.Contains("<views:TaskConversationView", shell, StringComparison.Ordinal);
        Assert.Contains("<views:TaskComposerView", shell, StringComparison.Ordinal);
        Assert.Contains("<views:TaskAgentsView", conversation, StringComparison.Ordinal);
        Assert.Contains("<views:TaskGoalsView", composer, StringComparison.Ordinal);
        Assert.Contains("<views:TaskOptionsView", composer, StringComparison.Ordinal);
        Assert.Contains("TaskConversationViewModel Conversation", taskViewModel, StringComparison.Ordinal);
        Assert.Contains("TaskComposerViewModel Composer", taskViewModel, StringComparison.Ordinal);
        Assert.Contains("TaskAgentsViewModel Agents", taskViewModel, StringComparison.Ordinal);
        Assert.Contains("TaskGoalsViewModel Goals", taskViewModel, StringComparison.Ordinal);
        Assert.Contains("TaskOptionsViewModel Options", taskViewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_rendering_keeps_destination_policy_in_the_WPF_boundary()
    {
        var root = FindRepositoryRoot();
        var renderer = File.ReadAllText(Path.Combine(
            root,
            "src",
            "SynthiaCode.App",
            "Controls",
            "MarkdownTextBlock.cs"));

        Assert.Contains("MarkdownDocumentParser.Parse", renderer, StringComparison.Ordinal);
        Assert.Contains("ExternalUriPolicy", renderer, StringComparison.Ordinal);
        Assert.Contains("LocalImageResourcePolicy", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Windows", File.ReadAllText(Path.Combine(
            root,
            "src",
            "SynthiaCode.Presentation",
            "Markdown",
            "MarkdownDocumentParser.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void Git_presentation_uses_repository_changes_actions_and_push_planning_components()
    {
        var root = FindRepositoryRoot();
        var gitView = File.ReadAllText(Path.Combine(root, "src", "SynthiaCode.App", "Views", "GitView.xaml"));
        var gitViewModel = File.ReadAllText(Path.Combine(root, "src", "SynthiaCode.App", "ViewModels", "GitViewModel.cs"));

        Assert.Contains("Git.RepositorySelection.", gitView, StringComparison.Ordinal);
        Assert.Contains("Git.Changes.", gitView, StringComparison.Ordinal);
        Assert.Contains("Git.Actions.", gitView, StringComparison.Ordinal);
        Assert.Contains("GitRepositorySelectionViewModel RepositorySelection", gitViewModel, StringComparison.Ordinal);
        Assert.Contains("GitChangesViewModel Changes", gitViewModel, StringComparison.Ordinal);
        Assert.Contains("GitActionsViewModel Actions", gitViewModel, StringComparison.Ordinal);
        Assert.Contains("GitPushPlanningViewModel PushPlanning", gitViewModel, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SynthiaCode.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the SynthiaCode repository root.");
    }
}
