using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

// ======================================================================
//  SpatialOverlap.Contact — the OBJECT-OBJECT NARROW PHASE (geometry-deconfliction
//  milestone 6; roadmap 4.5 / 4.6's contact test — one primitive, both jobs).
//
//  Two world-space triangle soups in; out: how far apart they are (0 when they
//  touch or cross), how deep one sits inside the other, and the host surface
//  normal at the contact. Pure geometry — "supported", "attached", "stranded"
//  are judgements and live in the harness's DetectorModel, where a proof owns
//  them. Every tolerance is a PARAMETER.
//
//  Why a soup-to-soup test and not a box test: the broad phase (ToWorld's
//  rotated AABB) says two rowboats moored side by side overlap; they do not
//  touch. An apple on a table and an apple 5 u above it have the same box
//  relation; only the surfaces tell them apart.
// ======================================================================

/// <summary>What a contact query found. <paramref name="Distance"/> is the least distance between any triangle of the
/// dependent and any triangle of the host — exactly 0 when a pair touches or crosses; capped at the query's
/// <c>maxDistance</c> when nothing is closer (so "nothing within reach" reads as the cap, never as infinity).
/// <paramref name="Penetration"/> is how far the dependent has gone through the host where the two TOUCH: over the
/// vertices of every dependent triangle within the contact band, the deepest one measured along the contact normal —
/// the vector from the nearest host surface point back to the vertex, projected on <paramref name="Normal"/> and
/// negated — 0 when no touching triangle has a vertex behind the contact surface. Contact-local and winding-free on
/// purpose: an open collision shell (most of Skyrim's kit pieces are single surfaces; 94 % of compressed-mesh bodies
/// have open edges) has no inside, so "behind the nearest face" cannot tell a bowl sunk into a floor from a barrel
/// standing under the storey above, and a single inverted face misreads — a face the dependent crosses, read along the
/// normal the contact as a whole presents, can and does not. <paramref name="EnclosedDepth"/> is the older reading,
/// kept and reported: the distance from the deepest dependent vertex to the nearest host face when the vertex is behind
/// every host face at that distance — meaningful only for a closed, outward-wound host (<paramref name="HostClosed"/>),
/// where it is the one signal for a dependent swallowed whole without touching a surface.
/// <paramref name="Normal"/> is the host's surface normal where the two meet — the area-weighted sum, over dependent
/// triangles whose nearest host triangle is within the band, of that host triangle's unit normal — and when nothing is
/// within the band, the normal of the single nearest host triangle (so a floating object still says what it floats
/// above). Zero when the host is out of reach.</summary>
public readonly record struct SoupContact(
    float Distance, float Penetration, Vector3 Normal,
    int ContactPairs, int PenetratingVertices, int DependentTriangles, int HostTriangles,
    float EnclosedDepth = 0f, int EnclosedVertices = 0, bool HostClosed = false, float FacingDistance = float.PositiveInfinity,
    int CrossedFaces = 0)
{
    /// <summary>Some dependent edge passes through the interior of a host face — the two surfaces cross, as distinct
    /// from touching. <paramref name="CrossedFaces"/> counts the host faces crossed.</summary>
    public bool Crosses => CrossedFaces > 0;
    /// <summary>Any dependent triangle FACING a host triangle within the contact band — the closest pair of points
    /// between them lies along the host's normal (a bottom over a top, a back against a wall, a face crossed), not
    /// beside it. Two objects standing side by side on a floor have coplanar bottoms within the band of each other and
    /// are not in contact; the facing test is what says so, and <paramref name="FacingDistance"/> is the least distance
    /// among such pairs (the reach cap when there are none) — the number a "touching" rule should read.</summary>
    public bool InContact => ContactPairs > 0;
    /// <summary>The penetration a caller should read: for a closed host the enclosed reading, which there is the exact
    /// least way out (and the one signal for a dependent swallowed whole); for an open shell the contact-local one, the
    /// only reading a surface without an inside can give.</summary>
    public float EffectivePenetration => HostClosed ? EnclosedDepth : Penetration;
    /// <summary>Cosine of the angle between the contact normal and +Z: 1 = a floor-like face under the dependent, 0 = a
    /// wall-like face beside it, −1 = a ceiling above it. NaN when there is no normal to read.</summary>
    public float NormalUp
    {
        get
        {
            float len = Normal.Length();
            return len < 1e-6f ? float.NaN : Normal.Z / len;
        }
    }
    public static readonly SoupContact OutOfReach = new(float.PositiveInfinity, 0f, Vector3.Zero, 0, 0, 0, 0);
}

public static partial class SpatialOverlap
{
    /// <summary>A model-space soup placed into the world by a reference's transform — the same composition
    /// <see cref="ToWorld"/> uses for a box (<see cref="RefrRotation"/>, then scale, then translation), applied to
    /// every vertex. Indices are shared with the model soup; only the vertex array is new.</summary>
    public static TriangleSoup TransformSoup(TriangleSoup model, Vec3 position, Vec3 rotation, float scale)
    {
        var r = RefrRotation(rotation);
        var t = new Vector3(position.X, position.Y, position.Z);
        var w = new Vector3[model.Vertices.Length];
        for (int i = 0; i < w.Length; i++) w[i] = Vector3.Transform(model.Vertices[i] * scale, r) + t;
        // a radius scales with its geometry; materials are not carried here (unchanged)
        float[]? rr = null;
        if (model.TriangleRadii is { } tr) { rr = new float[tr.Length]; for (int i = 0; i < tr.Length; i++) rr[i] = tr[i] * scale; }
        return new TriangleSoup(w, model.Indices, null, rr);
    }

    /// <summary>The bounds of a soup's vertices. Empty for an empty soup.</summary>
    public static Aabb SoupBounds(TriangleSoup soup)
    {
        var b = Aabb.Empty;
        foreach (var v in soup.Vertices) b = b.Add(v);
        return b;
    }

