using System.Security.Cryptography;
using System.Text.Json;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HarnessShared;

// THE PLAN'S POPULATION (P25, 2026-10-16). An evaluation compares arms that differ by a few plugins and mod folders (the
// delta, plus the plan's targets). A row can differ between two arms only if something the delta changes reaches it, so a
// probe that measures only the rows the delta can reach gives the same differential as a whole-order run at a fraction
// of the cost. This file reads the population file evalrun freezes into a plan and decides, per reference, whether it is
// IN SCOPE. The probe keeps its whole-order index: only the expensive per-pair / per-host work is skipped.
//
// The seeds come from the DELTA'S OWN FILES, never from the arm, so every arm scopes the same references: an arm that
// lacks a plugin cannot see the references that plugin overrides, and a per-arm derivation would credit a scope change.
//   S1 every placed reference a delta plugin file contains (new, override or deletion), read from the file by path;
//      and the cells those records sit in (a worldspace's persistent cell is never a region: its references count by key)
//   S2 every reference whose base's model is a file a delta mod folder provides (loose, or in an archive in the folder)
//   S3 every reference whose enable parent (any parent its XESP history names) is a seed, to a fixpoint
//   S4 every reference whose base record a delta plugin contains
// A delta that reaches references by a route this file does not model is REFUSED, by name: a collision-layer record
// (COLL) in a delta plugin, or a Base Object Swapper ini (*_SWAP.ini) in a delta mod folder.
//
// The identity: a scoped emit's Population is "<enable population>+population:<first 12 of the file's sha256>", so
// conflictdiff's population gate, the configuration fingerprint and evalrun's population check all see the scope.
public sealed class PopulationScope
{
    public const int Schema = 1;
    public const float CellSize = 4096f;
    /// <summary>A delta placement whose base bound reaches further than this from its origin gets a box of its own in the
    /// contact region, beyond the 3x3 ring of its cell.</summary>
    public const float LargeRadius = 2048f;
    /// <summary>The growth of a large placement's box: the largest dependent (256 u) twice over, so a dependent touching it
    /// has its origin inside.</summary>
    public const float LargeMargin = 512f;

    public string FilePath { get; }
    public string Sha256 { get; }
    public string Short => Sha256[..12];
    public IReadOnlyList<string> Plugins { get; }
    public IReadOnlyList<string> Mods { get; }

    // From the files (arm-independent).
    public HashSet<FormKey> FileRefs { get; } = new();
    public HashSet<FormKey> FileRecords { get; } = new();
    public HashSet<FormKey> FileCells { get; } = new();
    public HashSet<string> ModFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Cells where a delta plugin carries terrain or navmesh (interior cell keys; exterior worldspace + grid), whatever
    /// else the cell holds: the route half of stateprobe reads both (task 1 of window 2).</summary>
    public HashSet<FormKey> FileGeometryInterior { get; } = new();
    public HashSet<(FormKey Ws, int X, int Y)> FileGeometryExterior { get; } = new();
    /// <summary>References a delta plugin's worldspace records name in their large-reference lists (RNAM): the lists decide
    /// which far references a floor field admits.</summary>
    public HashSet<FormKey> FileListedLarge { get; } = new();
    /// <summary>The delta plugins that carry RACE records (actor heights and the tallest playable race: every actor band).</summary>
    public List<string> RaceCarriers { get; } = new();
    readonly Dictionary<FormKey, List<(FormKey Cell, FormKey Ws, bool Interior, Vec3 Pos, float Scale, FormKey? Base)>> _filePlacements = new();
    readonly Dictionary<FormKey, Vec3> _fileObnd = new();

    // Bound to one arm's index.
    public HashSet<FormKey> Seeds { get; } = new();
    public Dictionary<string, int> SeedCounts { get; } = new(StringComparer.Ordinal);
    readonly HashSet<FormKey> _regionInterior = new();
    readonly HashSet<(FormKey Ws, int X, int Y)> _regionExterior = new();
    readonly Dictionary<FormKey, List<Aabb>> _largeBoxes = new();
    bool _bound;

    PopulationScope(string path, string sha, IReadOnlyList<string> plugins, IReadOnlyList<string> mods)
    { FilePath = path; Sha256 = sha; Plugins = plugins; Mods = mods; }

    public static string IdentityOf(string basePopulation, string sha256) => PopulationRules.Identity(basePopulation, sha256);
    public string Identity(string basePopulation) => IdentityOf(basePopulation, Sha256);

