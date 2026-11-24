using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

// ======================================================================
//  RouteConnectivity — the ROUTE-LEVEL question (roadmap 4.7, Part B),
//  and the corridor's PATH LINE (4.7's closing step, the rule Part C
//  returned).
//
//  Milestone 4 counted a reference as blocking when ANY navmesh triangle
//  under it was severed. Piranha's framing (2026-09-02): the unit of
//  blockage is a route, not a triangle. So this rasterises the walkable
//  ground in a WINDOW around the object, marks where the object's solid
//  stands in the actor column, and asks the with-vs-without question:
//  which boundary-to-boundary connections that exist without the object
//  survive with it — and at what clearance.
//
//  Part C (56 arms in the lab, the same day) showed the engine does not
//  take the detour the raster finds: a commanded walker stalls against a
//  full-width obstacle 5 of 5, walks through whenever its own path line is
//  clear by its capsule 10 of 10, and with its line cut and a passage open
//  beside the obstacle gets through 12 of 22 whatever the passage's width.
//  So the raster stays as the machinery and a second question is asked of
//  it: the corridor's NATURAL PATH LINE — the ends of the ground on the
//  window's boundary, pathfound over the record's own navmesh links
//  (NavGraph) without the object and pulled taut — and whether the object's
//  solid cuts that line at capsule width inside the actor band; and, where
//  it does, the widest free lane beside it (the passage). A window with
//  more than two ends (a T, a crossing, a plaza) is measured on every pair
//  of ends and the worst line is reported: an actor crossing a T walks the
//  hall whichever arm it takes, and the Ratway station Part C measured IS
//  such a T within the window's reach. The clearance ladder is still
//  measured and reported; the rule no longer ranks on it.
//
//  Pure measurement. It knows nothing about BLOCKED / LIKELY-BLOCKED /
//  CLIPPED; that rule lives in DetectorModel over the facts returned here.
//  Every threshold — the window margin, the raster step, the actor band,
//  the ladder, the capsule radius, the passage scan — is a parameter.
// ======================================================================

/// <summary>Why a window has, or has not, a path line to test. Everything but <see cref="Line"/>,
/// <see cref="JunctionLine"/> and <see cref="Supplied"/> is "no line" and is reported, never read as clear.</summary>
public enum LineStatus
{
    /// <summary>Not asked: the object touches no walkable ground on this storey, so there is nothing to cut.</summary>
    NotAsked,
    /// <summary>Two ends on the window boundary, pathfound and pulled taut: a corridor, the case Part C measured.</summary>
    Line,
    /// <summary>Three or more ends (a T, a crossing, a plaza): every pair of ends was pathfound and the WORST line
    /// is reported — the one the object cuts with the narrowest passage, else any uncut one. A generalisation past
    /// what Part C measured, counted apart so a driver can say how much rests on it.</summary>
    JunctionLine,
    /// <summary>The caller supplied the line (the commanded corridor line of an in-game arm).</summary>
    Supplied,
    /// <summary>The ground the object stands on reaches the boundary nowhere an actor could enter.</summary>
    Island,
    /// <summary>Ground all round the window, or along more than half of its edge in one run: open ground, or a room
    /// or hall larger than the window — no ends.</summary>
    OpenGround,
    /// <summary>One end: the corridor stops inside the window.</summary>
    DeadEnd,
    /// <summary>More ends than the pairwise measurement is willing to take (a plaza with many entrances).</summary>
    TooManyEnds,
    /// <summary>An end's cell centre is on no drawable triangle of the graph — a raster/record disagreement.</summary>
    EndOffMesh,
    /// <summary>The raster connects the ends but the record's own links connect no pair of them.</summary>
    NoPath,
}

/// <summary>The corridor's natural path line through the window and what the object does to it.</summary>
/// <param name="Ends">Boundary runs wide enough for the capsule, on the component the object stands in.</param>
/// <param name="Pairs">Pairs of ends that the record's links connect (1 for a corridor); <paramref name="PairsCut"/>
/// how many of their lines the object cuts.</param>
/// <param name="Points">The taut line in plan, first end to second (the worst pair's, at a junction); empty when
/// there is no line.</param>
/// <param name="Cut">The object's solid meets the capsule disc swept along the line inside the actor band.</param>
/// <param name="CutFrom">Distance along the line (units) where the sweep first meets the object; <paramref name="CutTo"/> where it last does.</param>
/// <param name="Length">The line's plan length.</param>
/// <param name="Passage">The widest lane an actor can take PAST the object between the line's two ends with the object in
/// place: the largest lane at which the ends stay connected through free ground whose nearest non-ground is at least
/// half that lane away — the bottleneck of the best way round, not the width beside the object (a city gate piece with
/// open ground beside it read that ground as its passage when it was the width beside). Lane values are the raster's own, 2 × step × √d²;
/// 0 when the object severs the ends' ground inside the window; <paramref name="PassageCap"/> means the lane out-ran the
/// distance scan (wider than that, not measured). NaN when the line is not cut.</param>
/// <param name="CutRise">How far the object's world box rises above the floor at the cut — the max over the cut samples of
/// (box top − the floor under the sample: the physical floor when the caller gave <c>floorAt</c>, else the navmesh). What
/// a walker has to get over: when it lies inside the step band (<c>ActorBand.PhysicalStep</c>,
/// <c>ActorBand.PhysicalStepBandTop</c>] the route-cut verdict says so (Q23, 2026-09-05; above the feet since Q24). NaN
/// when the line is not cut. A box top, not the object's true crest at the cut — coarse on a sloped or stepped object,
/// exact on a beam or a block.</param>
/// <param name="CutFloat">How far the object's lowest material within the capsule's footprint stands above the floor along
/// the cut — the max over the cut samples of (the lowest surface the object presents within the capsule radius of the
/// sample, at or above the floor there, − that floor): the floor-bottom term (Q25 measured, Q26 folded, 2026-09-05). An
/// object resting on the floor reads 0 whatever its top; a beam lifted whole reads its underside; a rail reads its
/// underside wherever its posts are out of the capsule's reach. The worst along the cut, as the rise is: a composite object
/// grounded at one end of the sweep and floating at the other reads as floating — the walker meets the floating part from
/// one side. When it reaches <c>DetectorModel.FloatWall</c> the route-cut verdict calls the object a wall to the controller
/// at any rise and the step band does not apply. NaN when the line is not cut.</param>
/// <param name="PassageLeft">The widest free lane on the cross-sections through the object, per side of travel, at the
/// narrowest section — a diagnostic beside the passage, because Part C's key observation was WHICH side the engine
/// tries; counted only over ground connected to the ends.</param>
/// <param name="CutOnStairs">Every triangle contacting the selected line within its actor band passed the caller's
/// stairs-material predicate, including the contacts at the worst rise. Mixed or unread contacts do not grant the
/// exception; stairs elsewhere on the object do not count. The box-based rise and floor-bottom measurements stay unchanged.</param>
public sealed record RouteLine(
    LineStatus Status, string Why, int Ends, int Pairs, int PairsCut,
    Vector2[] Points,
    bool Cut, float CutFrom, float CutTo, float Length,
    float Passage, float PassageLeft, float PassageRight, float PassageCap,
    float CutRise,
    float CutFloat,
    float CapsuleRadius, bool CutOnStairs = false)
{
    public bool Found => Status is LineStatus.Line or LineStatus.JunctionLine or LineStatus.Supplied;
    public static RouteLine None(LineStatus status, string why, int ends, float capsuleRadius) =>
        new(status, why, ends, 0, 0, Array.Empty<Vector2>(), false, float.NaN, float.NaN, 0f, float.NaN, float.NaN, float.NaN, 0f, float.NaN, float.NaN, capsuleRadius);
}

