using System.Globalization;

namespace CncRemasterWorkshopFixer;

public enum MapInstallStatus
{
    NotInstalled,
    Installed,
    UpdateAvailable,
    ModifiedLocally,
    SourceUnavailable,
    MetadataUnavailable,
    Error,
    // Application/session interpretation; the filesystem scanner never assigns it.
    UnsubscribedPendingSteamCleanup
}

public sealed record WorkshopMapScanResult(
    ulong WorkshopItemId,
    string PgmPath,
    string Title,
    ulong? CreatorSteamId,
    MapGame? Game,
    MapInstallStatus InstallStatus,
    SteamWorkshopMetadataStatus MetadataStatus,
    int? SteamResult,
    string? Error,
    string? PackageHash = null,
    MapInstallMethod? InstallMethod = null,
    MapMetadata? Metadata = null);

public sealed class WorkshopMapScanner(SteamWorkshopMetadataClient metadataClient)
{
    public async Task<IReadOnlyList<WorkshopMapScanResult>> ScanAsync(
        IEnumerable<string> workshopRoots,
        string ugcCacheRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workshopRoots);
        ArgumentException.ThrowIfNullOrWhiteSpace(ugcCacheRoot);

        var inspected = EnumeratePackages(workshopRoots).Select(InspectCandidate).ToList();
        IReadOnlyDictionary<ulong, SteamWorkshopMetadata> metadata;
        try
        {
            metadata = (await metadataClient.GetMetadataAsync(
                    inspected.Select(item => item.WorkshopItemId), cancellationToken))
                .ToDictionary(item => item.WorkshopItemId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            metadata = inspected.Select(item => item.WorkshopItemId).Distinct().ToDictionary(
                itemId => itemId,
                itemId => SteamWorkshopMetadata.Failed(itemId, exception.Message));
        }

        return inspected.Select(item => CreateResult(item, metadata, ugcCacheRoot)).ToList();
    }

