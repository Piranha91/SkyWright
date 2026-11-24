using HousecarlCore;

namespace SkyWright.Engine;

// The surface-contact half of MeshIntersection (patch-miss packet, session 6): crossing length, coplanar areas and the
// buried area, on the same prepared meshes, exact predicates and ray parity as Query and the volume. MeshSurface.cs is
// the public surface and states the definitions.
public static partial class MeshIntersection
{
    internal sealed class SurfaceTotals
    {
        public readonly Dictionary<string, double> Crossing = new(), SameFacing = new(), OppositeFacing = new();
        public double Decided, Undecided, Rounding;
        public long InteriorSegments, EdgeSegments, Pairs;
        public DVec3Box Envelope = DVec3Box.Empty;
        // Lane D (Stage D): an opt-in receiver of every crossing segment as it is summed. Null by default: no caller's result changes.
        public Action<CrossingPiece>? Sink;
        public void Add(Dictionary<string, double> d, string key, double v) => d[key] = d.GetValueOrDefault(key) + v;
    }

    internal readonly record struct DVec3Box(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)
    {
        public static readonly DVec3Box Empty = new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity,
                                                     double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        public bool IsEmpty => MinX > MaxX;
        internal DVec3Box Add(double x, double y, double z) => new(Math.Min(MinX, x), Math.Min(MinY, y), Math.Min(MinZ, z),
                                                                   Math.Max(MaxX, x), Math.Max(MaxY, y), Math.Max(MaxZ, z));
    }

    internal readonly record struct SurfaceCore(string Status, string? Reason, SurfaceTotals? Totals);

