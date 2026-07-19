using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows.Input;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Logging;

namespace SynthiaCode.App.ViewModels;

public sealed class AccountViewModel : ObservableObject
{
    private readonly Func<CancellationToken, Task<CodexAccountReadResult>> readAccount;
    private readonly Func<CancellationToken, Task<CodexAccountRateLimitsResult>> readRateLimits;
    private readonly IAppLogger logger;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly AsyncRelayCommand refreshCommand;
    private string displayName = "Checking account";
    private string identityDetail = "Connecting to Codex";
    private string initials = "C";
    private string usageStatus = "Usage will appear after account discovery";
    private string resetCreditsLabel = string.Empty;
    private string creditsLabel = string.Empty;
    private bool isChatGptAccount;
    private bool isSignedIn;
    private bool isFlyoutOpen;
    private bool isBusy;
    private bool isStale = true;
    private bool isActive;
    private DateTimeOffset? lastRefreshedAt;

    public AccountViewModel(
        Func<CancellationToken, Task<CodexAccountReadResult>> readAccount,
        Func<CancellationToken, Task<CodexAccountRateLimitsResult>> readRateLimits,
        Action openSettings,
        ICommand signInCommand,
        ICommand signOutCommand,
        IAppLogger logger)
    {
        this.readAccount = readAccount ?? throw new ArgumentNullException(nameof(readAccount));
        this.readRateLimits = readRateLimits ?? throw new ArgumentNullException(nameof(readRateLimits));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(openSettings);
        ArgumentNullException.ThrowIfNull(signInCommand);
        ArgumentNullException.ThrowIfNull(signOutCommand);

        RefreshCommand = refreshCommand = new AsyncRelayCommand(
            () => RefreshAsync(),
            () => !IsBusy);
        OpenSettingsCommand = new RelayCommand(() =>
        {
            IsFlyoutOpen = false;
            openSettings();
        });
        SignInCommand = new RelayCommand(
            parameter =>
            {
                IsFlyoutOpen = false;
                signInCommand.Execute(parameter);
            },
            signInCommand.CanExecute);
        SignOutCommand = new RelayCommand(
            parameter =>
            {
                IsFlyoutOpen = false;
                signOutCommand.Execute(parameter);
            },
            signOutCommand.CanExecute);
    }

    public ObservableCollection<AccountUsageWindowViewModel> UsageBuckets { get; } = [];

    public ICommand RefreshCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand SignInCommand { get; }

    public ICommand SignOutCommand { get; }

    public string DisplayName
    {
        get => displayName;
        private set => SetProperty(ref displayName, value);
    }

    public string IdentityDetail
    {
        get => identityDetail;
        private set => SetProperty(ref identityDetail, value);
    }

    public string Initials
    {
        get => initials;
        private set => SetProperty(ref initials, value);
    }

    public string UsageStatus
    {
        get => usageStatus;
        private set => SetProperty(ref usageStatus, value);
    }

    public string ResetCreditsLabel
    {
        get => resetCreditsLabel;
        private set
        {
            if (SetProperty(ref resetCreditsLabel, value))
            {
                OnPropertyChanged(nameof(HasResetCredits));
            }
        }
    }

    public string CreditsLabel
    {
        get => creditsLabel;
        private set
        {
            if (SetProperty(ref creditsLabel, value))
            {
                OnPropertyChanged(nameof(HasCredits));
            }
        }
    }

    public bool HasResetCredits => !string.IsNullOrWhiteSpace(ResetCreditsLabel);

    public bool HasCredits => !string.IsNullOrWhiteSpace(CreditsLabel);

    public bool HasUsage => UsageBuckets.Count > 0;

    public bool IsChatGptAccount
    {
        get => isChatGptAccount;
        private set => SetProperty(ref isChatGptAccount, value);
    }

    public bool IsSignedIn
    {
        get => isSignedIn;
        private set => SetProperty(ref isSignedIn, value);
    }

