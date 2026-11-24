using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

// CONVEX HULL OF A POINT SET — the faces of a bhkConvexVerticesShape rebuilt from its vertices. Havok's convex-vertices
// shape IS the convex hull of its vertices, inflated by its convex radius; the stored half-space planes are a query aid.
// When the decoder cannot match a stored plane to three vertices within its tolerance it drops that face, and a hull with
// dropped faces has holes (or, with every face dropped, vertices and no triangles at all). This builds the faces from the
// vertices instead, and offsets a hull outward by a convex radius.

public static class ConvexHull
{
    /// <summary>Triangles (indices into <paramref name="pts"/>) of the convex hull, wound outward. A coplanar set gives its
    /// polygon triangulated in both windings (a flat plate meets things from either side); fewer than three non-collinear
    /// points give no triangles and <c>Degenerate</c> true. <paramref name="eps"/> is the coplanarity and duplicate tolerance
    /// in the points' own units.</summary>
    public static (List<int> Indices, bool Degenerate) FromVertices(IReadOnlyList<Vector3> pts, float eps = 1e-5f)
    {
        var none = (new List<int>(), true);
        int n = pts.Count;
        if (n < 3) return none;
        // initial simplex: the two points farthest apart along the widest axis, the point farthest from their line, the point
        // farthest from their plane
        int a = 0, b = 0;
        float best = -1;
        for (int axis = 0; axis < 3; axis++)
        {
            int lo = 0, hi = 0;
            for (int k = 1; k < n; k++)
            {
                if (C(pts[k], axis) < C(pts[lo], axis)) lo = k;
                if (C(pts[k], axis) > C(pts[hi], axis)) hi = k;
            }
            float d = Vector3.DistanceSquared(pts[lo], pts[hi]);
            if (d > best) { best = d; a = lo; b = hi; }
        }
        if (best <= eps * eps) return none;
        var ab = pts[b] - pts[a];
        int c = -1; float bestLine = eps;
        for (int k = 0; k < n; k++)
        {
            float d = Vector3.Cross(ab, pts[k] - pts[a]).Length() / ab.Length();
            if (d > bestLine) { bestLine = d; c = k; }
        }
        if (c < 0) return none;                                   // collinear
        var nrm = Vector3.Normalize(Vector3.Cross(ab, pts[c] - pts[a]));
        int dIdx = -1; float bestPlane = eps;
        for (int k = 0; k < n; k++)
        {
            float d = MathF.Abs(Vector3.Dot(nrm, pts[k] - pts[a]));
            if (d > bestPlane) { bestPlane = d; dIdx = k; }
        }
        if (dIdx < 0) return (Planar(pts, nrm, eps), false);      // coplanar: both windings

        // The float hull, which every hull this reader has built so far came from, is kept wherever it closes: its output is
        // unchanged for those. Where a point's rim is not one loop, a face has no area, or a point is left outside, the float
        // arithmetic has misjudged which faces a point sees (near-coincident points: an apex stored once per side), and the
        // hull is built again with exact orientation tests, which cannot misjudge.
        var hull = FloatHull(pts, eps, a, b, c, dIdx, out bool closed);
        if (closed && Check(pts, hull, pts, Tolerance(pts, eps)) is null) return (hull, hull.Count == 0);
        var exact = ExactHull(pts, a, b, c, dIdx);
        return (exact, exact.Count == 0);
    }

