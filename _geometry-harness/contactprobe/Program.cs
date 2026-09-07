using System.Diagnostics;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore.Progress;

// PLACEMENT CONTACT — the geometry half of stranded support and orphaned attachment (roadmap 4.5 / 4.6;
// charter milestone 8 over milestone 6's object-object narrow phase, built here once for both).
//
// The two classes, in Piranha's words: mod A puts apples on a table, mod B moves the table, nothing
// moves the apples; mod A hangs a sconce on a wall, mod B moves the wall, the sconce is neither moved
// nor deleted. The records pass (depprobe) measured that no declared edge carries either shape — the
// apples and the sconce are not declared, so contact has to be COMPUTED. This is that computation:
// for every physical host a plugin moved, relocated or removed, every physical thing that RESTED ON or
// was MOUNTED AGAINST it in the order without the mover (SpatialOverlap.Contact on the collision soups,
// the host at its previous snap), and what holds it under the winner — the host at its new place,
// something else the room was rebuilt with, nothing (STRANDED / ORPHANED), or something it is now inside
// (SUNK). Differential, never absolute: an object that floated as shipped is the absolute floor's
// business and is measured beside the class, never counted in it.
//
// Two splits ride on every count. FIXED / MOVABLE: 4.7 Part C showed a movable CLUTTER object is shoved,
// not a blocker — so a movable dependent left in the air falls at cell load rather than floating; only
// FIXED rows are the class until an in-game arm says otherwise, and movable rows are a context list. The
// motion class reads the record type and, for a MoveableStatic, the NIF's own motion system (the decoder
// now reports it), never a guess from the type alone. INTERIOR / EXTERIOR: terrain is not a reference,
// so an exterior dependent may be standing on the ground and read Stranded; counts are read on interiors.
//
// The rule is DetectorModel.PlacementContact (proofprobe-owned, a synthetic order behind it); the arms
// and the pairing are _shared/ContactIndex.cs over OverlapIndex's history — the same changed-only
// history driftprobe, depprobe and overlapprobe run on. The primitive is SpatialOverlap.Contact
// (crossprobe-owned). Everything here is I/O, geometry choice and tallies. Standing rules applied: the
// geometry CHOICE (collision / render / OBND box) labelled on every row; floors and the labelled set
// measured before any count is read (rules 2, 12); unjudgeable counted, never cleared (rule 6);
// nothing verified in game.
//
// Modes (one run, one mode):
//   (default)              the differential pass over every moved host — the class, the floor, the ranker
//   --absolute             every clutter- and furniture-scale ref against its neighbours as SHIPPED: touching /
//                          floating / sunk — the absolute floor the charter predicted is large
//   --labelled-mover X.esp every ref X moved, contact BEFORE X vs AFTER X — USSEP's placement fixes as a
//                          labelled positive set for the primitive (no game, no judgement)
//   --overlap-json FILE    overlapprobe's judged rows through the narrow phase: no contact / touch / penetrate
//                          — the milestone 6 ranker upgrade for 4.1
//
// usage: contactprobe [--list N] [--eps U] [--reach U] [--json FILE] [--emit FILE] [--base EMIT] [--all-rows] [--interior-only]
//                     [--absolute] [--labelled-mover X.esp] [--overlap-json FILE]
//                     [--only A.esm,B.esm,...] [--vanilla-assets] [--bos on|off]
//                     [--enable X.esp]* [--disable X.esp]* [--instance DIR] [--game DIR] [--profile NAME]
//   POPULATION      the LOAD-TIME state, and it always has been (R1, 2026-09-03): a reference exists in an arm when its
//                   XESP parent chain resolves enabled there, resolved PER ARM — the winner's, and the arm without a
//                   mover's. Q27 changed nothing here except that the emit now says so. --flags is refused: there is no
//                   flag-only contact population to reproduce.
//   --state SPEC    the world-state arm (roadmap 4.11): <switch>[,<switch>…], stage:<quest>:<n> (with --switch-map), or
//                   baseline — the runtime enable state every arm reads (R1) has those switches flipped, in the winner arm
//                   and in the arm without the mover alike. Without it, the load-time state, as every run before 2026-09-04.
//   --bos on|off    apply the Base Object Swapper layer (*_SWAP.ini) so every ref is judged by the base the game loads
//                   (default on; _shared/BosHook.cs); off = the record bases
//   --base EMIT     re-measure every record of that emit's rows under THIS order and carry the results in the emit as
//                   Predicates — the changed arm of a differential (conflictdiff reads both). See ConflictReport.cs.
//   --only          keep only the named plugins of the profile's order (the vanilla masters, or vanilla + USSEP)
//   --vanilla-assets resolve meshes from the game's own archives only — no mod folder, no replacer — so a
//                   labelled set authored against vanilla meshes is tested against vanilla meshes

// Nothing is compiled in — see _shared/HarnessTarget.cs. Flags win over the environment and over
// harness.local.json; Require() runs AFTER them.
using var __run = RunLog.BeginProcess("contactprobe", args);

string Instance = HarnessTarget.Instance;
string GamePath = HarnessTarget.Game;
string profileName = HarnessTarget.Profile;
{
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--instance" && i + 1 < args.Length) Instance = args[i + 1];
        else if (args[i] == "--game" && i + 1 < args.Length) GamePath = args[i + 1];
        else if (args[i] == "--profile" && i + 1 < args.Length) profileName = args[i + 1];
    }
}
Instance = HarnessTarget.Require(Instance, "MO2 instance directory", "instance", "--instance");
GamePath = HarnessTarget.Require(GamePath, "game directory", "game", "--game");
profileName = HarnessTarget.Require(profileName, "MO2 profile name", "profile", "--profile");
string profileDir = Path.Combine(Instance, "profiles", profileName);
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");

int listN = 25;
string? stateSpec = null, switchMapPath = null;
float eps = 4f;            // touching: surfaces within ε and no vertex deeper than ε inside. A chosen number, stated.
float reach = 128f;        // how far from a moved host a dependent is looked for (the records pass's contact-plausible radius).
string? jsonPath = null, emitPath = null, basePath = null, overlapJson = null, labelledMover = null, geomReport = null;
bool allRows = false, absolute = false, vanillaAssets = false, interiorOnly = false;
HashSet<string>? only = null;
bool bosOn = true;
var enablePlugins = new List<string>();
var disablePlugins = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--list" && i + 1 < args.Length) listN = int.Parse(args[++i]);
    else if (args[i] == "--eps" && i + 1 < args.Length) eps = float.Parse(args[++i]);
    else if (args[i] == "--reach" && i + 1 < args.Length) reach = float.Parse(args[++i]);
    else if (args[i] == "--json" && i + 1 < args.Length) jsonPath = args[++i];
    else if (args[i] == "--emit" && i + 1 < args.Length) emitPath = args[++i];
    else if (args[i] == "--base" && i + 1 < args.Length) basePath = args[++i];
    else if (args[i] == "--overlap-json" && i + 1 < args.Length) overlapJson = args[++i];
    else if (args[i] == "--labelled-mover" && i + 1 < args.Length) labelledMover = args[++i];
    else if (args[i] == "--geom-report" && i + 1 < args.Length) geomReport = args[++i];
    else if (args[i] == "--only" && i + 1 < args.Length) only = new HashSet<string>(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
    else if (args[i] == "--all-rows") allRows = true;
    else if (args[i] == "--absolute") absolute = true;
    else if (args[i] == "--vanilla-assets") vanillaAssets = true;
    else if (args[i] == "--interior-only") interiorOnly = true;
    else if (args[i] == "--enable" && i + 1 < args.Length) enablePlugins.Add(args[++i]);
    else if (args[i] == "--disable" && i + 1 < args.Length) disablePlugins.Add(args[++i]);
    else if (args[i] == "--bos" && i + 1 < args.Length) bosOn = BosHook.ParseFlag(args[++i]) ?? throw new ArgumentException("--bos takes on|off");
    else if (args[i] == "--state" && i + 1 < args.Length) stateSpec = args[++i];
    // --flags is refused rather than added (Q27): contactprobe has resolved the enable chain since R1 (2026-09-03), so
    // there is no flag-only population of it to reproduce, and one would be a mode known to be wrong about the game.
    else if (args[i] == "--flags")
    {
        try { PopulationCli.Resolve("contactprobe", true, null, flagsSupported: false); }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); }
        return 2;
    }
    else if (args[i] == "--switch-map" && i + 1 < args.Length) switchMapPath = args[++i];
}

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
if (only is not null)
{
    paths = paths.Where(p => only.Contains(Path.GetFileName(p))).ToList();
    Console.WriteLine($"--only: {paths.Count} of the profile's plugins kept ({string.Join(", ", paths.Select(Path.GetFileName))})");
    foreach (var name in only.Where(n => !paths.Any(p => string.Equals(Path.GetFileName(p), n, StringComparison.OrdinalIgnoreCase))))
        Console.WriteLine($"   ! --only names '{name}', which the profile's order does not contain");
}
var comp = Mo2LoadOrder.ReadComposition(profileDir);
var arch = ArchiveDiscovery.Discover(profileDir, modsDir, dataDir, overwriteDir, GamePath);
IReadOnlyList<string> enabledMods = vanillaAssets ? Array.Empty<string>() : comp.EnabledMods;
var archives = vanillaAssets ? arch.Archives.Where(a => a.Path.StartsWith(dataDir, StringComparison.OrdinalIgnoreCase)).ToList() : arch.Archives;
using var assets = AssetResolver.Build(vanillaAssets ? Path.Combine(Instance, "__no_overwrite__") : overwriteDir, modsDir, dataDir, enabledMods, archives);
Console.WriteLine($"load order: {paths.Count} plugins; meshes from {(vanillaAssets ? $"the game's own archives only ({archives.Count} archives)" : $"the profile's VFS ({enabledMods.Count} mods, {archives.Count} archives)")}\n");

var Vanilla = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{ "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm", "SkyrimVR.esm", "_ResourcePack.esl" };
const string Ussep = "Unofficial Skyrim Special Edition Patch.esp";

// ---------------------------------------------------------------------------------------
// Geometry — measured NIFs through the VFS; the soup per role chosen and LABELLED; OBND box as the fallback.
// ---------------------------------------------------------------------------------------
ContactIndex? cidx = null;
var meshGeom = new Dictionary<string, RefGeometry?>(StringComparer.OrdinalIgnoreCase);
// --geom-report: one line per measured mesh — which soup each role got and why, every solid body's layer and motion.
var geomLines = geomReport is null ? null : new List<string> { "mesh\thostSrc\tdepSrc\thostTris\tdepTris\trenderTris\tcollisionComplete\tphantomOnly\tbodies" };
long meshesMeasured = 0, hostColl = 0, hostRender = 0, depColl = 0, depRender = 0, meshNoSoup = 0, obndFallback = 0, noGeometry = 0, phantomOnly = 0, incompleteCollision = 0;

