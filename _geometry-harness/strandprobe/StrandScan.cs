using System.Numerics;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using SkyWright.Engine;

// THE STRANDED SCAN: one walk over the order, then the rule (SkyWright.Engine.StrandedRule) per placed reference.
//   the walk      every plugin in load order: base records (type, editor id, model, bounds), collision layer names, every
//                 cell under the last-sighting rule (LastSighting) with the plugin that last placed each reference, the
//                 load-time enable facts, and every interior cell's navmesh sightings with their plugin;
//   the views     a room's WINNING navmesh (every plugin) and, per placing plugin, its OWN view (that plugin and its declared
//                 master closure, DetectorModel.MasterClosure): the navmesh its author built against;
//   the cut       before any distance: the population (StrandedRule.OutOfPopulation), the same view (no navmesh differs:
//                 no row), the author's view (every plugin whose navmesh differs has the object's plugin among its masters);
//   the distances SubjectGeometry's shape, NavmeshReach to each view's triangles (the band for a trigger);
//   the clearance the room's solid objects of other mods: a model's reach region with particles set aside (MeshReach), a
//                 primitive by PrimitiveSolidity (a model-less activator's by MeshReach.OfPrimitiveRule); an object whose
//                 body could not be settled is a bound (its placed object bounds, or its primitive box).
// A navmesh is read in the cell it is filed in; a navmesh moved to another interior cell is not followed (none is known).

sealed record ScanInput(IReadOnlyList<string> Paths, Func<string, string> Family, Func<string, byte[]?> ReadMesh,
                        IReadOnlyList<PrimitiveMeasurement>? Table, bool Mirror, IReadOnlySet<string>? Cells, bool KeepAll);

/// <summary>One judged reference (a row, or measured with no row): its cell, plugins, distances, verdict.</summary>
sealed record ScanRow(FormKey Ref, FormKey Cell, string? CellEdid, string Placer, string? NavPlugin, string? BaseType, string? BaseEdid,
                      string Geometry, bool Trigger, double? Winning, double? Own, double? Clearance, string? Nearest, StrandedVerdict Verdict);

sealed class ScanResult
{
    public readonly List<ScanRow> Rows = new();
    public readonly SortedDictionary<string, long> Coverage = new(StringComparer.Ordinal);
    public readonly Dictionary<FormKey, (StrandedState State, string Reason)> All = new();
    public long Population, LeftByCut, Measured;
    public void Count(string why) => Coverage[why] = Coverage.GetValueOrDefault(why) + 1;
}

static class StrandScan
{
    sealed record BaseInfo(string Type, string? Edid, string? Model, Vec3? ObMin, Vec3? ObMax);
    sealed record NavObs(int Plugin, FormKey Cell, IReadOnlyList<NavmeshGeometry> Navs, IReadOnlyList<DeletedNavmesh> Dels);
    sealed record View(Dictionary<string, int> Map, Dictionary<string, NavmeshGeometry> Geo, Dictionary<string, int> RemovedBy, List<NavTriangle> Tris);
    sealed record Blocker(string Family, Aabb? Body, Aabb? Bound, string Name);

    public static ScanResult Run(ScanInput input)
    {
        var res = new ScanResult();
        var names = input.Paths.Select(p => Path.GetFileName(p)).ToList();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i++) index[names[i]] = i;
        var direct = new List<IReadOnlySet<int>>();
        var bases = new Dictionary<FormKey, BaseInfo>();
        var layers = new Dictionary<uint, string>();
        var enable = new EnableFactsIndex();
        var refs = new LastSightingSet();
        var winner = new Dictionary<FormKey, int>();
        var cellInfo = new Dictionary<FormKey, (bool Interior, string? Edid)>();
        var navObs = new Dictionary<FormKey, List<NavObs>>();

