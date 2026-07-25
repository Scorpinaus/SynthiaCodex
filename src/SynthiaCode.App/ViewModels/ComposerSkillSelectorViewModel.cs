using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.App.ViewModels;

public sealed record ComposerSkillLoadResult(
    IReadOnlyList<CodexSkillMetadata> Skills,
    bool IsSupported,
    string? Message = null);

public sealed record ComposerSkillToken(
    int Start,
    int Length,
    string Query)
{
    public static ComposerSkillToken? Find(string? text, int caretIndex)
    {
        text ??= string.Empty;
        if (caretIndex < 0 || caretIndex > text.Length)
        {
            return null;
        }

        var start = caretIndex;
        while (start > 0 && IsSkillNameCharacter(text[start - 1]))
        {
            start--;
        }

        if (start == 0 || text[start - 1] != '$')
        {
            return null;
        }

        var markerStart = start - 1;
        if (markerStart > 0 && !char.IsWhiteSpace(text[markerStart - 1]))
        {
            return null;
        }

        var end = caretIndex;
        while (end < text.Length && IsSkillNameCharacter(text[end]))
        {
            end++;
        }

        return new ComposerSkillToken(
            markerStart,
            end - markerStart,
            text[start..caretIndex]);
    }

    internal static bool Contains(string? text, string name) =>
        Enumerate(text).Any(token =>
            token.Query.Equals(name, StringComparison.OrdinalIgnoreCase));

    internal static string Remove(string? text, string name)
    {
        var value = text ?? string.Empty;
        var matches = Enumerate(value)
            .Where(token => token.Query.Equals(name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(token => token.Start)
            .ToList();
        foreach (var match in matches)
        {
            value = value.Remove(match.Start, match.Length);
        }

        return value.Trim();
    }

    internal static IReadOnlyList<ComposerSkillToken> Enumerate(string? text)
    {
        var value = text ?? string.Empty;
        var result = new List<ComposerSkillToken>();
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '$' || (index > 0 && !char.IsWhiteSpace(value[index - 1])))
            {
                continue;
            }

            var end = index + 1;
            while (end < value.Length && IsSkillNameCharacter(value[end]))
            {
                end++;
            }
            if (end > index + 1)
            {
                result.Add(new ComposerSkillToken(
                    index,
                    end - index,
                    value[(index + 1)..end]));
            }
            index = Math.Max(index, end - 1);
        }
        return result;
    }

    private static bool IsSkillNameCharacter(char value) =>
        char.IsLetterOrDigit(value) || value is '-' or '_' or ':' or '.';
}

public sealed class ComposerSkillItemViewModel
{
    public ComposerSkillItemViewModel(CodexSkillMetadata metadata)
    {
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
    }

    internal CodexSkillMetadata Metadata { get; }

    public string Name => Metadata.Name;

    public string Marker => $"${Name}";

    public string DisplayName =>
        FirstNonEmpty(Metadata.Interface?.DisplayName, Metadata.Name);

    public string Description =>
        FirstNonEmpty(
            Metadata.Interface?.ShortDescription,
            Metadata.ShortDescription,
            Metadata.Description);

    public string Path => Metadata.Path;

    public CodexSkillScope Scope => Metadata.Scope;

    public string ScopeLabel => Metadata.Scope.ToString();

    internal bool Matches(string? searchText)
    {
        var query = (searchText ?? string.Empty).Trim();
        if (query.StartsWith('$'))
        {
            query = query[1..];
        }
        if (query.Length == 0)
        {
            return true;
        }

        return new[] { Name, DisplayName, Description, Path, ScopeLabel }
            .Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "Unnamed skill";
}

public sealed class ComposerSkillSelectorViewModel : ObservableObject
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private readonly Func<CancellationToken, Task<ComposerSkillLoadResult>> loadSkills;
    private readonly Func<string> getComposerText;
    private readonly Action<string> setComposerText;
    private readonly AsyncRelayCommand openCommand;
    private readonly RelayCommand selectCommand;
    private readonly RelayCommand removeCommand;
    private string searchText = string.Empty;
    private string message = "Open the selector to load enabled skills.";
    private bool isOpen;
    private bool isBusy;
    private bool isSupported = true;
    private ComposerSkillToken? pendingToken;

