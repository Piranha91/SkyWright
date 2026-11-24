using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

public sealed partial class TriangleGrid
{
    const long BucketCellLimit = 32768;

    static bool CellSpanExceedsLimit(int x0, int y0, int z0, int x1, int y1, int z1)
    {
        long x = (long)x1 - x0 + 1, y = (long)y1 - y0 + 1, z = (long)z1 - z0 + 1;
        if (x <= 0 || y <= 0 || z <= 0) throw new ArgumentException("triangle_grid_inverted_cell_range");
        // Bound each product before multiplying, including full signed-int endpoint spans.
        return x > BucketCellLimit || y > BucketCellLimit || z > BucketCellLimit ||
            x > BucketCellLimit / y || x * y > BucketCellLimit / z;
    }

    readonly record struct CellRange(int X0, int Y0, int Z0, int X1, int Y1, int Z1)
    {
        public CellRange Group(int power) => new(FloorGroup(X0, power), FloorGroup(Y0, power), FloorGroup(Z0, power), FloorGroup(X1, power), FloorGroup(Y1, power), FloorGroup(Z1, power));
        public bool Intersects(in CellRange b) => X0 <= b.X1 && X1 >= b.X0 && Y0 <= b.Y1 && Y1 >= b.Y0 && Z0 <= b.Z1 && Z1 >= b.Z0;
        static int FloorGroup(int value, int power) => checked((int)((long)value >> power));
    }

    // The cell table: each occupied cell's triangles in ascending order, flat. Fixed once built.
    sealed class CellTable
    {
        public static readonly CellTable Empty = new(new CellRange(0, 0, 0, -1, -1, -1), [], null, 0, 0, null, [], [], 0);
        public readonly CellRange Span;      // the range of occupied cells; X1 < X0 when there are none
        readonly int[]? _dense;              // start per cell of the span, plus an end, when the span is small
        readonly int _sy, _sz;
        readonly CellKey[]? _keys;           // sorted occupied cells, otherwise
        readonly int[] _starts, _items;
        public int CellCount { get; }
        /// <summary>The size the older per-cell lists would have charged; a cache's decisions rest on this number.</summary>
        public long ChargedBytes { get; }

        readonly record struct CellKey(int X, int Y, int Z) : IComparable<CellKey>
        {
            public int CompareTo(CellKey o)
            {
                int c = X.CompareTo(o.X); if (c != 0) return c;
                c = Y.CompareTo(o.Y); return c != 0 ? c : Z.CompareTo(o.Z);
            }
        }
        readonly record struct Incidence(CellKey Cell, int T) : IComparable<Incidence>
        {
            public int CompareTo(Incidence o) { int c = Cell.CompareTo(o.Cell); return c != 0 ? c : T.CompareTo(o.T); }
        }

        CellTable(CellRange span, int[] items, int[]? dense, int sy, int sz, CellKey[]? keys, int[] starts, int[] counts, int cellCount)
        {
            Span = span; _items = items; _dense = dense; _sy = sy; _sz = sz; _keys = keys; _starts = starts; CellCount = cellCount;
            long bytes = cellCount * 48L;
            foreach (int n in counts) if (n > 0) bytes += 56 + ListCapacity(n) * 4L;
            ChargedBytes = bytes;
        }

