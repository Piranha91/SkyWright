using System.Numerics;
using Mutagen.Bethesda.Plugins;

using HousecarlCore;

namespace SkyWright.Engine;

// ======================================================================
//  NavmeshFloor — the navmesh-to-floor offset (roadmap 4.7's band, Q24).
//
//  Every vertical number the route work measures is stated ABOVE THE
//  NAVMESH: the actor band's bottom and top, an obstacle's rise at a cut,
//  the beam ladders of the in-game arms. The walker, though, steps and
//  ducks against its FEET, which rest on the physical floor — the
//  collision surface under the navmesh — and at the one station where both
//  were measured (2026-09-05, the Ratway corridor) the navmesh floats about
//  10 u above that floor, 5 u sixty-four units west of it and 17 u east.
//  So a band measured at one station transfers to another cell only through
//  that cell's own offset. This file measures the offset from the records:
//  for a point on the navmesh, the height of the floor beneath it.
//
//  The floor is the surface a walker's feet come to rest on: the HIGHEST
//  up-facing actor-solid collision surface under the point that lies no
//  deeper than `maxDepth` below the navmesh and no higher than `upReach`
//  above it. Above it, because a navmesh is authored slightly into as well
//  as slightly over its floor, and because the character controller steps
//  onto anything within its step height — a crate the navmesh passes under
//  IS the floor where the walker stands on it. Nothing within reach is "no
//  floor", and the reason is kept: a bridge deck over a chasm floor deeper
//  than the bound, a navmesh over water, a navmesh whose floor mesh carries
//  no actor-solid collision, a navmesh inside a solid.
//
//  Pure geometry over a column the caller supplies — the surfaces a
//  MeshColumnIndex presents at (x,y), and the terrain height where the cell
//  has a LAND record (exterior navmesh mostly rests on the heightmap, which
//  no NIF carries). Which collision layers an actor meets is the caller's
//  judgement (the harness's CollisionLayers); which surface is the floor is
//  this file's, stated once here and pinned by proofprobe.
// ======================================================================

/// <summary>Where the floor under a navmesh point came from.</summary>
public enum FloorSource : byte
{
    /// <summary>No floor within reach — <see cref="FloorReading.Reason"/> says why.</summary>
    None,
    /// <summary>A placed object's actor-solid collision surface.</summary>
    Mesh,
    /// <summary>The cell's LAND heightmap.</summary>
    Land,
}

/// <summary>Why a navmesh point has no floor within reach. Never read as "the navmesh is fine": every value is a
/// point the walker's feet cannot be placed from the records, and the sweep counts them apart.</summary>
public enum NoFloorReason : byte
{
    /// <summary>A floor was found.</summary>
    Found,
    /// <summary>No collision surface and no terrain in the column at all — a floor mesh with no actor-solid body, or
    /// a navmesh over nothing (water, a void).</summary>
    NoSurface,
    /// <summary>Surfaces exist but every one lies deeper than the bound — a deck over a drop.</summary>
    OnlyDeeper,
    /// <summary>Surfaces exist but every one lies higher than the reach — the navmesh runs under something with no
    /// floor of its own within reach.</summary>
    OnlyHigher,
    /// <summary>The only surfaces within reach face DOWN: the navmesh point is inside a solid, or under a ceiling with
    /// nothing to stand on beneath it.</summary>
    OnlyDownFacing,
}

/// <summary>The floor under one navmesh point.</summary>
/// <param name="NavZ">The navmesh surface height at the point.</param>
/// <param name="FloorZ">The floor's height; NaN when none was found.</param>
/// <param name="Embedded">The nearest surface at or below the navmesh faces down — the navmesh point sits inside a
/// solid (a navmesh authored into a thick floor, a navmesh through a crate). A diagnostic beside the reading: the
/// floor is still the highest up-facing surface within reach.</param>
/// <param name="Surfaces">How many surfaces the column presented, terrain included — zero is the NoSurface case.</param>
public readonly record struct FloorReading(float NavZ, float FloorZ, FloorSource Source, NoFloorReason Reason, bool Embedded, int Surfaces)
{
    public bool Found => Source != FloorSource.None;
    /// <summary>The offset δ: navmesh minus floor. POSITIVE when the navmesh floats above the floor (the Ratway's
    /// +10 u), negative when the floor stands above the navmesh (a sunk navmesh, a crate the walker steps onto).</summary>
    public float Offset => NavZ - FloorZ;
}

