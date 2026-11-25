using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

namespace ViewProbe;

public sealed record CellBoundsFact(Vec3 Min, Vec3 Max);
public sealed record GridCellFact(int X, int Y, FormKey Key, string? EditorId);
public sealed record WaterTruthFact(int X, int Y, bool Has, float Height, FormKey? Water);

/// <summary>
/// A typed, copied observation of the process-level tables after the real record walks. It is
/// intentionally smaller than the future prepared-cell value: this first RED proves the actual
/// acquisition lifetime and that the named survivors can cross the disposed-overlay boundary as
/// owned CLR values. No Mutagen getter is accepted by this constructor.
/// </summary>
public sealed record CellFactsProbe(
    string Cell,
    KeyValuePair<FormKey, FormKey>[] RefCellOf,
    string[] NavVersionPlugins,
    GridCellFact[] CellAtGrid,
    WaterTruthFact[] CellWaterTruth,
    FormKey? WsWaterTruth,
    float WsDefaultWaterHeightTruth,
    string StationWorldspaceEd,
    KeyValuePair<FormKey, string>[] BaseType,
    KeyValuePair<FormKey, CellBoundsFact>[] BaseObnd,
    string WatchSelectionKey)
{
    public string ContentHash()
    {
        // Mutagen FormKey and ValueTuple expose fields that System.Text.Json omits by default.
        // Project every member explicitly so a green sensitivity check proves content, not merely
        // that a 64-character digest was produced.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Cell,
            RefCellOf = RefCellOf.Select(x => new { Key = x.Key.ToString(), Value = x.Value.ToString() }),
            NavVersionPlugins,
            CellAtGrid = CellAtGrid.Select(x => new { x.X, x.Y, Key = x.Key.ToString(), x.EditorId }),
            CellWaterTruth = CellWaterTruth.Select(x => new { x.X, x.Y, x.Has, x.Height, Water = x.Water?.ToString() }),
            WsWaterTruth = WsWaterTruth?.ToString(), WsDefaultWaterHeightTruth, StationWorldspaceEd,
            BaseType = BaseType.Select(x => new { Key = x.Key.ToString(), x.Value }),
            BaseObnd = BaseObnd.Select(x => new { Key = x.Key.ToString(),
                Min = new[] { x.Value.Min.X, x.Value.Min.Y, x.Value.Min.Z },
                Max = new[] { x.Value.Max.X, x.Value.Max.Y, x.Value.Max.Z } }),
            WatchSelectionKey,
        });
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    internal static bool CopyAndHashSelfCheck()
    {
        var mod = ModKey.FromNameAndExtension("FactsRed.esp");
        var a = new FormKey(mod, 1); var b = new FormKey(mod, 2);
        var refCell = new Dictionary<FormKey, FormKey> { [a] = b };
        var nav = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A.esp" };
        var grid = new Dictionary<(int, int), (FormKey, string?)> { [(1, 2)] = (a, "Cell") };
        var water = new Dictionary<(int, int), (bool, float, FormKey?)> { [(1, 2)] = (true, 3f, b) };
        CellFactsProbe Make() => new("Cell", refCell.ToArray(), nav.Order().ToArray(),
            grid.Select(x => new GridCellFact(x.Key.Item1, x.Key.Item2, x.Value.Item1, x.Value.Item2)).ToArray(),
            water.Select(x => new WaterTruthFact(x.Key.Item1, x.Key.Item2, x.Value.Item1, x.Value.Item2, x.Value.Item3)).ToArray(),
            a, 4f, "World", new[] { new KeyValuePair<FormKey, string>(a, "Static") },
            new[] { new KeyValuePair<FormKey, CellBoundsFact>(a, new(new(1, 2, 3), new(4, 5, 6))) },
            new string('A', 64));
        var snapshot = Make(); string before = snapshot.ContentHash();
        refCell.Clear(); nav.Clear(); grid.Clear(); water.Clear();
        bool sourceMutationSafe = snapshot.ContentHash() == before;
        var variants = new CellFactsProbe[]
        {
            snapshot with { RefCellOf = Array.Empty<KeyValuePair<FormKey,FormKey>>() },
            snapshot with { NavVersionPlugins = new[] { "B.esp" } },
            snapshot with { CellAtGrid = new[] { snapshot.CellAtGrid[0] with { EditorId = "Other" } } },
            snapshot with { CellWaterTruth = new[] { snapshot.CellWaterTruth[0] with { Height = 9f } } },
            snapshot with { WsWaterTruth = b }, snapshot with { WsDefaultWaterHeightTruth = 10f },
            snapshot with { StationWorldspaceEd = "OtherWorld" },
            snapshot with { BaseType = Array.Empty<KeyValuePair<FormKey,string>>() },
            snapshot with { BaseObnd = Array.Empty<KeyValuePair<FormKey,CellBoundsFact>>() },
        };
        return sourceMutationSafe && variants.All(x => x.ContentHash() != before);
    }
}

