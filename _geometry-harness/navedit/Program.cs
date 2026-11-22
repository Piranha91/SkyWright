using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

// navedit — NAVMESH EDITING from the command line (task 2026-11-16-navmesh-repair; the surface houseCARL drives, chosen
// 2026-11-21: C:/hc-runs/2026-11-21-navgrid/PLAN.md). Two steps, so a caller can judge the plan before anything is written:
//
//   navedit plan  --edits EDITS.json --out PLAN.json [--instance DIR --game DIR --profile NAME]
//       Reads the winning version of each named navmesh on the profile's load order and plans the edits (_shared/NavEdit.cs):
//       per edit "planned" with the write ops for every navmesh it changes, or "refused" with why. Nothing is written but
//       PLAN.json. Exit 0 when every edit is planned, 1 when any is refused, 2 when the inputs cannot be read.
//   navedit write --plan PLAN.json --out PATCH.esp
//       Plans the same edits again on the same load order (a plan that no longer comes out the same is refused), writes the
//       ops into a NEW patch plugin through houseCARL core's write engine in this process (no server), forwards each
//       carried cell and worldspace from its winner, and reads the patch back: every changed navmesh must hold exactly the
//       planned geometry, links, doors and the grid NavGrid rebuilds, each kept triangle its cover fields, and every
//       carried record must equal its winner. Refused when the load order differs, or PATCH.esp exists. Exit 0 on a
//       read-back that holds.
//   navedit write --repair-plan PLAN.json --out PATCH.esp
//       houseCARL's request R1: writes a navownprobe --repair-plan (relink, follow the ground, NAVI) in this process, as a
//       navedit plan is written: the plan's ops through the core write engine into a NEW patch, every carried cell and
//       worldspace forwarded from its winner, then the plan's own read-back (_shared/NavRepairReadBack.cs, the one
//       navownprobe --repair-readback runs) beside the patch as PATCH.readback.json. The order is the plan's. Refused
//       navmeshes in the plan are not written, and are named. Exit 0 when the read-back holds.
//   navedit marker --markers MARKERS.json --out RESULT.json [--instance DIR --game DIR --profile NAME] [--enable X.esp|PATH ...]
//       The station marker check (_shared/NavMarker.cs): for each lab marker, the triangle it stands on, its nearest open
//       edge over connected ground, what lies beyond that edge, and the step across each edge link near it, with a
//       verdict "inside" or "refused". Reads the winning version of each named navmesh and, outward through edge links, of
//       every navmesh within reach of a marker; a linked navmesh the load order does not have is listed as absent.
//       MARKERS.json: {"worldspace": "00003C:Skyrim.esm" | "cell": "<interior cell FormKey>", "navmeshes": [...],
//       "markers": [{"name": "N start", "at": [x, y, z]}, ...], "margin": 64, "stepLimit": 64}: with "worldspace" (or "cell")
//       every winning navmesh in the cells within reach of a marker is found and checked, so an arm that changes the
//       layout needs no list; "navmeshes" adds named ones (all optional but one of the three; margin and stepLimit
//       default to 64). Writes nothing but RESULT.json. Exit 0 when every
//       marker is inside, 1 when any is refused, 2 when the inputs cannot be read. --enable (repeatable) builds the arm's
//       order as navownprobe does (_shared/HarnessOrderCli.cs): a name the profile's layout holds, or a patch file's path,
//       loaded last with its masters checked; so one profile plus a patch covers a patched arm.
//   navedit --selftest
//       Known answers for the edit model on small in-memory navmeshes (tests/test_navedit.py runs it).
//
// EDITS.json: {"edits": [EDIT, ...]}, one EDIT per navmesh, each one kind:
//   {"navmesh": "0E6430:Skyrim.esm", "move": [{"vertex": 517, "to": [x, y, z]} | {"vertex": 517, "by": [dx, dy, dz]}, ...]}
//   {"navmesh": "0E6430:Skyrim.esm", "remove": [12, 13], "add": {"vertices": [[x, y, z], ...], "triangles": [[a, b, c], ...]}}
//   {"navmesh": "0E6430:Skyrim.esm", "cut": {"outline": [[x, y], ...], "z": [bottom, top]}}   (NavCut: a convex obstacle)
//   {"navmesh": "0E6430:Skyrim.esm", "cut": {"ref": "0A1B2C:Skyrim.esm", "margin": 0}}      (NavCut by a placed obstacle: its
//       collision on the layers an actor meets, cut where it stands between step height and an actor's top above each
//       navmesh triangle's own plane; _shared/NavCutFootprint.cs)
//   An added triangle's corners index the navmesh's vertices, then the added ones (n, n+1, ...). A removed triangle's
//   place is taken by the navmesh's last triangle (highest removal first); added triangles are appended after the kept ones.
// The profile comes from the flags, then the environment and harness.local.json (_shared/HarnessTarget.cs), as for the
// other probes.

if (args.Length >= 1 && args[0] == "--selftest") return NavEditSelfTest.Run();
if (args.Length < 1 || args[0] is not ("plan" or "write" or "marker")) { Console.Error.WriteLine("usage: navedit plan --edits EDITS.json --out PLAN.json [--instance DIR --game DIR --profile NAME] | navedit write --plan PLAN.json --out PATCH.esp | navedit write --repair-plan PLAN.json --out PATCH.esp | navedit marker --markers MARKERS.json --out RESULT.json [--instance DIR --game DIR --profile NAME] | navedit --selftest"); return 2; }

var flags = new Dictionary<string, string>(StringComparer.Ordinal);
var enables = new List<string>();   // `marker` only: --enable may repeat (each a plugin name or a patch file's path)
for (int i = 1; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) { Console.Error.WriteLine($"navedit: '{args[i]}' is not a flag with a value; see navedit with no arguments for the usage."); return 2; }
    if (args[i] == "--enable" && args[0] == "marker") { enables.Add(args[++i]); continue; }
    flags[args[i]] = args[++i];
}
string[] known = args[0] switch
{
    "plan" => new[] { "--edits", "--out", "--instance", "--game", "--profile" },
    "marker" => new[] { "--markers", "--out", "--instance", "--game", "--profile" },
    _ => new[] { "--plan", "--repair-plan", "--out" },
};
foreach (var k in flags.Keys)
    if (!known.Contains(k)) { Console.Error.WriteLine($"navedit {args[0]}: '{k}' is not one of its flags ({string.Join(", ", known)})."); return 2; }

try { return args[0] switch { "plan" => NavEditCli.Plan(flags), "marker" => NavEditCli.Marker(flags, enables), _ => flags.ContainsKey("--repair-plan") ? NavEditCli.WriteRepair(flags) : NavEditCli.Write(flags) }; }
catch (NavEditCli.Refusal r) { Console.Error.WriteLine(r.Message); return 2; }

static class NavEditCli
{
    public sealed class Refusal(string message) : Exception(message);

    static string Need(Dictionary<string, string> f, string k, string what) => f.TryGetValue(k, out var v) ? v : throw new Refusal($"navedit: give {k} {what}.");

    sealed record Target(string Instance, string Game, string Profile)
    {
        public IReadOnlyList<string> Order()
        {
            var o = Mo2LoadOrder.Build(Path.Combine(Instance, "profiles", Profile), Path.Combine(Instance, "mods"), Path.Combine(Game, "Data"), Path.Combine(Instance, "overwrite"));
            if (o.OrderedPaths.Count == 0) throw new Refusal($"navedit: the profile '{Profile}' in {Instance} has no plugins in its load order; check the instance and profile.");
            return o.OrderedPaths;
        }

        /// <summary>The profile's files as the probes read them (HarnessOrderCli, the configuration's owner): the asset
        /// resolver a cut by reference reads the obstacle's mesh through. Refused when its load order is not this one.</summary>
        public AssetResolver Assets(IReadOnlyList<string> order)
        {
            string mods = Path.Combine(Instance, "mods"), data = Path.Combine(Game, "Data"), ow = Path.Combine(Instance, "overwrite");
            var cfg = HarnessOrderCli.Resolve(Profile, Path.Combine(Instance, "profiles", Profile), mods, data, ow, Game, null, null, null);
            if (cfg.Refused) throw new Refusal($"navedit: the profile's configuration could not be read ({cfg.RefusedReason}).");
            if (!cfg.OrderedPaths.SequenceEqual(order, StringComparer.OrdinalIgnoreCase))
                throw new Refusal("navedit: the profile's configuration and its load order disagree, so its files cannot be trusted for an obstacle's mesh; check the profile.");
            return AssetResolver.Build(ow, mods, data, cfg.EnabledMods, cfg.Archives.Archives);
        }
    }

    /// <summary>One planned edit: its row for PLAN.json, and the expected state of every navmesh it changes (with the old
    /// index of each triangle, -1 for an added one, so the read-back can check the fields the geometry model does not
    /// carry).</summary>
    sealed record Planned(JsonObject Row, bool Ok, List<RepairOp> Ops, Dictionary<FormKey, (NavmeshGeometry Geom, int[] OldIndex, string WinnerPath, bool Rebuilt)> Expect);