public static class NavmeshFloor
{
    /// <summary>How far below the navmesh a floor may lie and still count. Deeper is a drop the walker cannot be
    /// standing at the bottom of while walking this navmesh: a bridge deck over a chasm, a navmesh over deep water.
    /// The route rule's storey tolerance is the same number (RouteConnectivity.Measure, storeyTolerance) — ground
    /// further apart than this is two storeys, not one floor.</summary>
    public const float DefaultMaxDepth = 128f;

    /// <summary>How far above the navmesh a floor may stand and still be the floor. The character controller steps onto
    /// anything within its step height, so a surface within that reach is where the feet rest; DetectorModel.StepHeight
    /// (48 u, measured 2026-09-05) is the caller's value for a humanoid. The core has no band constant, so this default
    /// restates the measured number rather than importing the harness's.</summary>
    public const float DefaultUpReach = 48f;

    /// <summary>A triangle whose longest edge exceeds this is sampled at four interior points rather than one — a hall's
    /// navmesh is a few large triangles, and its floor can change within one.</summary>
    public const float DefaultSplitAbove = 192f;

    /// <summary>Read the floor under one navmesh point from the column the caller gathered.</summary>
    /// <param name="navZ">The navmesh surface height at the point.</param>
    /// <param name="surfaces">Every actor-solid collision surface at the point's (x,y): height and facing
    /// (<see cref="MeshColumnIndex.SurfacesAt"/>), any order.</param>
    /// <param name="landZ">The terrain height at the point, or null where the cell has no LAND (every interior).</param>
    public static FloorReading Read(float navZ, IReadOnlyList<(float Z, bool Up)> surfaces, float? landZ,
                                    float maxDepth = DefaultMaxDepth, float upReach = DefaultUpReach)
    {
        float lo = navZ - maxDepth, hi = navZ + upReach;
        float best = float.NaN; var source = FloorSource.None;
        bool anyDeeper = false, anyDownInReach = false;
        int count = 0;
        // The nearest surface at or below the navmesh, for the embedded diagnostic: an up-facing one within half a
        // unit of the highest wins a tie (a crate's underside coincident with the floor it stands on).
        float nearestBelow = float.NegativeInfinity; bool nearestBelowUp = false;
        foreach (var (z, up) in surfaces)
        {
            count++;
            if (z <= navZ + 0.5f)
            {
                if (z > nearestBelow + 0.5f) { nearestBelow = z; nearestBelowUp = up; }
                else if (z > nearestBelow - 0.5f && up) nearestBelowUp = true;
            }
            if (z < lo) { anyDeeper = true; continue; }
            if (z > hi) continue;
            if (!up) { anyDownInReach = true; continue; }
            if (float.IsNaN(best) || z > best) { best = z; source = FloorSource.Mesh; }
        }
        if (landZ is { } lz)
        {
            count++;
            if (lz <= navZ + 0.5f && lz > nearestBelow - 0.5f) { nearestBelow = MathF.Max(nearestBelow, lz); nearestBelowUp = true; }
            if (lz < lo) anyDeeper = true;
            else if (lz > hi) { }
            else if (float.IsNaN(best) || lz > best) { best = lz; source = FloorSource.Land; }
        }
        if (source == FloorSource.None)
        {
            var reason = count == 0 ? NoFloorReason.NoSurface
                       : anyDownInReach ? NoFloorReason.OnlyDownFacing
                       : anyDeeper ? NoFloorReason.OnlyDeeper
                       : NoFloorReason.OnlyHigher;
            return new FloorReading(navZ, float.NaN, FloorSource.None, reason, false, count);
        }
        bool embedded = !float.IsNegativeInfinity(nearestBelow) && !nearestBelowUp;
        return new FloorReading(navZ, best, source, NoFloorReason.Found, embedded, count);
    }