    public static IReadOnlyList<WorkshopMapPackage> EnumeratePackages(IEnumerable<string> workshopRoots)
    {
        ArgumentNullException.ThrowIfNull(workshopRoots);
        var packages = new List<WorkshopMapPackage>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in workshopRoots.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var fullRoot = Path.GetFullPath(root);
            if (!Directory.Exists(fullRoot))
            {
                continue;
            }

            // Steam stores each item directly beneath the app's content root.
            // Recursing would pick up unrelated or nested packages with unclear identity.
            foreach (var directory in Directory.EnumerateDirectories(fullRoot))
            {
                var name = Path.GetFileName(directory);
                if (!ulong.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var itemId))
                {
                    continue;
                }

                var pgmPath = Path.Combine(directory, "MAPDATA.PGM");
                if (File.Exists(pgmPath) && seenPaths.Add(Path.GetFullPath(pgmPath)))
                {
                    packages.Add(new WorkshopMapPackage(itemId, Path.GetFullPath(pgmPath)));
                }
            }
        }

        return packages.OrderBy(package => package.WorkshopItemId)
            .ThenBy(package => package.PgmPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static MapInstallStatus DetectInstallStatus(
        string steamPgmPath,
        ulong workshopItemId,
        ulong creatorSteamId,
        string ugcCacheRoot)
    {
        return DetectInstallState(steamPgmPath, workshopItemId, creatorSteamId, ugcCacheRoot).Status;
    }

    private static (MapInstallStatus Status, string? PackageHash) DetectInstallState(
        string steamPgmPath,
        ulong workshopItemId,
        ulong creatorSteamId,
        string ugcCacheRoot)
    {
        var source = NativeUgcCacheManager.ValidateSteamPackage(steamPgmPath, workshopItemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ugcCacheRoot);
        var target = NativeUgcCacheManager.GetCachePgmPath(
            ugcCacheRoot, creatorSteamId, workshopItemId);
        if (!File.Exists(target))
        {
            return (MapInstallStatus.NotInstalled, null);
        }

        var comparison = NativeUgcCacheManager.CompareAndHashSource(source, target);
        return comparison.AreIdentical
            ? (MapInstallStatus.Installed, comparison.SourceSha256)
            : (MapInstallStatus.UpdateAvailable, null);
    }

    private static InspectedWorkshopMap InspectCandidate(WorkshopMapPackage package)
    {
        try
        {
            if (new FileInfo(package.PgmPath).Length == 0)
            {
                throw new InvalidDataException("The Steam Workshop MAPDATA.PGM is empty.");
            }

            var archive = PgmArchive.Open(package.PgmPath);
            try
            {
                var game = MapGameClassifier.Classify(archive.Members);
                return new(package.WorkshopItemId, package.PgmPath,
                    game, MapMetadataReader.ReadFromArchive(archive, game), null, false);
            }
            catch (NotSupportedException exception)
            {
                return new(package.WorkshopItemId, package.PgmPath, null, null, exception.Message, false);
            }
        }
        catch (Exception exception)
        {
            // Inspection is diagnostic. It does not gate native copying unless the source
            // is clearly unusable; unknown future layouts remain enableable with Game Unknown.
            var fatal = false;
            try { NativeUgcCacheManager.ValidateSteamPackage(package.PgmPath, package.WorkshopItemId); }
            catch { fatal = true; }
            return new(package.WorkshopItemId, package.PgmPath, null, null, exception.Message, fatal);
        }
    }

    private static WorkshopMapScanResult CreateResult(
        InspectedWorkshopMap item,
        IReadOnlyDictionary<ulong, SteamWorkshopMetadata> metadataById,
        string ugcCacheRoot)
    {
        var fallbackTitle = item.WorkshopItemId.ToString(CultureInfo.InvariantCulture);
        metadataById.TryGetValue(item.WorkshopItemId, out var metadata);
        var title = string.IsNullOrWhiteSpace(metadata?.Title) ? fallbackTitle : metadata.Title;

        if (item.SourceInvalid)
        {
            return new(item.WorkshopItemId, item.PgmPath, title, metadata?.CreatorSteamId,
                item.Game, MapInstallStatus.Error, metadata?.Status ?? SteamWorkshopMetadataStatus.Unavailable,
                metadata?.Result, item.InspectionError, Metadata: item.Metadata);
        }

        if (metadata?.Status != SteamWorkshopMetadataStatus.Available || metadata.CreatorSteamId is null)
        {
            return new(item.WorkshopItemId, item.PgmPath, title, metadata?.CreatorSteamId,
                item.Game, MapInstallStatus.MetadataUnavailable,
                metadata?.Status ?? SteamWorkshopMetadataStatus.Unavailable,
                metadata?.Result, metadata?.Error ?? item.InspectionError, Metadata: item.Metadata);
        }

        try
        {
            var state = DetectInstallState(item.PgmPath, item.WorkshopItemId,
                metadata.CreatorSteamId.Value, ugcCacheRoot);
            return new(item.WorkshopItemId, item.PgmPath, title, metadata.CreatorSteamId,
                item.Game, state.Status, metadata.Status,
                metadata.Result, item.InspectionError, state.PackageHash,
                state.Status is MapInstallStatus.Installed or MapInstallStatus.UpdateAvailable
                    ? MapInstallMethod.NativeCache : null, item.Metadata);
        }
        catch (Exception exception)
        {
            return new(item.WorkshopItemId, item.PgmPath, title, metadata.CreatorSteamId,
                item.Game, MapInstallStatus.Error, metadata.Status, metadata.Result, exception.Message,
                Metadata: item.Metadata);
        }
    }

    private sealed record InspectedWorkshopMap(
        ulong WorkshopItemId,
        string PgmPath,
        MapGame? Game,
        MapMetadata? Metadata,
        string? InspectionError,
        bool SourceInvalid);
}

public sealed record WorkshopMapPackage(ulong WorkshopItemId, string PgmPath);
