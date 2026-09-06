using System.Diagnostics;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

// CELL WATER — missing water and water seams at cell boundaries (roadmap 4.9, Part A; 2026-09-04).
//
// The mechanism the community documents (Water Seams Fix, SLaWF, the Nexus FAQ): Special Edition's
// Update.esm gave exterior CELL records water data Oldrim never had — the WATR link, the flow velocity,
// the noise and env-map paths — and a plugin that overrides a CELL for an unrelated reason (a light, a
// package, a tree) carries whatever water it was saved with. A port from Oldrim, or a plugin saved
// without forwarding, reverts the cell to worldspace-default water; the boundary with a correctly
// forwarded neighbour is a visible seam, and where the height or the HasWater flag goes too, the water
// is missing. The published fixers restore the truth source's tuple on the winning override.
//
// Per CELL: the TRUTH tuple (the last Bethesda-or-USSEP contributor's), the FIRST tuple (the earliest
// contributor's — the pre-Update state on a vanilla cell), a water mod's tuple as a second truth when
// one is present (a rule over the records, never a name), the WINNER's tuple and the winner's kind.
// The verdict names how the winner differs (DetectorModel.ClassifyWater); the seam rule reads every
// edge-adjacent pair, truth arm against winner arm, with the surface resolved the way the engine
// resolves it (DetectorModel.WaterSeam); the missing-water rule reads the winning LAND under the
// resolved surface (DetectorModel.MissingWater). Everything here is I/O and tallies.
//
// Standing rules applied: the floor and the same-defect control before any count is read (rule 12 —
// Bethesda ships a vanilla harbour line whose grid points carry TWO CELL records, and vanilla cells
// whose surface is under the ground everywhere); unjudgeable counted by reason, never cleared (rule 6);
// every emitted row carries its physical predicate under its arm, and --base re-measures a base emit's
// rows under this one (DIFFERENTIAL-CREDIT-2026-09-03).
//
// NOT verified in game. A Dropped, Reverted or Seam row is a candidate; Part C looks.
//
// usage: waterprobe [--list N] [--near X,Y] [--worldspace EDID] [--eps U] [--min-visible F] [--water-mod-cells N]
//                   [--cell EDID|X,Y] [--json FILE] [--emit FILE] [--base EMIT] [--no-land]
//                   [--enable X.esp]* [--disable X.esp]* [--instance DIR] [--game DIR] [--profile NAME]

string Instance = HarnessTarget.Instance;
string GamePath = HarnessTarget.Game;
string profileName = HarnessTarget.Profile;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--instance" && i + 1 < args.Length) Instance = args[i + 1];
    else if (args[i] == "--game" && i + 1 < args.Length) GamePath = args[i + 1];
    else if (args[i] == "--profile" && i + 1 < args.Length) profileName = args[i + 1];
}
Instance = HarnessTarget.Require(Instance, "MO2 instance directory", "instance", "--instance");
GamePath = HarnessTarget.Require(GamePath, "game directory", "game", "--game");
profileName = HarnessTarget.Require(profileName, "MO2 profile name", "profile", "--profile");
string profileDir = Path.Combine(Instance, "profiles", profileName);
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");

int listN = 25;
(int X, int Y)? near = null;
string? worldFilter = null;
float eps = 0.5f;
float minVisible = 0.05f;
int waterModCells = 10;
string? cellQuery = null;
string? jsonPath = null, emitPath = null, basePath = null;
bool readLand = true;
var enablePlugins = new List<string>();
var disablePlugins = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--list" && i + 1 < args.Length) listN = int.Parse(args[++i]);
    else if (args[i] == "--near" && i + 1 < args.Length) { var s = args[++i].Split(','); near = (int.Parse(s[0]), int.Parse(s[1])); }
    else if (args[i] == "--worldspace" && i + 1 < args.Length) worldFilter = args[++i];
    else if (args[i] == "--eps" && i + 1 < args.Length) eps = float.Parse(args[++i]);
    else if (args[i] == "--min-visible" && i + 1 < args.Length) minVisible = float.Parse(args[++i]);
    else if (args[i] == "--water-mod-cells" && i + 1 < args.Length) waterModCells = int.Parse(args[++i]);
    else if (args[i] == "--cell" && i + 1 < args.Length) cellQuery = args[++i];
    else if (args[i] == "--json" && i + 1 < args.Length) jsonPath = args[++i];
    else if (args[i] == "--emit" && i + 1 < args.Length) emitPath = args[++i];
    else if (args[i] == "--base" && i + 1 < args.Length) basePath = args[++i];
    else if (args[i] == "--no-land") readLand = false;
    else if (args[i] == "--enable" && i + 1 < args.Length) enablePlugins.Add(args[++i]);
    else if (args[i] == "--disable" && i + 1 < args.Length) disablePlugins.Add(args[++i]);
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
var sw = Stopwatch.StartNew();
IReadOnlyList<string> paths;
if (enablePlugins.Count > 0 || disablePlugins.Count > 0)
{
    var syn = SyntheticOrder.Build(profileDir, modsDir, dataDir, overwriteDir, enablePlugins, disablePlugins);
    foreach (var w in syn.Warnings) Console.WriteLine($"   ! {w}");
    foreach (var a in syn.Applied) Console.WriteLine($"   synthetic: {a}");
    paths = syn.OrderedPaths;
}
else paths = Mo2LoadOrder.Build(profileDir, modsDir, dataDir, overwriteDir).OrderedPaths;
Console.WriteLine($"waterprobe — {Instance} / {profileName}");
Console.WriteLine($"load order: {paths.Count} plugins ({sw.ElapsedMilliseconds} ms)   seam eps {eps:0.##} u; visible floor {minVisible:P0}; water-mod rule: ≥1 foreign WATR override or own WATR on ≥{waterModCells} cells\n");

