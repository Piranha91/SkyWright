using SkyRender;
using GlVec3 = OpenTK.Mathematics.Vector3;
using ViewProbe;

namespace Workbench;

/// <summary>What clashes with what (stage-3 review M-1): the selected reference's triangle edges in one colour and the
/// conflict's other reference's in another, drawn by SkyRender's overlay line pass, which turns the depth test off, so
/// both show through whatever covers them (an overlap row's reference is often inside its partner). The legend names the
/// colours; <see cref="Enabled"/> is the bar's "Marks" toggle.</summary>
internal sealed class ConflictMarks : IRenderOverlay
{
    internal static readonly GlVec3 SelectedColour = new(0f, 1f, 1f);     // cyan
    internal static readonly GlVec3 PartnerColour = new(1f, 0f, 1f);      // magenta
    internal const string Legend = "cyan: the selected reference; magenta: the other reference of the open conflict (both drawn through anything in front)";
    internal const int MaxEdges = 40000;                                  // per reference; a whole building stays readable

    internal RefOcclusion? Selected, Partner;
    internal bool Enabled = Environment.GetEnvironmentVariable("WB_MARKS") != "off";
    readonly List<OverlayLineSegment> _lines = new();

    internal static IEnumerable<(System.Numerics.Vector3 A, System.Numerics.Vector3 B)> Edges(RefOcclusion ro)
    {
        var seen = new HashSet<(int, int, int)>();
        int n = 0;
        for (int s = 0; s < ro.Shapes.Count; s++)
        {
            var sh = ro.Shapes[s];
            for (int t = 0; t + 2 < sh.Tris.Length; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = sh.Tris[t + e], b = sh.Tris[t + (e + 1) % 3];
                    if (!seen.Add((s, Math.Min(a, b), Math.Max(a, b)))) continue;
                    if (++n > MaxEdges) yield break;
                    yield return (sh.Verts[a], sh.Verts[b]);
                }
        }
    }

    public void Draw(GlRenderer renderer, in OverlayContext context)
    {
        if (!Enabled || (Selected is null && Partner is null)) return;
        _lines.Clear();
        void Add(RefOcclusion? ro, GlVec3 col)
        {
            if (ro is null) return;
            foreach (var (a, b) in Edges(ro))
                _lines.Add(new OverlayLineSegment { A = new GlVec3(a.X, a.Y, a.Z), B = new GlVec3(b.X, b.Y, b.Z), Color = col });
        }
        Add(Partner, PartnerColour);
        Add(Selected, SelectedColour);
        if (!renderer.BeginOverlayLinePass(context, out var state)) return;
        renderer.DrawOverlayLines(_lines, 1.5f, context);
        renderer.EndOverlayLinePass(state);
    }
}