        // List<int> grows from 4 by doubling.
        static long ListCapacity(int n) => n <= 4 ? 4 : (long)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)n);

        /// <summary>Every triangle in the cells of its range grouped by <paramref name="power"/>, but for
        /// <paramref name="omit"/>, whose ids ascend.</summary>
        public static CellTable Build(CellRange[] ranges, int power, IReadOnlyList<int> omit)
        {
            long x0 = long.MaxValue, y0 = long.MaxValue, z0 = long.MaxValue, x1 = long.MinValue, y1 = long.MinValue, z1 = long.MinValue, total = 0;
            for (int t = 0, o = 0; t < ranges.Length; t++)
            {
                if (o < omit.Count && omit[o] == t) { o++; continue; }
                var r = ranges[t].Group(power);
                x0 = Math.Min(x0, r.X0); y0 = Math.Min(y0, r.Y0); z0 = Math.Min(z0, r.Z0);
                x1 = Math.Max(x1, r.X1); y1 = Math.Max(y1, r.Y1); z1 = Math.Max(z1, r.Z1);
                total = checked(total + ((long)r.X1 - r.X0 + 1) * ((long)r.Y1 - r.Y0 + 1) * ((long)r.Z1 - r.Z0 + 1));
            }
            if (total == 0) return Empty;
            var span = new CellRange((int)x0, (int)y0, (int)z0, (int)x1, (int)y1, (int)z1);
            long sx = x1 - x0 + 1, sy = y1 - y0 + 1, sz = z1 - z0 + 1, room = Math.Min(Array.MaxLength - 1L, Math.Max(256L, 4 * total));
            bool dense = sx <= room && sy <= room && sz <= room && sx * sy <= room && sx * sy * sz <= room;
            return dense ? BuildDense(ranges, power, omit, span, (int)(sx * sy * sz), (int)sy, (int)sz, checked((int)total))
                : BuildSorted(ranges, power, omit, span, checked((int)total));
        }

        static CellTable BuildDense(CellRange[] ranges, int power, IReadOnlyList<int> omit, CellRange span, int cells, int sy, int sz, int total)
        {
            var counts = new int[cells]; int occupied = 0;
            for (int t = 0, o = 0; t < ranges.Length; t++)
            {
                if (o < omit.Count && omit[o] == t) { o++; continue; }
                var r = ranges[t].Group(power);
                for (int x = r.X0; x <= r.X1; x++)
                    for (int y = r.Y0; y <= r.Y1; y++)
                    {
                        int row = ((x - span.X0) * sy + (y - span.Y0)) * sz - span.Z0;
                        for (int z = r.Z0; z <= r.Z1; z++) if (counts[row + z]++ == 0) occupied++;
                    }
            }
            var starts = new int[cells + 1];
            for (int c = 0; c < cells; c++) starts[c + 1] = starts[c] + counts[c];
            var fill = new int[cells]; Array.Copy(starts, fill, cells);
            var items = new int[total];
            for (int t = 0, o = 0; t < ranges.Length; t++)
            {
                if (o < omit.Count && omit[o] == t) { o++; continue; }
                var r = ranges[t].Group(power);
                for (int x = r.X0; x <= r.X1; x++)
                    for (int y = r.Y0; y <= r.Y1; y++)
                    {
                        int row = ((x - span.X0) * sy + (y - span.Y0)) * sz - span.Z0;
                        for (int z = r.Z0; z <= r.Z1; z++) items[fill[row + z]++] = t;
                    }
            }
            return new CellTable(span, items, starts, sy, sz, null, [], counts, occupied);
        }

        static CellTable BuildSorted(CellRange[] ranges, int power, IReadOnlyList<int> omit, CellRange span, int total)
        {
            var all = new Incidence[total]; int k = 0;
            for (int t = 0, o = 0; t < ranges.Length; t++)
            {
                if (o < omit.Count && omit[o] == t) { o++; continue; }
                var r = ranges[t].Group(power);
                for (long x = r.X0; x <= r.X1; x++)
                    for (long y = r.Y0; y <= r.Y1; y++)
                        for (long z = r.Z0; z <= r.Z1; z++)
                            all[k++] = new Incidence(new CellKey((int)x, (int)y, (int)z), t);
            }
            Array.Sort(all);
            var keys = new List<CellKey>(); var starts = new List<int>(); var items = new int[total];
            for (int i = 0; i < total; i++)
            {
                if (i == 0 || all[i].Cell != all[i - 1].Cell) { keys.Add(all[i].Cell); starts.Add(i); }
                items[i] = all[i].T;
            }
            var counts = new int[keys.Count];
            for (int c = 0; c < counts.Length; c++) counts[c] = (c + 1 < counts.Length ? starts[c + 1] : total) - starts[c];
            starts.Add(total);
            return new CellTable(span, items, null, 0, 0, keys.ToArray(), starts.ToArray(), counts, keys.Count);
        }

        /// <summary>The triangles in this cell, ascending; none for an empty cell or one outside the span.</summary>
        public ReadOnlySpan<int> At(int x, int y, int z)
        {
            if (x < Span.X0 || x > Span.X1 || y < Span.Y0 || y > Span.Y1 || z < Span.Z0 || z > Span.Z1) return default;
            if (_dense is { } d)
            {
                int c = ((x - Span.X0) * _sy + (y - Span.Y0)) * _sz + (z - Span.Z0);
                return _items.AsSpan(d[c], d[c + 1] - d[c]);
            }
            var key = new CellKey(x, y, z); var keys = _keys!;
            for (int lo = 0, hi = keys.Length - 1; lo <= hi;)
            {
                int mid = lo + (hi - lo) / 2, cmp = keys[mid].CompareTo(key);
                if (cmp == 0) return _items.AsSpan(_starts[mid], _starts[mid + 1] - _starts[mid]);
                if (cmp < 0) lo = mid + 1; else hi = mid - 1;
            }
            return default;
        }
    }

    // Used only when a face cannot fit in the fixed-cell table. The ordinary path
    // remains unchanged. Overflow faces merge at their first conceptual legacy cell,
    // preserving first-hit ties and callback order without replicating those faces.
    // Fixed once built; each query holds its own candidates, so queries may run at once.
    sealed class CompleteQueryOrder
    {
        readonly TriangleGrid _owner;
        readonly CellRange[] _ranges;
        int[] _overflow;
        int _power;
        public int OverflowCount => _overflow.Length;
        public long AdditionalApproxBytes => _ranges.Length * 24L + _ranges.Length * 4L + _overflow.Length * 4L + 256;

        public CompleteQueryOrder(TriangleGrid owner, int[] overflow)
        {
            _owner = owner; _overflow = overflow; _ranges = owner._ranges;
        }

        public TriangleGridSizing Populate(long target)
        {
            bool met = false;
            for (_power = 0; _power <= 31; _power++)
                if (Estimate(_power) <= target) { met = true; break; }
            if (!met) _power = 31;
            var overflow = new List<int>(); var omitted = new List<int>(); long incidences = 0;
            for (int t = 0; t < _ranges.Length; t++)
            {
                if (Incidences(_ranges[t]) > BucketCellLimit) omitted.Add(t);
                var r = _ranges[t].Group(_power); long count = Incidences(r);
                if (!met || count > BucketCellLimit) { overflow.Add(t); continue; }
                incidences = checked(incidences + count);
            }
            _overflow = overflow.ToArray(); _owner._cells = CellTable.Build(_ranges, _power, overflow);
            long bucket = _owner._cells.ChargedBytes;
            return new(_owner._cell, (double)_owner._cell * (1L << _power), _power, target,
                Estimate(_power), bucket, incidences, _owner._cells.CellCount, omitted.ToArray(), (int[])_overflow.Clone(),
                true, met, met ? null : "unattainable_bucket_target", 0);
        }
        static long Incidences(CellRange r)
        {
            long x = (long)r.X1 - r.X0 + 1, y = (long)r.Y1 - r.Y0 + 1, z = (long)r.Z1 - r.Z0 + 1;
            if (x <= 0 || y <= 0 || z <= 0) throw new ArgumentException("triangle_grid_inverted_cell_range");
            if (x > BucketCellLimit || y > BucketCellLimit || z > BucketCellLimit || x > BucketCellLimit / y)
                return BucketCellLimit + 1;
            long xy = x * y; return xy > BucketCellLimit / z ? BucketCellLimit + 1 : xy * z;
        }
        long Estimate(int power)
        {
            long total = 0;
            foreach (var r in _ranges)
            {
                long n = Incidences(r.Group(power));
                if (n > BucketCellLimit) continue; // Complete overflow, separately charged.
                if (n > (long.MaxValue - total) / 120) return long.MaxValue;
                total += 120 * n;
            }
            return total;
        }

        int Axis(float value) => _owner.CellAxis(value);
        CellRange Range(float x0, float y0, float z0, float x1, float y1, float z1)
        {
            var r = new CellRange(Axis(x0), Axis(y0), Axis(z0), Axis(x1), Axis(y1), Axis(z1));
            if (r.X0 > r.X1 || r.Y0 > r.Y1 || r.Z0 > r.Z1) throw new ArgumentException("triangle_grid_inverted_query_range");
            return r;
        }
        public Candidates Box(Aabb box, float reach) => Gather(CheckBox(box, reach), default, false);
        // The domain checks of a box query alone.
        public CellRange CheckBox(Aabb box, float reach)
        {
            if (!float.IsFinite(reach) || reach < 0) throw new ArgumentOutOfRangeException(nameof(reach), "triangle_grid_invalid_query_reach");
            return Range(box.Min.X - reach, box.Min.Y - reach, box.Min.Z - reach,
                         box.Max.X + reach, box.Max.Y + reach, box.Max.Z + reach);
        }
        public Candidates Point(Vector3 p, float reach)
        {
            if (!float.IsFinite(reach) || reach < 0) throw new ArgumentOutOfRangeException(nameof(reach), "triangle_grid_invalid_query_reach");
            (int X, int Y, int Z) center = (Axis(p.X), Axis(p.Y), Axis(p.Z));
            float rf = MathF.Ceiling(reach / _owner._cell);
            if (!float.IsFinite(rf) || rf > int.MaxValue - 2.0) throw new ArgumentOutOfRangeException(nameof(reach), "triangle_grid_query_ring_out_of_domain");
            int r = checked((int)rf + 1);
            if ((long)center.X - r < int.MinValue || (long)center.X + r > int.MaxValue ||
                (long)center.Y - r < int.MinValue || (long)center.Y + r > int.MaxValue ||
                (long)center.Z - r < int.MinValue || (long)center.Z + r > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(p), "triangle_grid_query_range_out_of_domain");
            return Gather(new(center.X - r, center.Y - r, center.Z - r, center.X + r, center.Y + r, center.Z + r), center, true);
        }
        Candidates Gather(CellRange query, (int X, int Y, int Z) center, bool point)
        {
            var found = new Candidates(_ranges, query, center, point);
            var coarse = query.Group(_power);
            if (CellSpanExceedsLimit(coarse.X0, coarse.Y0, coarse.Z0, coarse.X1, coarse.Y1, coarse.Z1))
            {
                for (int t = 0; t < _ranges.Length; t++) found.Add(t);
            }
            else
            {
                var s = _owner._cells.Span;
                long xa = Math.Max(coarse.X0, s.X0), xb = Math.Min(coarse.X1, s.X1), ya = Math.Max(coarse.Y0, s.Y0), yb = Math.Min(coarse.Y1, s.Y1), za = Math.Max(coarse.Z0, s.Z0), zb = Math.Min(coarse.Z1, s.Z1);
                for (long x = xa; x <= xb; x++)
                    for (long y = ya; y <= yb; y++)
                        for (long z = za; z <= zb; z++)
                            foreach (int t in _owner._cells.At((int)x, (int)y, (int)z))
                            {
                                // A face sits in every coarse cell of its range; take it at the first this query reaches.
                                var g = _ranges[t].Group(_power);
                                if (x == Math.Max(g.X0, coarse.X0) && y == Math.Max(g.Y0, coarse.Y0) && z == Math.Max(g.Z0, coarse.Z0)) found.Add(t);
                            }
                foreach (int t in _overflow) found.Add(t);
            }
            found.Sort();
            return found;
        }
    }

    // One query's candidates in visit order, on a pooled buffer; the order is total, so the sort has one answer.
    sealed class Candidates : IComparer<int>, IDisposable
    {
        readonly CellRange[] _ranges;
        readonly CellRange _query;
        readonly (int X, int Y, int Z) _center;
        readonly bool _point;
        readonly int[] _items;
        public int Count { get; private set; }
        public int this[int index] => _items[index];

        public Candidates(CellRange[] ranges, CellRange query, (int X, int Y, int Z) center, bool point)
        {
            _ranges = ranges; _query = query; _center = center; _point = point;
            _items = System.Buffers.ArrayPool<int>.Shared.Rent(ranges.Length);
        }
        public void Dispose() => System.Buffers.ArrayPool<int>.Shared.Return(_items);
        public void Add(int t) { if (_ranges[t].Intersects(_query)) _items[Count++] = t; }
        public void Sort() => Array.Sort(_items, 0, Count, this);
        public int Ring(int t) => RingOf(_ranges[t], _center.X, _center.Y, _center.Z);
        (int Ring, long X, long Y, long Z) Key(int t)
        {
            var b = _ranges[t]; int r = _point ? Ring(t) : 0;
            return _point ? (r, Math.Max(b.X0, (long)_center.X - r), Math.Max(b.Y0, (long)_center.Y - r), Math.Max(b.Z0, (long)_center.Z - r))
                : (0, Math.Max(b.X0, _query.X0), Math.Max(b.Y0, _query.Y0), Math.Max(b.Z0, _query.Z0));
        }
        public int Compare(int a, int b)
        {
            var ka = Key(a); var kb = Key(b);
            int c = ka.Ring.CompareTo(kb.Ring); if (c != 0) return c;
            c = ka.X.CompareTo(kb.X); if (c != 0) return c;
            c = ka.Y.CompareTo(kb.Y); if (c != 0) return c;
            c = ka.Z.CompareTo(kb.Z); return c != 0 ? c : a.CompareTo(b);
        }
    }

    (float Distance, int Triangle) NearestTriangleComplete(Vector3 a, Vector3 b, Vector3 c, float reach)
    {
        var qb = Aabb.Empty.Add(a).Add(b).Add(c); using var order = _completeOrder!.Box(qb, reach);
        float best = reach; int bestT = -1; float limit = SpatialOverlap.LimitSquared(best);
        for (int k = 0; k < order.Count; k++)
        {
            int t = order[k]; if (SpatialOverlap.BoxDistance(qb, _tb[t]) >= best) continue;
            var (ha, hb, hc) = GetTriangle(t); var d = SpatialOverlap.TriangleDistanceUnder(a, b, c, ha, hb, hc, limit);
            if (d < best) { best = d; bestT = t; if (best <= 0f) return (0f, bestT); limit = SpatialOverlap.LimitSquared(best); }
        }
        return (best, bestT);
    }
    void ForEachWithinComplete(Vector3 a, Vector3 b, Vector3 c, float band, Action<int, float, Vector3, Vector3, bool> visit)
    {
        var qb = Aabb.Empty.Add(a).Add(b).Add(c); using var order = _completeOrder!.Box(qb, band);
        for (int k = 0; k < order.Count; k++)
        {
            int t = order[k]; if (_n[t] == Vector3.Zero || SpatialOverlap.BoxDistance(qb, _tb[t]) > band) continue;
            var (ha, hb, hc) = GetTriangle(t);
            var d = SpatialOverlap.TriangleClosestPoints(a, b, c, ha, hb, hc, out var p, out var q, out var crosses);
            if (d <= band) visit(t, d, p, q, crosses);
        }
    }
    (float Distance, Vector3 Point, int Triangle) NearestPointComplete(Vector3 p, float reach)
    {
        using var order = _completeOrder!.Point(p, reach);
        float best = reach; int bestT = -1; var bestQ = Vector3.Zero; int previousRing = -1;
        for (int k = 0; k < order.Count; k++)
        {
            int t = order[k], r = order.Ring(t);
            if (r != previousRing) { if (bestT >= 0 && (r - 1) * _cell > best) break; previousRing = r; }
            if (SpatialOverlap.BoxDistance(Aabb.Empty.Add(p), _tb[t]) >= best) continue;
            var (a, b, c) = GetTriangle(t); var q = SpatialOverlap.ClosestPointOnTriangle(p, a, b, c);
            float d = (p - q).Length(); if (d < best) { best = d; bestT = t; bestQ = q; }
        }
        return bestT < 0 ? (float.NaN, Vector3.Zero, -1) : (best, bestQ, bestT);
    }
    (float Depth, Vector3 Normal) InsideDepthComplete(Vector3 p, float reach)
    {
        using var order = _completeOrder!.Point(p, reach);
        float best = reach; int bestT = -1; bool behindAll = true; var n = Vector3.Zero; int previousRing = -1;
        for (int k = 0; k < order.Count; k++)
        {
            int t = order[k], r = order.Ring(t);
            if (r != previousRing) { if (bestT >= 0 && (r - 1) * _cell > best) break; previousRing = r; }
            if (SpatialOverlap.BoxDistance(Aabb.Empty.Add(p), _tb[t]) > best + 1e-4f) continue;
            var (a, b, c) = GetTriangle(t); var q = SpatialOverlap.ClosestPointOnTriangle(p, a, b, c);
            float d = (p - q).Length(); if (_n[t] == Vector3.Zero) continue;
            bool behind = Vector3.Dot(p - q, _n[t]) < -1e-4f || (d < 1e-4f && Vector3.Dot(p - q, _n[t]) <= 0f);
            if (d < best - 1e-4f) { best = d; bestT = t; behindAll = behind; n = _n[t]; }
            else if (bestT >= 0 && MathF.Abs(d - best) <= 1e-4f) behindAll &= behind;
        }
        return bestT < 0 || !behindAll || best <= 1e-4f ? (0f, Vector3.Zero) : (best, n);
    }
}
