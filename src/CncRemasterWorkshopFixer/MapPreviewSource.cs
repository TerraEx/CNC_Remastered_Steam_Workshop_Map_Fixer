using System.Security.Cryptography;

namespace CncRemasterWorkshopFixer;

public enum MapPreviewSourceKind
{
    SteamPackage,
    AppDataPackage,
    GeneratedLocalTga
}

public sealed record MapPreviewData(byte[] TgaBytes, string CacheIdentity, MapPreviewSourceKind SourceKind);

/// <summary>Reads previews only from paths derived from the map's trusted identity.</summary>
public static class MapPreviewSource
{
    public static MapPreviewData? TryRead(
        WorkshopMapScanResult map,
        string ugcCacheRoot,
        string localMapsRoot)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentException.ThrowIfNullOrWhiteSpace(ugcCacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(localMapsRoot);

        // Prefer the current download, then fall back to attributable game-side copies.
        // Don't search local map folders: a similar filename doesn't establish identity.
        var candidates = new List<(MapPreviewSourceKind Kind, string Path, bool IsPackage)>();
        if (File.Exists(map.PgmPath))
        {
            candidates.Add((MapPreviewSourceKind.SteamPackage, Path.GetFullPath(map.PgmPath), true));
        }
        if (map.CreatorSteamId is ulong creator)
        {
            var appDataPgm = NativeUgcCacheManager.GetCachePgmPath(
                ugcCacheRoot, creator, map.WorkshopItemId);
            if (File.Exists(appDataPgm) && !candidates.Any(candidate =>
                    candidate.Path.Equals(appDataPgm, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add((MapPreviewSourceKind.AppDataPackage, appDataPgm, true));
            }

            if (map.Game is MapGame game)
            {
                var localTga = LocalMapFileRules.GetExpectedFiles(
                    localMapsRoot, game, creator, map.WorkshopItemId)
                    .Single(path => Path.GetExtension(path).Equals(".TGA", StringComparison.OrdinalIgnoreCase));
                if (File.Exists(localTga))
                {
                    candidates.Add((MapPreviewSourceKind.GeneratedLocalTga, localTga, false));
                }
            }
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var bytes = candidate.IsPackage
                    ? ReadPackagePreview(candidate.Path)
                    : File.ReadAllBytes(candidate.Path);
                var info = new FileInfo(candidate.Path);
                // A replacement can keep the same length and timestamp. Include the
                // preview bytes so that such an update doesn't reuse a stale thumbnail.
                var previewHash = Convert.ToHexString(SHA256.HashData(bytes));
                var identity = $"{map.WorkshopItemId}|{candidate.Kind}|{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{previewHash}";
                return new(bytes, identity, candidate.Kind);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               InvalidDataException or NotSupportedException or ArgumentException or
                                               OverflowException)
            {
                // A bad or concurrently disappearing preview source is presentation-only.
            }
        }

        return null;
    }

    private static byte[] ReadPackagePreview(string path)
    {
        var archive = PgmArchive.Open(path);
        var member = archive.Members.SingleOrDefault(member =>
            member.Name.Equals("MAPDATA.TGA", StringComparison.OrdinalIgnoreCase));
        if (member is null)
        {
            throw new InvalidDataException("The package does not contain one MAPDATA.TGA preview.");
        }
        return archive.Read(member);
    }
}
