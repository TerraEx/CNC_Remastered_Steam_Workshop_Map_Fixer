using System.IO;
using Steamworks;

namespace CncRemasterWorkshopFixer;

// Only this adapter knows Steamworks.NET types.
public interface ISteamWorkshopApi
{
    bool Init();
    uint GetAppId();
    IDisposable? Unsubscribe(ulong id, Action<WorkshopUnsubscribeResult> completed);
    void RunCallbacks();
    void Shutdown();
}

internal sealed class SteamWorkshopApi : ISteamWorkshopApi
{
    public bool Init() => SteamAPI.Init();
    public uint GetAppId() => SteamUtils.GetAppID().m_AppId;
    public void RunCallbacks() => SteamAPI.RunCallbacks();
    public void Shutdown() => SteamAPI.Shutdown();

    public IDisposable? Unsubscribe(ulong id, Action<WorkshopUnsubscribeResult> completed)
    {
        var call = SteamUGC.UnsubscribeItem(new PublishedFileId_t(id));
        if (call == SteamAPICall_t.Invalid) return null;
        // Confirm the requested item as well as OK; a callback alone isn't success.
        // Item-state bits can lag the callback.
        var result = CallResult<RemoteStorageUnsubscribePublishedFileResult_t>.Create((response, ioFailure) =>
        {
            var succeeded = !ioFailure && response.m_eResult == EResult.k_EResultOK && response.m_nPublishedFileId.m_PublishedFileId == id;
            completed(new(id, succeeded ? WorkshopUnsubscribeStatus.Succeeded : WorkshopUnsubscribeStatus.Failed,
                succeeded ? null : ioFailure ? "Steam reported an I/O failure."
                    : $"Steam returned {response.m_eResult} for Workshop ID {response.m_nPublishedFileId.m_PublishedFileId}."));
        });
        try { result.Set(call); return result; }
        catch { result.Dispose(); throw; }
    }
}

public sealed class SteamWorkshopSubscriptionService : ISteamWorkshopSubscriptionService
{
    // Steam initialisation and the working directory are process-wide, even when
    // callers use separate service instances. Keep the whole operation serialised.
    private static readonly SemaphoreSlim OperationGate = new(1, 1);
    private readonly ISteamWorkshopApi _api;
    private readonly Func<string?> _locateRunDirectory;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _clock;

    public SteamWorkshopSubscriptionService(ISteamWorkshopApi? api = null, Func<string?>? locateRunDirectory = null,
        TimeSpan? timeout = null, TimeProvider? clock = null)
    {
        _api = api ?? new SteamWorkshopApi();
        _locateRunDirectory = locateRunDirectory ?? CncSteamContextLocator.DiscoverRunDirectory;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        _clock = clock ?? TimeProvider.System;
    }

    public Task<IReadOnlyList<WorkshopUnsubscribeResult>> UnsubscribeAsync(
        IReadOnlyList<ulong> workshopIds, IProgress<string>? progress = null) =>
        Task.Run(async () => await ExecuteAsync(workshopIds, progress).ConfigureAwait(false));

    private async Task<IReadOnlyList<WorkshopUnsubscribeResult>> ExecuteAsync(
        IReadOnlyList<ulong> workshopIds, IProgress<string>? progress)
    {
        var ids = workshopIds.Where(MapRemovalOperationResult.IsUsableWorkshopId).Distinct().ToList();
        if (ids.Count == 0) return [];
        await OperationGate.WaitAsync().ConfigureAwait(false);
        var originalDirectory = Environment.CurrentDirectory;
        var initialized = false;
        var calls = new List<IDisposable>();
        var results = new Dictionary<ulong, WorkshopUnsubscribeResult>();
        try
        {
            var runDirectory = _locateRunDirectory();
            if (runDirectory is null || !CncSteamContextLocator.IsValidRunDirectory(runDirectory))
                throw new InvalidOperationException("The installed C&C Remastered Steam context (steam_appid.txt) could not be found or validated.");
            Environment.CurrentDirectory = runDirectory;
            initialized = _api.Init();
            if (!initialized) throw new InvalidOperationException("Steam could not initialise. Start Steam, sign in, and retry.");
            if (_api.GetAppId() != SteamWorkshopDiscovery.CncRemasteredAppId)
                throw new InvalidOperationException("Steam initialised with a different App ID; no unsubscribe requests were sent.");

            // One deadline covers the batch, so a missing callback doesn't add
            // another full timeout for every map in a large removal.
            var started = _clock.GetTimestamp();
            foreach (var id in ids)
            {
                try
                {
                    var call = _api.Unsubscribe(id, result => results.TryAdd(id, result));
                    if (call is null) results[id] = new(id, WorkshopUnsubscribeStatus.Failed, "Steam returned an invalid unsubscribe call handle.");
                    // CallResult registrations must stay alive until callbacks finish.
                    else calls.Add(call);
                }
                catch (Exception ex) { results[id] = new(id, WorkshopUnsubscribeStatus.Failed, ex.Message); }
            }
            while (results.Count < ids.Count && _clock.GetElapsedTime(started) < _timeout)
            {
                _api.RunCallbacks();
                progress?.Report($"Unsubscribing {results.Count} of {ids.Count} maps…");
                if (results.Count < ids.Count) await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
            }
            foreach (var id in ids.Where(id => !results.ContainsKey(id)))
                results[id] = new(id, WorkshopUnsubscribeStatus.TimedOut, "Steam did not confirm unsubscribe within the overall timeout; subscription state is unconfirmed.");
        }
        catch (Exception ex)
        {
            foreach (var id in ids.Where(id => !results.ContainsKey(id)))
                results[id] = new(id, initialized ? WorkshopUnsubscribeStatus.Failed : WorkshopUnsubscribeStatus.Unavailable, ex.Message);
        }
        finally
        {
            try
            {
                foreach (var call in calls) call.Dispose();
            }
            finally
            {
                // Steam can track this process after API shutdown.
                // Shutdown releases API resources; Steam owns later disk cleanup,
                // which may wait for process exit and the client's update queue.
                try { if (initialized) _api.Shutdown(); }
                finally
                {
                    try { Environment.CurrentDirectory = originalDirectory; }
                    finally { OperationGate.Release(); }
                }
            }
        }
        return ids.Select(id => results[id]).ToList();
    }
}
