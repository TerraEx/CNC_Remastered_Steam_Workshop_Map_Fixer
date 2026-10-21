using System.IO;

namespace CncRemasterWorkshopFixer;

public sealed record WorkshopRootResolution(
    IReadOnlyList<string> Roots,
    string? Warning);

public static class WorkshopRootPreference
{
    public static string ValidateSelection(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException("The selected Workshop folder does not exist.");
        }

        if (!IsCncRemasteredContentRoot(fullPath))
        {
            throw new ArgumentException(
                "Choose C&C Remastered's steamapps\\workshop\\content\\1213210 folder.", nameof(path));
        }

        return fullPath;
    }

    public static WorkshopRootResolution Resolve(
        string? rememberedRoot,
        Func<IReadOnlyList<string>> discoverRoots)
    {
        ArgumentNullException.ThrowIfNull(discoverRoots);
        if (string.IsNullOrWhiteSpace(rememberedRoot))
        {
            return new(discoverRoots(), null);
        }

        try
        {
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rememberedRoot));
            if (Directory.Exists(fullPath) && IsCncRemasteredContentRoot(fullPath))
            {
                return new([fullPath], null);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or
                                           PathTooLongException or System.Security.SecurityException)
        {
        }

        // An unplugged Steam library may return later. Fall back for this session
        // without discarding the remembered override from settings.
        return new(
            discoverRoots(),
            "The saved Workshop folder could not be found. Automatic Steam library discovery was used instead.");
    }

    private static bool IsCncRemasteredContentRoot(string path)
    {
        var directory = new DirectoryInfo(path);
        return directory.Name.Equals(SteamWorkshopDiscovery.CncRemasteredAppId.ToString(),
                   StringComparison.OrdinalIgnoreCase) &&
               directory.Parent?.Name.Equals("content", StringComparison.OrdinalIgnoreCase) == true &&
               directory.Parent.Parent?.Name.Equals("workshop", StringComparison.OrdinalIgnoreCase) == true &&
               directory.Parent.Parent.Parent?.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) == true;
    }
}