    /// <summary>Distance between two axis-aligned boxes — 0 when they overlap or touch. A lower bound on the distance
    /// between anything inside them, so a pair whose boxes are further apart than a threshold needs no surface test.</summary>
    public static float BoxDistance(Aabb a, Aabb b)
    {
        if (a.IsEmpty || b.IsEmpty) return float.PositiveInfinity;
        float dx = MathF.Max(0f, MathF.Max(a.Min.X - b.Max.X, b.Min.X - a.Max.X));
        float dy = MathF.Max(0f, MathF.Max(a.Min.Y - b.Max.Y, b.Min.Y - a.Max.Y));
        float dz = MathF.Max(0f, MathF.Max(a.Min.Z - b.Max.Z, b.Min.Z - a.Max.Z));
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>The contact between a DEPENDENT soup and a HOST soup, both already in world space. Asymmetric on
    /// purpose: penetration is read for the dependent's vertices against the host's surface, and the normal is the
    /// host's — "what is this thing resting on or hanging from" is a question about the host's faces.
    ///
    /// <para>Broad phase: the host's triangles in a 3-D uniform hash (<paramref name="cell"/> u; the
    /// <see cref="MeshColumnIndex"/> pattern with a third axis). Narrow phase, exact: for every dependent triangle the
    /// nearest host triangle within reach — six vertex-to-triangle and nine edge-to-edge closest points, and a
    /// segment-through-triangle test for the pair that crosses without any vertex or edge touching. For every dependent
    /// vertex the nearest host triangle within reach and which side of it the vertex is on.</para>
    ///
    /// <param name="maxDistance">Reach. Host triangles further than this from a dependent triangle are never visited;
    /// a dependent with nothing within reach reports <c>Distance = maxDistance</c>.</param>
    /// <param name="contactBand">A dependent triangle whose nearest host triangle is within this counts as a contact
    /// pair and contributes its host's normal. The rule's "touching" tolerance belongs to the caller; this only decides
    /// which faces the reported normal is read from.</param></summary>
    public static SoupContact Contact(TriangleSoup dependent, TriangleSoup host, float maxDistance, float contactBand = 4f, float cell = 32f, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (dependent.TriangleCount == 0 || host.TriangleCount == 0) return SoupContact.OutOfReach with { DependentTriangles = dependent.TriangleCount, HostTriangles = host.TriangleCount };
        var grid = TriangleGrid.Build(host, cell);
        return Contact(dependent, grid, maxDistance, contactBand, cancellationToken);
    }

    /// <summary>As <see cref="Contact(TriangleSoup, TriangleSoup, float, float, float)"/> over a host already indexed —
    /// for a host tested against many dependents.</summary>
    public static SoupContact Contact(TriangleSoup dependent, TriangleGrid host, float maxDistance, float contactBand = 4f, CancellationToken cancellationToken = default)
        => Contact(dependent, host, maxDistance, contactBand, cancellationToken, null);

    /// <summary>The same contact with opt-in step timers (<paramref name="timer"/>; null reads no clock). A separate overload, so the
    /// signature above stays as it was for binaries compiled against it.</summary>
    public static SoupContact Contact(TriangleSoup dependent, TriangleGrid host, float maxDistance, float contactBand, CancellationToken cancellationToken,
                                      ContactStepTimer? timer)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tm = timer;
        long tc0 = tm is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp(), t0 = tc0;
        if (tm is not null) { tm.Calls++; tm.DependentTriangles += dependent.TriangleCount; tm.HostTriangles += host.TriangleCount; }
        if (dependent.TriangleCount == 0 || host.TriangleCount == 0)
        {
            if (tm is not null) { tm.EmptyCalls++; tm.CallTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tc0; }
            return SoupContact.OutOfReach with { DependentTriangles = dependent.TriangleCount, HostTriangles = host.TriangleCount };
        }

        var dv = dependent.Vertices;
        var (best, facing, normal, nearestNormal, contactPairs, touchingVerts, crossedFaces) =
            SurfaceSteps(dependent, host, maxDistance, contactBand, cancellationToken, tm, ref t0);
        // ---- 3. penetration: the vertices of touching triangles, along the contact normal, to the nearest surface ----
        // A vertex cannot be further inside than the dependent is large, so the search reach is the dependent's own
        // extent (capped by the query's). The nearest surface point needs no winding; the normal is the contact's.
        var db = SoupBounds(dependent);
        float depReach = MathF.Min(maxDistance, MathF.Max(db.Max.X - db.Min.X, MathF.Max(db.Max.Y - db.Min.Y, db.Max.Z - db.Min.Z)));
        float deepest = 0f; int through = 0;
        float nlen = normal.Length();
        if (contactPairs > 0 && nlen > 1e-6f)
        {
            var nc = normal / nlen;
            foreach (var v in touchingVerts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (tm is not null) tm.PenetrationQueries++;
                var (d, q, _) = host.NearestPoint(dv[v], depReach);
                if (float.IsNaN(d)) continue;
                float depth = -Vector3.Dot(dv[v] - q, nc);
                if (depth <= 1e-3f) continue;
                // A vertex that went THROUGH the surface has the surface straight behind it along the normal: its
                // nearest host point is at about the depth. One whose nearest point is off to the side (a crate's top
                // corner beside a bottle it merely touches lower down) is beside the host, not through it.
                if (d > depth * 1.1f + 0.5f) continue;
                through++;
                if (depth > deepest) deepest = depth;
            }
        }

        if (tm is not null) { long now = System.Diagnostics.Stopwatch.GetTimestamp(); tm.PenetrationTicks += now - t0; t0 = now; }
        // ---- 4. the enclosed reading, per dependent vertex — the rule's number for a closed host, reported for an open one ----
        float enclosed = 0f; int inside = 0;
        var deepNormal = Vector3.Zero;
        for (int v = 0; v < dv.Length; v++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (depth, n) = host.InsideDepth(dv[v], maxDistance);
            if (depth <= 0f) continue;
            inside++;
            if (depth > enclosed) { enclosed = depth; deepNormal = n; }
        }

        if (tm is not null) { long now = System.Diagnostics.Stopwatch.GetTimestamp(); tm.EnclosedTicks += now - t0; tm.EnclosedQueries += dv.Length; tm.CallTicks += now - tc0; }
        if (contactPairs == 0) normal = inside > 0 ? deepNormal : nearestNormal;
        return new SoupContact(best, deepest, normal, contactPairs, through, dependent.TriangleCount, host.TriangleCount, enclosed, inside, host.IsClosed, facing, crossedFaces.Count);
    }