    public bool IsFlyoutOpen
    {
        get => isFlyoutOpen;
        set => SetProperty(ref isFlyoutOpen, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                refreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsStale
    {
        get => isStale;
        private set => SetProperty(ref isStale, value);
    }

    public bool IsActive
    {
        get => isActive;
        private set => SetProperty(ref isActive, value);
    }

    public DateTimeOffset? LastRefreshedAt
    {
        get => lastRefreshedAt;
        private set => SetProperty(ref lastRefreshedAt, value);
    }

    public void Activate()
    {
        if (IsActive)
        {
            return;
        }

        IsActive = true;
        _ = RefreshAsync();
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            IsBusy = true;
            var account = await readAccount(cancellationToken).ConfigureAwait(true);
            ApplyAccount(account);
            if (IsChatGptAccount)
            {
                ApplyRateLimits(await readRateLimits(cancellationToken).ConfigureAwait(true));
            }

            LastRefreshedAt = DateTimeOffset.UtcNow;
            IsStale = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            IsStale = true;
            UsageStatus = HasUsage ? "Usage may be out of date" : "Usage unavailable";
            logger.Log(
                AppLogLevel.Warning,
                "account_refresh_failed",
                "Could not refresh Codex account information.",
                exception: exception);
        }
        finally
        {
            IsBusy = false;
            refreshGate.Release();
        }
    }

    public void ApplyAccount(CodexAccountReadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        UsageBuckets.Clear();
        OnPropertyChanged(nameof(HasUsage));
        ResetCreditsLabel = string.Empty;
        CreditsLabel = string.Empty;

        if (result.Account is null)
        {
            IsSignedIn = false;
            IsChatGptAccount = false;
            DisplayName = result.RequiresOpenAiAuth ? "Not signed in" : "Codex user";
            IdentityDetail = result.RequiresOpenAiAuth
                ? "Sign in to view account usage"
                : "Authentication is handled by the active provider";
            Initials = "C";
            UsageStatus = result.RequiresOpenAiAuth ? "Sign in to view usage" : "ChatGPT usage is not applicable";
            return;
        }

        IsSignedIn = true;
        IsChatGptAccount = string.Equals(result.Account.Type, "chatgpt", StringComparison.OrdinalIgnoreCase);
        var plan = FormatPlan(result.Account.PlanType);
        if (IsChatGptAccount)
        {
            var email = result.Account.Email;
            DisplayName = CreateDisplayName(email);
            Initials = CreateInitials(DisplayName);
            IdentityDetail = JoinIdentityDetail(email, plan);
            UsageStatus = "Loading usage remaining";
            return;
        }

        DisplayName = result.Account.Type.ToLowerInvariant() switch
        {
            "apikey" => "API key account",
            "amazonbedrock" => "Amazon Bedrock",
            _ => "Codex user"
        };
        Initials = result.Account.Type.Equals("apiKey", StringComparison.OrdinalIgnoreCase) ? "AK" : "C";
        IdentityDetail = result.Account.CredentialSource ?? plan ?? "Provider-managed account";
        UsageStatus = "ChatGPT usage is not available for this account";
    }

    public void ApplyRateLimits(CodexAccountRateLimitsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        UsageBuckets.Clear();
        var showBucketNames = result.Limits.Count > 1;
        foreach (var limit in result.Limits)
        {
            var bucketLabel = showBucketNames ? limit.LimitName ?? limit.LimitId : null;
            AddWindow(limit.Primary, bucketLabel);
            AddWindow(limit.Secondary, bucketLabel);
        }

        OnPropertyChanged(nameof(HasUsage));
        UsageStatus = HasUsage ? "Usage remaining" : "Usage limits unavailable";
        ResetCreditsLabel = result.ResetCreditsAvailable is > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{result.ResetCreditsAvailable} limit reset{(result.ResetCreditsAvailable == 1 ? string.Empty : "s")} available")
            : string.Empty;
        CreditsLabel = FormatCredits(result.Limits.Select(limit => limit.Credits).FirstOrDefault(credits => credits is not null));
    }

    public bool TryApplyNotification(AppServerNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (notification.Method == "account/rateLimits/updated" &&
            notification.Params["rateLimits"] is JsonObject rateLimits)
        {
            ApplyRateLimits(new CodexAccountRateLimitsResult(
                [CodexAccountProtocolParser.ParseRateLimitSnapshot(rateLimits)],
                null));
            LastRefreshedAt = DateTimeOffset.UtcNow;
            IsStale = false;
            return true;
        }

        if (notification.Method is "account/updated" or "account/login/completed")
        {
            IsStale = true;
            _ = RefreshAsync();
            return true;
        }

        return notification.Method.StartsWith("account/", StringComparison.Ordinal);
    }

    public void MarkDisconnected()
    {
        IsStale = true;
        if (HasUsage)
        {
            UsageStatus = "Usage may be out of date";
        }
    }

    private void AddWindow(CodexRateLimitWindow? window, string? bucketLabel)
    {
        if (window is null)
        {
            return;
        }

        UsageBuckets.Add(new AccountUsageWindowViewModel(
            bucketLabel,
            FormatDuration(window.WindowDurationMins),
            Math.Clamp(100 - window.UsedPercent, 0, 100),
            FormatReset(window.ResetsAt)));
    }

    private static string CreateDisplayName(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return "ChatGPT user";
        }

        var separator = email.IndexOf('@');
        return separator > 0 ? email[..separator] : email;
    }