    /// <summary>The plan points a navmesh triangle is read at, with the navmesh height at each: its centroid, and when
    /// its longest edge exceeds <paramref name="splitAbove"/> three more interior points at the (½, ¼, ¼) barycentric
    /// permutations — interior on purpose, because a navmesh is authored flush against walls and an edge midpoint can
    /// stand inside one. Returns how many of <paramref name="into"/> were filled (1 or 4).</summary>
    public static int SamplePoints(Vec3 a, Vec3 b, Vec3 c, float splitAbove, Span<Vector3> into)
    {
        var va = new Vector3(a.X, a.Y, a.Z); var vb = new Vector3(b.X, b.Y, b.Z); var vc = new Vector3(c.X, c.Y, c.Z);
        into[0] = (va + vb + vc) / 3f;
        float longest = MathF.Max(Vector2.Distance(new Vector2(va.X, va.Y), new Vector2(vb.X, vb.Y)),
                        MathF.Max(Vector2.Distance(new Vector2(vb.X, vb.Y), new Vector2(vc.X, vc.Y)),
                                  Vector2.Distance(new Vector2(vc.X, vc.Y), new Vector2(va.X, va.Y))));
        if (longest <= splitAbove || into.Length < 4) return 1;
        into[1] = va * 0.5f + vb * 0.25f + vc * 0.25f;
        into[2] = va * 0.25f + vb * 0.5f + vc * 0.25f;
        into[3] = va * 0.25f + vb * 0.25f + vc * 0.5f;
        return 4;
    }

    /// <summary>Bilinear terrain height at world (x,y) from a decoded LAND heightmap (<see cref="LandHeight.Decode"/>,
    /// indexed [x, y], vertex spacing <see cref="LandHeight.VertexSpacing"/>) whose cell origin is
    /// (<paramref name="cellX"/>, <paramref name="cellY"/>) in grid units. Null outside the cell. <paramref name="saddle"/>
    /// is how far the bilinear reading can differ from a triangulated one at the point — the engine builds its terrain
    /// collision from these vertices as triangles, and which diagonal it cuts is not known here, so the reading carries
    /// its own error bound instead of a guess: the larger of the two diagonals' deviations at the point, which is
    /// |h00 + h11 − h10 − h01| times ty(1−tx) or tx(1−ty) for the 00–11 diagonal and tx·ty or (1−tx)(1−ty) for the
    /// other, a quarter of the sum at the quad's middle and zero at its corners.</summary>
    public static float? LandHeightAt(float[,] heights, int cellX, int cellY, float x, float y, out float saddle)
    {
        saddle = 0f;
        float fx = (x - cellX * 4096f) / LandHeight.VertexSpacing, fy = (y - cellY * 4096f) / LandHeight.VertexSpacing;
        if (fx < 0f || fy < 0f || fx > LandHeight.Side - 1 || fy > LandHeight.Side - 1) return null;
        int ix = Math.Min((int)MathF.Floor(fx), LandHeight.Side - 2), iy = Math.Min((int)MathF.Floor(fy), LandHeight.Side - 2);
        float tx = fx - ix, ty = fy - iy;
        float h00 = heights[ix, iy], h10 = heights[ix + 1, iy], h01 = heights[ix, iy + 1], h11 = heights[ix + 1, iy + 1];
        float s = MathF.Abs(h00 + h11 - h10 - h01);
        float d1 = tx >= ty ? ty * (1f - tx) : tx * (1f - ty);
        float d2 = tx + ty <= 1f ? tx * ty : (1f - tx) * (1f - ty);
        saddle = s * MathF.Max(d1, d2);
        return h00 * (1 - tx) * (1 - ty) + h10 * tx * (1 - ty) + h01 * (1 - tx) * ty + h11 * tx * ty;
    }