    /// <summary>Steps 1 and 2 of <see cref="Contact(TriangleSoup, TriangleGrid, float, float, CancellationToken, ContactStepTimer)"/>, moved here
    /// so that <see cref="ContactFacing"/> runs the very same statements: the least distance, the facing gap, the contact
    /// normal, the nearest triangle's normal, the contact pairs, and the touching vertices and crossed faces step 3 reads.
    /// With <paramref name="facingOnly"/> only the facing gap of the result is meaningful.</summary>
    static (float Best, float Facing, Vector3 Normal, Vector3 NearestNormal, int ContactPairs, HashSet<int> TouchingVerts, HashSet<int> CrossedFaces)
        SurfaceSteps(TriangleSoup dependent, TriangleGrid host, float maxDistance, float contactBand, CancellationToken cancellationToken,
                     ContactStepTimer? tm, ref long t0, bool facingOnly = false)
    {
        var dv = dependent.Vertices; var di = dependent.Indices;
        float best = maxDistance;
        float facing = maxDistance;
        var normal = Vector3.Zero;
        var nearestNormal = Vector3.Zero;
        int contactPairs = 0;
        var touchingVerts = new HashSet<int>();
        var candidates = new List<FacingCandidate>();
        float facingBound = maxDistance;

        // ---- 1. surface distance, per dependent triangle; the faces within the band gathered for step 2 ----
        for (int t = 0; t < di.Length / 3; t++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var a = dv[di[t * 3]]; var b = dv[di[t * 3 + 1]]; var c = dv[di[t * 3 + 2]];
            // Reach shrinks with the best distance so far, but never below the band: every dependent triangle
            // within the band of a host face has to be found, whatever the global minimum already is.
            float reach = MathF.Max(best, contactBand);
            if (tm is not null) t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            // Once only "is any face nearer than the band" is read from this search, the first such face settles it.
            bool gateOnly = best <= contactBand && (facingOnly || best <= 0f);
            var (d, hostTri) = gateOnly ? (best, host.AnyNearer(a, b, c, reach) ? 0 : -1) : host.NearestTriangle(a, b, c, reach);
            // Facing-only calls read lower nearest, band and candidate counts: they stop early and drop what cannot change the gap.
            if (tm is not null) { tm.NearestTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; tm.NearestQueries++; }
            if (hostTri < 0) continue;
            var n = host.UnitNormal(hostTri);
            if (d < best) { best = d; nearestNormal = n; }
            if (d <= contactBand)
            {
                // Every host face this triangle FACES within the band is a candidate: the closest pair of points
                // between the two lies along the host's normal (over it, against it, or through it), not beside it —
                // so a neighbour's coplanar bottom a few units away is not a contact — and two parallel faces facing
                // the same way (two bottoms on one floor) never are. Each candidate carries the triangle's area times
                // how parallel the two faces are (plus a small floor so a face the triangle merely crosses still
                // counts when nothing is parallel): a bottom face on a table top is parallel to it and perpendicular
                // to the table's side, so a bottle sunk at a shelf's edge, whose bottom crosses the side face too,
                // still reads "resting on", not "mounted against".
                var cross = Vector3.Cross(b - a, c - a);
                float area = cross.Length() * 0.5f;
                var nd = area > 1e-9f ? cross / (2f * area) : Vector3.Zero;
                int candBefore = candidates.Count;
                if (tm is not null) t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                bool settled = host.CollectFacing(a, b, c, contactBand, nd, area, t, candidates, facingOnly, ref facingBound, cancellationToken);
                if (tm is not null) { tm.BandTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; tm.BandQueries++; tm.BandCandidates += candidates.Count - candBefore; }
                if (settled)
                {
                    // The gap is known, but a bad later triangle still has to fail as it does in the full walk.
                    for (int rest = t + 1; rest < di.Length / 3; rest++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        host.CheckQueryDomain(dv[di[rest * 3]], dv[di[rest * 3 + 1]], dv[di[rest * 3 + 2]], MathF.Max(best, contactBand));
                    }
                    break;
                }
            }
        }
        if (tm is not null) t0 = System.Diagnostics.Stopwatch.GetTimestamp();

        // ---- 2. the contact: which candidates count, and the normal they present ----
        // A face the dependent sits entirely BEHIND without crossing is not a contact — a bottle beneath a shelf
        // board whose collision is its top face only, or a bedroll under the top of a book that rests on it — unless
        // some dependent edge passes THROUGH that same face, in which case the dependent has gone through the host
        // there and its faces behind the surface are the sunk part (a bench 3 u into a table top: its sides cross the
        // top, its bottom is behind it). A touch at distance 0 is not a crossing.
        var crossedFaces = new HashSet<int>();
        foreach (var cd in candidates) if (cd.Crosses) crossedFaces.Add(cd.HostTri);
        var touchingTris = new HashSet<int>();
        foreach (var cd in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cd.Behind && !crossedFaces.Contains(cd.HostTri)) continue;
            if (cd.Dist < facing) facing = cd.Dist;
            if (facingOnly) continue;
            normal += cd.HostNormal * cd.Weight;
            if (touchingTris.Add(cd.Tri))
            {
                contactPairs++;
                touchingVerts.Add(di[cd.Tri * 3]); touchingVerts.Add(di[cd.Tri * 3 + 1]); touchingVerts.Add(di[cd.Tri * 3 + 2]);
            }
        }

