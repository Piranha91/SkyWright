using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SkyWright.Engine;

/// <summary>A worldspace's persistent cell: the one the latest plugin copy that names one names. A later plugin's copy of
/// the worldspace record often carries no persistent cell, so the winning copy alone can name none.</summary>
public static class PersistentCell
{
    /// <summary>The persistent cell from what each plugin's copy of the worldspace names, in load order (null where a copy
    /// names none); null when no copy names one.</summary>
    public static FormKey? Of(IEnumerable<FormKey?> namedByEachCopyInOrder)
    {
        FormKey? found = null;
        foreach (var n in namedByEachCopyInOrder) if (n is { } k) found = k;
        return found;
    }
}

/// <summary>One way a candidate reached a seed: whose version, how far apart the two extents are, and by which rule.</summary>
public readonly record struct NearHit(FormKey Seed, string Provider, double Gap, string Rule);

/// <summary>The records standing near a seed set, nearest first, each with the hits that put it there; or a refusal.
/// <paramref name="Unread"/> names the plugins that could not be read, so what they hold is not in the answer;
/// <paramref name="Winners"/> holds the winning version of each key when a type filter asked for them.
/// <paramref name="CompleteWithin"/> is the radius up to which nothing the asked radius reaches can lie outside the cells
/// read (the nearest edge of each exterior seed's read square, less the seed's own extent; +∞ when every seed is
/// interior), and <paramref name="WithinIsComplete"/> says whether the asked radius stays inside it.</summary>
public sealed record NearAnswer(IReadOnlyList<FormKey> Keys, IReadOnlyDictionary<FormKey, IReadOnlyList<NearHit>> Hits,
                                string Note, IReadOnlyList<string> Unread, string? Refusal,
                                IReadOnlyDictionary<FormKey, IMajorRecordGetter>? Winners = null,
                                double CompleteWithin = double.PositiveInfinity, bool WithinIsComplete = true)
{
    public static NearAnswer Fail(string refusal) =>
        new(Array.Empty<FormKey>(), new Dictionary<FormKey, IReadOnlyList<NearHit>>(), "", Array.Empty<string>(), refusal);

    /// <summary>The per-match line: up to three hits, nearest first, and how many more.</summary>
    public string Annotate(FormKey fk)
    {
        if (!Hits.TryGetValue(fk, out var hits) || hits.Count == 0) return "";
        var shown = hits.Take(3).Select(h =>
            $"near {FormIdToken.Of(h.Seed)} by {h.Provider} (gap {Math.Round(h.Gap).ToString(CultureInfo.InvariantCulture)}, {h.Rule})");
        return string.Join("; ", shown) + (hits.Count > 3 ? $"; +{hits.Count - 3} more" : "");
    }

    /// <summary>One sentence naming the plugins that could not be read, or null when every plugin was read.</summary>
    public string? UnreadSentence => Unread.Count == 0 ? null
        : $"{Unread.Count} plugin(s) could not be read ({string.Join(", ", Unread)}), so what they place or move near it is not listed";
}

/// <summary>What stands near a set of placed references (the seeds). A placed reference's extent is a sphere about its
/// origin, sized by its base's ObjectBounds (or its own primitive) times its scale, which no rotation can make wrong and
/// which contains the model, so a match is a place to look rather than an overlap. A record a cell holds that is not
/// placed (terrain, navmesh) takes its cell's square. Every plugin's version of a candidate is tested, and a hit is
/// credited to the plugin whose version reached the seed; the winning version decides the record's type. Moved here from
/// houseCARL's <c>near=</c> (step 5 of the boundary), with the persistent cell taken from any plugin's copy.</summary>
public static class NearReferences
{
    /// <summary>The side of an exterior cell, in game units.</summary>
    public const float CellUnits = 4096f;

    /// <summary>How many grid cells around a seed are read: the reach of large references the engine loads with a
    /// cell, measured in game (loaded grid ±2, large references ±5).</summary>
    public const int GridReach = 5;

    public const string SphereRule = "sphere";
    /// <summary>A sphere whose size came from the reference's own primitive (an occlusion plane or box, a trigger),
    /// not its base's bounds: invisible geometry, labelled so a reader can set it aside.</summary>
    public const string PrimitiveRule = "primitive";
    public const string SquareRule = "cell square";
    public const string InteriorRule = "same interior cell";

    sealed record SeedRef(FormKey Key, string Space, Vec3 At, double Radius);

    readonly record struct Where(string Space, (int X, int Y)? Grid);

