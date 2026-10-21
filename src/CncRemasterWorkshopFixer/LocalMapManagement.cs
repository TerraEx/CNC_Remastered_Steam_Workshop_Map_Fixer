using System.Globalization;

namespace CncRemasterWorkshopFixer;

public static class LocalMapFileRules
{
    private static readonly IReadOnlyList<string> TiberianDawnExtensions =
        [".BIN", ".INI", ".JSON", ".TGA"];
    private static readonly IReadOnlyList<string> RedAlertExtensions =
        [".MPR", ".JSON", ".TGA"];

    public static IReadOnlyList<string> ExtensionsFor(MapGame game) => game switch
    {
        MapGame.TiberianDawn => TiberianDawnExtensions,
        MapGame.RedAlert => RedAlertExtensions,
        _ => throw new ArgumentOutOfRangeException(nameof(game))
    };

    public static string GetDestination(string localMapsRoot, MapGame game)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localMapsRoot);
        return Path.GetFullPath(Path.Combine(
            Path.GetFullPath(localMapsRoot), MapGameClassifier.DestinationDirectoryName(game)));
    }

    public static IReadOnlyList<string> GetExpectedFiles(
        string localMapsRoot,
        MapGame game,
        ulong creatorSteamId,
        ulong workshopItemId)
    {
        var destination = GetDestination(localMapsRoot, game);
        var basename = WorkshopNaming.CreateBasename(creatorSteamId, workshopItemId);
        return ExtensionsFor(game)
            .Select(extension => Path.Combine(destination, basename + extension))
            .Select(path => ValidateExactTarget(destination, basename, path, ExtensionsFor(game)))
            .ToList();
    }

    public static string ValidateExactTarget(
        string expectedDestination,
        string expectedBasename,
        string targetPath,
        IEnumerable<string> allowedExtensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDestination);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedBasename);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(allowedExtensions);

        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedDestination));
        var target = Path.GetFullPath(targetPath);
        var extension = Path.GetExtension(target).ToUpperInvariant();
        if (!allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing local-map deletion with unexpected extension '{extension}'.");
        }

        // Being somewhere beneath the map folder isn't enough: only this map's
        // exact basename and allowed extensions can be written or removed.
        var expected = Path.GetFullPath(Path.Combine(destination, expectedBasename + extension));
        if (!target.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetDirectoryName(target)!.Equals(destination, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing local-map deletion outside the exact expected destination.");
        }

        return target;
    }
}

public static class WorkshopPage
{
    public static Uri GetUri(ulong workshopItemId) =>
        new($"https://steamcommunity.com/sharedfiles/filedetails/?id={workshopItemId.ToString(CultureInfo.InvariantCulture)}");
}