/// <summary>The storey raster a measurement ran on, kept only when the caller asks (<c>keepRaster</c>) so the
/// measurement can be SEEN — the window, the ground, the object's cells, the perimeter runs, the ends and the line —
/// rather than trusted from its numbers. <see cref="Ascii"/> draws it, rows from the top (+Y) down.</summary>
public sealed record RouteRaster(int Nx, int Ny, float MinX, float MinY, float Step, byte[] State,
                                 IReadOnlyList<IReadOnlyList<int>> Runs, IReadOnlyList<Vector2> Ends, Vector2[] Line)
{
    /// <summary>'.' outside, '#' ground, 'X' ground the object stands in, 'r' a perimeter run cell, 'E' an end,
    /// '=' the line over ground, '%' the line over the object.</summary>
    public string Ascii()
    {
        var g = new char[Nx * Ny];
        for (int k = 0; k < g.Length; k++) g[k] = State[k] == 0 ? '.' : State[k] == 1 ? '#' : 'X';
        foreach (var run in Runs) foreach (var k in run) g[k] = 'r';
        for (int s = 0; s + 1 < Line.Length; s++)
        {
            var p = Line[s]; var q = Line[s + 1]; var d = q - p; float len = d.Length();
            if (len < 1e-3f) continue;
            for (float t = 0; t <= len; t += Step * 0.5f)
            {
                var pt = p + d * (t / len);
                int i = (int)MathF.Floor((pt.X - MinX) / Step), j = (int)MathF.Floor((pt.Y - MinY) / Step);
                if (i < 0 || j < 0 || i >= Nx || j >= Ny) continue;
                int k = j * Nx + i;
                g[k] = State[k] == 2 ? '%' : '=';
            }
        }
        foreach (var e in Ends)
        {
            int i = (int)MathF.Floor((e.X - MinX) / Step), j = (int)MathF.Floor((e.Y - MinY) / Step);
            if (i >= 0 && j >= 0 && i < Nx && j < Ny) g[j * Nx + i] = 'E';
        }
        var sb = new System.Text.StringBuilder();
        for (int j = Ny - 1; j >= 0; j--)
        {
            sb.Append($"{MinY + (j + 0.5f) * Step,8:0} ");
            for (int i = 0; i < Nx; i++) sb.Append(g[j * Nx + i]);
            sb.Append('\n');
        }
        sb.Append($"         x from {MinX + 0.5f * Step:0} to {MinX + (Nx - 0.5f) * Step:0}, {Step:0} u per cell\n");
        return sb.ToString();
    }
}

/// <summary>What a route measurement found. <paramref name="Judged"/> false means the window could not be
/// measured (no walkable ground, no boundary to connect, a raster too small to resolve a corridor) — NOT the same
/// as "clear".</summary>
/// <param name="Touched">Raster cells of walkable ground the object's solid stands in — the overlap the CLIPPED class
/// is about. Zero means the object never enters an actor column over navmesh in this window.</param>
/// <param name="SplitAt">Per clearance-ladder rung (index into <paramref name="Ladder"/>; rung 0 is clearance 0 — the
/// raw raster), whether the object DISCONNECTS boundary cells that were connected without it. Rung 0 true is the
/// blocked route.</param>
/// <param name="SplitAtWithout">The same test WITHOUT the object at each rung — the corridor's own clearance, so a
/// corridor that was already narrower than a rung is not blamed on the object.</param>
/// <param name="BoundaryGroups">How many boundary-touching walkable components the window had without the object;
/// fewer than one means there was no route to test.</param>
/// <param name="Storeys">How many distinct ground levels the window held within the object's vertical span; the
/// facts reported are the WORST storey's (a bridge is judged on its deck and on the street it spans, and the one
/// it blocks wins). <paramref name="StoreyZ"/> is that storey's ground height.</param>
/// <param name="Line">The corridor's path line and the cut test on it (the route-cut rule's facts); null when the
/// window was not judged.</param>
/// <param name="Raster">The storey raster, when the caller asked to keep it.</param>
public sealed record RouteFacts(
    bool Judged, string Why, int Touched, int Walkable, int Free,
    bool[] SplitAt, bool[] SplitAtWithout, float[] Ladder, int BoundaryGroups,
    float Step, int Nx, int Ny, float BandBottom, float BandTop, int Storeys = 0, float StoreyZ = 0f,
    RouteLine? Line = null, RouteRaster? Raster = null)
{
    /// <summary>The largest ladder clearance at which the object leaves every without-object connection intact, or
    /// −1 when it splits one even at clearance 0 (rung 0).</summary>
    public int MaxIntactRung { get { for (int k = 0; k < SplitAt.Length; k++) if (SplitAt[k]) return k - 1; return SplitAt.Length - 1; } }
    public int MaxIntactRungWithout { get { for (int k = 0; k < SplitAtWithout.Length; k++) if (SplitAtWithout[k]) return k - 1; return SplitAtWithout.Length - 1; } }
}

