using System.Numerics;
using HousecarlCore;

// lightprobe --self-test: known answers for the light readings (src/housecarl-core/LightReach.cs). Each case names the
// wrong answer it rejects. Prints PASS/FAIL per case and a summary line; exit 1 on any FAIL. A case that throws is a FAIL.
static class LightSelfTest
{
    static int pass, fail;

    static void Check(string name, string rejects, Func<bool> test)
    {
        bool ok;
        string? why = null;
        try { ok = test(); }
        catch (Exception ex) { ok = false; why = ex.GetType().Name + ": " + ex.Message; }
        if (ok) pass++; else fail++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  (rejects: {rejects}){(why is null ? "" : "  — " + why)}");
    }

    // An axis-aligned box of half-size h centred at c, 12 triangles. inward: normals point into the box (a room shell);
    // else outward (a closed solid).
    static TriangleSoup Box(Vector3 c, float h, bool inward)
    {
        var v = new List<Vector3>();
        for (int i = 0; i < 8; i++) v.Add(c + new Vector3((i & 1) == 0 ? -h : h, (i & 2) == 0 ? -h : h, (i & 4) == 0 ? -h : h));
        // outward-facing quads (counter-clockwise seen from outside)
        int[][] quads = { new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 } };
        var idx = new List<int>();
        foreach (var q in quads)
        {
            int[] t1 = { q[0], q[1], q[2] }, t2 = { q[0], q[2], q[3] };
            foreach (var t in new[] { t1, t2 })
                if (inward) idx.AddRange(new[] { t[0], t[2], t[1] }); else idx.AddRange(t);
        }
        return new TriangleSoup(v.ToArray(), idx.ToArray());
    }

    static bool OutwardIsOutward()
    {
        // guard the fixture itself: the outward box's +X face normal points +X
        var b = Box(Vector3.Zero, 1, inward: false);
        for (int t = 0; t < b.TriangleCount; t++)
        {
            var a = b.Vertices[b.Indices[3 * t]]; var bb = b.Vertices[b.Indices[3 * t + 1]]; var c = b.Vertices[b.Indices[3 * t + 2]];
            var n = Vector3.Cross(bb - a, c - a); var cen = (a + bb + c) / 3;
            if (Vector3.Dot(n, cen) <= 0) return false;
        }
        return true;
    }

    public static int Run()
    {
        pass = fail = 0;
        Console.WriteLine($"lightprobe self-test — {LightReach.PolicyVersion}");
        Check("fixture: the outward box faces out", "a fixture whose winding is wrong (every facing case below would invert)", OutwardIsOutward);

        Check("attenuation 1-(d/r)^2: 0 -> 1, r/2 -> 0.75, r -> 0, beyond r -> 0", "linear falloff; light beyond the radius",
              () => Math.Abs(LightReach.Attenuation(0, 100) - 1) < 1e-12 && Math.Abs(LightReach.Attenuation(50, 100) - 0.75) < 1e-12
                    && LightReach.Attenuation(100, 100) == 0 && LightReach.Attenuation(150, 100) == 0);

        Check("radius: the reference's XRDS wins over the base", "base radius only", () => LightReach.EffectiveRadius(280, 600f) == 600f);
        Check("radius: no XRDS (null or 0) keeps the base", "an absent override read as radius 0", () => LightReach.EffectiveRadius(280, null) == 280f && LightReach.EffectiveRadius(512, 0f) == 512f);

        // A room shell 1000 u wide (inward normals) owned by 1; a non-shadow light inside it and one outside it.
        var room = Box(Vector3.Zero, 500, inward: true);
        var scene = new LightScene(new[] { (room, (string[]?)null, 1) });
        Check("facing: a light inside a room sees front faces only", "inside read as outside",
              () => scene.Facing(new Vector3(0, 0, 0), 2000) is { Front: 64, Back: 0, None: 0 });
        Check("facing: a light outside the shell sees back faces or nothing", "outside read as inside",
              () => scene.Facing(new Vector3(-700, 0, 0), 2000) is var f && f.Front == 0 && f.Back > 0 && f.Back + f.None == 64);
        Check("facing: a light in the void sees nothing", "an empty scene read as enclosed",
              () => new LightScene(Array.Empty<(TriangleSoup, string[]?, int)>()).Facing(Vector3.Zero, 500) is { None: 64 });

        Check("leak: a light inside a convex room lights the walls and leaks nothing", "leak counted on unblocked surface",
              () => scene.Leak(new Vector3(0, 0, 0), 800) is var l && l.Lit > 0 && l.Leak == 0);
        Check("leak: a light 200 u outside the room's -X wall reaches the side walls' inner faces through that wall", "a wall stopping a non-shadow light (no leak read)",
              () => scene.Leak(new Vector3(-700, 0, 0), 1000) is var l && l.Leak > 0 && l.LeakByOwner.ContainsKey(1));
        Check("leak: a light outside, out of range of every inner face, leaks nothing", "leak beyond the radius",
              () => scene.Leak(new Vector3(-700, 0, 0), 150) is var l && l.Leak == 0 && l.Lit == 0);
        Check("leak: back-facing surface is not lit", "lighting both sides of a wall",
              () => new LightScene(new[] { (Box(Vector3.Zero, 100, inward: false), (string[]?)null, 1) }).Leak(new Vector3(0, 0, 0), 500) is var l && l.Lit == 0 && l.Leak == 0);

        // a closed solid between the light and a wall: the wall behind the solid leaks, the rest is lit
        var crate = Box(new Vector3(0, 0, 0), 50, inward: false);
        var wall = new TriangleSoup(new[] { new Vector3(200, -300, -300), new Vector3(200, 300, -300), new Vector3(200, 300, 300), new Vector3(200, -300, 300) },
                                    new[] { 0, 2, 1, 0, 3, 2 });   // normal -X, facing the light at -X
        var mixed = new LightScene(new[] { (crate, (string[]?)null, 2), (wall, (string[]?)null, 3) });
        Check("leak: the wall's shadowed part behind a crate leaks, its open part is lit; owner kept", "all-or-nothing per triangle; owner lost",
              () => mixed.Leak(new Vector3(-150, 0, 0), 800) is var l && l.LeakByOwner.GetValueOrDefault(3) > 0 && l.LitByOwner.GetValueOrDefault(3) > 0);

        Check("opaque only: an alpha wall neither blocks nor is lit", "alpha surfaces as occluders",
              () => new LightScene(new[] { (room, Enumerable.Repeat(SurfaceMaterial.AlphaBlend, room.TriangleCount).ToArray(), 1) }) is var s
                    && s.Triangles == 0 && s.NonOpaqueSkipped == room.TriangleCount);

        var degenerate = new TriangleSoup(new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(2, 0, 0), new Vector3(0, 1, 0) }, new[] { 0, 1, 2, 0, 1, 3 });
        Check("zero-area triangle: skipped and counted, never a refusal", "a refusal on a degenerate triangle (pair measure's D1)",
              () => new LightScene(new[] { (degenerate, (string[]?)null, 1) }) is { Triangles: 1, DegenerateSkipped: 1 });

        Check("shape: a light meets a shape when centre distance <= shape radius + light radius", "light radius alone; shape radius alone",
              () => LightReach.MeetsShape(new Vector3(0, 0, 0), 100, new Vector3(250, 0, 0), 150) && !LightReach.MeetsShape(new Vector3(0, 0, 0), 99, new Vector3(250, 0, 0), 150)
                    && !LightReach.MeetsShape(new Vector3(0, 0, 0), 100, new Vector3(250, 0, 0), 149));

        Check("point in solid (MeshAnchor, unchanged): a point inside a closed outward box reads inside", "point-in-solid never decided",
              () => MeshAnchor.TestPlaced(Box(Vector3.Zero, 50, inward: false), null, new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f, new Vector3(0, 0, 0),
                                          new BodyExtent(0, 10, 1)).Point == "inside");

        // Geometry the engine does not light is not a lit shape (defect L1): editor markers are not drawn, and only the
        // lighting shader takes point lights.
        Check("unlit: a shape named EditorMarker is not drawn", "an editor marker counted as lit render geometry",
              () => !LightReach.ShapeDrawn("EditorMarker", false, false, false) && LightReach.ShapeDrawn("CounterLeft01:0", false, false, false));
        Check("unlit: an Is-Marker base (XMarker) is not drawn", "an XMarker reference counted as lit render geometry",
              () => !LightReach.BaseDrawn(true) && LightReach.BaseDrawn(false));
        Check("unlit: a drawn effect-shader shape is not lit; a lighting-shader shape is", "an effect shape counted as lit by a point light",
              () => !LightReach.ShapeLit(true, false) && LightReach.ShapeLit(true, true) && !LightReach.ShapeLit(false, true));

        Console.WriteLine($"lightprobe self-test: {pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
