using System.Windows;

namespace CncRemasterWorkshopFixer;

public partial class App : Application
{
    private SingleInstanceGuard? _instanceGuard;

    public static ApplicationSettingsStore SettingsStore { get; } = new(ApplicationPaths.SettingsPath);

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceGuard = new SingleInstanceGuard("Local\\CncRemasterWorkshopFixer");
        if (!_instanceGuard.IsOwner)
        {
            var secondInstanceSettings = SettingsStore.LoadReadOnly();
            ThemeManager.Apply(secondInstanceSettings.Theme, secondInstanceSettings.Appearance);
            AppMessageDialog.Show(null, "C&C Remastered Workshop Map Fixer",
                "C&C Remastered Workshop Map Fixer is already running.",
                AppMessageSeverity.Information);
            Shutdown();
            return;
        }

        // Dynamic brushes must be replaced before StartupUri constructs the first window,
        // avoiding a flash of the fallback dark palette for Light/System users.
        var settings = SettingsStore.Load();
        ThemeManager.Apply(settings.Theme, settings.Appearance);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceGuard?.Dispose();
        base.OnExit(e);
    }
}