        if (tm is not null) { long now = System.Diagnostics.Stopwatch.GetTimestamp(); tm.CandidateTicks += now - t0; t0 = now; }
        return (best, facing, normal, nearestNormal, contactPairs, touchingVerts, crossedFaces);
    }

    /// <summary>The facing gap alone: exactly the <see cref="SoupContact.FacingDistance"/> that
    /// <see cref="Contact(TriangleSoup, TriangleGrid, float, float, CancellationToken, ContactStepTimer)"/> returns for the same arguments, by
    /// the same steps 1-2 (<see cref="SurfaceSteps"/>), without the penetration and enclosed-depth steps whose results a caller that reads
    /// only the gap would discard (ContactIndex's radius shell). <paramref name="timer"/>: opt-in step timers; null reads no clock.</summary>
    public static float ContactFacing(TriangleSoup dependent, TriangleGrid host, float maxDistance, float contactBand, CancellationToken cancellationToken,
                                      ContactStepTimer? timer)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tm = timer;
        long tc0 = tm is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp(), t0 = tc0;
        if (tm is not null) { tm.Calls++; tm.DependentTriangles += dependent.TriangleCount; tm.HostTriangles += host.TriangleCount; }
        if (dependent.TriangleCount == 0 || host.TriangleCount == 0)
        {
            if (tm is not null) { tm.EmptyCalls++; tm.CallTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tc0; }
            return SoupContact.OutOfReach.FacingDistance;
        }
        var (_, facing, _, _, _, _, _) = SurfaceSteps(dependent, host, maxDistance, contactBand, cancellationToken, tm, ref t0, facingOnly: true);
        if (tm is not null) tm.CallTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tc0;
        return facing;
    }

    /// <summary>The closest pair of points between two triangles, with their distance: 0 and a shared point when they
    /// touch or cross, else the least of the vertex-to-triangle and edge-to-edge pairs. <paramref name="crosses"/> says an
    /// edge of one passes through the INTERIOR of the other — a crossing, as distinct from a touch at a vertex, along an
    /// edge, or face to face.</summary>
    public static float TriangleClosestPoints(Vector3 a0, Vector3 a1, Vector3 a2, Vector3 b0, Vector3 b1, Vector3 b2, out Vector3 p, out Vector3 q, out bool crosses)
        => TriangleClosestPointsUnder(a0, a1, a2, b0, b1, b2, float.PositiveInfinity, out p, out q, out crosses);

    /// <summary><see cref="TriangleClosestPoints"/> for a caller that reads a distance only when it is under a limit: the same value and
    /// points whenever the least squared distance is under <paramref name="limitSquared"/> (see <see cref="LimitSquared"/>), else above the limit.</summary>
    internal static float TriangleClosestPointsUnder(Vector3 a0, Vector3 a1, Vector3 a2, Vector3 b0, Vector3 b1, Vector3 b2, float limitSquared, out Vector3 p, out Vector3 q, out bool crosses)
    {
        crosses = false;
        if (SegmentTriangleHit(a0, a1, b0, b1, b2, out var x, out var xi)) { p = q = x; crosses = xi; return 0f; }
        if (SegmentTriangleHit(b0, b1, a0, a1, a2, out x, out xi)) { p = q = x; crosses = xi; return 0f; }
        if (SegmentTriangleHit(a1, a2, b0, b1, b2, out x, out xi)) { p = q = x; crosses = xi; return 0f; }
        if (SegmentTriangleHit(b1, b2, a0, a1, a2, out x, out xi)) { p = q = x; crosses = xi; return 0f; }
        if (SegmentTriangleHit(a2, a0, b0, b1, b2, out x, out xi)) { p = q = x; crosses = xi; return 0f; }
        if (SegmentTriangleHit(b2, b0, a0, a1, a2, out x, out xi)) { p = q = x; crosses = xi; return 0f; }
        float best = limitSquared; p = a0; q = b0;
        Vector3 c = ClosestPointOnTriangle(a0, b0, b1, b2); float d = (a0 - c).LengthSquared(); if (d < best) { best = d; p = a0; q = c; }
        c = ClosestPointOnTriangle(a1, b0, b1, b2); d = (a1 - c).LengthSquared(); if (d < best) { best = d; p = a1; q = c; }
        c = ClosestPointOnTriangle(a2, b0, b1, b2); d = (a2 - c).LengthSquared(); if (d < best) { best = d; p = a2; q = c; }
        c = ClosestPointOnTriangle(b0, a0, a1, a2); d = (b0 - c).LengthSquared(); if (d < best) { best = d; p = c; q = b0; }
        c = ClosestPointOnTriangle(b1, a0, a1, a2); d = (b1 - c).LengthSquared(); if (d < best) { best = d; p = c; q = b1; }
        c = ClosestPointOnTriangle(b2, a0, a1, a2); d = (b2 - c).LengthSquared(); if (d < best) { best = d; p = c; q = b2; }
        float slack = EdgeSlack(a0, a1, a2, b0, b1, b2);
        EdgeBox ea0 = new(a0, a1), ea1 = new(a1, a2), ea2 = new(a2, a0), eb0 = new(b0, b1), eb1 = new(b1, b2), eb2 = new(b2, b0);
        EdgePoints(ea0, eb0, slack, ref best, ref p, ref q); EdgePoints(ea0, eb1, slack, ref best, ref p, ref q); EdgePoints(ea0, eb2, slack, ref best, ref p, ref q);
        EdgePoints(ea1, eb0, slack, ref best, ref p, ref q); EdgePoints(ea1, eb1, slack, ref best, ref p, ref q); EdgePoints(ea1, eb2, slack, ref best, ref p, ref q);
        EdgePoints(ea2, eb0, slack, ref best, ref p, ref q); EdgePoints(ea2, eb1, slack, ref best, ref p, ref q); EdgePoints(ea2, eb2, slack, ref best, ref p, ref q);
        return MathF.Sqrt(best);
    }

    // One edge pair of the closest-points test, in its place in the order.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    static void EdgePoints(in EdgeBox e, in EdgeBox f, float slack, ref float best, ref Vector3 p, ref Vector3 q)
    {
        if (EdgesCannotBeat(e, f, slack, best)) return;
        float d = SegmentClosestPoints(e.P, e.Q, f.P, f.Q, out var c1, out var c2);
        if (d < best) { best = d; p = c1; q = c2; }
    }

    // A square at or above this has a root strictly above the limit; infinity where the squares would leave the normal float range.
    internal static float LimitSquared(float limit) => limit >= 1e-15f && limit < 1e18f ? limit * limit * 1.000001f : float.PositiveInfinity;

    // An edge's own box, for the test that a pair of edges cannot beat the best so far.
    readonly struct EdgeBox
    {
        public readonly Vector3 P, Q, Lo, Hi;
        public EdgeBox(Vector3 p, Vector3 q) { P = p; Q = q; Lo = Vector3.Min(p, q); Hi = Vector3.Max(p, q); }
    }

    // Rounding room for the box test: 33 units of float rounding at the largest coordinate; infinite, so nothing is skipped, off the checked range.
    static float EdgeSlack(Vector3 a0, Vector3 a1, Vector3 a2, Vector3 b0, Vector3 b1, Vector3 b2)
    {
        Vector3 w0 = Vector3.Abs(a0), w1 = Vector3.Abs(a1), w2 = Vector3.Abs(a2), w3 = Vector3.Abs(b0), w4 = Vector3.Abs(b1), w5 = Vector3.Abs(b2);
        var sum = w0 + w1 + w2 + w3 + w4 + w5;
        if (!(sum.X + sum.Y + sum.Z < 1e9f)) return float.PositiveInfinity;
        var m = Vector3.Max(Vector3.Max(Vector3.Max(w0, w1), Vector3.Max(w2, w3)), Vector3.Max(w4, w5));
        return MathF.Max(m.X, MathF.Max(m.Y, m.Z)) * 2e-6f;
    }

    // True when the pair's computed squared distance cannot be under best: both closest points are clamped to their edges, so the box gap less the rounding room bounds it.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    static bool EdgesCannotBeat(in EdgeBox e, in EdgeBox f, float slack, float best)
    {
        if (best <= 0f) return true;
        if (best <= 1e-30f) return false;
        var gap = Vector3.Max(Vector3.Max(e.Lo - f.Hi, f.Lo - e.Hi) - new Vector3(slack), Vector3.Zero);
        return gap.LengthSquared() * 0.999998f >= best;
    }

    /// <summary><paramref name="interior"/>: the segment passes through the triangle's interior with both of its ends
    /// clear of the plane — a crossing — rather than touching it at an end, at a vertex or along an edge.</summary>
    static bool SegmentTriangleHit(Vector3 p0, Vector3 p1, Vector3 a, Vector3 b, Vector3 c, out Vector3 hit, out bool interior)
    {
        hit = default; interior = false;
        var dir = p1 - p0;
        var e1 = b - a; var e2 = c - a;
        var h = Vector3.Cross(dir, e2);
        float det = Vector3.Dot(e1, h);
        if (MathF.Abs(det) < 1e-9f) return false;
        float inv = 1f / det;
        var s = p0 - a;
        float u = inv * Vector3.Dot(s, h);
        if (u < -1e-5f || u > 1f + 1e-5f) return false;
        var qv = Vector3.Cross(s, e1);
        float v = inv * Vector3.Dot(dir, qv);
        if (v < -1e-5f || u + v > 1f + 1e-5f) return false;
        float t = inv * Vector3.Dot(e2, qv);
        if (t < -1e-5f || t > 1f + 1e-5f) return false;
        hit = p0 + dir * Math.Clamp(t, 0f, 1f);
        // Strictly inside on every parameter, by a margin that is a real fraction of the segment and the face — an end
        // resting on the face (t at 0 or 1) or an edge lying along the face's rim is a touch.
        const float m = 1e-3f;
        interior = t > m && t < 1f - m && u > m && v > m && u + v < 1f - m;
        return true;
    }

    /// <summary>Squared distance between two segments and the closest points on each.</summary>
    static float SegmentClosestPoints(Vector3 p0, Vector3 p1, Vector3 q0, Vector3 q1, out Vector3 c1, out Vector3 c2)
    {
        Vector3 d1 = p1 - p0, d2 = q1 - q0, r = p0 - q0;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
        float s, t;
        const float eps = 1e-9f;
        if (a <= eps && e <= eps) { c1 = p0; c2 = q0; return r.LengthSquared(); }
        if (a <= eps) { s = 0f; t = Math.Clamp(f / e, 0f, 1f); }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= eps) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
            else
            {
                float b = Vector3.Dot(d1, d2);
                float denom = a * e - b * b;
                s = denom != 0f ? Math.Clamp((b * f - c * e) / denom, 0f, 1f) : 0f;
                t = (b * s + f) / e;
                if (t < 0f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
                else if (t > 1f) { t = 1f; s = Math.Clamp((b - c) / a, 0f, 1f); }
            }
        }
        c1 = p0 + d1 * s; c2 = q0 + d2 * t;
        return (c1 - c2).LengthSquared();
    }

    // ---- triangle-triangle geometry, written out so it can be read against the reference (Ericson, Real-Time
    //      Collision Detection §5.1.5, §5.1.9, §5.3.6) rather than trusted from a library ----

    /// <summary>Closest point on triangle (a,b,c) to p — the Voronoi-region walk.</summary>
    public static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        float denom = 1f / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    /// <summary>Squared distance between segments p0–p1 and q0–q1.</summary>
    public static float SegmentDistanceSquared(Vector3 p0, Vector3 p1, Vector3 q0, Vector3 q1)
    {
        Vector3 d1 = p1 - p0, d2 = q1 - q0, r = p0 - q0;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
        float s, t;
        const float eps = 1e-9f;
        if (a <= eps && e <= eps) return r.LengthSquared();
        if (a <= eps) { s = 0f; t = Math.Clamp(f / e, 0f, 1f); }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= eps) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
            else
            {
                float b = Vector3.Dot(d1, d2);
                float denom = a * e - b * b;
                s = denom != 0f ? Math.Clamp((b * f - c * e) / denom, 0f, 1f) : 0f;
                t = (b * s + f) / e;
                if (t < 0f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
                else if (t > 1f) { t = 1f; s = Math.Clamp((b - c) / a, 0f, 1f); }
            }
        }
        var c1 = p0 + d1 * s; var c2 = q0 + d2 * t;
        return (c1 - c2).LengthSquared();
    }

    /// <summary>Does segment p0–p1 pass through triangle (a,b,c)? Möller–Trumbore with an inclusive edge tolerance, so a
    /// segment through a vertex or along an edge counts. A segment lying IN the triangle's plane is not a crossing — that
    /// case is caught by the edge and vertex distances, which read 0 for it.</summary>
    public static bool SegmentCrossesTriangle(Vector3 p0, Vector3 p1, Vector3 a, Vector3 b, Vector3 c)
    {
        var dir = p1 - p0;
        var e1 = b - a; var e2 = c - a;
        var h = Vector3.Cross(dir, e2);
        float det = Vector3.Dot(e1, h);
        if (MathF.Abs(det) < 1e-9f) return false;
        float inv = 1f / det;
        var s = p0 - a;
        float u = inv * Vector3.Dot(s, h);
        if (u < -1e-5f || u > 1f + 1e-5f) return false;
        var q = Vector3.Cross(s, e1);
        float v = inv * Vector3.Dot(dir, q);
        if (v < -1e-5f || u + v > 1f + 1e-5f) return false;
        float t = inv * Vector3.Dot(e2, q);
        return t >= -1e-5f && t <= 1f + 1e-5f;
    }

    /// <summary>Distance between two triangles: 0 when they touch or cross, else the least of the six vertex-to-triangle
    /// and nine edge-to-edge distances. Exact.</summary>
    public static float TriangleDistance(Vector3 a0, Vector3 a1, Vector3 a2, Vector3 b0, Vector3 b1, Vector3 b2)
        => TriangleDistanceUnder(a0, a1, a2, b0, b1, b2, float.PositiveInfinity);

    /// <summary><see cref="TriangleDistance"/> for a caller that reads it only when it is under a limit: the same value whenever the least
    /// squared distance is under <paramref name="limitSquared"/> (see <see cref="LimitSquared"/>), else above the limit.</summary>
    internal static float TriangleDistanceUnder(Vector3 a0, Vector3 a1, Vector3 a2, Vector3 b0, Vector3 b1, Vector3 b2, float limitSquared)
    {
        // A crossing pair has distance 0 and may have no vertex or edge pair at 0 (two triangles through each
        // other like an X) — so the crossing test comes first.
        if (SegmentCrossesTriangle(a0, a1, b0, b1, b2) || SegmentCrossesTriangle(a1, a2, b0, b1, b2) || SegmentCrossesTriangle(a2, a0, b0, b1, b2)
         || SegmentCrossesTriangle(b0, b1, a0, a1, a2) || SegmentCrossesTriangle(b1, b2, a0, a1, a2) || SegmentCrossesTriangle(b2, b0, a0, a1, a2))
            return 0f;
        float best = limitSquared;
        float d = (a0 - ClosestPointOnTriangle(a0, b0, b1, b2)).LengthSquared(); if (d < best) best = d;
        d = (a1 - ClosestPointOnTriangle(a1, b0, b1, b2)).LengthSquared(); if (d < best) best = d;
        d = (a2 - ClosestPointOnTriangle(a2, b0, b1, b2)).LengthSquared(); if (d < best) best = d;
        d = (b0 - ClosestPointOnTriangle(b0, a0, a1, a2)).LengthSquared(); if (d < best) best = d;
        d = (b1 - ClosestPointOnTriangle(b1, a0, a1, a2)).LengthSquared(); if (d < best) best = d;
        d = (b2 - ClosestPointOnTriangle(b2, a0, a1, a2)).LengthSquared(); if (d < best) best = d;
        float slack = EdgeSlack(a0, a1, a2, b0, b1, b2);
        EdgeBox ea0 = new(a0, a1), ea1 = new(a1, a2), ea2 = new(a2, a0), eb0 = new(b0, b1), eb1 = new(b1, b2), eb2 = new(b2, b0);
        EdgeDistance(ea0, eb0, slack, ref best); EdgeDistance(ea0, eb1, slack, ref best); EdgeDistance(ea0, eb2, slack, ref best);
        EdgeDistance(ea1, eb0, slack, ref best); EdgeDistance(ea1, eb1, slack, ref best); EdgeDistance(ea1, eb2, slack, ref best);
        EdgeDistance(ea2, eb0, slack, ref best); EdgeDistance(ea2, eb1, slack, ref best); EdgeDistance(ea2, eb2, slack, ref best);
        return MathF.Sqrt(best);
    }

    // One edge pair of the distance test, in its place in the order.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    static void EdgeDistance(in EdgeBox e, in EdgeBox f, float slack, ref float best)
    {
        if (EdgesCannotBeat(e, f, slack, best)) return;
        float d = SegmentDistanceSquared(e.P, e.Q, f.P, f.Q);
        if (d < best) best = d;
    }
}