// ---------------------------------------------------------------------------------------
// Pass 1 — every plugin once, into the shared index.
// ---------------------------------------------------------------------------------------
var idx = new CellBoundaryIndex();
var unreadable = new List<string>();
for (int p = 0; p < paths.Count; p++)
{
    ISkyrimModGetter mod;
    try { mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], SkyrimRelease.SkyrimSE); }
    catch { unreadable.Add(Path.GetFileName(paths[p])); continue; }
    using (mod as IDisposable) idx.Ingest(p, mod, readLand);
    if ((p + 1) % 150 == 0) Console.WriteLine($"   ... {p + 1}/{paths.Count} plugins, {idx.Cells.Count:N0} cells, {sw.Elapsed.TotalSeconds:0}s");
}
int exteriorCells = idx.Cells.Values.Count(c => !c.Interior && c.GX != int.MinValue);
int interiorCells = idx.Cells.Values.Count(c => c.Interior);
Console.WriteLine($"indexed {idx.Cells.Count:N0} cells ({exteriorCells:N0} exterior grid cells, {interiorCells:N0} interiors) over {idx.CellProvisions:N0} provisions; "
                + $"{idx.WaterSnaps:N0} water snaps, {idx.LandSnaps:N0} LAND snaps, {idx.Worlds.Count} worldspaces  ({sw.Elapsed.TotalSeconds:0.0}s)");
Console.WriteLine($"   plugins that could not be read (contribute nothing, counted per rule 6): {unreadable.Count:N0}" + (unreadable.Count > 0 ? "  — " + string.Join(", ", unreadable) : ""));

PluginKind Kind(int p) => idx.KindOf(p, waterModCells);
string PluginName(int p) => idx.PluginName(p);
string WsName(FormKey ws) => ws.IsNull ? "(interior)" : idx.WorldEditorId.TryGetValue(ws, out var e) && e is { Length: > 0 } ? e : ws.ToString();
string WatrName(string? key) => key is null ? "(none)" : FormKey.TryFactory(key, out var fk) && idx.WatrEditorId.TryGetValue(fk, out var e) ? $"{e} ({key})" : key;
string CellLabel(CellInfo c) => c.Interior ? $"{c.EditorId ?? c.Key.ToString()} (int)" : $"{WsName(c.Worldspace)} ({c.GX},{c.GY}){(c.EditorId is { } e ? " " + e : "")}";

// Plugin kinds as the rule settles them, stated up front.
var kindsSeen = Enumerable.Range(0, paths.Count).Where(p => idx.PluginName(p) != "(none)").Select(p => (P: p, K: Kind(p))).ToList();
Console.WriteLine($"\n==== PLUGIN KINDS (the truth rule's inputs) ====");
foreach (var g in kindsSeen.GroupBy(x => x.K).OrderBy(g => g.Key))
    Console.WriteLine($"   {g.Key,-9} {g.Count(),4}" + (g.Key is PluginKind.WaterMod or PluginKind.Generated
        ? "  — " + string.Join(", ", g.Select(x => { var (f, o) = idx.WaterModFacts(x.P); return g.Key == PluginKind.WaterMod ? $"{PluginName(x.P)} [{f} foreign WATR, own WATR on {o} cells]" : PluginName(x.P); }))
        : ""));