    static string FileSha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>Read the population file and every delta file it names. Throws <see cref="PopulationRefused"/> with one
    /// sentence when the file cannot be used.</summary>
    public static PopulationScope Load(string path)
    {
        if (!File.Exists(path)) throw new PopulationRefused($"--population {path}: no such file; re-plan the evaluation or drop --population.");
        string sha = FileSha(path);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(path)); }
        catch (JsonException ex) { throw new PopulationRefused($"--population {path} is not JSON ({ex.Message}); re-plan the evaluation."); }
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("schema", out var sch) || sch.ValueKind != JsonValueKind.Number || sch.GetInt32() != Schema)
                throw new PopulationRefused($"--population {path}: schema is not {Schema}; re-plan the evaluation with this controller.");
            var plugins = new List<(string Name, string Path, string Sha)>();
            if (root.TryGetProperty("plugins", out var ps) && ps.ValueKind == JsonValueKind.Array)
                foreach (var p in ps.EnumerateArray())
                    plugins.Add((p.GetProperty("name").GetString() ?? "", p.GetProperty("path").GetString() ?? "", (p.GetProperty("sha256").GetString() ?? "").ToLowerInvariant()));
            var mods = new List<(string Name, string Path)>();
            if (root.TryGetProperty("mods", out var ms) && ms.ValueKind == JsonValueKind.Array)
                foreach (var m in ms.EnumerateArray())
                    mods.Add((m.GetProperty("name").GetString() ?? "", m.GetProperty("path").GetString() ?? ""));
            if (plugins.Count == 0 && mods.Count == 0)
                throw new PopulationRefused($"--population {path} names no plugin and no mod folder; an empty population would judge nothing, so it is refused.");

            var scope = new PopulationScope(Path.GetFullPath(path), sha, plugins.Select(p => p.Name).ToList(), mods.Select(m => m.Name).ToList());
            foreach (var (name, ppath, psha) in plugins)
            {
                if (!File.Exists(ppath)) throw new PopulationRefused($"the population names {name} at {ppath}, which does not exist; re-plan the evaluation.");
                string got = FileSha(ppath);
                if (got != psha) throw new PopulationRefused($"the population names {name} with sha256 {psha[..Math.Min(12, psha.Length)]}, but {ppath} hashes to {got[..12]}; re-plan the evaluation.");
                scope.ReadPlugin(name, ppath);
            }
            foreach (var (name, mpath) in mods)
            {
                if (!Directory.Exists(mpath)) throw new PopulationRefused($"the population names mod folder {name} at {mpath}, which does not exist; re-plan the evaluation.");
                scope.ReadModFolder(name, mpath);
            }
            return scope;
        }
    }

    void ReadPlugin(string name, string path)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        foreach (var rec in mod.EnumerateMajorRecords())
        {
            if (rec is ICollisionLayerGetter)
                throw new PopulationRefused($"{name} carries a collision-layer record ({rec.FormKey}), which changes how every reference collides; run this probe on the whole order (no --population).");
            if (rec is IPlacedGetter) continue;
            if (rec is IRaceGetter && !RaceCarriers.Contains(name)) RaceCarriers.Add(name);
            if (rec is IWorldspaceGetter ws) FileListedLarge.UnionWith(LargeReferenceLists.ListedIn(ws));
            FileRecords.Add(rec.FormKey);
            if (rec is IObjectBoundedGetter ob && ob.ObjectBounds is { } b)
                _fileObnd[rec.FormKey] = new Vec3(MathF.Abs(b.Second.X - b.First.X), MathF.Abs(b.Second.Y - b.First.Y), MathF.Abs(b.Second.Z - b.First.Z));
        }
        foreach (var cp in PlacementExtract.FromMod(mod, includeEmpty: true, includeDeletions: true))
        {
            bool geometry = cp.Navmeshes.Count > 0 || cp.DeletedNavmeshes is { Count: > 0 }
                         || cp.Land is not null || cp.LandUnreadableReason is not null || cp.LandDeclaresNoHeights;
            if (geometry)
            {
                if (cp.Interior) FileGeometryInterior.Add(cp.Cell);
                else if (cp.Grid is { } gg && !cp.Worldspace.IsNull) FileGeometryExterior.Add((cp.Worldspace, gg.X, gg.Y));
            }
            bool worldPersistent = !cp.Interior && cp.Grid is null && !cp.Worldspace.IsNull;
            if (!worldPersistent && (cp.Refs.Count > 0 || cp.DeletedRefs is { Count: > 0 })) FileCells.Add(cp.Cell);
            foreach (var r in cp.Refs)
            {
                FileRefs.Add(r.FormKey);
                if (!_filePlacements.TryGetValue(r.FormKey, out var l)) _filePlacements[r.FormKey] = l = new();
                l.Add((cp.Cell, cp.Worldspace, cp.Interior, r.Position, r.Scale, r.Base));
            }
            foreach (var d in cp.DeletedRefs ?? Array.Empty<DeletedRef>()) FileRefs.Add(d.FormKey);
        }
    }

    static string NormalAsset(string p)
    {
        var s = (p ?? "").Replace('/', '\\').TrimStart('\\');
        return s.ToLowerInvariant();
    }

    /// <summary>A model path as the probes resolve it: under meshes\, backslashes, lower case.</summary>
    public static string NormalMesh(string rel)
    {
        var s = NormalAsset(rel);
        return s.StartsWith("meshes\\", StringComparison.Ordinal) ? s : "meshes\\" + s;
    }

    void ReadModFolder(string name, string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = NormalAsset(Path.GetRelativePath(dir, f));
            if (PopulationRules.IsSwapIni(rel))
                throw new PopulationRefused($"mod folder {name} ships a Base Object Swapper ini ({rel}), whose swaps this population does not model; run this probe on the whole order (no --population).");
            ModFiles.Add(rel);
            if (!rel.Contains('\\') && (rel.EndsWith(".bsa", StringComparison.Ordinal) || rel.EndsWith(".ba2", StringComparison.Ordinal)))
            {
                var reader = Archive.CreateReader(GameRelease.SkyrimSE, f);
                try { foreach (var e in reader.Files) ModFiles.Add(NormalAsset(e.Path)); }
                finally { (reader as IDisposable)?.Dispose(); }
            }
        }
    }

    static (int X, int Y) GridOf(Vec3 p) => ((int)MathF.Floor(p.X / CellSize), (int)MathF.Floor(p.Y / CellSize));

    void AddRing(FormKey ws, (int X, int Y) g)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                _regionExterior.Add((ws, g.X + dx, g.Y + dy));
    }

    /// <summary>Derive this arm's seeds and the contact region from the index. Arm-independent by construction: every input
    /// is either a delta file or a record the delta does not touch (whose state is the same in every arm).</summary>
    public void Bind(OverlapIndex idx)
    {
        Seeds.Clear(); SeedCounts.Clear(); _regionInterior.Clear(); _regionExterior.Clear(); _largeBoxes.Clear();
        void Seed(FormKey k, string why) { if (Seeds.Add(k)) SeedCounts[why] = SeedCounts.GetValueOrDefault(why) + 1; }
        foreach (var k in FileRefs) Seed(k, "S1 delta plugin record");
        foreach (var (key, _) in idx.History.Refs)
        {
            if (!idx.RefBase.TryGetValue(key, out var b)) continue;
            if (FileRecords.Contains(b)) { Seed(key, "S4 base record in a delta plugin"); continue; }
            if (ModFiles.Count > 0
                && ((idx.BaseMesh.TryGetValue(b, out var m) && ModFiles.Contains(NormalMesh(m)))
                 || (idx.BaseMeshFemale.TryGetValue(b, out var mf) && ModFiles.Contains(NormalMesh(mf)))))
                Seed(key, "S2 model from a delta mod folder");
        }
        // S3: a reference any of whose XESP parents is a seed, to a fixpoint (the chain's own untouched links are the same
        // records in every arm; a touched link is itself a seed).
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var (key, changes) in idx.EnableChanges)
            {
                if (Seeds.Contains(key)) continue;
                foreach (var c in changes)
                    if (c.Parent is { } p && Seeds.Contains(p)) { Seed(key, "S3 enable child of a seed"); grew = true; break; }
            }
        }
        foreach (var c in FileCells)
        {
            if (idx.CellName.TryGetValue(c, out var ci) && ci.Interior) _regionInterior.Add(c);
        }
        // The contact region: every interior cell a seed is ever filed in; around every exterior position a seed ever
        // takes (this arm's history and the delta files' own placements), the 3x3 ring of its cell; plus a box for a
        // placement whose base bound reaches beyond a cell's half width (OBND from the delta file, else the index).
        foreach (var k in Seeds)
        {
            var poses = new List<(FormKey Cell, FormKey Ws, bool Interior, Vec3 Pos, float Scale, FormKey? Base)>();
            if (idx.History.Of(k) is { } snaps)
                foreach (var s in snaps) poses.Add((s.Cell, s.Worldspace, s.Interior, s.Pos, s.Scale, idx.RefBase.TryGetValue(k, out var rb) ? rb : null));
            if (_filePlacements.TryGetValue(k, out var fp)) poses.AddRange(fp);
            foreach (var p in poses)
            {
                if (p.Interior) { _regionInterior.Add(p.Cell); continue; }
                if (p.Ws.IsNull) continue;
                AddRing(p.Ws, GridOf(p.Pos));
                Vec3? obnd = p.Base is { } bk ? (_fileObnd.TryGetValue(bk, out var fo) ? fo : idx.BaseObnd.TryGetValue(bk, out var io) ? io : null) : null;
                if (obnd is { } e)
                {
                    float r = 0.5f * MathF.Sqrt(e.X * e.X + e.Y * e.Y + e.Z * e.Z) * MathF.Max(1f, p.Scale);
                    if (r > LargeRadius)
                    {
                        float g = r + LargeMargin;
                        if (!_largeBoxes.TryGetValue(p.Ws, out var l)) _largeBoxes[p.Ws] = l = new();
                        l.Add(new Aabb(new Vec3(p.Pos.X - g, p.Pos.Y - g, p.Pos.Z - g), new Vec3(p.Pos.X + g, p.Pos.Y + g, p.Pos.Z + g), false));
                    }
                }
            }
        }
        // Every population cell is covered above: its references are S1 seeds, placed by the delta file (new or override) or,
        // for a deletion, by this arm's history of the deleted reference.
        _bound = true;
    }

    void RequireBound() { if (!_bound) throw new InvalidOperationException("PopulationScope.Bind was not called"); }

    /// <summary>The overlap rule: a seed, or filed (in this placement) in a population cell.</summary>
    public bool KeyInScope(FormKey key, FormKey cell)
    {
        RequireBound();
        return Seeds.Contains(key) || FileCells.Contains(cell);
    }

    /// <summary>The contact rule for one placement: an interior cell of the region, or an exterior position inside the
    /// region's ring or a large placement's box.</summary>
    public bool PlaceInScope(Snap s)
    {
        RequireBound();
        if (s.Interior) return _regionInterior.Contains(s.Cell);
        if (s.Worldspace.IsNull) return false;
        var g = GridOf(s.Pos);
        if (_regionExterior.Contains((s.Worldspace, g.X, g.Y))) return true;
        if (_largeBoxes.TryGetValue(s.Worldspace, out var l))
            foreach (var b in l)
                if (s.Pos.X >= b.Min.X && s.Pos.X <= b.Max.X && s.Pos.Y >= b.Min.Y && s.Pos.Y <= b.Max.Y && s.Pos.Z >= b.Min.Z && s.Pos.Z <= b.Max.Z) return true;
        return false;
    }

    // ---- the empty-space scope (window 2, task 1) ----------------------------------------------------------------------

    /// <summary>The interior cells an empty-space judgement in the population can read: the region's interior cells, closed
    /// so that a reference with an interior placement in the set has every interior placement in it, and so are its rack
    /// partners' (<paramref name="partners"/>). An assembly walk stays in one interior cell except through those two links,
    /// so a fixture of these cells reads, and leaves behind in the search's memos, only references of these cells.</summary>
    public HashSet<FormKey> InteriorClosure(OverlapIndex idx, Func<FormKey, IEnumerable<FormKey>>? partners, bool close = true)
    {
        RequireBound();
        var set = new HashSet<FormKey>(_regionInterior);
        if (!close) return set;
        var byCell = new Dictionary<FormKey, List<FormKey>>();
        foreach (var (key, snaps) in idx.History.Refs)
            foreach (var s in snaps)
                if (s.Interior)
                {
                    if (!byCell.TryGetValue(s.Cell, out var l)) byCell[s.Cell] = l = new();
                    if (l.Count == 0 || l[^1] != key) l.Add(key);
                }
        var queue = new Queue<FormKey>(set);
        var seenRef = new HashSet<FormKey>();
        void Pull(FormKey r)
        {
            if (idx.History.Of(r) is not { } hs) return;
            foreach (var s in hs)
                if (s.Interior && set.Add(s.Cell)) queue.Enqueue(s.Cell);
        }
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            if (!byCell.TryGetValue(c, out var refs)) continue;
            foreach (var r in refs)
            {
                if (!seenRef.Add(r)) continue;
                Pull(r);
                if (partners is not null) foreach (var p in partners(r)) Pull(p);
            }
        }
        return set;
    }

    // ---- the state route scope (window 2, task 1) ----------------------------------------------------------------------

    /// <summary>How far, in exterior cells, a floor reference reaches into another cell's field: filed in the 5x5 and entering
    /// the 3x3, or a listed large reference from rings 3 to 5 (_shared/FloorFeed.cs).</summary>
    public const int RouteRing = 5;
    readonly Dictionary<FormKey, HashSet<(int X, int Y)>> _routeCentres = new();
    readonly Dictionary<FormKey, HashSet<(int X, int Y)>> _largeCentres = new();
    public int RouteRingUsed { get; private set; } = RouteRing;

    /// <summary>The centres the route rule measures from: every exterior position a seed ever takes (this arm's history and
    /// the delta files' placements), every exterior cell a delta plugin carries terrain or navmesh for; and, apart, every
    /// position of a reference a delta worldspace record lists as large.</summary>
    public void BindRoute(OverlapIndex idx, int ring = RouteRing)
    {
        RequireBound();
        RouteRingUsed = ring;
        _routeCentres.Clear(); _largeCentres.Clear();
        void Add(Dictionary<FormKey, HashSet<(int, int)>> d, FormKey ws, (int, int) g)
        { if (!d.TryGetValue(ws, out var h)) d[ws] = h = new(); h.Add(g); }
        void Poses(FormKey k, Dictionary<FormKey, HashSet<(int, int)>> d)
        {
            if (idx.History.Of(k) is { } snaps)
                foreach (var s in snaps) if (!s.Interior && !s.Worldspace.IsNull) Add(d, s.Worldspace, GridOf(s.Pos));
            if (_filePlacements.TryGetValue(k, out var fp))
                foreach (var p in fp) if (!p.Interior && !p.Ws.IsNull) Add(d, p.Ws, GridOf(p.Pos));
        }
        foreach (var k in Seeds) Poses(k, _routeCentres);
        foreach (var (ws, x, y) in FileGeometryExterior) Add(_routeCentres, ws, (x, y));
        foreach (var k in FileListedLarge) Poses(k, _largeCentres);
    }

    static bool WithinRing(Dictionary<FormKey, HashSet<(int X, int Y)>> d, FormKey ws, (int X, int Y) g, int ring)
    {
        if (!d.TryGetValue(ws, out var h) || h.Count == 0) return false;
        for (int dx = -ring; dx <= ring; dx++)
            for (int dy = -ring; dy <= ring; dy++)
                if (h.Contains((g.X + dx, g.Y + dy))) return true;
        return false;
    }

    /// <summary>The route rule for a changed reference standing in <paramref name="cell"/>: which part of the rule admits it
    /// ("route-interior", "route-ring", "large-ref"), or null. <paramref name="grid"/> is the exterior cell's grid.</summary>
    public string? RouteRule(FormKey cell, bool interior, FormKey ws, (int X, int Y)? grid)
    {
        RequireBound();
        if (interior) return _regionInterior.Contains(cell) || FileGeometryInterior.Contains(cell) ? "route-interior" : null;
        if (ws.IsNull || grid is not { } g) return null;
        if (WithinRing(_routeCentres, ws, g, RouteRingUsed)) return "route-ring";
        if (WithinRing(_largeCentres, ws, g, RouteRingUsed)) return "large-ref";
        return null;
    }

    public static (int X, int Y) GridAt(Vec3 p) => GridOf(p);

    public string Describe() =>
        $"population {Short} ({FilePath}): {Plugins.Count} plugin(s), {Mods.Count} mod folder(s); {FileRefs.Count:N0} delta references, "
      + $"{FileCells.Count:N0} cells, {FileRecords.Count:N0} other records, {ModFiles.Count:N0} mod files; seeds {Seeds.Count:N0} ("
      + string.Join(", ", SeedCounts.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Value:N0}")) + ")";
}

/// <summary>The population file cannot be used; the message is the one sentence the probe prints.</summary>
public sealed class PopulationRefused(string message) : Exception(message);
