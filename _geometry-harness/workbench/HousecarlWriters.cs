namespace Workbench;

/// <summary>The one owner of the app's houseCARL writers: one in-process writer per write target, shared by the scene,
/// the navmesh repair desk and the context reads, so a target's load order is indexed once. Callers ask for the writer
/// of the target they have now on every use and never keep or close one. A profile change retires every writer: each is
/// closed off the calling thread (never on a window's), and a call still holding one is refused in one sentence.</summary>
internal static class HousecarlWriters
{
    static readonly object Gate = new();
    static readonly Dictionary<WriteTarget, PatchWriter> Open = new();

    /// <summary>The writer for <paramref name="target"/>: the open one, else a new one keeping its houseCARL data in
    /// <paramref name="dataDir"/>. Refused, as <see cref="PatchWriter.OpenInProcess"/> refuses, before anything opens.</summary>
    internal static PatchWriter For(WriteTarget target, string dataDir, string corpus, Action<string> log)
    {
        lock (Gate)
        {
            if (Open.TryGetValue(target, out var w)) return w;
            w = PatchWriter.OpenInProcess(target, dataDir, corpus, log);
            Open[target] = w;
            return w;
        }
    }

    /// <summary>True when this owner opened <paramref name="w"/> (and so closes it).</summary>
    internal static bool Owns(PatchWriter w) { lock (Gate) return Open.ContainsValue(w); }

    /// <summary>Closes every writer (a profile change, the app closing); the next use opens the new target's. Returns the
    /// close, which runs on the pool: it waits for calls under way, and a window must not.</summary>
    internal static Task RetireAll()
    {
        List<PatchWriter> old;
        lock (Gate) { old = Open.Values.ToList(); Open.Clear(); }
        return old.Count == 0 ? Task.CompletedTask : Task.Run(async () => { foreach (var w in old) await w.DisposeAsync(); });
    }
}
