using System.Numerics;
using System.Text;
using HarnessShared;
using HousecarlCore;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

/// <summary>Known answers for the visible crossing (lane D, Stage D texture alpha sampling; plan `runs/2026-10-04-alpha-sampling/GATE-A.md`
/// §5, review F1-F6). Expected values are analytic (stated beside each case) or come from a dense-sampling oracle written in this file
/// (<see cref="Oracle"/>: bilinear alpha by its definition at 400,000 midpoints — a different method from the per-cell root solving
/// the code uses). Each case names the wrong answer it rejects. "Fixture control" cases hold on the base; every other case is red until
/// SurfaceVisibility / SurfaceTexcoords / TextureAlpha / SweptGeometry.ContactVisible are implemented.</summary>
static class AlphaSamplingProof
{
    const double Tol = 1e-6;
    static bool Near(double? v, double want, double tol = Tol) => v is { } x && Math.Abs(x - want) <= Math.Max(Math.Abs(want) * tol, tol);
    static bool Is(SurfaceInterval? i, double lo, double hi, double tol = Tol) => i is { } x && Near(x.Lower, lo, tol) && Near(x.Upper, hi, tol);
    static string S(SurfaceInterval? i) => i is null ? "null" : $"[{i.Lower:0.######}, {i.Upper:0.######}]";
    static string Say(SurfaceVisibilityResult r) =>
        $"{r.Status} {r.Reason} alphaVisible {S(r.AlphaVisible)} visibleCrossing {S(r.VisibleCrossing)} sensitive {r.ThresholdSensitive} " +
        $"tags [{string.Join("; ", r.ByTags?.Select(t => $"{t.TagKey}: L {t.AlphaLength:0.######} vis {S(t.Visible)} lo {S(t.LadderLow)} hi {S(t.LadderHigh)}") ?? [])}] " +
        $"labels [{string.Join("; ", r.Labels)}]";

    static void Case(Action<string, bool, string> check, string name, Func<(bool Ok, string Detail)> body)
    {
        (bool ok, string detail) r;
        try { r = body(); }
        catch (Exception ex) { r = (false, ex.GetType().Name + ": " + ex.Message); }
        check(name, r.ok, r.detail);
    }

    // ---------------------------------------------------------------- fixtures: soups, texcoords, rules, planes
    static Vector3 V(float x, float y, float z) => new(x, y, z);
    static TriangleSoup Tris(params Vector3[][] tris)
    {
        var v = new List<Vector3>(); var i = new List<int>();
        foreach (var t in tris) foreach (var p in t) { i.Add(v.Count); v.Add(p); }
        return new TriangleSoup(v.ToArray(), i.ToArray());
    }
    // The alpha quad A: z = 0, x, y in [0, 100], split along (0,0)-(100,100). Two triangles.
    static TriangleSoup QuadA() => Tris(new[] { V(0, 0, 0), V(100, 0, 0), V(100, 100, 0) }, new[] { V(0, 0, 0), V(100, 100, 0), V(0, 100, 0) });
    // An opaque vertical plate in the plane y = y0, x in [-10, 110], z in [-10, 10]: crosses QuadA along y = y0, x in [0, 100].
    static TriangleSoup WallY(float y0) => Tris(new[] { V(-10, y0, -10), V(110, y0, -10), V(110, y0, 10) }, new[] { V(-10, y0, -10), V(110, y0, 10), V(-10, y0, 10) });
    static string[] Tags(TriangleSoup s, string tag) => Enumerable.Repeat(tag, s.TriangleCount).ToArray();

    static ShapeAlphaRule Rule(string cls = SurfaceMaterial.AlphaTest, bool test = true, bool blend = false, byte threshold = 128,
                               float material = 1f, bool vertexAlpha = false, float us = 1, float vs = 1, float uo = 0, float vo = 0,
                               TextureWrap wu = TextureWrap.Repeat, TextureWrap wv = TextureWrap.Repeat, bool effect = false) =>
        new(0, "Fixture", cls, test, blend, threshold, 4, material, vertexAlpha, us, vs, uo, vo, wu, wv, "textures\\fixture\\a.dds", effect, null);

    // Texture coordinates for every triangle of s by a function of the (model) vertex; vertex alpha by another (default 1).
    static SurfaceTexcoordSet Tc(TriangleSoup s, ShapeAlphaRule rule, Func<Vector3, (float U, float V)> uv, Func<Vector3, float>? va = null)
    {
        var list = new List<TriangleTexcoords>();
        for (int t = 0; t < s.TriangleCount; t++)
        {
            Vector3 p0 = s.Vertices[s.Indices[3 * t]], p1 = s.Vertices[s.Indices[3 * t + 1]], p2 = s.Vertices[s.Indices[3 * t + 2]];
            var (u0, v0) = uv(p0); var (u1, v1) = uv(p1); var (u2, v2) = uv(p2);
            list.Add(new TriangleTexcoords(0, u0, v0, u1, v1, u2, v2, va?.Invoke(p0) ?? 1, va?.Invoke(p1) ?? 1, va?.Invoke(p2) ?? 1));
        }
        return new SurfaceTexcoordSet("measured", null, list, new[] { rule });
    }
    static MemberAlpha Member(SurfaceTexcoordSet tc, AlphaPlane plane) => new(tc, _ => (plane, null));
    static (float, float) UvA(Vector3 p) => (p.X / 100f, p.Y / 100f);

    static AlphaPlane Plane(int w, int h, Func<int, int, byte> alpha)
    {
        var a = new byte[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) a[y * w + x] = alpha(x, y);
        return new AlphaPlane(w, h, a, "fixture");
    }
    // 8 x 8: alpha 255 in columns 0..2 of rows 0..3, 0 elsewhere (asymmetric in u and v, so a flip or a swap changes the answer).
    static AlphaPlane K1Plane() => Plane(8, 8, (x, y) => (byte)(x <= 2 && y <= 3 ? 255 : 0));

    // ---------------------------------------------------------------- the dense-sampling oracle (independent method)
    // Bilinear alpha by definition (D3D texel centres, row 0 = v 0), times vertex alpha, times material alpha; drawn by the given
    // predicate. map(s) gives (u, v, vertexAlpha) at parameter s in [0, 1] of a straight piece of world length L.
    static double Oracle(double L, Func<double, (double U, double V, double Va)> map, AlphaPlane p, ShapeAlphaRule r, Func<double, bool> drawn, int n = 400_000)
    {
        int hits = 0;
        for (int k = 0; k < n; k++)
        {
            var (u, v, va) = map((k + 0.5) / n);
            u = u * r.UScale + r.UOffset; v = v * r.VScale + r.VOffset;
            double x = u * p.Width - 0.5, y = v * p.Height - 0.5;
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            double fx = x - x0, fy = y - y0;
            double A(int i, int j) => p.At(i, j, r.WrapU, r.WrapV) / 255.0;
            double t = (1 - fx) * (1 - fy) * A(x0, y0) + fx * (1 - fy) * A(x0 + 1, y0) + (1 - fx) * fy * A(x0, y0 + 1) + fx * fy * A(x0 + 1, y0 + 1);
            if (drawn(t * va * r.MaterialAlpha)) hits++;
        }
        return L * hits / n;
    }
    static Func<double, (double, double, double)> AlongU(double v, double u0 = 0, double u1 = 1, Func<double, double>? va = null) =>
        s => (u0 + (u1 - u0) * s, v, va?.Invoke(s) ?? 1);

    // ---------------------------------------------------------------- DDS fixtures (hand-built headers and blocks)
    static byte[] Dds(int w, int h, bool compressed, string? fourCc, int linearOrPitch, byte[] payload)
    {
        var b = new List<byte>();
        void U32(uint x) => b.AddRange(BitConverter.GetBytes(x));
        b.AddRange(Encoding.ASCII.GetBytes("DDS "));
        U32(124);
        U32(compressed ? 0x1007u | 0x80000u : 0x1007u | 0x8u);   // CAPS|HEIGHT|WIDTH|PIXELFORMAT + LINEARSIZE / PITCH
        U32((uint)h); U32((uint)w); U32((uint)linearOrPitch); U32(0); U32(1);
        for (int i = 0; i < 11; i++) U32(0);
        U32(32);                                                     // pixel format size
        if (compressed) { U32(0x4); b.AddRange(Encoding.ASCII.GetBytes(fourCc!)); U32(0); U32(0); U32(0); U32(0); U32(0); }
        else { U32(0x40 | 0x1); U32(0); U32(32); U32(0x00FF0000); U32(0x0000FF00); U32(0x000000FF); U32(0xFF000000); }
        U32(0x1000); U32(0); U32(0); U32(0); U32(0);
        b.AddRange(payload);
        return b.ToArray();
    }
    // 4 x 4 B8G8R8A8: texel k (row k/4, column k%4) has alpha 16k, red 255-k, green 2k, blue 7 (no other channel equals alpha).
    static byte[] DdsBgra() { var p = new byte[64]; for (int k = 0; k < 16; k++) { p[4 * k] = 7; p[4 * k + 1] = (byte)(2 * k); p[4 * k + 2] = (byte)(255 - k); p[4 * k + 3] = (byte)(16 * k); } return Dds(4, 4, false, null, 16, p); }
    // BC1, one block: color0 = 0x0000 <= color1 = 0xFFFF (three colours + transparent); texels 0..7 index 0, texels 8..15 index 3.
    static byte[] DdsBc1()
    {
        uint idx = 0; for (int k = 8; k < 16; k++) idx |= 3u << (2 * k);
        var p = new List<byte>(); p.AddRange(BitConverter.GetBytes((ushort)0x0000)); p.AddRange(BitConverter.GetBytes((ushort)0xFFFF)); p.AddRange(BitConverter.GetBytes(idx));
        return Dds(4, 4, true, "DXT1", 8, p.ToArray());
    }
    // BC3, one block: alpha0 255 > alpha1 0; texels 0..7 index 0 (255), texels 8..15 index 1 (0); colour block any.
    static byte[] DdsBc3()
    {
        ulong bits = 0; for (int k = 8; k < 16; k++) bits |= 1UL << (3 * k);
        var p = new List<byte> { 255, 0 }; for (int i = 0; i < 6; i++) p.Add((byte)(bits >> (8 * i)));
        p.AddRange(BitConverter.GetBytes((ushort)0xFFFF)); p.AddRange(BitConverter.GetBytes((ushort)0x0000)); p.AddRange(BitConverter.GetBytes(0u));
        return Dds(4, 4, true, "DXT5", 16, p.ToArray());
    }

    // ---------------------------------------------------------------- NIF fixtures
    static readonly NiVersion Ver = new() { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = 100 };
    static BSTriShape Shape(string name, TriangleSoup s, Func<Vector3, (float U, float V)>? uv = null)
    {
        var shape = new BSTriShape { Name = new NiStringRef(name), Flags_ui = 0xE, Scale = 1f };
        var tris = new List<Triangle>();
        for (int k = 0; k < s.Indices.Length; k += 3) tris.Add(new Triangle((ushort)s.Indices[k], (ushort)s.Indices[k + 1], (ushort)s.Indices[k + 2]));
        var uvs = uv is null ? new List<TexCoord>() : s.Vertices.Select(p => { var (u, v) = uv(p); return new TexCoord { U = u, V = v }; }).ToList();
        shape.Create(Ver, s.Vertices.ToList(), tris, uvs, new());
        shape.IsSkinned = false;
        return shape;
    }
    static byte[] Save(NifFile f, string what)
    {
        using var ms = new MemoryStream();
        if (f.Save(ms) != 0) throw new InvalidOperationException("alpha fixture failed to save: " + what);
        return ms.ToArray();
    }
    // An indexed quad (4 vertices, 2 triangles) z = 0 over [x0, x0 + 100] x [0, 100].
    static TriangleSoup IndexedQuad(float x0) =>
        new(new[] { V(x0, 0, 0), V(x0 + 100, 0, 0), V(x0 + 100, 100, 0), V(x0, 100, 0) }, new[] { 0, 1, 2, 0, 2, 3 });

    // NiflySharp 1.1.0 has no public accessor for BSLightingShaderProperty's texture clamp mode (only the generated private field
    // `_textureClampMode`, listed in its XML doc); the fixture sets it through that field, as the reader will read it.
    static void SetLightingClamp(BSLightingShaderProperty ls, TexClampMode mode)
    {
        var field = typeof(BSLightingShaderProperty).GetField("_textureClampMode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("NiflySharp: BSLightingShaderProperty._textureClampMode not found");
        field.SetValue(ls, field.FieldType.IsEnum ? Enum.ToObject(field.FieldType, (int)mode) : Convert.ChangeType((int)mode, field.FieldType));
    }

    // K10 fixture: shape 0 hidden ("Hidden", opaque, x0 = -300), shape 1 opaque ("Solid", x0 = -150), shape 2 alpha-tested
    // ("AlphaQuad", x0 = 0) with UV = (x / 100, y / 100), a lighting shader (diffuse "Fixture\K10.dds", UV scale (2, 3), offset
    // (0.25, 0.5), clamp U / wrap V, material alpha 0.75, vertex alpha flag on with vertex colours alpha = x / 100) and a
    // NiAlphaProperty test on, blend off, threshold 100. Optional: a texture transform controller on the shader.
    static byte[] K10Nif(bool uvController = false)
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var hidden = Shape("Hidden", IndexedQuad(-300)); hidden.Flags_ui |= 1;
        root.Children.AddBlockRef(f.AddBlock(hidden));
        root.Children.AddBlockRef(f.AddBlock(Shape("Solid", IndexedQuad(-150))));
        var quad = IndexedQuad(0);
        var alpha = Shape("AlphaQuad", quad, p => (p.X / 100f, p.Y / 100f));
        alpha.SetVertexColors(quad.Vertices.Select(p => new Color4 { R = 0.2f, G = 0.4f, B = 0.6f, A = p.X / 100f }).ToList());
        var ts = new BSShaderTextureSet();
        ts.Textures = new List<NiString4> { new() { Content = "Fixture\\K10.dds" }, new() { Content = "Fixture\\K10_n.dds" } };
        var ls = new BSLightingShaderProperty
        {
            ShaderFlags_SSPF1 = SkyrimShaderPropertyFlags1.Vertex_Alpha,
            ShaderFlags_SSPF2 = SkyrimShaderPropertyFlags2.ZBuffer_Write | SkyrimShaderPropertyFlags2.Vertex_Colors,
            UVScale = new TexCoord { U = 2, V = 3 }, UVOffset = new TexCoord { U = 0.25f, V = 0.5f },
            Alpha = 0.75f,
        };
        SetLightingClamp(ls, TexClampMode.CLAMP_S_WRAP_T);
        ls.TextureSetRef = new NiBlockRef<BSShaderTextureSet>(f.AddBlock(ts));
        if (uvController)
        {
            var c = new NiTextureTransformController { Frequency = 1, StartTime = 0, StopTime = 1, NextController = new(-1), Interpolator = new(-1) };
            ls.Controller = new NiBlockRef<NiTimeController>(f.AddBlock(c));
        }
        alpha.ShaderPropertyRef = new NiBlockRef<BSShaderProperty>(f.AddBlock(ls));
        var ap = new NiAlphaProperty { Threshold = 100 };
        ap.Flags.Value = 0x12EC;                                   // test on, blend off (OverlapSurfaceProof's fixture value)
        alpha.AlphaPropertyRef = new NiBlockRef<NiAlphaProperty>(f.AddBlock(ap));
        root.Children.AddBlockRef(f.AddBlock(alpha));
        return Save(f, "k10");
    }

    public static void Run(Action<string, bool, string> check)
    {
        // ---- fixture controls (hold on the base) ----
        Case(check, "alpha K0: fixture control — QuadA x WallY(31.25) crosses along 100 u, 'alpha_test x opaque'", () =>
        {
            var r = MeshSurface.Measure(QuadA(), Tags(QuadA(), SurfaceMaterial.AlphaTest), WallY(31.25f), Tags(WallY(31.25f), SurfaceMaterial.Opaque), VolumeGeometry.Render);
            double l = r.CrossingLengthByTags is { } d && d.TryGetValue(SurfaceMaterial.Pair(SurfaceMaterial.AlphaTest, SurfaceMaterial.Opaque), out var x) ? x : -1;
            return (r.Status == "measured" && Near(l, 100), $"{r.Status} L {l}");
        });
        Case(check, "alpha K0b: fixture control — the oracle on K1 gives the analytic 12.5 (3 - 1/255)", () =>
        {
            double want = 12.5 * (3 - 1.0 / 255), got = Oracle(100, AlongU(0.3125), K1Plane(), Rule(), a => a > 128 / 255.0);
            return (Near(got, want, 1e-3), $"oracle {got} analytic {want}");
        });

        var k1Plane = K1Plane();
        // K1: alpha-tested quad, the K1 texture, crossing at v = 0.3125 (texel row 2 exactly). Visible texel-x range
        // (-1 + 128/255, 2 + 127/255): 3 - 1/255 texels = 12.5 (3 - 1/255) u = 37.45098 u. Rejects: alpha ignored (100); V flipped
        // (row 5: 0); U/V swapped (rows 0..3 at column 2: 12.5 (4 - 1/255) = 49.95 u).
        Case(check, "alpha K1: alpha-tested quad x opaque wall — visible 12.5(3 - 1/255) u of 100", () =>
        {
            var a = QuadA(); var b = WallY(31.25f);
            var r = SurfaceVisibility.Measure(a, Tags(a, SurfaceMaterial.AlphaTest), Member(Tc(a, Rule(), UvA), k1Plane), b, Tags(b, SurfaceMaterial.Opaque), null);
            double want = 12.5 * (3 - 1.0 / 255);
            var tag = r.ByTags?.FirstOrDefault(t => t.TagKey == SurfaceMaterial.Pair(SurfaceMaterial.AlphaTest, SurfaceMaterial.Opaque));
            return (r.Status == "measured" && Is(r.AlphaVisible, want, want) && Is(r.VisibleCrossing, want, want) && tag is not null && Near(tag.AlphaLength, 100), Say(r));
        });
        // K1b: the same pair with member B as A and A as B (the alpha member on side B). Rejects visibility read from side A only.
        Case(check, "alpha K1b: alpha member on side B — same visible length", () =>
        {
            var a = QuadA(); var b = WallY(31.25f);
            var r = SurfaceVisibility.Measure(b, Tags(b, SurfaceMaterial.Opaque), null, a, Tags(a, SurfaceMaterial.AlphaTest), Member(Tc(a, Rule(), UvA), k1Plane));
            double want = 12.5 * (3 - 1.0 / 255);
            return (r.Status == "measured" && Is(r.AlphaVisible, want, want), Say(r));
        });

        // K2: ramp alpha(column i) = i, W 256, H 1, threshold 128: visible texel-x (128, 255 + 127/255) -> (127 + 127/255) * 100/256 u.
        // Rejects threshold not /255 (none visible), nearest-texel (x >= 128.5 and a different wrap sliver), threshold read as 127.5.
        Case(check, "alpha K2: ramp texture, threshold 128 — visible (127 + 127/255) * 100/256 u", () =>
        {
            var a = QuadA(); var b = WallY(50);
            var ramp = Plane(256, 1, (x, _) => (byte)x);
            var r = SurfaceVisibility.Measure(a, Tags(a, SurfaceMaterial.AlphaTest), Member(Tc(a, Rule(), UvA), ramp), b, Tags(b, SurfaceMaterial.Opaque), null);
            double want = (127 + 127.0 / 255) * 100 / 256;
            return (r.Status == "measured" && Is(r.AlphaVisible, want, want), $"want {want}: " + Say(r));
        });
        // K2b: alpha exactly at the threshold everywhere (128): lower bound (>) 0, upper (>=) 100. Rejects one comparison taken silently.
        Case(check, "alpha K2b: alpha = threshold everywhere — visible interval [0, 100] (> vs >=)", () =>
        {
            var a = QuadA(); var b = WallY(50);
            var flat = Plane(4, 4, (_, _) => 128);
            var r = SurfaceVisibility.Measure(a, Tags(a, SurfaceMaterial.AlphaTest), Member(Tc(a, Rule(), UvA), flat), b, Tags(b, SurfaceMaterial.Opaque), null);
            return (r.Status == "measured" && Is(r.AlphaVisible, 0, 100), Say(r));
        });

        // K3: the shader's UV transform (scale 2 / 1.5, offset 0.25 / 0.1) on the K1 texture at v = 0.3125. Expected from the oracle.
        // Rejects the transform ignored (37.45 u).
        Case(check, "alpha K3: shader UV scale and offset — oracle length", () =>
        {
            var a = QuadA(); var b = WallY(31.25f);
            var rule = Rule(us: 2, vs: 1.5f, uo: 0.25f, vo: 0.1f);
            double want = Oracle(100, AlongU(0.3125), k1Plane, rule, x => x > 128 / 255.0);
            var r = SurfaceVisibility.Measure(a, Tags(a, SurfaceMaterial.AlphaTest), Member(Tc(a, rule, UvA), k1Plane), b, Tags(b, SurfaceMaterial.Opaque), null);
            return (r.Status == "measured" && Is(r.AlphaVisible, want, want, 2e-3) && Math.Abs(want - 12.5 * (3 - 1.0 / 255)) > 1, $"oracle {want}: " + Say(r));
        });
        // K4: UVs u = x / 50 (0..2): repeat shows the texture twice, clamp once (beyond u = 1 the edge column 7, alpha 0). Oracle both.
        Case(check, "alpha K4: repeat vs clamp beyond u = 1 — two oracle lengths", () =>
        {
            var a = QuadA(); var b = WallY(31.25f);
            var rep = Rule(); var clamp = Rule(wu: TextureWrap.Clamp);
            Func<Vector3, (float, float)> uv = p => (p.X / 50f, p.Y / 100f);
            double wantRep = Oracle(100, AlongU(0.3125, 0, 2), k1Plane, rep, x => x > 128 / 255.0);
            double wantClamp = Oracle(100, AlongU(0.3125, 0, 2), k1Plane, clamp, x => x > 128 / 255.0);
            var r1 = SurfaceVisibility.Measure(a, Tags(a, SurfaceMaterial.AlphaTest), Member(Tc(a, rep, uv), k1Plane), b, Tags(b, SurfaceMaterial.Opaque), null);
            var r2 = SurfaceVisibility.Measure(a, Tags(a, SurfaceMaterial.AlphaTest), Member(Tc(a, clamp, uv), k1Plane), b, Tags(b, SurfaceMaterial.Opaque), null);
            return (Is(r1.AlphaVisible, wantRep, wantRep, 2e-3) && Is(r2.AlphaVisible, wantClamp, wantClamp, 2e-3) && Math.Abs(wantRep - wantClamp) > 1,
                    $"repeat oracle {wantRep}: {Say(r1)} | clamp oracle {wantClamp}: {Say(r2)}");
        });

        // K5: DDS decode at mip 0 (Pfim). Rejects BC1 read as opaque, row order flipped, a colour channel read as alpha.
        Case(check, "alpha K5a: DDS B8G8R8A8 — alpha 16k per texel, row 0 first", () =>
        {
            var (p, fmt, why) = TextureAlpha.DecodeDds(DdsBgra(), "k5a");
            bool ok = p is { Width: 4, Height: 4 } && Enumerable.Range(0, 16).All(k => p.Alpha[k] == 16 * k);
            return (ok && why is null, $"{fmt} {why} [{(p is null ? "" : string.Join(",", p.Alpha))}]");
        });
        Case(check, "alpha K5b: DDS BC1 punch-through — rows 0-1 alpha 255, rows 2-3 alpha 0", () =>
        {
            var (p, fmt, why) = TextureAlpha.DecodeDds(DdsBc1(), "k5b");
            bool ok = p is { Width: 4, Height: 4 } && Enumerable.Range(0, 16).All(k => p.Alpha[k] == (k < 8 ? 255 : 0));
            return (ok && why is null, $"{fmt} {why} [{(p is null ? "" : string.Join(",", p.Alpha))}]");
        });
        Case(check, "alpha K5c: DDS BC3 alpha block — rows 0-1 alpha 255, rows 2-3 alpha 0", () =>
        {
            var (p, fmt, why) = TextureAlpha.DecodeDds(DdsBc3(), "k5c");
            bool ok = p is { Width: 4, Height: 4 } && Enumerable.Range(0, 16).All(k => p.Alpha[k] == (k < 8 ? 255 : 0));
            return (ok && why is null, $"{fmt} {why} [{(p is null ? "" : string.Join(",", p.Alpha))}]");
        });
        Case(check, "alpha K5d: not a DDS — a named refusal, no plane", () =>
        {
            var (p, fmt, why) = TextureAlpha.DecodeDds(Encoding.ASCII.GetBytes("not a texture at all, just text"), "k5d");
            return (p is null && !string.IsNullOrEmpty(why), $"{fmt} {why}");
        });

        // K6: opaque texture, vertex alpha a = 1 - x/100 (linear in x on both triangles), threshold 128: flag on -> x < 100 * 127/255;
        // flag off -> 100. Rejects vertex alpha ignored, or applied when the shader does not read it.
        Case(check, "alpha K6: vertex alpha on -> 100 * 127/255 u; off -> 100 u", () =>
        {
            var a = QuadA(); var b = WallY(50);
            var solid = Plane(4, 4, (_, _) => 255);
            Func<Vector3, float> va = p => 1 - p.X / 100f;
            var on = SurfaceVisibility.Measure(a, Tags(a, SurfaceMaterial.AlphaTest), Member(Tc(a, Rule(vertexAlpha: true), UvA, va), solid), b, Tags(b, SurfaceMaterial.Opaque), null);
            var off = SurfaceVisibility.Measure(a, Tags(a, SurfaceMaterial.AlphaTest), Member(Tc(a, Rule(vertexAlpha: false), UvA, va), solid), b, Tags(b, SurfaceMaterial.Opaque), null);
            double want = 100 * 127.0 / 255;
            return (Is(on.AlphaVisible, want, want) && Is(off.AlphaVisible, 100, 100), $"on {Say(on)} | off {Say(off)}");
        });
        // K6b: material alpha 0.5 x opaque texture, threshold 128: 0.5 < 128/255 -> not visible; threshold 100 -> visible 100.
        Case(check, "alpha K6b: material alpha 0.5 — invisible at threshold 128, visible at 100", () =>
        {
            var a = QuadA(); var b = WallY(50);
            var solid = Plane(4, 4, (_, _) => 255);
            var r128 = SurfaceVisibility.Measure(a, Tags(a, SurfaceMaterial.AlphaTest), Member(Tc(a, Rule(material: 0.5f), UvA), solid), b, Tags(b, SurfaceMaterial.Opaque), null);
            var r100 = SurfaceVisibility.Measure(a, Tags(a, SurfaceMaterial.AlphaTest), Member(Tc(a, Rule(threshold: 100, material: 0.5f), UvA), solid), b, Tags(b, SurfaceMaterial.Opaque), null);
            return (Is(r128.AlphaVisible, 0, 0) && Is(r100.AlphaVisible, 100, 100), $"128 {Say(r128)} | 100 {Say(r100)}");
        });

        // K7: alpha x alpha. A: K1 texture at v = 0.3125. B: the wall y = 31.25 as an alpha-tested quad x in [0,100], z in [-50,50],
        // u = x/100, v = (z + 50)/100 (crossing at v 0.5), texture W 8 H 1 with columns 2..5 opaque. Drawn on both: texel-x
        // (1 + 128/255, 2 + 127/255) -> 12.5 (1 - 1/255) u. Rejects the union (12.5 (6 - 1/255)) and either member alone.
        Case(check, "alpha K7: alpha x alpha — only where both are drawn, 12.5(1 - 1/255) u", () =>
        {
            var a = QuadA();
            var b = Tris(new[] { V(0, 31.25f, -50), V(100, 31.25f, -50), V(100, 31.25f, 50) }, new[] { V(0, 31.25f, -50), V(100, 31.25f, 50), V(0, 31.25f, 50) });
            var bPlane = Plane(8, 1, (x, _) => (byte)(x >= 2 && x <= 5 ? 255 : 0));
            var r = SurfaceVisibility.Measure(a, Tags(a, SurfaceMaterial.AlphaTest), Member(Tc(a, Rule(), UvA), k1Plane),
                                              b, Tags(b, SurfaceMaterial.AlphaTest), Member(Tc(b, Rule(), p => (p.X / 100f, (p.Z + 50) / 100f)), bPlane), null);
            double want = 12.5 * (1 - 1.0 / 255);
            return (r.Status == "measured" && Is(r.AlphaVisible, want, want), $"want {want}: " + Say(r));
        });

        // K8: a crossing lying along an alpha mesh's edge at a UV seam. A: two triangles sharing E = (0,50,0)-(100,50,0), one rising
        // to (50,0,30), one falling to (50,100,-30) (opposite sides of B), so the crossing with the opaque plate z = 0 lies on E
        // (100 u, edge pieces). On E, the rising triangle's UV is (x/100, 0.3125) (K1 row 2: 37.45 u drawn), the falling one's
        // (x/100, 0.8125) (row 6: none drawn). Expected [0, 12.5 (3 - 1/255)]. Rejects one triangle's UV for both sides.
        Case(check, "alpha K8: edge-lying crossing at a UV seam — [intersection 0, union 12.5(3 - 1/255)]", () =>
        {
            var a = Tris(new[] { V(0, 50, 0), V(100, 50, 0), V(50, 0, 30) }, new[] { V(100, 50, 0), V(0, 50, 0), V(50, 100, -30) });
            var b = Tris(new[] { V(-10, -10, 0), V(110, -10, 0), V(110, 110, 0) }, new[] { V(-10, -10, 0), V(110, 110, 0), V(-10, 110, 0) });
            var rows = new List<TriangleTexcoords>
            {
                new(0, 0f, 0.3125f, 1f, 0.3125f, 0.5f, 0f, 1, 1, 1),
                new(0, 1f, 0.8125f, 0f, 0.8125f, 0.5f, 1f, 1, 1, 1),
            };
            var tc = new SurfaceTexcoordSet("measured", null, rows, new[] { Rule() });
            var r = SurfaceVisibility.Measure(a, Tags(a, SurfaceMaterial.AlphaTest), Member(tc, k1Plane), b, Tags(b, SurfaceMaterial.Opaque), null);
            double want = 12.5 * (3 - 1.0 / 255);
            return (r.Status == "measured" && Is(r.AlphaVisible, 0, want) && Near(r.Contact?.CrossingLength?.Lower, 100), Say(r));
        });

        // K9: parity — the pieces sum to the pair measure's per-tag crossing exactly, and the contact equals MeshSurface.Measure.
        // Rejects pieces lost or double-counted, and a sink that changes the default result. Fixtures: K1, K7's B, K8, a turned pair.
        Case(check, "alpha K9: pieces sum to CrossingLengthByTags; contact equals MeshSurface.Measure", () =>
        {
            var k7b = Tris(new[] { V(0, 31.25f, -50), V(100, 31.25f, -50), V(100, 31.25f, 50) }, new[] { V(0, 31.25f, -50), V(100, 31.25f, 50), V(0, 31.25f, 50) });
            var k8a = Tris(new[] { V(0, 50, 0), V(100, 50, 0), V(50, 0, 30) }, new[] { V(100, 50, 0), V(0, 50, 0), V(50, 100, -30) });
            var k8b = Tris(new[] { V(-10, -10, 0), V(110, -10, 0), V(110, 110, 0) }, new[] { V(-10, -10, 0), V(110, 110, 0), V(-10, 110, 0) });
            var turned = Tris(new[] { V(13, -7, -20), V(80, 95, 25), V(-30, 60, 33) }, new[] { V(-5, 4, 40), V(70, 10, -35), V(20, 90, -10) });
            var pairs = new (TriangleSoup A, string TA, TriangleSoup B, string TB)[]
            {
                (QuadA(), SurfaceMaterial.AlphaTest, WallY(31.25f), SurfaceMaterial.Opaque),
                (QuadA(), SurfaceMaterial.AlphaTest, k7b, SurfaceMaterial.AlphaBlend),
                (k8a, SurfaceMaterial.AlphaTest, k8b, SurfaceMaterial.Opaque),
                (QuadA(), SurfaceMaterial.Opaque, turned, SurfaceMaterial.AlphaTest),
            };
            var detail = new List<string>(); bool ok = true;
            foreach (var (pa, ta, pb, tb) in pairs)
            {
                var (contact, pieces) = SurfaceVisibility.MeasureWithPieces(pa, Tags(pa, ta), pb, Tags(pb, tb), VolumeGeometry.Render);
                var plain = MeshSurface.Measure(pa, Tags(pa, ta), pb, Tags(pb, tb), VolumeGeometry.Render);
                var sums = pieces.Where(p => p.Decided).GroupBy(p => p.TagKey).ToDictionary(g => g.Key, g => g.Sum(p => p.Length));
                bool same = plain.CrossingLengthByTags is { } d && d.Count == sums.Count && d.All(kv => sums.TryGetValue(kv.Key, out var s) && Math.Abs(s - kv.Value) <= 1e-9 * Math.Max(1, kv.Value))
                            && contact.CrossingLength?.Lower == plain.CrossingLength?.Lower && contact.CrossingLength?.Upper == plain.CrossingLength?.Upper
                            && contact.InteriorSegments == plain.InteriorSegments && contact.EdgeSegments == plain.EdgeSegments && plain.CrossingLength?.Lower > 0;
                ok &= same;
                detail.Add($"{ta}x{tb}: plain [{string.Join(",", plain.CrossingLengthByTags?.Select(kv => kv.Key + "=" + kv.Value) ?? [])}] pieces [{string.Join(",", sums.Select(kv => kv.Key + "=" + kv.Value))}] n {pieces.Count}");
            }
            return (ok, string.Join(" | ", detail));
        });

        // K10: texture coordinates read from a NIF, aligned with the render soup (a hidden shape and an opaque shape before the alpha
        // shape), and the shape's alpha rule read from its properties. Rejects an off-by-shape UV lookup and any rule field misread.
        Case(check, "alpha K10: texcoords aligned with the render soup; rule read from the NIF", () =>
        {
            var nif = K10Nif();
            var surf = MeshSurface.ModelSurface(nif, VolumeGeometry.Render);
            if (surf.Soup is not { } soup) return (false, "fixture: " + surf.Reason);
            var tc = SurfaceTexcoords.Read(nif, soup);
            if (tc.Status != "measured" || tc.Triangles is not { } rows || tc.Shapes is not { } shapes) return (false, $"{tc.Status} {tc.Reason}");
            bool aligned = rows.Count == soup.TriangleCount;
            int alphaRows = 0;
            for (int t = 0; t < soup.TriangleCount && aligned; t++)
            {
                if (shapes[rows[t].Shape].Name != "AlphaQuad") continue;
                alphaRows++;
                var r = rows[t];
                float[] us = [r.U0, r.U1, r.U2], vs = [r.V0, r.V1, r.V2], aa = [r.A0, r.A1, r.A2];
                for (int k = 0; k < 3; k++)
                {
                    var p = soup.Vertices[soup.Indices[3 * t + k]];
                    aligned &= Math.Abs(us[k] - p.X / 100f) < 1e-3 && Math.Abs(vs[k] - p.Y / 100f) < 1e-3 && Math.Abs(aa[k] - p.X / 100f) < 1e-2;
                }
            }
            var rule = shapes.FirstOrDefault(s => s.Name == "AlphaQuad");
            bool ruleOk = rule is { AlphaTest: true, AlphaBlend: false, Threshold: 100, UsesVertexAlpha: true, Refusal: null, EffectShader: false }
                          && Math.Abs(rule.MaterialAlpha - 0.75f) < 1e-6 && rule.UScale == 2 && rule.VScale == 3 && rule.UOffset == 0.25f && rule.VOffset == 0.5f
                          && rule.WrapU == TextureWrap.Clamp && rule.WrapV == TextureWrap.Repeat
                          && string.Equals(rule.TexturePath, "Fixture\\K10.dds", StringComparison.OrdinalIgnoreCase) && rule.Class == SurfaceMaterial.AlphaTest;
            bool noHidden = shapes.All(s => s.Name != "Hidden") || rows.All(r => shapes[r.Shape].Name != "Hidden");
            return (aligned && alphaRows == 2 && ruleOk && noHidden, $"rows {rows.Count}/{soup.TriangleCount} alphaRows {alphaRows} rule {rule}");
        });
        // K10b: a soup that does not match the NIF (one vertex moved) refuses with the alignment reason. Rejects silent misalignment.
        Case(check, "alpha K10b: a misaligned soup refuses (texcoords: soup alignment)", () =>
        {
            var nif = K10Nif();
            var surf = MeshSurface.ModelSurface(nif, VolumeGeometry.Render);
            if (surf.Soup is not { } soup) return (false, "fixture: " + surf.Reason);
            var moved = (Vector3[])soup.Vertices.Clone(); moved[soup.Indices[3 * (soup.TriangleCount - 1)]] += new Vector3(0, 0, 5);
            var tc = SurfaceTexcoords.Read(nif, new TriangleSoup(moved, soup.Indices));
            return (tc.Status == "refused" && tc.Reason is { } why && why.Contains("alignment", StringComparison.OrdinalIgnoreCase), $"{tc.Status} {tc.Reason}");
        });

        // K11: refusals are never "not visible". (a) the texture source refuses for an alpha shape that crosses: the pair is refused
        // and attribution reads unresolved. (b) a texture transform controller on the shader refuses the shape's rule.
        Case(check, "alpha K11a: texture refused -> pair refused, attribution unresolved (never zero)", () =>
        {
            var a = QuadA(); var b = WallY(31.25f);
            var m = new MemberAlpha(Tc(a, Rule(), UvA), _ => (null, "missing texture: textures\\fixture\\a.dds"));
            var r = SurfaceVisibility.Measure(a, Tags(a, SurfaceMaterial.AlphaTest), m, b, Tags(b, SurfaceMaterial.Opaque), null);
            var att = SurfaceVisibility.Attribution(r, null, null, null);
            return (r.Status == "refused" && r.Reason is { } why && why.Contains("missing texture") && r.VisibleCrossing is null && att == "unresolved", $"{att}: {Say(r)}");
        });
        Case(check, "alpha K11b: texture transform controller on the shader refuses the shape's rule", () =>
        {
            var nif = K10Nif(uvController: true);
            var surf = MeshSurface.ModelSurface(nif, VolumeGeometry.Render);
            if (surf.Soup is not { } soup) return (false, "fixture: " + surf.Reason);
            var tc = SurfaceTexcoords.Read(nif, soup);
            var rule = tc.Shapes?.FirstOrDefault(s => s.Name == "AlphaQuad");
            return (rule?.Refusal is { } why && why.Contains("controller", StringComparison.OrdinalIgnoreCase), $"{tc.Status} {tc.Reason} rule {rule}");
        });
        Case(check, "alpha K11c: an alternate texture that cannot be resolved refuses that shape", () =>
        {
            var a = QuadA();
            var tc = Tc(a, Rule() with { Name = "AlphaQuad" }, UvA);
            var (tex, prov) = TextureAlpha.ForMember(tc, new[] { new AlternateTexture("AlphaQuad", 0, null) },
                                                     path => (DdsBc1(), new TextureProvenance(0, path, "Fixture", "loose", null, null, false, null, 0, 0, false, null)));
            var (plane, why) = tex(0);
            return (plane is null && why is not null && prov.Any(p => p.Refusal is not null), $"{why} | {string.Join("; ", prov)}");
        });

        // K12: blend-only shape, alpha 77 (0.302) everywhere: not visible at tau 0.5 and 0.9, visible at 0.1 -> threshold-sensitive.
        // Alpha 242 (0.949): visible at all three -> not sensitive. Rejects the ladder missing or the test threshold used for blend.
        Case(check, "alpha K12: blend-only alpha 0.30 -> sensitive (0.1 visible, 0.5 / 0.9 not); 0.95 -> not sensitive", () =>
        {
            var a = QuadA(); var b = WallY(50);
            var blend = Rule(cls: SurfaceMaterial.AlphaBlend, test: false, blend: true, threshold: 0);
            var low = SurfaceVisibility.Measure(a, Tags(a, SurfaceMaterial.AlphaBlend), Member(Tc(a, blend, UvA), Plane(4, 4, (_, _) => 77)), b, Tags(b, SurfaceMaterial.Opaque), null);
            var high = SurfaceVisibility.Measure(a, Tags(a, SurfaceMaterial.AlphaBlend), Member(Tc(a, blend, UvA), Plane(4, 4, (_, _) => 242)), b, Tags(b, SurfaceMaterial.Opaque), null);
            var tl = low.ByTags?.FirstOrDefault(); var th = high.ByTags?.FirstOrDefault();
            bool ok = Is(low.AlphaVisible, 0, 0) && Is(tl?.LadderLow, 100, 100) && Is(tl?.LadderHigh, 0, 0) && low.ThresholdSensitive
                      && Is(high.AlphaVisible, 100, 100) && Is(th?.LadderLow, 100, 100) && Is(th?.LadderHigh, 100, 100) && !high.ThresholdSensitive;
            return (ok, $"low {Say(low)} | high {Say(high)}");
        });
        // K12b: blend + test shape: its own test threshold decides, not tau (alpha 77, threshold 50 -> visible 100; the ladder null).
        Case(check, "alpha K12b: blend + test uses the test threshold, not tau_blend", () =>
        {
            var a = QuadA(); var b = WallY(50);
            var bt = Rule(cls: SurfaceMaterial.AlphaBlend, test: true, blend: true, threshold: 50);
            var r = SurfaceVisibility.Measure(a, Tags(a, SurfaceMaterial.AlphaBlend), Member(Tc(a, bt, UvA), Plane(4, 4, (_, _) => 77)), b, Tags(b, SurfaceMaterial.Opaque), null);
            return (Is(r.AlphaVisible, 100, 100) && r.ByTags?.FirstOrDefault()?.LadderLow is null && !r.ThresholdSensitive, Say(r));
        });
        // K12c: effect shader blend: labelled an upper bound.
        Case(check, "alpha K12c: effect shader alpha is labelled an upper bound (falloff taken as 1)", () =>
        {
            var a = QuadA(); var b = WallY(50);
            var fx = Rule(cls: SurfaceMaterial.AlphaBlend, test: false, blend: true, threshold: 0, effect: true);
            var r = SurfaceVisibility.Measure(a, Tags(a, SurfaceMaterial.AlphaBlend), Member(Tc(a, fx, UvA), Plane(4, 4, (_, _) => 242)), b, Tags(b, SurfaceMaterial.Opaque), null);
            return (Is(r.AlphaVisible, 100, 100) && r.Labels.Any(l => l.Contains("upper bound", StringComparison.OrdinalIgnoreCase)), Say(r));
        });

        // K14 (fixture control): a posed soup keeps the stored render soup's (NifGeometry.MeasureVisibleRender, which SurfaceTexcoords
        // aligns with) triangle order — a rigid pose preserves every triangle's edge
        // lengths at the same index, and some triangle moved. Holds on the base.
        Case(check, "alpha K14: fixture control — NifMotion.Pose keeps the stored render soup's triangle order", () =>
        {
            var nif = MotionProof.AlphaMoverRig();
            var model = NifMotion.Read(nif, null);
            var start = NifGeometry.MeasureVisibleRender(nif, includeTriangles: true);
            var posed = NifMotion.Pose(nif, model, model.Animations.First().Id, 0.5f, VolumeGeometry.Render);
            if (start.Model?.Soup is not { } s0 || posed.Soup is not { } s1) return (false, $"stored {start.Error}; posed {posed.Status} {posed.Reason}; model {model.Status} {model.Reason}");
            if (s0.TriangleCount != s1.TriangleCount) return (false, $"counts {s0.TriangleCount} vs {s1.TriangleCount}");
            int moved = 0; bool ok = true;
            for (int t = 0; t < s0.TriangleCount; t++)
            {
                Vector3 P(TriangleSoup s, int k) => s.Vertices[s.Indices[3 * t + k]];
                for (int k = 0; k < 3; k++)
                    ok &= Math.Abs(Vector3.Distance(P(s0, k), P(s0, (k + 1) % 3)) - Vector3.Distance(P(s1, k), P(s1, (k + 1) % 3))) < 1e-3f;
                if (Enumerable.Range(0, 3).Any(k => Vector3.Distance(P(s0, k), P(s1, k)) > 1e-3f)) moved++;
            }
            return (ok && moved > 0, $"triangles {s0.TriangleCount} moved {moved}");
        });

        // D5 (F4): swept visible witness. A plate (x = 0, y and z in [-50, 50], u = (y + 50)/100, v = (z + 50)/100, texture rows 0..3
        // opaque) against an opaque horizontal plate z = 0. Sample 1 (t 0) shifted -20: crossing at v 0.7 (rows 5/6: transparent).
        // Sample 2 (t 1) shifted +20: crossing at v 0.3 (rows 1/2: opaque) -> visible_witness at t 1, visible 100. With only sample 1
        // -> no_visible_at_samples, 1 pose. Rejects stopping at the first (alpha-only) sample, and "no visible" read as never.
        Case(check, "alpha D5: swept visible witness at the second sample; one transparent sample -> no_visible_at_samples", () =>
        {
            var plate = Tris(new[] { V(0, -50, -50), V(0, 50, -50), V(0, 50, 50) }, new[] { V(0, -50, -50), V(0, 50, 50), V(0, -50, 50) });
            TriangleSoup Shift(float dz) => new(plate.Vertices.Select(p => p + new Vector3(0, 0, dz)).ToArray(), plate.Indices);
            var tags = Tags(plate, SurfaceMaterial.AlphaTest);
            var tc = Tc(plate, Rule(), p => ((p.Y + 50) / 100f, (p.Z + 50) / 100f));
            var rows = Plane(8, 8, (_, y) => (byte)(y <= 3 ? 255 : 0));
            var floor = Tris(new[] { V(-10, -60, 0), V(10, -60, 0), V(10, 60, 0) }, new[] { V(-10, -60, 0), V(10, 60, 0), V(-10, 60, 0) });
            var floorTags = Tags(floor, SurfaceMaterial.Opaque);
            var two = new SweptRegion("built", null, VolumeGeometry.Render, Array.Empty<MotionAnimation>(),
                new[] { new SweptSample("seq:Fixture", 0f, Shift(-20), tags, 0.25), new SweptSample("seq:Fixture", 1f, Shift(20), tags, 0.25) }, 0.25, true, false);
            var one = two with { Samples = new[] { two.Samples[0] } };
            var d2 = SweptGeometry.ContactVisible(two, Member(tc, rows), floor, floorTags, null, false);
            var d1 = SweptGeometry.ContactVisible(one, Member(tc, rows), floor, floorTags, null, false);
            bool ok = d2.Decision == "visible_witness" && d2.Time == 1f && Near(d2.VisibleLower, 100, 1e-6)
                      && d1.Decision == "no_visible_at_samples" && d1.SampledPoses == 1 && Near(d1.Spacing, 0.25);
            return (ok, $"two: {d2.Decision} t {d2.Time} vis {d2.VisibleLower} | one: {d1.Decision} poses {d1.SampledPoses} spacing {d1.Spacing} {string.Join("; ", d1.Labels)}");
        });

        // K15: paths and the texture source. NormalizePath; ForMember asks the reader for the normalised NIF path, or the alternate
        // texture's path when one names the shape, and records provenance. Rejects an unnormalised lookup and an ignored alternate.
        Case(check, "alpha K15a: NormalizePath", () =>
        {
            var got = new[] { TextureAlpha.NormalizePath("Textures/Foo/Bar.DDS"), TextureAlpha.NormalizePath("data\\textures\\x.dds"),
                              TextureAlpha.NormalizePath("foo\\x.dds"), TextureAlpha.NormalizePath("") };
            bool ok = got[0] == "textures\\foo\\bar.dds" && got[1] == "textures\\x.dds" && got[2] == "textures\\foo\\x.dds" && got[3] is null;
            return (ok, string.Join(" | ", got.Select(g => g ?? "null")));
        });
        Case(check, "alpha K15b: the reader is asked for the NIF path, or the alternate texture's, with provenance", () =>
        {
            var a = QuadA();
            var asked = new List<string>();
            (byte[]?, TextureProvenance) Read(string path) { asked.Add(path); return (DdsBc1(), new TextureProvenance(0, path, "FixtureMod", "loose", null, null, false, null, 0, 0, false, null)); }
            var tc = Tc(a, Rule() with { Name = "AlphaQuad", TexturePath = "Fixture\\A.dds" }, UvA);
            var (t1, p1) = TextureAlpha.ForMember(tc, null, Read);
            var plain = t1(0);
            var (t2, p2) = TextureAlpha.ForMember(tc, new[] { new AlternateTexture("AlphaQuad", 0, "Fixture\\Alt.dds") }, Read);
            var alt = t2(0);
            bool ok = plain.Plane is not null && alt.Plane is not null && asked.SequenceEqual(new[] { "textures\\fixture\\a.dds", "textures\\fixture\\alt.dds" })
                      && p2.Any(p => p.AlternateTexture && p.Provider == "FixtureMod") && p1.All(p => !p.AlternateTexture);
            return (ok, $"asked [{string.Join(", ", asked)}] p1 [{string.Join("; ", p1)}] p2 [{string.Join("; ", p2)}]");
        });
        Case(check, "alpha K15c: Reader returns the VFS winner's bytes and provider (later mod wins over earlier)", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "hc-alpha-k15c-" + Guid.NewGuid().ToString("N"));
            try
            {
                string mods = Path.Combine(root, "mods"), data = Path.Combine(root, "data"), ow = Path.Combine(root, "overwrite");
                foreach (var (mod, payload) in new[] { ("ModEarly", DdsBgra()), ("ModLate", DdsBc1()) })
                {
                    var dir = Path.Combine(mods, mod, "textures", "fixture"); Directory.CreateDirectory(dir);
                    File.WriteAllBytes(Path.Combine(dir, "a.dds"), payload);
                }
                Directory.CreateDirectory(data); Directory.CreateDirectory(ow);
                using var assets = AssetResolver.Build(ow, mods, data, new[] { "ModEarly", "ModLate" }, Array.Empty<ActiveArchive>());
                var expect = assets.ResolveForPlacement("textures\\fixture\\a.dds");
                var (bytes, where) = TextureAlpha.Reader(assets)("textures\\fixture\\a.dds");
                bool ok = expect.Sources.Count > 0 && bytes is not null && where.Provider == expect.Sources[0].ProviderName && where.Kind == "loose"
                          && bytes.SequenceEqual(File.ReadAllBytes(expect.Sources[0].LooseFilePath!)) && where.Sha256 is { Length: 64 };
                return (ok, $"winner {expect.Sources.FirstOrDefault()?.ProviderName} got {where.Provider} {where.Kind} sha {where.Sha256}");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

        // K16: attribution on the visible crossing: an alpha-only crossing visible in the combined arm and absent elsewhere is
        // needs_both, where the opaque-only rule says no_overlap. Rejects attribution left on the opaque interval.
        Case(check, "alpha K16: visible alpha crossing attributes needs_both (opaque-only rule: no_overlap)", () =>
        {
            var a = QuadA(); var b = WallY(31.25f);
            var r = SurfaceVisibility.Measure(a, Tags(a, SurfaceMaterial.AlphaTest), Member(Tc(a, Rule(), UvA), k1Plane), b, Tags(b, SurfaceMaterial.Opaque), null);
            var att = SurfaceVisibility.Attribution(r, null, null, null);
            var old = r.Contact is { } c ? MeshSurface.Attribution(c, null, null, null) : "no contact";
            return (att == "needs_both" && old == "no_overlap", $"visible {att} opaque-only {old}");
        });
    }

}
