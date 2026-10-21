using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace CncRemasterWorkshopFixer;

public static partial class CncSteamContextLocator
{
    public static string? DiscoverRunDirectory() =>
        FindRunDirectory(SteamWorkshopDiscovery.DiscoverSteamLibraryRoots());

    public static string? FindRunDirectory(IEnumerable<string> libraryRoots)
    {
        foreach (var library in libraryRoots)
        {
            try
            {
                var steamApps = Path.Combine(Path.GetFullPath(library), "steamapps");
                var manifest = Path.Combine(steamApps, $"appmanifest_{SteamWorkshopDiscovery.CncRemasteredAppId}.acf");
                if (!File.Exists(manifest)) continue;
                var text = File.ReadAllText(manifest);
                var appId = AppIdRegex().Match(text).Groups[1].Value;
                if (appId != SteamWorkshopDiscovery.CncRemasteredAppId.ToString(CultureInfo.InvariantCulture)) continue;
                var directory = InstallDirectoryRegex().Match(text).Groups[1].Value;
                // Steam's installdir is a single directory name, never a supplied absolute/traversal path.
                if (string.IsNullOrWhiteSpace(directory) || directory is "." or ".." ||
                    directory.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
                var runDirectory = Path.Combine(steamApps, "common", directory);
                if (IsValidRunDirectory(runDirectory)) return runDirectory;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A stale/inaccessible library does not prevent trying another installed copy.
            }
        }
        return null;
    }

    public static bool IsValidRunDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return false;
        // Reuse the installed game's context. The fixer doesn't ship or create
        // an App ID file, nor guess an identity from the signed-in Steam account.
        var appIdFile = Path.Combine(directory, "steam_appid.txt");
        return File.Exists(appIdFile) &&
               uint.TryParse(File.ReadAllText(appIdFile).Trim(), NumberStyles.None,
                   CultureInfo.InvariantCulture, out var appId) &&
               appId == SteamWorkshopDiscovery.CncRemasteredAppId;
    }

    [GeneratedRegex("\"appid\"\\s+\"([0-9]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex AppIdRegex();

    [GeneratedRegex("\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDirectoryRegex();
}
