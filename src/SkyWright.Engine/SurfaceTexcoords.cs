using System.Numerics;
using System.Reflection;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;

using HousecarlCore;

namespace SkyWright.Engine;

// PER-TRIANGLE TEXTURE COORDINATES OF A RENDER SURFACE (lane D, Stage D alpha sampling). For every triangle of the render soup
// MeshSurface.ModelSurface reads (NifGeometry.MeasureVisibleRender: same shape order, hidden-shape exclusion and out-of-range drop
// rule), the shape it came from, its three vertices' UV (set 0) and vertex-colour alphas, and per shape the alpha rule the engine
// applies: NiAlphaProperty test / blend flags, test function and threshold; the shader's material alpha, vertex-alpha use, UV
// scale and offset, clamp mode and diffuse (lighting) or source (effect) texture path. Alignment with the soup is proven on every
// call: each triangle's three model-space positions are re-derived and compared with the soup's; a mismatch refuses.
//
// STATUS: API frozen at the lane D Gate B red gate; implemented at Gate C.

/// <summary>The alpha rule of one render shape. Class is the <see cref="SurfaceMaterial"/> tag
/// (<see cref="MeshVolume"/>'s classifier). Threshold is the NiAlphaProperty byte; TestFunction its bits 10-12 as stored.
/// UsesVertexAlpha: the shader reads vertex-colour alpha (vertex colours present and the shader's vertex-alpha flag set). Refusal
/// names why this shape's alpha cannot be read (an alpha or texture-coordinate controller, greyscale-to-palette alpha, no
/// texture path); null when readable.</summary>
public sealed record ShapeAlphaRule(int Shape, string Name, string Class, bool AlphaTest, bool AlphaBlend, byte Threshold, int TestFunction,
                                    float MaterialAlpha, bool UsesVertexAlpha, float UScale, float VScale, float UOffset, float VOffset,
                                    TextureWrap WrapU, TextureWrap WrapV, string? TexturePath, bool EffectShader, string? Refusal);

/// <summary>One soup triangle: its shape index and its three vertices' UV (set 0, as stored, before the shader's transform) and
/// vertex-colour alpha (1 when the shape has no vertex colours).</summary>
public readonly record struct TriangleTexcoords(int Shape, float U0, float V0, float U1, float V1, float U2, float V2, float A0, float A1, float A2);

/// <summary>Status <c>measured</c> or <c>refused</c> (Reason names it, e.g. <c>texcoords: soup alignment failed at triangle N</c>).
/// Triangles is index-aligned with the soup the caller passed.</summary>
public sealed record SurfaceTexcoordSet(string Status, string? Reason, IReadOnlyList<TriangleTexcoords>? Triangles, IReadOnlyList<ShapeAlphaRule>? Shapes);