    /// <summary>Crossing length (interior and edge-lying, the latter decided by exact side and interleave tests),
    /// coplanar areas split by facing, all keyed by "tagA x tagB".</summary>
    internal static SurfaceCore SurfaceContact(TriangleSoup a, string[] tagsA, TriangleSoup b, string[] tagsB,
                                               long maxTrianglePairs, CancellationToken cancellationToken,
                                               Action<CrossingPiece>? sink = null)
    {
        var budget = new WorkBudget(maxTrianglePairs, cancellationToken);
        var totals = new SurfaceTotals { Sink = sink };
        try
        {
            if (!TryPrepare(a, "a", budget, out var ma, out var error)) return new("refused", "A: " + error, null);
            if (!TryPrepare(b, "b", budget, out var mb, out error)) return new("refused", "B: " + error, null);

            // ---- triangle pairs: interior crossings and coplanar areas ----
            var stack = new Stack<(BvhNode A, BvhNode B)>();
            stack.Push((ma!.Root, mb!.Root));
            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pair = stack.Pop();
                if (!pair.A.Bounds.Overlaps(pair.B.Bounds, 0)) continue;
                if (pair.A.IsLeaf && pair.B.IsLeaf)
                {
                    for (int ia = pair.A.Start; ia < pair.A.Start + pair.A.Count; ia++)
                    for (int ib = pair.B.Start; ib < pair.B.Start + pair.B.Count; ib++)
                    {
                        int ta = ma.Order[ia], tb = mb.Order[ib];
                        Triangle x = ma.Triangles[ta], y = mb.Triangles[tb];
                        if (!x.Bounds.Overlaps(y.Bounds, 0)) continue;
                        budget.Consume();
                        totals.Pairs++;
                        PairContribution(x, y, SurfaceMaterialKey(tagsA[ta], tagsB[tb]), totals, ta, tb);
                    }
                    continue;
                }
                if (pair.B.IsLeaf || (!pair.A.IsLeaf && pair.A.Bounds.SurfaceArea >= pair.B.Bounds.SurfaceArea))
                { stack.Push((pair.A.Left!, pair.B)); stack.Push((pair.A.Right!, pair.B)); }
                else
                { stack.Push((pair.A, pair.B.Left!)); stack.Push((pair.A, pair.B.Right!)); }
            }

            // ---- edges lying in the other surface's faces ----
            var edgesA = EdgeIncidence(ma); var edgesB = EdgeIncidence(mb);
            var edgePairsDone = new HashSet<(Edge, Edge)>();
            EdgePass(ma, edgesA, tagsA, mb, edgesB, tagsB, aIsEdgeSide: true, totals, edgePairsDone, budget);
            EdgePass(mb, edgesB, tagsB, ma, edgesA, tagsA, aIsEdgeSide: false, totals, edgePairsDone, budget);
            return new("measured", null, totals);
        }
        catch (BudgetExceededException) { return new("incomplete", "triangle_pair_budget_exhausted", totals); }
        catch (OperationCanceledException) { return new("incomplete", "cancelled", totals); }
    }

    static string SurfaceMaterialKey(string a, string b) => a + " x " + b;

    static void EmitPiece(SurfaceTotals t, string kind, int[] ta, int[] tb, DVec3 p0, DVec3 p1, string key, bool decided) =>
        t.Sink?.Invoke(new CrossingPiece(kind, ta, tb, p0.X, p0.Y, p0.Z, p1.X, p1.Y, p1.Z, key, decided));

    static void PairContribution(in Triangle a, in Triangle b, string key, SurfaceTotals totals, int ta = -1, int tb = -1)
    {
        int[] bToA = [Robust.Orient3Sign(a.A, a.B, a.C, b.A), Robust.Orient3Sign(a.A, a.B, a.C, b.B), Robust.Orient3Sign(a.A, a.B, a.C, b.C)];
        int[] aToB = [Robust.Orient3Sign(b.A, b.B, b.C, a.A), Robust.Orient3Sign(b.A, b.B, b.C, a.B), Robust.Orient3Sign(b.A, b.B, b.C, a.C)];
        if (bToA.All(s => s == 0) && aToB.All(s => s == 0))
        {
            if (CoplanarProjectedRelation(a, b) != PairRelation.CoplanarOverlap) return;
            double area = CoplanarOverlapArea(a, b);
            if (area <= 0) return;
            bool same = DVec3.Dot(a.Normal, b.Normal) > 0;
            totals.Add(same ? totals.SameFacing : totals.OppositeFacing, key, area);
            return;
        }
        if (!HasBothSigns(bToA) || !HasBothSigns(aToB)) return;
        DVec3 line = DVec3.Cross(a.Normal, b.Normal);
        if (!(line.LengthSquared > 0)) return;
        line /= Math.Sqrt(line.LengthSquared);
        DVec3 origin = a.A;
        var ai = PlaneCut(a, aToB, b, line, origin);
        var bi = PlaneCut(b, bToA, a, line, origin);
        if (!ai.HasValue || !bi.HasValue) return;
        double lo = Math.Max(ai.Value.Min, bi.Value.Min), hi = Math.Min(ai.Value.Max, bi.Value.Max);
        double roundoff = 64 * MachineEpsilon * Math.Max(Math.Abs(lo), Math.Abs(hi));
        if (!(hi > lo + roundoff)) return;
        totals.Add(totals.Crossing, key, hi - lo);
        totals.Decided += hi - lo;
        totals.InteriorSegments++;
        // lo and hi are parameters along the line from origin, a vertex of A that is generally off the line, so origin + line*t
        // is not a point of the crossing; the rounding bound is stated for that arithmetic. The envelope takes the cut points that
        // carry lo and hi: each lies on an edge of one triangle (or is its vertex) in the other's plane, a point of the curve.
        DVec3 q0 = origin + line * lo, q1 = origin + line * hi;
        totals.Rounding += 64 * MachineEpsilon * (MaxAbs(q0) + MaxAbs(q1) + 1);
        DVec3 p0 = ai.Value.Min >= bi.Value.Min ? ai.Value.MinPoint : bi.Value.MinPoint;
        DVec3 p1 = ai.Value.Max <= bi.Value.Max ? ai.Value.MaxPoint : bi.Value.MaxPoint;
        // The pieces handed to an optional sink (alpha sampling) are the same on-curve points.
        EmitPiece(totals, "interior", [ta], [tb], p0, p1, key, true);
        totals.Envelope = totals.Envelope.Add(p0.X, p0.Y, p0.Z).Add(p1.X, p1.Y, p1.Z);
    }

    static double MaxAbs(DVec3 p) => Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z)));

    // Area of the overlap of two coplanar triangles: a clipped by b's three edge half-planes (in b's plane).
    static double CoplanarOverlapArea(in Triangle a, in Triangle b)
    {
        DVec3 n = b.Normal;
        var poly = new List<DVec3> { a.A, a.B, a.C };
        DVec3[] bv = [b.A, b.B, b.C];
        for (int e = 0; e < 3 && poly.Count > 0; e++)
        {
            DVec3 s = bv[e], t = bv[(e + 1) % 3];
            DVec3 inward = DVec3.Cross(n, t - s);
            var next = new List<DVec3>(poly.Count + 1);
            for (int i = 0; i < poly.Count; i++)
            {
                DVec3 cur = poly[i], nxt = poly[(i + 1) % poly.Count];
                double dc = DVec3.Dot(inward, cur - s), dn = DVec3.Dot(inward, nxt - s);
                if (dc >= 0) next.Add(cur);
                if ((dc > 0 && dn < 0) || (dc < 0 && dn > 0)) next.Add(cur + (nxt - cur) * (dc / (dc - dn)));
            }
            poly = next;
        }
        if (poly.Count < 3) return 0;
        DVec3 sum = new(0, 0, 0);
        for (int i = 1; i + 1 < poly.Count; i++) sum += DVec3.Cross(poly[i] - poly[0], poly[i + 1] - poly[0]);
        return 0.5 * Math.Sqrt(sum.LengthSquared);
    }

    // Welded edge -> the triangles using it and each one's third vertex.
    static Dictionary<Edge, List<(int Triangle, DVec3 Third)>> EdgeIncidence(Mesh m)
    {
        var d = new Dictionary<Edge, List<(int, DVec3)>>();
        for (int t = 0; t < m.Triangles.Length; t++)
        {
            Triangle tri = m.Triangles[t];
            int[] w = [m.Welded[tri.I0], m.Welded[tri.I1], m.Welded[tri.I2]];
            DVec3[] v = [tri.A, tri.B, tri.C];
            for (int i = 0; i < 3; i++)
            {
                int p = w[i], q = w[(i + 1) % 3];
                var e = new Edge(Math.Min(p, q), Math.Max(p, q));
                if (!d.TryGetValue(e, out var l)) d[e] = l = new();
                l.Add((t, v[(i + 2) % 3]));
            }
        }
        return d;
    }

    // conservative: a non-opaque incident face makes the edge's tag non-opaque
    static string EdgeTag(List<(int Triangle, DVec3 Third)> incident, string[] tags) =>
        incident.Select(i => tags[i.Triangle]).FirstOrDefault(t => t != "opaque") ?? "opaque";

    static void QueryBvh(Mesh m, Bounds box, List<int> hits)
    {
        hits.Clear();
        var stack = new Stack<BvhNode>(); stack.Push(m.Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!n.Bounds.Overlaps(box, 0)) continue;
            if (!n.IsLeaf) { stack.Push(n.Left!); stack.Push(n.Right!); continue; }
            for (int i = n.Start; i < n.Start + n.Count; i++) if (m.Triangles[m.Order[i]].Bounds.Overlaps(box, 0)) hits.Add(m.Order[i]);
        }
    }

    /// <summary>Edges of mesh E lying in faces of mesh F. An edge in the interior of a face counts when E's two incident
    /// faces lie strictly on opposite sides of that face; an edge lying along an edge of F is decided once per edge pair by
    /// the interleave test; any other incidence count is undecided (upper bound only). A zero side sign is coplanar
    /// adjacency (area, not crossing).</summary>
    static void EdgePass(Mesh e, Dictionary<Edge, List<(int Triangle, DVec3 Third)>> edgesE, string[] tagsE,
                         Mesh f, Dictionary<Edge, List<(int Triangle, DVec3 Third)>> edgesF, string[] tagsF,
                         bool aIsEdgeSide, SurfaceTotals totals, HashSet<(Edge, Edge)> edgePairsDone, WorkBudget budget)
    {
        var hits = new List<int>();
        foreach (var (edge, incident) in edgesE)
        {
            budget.Cancellation.ThrowIfCancellationRequested();
            DVec3 p = e.Vertices[edge.A], q = e.Vertices[edge.B];
            var box = Bounds.Empty.Add(p).Add(q);
            QueryBvh(f, box, hits);
            foreach (int ft in hits)
            {
                budget.Consume();
                Triangle t = f.Triangles[ft];
                if (Robust.Orient3Sign(t.A, t.B, t.C, p) != 0 || Robust.Orient3Sign(t.A, t.B, t.C, q) != 0) continue;
                if (!ClipSegmentToTriangle(p, q, t, out double s0, out double s1)) continue;
                double len = (s1 - s0) * Math.Sqrt((q - p).LengthSquared);
                if (!(len > 0)) continue;
                DVec3 c0 = p + (q - p) * s0, c1 = p + (q - p) * s1;
                // does the piece lie along an edge of t? then it is an edge-edge contact, decided once below
                int[] fw = [f.Welded[t.I0], f.Welded[t.I1], f.Welded[t.I2]];
                DVec3 n = t.Normal;
                DVec3[] fv = [t.A, t.B, t.C];
                bool onEdge = false;
                for (int i = 0; i < 3 && !onEdge; i++)
                {
                    DVec3 u = fv[i], v = fv[(i + 1) % 3];
                    if (CollinearInPlane(u, v, p, n) && CollinearInPlane(u, v, q, n))
                    {
                        onEdge = true;
                        var fe = new Edge(Math.Min(fw[i], fw[(i + 1) % 3]), Math.Max(fw[i], fw[(i + 1) % 3]));
                        var key = aIsEdgeSide ? (edge, fe) : (fe, edge);
                        if (!edgePairsDone.Add(key)) break;
                        EdgeEdge(p, q, incident, f.Vertices[fe.A], f.Vertices[fe.B], edgesF[fe], aIsEdgeSide,
                                 EdgeTag(incident, tagsE), EdgeTag(edgesF[fe], tagsF), totals);
                    }
                }
                if (onEdge) continue;
                // edge inside the face t: the side test on E's incident faces against t's plane
                string tagE = EdgeTag(incident, tagsE), tagF = tagsF[ft];
                string key2 = aIsEdgeSide ? SurfaceMaterialKey(tagE, tagF) : SurfaceMaterialKey(tagF, tagE);
                var signs = incident.Select(i => Robust.Orient3Sign(t.A, t.B, t.C, i.Third)).ToList();
                if (signs.Any(s => s == 0)) continue;                         // coplanar adjacency: area, not crossing
                int[] incE = incident.Select(i => i.Triangle).ToArray();
                int[] onF = [ft];
                if (incident.Count != 2)
                {
                    totals.Undecided += len;
                    EmitPiece(totals, "undecided", aIsEdgeSide ? incE : onF, aIsEdgeSide ? onF : incE, c0, c1, key2, false);
                    continue;
                }
                if (signs[0] == signs[1]) continue;                           // both faces on one side: touching
                EmitPiece(totals, "edge_face", aIsEdgeSide ? incE : onF, aIsEdgeSide ? onF : incE, c0, c1, key2, true);
                totals.Add(totals.Crossing, key2, len);
                totals.Decided += len;
                totals.EdgeSegments++;
                totals.Rounding += 64 * MachineEpsilon * (MaxAbs(c0) + MaxAbs(c1) + 1);
                totals.Envelope = totals.Envelope.Add(c0.X, c0.Y, c0.Z).Add(c1.X, c1.Y, c1.Z);
            }
        }
    }

    // Exact: x lies on the line u-v, all three in the plane with normal n (2-D orientation in the dominant projection).
    static bool CollinearInPlane(DVec3 u, DVec3 v, DVec3 x, DVec3 n)
    {
        int drop = Math.Abs(n.X) >= Math.Abs(n.Y) && Math.Abs(n.X) >= Math.Abs(n.Z) ? 0 : Math.Abs(n.Y) >= Math.Abs(n.Z) ? 1 : 2;
        return Robust.Orient2Sign(Project(u, drop), Project(v, drop), Project(x, drop)) == 0;
    }

    // Two collinear edges, one of each mesh: the overlap length, crossing when the four half-planes interleave.
    static void EdgeEdge(DVec3 p, DVec3 q, List<(int Triangle, DVec3 Third)> incE, DVec3 r, DVec3 s,
                         List<(int Triangle, DVec3 Third)> incF, bool aIsEdgeSide, string tagE, string tagF, SurfaceTotals totals)
    {
        DVec3 d = q - p; double dl = Math.Sqrt(d.LengthSquared); if (!(dl > 0)) return;
        DVec3 u = d / dl;
        double r0 = DVec3.Dot(r - p, u), s0 = DVec3.Dot(s - p, u);
        double lo = Math.Max(0, Math.Min(r0, s0)), hi = Math.Min(dl, Math.Max(r0, s0));
        double len = hi - lo;
        if (!(len > 0)) return;
        // A = the side named "A" in the key
        var incA = aIsEdgeSide ? incE : incF; var incB = aIsEdgeSide ? incF : incE;
        string key = aIsEdgeSide ? SurfaceMaterialKey(tagE, tagF) : SurfaceMaterialKey(tagF, tagE);
        // a face of one mesh in the plane of a face of the other along this line: coplanar adjacency (area, not crossing)
        foreach (var (_, ta) in incA)
            foreach (var (_, tb) in incB)
                if (Robust.Orient3Sign(p, q, ta, tb) == 0) return;
        int[] triA = incA.Select(i => i.Triangle).ToArray(), triB = incB.Select(i => i.Triangle).ToArray();
        if (incA.Count != 2 || incB.Count != 2) { totals.Undecided += len; EmitPiece(totals, "undecided", triA, triB, p + u * lo, p + u * hi, key, false); return; }
        DVec3 a1 = incA[0].Third, a2 = incA[1].Third, b1 = incB[0].Third, b2 = incB[1].Third;
        int s12 = Robust.Orient3Sign(p, q, a1, a2);
        int sb1 = Robust.Orient3Sign(p, q, a1, b1), sb2 = Robust.Orient3Sign(p, q, a1, b2);
        bool crossing;
        if (s12 == 0)
        {
            // A is flat along the line (or folded onto itself): B crosses when its two faces lie strictly on opposite sides
            if (DVec3.Dot(a1 - p, a2 - p) - DVec3.Dot(a1 - p, u) * DVec3.Dot(a2 - p, u) > 0) { totals.Undecided += len; EmitPiece(totals, "undecided", triA, triB, p + u * lo, p + u * hi, key, false); return; } // folded
            if (sb1 == 0 || sb2 == 0) return;
            crossing = sb1 != sb2;
        }
        else
        {
            int t1 = Robust.Orient3Sign(p, q, a2, b1), t2 = Robust.Orient3Sign(p, q, a2, b2);
            int s21 = Robust.Orient3Sign(p, q, a2, a1);
            if (sb1 == 0 || sb2 == 0 || t1 == 0 || t2 == 0) return;   // a face of B on a face of A: coplanar adjacency
            bool in1 = sb1 == s12 && t1 == s21, in2 = sb2 == s12 && t2 == s21;   // inside A's convex wedge
            crossing = in1 != in2;
        }
        if (!crossing) return;
        totals.Add(totals.Crossing, key, len);
        totals.Decided += len;
        totals.EdgeSegments++;
        DVec3 c0 = p + u * lo, c1 = p + u * hi;
        EmitPiece(totals, "edge_edge", triA, triB, c0, c1, key, true);
        totals.Rounding += 64 * MachineEpsilon * (MaxAbs(c0) + MaxAbs(c1) + 1);
        totals.Envelope = totals.Envelope.Add(c0.X, c0.Y, c0.Z).Add(c1.X, c1.Y, c1.Z);
    }

    // Clip the segment p-q (lying in t's plane) to triangle t: the parameter interval [s0, s1] of the part inside t.
    static bool ClipSegmentToTriangle(DVec3 p, DVec3 q, in Triangle t, out double s0, out double s1)
    {
        s0 = 0; s1 = 1;
        DVec3 n = t.Normal;
        DVec3[] v = [t.A, t.B, t.C];
        for (int i = 0; i < 3; i++)
        {
            DVec3 a = v[i], b = v[(i + 1) % 3];
            DVec3 inward = DVec3.Cross(n, b - a);
            double fp = DVec3.Dot(inward, p - a), fq = DVec3.Dot(inward, q - a);
            if (fp < 0 && fq < 0) return false;
            if (fp < 0) s0 = Math.Max(s0, fp / (fp - fq));
            else if (fq < 0) s1 = Math.Min(s1, fp / (fp - fq));
        }
        return s1 > s0;
    }

    internal readonly record struct BuriedCore(string Status, string? Reason, double Area, double Lower, double Upper, bool Converged, long Pieces);

    /// <summary>Area of surface S inside the validated closed solid C: a certified interval by triangle subdivision.
    /// A piece no triangle of C meets is wholly inside or outside and is decided by 3-ray parity at its centroid
    /// (ambiguous: undecided); a piece C meets is subdivided until the target or the budget; undecided pieces add to the
    /// upper bound only. The target is relative to the smaller of the two surface areas.</summary>
    internal static BuriedCore BuriedArea(TriangleSoup surface, VolumeSolid container, double relativeTarget, double absoluteTarget,
                                          int maxDepth, long maxPieces, CancellationToken cancellationToken)
    {
        var budget = new WorkBudget(long.MaxValue, cancellationToken);
        Mesh c = (Mesh)container.Handle;
        var tris = new List<(DVec3 A, DVec3 B, DVec3 C)>();
        double total = 0;
        for (int t = 0; t < surface.TriangleCount; t++)
        {
            var a = surface.Vertices[surface.Indices[3 * t]]; var b = surface.Vertices[surface.Indices[3 * t + 1]]; var d = surface.Vertices[surface.Indices[3 * t + 2]];
            var tri = (new DVec3(a.X, a.Y, a.Z), new DVec3(b.X, b.Y, b.Z), new DVec3(d.X, d.Y, d.Z));
            total += Area(tri.Item1, tri.Item2, tri.Item3);
            tris.Add(tri);
        }
        // Converged is judged against the SMALLER of the two surfaces: the buried area can never exceed the container's
        // surface, so a width judged against a much larger measured surface could read converged while deciding nothing.
        double containerArea = 0;
        foreach (var ct in c.Triangles) containerArea += Area(ct.A, ct.B, ct.C);
        double target = Math.Max(relativeTarget * Math.Min(total, containerArea), absoluteTarget);
        double lower = 0, undecided = 0;
        long pieces = 0;
        var hits = new List<int>();
        var current = tris;
        int depth = 0;
        try
        {
            while (true)
            {
                var next = new List<(DVec3 A, DVec3 B, DVec3 C)>();
                undecided = 0;
                foreach (var (a, b, d) in current)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pieces++;
                    double area = Area(a, b, d);
                    var piece = new Triangle(0, 0, 0, a, b, d);
                    if (!piece.Bounds.Overlaps(c.Root.Bounds, 0)) continue;           // outside the container's box
                    QueryBvh(c, piece.Bounds, hits);
                    bool meets = false, coplanar = false;
                    double covered = 0;
                    foreach (int ct in hits)
                    {
                        Triangle ctri = c.Triangles[ct];
                        if (Robust.Orient3Sign(ctri.A, ctri.B, ctri.C, a) == 0 && Robust.Orient3Sign(ctri.A, ctri.B, ctri.C, b) == 0
                            && Robust.Orient3Sign(ctri.A, ctri.B, ctri.C, d) == 0)
                        {
                            // the piece lies in the plane of a container face: the covered part is on the boundary, not inside
                            coplanar = true;
                            covered += CoplanarOverlapArea(piece, ctri);
                            continue;
                        }
                        if (Classify(piece, ctri, 0) != PairRelation.Separated) meets = true;
                    }
                    if (coplanar && covered >= area * (1 - 1e-9)) continue;   // wholly within the container's surface
                    if (!meets && !coplanar)
                    {
                        var r = PointInside((a + b + d) / 3, c, 0, budget);
                        if (r == InsideResult.Inside) { lower += area; continue; }
                        if (r == InsideResult.Outside) continue;
                    }
                    undecided += area;
                    next.Add((a, b, d));
                }
                if (undecided <= target) return new("measured", null, total, lower, lower + undecided, true, pieces);
                if (depth >= maxDepth || pieces + 4L * next.Count > maxPieces)
                    return new("measured", "bounded, not converged: subdivision depth or piece budget reached", total, lower, lower + undecided, false, pieces);
                current = new List<(DVec3, DVec3, DVec3)>(next.Count * 4);
                foreach (var (a, b, d) in next)
                {
                    DVec3 ab = (a + b) / 2, bd = (b + d) / 2, da = (d + a) / 2;
                    current.Add((a, ab, da)); current.Add((ab, b, bd)); current.Add((da, bd, d)); current.Add((ab, bd, da));
                }
                depth++;
            }
        }
        catch (OperationCanceledException)
        {
            return new("unknown", "cancelled", total, lower, lower + undecided, false, pieces);
        }
    }

    static double Area(DVec3 a, DVec3 b, DVec3 c) => 0.5 * Math.Sqrt(DVec3.Cross(b - a, c - a).LengthSquared);
}
