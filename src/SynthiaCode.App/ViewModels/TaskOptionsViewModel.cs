using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using SynthiaCode.App.Services;
using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.App.ViewModels;

public sealed class TaskOptionsViewModel : ObservableObject
{
    private readonly Func<bool> isTurnRunning;
    private readonly Action selectedModelChanged;
    private readonly AsyncRelayCommand loadModelsCommand;
    private readonly RelayCommand openOptionsCommand;
    private readonly RelayCommand showOptionsMainCommand;
    private readonly RelayCommand showModelsCommand;
    private readonly RelayCommand showReasoningCommand;
    private string modelOverride = string.Empty;
    private string reasoningEffortOverride = string.Empty;
    private string accountPlanLabel = string.Empty;
    private string modelCatalogError = string.Empty;
    private CodexModelOption? selectedModel;
    private CodexReasoningOption? selectedReasoning;
    private CodexServiceTierSelection serviceTierSelection;
    private ComposerOptionsPage optionsPage;
    private bool isOptionsFlyoutOpen;
    private bool isModelCatalogLoading;
    private bool isModelCatalogStale = true;

    public TaskOptionsViewModel(
        IComposerSupportActions actions,
        Func<bool> isTurnRunning,
        Action selectedModelChanged)
    {
        this.isTurnRunning = isTurnRunning;
        this.selectedModelChanged = selectedModelChanged;
        LoadModelsCommand = loadModelsCommand = new AsyncRelayCommand(actions.LoadModelsAsync, actions.CanLoadModels);
        OpenOptionsCommand = openOptionsCommand = new RelayCommand(
            () =>
            {
                OptionsPage = ComposerOptionsPage.Main;
                IsOptionsFlyoutOpen = true;
                if (IsModelCatalogStale && !IsModelCatalogLoading)
                {
                    loadModelsCommand.Execute(null);
                }
            },
            () => !isTurnRunning());
        ShowOptionsMainCommand = showOptionsMainCommand = new RelayCommand(
            () => OptionsPage = ComposerOptionsPage.Main);
        ShowModelsCommand = showModelsCommand = new RelayCommand(
            () => OptionsPage = ComposerOptionsPage.Models,
            () => ModelCatalog.Count > 0);
        ShowReasoningCommand = showReasoningCommand = new RelayCommand(
            () => OptionsPage = ComposerOptionsPage.Reasoning,
            () => ReasoningOptions.Count > 0);
    }

    public ObservableCollection<string> ModelOptions { get; } = [];

    public ObservableCollection<CodexModelOption> ModelCatalog { get; } = [];

    public ObservableCollection<CodexReasoningOption> ReasoningOptions { get; } = [];

    public ObservableCollection<string> ReasoningEffortOptions { get; } = [];

    public ICommand LoadModelsCommand { get; }

    public ICommand OpenOptionsCommand { get; }

    public ICommand ShowOptionsMainCommand { get; }

    public ICommand ShowModelsCommand { get; }

    public ICommand ShowReasoningCommand { get; }

    public string ModelOverride
    {
        get => modelOverride;
        set
        {
            if (SetProperty(ref modelOverride, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(ModelSelectionSummary));
            }
        }
    }

    public string ReasoningEffortOverride
    {
        get => reasoningEffortOverride;
        set
        {
            if (SetProperty(ref reasoningEffortOverride, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(ModelSelectionSummary));
            }
        }
    }

    public CodexModelOption? SelectedModel
    {
        get => selectedModel;
        set
        {
            if (!SetProperty(ref selectedModel, value))
            {
                return;
            }

            if (value is not null)
            {
                ModelOverride = value.Model;
            }
            RebuildReasoningOptions();
            ReconcileFastAvailability();
            OnPropertyChanged(nameof(ModelSelectionSummary));
            OnPropertyChanged(nameof(FastModeDescription));
            selectedModelChanged();
        }
    }

    public CodexReasoningOption? SelectedReasoning
    {
        get => selectedReasoning;
        set
        {
            if (SetProperty(ref selectedReasoning, value))
            {
                ReasoningEffortOverride = value?.ProtocolValue ?? string.Empty;
                OnPropertyChanged(nameof(ModelSelectionSummary));
            }
        }
    }