    static JsonArray Arr<T>(IEnumerable<T> xs) => new(xs.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());

    /// <summary>Plan every edit on this load order. The one planning path: `plan` writes its rows, `write` re-runs it and
    /// requires the same ops.</summary>
    static List<Planned> Compute(Target target, IReadOnlyList<string> order, JsonArray edits)
    {
        using var resolver = LoadOrderResolver.Build(order);
        var view = resolver.Capture();
        using var session = view.OpenSession();
        AssetResolver? assets = null;
        var (navi, naviOpen) = NaviView.Read(order);
        var outp = new List<Planned>();
        var claimed = new Dictionary<FormKey, string>();
        var naviClaimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<FormKey, (string Path, List<NavEdgeLink> Links)>? inboundIndex = null;
        try
        {
            foreach (var e in edits)
            {
                var key = FormKey.Factory((string)e!["navmesh"]!);
                var row = new JsonObject { ["navmesh"] = key.ToString(), ["edit"] = e.DeepClone() };
                var expect = new Dictionary<FormKey, (NavmeshGeometry, int[], string, bool)>();
                string? refuse = null; List<RepairOp> ops = new(); IReadOnlyList<string> why = Array.Empty<string>();
                var w = view.ResolveWinner(key);
                var path = w is null ? null : order.FirstOrDefault(p => string.Equals(Path.GetFileName(p), w.Value.WinnerPlugin, StringComparison.OrdinalIgnoreCase));
                var geom = path is null ? null : Winning(path, key);
                bool isMove = e["move"] is not null, isTri = e["remove"] is not null || e["add"] is not null, isCut = e["cut"] is not null, isUse = e["use_version"] is not null, isDoor = e["door"] is not null;
                if (w is null || path is null) refuse = $"{key} is in no plugin of this load order; check the FormKey and the profile.";
                else if (geom is null) refuse = $"{key} has no geometry in its winner {w.Value.WinnerPlugin} (deleted or empty), so there is nothing to edit.";
                else if ((isMove ? 1 : 0) + (isTri ? 1 : 0) + (isCut ? 1 : 0) + (isUse ? 1 : 0) + (isDoor ? 1 : 0) != 1) refuse = $"The edit of {key} must do one kind of thing: move vertices, remove and add triangles, cut, use a version, or add a door; split it into separate plans.";
                else if (NavUseVersionPlan.IslandRefusal(navi, key) is { } isl) refuse = isl;
                row["winner"] = w is null ? null : new JsonObject { ["plugin"] = w.Value.WinnerPlugin, ["path"] = path };
                if (refuse is null && isMove)
                {
                    var moves = new List<VertexMove>();
                    foreach (var m in e["move"]!.AsArray())
                    {
                        int v = (int)m!["vertex"]!;
                        if (m["to"] is JsonArray a) moves.Add(new VertexMove(v, new Vec3((float)a[0]!, (float)a[1]!, (float)a[2]!)));
                        else if (m["by"] is JsonArray b && v >= 0 && v < geom!.Vertices.Count) { var p0 = geom.Vertices[v]; moves.Add(new VertexMove(v, new Vec3(p0.X + (float)b[0]!, p0.Y + (float)b[1]!, p0.Z + (float)b[2]!))); }
                        else { refuse = $"The move of vertex {v} in {key} needs \"to\" or \"by\" with three numbers, and a vertex in range."; break; }
                    }
                    if (refuse is null)
                    {
                        var r = NavEdit.MoveVertices(geom!, moves);
                        why = r.Why; ops = r.Ops.ToList();
                        if (r.Status == "planned") { expect[key] = (r.After!, Enumerable.Range(0, r.After!.Triangles.Count).ToArray(), path!, true); row["moved"] = Arr(r.Moved); }
                    }
                }
                else if (refuse is null && isDoor)
                {
                    var dref = (string?)e["door"]?["ref"];
                    var door = dref is null ? FormKey.Null : FormKey.Factory(dref);
                    var dw = door.IsNull ? null : view.ResolveWinner(door);
                    var drec = dw is null ? null : view.GetRecord(session, dw.Value.WinnerPlugin, door, typeof(IPlacedObjectGetter)) as IPlacedObjectGetter;
                    // where the door puts an actor arriving through it: the teleport position its partner door names
                    var partner = drec?.TeleportDestination is { } td && !td.Door.FormKey.IsNull ? td.Door.FormKey : (FormKey?)null;
                    var pw = partner is { } pk ? view.ResolveWinner(pk) : null;
                    var prec = pw is null ? null : view.GetRecord(session, pw.Value.WinnerPlugin, partner!.Value, typeof(IPlacedObjectGetter)) as IPlacedObjectGetter;
                    // its state at load, by the one enable rule (EnableState: the enable parent's chain)
                    EnableFacts? Facts(FormKey k) => view.ResolveWinner(k) is { } kw && view.GetRecord(session, kw.WinnerPlugin, k, null) is IPlacedGetter pg && PlacementExtract.RefOf(pg) is { } pr
                        ? new EnableFacts(pr.InitiallyDisabled, pr.EnableParent, pr.EnableParentOpposite) : null;
                    var state = drec is null ? default : EnableState.Resolve(door, Facts);
                    // "atLoad": "any" lists a door the enable chain switches off at load (a quest-gated door): the base game lists such
                    // doors too, 166 of its 2,165 door-table entries (84 through an enable parent; Skyrim.esm and the DLC, 2026-11-22)
                    var atLoad = (string?)e["door"]?["atLoad"];
                    if (dref is null) refuse = $"The door edit of {key} needs \"ref\", the placed door's FormKey.";
                    else if (atLoad is not (null or "any")) refuse = $"The door edit of {key} has \"atLoad\": \"{atLoad}\"; give \"any\" to list a door switched off at load, or leave it out.";
                    else if (drec is null) refuse = $"{door} is not a placed object of this load order; name the door's placed reference.";
                    else if (partner is null) refuse = $"{door} is not a teleport door (it leads nowhere), so no navmesh triangle stands before it in the game's sense.";
                    else if (prec?.TeleportDestination is not { } back || back.Door.FormKey != door) refuse = $"{door}'s partner {partner} does not lead back to it in this order, so where an actor arrives through {door} is not known.";
                    else if (atLoad is null && !state.Resolved) refuse = $"Whether {door} stands at load cannot be told (its enable chain names a reference this order does not place, or loops); it is not added.";
                    else if (atLoad is null && !state.Enabled) refuse = $"{door} is disabled at load{(state.ViaParent ? " (by its enable parent's chain)" : "")}, so no actor can use it then; give \"atLoad\": \"any\" to list it for when it is enabled.";
                    // a refusal still says which door, who places it and how it is switched, for the caller's judgement
                    if (refuse is not null && drec is not null)
                        row["door"] = new JsonObject
                        {
                            ["door"] = NavUseVersionPlan.DoorRow(view, session, new NavDoorTriangle(door, -1)),
                            ["enabledAtLoad"] = new JsonObject { ["enabled"] = state.Enabled, ["viaParent"] = state.ViaParent, ["resolved"] = state.Resolved,
                                                                 ["enableParent"] = (PlacementExtract.RefOf(drec) is { EnableParent: { } ep }) ? ep.ToString() : null },
                        };
                    if (refuse is null && prec?.TeleportDestination is { } arrival)
                    {
                        var marker = new Vec3(arrival.Position.X, arrival.Position.Y, arrival.Position.Z);
                        var r = NavEdit.AddDoor(geom!, door, marker);
                        why = r.Why; ops = r.Ops.ToList();
                        JsonObject? naviRow = null;
                        if (r.Status == "planned")
                        {
                            var (np, nr) = NavRepairNavi.ForDoors(key, new[] { door }, navi);
                            if (nr is not null) { why = new[] { $"The navmesh info map cannot gain the door: {nr}." }; ops = new(); }
                            else { ops.AddRange(np.Ops); naviRow = NavRepairNavi.NaviJson(np); row["navi"] = naviRow; expect[key] = (r.After!, Enumerable.Range(0, r.After!.Triangles.Count).ToArray(), path!, false); }
                        }
                        row["door"] = new JsonObject
                        {
                            ["door"] = NavUseVersionPlan.DoorRow(view, session, new NavDoorTriangle(door, (short)r.Triangle)),
                            ["arrivalMarker"] = Arr(new[] { marker.X, marker.Y, marker.Z }), ["triangle"] = r.Triangle >= 0 ? r.Triangle : null,
                            ["planDistance"] = double.IsNaN(r.PlanDistance) ? null : Math.Round(r.PlanDistance, 1),
                            ["heightAboveTriangle"] = double.IsNaN(r.Height) ? null : Math.Round(r.Height, 1),
                            ["enabledAtLoad"] = new JsonObject { ["enabled"] = state.Enabled, ["viaParent"] = state.ViaParent }, ["atLoad"] = atLoad ?? "enabled", ["navi"] = naviRow?.DeepClone(),
                        };
                    }
                }
                else if (refuse is null && isUse)
                {
                    // the use_version glue's one owner (_shared/NavEdit.cs NavUseVersionPlan), navownprobe's repair plan calls it too
                    var u = NavUseVersionPlan.Plan(view, navi, order, key, geom!, w!.Value.WinnerPlugin, (string?)e["use_version"] ?? "", () => inboundIndex ??= InboundIndex(order));
                    refuse = u.Refuse; why = u.Why; ops = u.Ops;
                    foreach (var (fk, x) in u.Expect) expect[fk] = x;
                    if (u.Row is not null) row["useVersion"] = u.Row;
                    if (u.Why.Count == 0 && u.Row?["navi"] is JsonObject nv) row["navi"] = nv.DeepClone();   // main's place for a row's NAVI change
                }
                else if (refuse is null)
                {
                    var remove = e["remove"]?.AsArray().Select(x => (int)x!).ToList() ?? new List<int>();
                    var addV = e["add"]?["vertices"]?.AsArray().Select(x => new Vec3((float)x![0]!, (float)x[1]!, (float)x[2]!)).ToList() ?? new List<Vec3>();
                    var addT = e["add"]?["triangles"]?.AsArray().Select(x => ((int)x![0]!, (int)x[1]!, (int)x[2]!)).ToList() ?? new List<(int, int, int)>();
                    inboundIndex ??= InboundIndex(order);
                    var inbound = new Dictionary<FormKey, NavmeshGeometry>();
                    foreach (var (fk, (p, links)) in inboundIndex)
                        if (fk != key && links.Any(l => l.Mesh == key) && Winning(p, fk) is { } og) inbound[fk] = og;
                    TriEditResult r;
                    if (isCut)
                    {
                        var c = e["cut"]!;
                        var outline = c["outline"]?.AsArray().Select(x => ((float)x![0]!, (float)x[1]!)).ToList();
                        var z = c["z"]?.AsArray();
                        if (c["ref"] is not null)
                        {
                            assets ??= target.Assets(order);
                            var fp = Footprint(view, session, assets, geom!, FormKey.Factory((string)c["ref"]!), (float?)c["margin"] ?? 0f, row);
                            r = fp.Status != "planned" ? TriEditResult.Refused(fp.Why!)
                              : NavEdit.CutMany(geom!, fp.Parts.Select(f => (f.Outline, float.MinValue, float.MaxValue)).ToList(), inbound, Cover(path!, key),
                                                fp.Parts.Select(f => (IReadOnlyCollection<int>?)f.Applies).ToList());
                        }
                        else r = outline is null || z is null || z.Count != 2
                            ? TriEditResult.Refused($"The cut of {key} needs \"ref\" (a placed obstacle) or \"outline\" (corners [x, y] in order) with \"z\" [bottom, top].")
                            : NavEdit.Cut(geom!, outline, (float)z[0]!, (float)z[1]!, inbound, Cover(path!, key));
                    }
                    else r = NavEdit.EditTriangles(geom!, remove, addV, addT, inbound, Cover(path!, key));
                    why = r.Why; ops = r.Ops.ToList();
                    if (r.Status == "planned")
                    {
                        var old = new int[r.After!.Triangles.Count]; Array.Fill(old, -1);
                        for (int i = 0; i < r.Map.Count; i++) if (r.Map[i] >= 0) old[r.Map[i]] = i;
                        expect[key] = (r.After, old, path!, true);
                        // a navmesh whose links alone changed keeps its own grid, as its winner stores it
                        foreach (var (fk, og) in r.Others) expect[fk] = (og, Enumerable.Range(0, og.Triangles.Count).ToArray(), inboundIndex[fk].Path, false);
                        int gone = r.Map.Count(m => m < 0), added = r.After.Triangles.Count - (r.Map.Count - gone);
                        row["removed"] = gone; row["addedTriangles"] = added; row["addedVertices"] = r.After.Vertices.Count - geom!.Vertices.Count;
                        if (isCut) row["areaRemoved"] = Math.Round(PlanArea(geom!) - PlanArea(r.After), 1);
                    }
                }
                foreach (var fk in expect.Keys)
                    if (claimed.TryGetValue(fk, out var by)) { refuse = $"This edit changes {fk}, which the edit of {by} earlier in this plan changes too; plan them separately."; break; }
                foreach (var ne in (row["navi"]?["entries"] as JsonArray ?? new JsonArray()).Select(x => (string)x!["navmesh"]!))
                    if (refuse is null && naviClaimed.TryGetValue(ne, out var nby)) refuse = $"This edit changes {ne}'s navmesh info map entry, which the edit of {nby} earlier in this plan changes too; plan them separately.";
                if (refuse is not null) { why = new[] { refuse }; ops = new(); expect.Clear(); }
                foreach (var fk in expect.Keys) claimed[fk] = key.ToString();
                if (refuse is null) foreach (var ne in (row["navi"]?["entries"] as JsonArray ?? new JsonArray()).Select(x => (string)x!["navmesh"]!)) naviClaimed[ne] = key.ToString();
                bool ok = refuse is null && why.Count == 0;
                row["status"] = ok ? "planned" : "refused";
                row["why"] = Arr(why);
                row["changes"] = Arr(expect.Keys.Select(k => k.ToString()));
                row["ops"] = new JsonArray(ops.Select(OpJson).ToArray());
                outp.Add(new Planned(row, ok, ops, expect));
            }
        }
        finally { foreach (var o in naviOpen) o.Dispose(); }
        return outp;
    }

    /// <summary>A placed obstacle's cut: its winning placement, its base's model (OverlapIndex.ModelOf), the mesh through
    /// this order's files, its collision on the layers an actor meets (CollisionLayers.RoleSoups), placed in the world
    /// (SpatialOverlap.TransformSoup), and measured on each navmesh triangle by it, on that triangle's own plane
    /// (NavCutFootprint.OnNavmesh). What was read goes into the plan row, so a reader sees which mesh the parts came
    /// from and which triangles they were measured on.</summary>
    static NavCutPlan Footprint(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session, AssetResolver assets,
                                  NavmeshGeometry g, FormKey refKey, float margin, JsonObject row)
    {
        var w = view.ResolveWinner(refKey);
        if (w is null) return One($"The obstacle {refKey} is in no plugin of this load order; check its FormKey.");
        if (view.GetRecord(session, w.Value.WinnerPlugin, refKey, typeof(IPlacedObjectGetter)) is not IPlacedGetter placed || PlacementExtract.RefOf(placed) is not { } pr)
            return One($"{refKey} is not a placed object in {w.Value.WinnerPlugin}; name the obstacle's placed reference.");
        if (pr.Deleted) return One($"{refKey} is deleted in {w.Value.WinnerPlugin}, so it stands nowhere.");
        if (pr.InitiallyDisabled || pr.EnableParent is not null)
            return One($"{refKey} starts disabled or is switched by an enable parent, so whether it stands at load is not settled here; cut it by outline if it does.");
        if (pr.Base is not { } bk) return One($"{refKey} has no base object.");
        var bw = view.ResolveWinner(bk);
        var brec = bw is null ? null : view.GetRecord(session, bw.Value.WinnerPlugin, bk, null);
        if (brec is null || OverlapIndex.ModelOf(brec) is not { } model) return One($"The base {bk} of {refKey} has no model, so it has no collision to cut by.");
        string full = (model.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? model : Path.Combine("meshes", model)).Replace('/', '\\');
        var res = assets.ResolveForPlacement(full);
        if (res.Sources.Count == 0) return One($"The mesh {full} of {refKey} is in none of this load order's files.");
        var (bytes, err) = AssetResolver.ReadPlacementSource(res.Sources[0]);
        if (bytes is null) return One($"The mesh {full} could not be read ({err}).");
        if (NifGeometry.Measure(bytes, includeTriangles: true).Model is not { } m) return One($"The mesh {full} could not be measured.");
        var (host, hs, _, _) = CollisionLayers.RoleSoups(m);
        if (hs != 'c') return One($"The mesh {full} has no collision on the layers an actor meets, so the game would not cut navmesh round it; cut it by outline if it should be.");
        var world = SpatialOverlap.TransformSoup(host, pr.Position, pr.Rotation, pr.Scale);
        var fp = NavCutFootprint.OnNavmesh(world, g, margin);
        row["footprint"] = new JsonObject
        {
            ["ref"] = refKey.ToString(), ["winner"] = w.Value.WinnerPlugin, ["base"] = bk.ToString(), ["mesh"] = full,
            ["meshFrom"] = res.Sources[0].ArchivePath ?? res.Sources[0].LooseFilePath, ["collisionTriangles"] = host.TriangleCount,
            ["position"] = Arr(new[] { pr.Position.X, pr.Position.Y, pr.Position.Z }), ["margin"] = margin,
            ["band"] = Arr(new[] { NavCutFootprint.SliceBottom, NavCutFootprint.SliceTop }), ["trianglesMeasured"] = fp.Measured,
            ["collisionInBand"] = fp.InBand, ["parts"] = fp.Parts.Count,
            ["outlines"] = new JsonArray(fp.Parts.Select(f => (JsonNode)new JsonObject
            {
                ["triangle"] = f.Triangle, ["cuts"] = Arr(f.Applies), ["outline"] = new JsonArray(f.Outline.Select(p => (JsonNode)Arr(new[] { p.X, p.Y })).ToArray()),
            }).ToArray()),
        };
        return fp;
    }

    static NavCutPlan One(string why) => NavCutPlan.Refused(why);

    /// <summary>One door entry of a navmesh for the plan row: the door reference, the plugin whose version of it wins, the
    /// triangle before it, and where it leads (its teleport's door, that door's cell and the cell's editor ID); a field is
    /// null when the order does not provide it.</summary>
    /// <summary>Re-write a written patch IN PLACE with <paramref name="masters"/> added to its master list. The core write
    /// engine derives a patch's masters from what its records reference, and adds no others; a resolution patch must also
    /// master what it supersedes. Through the core's public pieces: the plugin read and written back in its own text lane
    /// (PluginTextEncoding.ReadFor / WriteInPlace, so text it did not change comes back byte-identical), the FormID floor
    /// (WriteEngine.EnsureFormIdFloor), the atomic swap (AtomicFile.Commit). COPIED from the core, which keeps it internal:
    /// the Mutagen write call of WriteEngine.WritePatchStaged (load order, extra masters, no FormID re-derivation, embedded
    /// encodings) and its staging folder. tests/test_navedit.py's drift check fails when the pinned core's copy of that code
    /// changes, so this copy is reviewed with it.</summary>
    static string? AddMasters(string patchPath, IReadOnlyList<string> order, IReadOnlyList<string> masters)
    {
        SkyrimMod patch;
        try { patch = SkyrimMod.CreateFromBinary(patchPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(patchPath)); }
        catch (Exception ex) { return $"the written patch {patchPath} could not be read back to add its masters ({ex.Message}); do not enable it."; }
        if (patch.UsingLocalization) return $"the written patch {patchPath} is flagged localized, so its masters cannot be added in place; do not enable it.";
        var known = new List<ISkyrimModDisposableGetter>();
        var tmpDir = Path.Combine(Path.GetDirectoryName(patchPath)!, ".navedit-tmp");
        try
        {
            foreach (var p in order) known.Add(SkyrimMod.CreateFromBinaryOverlay(p, SkyrimRelease.SkyrimSE));
            WriteEngine.EnsureFormIdFloor(patch);
            Directory.CreateDirectory(tmpDir);
            var tmp = Path.Combine(tmpDir, Path.GetFileName(patchPath));
            patch.BeginWrite
                .ToPath(tmp)
                .WithLoadOrder(known.ToArray())
                .WithExtraIncludedMasters(masters.Select(m => ModKey.FromFileName(m)).ToArray())
                .NoNextFormIDProcessing()
                .WithEmbeddedEncodings(PluginTextEncoding.WriteInPlace(patchPath))
                .Write();
            AtomicFile.Commit(tmp, patchPath);
            return null;
        }
        catch (Exception ex) { return $"adding the masters {string.Join(", ", masters)} to {patchPath} failed ({ex.GetType().Name}: {ex.Message}); do not enable it."; }
        finally
        {
            foreach (var k in known) k.Dispose();
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch (IOException) { }
        }
    }

    /// <summary>A navmesh's walkable area in plan: its triangles' plan areas, summed.</summary>
    static double PlanArea(NavmeshGeometry g) => g.Triangles.Sum(t =>
    {
        Vec3 a = g.Vertices[t.A], b = g.Vertices[t.B], c = g.Vertices[t.C];
        return Math.Abs(((double)b.X - a.X) * ((double)c.Y - a.Y) - ((double)b.Y - a.Y) * ((double)c.X - a.X)) / 2;
    });

    static JsonNode OpJson(RepairOp o)
    {
        var j = new JsonObject { ["formid"] = o.FormId, ["field_path"] = o.FieldPath, ["op"] = o.Op, ["key"] = o.Key, ["value"] = o.Value };
        if (o.FromPlugin is not null) j["from_plugin"] = o.FromPlugin;
        if (o.ComposeNode is not null) j["compose"] = o.ComposeNode.DeepClone();
        return j;
    }

    /// <summary>Every navmesh's winning link table on the order: FormKey to (winning plugin path, its links). Read once per
    /// plan, so a triangle edit finds every navmesh whose links name the one it renumbers.</summary>
    static Dictionary<FormKey, (string Path, List<NavEdgeLink> Links)> InboundIndex(IReadOnlyList<string> order) => NavUseVersionPlan.InboundIndex(order);

    /// <summary>Each triangle's cover fields as one plugin has them (the geometry model does not carry them).</summary>
    static List<(bool, ushort)> Cover(string pluginPath, FormKey key)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
        return mod.EnumerateMajorRecords<INavigationMeshGetter>().First(n => n.FormKey == key).Data!.Triangles.Select(t => (t.IsCover, t.CoverFlags)).ToList();
    }

