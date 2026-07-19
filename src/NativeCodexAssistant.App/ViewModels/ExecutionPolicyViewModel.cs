using NativeCodexAssistant.Core.Codex.AppServer;

namespace NativeCodexAssistant.App.ViewModels;

public sealed class ExecutionPolicyViewModel : ObservableObject
{
    public const string InheritLabel = "Use Codex configuration";
    public const string ReadOnlyLabel = "Read only";
    public const string WorkspaceWriteLabel = "Workspace write";
    public const string FullAccessLabel = "Full access";
    public const string UntrustedLabel = "Ask for untrusted commands";
    public const string OnRequestLabel = "Ask when requested";
    public const string NeverLabel = "Never ask";

    private readonly Func<string, string, bool> confirm;
    private readonly Action? changed;
    private CodexExecutionPolicyRequirements requirements = CodexExecutionPolicyRequirements.Unrestricted;
    private CodexExecutionPolicyConfig? effectiveConfig;
    private CodexSandbox? sandboxOverride = CodexSandbox.WorkspaceWrite;
    private CodexApprovalPolicy? approvalPolicyOverride = CodexApprovalPolicy.OnRequest;
    private string selectedSandboxMode = WorkspaceWriteLabel;
    private string selectedApprovalPolicy = OnRequestLabel;
    private string? configurationWarning;
    private bool suppressChanged;