/// <summary>Test-only event sink enabled by VP_CELL_FACTS_RED_OUT. It observes the real worklist
/// path but has no effect when the variable is absent.</summary>
public static class CellFactsRed
{
    sealed record Acquisition(int Lease, string Cell, string Hash, string WatchSelectionKey);
    static readonly object Gate = new();
    static readonly List<Acquisition> Acquisitions = new();
    static readonly HashSet<int> Disposals = new();
    static readonly Dictionary<int, (int Prepass, int Main)> Walks = new();
    static bool FaultFired;
    static string? CaughtFault;
    static string? CaughtUnexpectedFault;
    static readonly string? Output = Environment.GetEnvironmentVariable("VP_CELL_FACTS_RED_OUT");

    internal static void Acquired(CellScene lease, CellFactsProbe facts)
    {
        if (Output is null) return;
        lock (Gate)
        {
            Acquisitions.Add(new(RuntimeHelpers.GetHashCode(lease), facts.Cell,
                facts.ContentHash(), facts.WatchSelectionKey));
            Write();
        }
    }

    internal static void Disposed(CellScene lease)
    {
        if (Output is null) return;
        lock (Gate) { Disposals.Add(RuntimeHelpers.GetHashCode(lease)); Write(); }
    }

    internal static void Walk(CellScene? lease, bool prepass)
    {
        if (Output is null || lease is null) return;
        lock (Gate)
        {
            int id = RuntimeHelpers.GetHashCode(lease);
            var cur = Walks.GetValueOrDefault(id);
            Walks[id] = prepass ? (cur.Prepass + 1, cur.Main) : (cur.Prepass, cur.Main + 1);
            Write();
        }
    }

    public sealed class ExpectedFault : Exception
    { public ExpectedFault() : base("CELL_FACTS_RED_AFTER_PUBLISH") { } }

    public static void ThrowAfterPublishIfRequested()
    {
        if (Output is null || Environment.GetEnvironmentVariable("VP_CELL_FACTS_RED_FAIL_AFTER_PUBLISH") != "1") return;
        lock (Gate) { FaultFired = true; Write(); }
        throw new ExpectedFault();
    }

    public static void ThrowUnexpectedAfterPublishIfRequested()
    {
        if (Output is null || Environment.GetEnvironmentVariable("VP_CELL_FACTS_RED_FAIL_UNEXPECTED") != "1") return;
        throw new InvalidOperationException("CELL_FACTS_RED_UNEXPECTED_AFTER_PUBLISH");
    }

    public static bool IsExpectedFault(Exception ex) => Output is not null && ex is ExpectedFault;
    public static bool TestBoundaryEnabled => Output is not null;

    public static void CaughtExpectedFault(Exception ex)
    {
        lock (Gate) { CaughtFault = ex.ToString(); Write(); }
    }

