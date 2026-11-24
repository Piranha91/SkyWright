using HousecarlCore;
using HousecarlMcp;
using HarnessShared;

namespace Workbench;

/// <summary>houseCARL's own tool code in this process for one write target (<see cref="HousecarlCalls"/>): calls run off
/// the calling thread and never on a window's thread; the service is closed only when no call is under way, and a
/// call after that is refused in one sentence.</summary>
internal sealed class HousecarlLibrary : IDisposable
{
    readonly LoadOrderService _svc;
    // calls share it; Dispose takes it alone, so the service is never closed under a call, nor used after
    readonly ReaderWriterLockSlim _use = new();
    int _disposed;

    /// <summary>What answers the calls (<see cref="HousecarlCalls.Identity"/>).</summary>
    internal static string Identity => HousecarlCalls.Identity;

    /// <summary>The houseCARL server assembly this process loaded.</summary>
    internal static string AssemblyPath => HousecarlCalls.AssemblyPath;

    HousecarlLibrary(LoadOrderService svc) => _svc = svc;

    /// <summary>A service over the target's explicit Data, mods and profile folders, its store and spilled results in
    /// <paramref name="dataDir"/>; one rulebook per process.</summary>
    internal static HousecarlLibrary Open(WriteTarget target, string dataDir, string corpusPath)
        => new(HousecarlCalls.Open(target.DataDir, target.ModsDir, target.ProfileDir, dataDir, corpusPath));

    /// <summary>One tool call, run off the calling thread so a window never waits on houseCARL.</summary>
    internal Task<string> CallAsync(string tool, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
        => Task.Run(() => Invoke(tool, args, ct), ct);

    /// <summary>The call itself (<see cref="HousecarlCalls.Invoke"/>); refused on a window's thread, and after <see cref="Dispose"/>.</summary>
    internal string Invoke(string tool, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
    {
        if (OnWindowThread())
            throw new InvalidOperationException($"houseCARL's {tool} was called on a window's thread, where it would freeze the window; this is a fault in SkyWright, not in your load order.");
        _use.EnterReadLock();
        try
        {
            if (_disposed != 0)
                throw new InvalidOperationException($"houseCARL's {tool} was called after its profile was closed; open the profile again.");
            return HousecarlCalls.Invoke(_svc, tool, args, ct);
        }
        finally { _use.ExitReadLock(); }
    }

    /// <summary>A fingerprint of houseCARL's code (<see cref="HousecarlCalls.IdentityOf"/>).</summary>
    internal static string IdentityOf(params string[] assemblies) => HousecarlCalls.IdentityOf(assemblies);

    /// <summary>A window's thread: a single-threaded apartment with a WPF dispatcher. A pool thread is never one, even when
    /// some code has attached a dispatcher to it (Dispatcher.CurrentDispatcher on a pool thread does that, for good).</summary>
    internal static bool OnWindowThread() =>
        Thread.CurrentThread.GetApartmentState() == ApartmentState.STA && System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread) is not null;

    /// <summary>Waits for the calls under way, then closes the service; a later call is refused in one sentence. Refused
    /// on a window's thread, which must not wait on a read or an index build (<see cref="HousecarlWriters.RetireAll"/>
    /// closes on the pool).</summary>
    public void Dispose()
    {
        if (OnWindowThread())
            throw new InvalidOperationException("houseCARL's service was closed on a window's thread, where it would freeze the window until its calls end; this is a fault in SkyWright.");
        _use.EnterWriteLock();
        try { if (_disposed != 0) return; _disposed = 1; _svc.Dispose(); }
        finally { _use.ExitWriteLock(); }
    }
}
