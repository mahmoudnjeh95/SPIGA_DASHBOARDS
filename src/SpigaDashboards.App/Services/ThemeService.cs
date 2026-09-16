using System.Windows;
using SkiaSharp;
using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Services;

// Swaps the app's WPF chrome (backgrounds/text/borders, all DynamicResource-bound
// in Themes/DarkTheme.xaml and LightTheme.xaml) instantly across every open window,
// and exposes the current theme's colors for LiveChartsCore chart-building code
// (which draws via SkiaSharp, outside WPF's resource system -- those pick up a
// theme change on their next natural rebuild, e.g. the next slide rotation).
public static class ThemeService
{
    // Pack-URI form (not a bare relative path): the reliable way to load a
    // ResourceDictionary from code rather than from XAML markup, where the
    // base-URI context that makes a bare relative path resolve isn't available.
    private const string DarkThemeUri = "/SpigaDashboards.App;component/Themes/DarkTheme.xaml";
    private const string LightThemeUri = "/SpigaDashboards.App;component/Themes/LightTheme.xaml";

    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;

    public static SKColor ChartTextColor => CurrentTheme == AppTheme.Dark
        ? SKColors.White
        : new SKColor(0x1B, 0x1F, 0x26);

    public static SKColor ChartTileColor => CurrentTheme == AppTheme.Dark
        ? new SKColor(0x1B, 0x1F, 0x26)
        : SKColors.White;

    public static void Apply(AppTheme theme)
    {
        CurrentTheme = theme;

        var uri = theme == AppTheme.Dark ? DarkThemeUri : LightThemeUri;
        var newDictionary = new ResourceDictionary { Source = new Uri(uri, UriKind.Relative) };

        var appResources = Application.Current.Resources;
        var existing = appResources.MergedDictionaries
            .FirstOrDefault(d => d.Source != null && d.Source.OriginalString.Contains("Theme"));
        if (existing != null)
            appResources.MergedDictionaries.Remove(existing);

        appResources.MergedDictionaries.Add(newDictionary);
    }

    public static AppTheme Toggle(AppSettings settings, SettingsService settingsService)
    {
        var next = CurrentTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        Apply(next);
        settings.Theme = next;
        settingsService.Save(settings);
        return next;
    }
}
