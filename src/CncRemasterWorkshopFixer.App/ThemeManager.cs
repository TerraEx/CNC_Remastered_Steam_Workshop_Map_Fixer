using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace CncRemasterWorkshopFixer;

public enum AppTheme
{
    System,
    Light,
    Dark
}

public enum AppAppearance
{
    Standard,
    Gdi,
    Nod,
    Allies,
    Soviets
}

public sealed record ThemePalette(
    string Window,
    string Panel,
    string PanelAlt,
    string Header,
    string Border,
    string Text,
    string Muted,
    string Accent,
    string AccentHover,
    string PrimaryButtonText,
    string Selection,
    string SelectionText,
    string Tooltip,
    string TooltipText,
    string DisabledText,
    string Focus);

public static class ThemeManager
{
    public static IReadOnlyList<AppTheme> SupportedThemes { get; } =
        Enum.GetValues<AppTheme>();
    public static IReadOnlyList<AppAppearance> SupportedAppearances { get; } =
        Enum.GetValues<AppAppearance>();

    public static string DisplayName(AppTheme theme) => theme switch
    {
        _ => theme.ToString()
    };

    public static string DisplayName(AppAppearance appearance) => appearance switch
    {
        AppAppearance.Gdi => "GDI",
        _ => appearance.ToString()
    };

    public static ThemePalette GetPalette(AppTheme resolvedTheme, AppAppearance appearance) =>
        (resolvedTheme, appearance) switch
    {
        (AppTheme.Light, AppAppearance.Standard) => PaletteCatalog.LightStandard,
        (AppTheme.Dark, AppAppearance.Standard) => PaletteCatalog.DarkStandard,
        (AppTheme.Light, AppAppearance.Gdi) => PaletteCatalog.LightGdi,
        (AppTheme.Dark, AppAppearance.Gdi) => PaletteCatalog.DarkGdi,
        (AppTheme.Light, AppAppearance.Nod) => PaletteCatalog.LightNod,
        (AppTheme.Dark, AppAppearance.Nod) => PaletteCatalog.DarkNod,
        (AppTheme.Light, AppAppearance.Allies) => PaletteCatalog.LightAllies,
        (AppTheme.Dark, AppAppearance.Allies) => PaletteCatalog.DarkAllies,
        (AppTheme.Light, AppAppearance.Soviets) => PaletteCatalog.LightSoviets,
        (AppTheme.Dark, AppAppearance.Soviets) => PaletteCatalog.DarkSoviets,
        _ => PaletteCatalog.DarkStandard
    };

    private const string PersonalizeRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";
    public static AppTheme CurrentThemePreference { get; private set; } = AppTheme.System;
    public static AppTheme ResolvedTheme { get; private set; } = AppTheme.Dark;
    public static AppAppearance CurrentAppearance { get; private set; } = AppAppearance.Standard;
    public static AppTheme CurrentPreference => CurrentThemePreference;

    public static AppTheme ParsePreference(string? value) =>
        TryParsePreference(value, out var theme) ? theme : AppTheme.System;

    public static bool TryParsePreference(string? value, out AppTheme theme) =>
        Enum.TryParse(value?.Trim(), ignoreCase: true, out theme) && Enum.IsDefined(theme);

    public static void Apply(AppTheme preference, AppAppearance appearance = AppAppearance.Standard)
    {
        // Preserve System as the user's preference while resolving it to a concrete
        // palette. This lets the radio state remain System instead of silently becoming
        // Light/Dark after consulting Windows.
        CurrentThemePreference = preference;
        CurrentAppearance = appearance;
        ResolvedTheme = preference == AppTheme.System ? ReadWindowsTheme() : preference;
        var palette = GetPalette(ResolvedTheme, appearance);
        var resources = Application.Current.Resources;

        SetBrush(resources, "WindowBrush", palette.Window);
        SetBrush(resources, "PanelBrush", palette.Panel);
        SetBrush(resources, "PanelAltBrush", palette.PanelAlt);
        SetBrush(resources, "HeaderBrush", palette.Header);
        SetBrush(resources, "BorderBrush", palette.Border);
        SetBrush(resources, "TextBrush", palette.Text);
        SetBrush(resources, "MutedBrush", palette.Muted);
        SetBrush(resources, "AccentBrush", palette.Accent);
        SetBrush(resources, "AccentHoverBrush", palette.AccentHover);
        SetBrush(resources, "PrimaryButtonTextBrush", palette.PrimaryButtonText);
        SetBrush(resources, "SelectionBrush", palette.Selection);
        SetBrush(resources, "SelectionTextBrush", palette.SelectionText);
        SetBrush(resources, "TooltipBrush", palette.Tooltip);
        SetBrush(resources, "TooltipTextBrush", palette.TooltipText);
        SetBrush(resources, "DisabledTextBrush", palette.DisabledText);
        SetBrush(resources, "FocusBrush", palette.Focus);

    }