RefGeometry? GeomOf(FormKey baseKey)
{
    RefGeometry? g = null;
    if (cidx!.Index.BaseMesh.TryGetValue(baseKey, out var rel))
    {
        var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
        if (!meshGeom.TryGetValue(full, out g))
        {
            g = null;
            var res = assets.ResolveForPlacement(full);
            if (res.Sources.Count > 0)
            {
                var (bytes, _) = AssetResolver.ReadPlacementSource(res.Sources[0]);
                if (bytes is not null && NifGeometry.Measure(bytes, includeTriangles: true) is { Model: { } m })
                {
                    meshesMeasured++;
                    if (!m.CollisionComplete) incompleteCollision++;
                    // The HOST surface: the solid bodies on the layers an actor (and a dropped item) meets — a canopy
                    // leaves, a trunk stays. The DEPENDENT solid: every solid body but NONCOLLIDABLE (an item's WEAPON
                    // or CLUTTER body is its shape). Either falls back to the render soup, labelled 'r'.
                    var hc = m.CollisionSoupFor(b => b.Solid && CollisionLayers.IsActorSolid(b.Layer));
                    var dc = m.CollisionSoupFor(b => b.Solid && b.Layer != "SKYL_NONCOLLIDABLE");
                    TriangleSoup host, dep; char hs, ds;
                    if (m.CollisionComplete && hc is { TriangleCount: > 0 }) { host = hc; hs = 'c'; hostColl++; }
                    else if (m.Soup is { TriangleCount: > 0 }) { host = m.Soup; hs = 'r'; hostRender++; }
                    else { host = TriangleSoup.Empty; hs = '-'; }
                    if (m.CollisionComplete && dc is { TriangleCount: > 0 }) { dep = dc; ds = 'c'; depColl++; }
                    else if (m.Soup is { TriangleCount: > 0 }) { dep = m.Soup; ds = 'r'; depRender++; }
                    else { dep = TriangleSoup.Empty; ds = '-'; }
                    var box = SpatialOverlap.SoupBounds(host).Union(SpatialOverlap.SoupBounds(dep));
                    var bodies = m.CollisionBodies?.Where(b => b.Solid).Select(b => (b.Layer, b.MotionSystem)).ToList() ?? new List<(string, string)>();
                    bool phantom = m.HasCollision && !m.HasSolidCollision;
                    if (phantom) phantomOnly++;
                    if (box.IsEmpty) meshNoSoup++;
                    // A host must be real collision: the engine rests nothing on a mesh without it, and the inside test
                    // needs a closed solid. A render fallback still serves as a DEPENDENT, labelled.
                    g = box.IsEmpty ? null : new RefGeometry(box, host, dep, hs, ds, HostUsable: hs == 'c', bodies, phantom);
                    if (geomLines is not null)
                    {
                        // The winding and closure audit per solid body: triangles whose normal points toward the body's
                        // own vertex centroid (inward, for a convex body), and edges not shared by exactly two triangles
                        // (an open shell). The inside test needs closed and outward; this says how often it has them.
                        var audit = new List<string>();
                        foreach (var b in m.CollisionBodies ?? Array.Empty<CollisionBodyInfo>())
                        {
                            string tag = $"{b.Layer}/{(b.Solid ? "solid" : "phantom")}/{(b.MotionSystem == "" ? "?" : b.MotionSystem)}/{string.Join("+", b.ShapeTypes.Where(t => t.StartsWith("bhk") && t.Contains("Shape")).Select(t => t.Replace("bhk", "").Replace("Shape", "")))}/{b.IndexCount / 3}tri{(b.Unread.Count > 0 ? "/UNREAD" : "")}";
                            if (b.IndexCount >= 3 && m.CollisionSoup is { } cs)
                            {
                                var cen = System.Numerics.Vector3.Zero; int nv = 0;
                                var vset = new HashSet<int>();
                                for (int k = b.IndexStart; k < b.IndexStart + b.IndexCount; k++) if (vset.Add(cs.Indices[k])) { cen += cs.Vertices[cs.Indices[k]]; nv++; }
                                if (nv > 0) cen /= nv;
                                int inward = 0, tris = 0;
                                var edges = new Dictionary<(int, int), int>();
                                for (int k = b.IndexStart; k + 2 < b.IndexStart + b.IndexCount; k += 3)
                                {
                                    int i0 = cs.Indices[k], i1 = cs.Indices[k + 1], i2 = cs.Indices[k + 2];
                                    var p0 = cs.Vertices[i0]; var p1 = cs.Vertices[i1]; var p2 = cs.Vertices[i2];
                                    var n = System.Numerics.Vector3.Cross(p1 - p0, p2 - p0);
                                    if (n.LengthSquared() < 1e-9f) continue;
                                    tris++;
                                    if (System.Numerics.Vector3.Dot(n, (p0 + p1 + p2) / 3f - cen) < 0) inward++;
                                    foreach (var (a, bb) in new[] { (i0, i1), (i1, i2), (i2, i0) })
                                    { var e = a < bb ? (a, bb) : (bb, a); edges[e] = edges.GetValueOrDefault(e) + 1; }
                                }
                                int open = edges.Count(e => e.Value != 2);
                                tag += $"/inward{inward}of{tris}/openEdges{open}of{edges.Count}";
                            }
                            audit.Add(tag);
                        }
                        geomLines.Add($"{full}\t{hs}\t{ds}\t{host.TriangleCount}\t{dep.TriangleCount}\t{m.Soup?.TriangleCount ?? 0}\t{m.CollisionComplete}\t{phantom}\t{string.Join(" ", audit)}");
                    }
                }
            }
            meshGeom[full] = g;
        }
    }
    if (g is null)
    {
        var ob = cidx.Index.ObndBox(baseKey);
        if (!ob.IsEmpty) { obndFallback++; g = new RefGeometry(ob, ContactIndex.BoxSoup(ob), ContactIndex.BoxSoup(ob), 'o', 'o', HostUsable: false, Array.Empty<(string, string)>(), false); }
        else noGeometry++;
    }
    return g;
}
cidx = new ContactIndex(GeomOf) { Epsilon = eps, Reach = reach, NearBand = 2f * eps, BoxBand = 2f * eps + 2f };
var idx = cidx.Index;
// --bos on (default): the Base Object Swapper reader replaces each reference's record base with the base the
// game loads, and a moving override becomes a placement step under a pseudo-plugin after the last real one
// (_shared/BosHook.cs). --bos off: the record bases, as before 2026-09-04.
idx.Bos = bosOn ? new BosFormIndex() : null;

// ---------------------------------------------------------------------------------------
// Pass 1 — every plugin once, into the shared index.
// ---------------------------------------------------------------------------------------
var pluginKey = new ModKey[paths.Count];
var masterIdx = new HashSet<int>[paths.Count + (bosOn ? 1 : 0)];
var lightness = new List<(ModKey, bool)>();
var unreadable = new List<string>();
__run.Feature("plugins", paths.Count, "plugins").Stage("scan", "reading plugins", paths.Count, "plugins");

for (int p = 0; p < paths.Count; p++)
{
    __run.Advance();
    ISkyrimModGetter mod;
    try { mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], SkyrimRelease.SkyrimSE); }
    catch { unreadable.Add(Path.GetFileName(paths[p])); pluginKey[p] = ModKey.Null; masterIdx[p] = new HashSet<int>(); lightness.Add((ModKey.Null, false)); continue; }
    using (mod as IDisposable)
    {
        pluginKey[p] = mod.ModKey;
        lightness.Add((mod.ModKey, BosHook.IsLight(mod)));
        var mset = new HashSet<int>();
        try
        {
            var names = mod.ModHeader.MasterReferences.Select(m => m.Master).ToHashSet();
            for (int q = 0; q < p; q++) if (names.Contains(pluginKey[q])) mset.Add(q);
        }
        catch { }
        masterIdx[p] = mset;
        idx.Ingest(p, mod);
    }
    if ((p + 1) % 150 == 0) Console.WriteLine($"   ... {p + 1}/{paths.Count} plugins, {sw.Elapsed.TotalSeconds:0}s");
}
int bosPlugin = bosOn ? paths.Count : -1;
if (bosOn) masterIdx[bosPlugin] = BosHook.PseudoMasters(paths.Count);
var closure = DetectorModel.MasterClosure(masterIdx);
Console.WriteLine($"indexed {idx.History.Count:N0} refs ({idx.Placements:N0} placements; {idx.SilentRePlacements:N0} rotation/scale-only overrides not recorded; {idx.Deletions.EverCount:N0} refs deleted somewhere)  ({sw.Elapsed.TotalSeconds:0.0}s)");
Console.WriteLine($"   plugins that could not be read (contribute nothing, counted per rule 6): {unreadable.Count:N0}" + (unreadable.Count > 0 ? "  — " + string.Join(", ", unreadable) : ""));
if (bosOn)
{
    idx.Bos!.SetLoadOrder(lightness);
    var bosLayer = BosHook.Load(assets.LooseRoots, idx.Bos);
    var bosRep = BosHook.Apply(idx, bosLayer, bosPlugin);
    Console.WriteLine($"   BOS: {bosLayer.Inis.Count} *_SWAP.ini read ({bosLayer.LinesRead:N0} lines, {bosLayer.LinesFailed:N0} the game would refuse, {bosLayer.Anomalies.Count:N0} anomalies — bosprobe lists them)");
    foreach (var l in bosRep.Summary()) Console.WriteLine("   " + l);
}
else Console.WriteLine("   BOS: off — record bases (--bos on to apply the *_SWAP.ini layer)");
if (stateSpec is not null)
{
    var sx = SwitchIndex.Build(idx);
    sx.NameSwitches(idx.RefEditorId);
    try { cidx.State = StateArm.Parse(stateSpec, sx, switchMapPath is null ? null : SwitchMap.Read(switchMapPath)); }
    catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
    var changes = cidx.State.Changes(sx).ToList();
    Console.WriteLine($"   state {cidx.State.Name}: {sx.Switches.Count:N0} switches in the order; {cidx.State.Flips.Count} flipped — {changes.Count(c => c.After):N0} refs arrive, {changes.Count(c => !c.After):N0} depart; "
                    + "every arm's runtime enable state (R1) carries the flip");
}

