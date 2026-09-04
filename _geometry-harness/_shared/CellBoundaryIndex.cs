using System.Numerics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HarnessShared;

// CELL BOUNDARY INDEX — every plugin's word on a cell's WATER and its LANDSCAPE, in load order, as a
// changed-only history the way PlacementHistory keeps placements (2026-09-04, roadmap 4.9).
//
// Two classes read it. The water pass (waterprobe) wants, per cell, the truth tuple (the last Bethesda
// or USSEP contributor), the pre-Update tuple (the earliest contributor), a water mod's tuple when one
// exists, and the winner's — and the winning LAND to say whether the winner's surface is under the
// ground. The seam pass (seamprobe) wants, per pair of edge-adjacent cells, the two winning heightmaps
// and the authoring view of the later plugin (PlacementHistory's rule applied to LAND). One walk fills
// both; the decisions are DetectorModel's.
//
// Who a plugin IS is settled after the walk, not during it: Bethesda, USSEP and the generated outputs
// are filenames (PluginKinds.ByName); a WATER MOD is a rule over what the plugin's records do — it
// overrides WATR records it does not define, or points cells at WATR records of its own on many cells —
// and both counts are tallied here so KindOf can apply the rule with a stated threshold.
//
// Values are COPIED out of the getters. Every overlay is opened in a `using` by the caller and disposed
// at the end of its iteration; a retained getter is a use-after-free that happens to work until it doesn't.

public static class PluginKinds
{
    /// <summary>The seven base-game files the other probes already treat as Bethesda, plus Creation Club
    /// (`cc*.es*`) — Bethesda-on-Bethesda is a population of its own (rule 12), reported apart.</summary>
    static readonly HashSet<string> Bethesda = new(StringComparer.OrdinalIgnoreCase)
    { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm", "SkyrimVR.esm", "_ResourcePack.esl" };
    public const string Ussep = "Unofficial Skyrim Special Edition Patch.esp";
    /// <summary>The fixed filenames <c>tool-output-awareness</c> lists (patchprobe carries the same set). A Synthesis
    /// output folder's other plugins are named by its pipeline and are NOT caught by name — a stated limit.</summary>
    static readonly HashSet<string> Generated = new(StringComparer.OrdinalIgnoreCase)
    { "DynDOLOD.esm", "DynDOLOD.esp", "Occlusion.esp", "PGPatcher.esp", "PG_1.esp", "Synthesis.esp", "Requiem for the Indifferent.esp", "TexGen_Output.esp" };

    public static bool IsBethesda(string fileName) => Bethesda.Contains(fileName) || fileName.StartsWith("cc", StringComparison.OrdinalIgnoreCase) && IsPluginName(fileName);
    public static bool IsUssep(string fileName) => string.Equals(fileName, Ussep, StringComparison.OrdinalIgnoreCase);
    public static bool IsGenerated(string fileName) => Generated.Contains(fileName);
    static bool IsPluginName(string f) => f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".esl", StringComparison.OrdinalIgnoreCase);

    /// <summary>By name alone: Bethesda / Ussep / Generated / Other. WaterMod is the index's call (KindOf).</summary>
    public static PluginKind ByName(string fileName)
        => IsBethesda(fileName) ? PluginKind.Bethesda : IsUssep(fileName) ? PluginKind.Ussep : IsGenerated(fileName) ? PluginKind.Generated : PluginKind.Other;
}

/// <summary>One plugin's statement of a cell's water.</summary>
public readonly record struct WaterSnap(int Plugin, DetectorModel.WaterTuple Tuple);

/// <summary>One plugin's statement of a worldspace's water: its WATR, LOD water and height, the default water
/// height its cells fall back to, and the NoLodWater flag.</summary>
public readonly record struct WorldWaterSnap(int Plugin, string? Water, string? LodWater, float? LodWaterHeight, float? DefaultWaterHeight, bool NoLodWater)
{
    public bool SameAs(WorldWaterSnap o) => string.Equals(Water ?? "", o.Water ?? "", StringComparison.OrdinalIgnoreCase)
        && string.Equals(LodWater ?? "", o.LodWater ?? "", StringComparison.OrdinalIgnoreCase)
        && DetectorModel.HeightsEqual(LodWaterHeight, o.LodWaterHeight) && DetectorModel.HeightsEqual(DefaultWaterHeight, o.DefaultWaterHeight)
        && NoLodWater == o.NoLodWater;
}