    public static void CaughtUnexpected(Exception ex)
    {
        lock (Gate) { CaughtUnexpectedFault = ex.ToString(); Write(); }
    }

    public static bool Finish()
    {
        if (Output is null) return true;
        lock (Gate) { return Write(); }
    }

    static bool Write()
    {
        var perLease = Acquisitions.GroupBy(x => x.Lease).ToArray();
        bool acquired = Acquisitions.Count > 0;
        bool retainedOnce = acquired && perLease.All(g => g.Count() == 1);
        bool copiedReadable = acquired && Acquisitions.All(x => x.Hash.Length == 64);
        bool disposed = acquired && perLease.All(g => Disposals.Contains(g.Key));
        bool watchKeyPresent = acquired && Acquisitions.All(x => x.WatchSelectionKey.Length == 64);
        bool walkedOnce = acquired && perLease.All(g => Walks.GetValueOrDefault(g.Key) == (1, 1));
        bool copySensitive = CellFactsProbe.CopyAndHashSelfCheck();
        bool faultRequested = Environment.GetEnvironmentVariable("VP_CELL_FACTS_RED_FAIL_AFTER_PUBLISH") == "1";
        bool caughtFault = !faultRequested || (FaultFired && CaughtFault is not null
            && CaughtFault.Contains("CELL_FACTS_RED_AFTER_PUBLISH", StringComparison.Ordinal));
        bool unexpectedRequested = Environment.GetEnvironmentVariable("VP_CELL_FACTS_RED_FAIL_UNEXPECTED") == "1";
        bool caughtUnexpected = !unexpectedRequested || (CaughtUnexpectedFault is not null
            && CaughtUnexpectedFault.Contains("CELL_FACTS_RED_UNEXPECTED_AFTER_PUBLISH", StringComparison.Ordinal));
        var verdicts = new Dictionary<string, bool>
        {
            ["actualWalkPublishedCopiedFacts"] = copiedReadable,
            ["oneFactsAcquisitionPerCell"] = retainedOnce,
            ["onePrepassAndMainWalkPerCell"] = walkedOnce,
            ["actualCellFinallyDisposedFactsLease"] = disposed,
            ["filteredWalkSelectionIsKeyed"] = watchKeyPresent,
            ["copiedFactsIgnoreSourceMutationAndHashEveryField"] = copySensitive,
            ["intentionalExceptionCaughtByTestBoundary"] = caughtFault,
            ["unexpectedExceptionCaughtByTestBoundary"] = caughtUnexpected,
        };
        var doc = new
        {
            notMet = verdicts.Where(x => !x.Value).Select(x => x.Key).ToArray(),
            verdicts,
            acquisitions = Acquisitions,
            disposedLeaseIds = Disposals.Order().ToArray(),
            perLeaseAcquisitionCounts = perLease.ToDictionary(g => g.Key.ToString(), g => g.Count()),
            walkCounts = Walks.ToDictionary(x => x.Key.ToString(), x => new { x.Value.Prepass, x.Value.Main }),
            fault = new { requested = faultRequested, fired = FaultFired, caught = CaughtFault,
                          unexpectedRequested, unexpectedCaught = CaughtUnexpectedFault },
            pendingG6 = new[] { "after-static-install fault", "borrowed NPC return and GL context disposal order" },
        };
        var path = Path.GetFullPath(Output!);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        return verdicts.Values.All(x => x);
    }

    internal static string HashWatchSelection(IEnumerable<FormKey> npcs, IEnumerable<FormKey> doors,
                                               IEnumerable<string> navPlugins)
    {
        string text = string.Join("\n", npcs.Select(x => "npc:" + x).Order()) + "\n"
                    + string.Join("\n", doors.Select(x => "door:" + x).Order()) + "\n"
                    + string.Join("\n", navPlugins.Select(x => "nav:" + x).Order(StringComparer.OrdinalIgnoreCase));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
