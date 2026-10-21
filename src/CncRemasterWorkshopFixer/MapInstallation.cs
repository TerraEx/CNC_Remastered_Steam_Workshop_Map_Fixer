namespace CncRemasterWorkshopFixer;

public enum MapInstallMethod
{
    NativeCache,
    DirectLocal
}

public static class MapInstallationRules
{
    public static bool IsEligible(MapInstallStatus status) => status is
        MapInstallStatus.NotInstalled or MapInstallStatus.UpdateAvailable or MapInstallStatus.ModifiedLocally;
}

public static class MapBatchSelectionRules
{
    // Native cache identity needs creator/item metadata but not successful PGM inspection.
    public static bool IsSelectable(WorkshopMapScanResult map) =>
        map.CreatorSteamId is not null &&
        map.InstallStatus is MapInstallStatus.NotInstalled or
            MapInstallStatus.Installed or MapInstallStatus.UpdateAvailable or
            MapInstallStatus.ModifiedLocally or MapInstallStatus.SourceUnavailable;

    public static bool CanRemoveFromGame(WorkshopMapScanResult map) =>
        map.CreatorSteamId is not null &&
        map.InstallStatus is MapInstallStatus.Installed or MapInstallStatus.UpdateAvailable or
            MapInstallStatus.ModifiedLocally or MapInstallStatus.SourceUnavailable;

    public static bool IsEnabledSelectorMatch(WorkshopMapScanResult map) =>
        IsSelectable(map) && map.InstallStatus == MapInstallStatus.Installed;

    public static bool IsDisabledOrOutdatedSelectorMatch(WorkshopMapScanResult map) =>
        IsSelectable(map) &&
        map.InstallStatus is MapInstallStatus.NotInstalled or MapInstallStatus.UpdateAvailable;

    public static bool IsSourceUnavailableSelectorMatch(WorkshopMapScanResult map) =>
        IsSelectable(map) && map.InstallStatus == MapInstallStatus.SourceUnavailable;
}

public static class MapDisplayText
{
    public static string ForStatus(MapInstallStatus status) => status switch
    {
        MapInstallStatus.NotInstalled => "Not Enabled",
        MapInstallStatus.Installed => "Enabled",
        MapInstallStatus.UpdateAvailable => "Update Available",
        MapInstallStatus.ModifiedLocally => "Modified Locally",
        MapInstallStatus.SourceUnavailable => "Source Unavailable",
        MapInstallStatus.MetadataUnavailable => "Metadata Unavailable",
        MapInstallStatus.Error => "Error",
        MapInstallStatus.UnsubscribedPendingSteamCleanup => "Unsubscribed — pending Steam cleanup",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public static string ForGame(MapGame? game) => game switch
    {
        MapGame.TiberianDawn => "Tiberian Dawn",
        MapGame.RedAlert => "Red Alert",
        null => "Unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(game))
    };

    public static string ForMethod(MapInstallMethod? method) => method switch
    {
        MapInstallMethod.NativeCache => "Native",
        MapInstallMethod.DirectLocal => "Direct",
        null => string.Empty,
        _ => throw new ArgumentOutOfRangeException(nameof(method))
    };
}