    /// <summary>The records near <paramref name="seedKeys"/> within <paramref name="within"/> units. With
    /// <paramref name="winnerTypes"/>, only records whose winning version is one of those getter types are kept, and
    /// their winning versions come back in <see cref="NearAnswer.Winners"/>.</summary>
    public static NearAnswer Of(LoadOrderResolver.IndexView view, IReadOnlyList<FormKey> seedKeys, double within,
                                IReadOnlyCollection<Type>? winnerTypes = null, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        using var session = view.OpenSession();
        var unsized = new HashSet<FormKey>();
        var unread = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        int versionsRead = 0;
        Dictionary<FormKey, IMajorRecordGetter> Winners(IReadOnlyCollection<FormKey> keys, Type? type)
        {
            var got = WinnerBodies.For(view, session, keys, type is null ? null : new[] { type }, out var faults, ct);
            foreach (var p in faults.Keys) unread.Add(p);
            return got;
        }

        // ---- the seeds ------------------------------------------------------------------------------------
        var distinctSeeds = seedKeys.Distinct().ToList();
        if (distinctSeeds.Count == 0) return NearAnswer.Fail("no reference was named, so there is nothing to look near; name a placed reference.");
        foreach (var sk in distinctSeeds)
            if (view.ResolveWinner(sk) is null)
                return NearAnswer.Fail($"{FormIdToken.Of(sk)} is not in this load order, so nothing near it can be read; pick a reference from a plugin the load order holds.");
        var seedBodies = Winners(distinctSeeds, null);
        var seedCells = distinctSeeds.Select(view.ParentOf).Where(k => k is not null).Select(k => k!.Value).Distinct().ToList();
        var cellBodies = Winners(seedCells, typeof(ICellGetter));
        var seedRefs = new List<(FormKey Key, PlacedRef Ref, string Space)>();
        var wantGrids = new Dictionary<FormKey, HashSet<(int, int)>>();
        var interiorCells = new HashSet<FormKey>();
        foreach (var sk in distinctSeeds)
        {
            seedBodies.TryGetValue(sk, out var body);
            if (body is null)
                return NearAnswer.Fail($"{FormIdToken.Of(sk)} could not be read from the plugin that wins it ({view.ResolveWinner(sk)?.WinnerPlugin}), so nothing near it can be listed; close any program holding that plugin and try again.");
            if (body is not IPlacedGetter placed)
                return NearAnswer.Fail($"{FormIdToken.Of(sk)} is a {RecordNaming.StripOverlay(body.GetType().Name)} record, not a placed reference, so it stands nowhere; pick a reference placed in a cell.");
            if (PlacementExtract.RefOf(placed) is not { } r)
                return NearAnswer.Fail($"{FormIdToken.Of(sk)} is deleted in this load order and has no position, so nothing stands near it.");
            if (view.ParentOf(sk) is not { } ck || !cellBodies.TryGetValue(ck, out var cb) || cb is not ICellGetter cell)
                return NearAnswer.Fail($"the cell that holds {FormIdToken.Of(sk)} could not be read in this load order, so what stands near it cannot be listed.");
            if (cell.Flags.HasFlag(Cell.Flag.IsInteriorCell))
            {
                interiorCells.Add(ck);
                seedRefs.Add((sk, r, "int:" + ck));
                continue;
            }
            if (view.ParentOf(ck) is not { } wk)
                return NearAnswer.Fail($"{FormIdToken.Of(sk)} stands in a cell no worldspace holds in this load order, so what stands near it cannot be listed.");
            seedRefs.Add((sk, r, "wrld:" + wk));
            if (!wantGrids.TryGetValue(wk, out var g)) wantGrids[wk] = g = new();
            int gx = (int)Math.Floor(r.Position.X / CellUnits), gy = (int)Math.Floor(r.Position.Y / CellUnits);
            for (int dx = -GridReach; dx <= GridReach; dx++)
                for (int dy = -GridReach; dy <= GridReach; dy++) g.Add((gx + dx, gy + dy));
        }
        var seedSet = distinctSeeds.ToHashSet();

        // ---- the candidates: what the read cells hold ------------------------------------------------------
        var candidates = new Dictionary<FormKey, Where>();
        int cellsRead = 0;
        foreach (var (wrld, grids) in wantGrids)
        {
            var cellKeys = view.ChildrenOf(wrld);
            var cells = Winners(cellKeys, typeof(ICellGetter));
            FormKey? persistentCell = PersistentCellOf(view, session, wrld, unread, ct);
            foreach (var ck in cellKeys)
            {
                if (!cells.TryGetValue(ck, out var cb) || cb is not ICellGetter cell) continue;
                if (cell.Flags.HasFlag(Cell.Flag.IsInteriorCell)) continue;
                bool persistent = ck == persistentCell || cell.Grid is null;
                (int X, int Y)? grid = !persistent && cell.Grid is { } cg ? (cg.Point.X, cg.Point.Y) : null;
                if (grid is { } gp && !grids.Contains(gp)) continue;
                cellsRead++;
                foreach (var child in view.ChildrenOf(ck))
                    if (!seedSet.Contains(child)) candidates[child] = new Where("wrld:" + wrld, grid);
            }
        }
        foreach (var ic in interiorCells)
        {
            cellsRead++;
            foreach (var child in view.ChildrenOf(ic))
                if (!seedSet.Contains(child)) candidates[child] = new Where("int:" + ic, null);
        }

        // ---- every plugin's version of every candidate, read once ------------------------------------------
        var gather = new BodyGather(view, session, absent: BodyGather.Absent.Null, ct: ct);
        foreach (var fk in candidates.Keys)
            foreach (var p in view.TouchingPlugins(fk) ?? Array.Empty<string>())
                gather.Want(p, fk);
        gather.Gather();
        foreach (var p in gather.Faulted) unread.Add(p);
        var versions = new List<(FormKey Key, Where Where, string Provider, PlacedRef? Ref)>();
        foreach (var (fk, where) in candidates)
            foreach (var provider in view.TouchingPlugins(fk) ?? Array.Empty<string>())
            {
                ct.ThrowIfCancellationRequested();
                var body = gather.Body(provider, fk);
                if (body is null) continue;
                versionsRead++;
                if (body is IPlacedGetter placed)
                {
                    if (PlacementExtract.RefOf(placed) is { } r) versions.Add((fk, where, provider, r));   // deleted: stands nowhere
                }
                else versions.Add((fk, where, provider, null));
            }

        // ---- the bases' bounds, gathered once -----------------------------------------------------------------
        var baseKeys = seedRefs.Select(s => s.Ref.Base).Concat(versions.Select(v => v.Ref?.Base))
                               .Where(b => b is { IsNull: false }).Select(b => b!.Value).Distinct().ToList();
        var baseRadius = new Dictionary<FormKey, double>();
        foreach (var (bk, body) in Winners(baseKeys, null))
            if (BoundsOf(body) is { } ob)
            {
                double best = 0;
                foreach (var x in new[] { ob.First.X, ob.Second.X })
                    foreach (var y in new[] { ob.First.Y, ob.Second.Y })
                        foreach (var z in new[] { ob.First.Z, ob.Second.Z })
                            best = Math.Max(best, Math.Sqrt((double)x * x + (double)y * y + (double)z * z));
                baseRadius[bk] = best;
            }
        var seeds = seedRefs.Select(s => new SeedRef(s.Key, s.Space, s.Ref.Position, RadiusOf(s.Ref))).ToList();

        // ---- the test ---------------------------------------------------------------------------------------
        var hits = new Dictionary<FormKey, List<NearHit>>();
        foreach (var (fk, where, provider, placedRef) in versions)
        {
            double placedRadius = placedRef is { } sized ? RadiusOf(sized) : 0;
            bool byPrimitive = placedRef is { } shaped && SizedByPrimitive(shaped);
            foreach (var seed in seeds)
            {
                if (seed.Space != where.Space) continue;
                double gap; string rule;
                if (placedRef is { } pr)
                {
                    gap = Math.Max(0, Distance(pr.Position, seed.At) - placedRadius - seed.Radius);
                    rule = byPrimitive ? PrimitiveRule : SphereRule;
                }
                else if (where.Space.StartsWith("int:", StringComparison.Ordinal)) { gap = 0; rule = InteriorRule; }
                else if (where.Grid is { } g) { gap = Math.Max(0, SquareDistance(g, seed.At) - seed.Radius); rule = SquareRule; }
                else continue;                                                             // not placed, in no grid cell
                if (gap > within + 1e-9) continue;
                if (!hits.TryGetValue(fk, out var list)) hits[fk] = list = new();
                list.Add(new NearHit(seed.Key, provider, gap, rule));
            }
        }

        // ---- the winning version decides the type -----------------------------------------------------------
        Dictionary<FormKey, IMajorRecordGetter>? winners = null;
        if (winnerTypes is { Count: > 0 })
        {
            winners = Winners(hits.Keys.ToList(), null);
            foreach (var fk in hits.Keys.ToList())
                if (!winners.TryGetValue(fk, out var w) || !winnerTypes.Any(t => t.IsInstanceOfType(w)))
                {
                    hits.Remove(fk);
                    winners.Remove(fk);
                }
        }

        var ordered = hits.OrderBy(kv => kv.Value.Min(h => h.Gap)).ThenBy(kv => FormIdToken.Of(kv.Key), StringComparer.Ordinal)
                          .Select(kv => kv.Key).ToList();
        var frozen = hits.ToDictionary(kv => kv.Key,
            kv => (IReadOnlyList<NearHit>)kv.Value.OrderBy(h => h.Gap).ThenBy(h => h.Provider, StringComparer.OrdinalIgnoreCase).ToList());
        clock.Stop();
        var note = $"{seeds.Count} reference(s); read {cellsRead} cell(s) (the grid cells within {GridReach} grid cells of each exterior reference, " +
                   $"each worldspace's persistent cell, and each interior reference's own cell) and {versionsRead} record version(s) in {clock.ElapsedMilliseconds} ms; " +
                   $"{ordered.Count} record(s) within {within.ToString(CultureInfo.InvariantCulture)} unit(s). A reference's extent is a sphere about its origin " +
                   "sized by its base's ObjectBounds or its own primitive, times its scale (rotation-free, a superset of the model, so a match is a place to look, " +
                   "not an overlap); a sphere set by the reference's own primitive (an occlusion plane or box, a trigger) is labelled 'primitive'; " +
                   "terrain and navmesh take their cell's square." +
                   (unsized.Count > 0 ? $" {unsized.Count} reference(s) had no bounds the load order could read and count by their origin only." : "") +
                   $" A plugin that moves a reference in from further than {GridReach} cells away without winning it is not read.";
        return new NearAnswer(ordered, frozen, note, unread.ToList(), null, winners);

        bool SizedByPrimitive(PlacedRef r)
        {
            if (r.Primitive is not { } prim) return false;
            double baseR = r.Base is { IsNull: false } bk && baseRadius.TryGetValue(bk, out var br) ? br : 0;
            return PrimitiveHalf(prim) > baseR;
        }

        static double PrimitiveHalf(PlacedPrimitiveInfo prim) =>
            0.5 * Math.Sqrt((double)prim.Bounds.X * prim.Bounds.X + (double)prim.Bounds.Y * prim.Bounds.Y + (double)prim.Bounds.Z * prim.Bounds.Z);

        double RadiusOf(PlacedRef r)
        {
            double radius = 0; bool known = false;
            if (r.Base is { IsNull: false } bk && baseRadius.TryGetValue(bk, out var br)) { radius = br; known = true; }
            if (r.Primitive is { } prim)
            {
                radius = Math.Max(radius, PrimitiveHalf(prim)); known = true;
            }
            if (!known) unsized.Add(r.FormKey);
            return radius * r.Scale;
        }
    }