/// <summary>One plugin's statement of a cell's LAND: the VHGT offset and gradients, the base texture of each quadrant
/// (BottomLeft, BottomRight, TopLeft, TopRight — Mutagen's Quadrant order), decoded to heights on demand.</summary>
public sealed class LandSnap
{
    public int Plugin { get; }
    public float Offset { get; }
    public sbyte[,] Gradients { get; }
    public string?[] QuadBase { get; }
    float[,]? _heights;

    public LandSnap(int plugin, float offset, sbyte[,] gradients, string?[] quadBase)
    { Plugin = plugin; Offset = offset; Gradients = gradients; QuadBase = quadBase; }

    /// <summary>World-unit heights [x, y], decoded once through HousecarlCore.LandHeight.</summary>
    public float[,] Heights => _heights ??= LandHeight.Decode(Offset, Gradients);

    public bool SameHeightsAs(LandSnap o)
    {
        if (Offset != o.Offset) return false;
        for (int y = 0; y < LandHeight.Side; y++)
            for (int x = 0; x < LandHeight.Side; x++)
                if (Gradients[x, y] != o.Gradients[x, y]) return false;
        return true;
    }
    public bool SameTexturesAs(LandSnap o)
    {
        for (int q = 0; q < 4; q++)
            if (!string.Equals(QuadBase[q] ?? "", o.QuadBase[q] ?? "", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}

public sealed class CellInfo
{
    public FormKey Key { get; init; }
    public FormKey Worldspace { get; init; }          // Null for an interior
    public int GX { get; init; }
    public int GY { get; init; }
    public bool Interior => Worldspace.IsNull;
    public string? EditorId { get; set; }
    public string? Name { get; set; }
    /// <summary>The plugin that defined the record (its FormKey's mod, when loaded; else the first provider seen).</summary>
    public int Definer { get; set; } = -1;
    /// <summary>The last plugin to PROVIDE the record — the load-order winner, whether or not it changed anything.</summary>
    public int Winner { get; set; } = -1;
    public List<WaterSnap> Water { get; } = new(1);
    public List<LandSnap> Land { get; } = new(1);
    /// <summary>Providers whose LAND could not be decoded (a truncated record); counted, never guessed at.</summary>
    public int LandUnreadable { get; set; }
}

public sealed class CellBoundaryIndex
{
    readonly Dictionary<FormKey, CellInfo> _cells = new();
    readonly Dictionary<(FormKey Ws, int X, int Y), List<FormKey>> _byGrid = new();
    readonly Dictionary<FormKey, List<WorldWaterSnap>> _worlds = new();
    readonly Dictionary<FormKey, string?> _worldEdid = new();
    readonly Dictionary<FormKey, string> _watrEdid = new();
    readonly Dictionary<FormKey, int> _watrDefiner = new();
    readonly Dictionary<int, int> _foreignWatrOverrides = new();
    readonly Dictionary<int, int> _ownWatrCells = new();
    readonly Dictionary<int, string> _pluginName = new();
    readonly Dictionary<int, ModKey> _pluginKey = new();

    public IReadOnlyDictionary<FormKey, CellInfo> Cells => _cells;
    public IReadOnlyDictionary<FormKey, List<WorldWaterSnap>> Worlds => _worlds;
    public IReadOnlyDictionary<FormKey, string?> WorldEditorId => _worldEdid;
    public IReadOnlyDictionary<FormKey, string> WatrEditorId => _watrEdid;
    public IReadOnlyDictionary<FormKey, int> WatrDefiner => _watrDefiner;
    public long WaterSnaps { get; private set; }
    public long LandSnaps { get; private set; }
    public long CellProvisions { get; private set; }

    public string PluginName(int p) => p >= 0 && _pluginName.TryGetValue(p, out var n) ? n : "(none)";

    /// <summary>WATR records the plugin overrides without defining, and cells it points at WATR records of its own.</summary>
    public (int ForeignWatrOverrides, int OwnWatrCells) WaterModFacts(int p)
        => (_foreignWatrOverrides.TryGetValue(p, out var f) ? f : 0, _ownWatrCells.TryGetValue(p, out var o) ? o : 0);

    /// <summary>Who plugin <paramref name="p"/> is, for the truth rule. Names first (Bethesda / USSEP / generated), then
    /// the water-mod rule: at least one foreign WATR override, or its own WATR on at least
    /// <paramref name="ownWatrCells"/> cells. The threshold is the caller's, printed.</summary>
    public PluginKind KindOf(int p, int ownWatrCells = 10)
    {
        if (p < 0) return PluginKind.Other;
        var byName = PluginKinds.ByName(PluginName(p));
        if (byName != PluginKind.Other) return byName;
        var (foreign, own) = WaterModFacts(p);
        return foreign >= 1 || own >= ownWatrCells ? PluginKind.WaterMod : PluginKind.Other;
    }

    public IReadOnlyList<FormKey>? AtGrid(FormKey ws, int x, int y) => _byGrid.TryGetValue((ws, x, y), out var l) ? l : null;

    /// <summary>The cell at a grid point when exactly one record sits there; null when none or more than one.</summary>
    public CellInfo? Single(FormKey ws, int x, int y)
        => AtGrid(ws, x, y) is { Count: 1 } l ? _cells[l[0]] : null;

    /// <summary>Every exterior grid point with more than one CELL record — the wrinkle the harbour line showed.</summary>
    public IEnumerable<((FormKey Ws, int X, int Y) Grid, IReadOnlyList<FormKey> Keys)> DuplicateGridPoints()
        => _byGrid.Where(kv => kv.Value.Count > 1).Select(kv => (kv.Key, (IReadOnlyList<FormKey>)kv.Value));

    public void Ingest(int p, ISkyrimModGetter mod, bool land = true)
    {
        _pluginName[p] = mod.ModKey.FileName.String;
        _pluginKey[p] = mod.ModKey;

        // WATR: the definer of each, and the plugin's foreign overrides.
        foreach (var w in mod.Waters.Records)
        {
            if (!string.IsNullOrEmpty(w.EditorID)) _watrEdid[w.FormKey] = w.EditorID!;
            if (w.FormKey.ModKey == mod.ModKey) _watrDefiner.TryAdd(w.FormKey, p);
            else _foreignWatrOverrides[p] = (_foreignWatrOverrides.TryGetValue(p, out var f) ? f : 0) + 1;
        }

        // Worldspaces and their exterior cells.
        foreach (var ws in mod.Worldspaces.Records)
        {
            if (!string.IsNullOrEmpty(ws.EditorID)) _worldEdid[ws.FormKey] = ws.EditorID;
            else _worldEdid.TryAdd(ws.FormKey, null);
            var snap = new WorldWaterSnap(p,
                Key(ws.Water.FormKeyNullable), Key(ws.LodWater.FormKeyNullable),
                ws.LodWaterHeight, ws.LandDefaults?.DefaultWaterHeight,
                ws.Flags.HasFlag(Worldspace.Flag.NoLodWater));
            if (!_worlds.TryGetValue(ws.FormKey, out var wh)) _worlds[ws.FormKey] = wh = new List<WorldWaterSnap>(1);
            if (wh.Count == 0 || !wh[^1].SameAs(snap)) wh.Add(snap);

            // The persistent cell: gridless whatever its XCLC says (small worldspaces stamp it (0,0), which
            // collides with the real (0,0) cell and read as a duplicate grid point on the first run).
            if (ws.TopCell is { } tc) IngestCell(p, mod, tc, ws.FormKey, land, persistent: true);
            foreach (var blk in ws.SubCells)
                foreach (var sub in blk.Items)
                    foreach (var c in sub.Items)
                        IngestCell(p, mod, c, ws.FormKey, land);
        }

        // Interiors: water is a placed basin with no grid, but the tuple still travels with the CELL record.
        foreach (var blk in mod.Cells.Records)
            foreach (var sub in blk.SubBlocks)
                foreach (var c in sub.Cells)
                    IngestCell(p, mod, c, FormKey.Null, false);
    }

    static string? Key(FormKey? k) => k is { } f && !f.IsNull ? f.ToString() : null;

    void IngestCell(int p, ISkyrimModGetter mod, ICellGetter c, FormKey worldspace, bool land, bool persistent = false)
    {
        CellProvisions++;
        if (!_cells.TryGetValue(c.FormKey, out var info))
        {
            int gx = 0, gy = 0;
            if (!worldspace.IsNull && !persistent && c.Grid?.Point is { } gp) { gx = gp.X; gy = gp.Y; }
            else if (!worldspace.IsNull) { gx = int.MinValue; gy = int.MinValue; }   // the worldspace's persistent cell has no grid
            info = new CellInfo { Key = c.FormKey, Worldspace = worldspace, GX = gx, GY = gy };
            _cells[c.FormKey] = info;
            if (!worldspace.IsNull && gx != int.MinValue)
            {
                if (!_byGrid.TryGetValue((worldspace, gx, gy), out var l)) _byGrid[(worldspace, gx, gy)] = l = new List<FormKey>(1);
                l.Add(c.FormKey);
            }
        }
        if (info.Definer < 0 && (c.FormKey.ModKey == mod.ModKey || info.Winner < 0)) info.Definer = p;
        info.Winner = p;
        if (!string.IsNullOrEmpty(c.EditorID)) info.EditorId = c.EditorID;
        if (c.Name?.String is { Length: > 0 } nm) info.Name = nm;

        var waterKey = c.Water.FormKeyNullable;
        if (waterKey is { } wk && !wk.IsNull && wk.ModKey == mod.ModKey)
            _ownWatrCells[p] = (_ownWatrCells.TryGetValue(p, out var o) ? o : 0) + 1;
        var tuple = new DetectorModel.WaterTuple(
            c.Flags.HasFlag(Cell.Flag.HasWater), c.Flags.HasFlag(Cell.Flag.NoLodWater),
            Key(waterKey), c.WaterHeight,
            c.WaterVelocity is { } wv ? new Vector3(wv.Angle.X, wv.Angle.Y, wv.Angle.Z) : null,
            c.WaterVelocity is { } wv2 ? new Vector3(wv2.Offset.X, wv2.Offset.Y, wv2.Offset.Z) : null,
            string.IsNullOrEmpty(c.WaterNoiseTexture) ? null : c.WaterNoiseTexture,
            string.IsNullOrEmpty(c.WaterEnvironmentMap) ? null : c.WaterEnvironmentMap);
        if (info.Water.Count == 0 || DetectorModel.WaterDiff(info.Water[^1].Tuple, tuple).Count > 0)
        { info.Water.Add(new WaterSnap(p, tuple)); WaterSnaps++; }

        if (!land || info.Interior) return;
        ILandscapeGetter? ls;
        try { ls = c.Landscape; } catch { info.LandUnreadable++; return; }
        if (ls?.VertexHeightMap is not { } vh) return;
        var grad = LandHeight.Gradients(vh);
        if (grad is null) { info.LandUnreadable++; return; }
        var quad = new string?[4];
        try
        {
            foreach (var layer in ls.Layers)
            {
                if (layer is IAlphaLayerGetter) continue;
                int qi = layer.Header is { } lh ? (int)lh.Quadrant : -1;
                if (qi is >= 0 and < 4 && quad[qi] is null) quad[qi] = Key(layer.Header?.Texture.FormKeyNullable);
            }
        }
        catch { }
        var snap = new LandSnap(p, vh.Offset, grad, quad);
        if (info.Land.Count == 0 || !info.Land[^1].SameHeightsAs(snap) || !info.Land[^1].SameTexturesAs(snap))
        { info.Land.Add(snap); LandSnaps++; }
    }

    // ---- queries the rules need ----------------------------------------------------------------

    /// <summary>The last snap by a plugin in <paramref name="kinds"/> (the truth rule), or null.</summary>
    public static WaterSnap? LastOfKind(IReadOnlyList<WaterSnap> h, Func<int, PluginKind> kindOf, params PluginKind[] kinds)
    {
        for (int i = h.Count - 1; i >= 0; i--)
            if (Array.IndexOf(kinds, kindOf(h[i].Plugin)) >= 0) return h[i];
        return null;
    }

    /// <summary>The authoring view of a LAND: the last snap by <paramref name="byPlugin"/> or a plugin in its closure.</summary>
    public static LandSnap? LandAsSeenBy(IReadOnlyList<LandSnap> h, int byPlugin, IReadOnlyList<HashSet<int>> closure)
    {
        for (int i = h.Count - 1; i >= 0; i--)
            if (h[i].Plugin == byPlugin || closure[byPlugin].Contains(h[i].Plugin)) return h[i];
        return null;
    }

    /// <summary>The state before <paramref name="byPlugin"/>'s own edit: the last snap by a plugin in its closure, itself excluded.</summary>
    public static LandSnap? LandBefore(IReadOnlyList<LandSnap> h, int byPlugin, IReadOnlyList<HashSet<int>> closure)
    {
        for (int i = h.Count - 1; i >= 0; i--)
            if (h[i].Plugin != byPlugin && closure[byPlugin].Contains(h[i].Plugin)) return h[i];
        return null;
    }

    /// <summary>The worldspace's winning water statement, or null when no plugin stated the worldspace.</summary>
    public WorldWaterSnap? WorldWinner(FormKey ws) => _worlds.TryGetValue(ws, out var h) && h.Count > 0 ? h[^1] : null;

    /// <summary>The worldspace as the last plugin of <paramref name="kinds"/> left it.</summary>
    public WorldWaterSnap? WorldOfKind(FormKey ws, Func<int, PluginKind> kindOf, params PluginKind[] kinds)
    {
        if (!_worlds.TryGetValue(ws, out var h)) return null;
        for (int i = h.Count - 1; i >= 0; i--)
            if (Array.IndexOf(kinds, kindOf(h[i].Plugin)) >= 0) return h[i];
        return null;
    }

    /// <summary>A cell's tuple resolved through a worldspace statement, with the engine's precedence.</summary>
    public static DetectorModel.ResolvedWater Resolve(DetectorModel.WaterTuple t, WorldWaterSnap? ws, bool exterior = true)
    {
        string? water = t.Water ?? ws?.Water;
        // HasWater cleared on an EXTERIOR cell that defers to the worldspace default does not remove the sea: measured
        // 2026-09-04 on the constructed harbour fixture (the swimmer swam, `GetActualWaterLevel` −14,000, the frames
        // identical). The flag is honoured where the cell states its own height, which is the case NOT measured.
        bool has = t.HasWater || (exterior && ws is not null && DetectorModel.HeightKind(t.Height) is WaterHeightKind.Unset or WaterHeightKind.UnsignedWrapped
                                  && DetectorModel.HeightKind(ws.Value.DefaultWaterHeight) == WaterHeightKind.Real);
        // The engine's reading (measured 2026-09-04): +3.4028235E+38 and the wrapped 4.29e9 both defer to the worldspace
        // default (a saturating int conversion); the negative sentinels are no surface at all.
        float? z = DetectorModel.SurfaceHeight(t.Height, ws?.DefaultWaterHeight);
        return new DetectorModel.ResolvedWater(has, water, z);
    }

    /// <summary>The share of a heightmap's vertices that lie BELOW <paramref name="surfaceZ"/> — the water is visible there.</summary>
    public static float VisibleShare(float[,] heights, float surfaceZ)
    {
        int below = 0, n = LandHeight.Side * LandHeight.Side;
        for (int y = 0; y < LandHeight.Side; y++)
            for (int x = 0; x < LandHeight.Side; x++)
                if (heights[x, y] < surfaceZ) below++;
        return (float)below / n;
    }

    // ---- the fact assembly the probes and the proof share -------------------------------------
    // The DECISIONS are DetectorModel's; this is where the index turns a cell's history into the facts
    // those decisions take, so that waterprobe, seamprobe and proofprobe cannot assemble them three ways.

    /// <summary>One cell judged: the tuples, the verdict, the resolved surfaces under both arms, the visible shares, the
    /// missing-water call and its physical predicate.</summary>
    public sealed record CellWaterJudgement(CellInfo Cell, DetectorModel.WaterTuple First, DetectorModel.WaterTuple Truth, int TruthPlugin, WaterSnap? WaterMod,
        DetectorModel.WaterTuple Winner, PluginKind WinnerKind, bool AuthorTruth, bool Duplicate,
        WaterVerdict Verdict, WaterBlocker Blocker, string Why,
        DetectorModel.ResolvedWater RTruth, DetectorModel.ResolvedWater RWin, float? VisTruth, float? VisWin,
        bool Missing, PhysicalState Physical, string MissingWhy);

    /// <summary>Judge one cell's water. Null when the cell carries no water statement at all.</summary>
    public CellWaterJudgement? JudgeWater(CellInfo c, Func<int, PluginKind> kindOf, float minVisible, ISet<(FormKey, int, int)>? duplicateGrid = null)
    {
        var h = c.Water;
        if (h.Count == 0) return null;
        var first = h[0].Tuple;
        var truthSnap = LastOfKind(h, kindOf, PluginKind.Bethesda, PluginKind.Ussep);
        bool authorTruth = truthSnap is null;
        var truth = truthSnap?.Tuple ?? first;
        int truthPlugin = truthSnap?.Plugin ?? h[0].Plugin;
        var wm = LastOfKind(h, kindOf, PluginKind.WaterMod);
        var winner = h[^1].Tuple;
        var wKind = kindOf(c.Winner);
        bool dup = !c.Interior && c.GX != int.MinValue
                && (duplicateGrid?.Contains((c.Worldspace, c.GX, c.GY)) ?? (AtGrid(c.Worldspace, c.GX, c.GY)?.Count ?? 0) > 1);
        var facts = new DetectorModel.WaterFacts(truth, first, wm?.Tuple, winner, wKind, authorTruth, dup);
        var (verdict, blocker, why) = DetectorModel.ClassifyWater(facts);

        // Resolved surfaces, each arm through its own worldspace statement.
        var wsTruth = c.Interior ? null : WorldOfKind(c.Worldspace, kindOf, PluginKind.Bethesda, PluginKind.Ussep) ?? WorldWinner(c.Worldspace);
        var wsWin = c.Interior ? null : WorldWinner(c.Worldspace);
        var rTruth = Resolve(truth, wsTruth, !c.Interior);
        var rWin = Resolve(winner, wsWin, !c.Interior);
        float? visTruth = null, visWin = null;
        if (c.Land.Count > 0)
        {
            // The winning LAND under each arm: the truth arm's is the last Bethesda/USSEP LAND, the winner's the last.
            var landTruth = c.Land.LastOrDefault(l => kindOf(l.Plugin) is PluginKind.Bethesda or PluginKind.Ussep) ?? c.Land[0];
            var landWin = c.Land[^1];
            if (rTruth.Has && rTruth.Z is { } zt) visTruth = VisibleShare(landTruth.Heights, zt);
            if (rWin.Has && rWin.Z is { } zw) visWin = VisibleShare(landWin.Heights, zw);
        }
        var (missing, physical, missingWhy) = DetectorModel.MissingWater(verdict, rTruth.Has, visTruth, rWin.Has, visWin, minVisible);
        return new CellWaterJudgement(c, first, truth, truthPlugin, wm, winner, wKind, authorTruth, dup, verdict, blocker, why,
                                      rTruth, rWin, visTruth, visWin, missing, physical, missingWhy);
    }

    /// <summary>One edge-adjacent pair measured: the winners' edge, the later plugin's view, the state before its edit,
    /// the textures along the edge, and the rule's verdict.</summary>
    public sealed record LandPairMeasure(CellInfo A, CellInfo B, CellEdge EdgeOfA, CellInfo LaterCell, CellInfo OtherCell, int Later,
        float MaxDelta, int OverEps, float ViewDelta, float? PrevDelta, bool OtherSeen,
        LandSeamVerdict Verdict, SeamOrigin? Origin, SeamSight? Sight, string Why, bool TextureSeam, string TexWhy);

    /// <summary>Measure the pair (A, B) where B is A's neighbour across <paramref name="edgeOfA"/> (East: B = A + (1,0);
    /// North: B = A + (0,1)). Null when either side has no LAND.</summary>
    public static LandPairMeasure? MeasurePair(CellInfo a, CellInfo b, CellEdge edgeOfA, IReadOnlyList<HashSet<int>> closure, float eps)
    {
        if (a.Land.Count == 0 || b.Land.Count == 0) return null;
        var edgeOfB = edgeOfA switch { CellEdge.East => CellEdge.West, CellEdge.North => CellEdge.South, CellEdge.West => CellEdge.East, _ => CellEdge.North };
        var wa = a.Land[^1]; var wb = b.Land[^1];
        int later = Math.Max(wa.Plugin, wb.Plugin);
        var (laterCell, otherCell, laterEdge, otherEdge) = wa.Plugin >= wb.Plugin ? (a, b, edgeOfA, edgeOfB) : (b, a, edgeOfB, edgeOfA);
        var laterWin = laterCell.Land[^1]; var otherWin = otherCell.Land[^1];
        var win = DetectorModel.EdgeMismatch(LandHeight.Edge(laterWin.Heights, laterEdge), LandHeight.Edge(otherWin.Heights, otherEdge), eps);

        // The authoring view: the other side as the later plugin saw it; the state before the later plugin's edit.
        var otherSeen = LandAsSeenBy(otherCell.Land, later, closure);
        bool seen = otherWin.Plugin == later || closure[later].Contains(otherWin.Plugin);
        // Never saw the other side placed at all: it matched nothing, so the combination is all there is (Blind).
        float view = otherSeen is null ? 0f
                   : DetectorModel.EdgeMismatch(LandHeight.Edge(laterWin.Heights, laterEdge), LandHeight.Edge(otherSeen.Heights, otherEdge), eps).MaxDelta;
        var laterBefore = LandBefore(laterCell.Land, later, closure);
        float? prev = laterBefore is null || otherSeen is null ? null
                    : DetectorModel.EdgeMismatch(LandHeight.Edge(laterBefore.Heights, laterEdge), LandHeight.Edge(otherSeen.Heights, otherEdge), eps).MaxDelta;
        var (verdict, origin, sight, why) = DetectorModel.LandSeam(new DetectorModel.LandSeamFacts(win.MaxDelta, view, prev, seen), eps);

        // Textures along the edge: the later side's two quadrants against the other side's two. Quadrant order is
        // BottomLeft, BottomRight, TopLeft, TopRight; east is BR+TR, west BL+TL, north TL+TR, south BL+BR.
        static int[] Q(CellEdge e) => e switch { CellEdge.East => new[] { 1, 3 }, CellEdge.West => new[] { 0, 2 }, CellEdge.North => new[] { 2, 3 }, _ => new[] { 0, 1 } };
        var qa = Q(laterEdge); var qb = Q(otherEdge);
        bool texSeam = false; var texWhy = new List<string>();
        for (int k = 0; k < 2; k++)
        {
            var ta = laterWin.QuadBase[qa[k]]; var tb = otherWin.QuadBase[qb[k]];
            if (!string.Equals(ta ?? "", tb ?? "", StringComparison.OrdinalIgnoreCase)) { texSeam = true; texWhy.Add($"{ta ?? "(none)"} | {tb ?? "(none)"}"); }
        }
        return new LandPairMeasure(a, b, edgeOfA, laterCell, otherCell, later, win.MaxDelta, win.OverEps, view, prev, seen,
                                   verdict, origin, sight, why, texSeam, string.Join("; ", texWhy));
    }
}
