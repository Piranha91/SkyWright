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
if (args.Length < 1 || args[0] is not ("plan" or "write")) { Console.Error.WriteLine("usage: navedit plan --edits EDITS.json --out PLAN.json [--instance DIR --game DIR --profile NAME] | navedit write --plan PLAN.json --out PATCH.esp | navedit --selftest"); return 2; }

var flags = new Dictionary<string, string>(StringComparer.Ordinal);
for (int i = 1; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) { Console.Error.WriteLine($"navedit: '{args[i]}' is not a flag with a value; see navedit with no arguments for the usage."); return 2; }
    flags[args[i]] = args[++i];
}
string[] known = args[0] == "plan" ? new[] { "--edits", "--out", "--instance", "--game", "--profile" } : new[] { "--plan", "--out" };
foreach (var k in flags.Keys)
    if (!known.Contains(k)) { Console.Error.WriteLine($"navedit {args[0]}: '{k}' is not one of its flags ({string.Join(", ", known)})."); return 2; }

try { return args[0] == "plan" ? NavEditCli.Plan(flags) : NavEditCli.Write(flags); }
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
                bool isMove = e["move"] is not null, isTri = e["remove"] is not null || e["add"] is not null, isCut = e["cut"] is not null, isUse = e["use_version"] is not null;
                if (w is null || path is null) refuse = $"{key} is in no plugin of this load order; check the FormKey and the profile.";
                else if (geom is null) refuse = $"{key} has no geometry in its winner {w.Value.WinnerPlugin} (deleted or empty), so there is nothing to edit.";
                else if ((isMove ? 1 : 0) + (isTri ? 1 : 0) + (isCut ? 1 : 0) + (isUse ? 1 : 0) != 1) refuse = $"The edit of {key} must do one kind of thing: move vertices, remove and add triangles, cut, or use a version; split it into separate plans.";
                else if (navi.Problem is { } np) refuse = $"The navmesh info map cannot be read ({np}), so whether {key} is an island is not known.";
                else if (navi.Effective.TryGetValue(key, out var ent) && ent.Src.Map.MapInfos[ent.Index].Island is not null)
                    refuse = $"{key} is an island in the navmesh info map, which keeps its own copy of the geometry; editing islands is not supported yet.";
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
                else if (refuse is null && isUse)
                {
                    var from = (string?)e["use_version"] ?? "";
                    var fromPath = order.FirstOrDefault(p => string.Equals(Path.GetFileName(p), from, StringComparison.OrdinalIgnoreCase));
                    var version = fromPath is null ? null : Winning(fromPath, key);
                    if (fromPath is null) refuse = $"{from} is not in this load order, so its version of {key} (and its masters) cannot be read; enable it, or name a plugin of the order.";
                    else if (string.Equals(from, w!.Value.WinnerPlugin, StringComparison.OrdinalIgnoreCase)) refuse = $"{from} already wins {key}, so there is no other version to use.";
                    else if (version is null) refuse = $"{from} has no version of {key}, or deletes it, so there is nothing to carry.";
                    else
                    {
                        // this order's winners of every navmesh the version links into and every navmesh linking into the winner
                        inboundIndex ??= InboundIndex(order);
                        var near = new Dictionary<FormKey, NavmeshGeometry>();
                        foreach (var m in (version.EdgeLinks ?? Array.Empty<NavEdgeLink>()).Select(l => l.Mesh).Distinct())
                            if (m != key && view.ResolveWinner(m) is { } mw && order.FirstOrDefault(p => string.Equals(Path.GetFileName(p), mw.WinnerPlugin, StringComparison.OrdinalIgnoreCase)) is { } mp && Winning(mp, m) is { } mg) near[m] = mg;
                        foreach (var (fk, (p, links)) in inboundIndex)
                            if (fk != key && !near.ContainsKey(fk) && links.Any(l => l.Mesh == key) && Winning(p, fk) is { } og) near[fk] = og;
                        var r = NavEdit.UseVersion(geom!, version, Path.GetFileName(fromPath), near, NavRuleDefaults.Edge);
                        why = r.Why; ops = r.Ops.ToList();
                        // each door's reference must resolve in this order
                        var badDoors = r.Doors.Where(d => view.ResolveWinner(d) is null).ToList();
                        if (r.Status == "planned" && badDoors.Count > 0) { why = new[] { $"{from}'s version stands before door(s) {string.Join(", ", badDoors)}, which no plugin of this order provides." }; ops = new(); }
                        // the navmesh info map: each neighbour the version gains, both ways
                        JsonObject? naviRow = null;
                        if (why.Count == 0 && r.Gained.Count > 0)
                        {
                            var need = new Dictionary<FormKey, IReadOnlyCollection<FormKey>> { [key] = r.Gained };
                            foreach (var m in r.Gained) need[m] = new[] { key };
                            var (np, refusedNavi) = NavRepairNavi.ForPairs(need, navi);
                            if (refusedNavi.Count > 0) { why = refusedNavi.Select(kv => $"The navmesh info map cannot gain the version's new neighbours: {kv.Value}.").ToArray(); ops = new(); }
                            else { ops.AddRange(np.Ops); naviRow = new JsonObject { ["record"] = np.Record?.ToString(), ["winner"] = np.WinnerPlugin, ["why"] = Arr(np.Why) }; }
                        }
                        if (why.Count == 0)
                        {
                            expect[key] = (r.After!, Enumerable.Range(0, r.After!.Triangles.Count).ToArray(), fromPath, true);
                            foreach (var (fk, og) in r.Others) expect[fk] = (og, Enumerable.Range(0, og.Triangles.Count).ToArray(), inboundIndex[fk].Path, false);   // a re-pointed neighbour keeps its own winner's grid
                        }
                        row["useVersion"] = new JsonObject
                        {
                            ["from"] = from, ["fromPath"] = fromPath,
                            ["vertices"] = new JsonObject { ["before"] = geom!.Vertices.Count, ["after"] = version.Vertices.Count },
                            ["triangles"] = new JsonObject { ["before"] = geom.Triangles.Count, ["after"] = version.Triangles.Count },
                            ["linksMatched"] = r.Matched, ["linksChanged"] = r.Changed, ["ledgeLinksCarriedUnverified"] = r.LedgesCarried, ["linksCut"] = 0, ["neighbourLinksRepointed"] = r.Repointed,
                            ["navmeshesRepointed"] = Arr(r.Others.Keys.Select(k => k.ToString())), ["doorsChecked"] = r.Doors.Count,
                            ["gainedNeighbours"] = Arr(r.Gained.Select(k => k.ToString())), ["navi"] = naviRow, ["grid"] = "rebuilt",
                            // the plugins whose versions of K the patch supersedes (every one after the source, in load order):
                            // the patch masters each, so nothing can sort it above them and let one win K again (houseCARL,
                            // 2026-11-22). A neighbour whose links are only re-pointed needs none.
                            ["masters"] = Arr((view.TouchingPlugins(key) ?? Array.Empty<string>())
                                .SkipWhile(n => !string.Equals(n, Path.GetFileName(fromPath), StringComparison.OrdinalIgnoreCase)).Skip(1)),
                        };
                    }
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
                if (refuse is not null) { why = new[] { refuse }; ops = new(); expect.Clear(); }
                foreach (var fk in expect.Keys) claimed[fk] = key.ToString();
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
    static Dictionary<FormKey, (string Path, List<NavEdgeLink> Links)> InboundIndex(IReadOnlyList<string> order)
    {
        var win = new Dictionary<FormKey, (string, List<NavEdgeLink>)>();
        foreach (var p in order)
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(p, SkyrimRelease.SkyrimSE);
            foreach (var nm in mod.EnumerateMajorRecords<INavigationMeshGetter>())
            {
                if (nm.IsDeleted || nm.Data is not { } d) { win.Remove(nm.FormKey); continue; }
                win[nm.FormKey] = (p, d.EdgeLinks.Select(l => new NavEdgeLink(l.Mesh.FormKey, l.TriangleIndex, l.Unknown)).ToList());
            }
        }
        return win;
    }

    /// <summary>Each triangle's cover fields as one plugin has them (the geometry model does not carry them).</summary>
    static List<(bool, ushort)> Cover(string pluginPath, FormKey key)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
        return mod.EnumerateMajorRecords<INavigationMeshGetter>().First(n => n.FormKey == key).Data!.Triangles.Select(t => (t.IsCover, t.CoverFlags)).ToList();
    }

    /// <summary>The navmesh as one plugin has it, through the one NAVM reader (PlacementExtract); null when absent or deleted.</summary>
    static NavmeshGeometry? Winning(string pluginPath, FormKey key)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
        foreach (var cp in PlacementExtract.FromMod(mod))
            foreach (var n in cp.Navmeshes)
                if (n.FormKey == key) return n.Deleted ? null : n;
        return null;
    }

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
            Struct = o.ComposeNode is { } c ? new StructSpec
            {
                Type = (string)c["type"]!,
                Fields = c["fields"]!.AsObject().ToDictionary(kv => kv.Key, kv => (string)kv.Value!),
            } : null,
        }).ToList();
        using (var resolver = LoadOrderResolver.Build(order))
        {
            var applied = WritePatchBuilder.Apply(resolver, rulebook, edits, outPath, extend: false);
            if (!applied.Success) { Console.Error.WriteLine($"navedit write: the write engine refused the patch: {applied.Error}"); return 1; }
            foreach (var op in applied.Ops.Where(o => !o.Applied)) { Console.Error.WriteLine($"navedit write: {op.Label} was not applied: {op.Error}"); return 1; }

            // the containers the engine carried (each navmesh's cell and worldspace) must be their winners' versions
            JsonArray carried;
            using (var patch = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE)) carried = NavRepairReadBack.Carried(patch, order).Records;
            foreach (var g in carried.Where(c => (bool?)c!["holds"] == false && c["winner"] is not null).GroupBy(c => (string)c!["winner"]!, StringComparer.OrdinalIgnoreCase))
            {
                var fw = WritePatchBuilder.ForwardRecords(resolver,
                    g.Select(c => new WritePatchBuilder.ForwardSpec { Target = FormKey.Factory((string)c!["record"]!), FromPlugin = g.Key }).ToList(),
                    outPath, extend: true, sourceParam: g.Key);
                if (!fw.Success) { Console.Error.WriteLine($"navedit write: forwarding the carried records from {g.Key} failed: {fw.Error}"); return 1; }
            }
        }
        var masters = again.SelectMany(p => p.Row["useVersion"]?["masters"]?.AsArray().Select(x => (string)x!) ?? Array.Empty<string>())
                           .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (masters.Count > 0 && AddMasters(outPath, order, masters) is { } merr) { Console.Error.WriteLine($"navedit write: {merr}"); return 1; }
        return ReadBack(outPath, order, again, masters);
    }

    /// <summary>The written patch against the plan: every changed navmesh's vertices, triangles (corners, edge fields, flags),
    /// links, doors, box and grid; each kept triangle's cover fields against its winner's; no other navmesh in the patch;
    /// every carried record equal to its winner. One line per check; 0 when all hold.</summary>
    static int ReadBack(string patchPath, IReadOnlyList<string> order, List<Planned> planned, IReadOnlyList<string> masters)
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
            var why = new List<string>();
            if (!geoms.TryGetValue(key, out var got) || !raw.TryGetValue(key, out var rec) || rec.Data is not { } d) { Console.WriteLine($"FAIL {key}: not in the patch"); failed++; continue; }
            if (!got.Vertices.SequenceEqual(want.Vertices)) why.Add($"vertices differ ({got.Vertices.Count} written, {want.Vertices.Count} planned)");
            if (!got.Triangles.SequenceEqual(want.Triangles))
            {
                int i = Enumerable.Range(0, Math.Min(got.Triangles.Count, want.Triangles.Count)).FirstOrDefault(k => got.Triangles[k] != want.Triangles[k], -1);
                why.Add(i < 0 ? $"{got.Triangles.Count} triangles written, {want.Triangles.Count} planned" : $"triangle {i} is {got.Triangles[i]}, planned {want.Triangles[i]}");
            }
            if (!(got.EdgeLinks ?? []).SequenceEqual(want.EdgeLinks ?? [])) why.Add("the edge-link table differs from the plan");
            if (!(got.DoorTriangles ?? []).SequenceEqual(want.DoorTriangles ?? [])) why.Add("the door table differs from the plan");
            using (var wmod = SkyrimMod.CreateFromBinaryOverlay(winnerPath, SkyrimRelease.SkyrimSE))
            {
                var wd = wmod.EnumerateMajorRecords<INavigationMeshGetter>().First(n => n.FormKey == key).Data!;
                if (rebuilt)
                {
                    var g = NavGrid.Rebuild(got.Vertices, got.Triangles.Select(x => ((int)x.A, (int)x.B, (int)x.C)).ToList());
                    if (d.NavmeshGridDivisor != g.Divisor || d.MaxDistanceX != g.SectionX || d.MaxDistanceY != g.SectionY || !d.NavmeshGrid.Span.SequenceEqual(g.CellBytes))
                        why.Add("the grid is not the one NavGrid rebuilds from the written navmesh");
                    if (got.Min != g.Min || got.Max != g.Max) why.Add("the box is not the used vertices' extent");
                }
                else if (d.NavmeshGridDivisor != wd.NavmeshGridDivisor || d.MaxDistanceX != wd.MaxDistanceX || d.MaxDistanceY != wd.MaxDistanceY
                         || !d.NavmeshGrid.Span.SequenceEqual(wd.NavmeshGrid.Span) || d.Min != wd.Min || d.Max != wd.Max)
                    why.Add("its geometry is unchanged, so its grid and box must be its winner's, and they are not");
                for (int i = 0; i < d.Triangles.Count && i < oldIndex.Length; i++)
                {
                    var (now, was) = (d.Triangles[i], oldIndex[i] >= 0 ? wd.Triangles[oldIndex[i]] : null);
                    bool coverOk = was is null ? !now.IsCover && now.CoverFlags == 0 : now.IsCover == was.IsCover && now.CoverFlags == was.CoverFlags;
                    if (!coverOk) { why.Add($"triangle {i}'s cover fields are not {(was is null ? "clear on an added triangle" : $"its winner's (triangle {oldIndex[i]})")}"); break; }
                }
            }
            Console.WriteLine(why.Count == 0 ? $"ok   {key}: vertices, triangles, links, doors, cover, box and grid as planned{(rebuilt ? " (grid rebuilt)" : " (links only; grid kept as its winner's)")}" : $"FAIL {key}: {string.Join("; ", why)}");
            if (why.Count > 0) failed++;
        }
        var extra = raw.Keys.Where(k => !expect.ContainsKey(k)).ToList();
        if (extra.Count > 0) { Console.WriteLine($"FAIL the patch carries navmeshes the plan does not: {string.Join(", ", extra)}"); failed++; }
        var (recs, _, bad) = NavRepairReadBack.Carried(patch, order);
        foreach (var c in recs) Console.WriteLine($"{((bool)c!["holds"]! ? "ok  " : "FAIL")} carried {c["type"]} {c["record"]} equals its winner {c["winner"]}" + ((bool)c["holds"]! ? "" : ": " + string.Join("; ", c["differences"]!.AsArray().Select(x => (string)x!))));
        failed += bad;
        Console.WriteLine(failed == 0 ? $"patch written and read back: {patchPath}" : $"navedit write: {failed} check(s) failed on {patchPath}; do not enable it.");
        return failed == 0 ? 0 : 1;
    }
}
