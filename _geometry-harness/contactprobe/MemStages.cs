using HarnessShared;

/// <summary>HC_MEMSTAGES (diagnostic, off unless the variable is set): the live heap after a compacting full collection at named
/// points of the default pass, with what the pass holds there. Unset, it reads nothing, collects nothing and prints nothing.</summary>
static class MemStages
{
    public static readonly bool On = Environment.GetEnvironmentVariable("HC_MEMSTAGES") is not null;

    public static void Print(string stage, ContactIndex cidx, int rows, int hostsDone)
    {
        if (!On) return;
        // Compacting, both generations and the large-object heap, twice around the finalizers: a plain collection leaves
        // the heap committed and reads high (project memory: memory guards need a compacting GC).
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long live = GC.GetTotalMemory(false);
        var (bases, soupBytes) = cidx.GeometryMemo();
        var kinds = string.Join(", ", cidx.Budget.BytesNow().Select(k => $"{k.Name} {k.Bytes / 1e9:0.000}"));
        Console.WriteLine($"   [memstages] {stage}: live {live / 1e9:0.000} GB; geometry memo {bases:N0} bases, soups {soupBytes / 1e9:0.000} GB; "
                        + $"budget {cidx.Budget.Bytes / 1e9:0.000} of {cidx.Budget.Limit / 1e9:0.000} GB ({kinds}); "
                        + $"enable memos {cidx.Arms.MemoEntries:N0}; rows {rows:N0}; hosts {hostsDone:N0}; working set {Environment.WorkingSet / 1e9:0.000} GB");
    }
}
