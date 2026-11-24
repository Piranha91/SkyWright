using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;

using HousecarlCore;

namespace SkyWright.Engine;

// WIND-ANIMATED SHAPES IN STATIC GEOMETRY (animated motion follow-ups, item 1, decision E1). A render shape whose
// lighting or effect shader carries Skyrim shader flags 2 Tree_Anim is moved by the vertex shader at runtime with a
// per-vertex weight (vertex colour alpha) and an amplitude the engine sets; the files give no amplitude. The static
// measurements (contact rows, --surface, --volume, the pair review, overlap discovery) keep their values and carry a
// label stating only what the files say: the flag, how many vertices have alpha above 0, the largest alpha. Nothing
// here says a shape is still or gives a bound.
//
// STATUS: API frozen at the Gate B red gate; implemented.

/// <summary>One shape (NifFile.GetShapes order) whose shader carries Tree_Anim. Colours false when the shape stores no
/// vertex colours (then the counts are 0).</summary>
public sealed record WindShape(int ShapeIndex, string Name, string Shader, int Vertices, bool Colours,
                               int VerticesAlphaAboveZero, float AlphaMax);

/// <summary>The label a static result carries for a member whose mesh has wind shapes.</summary>
public sealed record WindLabel(string Flag, IReadOnlyList<WindShape> Shapes, string Note);

public static class NifWind
{
    public const string Flag = "Tree_Anim";

    /// <summary>The fixed note: file facts only (review F4).</summary>
    public const string Note = "these shapes carry the Tree_Anim flag (wind animation); measured at their stored vertex positions; the files give no sway amplitude";

    /// <summary>Whether a shader carries Tree_Anim (lighting or effect shader).</summary>
    public static bool IsTreeAnim(INiShader? shader) => shader switch
    {
        BSLightingShaderProperty l => l.ShaderFlags_SSPF2.HasFlag(SkyrimShaderPropertyFlags2.Tree_Anim),
        BSEffectShaderProperty e => e.ShaderFlags_SSPF2.HasFlag(SkyrimShaderPropertyFlags2.Tree_Anim),
        _ => false,
    };

    /// <summary>Every Tree_Anim shape of a NIF that the static readers measure (not hidden by a flag on the shape or an
    /// ancestor, NifGeometry's rule), in GetShapes order; empty when none or the NIF does not load.</summary>
    public static IReadOnlyList<WindShape> Shapes(byte[] nif)
    {
        var f = new NifFile();
        try { using var ms = new MemoryStream(nif, writable: false); if (f.Load(ms) != 0) return Array.Empty<WindShape>(); }
        catch { return Array.Empty<WindShape>(); }
        var list = new List<WindShape>();
        int index = 0;
        foreach (var sh in f.GetShapes())
        {
            int i = index++;
            var shader = f.GetShader(sh);
            if (!IsTreeAnim(shader) || Hidden(f, sh as NiAVObject)) continue;
            List<NiflySharp.Structs.Color4>? colours = sh switch
            {
                BSTriShape bt when bt.HasVertexColors => bt.VertexColors,
                NiGeometry g when g.GeometryData is { HasVertexColors: true } gd => gd.VertexColors,
                _ => null,
            };
            int above = 0; float max = 0;
            if (colours is not null)
                foreach (var c in colours) { if (c.A > 0) above++; if (c.A > max) max = c.A; }
            string name = string.IsNullOrEmpty(sh.Name?.String) ? $"#{i + 1}" : sh.Name!.String!;
            list.Add(new WindShape(i, name, shader!.GetType().Name, sh.VertexCount, colours is { Count: > 0 }, above, max));
        }
        return list;
    }

    // NifGeometry's hidden rule: the Hidden bit (1) on the shape or any node above it
    static bool Hidden(NifFile f, NiAVObject? shape)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (var cur = shape; cur is not null && seen.Add(cur) && seen.Count < 256; cur = f.GetParentNode(cur))
            if (((cur.Flags_ui | cur.Flags_us) & 1) != 0) return true;
        return false;
    }

    /// <summary>The label for a mesh, or null when it has no Tree_Anim shape.</summary>
    public static WindLabel? Label(byte[] nif)
    {
        var s = Shapes(nif);
        return s.Count == 0 ? null : new WindLabel(Flag, s, Note);
    }

    /// <summary>The label as every probe writes it (one JSON shape whatever the probe's naming policy).</summary>
    public static object? Json(WindLabel? label) => label is null ? null : new
    {
        flag = label.Flag,
        shapes = label.Shapes.Select(x => new { shapeIndex = x.ShapeIndex, shape = x.Name, shader = x.Shader, vertices = x.Vertices, colours = x.Colours,
                                                verticesAlphaAboveZero = x.VerticesAlphaAboveZero, alphaMax = x.AlphaMax }).ToArray(),
        note = label.Note,
    };

    /// <summary>The one-line note a contact row's dependent record carries: only when its contact geometry is its render
    /// fallback (<paramref name="renderSourced"/>); a collision-sourced dependent is not moved by the vertex shader.</summary>
    public static string? ContactNote(WindLabel? label, bool renderSourced)
    {
        if (label is null || !renderSourced) return null;
        var shapes = string.Join("; ", label.Shapes.Select(x => x.Colours
            ? $"shape '{x.Name}': {x.VerticesAlphaAboveZero} of {x.Vertices} vertices with alpha above 0, alpha max {x.AlphaMax.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}"
            : $"shape '{x.Name}': no vertex colours"));
        return $"{label.Flag}: {shapes}. {label.Note}";
    }
}