// ---------------------------------------------------------------------------------------
// Worldspace water — winner vs the last Bethesda statement, always reported.
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n==== WORLDSPACE WATER (winner vs the last Bethesda/USSEP statement) ====");
foreach (var (ws, hist) in idx.Worlds.OrderByDescending(kv => idx.Cells.Values.Count(c => c.Worldspace == kv.Key)).Take(12))
{
    var win = hist[^1];
    var truth = idx.WorldOfKind(ws, Kind, PluginKind.Bethesda, PluginKind.Ussep);
    string state = truth is { } t ? (t.SameAs(win) ? "forwarded" : "CHANGED") : "author-truth";
    Console.WriteLine($"   {WsName(ws),-28} {state,-12} winner {PluginName(win.Plugin)}: Water {WatrName(win.Water)}, LodWater {WatrName(win.LodWater)} @ {Fmt(win.LodWaterHeight)}, default height {Fmt(win.DefaultWaterHeight)}{(win.NoLodWater ? ", NoLodWater" : "")}; {hist.Count} distinct statement(s) over the order");
    if (truth is { } tt && !tt.SameAs(win))
        Console.WriteLine($"      truth ({PluginName(tt.Plugin)}): Water {WatrName(tt.Water)}, LodWater {WatrName(tt.LodWater)} @ {Fmt(tt.LodWaterHeight)}, default height {Fmt(tt.DefaultWaterHeight)}{(tt.NoLodWater ? ", NoLodWater" : "")}");
}
static string Fmt(float? h) => DetectorModel.HeightKind(h) == WaterHeightKind.Real ? h!.Value.ToString("0.#") : DetectorModel.HeightKind(h).ToString();

// ---------------------------------------------------------------------------------------
// Per cell — facts, verdict, resolved surface, visible share under truth and winner.
// ---------------------------------------------------------------------------------------
var dupGrid = new HashSet<(FormKey, int, int)>(idx.DuplicateGridPoints().Select(d => d.Grid));
var rows = new List<Row>(idx.Cells.Count);
foreach (var c in idx.Cells.Values)
    if (idx.JudgeWater(c, Kind, minVisible, dupGrid) is { } j)
        rows.Add(new Row(j, c.Water.Select(s => s.Plugin).ToArray()));
var byKey = rows.ToDictionary(r => r.Cell.Key);
Console.WriteLine($"\njudged {rows.Count:N0} cells with a water statement ({rows.Count(r => !r.Cell.Interior):N0} exterior, {rows.Count(r => r.Cell.Interior):N0} interior); "
                + $"{rows.Count(r => r.AuthorTruth):N0} defined by a mod (author truth); {dupGrid.Count:N0} grid point(s) carry more than one CELL record  ({sw.Elapsed.TotalSeconds:0.0}s)");

// --cell: one cell's full history, then stop.
if (cellQuery is not null)
{
    var hits = rows.Where(r => string.Equals(r.Cell.EditorId, cellQuery, StringComparison.OrdinalIgnoreCase)
                            || (!r.Cell.Interior && $"{r.Cell.GX},{r.Cell.GY}" == cellQuery && (worldFilter is null || string.Equals(WsName(r.Cell.Worldspace), worldFilter, StringComparison.OrdinalIgnoreCase)))).ToList();
    if (hits.Count == 0) Console.WriteLine($"\n--cell {cellQuery}: no cell matched (an EditorID, or X,Y with --worldspace)");
    foreach (var r in hits)
    {
        Console.WriteLine($"\n==== {CellLabel(r.Cell)} {r.Cell.Key} — {r.Verdict} ({r.Why}) ====");
        foreach (var s in r.Cell.Water)
            Console.WriteLine($"   {PluginName(s.Plugin),-48} {Kind(s.Plugin),-9} {Describe(s.Tuple)}");
        Console.WriteLine($"   record winner: {PluginName(r.Cell.Winner)} ({r.WinnerKind}); truth: {PluginName(r.TruthPlugin)}{(r.AuthorTruth ? " (author)" : "")}"
                        + (r.WaterMod is { } wm ? $"; water mod: {PluginName(wm.Plugin)}" : ""));
        Console.WriteLine($"   resolved: truth {Res(r.RTruth)} visible {Pct(r.VisTruth)}; winner {Res(r.RWin)} visible {Pct(r.VisWin)} — {r.MissingWhy}");
        if (r.Cell.Land.Count > 0) Console.WriteLine($"   LAND statements: {string.Join(", ", r.Cell.Land.Select(l => PluginName(l.Plugin)))}");
        // The floor under the surface, so a Part C reading (the player's settled z) can be told from swimming.
        foreach (var l in r.Cell.Land)
        {
            var hh = l.Heights; float mn = float.MaxValue, mx = float.MinValue, sum = 0f;
            for (int y = 0; y < HousecarlCore.LandHeight.Side; y++) for (int x = 0; x < HousecarlCore.LandHeight.Side; x++) { var v = hh[x, y]; mn = MathF.Min(mn, v); mx = MathF.Max(mx, v); sum += v; }
            Console.WriteLine($"   LAND by {PluginName(l.Plugin),-40} z min {mn:0} / mean {sum / (33f * 33f):0} / max {mx:0}; centre {hh[16, 16]:0}"
                            + (r.RWin.Z is { } zw ? $"; below the winner's surface ({zw:0}) at {CellBoundaryIndex.VisibleShare(hh, zw):P0} of vertices" : ""));
            // WP_CELL_GRID=1 — the 33×33 heights, north row first, west to east: the spot-picker for a Part C station.
            if (Environment.GetEnvironmentVariable("WP_CELL_GRID") == "1")
                for (int y = HousecarlCore.LandHeight.Side - 1; y >= 0; y--)
                    Console.WriteLine($"      y{y,2} " + string.Join(" ", Enumerable.Range(0, HousecarlCore.LandHeight.Side).Select(x => $"{hh[x, y],6:0}")));
        }
    }
    return 0;
}

