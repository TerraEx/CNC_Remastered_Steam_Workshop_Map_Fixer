using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Animation;

namespace CncRemasterWorkshopFixer;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.RefreshAsync();
        Closed += (_, _) => _viewModel.Dispose();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.RefreshAsync();

    private void DismissToast_Click(object sender, RoutedEventArgs e) => _viewModel.DismissToast();

    private void ToastOverlay_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue && SystemParameters.ClientAreaAnimation)
        {
            ((Storyboard)FindResource("ToastFadeIn")).Begin(ToastOverlay);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.CanOpenSettings)
        {
            new SettingsWindow(_viewModel) { Owner = this }.ShowDialog();
        }
    }

    private async void InstallSelected_Click(object sender, RoutedEventArgs e)
    {
        var modified = _viewModel.Maps.Where(map => map.IsSelected &&
            map.Result.InstallStatus == MapInstallStatus.ModifiedLocally).ToList();
        if (modified.Count > 0 && new ModifiedLocalConfirmationWindow(modified.Select(map => map.Title))
            { Owner = this }.ShowDialog() != true)
        {
            return;
        }
        await _viewModel.InstallAsync(selectedOnly: true);
    }

    private async void InstallAll_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.InstallAsync(selectedOnly: false);

    private async void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.SelectedRemovableMaps;
        if (selected.Count == 0)
        {
            return;
        }

        await ConfirmRemovalAsync(selected);
    }

    private async Task ConfirmRemovalAsync(IReadOnlyList<MapRowViewModel> maps)
    {
        var dialog = new RemoveConfirmationWindow(
            maps, _viewModel.UgcCacheRoot, _viewModel.LocalMapsRoot,
            (unsubscribe, progress) => _viewModel.RemoveMapsAsync(maps, unsubscribe, progress))
        {
            Owner = this
        };
        // The confirmation window recomputes and displays exact validated targets; the
        // actual manager validates them again immediately before deletion.
        if (dialog.ShowDialog() == true && dialog.Result is { } result)
            await _viewModel.CompleteRemovalAsync(result);
        else if (dialog.OperationError is { } error)
            _viewModel.ReportRemovalError(error);
    }

    private void SelectInstalled_Click(object sender, RoutedEventArgs e) =>
        _viewModel.SelectInstalled();

    private void SelectAll_Click(object sender, RoutedEventArgs e) =>
        _viewModel.SelectAll();

    private void SelectMissingOrOutdated_Click(object sender, RoutedEventArgs e) =>
        _viewModel.SelectMissingOrOutdated();

    private void SelectSourceUnavailable_Click(object sender, RoutedEventArgs e) =>
        _viewModel.SelectSourceUnavailable();

    private void ClearSelection_Click(object sender, RoutedEventArgs e) =>
        _viewModel.ClearSelection();

    private void OpenWorkshopPage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.OpenSelectedWorkshopPage();
        }
        catch (Exception exception)
        {
            AppMessageDialog.Show(this, "Open Workshop Page",
                $"The Workshop page could not be opened:\n\n{exception.Message}",
                AppMessageSeverity.Error);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        var focusedRow = _viewModel.IsGalleryView
            ? FindAncestor<ListBoxItem>(focused)?.DataContext as MapRowViewModel
            : FindAncestor<DataGridRow>(focused)?.DataContext as MapRowViewModel;
        // Space on the row toggles batch membership. A focused checkbox already
        // handles Space itself, so intercepting it here would toggle twice.
        if (e.Key != Key.Space || FindAncestor<CheckBox>(focused) is not null ||
            focusedRow is null || !ReferenceEquals(focusedRow, _viewModel.SelectedMap))
        {
            return;
        }

        focusedRow.IsSelected = !focusedRow.IsSelected;
        e.Handled = true;
    }

    private void MapContextMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement target || target.ContextMenu is not { } menu ||
            target.DataContext is not MapRowViewModel map)
        {
            e.Handled = true;
            return;
        }

        // A context-menu click is a navigation/repair target change only; it never changes
        // the row's independent batch-selection checkbox.
        _viewModel.SelectedMap = map;
        menu.Tag = map;

        var fix = GetMenuItem(menu, "fix");
        var remove = GetMenuItem(menu, "remove");
        var workshop = GetMenuItem(menu, "workshop");
        var source = GetMenuItem(menu, "source");
        var enabled = GetMenuItem(menu, "enabled");
        var copy = GetMenuItem(menu, "copy");

        fix.Header = map.Result.InstallStatus is MapInstallStatus.Installed or
            MapInstallStatus.UpdateAvailable or MapInstallStatus.ModifiedLocally
            ? "Re-fix / Refresh Map" : "Fix / Enable Map";
        fix.IsEnabled = _viewModel.CanFixMap(map);
        remove.IsEnabled = _viewModel.CanRemoveMap(map);
        workshop.IsEnabled = _viewModel.CanOpenWorkshopPageFor(map);
        source.IsEnabled = _viewModel.GetWorkshopSourceFolder(map) is not null;
        enabled.IsEnabled = _viewModel.GetEnabledMapFolder(map) is not null;
        copy.IsEnabled = true;
    }

    private async void MapContextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item ||
            ItemsControl.ItemsControlFromItemContainer(item) is not ContextMenu menu ||
            menu.Tag is not MapRowViewModel map)
        {
            return;
        }

        try
        {
            switch (item.Tag as string)
            {
                case "fix":
                    if (map.Result.InstallStatus == MapInstallStatus.ModifiedLocally &&
                        new ModifiedLocalConfirmationWindow([map.Title]) { Owner = this }.ShowDialog() != true)
                    {
                        break;
                    }
                    await _viewModel.InstallMapAsync(map);
                    break;
                case "remove":
                    if (_viewModel.CanRemoveMap(map)) await ConfirmRemovalAsync([map]);
                    break;
                case "workshop":
                    _viewModel.OpenWorkshopPage(map);
                    break;
                case "source":
                    OpenFolder(_viewModel.GetWorkshopSourceFolder(map));
                    break;
                case "enabled":
                    OpenFolder(_viewModel.GetEnabledMapFolder(map));
                    break;
                case "copy":
                    _viewModel.CopyWorkshopId(map);
                    break;
            }
        }
        catch (Exception exception)
        {
            AppMessageDialog.Show(this, "Map Action", exception.Message, AppMessageSeverity.Error);
        }
    }

    private static void OpenFolder(string? path)
    {
        if (path is null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
        {
            UseShellExecute = true
        });
    }

    private static MenuItem GetMenuItem(ContextMenu menu, string tag) =>
        menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, tag));

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current switch
            {
                Visual or Visual3D => VisualTreeHelper.GetParent(current),
                FrameworkContentElement content => content.Parent,
                _ => LogicalTreeHelper.GetParent(current)
            };
        }

        return null;
    }

}
