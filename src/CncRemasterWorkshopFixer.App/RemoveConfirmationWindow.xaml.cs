using System.IO;
using System.ComponentModel;
using System.Windows;

namespace CncRemasterWorkshopFixer;

public partial class RemoveConfirmationWindow : Window
{
    private readonly Func<bool, IProgress<string>, Task<MapRemovalOperationResult>> _remove;
    private bool _isRunning;
    public MapRemovalOperationResult? Result { get; private set; }
    public Exception? OperationError { get; private set; }

    public RemoveConfirmationWindow(
        IReadOnlyList<MapRowViewModel> maps,
        string ugcCacheRoot,
        string localMapsRoot,
        Func<bool, IProgress<string>, Task<MapRemovalOperationResult>> remove)
    {
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(remove);
        _remove = remove;
        InitializeComponent();
        // Derive the confirmation list from the same allow-listed rules used by deletion;
        // never display or later delete an arbitrary path supplied by a row.
        var items = maps.Select(map =>
        {
            var result = map.Result;
            var creatorSteamId = result.CreatorSteamId
                ?? throw new InvalidOperationException("A removable map must have a creator identity.");
            var files = new List<string>();
            if (result.InstallMethod == MapInstallMethod.NativeCache)
                files.Add(NativeUgcCacheManager.GetCachePgmPath(
                    ugcCacheRoot, creatorSteamId, result.WorkshopItemId));
            if (result.Game is not null)
            {
                files.AddRange(LocalMapFileRules.GetExpectedFiles(
                    localMapsRoot, result.Game.Value,
                    creatorSteamId, result.WorkshopItemId));
            }

            return new RemovalConfirmationItem(
                result.Title,
                $"{MapDisplayText.ForGame(result.Game)}  •  {MapDisplayText.ForMethod(result.InstallMethod)}  •  Workshop ID {result.WorkshopItemId}" +
                (result.Game is null ? "  •  Local payloads will be preserved because the game is unknown" : string.Empty),
                files.Select(Path.GetFileName).ToList()!);
        }).ToList();
        DataContext = new RemovalConfirmationViewModel(RemovalConfirmationText.For(maps), items,
            maps.Count == 1 ? "Also unsubscribe this map from Steam Workshop" : "Also unsubscribe these maps from Steam Workshop",
            maps.Any(map => MapRemovalOperationResult.IsUsableWorkshopId(map.Result.WorkshopItemId))
                ? Visibility.Visible : Visibility.Collapsed);
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        _isRunning = true;
        UnsubscribeOption.IsEnabled = CancelButton.IsEnabled = RemoveButton.IsEnabled = false;
        // Disable WPF's Escape shortcut too. Once removal starts, the modal must
        // stay open through Steam callbacks and restoration of the process directory.
        CancelButton.IsCancel = false;
        OperationStatus.Text = "Removing maps from the game…";
        try
        {
            Result = await _remove(UnsubscribeOption.IsChecked == true,
                new Progress<string>(message => { if (_isRunning) OperationStatus.Text = message; }));
        }
        catch (Exception ex) { OperationError = ex; }
        finally { _isRunning = false; }
        DialogResult = Result is not null;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRunning) DialogResult = false;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isRunning) e.Cancel = true;
    }
}

internal sealed record RemovalConfirmationItem(
    string Title,
    string Details,
    IReadOnlyList<string> FileNames);

internal sealed record RemovalConfirmationViewModel(
    string Message,
    IReadOnlyList<RemovalConfirmationItem> Items,
    string UnsubscribeLabel,
    Visibility UnsubscribeVisibility);

public static class RemovalConfirmationText
{
    public static string For(IReadOnlyList<MapRowViewModel> maps)
    {
        ArgumentNullException.ThrowIfNull(maps);
        if (maps.Count == 0)
        {
            throw new ArgumentException("At least one map is required.", nameof(maps));
        }

        var methods = maps.Select(map => map.Result.InstallMethod ?? MapInstallMethod.NativeCache).Distinct().ToList();
        var unavailable = maps.Count(map => map.Result.InstallStatus == MapInstallStatus.SourceUnavailable);
        if (methods.Count == 1 && methods[0] == MapInstallMethod.DirectLocal)
            return "Removes the exact Direct Local files owned by the fixer. Workshop source remains untouched by the fixer. Steam subscription stays unchanged unless you select Also unsubscribe." +
                   (unavailable > 0 ? " Source Unavailable maps are removed from remembered fixer ownership." : string.Empty);
        if (methods.Count == 1 && methods[0] == MapInstallMethod.NativeCache)
            return "Removes C&C's native cached package and exact generated local map files where present. The downloaded Workshop MAPDATA.PGM packages remain untouched by the fixer. Steam subscription stays unchanged unless you select Also unsubscribe." +
                   (unavailable > 0 ? " For Source Unavailable maps, only the remembered C&C-side copies are removed; local removal does not change Steam subscription state." : string.Empty);
        return "Each map is removed according to its tracked installation method. Exact Native cache/generated files or fixer-owned Direct Local files are removed; Workshop sources remain untouched by the fixer. Steam subscriptions stay unchanged unless you select Also unsubscribe.";
    }
}