string Describe(DetectorModel.WaterTuple t) => (t.HasWater ? "HasWater" : "no-water") + (t.NoLodWater ? "+NoLod" : "") + $" Water={WatrName(t.Water)} H={Fmt(t.Height)}"
    + (t.VelocityAngle is { } va ? $" vel=({va.X:0.##},{va.Y:0.##},{va.Z:0.##})" : "") + (t.Noise is { } n ? $" noise={n}" : "") + (t.EnvMap is { } e ? $" env={e}" : "");
string Res(DetectorModel.ResolvedWater r) => !r.Has ? "no water" : $"{WatrName(r.Water)} z {(r.Z is { } z ? z.ToString("0.#") : "?")}";
static string Pct(float? f) => f is { } v ? v.ToString("P0") : "n/a";

// ---------------------------------------------------------------------------------------
// The census — rule 12's split before any count is read.
// ---------------------------------------------------------------------------------------
var ext = rows.Where(r => !r.Cell.Interior).ToList();
var seEdits = ext.Where(r => !r.AuthorTruth && DetectorModel.WaterDiff(r.First, r.Truth).Count > 0).ToList();
Console.WriteLine($"\n==== THE SE WATER EDITS — exterior cells whose Bethesda/USSEP truth differs from the earliest contributor's tuple: {seEdits.Count:N0} ====");
Console.WriteLine("   (the Update.esm additions the mechanism is about; won by, top winners)");
foreach (var g in seEdits.GroupBy(r => r.Cell.Winner).OrderByDescending(g => g.Count()).Take(listN))
    Console.WriteLine($"   {g.Count(),6}  {PluginName(g.Key),-52} {Kind(g.Key),-9} " + VerdictSplit(g));
string VerdictSplit(IEnumerable<Row> g) => string.Join(", ", g.GroupBy(r => r.Verdict).OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Count():N0}"));

Console.WriteLine($"\n==== VERDICTS × POPULATION (exterior cells with water in truth or winner) ====");
var watered = ext.Where(r => r.Truth.HasWater || r.Winner.HasWater).ToList();
Console.WriteLine($"   {"population",-16}" + string.Join("", Enum.GetValues<WaterVerdict>().Select(v => $"{v,12}")) + "     total");
foreach (var pop in Enum.GetValues<WaterPopulation>())
{
    var g = watered.Where(r => DetectorModel.ClassifyWaterPopulation(r.WinnerKind) == pop).ToList();
    Console.WriteLine($"   {pop,-16}" + string.Join("", Enum.GetValues<WaterVerdict>().Select(v => $"{g.Count(r => r.Verdict == v),12:N0}")) + $"{g.Count,10:N0}");
}
Console.WriteLine($"   unjudgeable by reason: " + string.Join(", ", watered.Where(r => r.Verdict == WaterVerdict.Unjudgeable).GroupBy(r => r.Blocker).Select(g => $"{g.Key} {g.Count():N0}")));
var interiorW = rows.Where(r => r.Cell.Interior && (r.Truth.HasWater || r.Winner.HasWater)).ToList();
Console.WriteLine($"   interiors, apart: {interiorW.Count:N0} with water — " + VerdictSplit(interiorW));