    public ComposerSkillSelectorViewModel(
        Func<CancellationToken, Task<ComposerSkillLoadResult>>? loadSkills,
        Func<string> getComposerText,
        Action<string> setComposerText)
    {
        this.loadSkills = loadSkills ?? (_ => Task.FromResult(
            new ComposerSkillLoadResult([], IsSupported: false)));
        this.getComposerText = getComposerText ?? throw new ArgumentNullException(nameof(getComposerText));
        this.setComposerText = setComposerText ?? throw new ArgumentNullException(nameof(setComposerText));
        OpenCommand = openCommand = new AsyncRelayCommand(() => OpenAsync(), () => !IsBusy);
        SelectCommand = selectCommand = new RelayCommand(
            parameter => Select(parameter as ComposerSkillItemViewModel),
            parameter => parameter is ComposerSkillItemViewModel && !IsBusy);
        RemoveCommand = removeCommand = new RelayCommand(
            parameter => Remove(parameter as ComposerSkillItemViewModel),
            parameter => parameter is ComposerSkillItemViewModel && !IsBusy);
    }

    public ObservableCollection<ComposerSkillItemViewModel> AvailableSkills { get; } = [];

    public ObservableCollection<ComposerSkillItemViewModel> FilteredSkills { get; } = [];

    public ObservableCollection<ComposerSkillItemViewModel> SelectedSkills { get; } = [];

    public ICommand OpenCommand { get; }

    public ICommand SelectCommand { get; }

    public ICommand RemoveCommand { get; }