    private static string CreateInitials(string displayName)
    {
        var parts = displayName.Split(['.', '_', '-', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            return string.Concat(parts[0][0], parts[1][0]).ToUpperInvariant();
        }

        var value = parts.FirstOrDefault() ?? "C";
        return value[..Math.Min(2, value.Length)].ToUpperInvariant();
    }

    private static string JoinIdentityDetail(string? email, string? plan)
    {
        var values = new[] { email, plan }.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        return values.Count == 0 ? "ChatGPT account" : string.Join(" · ", values);
    }

    private static string? FormatPlan(string? planType) => planType?.ToLowerInvariant() switch
    {
        null or "" => null,
        "self_serve_business_usage_based" or "business" => "Business",
        "enterprise_cbp_usage_based" or "enterprise" => "Enterprise",
        "prolite" => "Pro Lite",
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(planType.Replace('_', ' '))
    };

    private static string FormatDuration(long? minutes)
    {
        if (minutes is null or <= 0)
        {
            return "Usage limit";
        }

        if (minutes == 10_080)
        {
            return "Weekly limit";
        }

        if (minutes % 1_440 == 0)
        {
            var days = minutes / 1_440;
            return days == 1 ? "Daily limit" : $"{days}-day limit";
        }

        if (minutes % 60 == 0)
        {
            var hours = minutes / 60;
            return $"{hours}-hour limit";
        }

        return $"{minutes}-minute limit";
    }

    private static string FormatReset(DateTimeOffset? reset) => reset is null
        ? string.Empty
        : $"Resets {reset.Value.ToLocalTime():g}";

    private static string FormatCredits(CodexCreditsSnapshot? credits)
    {
        if (credits is null)
        {
            return string.Empty;
        }

        if (credits.Unlimited)
        {
            return "Credits: unlimited";
        }

        return !string.IsNullOrWhiteSpace(credits.Balance)
            ? $"Credits remaining: {credits.Balance}"
            : credits.HasCredits ? "Credits available" : "No credits remaining";
    }
}

public sealed record AccountUsageWindowViewModel(
    string? BucketLabel,
    string Label,
    int RemainingPercent,
    string ResetLabel)
{
    public bool HasBucketLabel => !string.IsNullOrWhiteSpace(BucketLabel);

    public string RemainingLabel => $"{RemainingPercent}% remaining";

    public bool IsLow => RemainingPercent is > 0 and <= 20;

    public bool IsExhausted => RemainingPercent == 0;
}
