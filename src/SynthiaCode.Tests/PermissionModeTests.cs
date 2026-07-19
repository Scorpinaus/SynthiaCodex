using SynthiaCode.App.ViewModels;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Settings;
using SynthiaCode.Infrastructure.Settings;

internal static class PermissionModeTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("permission modes resolve exact modern and legacy policies", ResolvesPoliciesAsync),
        ("permission modes respect managed reviewer and profile restrictions", RespectsRequirementsAsync),
        ("permission mode settings migrate without broadening access", MigratesSettingsAsync),
        ("permission mode view model presents three modes and named profiles", PresentsModesAsync),
        ("main view model routes every lifecycle request through resolved permissions", MainLifecycleUsesResolverAsync)
    ];

    private static Task ResolvesPoliciesAsync()
    {
        var modern = new CodexPermissionCapabilities(SupportsPermissionProfiles: true, SupportsAutoReview: true);

        var ask = CodexPermissionModeResolver.Resolve(
            CodexPermissionMode.AskForApproval,
            null,
            [],
            CodexExecutionPolicyRequirements.Unrestricted,
            modern);
        Assert(ask.IsAvailable, "Ask for approval is available");
        Assert(ask.PermissionProfileId == ":workspace", "Ask uses the workspace profile");
        Assert(ask.Sandbox is null, "modern Ask omits the legacy sandbox");
        Assert(ask.ApprovalPolicy == CodexApprovalPolicy.OnRequest, "Ask uses on-request approvals");
        Assert(ask.ApprovalsReviewer == CodexApprovalsReviewer.User, "Ask uses the user reviewer");

        var approve = CodexPermissionModeResolver.Resolve(
            CodexPermissionMode.ApproveForMe,
            null,
            [],
            CodexExecutionPolicyRequirements.Unrestricted,
            modern);
        Assert(approve.PermissionProfileId == ":workspace", "Approve keeps the workspace boundary");
        Assert(approve.ApprovalsReviewer == CodexApprovalsReviewer.AutoReview, "Approve uses automatic review");

        var customDefault = CodexPermissionModeResolver.Resolve(
            CodexPermissionMode.Custom,
            null,
            [],
            CodexExecutionPolicyRequirements.Unrestricted,
            modern);
        Assert(customDefault.IsAvailable, "config.toml default is available");
        Assert(customDefault.PermissionProfileId is null && customDefault.Sandbox is null &&
               customDefault.ApprovalPolicy is null && customDefault.ApprovalsReviewer is null,
            "Custom default omits every policy override");

        var profile = new CodexPermissionProfileSummary("team-safe", "Team profile", true);
        var customNamed = CodexPermissionModeResolver.Resolve(
            CodexPermissionMode.Custom,
            profile.Id,
            [profile],
            CodexExecutionPolicyRequirements.Unrestricted,
            modern);
        Assert(customNamed.PermissionProfileId == "team-safe", "named Custom uses its profile only");
        Assert(customNamed.ApprovalPolicy is null && customNamed.ApprovalsReviewer is null,
            "named Custom does not override profile policy or reviewer");

        var legacy = CodexPermissionModeResolver.Resolve(
            CodexPermissionMode.AskForApproval,
            null,
            [],
            CodexExecutionPolicyRequirements.Unrestricted,
            new CodexPermissionCapabilities(SupportsPermissionProfiles: false, SupportsAutoReview: true));
        Assert(legacy.PermissionProfileId is null && legacy.Sandbox == CodexSandbox.WorkspaceWrite,
            "legacy Ask falls back to workspace-write");
        return Task.CompletedTask;
    }

    private static Task RespectsRequirementsAsync()
    {
        var requirements = new CodexExecutionPolicyRequirements(
            [CodexSandbox.WorkspaceWrite],
            [CodexApprovalPolicy.OnRequest],
            [CodexApprovalsReviewer.User],
            [":workspace", "allowed-profile"]);
        var capabilities = new CodexPermissionCapabilities(true, true);

        var approve = CodexPermissionModeResolver.Resolve(
            CodexPermissionMode.ApproveForMe,
            null,
            [],
            requirements,
            capabilities);
        Assert(!approve.IsAvailable && approve.UnavailableReason?.Contains("automatic", StringComparison.OrdinalIgnoreCase) == true,
            "managed reviewer restrictions disable Approve for me");

        var denied = CodexPermissionModeResolver.Resolve(
            CodexPermissionMode.Custom,
            "denied-profile",
            [new CodexPermissionProfileSummary("denied-profile", null, false)],
            requirements,
            capabilities);
        Assert(!denied.IsAvailable, "a denied named profile is not serialized");

        var missing = CodexPermissionModeResolver.Resolve(
            CodexPermissionMode.Custom,
            "missing-profile",
            [],
            requirements,
            capabilities);
        Assert(!missing.IsAvailable, "a stale named profile is not replaced silently");

        var unknown = CodexPermissionModeResolver.Resolve(
            "future-mode".ParsePermissionMode(),
            null,
            [],
            CodexExecutionPolicyRequirements.Unrestricted,
            capabilities);
        Assert(!unknown.IsAvailable, "an unknown future mode fails closed instead of becoming Ask");
        return Task.CompletedTask;
    }

    private static async Task MigratesSettingsAsync()
    {
        var safeLegacy = new AppSettings
        {
            PermissionMode = null,
            SandboxModeOverride = "workspace-write",
            ApprovalPolicyOverride = "on-request"
        };
        AppSettingsPermissionMigration.Migrate(safeLegacy);
        Assert(safeLegacy.PermissionMode == "ask-for-approval", "safe legacy defaults migrate to Ask");

        var inherited = new AppSettings
        {
            PermissionMode = null,
            SandboxModeOverride = null,
            ApprovalPolicyOverride = null
        };
        AppSettingsPermissionMigration.Migrate(inherited);
        Assert(inherited.PermissionMode == "custom", "explicit inheritance migrates to Custom");

        var nonstandard = new AppSettings
        {
            PermissionMode = null,
            SandboxModeOverride = "read-only",
            ApprovalPolicyOverride = "never"
        };
        AppSettingsPermissionMigration.Migrate(nonstandard);
        Assert(nonstandard.PermissionMode == "custom-legacy", "nonstandard legacy settings stay distinct");
        Assert(nonstandard.SandboxModeOverride == "read-only" && nonstandard.ApprovalPolicyOverride == "never",
            "nonstandard legacy values are preserved exactly");
        AppSettingsPermissionMigration.Migrate(nonstandard);
        Assert(nonstandard.PermissionMode == "custom-legacy", "migration is idempotent");

        var selected = new AppSettings
        {
            PermissionMode = "custom",
            CustomPermissionProfileId = "team-safe",
            ExecutionPolicySchemaVersion = AppSettingsPermissionMigration.CurrentSchemaVersion
        };
        var snapshot = AppSettingsSnapshot.Create(selected);
        Assert(snapshot.PermissionMode == "custom" && snapshot.CustomPermissionProfileId == "team-safe",
            "mode and profile survive snapshots");

        using var temp = TempWorkspace.Create();
        var store = new JsonSettingsStore(temp.Root, new TestLogger());
        await store.SaveAsync(selected);
        var reloaded = await store.LoadAsync();
        Assert(reloaded.PermissionMode == "custom" && reloaded.CustomPermissionProfileId == "team-safe" &&
               reloaded.ExecutionPolicySchemaVersion == AppSettingsPermissionMigration.CurrentSchemaVersion,
            "mode and profile survive a JSON settings round trip");
    }

    private static Task PresentsModesAsync()
    {
        var changed = 0;
        var viewModel = new ExecutionPolicyViewModel((_, _) => true, () => changed++);
        viewModel.Initialize("ask-for-approval", null, null, null);
        viewModel.ApplyCapabilities(new CodexPermissionCapabilities(true, true));
        viewModel.ApplyProfiles(
        [
            new CodexPermissionProfileSummary("team-safe", "Team profile", true),
            new CodexPermissionProfileSummary("blocked", "Managed off", false)
        ]);

        Assert(viewModel.ModeOptions.SequenceEqual(["Ask for approval", "Approve for me", "Custom"]),
            "the primary UI exposes exactly three modes");
        Assert(viewModel.SelectedMode == "Ask for approval", "Ask is selected after migration");
        viewModel.SelectedMode = "Approve for me";
        Assert(viewModel.ResolvedPolicy.ApprovalsReviewer == CodexApprovalsReviewer.AutoReview,
            "selecting Approve changes only the reviewer behavior");
        viewModel.SelectedMode = "Custom";
        viewModel.SelectedCustomProfileId = "team-safe";
        Assert(viewModel.ResolvedPolicy.PermissionProfileId == "team-safe", "Custom selects a named profile");
        Assert(viewModel.PermissionProfiles.Single(profile => profile.Id == "blocked").Allowed == false,
            "blocked profiles remain visible");
        var blockedOption = viewModel.CustomProfileOptions.Single(profile => profile.Id == "blocked");
        Assert(!blockedOption.IsAllowed && blockedOption.Description == "Managed off",
            "blocked profiles are disabled with their descriptions preserved");
        Assert(changed >= 2, "mode changes are persisted");
        return Task.CompletedTask;
    }

    private static Task MainLifecycleUsesResolverAsync()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "SynthiaCode.App")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
        var source = File.ReadAllText(Path.Combine(root, "src", "SynthiaCode.App", "ViewModels", "MainViewModel.cs"));
        Assert(!source.Contains("CodexApprovalsReviewer.User", StringComparison.Ordinal),
            "MainViewModel must not hardcode a reviewer on any lifecycle path");
        Assert(source.Contains("ResolvePermissionPolicy", StringComparison.Ordinal),
            "one permission resolver is shared by thread and turn lifecycle requests");
        return Task.CompletedTask;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