/// <summary>A 3-D uniform hash over a world-space soup's triangles, answering "which of your triangles is nearest this
/// triangle / this point, within reach" and "is this point inside you". The grid narrows; the answers are exact.</summary>
public sealed partial class TriangleGrid
{
    readonly Vector3[] _v;
    readonly int[] _i;
    readonly Vector3[] _n;            // unit normal per triangle (zero for a degenerate one)
    readonly Aabb[] _tb;              // bounds per triangle
    CellTable _cells = CellTable.Empty; // set once, while building
    Aabb _bounds = Aabb.Empty;        // bounds of every triangle
    readonly float _cell;
    readonly CellRange[] _ranges;     // cell range per triangle, for the stateless visit test
    CompleteQueryOrder? _completeOrder;

    TriangleGrid(TriangleSoup s, float cell)
    {
        _v = s.Vertices; _i = s.Indices; _cell = cell;
        int n = s.TriangleCount;
        _n = new Vector3[n]; _tb = new Aabb[n]; _ranges = new CellRange[n];
    }

    public int TriangleCount => _i.Length / 3;
    public Vector3 UnitNormal(int tri) => _n[tri];
    /// <summary>The indexed triangle's vertices, without a bucket query. Includes triangles that the grid's
    /// acceleration buckets omit; callers bound their own complete enumeration.</summary>
    public (Vector3 A, Vector3 B, Vector3 C) GetTriangle(int tri)
    {
        if ((uint)tri >= (uint)TriangleCount) throw new ArgumentOutOfRangeException(nameof(tri));
        return (_v[_i[tri * 3]], _v[_i[tri * 3 + 1]], _v[_i[tri * 3 + 2]]);
    }
    /// <summary>How wide the indexed surface is along <paramref name="unitDirection"/>: the spread (max − min) of its
    /// vertices projected on that direction, in world units. The bound a penetration reading has to respect — an interior
    /// point of a closed object is never further from its surface than half the object's width in ANY direction, so a
    /// depth larger than the whole width along the contact normal is the surface's artefact, not an embedding (the
    /// placement-contact rule's R4 fold, 2026-09-03). 0 for an empty soup.</summary>
    public float ExtentAlong(Vector3 unitDirection)
    {
        if (_v.Length == 0) return 0f;
        float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
        foreach (var v in _v) { float d = Vector3.Dot(v, unitDirection); if (d < lo) lo = d; if (d > hi) hi = d; }
        return hi - lo;
    }
    public Aabb BoundsOf(int tri) => _tb[tri];
    /// <summary>Every edge is shared by exactly two triangles — the soup is a closed surface. Vertices are welded by
    /// position (to 0.01 u) first, because a compressed-mesh chunk repeats its neighbour's vertices with its own indices
    /// and a hard edge in any export duplicates them; by index alone nearly every kit piece read open. What makes the
    /// enclosed reading sound.</summary>
    public bool IsClosed { get; private set; }
    /// <summary>The grid's approximate managed size in bytes, fixed at build: vertices, indices, normals, triangle bounds, the
    /// per-triangle cell ranges (charged at the old visit-stamp rate so cache decisions stay as they were) and the cell table with its lists. For a
    /// caller that bounds a cache of grids by bytes; object headers and bucket arrays are approximated.</summary>
    public long ApproxBytes { get; private set; }
    /// <summary>Faces stored outside the cell table to bound bucket replication. All four queries include them.</summary>
    public int OverflowTriangleCount => _completeOrder?.OverflowCount ?? 0;

