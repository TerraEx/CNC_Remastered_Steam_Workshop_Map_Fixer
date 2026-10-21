namespace CncRemasterWorkshopFixer;

public sealed record WorkshopInventoryResult(IReadOnlyList<WorkshopMapScanResult> Maps, string? RegistryWarning);

public sealed class WorkshopMapInventory(
    WorkshopMapScanner scanner,
    KnownEnabledMapRegistry registry,
    Func<DateTimeOffset>? utcNow = null)
{
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    public async Task<WorkshopInventoryResult> RefreshAsync(
        IEnumerable<string> workshopRoots,
        string ugcCacheRoot,
        string localMapsRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workshopRoots);
        ArgumentException.ThrowIfNullOrWhiteSpace(ugcCacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(localMapsRoot);
        var current = (await scanner.ScanAsync(workshopRoots, ugcCacheRoot, cancellationToken)).ToList();
        var read = registry.Read();
        if (!read.IsUsable) return new(current, read.Warning);

        var tracked = read.Maps.ToDictionary(map => map.WorkshopItemId);
        var warnings = new List<string>();
        for (var index = 0; index < current.Count; index++)
        {
            var map = current[index];
            tracked.TryGetValue(map.WorkshopItemId, out var entry);
            if (map.InstallMethod != MapInstallMethod.NativeCache && entry is not null)
            {
                // Saved creator identity lets us recognise an existing native copy
                // even when this refresh couldn't obtain Steam metadata.
                var trustedNativePgm = NativeUgcCacheManager.GetCachePgmPath(
                    ugcCacheRoot, entry.CreatorSteamId, entry.WorkshopItemId);
                if (File.Exists(trustedNativePgm))
                {
                    var comparison = NativeUgcCacheManager.CompareAndHashSource(map.PgmPath, trustedNativePgm);
                    var sourceHash = comparison.SourceSha256 ?? PackageHash.Sha256(map.PgmPath);
                    map = map with
                    {
                        CreatorSteamId = entry.CreatorSteamId,
                        Game = map.Game ?? entry.Game,
                        InstallStatus = comparison.AreIdentical ? MapInstallStatus.Installed : MapInstallStatus.UpdateAvailable,
                        PackageHash = sourceHash,
                        InstallMethod = MapInstallMethod.NativeCache
                    };
                    current[index] = map;
                }
            }
            // A native PGM takes precedence over Direct ownership: C&C can unpack
            // it again, so treating only the extracted files as the install is misleading.
            if (map.InstallMethod == MapInstallMethod.NativeCache)
            {
                var creator = map.CreatorSteamId!.Value;
                tracked[map.WorkshopItemId] = new KnownEnabledMap(
                    map.WorkshopItemId, creator, map.Title, map.Game, map.PgmPath,
                    NativeUgcCacheManager.GetCachePgmPath(ugcCacheRoot, creator, map.WorkshopItemId),
                    map.PackageHash ?? PackageHash.Sha256(map.PgmPath), _utcNow(), MapInstallMethod.NativeCache);
                continue;
            }

            if (entry?.InstallationMethod == MapInstallMethod.DirectLocal &&
                (map.CreatorSteamId is null || map.CreatorSteamId == entry.CreatorSteamId))
            {
                current[index] = EvaluateDirect(map with
                {
                    CreatorSteamId = entry.CreatorSteamId,
                    Game = map.Game ?? entry.Game
                }, entry, localMapsRoot, tracked);
                continue;
            }

            if (entry?.InstallationMethod == MapInstallMethod.NativeCache)
            {
                var localFiles = entry.Game is null ? [] : LocalMapFileRules.GetExpectedFiles(
                    localMapsRoot, entry.Game.Value, entry.CreatorSteamId, entry.WorkshopItemId);
                if (localFiles.Any(File.Exists))
                    current[index] = map with { InstallStatus = MapInstallStatus.UpdateAvailable, InstallMethod = MapInstallMethod.NativeCache };
                else
                    tracked.Remove(map.WorkshopItemId);
            }
        }

        // Missing Steam content doesn't mean the game-side copy disappeared. Keep
        // those rows only while exact, previously tracked targets still exist.
        var discoveredIds = current.Select(map => map.WorkshopItemId).ToHashSet();
        var unavailable = new List<WorkshopMapScanResult>();
        foreach (var entry in tracked.Values.Where(entry => !discoveredIds.Contains(entry.WorkshopItemId)).ToList())
        {
            var nativePgm = NativeUgcCacheManager.GetCachePgmPath(
                ugcCacheRoot, entry.CreatorSteamId, entry.WorkshopItemId);
            var effectiveEntry = entry;
            if (File.Exists(nativePgm) && entry.InstallationMethod != MapInstallMethod.NativeCache)
            {
                effectiveEntry = entry with
                {
                    InstallationMethod = MapInstallMethod.NativeCache,
                    AppDataPgmPath = nativePgm,
                    DirectFiles = null
                };
                tracked[entry.WorkshopItemId] = effectiveEntry;
            }
            var hasState = effectiveEntry.InstallationMethod == MapInstallMethod.DirectLocal
                ? DirectTargets(localMapsRoot, effectiveEntry).Any(File.Exists)
                : File.Exists(nativePgm) ||
                  (effectiveEntry.Game is not null && LocalMapFileRules.GetExpectedFiles(localMapsRoot, effectiveEntry.Game.Value,
                      effectiveEntry.CreatorSteamId, effectiveEntry.WorkshopItemId).Any(File.Exists));
            if (!hasState)
            {
                tracked.Remove(entry.WorkshopItemId);
                continue;
            }
            unavailable.Add(new WorkshopMapScanResult(effectiveEntry.WorkshopItemId, effectiveEntry.SteamSourcePathLastSeen,
                effectiveEntry.Title, effectiveEntry.CreatorSteamId, effectiveEntry.Game, MapInstallStatus.SourceUnavailable,
                SteamWorkshopMetadataStatus.Unavailable, null,
                "The Steam Workshop source for this previously enabled map is not currently available. The map's game-side copy is still present.",
                effectiveEntry.PackageHash, effectiveEntry.InstallationMethod));
        }

        try { registry.Write(tracked.Values); }
        catch (Exception exception) { warnings.Add($"Known-enabled map tracking could not be saved: {exception.Message}"); }
        return new(current.Concat(unavailable).ToList(), warnings.Count == 0 ? null : string.Join(" ", warnings));
    }

    public NativeCacheRemovalResult RemoveFromGame(
        WorkshopMapScanResult map, string ugcCacheRoot, string localMapsRoot)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.CreatorSteamId is null || map.InstallMethod is null)
            throw new InvalidOperationException("The map has no tracked installation method to remove.");
        var read = registry.Read();
        var entry = read.IsUsable
            ? read.Maps.SingleOrDefault(candidate => candidate.WorkshopItemId == map.WorkshopItemId)
            : null;

        NativeCacheRemovalResult result;
        if (map.InstallMethod == MapInstallMethod.DirectLocal)
        {
            if (!read.IsUsable || entry is null || entry.InstallationMethod != MapInstallMethod.DirectLocal)
                throw new InvalidOperationException("The map no longer has valid Direct Local ownership.");
            var expected = DirectTargets(localMapsRoot, entry);
            var removed = DirectLocalInstaller.RemoveOwned(localMapsRoot, entry);
            result = new(removed, expected.Except(removed, StringComparer.OrdinalIgnoreCase).ToList(), true);
        }
        else
        {
            result = new NativeUgcCacheManager().RemoveKnownFromGame(
                ugcCacheRoot, localMapsRoot, map.Game, map.CreatorSteamId.Value, map.WorkshopItemId);
        }
        if (read.IsUsable)
            registry.Write(read.Maps.Where(candidate => candidate.WorkshopItemId != map.WorkshopItemId));
        return result;
    }

    public NativeCacheRemovalResult RemoveSourceUnavailable(WorkshopMapScanResult map, string ugcCacheRoot, string localMapsRoot) =>
        RemoveFromGame(map, ugcCacheRoot, localMapsRoot);

    private static WorkshopMapScanResult EvaluateDirect(WorkshopMapScanResult map, KnownEnabledMap entry,
        string localMapsRoot, IDictionary<ulong, KnownEnabledMap> tracked)
    {
        if (!KnownEnabledMapRegistry.IsValid(entry) || entry.Game is null)
            return map;
        var targets = DirectTargets(localMapsRoot, entry);
        var present = targets.Count(File.Exists);
        if (present == 0)
        {
            tracked.Remove(entry.WorkshopItemId);
            return map with { InstallMethod = null };
        }
        // Local edits or a partial payload take priority over a Workshop update.
        // Otherwise an ordinary update could silently overwrite the user's changes.
        if (present != targets.Count || !FilesMatch(entry, targets))
            return map with { InstallStatus = MapInstallStatus.ModifiedLocally, InstallMethod = MapInstallMethod.DirectLocal };
        var sourceHash = PackageHash.Sha256(map.PgmPath);
        return map with
        {
            InstallStatus = sourceHash.Equals(entry.PackageHash, StringComparison.OrdinalIgnoreCase)
                ? MapInstallStatus.Installed : MapInstallStatus.UpdateAvailable,
            PackageHash = sourceHash,
            InstallMethod = MapInstallMethod.DirectLocal
        };
    }

    private static bool FilesMatch(KnownEnabledMap entry, IReadOnlyList<string> targets)
    {
        var hashes = entry.DirectFiles!.ToDictionary(file => KnownEnabledMapRegistry.NormalizeExtension(file.Extension),
            file => file.Sha256, StringComparer.OrdinalIgnoreCase);
        return targets.All(path => hashes.TryGetValue(Path.GetExtension(path).ToUpperInvariant(), out var expected) &&
                                   PackageHash.Sha256(path).Equals(expected, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> DirectTargets(string localMapsRoot, KnownEnabledMap entry) =>
        entry.Game is null ? [] : LocalMapFileRules.GetExpectedFiles(localMapsRoot, entry.Game.Value,
            entry.CreatorSteamId, entry.WorkshopItemId);
}