    public ExecutionPolicyViewModel(
        Func<string, string, bool> confirm,
        Action? changed = null)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        this.confirm = confirm;
        this.changed = changed;
    }

    public IReadOnlyList<string> SandboxModeOptions { get; } =
        [InheritLabel, ReadOnlyLabel, WorkspaceWriteLabel, FullAccessLabel];

    public IReadOnlyList<string> ApprovalPolicyOptions { get; } =
        [InheritLabel, UntrustedLabel, OnRequestLabel, NeverLabel];

    public CodexSandbox? SandboxOverride => sandboxOverride;

    public CodexApprovalPolicy? ApprovalPolicyOverride => approvalPolicyOverride;

    public string SelectedSandboxMode
    {
        get => selectedSandboxMode;
        set
        {
            var parsed = ParseSandboxLabel(value);
            if (!TrySelectSandbox(parsed))
            {
                OnPropertyChanged();
            }
        }
    }

    public string SelectedApprovalPolicy
    {
        get => selectedApprovalPolicy;
        set
        {
            var parsed = ParseApprovalLabel(value);
            if (!TrySelectApprovalPolicy(parsed))
            {
                OnPropertyChanged();
            }
        }
    }

    public bool IsManaged => requirements.AllowedSandboxes.Count > 0 || requirements.AllowedApprovalPolicies.Count > 0;

    public string ManagedSummary => IsManaged
        ? "Some execution settings are managed by Codex configuration requirements."
        : "No managed execution-policy restrictions were reported.";

    public string EffectiveSummary
    {
        get
        {
            var sandbox = effectiveConfig?.Sandbox ?? SandboxOverride;
            var approval = effectiveConfig?.ApprovalPolicy ?? ApprovalPolicyOverride;
            return $"Effective: {SandboxLabel(sandbox)} · {ApprovalLabel(approval)}";
        }
    }

    public string? ConfigurationWarning
    {
        get => configurationWarning;
        private set => SetProperty(ref configurationWarning, value);
    }

    public void Initialize(string? sandboxModeOverride, string? approvalPolicyOverrideValue)
    {
        suppressChanged = true;
        try
        {
            sandboxOverride = ParseSandboxProtocol(sandboxModeOverride);
            approvalPolicyOverride = ParseApprovalProtocol(approvalPolicyOverrideValue);
            selectedSandboxMode = SandboxLabel(sandboxOverride);
            selectedApprovalPolicy = ApprovalLabel(approvalPolicyOverride);
            OnPropertyChanged(nameof(SelectedSandboxMode));
            OnPropertyChanged(nameof(SelectedApprovalPolicy));
            OnPropertyChanged(nameof(EffectiveSummary));
            ValidateCurrentSelection();
        }
        finally
        {
            suppressChanged = false;
        }
    }

    public bool TrySelectSandbox(CodexSandbox? sandbox)
    {
        if (sandboxOverride == sandbox)
        {
            return true;
        }

        if (sandbox is not null &&
            requirements.AllowedSandboxes.Count > 0 &&
            !requirements.AllowedSandboxes.Contains(sandbox.Value))
        {
            ConfigurationWarning = $"{SandboxLabel(sandbox)} is blocked by managed Codex requirements.";
            return false;
        }

        if (sandbox == CodexSandbox.DangerFullAccess &&
            !confirm(
                "Enable full filesystem access?",
                "Full access removes the filesystem sandbox for future Codex requests. Only enable it for workspaces you trust."))
        {
            return false;
        }

        sandboxOverride = sandbox;
        selectedSandboxMode = SandboxLabel(sandbox);
        ConfigurationWarning = null;
        OnPropertyChanged(nameof(SandboxOverride));
        OnPropertyChanged(nameof(SelectedSandboxMode));
        OnPropertyChanged(nameof(EffectiveSummary));
        NotifyChanged();
        return true;
    }

    public bool TrySelectApprovalPolicy(CodexApprovalPolicy? policy)
    {
        if (approvalPolicyOverride == policy)
        {
            return true;
        }

        if (policy is not null &&
            requirements.AllowedApprovalPolicies.Count > 0 &&
            !requirements.AllowedApprovalPolicies.Contains(policy.Value))
        {
            ConfigurationWarning = $"{ApprovalLabel(policy)} is blocked by managed Codex requirements.";
            return false;
        }

        if (policy == CodexApprovalPolicy.Never &&
            !confirm(
                "Disable approval prompts?",
                "Never ask allows Codex to proceed without interactive approval when the sandbox permits it."))
        {
            return false;
        }

        approvalPolicyOverride = policy;
        selectedApprovalPolicy = ApprovalLabel(policy);
        ConfigurationWarning = null;
        OnPropertyChanged(nameof(ApprovalPolicyOverride));
        OnPropertyChanged(nameof(SelectedApprovalPolicy));
        OnPropertyChanged(nameof(EffectiveSummary));
        NotifyChanged();
        return true;
    }

    public void ApplyRequirements(CodexExecutionPolicyRequirements value)
    {
        requirements = value ?? CodexExecutionPolicyRequirements.Unrestricted;
        OnPropertyChanged(nameof(IsManaged));
        OnPropertyChanged(nameof(ManagedSummary));
        var changedSelection = false;
        if (sandboxOverride is not null &&
            requirements.AllowedSandboxes.Count > 0 &&
            !requirements.AllowedSandboxes.Contains(sandboxOverride.Value))
        {
            sandboxOverride = null;
            selectedSandboxMode = InheritLabel;
            changedSelection = true;
            OnPropertyChanged(nameof(SandboxOverride));
            OnPropertyChanged(nameof(SelectedSandboxMode));
        }

        if (approvalPolicyOverride is not null &&
            requirements.AllowedApprovalPolicies.Count > 0 &&
            !requirements.AllowedApprovalPolicies.Contains(approvalPolicyOverride.Value))
        {
            approvalPolicyOverride = null;
            selectedApprovalPolicy = InheritLabel;
            changedSelection = true;
            OnPropertyChanged(nameof(ApprovalPolicyOverride));
            OnPropertyChanged(nameof(SelectedApprovalPolicy));
        }

        ValidateCurrentSelection();
        OnPropertyChanged(nameof(EffectiveSummary));
        if (changedSelection)
        {
            NotifyChanged();
        }
    }

    public void ApplyEffectiveConfig(CodexExecutionPolicyConfig value)
    {
        effectiveConfig = value;
        OnPropertyChanged(nameof(EffectiveSummary));
    }

    public string? SandboxSettingsValue => SandboxOverride?.ToProtocolValue();

    public string? ApprovalSettingsValue => ApprovalPolicyOverride?.ToProtocolValue();

    private void ValidateCurrentSelection()
    {
        if (sandboxOverride is not null &&
            requirements.AllowedSandboxes.Count > 0 &&
            !requirements.AllowedSandboxes.Contains(sandboxOverride.Value))
        {
            ConfigurationWarning = $"{SandboxLabel(sandboxOverride)} is blocked by managed Codex requirements.";
            return;
        }

        if (approvalPolicyOverride is not null &&
            requirements.AllowedApprovalPolicies.Count > 0 &&
            !requirements.AllowedApprovalPolicies.Contains(approvalPolicyOverride.Value))
        {
            ConfigurationWarning = $"{ApprovalLabel(approvalPolicyOverride)} is blocked by managed Codex requirements.";
            return;
        }

        ConfigurationWarning = null;
    }

    private void NotifyChanged()
    {
        if (!suppressChanged)
        {
            changed?.Invoke();
        }
    }

    private static CodexSandbox? ParseSandboxLabel(string? value) => value switch
    {
        InheritLabel => null,
        ReadOnlyLabel => CodexSandbox.ReadOnly,
        WorkspaceWriteLabel => CodexSandbox.WorkspaceWrite,
        FullAccessLabel => CodexSandbox.DangerFullAccess,
        _ => throw new ArgumentException($"Unknown sandbox option: {value}", nameof(value))
    };

    private static CodexApprovalPolicy? ParseApprovalLabel(string? value) => value switch
    {
        InheritLabel => null,
        UntrustedLabel => CodexApprovalPolicy.Untrusted,
        OnRequestLabel => CodexApprovalPolicy.OnRequest,
        NeverLabel => CodexApprovalPolicy.Never,
        _ => throw new ArgumentException($"Unknown approval option: {value}", nameof(value))
    };

    private static CodexSandbox? ParseSandboxProtocol(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "read-only" => CodexSandbox.ReadOnly,
        "workspace-write" => CodexSandbox.WorkspaceWrite,
        "danger-full-access" => CodexSandbox.DangerFullAccess,
        _ => CodexSandbox.WorkspaceWrite
    };

    private static CodexApprovalPolicy? ParseApprovalProtocol(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "untrusted" => CodexApprovalPolicy.Untrusted,
        "on-request" => CodexApprovalPolicy.OnRequest,
        "never" => CodexApprovalPolicy.Never,
        _ => CodexApprovalPolicy.OnRequest
    };

    private static string SandboxLabel(CodexSandbox? sandbox) => sandbox switch
    {
        null => InheritLabel,
        CodexSandbox.ReadOnly => ReadOnlyLabel,
        CodexSandbox.WorkspaceWrite => WorkspaceWriteLabel,
        CodexSandbox.DangerFullAccess => FullAccessLabel,
        _ => InheritLabel
    };

    private static string ApprovalLabel(CodexApprovalPolicy? policy) => policy switch
    {
        null => InheritLabel,
        CodexApprovalPolicy.Untrusted => UntrustedLabel,
        CodexApprovalPolicy.OnRequest => OnRequestLabel,
        CodexApprovalPolicy.Never => NeverLabel,
        _ => InheritLabel
    };
}