    public static TriangleGrid Build(TriangleSoup soup, float cell = 32f)
    {
        if (!float.IsFinite(cell) || cell <= 0f)
            throw new ArgumentOutOfRangeException(nameof(cell), "triangle_grid_cell_must_be_finite_positive");
        var g = new TriangleGrid(soup, cell);
        g.InitializeGeometry();
        var overflow = new List<int>();
        for (int t = 0; t < g.TriangleCount; t++)
        {
            var (x0, y0, z0, x1, y1, z1) = g._ranges[t];
            if (CellSpanExceedsLimit(x0, y0, z0, x1, y1, z1)) overflow.Add(t);
        }
        g._cells = CellTable.Build(g._ranges, 0, overflow);
        if (overflow.Count > 0) g._completeOrder = new CompleteQueryOrder(g, overflow.ToArray());
        long bytes = g._v.Length * 12L + g._i.Length * 4L + g._n.Length * 12L + g._tb.Length * 24L + g.TriangleCount * 4L + g._cells.ChargedBytes;
        bytes += g._completeOrder?.AdditionalApproxBytes ?? 0;
        g.ApproxBytes = bytes;
        return g;
    }

    void InitializeGeometry()
    {
        var g = this;
        var weld = new Dictionary<(int, int, int), int>(g._v.Length);
        var canon = new int[g._v.Length];
        for (int k = 0; k < g._v.Length; k++)
        {
            var v = g._v[k];
            var key = ((int)MathF.Round(v.X * 100f), (int)MathF.Round(v.Y * 100f), (int)MathF.Round(v.Z * 100f));
            if (!weld.TryGetValue(key, out var id)) weld[key] = id = k;
            canon[k] = id;
        }
        var edges = new Dictionary<long, int>(g.TriangleCount * 2);
        bool degenerate = false;
        for (int t = 0; t < g.TriangleCount; t++)
        {
            int i0 = canon[g._i[t * 3]], i1 = canon[g._i[t * 3 + 1]], i2 = canon[g._i[t * 3 + 2]];
            if (i0 == i1 || i1 == i2 || i2 == i0) { degenerate = true; continue; }
            CountEdge(edges, i0, i1); CountEdge(edges, i1, i2); CountEdge(edges, i2, i0);
        }
        g.IsClosed = g.TriangleCount > 0 && !degenerate && edges.Values.All(n => n == 2);
        for (int t = 0; t < g.TriangleCount; t++)
        {
            var a = g._v[g._i[t * 3]]; var b = g._v[g._i[t * 3 + 1]]; var c = g._v[g._i[t * 3 + 2]];
            var n = Vector3.Cross(b - a, c - a);
            float len = n.Length();
            g._n[t] = len < 1e-9f ? Vector3.Zero : n / len;
            var tb = Aabb.Empty.Add(a).Add(b).Add(c);
            g._tb[t] = tb;
            g._bounds = g._bounds.Union(tb);
            g._ranges[t] = new CellRange(g.CellAxis(tb.Min.X), g.CellAxis(tb.Min.Y), g.CellAxis(tb.Min.Z), g.CellAxis(tb.Max.X), g.CellAxis(tb.Max.Y), g.CellAxis(tb.Max.Z));
        }
    }

    static void CountEdge(Dictionary<long, int> edges, int p, int q)
    {
        long e = p < q ? ((long)p << 32) | (uint)q : ((long)q << 32) | (uint)p;
        System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(edges, e, out _)++;
    }

