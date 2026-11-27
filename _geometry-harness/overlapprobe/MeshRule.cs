using System.Security.Cryptography;
using System.Text;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

// THE FOLDER RULE (mesh-responsibility/GATE-A.md, GATE-B.md): a crossing that exists only because of the mesh a mod folder
// provides is that folder's. Which folder is responsible for a shape is _shared/MeshResponsibility.cs; the decision is
// DetectorModel.MeshProvenance. This file holds the run's half: which base-game references come back in, which pairs the
// rule forms, the mesh counterfactual of one intersecting pair, and the coverage line.
//
// The folders credited: with --population, the population's folders; otherwise --mesh-folder (repeatable; "*" = every
// folder of the view); with neither, none, and the rule changes nothing.
// Readmission: a base-game reference whose responsible plugin is a core master is set aside as today unless its shape is a
// credited folder's (a target) or its conservative box (its base's decoded render bounds, placed) comes within the surface
// tolerance of a target's box; with a population, only targets in it. Those references go through the shared candidate gates
// (OverlapIndex.SurfaceCandidates) twice: once with the box standing in for the mesh, once with the mesh.
sealed class MeshRule
{
    const float Grid = 2048f;
    readonly OverlapIndex _idx;
    readonly AssetResolver _assets;
    readonly PopulationScope? _population;
    readonly IReadOnlyList<HashSet<int>> _closure;
    readonly Func<int, bool> _isCore;
    readonly double _tol; readonly long _maxTri; readonly TimeSpan _timeout;
    readonly PairResultCache? _results;
    readonly Dictionary<string, int> _priority = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<int>> _folderPlugins = new(StringComparer.OrdinalIgnoreCase);
    readonly NifRenderGeometryCache _cache = new();
    readonly Dictionary<string, (TriangleSoup? Soup, string? Reason)> _fallbackSoup = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (TriangleSoup? Soup, string? Reason)> _gameSoup = new(StringComparer.OrdinalIgnoreCase);
    public MeshResponsibility Owners { get; }
    public bool Active { get; }
    public string Scope { get; }
    public string IdentitySuffix { get; }
    public HashSet<FormKey> Readmitted { get; } = new();
    public HashSet<FormKey> Targets { get; } = new();
    public long Judged, CrossesWithout, Unknown, Joint, SeenRows, Partners, SeenNotTested, TouchingNotCredited, UnknownJudgedByPluginRule;
    public double ReadmitSeconds, JudgeSeconds;

    public MeshRule(OverlapIndex idx, AssetResolver assets, PopulationScope? population, IReadOnlyList<string> meshFolders,
                    ResolvedConfiguration cfg, IReadOnlyList<HashSet<int>> closure, Func<int, bool> isCore,
                    double tolerance, long maxTrianglePairs, double pairSeconds, PairResultCache? results)
    {
        _idx = idx; _assets = assets; _population = population; _closure = closure; _isCore = isCore;
        _tol = tolerance; _maxTri = maxTrianglePairs; _timeout = TimeSpan.FromSeconds(pairSeconds); _results = results;
        bool every = population is null && meshFolders.Contains("*");
        IEnumerable<string>? credited = population is not null ? population.Mods : every ? null : meshFolders;
        Owners = new MeshResponsibility(assets, credited);
        Active = every || (credited?.Any() ?? false);
        Scope = population is not null ? $"the population's folders ({Names(population.Mods)})"
              : every ? "every folder of the view (--mesh-folder *)"
              : meshFolders.Count > 0 ? $"the folders --mesh-folder names ({Names(meshFolders)})"
              : "no folder: neither --population nor --mesh-folder names one, so no crossing is credited to a mesh";
        IdentitySuffix = population is not null || !Active ? "" : PopulationRules.MeshIdentity("", meshFolders);
        _priority["overwrite"] = -1;
        for (int i = 0; i < cfg.EnabledMods.Count; i++) _priority.TryAdd(cfg.EnabledMods[i], i);
        for (int i = 0; i < cfg.Plugins.Count; i++)
        {
            if (!_folderPlugins.TryGetValue(cfg.Plugins[i].Provider, out var l)) _folderPlugins[cfg.Plugins[i].Provider] = l = new();
            l.Add(i);
        }
    }

