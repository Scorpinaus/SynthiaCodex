namespace SynthiaCode.Core.Settings;

public static class AppSettingsSnapshot
{
    public static AppSettings Create(AppSettings source) => SettingsStorageMapper.Clone(source);
}
