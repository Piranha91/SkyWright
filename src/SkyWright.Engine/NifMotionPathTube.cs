using System.Numerics;
using NiflySharp.Blocks;
using static SkyWright.Engine.ExactDist;

using HousecarlCore;

namespace SkyWright.Engine;

// PATH TUBE (contactprobe pair-measure gaps, lane 3b, gap d). A NiPathInterpolator moves its node along the spline of its NiPosData
// keys. Whatever the percent keys and the follow / bank flags do, the node origin stays on that curve and every point under the node
// stays within the subtree radius R of the origin (any rotation). The tube is the curve grown by R: a list of capsules, each holding a
// piece of the curve for every tangent reading the files leave open (tangent as stored, and times the segment duration, as
// PathReach.PathHull reads them: the curves with tangents k m0, k m1 for k in [0, kmax]), each grown by R. The Hermite curve is linear
// in k, so on a parameter interval every such curve lies in the convex hull of the eight Bezier control points of the k = 0 and
// k = kmax sub-curves; the capsule holds those eight points. A partner farther than every capsule's radius from its axis is never met by
// the path-driven subtree. Nothing here says when the node is where: a partner inside the tube is cannot_tell, never a witness.
//
// STATUS: implemented (red at 7bbbe518).

/// <summary>A capsule: the segment A-B grown by Radius (model or world units, as the containing result says).</summary>
public sealed record TubePiece(Vector3 A, Vector3 B, double Radius);

/// <summary>Status <c>built</c>, <c>none</c> (no path-driven node) or <c>refused</c> (Reason names it). PathNodes names the
/// path-driven nodes the tube covers; shapes under them are covered by the tube and nowhere else.</summary>
public sealed record PathTubeResult(string Status, string? Reason, IReadOnlyList<TubePiece> Pieces, IReadOnlyList<string> PathNodes,
                                    IReadOnlyList<string> Labels);

public static partial class NifMotion
{
    public const string PathTubeLabel = "uncertified: path - the spline curve (both tangent readings) grown by its subtree's radius, any rotation";

