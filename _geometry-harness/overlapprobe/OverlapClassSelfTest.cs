using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using F = HarnessShared.OverlapClassFixture;

// overlapprobe --self-test, slice 4: _shared/OverlapClassFixture.cs written beside the base-game fixture, the order run
// through the probe's own passes (the shared index, the BOS layer from the real _SWAP.ini, the load-time population, the
// candidates, the actual-surface sweep and the provenance rule), and the decisions the probe's top level hands to its
// classes checked on the rows. Expectations are worked out by hand from the fixture's table.
//
// The decode: Program.cs's GeometryOf is a local function over about fifteen run-scoped values (reach rules, the swept
// motion proxy, its caches and tallies), so it cannot be called from here. The pipeline below makes the calls it makes on
// a plain model: AssetResolver over the fixture's mod folders, ResolveForPlacement, ReadPlacementSource and
// NifRenderGeometryCache.Measure, keeping a model only when it is usable for surface intersection. The probe runs on the
// same fixture exercise GeometryOf itself (byte-identical checks).
static class OverlapClassSelfTest
{
    static readonly ISet<string> Obstructing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "Static", "MoveableStatic", "Tree", "Furniture", "Door", "Container", "Activator", "Flora" };   // Program.cs's set

    sealed record Pipeline(OverlapIndex Index, EnableArms Arms, IReadOnlyList<HashSet<int>> Closure, Func<int, string> Name,
                           Func<FormKey, (TriangleSoup? Soup, char Src, string? Reason)> Geometry,
                           List<SurfaceOverlapCandidate> Candidates, CandidateSkips Skips, List<SurfaceOverlapRow> Rows);

    static Pipeline Build(F.Written w, string root)
    {
        int n = w.Order.Count;
        var idx = new OverlapIndex { Bos = new BosFormIndex() };
        var pluginKey = new ModKey[n];
        var masterIdx = new HashSet<int>[n + 1];
        var lightness = new List<(ModKey, bool)>();
        for (int p = 0; p < n; p++)
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(w.Order[p], SkyrimRelease.SkyrimSE);
            pluginKey[p] = mod.ModKey;
            lightness.Add((mod.ModKey, BosHook.IsLight(mod)));
            var names = mod.ModHeader.MasterReferences.Select(m => m.Master).ToHashSet();
            masterIdx[p] = Enumerable.Range(0, p).Where(q => names.Contains(pluginKey[q])).ToHashSet();
            idx.Ingest(p, mod);
        }
        int bosPlugin = n;
        masterIdx[bosPlugin] = BosHook.PseudoMasters(n);
        var closure = DetectorModel.MasterClosure(masterIdx);
        string Name(int p) => p == bosPlugin ? BosApplyReport.PseudoPluginName : pluginKey[p].FileName.String;

        var empty = Path.Combine(root, "ovc-empty"); Directory.CreateDirectory(empty);
        var assets = AssetResolver.Build(empty, w.ModsDir, empty, new[] { F.OcA.Name, F.Skyrim.Name }, Array.Empty<ActiveArchive>());
        idx.Bos!.SetLoadOrder(lightness);
        BosHook.Apply(idx, BosHook.Load(assets.LooseRoots, idx.Bos), bosPlugin);

        var cache = new NifRenderGeometryCache();
        var memo = new Dictionary<FormKey, (TriangleSoup?, char, string?)>();
        (TriangleSoup? Soup, char Src, string? Reason) Geometry(FormKey baseKey)
        {
            if (memo.TryGetValue(baseKey, out var hit)) return hit;
            if (!idx.BaseMesh.TryGetValue(baseKey, out var rel) || string.IsNullOrWhiteSpace(rel)) return memo[baseKey] = (null, '-', "the winning base record has no model path");
            var full = (rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel)).Replace('/', '\\');
            var res = assets.ResolveForPlacement(full);
            if (res.Sources.Count == 0) return memo[baseKey] = (null, '-', "no active provider supplies the winning model path");
            var (bytes, err) = AssetResolver.ReadPlacementSource(res.Sources[0]);
            if (bytes is null) return memo[baseKey] = (null, '-', err ?? "unreadable");
            var m = cache.Measure(bytes, includeTriangles: true);
            return memo[baseKey] = m is { UsableForSurfaceIntersection: true, Soup: { TriangleCount: > 0 } soup } ? (soup, 'm', null) : (null, '-', m.Reason ?? "no complete strict render triangles");
        }

        var arms = new EnableArms(idx);
        var pop = EnablePopulation.Of(arms.WinnerRawFacts, EnablePopulation.BaselineStateName);
        var candidates = idx.SurfaceCandidates(Geometry, Obstructing, p => BaseGamePlugins.IsCoreMaster(Name(p)), out var skips, pop.Predicate);
        candidates = candidates.Where(c =>
        {
            if (!arms.Winner(c.Key).Resolved) { skips.EnableStateUnresolved++; return false; }
            if (idx.BosSwaps.TryGetValue(c.Key, out var bos) && (bos.Random || bos.PerSession || bos.PropertiesRandom || bos.Alternatives.Count != 1))
            { skips.BosUnresolved++; return false; }
            return true;
        }).ToList();
        var sweep = OverlapIndex.SurfacePairs(candidates, closure, 1e-5, 2_000_000, TimeSpan.FromSeconds(120));
        var rows = sweep.CrossHits.Select(h => idx.JudgeSurface(h, closure, Geometry, 1e-5, 2_000_000, TimeSpan.FromSeconds(120), arms)).ToList();
        return new Pipeline(idx, arms, closure, Name, Geometry, candidates, skips, rows);
    }

    static string Pair(SurfaceOverlapRow r) => $"{r.LaterRef.Key}x{r.OtherRef.Key}";
    static string Pair(FormKey later, FormKey other) => $"{later}x{other}";
    static string Pairs(IEnumerable<SurfaceOverlapRow> rows) => string.Join(" ", rows.Select(Pair).OrderBy(x => x, StringComparer.Ordinal));
    static string Expect(params (FormKey Later, FormKey Other)[] pairs) => string.Join(" ", pairs.Select(p => Pair(p.Later, p.Other)).OrderBy(x => x, StringComparer.Ordinal));

    public static void Run(ProbeSelfTest t)
    {
        var w = F.Write(t.Root);
        t.Check("PD4-O-F0 the fixture writes the eight plugins, the box, the _SWAP.ini and the base emit",
                w.Order.Select(Path.GetFileName).SequenceEqual(F.Order) && w.Order.All(File.Exists)
                && File.Exists(Path.Combine(w.ModsDir, F.Skyrim.Name, "meshes", F.MeshRel)) && File.Exists(Path.Combine(w.ModsDir, F.OcA.Name, F.SwapIni))
                && ConflictIo.Read(w.BaseEmit).Conflicts.Count == 10,
                string.Join(", ", w.Order.Select(Path.GetFileName)));

        var pl = Build(w, t.Root);
        var g = pl.Geometry(F.Box);
        t.Check("PD4-O-G  the box decodes to 12 strict render triangles; OcBare has no model",
                g.Soup is { TriangleCount: 12 } && pl.Geometry(F.Bare).Soup is null, $"box {g.Soup?.TriangleCount}, bare {pl.Geometry(F.Bare).Reason}");
        var s = pl.Skips;
        t.Check("PD4-O-R1 candidates: 11; skipped deleted 1, not present 1, base game 1, no geometry 1, unresolved chain 1, two BOS outcomes 1",
                pl.Candidates.Count == 11 && s.Deleted == 1 && s.Disabled == 1 && s.ExcludedResponsible == 1 && s.NoGeometry == 1
                && s.EnableStateUnresolved == 1 && s.BosUnresolved == 1,
                $"{pl.Candidates.Count}; {s.Deleted}/{s.Disabled}/{s.ExcludedResponsible}/{s.NoGeometry}/{s.EnableStateUnresolved}/{s.BosUnresolved}");
        t.Check("PD4-O-R2 five intersections judged: B1xA1, E1xD1, S1xA2, XxY, ZxW (later x other)",
                Pairs(pl.Rows) == Expect((F.B1, F.A1), (F.E1, F.D1), (F.S1, F.A2), (F.X, F.Y), (F.Z, F.W)), Pairs(pl.Rows));

        // OV1 and OV4: overlapprobe/OverlapClass.cs, with the probe's population split.
        OverlapPopulation PopOf(SurfaceOverlapHit h) => DetectorModel.ClassifyOverlapPopulation(
            BaseGamePlugins.IsBaseGame(pl.Name(h.A.Win.Plugin)), BaseGamePlugins.IsBaseGame(pl.Name(h.B.Win.Plugin)),
            BaseGamePlugins.IsUssep(pl.Name(h.A.Win.Plugin)), BaseGamePlugins.IsUssep(pl.Name(h.B.Win.Plugin)));
        var split = OverlapClass.Of(pl.Rows, PopOf);
        t.Check("PD4-O-U  provenance unknown is exactly ZxW (the previous arm's base has no model); four rows assessed",
                Pairs(split.ProvenanceUnknown) == Expect((F.Z, F.W)) && split.Provenance.Count == 4, $"unknown {Pairs(split.ProvenanceUnknown)}; assessed {split.Provenance.Count}");
        t.Check("PD4-O-C  the class is exactly B1xA1 (Introduced + Blind, mod on mod)", Pairs(split.Class) == Expect((F.B1, F.A1)), Pairs(split.Class));
        t.Check("PD4-O-C2 the Bethesda floor is exactly E1xD1, withheld from the class", Pairs(split.Floor) == Expect((F.E1, F.D1)), Pairs(split.Floor));
        t.Check("PD4-O-C3 introduced + seen is exactly S1xA2 (OcSeen masters OcA)", Pairs(split.Knowing) == Expect((F.S1, F.A2)), Pairs(split.Knowing));
        t.Check("PD4-O-C4 inherited is exactly XxY (X already crossed Y at its OcA place)", Pairs(split.Inherited) == Expect((F.X, F.Y)), Pairs(split.Inherited));
    }
}