string PluginName(int p) => p < 0 ? "(none)" : p == bosPlugin ? BosApplyReport.PseudoPluginName : pluginKey[p].FileName.String;
bool IsVanilla(int p) => p >= 0 && Vanilla.Contains(PluginName(p));
bool IsUssep(int p) => p >= 0 && string.Equals(PluginName(p), Ussep, StringComparison.OrdinalIgnoreCase);
string ClassOf(int p) => IsVanilla(p) ? "Bethesda" : IsUssep(p) ? "USSEP" : "mod";
string CellOf(FormKey c) => idx.CellName.TryGetValue(c, out var v) ? (v.Edid ?? c.ToString()) + (v.Interior ? " (int)" : " (ext)") : c.ToString();
string TypeOf(FormKey baseKey) => idx.BaseInfo.TryGetValue(baseKey, out var bi) ? bi.Type : "?";
string EdidOf(FormKey baseKey) => idx.BaseInfo.TryGetValue(baseKey, out var bi) && bi.Edid is { } e ? e : "";
string MeshOf(FormKey baseKey) => idx.BaseMesh.TryGetValue(baseKey, out var m) ? Path.GetFileName(m) : "";
string BaseLabel(FormKey baseKey) => $"{TypeOf(baseKey)} {EdidOf(baseKey)} <{MeshOf(baseKey)}>";
static float Rate(long hit, long total) => total == 0 ? 0 : 100f * hit / total;
string Src(ArmContact? a) => a is null ? "--" : $"{a.DepSrc}{a.HostSrc}";
string Contact(ArmContact? a) => a is null ? "(none within band)" : $"facing {a.Contact.FacingDistance:0.#}u (raw {a.Contact.Distance:0.#}u) pen {a.Contact.EffectivePenetration:0.#}u{(a.Contact.HostClosed ? "" : " (open host)")} up {a.Contact.NormalUp:0.00}";
void WriteGeomReport()
{
    if (geomReport is null || geomLines is null) return;
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(geomReport))!);
    File.WriteAllLines(geomReport, geomLines);
    Console.WriteLine($"wrote {geomLines.Count - 1:N0} mesh geometry lines -> {geomReport}");
}
string AgainstLabel(ArmContact? a) => a is null ? "" : $"{BaseLabel(cidx!.WinnerBase(a.Against))} {a.Against}";
string GeometryLine() => $"geometry: {meshesMeasured:N0} meshes measured (host soup collision {hostColl:N0} / render {hostRender:N0}; dependent soup collision {depColl:N0} / render {depRender:N0}; "
                       + $"no soup {meshNoSoup:N0}; incomplete collision decode {incompleteCollision:N0}; phantom-only {phantomOnly:N0}), {obndFallback:N0} bases on an OBND box (labelled 'o'), {noGeometry:N0} with nothing";

// ---------------------------------------------------------------------------------------
// --overlap-json: the milestone 6 ranker upgrade — 4.1's broad-phase hits through the narrow phase.
// ---------------------------------------------------------------------------------------
if (overlapJson is not null)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(overlapJson));
    var tally = new Dictionary<(string Scale, string Verdict), int>();
    var byPop = new Dictionary<(string Pop, string Verdict), int>();
    var depthBands = new Dictionary<string, int>();
    int n = 0, noGeom = 0, notPlaced = 0;
    var examples = new List<(string Verdict, float Rank, string Text)>();
    foreach (var row in doc.RootElement.EnumerateArray())
    {
        n++;
        FormKey later, other;
        try { later = FormKey.Factory(row.GetProperty("Later").GetString()!); other = FormKey.Factory(row.GetProperty("Other").GetString()!); }
        catch { notPlaced++; continue; }
        string scale = row.TryGetProperty("Scale", out var sc) ? sc.GetString() ?? "?" : "?";
        string pop = row.TryGetProperty("Population", out var pp) ? pp.GetString() ?? "?" : "?";
        float rank = row.TryGetProperty("Rank", out var rk) ? rk.GetSingle() : 0f;
        var pl = cidx.WinnerPlacement(later); var po = cidx.WinnerPlacement(other);
        if (pl is null || po is null) { notPlaced++; continue; }
        var bl = cidx.WinnerBase(later); var bo = cidx.WinnerBase(other);
        var boxL = cidx.WorldBox(bl, pl.Value.Snap); var boxO = cidx.WorldBox(bo, po.Value.Snap);
        if (boxL.IsEmpty || boxO.IsEmpty) { noGeom++; continue; }
        static float Vol(Aabb b) => (b.Max.X - b.Min.X) * (b.Max.Y - b.Min.Y) * (b.Max.Z - b.Min.Z);
        // The smaller box is the thing buried — the ranker's own convention — and it plays the dependent.
        bool laterIsDep = Vol(boxL) <= Vol(boxO);
        // Both sides are SURFACES here (the question is whether the two meet, not whether one holds the other), so a
        // render-only mesh stands in, labelled 'r' on the row.
        var c = laterIsDep ? cidx.PairContact(later, pl.Value.SnapIndex, bl, pl.Value.Snap, other, po.Value.SnapIndex, bo, po.Value.Snap, anySurface: true)
                           : cidx.PairContact(other, po.Value.SnapIndex, bo, po.Value.Snap, later, pl.Value.SnapIndex, bl, pl.Value.Snap, anySurface: true);
        if (c is null) { noGeom++; continue; }
        string v = c.Contact.EffectivePenetration > eps ? "penetrate" : c.Contact.Crosses ? "cross" : c.Contact.FacingDistance <= eps ? "touch" : "no contact";
        tally[(scale, v)] = tally.GetValueOrDefault((scale, v)) + 1;
        byPop[(pop, v)] = byPop.GetValueOrDefault((pop, v)) + 1;
        if (v == "penetrate")
        {
            string band = c.Contact.EffectivePenetration <= 16 ? "<=16u" : c.Contact.EffectivePenetration <= 64 ? "<=64u" : c.Contact.EffectivePenetration <= 256 ? "<=256u" : ">256u";
            depthBands[band] = depthBands.GetValueOrDefault(band) + 1;
        }
        if (examples.Count < 4000)
            examples.Add((v, rank, $"{BaseLabel(bl)} ({PluginName(pl.Value.Snap.Plugin)}) x {BaseLabel(bo)} ({PluginName(po.Value.Snap.Plugin)}) in {CellOf(pl.Value.Snap.Cell)}  {Contact(c)} [{Src(c)}]  {later} / {other}"));
    }
    Console.WriteLine($"\n==== THE 4.1 RANKER UPGRADE — {n:N0} judged overlap rows through the narrow phase (ε {eps:0.#}u) ====");
    Console.WriteLine($"   {GeometryLine()}");
    Console.WriteLine($"   not judged: {notPlaced:N0} rows whose refs are not placed under this order, {noGeom:N0} without geometry on a side");
    var verdicts = new[] { "no contact", "touch", "cross", "penetrate" };
    var scales = tally.Keys.Select(k => k.Scale).Distinct().OrderBy(s => s).ToList();
    Console.WriteLine($"   (touch: surfaces within ε and nothing crossed; cross: an edge of one passes through a face of the other, nothing deeper than ε along the contact normal — two walls through each other read here; penetrate: deeper than ε)");
    Console.WriteLine($"   {"scale of the buried box",-26} " + string.Join(" ", verdicts.Select(v => $"{v,12}")) + $" {"total",8}");
    foreach (var s in scales)
    {
        int tot = verdicts.Sum(v => tally.GetValueOrDefault((s, v)));
        Console.WriteLine($"   {s,-26} " + string.Join(" ", verdicts.Select(v => $"{tally.GetValueOrDefault((s, v)),12:N0}")) + $" {tot,8:N0}");
    }
    int all = tally.Values.Sum();
    Console.WriteLine($"   {"all",-26} " + string.Join(" ", verdicts.Select(v => $"{tally.Where(k => k.Key.Verdict == v).Sum(k => k.Value),12:N0}")) + $" {all,8:N0}");
    Console.WriteLine($"   shares: " + string.Join(", ", verdicts.Select(v => $"{v} {Rate(tally.Where(k => k.Key.Verdict == v).Sum(k => k.Value), all):0.0}%")));
    Console.WriteLine($"   by population: " + string.Join("; ", byPop.Keys.Select(k => k.Pop).Distinct().OrderBy(p => p).Select(p => $"{p}: " + string.Join(", ", verdicts.Select(v => $"{v} {byPop.GetValueOrDefault((p, v)):N0}")))));
    Console.WriteLine($"   penetration depth of the penetrating rows: " + string.Join(", ", new[] { "<=16u", "<=64u", "<=256u", ">256u" }.Select(b => $"{b} {depthBands.GetValueOrDefault(b):N0}")));
    foreach (var v in verdicts)
    {
        Console.WriteLine($"\n   ---- {v}: top {Math.Min(listN, 10)} by the broad-phase rank ----");
        foreach (var e in examples.Where(e => e.Verdict == v).OrderByDescending(e => e.Rank).Take(Math.Min(listN, 10)))
            Console.WriteLine($"   {e.Rank,7:0}  {e.Text}");
    }
    Console.WriteLine($"\nelapsed {sw.Elapsed.TotalSeconds:0.0}s");
    return 0;
}

