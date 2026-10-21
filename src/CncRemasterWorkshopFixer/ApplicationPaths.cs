namespace CncRemasterWorkshopFixer;

/// <summary>
/// Canonical locations for the fixer's small amount of persistent user state.
/// Game-owned AppData and Documents paths do not belong here.
/// </summary>
public static class ApplicationPaths
{
    private const string ApplicationDirectoryName = "CncRemasterWorkshopFixer";

    public static string UserDataRoot => GetUserDataRoot(GetLocalApplicationDataRoot());
    public static string SettingsPath => GetSettingsPath(GetLocalApplicationDataRoot());
    public static string KnownEnabledRegistryPath => GetKnownEnabledRegistryPath(GetLocalApplicationDataRoot());

    public static string GetUserDataRoot(string localApplicationDataRoot) =>
        Path.Combine(RequireRoot(localApplicationDataRoot), ApplicationDirectoryName);

    public static string GetSettingsPath(string localApplicationDataRoot) =>
        Path.Combine(GetUserDataRoot(localApplicationDataRoot), "settings.json");

    public static string GetKnownEnabledRegistryPath(string localApplicationDataRoot) =>
        Path.Combine(GetUserDataRoot(localApplicationDataRoot), "known-enabled-maps.json");

    private static string GetLocalApplicationDataRoot()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("Windows local application-data folder could not be resolved.");
        }

        return root;
    }

    private static string RequireRoot(string root) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(root)
            ? throw new ArgumentException("A local application-data root is required.", nameof(root))
            : root);
}
