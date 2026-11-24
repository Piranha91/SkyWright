using System.Numerics;
using HousecarlCore;
using SkyWright.Engine;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

// Motion rigs and undrawn shapes (2026-10-14): the motion reader's shapes answer to the one drawing predicate (ShapeDrawing), and
// the swept tests' geometry-only steps read the drawn view of both members. Plan: _claude/runs/2026-10-14-motion-rigs/GATE-A.md §3,
// reviewed in sessions/2026-10-14-motion-rigs-gate-a-review.md (design T). Cases MR-1 to MR-9, each with a control whose marker is
// renamed EditorMarker01 (drawn: the predicate is an exact name test). Red on 8a54eb4b, green after.
static partial class MotionProof
{
    const string MrMarker = ShapeDrawing.EditorMarkerName, MrRenamed = "EditorMarker01";
    static readonly Vec3 MrZ = new(0, 0, 0);

    // a quad in the plane y = 0 (x0..x1, z0..z1) and one in the plane x = x (y0..y1, z0..z1); horizontal quads are FlatQuad
    static TriangleSoup MrQuadY0(float x0, float x1, float z0, float z1) =>
        new(new[] { new Vector3(x0, 0, z0), new Vector3(x1, 0, z0), new Vector3(x1, 0, z1), new Vector3(x0, 0, z1) }, new[] { 0, 1, 2, 0, 2, 3 });
    static TriangleSoup MrQuadX(float x, float y0, float y1, float z0, float z1) =>
        new(new[] { new Vector3(x, y0, z0), new Vector3(x, y1, z0), new Vector3(x, y1, z1), new Vector3(x, y0, z1) }, new[] { 0, 1, 2, 0, 2, 3 });
    static string[] MrOpaque(TriangleSoup s) => Enumerable.Repeat(SurfaceMaterial.Opaque, s.TriangleCount).ToArray();

    /// <summary>Fixture R: sequence "Slide" (CLAMP, 1 s) moves N1 from x 0 to 100; under N2 a 10 u cube "Frame" at (-500,0,0) and
    /// the marker, a quad in y = 0, x 0..20, z -10..10.</summary>
    static byte[] MrRigR(string marker) =>
        ChainNif(null, new[] { ("Frame", "N2", Cube(-500, 0, 0, 5), false), (marker, "N2", MrQuadY0(0, 20, -10, 10), false) },
                 new[] { ("Slide", new[] { new FxKeys("N1", X0: 0, X1: 100) }, CycleType.CYCLE_CLAMP) });
    // the static partner of fixture R: x = 60, y and z -15..15; the marker crosses it for slide 40..60 along 20 u
    static readonly TriangleSoup MrP = MrQuadX(60, -15, 15, -15, 15);

    static SweptRegion MrRegion(byte[] nif) =>
        SweptGeometry.Place(SweptGeometry.Build(nif, NifMotion.Read(nif, null), null, VolumeGeometry.Render, new MotionOptions()), MrZ, MrZ, 1f);
    static string MrShow(SweptDecision d) => $"{d.Decision} cross={d.WitnessCrossingLower:0.###} width={d.Width:0.###} at {d.AnimationId}@{d.Time} reason={d.Reason}";

    /// <summary>A path rig: root -> "Bird" (NiPathInterpolator, LINEAR keys p0 at 0 to p1 at 1) and named shapes under Bird or the root.</summary>
    static byte[] MrPathNif(Vector3 p0, Vector3 p1, params (string Name, bool UnderBird, TriangleSoup Soup)[] shapes)
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var bird = new NiNode { Name = new NiStringRef("Bird"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33() };
        int bi = f.AddBlock(bird); root.Children.AddBlockRef(bi);
        foreach (var (name, under, soup) in shapes) (under ? bird : root).Children.AddBlockRef(f.AddBlock(Shape(name, soup)));
        var g = new KeyGroup<Vector3>
        {
            Interpolation = KeyType.LINEAR_KEY, NumKeys = 2,
            Keys = new() { new Key<Vector3> { Time = 0, Value = p0, Forward = Vector3.Zero, Backward = Vector3.Zero },
                           new Key<Vector3> { Time = 1, Value = p1, Forward = Vector3.Zero, Backward = Vector3.Zero } },
        };
        var pi = new NiPathInterpolator { PathData = new(f.AddBlock(new NiPosData { Data = g })), PercentData = new(-1) };
        var ctl = new NiTransformController { Interpolator = new(f.AddBlock(pi)), StartTime = 0, StopTime = 1, Frequency = 1 };
        ctl.Flags.Active = true; ctl.Target = new(bi); bird.Controller = new(f.AddBlock(ctl));
        return Save(f, "motion-rigs path");
    }
    static readonly TriangleSoup MrBody = Box(-2, -2, 48, 2, 2, 52);   // the 4 u box 50 u above Bird: subtree radius BirdR