        for (int i = 0; i < input.Paths.Count; i++)
        {
            var mod = PluginOpen.Open(input.Paths[i], "strandprobe's walk")!;
            int pi = i;
            PluginOpen.Guard(input.Paths[i], "strandprobe's walk", () =>
            {
                using (mod as IDisposable)
                {
                    direct.Add(mod.ModHeader.MasterReferences.Select(m => index.TryGetValue(m.Master.FileName.String, out var mi) ? mi : -1)
                                  .Where(mi => mi >= 0 && mi < pi).ToHashSet());
                    foreach (var rec in mod.EnumerateMajorRecords())
                    {
                        if (rec is IPlacedGetter pg) { if (pg.IsDeleted) enable.NoteDeleted(pg.FormKey); continue; }
                        string? model = rec is IModeledGetter m && m.Model?.File is { } f && !string.IsNullOrWhiteSpace(f.GivenPath) ? f.GivenPath : null;
                        Vec3? lo = null, hi = null;
                        if (rec is IObjectBoundedGetter ob && ob.ObjectBounds is { } b)
                        { lo = new Vec3(b.First.X, b.First.Y, b.First.Z); hi = new Vec3(b.Second.X, b.Second.Y, b.Second.Z); }
                        bases[rec.FormKey] = new BaseInfo(rec.GetType().Name.Replace("BinaryOverlay", ""), rec.EditorID, model, lo, hi);
                        if (rec is ICollisionLayerGetter cl) layers[cl.Index] = cl.EditorID ?? cl.FormKey.ToString();
                    }
                    foreach (var cp in LastSighting.Cells(mod))
                    {
                        foreach (var r in cp.Refs) enable.Note(r);
                        refs.Observe(cp, (_, r) => { winner[r.FormKey] = pi; return true; });
                        cellInfo[cp.Cell] = (cp.Interior, cp.CellEditorId ?? (cellInfo.TryGetValue(cp.Cell, out var was) ? was.Edid : null));
                        if (cp.Interior && (cp.Navmeshes.Count > 0 || cp.DeletedNavmeshes is { Count: > 0 }))
                        {
                            if (!navObs.TryGetValue(cp.Cell, out var list)) navObs[cp.Cell] = list = new();
                            list.Add(new NavObs(pi, cp.Cell, cp.Navmeshes, cp.DeletedNavmeshes ?? (IReadOnlyList<DeletedNavmesh>)Array.Empty<DeletedNavmesh>()));
                        }
                    }
                }
            });
        }
        var closure = DetectorModel.MasterClosure(direct);
        var pop = EnablePopulation.LoadTime(enable);
        var present = pop.Predicate!;

        var byCell = new Dictionary<FormKey, List<FormKey>>();
        foreach (var (k, c) in refs.CellOf) { if (!byCell.TryGetValue(c, out var l)) byCell[c] = l = new(); l.Add(k); }
        var views = new Dictionary<(FormKey Cell, int Placer), View>();
        var winViews = new Dictionary<FormKey, View>();
        var blockers = new Dictionary<FormKey, List<Blocker>>();