Console.WriteLine($"\n==== NON-FORWARDING WINNERS (exterior; who carries the changed tuples) ====");
foreach (var g in watered.Where(r => r.Verdict is not (WaterVerdict.Forwarded or WaterVerdict.Unjudgeable)).GroupBy(r => r.Cell.Winner).OrderByDescending(g => g.Count()).Take(listN))
    Console.WriteLine($"   {g.Count(),6}  {PluginName(g.Key),-52} {Kind(g.Key),-9} " + VerdictSplit(g));

// Bethesda on Bethesda: what the DLC, CC and USSEP did to the tuple before them (the floor's own edits).
Console.WriteLine($"\n==== BETHESDA-ON-BETHESDA — a Bethesda/USSEP winner against the Bethesda/USSEP statement before it ====");
{
    var bb = new Dictionary<int, (int Same, int Changed, List<string> Fields)>();
    foreach (var r in watered.Where(r => r.WinnerKind is PluginKind.Bethesda or PluginKind.Ussep))
    {
        var bs = r.Cell.Water.Where(s => Kind(s.Plugin) is PluginKind.Bethesda or PluginKind.Ussep).ToList();
        if (bs.Count < 2 || bs[^1].Plugin == 0) continue;
        var d = DetectorModel.WaterDiff(bs[^1].Tuple, bs[^2].Tuple);
        (int Same, int Changed, List<string> Fields) e = bb.TryGetValue(bs[^1].Plugin, out var v) ? v : (0, 0, new List<string>());
        if (d.Count == 0) e.Same++; else { e.Changed++; e.Fields.AddRange(d); }
        bb[bs[^1].Plugin] = e;
    }
    foreach (var (p, v) in bb.OrderByDescending(kv => kv.Value.Same + kv.Value.Changed))
        Console.WriteLine($"   {PluginName(p),-52} re-states {v.Same,5:N0}, changes {v.Changed,5:N0}" + (v.Changed > 0 ? "  (" + string.Join(", ", v.Fields.GroupBy(f => f).Select(g => $"{g.Key} {g.Count()}")) + ")" : ""));
    if (bb.Count == 0) Console.WriteLine("   (no Bethesda/USSEP winner sits over an earlier Bethesda/USSEP statement)");
}

Console.WriteLine($"\n==== HEIGHT SENTINELS (winner tuples, exterior) ====");
foreach (var g in ext.GroupBy(r => DetectorModel.HeightKind(r.Winner.Height)).OrderByDescending(g => g.Count()))
    Console.WriteLine($"   {g.Key,-16} {g.Count(),7:N0}" + (g.Key is not (WaterHeightKind.Real or WaterHeightKind.Unset) ? $"  e.g. {string.Join(", ", g.Take(4).Select(r => $"{CellLabel(r.Cell)} = {r.Winner.Height:R}"))}" : ""));

Console.WriteLine($"\n==== THE DUPLICATE-CELL WRINKLE — grid points with more than one CELL record: {dupGrid.Count:N0} ====");
foreach (var d in idx.DuplicateGridPoints().OrderBy(d => WsName(d.Grid.Ws)).ThenBy(d => d.Grid.Y).ThenBy(d => d.Grid.X).Take(listN))
{
    Console.WriteLine($"   {WsName(d.Grid.Ws)} ({d.Grid.X},{d.Grid.Y}):");
    foreach (var k in d.Keys)
        if (byKey.TryGetValue(k, out var r))
            Console.WriteLine($"      {k}  {(r.Cell.EditorId ?? "")}  defined by {PluginName(r.Cell.Definer)}, won by {PluginName(r.Cell.Winner)}, {r.Cell.Water.Count} statement(s): winner {Describe(r.Winner)}");
}

