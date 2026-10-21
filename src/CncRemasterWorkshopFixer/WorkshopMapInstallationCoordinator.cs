namespace CncRemasterWorkshopFixer;

public sealed record MapInstallAttempt(
    WorkshopMapScanResult Map,
    MapInstallMethod Method,
    object? Result,
    string? Error)
{
    public bool Succeeded => Result is not null;
}

public sealed class WorkshopMapInstallationCoordinator(
    KnownEnabledMapRegistry registry,
    NativeUgcCacheManager? nativeInstaller = null,
    DirectLocalInstaller? directInstaller = null,
    Func<DateTimeOffset>? utcNow = null)
{
    private readonly NativeUgcCacheManager _nativeInstaller = nativeInstaller ?? new();
    private readonly DirectLocalInstaller _directInstaller = directInstaller ?? new();
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    public IReadOnlyList<MapInstallAttempt> Install(
        IEnumerable<WorkshopMapScanResult> maps,
        MapInstallMethod defaultMethod,
        string ugcCacheRoot,
        string localMapsRoot)
    {
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentException.ThrowIfNullOrWhiteSpace(ugcCacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(localMapsRoot);
        var read = registry.Read();
        var tracked = read.IsUsable
            ? read.Maps.ToDictionary(map => map.WorkshopItemId)
            : new Dictionary<ulong, KnownEnabledMap>();
        var attempts = new List<MapInstallAttempt>();

        foreach (var map in maps.Where(map => MapInstallationRules.IsEligible(map.InstallStatus)))
        {
            // A settings change only chooses the method for a new enable. Updates
            // and repairs must keep the method that owns the existing game-side state.
            var method = map.InstallMethod ?? defaultMethod;
            try
            {
                if (map.CreatorSteamId is null)
                    throw new InvalidOperationException("The map creator SteamID is unavailable.");
                if (method == MapInstallMethod.DirectLocal && !read.IsUsable)
                    throw new InvalidOperationException("Direct Local Install cannot proceed because reliable ownership tracking is unavailable.");

                object result;
                KnownEnabledMap entry;
                if (method == MapInstallMethod.NativeCache)
                {
                    result = _nativeInstaller.Install(map.PgmPath, map.WorkshopItemId,
                        map.CreatorSteamId.Value, ugcCacheRoot);
                    var hash = PackageHash.Sha256(map.PgmPath);
                    entry = CreateEntry(map, method, hash,
                        NativeUgcCacheManager.GetCachePgmPath(ugcCacheRoot, map.CreatorSteamId.Value, map.WorkshopItemId), null);
                }
                else
                {
                    if (map.Game is null)
                        throw new InvalidOperationException("Direct Local Install requires a known Tiberian Dawn or Red Alert game destination. Use Native C&C Cache for this item.");
                    tracked.TryGetValue(map.WorkshopItemId, out var existing);
                    if (existing?.InstallationMethod != MapInstallMethod.DirectLocal) existing = null;
                    // A new Direct install must prove ownership persistence is available
                    // before creating any game-side files.
                    if (existing is null) registry.Write(tracked.Values);
                    var direct = _directInstaller.Install(map.PgmPath, map.WorkshopItemId,
                        map.CreatorSteamId.Value, localMapsRoot, existing);
                    result = direct;
                    entry = CreateEntry(map, method, direct.PackageSha256, null,
                        direct.Files.Select(file => new KnownEnabledFile(file.Extension, file.Sha256)).ToList());
                }

                tracked[map.WorkshopItemId] = entry;
                try { registry.Write(tracked.Values); }
                catch
                {
                    // A first Direct install mustn't leave files we can't attribute
                    // on the next refresh if saving its ownership record fails.
                    if (method == MapInstallMethod.DirectLocal && !read.Maps.Any(old => old.WorkshopItemId == map.WorkshopItemId))
                    {
                        DirectLocalInstaller.RemoveOwned(localMapsRoot, entry);
                        tracked.Remove(map.WorkshopItemId);
                    }
                    throw;
                }
                attempts.Add(new(map, method, result, null));
            }
            catch (Exception exception)
            {
                attempts.Add(new(map, method, null, exception.Message));
            }
        }
        return attempts;
    }

    private KnownEnabledMap CreateEntry(WorkshopMapScanResult map, MapInstallMethod method,
        string packageHash, string? appDataPgmPath, IReadOnlyList<KnownEnabledFile>? directFiles) =>
        new(map.WorkshopItemId, map.CreatorSteamId!.Value, map.Title, map.Game, map.PgmPath,
            appDataPgmPath, packageHash, _utcNow(), method, directFiles);
}

// Native-only adapter for the CLI; the application
// uses WorkshopMapInstallationCoordinator.
public sealed class WorkshopMapBatchInstaller(NativeUgcCacheManager? cacheManager = null)
{
    private readonly NativeUgcCacheManager _cacheManager = cacheManager ?? new();

    public IReadOnlyList<MapInstallAttempt> Install(IEnumerable<WorkshopMapScanResult> maps, string destinationRoot)
    {
        var attempts = new List<MapInstallAttempt>();
        foreach (var map in maps.Where(map => MapInstallationRules.IsEligible(map.InstallStatus)))
        {
            try
            {
                if (map.CreatorSteamId is null) throw new InvalidOperationException("The map creator SteamID is unavailable.");
                attempts.Add(new(map, MapInstallMethod.NativeCache,
                    _cacheManager.Install(map.PgmPath, map.WorkshopItemId, map.CreatorSteamId.Value, destinationRoot), null));
            }
            catch (Exception exception)
            {
                attempts.Add(new(map, MapInstallMethod.NativeCache, null, exception.Message));
            }
        }
        return attempts;
    }
}