    /// <summary>One triangle's reading from its samples: the median offset over the samples that found a floor, found
    /// when at least half did, else the most common reason among the rest.</summary>
    public static (bool Found, float Offset, NoFloorReason Reason, int FoundSamples, int Samples, bool AnyEmbedded, bool AnyLand)
        Reduce(ReadOnlySpan<FloorReading> readings)
    {
        int n = readings.Length, found = 0; bool embedded = false, land = false;
        Span<float> offs = stackalloc float[Math.Max(n, 1)];
        Span<int> reasons = stackalloc int[8];
        foreach (var r in readings)
        {
            if (r.Found) { offs[found++] = r.Offset; if (r.Embedded) embedded = true; if (r.Source == FloorSource.Land) land = true; }
            else reasons[(int)r.Reason]++;
        }
        if (n == 0) return (false, float.NaN, NoFloorReason.NoSurface, 0, 0, false, false);
        if (found * 2 < n)
        {
            int best = 1;
            for (int k = 2; k < reasons.Length; k++) if (reasons[k] > reasons[best]) best = k;
            return (false, float.NaN, (NoFloorReason)best, found, n, embedded, land);
        }
        var s = offs[..found]; s.Sort();
        float med = found % 2 == 1 ? s[found / 2] : (s[found / 2 - 1] + s[found / 2]) * 0.5f;
        return (true, med, NoFloorReason.Found, found, n, embedded, land);
    }

    /// <summary>Quantiles of a sample, by linear interpolation; NaN on an empty sample. <paramref name="values"/> is
    /// sorted in place.</summary>
    public static float Quantile(List<float> values, float q)
    {
        if (values.Count == 0) return float.NaN;
        values.Sort();
        float pos = q * (values.Count - 1);
        int i = (int)MathF.Floor(pos);
        if (i + 1 >= values.Count) return values[^1];
        return values[i] + (values[i + 1] - values[i]) * (pos - i);
    }

    /// <summary>Cluster heights into STOREYS: sorted, a new storey wherever consecutive heights are further apart than
    /// <paramref name="tolerance"/> — the route rule's own clustering (RouteConnectivity.Measure step 2). Returns the
    /// storey index of every input, in input order, and the storey count.</summary>
    public static (int[] Storey, int Count) Storeys(IReadOnlyList<float> heights, float tolerance = DefaultMaxDepth)
    {
        int n = heights.Count;
        var order = new int[n];
        for (int k = 0; k < n; k++) order[k] = k;
        Array.Sort(order, (p, q) => heights[p].CompareTo(heights[q]));
        var storey = new int[n];
        int s = 0;
        for (int k = 0; k < n; k++)
        {
            if (k > 0 && heights[order[k]] - heights[order[k - 1]] > tolerance) s++;
            storey[order[k]] = s;
        }
        return (storey, n == 0 ? 0 : s + 1);
    }
}

/// <summary>The floor under one cell's navmesh, asked point by point: the placed solids that can be a floor (a world box
/// each for the broad phase, a column index built on first use from the caller's actor-solid soup), the terrain by grid
/// cell, and <see cref="NavmeshFloor.Read"/> over the column they present. One per cell — an exterior's with its 3×3
/// neighbourhood, because a floor mesh placed in the next cell reaches under this one (measured 2026-09-05: read from
/// the cell's own refs alone, the floor under 3.7 % of exterior triangles is lost and another 4 % falls to the terrain
/// under a neighbour's street). The route rule asks <see cref="FloorAt"/> for every raster cell it stands an actor on.</summary>
public sealed class FloorField
{
    sealed class Candidate
    {
        public Candidate(FormKey id, Aabb world, Func<MeshColumnIndex?> build) { Id = id; World = world; _columns = new(build); }
        public readonly FormKey Id; public readonly Aabb World;
        readonly Lazy<MeshColumnIndex?> _columns;
        public MeshColumnIndex? Columns => _columns.Value;
    }