// ---------------------------------------------------------------------------------------
// Seams — every edge-adjacent pair, truth arm against winner arm.
// ---------------------------------------------------------------------------------------
var seams = new List<SeamRow>();
foreach (var a in ext)
{
    if (a.Dup) continue;
    foreach (var (dx, dy) in new[] { (1, 0), (0, 1) })
    {
        var bc = idx.Single(a.Cell.Worldspace, a.Cell.GX + dx, a.Cell.GY + dy);
        if (bc is null || !byKey.TryGetValue(bc.Key, out var b)) continue;
        if (a.Verdict == WaterVerdict.Unjudgeable || b.Verdict == WaterVerdict.Unjudgeable) continue;
        var (sv, why) = DetectorModel.WaterSeam(a.RTruth, b.RTruth, a.RWin, b.RWin, a.Verdict, b.Verdict, eps);
        if (sv == WaterSeamVerdict.None) continue;
        // The physical half under this arm: the two resolved surfaces still differ.
        var phys = DetectorModel.WaterJoined(a.RWin, b.RWin, eps) ? PhysicalState.Gone : PhysicalState.Holds;
        string detail = $"winner {Res(a.RWin)} | {Res(b.RWin)}";
        seams.Add(new SeamRow(a, b, dx == 1 ? "E" : "N", sv, why, phys, detail));
    }
}
Console.WriteLine($"\n==== WATER SEAMS — edge-adjacent pairs, truth arm vs winner arm (eps {eps:0.##} u) ====");
foreach (var g in seams.GroupBy(s => s.Verdict).OrderBy(g => g.Key))
    Console.WriteLine($"   {g.Key,-10} {g.Count(),6:N0}   " + string.Join(", ", g.GroupBy(s => DetectorModel.ClassifyWaterPopulation(s.A.Verdict != WaterVerdict.Forwarded ? s.A.WinnerKind : s.B.WinnerKind)).OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Count():N0}")));
Console.WriteLine($"   the floor: {seams.Count(s => s.Verdict == WaterSeamVerdict.Inherited):N0} pair(s) apart in both arms (Bethesda ships them); {seams.Count(s => s.Verdict == WaterSeamVerdict.Joined):N0} joined by a later plugin (a fixer's shape)");
var seamRows = seams.Where(s => s.Verdict == WaterSeamVerdict.Seam).OrderBy(s => Rank(s.A.Cell)).ThenBy(s => Rank(s.B.Cell)).ToList();
foreach (var s in seamRows.Take(listN))
    Console.WriteLine($"   SEAM {CellLabel(s.A.Cell)} {s.Dir} {CellLabel(s.B.Cell)}: {s.Why}\n"
                    + $"        A {s.A.Verdict} won by {PluginName(s.A.Cell.Winner)}; B {s.B.Verdict} won by {PluginName(s.B.Cell.Winner)}");
double Rank(CellInfo c)
{
    if (near is { } n) return Math.Max(Math.Abs(c.GX - n.X), Math.Abs(c.GY - n.Y)) + (worldFilter is not null && !string.Equals(WsName(c.Worldspace), worldFilter, StringComparison.OrdinalIgnoreCase) ? 10_000 : 0);
    return (WsName(c.Worldspace) == "Tamriel" ? 0 : 100_000) + Math.Abs(c.GX) + Math.Abs(c.GY);
}

// ---------------------------------------------------------------------------------------
// Missing-water candidates, and the same-defect control.
// ---------------------------------------------------------------------------------------
var missing = ext.Where(r => r.Missing).OrderBy(r => Rank(r.Cell)).ToList();
var control = ext.Where(r => r.RTruth.Has && r.VisTruth is { } v && v < minVisible).ToList();
Console.WriteLine($"\n==== MISSING-WATER CANDIDATES: {missing.Count:N0} (exterior; the truth's surface shows over ≥{minVisible:P0} of the cell, the winner's does not) ====");
Console.WriteLine($"   the same-defect control: {control.Count:N0} cell(s) whose TRUTH surface is already under the ground everywhere (Bethesda ships them; never a candidate) — "
                + string.Join(", ", control.GroupBy(r => Kind(r.TruthPlugin)).Select(g => $"{g.Key} {g.Count():N0}")));
Console.WriteLine($"   unmeasured: {ext.Count(r => r.Physical == PhysicalState.Unmeasured && r.Verdict is not (WaterVerdict.Forwarded or WaterVerdict.Unjudgeable)):N0} non-forwarding cell(s) without a LAND to measure against");
foreach (var g in missing.GroupBy(r => r.Verdict)) Console.WriteLine($"   by verdict: {g.Key} {g.Count():N0}");
foreach (var r in missing.Take(listN))
    Console.WriteLine($"   {CellLabel(r.Cell),-44} {r.Verdict,-9} won by {PluginName(r.Cell.Winner)} ({r.WinnerKind}); truth {PluginName(r.TruthPlugin)}: {Res(r.RTruth)} vis {Pct(r.VisTruth)} -> winner {Res(r.RWin)} vis {Pct(r.VisWin)}\n"
                    + $"        {r.Why}; {r.MissingWhy}");

// The non-forwarding rows themselves, ranked — the search's raw material.
var nonFwd = ext.Where(r => r.Verdict is WaterVerdict.Dropped or WaterVerdict.Reverted or WaterVerdict.Retyped or WaterVerdict.Altered).OrderBy(r => Rank(r.Cell)).ToList();
Console.WriteLine($"\n==== NON-FORWARDING CELLS, ranked{(near is { } nn ? $" by distance to ({nn.X},{nn.Y})" : "")}: {nonFwd.Count:N0} ====");
foreach (var r in nonFwd.Take(listN))
    Console.WriteLine($"   {CellLabel(r.Cell),-44} {r.Verdict,-9} {PluginName(r.Cell.Winner)} ({r.WinnerKind}) — {r.Why}");

// ---------------------------------------------------------------------------------------
// --emit / --base / --json
// ---------------------------------------------------------------------------------------
if (emitPath is not null)
{
    var conflicts = new List<Conflict>();
    foreach (var r in missing)
        conflicts.Add(new Conflict("WATER-MISSING", PluginName(r.TruthPlugin), PluginName(r.Cell.Winner),
            $"{CellLabel(r.Cell)}: {r.Verdict} — {r.Why}; {r.MissingWhy}",
            new List<ConflictRecord> { new(r.Cell.Key.ToString(), "cell", CellLabel(r.Cell), r.Physical, r.MissingWhy) }));
    foreach (var s in seamRows)
    {
        var attributed = s.A.Verdict != WaterVerdict.Forwarded ? s.A : s.B;
        var other = ReferenceEquals(attributed, s.A) ? s.B : s.A;
        conflicts.Add(new Conflict("WATER-SEAM", PluginName(other.Cell.Winner), PluginName(attributed.Cell.Winner),
            $"{CellLabel(s.A.Cell)} {s.Dir} {CellLabel(s.B.Cell)}: {s.Why}",
            new List<ConflictRecord> { new(attributed.Cell.Key.ToString(), "cell", CellLabel(attributed.Cell), s.Physical, s.Detail) },
            new List<ConflictRecord> { new(other.Cell.Key.ToString(), "cell", CellLabel(other.Cell)) }));
    }
    List<Predicate>? predicates = null;
    if (basePath is not null)
    {
        var baseFile = ConflictIo.Read(basePath);
        predicates = new List<Predicate>();
        foreach (var c in baseFile.Conflicts)
        {
            string? other = c.Alternative is { Count: > 0 } alt ? alt[0].FormKey : null;
            foreach (var rec in c.Necessary)
            {
                if (!FormKey.TryFactory(rec.FormKey, out var fk) || !byKey.TryGetValue(fk, out var r))
                { predicates.Add(new Predicate(c.Kind, c.A, c.B, rec.FormKey, other, PhysicalState.Unmeasured, "the cell is not in this order")); continue; }
                if (c.Kind == "WATER-MISSING")
                {
                    // The predicate under THIS arm: the winner's surface is invisible (or absent) where the base row's truth showed water.
                    var (_, ph, why) = DetectorModel.MissingWater(r.Verdict == WaterVerdict.Forwarded ? WaterVerdict.Altered : r.Verdict, r.RTruth.Has, r.VisTruth, r.RWin.Has, r.VisWin, minVisible);
                    if (r.Verdict == WaterVerdict.Forwarded) ph = r.RWin.Has && (r.VisWin is null || r.VisWin >= minVisible) ? PhysicalState.Gone : r.VisWin is null ? PhysicalState.Unmeasured : PhysicalState.Holds;
                    predicates.Add(new Predicate(c.Kind, c.A, c.B, rec.FormKey, other, ph, why));
                }
                else if (c.Kind == "WATER-SEAM" && other is not null && FormKey.TryFactory(other, out var ok) && byKey.TryGetValue(ok, out var o))
                {
                    var ph = DetectorModel.WaterJoined(r.RWin, o.RWin, eps) ? PhysicalState.Gone : PhysicalState.Holds;
                    predicates.Add(new Predicate(c.Kind, c.A, c.B, rec.FormKey, other, ph, $"winner {Res(r.RWin)} | {Res(o.RWin)}"));
                }
                else predicates.Add(new Predicate(c.Kind, c.A, c.B, rec.FormKey, other, PhysicalState.Unmeasured, "not a water row"));
            }
        }
        Console.WriteLine($"\n   --base {Path.GetFileName(basePath)}: {predicates.Count:N0} base-row predicate(s) re-measured under this order — "
                        + string.Join(", ", predicates.GroupBy(p => p.Physical).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count():N0}")));
    }
    var coverage = new List<CoverageLine>
    {
        new("plugins", paths.Count - unreadable.Count, new Dictionary<string, long> { ["UnreadablePlugin"] = unreadable.Count }),
        new("exterior cells with water", watered.Count(r => r.Verdict != WaterVerdict.Unjudgeable),
            watered.Where(r => r.Verdict == WaterVerdict.Unjudgeable).GroupBy(r => r.Blocker.ToString()).ToDictionary(g => g.Key, g => (long)g.Count()),
            new Dictionary<string, long> { ["Interior"] = interiorW.Count },
            $"same-defect control {control.Count:N0} (truth surface under the ground); no LAND to measure {ext.Count(r => r.Physical == PhysicalState.Unmeasured && r.Verdict is not (WaterVerdict.Forwarded or WaterVerdict.Unjudgeable)):N0}"),
        new("edge-adjacent pairs", seams.Count + 0, new Dictionary<string, long>(), null,
            $"seam {seamRows.Count:N0}; inherited {seams.Count(s => s.Verdict == WaterSeamVerdict.Inherited):N0}; joined {seams.Count(s => s.Verdict == WaterSeamVerdict.Joined):N0} (pairs with no disagreement in either arm are not listed)"),
    };
    // CELL water records carry no placed references, so no enable population applies; said rather than left null.
    ConflictIo.Write(emitPath, new ConflictFile("waterprobe", Instance, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), conflicts, coverage, predicates, basePath,
                                                "n/a (CELL water records; no placed-reference population)", "n/a"));
    Console.WriteLine($"\nemitted {conflicts.Count:N0} rows ({missing.Count:N0} WATER-MISSING, {seamRows.Count:N0} WATER-SEAM) -> {emitPath}");
}

if (jsonPath is not null)
{
    var dump = new
    {
        instance = Instance, profile = profileName, plugins = paths.Count, unreadable, eps, minVisible, waterModCells,
        kinds = kindsSeen.Select(x => new { plugin = PluginName(x.P), kind = x.K.ToString(), facts = idx.WaterModFacts(x.P) }),
        cells = rows.Select(r => new
        {
            key = r.Cell.Key.ToString(), label = CellLabel(r.Cell), worldspace = WsName(r.Cell.Worldspace), x = r.Cell.GX, y = r.Cell.GY, interior = r.Cell.Interior,
            verdict = r.Verdict.ToString(), blocker = r.Blocker.ToString(), why = r.Why, winner = PluginName(r.Cell.Winner), winnerKind = r.WinnerKind.ToString(),
            truthPlugin = PluginName(r.TruthPlugin), authorTruth = r.AuthorTruth, duplicate = r.Dup,
            truth = r.Truth, first = r.First, winnerTuple = r.Winner, waterMod = r.WaterMod?.Tuple,
            resolvedTruth = r.RTruth, resolvedWinner = r.RWin, visibleTruth = r.VisTruth, visibleWinner = r.VisWin,
            missing = r.Missing, physical = r.Physical.ToString(), missingWhy = r.MissingWhy,
            contributors = r.Contributors.Select(PluginName),
        }),
        seams = seams.Select(s => new { a = s.A.Cell.Key.ToString(), b = s.B.Cell.Key.ToString(), aLabel = CellLabel(s.A.Cell), bLabel = CellLabel(s.B.Cell), dir = s.Dir, verdict = s.Verdict.ToString(), why = s.Why, physical = s.Physical.ToString() }),
        duplicates = idx.DuplicateGridPoints().Select(d => new { worldspace = WsName(d.Grid.Ws), d.Grid.X, d.Grid.Y, keys = d.Keys.Select(k => k.ToString()) }),
    };
    File.WriteAllText(jsonPath, JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"json -> {jsonPath}");
}

Console.WriteLine($"\ndone in {sw.Elapsed.TotalSeconds:0.0}s");
return 0;

sealed record Row(CellBoundaryIndex.CellWaterJudgement J, int[] Contributors)
{
    public CellInfo Cell => J.Cell; public DetectorModel.WaterTuple First => J.First; public DetectorModel.WaterTuple Truth => J.Truth;
    public int TruthPlugin => J.TruthPlugin; public WaterSnap? WaterMod => J.WaterMod; public DetectorModel.WaterTuple Winner => J.Winner;
    public PluginKind WinnerKind => J.WinnerKind; public bool AuthorTruth => J.AuthorTruth; public bool Dup => J.Duplicate;
    public WaterVerdict Verdict => J.Verdict; public WaterBlocker Blocker => J.Blocker; public string Why => J.Why;
    public DetectorModel.ResolvedWater RTruth => J.RTruth; public DetectorModel.ResolvedWater RWin => J.RWin;
    public float? VisTruth => J.VisTruth; public float? VisWin => J.VisWin;
    public bool Missing => J.Missing; public PhysicalState Physical => J.Physical; public string MissingWhy => J.MissingWhy;
}
sealed record SeamRow(Row A, Row B, string Dir, WaterSeamVerdict Verdict, string Why, PhysicalState Physical, string Detail);