// ---------------------------------------------------------------------------------------
// --labelled-mover: contact before vs after one plugin's moves — the labelled positive set for the primitive.
// ---------------------------------------------------------------------------------------
if (labelledMover is not null)
{
    int m = Array.FindIndex(pluginKey, k => string.Equals(k.FileName.String, labelledMover, StringComparison.OrdinalIgnoreCase));
    if (m < 0) { Console.Error.WriteLine($"--labelled-mover '{labelledMover}' is not in this order"); return 2; }
    Func<Snap, bool>? scope = interiorOnly ? s => s.Interior : null;
    var lskips = new ContactSkips();
    var before = cidx.PhysicalWithout(m, closure, scope, out var beforeIdx, lskips);
    long beforeDisabled = lskips.WinnerRuntimeDisabled, beforeUnresolved = lskips.WinnerEnableUnresolved;
    lskips.WinnerRuntimeDisabled = 0; lskips.WinnerEnableUnresolved = 0;
    var after = cidx.PhysicalWinners(scope, lskips);
    Console.WriteLine($"\n==== THE LABELLED SET — every ref {labelledMover} moved: contact BEFORE it vs AFTER it (ε {eps:0.#}u{(interiorOnly ? ", interiors only" : "")}) ====");
    Console.WriteLine($"   arms: {before.Count:N0} physical refs placed without the mover, {after.Count:N0} under the winner (any size — R3); left out as disabled at runtime (R1): {beforeDisabled:N0} / {lskips.WinnerRuntimeDisabled:N0}, chain unresolved {beforeUnresolved:N0} / {lskips.WinnerEnableUnresolved:N0}");
    string Class(ArmContact? a) => a is null ? "floating" : a.Contact.EffectivePenetration > eps ? "sunk" : a.Contact.FacingDistance <= eps ? "touching" : "floating";
    var conf = new Dictionary<(string Motion, string Before, string After), int>();
    var confSmall = new Dictionary<(string Before, string After), int>();
    var confVertical = new Dictionary<(string Before, string After), int>();
    var confVerticalFixed = new Dictionary<(string Before, string After), int>();
    int tested = 0, movedFar = 0, verticalOnly = 0;
    var rowsOut = new List<object>();
    var examples = new List<(string Key, float Dz, string Text)>();
    int seen = 0;
    foreach (var (key, snaps) in idx.History.Refs)
    {
        var win = snaps[^1];
        if (win.Plugin != m || win.Disabled || win.Deleted || idx.IsDeletedNow(key)) continue;
        if (!beforeIdx.TryGetValue(key, out var bi)) continue;
        var bs = snaps[bi];
        if (bs.Space != win.Space) continue;
        if (scope is not null && !scope(win)) continue;
        var wb = cidx.WinnerBase(key); var bb = cidx.ArmBase(key, m, closure);
        if (bb.IsNull || !cidx.IsDependentType(wb)) continue;
        var winBox = cidx.WorldBox(wb, win); var befBox = cidx.WorldBox(bb, bs);
        if (winBox.IsEmpty || befBox.IsEmpty) continue;
        if (ContactIndex.ScaleOf(winBox) == OverlapScale.Architecture) continue;
        if (++seen % 500 == 0) Console.WriteLine($"   ... {seen:N0} moved refs, {sw.Elapsed.TotalSeconds:0}s");
        var cb = cidx.BestContact(key, bi, bb, bs, befBox, before, k => beforeIdx.TryGetValue(k, out var i) ? (idx.History.Of(k)![i], i) : null, (k, _) => cidx.ArmBase(k, m, closure), out _);
        var ca = cidx.BestContact(key, snaps.Count - 1, wb, win, winBox, after, cidx.WinnerPlacement, (k, _) => cidx.WinnerBase(k), out _);
        tested++;
        float dx = win.Pos.X - bs.Pos.X, dy = win.Pos.Y - bs.Pos.Y, dz = win.Pos.Z - bs.Pos.Z;
        float dxy = MathF.Sqrt(dx * dx + dy * dy);
        bool far = PlacementHistory.Dist(bs.Pos, win.Pos) > PlacementHistory.MoveThreshold;
        if (far) movedFar++;
        bool vertical = dxy < 1f && MathF.Abs(dz) >= 1f;
        if (vertical) verticalOnly++;
        var motion = cidx.MotionOf(wb).ToString();
        string b = Class(cb), a = Class(ca);
        conf[(motion, b, a)] = conf.GetValueOrDefault((motion, b, a)) + 1;
        if (!far) confSmall[(b, a)] = confSmall.GetValueOrDefault((b, a)) + 1;
        if (vertical) { confVertical[(b, a)] = confVertical.GetValueOrDefault((b, a)) + 1; if (motion == "Fixed") confVerticalFixed[(b, a)] = confVerticalFixed.GetValueOrDefault((b, a)) + 1; }
        rowsOut.Add(new { key = key.ToString(), @base = BaseLabel(wb), cell = CellOf(win.Cell), motion, dx, dy, dz, far, before = b, after = a,
                          beforeDist = cb?.Contact.FacingDistance, beforePen = cb?.Contact.EffectivePenetration, beforeUp = cb?.Contact.NormalUp, beforeAgainst = cb?.Against.ToString(), beforeAgainstBase = AgainstLabel(cb), beforeSrc = Src(cb),
                          afterDist = ca?.Contact.FacingDistance, afterPen = ca?.Contact.EffectivePenetration, afterUp = ca?.Contact.NormalUp, afterAgainst = ca?.Against.ToString(), afterAgainstBase = AgainstLabel(ca), src = Src(ca) });
        if (examples.Count < 20000) examples.Add(($"{b}->{a}", dz, $"{BaseLabel(wb)} in {CellOf(win.Cell)} moved ({dx:0.#},{dy:0.#},{dz:0.#})  before {Contact(cb)} vs {AgainstLabel(cb)} [{Src(cb)}]  after {Contact(ca)} vs {AgainstLabel(ca)} [{Src(ca)}]  {key}"));
    }
    Console.WriteLine($"   refs tested: {tested:N0} (clutter- and furniture-scale, placed in both arms, same space); {movedFar:N0} moved over {PlacementHistory.MoveThreshold:0}u, {verticalOnly:N0} moved vertically only");
    var classes = new[] { "touching", "floating", "sunk" };
    void Confusion(string title, Func<string, string, int> get)
    {
        Console.WriteLine($"\n   {title}");
        Console.WriteLine($"   {"before \\ after",-18} " + string.Join(" ", classes.Select(c => $"{c,10}")) + $" {"total",8}");
        foreach (var b in classes)
            Console.WriteLine($"   {b,-18} " + string.Join(" ", classes.Select(a => $"{get(b, a),10:N0}")) + $" {classes.Sum(a => get(b, a)),8:N0}");
        int broken = classes.Where(b => b != "touching").Sum(b => classes.Sum(a => get(b, a)));
        int fixedNow = classes.Where(b => b != "touching").Sum(b => get(b, "touching"));
        int unbroken = classes.Where(a => a != "touching").Sum(a => get("touching", a));
        int okBefore = classes.Sum(a => get("touching", a));
        Console.WriteLine($"   agreement: of {broken:N0} floating/sunk before, {fixedNow:N0} touch after ({Rate(fixedNow, broken):0.0}%); of {okBefore:N0} touching before, {unbroken:N0} float/sink after ({Rate(unbroken, okBefore):0.0}%)");
    }
    Confusion("all tested refs", (b, a) => conf.Where(k => k.Key.Before == b && k.Key.After == a).Sum(k => k.Value));
    foreach (var mo in new[] { "Fixed", "Movable", "Unknown" })
        Confusion($"{mo} dependents", (b, a) => conf.GetValueOrDefault((mo, b, a)));
    Confusion($"moves of {PlacementHistory.MoveThreshold:0}u or less (the nudges driftprobe calls jitter)", (b, a) => confSmall.GetValueOrDefault((b, a)));
    Confusion("VERTICAL-ONLY moves (under 1 u sideways, 1 u or more up or down) — the shape a floating or sunken fix takes; the cleanest labelled subset", (b, a) => confVertical.GetValueOrDefault((b, a)));
    Confusion("...vertical-only, Fixed dependents", (b, a) => confVerticalFixed.GetValueOrDefault((b, a)));
    // Most of USSEP's vertical nudges are smaller than ε, and a float smaller than ε reads "touching" on both sides of
    // the fix. So the same subset is read again at an ε matched to the nudge, and threshold-free: did the fix bring the
    // thing CLOSER to a perfect contact (the larger of its gap and its penetration shrank)?
    var vrows = rowsOut.Select(o => (dynamic)o).Where(r => MathF.Sqrt((float)r.dx * (float)r.dx + (float)r.dy * (float)r.dy) < 1f && MathF.Abs((float)r.dz) >= 1f).ToList();
    static string ClassAt(float? dist, float? pen, float e) => dist is null ? "floating" : pen > e ? "sunk" : dist <= e ? "touching" : "floating";
    foreach (var e in new[] { 1f, 2f })
    {
        var cv = new Dictionary<(string, string), int>();
        foreach (var r in vrows)
        {
            string b = ClassAt((float?)r.beforeDist, (float?)r.beforePen, e), a = ClassAt((float?)r.afterDist, (float?)r.afterPen, e);
            cv[(b, a)] = cv.GetValueOrDefault((b, a)) + 1;
        }
        Confusion($"...vertical-only, read at ε = {e:0}u (the size of the nudge, not the rule's ε)", (b, a) => cv.GetValueOrDefault((b, a)));
    }
    static float Score(float? dist, float? pen) => dist is null ? 128f : MathF.Max((float)dist, (float)(pen ?? 0f));
    int closer = 0, further = 0, same = 0;
    foreach (var r in vrows)
    {
        float sb = Score((float?)r.beforeDist, (float?)r.beforePen), sa = Score((float?)r.afterDist, (float?)r.afterPen);
        if (sa < sb - 0.25f) closer++; else if (sa > sb + 0.25f) further++; else same++;
    }
    Console.WriteLine($"\n   vertical-only, threshold-free: the deviation from a perfect contact (the larger of gap and penetration) shrank for {closer:N0} of {vrows.Count:N0} ({Rate(closer, vrows.Count):0.0}%), grew for {further:N0} ({Rate(further, vrows.Count):0.0}%), stayed within 0.25 u for {same:N0}");
    Console.WriteLine($"\n   vertical-only moves by size: " + string.Join(", ", new[] { (0f, 2f), (2f, 4f), (4f, 8f), (8f, 16f), (16f, 1e9f) }.Select(band =>
        $"{band.Item1:0}–{(band.Item2 > 1e8 ? "" : band.Item2.ToString("0"))}u {rowsOut.Count(o => { dynamic r = o; return MathF.Sqrt(r.dx * r.dx + r.dy * r.dy) < 1f && MathF.Abs(r.dz) >= 1f && MathF.Abs(r.dz) >= band.Item1 && MathF.Abs(r.dz) < band.Item2; }):N0}")));
    foreach (var cls in new[] { "floating->touching", "sunk->touching", "floating->floating", "sunk->sunk", "touching->floating", "touching->sunk" })
    {
        Console.WriteLine($"\n   ---- {cls}: {Math.Min(listN, 8)} examples ----");
        foreach (var e in examples.Where(e => e.Key == cls).Take(Math.Min(listN, 8))) Console.WriteLine($"   {e.Text}");
    }
    if (jsonPath is not null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonPath))!);
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(new { mover = labelledMover, eps, tested, movedFar, verticalOnly, rows = rowsOut }, new JsonSerializerOptions { WriteIndented = false, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        Console.WriteLine($"\nwrote {jsonPath}");
    }
    Console.WriteLine($"\n{GeometryLine()}");
    WriteGeomReport();
    Console.WriteLine($"elapsed {sw.Elapsed.TotalSeconds:0.0}s");
    return 0;
}