    /// <summary>The persistent cell of a worldspace in this load order, read from every plugin's copy of the worldspace
    /// (<see cref="PersistentCell.Of"/>); a plugin whose copy could not be read is added to <paramref name="unread"/>.</summary>
    public static FormKey? PersistentCellOf(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session, FormKey worldspace,
                                            ISet<string> unread, CancellationToken ct = default)
    {
        var providers = (view.TouchingPlugins(worldspace) ?? Array.Empty<string>()).OrderBy(view.OrderIndexOf).ToList();
        var gather = new BodyGather(view, session, new[] { typeof(IWorldspaceGetter) }, BodyGather.Absent.Null, ct: ct);
        foreach (var p in providers) gather.Want(p, worldspace);
        gather.Gather();
        foreach (var p in gather.Faulted) unread.Add(p);
        return PersistentCell.Of(providers.Select(p => gather.Body(p, worldspace) is IWorldspaceGetter ws && ws.TopCell is { } top ? top.FormKey : (FormKey?)null));
    }

    static readonly ConcurrentDictionary<Type, PropertyInfo?> BoundsProp = new();

    /// <summary>A record's ObjectBounds, by reflection over Mutagen's model: the 38 types that carry them need no list here.</summary>
    static IObjectBoundsGetter? BoundsOf(IMajorRecordGetter body)
    {
        var prop = BoundsProp.GetOrAdd(body.GetType(), t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.Name == "ObjectBounds" && typeof(IObjectBoundsGetter).IsAssignableFrom(p.PropertyType)));
        try { return prop?.GetValue(body) as IObjectBoundsGetter; }
        catch (TargetInvocationException) { return null; }
    }

    static double Distance(Vec3 a, Vec3 b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>The XY distance from a point to a grid cell's square (zero inside it).</summary>
    static double SquareDistance((int X, int Y) g, Vec3 p)
    {
        double minX = g.X * (double)CellUnits, minY = g.Y * (double)CellUnits;
        double dx = Math.Max(Math.Max(minX - p.X, 0), p.X - (minX + CellUnits));
        double dy = Math.Max(Math.Max(minY - p.Y, 0), p.Y - (minY + CellUnits));
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