    static string Names(IEnumerable<string> n) => string.Join(", ", n.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    string? ModelOf(FormKey b) => MeshResponsibility.ModelPathOf(_idx, b);
    bool InPop(FormKey k, Snap s) => _population is null || _population.KeyInScope(k, s.Cell);
    public MeshOwner? CreditedOwner(FormKey b) => ModelOf(b) is { } m && Owners.Of(m) is { Shape: MeshShape.Credited } o ? o : null;

    /// <summary>The base-game references that come back in: targets, and partners whose box comes within the tolerance of one.</summary>
    public HashSet<FormKey> Readmit(IReadOnlyList<SurfaceOverlapCandidate> candidates, ISet<string> obstructing, Func<FormKey, bool>? enabledOf)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var want = new HashSet<FormKey>();
        if (!Active) return want;
        var tNon = candidates.Where(c => CreditedOwner(c.Base) is not null && InPop(c.Key, c.Win)).ToList();
        var spaces = tNon.Select(c => c.Win.Space).ToHashSet();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, snaps) in _idx.History.Refs)
        {
            var s = _idx.WinningGeometryPose(key) ?? snaps[^1];
            if (!_isCore(s.Plugin) || !_idx.RefBase.TryGetValue(key, out var b) || ModelOf(b) is not { } m) continue;
            if (Owners.Of(m).Shape == MeshShape.Credited && InPop(key, s)) spaces.Add(s.Space);
        }
        if (spaces.Count == 0) { ReadmitSeconds = clock.Elapsed.TotalSeconds; return want; }
        foreach (var (key, snaps) in _idx.History.Refs)
        {
            var s = _idx.WinningGeometryPose(key) ?? snaps[^1];
            if (_isCore(s.Plugin) && spaces.Contains(s.Space) && _idx.RefBase.TryGetValue(key, out var b) && ModelOf(b) is { } m) paths.Add(m);
        }
        var bounds = Bounds(paths);
        var boxes = new Dictionary<string, TriangleSoup>(StringComparer.OrdinalIgnoreCase);
        (TriangleSoup? Soup, char Src, string? Reason) Proxy(FormKey b)
        {
            if (ModelOf(b) is not { } m || !bounds.TryGetValue(m, out var bb) || bb is null) return (null, '-', "no decoded render bounds");
            if (!boxes.TryGetValue(m, out var soup)) boxes[m] = soup = ContactIndex.BoxSoup(bb.Value);
            return (soup, 'b', null);
        }
        var core = _idx.SurfaceCandidates(Proxy, obstructing, p => !_isCore(p), out _, enabledOf, (k, s) => spaces.Contains(s.Space));
        var tCore = core.Where(c => CreditedOwner(c.Base) is not null && InPop(c.Key, c.Win)).ToList();
        // Sight before cost (Gate C): a reference comes back in only when some pair it could form might be a Blind folder row.
        // The partners are the box-near references (a superset of the pairs the sweep can find), and a pair is kept when any
        // credited folder on it would be Blind for its two placements, so a reference left out cannot make a Blind row.
        Dictionary<(FormKey, int, int), List<SurfaceOverlapCandidate>> Hash(IEnumerable<SurfaceOverlapCandidate> list)
        {
            var h = new Dictionary<(FormKey, int, int), List<SurfaceOverlapCandidate>>();
            foreach (var c in list)
                foreach (var (x, y) in Cells(c.WorldBounds))
                {
                    if (!h.TryGetValue((c.Win.Space, x, y), out var l)) h[(c.Win.Space, x, y)] = l = new();
                    l.Add(c);
                }
            return h;
        }
        bool PairCanBeBlind(SurfaceOverlapCandidate a, SurfaceOverlapCandidate b)
            => (CreditedOwner(a.Base) is { } oa && InPop(a.Key, a.Win) && BlindPossible(oa.Folder!, a.Win.Plugin, b.Win.Plugin) && PartnerReaches(a, oa, b))
            || (CreditedOwner(b.Base) is { } ob && InPop(b.Key, b.Win) && BlindPossible(ob.Folder!, a.Win.Plugin, b.Win.Plugin) && PartnerReaches(b, ob, a));
        var coreHash = Hash(core);
        var candHash = Hash(candidates.Where(c => spaces.Contains(c.Win.Space)));
        IEnumerable<SurfaceOverlapCandidate> NearIn(Dictionary<(FormKey, int, int), List<SurfaceOverlapCandidate>> h, SurfaceOverlapCandidate t)
        {
            var seen = new HashSet<FormKey>();
            foreach (var (x, y) in Cells(t.WorldBounds))
                if (h.TryGetValue((t.Win.Space, x, y), out var l))
                    foreach (var c in l)
                        if (c.Key != t.Key && seen.Add(c.Key) && Near(t.WorldBounds, c.WorldBounds, _tol)) yield return c;
        }
        var tCoreKeys = tCore.Select(c => c.Key).ToHashSet();
        foreach (var t in tNon.Concat(tCore))
        {
            foreach (var c in NearIn(coreHash, t))
            {
                if (PairCanBeBlind(t, c)) { want.Add(c.Key); if (tCoreKeys.Contains(t.Key)) want.Add(t.Key); }
                else if (!want.Contains(c.Key)) SeenNotReadmitted.Add(c.Key);
            }
            if (tCoreKeys.Contains(t.Key))
                foreach (var z in NearIn(candHash, t))
                    if (PairCanBeBlind(t, z)) want.Add(t.Key);
        }
        SeenNotReadmitted.ExceptWith(want);
        foreach (var c in tCore) if (!want.Contains(c.Key)) SeenNotReadmitted.Add(c.Key);
        Partners = want.Count(k => !tCoreKeys.Contains(k));
        ReadmitSeconds = clock.Elapsed.TotalSeconds;
        return want;
    }

    /// <summary>Seen folder pairs by (folder, the two placements' plugin indices, what happened), for the console: who sight
    /// rests on, printed so it can be checked against the plugins' own headers.</summary>
    public Dictionary<(string Folder, int Low, int High, string What), long> SeenTally { get; } = new();
    static void Tally(Dictionary<(string, int, int, string), long> t, string folder, int a, int b, string what)
    {
        var k = (folder, Math.Min(a, b), Math.Max(a, b), what);
        t[k] = t.GetValueOrDefault(k) + 1;
    }

    /// <summary>The plugin indices a folder ships (cfg.Plugins' provider), for the console.</summary>
    public IReadOnlyList<int> PluginsOf(string folder) => _folderPlugins.TryGetValue(folder, out var l) ? l : Array.Empty<int>();

    /// <summary>Base-game references near a target that stayed set aside because every pair they could form is Seen.</summary>
    public HashSet<FormKey> SeenNotReadmitted { get; } = new();

    /// <summary>Whether a row the folder's mesh made for two placements could be Blind: neither sight (a) nor (b) holds.</summary>
    public bool BlindPossible(string folder, int pa, int pb)
    {
        var f = Facts(folder, pa, pb, false);
        return !(f.FolderPluginInLaterClosure || f.FolderPluginMastersBoth);
    }

    IEnumerable<(int, int)> Cells(Aabb b)
    {
        int x0 = (int)MathF.Floor((float)((b.Min.X - _tol) / Grid)), x1 = (int)MathF.Floor((float)((b.Max.X + _tol) / Grid));
        int y0 = (int)MathF.Floor((float)((b.Min.Y - _tol) / Grid)), y1 = (int)MathF.Floor((float)((b.Max.Y + _tol) / Grid));
        for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) yield return (x, y);
    }

    static bool Near(Aabb a, Aabb b, double t) =>
        !((double)b.Min.X - a.Max.X > t || (double)a.Min.X - b.Max.X > t || (double)b.Min.Y - a.Max.Y > t
          || (double)a.Min.Y - b.Max.Y > t || (double)b.Min.Z - a.Max.Z > t || (double)a.Min.Z - b.Max.Z > t);

    /// <summary>Decoded render bounds per model path, each archive's entries read in one table walk.</summary>
    Dictionary<string, Aabb?> Bounds(IEnumerable<string> paths)
    {
        var bytesByPath = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        var byArchive = new Dictionary<string, List<(string Path, string Entry)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            var res = _assets.ResolveForPlacement(p);
            if (res.Sources.Count == 0) { bytesByPath[p] = null; continue; }
            var w = res.Sources[0];
            if (w.Kind == AssetKind.Bsa && w.ArchivePath is { } ap)
            {
                if (!byArchive.TryGetValue(ap, out var l)) byArchive[ap] = l = new();
                l.Add((p, w.EntryPath));
            }
            else bytesByPath[p] = AssetResolver.ReadPlacementSource(w).Bytes;
        }
        foreach (var (archive, wanted) in byArchive)
        {
            Dictionary<string, byte[]> got;
            try { got = AssetResolver.TryReadArchiveEntries(archive, wanted.Select(x => x.Entry).ToList()); }
            catch (Exception) { got = new(); }
            foreach (var (p, e) in wanted) bytesByPath[p] = got.TryGetValue(e, out var b) ? b : null;
        }
        var bounds = new Dictionary<string, Aabb?>(StringComparer.OrdinalIgnoreCase);
        var boundsCache = new NifRenderGeometryCache();
        foreach (var (p, b) in bytesByPath)
        {
            if (b is null) { bounds[p] = null; continue; }
            var o = boundsCache.Measure(b, includeTriangles: false);
            bounds[p] = o.Complete && o.ModelBounds is { IsEmpty: false } mb ? mb : null;
        }
        return bounds;
    }

    /// <summary>Record the targets among the final candidates (readmitted ones included).</summary>
    public void SetTargets(IEnumerable<SurfaceOverlapCandidate> candidates)
    {
        foreach (var c in candidates) if (CreditedOwner(c.Base) is not null && InPop(c.Key, c.Win)) Targets.Add(c.Key);
    }

    /// <summary>A pair with a readmitted side is formed only when one side is a target.</summary>
    public bool PairAllowed(SurfaceOverlapCandidate a, SurfaceOverlapCandidate b)
        => !(Readmitted.Contains(a.Key) || Readmitted.Contains(b.Key)) || Targets.Contains(a.Key) || Targets.Contains(b.Key);

    /// <summary>A same-responsible intersection the rule needs to see: one side's shape is a credited folder's.</summary>
    public bool HasCreditedSide(SurfaceOverlapCandidate a, SurfaceOverlapCandidate b)
        => Active && (CreditedOwner(a.Base) is not null || CreditedOwner(b.Base) is not null);

    (TriangleSoup? Soup, string? Reason) SoupOf(PlacementSource? s, Dictionary<string, (TriangleSoup?, string?)> memo, string key)
    {
        if (memo.TryGetValue(key, out var hit)) return hit;
        if (s is null) return memo[key] = (null, "no copy below the folder's");
        var (bytes, err) = AssetResolver.ReadPlacementSource(s);
        if (bytes is null) return memo[key] = (null, err ?? "the copy could not be read");
        var o = _cache.Measure(bytes, includeTriangles: true);
        return memo[key] = o.UsableForSurfaceIntersection ? (o.Soup, null) : (null, o.Reason ?? "no complete strict render triangles in the copy below the folder's");
    }

    /// <summary>The game's own copy of a path (the first source whose layer is Data), else the bottom provider.</summary>
    PlacementSource? GameSource(string path)
    {
        var src = _assets.ResolveForPlacement(path).Sources;
        return src.FirstOrDefault(x => MeshResponsibility.IsGame(MeshResponsibility.FolderOf(x))) ?? src.LastOrDefault();
    }

    public enum Outcome { NotJudged, Folder, Persists, Unknown, SeenSkipped, Touching }

    // ---- the changed-region bound (Gate C) ------------------------------------------------------------------------------
    // A pair whose relation is a SURFACE relation (crossing, coplanar overlap, touching: MeshIntersection.QuerySurfaces, decided
    // by triangle pairs) keeps that relation when each swapped side's changed triangles (those its copy has and the other copy
    // lacks, both ways) stay clear of the other side's box: every triangle pair that made the relation is in both copies. A
    // relation decided with separated surfaces (containment: an inside test over the whole closed shell) never takes the
    // shortcut, nor does a model whose triangles could not be read, nor a run with --motion swept (its soup is a proxy).
    // Triangle identity: the three vertices' float bit patterns in model space, sorted (winding-insensitive); a multiset.
    public bool NoShortcut { get; init; }
    /// <summary>The changed region's box is grown by this much (world units) before it is tested: the box is placed in float
    /// world coordinates while the surface query works in a pair frame, so a seam the query calls touching can read a hair apart
    /// here. Any doubt counts as changed.</summary>
    public const double RegionMargin = 1.0;
    public long RegionShortcuts;
    readonly Dictionary<string, Aabb?> _region = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The model-space box of the triangles one copy has and the other lacks; Aabb.Empty when none; null when either
    /// copy cannot be read (any doubt counts as changed).</summary>
    Aabb? ChangedRegion(MeshOwner o, PlacementSource? against)
    {
        string key = o.Path + "|" + (against is null ? "-" : against.Kind + ":" + (against.LooseFilePath ?? against.ArchivePath + "#" + against.EntryPath));
        if (_region.TryGetValue(key, out var hit)) return hit;
        TriangleSoup? Soup(PlacementSource? src)
        {
            if (src is null) return null;
            var (bytes, _) = AssetResolver.ReadPlacementSource(src);
            if (bytes is null) return null;
            var m = _cache.Measure(bytes, includeTriangles: true);
            return m.UsableForSurfaceIntersection ? m.Soup : null;
        }
        var w = Soup(o.Winner); var f = Soup(against);
        if (w is null || f is null) return _region[key] = null;
        static Dictionary<((int, int, int), (int, int, int), (int, int, int)), int> Tris(TriangleSoup t)
        {
            var d = new Dictionary<((int, int, int), (int, int, int), (int, int, int)), int>();
            for (int i = 0; i + 2 < t.Indices.Length; i += 3)
            {
                var v = new[] { t.Indices[i], t.Indices[i + 1], t.Indices[i + 2] }
                    .Select(k => (BitConverter.SingleToInt32Bits(t.Vertices[k].X), BitConverter.SingleToInt32Bits(t.Vertices[k].Y), BitConverter.SingleToInt32Bits(t.Vertices[k].Z)))
                    .Order().ToArray();
                var tk = (v[0], v[1], v[2]);
                d[tk] = d.GetValueOrDefault(tk) + 1;
            }
            return d;
        }
        var tw = Tris(w); var tf = Tris(f);
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        bool any = false;
        void Grow(((int, int, int), (int, int, int), (int, int, int)) t)
        {
            any = true;
            foreach (var (x, y, z) in new[] { t.Item1, t.Item2, t.Item3 })
            {
                float fx = BitConverter.Int32BitsToSingle(x), fy = BitConverter.Int32BitsToSingle(y), fz = BitConverter.Int32BitsToSingle(z);
                minX = Math.Min(minX, fx); minY = Math.Min(minY, fy); minZ = Math.Min(minZ, fz);
                maxX = Math.Max(maxX, fx); maxY = Math.Max(maxY, fy); maxZ = Math.Max(maxZ, fz);
            }
        }
        foreach (var (t, n) in tw) if (tf.GetValueOrDefault(t) != n) Grow(t);
        foreach (var (t, n) in tf) if (tw.GetValueOrDefault(t) != n) Grow(t);
        if (!any) return _region[key] = Aabb.Empty;
        if (!float.IsFinite(minX + minY + minZ + maxX + maxY + maxZ)) return _region[key] = null;
        return _region[key] = new Aabb(new Vec3(minX, minY, minZ), new Vec3(maxX, maxY, maxZ), false);
    }

    /// <summary>The pair keeps its surface relation with the swapped sides' other copies (see the section comment).</summary>
    bool Unchanged(SurfaceOverlapHit h, List<(SurfaceOverlapCandidate Ref, MeshOwner Owner, PlacementSource? Against)> swap)
    {
        if (NoShortcut || h.Query.SurfaceRelation is not ("crossing" or "coplanar_overlap" or "touching")) return false;
        foreach (var (r, o, against) in swap)
        {
            if (ChangedRegion(o, against) is not { } region) return false;
            if (region.IsEmpty) continue;
            var other = r.Key == h.A.Key ? h.B : h.A;
            var world = SpatialOverlap.SoupBounds(SpatialOverlap.TransformSoup(ContactIndex.BoxSoup(region), r.Win.Pos, r.Win.Rot, r.Win.Scale));
            if (Near(world, other.WorldBounds, Math.Max(_tol, RegionMargin))) return false;
        }
        return true;
    }

    /// <summary>For readmission: a partner can only take part in a folder row when its box comes within the tolerance of the
    /// target's changed region (a surface relation), or the two boxes nest (containment, decided over the whole shell).</summary>
    bool PartnerReaches(SurfaceOverlapCandidate t, MeshOwner o, SurfaceOverlapCandidate c)
    {
        if (NoShortcut) return true;
        static bool Inside(Aabb a, Aabb b) => a.Min.X >= b.Min.X && a.Min.Y >= b.Min.Y && a.Min.Z >= b.Min.Z && a.Max.X <= b.Max.X && a.Max.Y <= b.Max.Y && a.Max.Z <= b.Max.Z;
        if (Inside(t.WorldBounds, c.WorldBounds) || Inside(c.WorldBounds, t.WorldBounds)) return true;
        if (ChangedRegion(o, o.Fallback) is not { } region) return true;
        if (region.IsEmpty) return false;
        var world = SpatialOverlap.SoupBounds(SpatialOverlap.TransformSoup(ContactIndex.BoxSoup(region), t.Win.Pos, t.Win.Rot, t.Win.Scale));
        if (Near(world, c.WorldBounds, Math.Max(_tol, RegionMargin))) return true;
        // the joint swap reads the game's copy: the partner reaches when it reaches that region too
        var game = GameSource(o.Path);
        if (game is null || ReferenceEquals(game, o.Fallback)) return false;
        if (ChangedRegion(o, game) is not { } g) return true;
        return !g.IsEmpty && Near(SpatialOverlap.SoupBounds(SpatialOverlap.TransformSoup(ContactIndex.BoxSoup(g), t.Win.Pos, t.Win.Rot, t.Win.Scale)), c.WorldBounds, Math.Max(_tol, RegionMargin));
    }

    /// <summary>One intersecting pair under the folder rule.</summary>
    public sealed record Verdict(SurfaceOverlapHit Hit, Outcome Kind, string? Folder, IReadOnlyList<string> AlsoFolders, bool JointSwap,
                                 SurfaceOverlapCandidate Necessary, SurfaceOverlapCandidate Other, IReadOnlyList<(SurfaceOverlapCandidate Ref, MeshOwner Owner)> Swapped,
                                 SurfaceQueryOutcome? Counterfactual, string? Reason, OverlapOrigin? Origin, OverlapSight Sight, string Why,
                                 DetectorModel.MeshFacts Facts);

    int Priority(string folder) => _priority.TryGetValue(folder, out var p) ? p : int.MaxValue;

    public Verdict Judge(SurfaceOverlapHit h)
    {
        var oa = CreditedOwner(h.A.Base); var ob = CreditedOwner(h.B.Base);
        if (!Active || (oa is null && ob is null))
            return new(h, Outcome.NotJudged, null, Array.Empty<string>(), false, h.A, h.B, Array.Empty<(SurfaceOverlapCandidate, MeshOwner)>(), null, null, null, OverlapSight.Blind, "", default);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var sides = new List<(SurfaceOverlapCandidate Ref, MeshOwner Owner)>();
        if (oa is not null) sides.Add((h.A, oa));
        if (ob is not null) sides.Add((h.B, ob));
        var folders = sides.Select(s => s.Owner.Folder!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(Priority).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        // Sight before cost (Gate C): a pair of one plugin, or of two base-game placements, ends as no row whether or not the
        // folder made it when every folder on it would be Seen; it is counted, not tested.
        // Touching is never a folder's (decision 2026-11-19, the probes follow what happens in game): surfaces that only touch
        // show nothing to a player, and kit seams touch by design. Such a pair gets no counterfactual and no folder row; a
        // cross-plugin pair goes on to the plugin rule as before.
        if (h.Query.SurfaceRelation == "touching")
        {
            TouchingNotCredited++;
            return new(h, Outcome.Touching, null, folders, false, h.A, h.B, sides, null, null, null, OverlapSight.Blind, "", default);
        }
        bool floorPair = h.A.Win.Plugin == h.B.Win.Plugin || (_isCore(h.A.Win.Plugin) && _isCore(h.B.Win.Plugin));
        if (floorPair && folders.All(f => !BlindPossible(f, h.A.Win.Plugin, h.B.Win.Plugin)))
        {
            SeenNotTested++;
            foreach (var f in folders) Tally(SeenTally, f, h.A.Win.Plugin, h.B.Win.Plugin, "not tested");
            return new(h, Outcome.SeenSkipped, null, folders, false, h.A, h.B, sides, null, null, null, OverlapSight.Seen, "", default);
        }
        Judged++;
        SurfaceQueryOutcome? cf = null; string? credited = null; bool joint = false; string? unknown = null;
        foreach (var f in folders)
        {
            var swap = sides.Where(s => string.Equals(s.Owner.Folder, f, StringComparison.OrdinalIgnoreCase)).ToList();
            if (Unchanged(h, swap.Select(s => (s.Ref, s.Owner, s.Owner.Fallback)).ToList())) { RegionShortcuts++; continue; }
            var (q, why) = Query(h, swap.Select(s => (s.Ref.Key, SoupOf(s.Owner.Fallback, _fallbackSoup, s.Owner.Path))).ToList());
            if (q is null || q.Unknown) { unknown = why ?? q?.Reason ?? "the counterfactual actual-shape result is unknown"; cf = q; break; }
            if (q.Intersects == false) { credited = f; cf = q; break; }
            cf = q;
        }
        bool stacked = sides.Any(s => s.Owner.Fallback is { } fb && !MeshResponsibility.IsGame(MeshResponsibility.FolderOf(fb)));
        if (unknown is null && credited is null && (folders.Count > 1 || stacked))
        {
            if (Unchanged(h, sides.Select(s => (s.Ref, s.Owner, GameSource(s.Owner.Path))).ToList())) RegionShortcuts++;
            else
            {
                Joint++;
                var (q, why) = Query(h, sides.Select(s => (s.Ref.Key, SoupOf(GameSource(s.Owner.Path), _gameSoup, s.Owner.Path))).ToList());
                if (q is null || q.Unknown) unknown = why ?? q?.Reason ?? "the joint counterfactual actual-shape result is unknown";
                else if (q.Intersects == false) { credited = folders[0]; joint = true; }
                cf = q ?? cf;
            }
        }
        JudgeSeconds += clock.Elapsed.TotalSeconds;
        if (unknown is not null)
        {
            Unknown++;
            return new(h, Outcome.Unknown, null, folders, false, h.A, h.B, sides, cf, unknown, null, OverlapSight.Blind, unknown, default);
        }
        if (credited is null)
        {
            CrossesWithout++;
            return new(h, Outcome.Persists, null, folders, false, h.A, h.B, sides, cf, null, null, OverlapSight.Blind, "", default);
        }
        var mine = sides.Where(s => joint || string.Equals(s.Owner.Folder, credited, StringComparison.OrdinalIgnoreCase)).ToList();
        var nec = mine.Select(s => s.Ref).OrderBy(r => r.Key.ToString(), StringComparer.Ordinal).First();
        var other = nec.Key == h.A.Key ? h.B : h.A;
        var facts = Facts(credited, h.A.Win.Plugin, h.B.Win.Plugin, joint);
        var (origin, sight, reason) = DetectorModel.MeshProvenance(facts);
        if (sight == OverlapSight.Seen) { SeenRows++; Tally(SeenTally, credited, h.A.Win.Plugin, h.B.Win.Plugin, "made by the folder"); }
        return new(h, Outcome.Folder, credited, folders.Where(f => !string.Equals(f, credited, StringComparison.OrdinalIgnoreCase)).ToList(), joint,
                   nec, other, mine, cf, null, origin, sight, reason, facts);
    }

    /// <summary>Sight (GATE-B review finding 1): (a) the later placement's plugin had the folder's plugin in its closure and the
    /// earlier placement in view; (b) a plugin the folder ships has both placements' plugins in its closure.</summary>
    DetectorModel.MeshFacts Facts(string folder, int pa, int pb, bool joint)
    {
        int later = Math.Max(pa, pb), earlier = Math.Min(pa, pb);
        bool In(int owner, int x) => owner == x || (owner >= 0 && owner < _closure.Count && _closure[owner].Contains(x));
        var plugins = _folderPlugins.TryGetValue(folder, out var l) ? l : new List<int>();
        bool a = In(later, earlier) && plugins.Any(p => In(later, p));
        bool b = plugins.Any(p => In(p, pa) && In(p, pb));
        return new DetectorModel.MeshFacts(false, a, b, joint);
    }

    (SurfaceQueryOutcome? Query, string? Why) Query(SurfaceOverlapHit h, List<(FormKey Key, (TriangleSoup? Soup, string? Reason) Mesh)> swaps)
    {
        (TriangleSoup World, SurfaceQueryOutcome.Placed? Placed)? Side(SurfaceOverlapCandidate c, out string? why)
        {
            why = null;
            var s = swaps.FirstOrDefault(x => x.Key == c.Key);
            if (s.Key != c.Key) return (c.WorldSoup, c.Placed);
            if (s.Mesh.Soup is not { } soup) { why = s.Mesh.Reason; return null; }
            return (SpatialOverlap.TransformSoup(soup, c.Win.Pos, c.Win.Rot, c.Win.Scale), new(soup, c.Win.Pos, c.Win.Rot, c.Win.Scale));
        }
        var a = Side(h.A, out var wa); var b = Side(h.B, out var wb);
        if (a is null || b is null) return (null, wa ?? wb);
        return (SurfaceQueryOutcome.QueryPlaced(a.Value.World, a.Value.Placed, b.Value.World, b.Value.Placed, _tol, _maxTri, _timeout,
                                                default, null, null, _results), null);
    }

    /// <summary>The coverage line: what the rule judged and read, and the scope in a sentence. No timing: the emit is the
    /// same for the same inputs, so a --reverse-of emit can equal the full reverse's (its timings are printed instead).</summary>
    public CoverageLine Coverage()
    {
        long Count(MeshShape s) => Owners.Answered.Count(o => o.Shape == s);
        var unj = new Dictionary<string, long>();
        if (Unknown > 0) unj["MeshCounterfactualUnknown"] = Unknown;
        if (Count(MeshShape.Unreadable) > 0) unj["MeshCopyUnreadable"] = Count(MeshShape.Unreadable);
        return new CoverageLine("mesh responsibility", Judged, unj,
            new Dictionary<string, long>
            {
                ["CrossesWithoutTheFolderMesh"] = CrossesWithout, ["MeshEqualsReplaced"] = Count(MeshShape.EqualBytes),
                ["NewMeshNoFallback"] = Count(MeshShape.NoFallback), ["BaseGameReadmitted"] = Readmitted.Count,
                ["JointSwap"] = Joint, ["SeenFolderRows"] = SeenRows, ["SeenNotTested"] = SeenNotTested,
                ["SeenNotReadmitted"] = SeenNotReadmitted.Count, ["ChangedRegionClear"] = RegionShortcuts,
                ["TouchingNotCredited"] = TouchingNotCredited, ["MeshUnknownJudgedByPluginRule"] = UnknownJudgedByPluginRule,
            },
            $"credited: {Scope}; {Count(MeshShape.Credited):N0} model path(s) a credited folder reshapes; {Targets.Count:N0} target reference(s), "
          + $"{Partners:N0} base-game partner(s) readmitted");
    }
}