// ---------------------------------------------------------------------------------------
// --absolute: every clutter- and furniture-scale ref against its neighbours as shipped.
// ---------------------------------------------------------------------------------------
if (absolute)
{
    Func<Snap, bool>? scope = interiorOnly ? s => s.Interior : null;
    var after = cidx.PhysicalWinners(scope);
    Console.WriteLine($"\n==== THE ABSOLUTE FLOOR — every clutter- and furniture-scale ref against everything physical near it, as shipped (ε {eps:0.#}u{(interiorOnly ? ", interiors only" : "")}) ====");
    var tally = new Dictionary<(string Domain, string Motion, string Scale, string Class), int>();
    var cityTally = new Dictionary<(string City, string Class), int>();
    var meshTally = new Dictionary<(string Mesh, string Class), int>();
    string[] cities = { "Whiterun", "Solitude", "Riften", "Windhelm", "Markarth", "Falkreath", "Morthal", "Dawnstar", "Winterhold" };
    int tested = 0, seen = 0;
    var floating = new List<(float Dist, string Text)>();
    var sunk = new List<(float Pen, string Text)>();
    foreach (var (key, snaps) in idx.History.Refs)
    {
        var win = snaps[^1];
        if (win.Disabled || win.Deleted || idx.IsDeletedNow(key)) continue;
        if (scope is not null && !scope(win)) continue;
        var b = cidx.WinnerBase(key);
        if (!cidx.IsDependentType(b)) continue;
        var box = cidx.WorldBox(b, win);
        if (box.IsEmpty) continue;
        var scale = ContactIndex.ScaleOf(box);
        if (scale == OverlapScale.Architecture) continue;
        if (++seen % 20000 == 0) Console.WriteLine($"   ... {seen:N0} refs, {sw.Elapsed.TotalSeconds:0}s");
        var c = cidx.BestContact(key, snaps.Count - 1, b, win, box, after, cidx.WinnerPlacement, (k, _) => cidx.WinnerBase(k), out _);
        tested++;
        string cls = c is null ? "floating" : c.Contact.EffectivePenetration > eps ? "sunk" : c.Contact.FacingDistance <= eps ? "touching" : "floating";
        string domain = win.Interior ? "interior" : "exterior";
        string motion = cidx.MotionOf(b).ToString();
        tally[(domain, motion, scale.ToString(), cls)] = tally.GetValueOrDefault((domain, motion, scale.ToString(), cls)) + 1;
        var cellName = idx.CellName.TryGetValue(win.Cell, out var cn) ? cn.Edid ?? "" : "";
        var city = cities.FirstOrDefault(ci => cellName.StartsWith(ci, StringComparison.OrdinalIgnoreCase));
        if (city is not null && win.Interior) cityTally[(city, cls)] = cityTally.GetValueOrDefault((city, cls)) + 1;
        var mesh = MeshOf(b);
        meshTally[(mesh, cls)] = meshTally.GetValueOrDefault((mesh, cls)) + 1;
        if (cls == "floating" && c is not null && floating.Count < 50000) floating.Add((c.Contact.FacingDistance, $"{BaseLabel(b)} in {CellOf(win.Cell)} {Contact(c)} [{Src(c)}] {key}"));
        if (cls == "sunk" && sunk.Count < 50000) sunk.Add((c!.Contact.EffectivePenetration, $"{BaseLabel(b)} in {CellOf(win.Cell)} {Contact(c)} [{Src(c)}] {key}"));
    }
    var classes = new[] { "touching", "floating", "sunk" };
    Console.WriteLine($"   refs tested: {tested:N0}");
    void Table(string title, Func<string, int> get)
    {
        int tot = classes.Sum(get);
        Console.WriteLine($"   {title,-40} " + string.Join(" ", classes.Select(c => $"{c} {get(c),8:N0} ({Rate(get(c), tot),5:0.0}%)")) + $"   of {tot:N0}");
    }
    foreach (var d in new[] { "interior", "exterior" })
    {
        Console.WriteLine($"\n   ---- {d} ----");
        Table("all", c => tally.Where(k => k.Key.Domain == d && k.Key.Class == c).Sum(k => k.Value));
        foreach (var mo in new[] { "Fixed", "Movable", "Unknown" })
            Table($"{mo}", c => tally.Where(k => k.Key.Domain == d && k.Key.Motion == mo && k.Key.Class == c).Sum(k => k.Value));
        foreach (var sc in new[] { "Clutter", "Furniture" })
            Table($"{sc}-scale", c => tally.Where(k => k.Key.Domain == d && k.Key.Scale == sc && k.Key.Class == c).Sum(k => k.Value));
        foreach (var mo in new[] { "Fixed", "Movable" })
            foreach (var sc in new[] { "Clutter", "Furniture" })
                Table($"{mo} {sc}-scale", c => tally.GetValueOrDefault((d, mo, sc, c)));
    }
    Console.WriteLine($"\n   ---- city interiors (cell editor id prefix) ----");
    foreach (var ci in cities) Table(ci, c => cityTally.GetValueOrDefault((ci, c)));
    Console.WriteLine($"\n   ---- meshes most often floating / sunk (what the floor is made of) ----");
    foreach (var cls in new[] { "floating", "sunk" })
    {
        Console.WriteLine($"   {cls}:");
        foreach (var g in meshTally.Where(k => k.Key.Class == cls).OrderByDescending(k => k.Value).Take(Math.Min(listN, 15)))
        {
            int tot = classes.Sum(c => meshTally.GetValueOrDefault((g.Key.Mesh, c)));
            Console.WriteLine($"      {g.Value,7:N0} of {tot,7:N0} ({Rate(g.Value, tot),5:0.0}%)  {g.Key.Mesh}");
        }
    }
    Console.WriteLine($"\n   ---- floating, by gap (largest first — what 'floating' looks like at the top) ----");
    foreach (var f in floating.OrderByDescending(f => f.Dist).Take(Math.Min(listN, 12))) Console.WriteLine($"   {f.Text}");
    Console.WriteLine($"   ---- floating, by gap (smallest first — what a near miss looks like) ----");
    foreach (var f in floating.OrderBy(f => f.Dist).Take(Math.Min(listN, 12))) Console.WriteLine($"   {f.Text}");
    Console.WriteLine($"   ---- sunk, deepest first ----");
    foreach (var s in sunk.OrderByDescending(s => s.Pen).Take(Math.Min(listN, 12))) Console.WriteLine($"   {s.Text}");
    Console.WriteLine($"\n{GeometryLine()}");
    Console.WriteLine($"Limits: 'floating' means no physical reference within ε — terrain is not a reference, so exterior refs on the ground read floating; interiors are the floor.");
    Console.WriteLine($"elapsed {sw.Elapsed.TotalSeconds:0.0}s");
    return 0;
}

// ---------------------------------------------------------------------------------------
// The differential pass — every moved host, every dependent within reach, two arms.
// ---------------------------------------------------------------------------------------
var skips = new ContactSkips();
var hosts = cidx.MovedHosts(skips, closure);
Console.WriteLine($"\n==== 1. PLUMBING ====");
Console.WriteLine($"   hosts (physical, furniture-scale or larger, whose last placement step is a move, a relocation or a removal): {hosts.Count:N0}");
foreach (var ch in Enum.GetValues<HostChange>())
    Console.WriteLine($"      {ch,-10} {hosts.Count(h => h.Change == ch),8:N0}   by mover class: " + string.Join(", ", new[] { "Bethesda", "USSEP", "mod" }.Select(c => $"{c} {hosts.Count(h => h.Change == ch && ClassOf(h.Mover) == c):N0}"))
                    + (ch == HostChange.Removed ? $"   (of which through an enable parent a later plugin disabled — R1: {hosts.Count(h => h.RemovedViaParent):N0})" : ""));
Console.WriteLine($"      set aside (rule 6): {skips.HostNoGeometry:N0} without geometry, {skips.HostNoCollision:N0} without solid collision (nothing rests on them in the engine either), {skips.HostVolume:N0} volume-scale, {skips.HostTooSmall:N0} under {cidx.MinHostDim:0}u (clutter that moved — a dependent, not a host)");
Console.WriteLine($"      runtime state (R1): {skips.HostRuntimeDisabled:N0} hosts disabled at runtime in the arm without their mover (never there to hold anything), {skips.HostEnableUnresolved:N0} whose enable chain the order cannot follow");
Console.WriteLine($"   top movers: " + string.Join(", ", hosts.GroupBy(h => h.Mover).OrderByDescending(g => g.Count()).Take(10).Select(g => $"{PluginName(g.Key)} {g.Count():N0}")));
{
    var (flagOnly, nameOnly) = cidx.AuditMarkers();
    Console.WriteLine($"   marker rule (R2): {idx.BaseIsMarker.Count:N0} bases carry the Is-Marker flag; the old name heuristics disagree on {flagOnly.Count:N0} flagged bases with ordinary names"
                    + $" and {nameOnly.Count:N0} named 'marker' (or MarkerX*.nif) without the flag — reported, not used");
    Console.WriteLine($"      flag only, by type: " + string.Join(", ", flagOnly.GroupBy(k => TypeOf(k)).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key} {g.Count():N0}"))
                    + $"; e.g. " + string.Join("; ", flagOnly.Take(6).Select(k => $"{BaseLabel(k)} {k}")));
    Console.WriteLine($"      name only, by type: " + string.Join(", ", nameOnly.GroupBy(k => TypeOf(k)).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key} {g.Count():N0}"))
                    + $"; e.g. " + string.Join("; ", nameOnly.Take(6).Select(k => $"{BaseLabel(k)} {k}")));
}
var depHash = cidx.DependentSnaps(skips);
Func<Snap, bool>? winScope = null;
var winHash = cidx.PhysicalWinners(winScope, skips);
Console.WriteLine($"   dependents hashed: {depHash.Count:N0} undeleted snaps of clutter- and furniture-scale physical refs — the arm decides which exist at runtime ({skips.DepNoGeometry:N0} winning refs without geometry, {skips.DepVolume:N0} over {cidx.MaxDependentDim:0}u set aside)");
Console.WriteLine($"   winner arm: {winHash.Count:N0} physical refs placed, undeleted and enabled at runtime under the winner, any size (R3); left out: {skips.WinnerRuntimeDisabled:N0} disabled at runtime (R1), {skips.WinnerEnableUnresolved:N0} with an unresolved chain  ({sw.Elapsed.TotalSeconds:0.0}s)");

