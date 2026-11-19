using HousecarlCore;

namespace Workbench;

/// <summary>The record index of each load order the window has opened (<see cref="WorldIndex"/>), built once per load
/// order on a background task and kept for the session: the World tab's tree and the Results tab's row places read it.</summary>
internal static class LoadOrderIndexes
{
    const int Keep = 6;                                    // a results pair's two setups, a browsed load order, and room to go back
    static readonly object Gate = new();
    static readonly List<(string Key, Task<WorldIndex> Index)> Built = new();

    /// <summary>The load order's plugin files, winner last, read by the owner viewprobe reads them with: the profile's
    /// own lists, or the candidate order file resolved through that profile. Refused in one sentence.</summary>
    internal static IReadOnlyList<string> PathsOf(LoadOrderSpec o)
    {
        string profileDir = Path.Combine(o.Instance, "profiles", o.Profile), mods = Path.Combine(o.Instance, "mods"),
               overwrite = Path.Combine(o.Instance, "overwrite"), data = Path.Combine(o.Game, "Data");
        if (!Directory.Exists(profileDir)) throw new InvalidOperationException($"the profile folder '{profileDir}' does not exist, so its load order cannot be read.");
        if (!Directory.Exists(data)) throw new InvalidOperationException($"'{o.Game}' has no Data folder, so the load order's plugins cannot be found.");
        if (o.OrderFile is null)
        {
            var r = Mo2LoadOrder.Build(profileDir, mods, data, overwrite);
            if (r.OrderedPaths.Count == 0)
                throw new InvalidOperationException($"the load order of {o.Label} is empty ({string.Join("; ", r.Warnings.DefaultIfEmpty("its plugins.txt and loadorder.txt list nothing"))}).");
            return r.OrderedPaths;
        }
        if (!File.Exists(o.OrderFile)) throw new InvalidOperationException($"the order file '{o.OrderFile}' does not exist, so {o.Label} cannot be read.");
        var cfg = HarnessShared.HarnessOrderCli.Resolve(o.Profile, profileDir, mods, data, overwrite, o.Game, o.OrderFile, null, null);
        if (cfg.Refused) throw new InvalidOperationException(cfg.RefusedReason ?? $"the order file '{o.OrderFile}' was refused.");
        return cfg.OrderedPaths;
    }

    /// <summary>The index of a load order: the one already built or being built, else a new build. A build that failed
    /// is not kept, so asking again tries again.</summary>
    internal static Task<WorldIndex> For(LoadOrderSpec o, IProgress<string>? say = null)
    {
        var key = string.Join("|", Path.GetFullPath(o.Instance), o.Profile, Path.GetFullPath(o.Game), o.OrderFile is null ? "" : Path.GetFullPath(o.OrderFile)).ToLowerInvariant();
        lock (Gate)
        {
            Built.RemoveAll(b => b.Index.IsFaulted || b.Index.IsCanceled);
            int at = Built.FindIndex(b => b.Key == key);
            if (at >= 0) { var hit = Built[at]; Built.RemoveAt(at); Built.Add(hit); return hit.Index; }
            var task = Task.Run(async () =>
            {
                var paths = PathsOf(o);
                say?.Report($"reading {paths.Count} plugin(s) of {o.Label}");
                var corpus = Environment.GetEnvironmentVariable("WB_CORPUS") is { Length: > 0 } cp ? cp : Program.DefaultCorpus();
                return await WorldIndex.BuildAsync(paths, say, CancellationToken.None, corpus);
            });
            Built.Add((key, task));
            // the oldest is forgotten, not disposed: a tab may still be showing it, and it holds no file open
            while (Built.Count > Keep) Built.RemoveAt(0);
            return task;
        }
    }
}
