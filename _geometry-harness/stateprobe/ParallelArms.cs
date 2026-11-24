using System.Runtime.ExceptionServices;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

// THE ARMS ACROSS CORES (--threads): each arm's work runs on a worker, and its result is merged on the calling thread in
// arm order, so the rows, their order and every total come out as a sequential run writes them.
static class ParallelArms
{
    const int WorkerStackBytes = 16 * 1024 * 1024;

    /// <summary>Run <paramref name="work"/> for every arm and <paramref name="merge"/> each result in arm order. One thread or
    /// fewer: the plain sequential loop on the calling thread.</summary>
    public static void Run<T>(IReadOnlyList<StateArm> arms, int threads, Func<StateArm, T> work, Action<int, T> merge)
    {
        if (threads <= 1 || arms.Count <= 1)
        {
            for (int i = 0; i < arms.Count; i++) merge(i, work(arms[i]));
            return;
        }
        // Arms of one name share the name-keyed memos, whose first caller decides, so they stay one unit in arm order.
        var units = Enumerable.Range(0, arms.Count).GroupBy(i => arms[i].Name, StringComparer.Ordinal).Select(g => g.ToArray()).ToArray();
        var slots = new T[arms.Count];
        var ready = new bool[arms.Count];
        var gate = new object();
        ExceptionDispatchInfo? failure = null;
        int next = -1;
        void Worker()
        {
            try
            {
                for (int u = Interlocked.Increment(ref next); u < units.Length && Volatile.Read(ref failure) is null; u = Interlocked.Increment(ref next))
                    foreach (int i in units[u])
                    {
                        var r = work(arms[i]);
                        lock (gate) { slots[i] = r; ready[i] = true; Monitor.PulseAll(gate); }
                    }
            }
            catch (Exception ex)
            {
                lock (gate) { failure ??= ExceptionDispatchInfo.Capture(ex); Monitor.PulseAll(gate); }
            }
        }
        var pool = Enumerable.Range(0, threads).Select(n => new Thread(Worker, WorkerStackBytes) { IsBackground = true, Name = $"state-arm-{n}" }).ToArray();
        foreach (var t in pool) t.Start();
        for (int i = 0; i < arms.Count; i++)
        {
            T r;
            lock (gate)
            {
                while (!ready[i] && failure is null) Monitor.Wait(gate);
                if (!ready[i]) break;
                r = slots[i]; slots[i] = default!;
            }
            merge(i, r);
        }
        foreach (var t in pool) t.Join();
        failure?.Throw();
    }

    /// <summary>The first reference of an enable-chain cycle whose answers depend on where the chain is entered, or null when no
    /// cycle has that property. A memo shared between arms fixes such a cycle's answers at its first query, so arm order decides them.</summary>
    public static FormKey? ChainCycleOrderMatters(IEnumerable<FormKey> keys, Func<FormKey, EnableFacts?> facts)
    {
        var done = new HashSet<FormKey>();
        foreach (var start in keys)
        {
            var path = new List<FormKey>();
            var at = new Dictionary<FormKey, int>();
            var k = start;
            bool closed = false;
            while (k.ID != EnableState.PlayerRefId && !done.Contains(k))
            {
                if (at.ContainsKey(k)) { closed = true; break; }
                at[k] = path.Count; path.Add(k);
                if (facts(k)?.Parent is not { } parent) break;
                k = parent;
            }
            if (closed)
            {
                var cycle = path.Skip(at[k]).ToList();
                EnableResolution[]? first = null;
                foreach (var entry in cycle)
                {
                    var memo = new Dictionary<FormKey, EnableResolution>();
                    EnableState.Resolve(entry, facts, memo);
                    var answers = cycle.Select(c => memo[c]).ToArray();
                    if (first is null) first = answers;
                    else if (!answers.SequenceEqual(first)) return entry;
                }
            }
            done.UnionWith(path);
        }
        return null;
    }
}

/// <summary>One cell's navmesh index and its walkable graph, each built once, the graph only when a route first needs it.</summary>
sealed class NavCell
{
    public NavmeshIndex Index { get; }
    public Lazy<Dictionary<NavGraph.TriRef, NavGraph.Node>> Graph { get; }
    public NavCell(NavmeshIndex index) { Index = index; Graph = new(() => NavGraph.Build(index).Nodes); }
}