var rows = new List<ContactRow>();
int done = 0;
foreach (var h in hosts.OrderBy(h => h.Previous.Space.ToString()).ThenBy(h => h.Previous.Cell.ToString()))
{
    rows.AddRange(cidx.Judge(h, depHash, winHash, closure, skips, keepNoRelation: allRows));
    if (++done % 2000 == 0) Console.WriteLine($"   ... {done:N0}/{hosts.Count:N0} hosts, {rows.Count:N0} rows, {sw.Elapsed.TotalSeconds:0}s");
}
Console.WriteLine($"   pairs within the box band: {rows.Count + skips.BoxesApart:N0} tested; set aside: {skips.BoxesApart:N0} boxes further apart than {cidx.BoxBand:0}u, "
                + $"{skips.DepIntroducedByMover:N0} dependents the mover introduced, {skips.DepOtherSpace:N0} in another space, "
                + $"{skips.DepAbsentInArm:N0} dependents disabled at runtime in the arm without the mover (R1), {skips.DepEnableUnresolved:N0} with an unresolved chain  ({sw.Elapsed.TotalSeconds:0.0}s)");
Console.WriteLine($"   {GeometryLine()}");
var mstt = idx.BaseInfo.Where(kv => kv.Value.Type == "MoveableStatic").Select(kv => cidx.GeomOf(kv.Key)).Where(g => g is not null).ToList();
var motions = mstt.SelectMany(g => g!.SolidBodies.Select(b => b.MotionSystem == "" ? "(unread)" : b.MotionSystem)).GroupBy(x => x).OrderByDescending(g => g.Count()).ToList();
Console.WriteLine($"   MoveableStatic bases with geometry: {mstt.Count:N0}; their solid bodies' motion systems: " + string.Join(", ", motions.Select(g => $"{g.Key} {g.Count():N0}"))
                + $"; classified movable {idx.BaseInfo.Count(kv => kv.Value.Type == "MoveableStatic" && cidx.MotionOf(kv.Key) == DependentMotion.Movable):N0}, fixed {idx.BaseInfo.Count(kv => kv.Value.Type == "MoveableStatic" && cidx.MotionOf(kv.Key) == DependentMotion.Fixed):N0}");

var rel = rows.Where(r => r.Relation != ContactRelation.None).ToList();
var near = rows.Where(r => r.Relation == ContactRelation.None && r.Verdict == ContactVerdict.NoRelation).ToList();
var unj = rows.Where(r => r.Verdict == ContactVerdict.Unjudgeable).ToList();
DependencyPopulation Pop(ContactRow r) => DetectorModel.ClassifyDependencyPopulation(IsVanilla(r.Host.Mover), IsVanilla(r.DepAuthor), IsUssep(r.Host.Mover));
bool Fixed(ContactRow r) => r.Motion == DependentMotion.Fixed;
bool IsClass(ContactRow r) => r.Verdict is ContactVerdict.Stranded or ContactVerdict.Orphaned;
bool Interior(ContactRow r) => r.DepAuthoring.Interior;
float RankOf(ContactRow r) => r.Verdict == ContactVerdict.Sunk ? r.Facts.WinnerPenetration : r.Facts.WinnerDistance;

Console.WriteLine($"\n==== 2. RELATIONSHIPS AS AUTHORED — {rel.Count:N0} pairs in contact in the arm without the mover ({near.Count:N0} near misses within {cidx.NearBand:0}u kept for sensitivity; {unj.Count:N0} unjudgeable) ====");
foreach (var rr in new[] { ContactRelation.Supported, ContactRelation.Attached, ContactRelation.Hung })
    Console.WriteLine($"   {rr,-10} {rel.Count(r => r.Relation == rr),8:N0}   Fixed {rel.Count(r => r.Relation == rr && Fixed(r)),7:N0}  Movable {rel.Count(r => r.Relation == rr && r.Motion == DependentMotion.Movable),7:N0}  Unknown {rel.Count(r => r.Relation == rr && r.Motion == DependentMotion.Unknown),5:N0}"
                    + $"   interior {rel.Count(r => r.Relation == rr && Interior(r)),7:N0}  exterior {rel.Count(r => r.Relation == rr && !Interior(r)),7:N0}");
