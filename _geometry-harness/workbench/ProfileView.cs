using HousecarlCore;

namespace Workbench;

/// <summary>SkyWright's own index of a write target's load order, for the reads the app answers itself (the context
/// panel's doors): the same plugins in the same order the in-process houseCARL service reads for that target (its
/// profile's lists through <see cref="Mo2LoadOrder"/>, explicit Data and mods folders, no overwrite layer), kept fresh
/// the same way: the profile's lists are read again on every capture, a changed plugin list builds a new index, and a
/// changed plugin file is re-read (<see cref="LoadOrderResolver.RefreshIfStale"/>).</summary>
internal sealed class ProfileView : IDisposable
{
    readonly WriteTarget _target;
    readonly object _gate = new();
    LoadOrderResolver? _resolver;
    IReadOnlyList<string> _paths = Array.Empty<string>();

    internal ProfileView(WriteTarget target) => _target = target;

    internal WriteTarget Target => _target;

    /// <summary>The target's ordered plugin files, read now, as the service reads them.</summary>
    internal IReadOnlyList<string> PathsNow()
    {
        var order = Mo2LoadOrder.Build(_target.ProfileDir, _target.ModsDir, _target.DataDir);
        if (order.OrderedPaths.Count == 0)
            throw new InvalidOperationException($"the load order of {_target} lists no active plugins ({string.Join("; ", order.Warnings.DefaultIfEmpty("its plugins.txt and loadorder.txt list nothing"))}), so nothing near a reference can be read.");
        return order.OrderedPaths;
    }

    /// <summary>One captured build of the load order as it is on disk now.</summary>
    internal LoadOrderResolver.IndexView Capture()
    {
        var paths = PathsNow();
        lock (_gate)
        {
            if (_resolver is null || !paths.SequenceEqual(_paths, StringComparer.OrdinalIgnoreCase))
            {
                _resolver?.Dispose();
                _resolver = LoadOrderResolver.Build(paths);
                _paths = paths;
            }
            else _resolver.RefreshIfStale();
            return _resolver.Capture();
        }
    }

    public void Dispose()
    {
        lock (_gate) { _resolver?.Dispose(); _resolver = null; }
    }
}
