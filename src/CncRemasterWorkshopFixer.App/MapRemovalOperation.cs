namespace CncRemasterWorkshopFixer;

public enum WorkshopUnsubscribeStatus { Succeeded, Failed, TimedOut, Unavailable }

public sealed record WorkshopUnsubscribeResult(
    ulong WorkshopItemId, WorkshopUnsubscribeStatus Status, string? Error = null)
{
    public bool Succeeded => Status == WorkshopUnsubscribeStatus.Succeeded;
}

public interface ISteamWorkshopSubscriptionService
{
    Task<IReadOnlyList<WorkshopUnsubscribeResult>> UnsubscribeAsync(
        IReadOnlyList<ulong> workshopIds, IProgress<string>? progress = null);
}

public sealed record LocalMapRemovalAttempt(
    WorkshopMapScanResult Map, NativeCacheRemovalResult? Removal, string? Error)
{
    public bool Succeeded => Removal is not null;
}

public sealed record MapRemovalOperationResult(
    IReadOnlyList<LocalMapRemovalAttempt> LocalResults,
    bool UnsubscribeRequested,
    IReadOnlyList<WorkshopUnsubscribeResult> UnsubscribeResults)
{
    public int RequestedCount => LocalResults.Count;
    public int LocalSucceeded => LocalResults.Count(item => item.Succeeded);
    public int LocalFailed => RequestedCount - LocalSucceeded;
    public int UnsubscribeEligibleCount => LocalResults.Count(item => item.Succeeded && IsUsableWorkshopId(item.Map.WorkshopItemId));
    public int UnsubscribeSucceeded => UnsubscribeResults.Count(item => item.Succeeded);
    public int UnsubscribeFailed => UnsubscribeResults.Count - UnsubscribeSucceeded;
    public static bool IsUsableWorkshopId(ulong id) => id != 0 && id != ulong.MaxValue;
}

/// <summary>Coordinates existing local deletion with an optional account operation at the app edge.</summary>
public sealed class MapRemovalOperation(
    Func<WorkshopMapScanResult, Task<NativeCacheRemovalResult>> removeLocal,
    ISteamWorkshopSubscriptionService subscriptions)
{
    public async Task<MapRemovalOperationResult> RunAsync(
        IReadOnlyList<WorkshopMapScanResult> maps, bool unsubscribe, IProgress<string>? progress = null)
    {
        var local = new List<LocalMapRemovalAttempt>();
        progress?.Report("Removing maps from the game…");
        foreach (var map in maps)
        {
            try { local.Add(new(map, await removeLocal(map), null)); }
            catch (Exception ex) { local.Add(new(map, null, ex.Message)); }
        }

        // Unsubscribe only after local removal succeeds. Steam being unavailable
        // mustn't undo that removal, and a failed removal must keep its subscription.
        var ids = local.Where(item => item.Succeeded && MapRemovalOperationResult.IsUsableWorkshopId(item.Map.WorkshopItemId))
            .Select(item => item.Map.WorkshopItemId).Distinct().ToList();
        IReadOnlyList<WorkshopUnsubscribeResult> steam = [];
        if (unsubscribe && ids.Count > 0)
        {
            progress?.Report("Unsubscribing from Steam Workshop…");
            try
            {
                var results = await subscriptions.UnsubscribeAsync(ids, progress);
                // Missing responses must never silently count as success.
                steam = ids.Select(id => results.FirstOrDefault(result => result.WorkshopItemId == id)
                    ?? new(id, WorkshopUnsubscribeStatus.Failed, "Steam returned no result for this item.")).ToList();
            }
            catch (Exception ex)
            {
                steam = ids.Select(id => new WorkshopUnsubscribeResult(id, WorkshopUnsubscribeStatus.Unavailable, ex.Message)).ToList();
            }
        }
        return new(local, unsubscribe, steam);
    }
}

public sealed record MapRemovalNotification(string Title, string Message, ToastSeverity Severity)
{
    public static MapRemovalNotification For(MapRemovalOperationResult result)
    {
        var count = result.LocalSucceeded;
        var subject = count == 1 ? "1 map was" : $"{count} maps were";
        if (count == 0)
            return new("Removal failed", "Maps could not be removed. See Activity for details.", ToastSeverity.Error);
        if (result.LocalFailed == 0 && result.UnsubscribeFailed == 0)
        {
            var message = $"{subject} removed from the game";
            if (result.UnsubscribeSucceeded == count) message += " and unsubscribed from Steam Workshop";
            else if (result.UnsubscribeSucceeded > 0)
                message += $"; {result.UnsubscribeSucceeded} {(result.UnsubscribeSucceeded == 1 ? "map was" : "maps were")} unsubscribed from Steam Workshop";
            return new(count == 1 ? "Map removed" : "Maps removed", message + ".", ToastSeverity.Success);
        }
        var details = new List<string>();
        if (result.LocalFailed > 0)
            details.Add($"{result.LocalFailed} local {(result.LocalFailed == 1 ? "removal" : "removals")} failed");
        if (result.UnsubscribeFailed > 0)
            details.Add(result.UnsubscribeResults.All(item => item.Status == WorkshopUnsubscribeStatus.Unavailable)
                ? "Steam Workshop unsubscribe was unavailable"
                : $"{result.UnsubscribeFailed} Workshop {(result.UnsubscribeFailed == 1 ? "unsubscribe" : "unsubscribes")} failed");
        return new("Removed with warnings", $"{subject} removed from the game; {string.Join("; ", details)}. See Activity for details.", ToastSeverity.Warning);
    }
}
