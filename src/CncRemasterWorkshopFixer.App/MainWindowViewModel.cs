using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace CncRemasterWorkshopFixer;

public enum ToastSeverity
{
    Success,
    Warning,
    Error
}

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    // One lifetime token covers background work initiated by this window. Closing the
    // window cancels scans/operations and disposes the sole metadata HttpClient.
    private readonly HttpClient _httpClient;
    private readonly WorkshopMapScanner _scanner;
    private readonly WorkshopMapInventory _inventory;
    private readonly WorkshopMapInstallationCoordinator _installer;
    private readonly ApplicationSettingsStore _settingsStore;
    private readonly Func<IReadOnlyList<string>> _discoverWorkshopRoots;
    private readonly ISteamWorkshopSubscriptionService _subscriptions;
    // Confirmed account results only; never persisted or reconciled during scanning.
    private readonly HashSet<ulong> _pendingSteamCleanupIds = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PreviewThumbnailService _previewService;
    private readonly Func<TimeSpan, CancellationToken, Task> _toastDelay;
    private CancellationTokenSource? _toastExpiry;
    private string _toastTitle = string.Empty;
    private string _toastMessage = string.Empty;
    private ToastSeverity _toastSeverity;
    private bool _isToastVisible;
    private bool _isBusy;
    private bool _isGalleryView;
    private string _statusText = "Ready.";
    private string _workshopRootsDisplay = "Discovering…";
    private string _workshopModeDisplay = "Automatic discovery";
    private string? _workshopPreferenceWarning;
    private string? _manualWorkshopRoot;
    private MapRowViewModel? _selectedMap;
    private string _searchText = string.Empty;
    private MapGame? _gameFilter;
    private MapType? _typeFilter;

    public MainWindowViewModel(
        ApplicationSettingsStore? settingsStore = null,
        Func<IReadOnlyList<string>>? discoverWorkshopRoots = null,
        string? knownEnabledRegistryPath = null,
        PreviewThumbnailService? previewService = null,
        Func<TimeSpan, CancellationToken, Task>? toastDelay = null,
        ISteamWorkshopSubscriptionService? subscriptions = null,
        HttpMessageHandler? metadataHandler = null)
    {
        _settingsStore = settingsStore ?? App.SettingsStore;
        _discoverWorkshopRoots = discoverWorkshopRoots ?? SteamWorkshopDiscovery.DiscoverWorkshopRoots;
        _subscriptions = subscriptions ?? new SteamWorkshopSubscriptionService();
        _httpClient = metadataHandler is null ? new HttpClient() : new HttpClient(metadataHandler);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(ApplicationInfo.UserAgent);
        _scanner = new WorkshopMapScanner(new SteamWorkshopMetadataClient(_httpClient));
        var registry = new KnownEnabledMapRegistry(
            knownEnabledRegistryPath ?? KnownEnabledMapRegistry.GetDefaultPath());
        _inventory = new WorkshopMapInventory(_scanner, registry);
        _installer = new WorkshopMapInstallationCoordinator(registry);
        _manualWorkshopRoot = _settingsStore.Load().ManualWorkshopRoot;
        _previewService = previewService ?? new PreviewThumbnailService();
        _toastDelay = toastDelay ?? Task.Delay;
        UgcCacheRoot = NativeUgcCacheManager.GetDefaultCacheRoot();
        LocalMapsRoot = NativeUgcCacheManager.GetDefaultLocalMapsRoot();
        VisibleMaps = CollectionViewSource.GetDefaultView(Maps);
        VisibleMaps.Filter = item => MatchesFilters((MapRowViewModel)item);
    }

    public ObservableCollection<MapRowViewModel> Maps { get; } = [];
    public string ToastTitle { get => _toastTitle; private set => SetField(ref _toastTitle, value); }
    public string ToastMessage { get => _toastMessage; private set => SetField(ref _toastMessage, value); }
    public ToastSeverity ToastSeverity { get => _toastSeverity; private set => SetField(ref _toastSeverity, value); }
    public bool IsToastVisible { get => _isToastVisible; private set => SetField(ref _isToastVisible, value); }

    public void ShowToast(string title, string message, ToastSeverity severity)
    {
        if (_lifetime.IsCancellationRequested) return;
        _toastExpiry?.Cancel();
        _toastExpiry?.Dispose();
        var expiry = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _toastExpiry = expiry;
        ToastTitle = title;
        ToastMessage = message;
        ToastSeverity = severity;
        IsToastVisible = true;
        _ = ExpireToastAsync(expiry);
    }

    public void DismissToast()
    {
        _toastExpiry?.Cancel();
        _toastExpiry?.Dispose();
        _toastExpiry = null;
        IsToastVisible = false;
    }

    private async Task ExpireToastAsync(CancellationTokenSource expiry)
    {
        var token = expiry.Token;
        try
        {
            await _toastDelay(TimeSpan.FromSeconds(5), token);
            // Even a late completion from the old delay must not dismiss a replacement.
            if (!token.IsCancellationRequested && ReferenceEquals(_toastExpiry, expiry)) DismissToast();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }
    public ICollectionView VisibleMaps { get; }
    public string SearchText
    {
        get => _searchText;
        set { if (SetField(ref _searchText, value)) RefreshFilters(); }
    }
    public MapGame? GameFilter
    {
        get => _gameFilter;
        set
        {
            if (!SetField(ref _gameFilter, value)) return;
            OnPropertyChanged(nameof(IsAllGames));
            OnPropertyChanged(nameof(IsTiberianDawn));
            OnPropertyChanged(nameof(IsRedAlert));
            RefreshFilters();
        }
    }
    public MapType? TypeFilter
    {
        get => _typeFilter;
        set
        {
            if (!SetField(ref _typeFilter, value)) return;
            OnPropertyChanged(nameof(IsAllTypes));
            OnPropertyChanged(nameof(IsSinglePlayer));
            OnPropertyChanged(nameof(IsMultiplayer));
            RefreshFilters();
        }
    }
    public bool IsAllGames { get => GameFilter is null; set { if (value) GameFilter = null; } }
    public bool IsTiberianDawn { get => GameFilter == MapGame.TiberianDawn; set { if (value) GameFilter = MapGame.TiberianDawn; } }
    public bool IsRedAlert { get => GameFilter == MapGame.RedAlert; set { if (value) GameFilter = MapGame.RedAlert; } }
    public bool IsAllTypes { get => TypeFilter is null; set { if (value) TypeFilter = null; } }
    public bool IsSinglePlayer { get => TypeFilter == MapType.SinglePlayer; set { if (value) TypeFilter = MapType.SinglePlayer; } }
    public bool IsMultiplayer { get => TypeFilter == MapType.Multiplayer; set { if (value) TypeFilter = MapType.Multiplayer; } }

    private bool MatchesFilters(MapRowViewModel map) =>
        (string.IsNullOrEmpty(SearchText) || map.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
         map.Result.Metadata?.Author?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) == true) &&
        (GameFilter is null || map.Result.Game == GameFilter) &&
        (TypeFilter is null || map.Result.Metadata?.Type == TypeFilter);

    private void RefreshFilters()
    {
        // Batch checkboxes are independent of WPF row highlighting. Clear excluded
        // rows before refreshing the shared view so invisible maps cannot stay selected.
        foreach (var map in Maps.Where(map => !MatchesFilters(map)))
        {
            map.IsSelected = false;
        }
        if (SelectedMap is not null && !MatchesFilters(SelectedMap)) SelectedMap = null;
        VisibleMaps.Refresh();
        NotifyActionState();
        NotifySelectionState();
        OnPropertyChanged(nameof(EmptyMessageVisibility));
        OnPropertyChanged(nameof(EmptyMessageText));
    }
    public ObservableCollection<string> Activity { get; } = [];
    public string UgcCacheRoot { get; }
    public string LocalMapsRoot { get; }
    public ApplicationSettings Settings => _settingsStore.Load();
    public MapInstallMethod EnableInstallMethod => Settings.DefaultInstallMethod;
    public string? ManualWorkshopRoot => _manualWorkshopRoot;
    public string WorkshopModeDisplay
    {
        get => _workshopModeDisplay;
        private set => SetField(ref _workshopModeDisplay, value);
    }
    public string? WorkshopPreferenceWarning
    {
        get => _workshopPreferenceWarning;
        private set => SetField(ref _workshopPreferenceWarning, value);
    }

    public bool IsGalleryView
    {
        get => _isGalleryView;
        set
        {
            if (!SetField(ref _isGalleryView, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ListVisibility));
            OnPropertyChanged(nameof(GalleryVisibility));
            OnPropertyChanged(nameof(IsListView));
            if (value)
            {
                RequestGalleryPreviews(forceSourceCheck: false);
            }
        }
    }

    public bool IsListView
    {
        get => !IsGalleryView;
        set { if (value) IsGalleryView = false; }
    }

    public Visibility ListVisibility => IsGalleryView ? Visibility.Collapsed : Visibility.Visible;
    public Visibility GalleryVisibility => IsGalleryView ? Visibility.Visible : Visibility.Collapsed;

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string WorkshopRootsDisplay
    {
        get => _workshopRootsDisplay;
        private set => SetField(ref _workshopRootsDisplay, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                // Busy is the shared gate for refresh and every mutating/selecting action;
                // notify all dependent commands together to avoid interleaved operations.
                NotifyActionState();
            }
        }
    }

    public bool CanRefresh => !IsBusy;
    public bool CanOpenSettings => !IsBusy;
    public bool CanUseAutomaticWorkshopDiscovery => !IsBusy && _manualWorkshopRoot is not null;
    public int SelectedCount => Maps.Count(map => map.IsSelected);
    public int SelectedInstallCount => Maps.Count(map => map.IsSelected && map.IsInstallEligible);
    public int SelectedRemoveCount => Maps.Count(map => map.IsSelected && map.CanRemoveFromGame);
    public bool CanInstallSelected => !IsBusy && SelectedInstallCount > 0;
    public bool CanInstallAll => !IsBusy && Maps.Any(map => map.IsInstallEligible &&
        map.Result.InstallStatus != MapInstallStatus.ModifiedLocally);
    public bool CanRemoveSelected => !IsBusy && SelectedRemoveCount > 0;
    public bool CanOpenWorkshopPage => !IsBusy && SelectedMap is not null;
    public bool CanSelectAll => !IsBusy && Maps.Any(map => MatchesFilters(map) && map.IsBatchSelectable);
    public bool CanSelectInstalled => !IsBusy &&
        Maps.Any(map => MatchesFilters(map) && MapBatchSelectionRules.IsEnabledSelectorMatch(map.Result));
    public bool CanSelectMissingOrOutdated => !IsBusy &&
        Maps.Any(map => MatchesFilters(map) && MapBatchSelectionRules.IsDisabledOrOutdatedSelectorMatch(map.Result));
    public bool CanSelectSourceUnavailable => !IsBusy &&
        Maps.Any(map => MatchesFilters(map) && MapBatchSelectionRules.IsSourceUnavailableSelectorMatch(map.Result));
    public bool CanClearSelection => !IsBusy && SelectedCount > 0;
    public string InstallSelectedText => SelectedInstallCount == 0
        ? "Enable Selected"
        : $"Enable Selected ({SelectedInstallCount})";
    public string RemoveSelectedText => SelectedRemoveCount == 0
        ? "Remove from Game"
        : $"Remove from Game ({SelectedRemoveCount})";
    public string SelectionSummary
    {
        get
        {
            if (SelectedCount == 0)
            {
                return "No maps selected for batch actions.";
            }

            var enabled = Maps.Count(map => map.IsSelected &&
                MapBatchSelectionRules.IsEnabledSelectorMatch(map.Result));
            var needsEnable = SelectedInstallCount;
            var sourceUnavailable = Maps.Count(map => map.IsSelected &&
                MapBatchSelectionRules.IsSourceUnavailableSelectorMatch(map.Result));
            var groups = new List<string>();
            if (enabled > 0)
            {
                groups.Add($"{enabled} enabled");
            }
            if (needsEnable > 0)
            {
                groups.Add($"{needsEnable} {(needsEnable == 1 ? "needs" : "need")} enablement or repair");
            }
            if (sourceUnavailable > 0)
            {
                groups.Add($"{sourceUnavailable} source unavailable");
            }

            return $"{SelectedCount} map{(SelectedCount == 1 ? string.Empty : "s")} selected • " +
                string.Join(" • ", groups);
        }
    }
    public Visibility EmptyMessageVisibility => !IsBusy && VisibleMaps.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    public string EmptyMessageText => Maps.Count == 0
        ? "No Workshop maps found. Choose a Workshop folder or try Refresh."
        : "No maps match the current filters.";

    public MapRowViewModel? SelectedMap
    {
        get => _selectedMap;
        set
        {
            if (SetField(ref _selectedMap, value))
            {
                // DataGrid highlighting chooses a detail/navigation target only. Batch
                // install/remove membership lives in MapRowViewModel.IsSelected.
                OnPropertyChanged(nameof(CanRemoveSelected));
                OnPropertyChanged(nameof(CanOpenWorkshopPage));
            }
        }
    }

    public IReadOnlyList<MapRowViewModel> SelectedRemovableMaps =>
        Maps.Where(map => map.IsSelected && map.CanRemoveFromGame).ToList();

    public bool CanFixMap(MapRowViewModel map) => !IsBusy &&
        map.Result.CreatorSteamId is not null && File.Exists(map.Result.PgmPath) &&
        map.Result.InstallStatus is MapInstallStatus.NotInstalled or MapInstallStatus.Installed or
            MapInstallStatus.UpdateAvailable or MapInstallStatus.ModifiedLocally;
    public bool CanRemoveMap(MapRowViewModel map) => !IsBusy && map.CanRemoveFromGame;
    public bool CanOpenWorkshopPageFor(MapRowViewModel map) => !IsBusy;

    public string? GetWorkshopSourceFolder(MapRowViewModel map)
    {
        var folder = Path.GetDirectoryName(map.Result.PgmPath);
        return folder is not null && Directory.Exists(folder) ? folder : null;
    }

    public string? GetEnabledMapFolder(MapRowViewModel map)
    {
        var result = map.Result;
        if (result.CreatorSteamId is null || result.InstallMethod is null)
        {
            return null;
        }

        if (result.InstallMethod == MapInstallMethod.NativeCache)
        {
            var pgm = NativeUgcCacheManager.GetCachePgmPath(
                UgcCacheRoot, result.CreatorSteamId.Value, result.WorkshopItemId);
            if (File.Exists(pgm))
            {
                return Path.GetDirectoryName(pgm);
            }
        }

        if (result.Game is null)
        {
            return null;
        }

        var folder = LocalMapFileRules.GetDestination(LocalMapsRoot, result.Game.Value);
        return Directory.Exists(folder) && LocalMapFileRules.GetExpectedFiles(
            LocalMapsRoot, result.Game.Value, result.CreatorSteamId.Value, result.WorkshopItemId).Any(File.Exists)
            ? folder : null;
    }

    public async Task InstallMapAsync(MapRowViewModel map)
    {
        if (!CanFixMap(map))
        {
            return;
        }

        IsBusy = true;
        try
        {
            // The batch coordinator intentionally skips current installations. A per-map
            // refresh explicitly requests a fresh copy while retaining the same installer.
            var requestedMap = map.Result.InstallStatus == MapInstallStatus.Installed
                ? map.Result with { InstallStatus = MapInstallStatus.UpdateAvailable }
                : map.Result;
            var attempts = await Task.Run(() => _installer.Install(
                [requestedMap], Settings.DefaultInstallMethod, UgcCacheRoot, LocalMapsRoot), _lifetime.Token);
            var attempt = attempts.Single();
            var action = map.Result.InstallStatus switch
            {
                MapInstallStatus.Installed => "Refreshed",
                MapInstallStatus.UpdateAvailable => "Updated",
                MapInstallStatus.ModifiedLocally => "Repaired",
                _ => "Enabled"
            };
            AddActivity(attempt.Succeeded
                ? $"{action} {attempt.Map.Title}."
                : $"Failed to enable {attempt.Map.Title}: {attempt.Error}");
            StatusText = attempt.Succeeded ? $"{action} {map.Title}. Refreshing…" : $"Enablement failed for {map.Title}.";
            ShowToast(attempt.Succeeded ? $"Map {action.ToLowerInvariant()}" : "Enablement failed",
                attempt.Succeeded ? $"{map.Title} was {action.ToLowerInvariant()} successfully."
                    : "The map could not be enabled. See Activity for details.",
                attempt.Succeeded ? ToastSeverity.Success : ToastSeverity.Error);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            StatusText = $"Enablement failed: {exception.Message}";
            AddActivity(StatusText);
            ShowToast("Enablement failed", "The map could not be enabled. See Activity for details.", ToastSeverity.Error);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    public void OpenWorkshopPage(MapRowViewModel map)
    {
        Process.Start(new ProcessStartInfo(WorkshopPage.GetUri(map.Result.WorkshopItemId).AbsoluteUri)
        {
            UseShellExecute = true
        });
    }

    public void CopyWorkshopId(MapRowViewModel map) =>
        Clipboard.SetText(map.Result.WorkshopItemId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public void UseTheme(AppTheme theme)
    {
        if (!Enum.IsDefined(theme))
        {
            throw new ArgumentOutOfRangeException(nameof(theme));
        }

        ThemeManager.Apply(theme, Settings.Appearance);
        _settingsStore.SaveTheme(theme);
        OnPropertyChanged(nameof(Settings));
    }

    public void UseAppearance(AppAppearance appearance)
    {
        if (!Enum.IsDefined(appearance))
        {
            throw new ArgumentOutOfRangeException(nameof(appearance));
        }

        ThemeManager.Apply(Settings.Theme, appearance);
        _settingsStore.SaveAppearance(appearance);
        OnPropertyChanged(nameof(Settings));
    }

    public void UseDefaultInstallMethod(MapInstallMethod method)
    {
        if (!Enum.IsDefined(method))
        {
            throw new ArgumentOutOfRangeException(nameof(method));
        }

        _settingsStore.SaveDefaultInstallMethod(method);
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(EnableInstallMethod));
    }

    public async Task UseManualWorkshopRootAsync(string root)
    {
        if (IsBusy)
        {
            return;
        }

        _manualWorkshopRoot = WorkshopRootPreference.ValidateSelection(root);
        _settingsStore.SaveManualWorkshopRoot(_manualWorkshopRoot);
        OnPropertyChanged(nameof(ManualWorkshopRoot));
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(CanUseAutomaticWorkshopDiscovery));
        await RefreshAsync();
    }

    public async Task UseAutomaticWorkshopDiscoveryAsync()
    {
        if (IsBusy || _manualWorkshopRoot is null)
        {
            return;
        }

        _manualWorkshopRoot = null;
        _settingsStore.SaveManualWorkshopRoot(null);
        OnPropertyChanged(nameof(ManualWorkshopRoot));
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(CanUseAutomaticWorkshopDiscovery));
        AddActivity("Automatic Steam Workshop folder discovery restored.");
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "Scanning Workshop maps…";
        OnPropertyChanged(nameof(EmptyMessageVisibility));
        try
        {
            var rootResolution = await Task.Run(
                () => WorkshopRootPreference.Resolve(
                    _manualWorkshopRoot, _discoverWorkshopRoots),
                _lifetime.Token);
            var roots = rootResolution.Roots;

            WorkshopModeDisplay = _manualWorkshopRoot is null
                ? "Automatic discovery"
                : rootResolution.Warning is null
                    ? "Manual override"
                    : "Manual override unavailable; automatic discovery is active for this session";
            WorkshopPreferenceWarning = rootResolution.Warning;

            if (rootResolution.Warning is not null)
            {
                AddActivity(rootResolution.Warning);
            }

            WorkshopRootsDisplay = roots.Count == 0
                ? "No Workshop folder discovered"
                : string.Join("  •  ", roots);

            StatusText = roots.Count == 0 ? "Checking remembered maps…" : "Resolving Steam metadata…";
            var inventory = await Task.Run(
                async () => await _inventory.RefreshAsync(
                    roots, UgcCacheRoot, LocalMapsRoot, _lifetime.Token),
                _lifetime.Token);
            // Interpret confirmed unsubscribes before row eligibility and counts are built.
            var results = inventory.Maps.Select(ApplyPendingSteamCleanup).ToList();

            // Seed a predictable title order. Subsequent DataGrid sorting is view-only and
            // its native SortDirection drives the visible header chevron.
            ReplaceMaps(results.OrderBy(map => map.Title, StringComparer.CurrentCultureIgnoreCase));
            if (IsGalleryView)
            {
                RequestGalleryPreviews(forceSourceCheck: true);
            }
            var eligible = results.Count(map => MapInstallationRules.IsEligible(map.InstallStatus));
            var metadataUnavailable = results.Count(map => map.InstallStatus == MapInstallStatus.MetadataUnavailable);
            var sourceUnavailable = results.Count(map => map.InstallStatus == MapInstallStatus.SourceUnavailable);
            StatusText = $"{results.Count} Workshop maps found. {eligible} maps need enablement or repair." +
                (sourceUnavailable == 0 ? string.Empty : $" {sourceUnavailable} previously enabled map(s) have a Source Unavailable state.") +
                (metadataUnavailable == 0
                    ? string.Empty
                    : $" Steam metadata is unavailable for {metadataUnavailable}; Refresh to retry.");
            if (roots.Count == 0 && results.Count == 0)
            {
                StatusText = "No C&C Remastered Workshop folder was found and no remembered game-side maps remain. Choose a Workshop folder manually to continue.";
            }
            if (!string.IsNullOrWhiteSpace(inventory.RegistryWarning))
            {
                AddActivity(inventory.RegistryWarning);
            }
            AddActivity(StatusText);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText = $"Refresh failed: {exception.Message}";
            AddActivity(StatusText);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(EmptyMessageVisibility));
        }
    }

    public async Task InstallAsync(bool selectedOnly)
    {
        if (IsBusy)
        {
            return;
        }

        // Enable All excludes local edits. Repairing them requires an explicit
        // selection and the window's overwrite confirmation.
        var maps = Maps
            .Where(row => row.IsInstallEligible &&
                          (selectedOnly ? row.IsSelected : row.Result.InstallStatus != MapInstallStatus.ModifiedLocally))
            .Select(row => row.Result)
            .ToList();
        if (maps.Count == 0)
        {
            return;
        }

        IsBusy = true;
        StatusText = $"Enabling {maps.Count} map(s)…";
        try
        {
            var attempts = await Task.Run(() => _installer.Install(
                maps, Settings.DefaultInstallMethod, UgcCacheRoot, LocalMapsRoot), _lifetime.Token);
            foreach (var attempt in attempts)
            {
                var action = attempt.Map.InstallStatus switch
                {
                    MapInstallStatus.UpdateAvailable => "Updated",
                    MapInstallStatus.ModifiedLocally => "Repaired",
                    _ => "Enabled"
                };
                AddActivity(attempt.Succeeded
                    ? $"{action} {attempt.Map.Title} using " +
                      (attempt.Method == MapInstallMethod.NativeCache ? "Native C&C Cache." : "Direct Local Install.")
                    : $"Failed to enable {attempt.Map.Title}: {attempt.Error}");
            }

            var succeeded = attempts.Count(attempt => attempt.Succeeded);
            var failed = attempts.Count - succeeded;
            StatusText = failed == 0
                ? $"Enabled {succeeded} map(s). Refreshing…"
                : $"Enabled {succeeded} map(s); {failed} failed. Refreshing…";
            ShowToast(failed == 0 ? "Maps enabled" : succeeded == 0 ? "Enablement failed" : "Completed with warnings",
                failed == 0 ? $"{succeeded} map(s) were enabled successfully."
                    : $"{succeeded} map(s) were enabled; {failed} failed. See Activity for details.",
                failed == 0 ? ToastSeverity.Success : succeeded == 0 ? ToastSeverity.Error : ToastSeverity.Warning);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            StatusText = $"Enablement failed: {exception.Message}";
            AddActivity(StatusText);
            ShowToast("Enablement failed", "Maps could not be enabled. See Activity for details.", ToastSeverity.Error);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        // Refresh re-reads the native cache bytes rather than assuming the copy succeeded.
        await RefreshAsync();
    }

    public async Task<MapRemovalOperationResult> RemoveMapsAsync(
        IReadOnlyList<MapRowViewModel> rows, bool unsubscribe, IProgress<string> progress)
    {
        if (IsBusy || rows.Count == 0 || rows.Any(row => !row.CanRemoveFromGame))
            throw new InvalidOperationException("The confirmed maps are no longer removable.");
        // Snapshot exactly the rows whose targets were displayed in the confirmation.
        var maps = rows.Select(row => row.Result).ToList();
        IsBusy = true;
        try
        {
            var operation = new MapRemovalOperation(map => Task.Run(() =>
                _inventory.RemoveFromGame(map, UgcCacheRoot, LocalMapsRoot), _lifetime.Token),
                _subscriptions);
            return await operation.RunAsync(maps, unsubscribe, progress);
        }
        finally { IsBusy = false; }
    }

    public async Task CompleteRemovalAsync(MapRemovalOperationResult result)
    {
        // A surviving Steam PGM isn't evidence that unsubscribe failed. Remember
        // confirmed results for this session so refresh can't offer to enable it again.
        if (result.UnsubscribeRequested)
        {
            foreach (var item in result.UnsubscribeResults.Where(item => item.Succeeded &&
                         MapRemovalOperationResult.IsUsableWorkshopId(item.WorkshopItemId)))
                _pendingSteamCleanupIds.Add(item.WorkshopItemId);
        }
        // Make current rows read-only even if the following refresh fails.
        foreach (var row in Maps.Where(row => _pendingSteamCleanupIds.Contains(row.Result.WorkshopItemId)))
            row.UpdateResult(ApplyPendingSteamCleanup(row.Result));

        foreach (var attempt in result.LocalResults)
        {
            if (attempt.Removal is not { } removal)
            {
                AddActivity($"Failed to remove {attempt.Map.Title} (Workshop ID {attempt.Map.WorkshopItemId}) from C&C: {attempt.Error}");
                continue;
            }
            foreach (var file in removal.RemovedFiles) AddActivity($"Removed {Path.GetFileName(file)}.");
            foreach (var file in removal.MissingFiles) AddActivity($"Already missing: {Path.GetFileName(file)}.");
            AddActivity($"Removed {attempt.Map.Title} (Workshop ID {attempt.Map.WorkshopItemId}) from the game: {removal.RemovedFiles.Count} file(s) deleted" +
                (removal.MissingFiles.Count == 0 ? "." : $"; {removal.MissingFiles.Count} exact target(s) were already missing.") +
                (removal.LocalPayloadCleanupAttempted ? string.Empty : " Game type is unknown, so local payload files were left untouched."));
        }
        foreach (var item in result.UnsubscribeResults)
        {
            var title = result.LocalResults.First(attempt => attempt.Map.WorkshopItemId == item.WorkshopItemId).Map.Title;
            AddActivity(item.Succeeded
                ? $"Unsubscribed {title} (Workshop ID {item.WorkshopItemId}) from Steam Workshop."
                : $"Workshop unsubscribe {item.Status.ToString().ToLowerInvariant()} for {title} (Workshop ID {item.WorkshopItemId}): {item.Error} Local removal remains successful.");
        }
        if (result.UnsubscribeRequested)
        {
            foreach (var attempt in result.LocalResults.Where(item => item.Succeeded &&
                         !MapRemovalOperationResult.IsUsableWorkshopId(item.Map.WorkshopItemId)))
                AddActivity($"Skipped Workshop unsubscribe for {attempt.Map.Title}: no usable Workshop item ID.");
        }
        var notification = MapRemovalNotification.For(result);
        AddActivity(notification.Message);
        // Steam has finished and restored the process directory before inventory work starts.
        await RefreshAsync();
        StatusText = notification.Message;
        ShowToast(notification.Title, notification.Message, notification.Severity);
    }

    public void ReportRemovalError(Exception exception)
    {
        StatusText = $"Remove from Game failed: {exception.Message}";
        AddActivity(StatusText);
        ShowToast("Removal failed", "The operation could not complete. See Activity for details.", ToastSeverity.Error);
    }

    public void OpenSelectedWorkshopPage()
    {
        if (SelectedMap is null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo(WorkshopPage.GetUri(SelectedMap.Result.WorkshopItemId).AbsoluteUri)
        {
            UseShellExecute = true
        });
    }

    public void SelectAll()
    {
        if (!IsBusy)
        {
            SetBatchSelection(map => map.IsBatchSelectable);
        }
    }

    public void SelectInstalled()
    {
        if (!IsBusy)
        {
            SetBatchSelection(map => MapBatchSelectionRules.IsEnabledSelectorMatch(map.Result));
        }
    }

    public void SelectMissingOrOutdated()
    {
        if (!IsBusy)
        {
            SetBatchSelection(map => MapBatchSelectionRules.IsDisabledOrOutdatedSelectorMatch(map.Result));
        }
    }

    public void SelectSourceUnavailable()
    {
        if (!IsBusy)
        {
            SetBatchSelection(map => MapBatchSelectionRules.IsSourceUnavailableSelectorMatch(map.Result));
        }
    }

    public void ClearSelection()
    {
        if (!IsBusy)
        {
            SetBatchSelection(_ => false);
        }
    }

    private void SetBatchSelection(Func<MapRowViewModel, bool> predicate)
    {
        foreach (var map in Maps)
        {
            map.IsSelected = MatchesFilters(map) && predicate(map);
        }
    }

    private void ReplaceMaps(IEnumerable<WorkshopMapScanResult> results)
    {
        // Detach row handlers before replacement; otherwise discarded rows keep this view
        // model alive and can trigger stale selection notifications.
        foreach (var map in Maps)
        {
            map.PropertyChanged -= Map_PropertyChanged;
        }

        // Reuse rows by item ID to preserve checked selection and loaded previews
        // across refresh; UpdateResult clears anything that is no longer eligible.
        var existingById = Maps.ToDictionary(map => map.Result.WorkshopItemId);
        var selectedId = SelectedMap?.Result.WorkshopItemId;
        Maps.Clear();
        foreach (var result in results)
        {
            var map = existingById.TryGetValue(result.WorkshopItemId, out var existing)
                ? existing
                : new MapRowViewModel(result);
            map.UpdateResult(result);
            map.PropertyChanged += Map_PropertyChanged;
            Maps.Add(map);
        }
        SelectedMap = selectedId is ulong id
            ? Maps.FirstOrDefault(map => map.Result.WorkshopItemId == id)
            : null;

        RefreshFilters();
    }

    private WorkshopMapScanResult ApplyPendingSteamCleanup(WorkshopMapScanResult map) =>
        _pendingSteamCleanupIds.Contains(map.WorkshopItemId)
            ? map with { InstallStatus = MapInstallStatus.UnsubscribedPendingSteamCleanup }
            : map;

    public async Task LoadGalleryPreviewsAsync(bool forceSourceCheck = false)
    {
        var rows = Maps.ToList();
        var rowsToLoad = rows.Where(row => forceSourceCheck || row.PreviewImage is null).ToList();
        foreach (var row in rowsToLoad.Where(row => row.PreviewImage is null))
        {
            row.SetPreviewLoading();
        }

        await Task.WhenAll(rowsToLoad.Select(async row =>
        {
            var result = row.Result;
            var preview = await _previewService.LoadAsync(
                result, UgcCacheRoot, LocalMapsRoot, _lifetime.Token);
            // Refresh may replace a row's result while decoding is still running.
            // Don't attach the old source's thumbnail to the newer result.
            if (!_lifetime.IsCancellationRequested && ReferenceEquals(row.Result, result))
            {
                row.SetPreview(preview?.Image, preview?.CacheIdentity);
            }
        }));

        _previewService.RemoveEntriesExcept(Maps
            .Select(row => row.PreviewCacheIdentity)
            .Where(identity => identity is not null)
            .Select(identity => identity!)
            .ToHashSet(StringComparer.Ordinal));
    }

    private async void RequestGalleryPreviews(bool forceSourceCheck)
    {
        try
        {
            await LoadGalleryPreviewsAsync(forceSourceCheck);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void Map_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MapRowViewModel.IsSelected))
        {
            NotifySelectionState();
        }
    }

    private void AddActivity(string message)
    {
        Activity.Add($"{DateTime.Now:t}  {message}");
        while (Activity.Count > 100)
        {
            Activity.RemoveAt(0);
        }
    }

    private void NotifyActionState()
    {
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanOpenSettings));
        OnPropertyChanged(nameof(CanUseAutomaticWorkshopDiscovery));
        OnPropertyChanged(nameof(CanInstallSelected));
        OnPropertyChanged(nameof(CanInstallAll));
        OnPropertyChanged(nameof(CanRemoveSelected));
        OnPropertyChanged(nameof(CanOpenWorkshopPage));
        OnPropertyChanged(nameof(CanSelectAll));
        OnPropertyChanged(nameof(CanSelectInstalled));
        OnPropertyChanged(nameof(CanSelectMissingOrOutdated));
        OnPropertyChanged(nameof(CanSelectSourceUnavailable));
        OnPropertyChanged(nameof(CanClearSelection));
    }

    private void NotifySelectionState()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedInstallCount));
        OnPropertyChanged(nameof(SelectedRemoveCount));
        OnPropertyChanged(nameof(SelectedRemovableMaps));
        OnPropertyChanged(nameof(CanInstallSelected));
        OnPropertyChanged(nameof(CanRemoveSelected));
        OnPropertyChanged(nameof(CanClearSelection));
        OnPropertyChanged(nameof(InstallSelectedText));
        OnPropertyChanged(nameof(RemoveSelectedText));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    public void Dispose()
    {
        DismissToast();
        _lifetime.Cancel();
        _lifetime.Dispose();
        _httpClient.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public sealed class MapRowViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    private WorkshopMapScanResult _result;
    private BitmapSource? _previewImage;
    private string _previewMessage = "Preview unavailable";
    private string? _previewCacheIdentity;

    public MapRowViewModel(WorkshopMapScanResult result)
    {
        _result = result;
        IsInstallEligible = MapInstallationRules.IsEligible(result.InstallStatus);
        IsBatchSelectable = MapBatchSelectionRules.IsSelectable(result);
    }

    public WorkshopMapScanResult Result => _result;
    public string Title => Result.Title;
    public string AuthorText
    {
        get
        {
            var author = Result.Metadata?.Author;
            return string.IsNullOrWhiteSpace(author) ? string.Empty : author;
        }
    }
    public string GameText => MapDisplayText.ForGame(Result.Game);
    public string StatusText => MapDisplayText.ForStatus(Result.InstallStatus);
    public string MethodText => MapDisplayText.ForMethod(Result.InstallMethod);
    public bool IsInstallEligible { get; private set; }
    public bool IsBatchSelectable { get; private set; }
    public bool CanRemoveFromGame => MapBatchSelectionRules.CanRemoveFromGame(Result);
    public string Details
    {
        get
        {
            var lines = new List<string>();
            var metadata = Result.Metadata;
            if (!string.IsNullOrWhiteSpace(metadata?.Author))
            {
                lines.Add($"Author: {metadata.Author}");
            }
            if (metadata?.Type is not null)
            {
                lines.Add($"Type: {(metadata.Type == MapType.SinglePlayer ? "Single-player" : "Multiplayer")}");
            }
            if (!string.IsNullOrWhiteSpace(metadata?.Theater))
            {
                lines.Add($"Theater: {metadata.Theater}");
            }
            if (metadata?.Width is not null && metadata.Height is not null)
            {
                lines.Add($"Size: {metadata.Width} × {metadata.Height}");
            }
            lines.Add($"Workshop ID: {Result.WorkshopItemId}");
            if (Result.InstallStatus == MapInstallStatus.UnsubscribedPendingSteamCleanup)
            {
                lines.Add("Steam confirmed the Workshop unsubscribe.");
                lines.Add("The downloaded package is pending Steam cleanup.");
                lines.Add("Closing the Fixer allows Steam to process that cleanup; timing depends on Steam.");
            }
            if (!string.IsNullOrWhiteSpace(Result.Error))
            {
                lines.Add(Result.Error);
            }
            return string.Join('\n', lines);
        }
    }
    public BitmapSource? PreviewImage => _previewImage;
    public string PreviewMessage => _previewMessage;
    public Visibility PreviewMessageVisibility => PreviewImage is null ? Visibility.Visible : Visibility.Collapsed;
    public string? PreviewCacheIdentity => _previewCacheIdentity;

    public void UpdateResult(WorkshopMapScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var identityChanged = Result.PgmPath != result.PgmPath || Result.PackageHash != result.PackageHash ||
            Result.CreatorSteamId != result.CreatorSteamId || Result.Game != result.Game;
        _result = result;
        IsInstallEligible = MapInstallationRules.IsEligible(result.InstallStatus);
        IsBatchSelectable = MapBatchSelectionRules.IsSelectable(result);
        if (!IsBatchSelectable)
        {
            IsSelected = false;
        }
        if (identityChanged)
        {
            _previewImage = null;
            _previewCacheIdentity = null;
            _previewMessage = "Preview unavailable";
            OnPropertyChanged(nameof(PreviewImage));
            OnPropertyChanged(nameof(PreviewMessage));
            OnPropertyChanged(nameof(PreviewMessageVisibility));
        }
        foreach (var property in new[] { nameof(Title), nameof(AuthorText), nameof(GameText), nameof(StatusText), nameof(MethodText),
                     nameof(IsInstallEligible), nameof(IsBatchSelectable), nameof(CanRemoveFromGame), nameof(Details) })
        {
            OnPropertyChanged(property);
        }
    }

    public void SetPreviewLoading()
    {
        if (_previewImage is null)
        {
            _previewMessage = "Loading preview…";
            OnPropertyChanged(nameof(PreviewMessage));
        }
    }

    public void SetPreview(BitmapSource? image, string? cacheIdentity = null)
    {
        _previewImage = image;
        _previewCacheIdentity = cacheIdentity;
        _previewMessage = image is null ? "Preview unavailable" : string.Empty;
        OnPropertyChanged(nameof(PreviewImage));
        OnPropertyChanged(nameof(PreviewMessage));
        OnPropertyChanged(nameof(PreviewMessageVisibility));
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            // Enforce eligibility in the model as well as through a disabled checkbox so
            // keyboard/binding/programmatic changes cannot select an unsafe row.
            var next = IsBatchSelectable && value;
            if (_isSelected == next)
            {
                return;
            }

            _isSelected = next;
            OnPropertyChanged(nameof(IsSelected));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