    public CodexServiceTierSelection ServiceTierSelection
    {
        get => serviceTierSelection;
        set
        {
            if (SetProperty(ref serviceTierSelection, value))
            {
                OnPropertyChanged(nameof(IsFastModeEnabled));
                OnPropertyChanged(nameof(ModelSelectionSummary));
            }
        }
    }

    public bool IsFastModeEnabled
    {
        get => ServiceTierSelection == CodexServiceTierSelection.Fast;
        set
        {
            if (value && !IsFastModeAvailable)
            {
                return;
            }
            ServiceTierSelection = value
                ? CodexServiceTierSelection.Fast
                : CodexServiceTierSelection.Standard;
        }
    }

    public bool IsFastModeAvailable => SelectedModel?.SupportsFastMode == true;

    public string FastModeDescription => SelectedModel?.FastServiceTier?.Description
        ?? (SelectedModel is null
            ? "Load models to check Fast availability."
            : IsFastModeAvailable
                ? "Faster responses at higher credit use."
                : $"Fast is not available for {SelectedModel.DisplayName} on this account.");

    public string ModelSelectionSummary
    {
        get
        {
            var model = SelectedModel?.DisplayName
                ?? (string.IsNullOrWhiteSpace(ModelOverride) ? "Default model" : ModelOverride);
            var reasoning = SelectedReasoning?.DisplayName
                ?? ParseReasoningEffort(ReasoningEffortOverride)?.ToDisplayName();
            var values = new List<string> { model };
            if (!string.IsNullOrWhiteSpace(reasoning))
            {
                values.Add(reasoning);
            }
            if (IsFastModeEnabled)
            {
                values.Add("Fast");
            }
            return string.Join(" · ", values);
        }
    }

    public string AccountPlanLabel
    {
        get => accountPlanLabel;
        private set
        {
            if (SetProperty(ref accountPlanLabel, value))
            {
                OnPropertyChanged(nameof(HasAccountPlanLabel));
            }
        }
    }

    public bool HasAccountPlanLabel => !string.IsNullOrWhiteSpace(AccountPlanLabel);

    public bool IsModelCatalogLoading
    {
        get => isModelCatalogLoading;
        private set => SetProperty(ref isModelCatalogLoading, value);
    }

    public bool IsModelCatalogStale
    {
        get => isModelCatalogStale;
        private set => SetProperty(ref isModelCatalogStale, value);
    }

    public string ModelCatalogError
    {
        get => modelCatalogError;
        private set
        {
            if (SetProperty(ref modelCatalogError, value))
            {
                OnPropertyChanged(nameof(HasModelCatalogError));
            }
        }
    }

    public bool HasModelCatalogError => !string.IsNullOrWhiteSpace(ModelCatalogError);

    public bool IsOptionsFlyoutOpen
    {
        get => isOptionsFlyoutOpen;
        set => SetProperty(ref isOptionsFlyoutOpen, value);
    }

    public ComposerOptionsPage OptionsPage
    {
        get => optionsPage;
        set
        {
            if (SetProperty(ref optionsPage, value))
            {
                OnPropertyChanged(nameof(IsOptionsMainPage));
                OnPropertyChanged(nameof(IsOptionsModelPage));
                OnPropertyChanged(nameof(IsOptionsReasoningPage));
            }
        }
    }

    public bool IsOptionsMainPage => OptionsPage == ComposerOptionsPage.Main;

    public bool IsOptionsModelPage => OptionsPage == ComposerOptionsPage.Models;

    public bool IsOptionsReasoningPage => OptionsPage == ComposerOptionsPage.Reasoning;

    public bool IsTurnRunning => isTurnRunning();

    public void CloseForRunningTurn() => IsOptionsFlyoutOpen = false;

