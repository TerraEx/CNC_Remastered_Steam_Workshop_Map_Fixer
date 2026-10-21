using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace CncRemasterWorkshopFixer;

public partial class SettingsWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private bool _controlsReady;

    public SettingsWindow(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        // Setting radio choices raises Checked during construction. Leave the
        // handlers gated until all saved values are loaded, or defaults get saved back.
        var settings = viewModel.Settings;
        (settings.Theme switch
        {
            AppTheme.Light => LightThemeChoice,
            AppTheme.Dark => DarkThemeChoice,
            _ => SystemThemeChoice
        }).IsChecked = true;
        (settings.Appearance switch
        {
            AppAppearance.Gdi => GdiAppearanceChoice,
            AppAppearance.Nod => NodAppearanceChoice,
            AppAppearance.Allies => AlliesAppearanceChoice,
            AppAppearance.Soviets => SovietsAppearanceChoice,
            _ => StandardAppearanceChoice
        }).IsChecked = true;
        (settings.DefaultInstallMethod == MapInstallMethod.DirectLocal
            ? DirectLocalChoice
            : NativeCacheChoice).IsChecked = true;
        _controlsReady = true;
        RefreshWorkshopDisplay();
    }

    private void ThemeChoice_Checked(object sender, RoutedEventArgs e)
    {
        if (_controlsReady && sender is RadioButton { Tag: string value } &&
            ThemeManager.TryParsePreference(value, out var theme))
        {
            _viewModel.UseTheme(theme);
        }
    }

    private void AppearanceChoice_Checked(object sender, RoutedEventArgs e)
    {
        if (_controlsReady && sender is RadioButton { Tag: string value } &&
            Enum.TryParse(value, ignoreCase: true, out AppAppearance appearance) && Enum.IsDefined(appearance))
        {
            _viewModel.UseAppearance(appearance);
        }
    }

    private void InstallMethodChoice_Checked(object sender, RoutedEventArgs e)
    {
        if (_controlsReady && sender is RadioButton { Tag: string value } &&
            Enum.TryParse(value, ignoreCase: true, out MapInstallMethod method) && Enum.IsDefined(method))
        {
            _viewModel.UseDefaultInstallMethod(method);
        }
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the C&C Remastered Workshop content folder",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await _viewModel.UseManualWorkshopRootAsync(dialog.FolderName);
            RefreshWorkshopDisplay();
        }
        catch (Exception exception) when (exception is ArgumentException or DirectoryNotFoundException or
                                           NotSupportedException or PathTooLongException)
        {
            AppMessageDialog.Show(this, "Choose Workshop Folder", exception.Message,
                AppMessageSeverity.Warning);
        }
    }

    private async void UseAutomaticWorkshopDiscovery_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.UseAutomaticWorkshopDiscoveryAsync();
        RefreshWorkshopDisplay();
    }

    private void About_Click(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = Owner ?? this }.ShowDialog();

    private void RefreshWorkshopDisplay()
    {
        WorkshopModeText.Text = $"Mode: {_viewModel.WorkshopModeDisplay}";
        ConfiguredFolderText.Text = _viewModel.ManualWorkshopRoot is null
            ? "Configured folder: None (automatic discovery is the default)"
            : $"Configured folder: {_viewModel.ManualWorkshopRoot}";
        ResolvedFolderText.Text = $"Resolved folder: {_viewModel.WorkshopRootsDisplay}";
        WorkshopWarningText.Text = _viewModel.WorkshopPreferenceWarning ?? string.Empty;
        WorkshopWarningText.Visibility = _viewModel.WorkshopPreferenceWarning is null
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
