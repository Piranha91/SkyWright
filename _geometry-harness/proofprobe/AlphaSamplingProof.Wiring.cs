using System.Numerics;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using MutagenAlternate = Mutagen.Bethesda.Skyrim.AlternateTexture;

/// <summary>Known answers for the contactprobe wiring of the visible crossing (lane 2 of the 2026-10-07 task): the placed measure
/// with zero-area triangles left out, the lazy alpha source, the moving member's texture-coordinate count, and the base records'
/// alternate textures read into the overlap index. Each case names the wrong answer it rejects.</summary>
static partial class AlphaSamplingProof
{
    public static void RunWiring(Action<string, bool, string> check)
    {
        var none = new Vec3(0, 0, 0);
        double k1 = 12.5 * (3 - 1.0 / 255);

        // W1: member A is a collinear (zero-area) triangle followed by QuadA's two; the placed measure leaves the first out, so the
        // pieces name triangles 0 and 1 of the kept soup. They must be read as triangles 1 and 2 of the full soup: visible = K1's
        // 12.5 (3 - 1/255). The contact must equal MeshSurface.MeasurePlacedFramed's (ZeroAreaSkipped A = 1). Rejects pieces read
        // against the full soup without the index map (the UV of the wrong triangle, or of the zero-area one).
        Case(check, "alpha W1: placed measure with a zero-area triangle first — visible K1 length; contact equals the placed measure", () =>
        {
            var q = QuadA();
            var a = Tris(new[] { V(0, 0, 0), V(50, 0, 0), V(100, 0, 0) }, new[] { q.Vertices[0], q.Vertices[1], q.Vertices[2] }, new[] { q.Vertices[3], q.Vertices[4], q.Vertices[5] });
            var b = WallY(31.25f);
            var ta = Tags(a, SurfaceMaterial.AlphaTest); var tb = Tags(b, SurfaceMaterial.Opaque);
            // the zero-area triangle's texture coordinates put it in the opaque corner, so reading it for a piece changes the length
            var rows = Tc(a, Rule(), UvA).Triangles!.ToList();
            rows[0] = new TriangleTexcoords(0, 0.05f, 0.05f, 0.1f, 0.05f, 0.05f, 0.1f, 1, 1, 1);
            var tc = new SurfaceTexcoordSet("measured", null, rows, new[] { Rule() });
            var (v, o) = SurfaceVisibility.MeasurePlaced(a, ta, () => Member(tc, K1Plane()), none, none, 1f, b, tb, () => null, none, none, 1f);
            var (plain, po) = MeshSurface.MeasurePlacedFramed(a, ta, none, none, 1f, b, tb, none, none, 1f, VolumeGeometry.Render);
            var c = v.Contact;
            bool same = c is not null && c.Status == plain.Status && c.CrossingLength?.Lower == plain.CrossingLength?.Lower && c.CrossingLength?.Upper == plain.CrossingLength?.Upper
                        && c.ZeroAreaSkipped == plain.ZeroAreaSkipped && plain.ZeroAreaSkipped is { A: 1, B: 0 } && o == po
                        && (c.EnvelopeMin ?? []).SequenceEqual(plain.EnvelopeMin ?? []) && (c.EnvelopeMax ?? []).SequenceEqual(plain.EnvelopeMax ?? []);
            return (v.Status == "measured" && Is(v.AlphaVisible, k1, k1) && same, $"{Say(v)} | contact same {same} skipped {plain.ZeroAreaSkipped}");
        });

        // W2: the alpha source is asked for only when a piece lies on an alpha triangle, and once per member. Opaque x opaque: never
        // asked, visible = the opaque crossing (100). Alpha x opaque: asked once although the crossing has pieces on both triangles of
        // the quad. Rejects an eager read (an opaque pair refused by a member's unreadable texture) and a read per piece.
        Case(check, "alpha W2: alpha source asked only for an alpha piece, once per member", () =>
        {
            var a = QuadA(); var b = WallY(31.25f);
            int asked = 0;
            Func<MemberAlpha?> src = () => { asked++; return Member(Tc(a, Rule(), UvA), K1Plane()); };
            var (op, _) = SurfaceVisibility.MeasurePlaced(a, Tags(a, SurfaceMaterial.Opaque), src, none, none, 1f, b, Tags(b, SurfaceMaterial.Opaque), () => null, none, none, 1f);
            int askedOpaque = asked;
            var (al, _) = SurfaceVisibility.MeasurePlaced(a, Tags(a, SurfaceMaterial.AlphaTest), src, none, none, 1f, b, Tags(b, SurfaceMaterial.Opaque), () => null, none, none, 1f);
            // the diagonal-crossing variant puts pieces on both triangles of the quad
            var diag = Tris(new[] { V(-10, 110, -10), V(110, -10, -10), V(110, -10, 10) }, new[] { V(-10, 110, -10), V(110, -10, 10), V(-10, 110, 10) });
            int before = asked;
            var (dg, _) = SurfaceVisibility.MeasurePlaced(a, Tags(a, SurfaceMaterial.AlphaTest), src, none, none, 1f, diag, Tags(diag, SurfaceMaterial.Opaque), () => null, none, none, 1f);
            int pieces = SurfaceVisibility.MeasureWithPieces(a, Tags(a, SurfaceMaterial.AlphaTest), diag, Tags(diag, SurfaceMaterial.Opaque), VolumeGeometry.Render).Pieces.Count;
            bool ok = askedOpaque == 0 && op.Status == "measured" && Is(op.VisibleCrossing, 100, 100)
                      && al.Status == "measured" && Is(al.AlphaVisible, k1, k1) && asked - before == 1 && pieces >= 2 && dg.Status == "measured";
            return (ok, $"opaque asked {askedOpaque} vis {S(op.VisibleCrossing)} | alpha {S(al.AlphaVisible)} | diagonal asked {asked - before} over {pieces} pieces");
        });

        // W3: a moving member's texture coordinates come from its static soup. A sample whose triangle count differs from the
        // texture coordinates' refuses, naming both counts. Fixture: D5's plate with one extra leading row in the texture
        // coordinates, so rows would be read one triangle off. Rejects measuring a posed soup against misaligned coordinates.
        Case(check, "alpha W3: swept sample whose triangle count differs from the texture coordinates refuses", () =>
        {
            var plate = Tris(new[] { V(0, -50, -50), V(0, 50, -50), V(0, 50, 50) }, new[] { V(0, -50, -50), V(0, 50, 50), V(0, -50, 50) });
            TriangleSoup Shift(float dz) => new(plate.Vertices.Select(p => p + new Vector3(0, 0, dz)).ToArray(), plate.Indices);
            var tags = Tags(plate, SurfaceMaterial.AlphaTest);
            var good = Tc(plate, Rule(), p => ((p.Y + 50) / 100f, (p.Z + 50) / 100f));
            var rows = new List<TriangleTexcoords> { new(0, 0, 0, 0, 0, 0, 0, 1, 1, 1) }; rows.AddRange(good.Triangles!);
            var bad = new SurfaceTexcoordSet("measured", null, rows, new[] { Rule() });
            var tex = Plane(8, 8, (_, y) => (byte)(y <= 3 ? 255 : 0));
            var floor = Tris(new[] { V(-10, -60, 0), V(10, -60, 0), V(10, 60, 0) }, new[] { V(-10, -60, 0), V(10, 60, 0), V(-10, 60, 0) });
            var region = new SweptRegion("built", null, VolumeGeometry.Render, Array.Empty<MotionAnimation>(),
                new[] { new SweptSample("seq:Fixture", 0f, Shift(-20), tags, 0.25), new SweptSample("seq:Fixture", 1f, Shift(20), tags, 0.25) }, 0.25, true, false);
            var d = SweptGeometry.ContactVisible(region, Member(bad, tex), floor, Tags(floor, SurfaceMaterial.Opaque), null, false);
            var control = SweptGeometry.ContactVisible(region, Member(good, tex), floor, Tags(floor, SurfaceMaterial.Opaque), null, false);
            bool ok = d.Decision == "refused" && (d.Reason ?? "").Contains("2 triangles", StringComparison.Ordinal) && (d.Reason ?? "").Contains("coordinates 3", StringComparison.Ordinal)
                      && control.Decision == "visible_witness";
            return (ok, $"misaligned: {d.Decision} {d.Reason} | control: {control.Decision}");
        });

        // W4: the overlap index reads a base record's alternate textures and the texture sets' diffuse paths, winner wins; a later
        // record without alternates removes them. Rejects alternates never read (the alpha reader would use the NIF's texture where
        // the record replaces it) and a stale earlier record's alternates kept.
        Case(check, "alpha W4: overlap index reads alternate textures and texture-set diffuse paths, winner wins", () =>
        {
            var release = SkyrimRelease.SkyrimSE;
            var k0 = new ModKey("AltTexMaster", ModType.Master); var k1m = new ModKey("AltTexPatch", ModType.Plugin);
            var txst = new FormKey(k0, 0x801); var stat = new FormKey(k0, 0x802); var plain = new FormKey(k0, 0x803);
            var m0 = new SkyrimMod(k0, release);
            m0.TextureSets.Add(new TextureSet(txst, release) { EditorID = "AltSet", Diffuse = "Fixture\\Alt.dds" });
            m0.Statics.Add(new Static(stat, release)
            {
                EditorID = "AltStatic",
                Model = new Model { File = "meshes\\fixture\\alt.nif", AlternateTextures = new ExtendedList<MutagenAlternate>
                    { new MutagenAlternate { Name = "AlphaQuad", Index = 0, NewTexture = new FormLink<ITextureSetGetter>(txst) } } },
            });
            m0.Statics.Add(new Static(plain, release)
            {
                EditorID = "WasAlt",
                Model = new Model { File = "meshes\\fixture\\b.nif", AlternateTextures = new ExtendedList<MutagenAlternate>
                    { new MutagenAlternate { Name = "Other", Index = 1, NewTexture = new FormLink<ITextureSetGetter>(txst) } } },
            });
            var m1 = new SkyrimMod(k1m, release);
            m1.Statics.Add(new Static(plain, release) { EditorID = "WasAlt", Model = new Model { File = "meshes\\fixture\\b.nif" } });
            var idx = new OverlapIndex();
            idx.Ingest(0, m0); idx.Ingest(1, m1);
            bool hasAlt = idx.BaseAlternateTextures.TryGetValue(stat, out var alts) && alts.Count == 1 && alts[0].Name == "AlphaQuad" && alts[0].Index == 0 && alts[0].TextureSet == txst;
            bool diffuse = idx.TextureSetDiffuse.TryGetValue(txst, out var d) && d == "Fixture\\Alt.dds";
            bool removed = !idx.BaseAlternateTextures.ContainsKey(plain);
            return (hasAlt && diffuse && removed, $"alternates {(alts is null ? "none" : string.Join(",", alts))}; diffuse {d ?? "none"}; later record removes {removed}");
        });
    }
}