    const float Bucket = 256f;
    readonly List<Candidate> _cands = new();
    readonly Dictionary<(int, int), List<int>> _grid = new();
    readonly Dictionary<(int, int), float[,]> _land = new();
    // One read's scratch, per thread: a field takes concurrent reads once built.
    [ThreadStatic] static List<(float Z, bool Up)>? t_surfaces;

    /// <param name="maxDepth">How far below the navmesh a floor may lie (<see cref="NavmeshFloor.DefaultMaxDepth"/>).</param>
    /// <param name="upReach">How far above the navmesh a floor may stand (<see cref="NavmeshFloor.DefaultUpReach"/>).</param>
    /// <param name="fallbackOffset">What <see cref="FloorAt"/> assumes where no floor is within reach: the navmesh minus
    /// this. A caller measuring a band that was itself measured at a known offset passes that offset, so an unread floor
    /// reproduces the band exactly as measured; every such read is counted in <see cref="Fallbacks"/>.</param>
    public FloorField(float maxDepth = NavmeshFloor.DefaultMaxDepth, float upReach = NavmeshFloor.DefaultUpReach, float fallbackOffset = 0f)
    { MaxDepth = maxDepth; UpReach = upReach; FallbackOffset = fallbackOffset; }

    public float MaxDepth { get; }
    public float UpReach { get; }
    public float FallbackOffset { get; }
    public long Reads, Found, Fallbacks, LandFloors;
    /// <summary>Refs the caller's enable resolution kept out of the candidates (a switch child disabled at load).</summary>
    public int SkippedDisabled;
    public int Candidates => _cands.Count;
    public int LandCells => _land.Count;

    /// <summary>The offsets (navmesh − floor) the field's reads found, in 1 u bins from −64 to +64 (the ends hold the
    /// tails), so a caller can say which floor its band actually stood on — a median and a spread per cell, not a
    /// promise. Fallbacks are not in it.</summary>
    readonly int[] _hist = new int[129];
    public IReadOnlyList<int> OffsetHistogram => _hist;

    /// <summary>A quantile of the offsets found, from the histogram (to the unit); NaN before any floor was found.</summary>
    public float OffsetQuantile(float q) => Quantile(_hist, q);

    public static float Quantile(IReadOnlyList<int> hist, float q)
    {
        long n = 0; foreach (var c in hist) n += c;
        if (n == 0) return float.NaN;
        long target = (long)MathF.Ceiling(q * n); if (target < 1) target = 1;
        long acc = 0;
        for (int b = 0; b < hist.Count; b++) { acc += hist[b]; if (acc >= target) return b - 64; }
        return hist.Count - 1 - 64;
    }

    /// <summary>A LAND heightmap (<see cref="LandHeight.Decode"/>) for the grid cell at <paramref name="grid"/>.</summary>
    public void AddLand((int X, int Y) grid, float[,] heights) => _land[grid] = heights;

    /// <summary>A placed solid: its world AABB, and how to build its world-space column index when a read first needs it.
    /// <paramref name="id"/> lets a read exclude the object itself (the floor a walker stands on when it MEETS an object is
    /// never that object's own top).</summary>
    public void Add(FormKey id, Aabb world, Func<MeshColumnIndex?> build)
    {
        if (world.IsEmpty) return;
        int idx = _cands.Count;
        _cands.Add(new Candidate(id, world, build));
        int bx0 = (int)MathF.Floor(world.Min.X / Bucket), bx1 = (int)MathF.Floor(world.Max.X / Bucket);
        int by0 = (int)MathF.Floor(world.Min.Y / Bucket), by1 = (int)MathF.Floor(world.Max.Y / Bucket);
        for (int bx = bx0; bx <= bx1; bx++)
            for (int by = by0; by <= by1; by++)
            {
                if (!_grid.TryGetValue((bx, by), out var list)) _grid[(bx, by)] = list = new List<int>();
                list.Add(idx);
            }
    }

