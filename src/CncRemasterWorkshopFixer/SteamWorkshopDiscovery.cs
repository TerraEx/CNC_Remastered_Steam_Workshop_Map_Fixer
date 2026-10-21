using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace CncRemasterWorkshopFixer;

public static partial class SteamWorkshopDiscovery
{
    public const uint CncRemasteredAppId = 1213210;

    public static IReadOnlyList<string> DiscoverWorkshopRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        // Registry discovery is a convenience, not a prerequisite: callers can always
        // supply an explicit Workshop content root when Steam uses an unusual layout.
        var steamRoots = GetRegisteredSteamRoots();
        return FindWorkshopRoots(steamRoots);
    }

    public static IReadOnlyList<string> FindWorkshopRoots(IEnumerable<string> steamRoots)
    {
        return FindSteamLibraryRoots(steamRoots)
            .Select(root => Path.Combine(root, "steamapps", "workshop", "content", CncRemasteredAppId.ToString()))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<string> DiscoverSteamLibraryRoots() =>
        OperatingSystem.IsWindows() ? FindSteamLibraryRoots(GetRegisteredSteamRoots()) : [];

    public static IReadOnlyList<string> FindSteamLibraryRoots(IEnumerable<string> steamRoots)
    {
        ArgumentNullException.ThrowIfNull(steamRoots);

        var libraryRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var steamRoot in steamRoots.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var fullSteamRoot = Path.GetFullPath(steamRoot);
            libraryRoots.Add(fullSteamRoot);

            // libraryfolders.vdf paths are library roots; each gets the standard
            // steamapps/workshop/content/<app-id> suffix. Missing libraries are ignored
            // independently so one stale Steam entry cannot block other libraries.
            var libraryFoldersPath = Path.Combine(fullSteamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFoldersPath))
            {
                continue;
            }

            foreach (var libraryRoot in ParseLibraryFolders(File.ReadAllText(libraryFoldersPath)))
            {
                libraryRoots.Add(Path.GetFullPath(libraryRoot));
            }
        }

        return libraryRoots.ToList();
    }

    public static IReadOnlyList<string> ParseLibraryFolders(string vdf)
    {
        ArgumentNullException.ThrowIfNull(vdf);

        return LibraryPathRegex()
            .Matches(vdf)
            .Select(match => match.Groups[1].Value.Replace("\\\\", "\\", StringComparison.Ordinal))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> GetRegisteredSteamRoots()
    {
        var candidates = new (RegistryHive Hive, RegistryView View, string Key, string Value)[]
        {
            (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Valve\Steam", "InstallPath"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Valve\Steam", "InstallPath")
        };

        foreach (var candidate in candidates)
        {
            string? path = null;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(candidate.Hive, candidate.View);
                using var key = baseKey.OpenSubKey(candidate.Key);
                path = key?.GetValue(candidate.Value) as string;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // A missing/inaccessible registry view should not prevent manual-root scanning.
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                yield return path;
            }
        }
    }

    [GeneratedRegex("\\\"path\\\"\\s+\\\"((?:\\\\\\\\|[^\\\"])*)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathRegex();
}