    /// <summary>The navmesh as one plugin has it, through the one NAVM reader (PlacementExtract); null when absent or deleted.</summary>
    static NavmeshGeometry? Winning(string pluginPath, FormKey key) => NavUseVersionPlan.Winning(pluginPath, key);

    public static int Plan(Dictionary<string, string> f)
    {
        var editsPath = Need(f, "--edits", "EDITS.json, the edits to plan");
        var outPath = Need(f, "--out", "PLAN.json, where the plan goes");
        var t = new Target(
            HarnessTarget.Require(f.GetValueOrDefault("--instance") ?? HarnessTarget.Instance, "MO2 instance directory", "instance", "--instance"),
            HarnessTarget.Require(f.GetValueOrDefault("--game") ?? HarnessTarget.Game, "game directory", "game", "--game"),
            HarnessTarget.Require(f.GetValueOrDefault("--profile") ?? HarnessTarget.Profile, "MO2 profile name", "profile", "--profile"));
        JsonArray edits;
        try { edits = JsonNode.Parse(File.ReadAllText(editsPath))!["edits"]!.AsArray(); }
        catch (Exception ex) { throw new Refusal($"navedit: the edits file '{editsPath}' could not be read as {{\"edits\": [...]}} ({ex.Message}); see the usage for its shape."); }
        var order = t.Order();
        var planned = Compute(t, order, edits);
        var doc = new JsonObject
        {
            ["tool"] = "navedit plan", ["instance"] = t.Instance, ["game"] = t.Game, ["profile"] = t.Profile,
            ["order"] = Arr(order), ["edits"] = new JsonArray(planned.Select(p => (JsonNode)p.Row).ToArray()),
        };
        File.WriteAllText(outPath, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        foreach (var p in planned)
            Console.WriteLine($"{p.Row["navmesh"]}: {p.Row["status"]}" + (p.Ok ? $" (changes {p.Expect.Count} navmesh(es), {p.Ops.Count} ops)" : " - " + string.Join(" ", p.Row["why"]!.AsArray().Select(x => (string)x!))));
        Console.WriteLine($"plan written to {outPath}");
        return planned.All(p => p.Ok) ? 0 : 1;
    }

    /// <summary>`navedit marker`: the station marker check on the winning versions of the named navmeshes and of every
    /// navmesh they link to (one hop), so an edge link is followed and what lies beyond an open edge is seen.</summary>
    public static int Marker(Dictionary<string, string> f, IReadOnlyList<string> enables)
    {
        var inPath = Need(f, "--markers", "MARKERS.json, the navmeshes and the markers to check");
        var outPath = Need(f, "--out", "RESULT.json, where the result goes");
        var t = new Target(
            HarnessTarget.Require(f.GetValueOrDefault("--instance") ?? HarnessTarget.Instance, "MO2 instance directory", "instance", "--instance"),
            HarnessTarget.Require(f.GetValueOrDefault("--game") ?? HarnessTarget.Game, "game directory", "game", "--game"),
            HarnessTarget.Require(f.GetValueOrDefault("--profile") ?? HarnessTarget.Profile, "MO2 profile name", "profile", "--profile"));
        List<FormKey> named; List<(string Name, Vec3 At)> markers; double margin, stepLimit; FormKey? inWorld = null, inCell = null;
        try
        {
            var doc = JsonNode.Parse(File.ReadAllText(inPath))!;
            named = (doc["navmeshes"] as JsonArray ?? new JsonArray()).Select(x => FormKey.Factory((string)x!)).ToList();
            if (doc["worldspace"] is { } ws) inWorld = FormKey.Factory((string)ws!);
            if (doc["cell"] is { } ce) inCell = FormKey.Factory((string)ce!);
            markers = doc["markers"]!.AsArray().Select((m, i) =>
            {
                var a = m!["at"]!.AsArray();
                return ((string?)m["name"] ?? $"marker {i + 1}", new Vec3((float)a[0]!, (float)a[1]!, (float)a[2]!));
            }).ToList();
            margin = (double?)doc["margin"] ?? NavMarker.DefaultMargin;
            stepLimit = (double?)doc["stepLimit"] ?? NavMarker.DefaultStepLimit;
        }
        catch (Exception ex) { throw new Refusal($"navedit marker: the markers file '{inPath}' could not be read as {{\"navmeshes\": [...], \"markers\": [{{\"name\", \"at\": [x, y, z]}}]}} ({ex.Message}); see the usage for its shape."); }
        if (markers.Count == 0) throw new Refusal("navedit marker: give at least one marker.");
        if (named.Count == 0 && inWorld is null && inCell is null)
            throw new Refusal("navedit marker: give \"worldspace\" (an exterior, e.g. \"00003C:Skyrim.esm\") or \"cell\" (an interior) so the navmeshes under the markers can be found, or name them in \"navmeshes\".");
        if (inWorld is not null && inCell is not null) throw new Refusal("navedit marker: give \"worldspace\" or \"cell\", not both: a marker stands in one of them.");
        if (margin <= 0 || stepLimit <= 0) throw new Refusal("navedit marker: margin and stepLimit must be positive distances in game units.");
        // the arm's layout: the profile's order, or with --enable the order HarnessOrderCli (its one owner) builds, a
        // patch file loaded last with its masters checked, as navownprobe takes it
        IReadOnlyList<string> order; var applied = new JsonArray();
        if (enables.Count == 0) order = t.Order();
        else
        {
            string mods = Path.Combine(t.Instance, "mods"), data = Path.Combine(t.Game, "Data"), ow = Path.Combine(t.Instance, "overwrite");
            var cfg = HarnessOrderCli.Resolve(t.Profile, Path.Combine(t.Instance, "profiles", t.Profile), mods, data, ow, t.Game, null, enables, null);
            if (cfg.Refused) throw new Refusal($"navedit marker: the order with {string.Join(", ", enables)} could not be built ({cfg.RefusedReason}).");
            order = cfg.OrderedPaths;
            foreach (var a in cfg.Applied.Concat(cfg.Limits)) applied.Add(a);
        }
        using var resolver = LoadOrderResolver.Build(order);
        var view = resolver.Capture();
        var meshes = new Dictionary<FormKey, NavmeshGeometry>();
        var winners = new JsonObject();
        bool Load(FormKey k, bool required)
        {
            if (meshes.ContainsKey(k)) return true;
            var w = view.ResolveWinner(k);
            var path = w is null ? null : order.FirstOrDefault(p => string.Equals(Path.GetFileName(p), w.Value.WinnerPlugin, StringComparison.OrdinalIgnoreCase));
            var g = path is null ? null : Winning(path, k);
            if (g is null)
            {
                if (required) throw new Refusal($"navedit marker: {k} has no navmesh geometry on this load order (absent, deleted or empty); check the FormKey and the profile.");
                return false;
            }
            meshes[k] = g; winners[k.ToString()] = w!.Value.WinnerPlugin;
            return true;
        }
        foreach (var k in named) Load(k, true);
        var absent = new HashSet<FormKey>();
        // the navmeshes the ARM puts under the markers, found rather than named (houseCARL, 2026-11-22: in a layout-change
        // arm which navmesh stands under a marker is exactly what is not known in advance): every navmesh of the interior
        // cell, or every navmesh of the worldspace whose own vertices come within reach of a marker in plan (by geometry,
        // not by the record's grid coordinates, whose stored order is not something this tool should rely on)
        var found = new List<FormKey>();
        if (inWorld is not null || inCell is not null)
        {
            double r0 = Math.Max(2 * margin, 128);
            bool Reaches(IReadOnlyList<Noggog.P3Float> vs)
            {
                if (vs.Count == 0) return false;
                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                foreach (var v in vs) { x0 = MathF.Min(x0, v.X); y0 = MathF.Min(y0, v.Y); x1 = MathF.Max(x1, v.X); y1 = MathF.Max(y1, v.Y); }
                return markers.Any(m => m.At.X >= x0 - r0 && m.At.X <= x1 + r0 && m.At.Y >= y0 - r0 && m.At.Y <= y1 + r0);
            }
            var under = new HashSet<FormKey>();
            foreach (var p in order)
            {
                try
                {
                using var mod = SkyrimMod.CreateFromBinaryOverlay(p, SkyrimRelease.SkyrimSE);
                foreach (var nm in mod.EnumerateMajorRecords<INavigationMeshGetter>())
                {
                    bool here = nm.Data?.Parent switch
                    {
                        IWorldspaceNavmeshParentGetter w => inWorld is { } wk && w.Parent.FormKey == wk && Reaches(nm.Data!.Vertices),
                        ICellNavmeshParentGetter c => inCell is { } ck && c.Parent.FormKey == ck,
                        _ => false,
                    };
                    if (here) under.Add(nm.FormKey);   // the winner's own geometry decides below (a later deletion drops it)
                }
                }
                catch (Exception ex) when (ex is not Refusal)
                {
                    throw new Refusal($"navedit marker: the navmeshes of {Path.GetFileName(p)} could not be read ({ex.GetType().Name}: {ex.Message}); check the plugin, or name the navmeshes in MARKERS.json instead of a worldspace or cell.");
                }
            }
            foreach (var k in under.OrderBy(k => k.ToString(), StringComparer.Ordinal))
                if (!meshes.ContainsKey(k)) { if (Load(k, false)) found.Add(k); else absent.Add(k); }   // a deleted or emptied winner: none there
        }
        // follow edge links outward from every loaded navmesh whose ground lies within reach of a marker, until none adds
        // one: a navmesh the load order does not have is absent (an edge into it is open), never silently skipped
        double reach = Math.Max(2 * margin, 128);
        bool Near(NavmeshGeometry g) => markers.Any(m => g.Vertices.Count > 0
            && m.At.X >= g.Vertices.Min(v => v.X) - reach && m.At.X <= g.Vertices.Max(v => v.X) + reach
            && m.At.Y >= g.Vertices.Min(v => v.Y) - reach && m.At.Y <= g.Vertices.Max(v => v.Y) + reach);
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var g in meshes.Values.Where(Near).ToList())
                foreach (var l in (g.EdgeLinks ?? Array.Empty<NavEdgeLink>()).Select(l => l.Mesh).Distinct())
                {
                    if (meshes.ContainsKey(l) || absent.Contains(l)) continue;
                    if (Load(l, false)) grew = true; else absent.Add(l);
                }
        }
        static JsonNode? Fk(FormKey? k) => k is null ? null : JsonValue.Create(k.Value.ToString());
        static double R(double v) => Math.Round(v, 1);
        var rows = new JsonArray();
        int refused = 0;
        foreach (var (name, at) in markers)
        {
            var r = NavMarker.Check(meshes, at, margin, stepLimit, absent);
            if (r.Verdict != "inside") refused++;
            rows.Add(new JsonObject
            {
                ["name"] = name, ["at"] = Arr(new[] { at.X, at.Y, at.Z }), ["verdict"] = r.Verdict, ["why"] = Arr(r.Why),
                ["standing"] = r.Mesh is null ? null : new JsonObject { ["navmesh"] = Fk(r.Mesh), ["triangle"] = r.Triangle, ["z"] = R(r.StandZ!.Value), ["markerAbove"] = R(r.Above!.Value) },
                ["openEdge"] = r.OpenEdge is null ? null : new JsonObject
                {
                    ["navmesh"] = Fk(r.OpenEdge.Mesh), ["triangle"] = r.OpenEdge.Triangle, ["edge"] = r.OpenEdge.Edge, ["distance"] = R(r.OpenEdge.Distance),
                    ["at"] = Arr(new[] { R(r.OpenEdge.At.X), R(r.OpenEdge.At.Y), R(r.OpenEdge.At.Z) }),
                    ["beyond"] = r.Beyond is null ? null : r.Beyond.Mesh is null ? new JsonObject { ["nothingWithin"] = NavMarker.BeyondReach }
                        : new JsonObject { ["navmesh"] = Fk(r.Beyond.Mesh), ["triangle"] = r.Beyond.Triangle, ["step"] = R(r.Beyond.Step),
                                           ["gapAtLeast"] = R(r.Beyond.Gap), ["gapUnder"] = R(r.Beyond.Gap + NavMarker.BeyondStep) },
                },
                ["noOpenEdgeWithin"] = r.OpenEdge is null && r.Mesh is not null ? R(r.Reach) : null,
                ["linkSteps"] = new JsonArray(r.LinkSteps.Select(s => (JsonNode)new JsonObject
                {
                    ["navmesh"] = Fk(s.Mesh), ["triangle"] = s.Triangle, ["edge"] = s.Edge, ["to"] = Fk(s.ToMesh), ["toTriangle"] = s.ToTriangle,
                    ["type"] = s.Type, ["distance"] = R(s.Distance), ["step"] = R(s.Step),
                }).ToArray()),
                ["unfollowedLinks"] = new JsonArray(r.Unfollowed.Select(u => (JsonNode)new JsonObject { ["navmesh"] = u.Mesh.ToString(), ["distance"] = R(u.Distance) }).ToArray()),
            });
            Console.WriteLine($"{name}: {r.Verdict}" + (r.OpenEdge is { } oe ? $" (open edge {oe.Distance:0} u)" : "") + (r.Why.Count > 0 ? " - " + string.Join(" ", r.Why) : ""));
        }
        var outDoc = new JsonObject
        {
            ["tool"] = "navedit marker", ["instance"] = t.Instance, ["game"] = t.Game, ["profile"] = t.Profile, ["enable"] = Arr(enables),
            ["orderNotes"] = applied, ["order"] = Arr(order),
            ["margin"] = margin, ["stepLimit"] = stepLimit, ["maxAbove"] = NavMarker.DefaultMaxAbove, ["navmeshes"] = winners,
            ["absent"] = Arr(absent.Select(k => k.ToString()).OrderBy(x => x, StringComparer.Ordinal)),
            ["found"] = Arr(found.Select(k => k.ToString())), ["worldspace"] = inWorld?.ToString(), ["cell"] = inCell?.ToString(), ["markers"] = rows,
        };
        File.WriteAllText(outPath, outDoc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"markers: {markers.Count - refused} inside, {refused} refused; written to {outPath}");
        return refused == 0 ? 0 : 1;
    }

    public static int Write(Dictionary<string, string> f)
    {
        var planPath = Need(f, "--plan", "PLAN.json, from navedit plan");
        var outPath = Path.GetFullPath(Need(f, "--out", "PATCH.esp, the new patch plugin"));
        if (File.Exists(outPath)) throw new Refusal($"navedit write: {outPath} already exists; give a new patch name (navedit writes new patches only).");
        JsonNode plan;
        try { plan = JsonNode.Parse(File.ReadAllText(planPath))!; }
        catch (Exception ex) { throw new Refusal($"navedit write: the plan '{planPath}' could not be read ({ex.Message}); write it with navedit plan."); }
        var t = new Target((string)plan["instance"]!, (string)plan["game"]!, (string)plan["profile"]!);
        var order = t.Order();
        if (!order.SequenceEqual(plan["order"]!.AsArray().Select(x => (string)x!), StringComparer.OrdinalIgnoreCase))
            throw new Refusal($"navedit write: the load order of '{t.Profile}' changed since the plan was made; run navedit plan again.");
        var rows = plan["edits"]!.AsArray();
        if (rows.Any(r => (string?)r!["status"] != "planned")) throw new Refusal("navedit write: the plan has refused edits; fix or drop them and plan again, so a patch never carries half an edit set.");
        var again = Compute(t, order, new JsonArray(rows.Select(r => r!["edit"]!.DeepClone()).ToArray()));
        for (int i = 0; i < rows.Count; i++)
            if (!again[i].Ok || again[i].Row["ops"]!.ToJsonString() != rows[i]!["ops"]!.ToJsonString())
                throw new Refusal($"navedit write: {rows[i]!["navmesh"]} no longer plans the same on this load order; run navedit plan again.");

        var root = CheckoutLayout.FindRoot() ?? throw new Refusal("navedit write: the SkyWright checkout (SkyWright.sln) was not found above this program, so the rulebook cannot be located.");
        var corpus = CheckoutLayout.Corpus(root);
        if (!File.Exists(corpus)) throw new Refusal($"navedit write: the rulebook {corpus} is missing; generate it (see CLAUDE.md, Build and check).");
        var rulebook = CorpusRulebook.Load(corpus);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        var edits = again.SelectMany(p => p.Ops).Select(o => new WritePatchBuilder.PatchEdit
        {
            Target = FormKey.Factory(o.FormId), Path = o.FieldPath.Split('.'), Verb = o.Op, Key = o.Key, Value = o.Value, FromPlugin = o.FromPlugin,
            Struct = o.ComposeNode is JsonObject c ? MapCompose(c) : null,   // nested sets too (a NAVI whole-entry add), one mapping
        }).ToList();
        using (var resolver = LoadOrderResolver.Build(order))
        {
            var applied = WritePatchBuilder.Apply(resolver, rulebook, edits, outPath, extend: false);
            if (!applied.Success) { Console.Error.WriteLine($"navedit write: the write engine refused the patch: {applied.Error}"); return 1; }
            foreach (var op in applied.Ops.Where(o => !o.Applied)) { Console.Error.WriteLine($"navedit write: {op.Label} was not applied: {op.Error}"); return 1; }

            // a use_version that gains a neighbour, or a door edit, changes NAVI: never forward the winner's NAVI over it
            if (ForwardCarried(resolver, outPath, order, NaviPlanned(again)) is { } ferr) { Console.Error.WriteLine(ferr); return 1; }
        }
        var masters = again.SelectMany(p => (p.Row["useVersion"]?["masters"]?.AsArray().Select(x => (string)x!) ?? Array.Empty<string>())
                               .Concat(p.Row["useVersion"]?["navi"]?["entries"] is JsonArray { Count: > 0 } && (string?)p.Row["useVersion"]?["navi"]?["winner"]?["plugin"] is { } nw
                                       ? new[] { nw } : Array.Empty<string>()))   // the NAVI winner too, when the row changes NAVI
                           .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (masters.Count > 0 && AddMasters(outPath, order, masters) is { } merr) { Console.Error.WriteLine($"navedit write: {merr}"); return 1; }
        // every edit's NAVI change, as one: the read-back holds the patch's NAVI to the winner's plus all of them
        var naviEntries = again.Where(p => p.Row["navi"]?["entries"] is JsonArray a && a.Count > 0).ToList();
        JsonObject? naviAll = null;
        if (naviEntries.Count > 0)
        {
            naviAll = (JsonObject)naviEntries[0].Row["navi"]!.DeepClone();
            naviAll["entries"] = new JsonArray(naviEntries.SelectMany(p => p.Row["navi"]!["entries"]!.AsArray()).Select(x => x!.DeepClone()).ToArray());
        }
        return ReadBack(outPath, order, again, masters, naviAll);
    }

    /// <summary>The containers the engine carried (each navmesh's cell and worldspace) made their winners' versions: every
    /// carried record the read-back finds is not its winner's is forwarded from that winner. Null when done, else the error.</summary>
    static bool NaviPlanned(IEnumerable<Planned> planned) => planned.Any(p => p.Row["navi"]?["entries"] is JsonArray { Count: > 0 });

    static string? ForwardCarried(LoadOrderResolver resolver, string outPath, IReadOnlyList<string> order, bool naviPlanned = false)
    {
        // a NAVI the plan changes is not a carried record: forwarding its winner would undo the planned entries (the S2RN
        // known answer, 2026-11-22: 144 entries written where 145 were planned); the read-back holds it to the plan instead
        JsonArray carried;
        using (var patch = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE)) carried = NavRepairReadBack.Carried(patch, order, naviPlanned).Records;
        foreach (var g in carried.Where(c => (bool?)c!["holds"] == false && c["winner"] is not null).GroupBy(c => (string)c!["winner"]!, StringComparer.OrdinalIgnoreCase))
        {
            var fw = WritePatchBuilder.ForwardRecords(resolver,
                g.Select(c => new WritePatchBuilder.ForwardSpec { Target = FormKey.Factory((string)c!["record"]!), FromPlugin = g.Key }).ToList(),
                outPath, extend: true, sourceParam: g.Key);
            if (!fw.Success) return $"navedit write: forwarding the carried records from {g.Key} failed: {fw.Error}";
        }
        return null;
    }

    // THE COMPOSE WIRE SHAPE, COPIED: a repair plan's op carries housecarl_apply's compose JSON ({type, fields, ctor_args,
    // sets: [{path, verb, key, value, compose}]}, NAVI's NavigationMapInfo with its Parent arm among them). Its owner is the
    // houseCARL server's WriteMappers.MapStruct and SplitPath (housecarl-mcp, which navedit does not reference); this is a
    // faithful translation of them, and tests/test_navedit.py's drift case fingerprints both so a change there is reviewed.
    static string[] SplitPath(string dotted) => dotted.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static StructSpec MapCompose(JsonObject c)
    {
        var type = (string?)c["type"];
        if (string.IsNullOrWhiteSpace(type)) throw new Refusal("navedit write: a compose in the repair plan names no type; write the plan again with navownprobe --repair-plan.");
        List<WriteRequest>? sets = null;
        if (c["sets"] is JsonArray ss && ss.Count > 0)
        {
            sets = new List<WriteRequest>(ss.Count);
            foreach (var n in ss)
            {
                var path = (string?)n!["path"];
                if (string.IsNullOrWhiteSpace(path)) throw new Refusal("navedit write: a compose's nested set in the repair plan has no path; write the plan again.");
                var verb = (string?)n["verb"];
                sets.Add(new WriteRequest
                {
                    RecordType = type!, Path = SplitPath(path!), Verb = string.IsNullOrWhiteSpace(verb) ? "Set" : verb!,
                    Key = (string?)n["key"], Value = (string?)n["value"],
                    Struct = n["compose"] is JsonObject nc ? MapCompose(nc) : null,
                });
            }
        }
        return new StructSpec
        {
            Type = type!,
            Fields = (c["fields"] as JsonObject)?.ToDictionary(kv => kv.Key, kv => (string)kv.Value!),
            CtorArgs = (c["ctor_args"] as JsonArray)?.Select(x => (string)x!).ToArray(),
            Sets = sets,
        };
    }

    /// <summary>`navedit write --repair-plan` (R1): a navownprobe repair plan written in this process.</summary>
    public static int WriteRepair(Dictionary<string, string> f)
    {
        if (f.ContainsKey("--plan")) throw new Refusal("navedit write: give --plan (a navedit plan) or --repair-plan (a navownprobe repair plan), not both.");
        var planPath = Need(f, "--repair-plan", "PLAN.json, from navownprobe --repair-plan");
        var outPath = Path.GetFullPath(Need(f, "--out", "PATCH.esp, the new patch plugin"));
        if (File.Exists(outPath)) throw new Refusal($"navedit write: {outPath} already exists; give a new patch name (navedit writes new patches only).");
        JsonNode plan;
        try { plan = JsonNode.Parse(File.ReadAllText(planPath))!; }
        catch (Exception ex) { throw new Refusal($"navedit write: the repair plan '{planPath}' could not be read ({ex.Message}); write it with navownprobe --repair-plan."); }
        var order = (plan["order"] as JsonArray)?.Select(x => (string)x!).ToList() ?? new List<string>();
        if (order.Count == 0) throw new Refusal("navedit write: the repair plan names no load order; write it again with a current navownprobe --repair-plan.");
        if (order.FirstOrDefault(p => !File.Exists(p)) is { } gone) throw new Refusal($"navedit write: the plan's load order names {gone}, which is not on disk; plan again on the current order.");
        var plans = (plan["plans"] as JsonArray ?? new JsonArray()).Select(p => p!).ToList();
        var refused = plans.Where(p => (string?)p["status"] == "refused").Select(p => (string)p["source"]!).ToList();
        if (plans.Count == refused.Count) throw new Refusal("navedit write: the repair plan has nothing planned (every navmesh refused or none listed), so there is no patch to write.");
        // the plan's ops are index-based on each winner as it was when planned: a winner changed since (a mod updated in
        // place) would take them onto the wrong triangles and the read-back, which applies the same change, would still hold
        foreach (var p in plans.Where(p => (string?)p["status"] != "refused"))
        {
            var wp = (string?)p["winner"]?["path"]; var want = (string?)p["winner"]?["sha256"];
            if (wp is null) continue;
            if (want is null) { Console.WriteLine($"WARNING: the repair plan does not record the hash of {Path.GetFileName(wp)} (a plan from before 2026-11-22), so a change to it since planning cannot be told; plan again with a current navownprobe --repair-plan to be sure."); continue; }
            using var fs = File.OpenRead(wp);
            if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)), want, StringComparison.OrdinalIgnoreCase))
                throw new Refusal($"navedit write: {Path.GetFileName(wp)} changed since the repair plan was made, so its ops may land on other triangles; plan again.");
        }
        var ops = (plan["ops"] as JsonArray ?? new JsonArray()).Select(o => o!).ToList();
        // use_version rows: re-planned here on the plan's order by navedit's own code (NavUseVersionPlan), and written from that
        // plan after its ops are required to equal the row's, as navedit write --plan does; their ops carry a CopyFrom's source
        var uvRows = NavRepairReadBack.UseVersionRows(plan);
        var uv = new List<(JsonNode Row, UseVersionPlan U)>();
        if (uvRows.Count > 0)
        {
            using var uvResolver = LoadOrderResolver.Build(order);
            var uvView = uvResolver.Capture();
            var (uvNavi, uvNaviOpen) = NaviView.Read(order);
            Dictionary<FormKey, (string Path, List<NavEdgeLink> Links)>? inbound = null;
            try
            {
                foreach (var row in uvRows)
                {
                    var key = FormKey.Factory((string)row["source"]!);
                    var from = (string)row["winner"]!["plugin"]!;
                    var w = uvView.ResolveWinner(key);
                    var wp = w is null ? null : order.FirstOrDefault(o => string.Equals(Path.GetFileName(o), w.Value.WinnerPlugin, StringComparison.OrdinalIgnoreCase));
                    var geom = wp is null ? null : Winning(wp, key);
                    if (geom is null) throw new Refusal($"navedit write: {key} has no winning geometry on the plan's order now, so carrying {from}'s version cannot be re-planned; plan again.");
                    var u = NavUseVersionPlan.IslandRefusal(uvNavi, key) is { } isl
                        ? new UseVersionPlan(isl, Array.Empty<string>(), new List<RepairOp>(), new(), null)
                        : NavUseVersionPlan.Plan(uvView, uvNavi, order, key, geom, w!.Value.WinnerPlugin, from, () => inbound ??= InboundIndex(order));
                    if (!u.Planned || NavRepairRunJson.OpsJson(u.Ops).ToJsonString() != row["edits"]!.ToJsonString())
                        throw new Refusal($"navedit write: carrying {from}'s version of {key} no longer plans the same on this load order ({u.Refuse ?? (u.Why.Count > 0 ? string.Join(" ", u.Why) : "other ops")}); plan again.");
                    uv.Add((row, u));
                }
            }
            finally { foreach (var m in uvNaviOpen) m.Dispose(); }
        }
        if (ops.Count == 0 && uv.Count == 0) throw new Refusal("navedit write: the repair plan carries no ops; write it again with navownprobe --repair-plan.");
        // the planner's exclusions, held again before anything is written (review of slice B, finding 3): a stale or edited plan
        // must not leave a patch on disk that no read-back can hold
        int uvNavis = uv.Count(x => x.Row["useVersion"]?["navi"]?["entries"] is JsonArray { Count: > 0 });
        if (uvNavis > 1 || (uvNavis == 1 && plan["navi"]?["entries"] is JsonArray { Count: > 0 }))
            throw new Refusal("navedit write: the repair plan carries more than one change to the navmesh info map, which one patch cannot be held to; plan again with a current navownprobe --repair-plan.");
        var rowSources = plans.Where(p => (string?)p["status"] != "refused" && (string?)p["class"] != "use_version").Select(p => FormKey.Factory((string)p["source"]!)).ToHashSet();
        var uvSeen = new HashSet<FormKey>();
        foreach (var (row, u) in uv)
            foreach (var k in u.Expect.Keys)
                if (rowSources.Contains(k) || !uvSeen.Add(k))
                    throw new Refusal($"navedit write: {k} is changed by carrying a version of {(string)row["source"]!} and by another row of the plan, which one patch cannot hold both of; plan again with a current navownprobe --repair-plan.");
        var root = CheckoutLayout.FindRoot() ?? throw new Refusal("navedit write: the SkyWright checkout (SkyWright.sln) was not found above this program, so the rulebook cannot be located.");
        var corpus = CheckoutLayout.Corpus(root);
        if (!File.Exists(corpus)) throw new Refusal($"navedit write: the rulebook {corpus} is missing; generate it (see CLAUDE.md, Build and check).");
        var rulebook = CorpusRulebook.Load(corpus);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var edits = ops.Select(o => new WritePatchBuilder.PatchEdit
        {
            Target = FormKey.Factory((string)o["formid"]!), Path = SplitPath((string)o["field_path"]!), Verb = (string)o["op"]!,
            Key = (string?)o["key"], Value = (string?)o["value"],
            Struct = o["compose"] is JsonObject c ? MapCompose(c) : null,
        }).ToList();
        edits.AddRange(uv.SelectMany(x => x.U.Ops).Select(o => new WritePatchBuilder.PatchEdit
        {
            Target = FormKey.Factory(o.FormId), Path = o.FieldPath.Split('.'), Verb = o.Op, Key = o.Key, Value = o.Value, FromPlugin = o.FromPlugin,
            Struct = o.ComposeNode is JsonObject uc ? MapCompose(uc) : null,   // the same mapping navedit write --plan uses
        }));
        var uvNaviRow = uv.Select(x => x.Row["useVersion"]?["navi"]).FirstOrDefault(n => n?["entries"] is JsonArray { Count: > 0 });
        using (var resolver = LoadOrderResolver.Build(order))
        {
            var applied = WritePatchBuilder.Apply(resolver, rulebook, edits, outPath, extend: false);
            if (!applied.Success) { Console.Error.WriteLine($"navedit write: the write engine refused the patch: {applied.Error}"); return 1; }
            foreach (var op in applied.Ops.Where(o => !o.Applied)) { Console.Error.WriteLine($"navedit write: {op.Label} was not applied: {op.Error}"); return 1; }
            bool naviPlanned = plan["navi"]?["entries"] is JsonArray ne && ne.Count > 0 || uvNaviRow is not null;
            if (ForwardCarried(resolver, outPath, order, naviPlanned) is { } ferr) { Console.Error.WriteLine(ferr); return 1; }
        }
        // the ruling of 2026-11-22 (825ee14): a resolution patch masters every plugin whose version it supersedes; here the
        // version carried is each overridden navmesh's winner, so each winner's plugin is a master (else a re-sort can put
        // the patch below it, and the repair silently stops applying). The same master step as use_version.
        // a patch that carries a NAVI change supersedes the NAVI winner too: master it, or a re-sort below it loses the NAVI
        // change while the navmesh edits stay (houseCARL, 2026-11-22)
        var naviWinner = plan["navi"]?["entries"] is JsonArray { Count: > 0 } ? (string?)plan["navi"]?["winner"]?["plugin"] : null;
        // a use_version row supersedes every version after its source (its row's masters) and, with a NAVI change, the NAVI winner
        // ... and each re-pointed neighbour's winner, whose version its override supersedes (review of slice B, finding 4)
        var uvMasters = uv.SelectMany(x => x.Row["useVersion"]?["masters"]?.AsArray().Select(m => (string?)m) ?? Array.Empty<string?>())
                          .Concat(uv.SelectMany(x => x.U.Expect.Values.Where(e => !e.Rebuilt).Select(e => (string?)Path.GetFileName(e.WinnerPath))))
                          .Append(uvNaviRow is not null ? (string?)uvNaviRow["winner"]?["plugin"] : null);
        var masters = plans.Where(p => (string?)p["status"] != "refused").Select(p => (string?)p["winner"]?["plugin"]).Append(naviWinner).Concat(uvMasters)
                           .Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (masters.Count > 0 && AddMasters(outPath, order, masters) is { } merr) { Console.Error.WriteLine($"navedit write: {merr}"); return 1; }
        foreach (var r in refused) Console.WriteLine($"not written (refused in the plan): {r}");
        Console.WriteLine($"patch written: {outPath} ({ops.Count + uv.Sum(x => x.U.Ops.Count)} ops, {plans.Count - refused.Count} navmesh plan(s)); its read-back:");
        var rbPath = Path.ChangeExtension(outPath, null) + ".readback.json";
        var uvExpect = uv.SelectMany(x => x.U.Expect).ToDictionary(kv => kv.Key, kv => kv.Value);
        int rb = NavRepairReadBack.Run(planPath, outPath, rbPath, uvExpect);
        using var written = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
        var declared = written.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToList();
        var missing = masters.Where(m => !declared.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
        var pos = declared.Select(d => order.FindIndex(o => string.Equals(Path.GetFileName(o), d, StringComparison.OrdinalIgnoreCase))).ToList();
        bool inOrder = pos.All(i => i >= 0) && pos.Zip(pos.Skip(1)).All(z => z.First < z.Second);
        bool mastersHold = missing.Count == 0 && inOrder;
        if (File.Exists(rbPath) && JsonNode.Parse(File.ReadAllText(rbPath)) is JsonObject rbDoc)
        {   // the read-back file says the whole verdict, the masters included
            rbDoc["masters"] = new JsonObject { ["declared"] = Arr(declared), ["required"] = Arr(masters), ["holds"] = mastersHold };
            if (!mastersHold) rbDoc["holds"] = false;
            File.WriteAllText(rbPath, rbDoc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        if (!mastersHold)
        {
            Console.WriteLine($"   FAILS  the patch's masters [{string.Join(", ", declared)}] " + (missing.Count > 0 ? $"lack {string.Join(", ", missing)}, whose versions it supersedes" : "are not in load order"));
            return 1;
        }
        Console.WriteLine($"   holds  the patch masters every winner it supersedes ({string.Join(", ", masters)}), in load order");
        return rb;
    }

    /// <summary>The written patch against the plan: every changed navmesh's vertices, triangles (corners, edge fields, flags),
    /// links, doors, box and grid; each kept triangle's cover fields against its winner's; no other navmesh in the patch;
    /// every carried record equal to its winner. One line per check; 0 when all hold.</summary>
    static int ReadBack(string patchPath, IReadOnlyList<string> order, List<Planned> planned, IReadOnlyList<string> masters, JsonObject? navi)
    {
        int failed = 0;
        var expect = planned.SelectMany(p => p.Expect).ToDictionary(kv => kv.Key, kv => kv.Value);
        using var patch = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
        // every plugin a use_version supersedes is a master, and the masters stand in load order
        var declared = patch.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToList();
        var missing = masters.Where(m => !declared.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
        var pos = declared.Select(d => order.ToList().FindIndex(o => string.Equals(Path.GetFileName(o), d, StringComparison.OrdinalIgnoreCase))).ToList();
        bool inOrder = pos.All(i => i >= 0) && pos.Zip(pos.Skip(1)).All(z => z.First < z.Second);
        if (missing.Count > 0 || !inOrder) { Console.WriteLine($"FAIL the patch's masters [{string.Join(", ", declared)}] " + (missing.Count > 0 ? $"lack {string.Join(", ", missing)}, whose versions it supersedes" : "are not in load order")); failed++; }
        else if (masters.Count > 0) Console.WriteLine($"ok   the patch masters every plugin it supersedes ({string.Join(", ", masters)}), in load order");
        var raw = patch.EnumerateMajorRecords<INavigationMeshGetter>().ToDictionary(n => n.FormKey);
        var geoms = PlacementExtract.FromMod(patch).SelectMany(cp => cp.Navmeshes).ToDictionary(n => n.FormKey);
        foreach (var (key, (want, oldIndex, winnerPath, rebuilt)) in expect)
        {
            // the per-navmesh check's one owner (NavRepairReadBack.GeometryDifferences): R1 holds use_version rows to it too
            var why = NavRepairReadBack.GeometryDifferences(key, want, oldIndex, winnerPath, rebuilt, geoms.GetValueOrDefault(key), raw.GetValueOrDefault(key));
            if (why is null) { Console.WriteLine($"FAIL {key}: not in the patch"); failed++; continue; }
            Console.WriteLine(why.Count == 0 ? $"ok   {key}: vertices, triangles, links, doors, cover, box and grid as planned{(rebuilt ? " (grid rebuilt)" : " (links only; grid kept as its winner's)")}" : $"FAIL {key}: {string.Join("; ", why)}");
            if (why.Count > 0) failed++;
        }
        var extra = raw.Keys.Where(k => !expect.ContainsKey(k)).ToList();
        if (extra.Count > 0) { Console.WriteLine($"FAIL the patch carries navmeshes the plan does not: {string.Join(", ", extra)}"); failed++; }
        var (recs, _, bad) = NavRepairReadBack.Carried(patch, order, naviPlanned: navi is not null);
        if (navi is not null)
        {
            var (ndiff, ncompared) = NavRepairReadBack.NaviCheck(navi, patch);
            Console.WriteLine(ndiff.Count == 0 ? $"ok   NAVI {navi["record"]} is its winner's with exactly the planned entries changed ({ncompared} entries compared)" : $"FAIL NAVI {navi["record"]}: {string.Join("; ", ndiff)}");
            if (ndiff.Count > 0) failed++;
        }
        // each carried version's NAVI entry, as the game takes it with the patch last, against the version itself (one owner:
        // NavRepairReadBack.NaviFollowsVersion, R1's read-back holds its use_version rows to it too)
        foreach (var p in planned.Where(p => p.Ok && p.Row["useVersion"] is JsonObject))
        {
            var key = FormKey.Factory((string)p.Row["navmesh"]!);
            var from = (string?)p.Row["useVersion"]!["fromPath"] ?? "";
            var fd = NavRepairReadBack.NaviFollowsVersion(order, patchPath, key, from);
            Console.WriteLine(fd.Count == 0 ? $"ok   NAVI follows {Path.GetFileName(from)}'s version of {key}: its linked doors are the version's door table, in order, and its neighbours the version's link targets"
                                            : $"FAIL NAVI does not follow {Path.GetFileName(from)}'s version of {key}: {string.Join("; ", fd)}");
            if (fd.Count > 0) failed++;
        }
        foreach (var c in recs) Console.WriteLine($"{((bool)c!["holds"]! ? "ok  " : "FAIL")} carried {c["type"]} {c["record"]} equals its winner {c["winner"]}" + ((bool)c["holds"]! ? "" : ": " + string.Join("; ", c["differences"]!.AsArray().Select(x => (string)x!))));
        failed += bad;
        Console.WriteLine(failed == 0 ? $"patch written and read back: {patchPath}" : $"navedit write: {failed} check(s) failed on {patchPath}; do not enable it.");
        return failed == 0 ? 0 : 1;
    }
}