    /// <summary>Explicit bounded bucket sizing. Queries retain the virtual legacy
    /// cell traversal; this changes storage only. Build(soup, cell) stays fixed.</summary>
    public static TriangleGrid BuildAdaptive(TriangleSoup soup, float legacyCell = 32f,
        long targetBucketApproxBytes = 64L * 1024 * 1024)
    {
        if (!float.IsFinite(legacyCell) || legacyCell <= 0f)
            throw new ArgumentOutOfRangeException(nameof(legacyCell), "triangle_grid_cell_must_be_finite_positive");
        if (targetBucketApproxBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(targetBucketApproxBytes), "triangle_grid_negative_bucket_target");
        var g = new TriangleGrid(soup, legacyCell); g.InitializeGeometry();
        var order = new CompleteQueryOrder(g, []);
        g.Sizing = order.Populate(targetBucketApproxBytes);
        if (g.Sizing.GroupingPower > 0 || order.OverflowCount > 0) g._completeOrder = order;
        g.ApproxBytes = g.MeasureApproxBytes();
        g.Sizing = g.Sizing with { ApproxBytes = g.ApproxBytes };
        return g;
    }
    /// <summary>Present for explicit adaptive builds; estimate is not an allocation
    /// bound or a promise that this grid fits current process/free memory.</summary>
    public TriangleGridSizing? Sizing { get; private set; }

    long MeasureApproxBytes()
    {
        long bytes = _v.Length * 12L + _i.Length * 4L + _n.Length * 12L + _tb.Length * 24L + TriangleCount * 4L + _cells.ChargedBytes;
        bytes += _completeOrder?.AdditionalApproxBytes ?? 0;
        if (Sizing is { } s) bytes += (s.LegacyOmittedIds.Length + s.OverflowIds.Length) * 4L + 256;
        return bytes;
    }

    int CellAxis(float value)
    {
        float q = MathF.Floor(value / _cell);
        if (!float.IsFinite(q) || (double)q < int.MinValue || (double)q >= (double)int.MaxValue + 1)
            throw new ArgumentOutOfRangeException(nameof(value), "triangle_grid_cell_index_out_of_domain");
        return (int)q;
    }
    (int, int, int) CellOf(Vector3 p) => (CellAxis(p.X), CellAxis(p.Y), CellAxis(p.Z));

    // True at the first cell of a box query's x, y, z loop that holds this range: the corner where the two ranges meet.
    static bool FirstInBox(in CellRange b, int x, int y, int z, int x0, int y0, int z0)
        => x == Math.Max(b.X0, x0) && y == Math.Max(b.Y0, y0) && z == Math.Max(b.Z0, z0);

    // True at the first cell of a ring search that holds this range: its nearest ring, then that ring's first cell in loop order.
    static bool FirstInRing(in CellRange b, int x, int y, int z, int cx, int cy, int cz, int r)
        => x == Math.Max(b.X0, cx - r) && y == Math.Max(b.Y0, cy - r) && z == Math.Max(b.Z0, cz - r) && RingOf(b, cx, cy, cz) == r;

    static int RingOf(in CellRange b, int cx, int cy, int cz) => checked((int)RingGap(b, cx, cy, cz));

    // The first ring around the centre that meets this range.
    static long RingGap(in CellRange b, int cx, int cy, int cz)
    {
        static long Gap(int c, int lo, int hi) => c < lo ? (long)lo - c : c > hi ? (long)c - hi : 0;
        return Math.Max(Gap(cx, b.X0, b.X1), Math.Max(Gap(cy, b.Y0, b.Y1), Gap(cz, b.Z0, b.Z1)));
    }

    // The last ring around the centre that meets this range.
    static long FarRing(in CellRange b, int cx, int cy, int cz)
    {
        static long Far(int c, int lo, int hi) => Math.Max(Math.Abs((long)lo - c), Math.Abs((long)hi - c));
        return Math.Max(Far(cx, b.X0, b.X1), Math.Max(Far(cy, b.Y0, b.Y1), Far(cz, b.Z0, b.Z1)));
    }

    /// <summary>The nearest triangle to (a,b,c) within <paramref name="reach"/>, and its distance; (reach, −1) when
    /// none is that close. Candidates come from the cells the query triangle's bounds cover, grown by the reach; each is
    /// rejected on box distance before the exact test.</summary>
    public (float Distance, int Triangle) NearestTriangle(Vector3 a, Vector3 b, Vector3 c, float reach)
    {
        if (_completeOrder is not null) return NearestTriangleComplete(a, b, c, reach);
        var qb = Aabb.Empty.Add(a).Add(b).Add(c);
        float best = reach; int bestT = -1; float limit = SpatialOverlap.LimitSquared(best);
        var lo = new Vector3(qb.Min.X - reach, qb.Min.Y - reach, qb.Min.Z - reach);
        var hi = new Vector3(qb.Max.X + reach, qb.Max.Y + reach, qb.Max.Z + reach);
        var (x0, y0, z0) = CellOf(lo); var (x1, y1, z1) = CellOf(hi);
        // No triangle's box is nearer than the whole surface's box, and rounding keeps that order.
        if (SpatialOverlap.BoxDistance(qb, _bounds) >= best) return (best, bestT);
        var s = _cells.Span;
        x0 = Math.Max(x0, s.X0); y0 = Math.Max(y0, s.Y0); z0 = Math.Max(z0, s.Z0);
        x1 = Math.Min(x1, s.X1); y1 = Math.Min(y1, s.Y1); z1 = Math.Min(z1, s.Z1);
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                for (int z = z0; z <= z1; z++)
                    foreach (var t in _cells.At(x, y, z))
                    {
                        if (!FirstInBox(_ranges[t], x, y, z, x0, y0, z0)) continue;
                        if (SpatialOverlap.BoxDistance(qb, _tb[t]) >= best) continue;
                        var d = SpatialOverlap.TriangleDistanceUnder(a, b, c, _v[_i[t * 3]], _v[_i[t * 3 + 1]], _v[_i[t * 3 + 2]], limit);
                        if (d < best) { best = d; bestT = t; if (best <= 0f) return (0f, bestT); limit = SpatialOverlap.LimitSquared(best); }
                    }
        return (best, bestT);
    }