    /// <summary>The floor under the navmesh point (x, y, navZ). <paramref name="landSaddle"/> is the terrain reading's
    /// own error bound at the point (<see cref="NavmeshFloor.LandHeightAt"/>), 0 where the terrain was not asked.</summary>
    public FloorReading Read(float x, float y, float navZ, out float landSaddle, FormKey? exclude = null)
    {
        Interlocked.Increment(ref Reads);
        var surfaces = t_surfaces ??= new();
        surfaces.Clear();
        landSaddle = 0f;
        if (_grid.TryGetValue(((int)MathF.Floor(x / Bucket), (int)MathF.Floor(y / Bucket)), out var list))
            foreach (int idx in list)
            {
                var c = _cands[idx];
                if (exclude is { } ex && c.Id == ex) continue;
                if (x < c.World.Min.X || x > c.World.Max.X || y < c.World.Min.Y || y > c.World.Max.Y) continue;
                if (c.World.Max.Z < navZ - MaxDepth || c.World.Min.Z > navZ + UpReach) continue;
                c.Columns?.SurfacesAt(x, y, surfaces);
            }
        float? landZ = null;
        if (_land.Count > 0)
        {
            var g = ((int)MathF.Floor(x / 4096f), (int)MathF.Floor(y / 4096f));
            if (_land.TryGetValue(g, out var h)) landZ = NavmeshFloor.LandHeightAt(h, g.Item1, g.Item2, x, y, out landSaddle);
        }
        var r = NavmeshFloor.Read(navZ, surfaces, landZ, MaxDepth, UpReach);
        if (r.Found)
        {
            Interlocked.Increment(ref Found); if (r.Source == FloorSource.Land) Interlocked.Increment(ref LandFloors);
            Interlocked.Increment(ref _hist[Math.Clamp((int)MathF.Round(r.Offset) + 64, 0, 128)]);
        }
        return r;
    }

    public FloorReading Read(float x, float y, float navZ) => Read(x, y, navZ, out _);

    /// <summary>The physical floor height the band is measured from at a navmesh point — the reading's floor, or the
    /// navmesh minus <see cref="FallbackOffset"/> where none is within reach (counted). The delegate
    /// <c>RouteConnectivity.Measure</c> takes as <c>floorAt</c>.</summary>
    public float FloorAt(float x, float y, float navZ)
    {
        var r = Read(x, y, navZ, out _);
        if (r.Found) return r.FloorZ;
        Interlocked.Increment(ref Fallbacks);
        return navZ - FallbackOffset;
    }

    /// <summary>Which placed candidate supplies an up-facing surface at (x, y) within <paramref name="tolerance"/> of
    /// <paramref name="z"/> — the object a reading's floor came from — or null when none does (the terrain, or nothing).
    /// A diagnostic for the probes: a floor that reads a whole storey above the navmesh is only actionable once the
    /// object laying it is named. Does not count as a read.</summary>
    public FormKey? Supplier(float x, float y, float z, float tolerance = 0.5f)
    {
        if (!_grid.TryGetValue(((int)MathF.Floor(x / Bucket), (int)MathF.Floor(y / Bucket)), out var list)) return null;
        FormKey? best = null; float bestGap = float.MaxValue;
        var surfaces = new List<(float Z, bool Up)>();
        foreach (int idx in list)
        {
            var c = _cands[idx];
            if (x < c.World.Min.X || x > c.World.Max.X || y < c.World.Min.Y || y > c.World.Max.Y) continue;
            if (c.World.Max.Z < z - tolerance || c.World.Min.Z > z + tolerance) continue;
            surfaces.Clear();
            c.Columns?.SurfacesAt(x, y, surfaces);
            foreach (var (sz, up) in surfaces)
            {
                if (!up) continue;
                float gap = MathF.Abs(sz - z);
                if (gap <= tolerance && gap < bestGap) { bestGap = gap; best = c.Id; }
            }
        }
        return best;
    }
}