public static class SurfaceTexcoords
{
    /// <summary>Texture coordinates for every triangle of <paramref name="modelSoup"/>, which must be the stored-pose model-space render
    /// soup of the same bytes (<see cref="MeshSurface.ModelSurface"/>'s, or <see cref="NifGeometry.MeasureVisibleRender"/>'s for a member
    /// whose live pose refuses the former). A posed soup from <see cref="NifMotion.Pose"/> keeps that triangle order and reuses the result.</summary>
    public static SurfaceTexcoordSet Read(byte[] nifBytes, TriangleSoup modelSoup)
    {
        SurfaceTexcoordSet Refuse(string why) => new("refused", "texcoords: " + why, null, null);
        if (modelSoup is null) return Refuse("no soup");
        var g = NifGeometry.MeasureVisibleRender(nifBytes, includeTriangles: true);
        if (g.Model is not { Soup: { } stored, SoupShapeOfTriangle: { } map } model) return Refuse("render unreadable: " + g.Error);
        // 1. the caller's soup is the stored render soup: same triangles, same corner positions
        if (modelSoup.TriangleCount != stored.TriangleCount)
            return Refuse($"soup alignment failed: {modelSoup.TriangleCount} triangles, the stored render soup has {stored.TriangleCount}");
        for (int t = 0; t < stored.TriangleCount; t++)
            for (int k = 0; k < 3; k++)
                if (modelSoup.Vertices[modelSoup.Indices[3 * t + k]] != stored.Vertices[stored.Indices[3 * t + k]])
                    return Refuse($"soup alignment failed at triangle {t}");

        var nif = new NifFile();
        using (var ms = new MemoryStream(nifBytes, writable: false))
            if (nif.Load(ms) != 0) return Refuse("the NIF could not be re-read");
        var shapes = nif.GetShapes().ToList();
        if (shapes.Count != model.Shapes.Count) return Refuse($"shape count {shapes.Count} differs from the geometry reader's {model.Shapes.Count}");
        var classes = MeshVolume.ShapeMaterials(nifBytes);
        if (classes is null || classes.Count != shapes.Count) return Refuse("the NIF could not be re-read for its materials");

        // 2. rebuild the soup's index layout per shape with NifGeometry's rules (visible shapes only, out-of-range triangles dropped)
        var rows = new TriangleTexcoords[stored.TriangleCount];
        var rules = new List<ShapeAlphaRule>();
        int vertexBase = 0, next = 0;
        for (int i = 0; i < shapes.Count; i++)
        {
            var shape = shapes[i];
            rules.Add(RuleOf(nif, shape, i, classes[i].Name, classes[i].Class));
            bool hidden = false;
            for (NiAVObject? cur = shape as NiAVObject; cur is not null; cur = nif.GetParentNode(cur))
                if (((cur.Flags_ui | cur.Flags_us) & 1) != 0) { hidden = true; break; }
            var positions = Positions(shape);
            if (hidden || positions is null) continue;
            var uvs = Uvs(shape);
            var alphas = VertexAlphas(shape);
            List<NiflySharp.Structs.Triangle>? tris = null;
            try { tris = shape.Triangles; } catch { }
            if (tris is not null)
                foreach (var tri in tris)
                {
                    if (tri.V1 >= positions.Count || tri.V2 >= positions.Count || tri.V3 >= positions.Count) continue;
                    if (next >= rows.Length) return Refuse($"soup alignment failed: shape '{classes[i].Name}' adds more triangles than the soup holds");
                    int t = next++;
                    if (map[t] != i || stored.Indices[3 * t] != vertexBase + tri.V1 || stored.Indices[3 * t + 1] != vertexBase + tri.V2 || stored.Indices[3 * t + 2] != vertexBase + tri.V3)
                        return Refuse($"soup alignment failed at triangle {t} (shape '{classes[i].Name}')");
                    Vector2 Uv(int v) => uvs is not null && v < uvs.Count ? uvs[v] : new Vector2(float.NaN, float.NaN);
                    float A(int v) => alphas is not null && v < alphas.Count ? alphas[v] : 1f;
                    Vector2 a = Uv(tri.V1), b = Uv(tri.V2), c = Uv(tri.V3);
                    rows[t] = new TriangleTexcoords(i, a.X, a.Y, b.X, b.Y, c.X, c.Y, A(tri.V1), A(tri.V2), A(tri.V3));
                }
            if (uvs is null || uvs.Count < positions.Count)
                rules[i] = rules[i] with { Refusal = rules[i].Refusal ?? $"shape '{classes[i].Name}' has no texture coordinates" };
            vertexBase += positions.Count;
        }
        if (next != rows.Length) return Refuse($"soup alignment failed: {next} triangles rebuilt, the soup holds {rows.Length}");
        return new("measured", null, rows, rules);
    }