    static (MotionModel Model, PathTubeResult Tube) MrPath(byte[] nif)
    {
        var m = NifMotion.Read(nif, null, null, new MotionReadOptions(PathHull: true));
        return (m, NifMotion.PathTube(nif, m, null));
    }

    /// <summary>MotionReview's path-tube decision against a static partner (MotionReview.cs, the "else" branch): the outside surface
    /// crossing or burying the partner at its stored pose is a witness; otherwise the tube's own decision.</summary>
    static (string Decision, double Crossing, string Detail) MrPathDecision(byte[] nif, TriangleSoup partner)
    {
        var (m, tube) = MrPath(nif);
        var outside = NifMotion.OutsidePathSurface(nif, m, tube, VolumeGeometry.Render, null);
        var td = SweptGeometry.ContactTube(NifMotion.PlaceTube(tube, MrZ, MrZ, 1f), partner, MrOpaque(partner));
        if (outside.Status != "measured") return (td.Decision, 0, $"outside {outside.Status} {outside.Reason}; tube {td.Decision}");
        var (sr, _) = MeshSurface.MeasurePlacedFramed(outside.Soup!, outside.Tags, MrZ, MrZ, 1f, partner, MrOpaque(partner), MrZ, MrZ, 1f, VolumeGeometry.Render);
        double cross = MeshSurface.OpaqueCrossing(sr)?.Lower ?? 0;
        bool buried = (sr.AInB?.Inside?.Lower ?? 0) > 0 || (sr.BInA?.Inside?.Lower ?? 0) > 0;
        string d = sr.Status != "measured" ? "cannot_tell" : cross > 0 || buried ? "witness" : td.Decision;
        return (d, cross, $"outside {outside.Status} ({outside.Soup?.TriangleCount} tris) crossing {cross:0.###}; tube {td.Decision}");
    }

