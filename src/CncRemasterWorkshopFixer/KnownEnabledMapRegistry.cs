using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CncRemasterWorkshopFixer;

public sealed record KnownEnabledFile(string Extension, string Sha256);

public sealed record KnownEnabledMap(
    ulong WorkshopItemId,
    ulong CreatorSteamId,
    string Title,
    MapGame? Game,
    string SteamSourcePathLastSeen,
    string? AppDataPgmPath,
    string PackageHash,
    DateTimeOffset LastSeenUtc,
    MapInstallMethod InstallationMethod = MapInstallMethod.NativeCache,
    IReadOnlyList<KnownEnabledFile>? DirectFiles = null);

public sealed record KnownEnabledRegistryDocument(int Version, IReadOnlyList<KnownEnabledMap> Maps)
{
    public const int CurrentVersion = 2;
}

public sealed record KnownEnabledRegistryReadResult(bool IsUsable, IReadOnlyList<KnownEnabledMap> Maps, string? Warning);

public sealed class KnownEnabledMapRegistry(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Path { get; } = System.IO.Path.GetFullPath(
        string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("A registry path is required.", nameof(path)) : path);

    public static string GetDefaultPath() => ApplicationPaths.KnownEnabledRegistryPath;

    public KnownEnabledRegistryReadResult Read()
    {
        if (!File.Exists(Path)) return new(true, [], null);
        try
        {
            var document = JsonSerializer.Deserialize<KnownEnabledRegistryDocument>(File.ReadAllBytes(Path), JsonOptions)
                ?? throw new InvalidDataException("The known-enabled map registry is empty.");
            if (document.Version is not (1 or KnownEnabledRegistryDocument.CurrentVersion))
                return new(false, [], $"Known-enabled map registry version {document.Version} is not supported; tracking is disabled for this refresh.");

            var maps = document.Maps?.ToList() ?? throw new InvalidDataException("The known-enabled map registry contains no map collection.");
            // Version 1 predates Direct installs; its records only describe native
            // cache copies and must never become evidence of Direct file ownership.
            if (document.Version == 1)
            {
                if (maps.Any(map => !IsValidV1(map)) || HasDuplicateItems(maps))
                    return new(false, [], "The known-enabled map registry contains invalid data; tracking is disabled for this refresh.");
                maps = maps.Select(map => map with
                {
                    InstallationMethod = MapInstallMethod.NativeCache,
                    DirectFiles = null
                }).ToList();
                Write(maps);
            }

            // Don't partially salvage ownership data. A bad record makes tracking
            // unavailable, so callers can't treat an incomplete registry as trusted.
            if (maps.Any(map => !IsValid(map)) || HasDuplicateItems(maps))
                return new(false, [], "The known-enabled map registry contains invalid data; tracking is disabled for this refresh.");
            return new(true, maps, null);
        }
        catch (Exception exception)
        {
            return new(false, [], $"The known-enabled map registry could not be read; tracking is disabled for this refresh. {exception.Message}");
        }
    }

    public void Write(IEnumerable<KnownEnabledMap> maps)
    {
        ArgumentNullException.ThrowIfNull(maps);
        var entries = maps.OrderBy(map => map.WorkshopItemId).ToList();
        if (entries.Any(map => !IsValid(map)) || HasDuplicateItems(entries))
            throw new InvalidDataException("Refusing to write an invalid known-enabled map registry.");

        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        // Keep the previous ownership document intact until the replacement is
        // flushed and ready to rename into place.
        var temporary = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new KnownEnabledRegistryDocument(KnownEnabledRegistryDocument.CurrentVersion, entries), JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static bool IsValid(KnownEnabledMap map)
    {
        if (!IsValidCommon(map) || !Enum.IsDefined(map.InstallationMethod)) return false;
        if (map.InstallationMethod == MapInstallMethod.NativeCache)
            return !string.IsNullOrWhiteSpace(map.AppDataPgmPath) && map.DirectFiles is null or { Count: 0 };
        if (map.Game is null || map.DirectFiles is null) return false;
        var expected = LocalMapFileRules.ExtensionsFor(map.Game.Value);
        return map.DirectFiles.Count == expected.Count &&
               map.DirectFiles.Select(file => NormalizeExtension(file.Extension)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == expected.Count &&
               map.DirectFiles.All(file => expected.Contains(NormalizeExtension(file.Extension), StringComparer.OrdinalIgnoreCase) && IsSha256(file.Sha256));
    }

    private static bool IsValidV1(KnownEnabledMap map) => IsValidCommon(map) && !string.IsNullOrWhiteSpace(map.AppDataPgmPath);

    private static bool IsValidCommon(KnownEnabledMap map) =>
        map.WorkshopItemId != 0 && map.CreatorSteamId != 0 && !string.IsNullOrWhiteSpace(map.Title) &&
        !string.IsNullOrWhiteSpace(map.SteamSourcePathLastSeen) && IsSha256(map.PackageHash) &&
        (map.Game is null || Enum.IsDefined(map.Game.Value));

    private static bool HasDuplicateItems(IReadOnlyCollection<KnownEnabledMap> maps) =>
        maps.Select(map => map.WorkshopItemId).Distinct().Count() != maps.Count;

    internal static string NormalizeExtension(string extension) =>
        (extension.StartsWith('.') ? extension : "." + extension).ToUpperInvariant();

    internal static bool IsSha256(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

public static class PackageHash
{
    public static string Sha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