    // The alpha rule of one shape (class from MeshVolume.ShapeMaterials; flags and values from the properties).
    static ShapeAlphaRule RuleOf(NifFile nif, INiShape shape, int index, string name, string cls)
    {
        var refusals = new List<string>();
        bool test = false, blend = false; byte threshold = 0; int func = 0;
        if (shape.HasAlphaProperty && nif.GetBlock<NiAlphaProperty>(shape.AlphaPropertyRef) is { } ap)
        {
            test = ap.Flags.AlphaTest; blend = ap.Flags.AlphaBlend; threshold = ap.Threshold; func = (ap.Flags.Value >> 10) & 7;
            if (ControllerNames(nif, ap.Controller?.Index ?? -1).FirstOrDefault() is { } c) refusals.Add($"animated alpha: {c} on the alpha property");
        }
        var shader = nif.GetShader(shape);
        float material = 1f, us = 1, vs = 1, uo = 0, vo = 0;
        bool vertexAlpha = false, effect = false;
        var wu = TextureWrap.Repeat; var wv = TextureWrap.Repeat;
        string? path = null;
        bool hasColours = VertexAlphas(shape) is { Count: > 0 };
        if (shader is BSLightingShaderProperty ls)
        {
            material = ls.Alpha;
            us = ls.UVScale.U; vs = ls.UVScale.V; uo = ls.UVOffset.U; vo = ls.UVOffset.V;
            vertexAlpha = hasColours && ls.ShaderFlags_SSPF1.HasFlag(SkyrimShaderPropertyFlags1.Vertex_Alpha);
            var (mode, why) = NiflyClampAccess.LightingClampMode(ls);
            if (mode is { } m) (wu, wv) = NiflyClampAccess.Wraps(m); else refusals.Add(why!);
            if (ls.TextureSetRef is not null && nif.GetBlock(ls.TextureSetRef) is BSShaderTextureSet ts && ts.Textures is { Count: > 0 } tx)
                path = tx[0]?.Content;
            if (ls.ShaderFlags_SSPF1.HasFlag(SkyrimShaderPropertyFlags1.Greyscale_To_PaletteAlpha)) refusals.Add("greyscale-to-palette alpha is not modelled");
        }
        else if (shader is BSEffectShaderProperty es)
        {
            effect = true;
            material = es.BaseColor.A;
            us = es.UVScale.U; vs = es.UVScale.V; uo = es.UVOffset.U; vo = es.UVOffset.V;
            vertexAlpha = hasColours && es.ShaderFlags_SSPF1.HasFlag(SkyrimShaderPropertyFlags1.Vertex_Alpha);
            var (emode, ewhy) = NiflyClampAccess.FromNumber(Convert.ToInt64(es.TextureClampMode));
            if (emode is { } em) (wu, wv) = NiflyClampAccess.Wraps(em); else refusals.Add(ewhy!);
            path = es.SourceTexture?.Content;
            if (es.ShaderFlags_SSPF1.HasFlag(SkyrimShaderPropertyFlags1.Greyscale_To_PaletteAlpha)) refusals.Add("greyscale-to-palette alpha is not modelled");
        }
        else if (cls is SurfaceMaterial.AlphaTest or SurfaceMaterial.AlphaBlend) refusals.Add("no lighting or effect shader: the texture is unknown");
        if (shader is NiObjectNET net)
            foreach (var c in ControllerNames(nif, net.Controller?.Index ?? -1))
                if (c.Contains("TextureTransform", StringComparison.Ordinal) || c.Contains("Flip", StringComparison.Ordinal)
                    || c.Contains("Alpha", StringComparison.Ordinal) || c.Contains("Offset", StringComparison.Ordinal) || c.Contains("Scale", StringComparison.Ordinal))
                    refusals.Add($"animated alpha or texture coordinates: {c} on the shader");
        if (string.IsNullOrWhiteSpace(path)) path = null;
        return new(index, name, cls, test, blend, threshold, func, material, vertexAlpha, us, vs, uo, vo, wu, wv, path, effect,
                   refusals.Count == 0 ? null : string.Join("; ", refusals));
    }

    // Every controller in a chain, as "Type" or "Type(variable)" for shader float controllers.
    static IEnumerable<string> ControllerNames(NifFile nif, int index)
    {
        for (int guard = 0; index >= 0 && index < nif.Blocks.Count && guard < 64; guard++)
        {
            var block = nif.Blocks[index];
            string name = block.GetType().Name;
            var variable = block.GetType().GetProperty("ControlledVariable", BindingFlags.Public | BindingFlags.Instance)?.GetValue(block);
            yield return variable is null ? name : $"{name}({variable})";
            index = block is NiTimeController tc ? (tc.NextController?.Index ?? -1) : -1;
        }
    }

    // Vertex positions the way NifGeometry reads them (a VertexPositions / Vertices list on the shape, else on its data block).
    static List<Vector3>? Positions(INiShape shape)
    {
        static List<Vector3>? Read(object? o)
        {
            if (o is null) return null;
            var p = o.GetType().GetProperty("VertexPositions", BindingFlags.Public | BindingFlags.Instance)
                    ?? o.GetType().GetProperty("Vertices", BindingFlags.Public | BindingFlags.Instance);
            if (p is null || p.PropertyType != typeof(List<Vector3>)) return null;
            try { return p.GetValue(o) as List<Vector3>; } catch { return null; }
        }
        if (Read(shape) is { } direct) return direct;
        try { return Read(shape.GeometryData); } catch { return null; }
    }

    static List<Vector2>? Uvs(INiShape shape)
    {
        try
        {
            if (shape is BSTriShape bt) return bt.UVs?.Select(u => new Vector2(u.U, u.V)).ToList();
            if (shape.GeometryData is NiGeometryData gd && gd.UVSets is { Count: > 0 } sets) return sets.Select(u => new Vector2(u.U, u.V)).ToList();   // set-major: set 0 is the first vertex-count entries
        }
        catch { }
        return null;
    }

    static List<float>? VertexAlphas(INiShape shape)
    {
        try
        {
            if (shape is BSTriShape bt) return bt.HasVertexColors ? bt.VertexColors?.Select(c => c.A).ToList() : null;
            if (shape.GeometryData is NiGeometryData gd && gd.VertexColors is { Count: > 0 } vc) return vc.Select(c => c.A).ToList();
        }
        catch { }
        return null;
    }
}

