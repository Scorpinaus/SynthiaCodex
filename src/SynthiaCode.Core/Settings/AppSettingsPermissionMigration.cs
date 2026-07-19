namespace SynthiaCode.Core.Settings;

public static class AppSettingsPermissionMigration
{
    public const int CurrentSchemaVersion = 1;

    public static bool Migrate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!string.IsNullOrWhiteSpace(settings.PermissionMode))
        {
            if (settings.ExecutionPolicySchemaVersion >= CurrentSchemaVersion)
            {
                return false;
            }

            settings.ExecutionPolicySchemaVersion = CurrentSchemaVersion;
            return true;
        }

        if (settings.SandboxModeOverride is null && settings.ApprovalPolicyOverride is null)
        {
            settings.PermissionMode = "custom";
        }
        else if (string.Equals(settings.SandboxModeOverride, "workspace-write", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(settings.ApprovalPolicyOverride, "on-request", StringComparison.OrdinalIgnoreCase))
        {
            settings.PermissionMode = "ask-for-approval";
        }
        else
        {
            settings.PermissionMode = "custom-legacy";
        }

        settings.ExecutionPolicySchemaVersion = CurrentSchemaVersion;
        return true;
    }
}