    public string SearchText
    {
        get => searchText;
        set
        {
            if (SetProperty(ref searchText, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    public string Message
    {
        get => message;
        private set => SetProperty(ref message, value);
    }

    public bool IsOpen
    {
        get => isOpen;
        set => SetProperty(ref isOpen, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                openCommand.RaiseCanExecuteChanged();
                selectCommand.RaiseCanExecuteChanged();
                removeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsSupported
    {
        get => isSupported;
        private set => SetProperty(ref isSupported, value);
    }

    public bool HasSelectedSkills => SelectedSkills.Count > 0;

    public bool HasFilteredSkills => FilteredSkills.Count > 0;

    public async Task OpenAsync(
        ComposerSkillToken? token = null,
        CancellationToken cancellationToken = default)
    {
        pendingToken = token;
        SearchText = token?.Query ?? string.Empty;
        IsOpen = true;
        IsBusy = true;
        Message = "Loading enabled skills...";
        try
        {
            var result = await loadSkills(cancellationToken).ConfigureAwait(true);
            IsSupported = result.IsSupported;
            AvailableSkills.Clear();
            foreach (var metadata in result.Skills
                         .Where(skill => skill.Enabled)
                         .Where(skill => !string.IsNullOrWhiteSpace(skill.Name))
                         .Where(skill => !string.IsNullOrWhiteSpace(skill.Path) && Path.IsPathRooted(skill.Path))
                         .OrderBy(skill => skill.Interface?.DisplayName ?? skill.Name, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(skill => skill.Path, StringComparer.OrdinalIgnoreCase))
            {
                AvailableSkills.Add(new ComposerSkillItemViewModel(metadata));
            }

            ApplyFilter();
            Message = result.Message ?? (result.IsSupported
                ? AvailableSkills.Count == 0
                    ? "No enabled skills are available for this workspace."
                    : $"{AvailableSkills.Count} enabled skill{(AvailableSkills.Count == 1 ? string.Empty : "s")} available."
                : "This Codex app-server version does not expose skill discovery.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            IsOpen = false;
            throw;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            AvailableSkills.Clear();
            ApplyFilter();
            Message = $"Skills could not be loaded: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void NotifyContextChanged()
    {
        IsOpen = false;
        pendingToken = null;
        AvailableSkills.Clear();
        FilteredSkills.Clear();
        SelectedSkills.Clear();
        SearchText = string.Empty;
        Message = "Open the selector to load enabled skills for this workspace.";
        OnPropertyChanged(nameof(HasSelectedSkills));
    }

    public void ReconcileText(string? text)
    {
        var removed = SelectedSkills
            .Where(item => !ComposerSkillToken.Contains(text, item.Name))
            .ToList();
        foreach (var item in removed)
        {
            SelectedSkills.Remove(item);
        }
        if (removed.Count > 0)
        {
            OnPropertyChanged(nameof(HasSelectedSkills));
        }
    }

    public IReadOnlyList<CodexSkillInput> ResolveSkillInputs(
        string? text,
        IEnumerable<CodexSkillInput>? preservedBindings = null)
    {
        var preserved = (preservedBindings ?? [])
            .Where(IsValidSkillInput)
            .ToList();
        var result = new List<CodexSkillInput>();
        foreach (var token in ComposerSkillToken.Enumerate(text)
                     .GroupBy(item => item.Query, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First()))
        {
            var bound = preserved.FirstOrDefault(item =>
                    item.Name.Equals(token.Query, StringComparison.OrdinalIgnoreCase))
                ?? SelectedSkills
                    .Where(item => item.Name.Equals(token.Query, StringComparison.OrdinalIgnoreCase))
                    .Select(item => new CodexSkillInput(item.Name, item.Path))
                    .FirstOrDefault();
            if (bound is not null)
            {
                AddResolved(result, bound);
                continue;
            }

            var matches = AvailableSkills
                .Where(item => item.Name.Equals(token.Query, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count > 1)
            {
                throw new InvalidOperationException(
                    $"More than one enabled skill is named '${token.Query}'. Select the intended skill from the Skills picker.");
            }
            if (matches.Count == 1)
            {
                AddResolved(result, new CodexSkillInput(matches[0].Name, matches[0].Path));
            }
        }

        return result;
    }

    public void ClearSelectedSkills()
    {
        IsOpen = false;
        pendingToken = null;
        SelectedSkills.Clear();
        OnPropertyChanged(nameof(HasSelectedSkills));
    }

    private void Select(ComposerSkillItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        foreach (var existing in SelectedSkills
                     .Where(existing => existing.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            SelectedSkills.Remove(existing);
        }
        if (!SelectedSkills.Any(existing => PathComparer.Equals(existing.Path, item.Path)))
        {
            SelectedSkills.Add(item);
        }

        var text = getComposerText();
        if (pendingToken is { } token &&
            token.Start >= 0 &&
            token.Start + token.Length <= text.Length &&
            text[token.Start] == '$')
        {
            text = string.Concat(
                text.AsSpan(0, token.Start),
                item.Marker,
                " ",
                text.AsSpan(token.Start + token.Length));
        }
        else if (!ComposerSkillToken.Contains(text, item.Name))
        {
            text = string.IsNullOrWhiteSpace(text)
                ? $"{item.Marker} "
                : $"{text.TrimEnd()} {item.Marker} ";
        }

        setComposerText(text);
        pendingToken = null;
        IsOpen = false;
        OnPropertyChanged(nameof(HasSelectedSkills));
    }

    private void Remove(ComposerSkillItemViewModel? item)
    {
        if (item is null || !SelectedSkills.Remove(item))
        {
            return;
        }

        setComposerText(ComposerSkillToken.Remove(getComposerText(), item.Name));
        OnPropertyChanged(nameof(HasSelectedSkills));
    }

    private void ApplyFilter()
    {
        FilteredSkills.Clear();
        foreach (var item in AvailableSkills.Where(item => item.Matches(SearchText)))
        {
            FilteredSkills.Add(item);
        }
        OnPropertyChanged(nameof(HasFilteredSkills));
    }

    private static bool IsValidSkillInput(CodexSkillInput input) =>
        !string.IsNullOrWhiteSpace(input.Name) &&
        !string.IsNullOrWhiteSpace(input.Path) &&
        Path.IsPathRooted(input.Path) &&
        Path.GetFileName(input.Path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase);

    private static void AddResolved(
        ICollection<CodexSkillInput> result,
        CodexSkillInput input)
    {
        if (!IsValidSkillInput(input))
        {
            throw new InvalidOperationException(
                $"Skill '${input.Name}' is not bound to an absolute SKILL.md path.");
        }
        if (result.Any(existing => PathComparer.Equals(existing.Path, input.Path)))
        {
            return;
        }
        if (result.Count >= CodexFollowUpQueue.MaximumSkillInputs)
        {
            throw new InvalidOperationException(
                $"A prompt can explicitly invoke at most {CodexFollowUpQueue.MaximumSkillInputs} skills.");
        }

        result.Add(new CodexSkillInput(input.Name.Trim(), Path.GetFullPath(input.Path)));
    }
}