    public static void RunMotionRigs(Action<string, bool, string> check)
    {
        // ================= fixture R: a clip rig whose marker crosses a static partner and whose drawn shape stays 455 u away =================
        // MR-1: the clip-only swept contact. Rejects counting the marker (witness, the base) and a fix that drops shapes by any rule
        // other than the name (the renamed control must still cross along 20 u). Also: the posed soup keeps the stored render soup's
        // triangle count, the marker's 2 triangles tagged not_drawn (the alpha reader's alignment).
        Case(check, "MR-1 clip rig: marker-only crossing is never; renamed marker witnesses 20 u; posed soup keeps 14 triangles, 2 not_drawn", () =>
        {
            var nif = MrRigR(MrMarker); var ren = MrRigR(MrRenamed);
            var a = SweptGeometry.Contact(MrRegion(nif), MrP, MrOpaque(MrP), false);
            var b = SweptGeometry.Contact(MrRegion(ren), MrP, MrOpaque(MrP), false);
            var posed = NifMotion.Pose(nif, NifMotion.Read(nif, null), "stored", 0f, VolumeGeometry.Render);
            int tris = posed.Soup?.TriangleCount ?? -1, nd = posed.Tags?.Count(t => t == SurfaceMaterial.NotDrawn) ?? -1;
            bool ok = a.Decision == "never" && b.Decision == "witness" && Math.Abs((b.WitnessCrossingLower ?? 0) - 20) < 0.01 && tris == 14 && nd == 2;
            return (ok, $"marker {MrShow(a)} | renamed {MrShow(b)} | posed {tris} tris, {nd} not_drawn");
        });
        // MR-2: the shared member loop (lane M) with the contact predicate. Rejects the predicate's clear test counting the marker.
        Case(check, "MR-2 member loop: marker-only crossing is never; renamed marker witness", () =>
        {
            MemberDecision D(byte[] nif)
            {
                var (pred, why) = MemberPredicates.Contact(MrP, MrOpaque(MrP), false, VolumeGeometry.Render, new MemberCapture());
                if (pred is null) throw new InvalidOperationException(why);
                return MemberMotion.Decide(nif, MemberMotion.Read(nif, null, Mstt), VolumeGeometry.Render, V0, V0, 1f, new MemberSweep(), pred, null);
            }
            var a = D(MrRigR(MrMarker)); var b = D(MrRigR(MrRenamed));
            return (a.Decision == "never" && b.Decision == "witness", $"marker {ShowMD(a)} | renamed {ShowMD(b)}");
        });
        // MR-3: the visible-contact test's never step. Rejects its step 1 reading the marker (base: not never).
        Case(check, "MR-3 visible contact: marker-only crossing is never; renamed marker not never", () =>
        {
            var a = SweptGeometry.ContactVisible(MrRegion(MrRigR(MrMarker)), null, MrP, MrOpaque(MrP), null, false);
            var b = SweptGeometry.ContactVisible(MrRegion(MrRigR(MrRenamed)), null, MrP, MrOpaque(MrP), null, false);
            return (a.Decision == "never" && b.Decision != "never", $"marker {a.Decision} {a.Reason} | renamed {b.Decision} {b.Reason}");
        });

        // ================= fixture Q: a path rig whose marker sits under the root, outside the path subtree (the hawk's case) =================
        // Bird's path runs x 1000..1100 (radius 52.08): the tube is far from P2 (x = 10). The marker (y = 0, x 0..20, z -10..10)
        // crosses P2 along x = 10, z -10..10: 20 u.
        static byte[] Q(string marker) => MrPathNif(new(1000, 0, 0), new(1100, 0, 0), ("Body", true, MrBody), (marker, false, MrQuadY0(0, 20, -10, 10)));
        var p2 = MrQuadX(10, -15, 15, -15, 15);
        // MR-4: the outside surface holds drawn shapes only. Rejects counting the marker (measured, the base).
        Case(check, "MR-4 path rig, marker outside the subtree: outside surface empty; renamed marker measured, 2 triangles", () =>
        {
            TaggedSurface O(byte[] nif) { var (m, t) = MrPath(nif); return NifMotion.OutsidePathSurface(nif, m, t, VolumeGeometry.Render, null); }
            var a = O(Q(MrMarker)); var b = O(Q(MrRenamed));
            return (a.Status == "empty" && b.Status == "measured" && b.Soup?.TriangleCount == 2, $"marker {a.Status} {a.Reason} | renamed {b.Status} {b.Soup?.TriangleCount}");
        });
        // MR-5: the path-tube decision as MotionReview composes it. Rejects the marker's stored-pose crossing (witness, the base).
        Case(check, "MR-5 path rig, marker outside the subtree: decision never (tube never); renamed marker witness, crossing 20 u", () =>
        {
            var a = MrPathDecision(Q(MrMarker), p2); var b = MrPathDecision(Q(MrRenamed), p2);
            return (a.Decision == "never" && b.Decision == "witness" && Math.Abs(b.Crossing - 20) < 0.01, $"marker {a.Detail} -> {a.Decision} | renamed {b.Detail} -> {b.Decision}");
        });

        // ================= fixture S: a path rig whose marker sits under the path node =================
        // Bird's path runs x 0..100. Body: radius 52.0769; marker (horizontal, z = 200, x and y -10..10): radius 200.4994. The
        // partner P3 (horizontal, z = 120, x 40..60, y -10..10) is 120 u from the axis: inside 200.5, outside 52.08 + a cover of 8.
        static byte[] S(string marker) => MrPathNif(new(0, 0, 0), new(100, 0, 0), ("Body", true, MrBody), (marker, true, FlatQuad(-10, -10, 10, 10, 200)));
        var p3 = FlatQuad(40, -10, 60, 10, 120);
        // MR-6: the tube's radius holds drawn shapes only. Rejects the marker widening the tube (cannot_tell, the base).
        Case(check, "MR-6 path rig, marker under the path node: tube radius < 61 and never; renamed marker radius >= 200.49 and cannot_tell", () =>
        {
            (double R, string D) T(byte[] nif)
            {
                var (_, tube) = MrPath(nif);
                if (tube.Status != "built" || tube.Pieces.Count == 0) return (double.NaN, $"tube {tube.Status} {tube.Reason}");
                return (tube.Pieces.Max(p => p.Radius), SweptGeometry.ContactTube(NifMotion.PlaceTube(tube, MrZ, MrZ, 1f), p3, MrOpaque(p3)).Decision);
            }
            var a = T(S(MrMarker)); var b = T(S(MrRenamed));
            return (a.R < 61 && a.D == "never" && b.R >= 200.49 && b.D == "cannot_tell", $"marker R={a.R:0.###} {a.D} | renamed R={b.R:0.###} {b.D}");
        });
        // MR-7: a path node whose only shape is the marker drives nothing drawn: no tube for it. Rejects building one (the base).
        Case(check, "MR-7 path node with only a marker: 'drives no visible shape: no tube'; renamed marker: a tube", () =>
        {
            static byte[] S7(string marker) => MrPathNif(new(0, 0, 0), new(100, 0, 0), ("Body", false, MrBody), (marker, true, FlatQuad(-10, -10, 10, 10, 200)));
            var (_, a) = MrPath(S7(MrMarker)); var (_, b) = MrPath(S7(MrRenamed));
            bool label = a.Labels.Contains("path on 'Bird' drives no visible shape: no tube");
            return (label && a.Pieces.Count == 0 && b.Pieces.Count > 0, $"marker {a.Status} pieces {a.Pieces.Count} labels [{string.Join("; ", a.Labels)}] | renamed {b.Status} pieces {b.Pieces.Count}");
        });

        // ================= the partner side: a static partner mesh whose marker is in the mover's way =================
        // PM (read through MeshSurface.ModelSurface): "Frame", a quad at x = 1000, and the marker, a quad at x = 50 (y, z -15..15).
        static byte[] PM(string marker) => ChainNif(null, new[] { ("Frame", "N1", MrQuadX(1000, -15, 15, -15, 15), false), (marker, "N1", MrQuadX(50, -15, 15, -15, 15), false) },
                                                    Array.Empty<(string, FxKeys[], CycleType)>());
        static (TriangleSoup Soup, string[] Tags) Surf(byte[] nif)
        {
            var s = MeshSurface.ModelSurface(nif, VolumeGeometry.Render);
            if (s.Status != "measured") throw new InvalidOperationException($"partner surface {s.Status} {s.Reason}");
            return (s.Soup!, s.Tags!);
        }
        // MR-8: a 10 u cube sliding x 0..100 against PM. Rejects the never-test reading the partner's marker (cannot_tell, the base).
        Case(check, "MR-8 swept contact against a partner's marker: never; renamed marker witness, crossing 40 u", () =>
        {
            var mover = MrRegion(ChainNif(null, new[] { ("Body", "N2", Cube(0, 0, 0, 5), false) }, new[] { ("Slide", new[] { new FxKeys("N1", X0: 0, X1: 100) }, CycleType.CYCLE_CLAMP) }));
            var (sa, ta) = Surf(PM(MrMarker)); var (sb, tb) = Surf(PM(MrRenamed));
            var a = SweptGeometry.Contact(mover, sa, ta, false); var b = SweptGeometry.Contact(mover, sb, tb, false);
            return (a.Decision == "never" && b.Decision == "witness" && Math.Abs((b.WitnessCrossingLower ?? 0) - 40) < 0.01, $"marker {MrShow(a)} | renamed {MrShow(b)}");
        });
        // MR-9: the path tube against PM. Rejects the tube ignoring the partner's tags (cannot_tell, the base).
        Case(check, "MR-9 path tube against a partner's marker: never; renamed marker cannot_tell", () =>
        {
            var (_, tube) = MrPath(MrPathNif(new(0, 0, 0), new(100, 0, 0), ("Body", true, MrBody)));
            var placed = NifMotion.PlaceTube(tube, MrZ, MrZ, 1f);
            var (sa, ta) = Surf(PM(MrMarker)); var (sb, tb) = Surf(PM(MrRenamed));
            var a = SweptGeometry.ContactTube(placed, sa, ta); var b = SweptGeometry.ContactTube(placed, sb, tb);
            return (a.Decision == "never" && b.Decision == "cannot_tell", $"marker {a.Decision} {a.Reason} | renamed {b.Decision} {b.Reason}");
        });
        // MR-10: two movers. A = fixture R; B = a 10 u cube at (60,0,0) sliding x 0..1 (CLAMP). A's marker (plane y = 0) passes
        // through B's cube for slide 35..66; A's Frame stays about 450 u away. Rejects the two-mover never test reading A's marker.
        Case(check, "MR-10 two movers: A's marker-only crossing is never; renamed marker not never", () =>
        {
            var bNif = ChainNif(null, new[] { ("Body", "N2", Cube(60, 0, 0, 5), false) }, new[] { ("Nudge", new[] { new FxKeys("N1", X0: 0, X1: 1) }, CycleType.CYCLE_CLAMP) });
            SweptPairDecision Both(byte[] aNif)
            {
                var ma = NifMotion.Read(aNif, null); var mb = NifMotion.Read(bNif, null);
                var ra = MrRegion(aNif); var rb = MrRegion(bNif);
                var ha = SweptGeometry.HeldSoups(aNif, ma, VolumeGeometry.Render, MrZ, MrZ, 1f);
                var hb = SweptGeometry.HeldSoups(bNif, mb, VolumeGeometry.Render, MrZ, MrZ, 1f);
                return SweptGeometry.ContactBoth(ra, ha, rb, hb, null);
            }
            var a = Both(MrRigR(MrMarker)); var b = Both(MrRigR(MrRenamed));
            return (a.Decision == "never" && b.Decision != "never", $"marker {a.Decision} {a.Reason} | renamed {b.Decision} {b.Reason}");
        });
    }
}