    /// <summary>Every triangle within <paramref name="band"/> of (a,b,c), with its distance and the closest pair of
    /// points (the query's, then the triangle's) — the faces a touching triangle may be in contact with. Exact per
    /// candidate; the grid only narrows.</summary>
    public void ForEachWithin(Vector3 a, Vector3 b, Vector3 c, float band, Action<int, float, Vector3, Vector3, bool> visit)
    {
        if (_completeOrder is not null) { ForEachWithinComplete(a, b, c, band, visit); return; }
        var qb = Aabb.Empty.Add(a).Add(b).Add(c);
        var lo = new Vector3(qb.Min.X - band, qb.Min.Y - band, qb.Min.Z - band);
        var hi = new Vector3(qb.Max.X + band, qb.Max.Y + band, qb.Max.Z + band);
        var (x0, y0, z0) = CellOf(lo); var (x1, y1, z1) = CellOf(hi);
        var s = _cells.Span;
        x0 = Math.Max(x0, s.X0); y0 = Math.Max(y0, s.Y0); z0 = Math.Max(z0, s.Z0);
        x1 = Math.Min(x1, s.X1); y1 = Math.Min(y1, s.Y1); z1 = Math.Min(z1, s.Z1);
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                for (int z = z0; z <= z1; z++)
                    foreach (var t in _cells.At(x, y, z))
                    {
                        if (!FirstInBox(_ranges[t], x, y, z, x0, y0, z0)) continue;
                        if (_n[t] == Vector3.Zero) continue;
                        if (SpatialOverlap.BoxDistance(qb, _tb[t]) > band) continue;
                        var d = SpatialOverlap.TriangleClosestPoints(a, b, c, _v[_i[t * 3]], _v[_i[t * 3 + 1]], _v[_i[t * 3 + 2]], out var p, out var q, out var crosses);
                        if (d <= band) visit(t, d, p, q, crosses);
                    }
    }

    /// <summary>The nearest point of the surface to <paramref name="p"/> within <paramref name="reach"/>, its distance
    /// and its triangle; (NaN, zero, −1) when nothing is that close. Winding plays no part.</summary>
    public (float Distance, Vector3 Point, int Triangle) NearestPoint(Vector3 p, float reach)
    {
        if (_completeOrder is not null) return NearestPointComplete(p, reach);
        float best = reach; int bestT = -1; var bestQ = Vector3.Zero;
        var (cx, cy, cz) = CellOf(p);
        var pb = Aabb.Empty.Add(p);
        if (SpatialOverlap.BoxDistance(pb, _bounds) >= best) return (float.NaN, Vector3.Zero, -1);
        var s = _cells.Span;
        int maxRing = (int)MathF.Ceiling(reach / _cell) + 1;
        long first = RingGap(s, cx, cy, cz), last = Math.Min(maxRing, FarRing(s, cx, cy, cz));
        for (long rl = first; rl <= last; rl++)
        {
            int r = (int)rl;
            if (bestT >= 0 && (r - 1) * _cell > best) break;
            int xa = Math.Max(cx - r, s.X0), xb = Math.Min(cx + r, s.X1), ya = Math.Max(cy - r, s.Y0), yb = Math.Min(cy + r, s.Y1), za = Math.Max(cz - r, s.Z0), zb = Math.Min(cz + r, s.Z1);
            for (int x = xa; x <= xb; x++)
                for (int y = ya; y <= yb; y++)
                {
                    // Inside the ring's x and y faces only its two z faces are on the ring.
                    bool face = Math.Abs(x - cx) == r || Math.Abs(y - cy) == r;
                    int zs = face ? za : cz - r >= za ? cz - r : cz + r, step = face ? 1 : 2 * r;
                    for (int z = zs; z <= zb; z += step)
                        foreach (var t in _cells.At(x, y, z))
                        {
                            if (!FirstInRing(_ranges[t], x, y, z, cx, cy, cz, r)) continue;
                            if (SpatialOverlap.BoxDistance(pb, _tb[t]) >= best) continue;
                            var q = SpatialOverlap.ClosestPointOnTriangle(p, _v[_i[t * 3]], _v[_i[t * 3 + 1]], _v[_i[t * 3 + 2]]);
                            float d = (p - q).Length();
                            if (d < best) { best = d; bestT = t; bestQ = q; }
                        }
                }
        }
        return bestT < 0 ? (float.NaN, Vector3.Zero, -1) : (best, bestQ, bestT);
    }

    /// <summary>How far inside the surface <paramref name="p"/> is, and the normal of the face it is behind: the distance
    /// to the nearest triangle within <paramref name="reach"/> when p is on the back side of EVERY triangle at that
    /// distance — so a point outside a convex edge (behind one face, in front of the other) reads outside, and a point
    /// inside a concave corner (behind both) reads inside. (0, zero) when p is outside or nothing is within reach.
    /// Needs the soup wound consistently outward; a mixed-winding or one-sided open mesh is a stated limit.</summary>
    public (float Depth, Vector3 Normal) InsideDepth(Vector3 p, float reach)
    {
        if (_completeOrder is not null) return InsideDepthComplete(p, reach);
        float best = reach; int bestT = -1;
        bool behindAll = true; var n = Vector3.Zero;
        var (cx, cy, cz) = CellOf(p);
        var pb = Aabb.Empty.Add(p);
        // Beyond this no triangle passes the box test below, so best stays at reach and nothing is found.
        if (SpatialOverlap.BoxDistance(pb, _bounds) > best + 1e-4f) return (0f, Vector3.Zero);
        var s = _cells.Span;
        // Ring search outward; a ring at index r can only hold triangles at distance ≥ (r − 1) · cell from p.
        int maxRing = (int)MathF.Ceiling(reach / _cell) + 1;
        long first = RingGap(s, cx, cy, cz), last = Math.Min(maxRing, FarRing(s, cx, cy, cz));
        for (long rl = first; rl <= last; rl++)
        {
            int r = (int)rl;
            if (bestT >= 0 && (r - 1) * _cell > best) break;
            int xa = Math.Max(cx - r, s.X0), xb = Math.Min(cx + r, s.X1), ya = Math.Max(cy - r, s.Y0), yb = Math.Min(cy + r, s.Y1), za = Math.Max(cz - r, s.Z0), zb = Math.Min(cz + r, s.Z1);
            for (int x = xa; x <= xb; x++)
                for (int y = ya; y <= yb; y++)
                {
                    // Inside the ring's x and y faces only its two z faces are on the ring.
                    bool face = Math.Abs(x - cx) == r || Math.Abs(y - cy) == r;
                    int zs = face ? za : cz - r >= za ? cz - r : cz + r, step = face ? 1 : 2 * r;
                    for (int z = zs; z <= zb; z += step)
                        foreach (var t in _cells.At(x, y, z))
                        {
                            if (!FirstInRing(_ranges[t], x, y, z, cx, cy, cz, r)) continue;
                            float lb = SpatialOverlap.BoxDistance(pb, _tb[t]);
                            if (lb > best + 1e-4f) continue;
                            var a = _v[_i[t * 3]]; var b = _v[_i[t * 3 + 1]]; var c = _v[_i[t * 3 + 2]];
                            var q = SpatialOverlap.ClosestPointOnTriangle(p, a, b, c);
                            float d = (p - q).Length();
                            if (_n[t] == Vector3.Zero) continue;
                            bool behind = Vector3.Dot(p - q, _n[t]) < -1e-4f || (d < 1e-4f && Vector3.Dot(p - q, _n[t]) <= 0f);
                            if (d < best - 1e-4f) { best = d; bestT = t; behindAll = behind; n = _n[t]; }
                            else if (bestT >= 0 && MathF.Abs(d - best) <= 1e-4f) { behindAll &= behind; }
                        }
                }
        }
        if (bestT < 0 || !behindAll || best <= 1e-4f) return (0f, Vector3.Zero);
        return (best, n);
    }
}