        View MakeView(FormKey cell, Func<int, bool> include)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal); var geo = new Dictionary<string, NavmeshGeometry>(StringComparer.Ordinal);
            var removed = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var o in navObs.GetValueOrDefault(cell) ?? new())
            {
                if (!include(o.Plugin)) continue;
                foreach (var n in o.Navs)
                {
                    string key = n.FormKey.ToString();
                    if (n.Deleted) { map.Remove(key); geo.Remove(key); removed[key] = o.Plugin; }
                    else { map[key] = o.Plugin; geo[key] = n; removed.Remove(key); }
                }
                foreach (var d in o.Dels) { string key = d.FormKey.ToString(); map.Remove(key); geo.Remove(key); removed[key] = o.Plugin; }
            }
            var tris = new List<NavTriangle>();
            foreach (var nv in geo.Values)
                for (int t = 0; t < nv.Triangles.Count; t++)
                {
                    var tr = nv.Triangles[t];
                    if (tr.A < 0 || tr.B < 0 || tr.C < 0 || tr.A >= nv.Vertices.Count || tr.B >= nv.Vertices.Count || tr.C >= nv.Vertices.Count) continue;
                    Vector3 V3(Vec3 v) => new(v.X, v.Y, v.Z);
                    tris.Add(new NavTriangle(V3(nv.Vertices[tr.A]), V3(nv.Vertices[tr.B]), V3(nv.Vertices[tr.C]), (ushort)tr.Flags, nv.FormKey.ToString(), t));
                }
            return new View(map, geo, removed, tris);
        }

        Aabb PlacedBox(Vec3 lo, Vec3 hi, PlacedRef r)
        {
            var v = new[] { new Vector3(lo.X, lo.Y, lo.Z), new Vector3(hi.X, lo.Y, lo.Z), new Vector3(hi.X, hi.Y, lo.Z), new Vector3(lo.X, hi.Y, lo.Z),
                            new Vector3(lo.X, lo.Y, hi.Z), new Vector3(hi.X, lo.Y, hi.Z), new Vector3(hi.X, hi.Y, hi.Z), new Vector3(lo.X, hi.Y, hi.Z) };
            var ix = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
            return SpatialOverlap.SoupBounds(SpatialOverlap.TransformSoup(new TriangleSoup(v, ix), r.Position, r.Rotation, r.Scale));
        }

        List<Blocker> BlockersOf(FormKey cell)
        {
            if (blockers.TryGetValue(cell, out var have)) return have;
            var list = new List<Blocker>();
            foreach (var k in byCell.GetValueOrDefault(cell) ?? new())
            {
                var r = refs.Refs[k];
                if (r.Deleted || !present(k) || r.Base is not { } bk || !bases.TryGetValue(bk, out var bi)) continue;
                string fam = input.Family(names[winner[k]]), name = $"{bi.Edid ?? bi.Type} ({k})";
                if (bi.Model is { } model)
                {
                    var bytes = input.ReadMesh(model);
                    var region = bytes is null ? null : MeshReach.OfModel(bytes, r.Position, r.Rotation, r.Scale, null, new ReachOptions(ParticlesAside: true));
                    if (region is { Status: "certified" or "uncertified" } && !region.Box.IsEmpty) list.Add(new Blocker(fam, region.Box, null, name));
                    else if (region is { Status: "no_geometry" }) { }
                    else list.Add(new Blocker(fam, null, bi.ObMin is { } lo && bi.ObMax is { } hi ? PlacedBox(lo, hi, r) : null, name));
                }
                else if (r.Primitive is { } prim)
                {
                    var half = new Vector3(prim.Bounds.X / 2, prim.Bounds.Y / 2, prim.Bounds.Z / 2);
                    var lo = new Vec3(-half.X, -half.Y, -half.Z); var hi = new Vec3(half.X, half.Y, half.Z);
                    var rule = MeshReach.OfPrimitiveRule(bi.Type, false, r.CollisionLayerIndex, "not settled by the reach rules");
                    var decided = rule.Status == "no_geometry" ? rule
                        : input.Table is { } table
                            ? PrimitiveSolidity.Decide(new PrimitiveFacts(bk.ToString(), bi.Type, r.CollisionLayerIndex is { } li && layers.TryGetValue(li, out var ln) ? ln : null,
                                                                          prim.Type, half, r.Position, r.Rotation), table, "no measurement")
                            : new ReachRegion("refused", "no primitive table was given", Aabb.Empty, MeshReach.PolicyVersion, Array.Empty<string>(), Array.Empty<ReachPart>());
                    var blocks = StrandedRule.PrimitiveBlocks(decided.Status);
                    if (blocks == true) list.Add(new Blocker(fam, PlacedBox(lo, hi, r), null, name));
                    else if (blocks is null) list.Add(new Blocker(fam, null, PlacedBox(lo, hi, r), name + ": " + decided.Reason));
                }
            }
            return blockers[cell] = list;
        }

        foreach (var (k, r) in refs.Refs)
        {
            var cell = refs.CellOf[k];
            var (interior, cedid) = cellInfo.GetValueOrDefault(cell);
            if (input.Cells is { } want && !(want.Contains(cell.ToString()) || (cedid is not null && want.Contains(cedid)))) continue;
            if (r.Deleted) continue;
            if (StrandedRule.OutOfPopulation(interior, present(k)) is { } outside)
            { res.Count(outside); if (input.KeepAll) res.All[k] = (StrandedState.NoRow, outside); continue; }
            res.Population++;
            int placer = winner[k];
            var own = closure[placer].Append(placer).ToHashSet();
            if (!winViews.TryGetValue(cell, out var win)) winViews[cell] = win = MakeView(cell, _ => true);
            if (!views.TryGetValue((cell, placer), out var mine)) views[(cell, placer)] = mine = MakeView(cell, own.Contains);
            if (StrandedRule.SameView(mine.Map, win.Map))
            { res.Count(StrandedRule.SameNavmesh); if (input.KeepAll) res.All[k] = (StrandedState.NoRow, StrandedRule.SameNavmesh); continue; }
            // the plugins whose sighting makes the winning navmesh differ from the own view
            var differ = new HashSet<int>();
            foreach (var (key, p) in win.Map) if (!mine.Map.TryGetValue(key, out var q) || q != p) differ.Add(p);
            foreach (var key in mine.Map.Keys) if (!win.Map.ContainsKey(key) && win.RemovedBy.TryGetValue(key, out var rp)) differ.Add(rp);
            if (input.Mirror && differ.Count > 0 && differ.All(d => StrandedRule.AuthorHad(closure[d], placer)))
            { res.Count(StrandedRule.AuthorHadIt); if (input.KeepAll) res.All[k] = (StrandedState.NoRow, StrandedRule.AuthorHadIt); continue; }
            res.LeftByCut++;

            BaseInfo? bi = r.Base is { } bk && bases.TryGetValue(bk, out var found) ? found : null;
            var shape = SubjectGeometry.Of(r, bi?.Model, input.ReadMesh);
            bool trigger = StrandedRule.IsTrigger(bi?.Type, bi?.Model is not null, r.Primitive?.Type);
            NavDistance Measure(List<NavTriangle> tris) => shape.World is { } w
                ? (trigger ? NavmeshReach.FromSoupBand(w, tris, StrandedRule.BandLo, StrandedRule.BandHi) : NavmeshReach.FromSoup(w, tris))
                : (trigger ? NavmeshReach.FromPointBand(shape.Anchor, tris, StrandedRule.BandLo, StrandedRule.BandHi) : NavmeshReach.FromPoint(shape.Anchor, tris));
            NavDistance dw, dow;
            if (shape.Unread is null) { dw = Measure(win.Tris); dow = Measure(mine.Tris); res.Measured++; }
            else dw = dow = new NavDistance("refused", "the body was not read", null, null, null, null, null);
            // the other mod: the plugin whose navmesh holds the nearest winning triangle, when it is not in the own view;
            // else the latest plugin that makes the views differ
            int? nav = dw.Navmesh is { } nm && win.Map.TryGetValue(nm, out var np) && !own.Contains(np) ? np : differ.Count > 0 ? differ.Max() : null;
            if (input.Mirror && nav is { } nv0 && StrandedRule.AuthorHad(closure[nv0], placer))
            { res.Count(StrandedRule.AuthorHadIt); if (input.KeepAll) res.All[k] = (StrandedState.NoRow, StrandedRule.AuthorHadIt); continue; }

            string fam = input.Family(names[placer]);
            var subjectBox = shape.World is { } sw ? SpatialOverlap.SoupBounds(sw) : new Aabb(r.Position, r.Position, false);
            double? nearest = null, unread = null; string? nearestName = null, unreadName = null; bool anyOther = false;
            foreach (var b in BlockersOf(cell))
            {
                if (b.Name.EndsWith($"({k})", StringComparison.Ordinal) || StrandedRule.SameMod(b.Family, fam)) continue;
                if (b.Body is { } body)
                {
                    anyOther = true;
                    double g = MeshReach.Gap(subjectBox, body);
                    if (nearest is null || g < nearest) { nearest = g; nearestName = b.Name; }
                }
                else
                {
                    double g = b.Bound is { } bound ? MeshReach.Gap(subjectBox, bound) : 0;
                    if (unread is null || g < unread) { unread = g; unreadName = b.Name + (b.Bound is null ? " (no bound)" : ""); }
                }
            }
            var facts = new StrandedFacts(trigger, dw, dow, new StrandedClearance(anyOther, nearest, nearestName, unread, unreadName), shape.Unread,
                                          bi?.Type, bi?.Edid, r.Scripts is { Count: > 0 });
            var v = StrandedRule.Decide(facts);
            if (input.KeepAll) res.All[k] = (v.State, v.Reason);
            if (v.State == StrandedState.NoRow) { res.Count(v.Reason); continue; }
            res.Rows.Add(new ScanRow(k, cell, cedid, names[placer], nav is { } n2 ? names[n2] : null, bi?.Type, bi?.Edid, shape.Geometry, trigger,
                                     dw.Distance, dow.Distance, nearest, nearestName, v));
            res.Count(v.State == StrandedState.Reported ? "reported" : "not judged: " + v.Reason.Split(':')[0]);
        }
        return res;
    }
}