Console.WriteLine($"   geometry sources on the relationship rows (dependent, host): " + string.Join(", ", rel.GroupBy(r => Src(r.Authoring)).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count():N0}")));
Console.WriteLine($"   unjudgeable by reason: " + string.Join(", ", unj.GroupBy(r => r.Reason).Select(g => $"{g.Key} {g.Count():N0}")));

Console.WriteLine($"\n==== 3. VERDICTS over the relationship rows ====");
Console.WriteLine($"   {"verdict",-14} {"all",8} {"Fixed",8} {"Movable",8} {"Unknown",8} {"interior",9} {"exterior",9}   {"Fixed interior",15}");
foreach (var v in new[] { ContactVerdict.Stranded, ContactVerdict.Orphaned, ContactVerdict.Sunk, ContactVerdict.ReSupported, ContactVerdict.MovedTogether, ContactVerdict.Removed, ContactVerdict.Unjudgeable })
{
    var vr = rel.Where(r => r.Verdict == v).ToList();
    Console.WriteLine($"   {v,-14} {vr.Count,8:N0} {vr.Count(Fixed),8:N0} {vr.Count(r => r.Motion == DependentMotion.Movable),8:N0} {vr.Count(r => r.Motion == DependentMotion.Unknown),8:N0} {vr.Count(Interior),9:N0} {vr.Count(r => !Interior(r)),9:N0}   {vr.Count(r => Fixed(r) && Interior(r)),15:N0}");
}
Console.WriteLine($"   ReSupported by what: host at its new place {rel.Count(r => r.Verdict == ContactVerdict.ReSupported && r.Facts.WinnerContactIsHost):N0}, another reference {rel.Count(r => r.Verdict == ContactVerdict.ReSupported && !r.Facts.WinnerContactIsHost):N0}; "
                + $"Sunk into: the host {rel.Count(r => r.Verdict == ContactVerdict.Sunk && r.Facts.WinnerContactIsHost):N0}, something else {rel.Count(r => r.Verdict == ContactVerdict.Sunk && !r.Facts.WinnerContactIsHost):N0}");
bool OverCap(ContactRow r) => r.Winner is { } w && w.AgainstMaxDim > cidx.MaxHostDim;
Console.WriteLine($"   the folds, over the relationship rows: R1 Unjudgeable / RuntimeDisabled {rel.Count(r => r.Reason == ContactBlocker.RuntimeDisabled):N0} (Fixed mod-mover {rel.Count(r => r.Reason == ContactBlocker.RuntimeDisabled && Fixed(r) && Pop(r) == DependencyPopulation.ModMover):N0}), "
                + $"EnableChainUnresolved {rel.Count(r => r.Reason == ContactBlocker.EnableChainUnresolved):N0}; "
                + $"R3 ReSupported by an against-ref over the {cidx.MaxHostDim:0}u moved-host cap {rel.Count(r => r.Verdict == ContactVerdict.ReSupported && OverCap(r)):N0} (Fixed mod-mover {rel.Count(r => r.Verdict == ContactVerdict.ReSupported && OverCap(r) && Fixed(r) && Pop(r) == DependencyPopulation.ModMover):N0}), "
                + $"Sunk into one {rel.Count(r => r.Verdict == ContactVerdict.Sunk && OverCap(r)):N0}; "
                + $"R4 Unjudgeable / DepthExceedsObject {rel.Count(r => r.Reason == ContactBlocker.DepthExceedsObject):N0} (Fixed mod-mover {rel.Count(r => r.Reason == ContactBlocker.DepthExceedsObject && Fixed(r) && Pop(r) == DependencyPopulation.ModMover):N0})");
Console.WriteLine($"   by host change: " + string.Join("; ", Enum.GetValues<HostChange>().Select(ch => $"{ch}: class {rel.Count(r => r.Host.Change == ch && IsClass(r)):N0} of {rel.Count(r => r.Host.Change == ch):N0}")));

Console.WriteLine($"\n==== 4. POPULATION SPLIT (rule 12) — (mover class, dependent's author class) over the relationship rows ====");
Console.WriteLine($"   {"population",-30} {"rows",7} {"Stranded",9} {"Orphaned",9} {"Sunk",6} {"ReSupp.",8} {"MovedTog.",9} {"Removed",8} {"Unjudg.",8}   {"FIXED Stranded",14} {"FIXED Orphaned",14} {"FIXED Sunk",10}   {"..interior",10}");
foreach (var pop in Enum.GetValues<DependencyPopulation>())
{
    var pr = rel.Where(r => Pop(r) == pop).ToList();
    Console.WriteLine($"   {pop,-30} {pr.Count,7:N0} {pr.Count(r => r.Verdict == ContactVerdict.Stranded),9:N0} {pr.Count(r => r.Verdict == ContactVerdict.Orphaned),9:N0} {pr.Count(r => r.Verdict == ContactVerdict.Sunk),6:N0}"
                    + $" {pr.Count(r => r.Verdict == ContactVerdict.ReSupported),8:N0} {pr.Count(r => r.Verdict == ContactVerdict.MovedTogether),9:N0} {pr.Count(r => r.Verdict == ContactVerdict.Removed),8:N0} {pr.Count(r => r.Verdict == ContactVerdict.Unjudgeable),8:N0}"
                    + $"   {pr.Count(r => Fixed(r) && r.Verdict == ContactVerdict.Stranded),14:N0} {pr.Count(r => Fixed(r) && r.Verdict == ContactVerdict.Orphaned),14:N0} {pr.Count(r => Fixed(r) && r.Verdict == ContactVerdict.Sunk),10:N0}"
                    + $"   {pr.Count(r => Fixed(r) && IsClass(r) && Interior(r)),10:N0}");
}
Console.WriteLine($"   by mover class x author class (class rows, Fixed): " + string.Join("; ", rel.Where(r => Fixed(r) && IsClass(r)).GroupBy(r => (ClassOf(r.Host.Mover), ClassOf(r.DepAuthor))).OrderByDescending(g => g.Count()).Select(g => $"{g.Key.Item1} moved / {g.Key.Item2} authored {g.Count():N0}")));
var classRows = rel.Where(r => Fixed(r) && IsClass(r) && Pop(r) == DependencyPopulation.ModMover).ToList();
Console.WriteLine($"   the class (mod mover, Fixed, Stranded + Orphaned): {classRows.Count:N0} — Seen {classRows.Count(r => r.Seen):N0} (the dependent was in the mover's editor) / Blind {classRows.Count(r => !r.Seen):N0}; "
                + $"author after the mover {classRows.Count(r => r.AuthorAfterMover):N0}; mover's side overrode the dependent without moving it {classRows.Count(r => r.DepTouchedByMoverSide):N0}");

void PrintRow(ContactRow r)
{
    Console.WriteLine($"      {r.Verdict,-12} {r.Relation,-9} {r.Motion,-7} {(Interior(r) ? "int" : "ext"),-3} rank {RankOf(r),6:0.#}u  dependent {BaseLabel(r.DepBase)} {r.Dependent}");
    Console.WriteLine($"         host {BaseLabel(r.Host.PrevBase)} {r.Host.Key} {r.Host.Change} by {PluginName(r.Host.Mover)}{(r.Host.Change == HostChange.Moved ? $" ({PlacementHistory.Dist(r.Host.Previous.Pos, r.Host.Winner.Pos):0}u)" : "")} in {CellOf(r.DepAuthoring.Cell)}; dependent placed by {PluginName(r.DepAuthor)}"
                    + $" [{(r.Seen ? "seen" : "blind")}{(r.AuthorAfterMover ? ", author after mover" : "")}{(r.DepTouchedByMoverSide ? ", mover's side overrode it" : "")}]");
    Console.WriteLine($"         authoring {Contact(r.Authoring)} [{Src(r.Authoring)}]; winner {Contact(r.Winner)}{(r.Winner is { } w ? (w.Against == r.Host.Key ? " (the host)" : $" (against {BaseLabel(cidx!.WinnerBase(w.Against))})") : "")} [{Src(r.Winner)}]");
}

var floor = rel.Where(r => Pop(r) == DependencyPopulation.BethesdaFloor).ToList();
Console.WriteLine($"\n==== 5. THE FLOOR — Bethesda-on-Bethesda relationship rows: {floor.Count:N0} (Fixed Stranded {floor.Count(r => Fixed(r) && r.Verdict == ContactVerdict.Stranded):N0}, Orphaned {floor.Count(r => Fixed(r) && r.Verdict == ContactVerdict.Orphaned):N0}, Sunk {floor.Count(r => Fixed(r) && r.Verdict == ContactVerdict.Sunk):N0}; Movable class {floor.Count(r => !Fixed(r) && IsClass(r)):N0}) — control candidates ====");
foreach (var r in floor.Where(r => IsClass(r) || r.Verdict == ContactVerdict.Sunk).OrderByDescending(Fixed).ThenByDescending(RankOf).Take(listN)) PrintRow(r);

var ussep = rel.Where(r => Pop(r) == DependencyPopulation.UssepMover).ToList();
Console.WriteLine($"\n==== 6. USSEP AS MOVER — {ussep.Count:N0} relationship rows (Fixed Stranded {ussep.Count(r => Fixed(r) && r.Verdict == ContactVerdict.Stranded):N0}, Orphaned {ussep.Count(r => Fixed(r) && r.Verdict == ContactVerdict.Orphaned):N0}, Sunk {ussep.Count(r => Fixed(r) && r.Verdict == ContactVerdict.Sunk):N0}, ReSupported {ussep.Count(r => r.Verdict == ContactVerdict.ReSupported):N0}, MovedTogether {ussep.Count(r => r.Verdict == ContactVerdict.MovedTogether):N0}) ====");
foreach (var r in ussep.Where(r => Fixed(r) && (IsClass(r) || r.Verdict == ContactVerdict.Sunk)).OrderByDescending(Interior).ThenByDescending(RankOf).Take(Math.Min(listN, 12))) PrintRow(r);

Console.WriteLine($"\n==== 7. THE CLASS — mod mover, FIXED dependents, Stranded + Orphaned: {classRows.Count:N0} rows ({classRows.Count(Interior):N0} interior) over {classRows.Select(r => r.Dependent).Distinct().Count():N0} distinct dependents; Sunk beside: {rel.Count(r => Fixed(r) && r.Verdict == ContactVerdict.Sunk && Pop(r) == DependencyPopulation.ModMover):N0} ====");
Console.WriteLine($"   what a Fixed Stranded row IS, in game (2026-09-03, 3 of 3 runtime-real rows): an object hanging in the air where its host was — it FLOATS IN AIR; what a Fixed Orphaned row looks like in game is unseen");
Console.WriteLine($"   ---- by (mover, dependent's author) pair; CANDIDATES, each row's runtime existence resolved from the records (R1), not watched in game ----");
var pairs = rel.Where(r => Fixed(r) && Pop(r) == DependencyPopulation.ModMover).GroupBy(r => (Mover: PluginName(r.Host.Mover), Author: PluginName(r.DepAuthor)))
               .OrderByDescending(g => g.Count(IsClass)).ThenByDescending(g => g.Count()).ToList();
Console.WriteLine($"   {"Stranded",8} {"Orphaned",8} {"Sunk",6} {"ReSupp",7} {"MovedTog",8} {"distinct deps",13}  {"int/ext of class",16}  mover  moved a host under a dependent placed by  author");
foreach (var g in pairs.Where(g => g.Any(IsClass)).Take(listN))
    Console.WriteLine($"   {g.Count(r => r.Verdict == ContactVerdict.Stranded),8:N0} {g.Count(r => r.Verdict == ContactVerdict.Orphaned),8:N0} {g.Count(r => r.Verdict == ContactVerdict.Sunk),6:N0} {g.Count(r => r.Verdict == ContactVerdict.ReSupported),7:N0} {g.Count(r => r.Verdict == ContactVerdict.MovedTogether),8:N0} {g.Where(IsClass).Select(r => r.Dependent).Distinct().Count(),13:N0}  {g.Count(r => IsClass(r) && Interior(r)),7:N0}/{g.Count(r => IsClass(r) && !Interior(r)),-7:N0}  {g.Key.Mover}  x  {g.Key.Author}");
Console.WriteLine($"\n   ---- the ranker: top {listN} Fixed Stranded + Orphaned, interiors first, by how far the nearest thing is (nothing within ε; the number is the gap to whatever is nearest within {reach:0}u) ----");
foreach (var r in classRows.OrderByDescending(Interior).ThenByDescending(RankOf).Take(listN)) PrintRow(r);
Console.WriteLine($"\n   ---- Orphaned only (attachment): top {Math.Min(listN, 10)} ----");
foreach (var r in classRows.Where(r => r.Verdict == ContactVerdict.Orphaned).OrderByDescending(Interior).ThenByDescending(RankOf).Take(Math.Min(listN, 10))) PrintRow(r);
Console.WriteLine($"\n   ---- Sunk (mod mover, Fixed): top {Math.Min(listN, 10)} by penetration ----");
foreach (var r in rel.Where(r => Fixed(r) && r.Verdict == ContactVerdict.Sunk && Pop(r) == DependencyPopulation.ModMover).OrderByDescending(RankOf).Take(Math.Min(listN, 10))) PrintRow(r);
Console.WriteLine($"\n   ---- ReSupported (mod mover, Fixed): {Math.Min(listN, 6)} examples — the room rebuilt around the dependent ----");
foreach (var r in rel.Where(r => Fixed(r) && r.Verdict == ContactVerdict.ReSupported && Pop(r) == DependencyPopulation.ModMover && !r.Facts.WinnerContactIsHost).Take(Math.Min(listN, 6))) PrintRow(r);

var mov = rel.Where(r => r.Motion == DependentMotion.Movable && Pop(r) == DependencyPopulation.ModMover).ToList();
Console.WriteLine($"\n==== 8. MOVABLE DEPENDENTS (mod mover) — the context list, not the class: physics drops or shoves these at cell load (4.7 Part C) ====");
Console.WriteLine($"   " + string.Join(", ", new[] { ContactVerdict.Stranded, ContactVerdict.Orphaned, ContactVerdict.Sunk, ContactVerdict.ReSupported, ContactVerdict.MovedTogether, ContactVerdict.Removed }.Select(v => $"{v} {mov.Count(r => r.Verdict == v):N0}")));
Console.WriteLine($"   Movable Stranded + Orphaned : Fixed Stranded + Orphaned = {mov.Count(IsClass):N0} : {classRows.Count:N0}" + (classRows.Count > 0 ? $" ({(float)mov.Count(IsClass) / classRows.Count:0.0}x)" : ""));
foreach (var r in mov.Where(IsClass).OrderByDescending(Interior).ThenByDescending(RankOf).Take(Math.Min(listN, 8))) PrintRow(r);

Console.WriteLine($"\n==== 9. SENSITIVITY — every measured row re-judged at other ε (the class = mod mover, Fixed, Stranded + Orphaned) ====");
foreach (var e in new[] { eps / 2, eps, eps * 2 })
{
    int nCls = 0, nRel = 0, nSunk = 0, nRe = 0;
    foreach (var r in rows.Where(r => r.Facts.AuthoringMeasured && (r.Facts.WinnerMeasured || r.Verdict is ContactVerdict.Removed or ContactVerdict.MovedTogether)))
    {
        var v = DetectorModel.PlacementContact(r.Facts, e, cidx.SupportCos, cidx.AttachCos);
        if (v.Relation == ContactRelation.None) continue;
        nRel++;
        if (!Fixed(r) || Pop(r) != DependencyPopulation.ModMover) continue;
        if (v.Verdict is ContactVerdict.Stranded or ContactVerdict.Orphaned) nCls++;
        else if (v.Verdict == ContactVerdict.Sunk) nSunk++;
        else if (v.Verdict == ContactVerdict.ReSupported) nRe++;
    }
    Console.WriteLine($"   ε {e,4:0.#}u  relationships {nRel,8:N0}   class {nCls,7:N0}   Sunk {nSunk,6:N0}   ReSupported {nRe,7:N0}{(e > eps ? "   (rows first in contact beyond the near band are not here — a re-run with --eps would find them)" : "")}");
}

// THE PHYSICAL PREDICATE of a CONTACT row (2026-09-03; handoff 2026-09-12): the dependent still touches nothing within ε
// under the arm being read — the class's own winner-arm test with the provenance half ("the mover's side did not re-place
// it") taken off. A sham on the mover's side that nudges the dependent 1 u reads MovedTogether and leaves the class; the
// dependent still floats. A dependent deleted or disabled at runtime under the arm is Gone (nothing floats); one whose
// geometry or enable chain cannot be read is Unmeasured.
(PhysicalState State, string Detail) ContactPhysical(bool measured, float dist, float pen, string against)
{
    if (!measured) return (PhysicalState.Unmeasured, "the winner arm was not measured");
    if (pen > eps) return (PhysicalState.Gone, $"sunk {pen:0.#}u into {against}");
    if (dist <= eps) return (PhysicalState.Gone, $"touches {against} at {dist:0.#}u");
    return (PhysicalState.Holds, $"touches nothing within {eps:0.#}u (nearest {dist:0.#}u)");
}
(PhysicalState State, string Detail) ContactPredicate(FormKey dep)
{
    var snaps = idx.History.Of(dep);
    if (snaps is null || snaps.Count == 0) return (PhysicalState.Unmeasured, "the dependent is not placed in this order");
    var win = snaps[^1];
    if (win.Deleted || idx.IsDeletedNow(dep)) return (PhysicalState.Gone, "the dependent is deleted under this order — nothing floats");
    var en = cidx!.WinnerEnable(dep);
    if (!en.Resolved) return (PhysicalState.Unmeasured, "the dependent's enable chain cannot be followed in this order");
    if (!en.Enabled) return (PhysicalState.Gone, "the dependent is disabled at runtime under this order — nothing floats");
    var b = cidx.WinnerBase(dep);
    if (cidx.GeomOf(b) is not { CanDepend: true }) return (PhysicalState.Unmeasured, "the dependent has no solid to test under this order");
    var box = cidx.WorldBox(b, win);
    if (box.IsEmpty) return (PhysicalState.Unmeasured, "the dependent has no geometry under this order");
    var c = cidx.BestContact(dep, snaps.Count - 1, b, win, box, winHash, cidx.WinnerPlacement, (k, _) => cidx.WinnerBase(k), out _);
    if (c is null) return (PhysicalState.Holds, $"touches nothing within {cidx.BoxBand:0.#}u of its box");
    return ContactPhysical(true, c.Contact.FacingDistance, c.Contact.EffectivePenetration, BaseLabel(cidx.WinnerBase(c.Against)));
}

// --emit: the class only — Fixed Stranded + Orphaned with a mod or USSEP mover, the Bethesda floor withheld. The
// dependent is the necessary record (a patch re-places or deletes it); moving the host back is the alternative. Every
// record carries its physical predicate under this arm (Holds by construction for a class row); with --base, every record
// of the named emit is re-measured under this arm too (2026-09-03).
if (emitPath is not null)
{
    var conflicts = new List<Conflict>();
    foreach (var r in rel.Where(r => Fixed(r) && IsClass(r) && Pop(r) != DependencyPopulation.BethesdaFloor))
    {
        var (st, why) = ContactPhysical(r.Facts.WinnerMeasured, r.Facts.WinnerDistance, r.Facts.WinnerPenetration, r.Winner is { } w ? BaseLabel(cidx!.WinnerBase(w.Against)) : "(nothing)");
        conflicts.Add(new Conflict("CONTACT", PluginName(r.DepAuthor), PluginName(r.Host.Mover),
            $"{r.Verdict}: {BaseLabel(r.DepBase)} {r.Relation} on {BaseLabel(r.Host.PrevBase)} which {PluginName(r.Host.Mover)} {r.Host.Change}; nearest thing now {r.Facts.WinnerDistance:0.#}u  {r.Dependent} / {r.Host.Key} in {CellOf(r.DepAuthoring.Cell)}",
            new List<ConflictRecord> { new(r.Dependent.ToString(), "placed-ref", CellOf(r.DepAuthoring.Cell), st, why) },
            new List<ConflictRecord> { new(r.Host.Key.ToString(), "placed-ref", null) }));
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
                if (c.Kind != "CONTACT") { predicates.Add(new Predicate(c.Kind, c.A, c.B, rec.FormKey, other, PhysicalState.Unmeasured, "not a contact row")); continue; }
                var (st, why) = ContactPredicate(FormKey.Factory(rec.FormKey));
                predicates.Add(new Predicate(c.Kind, c.A, c.B, rec.FormKey, other, st, why));
            }
        }
        Console.WriteLine($"\n   --base {Path.GetFileName(basePath)}: {predicates.Count:N0} base-row predicate(s) re-measured under this order — "
                        + string.Join(", ", predicates.GroupBy(p => p.Physical).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count():N0}")));
    }
    var coverage = new List<CoverageLine>
    {
        // The population this run judged, and the chains it could not read to the end — the contact pass has resolved
        // the chain since R1, but until Q27 nothing said so in the emit, and conflictdiff could not see the unresolved
        // share move. Counted off the winner memo, so no answer changes and the console output is what it always was.
        new("enable chains (winner arm)", cidx.Arms.WinnerQueried - cidx.Arms.WinnerUnresolved,
            cidx.Arms.WinnerUnresolved > 0 ? new Dictionary<string, long> { ["EnableChainUnresolved"] = cidx.Arms.WinnerUnresolved } : new Dictionary<string, long>(),
            new Dictionary<string, long> { ["NotPresentAtLoad"] = cidx.Arms.WinnerAbsent },
            $"population load-time / state {cidx.State?.Name ?? EnablePopulation.BaselineStateName}"),
        new("plugins", paths.Count - unreadable.Count, new Dictionary<string, long> { ["UnreadablePlugin"] = unreadable.Count }),
        // A host without solid collision is SET ASIDE by definition — the engine rests nothing on it either (the plumbing line
        // above says the same) — not unjudgeable: the coverage rule's first real case (2026-09-03, the lab's JK's–OCW patch, which
        // moves ~490 more collision-less hosts than the order without it) refused its verdict on this line until it was classed so.
        // A host whose mesh could not be read at all stays unjudgeable.
        new("moved hosts", hosts.Count,
            new Dictionary<string, long> { ["NoGeometry"] = skips.HostNoGeometry, ["EnableChainUnresolved"] = skips.HostEnableUnresolved },
            new Dictionary<string, long> { ["NoSolidCollision"] = skips.HostNoCollision, ["VolumeScale"] = skips.HostVolume, ["TooSmallToHost"] = skips.HostTooSmall, ["RuntimeDisabledInArm"] = skips.HostRuntimeDisabled }),
        new("dependent snaps hashed", depHash.Count, new Dictionary<string, long> { ["NoGeometry"] = skips.DepNoGeometry }, new Dictionary<string, long> { ["OverDependentSize"] = skips.DepVolume }),
        new("winner-arm physical refs", winHash.Count, new Dictionary<string, long> { ["EnableChainUnresolved"] = skips.WinnerEnableUnresolved }, new Dictionary<string, long> { ["RuntimeDisabled"] = skips.WinnerRuntimeDisabled }),
        new("dependent-host pairs", rows.Count - unj.Count,
            unj.GroupBy(r => r.Reason.ToString()).ToDictionary(g => g.Key, g => (long)g.Count()).Concat(new[] { new KeyValuePair<string, long>("DepEnableChainUnresolved", skips.DepEnableUnresolved) }).ToDictionary(kv => kv.Key, kv => kv.Value),
            new Dictionary<string, long> { ["BoxesApart"] = skips.BoxesApart, ["DepIntroducedByMover"] = skips.DepIntroducedByMover, ["DepOtherSpace"] = skips.DepOtherSpace, ["DepAbsentInArm"] = skips.DepAbsentInArm },
            $"relationships {rel.Count:N0}, class {classRows.Count:N0}"),
    };
    ConflictIo.Write(emitPath, new ConflictFile("contactprobe", Instance, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), conflicts, coverage, predicates, basePath,
                                                "load-time", cidx.State?.Name ?? EnablePopulation.BaselineStateName));
    Console.WriteLine($"\nemitted {conflicts.Count:N0} Fixed Stranded + Orphaned conflicts -> {emitPath}");
}

