using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.App.ViewModels;

public sealed record PermissionProfileOption(
    string? Id,
    string Label,
    string Description,
    bool IsAllowed);

public sealed class ExecutionPolicyViewModel : ObservableObject
{
    public const string AskForApprovalLabel = "Ask for approval";
    public const string ApproveForMeLabel = "Approve for me";
    public const string CustomLabel = "Custom";
    public const string CustomLegacyLabel = "Custom · Legacy override";
    public const string UnknownSavedModeLabel = "Unavailable saved mode";
    public const string ConfigDefaultLabel = "Use config.toml default";
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
    private CodexPermissionCapabilities capabilities = new(false, true);
    private IReadOnlyList<CodexPermissionProfileSummary> profiles = [];
    private CodexExecutionPolicyConfig? effectiveConfig;
    private CodexPermissionMode mode = CodexPermissionMode.AskForApproval;
    private string? customProfileId;
    private CodexSandbox? legacySandbox = CodexSandbox.WorkspaceWrite;
    private CodexApprovalPolicy? legacyApprovalPolicy = CodexApprovalPolicy.OnRequest;
    private string? configurationWarning;
    private bool suppressChanged;

    public ExecutionPolicyViewModel(Func<string, string, bool> confirm, Action? changed = null)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        this.confirm = confirm;
        this.changed = changed;
    }

    public IReadOnlyList<string> ModeOptions { get; } =
        [AskForApprovalLabel, ApproveForMeLabel, CustomLabel];

    public IReadOnlyList<string> SandboxModeOptions { get; } =
        [InheritLabel, ReadOnlyLabel, WorkspaceWriteLabel, FullAccessLabel];

    public IReadOnlyList<string> ApprovalPolicyOptions { get; } =
        [InheritLabel, UntrustedLabel, OnRequestLabel, NeverLabel];

    public IReadOnlyList<CodexPermissionProfileSummary> PermissionProfiles => profiles;

    public IReadOnlyList<PermissionProfileOption> CustomProfileOptions =>
        [
            new(null, ConfigDefaultLabel, "Follow the default_permissions setting from config.toml.", true),
            .. profiles.Select(profile => new PermissionProfileOption(
                profile.Id,
                profile.Id,
                profile.Description ?? "Named config.toml permission profile",
                profile.Allowed &&
                (requirements.AllowedPermissionProfiles.Count == 0 ||
                 requirements.AllowedPermissionProfiles.Contains(profile.Id, StringComparer.Ordinal))))
        ];

    public string SelectedMode
    {
        get => ModeLabel(mode);
        set
        {
            var selected = ParseModeLabel(value);
            if (selected == mode)
            {
                return;
            }

            mode = selected;
            if (mode != CodexPermissionMode.Custom)
            {
                customProfileId = null;
            }

            NotifyPolicyChanged();
        }
    }

    public string SelectedCustomProfile
    {
        get => customProfileId ?? ConfigDefaultLabel;
        set => SelectedCustomProfileId = string.Equals(value, ConfigDefaultLabel, StringComparison.Ordinal) ? null : value;
    }

    public string? SelectedCustomProfileId
    {
        get => customProfileId;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(customProfileId, normalized, StringComparison.Ordinal))
            {
                return;
            }

            customProfileId = normalized;
            NotifyPolicyChanged();
        }
    }

    public bool IsCustom => mode is CodexPermissionMode.Custom or CodexPermissionMode.CustomLegacy;

    public bool IsLegacyCustom => mode == CodexPermissionMode.CustomLegacy;

    public CodexResolvedPermissionMode ResolvedPolicy => CodexPermissionModeResolver.Resolve(
        mode,
        customProfileId,
        profiles,
        requirements,
        capabilities,
        legacySandbox,
        legacyApprovalPolicy);

    public CodexSandbox? SandboxOverride => ResolvedPolicy.Sandbox;

    public CodexApprovalPolicy? ApprovalPolicyOverride => ResolvedPolicy.ApprovalPolicy;

    public CodexApprovalsReviewer? ApprovalsReviewerOverride => ResolvedPolicy.ApprovalsReviewer;

    public string? PermissionProfileId => ResolvedPolicy.PermissionProfileId;

    public string SelectedSandboxMode
    {
        get => SandboxLabel(legacySandbox);
        set
        {
            if (!TrySelectSandbox(ParseSandboxLabel(value)))
            {
                OnPropertyChanged();
            }
        }
    }

    public string SelectedApprovalPolicy
    {
        get => ApprovalLabel(legacyApprovalPolicy);
        set
        {
            if (!TrySelectApprovalPolicy(ParseApprovalLabel(value)))
            {
                OnPropertyChanged();
            }
        }
    }

    public bool IsManaged =>
        requirements.AllowedSandboxes.Count > 0 ||
        requirements.AllowedApprovalPolicies.Count > 0 ||
        requirements.AllowedApprovalsReviewers.Count > 0 ||
        requirements.AllowedPermissionProfiles.Count > 0;

    public string ManagedSummary => IsManaged
        ? "Some permission modes are restricted by managed Codex requirements."
        : "No managed permission-mode restrictions were reported.";

    public string EffectiveSummary
    {
        get
        {
            var resolved = ResolvedPolicy;
            if (!resolved.IsAvailable)
            {
                return "Effective: unavailable";
            }

            if (mode == CodexPermissionMode.Custom && string.IsNullOrWhiteSpace(customProfileId))
            {
                return "Effective: config.toml default";
            }

            var boundary = resolved.PermissionProfileId ?? SandboxLabel(resolved.Sandbox);
            var reviewer = resolved.ApprovalsReviewer switch
            {
                CodexApprovalsReviewer.User => "Ask the user",
                CodexApprovalsReviewer.AutoReview => "Review automatically",
                null => "Profile/config reviewer",
                _ => "Legacy reviewer"
            };
            return $"Effective: {boundary} · {reviewer}";
        }
    }

    public string? ConfigurationWarning
    {
        get => configurationWarning ?? ResolvedPolicy.UnavailableReason;
        private set => SetProperty(ref configurationWarning, value);
    }

    public string PermissionModeSettingsValue => mode.ToSettingsValue();

    public string? CustomProfileSettingsValue => customProfileId;

    public string? SandboxSettingsValue => mode == CodexPermissionMode.CustomLegacy ? legacySandbox?.ToProtocolValue() : null;

    public string? ApprovalSettingsValue => mode == CodexPermissionMode.CustomLegacy ? legacyApprovalPolicy?.ToProtocolValue() : null;

    public void Initialize(string? sandboxModeOverride, string? approvalPolicyOverrideValue)
    {
        var inferredMode = sandboxModeOverride is null && approvalPolicyOverrideValue is null
            ? "custom"
            : string.Equals(sandboxModeOverride, "workspace-write", StringComparison.OrdinalIgnoreCase) &&
              string.Equals(approvalPolicyOverrideValue, "on-request", StringComparison.OrdinalIgnoreCase)
                ? "ask-for-approval"
                : "custom-legacy";
        Initialize(inferredMode, null, sandboxModeOverride, approvalPolicyOverrideValue);
    }

    public void Initialize(
        string? permissionMode,
        string? selectedProfileId,
        string? sandboxModeOverride,
        string? approvalPolicyOverrideValue)
    {
        suppressChanged = true;
        try
        {
            mode = permissionMode.ParsePermissionMode();
            customProfileId = string.IsNullOrWhiteSpace(selectedProfileId) ? null : selectedProfileId;
            legacySandbox = ParseSandboxProtocol(sandboxModeOverride);
            legacyApprovalPolicy = ParseApprovalProtocol(approvalPolicyOverrideValue);
            ConfigurationWarning = null;
            RaiseAll();
        }
        finally
        {
            suppressChanged = false;
        }
    }

    public void ApplyCapabilities(CodexPermissionCapabilities value)
    {
        capabilities = value ?? new CodexPermissionCapabilities(false, true);
        RaiseAll();
    }

    public void ApplyProfiles(IReadOnlyList<CodexPermissionProfileSummary> value)
    {
        profiles = value ?? [];
        OnPropertyChanged(nameof(PermissionProfiles));
        OnPropertyChanged(nameof(CustomProfileOptions));
        RaiseResolution();
    }

    public bool TrySelectSandbox(CodexSandbox? sandbox)
    {
        if (sandbox is not null && requirements.AllowedSandboxes.Count > 0 && !requirements.AllowedSandboxes.Contains(sandbox.Value))
        {
            ConfigurationWarning = $"{SandboxLabel(sandbox)} is blocked by managed Codex requirements.";
            return false;
        }

        if (sandbox == CodexSandbox.DangerFullAccess &&
            !confirm("Enable full filesystem access?", "Full access removes the filesystem sandbox for future Codex requests. Only enable it for workspaces you trust."))
        {
            return false;
        }

        legacySandbox = sandbox;
        mode = CodexPermissionMode.CustomLegacy;
        ConfigurationWarning = null;
        NotifyPolicyChanged();
        return true;
    }

    public bool TrySelectApprovalPolicy(CodexApprovalPolicy? policy)
    {
        if (policy is not null && requirements.AllowedApprovalPolicies.Count > 0 && !requirements.AllowedApprovalPolicies.Contains(policy.Value))
        {
            ConfigurationWarning = $"{ApprovalLabel(policy)} is blocked by managed Codex requirements.";
            return false;
        }

        if (policy == CodexApprovalPolicy.Never &&
            !confirm("Disable approval prompts?", "Never ask allows Codex to proceed without interactive approval when the sandbox permits it."))
        {
            return false;
        }

        legacyApprovalPolicy = policy;
        mode = CodexPermissionMode.CustomLegacy;
        ConfigurationWarning = null;
        NotifyPolicyChanged();
        return true;
    }

    public void ApplyRequirements(CodexExecutionPolicyRequirements value)
    {
        requirements = value ?? CodexExecutionPolicyRequirements.Unrestricted;
        OnPropertyChanged(nameof(CustomProfileOptions));
        RaiseAll();
    }

    public void ApplyEffectiveConfig(CodexExecutionPolicyConfig value)
    {
        effectiveConfig = value;
        OnPropertyChanged(nameof(EffectiveSummary));
    }

    private void NotifyPolicyChanged()
    {
        RaiseAll();
        if (!suppressChanged)
        {
            changed?.Invoke();
        }
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(SelectedMode));
        OnPropertyChanged(nameof(SelectedCustomProfile));
        OnPropertyChanged(nameof(SelectedCustomProfileId));
        OnPropertyChanged(nameof(IsCustom));
        OnPropertyChanged(nameof(IsLegacyCustom));
        OnPropertyChanged(nameof(SelectedSandboxMode));
        OnPropertyChanged(nameof(SelectedApprovalPolicy));
        OnPropertyChanged(nameof(IsManaged));
        OnPropertyChanged(nameof(ManagedSummary));
        RaiseResolution();
    }

    private void RaiseResolution()
    {
        OnPropertyChanged(nameof(ResolvedPolicy));
        OnPropertyChanged(nameof(SandboxOverride));
        OnPropertyChanged(nameof(ApprovalPolicyOverride));
        OnPropertyChanged(nameof(ApprovalsReviewerOverride));
        OnPropertyChanged(nameof(PermissionProfileId));
        OnPropertyChanged(nameof(EffectiveSummary));
        OnPropertyChanged(nameof(ConfigurationWarning));
    }

    private static CodexPermissionMode ParseModeLabel(string? value) => value switch
    {
        AskForApprovalLabel => CodexPermissionMode.AskForApproval,
        ApproveForMeLabel => CodexPermissionMode.ApproveForMe,
        CustomLabel => CodexPermissionMode.Custom,
        CustomLegacyLabel => CodexPermissionMode.CustomLegacy,
        _ => throw new ArgumentException($"Unknown permission mode: {value}", nameof(value))
    };

    private static string ModeLabel(CodexPermissionMode value) => value switch
    {
        CodexPermissionMode.AskForApproval => AskForApprovalLabel,
        CodexPermissionMode.ApproveForMe => ApproveForMeLabel,
        CodexPermissionMode.Custom => CustomLabel,
        CodexPermissionMode.CustomLegacy => CustomLegacyLabel,
        _ => UnknownSavedModeLabel
    };

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
        _ => null
    };

    private static CodexApprovalPolicy? ParseApprovalProtocol(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "untrusted" => CodexApprovalPolicy.Untrusted,
        "on-request" => CodexApprovalPolicy.OnRequest,
        "never" => CodexApprovalPolicy.Never,
        _ => null
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