    /// <summary>The model-space tube of every path-driven subtree (the model read with <see cref="MotionReadOptions.PathHull"/>), each
    /// capsule's curve-cover width at most <paramref name="tolerance"/> beyond what the two tangent readings force apart.</summary>
    public static PathTubeResult PathTube(byte[] nif, MotionModel model, Func<string, byte[]?>? read, double tolerance = 8)
    {
        PathTubeResult Refuse(string why) => new("refused", why, Array.Empty<TubePiece>(), Array.Empty<string>(), Array.Empty<string>());
        if (model.Status == "refused") return Refuse(model.Reason ?? "motion model refused");
        var rig = RigOf(nif, model, read);
        if (rig.PathNodes.Count == 0) return new("none", null, Array.Empty<TubePiece>(), Array.Empty<string>(), Array.Empty<string>());
        var labels = new List<string>();
        var pieces = new List<TubePiece>();
        var names = new List<string>();
        var blocks = rig.Nif.Blocks;
        var driven = new HashSet<string>(rig.Sources.Values.SelectMany(s => s.Nodes), StringComparer.Ordinal);
        foreach (var (node, pi) in rig.PathNodes)
        {
            string nm = node.Name?.String ?? "?";
            // a path node with no drawn shape (ShapeDrawing) under it moves nothing that can meet a partner: no tube for it
            if (!rig.Shapes.Any(sh => sh.Drawn && sh.Chain.Contains(node)))
            {
                labels.Add($"path on '{nm}' drives no visible shape: no tube");
                continue;
            }
            names.Add(nm);
            if (pi.PathData?.Index is not int pd || pd < 0 || pd >= blocks.Count || blocks[pd] is not NiPosData pos || pos.Data.Keys is not { Count: > 0 } keys)
                return Refuse($"path on '{nm}': no position keys");
            if (pi.PercentData?.Index is int fd && fd >= 0 && fd < blocks.Count && blocks[fd] is NiFloatData pct && pct.Data.Keys is { Count: > 0 } pk)
            {
                var vals = pk.Select(k => k.Value).ToList();
                if (vals.Any(v => !(v >= -1e-3f && v <= 1 + 1e-3f))) return Refuse($"path on '{nm}': " + PathReach.PercentRefusal(vals));
                if (PathReach.PercentRefusal(vals) is not null && !labels.Contains(PathPercentClampLabel)) labels.Add(PathPercentClampLabel);
            }
            string kt = pos.Data.Interpolation.ToString();
            if (kt is not ("LINEAR_KEY" or "QUADRATIC_KEY" or "TBC_KEY")) return Refuse($"path on '{nm}': key type {kt} is not in the path model");
            var parent = rig.Nif.GetParentNode(node) as NiAVObject;
            if (parent is not null && rig.Chain(parent).Any(p => p.Name?.String is { } pn && driven.Contains(pn)))
                return Refuse($"path on '{nm}': its parent chain moves");
            var px = parent is null ? NodeXf.Identity : rig.Model(parent, null, 0, false);
            double R = PathSubtreeRadius(rig, node, out var rwhy, drawnOnly: true);
            if (rwhy is not null) return Refuse($"path on '{nm}': {rwhy}");
            double ps = Math.Abs(px.S);
            // both tangent readings (out/in as stored, and swapped), as PathBox holds them
            var ka = keys.Select(k => new PathKey(k.Time, k.Value, k.Forward, k.Backward, k.TBC is { } tb ? tb.T : 0, k.TBC is { } tc ? tc.C : 0, k.TBC is { } tq ? tq.B : 0)).ToList();
            var kb = keys.Select(k => new PathKey(k.Time, k.Value, k.Backward, k.Forward, k.TBC is { } tb ? tb.T : 0, k.TBC is { } tc ? tc.C : 0, k.TBC is { } tq ? tq.B : 0)).ToList();
            void Emit(Vector3 a, Vector3 b, double w)
            {
                var A = px.Apply(a); var B = px.Apply(b);
                double mag = 1 + Math.Max(A.Length(), B.Length());
                pieces.Add(new TubePiece(A, B, (w + R) * ps + 1e-5 * mag));
            }
            if (keys.Count == 1) { Emit(ka[0].Value, ka[0].Value, 0); continue; }
            for (int i = 0; i + 1 < keys.Count; i++)
            {
                if (kt == "LINEAR_KEY") { Emit(ka[i].Value, ka[i + 1].Value, 0); continue; }
                foreach (var kk in new[] { ka, kb })
                {
                    var (m0, m1, kmax) = PathReach.SegmentTangents(kk, kt, i);
                    var p0 = kk[i].Value; var p1 = kk[i + 1].Value;
                    // k = 0: the chord's Bezier control points; k = kmax: the fullest reading
                    var c0 = new[] { p0, p0, p1, p1 };
                    var c1 = new[] { p0, p0 + kmax * m0 / 3, p1 - kmax * m1 / 3, p1 };
                    CoverFamily(c0, c1, tolerance, 0, Emit);
                }
            }
        }
        if (pieces.Count == 0) return new("none", "no path node drives a visible shape", Array.Empty<TubePiece>(), names, labels);
        if (!labels.Contains(PathTubeLabel)) labels.Add(PathTubeLabel);
        return new("built", null, pieces, names, labels);
    }

    // Cover the family of cubic Beziers between control polygons c0 and c1 (every convex combination, same parameter) by capsules.
    static void CoverFamily(Vector3[] c0, Vector3[] c1, double tolerance, int depth, Action<Vector3, Vector3, double> emit)
    {
        var a = (c0[0] + c1[0]) / 2; var b = (c0[3] + c1[3]) / 2;
        double w = 0;
        foreach (var p in c0.Concat(c1)) w = Math.Max(w, SegDistance(p, a, b));
        // the width the two readings force at this piece: half their largest separation at the ends and middle
        double forced = Math.Max(Vector3.Distance(c0[0], c1[0]), Math.Max(Vector3.Distance(c0[3], c1[3]), Vector3.Distance(Mid(c0), Mid(c1)))) / 2;
        if (w <= forced + tolerance || depth >= 18) { emit(a, b, w); return; }
        var (l0, r0) = Split(c0); var (l1, r1) = Split(c1);
        CoverFamily(l0, l1, tolerance, depth + 1, emit);
        CoverFamily(r0, r1, tolerance, depth + 1, emit);
    }

    static Vector3 Mid(Vector3[] c) => (c[0] + 3 * c[1] + 3 * c[2] + c[3]) / 8;

    static (Vector3[] L, Vector3[] R) Split(Vector3[] c)
    {
        var p01 = (c[0] + c[1]) / 2; var p12 = (c[1] + c[2]) / 2; var p23 = (c[2] + c[3]) / 2;
        var p012 = (p01 + p12) / 2; var p123 = (p12 + p23) / 2; var m = (p012 + p123) / 2;
        return (new[] { c[0], p01, p012, m }, new[] { m, p123, p23, c[3] });
    }