    public void ApplyModelCatalog(
        IEnumerable<CodexModelOption> models,
        CodexAccountInfo? account)
    {
        ArgumentNullException.ThrowIfNull(models);
        var requestedModel = ModelOverride;
        var requestedReasoning = ReasoningEffortOverride;
        var visibleModels = models
            .Where(model => !model.Hidden)
            .DistinctBy(model => model.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ModelCatalog.Clear();
        ModelOptions.Clear();
        foreach (var model in visibleModels)
        {
            ModelCatalog.Add(model);
            ModelOptions.Add(model.Model);
        }

        AccountPlanLabel = FormatAccountPlan(account);
        ModelCatalogError = string.Empty;
        IsModelCatalogLoading = false;
        IsModelCatalogStale = false;

        reasoningEffortOverride = requestedReasoning;
        var match = visibleModels.FirstOrDefault(model =>
                string.Equals(model.Model, requestedModel, StringComparison.OrdinalIgnoreCase))
            ?? visibleModels.FirstOrDefault(model => model.IsDefault)
            ?? visibleModels.FirstOrDefault();
        if (ReferenceEquals(SelectedModel, match))
        {
            RebuildReasoningOptions();
            ReconcileFastAvailability();
            OnPropertyChanged(nameof(FastModeDescription));
        }
        else
        {
            SelectedModel = match;
        }
        showModelsCommand.RaiseCanExecuteChanged();
        showReasoningCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(ModelSelectionSummary));
    }

    public void SetModelCatalogLoading()
    {
        IsModelCatalogLoading = true;
        ModelCatalogError = string.Empty;
    }

    public void SetModelCatalogError(string message)
    {
        IsModelCatalogLoading = false;
        IsModelCatalogStale = true;
        ModelCatalogError = message;
    }

    public void InvalidateModelCatalog()
    {
        IsModelCatalogStale = true;
        AccountPlanLabel = string.Empty;
    }

    public void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(IsTurnRunning));
        loadModelsCommand.RaiseCanExecuteChanged();
        openOptionsCommand.RaiseCanExecuteChanged();
        showOptionsMainCommand.RaiseCanExecuteChanged();
        showModelsCommand.RaiseCanExecuteChanged();
        showReasoningCommand.RaiseCanExecuteChanged();
    }

    private void RebuildReasoningOptions()
    {
        var requested = ParseReasoningEffort(ReasoningEffortOverride);
        ReasoningOptions.Clear();
        ReasoningEffortOptions.Clear();
        foreach (var option in SelectedModel?.SupportedReasoningEfforts ?? [])
        {
            ReasoningOptions.Add(option);
            ReasoningEffortOptions.Add(option.ProtocolValue);
        }

        SelectedReasoning = ReasoningOptions.FirstOrDefault(option => option.Effort == requested)
            ?? ReasoningOptions.FirstOrDefault(option => option.Effort == SelectedModel?.DefaultReasoningEffort)
            ?? ReasoningOptions.FirstOrDefault();
        showReasoningCommand.RaiseCanExecuteChanged();
    }

    private void ReconcileFastAvailability()
    {
        OnPropertyChanged(nameof(IsFastModeAvailable));
        if (!IsFastModeAvailable && ServiceTierSelection == CodexServiceTierSelection.Fast)
        {
            ServiceTierSelection = CodexServiceTierSelection.Standard;
        }
        OnPropertyChanged(nameof(IsFastModeEnabled));
    }

    private static CodexReasoningEffort? ParseReasoningEffort(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "none" => CodexReasoningEffort.None,
        "minimal" => CodexReasoningEffort.Minimal,
        "low" => CodexReasoningEffort.Low,
        "medium" => CodexReasoningEffort.Medium,
        "high" => CodexReasoningEffort.High,
        "xhigh" => CodexReasoningEffort.XHigh,
        _ => null
    };

    private static string FormatAccountPlan(CodexAccountInfo? account)
    {
        if (account is null ||
            !string.Equals(account.Type, "chatgpt", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(account.PlanType))
        {
            return string.Empty;
        }

        var plan = account.PlanType.ToLowerInvariant() switch
        {
            "self_serve_business_usage_based" or "business" => "Business",
            "enterprise_cbp_usage_based" or "enterprise" => "Enterprise",
            "prolite" => "Pro Lite",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(account.PlanType.Replace('_', ' '))
        };
        return $"ChatGPT {plan}";
    }
}