    private static AppTheme ReadWindowsTheme()
    {
        if (!OperatingSystem.IsWindows())
        {
            return AppTheme.Light;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeRegistryPath);
            return key?.GetValue(AppsUseLightThemeValue) is int value && value == 0
                ? AppTheme.Dark
                : AppTheme.Light;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return AppTheme.Light;
        }
    }

    private static void SetBrush(ResourceDictionary resources, string key, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        resources[key] = brush;
    }

    private static ThemePalette MakePalette(
        string window, string panel, string panelAlt, string header, string border,
        string text, string muted, string accent, string accentHover,
        string primaryButtonText, string selection, string selectionText,
        string tooltip, string tooltipText, string disabledText, string focus) =>
        new(window, panel, panelAlt, header, border, text, muted, accent, accentHover,
            primaryButtonText, selection, selectionText, tooltip, tooltipText, disabledText, focus);

    private static class PaletteCatalog
    {
        public static ThemePalette DarkStandard { get; } = MakePalette(
            "#17191E", "#20242B", "#282D35", "#303640", "#3B424D", "#F2F4F7", "#B8C0CC",
            "#D49A45", "#E6AA55", "#17191E", "#6D5127", "#FFFFFF", "#282D35", "#F2F4F7", "#7F8997", "#E6AA55");

        public static ThemePalette LightStandard { get; } = MakePalette(
            "#F3F5F7", "#FFFFFF", "#F7F8FA", "#E8ECF1", "#BCC5D0", "#17212B", "#52616F",
            "#9B5D08", "#814C05", "#FFFFFF", "#CDE2F7", "#102A43", "#FFFFFF", "#17212B", "#6B7785", "#814C05");

        public static ThemePalette DarkGdi { get; } = MakePalette(
            "#171A1D", "#23282D", "#2D3338", "#343B41", "#56616A", "#F5F7F8", "#B9C3CA",
            "#D0A23A", "#E3B94D", "#171A1D", "#66501F", "#FFFFFF", "#2D3338", "#F5F7F8", "#828E96", "#E3B94D");

        public static ThemePalette DarkNod { get; } = MakePalette(
            "#151416", "#211D20", "#2C2327", "#35272D", "#5B3A43", "#F7F3F4", "#C0B4B8",
            "#C83B4B", "#E25561", "#FFFFFF", "#632B35", "#FFFFFF", "#2C2327", "#F7F3F4", "#86777C", "#E25561");

        public static ThemePalette DarkAllies { get; } = MakePalette(
            "#151B25", "#202B3A", "#2A384A", "#30445A", "#526A83", "#F3F7FB", "#B5C4D4",
            "#4D9BE6", "#70B4F2", "#102033", "#244F7E", "#FFFFFF", "#2A384A", "#F3F7FB", "#8094A9", "#70B4F2");

        public static ThemePalette DarkSoviets { get; } = MakePalette(
            "#1A1718", "#2A2022", "#38282B", "#432B30", "#67444A", "#F7F1EF", "#C5B5B0",
            "#C94B43", "#E06458", "#FFFFFF", "#713435", "#FFFFFF", "#38282B", "#F7F1EF", "#8D7975", "#E06458");

        public static ThemePalette LightGdi { get; } = MakePalette(
            "#F2F1ED", "#FAF9F6", "#F0F0EC", "#E4E5E2", "#697177", "#20272B", "#586168",
            "#855600", "#6D4600", "#FFFFFF", "#D8C58D", "#20272B", "#F5F4F0", "#20272B", "#68737B", "#6D4600");
        public static ThemePalette LightNod { get; } = MakePalette(
            "#F1F0F1", "#FAF9F9", "#F0ECED", "#E8E1E3", "#75686D", "#242124", "#62575C",
            "#A52E3B", "#84202B", "#FFFFFF", "#E2B8BD", "#242124", "#F6F3F4", "#242124", "#76686D", "#84202B");
        public static ThemePalette LightAllies { get; } = MakePalette(
            "#EFF2F5", "#FAFBFC", "#EEF3F7", "#E1E9F0", "#64788A", "#1D2A36", "#566775",
            "#226AA7", "#155184", "#FFFFFF", "#BFD4E7", "#1D2A36", "#F3F7FA", "#1D2A36", "#627585", "#155184");
        public static ThemePalette LightSoviets { get; } = MakePalette(
            "#F2F0EF", "#FBFAF9", "#F1ECEB", "#E9E0DF", "#78686A", "#2B2424", "#675857",
            "#A73531", "#852521", "#FFFFFF", "#E2C1BF", "#2B2424", "#F7F3F1", "#2B2424", "#786665", "#852521");
    }
}
