using Microsoft.Win32;
using System.Windows;

namespace SynthiaCode.App.Services;

public sealed class WpfThemeService : IThemeService
{
    private static readonly string[] ThemeDictionaryNames = ["LightTheme.xaml", "DarkTheme.xaml", "HighContrastTheme.xaml"];
    private string requestedTheme = "System";

    public WpfThemeService()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public void ApplyTheme(string theme)
    {
        requestedTheme = theme;
        var resources = Application.Current?.Resources
            ?? throw new InvalidOperationException("WPF application resources are not available.");
        var resolvedTheme = ResolveTheme(theme);
        var replacement = new ResourceDictionary
        {
            Source = new Uri($"Themes/{resolvedTheme}Theme.xaml", UriKind.Relative)
        };

        var existing = resources.MergedDictionaries
            .Where(dictionary => ThemeDictionaryNames.Any(name =>
                dictionary.Source?.OriginalString.EndsWith(name, StringComparison.OrdinalIgnoreCase) == true))
            .ToList();
        foreach (var dictionary in existing)
        {
            resources.MergedDictionaries.Remove(dictionary);
        }

        resources.MergedDictionaries.Insert(0, replacement);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        var application = Application.Current;
        if (application is null || application.Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = application.Dispatcher.BeginInvoke(new Action(() => ApplyTheme(requestedTheme)));
    }

    private static string ResolveTheme(string? theme)
    {
        if (SystemParameters.HighContrast)
        {
            return "HighContrast";
        }

        if (string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase))
        {
            return "Dark";
        }

        if (string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase))
        {
            return "Light";
        }

        var appsUseLightTheme = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme",
            1);
        return appsUseLightTheme is int value && value == 0 ? "Dark" : "Light";
    }
}