    static double SegDistance(Vector3 p, Vector3 a, Vector3 b)
    {
        DV P = D(p), A = D(a), B = D(b), ab = B - A;
        double l = Dot(ab, ab);
        double t = l > 0 ? Math.Clamp(Dot(P - A, ab) / l, 0, 1) : 0;
        return (P - (A + ab * t)).Len;
    }

    /// <summary>What a path model holds outside its path-driven subtrees, as a static model-space surface: render = the drawn
    /// shapes (ShapeDrawing) not under a path node (tagged by material); collision = refused unless the model has no solid collision
    /// (then <c>no_solid_collision</c>), since a body's place under a path node is not tracked. Status <c>measured</c>, <c>empty</c>
    /// (nothing outside the subtrees) or <c>refused</c> (a shape outside them moves, or the geometry cannot be read).</summary>
    public static TaggedSurface OutsidePathSurface(byte[] nif, MotionModel pathModel, PathTubeResult tube, VolumeGeometry geometry, Func<string, byte[]?>? read)
    {
        TaggedSurface Refuse(string why) => new("refused", why, null, null);
        if (tube.Status != "built") return Refuse(tube.Reason ?? "path tube not built");
        if (geometry == VolumeGeometry.Collision)
        {
            var c = MeshSurface.ModelSurface(nif, VolumeGeometry.Collision);
            return c.Status == "refused" && c.Reason == "no_solid_collision" ? c : Refuse("path model with solid collision: a body's place under a path node is not in the tube model");
        }
        var rig = RigOf(nif, pathModel, read);
        var pathNodes = new HashSet<NiAVObject>(rig.PathNodes.Select(x => x.Node), ReferenceEqualityComparer.Instance);
        var outside = rig.Shapes.Where(s => s.Drawn && !s.Chain.Any(pathNodes.Contains)).Select(s => s.Index).ToHashSet();
        foreach (var ms in pathModel.Shapes.Where(s => outside.Contains(s.ShapeIndex) && s.Mover != "static"))
            return Refuse($"shape '{ms.Name}' moves outside the path subtree: not combined with the tube");
        if (outside.Count == 0) return new("empty", null, null, null);
        var g = NifGeometry.MeasureVisibleRender(nif, includeTriangles: true);
        if (g.Model is not { Soup: { } soup, SoupShapeOfTriangle: { } map }) return Refuse($"render_unreadable: {g.Error}");
        var materials = MeshVolume.ShapeMaterials(nif);
        if (materials is null || materials.Count != g.Model.Shapes.Count) return Refuse("material: the NIF could not be re-read for its materials");
        var keep = Enumerable.Range(0, soup.TriangleCount).Where(t => outside.Contains(map[t])).ToList();
        if (keep.Count == 0) return new("empty", null, null, null);
        var idx = new List<int>(keep.Count * 3); var tags = new string[keep.Count];
        for (int i = 0; i < keep.Count; i++)
        {
            int t = keep[i];
            idx.Add(soup.Indices[3 * t]); idx.Add(soup.Indices[3 * t + 1]); idx.Add(soup.Indices[3 * t + 2]);
            tags[i] = materials[map[t]].Class;
        }
        return new("measured", null, new TriangleSoup(soup.Vertices, idx.ToArray()), tags);
    }

    /// <summary>The tube placed by its reference's transform (SpatialOverlap's placement); radii scale with |scale|.</summary>
    public static PathTubeResult PlaceTube(PathTubeResult tube, Vec3 position, Vec3 rotation, float scale)
    {
        if (tube.Status != "built") return tube;
        var ends = new Vector3[tube.Pieces.Count * 2];
        for (int i = 0; i < tube.Pieces.Count; i++) { ends[2 * i] = tube.Pieces[i].A; ends[2 * i + 1] = tube.Pieces[i].B; }
        var w = SpatialOverlap.TransformSoup(new TriangleSoup(ends, Array.Empty<int>()), position, rotation, scale).Vertices;
        var placed = new List<TubePiece>(tube.Pieces.Count);
        for (int i = 0; i < tube.Pieces.Count; i++)
        {
            double mag = 1 + Math.Max(w[2 * i].Length(), w[2 * i + 1].Length());
            placed.Add(new TubePiece(w[2 * i], w[2 * i + 1], tube.Pieces[i].Radius * Math.Abs(scale) + 1e-6 * mag));   // + float placement rounding
        }
        return tube with { Pieces = placed };
    }
}