public static class RouteConnectivity
{
    /// <summary>The walker's collision radius in plan (u): Part C's walker stopped 20-29 u short of a face, re-measured
    /// 2026-09-05 at 26-28 u (see <c>capsuleRadius</c> on <see cref="Measure"/>). One name for the route rule and the
    /// anchor body test (G1 build list row 2).</summary>
    public const float DefaultCapsuleRadius = 22f;

    const byte Outside = 0, FreeCell = 1, BlockedCell = 2;

    /// <summary>Measure one object against the walkable ground around it.</summary>
    /// <param name="navs">The cell's winning navmesh, indexed.</param>
    /// <param name="obj">The object's SOLID triangles in world space (the collision soup, transformed).</param>
    /// <param name="objectBox">The object's world AABB — the window is this, grown by <paramref name="margin"/> in plan.</param>
    /// <param name="margin">How far past the object's box the window reaches. Must exceed the widest ladder rung, or a
    /// detour that hugs the window edge cannot be seen.</param>
    /// <param name="step">Raster cell size in units. Coarsened (doubled) when the window would exceed
    /// <paramref name="maxCells"/>, and the step used is reported.</param>
    /// <param name="clearance">Bottom of the actor band (the step height) above the floor the band is measured from: the
    /// navmesh surface, or the physical floor <paramref name="floorAt"/> gives under each raster cell.</param>
    /// <param name="height">Top of the actor band above that floor (the head the character controller actually ducks —
    /// see ActorBand.ControllerFactor; Part C measured a 132 u Nord under 120 u and not 100 u).</param>
    /// <param name="ladder">Clearance rungs in units, ascending; rung 0 must be 0. A rung c means "an actor c wide" —
    /// a cell passes when every cell within c/2 of it is free ground.</param>
    /// <param name="storeyTolerance">Ground heights within this of each other belong to one STOREY. Every navmesh
    /// triangle whose height at a cell lies within the object's vertical span (grown by this) is a candidate; the
    /// candidates are clustered into storeys and each storey is measured on its own — a bridge is judged on its deck
    /// and on the street beneath, a city wall whose collision reaches 800 u below the street is judged on the
    /// street. The worst storey's facts are returned.</param>
    /// <param name="capsuleRadius">The walker's collision radius in plan — Part C's walker stopped 20–29 u short of a
    /// face, so 22 u; re-measured 2026-09-05 at 26–28 u under every avoidance setting tried (fAvoidNodeRadiusAdd 0/44,
    /// fDefaultStaticAvoidNodeRadius 0/128, fAvoidanceDistanceRadiusMult 0), so the stop is the collision's and not an
    /// avoid node's. The disc swept along the path line; also the narrowest boundary run that counts as an end.</param>
    /// <param name="graph">The cell's <see cref="NavGraph"/> nodes, built once per cell by the caller; built here from
    /// <paramref name="navs"/> when null (and needed).</param>
    /// <param name="lineOverride">A caller-supplied path line in plan (an in-game arm's commanded corridor line) —
    /// the cut and passage are then measured on it instead of the pathfound line.</param>
    /// <param name="keepRaster">Keep the storey raster on the facts, for a caller that wants to draw it.</param>
    /// <param name="maxEnds">The most ends a window may have before the pairwise measurement declines it.</param>
    /// <param name="floorAt">The PHYSICAL floor under a raster cell — (x, y, navmesh z) → the height the walker's feet
    /// rest on there (FloorField.FloorAt; Q24, 2026-09-05). When given, the band, the capsule slab and the cut rise are
    /// measured from it instead of the navmesh, which floats 0–20 u above the collision floor within one corridor and
    /// about 10 u at the station the band was measured on. Null keeps every run before that date byte for byte: the
    /// navmesh is the floor. The storeys and the graph lookups stay on the navmesh either way.</param>
    /// <param name="isStairsMaterial">Optional caller-owned material classification. A cut gets the stairs fact only
    /// when every triangle contacting its selected line passes this predicate; unknown or mixed contact stays conservative.</param>
    public static RouteFacts Measure(NavmeshIndex navs, MeshColumnIndex obj, Aabb objectBox,
                                     float margin, float step, float clearance, float height, float[] ladder,
                                     float storeyTolerance = 128f, int maxCells = 16384, int deletedFlag = 0x08,
                                     float capsuleRadius = DefaultCapsuleRadius,
                                     IReadOnlyDictionary<NavGraph.TriRef, NavGraph.Node>? graph = null,
                                     Vector2[]? lineOverride = null, bool keepRaster = false, int maxEnds = 8,
                                     Func<float, float, float, float>? floorAt = null, Func<string?, bool>? isStairsMaterial = null)
    {
        if (objectBox.IsEmpty) return Unjudged("object box is empty", ladder, step);
        if (ladder.Length == 0 || ladder[0] != 0f) throw new ArgumentException("the clearance ladder must start at 0", nameof(ladder));

        float minX = objectBox.Min.X - margin, maxX = objectBox.Max.X + margin;
        float minY = objectBox.Min.Y - margin, maxY = objectBox.Max.Y + margin;
        int nx, ny;
        while (true)
        {
            nx = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / step));
            ny = Math.Max(1, (int)MathF.Ceiling((maxY - minY) / step));
            if ((long)nx * ny <= maxCells) break;
            step *= 2f;
        }
        if (nx < 4 || ny < 4) return Unjudged("window too small to resolve a corridor", ladder, step);

        // ---- 1. the ground: every navmesh height under every cell, within the object's REACH ----
        // An object standing in the actor column over a floor at z touches the band z + clearance .. z + height, so
        // the floors it can affect at all lie in [box.Min.Z − height, box.Max.Z − clearance]. The probe is that range
        // grown by a storey either side, so ground the object cannot reach (the street under a bridge deck) is still
        // measured and comes back JUDGED with nothing touched — "None", not "no ground".
        float probeLo = objectBox.Min.Z - height - storeyTolerance, probeHi = objectBox.Max.Z - clearance + storeyTolerance;
        var candidates = new List<float>[nx * ny];
        var allZ = new List<float>();
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                float cx = minX + (i + 0.5f) * step, cy = minY + (j + 0.5f) * step;
                var probe = new Aabb(new Vec3(cx - 0.5f, cy - 0.5f, probeLo), new Vec3(cx + 0.5f, cy + 0.5f, probeHi), false);
                List<float>? zs = null;
                foreach (var hit in navs.Query(probe))
                {
                    if ((hit.Flags & deletedFlag) != 0) continue;
                    var nav = navs.NavAt(hit.NavIndex);
                    var tri = nav.Triangles[hit.TriangleIndex];
                    var a = nav.Vertices[tri.A]; var b = nav.Vertices[tri.B]; var c = nav.Vertices[tri.C];
                    if (HeightAt(a, b, c, cx, cy) is not { } z) continue;
                    if (z < probeLo || z > probeHi) continue;
                    (zs ??= new List<float>()).Add(z);
                    allZ.Add(z);
                }
                candidates[j * nx + i] = zs!;
            }
        if (allZ.Count < 4) return Unjudged("no walkable ground in the window", ladder, step);

        // ---- 2. storeys: cluster the heights, then measure each and keep the worst ----
        allZ.Sort();
        var storeys = new List<(float Lo, float Hi)>();
        float lo = allZ[0], hi = allZ[0];
        for (int k = 1; k < allZ.Count; k++)
        {
            if (allZ[k] - hi <= storeyTolerance) { hi = allZ[k]; continue; }
            storeys.Add((lo, hi)); lo = hi = allZ[k];
        }
        storeys.Add((lo, hi));

        // The graph is built at most once per call, and only when a storey needs a line.
        IReadOnlyDictionary<NavGraph.TriRef, NavGraph.Node>? nodes = graph;
        IReadOnlyDictionary<NavGraph.TriRef, NavGraph.Node> GraphOf() => nodes ??= NavGraph.Build(navs).Nodes;

        RouteFacts? worst = null;
        foreach (var (sLo, sHi) in storeys)
        {
            float mid = (sLo + sHi) * 0.5f;
            var facts = MeasureStorey(obj, candidates, nx, ny, minX, minY, step, clearance, height, objectBox.Max.Z, ladder, sLo, sHi, mid,
                                      capsuleRadius, GraphOf, lineOverride, keepRaster, maxEnds, floorAt, isStairsMaterial);
            facts = facts with { Storeys = storeys.Count, StoreyZ = mid };
            if (worst is null || Outranks(facts, worst)) worst = facts;
        }
        return worst!;
    }

    // The storey that matters is the one the object does something to. A cut line outranks an uncut one, and among
    // cut storeys the narrower passage wins; a storey with no line outranks one where the object touches nothing; an
    // unjudgeable storey never masks a judged one. The ladder's severity breaks the remaining ties.
    static bool Outranks(RouteFacts a, RouteFacts b)
    {
        var (ca, sa) = Severity(a); var (cb, sb) = Severity(b);
        return ca != cb ? ca > cb : sa > sb;
    }

    static (int Cut, float Secondary) Severity(RouteFacts f)
    {
        if (!f.Judged) return (-1, 0f);
        if (f.Touched == 0) return (0, 0f);
        float ladderSev = 1 + (f.Ladder.Length - 1 - f.MaxIntactRung);
        return f.Line is not { } l ? (1, ladderSev)
             : !l.Found ? (1, ladderSev)
             : !l.Cut ? (2, ladderSev)
             : (3, -l.Passage);
    }

    static RouteFacts MeasureStorey(MeshColumnIndex obj, List<float>[] candidates, int nx, int ny, float minX, float minY,
                                    float step, float clearance, float height, float objectTop, float[] ladder, float sLo, float sHi, float mid,
                                    float capsuleRadius, Func<IReadOnlyDictionary<NavGraph.TriRef, NavGraph.Node>> graphOf,
                                    Vector2[]? lineOverride, bool keepRaster, int maxEnds, Func<float, float, float, float>? floorAt, Func<string?, bool>? isStairsMaterial)
    {
        var state = new byte[nx * ny];
        var floorZ = new float[nx * ny];
        int walkable = 0;
        for (int k = 0; k < candidates.Length; k++)
        {
            var zs = candidates[k];
            if (zs is null) continue;
            float best = float.NaN, bestD = float.MaxValue;
            foreach (var z in zs)
            {
                if (z < sLo - 1e-3f || z > sHi + 1e-3f) continue;
                float d = MathF.Abs(z - mid);
                if (d < bestD) { bestD = d; best = z; }
            }
            if (float.IsNaN(best)) continue;
            state[k] = FreeCell; floorZ[k] = best; walkable++;
        }
        if (walkable < 4) return Unjudged("no walkable ground on this storey", ladder, step);

        // The floor the band stands on: the navmesh (floorZ), or the physical floor under each ground cell when the caller
        // gave one — read at the cell's centre, on the storey's own navmesh height. floorZ keeps the navmesh for the storey
        // bookkeeping and the graph lookups; physZ is what the actor column, the capsule slab and the cut rise use.
        var physZ = floorZ;
        if (floorAt is not null)
        {
            physZ = new float[nx * ny];
            for (int k = 0; k < state.Length; k++)
                if (state[k] == FreeCell) physZ[k] = floorAt(minX + (k % nx + 0.5f) * step, minY + (k / nx + 0.5f) * step, floorZ[k]);
        }

        // ---- 3. the object: mark the cells whose actor column it stands in ----
        int touched = 0;
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                int k = j * nx + i;
                if (state[k] != FreeCell) continue;
                float x0 = minX + i * step, y0 = minY + j * step;
                var col = new Aabb(new Vec3(x0, y0, physZ[k] + clearance), new Vec3(x0 + step, y0 + step, physZ[k] + height), false);
                if (obj.BlocksColumn(col)) { state[k] = BlockedCell; touched++; }
            }

        // ---- 4. with vs without, per clearance rung ----
        // "Without" is the walkable set (Free ∪ Blocked); "with" is Free only. At rung c a cell passes when every
        // cell within c/2 (in plan) is ground of the arm being tested — the actor's width, not just its centre line.
        var walkableMask = new bool[nx * ny];
        var freeMask = new bool[nx * ny];
        for (int k = 0; k < state.Length; k++) { walkableMask[k] = state[k] != Outside; freeMask[k] = state[k] == FreeCell; }

        var labelsWithout0 = Label(walkableMask, nx, ny);
        int boundaryGroups = BoundaryGroups(labelsWithout0, nx, ny);
        if (boundaryGroups < 1) return Unjudged("no walkable ground reaches the window boundary", ladder, step);

        // One distance scan per arm serves every rung: a cell passes rung c when no non-ground cell lies within c/2.
        float maxRadius = ladder[^1] / 2f;
        int rcMax = (int)MathF.Ceiling(maxRadius / step);
        var distFree = DistanceToNonGround(freeMask, nx, ny, rcMax);
        var distWalkable = DistanceToNonGround(walkableMask, nx, ny, rcMax);

        var splitAt = new bool[ladder.Length];
        var splitAtWithout = new bool[ladder.Length];
        for (int r = 0; r < ladder.Length; r++)
        {
            var withLabels = Label(Threshold(distFree, ladder[r] / 2f, step), nx, ny);
            splitAt[r] = Splits(labelsWithout0, withLabels, nx, ny);
            var withoutLabels = Label(Threshold(distWalkable, ladder[r] / 2f, step), nx, ny);
            splitAtWithout[r] = Splits(labelsWithout0, withoutLabels, nx, ny);
        }

        // ---- 5. the corridor's natural path line, and whether the object cuts it ----
        var runs = new List<List<int>>(); var ends = new List<Vector2>();
        var line = touched == 0
            ? RouteLine.None(LineStatus.NotAsked, "the object touches no walkable ground on this storey", 0, capsuleRadius)
            : MeasureLine(obj, state, floorZ, physZ, labelsWithout0, freeMask, distFree, rcMax, nx, ny, minX, minY, step, clearance, height, objectTop,
                          capsuleRadius, graphOf, lineOverride, maxEnds, runs, ends, isStairsMaterial);

        int free = 0; foreach (var f in freeMask) if (f) free++;
        var raster = keepRaster ? new RouteRaster(nx, ny, minX, minY, step, state, runs, ends, line.Points) : null;
        return new RouteFacts(true, "", touched, walkable, free, splitAt, splitAtWithout, ladder, boundaryGroups,
                              step, nx, ny, clearance, height, Line: line, Raster: raster);
    }

    /// <summary>One line's sweep: whether the object cuts it, where, and the passage beside the object.</summary>
    readonly record struct Sweep(bool Cut, float CutFrom, float CutTo, float Length, float Passage, float Left, float Right, float CutRise, float CutFloat, bool CutOnStairs = false);

    /// <summary>The path line(s): the ends of the object's ground on the window boundary, pathfound over the record's
    /// links without the object and pulled taut (every pair at a junction; or the caller's line); the capsule sweep
    /// along each inside the band; the worst reported, with the passage beside the object where it is cut.</summary>
    static RouteLine MeasureLine(MeshColumnIndex obj, byte[] state, float[] floorZ, float[] physZ, int[] labels, bool[] freeMask,
                                 int[] distFree, int rcMax, int nx, int ny, float minX, float minY, float step,
                                 float clearance, float height, float objectTop, float r,
                                 Func<IReadOnlyDictionary<NavGraph.TriRef, NavGraph.Node>> graphOf,
                                 Vector2[]? lineOverride, int maxEnds, List<List<int>> runsOut, List<Vector2> endsOut, Func<string?, bool>? isStairsMaterial)
    {
        int far = (rcMax + 1) * (rcMax + 1);
        float cap = 2f * (rcMax + 1) * step;
        // The lane values the raster can produce, ascending: 2 × step × √d² for every squared cell distance the free
        // ground actually shows, the cap last. A labelling per value is the price of the bottleneck.
        var d2Values = new SortedSet<int>();
        for (int k = 0; k < freeMask.Length; k++) if (freeMask[k] && distFree[k] > 0) d2Values.Add(Math.Min(distFree[k], far));

        var candidates = new List<Vector2[]>();
        LineStatus status; string why = ""; int ends;
        if (lineOverride is { Length: >= 2 })
        {
            candidates.Add(lineOverride); status = LineStatus.Supplied; why = "line supplied by the caller"; ends = 2;
        }
        else
        {
            // (a) the component the object stands in — the one holding most of its touched cells. Blocked cells are
            // walkable without the object, so they carry a label.
            var touchedBy = new Dictionary<int, int>();
            for (int k = 0; k < state.Length; k++)
                if (state[k] == BlockedCell && labels[k] != 0) { touchedBy.TryGetValue(labels[k], out var n); touchedBy[labels[k]] = n + 1; }
            int comp = 0, compN = -1;
            foreach (var (lab, n) in touchedBy) if (n > compN) { comp = lab; compN = n; }

            // (b) that component's ground on the window's perimeter, as runs. A run narrower than the capsule is not a
            // place an actor enters; the whole perimeter as one run is open ground.
            var runs = PerimeterRuns(labels, comp, nx, ny, out int perimeter);
            runsOut.AddRange(runs);
            int minRun = Math.Max(1, (int)MathF.Ceiling(2f * r / step));
            var wide = runs.Where(run => run.Count >= minRun).ToList();
            if (runs.Count == 1 && runs[0].Count == perimeter)
                return RouteLine.None(LineStatus.OpenGround, "ground all round the window: no ends", 0, r);
            if (runs.Count == 0)
                return RouteLine.None(LineStatus.Island, "the ground the object stands on reaches no window edge", 0, r);
            if (wide.Count == 0)
                return RouteLine.None(LineStatus.Island, $"the ground the object stands on reaches the window edge only through {runs.Count} run(s) narrower than the capsule", 0, r);
            if (wide.Count == 1)
                return wide[0].Count * 2 > perimeter
                    ? RouteLine.None(LineStatus.OpenGround, $"ground along {100 * wide[0].Count / perimeter} % of the window's edge in one run: a room or field larger than the window, no ends", 1, r)
                    : RouteLine.None(LineStatus.DeadEnd, "one end: the corridor stops inside the window", 1, r);
            if (wide.Count > maxEnds)
                return RouteLine.None(LineStatus.TooManyEnds, $"{wide.Count} ends: more entrances than the pairwise measurement takes ({maxEnds})", wide.Count, r);
            ends = wide.Count;

            // (c) the ends are the runs' middles — the midpoint of the two middle cells when a run has an even count,
            // so the end does not depend on which way the perimeter walk passed the run; a line is the record's own
            // walk between two of them: the one pair of a corridor, every pair of a junction.
            var mids = wide.Select(RunMiddle).ToList();
            foreach (var (ex, ey, _) in mids) endsOut.Add(new Vector2(ex, ey));
            var nodes = graphOf();
            var tris = mids.Select(m => NavGraph.TriangleAt(nodes, m.X, m.Y, m.Z, edgeTolerance: 1e-3f)).ToList();
            int offMesh = tris.Count(t => t is null), noPath = 0;
            for (int a = 0; a < mids.Count; a++)
                for (int b = a + 1; b < mids.Count; b++)
                {
                    if (tris[a] is not { } ta || tris[b] is not { } tb) continue;
                    var taut = NavGraph.TautPath(nodes, ta.Tri, tb.Tri, new Vector2(mids[a].X, mids[a].Y), new Vector2(mids[b].X, mids[b].Y));
                    if (taut is null) { noPath++; continue; }
                    candidates.Add(taut.Corners.ToArray());
                }
            if (candidates.Count == 0)
                return offMesh > 0 && noPath == 0
                    ? RouteLine.None(LineStatus.EndOffMesh, $"{offMesh} end(s) on no drawable triangle", ends, r)
                    : RouteLine.None(LineStatus.NoPath, $"the raster connects the {ends} ends but the record's links connect no pair of them ({noPath} pair(s) tried)", ends, r);
            status = ends == 2 ? LineStatus.Line : LineStatus.JunctionLine;
            if (ends > 2) why = $"{ends} ends, {candidates.Count} pair(s) connected: the worst line is reported";
        }

        // (d) sweep every candidate line; keep the worst — cut with the narrowest passage, else any uncut one.
        var freeLabels = Label(freeMask, nx, ny);
        Vector2[]? bestPts = null; Sweep best = default; int pairsCut = 0;
        foreach (var pts in candidates)
        {
            var sw = SweepLine(pts);
            if (sw.Cut) pairsCut++;
            bool better = bestPts is null || (sw.Cut && (!best.Cut || sw.Passage < best.Passage));
            if (better) { bestPts = pts; best = sw; }
        }
        return new RouteLine(status, why, ends, candidates.Count, pairsCut, bestPts!, best.Cut, best.CutFrom, best.CutTo, best.Length,
                             best.Passage, best.Left, best.Right, cap, best.CutRise, best.CutFloat, r, best.CutOnStairs);

        // The sweep: a disc of the capsule radius every few units along the line, inside the band over the floor the
        // raster read under that point. Samples off the raster or off the ground are skipped — the object's own cells
        // are inside the window by construction, so a cut cannot hide there. Then the passage: across the corridor at
        // every cut sample, the widest free lane on either side of the line; the narrowest such section over the cut
        // stretch is the passage. The distance transform is asked per cell, only for the cells the sections visit.
        Sweep SweepLine(Vector2[] pts)
        {
            const float sampleStep = 4f;
            float length = 0f;
            bool cut = false; bool cutOnStairs = true; float cutFrom = float.NaN, cutTo = float.NaN, cutRise = float.NaN, cutFloat = float.NaN;
            var cutSamples = new List<(Vector2 P, Vector2 Dir)>();
            for (int s = 0; s + 1 < pts.Length; s++)
            {
                var p = pts[s]; var q = pts[s + 1];
                var d = q - p; float len = d.Length();
                if (len < 1e-3f) continue;
                var dir = d / len;
                for (float t = 0f; ; t += sampleStep)
                {
                    bool last = t >= len;
                    if (last) t = len;
                    var pt = p + dir * t;
                    int k = CellAt(pt, minX, minY, step, nx, ny);
                    if (k >= 0 && state[k] != Outside)
                    {
                        float fz = physZ[k];   // the floor the band stands on here: the navmesh, or the physical floor
                        if (obj.IntersectsDiscInSlab(pt.X, pt.Y, r, fz + clearance, fz + height, isStairsMaterial, out bool onStairs))
                        {
                            if (!cut) { cut = true; cutFrom = length + t; }
                            cutTo = length + t;
                            cutSamples.Add((pt, dir));
                            cutOnStairs &= onStairs;
                            // the object's box top above the floor under this cut sample — the max over the cut: what the
                            // walker has to get over (Q23: the step band the verdict names); above the physical floor when
                            // the caller gave one (Q24), else above the navmesh
                            float rise = objectTop - fz;
                            if (float.IsNaN(cutRise) || rise > cutRise) cutRise = rise;
                            // the floor-bottom term (Q26): the object's lowest material within the capsule's footprint at or above
                            // the floor here — the max over the cut, the worst contact along it, as the rise is the worst rise
                            float lowest = obj.LowestSurfaceInDisc(pt.X, pt.Y, r, fz);
                            if (!float.IsNaN(lowest)) { float fl = lowest - fz; if (float.IsNaN(cutFloat) || fl > cutFloat) cutFloat = fl; }
                        }
                    }
                    if (last) break;
                }
                length += len;
            }
            if (!cut) return new Sweep(false, float.NaN, float.NaN, length, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN);

            // The passage: the bottleneck of the best way round. At lane L the passable ground is the free cells whose
            // nearest non-ground is at least L/2 away (≥, so a lane exactly L wide passes — the erosion ladder's strict
            // test is the other convention, and the two must not be confused); the passage is the largest L at which
            // the line's two ends — the first and last such cells along the line — share a component. Not connected
            // even on the raw free ground means the object severs the ends' ground inside the window: 0, whatever lies
            // beside it. A lane that is still connected at the scan's cap reads as the cap. A line whose ends are off
            // the raster (a supplied line) is measured from its first and last on-raster cells.
            float passage = 0f; int through = 0; bool severed = true;
            foreach (int t2 in new[] { 0 }.Concat(d2Values))
            {
                var m = t2 == 0 ? freeMask : Threshold(distFree, t2);
                var lab = Label(m, nx, ny);
                int la = FirstLabelAlong(m, lab, pts, forward: true), lb = FirstLabelAlong(m, lab, pts, forward: false);
                if (la == 0 || lb == 0 || la != lb) break;
                severed = false;
                if (t2 == 0) through = la;
                else passage = t2 >= far ? cap : 2f * step * MathF.Sqrt(t2);
            }
            if (severed) return new Sweep(true, cutFrom, cutTo, length, 0f, 0f, 0f, cutRise, cutFloat, cutOnStairs);

            // Per side, the widest lane on the cross-sections through the object, over ground connected to the ends —
            // the diagnostic beside the number.
            float left = float.NaN, right = float.NaN, atObject = float.PositiveInfinity;
            int stride = Math.Max(1, (int)MathF.Round(step / sampleStep));
            for (int s = 0; s < cutSamples.Count; s += stride)
            {
                var (pt, dir) = cutSamples[s];
                var perp = new Vector2(dir.Y, -dir.X);                  // to the RIGHT of travel
                float bestR = 0f, bestL = 0f;
                for (float t = 0f; ; t += step)
                {
                    int k = CellAt(pt + perp * t, minX, minY, step, nx, ny);
                    if (k < 0) break;
                    if (freeMask[k] && freeLabels[k] == through) bestR = MathF.Max(bestR, Lane(k));
                }
                for (float t = step; ; t += step)
                {
                    int k = CellAt(pt - perp * t, minX, minY, step, nx, ny);
                    if (k < 0) break;
                    if (freeMask[k] && freeLabels[k] == through) bestL = MathF.Max(bestL, Lane(k));
                }
                float lane = MathF.Max(bestL, bestR);
                if (lane < atObject) { atObject = lane; left = bestL; right = bestR; }
            }
            if (float.IsPositiveInfinity(atObject)) { left = right = 0f; }
            return new Sweep(true, cutFrom, cutTo, length, passage, left, right, cutRise, cutFloat, cutOnStairs);
        }

        // The label of the first cell of `mask` met walking the line from its start (forward) or its end (backward);
        // 0 when the line meets none before leaving the raster.
        int FirstLabelAlong(bool[] mask, int[] lab, Vector2[] line, bool forward)
        {
            int n = line.Length;
            for (int s = 0; s + 1 < n; s++)
            {
                var p = forward ? line[s] : line[n - 1 - s]; var q = forward ? line[s + 1] : line[n - 2 - s];
                var d = q - p; float len = d.Length();
                if (len < 1e-3f) continue;
                var dir = d / len;
                for (float t = 0f; t <= len; t += step * 0.5f)
                {
                    int k = CellAt(p + dir * t, minX, minY, step, nx, ny);
                    if (k >= 0 && mask[k]) return lab[k];
                }
            }
            return 0;
        }

        // The free cells whose squared cell distance to non-ground is at least t2 — the ≥ convention (see above).
        bool[] Threshold(int[] d2, int t2)
        {
            var m = new bool[d2.Length];
            for (int k = 0; k < d2.Length; k++) m[k] = d2[k] >= t2;
            return m;
        }

        (float X, float Y, float Z) RunMiddle(List<int> run)
        {
            int ka = run[(run.Count - 1) / 2], kb = run[run.Count / 2];
            return (minX + ((ka % nx) + (kb % nx) + 1f) * 0.5f * step,
                    minY + ((ka / nx) + (kb / nx) + 1f) * 0.5f * step,
                    (floorZ[ka] + floorZ[kb]) * 0.5f);
        }

        float Lane(int k) => distFree[k] >= far ? cap : 2f * MathF.Sqrt(distFree[k]) * step;
    }

    static int CellAt(Vector2 p, float minX, float minY, float step, int nx, int ny)
    {
        int i = (int)MathF.Floor((p.X - minX) / step), j = (int)MathF.Floor((p.Y - minY) / step);
        return i < 0 || j < 0 || i >= nx || j >= ny ? -1 : j * nx + i;
    }

    /// <summary>The window's perimeter cells walked once round (bottom row left to right, right column up, top row
    /// right to left, left column down), cut into maximal cyclic runs of cells carrying <paramref name="label"/>. A
    /// run is a list of cell indices in walk order. <paramref name="perimeter"/> is the walk's length.</summary>
    public static List<List<int>> PerimeterRuns(int[] labels, int label, int nx, int ny, out int perimeter)
    {
        var walk = new List<int>();
        for (int i = 0; i < nx; i++) walk.Add(i);
        for (int j = 1; j < ny; j++) walk.Add(j * nx + nx - 1);
        for (int i = nx - 2; i >= 0; i--) walk.Add((ny - 1) * nx + i);
        for (int j = ny - 2; j >= 1; j--) walk.Add(j * nx);
        perimeter = walk.Count;
        var runs = new List<List<int>>();
        if (label == 0) return runs;
        int start = walk.FindIndex(k => labels[k] != label);
        if (start < 0) { runs.Add(new List<int>(walk)); return runs; }          // ground all the way round
        List<int>? run = null;
        for (int n = 0; n < walk.Count; n++)
        {
            int k = walk[(start + n) % walk.Count];
            if (labels[k] == label) (run ??= new List<int>()).Add(k);
            else if (run is not null) { runs.Add(run); run = null; }
        }
        if (run is not null) runs.Add(run);
        return runs;
    }

    static RouteFacts Unjudged(string why, float[] ladder, float step) =>
        new(false, why, 0, 0, 0, new bool[ladder.Length], new bool[ladder.Length], ladder, 0, step, 0, 0, 0, 0);

    /// <summary>Height of the triangle at (x,y), or null outside its plan projection or when it is edge-on.</summary>
    public static float? HeightAt(Vec3 a, Vec3 b, Vec3 c, float x, float y)
    {
        float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        if (MathF.Abs(d) < 1e-6f) return null;
        float u = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / d;
        float v = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / d;
        float w = 1f - u - v;
        if (u < -1e-4f || v < -1e-4f || w < -1e-4f) return null;
        return u * a.Z + v * b.Z + w * c.Z;
    }

    /// <summary>For every set cell, the SQUARED distance in cells to the nearest cell that is not set, scanning a window
    /// of <paramref name="maxRadiusCells"/>; a set cell with no unset neighbour inside the window gets a value past
    /// the window (it passes every rung). Unset cells get −1. Past the window edge counts as ground: the window is a
    /// crop, not a wall. One scan serves every ladder rung — <see cref="Threshold"/> turns it into the eroded mask.</summary>
    public static int[] DistanceToNonGround(bool[] mask, int nx, int ny, int maxRadiusCells)
    {
        var d2 = new int[nx * ny];
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
                d2[j * nx + i] = DistanceToNonGroundAt(mask, nx, ny, i, j, maxRadiusCells);
        return d2;
    }

    /// <summary>One cell of <see cref="DistanceToNonGround"/>, for a caller that needs a few cells at a wider radius
    /// than a whole-raster scan is worth (the passage's cross-sections).</summary>
    public static int DistanceToNonGroundAt(bool[] mask, int nx, int ny, int i, int j, int maxRadiusCells)
    {
        int far = (maxRadiusCells + 1) * (maxRadiusCells + 1);
        if (!mask[j * nx + i]) return -1;
        int best = far;
        for (int dj = -maxRadiusCells; dj <= maxRadiusCells; dj++)
        {
            int jj = j + dj;
            if (jj < 0 || jj >= ny) continue;
            int dj2 = dj * dj;
            if (dj2 >= best) continue;
            for (int di = -maxRadiusCells; di <= maxRadiusCells; di++)
            {
                int ii = i + di;
                if (ii < 0 || ii >= nx) continue;
                int dd = di * di + dj2;
                if (dd >= best) continue;
                if (!mask[jj * nx + ii]) best = dd;
            }
        }
        return best;
    }

    /// <summary>The eroded mask at one radius: a cell passes when its nearest non-ground cell is FARTHER than
    /// <paramref name="radius"/> (in units). Radius 0 keeps every set cell.</summary>
    public static bool[] Threshold(int[] d2, float radius, float step)
    {
        float r2 = (radius / step) * (radius / step);
        var outMask = new bool[d2.Length];
        for (int k = 0; k < d2.Length; k++) outMask[k] = d2[k] >= 0 && d2[k] > r2 + 1e-6f;
        return outMask;
    }

    /// <summary>A cell survives erosion by <paramref name="radius"/> units when every cell whose centre lies within
    /// that radius of its centre is set. Radius 0 is the identity. Convenience over the distance scan.</summary>
    public static bool[] Erode(bool[] mask, int nx, int ny, float radius, float step)
    {
        if (radius <= 0f) return (bool[])mask.Clone();
        return Threshold(DistanceToNonGround(mask, nx, ny, (int)MathF.Ceiling(radius / step)), radius, step);
    }

    /// <summary>4-connected component labels (1-based; 0 = not set).</summary>
    public static int[] Label(bool[] mask, int nx, int ny)
    {
        var lab = new int[nx * ny];
        var stack = new Stack<int>();
        int next = 0;
        for (int s = 0; s < mask.Length; s++)
        {
            if (!mask[s] || lab[s] != 0) continue;
            next++;
            lab[s] = next; stack.Push(s);
            while (stack.Count > 0)
            {
                int k = stack.Pop();
                int i = k % nx, j = k / nx;
                if (i > 0) Push(k - 1);
                if (i < nx - 1) Push(k + 1);
                if (j > 0) Push(k - nx);
                if (j < ny - 1) Push(k + nx);
            }
        }
        return lab;
        void Push(int k2) { if (mask[k2] && lab[k2] == 0) { lab[k2] = next; stack.Push(k2); } }
    }

    static bool IsBoundary(int i, int j, int nx, int ny) => i == 0 || j == 0 || i == nx - 1 || j == ny - 1;

    static int BoundaryGroups(int[] labels, int nx, int ny)
    {
        var seen = new HashSet<int>();
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
                if (IsBoundary(i, j, nx, ny) && labels[j * nx + i] != 0) seen.Add(labels[j * nx + i]);
        return seen.Count;
    }

    /// <summary>Does the second labelling DISCONNECT boundary cells the first connects? For every component of the
    /// reference labelling that reaches the boundary, its surviving boundary cells must all carry ONE label in the
    /// test labelling — two labels means the object (or the erosion) cut that component in two between two places an
    /// actor could enter the window from. A component whose boundary cells ALL vanished has no route left at that
    /// clearance and counts as split; one that merely lost some of its entrances (an object flush against the window
    /// edge) does not — removing ground is not severing a route.</summary>
    public static bool Splits(int[] reference, int[] test, int nx, int ny)
    {
        var first = new Dictionary<int, int>();
        var reachBoundary = new HashSet<int>();
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                if (!IsBoundary(i, j, nx, ny)) continue;
                int k = j * nx + i;
                int r = reference[k], t = test[k];
                if (r == 0) continue;
                reachBoundary.Add(r);
                if (t == 0) continue;
                if (first.TryGetValue(r, out var seenT)) { if (seenT != t) return true; }
                else first[r] = t;
            }
        foreach (var r in reachBoundary) if (!first.ContainsKey(r)) return true;
        return false;
    }
}
