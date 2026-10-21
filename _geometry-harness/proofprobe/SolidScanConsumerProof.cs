// Known answers for the full self-intersection scan through its two probe consumers that validate a solid on their own:
// SweptGeometry.Volume (motionprobe sweep: each sample of a solid region) and MeshAnchor.TestPlaced (lightprobe: each
// component of an anchor's partner). One closed, connected, consistently wound 16-triangle box whose top fan is pushed through
// its bottom; only the fan x bottom triangle pairs cross. Split 8/8 along z, the bottom (and the lower side halves) sit in the
// left leaf and the fan in the right. W1 gives the bottom the lower triangle indices (the orientation the old scan skipped);
// W1c gives the fan the lower indices and must agree on any build.
using System.Numerics;
using HousecarlCore;

static class SolidScanConsumerProof
{
    const float H = 4f;
    const string SelfReason = "self_intersecting_or_self_touching_mesh";

    /// <summary>Bottom fan (4), sides (8), top fan to an apex below the bottom (4); fanFirst puts the top fan at indices 0-3.</summary>
    static TriangleSoup DentedBox(bool fanFirst, bool dent = true)
    {
        var b = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) };
        var t = b.Select(v => v with { Z = H }).ToArray();
        var c = new Vector3(0, 0, 0);
        var apex = dent ? new Vector3(0.31f, 0.17f, -1f) : new Vector3(0.31f, 0.17f, H + 1f);
        var v = new List<Vector3>(); var ix = new List<int>();
        int V(Vector3 p) { v.Add(p); return v.Count - 1; }
        int[] bi = b.Select(V).ToArray(), ti = t.Select(V).ToArray(); int ci = V(c), ai = V(apex);
        var bottom = Enumerable.Range(0, 4).Select(i => new[] { bi[(i + 1) % 4], bi[i], ci }).ToList();
        var sides = Enumerable.Range(0, 4).SelectMany(i => new[] { new[] { bi[i], bi[(i + 1) % 4], ti[(i + 1) % 4] }, new[] { bi[i], ti[(i + 1) % 4], ti[i] } }).ToList();
        var fan = Enumerable.Range(0, 4).Select(i => new[] { ti[i], ti[(i + 1) % 4], ai }).ToList();
        foreach (var tri in fanFirst ? fan.Concat(sides).Concat(bottom) : bottom.Concat(sides).Concat(fan)) ix.AddRange(tri);
        return new TriangleSoup(v.ToArray(), ix.ToArray());
    }

    static SweptRegion Region(TriangleSoup s) => new("built", null, VolumeGeometry.Render, Array.Empty<MotionAnimation>(),
        new[] { new SweptSample("w", 0f, s, null, 0.0) }, 0.0, true, true);

    public static void Run(Action<string, bool, string> check)
    {
        foreach (var (name, fanFirst) in new[]
        {
            ("solid scan W1: a solid sweep whose sample self-crosses with the lower indices in the left leaf is refused", false),
            ("solid scan W1c: a solid sweep whose sample self-crosses with the lower indices in the right leaf is refused", true),
        })
        {
            try
            {
                var v = SweptGeometry.Volume(Region(DentedBox(fanFirst)), null);
                check(name, v.Status == "refused" && v.Reason == "a sampled pose is not a closed solid", $"status={v.Status} reason={v.Reason}");
            }
            catch (Exception ex) { check(name, false, $"threw {ex.GetType().Name}: {ex.Message}"); }
        }
        foreach (var (name, fanFirst) in new[]
        {
            ("solid scan W2: an anchor in a partner component that self-crosses with the lower indices in the left leaf is undecided", false),
            ("solid scan W2c: an anchor in a partner component that self-crosses with the lower indices in the right leaf is undecided", true),
        })
        {
            try
            {
                var r = MeshAnchor.TestPlaced(DentedBox(fanFirst), null, new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f, new Vector3(0.9f, 0.9f, 0.5f), new BodyExtent(0, 10, 1));
                check(name, r.Point == "undecided" && r.PointReason == SelfReason, $"point={r.Point} reason={r.PointReason}");
            }
            catch (Exception ex) { check(name, false, $"threw {ex.GetType().Name}: {ex.Message}"); }
        }
        // Controls: the same box with its fan above the top (no crossing) is a valid solid on every build.
        try
        {
            var v = SweptGeometry.Volume(Region(DentedBox(false, dent: false)), null);
            check("solid scan W3: the same box without the dent sweeps as a solid", v.Status != "refused", $"status={v.Status} reason={v.Reason}");
            var r = MeshAnchor.TestPlaced(DentedBox(false, dent: false), null, new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f, new Vector3(0.9f, 0.9f, 0.5f), new BodyExtent(0, 10, 1));
            check("solid scan W3: an anchor inside the same box without the dent reads inside", r.Point == "inside", $"point={r.Point} reason={r.PointReason}");
        }
        catch (Exception ex) { check("solid scan W3: the same box without the dent sweeps as a solid", false, $"threw {ex.GetType().Name}: {ex.Message}"); }
    }
}