public static partial class SweptGeometry
{
    /// <summary>The tube against a partner that moves too (its placed swept region): never only when every sample is farther from
    /// every capsule than the capsule's radius grown by that sample's certified displacement bound; otherwise cannot_tell (the deepest
    /// reach over the samples), or refused. The two clocks need not be linked: the tube holds the path subtree at every time.</summary>
    public static SweptDecision ContactTubeSwept(PathTubeResult placedTube, SweptRegion placedPartner)
    {
        if (placedTube.Status != "built") return new("refused", placedTube.Reason ?? "path tube not built", null, null, null, 0, false, null, null);
        if (placedPartner.Status != "built" || placedPartner.Samples.Count == 0)
            return new("refused", "partner: " + (placedPartner.Reason ?? "swept region not built"), null, null, null, 0, false, null, null);
        SweptDecision? worst = null;
        foreach (var s in placedPartner.Samples)
        {
            var grown = placedTube with { Pieces = placedTube.Pieces.Select(p => p with { Radius = p.Radius + s.Delta }).ToList() };
            var d = ContactTube(grown, s.Soup, s.Tags);
            if (d.Decision == "refused") return d;
            if (d.Decision == "cannot_tell" && (worst is null || d.Width > worst.Width))
                worst = d with { Reason = $"partner at {s.AnimationId}@{s.Time} (displacement bound {s.Delta:0.###} u): {d.Reason}" };
        }
        return worst ?? new("never", null, null, null, NifMotion.PathTubeLabel, 0, placedPartner.Converged, null, null);
    }

    /// <summary>never (every partner triangle lies farther than each capsule's radius from its axis) or cannot_tell (some triangle
    /// lies within a capsule; Width = the deepest such reach), or refused. Never a witness: the tube says where the subtree can be,
    /// not when.</summary>
    public static SweptDecision ContactTube(PathTubeResult placedTube, TriangleSoup partner, string[]? partnerTags)
    {
        if (placedTube.Status != "built") return new("refused", placedTube.Reason ?? "path tube not built", null, null, null, 0, false, null, null);
        if (partner is null || partner.TriangleCount == 0) return new("refused", "partner: empty_mesh", null, null, null, 0, false, null, null);
        // the partner's undrawn triangles (ShapeDrawing) cannot be met: only its drawn view is tested
        (partner, _) = MeshSurface.Drawn(partner, partnerTags);
        int n = partner.TriangleCount;
        var lo = new Vector3[n]; var hi = new Vector3[n];
        for (int t = 0; t < n; t++)
        {
            var a = partner.Vertices[partner.Indices[3 * t]]; var b = partner.Vertices[partner.Indices[3 * t + 1]]; var c = partner.Vertices[partner.Indices[3 * t + 2]];
            if (!float.IsFinite(a.X + a.Y + a.Z + b.X + b.Y + b.Z + c.X + c.Y + c.Z)) return new("refused", "partner: nonfinite_vertex", null, null, null, 0, false, null, null);
            lo[t] = Vector3.Min(a, Vector3.Min(b, c)); hi[t] = Vector3.Max(a, Vector3.Max(b, c));
        }
        double deepest = double.NegativeInfinity; int pieceHit = -1;
        for (int k = 0; k < placedTube.Pieces.Count; k++)
        {
            var cp = placedTube.Pieces[k];
            var r = (float)cp.Radius;
            var clo = Vector3.Min(cp.A, cp.B) - new Vector3(r + 1); var chi = Vector3.Max(cp.A, cp.B) + new Vector3(r + 1);
            for (int t = 0; t < n; t++)
            {
                if (hi[t].X < clo.X || lo[t].X > chi.X || hi[t].Y < clo.Y || lo[t].Y > chi.Y || hi[t].Z < clo.Z || lo[t].Z > chi.Z) continue;
                var (P, Q) = SegTri(D(cp.A), D(cp.B), D(partner.Vertices[partner.Indices[3 * t]]), D(partner.Vertices[partner.Indices[3 * t + 1]]), D(partner.Vertices[partner.Indices[3 * t + 2]]));
                double d = (P - Q).Len;
                // the ternary search lands within about 1e-9 of the minimum; allow it
                double reach = cp.Radius - d + 1e-6 * (1 + cp.Radius);
                if (reach >= 0 && reach > deepest) { deepest = reach; pieceHit = k; }
            }
        }
        if (pieceHit < 0) return new("never", null, null, null, NifMotion.PathTubeLabel, 0, true, null, null);
        return new("cannot_tell", $"a partner triangle lies inside the path tube (capsule {pieceHit} of {placedTube.Pieces.Count}, {deepest:0.###} u inside its radius): the path-driven subtree may reach it; the tube cannot say when",
                   null, null, NifMotion.PathTubeLabel, deepest, true, null, null);
    }
}
