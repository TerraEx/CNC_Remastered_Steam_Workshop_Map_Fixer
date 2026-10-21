using System.Threading;

namespace CncRemasterWorkshopFixer;

public sealed class SingleInstanceGuard : IDisposable
{
    private static readonly object OwnedNamesLock = new();
    private static readonly HashSet<string> OwnedNames = new(StringComparer.Ordinal);
    private readonly Mutex _mutex;
    private readonly string _mutexName;
    private bool _ownsMutex;

    public SingleInstanceGuard(string mutexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        _mutexName = mutexName;
        _mutex = new Mutex(false, mutexName);
        try
        {
            _ownsMutex = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // An abandoned mutex is available to this process and is not evidence
            // that another application instance is still running.
            _ownsMutex = true;
        }

        if (_ownsMutex)
        {
            lock (OwnedNamesLock)
            {
                if (!OwnedNames.Add(_mutexName))
                {
                    // Mutex ownership is recursive for the same Windows thread.
                    // Keep the first guard as the owner when a second guard is
                    // constructed in-process.
                    _mutex.ReleaseMutex();
                    _ownsMutex = false;
                }
            }
        }
    }

    public bool IsOwner => _ownsMutex;

    public void Dispose()
    {
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
            lock (OwnedNamesLock) OwnedNames.Remove(_mutexName);
            _ownsMutex = false;
        }
        _mutex.Dispose();
    }
}
