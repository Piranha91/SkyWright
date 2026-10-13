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

    static byte[] Vanilla(string data, string rel)
    {
        foreach (var bsa in new[] { "Skyrim - Meshes0.bsa", "Skyrim - Meshes1.bsa" })
            if (AssetResolver.TryReadArchiveEntry(Path.Combine(data, bsa), "meshes\\" + rel) is { } b) return b;
        throw new FileNotFoundException("not in the vanilla mesh archives: " + rel);
    }

    static Vector3 Centre(Vector3 mn, Vector3 mx) => (mn + mx) / 2;
    static bool Near(Vector3 a, Vector3 b, float tol) => Vector3.Distance(a, b) <= tol;

    /// <summary>Known answers on vanilla meshes, read from the game's archives (values from runs/2026-10-13-light-fixtures/gate-a).</summary>
    static void EmitterReaderCases(string data)
    {
        EmitterModel E(string rel) => LightReach.ReadEmitters(Vanilla(data, rel));
        Check("emitters: a horn candle has one emissive part (value 0.498) at its flame", "a candle with no emitting part",
              () => E(@"clutter\candles\candlehorntable01.nif") is { Status: "read" } m && m.Parts.Count == 1 && m.Parts[0].Kind == "emissive"
                    && Math.Abs((m.Parts[0].Value ?? 0) - 0.498) < 0.001 && Near(Centre(m.Parts[0].Min, m.Parts[0].Max), new Vector3(0, 0, 9.8f), 0.5f));
        Check("emitters: a table has none (Own_Emit set, emissive black)", "Own_Emit alone read as emitting",
              () => E(@"clutter\common\commontableround01.nif") is { Status: "read" } m && m.Parts.Count == 0);
        Check("emitters: a Fire Salts pile reads one emissive part (the fold, not the reader, excludes items)", "the reader deciding by record type",
              () => E(@"clutter\ingredients\firesaltpile.nif") is { Status: "read" } m && m.Parts.Count == 1 && m.Parts[0].Kind == "emissive");
        Check("emitters: a Resist Fire potion has none (emissive black)", "a dark potion read as emitting",
              () => E(@"clutter\potions\potionresistfire50.nif") is { Status: "read" } m && m.Parts.Count == 0);
        Check("emitters: a farmhouse shell emits only at its glow-map window part", "the whole shell, or no part, read as emitting",
              () => E(@"architecture\farmhouse\interior\farmint2end01.nif") is { Status: "read" } m && m.Parts.Count == 1
                    && Math.Abs((m.Parts[0].Value ?? 0) - 3) < 1e-6 && Near(Centre(m.Parts[0].Min, m.Parts[0].Max), new Vector3(-131, 0, 494), 1f));
        Check("emitters: a burning fireplace emits from its flames, glow and two particle emitters (8 u and 37.5 u up), not its wood",
              "unlit wood counted; particle emitters at their reach box; flames missed",
              () => E(@"clutter\woodfires\fireplacewood01burning.nif") is { Status: "read" } m
                    && m.Parts.Count(p => p.Kind == "self-lit effect") == 3 && m.Parts.All(p => p.Kind != "emissive")
                    && m.Parts.Where(p => p.Kind == "particle emitter").Select(p => Centre(p.Min, p.Max)).OrderBy(c => c.Z).ToList() is { Count: 2 } pe
                    && Near(pe[0], new Vector3(0, 0, 8.0f), 0.2f) && Near(pe[1], new Vector3(0, 0, 37.5f), 0.2f));
        Check("emitters: a cobweb (effect shader with Effect_Lighting) has none", "a lit ambient effect read as a light source",
              () => E(@"effects\fxcobwebcorner01.nif") is { Status: "read" } m && m.Parts.Count == 0);
        Check("emitters: the torch's two ember emitters sit at its head (y 32-34)", "particle emitters missing or at the model origin",
              () => E(@"weapons\torch\torch.nif") is { Status: "read" } m
                    && m.Parts.Where(p => p.Kind == "particle emitter").Select(p => Centre(p.Min, p.Max)).OrderBy(c => c.Y).ToList() is { Count: 2 } pe
                    && Near(pe[0], new Vector3(0, 32.5f, 1.0f), 0.2f) && Near(pe[1], new Vector3(0, 33.4f, 0.3f), 0.2f));
        Check("particle emitters: a mesh emitter the reach model refuses (it moves) still has a stored-pose extent", "a whole-system refusal on reach grounds",
              () => ParticleReach.EmitterExtents(Vanilla(data, @"furniture\alchemyworkbench.nif")) is var es
                    && es.Any(e => e.SystemName == "PArray_OuterArmsSparks" && e.Refusal is null && !e.Box.IsEmpty && e.Moving));
    }

    public static int Run(string[] args)
    {
        string? meshesFrom = null;
        for (int i = 1; i < args.Length; i++) if (args[i] == "--meshes-from" && i + 1 < args.Length) meshesFrom = args[++i];
        pass = fail = 0;
        Console.WriteLine($"lightprobe self-test — {LightReach.PolicyVersion}");
        Check("fixture: the outward box faces out", "a fixture whose winding is wrong (every facing case below would invert)", OutwardIsOutward);

        Check("attenuation 1-(d/r)^2: 0 -> 1, r/2 -> 0.75, r -> 0, beyond r -> 0", "linear falloff; light beyond the radius",
              () => Math.Abs(LightReach.Attenuation(0, 100) - 1) < 1e-12 && Math.Abs(LightReach.Attenuation(50, 100) - 0.75) < 1e-12
                    && LightReach.Attenuation(100, 100) == 0 && LightReach.Attenuation(150, 100) == 0);

        // XRDS on a light reference is an offset added to the base radius (xEdit wbREFRRadiusToStr; reverse-engineered
        // ExtraRadius.radiusModifier), not a replacement: vanilla stores negative values.
        Check("radius: the reference's XRDS is added to the base", "XRDS read as a replacement radius", () => LightReach.EffectiveRadius(280, 320f) == 600f);
        Check("radius: a negative XRDS shrinks the light", "a negative offset ignored (base kept)", () => Math.Abs(LightReach.EffectiveRadius(280, -145.96465f) - 134.03535f) < 1e-3);
        Check("radius: no XRDS (null or 0) keeps the base", "an absent offset read as radius 0", () => LightReach.EffectiveRadius(280, null) == 280f && LightReach.EffectiveRadius(512, 0f) == 512f);

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

        // C4 assignment by the shape's box (sixth fold, rule a): a light whose sphere meets the shape's bound sphere but whose
        // radius does not reach the shape's box takes no slot.
        Check("box reach: a light 100 u beyond a box face, radius 99 / 101", "the bound sphere deciding the count",
              () => !LightReach.MeetsShapeBox(new Vector3(200, 50, 50), 99, Vector3.Zero, new Vector3(100, 0, 0), new Vector3(0, 100, 0), new Vector3(0, 0, 100))
                    && LightReach.MeetsShapeBox(new Vector3(200, 50, 50), 101, Vector3.Zero, new Vector3(100, 0, 0), new Vector3(0, 100, 0), new Vector3(0, 0, 100)));
        Check("box reach: a rotated box (45 deg about Z) measured in its own frame", "an axis-aligned world box assumed (it would read 35 u)",
              () => LightReach.MeetsShapeBox(new Vector3(0, 0, 0), 1, new Vector3(0, 0, 0), new Vector3(70.71068f, 70.71068f, 0), new Vector3(-70.71068f, 70.71068f, 0), new Vector3(0, 0, 100))
                    && !LightReach.MeetsShapeBox(new Vector3(35.35534f, -35.35534f, 50), 49, new Vector3(0, 0, 0), new Vector3(70.71068f, 70.71068f, 0), new Vector3(-70.71068f, 70.71068f, 0), new Vector3(0, 0, 100))
                    && LightReach.MeetsShapeBox(new Vector3(35.35534f, -35.35534f, 50), 51, new Vector3(0, 0, 0), new Vector3(70.71068f, 70.71068f, 0), new Vector3(-70.71068f, 70.71068f, 0), new Vector3(0, 0, 100)));

        // ---- emitting parts (lane A, 2026-10-13; runs/2026-10-13-light-fixtures/GATE-A.md section 3) ----
        // Placement: an emitting part's model-frame box is placed exactly as lightprobe places a lit shape's box (TransformSoup of the corner
        // and the three edge ends), so the fold measures in the same frame the light positions are in.
        Check("emitters: a part box is placed by the reference's turned pose", "an unplaced or translated-only box (the rotation dropped)", () =>
        {
            var mn = new Vector3(-10, -5, 0); var mx = new Vector3(10, 5, 30);
            var pos = new Vec3(1000, -200, 50); var rot = new Vec3(0.3f, -0.2f, 1.2f); float sc = 1.5f;
            var w = SpatialOverlap.TransformSoup(new TriangleSoup(new[] { mn, new Vector3(mx.X, mn.Y, mn.Z), new Vector3(mn.X, mx.Y, mn.Z), new Vector3(mn.X, mn.Y, mx.Z) },
                                                                  Array.Empty<int>()), pos, rot, sc).Vertices;
            var (o, ex, ey, ez) = LightReach.PlaceBox(mn, mx, pos, rot, sc);
            static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-3f;
            return Near(o, w[0]) && Near(ex, w[1] - w[0]) && Near(ey, w[2] - w[0]) && Near(ez, w[3] - w[0]);
        });
        if (meshesFrom is null)
            Console.WriteLine("NOT RUN  emitter reader cases on vanilla meshes (they need --meshes-from GAME_DATA, the folder holding Skyrim - Meshes0/1.bsa)");
        else
            EmitterReaderCases(meshesFrom);

        Console.WriteLine($"lightprobe self-test: {pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