    // The incremental hull in float over faces (i, j, k) wound outward. Each point grows the hull over the CONNECTED region
    // of faces it sees by more than eps, reached from the face it sees most across shared edges; a point that sees none lies
    // within eps of the hull and is left out. Faces are scanned through the live list, so a point costs the hull's current
    // size, not every face ever made. closed is false, and the result empty or open, when a rim is not one simple loop or a
    // face has no area.
    static List<int> FloatHull(IReadOnlyList<Vector3> pts, float eps, int a, int b, int c, int dIdx, out bool closed)
    {
        closed = true;
        int n = pts.Count;
        var faces = new List<(int A, int B, int C, Vector3 N, float W, bool Alive)>();
        var faceOf = new Dictionary<(int, int), int>();          // directed edge -> the live face that carries it
        var live = new List<int>();
        int dead = 0;
        var inside = (pts[a] + pts[b] + pts[c] + pts[dIdx]) / 4f;
        void Put(int i, int j, int k, Vector3 fn, float w)
        {
            int f = faces.Count;
            faces.Add((i, j, k, fn, w, true));
            faceOf[(i, j)] = f; faceOf[(j, k)] = f; faceOf[(k, i)] = f;
            live.Add(f);
        }
        // the first four faces take their winding from the simplex's centre; a face grown from the rim keeps the rim's
        void AddSimplexFace(int i, int j, int k)
        {
            var fn = Vector3.Cross(pts[j] - pts[i], pts[k] - pts[i]);
            float len = fn.Length();
            if (len <= 0) { Put(i, j, k, Vector3.Zero, 0); return; }
            fn /= len;
            if (Vector3.Dot(fn, inside - pts[i]) > 0) { (j, k) = (k, j); fn = -fn; }
            Put(i, j, k, fn, Vector3.Dot(fn, pts[i]));
        }
        void AddRimFace(int i, int j, int k)
        {
            var fn = Vector3.Cross(pts[j] - pts[i], pts[k] - pts[i]);
            float len = fn.Length();
            if (len <= 0) { Put(i, j, k, Vector3.Zero, 0); return; }
            fn /= len;
            Put(i, j, k, fn, Vector3.Dot(fn, pts[i]));
        }
        AddSimplexFace(a, b, c); AddSimplexFace(a, b, dIdx); AddSimplexFace(a, c, dIdx); AddSimplexFace(b, c, dIdx);
        var used = new HashSet<int> { a, b, c, dIdx };
        var visible = new List<int>();
        var region = new HashSet<int>();
        var queue = new Queue<int>();
        var edges = new HashSet<(int, int)>();
        var rimNext = new Dictionary<int, int>();
        for (int p = 0; p < n; p++)
        {
            if (used.Contains(p)) continue;
            var q = pts[p];
            if (dead > live.Count)
            {
                live.RemoveAll(f => !faces[f].Alive);
                dead = 0;
            }
            int seed = -1; float most = eps;
            foreach (int f in live)
            {
                if (!faces[f].Alive) continue;
                float d = Vector3.Dot(faces[f].N, q) - faces[f].W;
                if (d > most) { most = d; seed = f; }
            }
            if (seed < 0) continue;                               // inside or on the hull
            // the connected region the point sees
            region.Clear(); queue.Clear();
            region.Add(seed); queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                var (i, j, k, _, _, _) = faces[queue.Dequeue()];
                Reach(j, i); Reach(k, j); Reach(i, k);
            }
            void Reach(int u, int v)
            {
                if (!faceOf.TryGetValue((u, v), out int g) || !faces[g].Alive || region.Contains(g)) return;
                if (Vector3.Dot(faces[g].N, q) - faces[g].W > eps) { region.Add(g); queue.Enqueue(g); }
            }
            visible.Clear(); visible.AddRange(region); visible.Sort();
            // rim: directed edges of visible faces whose reverse is not an edge of a visible face
            edges.Clear();
            foreach (int f in visible)
            {
                var (i, j, k, _, _, _) = faces[f];
                if (!edges.Remove((j, i))) edges.Add((i, j));
                if (!edges.Remove((k, j))) edges.Add((j, k));
                if (!edges.Remove((i, k))) edges.Add((k, i));
            }
            // one simple loop, or the point is left out: a rim that pinches or comes in two pieces would leave the surface open
            rimNext.Clear();
            bool simple = true;
            foreach (var (i, j) in edges) if (!rimNext.TryAdd(i, j)) { simple = false; break; }
            if (simple && edges.Count >= 3)
            {
                int start = rimNext.Keys.First(), cur = start, steps = 0;
                do { if (!rimNext.TryGetValue(cur, out cur)) { simple = false; break; } steps++; } while (cur != start && steps <= edges.Count);
                simple = simple && cur == start && steps == edges.Count;
            }
            else simple = false;
            if (!simple) { closed = false; return new List<int>(); }
            foreach (int f in visible) { faces[f] = faces[f] with { Alive = false }; dead++; }
            foreach (var (i, j) in edges) AddRimFace(i, j, p);
            used.Add(p);
        }
        var idx = new List<int>();
        foreach (var f in faces)
        {
            if (!f.Alive) continue;
            if (f.N == Vector3.Zero) { closed = false; continue; }   // a face with no area leaves the surface open
            idx.Add(f.A); idx.Add(f.B); idx.Add(f.C);
        }
        return idx;
    }


    /// <summary>Is <paramref name="idx"/> over <paramref name="v"/> a closed convex hull holding <paramref name="inside"/>?
    /// Null when it is: every directed edge matched once by its reverse, at most 2n-4 faces over the n vertices used, no face
    /// without area, and every point of <paramref name="inside"/> within <paramref name="tol"/> of the inner side of every
    /// face's plane. Otherwise the first thing that fails, in words.</summary>
    public static string? Check(IReadOnlyList<Vector3> v, IReadOnlyList<int> idx, IReadOnlyList<Vector3> inside, float tol)
    {
        if (idx.Count == 0 || idx.Count % 3 != 0) return $"{idx.Count} indices";
        var used = new HashSet<int>(idx);
        int faces = idx.Count / 3;
        if (faces > 2 * used.Count - 4) return $"{faces} faces over {used.Count} vertices, more than 2n-4 = {2 * used.Count - 4}";
        var directed = new Dictionary<(int, int), int>(idx.Count);
        for (int k = 0; k < idx.Count; k += 3)
            for (int e = 0; e < 3; e++)
            {
                var d = (idx[k + e], idx[k + (e + 1) % 3]);
                directed[d] = directed.GetValueOrDefault(d) + 1;
            }
        foreach (var (d, count) in directed)
            if (count != 1 || !directed.ContainsKey((d.Item2, d.Item1))) return $"edge {d.Item1}-{d.Item2} is not matched once by its reverse";
        // planes in double: a thin face's float normal can be off by more than the tolerance across the shape
        for (int k = 0; k < idx.Count; k += 3)
        {
            Vector3 p0 = v[idx[k]], p1 = v[idx[k + 1]], p2 = v[idx[k + 2]];
            double ux = (double)p1.X - p0.X, uy = (double)p1.Y - p0.Y, uz = (double)p1.Z - p0.Z;
            double vx = (double)p2.X - p0.X, vy = (double)p2.Y - p0.Y, vz = (double)p2.Z - p0.Z;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (!(len > 0)) return $"face {k / 3} has no area";
            nx /= len; ny /= len; nz /= len;
            foreach (var q in inside)
            {
                double d = nx * ((double)q.X - p0.X) + ny * ((double)q.Y - p0.Y) + nz * ((double)q.Z - p0.Z);
                if (d > tol) return $"a point lies {d:G4} beyond face {k / 3}";
            }
        }
        return null;
    }

    // how far outside its own hull a point may lie: eps (a point within eps is left out) twice over, plus float rounding at the
    // set's size
    static float Tolerance(IReadOnlyList<Vector3> pts, float eps)
    {
        float size = 0;
        foreach (var p in pts) size = MathF.Max(size, MathF.Max(MathF.Abs(p.X), MathF.Max(MathF.Abs(p.Y), MathF.Abs(p.Z))));
        return 2 * eps + 1e-6f * size;
    }

    // The incremental hull with EXACT orientation tests: a point sees a face when it lies strictly beyond the face's plane
    // through its three vertices, decided in double where the rounding bound settles the sign and with integers where it
    // does not. A strictly visible region of a convex hull is one disc, so the rim is one loop and the surface stays closed;
    // no point is left outside, none is lost to a tolerance, and no new face is flat (a point on an edge's line sees neither
    // face of that edge). Points in index order, the simplex the float hull chose.
    static List<int> ExactHull(IReadOnlyList<Vector3> pts, int a, int b, int c, int dIdx)
    {
        int n = pts.Count;
        var exact = new Exact(pts);
        var faces = new List<(int A, int B, int C, bool Alive)>();
        var faceOf = new Dictionary<(int, int), int>();
        var live = new List<int>();
        int dead = 0;
        void Put(int i, int j, int k)
        {
            int f = faces.Count;
            faces.Add((i, j, k, true));
            faceOf[(i, j)] = f; faceOf[(j, k)] = f; faceOf[(k, i)] = f;
            live.Add(f);
        }
        // the simplex's faces wound so the fourth vertex is not beyond
        void Simplex(int i, int j, int k, int other) { if (exact.Beyond(i, j, k, other) > 0) Put(i, k, j); else Put(i, j, k); }
        Simplex(a, b, c, dIdx); Simplex(a, b, dIdx, c); Simplex(a, c, dIdx, b); Simplex(b, c, dIdx, a);
        var used = new HashSet<int> { a, b, c, dIdx };
        var region = new HashSet<int>();
        var queue = new Queue<int>();
        var visible = new List<int>();
        var edges = new HashSet<(int, int)>();
        for (int p = 0; p < n; p++)
        {
            if (used.Contains(p)) continue;
            if (dead > live.Count) { live.RemoveAll(f => !faces[f].Alive); dead = 0; }
            int seed = -1;
            foreach (int f in live)
                if (faces[f].Alive && exact.Beyond(faces[f].A, faces[f].B, faces[f].C, p) > 0) { seed = f; break; }
            if (seed < 0) continue;                               // inside or on the hull
            region.Clear(); queue.Clear();
            region.Add(seed); queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                var (i, j, k, _) = faces[queue.Dequeue()];
                Reach(j, i); Reach(k, j); Reach(i, k);
            }
            void Reach(int u, int v)
            {
                if (!faceOf.TryGetValue((u, v), out int g) || !faces[g].Alive || region.Contains(g)) return;
                if (exact.Beyond(faces[g].A, faces[g].B, faces[g].C, p) > 0) { region.Add(g); queue.Enqueue(g); }
            }
            visible.Clear(); visible.AddRange(region); visible.Sort();
            edges.Clear();
            foreach (int f in visible)
            {
                var (i, j, k, _) = faces[f];
                if (!edges.Remove((j, i))) edges.Add((i, j));
                if (!edges.Remove((k, j))) edges.Add((j, k));
                if (!edges.Remove((i, k))) edges.Add((k, i));
            }
            foreach (int f in visible) { faces[f] = faces[f] with { Alive = false }; dead++; }
            foreach (var (i, j) in edges) Put(i, j, p);
            used.Add(p);
        }
        var idx = new List<int>();
        foreach (var f in faces)
            if (f.Alive) { idx.Add(f.A); idx.Add(f.B); idx.Add(f.C); }
        return idx;
    }

    /// <summary>The sign of the orientation determinant, exactly: whether point d lies beyond the plane of the triangle
    /// (a, b, c) wound outward, that is (b-a) x (c-a) . (d-a) &gt; 0. Double arithmetic answers where its rounding cannot flip
    /// the sign (the orient3d error bound of Shewchuk 1997); otherwise the coordinates, each a float and so an integer times a
    /// power of two, are put over one power of two and the determinant is taken in integers.</summary>
    sealed class Exact(IReadOnlyList<Vector3> pts)
    {
        const double Eps = 1.1102230246251565e-16;
        const double Bound = (7.0 + 56.0 * Eps) * Eps;
        System.Numerics.BigInteger[]? _x;
        int _shift;

        public int Beyond(int a, int b, int c, int d)
        {
            Vector3 pa = pts[a], pb = pts[b], pc = pts[c], pd = pts[d];
            double ux = (double)pb.X - pa.X, uy = (double)pb.Y - pa.Y, uz = (double)pb.Z - pa.Z;
            double vx = (double)pc.X - pa.X, vy = (double)pc.Y - pa.Y, vz = (double)pc.Z - pa.Z;
            double wx = (double)pd.X - pa.X, wy = (double)pd.Y - pa.Y, wz = (double)pd.Z - pa.Z;
            double det = ux * (vy * wz - vz * wy) + uy * (vz * wx - vx * wz) + uz * (vx * wy - vy * wx);
            double perm = Math.Abs(ux) * (Math.Abs(vy * wz) + Math.Abs(vz * wy)) + Math.Abs(uy) * (Math.Abs(vz * wx) + Math.Abs(vx * wz))
                        + Math.Abs(uz) * (Math.Abs(vx * wy) + Math.Abs(vy * wx));
            if (det > Bound * perm) return 1;
            if (-det > Bound * perm) return -1;
            return Integer(a, b, c, d);
        }

        int Integer(int a, int b, int c, int d)
        {
            if (_x is null)
            {
                // every finite float is m * 2^e with an integer m; all are scaled by the smallest exponent present
                int minExp = int.MaxValue;
                foreach (var p in pts)
                {
                    if (p.X != 0) minExp = Math.Min(minExp, Exponent(p.X));
                    if (p.Y != 0) minExp = Math.Min(minExp, Exponent(p.Y));
                    if (p.Z != 0) minExp = Math.Min(minExp, Exponent(p.Z));
                }
                _shift = minExp == int.MaxValue ? 0 : -minExp;
                var x = new System.Numerics.BigInteger[pts.Count * 3];
                for (int k = 0; k < pts.Count; k++)
                {
                    x[3 * k] = Scaled(pts[k].X); x[3 * k + 1] = Scaled(pts[k].Y); x[3 * k + 2] = Scaled(pts[k].Z);
                }
                _x = x;
            }
            var X = _x;
            System.Numerics.BigInteger D(int p, int q, int axis) => X[3 * p + axis] - X[3 * q + axis];
            var ux = D(b, a, 0); var uy = D(b, a, 1); var uz = D(b, a, 2);
            var vx = D(c, a, 0); var vy = D(c, a, 1); var vz = D(c, a, 2);
            var wx = D(d, a, 0); var wy = D(d, a, 1); var wz = D(d, a, 2);
            var det = ux * (vy * wz - vz * wy) + uy * (vz * wx - vx * wz) + uz * (vx * wy - vy * wx);
            return det.Sign;
        }

        // the exponent e of a float's lowest mantissa bit: f = m * 2^e with m an integer
        static int Exponent(float f)
        {
            int e = (BitConverter.SingleToInt32Bits(f) >> 23) & 0xFF;
            return (e == 0 ? 1 : e) - 127 - 23;
        }

        System.Numerics.BigInteger Scaled(float f)
        {
            if (f == 0) return System.Numerics.BigInteger.Zero;
            int bits = BitConverter.SingleToInt32Bits(f);
            long m = bits & 0x7FFFFF;
            if (((bits >> 23) & 0xFF) != 0) m |= 0x800000;
            var r = new System.Numerics.BigInteger(m) << (Exponent(f) + _shift);
            return bits < 0 ? -r : r;
        }
    }

    static float C(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>A coplanar set: its 2D convex polygon (monotone chain in the plane), fanned in both windings.</summary>
    static List<int> Planar(IReadOnlyList<Vector3> pts, Vector3 nrm, float eps)
    {
        var u = Vector3.Normalize(Vector3.Cross(MathF.Abs(nrm.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX, nrm));
        var v = Vector3.Cross(nrm, u);
        var order = Enumerable.Range(0, pts.Count)
            .Select(k => (K: k, X: Vector3.Dot(pts[k], u), Y: Vector3.Dot(pts[k], v)))
            .OrderBy(t => t.X).ThenBy(t => t.Y).ToList();
        double Cross((int K, float X, float Y) o, (int K, float X, float Y) p, (int K, float X, float Y) r)
            => (double)(p.X - o.X) * (r.Y - o.Y) - (double)(p.Y - o.Y) * (r.X - o.X);
        var hull = new List<(int K, float X, float Y)>();
        double tol = eps * eps;
        foreach (var t in order) { while (hull.Count >= 2 && Cross(hull[^2], hull[^1], t) <= tol) hull.RemoveAt(hull.Count - 1); hull.Add(t); }
        int lower = hull.Count + 1;
        for (int k = order.Count - 2; k >= 0; k--)
        {
            var t = order[k];
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], t) <= tol) hull.RemoveAt(hull.Count - 1);
            hull.Add(t);
        }
        hull.RemoveAt(hull.Count - 1);
        var idx = new List<int>();
        if (hull.Count < 3) return idx;
        for (int k = 1; k + 1 < hull.Count; k++) { idx.Add(hull[0].K); idx.Add(hull[k].K); idx.Add(hull[k + 1].K); }
        for (int k = 1; k + 1 < hull.Count; k++) { idx.Add(hull[0].K); idx.Add(hull[k + 1].K); idx.Add(hull[k].K); }
        return idx;
    }

    /// <summary>The hull offset outward by <paramref name="radius"/> (Havok's convex radius): each hull vertex moves to where
    /// its incident face planes, each pushed out by the radius, meet (least squares where more than three faces meet and do
    /// not agree; a box grows by exactly the radius on every face), the move capped at three radii at very sharp vertices. A
    /// vertex of a flat plate (faces in both windings) moves by the radius to both sides: the plate becomes a slab two radii
    /// thick (its rim is not grown in its plane). One or two points per vertex keep the face count of the core hull.</summary>
    public static (Vector3[] Vertices, List<int> Indices) Offset(IReadOnlyList<Vector3> pts, IReadOnlyList<int> idx, float radius, float eps = 1e-5f)
    {
        var outPts = GrownPoints(pts, idx, radius, eps);
        var (oi, _) = FromVertices(outPts, eps);
        // keep only the hull's own vertices
        var remap = new Dictionary<int, int>();
        var verts = new List<Vector3>();
        var idx2 = new List<int>(oi.Count);
        foreach (int k in oi)
        {
            if (!remap.TryGetValue(k, out int m)) { m = verts.Count; remap[k] = m; verts.Add(outPts[k]); }
            idx2.Add(m);
        }
        return (verts.ToArray(), idx2);
    }

    /// <summary>The points <see cref="Offset"/> takes the hull of: each vertex moved out by the radius along its faces' normals.</summary>
    public static List<Vector3> GrownPoints(IReadOnlyList<Vector3> pts, IReadOnlyList<int> idx, float radius, float eps = 1e-5f)
    {
        var dirsByVertex = new Dictionary<int, List<Vector3>>();
        var centre = Vector3.Zero; int nc = 0;
        foreach (int k in idx.Distinct()) { centre += pts[k]; nc++; }
        if (nc > 0) centre /= nc;
        for (int k = 0; k + 2 < idx.Count; k += 3)
        {
            var fn = Vector3.Cross(pts[idx[k + 1]] - pts[idx[k]], pts[idx[k + 2]] - pts[idx[k]]);
            if (fn.LengthSquared() <= 0) continue;
            fn = Vector3.Normalize(fn);
            // outward (a caller's winding may not be); a plate's centre lies on its plane and keeps both windings' normals
            if (Vector3.Dot(fn, (pts[idx[k]] + pts[idx[k + 1]] + pts[idx[k + 2]]) / 3f - centre) < -eps) fn = -fn;
            for (int j = 0; j < 3; j++)
            {
                if (!dirsByVertex.TryGetValue(idx[k + j], out var l)) dirsByVertex[idx[k + j]] = l = new List<Vector3>();
                if (!l.Any(d => Vector3.DistanceSquared(d, fn) < 1e-8f)) l.Add(fn);
            }
        }
        var outPts = new List<Vector3>();
        foreach (var (v, l) in dirsByVertex)
        {
            bool plate = l.Any(a => l.Any(b => Vector3.Dot(a, b) < -0.999f));
            if (plate) { foreach (var n in l) outPts.Add(pts[v] + radius * n); continue; }
            // the displacement d with n·d = radius for every incident face normal n, in the least-squares sense:
            // (sum n n^T) d = radius * sum n, lightly regularised so two faces (an edge) or one face give the minimum-norm
            // answer; exact wherever the faces at the vertex are consistent (three faces, a box corner)
            double a00 = 0, a01 = 0, a02 = 0, a11 = 0, a12 = 0, a22 = 0, b0 = 0, b1 = 0, b2 = 0;
            foreach (var n in l)
            {
                a00 += n.X * n.X; a01 += n.X * n.Y; a02 += n.X * n.Z; a11 += n.Y * n.Y; a12 += n.Y * n.Z; a22 += n.Z * n.Z;
                b0 += n.X; b1 += n.Y; b2 += n.Z;
            }
            const double lam = 1e-6;
            a00 += lam; a11 += lam; a22 += lam;
            double det = a00 * (a11 * a22 - a12 * a12) - a01 * (a01 * a22 - a12 * a02) + a02 * (a01 * a12 - a11 * a02);
            if (Math.Abs(det) < 1e-18) continue;
            double x = (b0 * (a11 * a22 - a12 * a12) - a01 * (b1 * a22 - a12 * b2) + a02 * (b1 * a12 - a11 * b2)) / det;
            double y = (a00 * (b1 * a22 - a12 * b2) - b0 * (a01 * a22 - a12 * a02) + a02 * (a01 * b2 - b1 * a02)) / det;
            double z = (a00 * (a11 * b2 - b1 * a12) - a01 * (a01 * b2 - b1 * a02) + b0 * (a01 * a12 - a11 * a02)) / det;
            var dv = new Vector3((float)x, (float)y, (float)z) * radius;
            if (dv.Length() > 3 * radius) dv = Vector3.Normalize(dv) * 3 * radius;   // a very sharp vertex: capped
            outPts.Add(pts[v] + dv);
        }
        return outPts;
    }
}