if (jsonPath is not null)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(jsonPath));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    var dump = rows.Where(r => r.Relation != ContactRelation.None || r.Facts.WinnerMeasured).Select(r => new
    {
        Dependent = r.Dependent.ToString(), DepBase = BaseLabel(r.DepBase), DepAuthor = PluginName(r.DepAuthor), DepScale = r.DepScale.ToString(), Motion = r.Motion.ToString(),
        Host = r.Host.Key.ToString(), HostBase = BaseLabel(r.Host.PrevBase), HostChange = r.Host.Change.ToString(), Mover = PluginName(r.Host.Mover), HostScale = r.HostScale.ToString(),
        HostMoved = PlacementHistory.Dist(r.Host.Previous.Pos, r.Host.Winner.Pos), HostPrev = r.Host.Previous.Pos.ToString(), HostNow = r.Host.Winner.Pos.ToString(),
        Cell = CellOf(r.DepAuthoring.Cell), CellKey = r.DepAuthoring.Cell.ToString(), Interior = Interior(r), DependentPos = r.DepAuthoring.Pos.ToString(),
        Population = Pop(r).ToString(), r.Seen, r.AuthorAfterMover, r.DepTouchedByMoverSide,
        Relation = r.Relation.ToString(), Verdict = r.Verdict.ToString(), Reason = r.Reason.ToString(), r.Why,
        AuthoringDistance = r.Facts.AuthoringDistance, AuthoringPenetration = r.Facts.AuthoringPenetration, AuthoringNormalUp = r.Facts.AuthoringNormalUp, AuthoringSrc = Src(r.Authoring),
        WinnerMeasured = r.Facts.WinnerMeasured, WinnerDistance = r.Facts.WinnerDistance, WinnerPenetration = r.Facts.WinnerPenetration, WinnerContactIsHost = r.Facts.WinnerContactIsHost,
        WinnerAgainst = r.Winner?.Against.ToString(), WinnerSrc = Src(r.Winner),
        WinnerAgainstExtent = r.Facts.WinnerAgainstExtent, WinnerAgainstMaxDim = r.Winner?.AgainstMaxDim,
        DependentDisabled = r.Facts.DependentDisabled, DependentDisabledViaParent = r.Facts.DependentDisabledViaParent, HostRemovedViaParent = r.Host.RemovedViaParent,
        AuthoringRawDistance = r.Authoring?.Contact.Distance, WinnerRawDistance = r.Winner?.RawDistance, WinnerRawAgainst = r.Winner is { } wr && !wr.RawAgainst.IsNull ? wr.RawAgainst.ToString() : null,
    });
    File.WriteAllText(jsonPath, JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = false, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
    Console.WriteLine($"wrote {jsonPath}");
}

Console.WriteLine($"\nA Stranded / Orphaned row says the dependent touched its host in the order without the mover and touches nothing within ε now, both of them existing at runtime in their arm;");
Console.WriteLine($"a Fixed Stranded row FLOATS IN AIR (in game 2026-09-03, 3 of 3 runtime-real rows); a Fixed Orphaned row's appearance in game is unseen. These rows are not watched in game.");
Console.WriteLine($"Limits: ε {eps:0.#}u and the 30° bands are chosen numbers; rotation- and scale-only host moves are invisible ({idx.SilentRePlacements:N0} such overrides);");
Console.WriteLine($"        terrain is not a reference (exterior rows are split out); the arm without the mover steps back ONE placement; a render-soup row ('r') is labelled, a box row ('o') is a box;");
Console.WriteLine($"        runtime existence is the enable chain as the records state it (R1) — a script that enables or disables a reference is invisible here; a marker is the Is-Marker flag (R2).");
WriteGeomReport();
Console.WriteLine($"elapsed {sw.Elapsed.TotalSeconds:0.0}s");
return 0;
