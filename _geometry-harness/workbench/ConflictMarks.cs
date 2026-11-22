using System.Numerics;
using HousecarlCore;
using SkyRender;
using GlVec3 = OpenTK.Mathematics.Vector3;
using ViewProbe;

namespace Workbench;

/// <summary>What clashes with what (stage-3 review M-1; walkthrough problems 5 and 10): the selected reference drawn solid
/// with a cyan tint and the conflict's other reference with a magenta one, over their own textures, so both read as the
/// objects they are; where the two pass into each other in bright yellow. The tints are SkyRender's depth-tested
/// translucent tint (they shade what is in view); so that a buried object (an overlap row's object is often inside its
/// partner) is still seen, each object's outline and the line where the two cross are also drawn through anything in front,
/// the outline dimmed. Where they cross is houseCARL's own measure (<see cref="SurfaceVisibility.MeasureWithPieces"/>):
/// its crossing segments and the triangles they lie on. <see cref="Enabled"/> is the bar's "Marks" toggle.</summary>
internal sealed class ConflictMarks : IRenderOverlay
{
    internal static readonly GlVec3 SelectedColour = new(0f, 1f, 1f);     // cyan
    internal static readonly GlVec3 PartnerColour = new(1f, 0f, 1f);      // magenta
    internal static readonly GlVec3 OverlapColour = new(1f, 0.9f, 0.05f); // yellow
    internal const string Legend = "cyan tint: the selected object; magenta tint: the other object of the open clash; yellow: where the two pass into each other "
                                 + "(their outlines and the yellow line show through anything in front; leaves and other see-through cards are outlined, not tinted)";
    internal const int MaxEdges = 40000;                                  // per reference; a whole building stays readable
    const float TintAlpha = 0.45f, OverlapAlpha = 0.9f, OutlineDim = 0.55f;
    const double CreaseCos = 0.77;                                        // an edge between faces more than about 40° apart is drawn

    internal RefOcclusion? Selected, Partner;
    internal bool Enabled = Environment.GetEnvironmentVariable("WB_MARKS") != "off";
    /// <summary>Asked for a new frame when the crossing measured off the window's thread arrives.</summary>
    internal Action? Changed;

    RefOcclusion? _builtSel, _builtPartner;
    List<float> _selTris = new(), _partnerTris = new(), _overlapTris = new();
    readonly List<OverlayLineSegment> _lines = new();
    List<(Vector3 A, Vector3 B)> _crossing = new();
    int _generation;
    CancellationTokenSource? _measuring;

    /// <summary>What the crossing measure said for the pair shown: null while measuring or with no pair.</summary>
    internal string? Overlap { get; private set; }
    internal int CrossingSegments => _crossing.Count;

    /// <summary>Every triangle edge of a reference, once each (the pick shapes, Y-up).</summary>
    internal static IEnumerable<(Vector3 A, Vector3 B)> Edges(RefOcclusion ro)
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

    /// <summary>The edges that give a reference its shape: an open edge (one face) and a crease (faces more than about
    /// 40° apart). Edges across a smooth surface are left out, so the outline reads as the object, not as its triangles.</summary>
    internal static IEnumerable<(Vector3 A, Vector3 B, bool CutOut)> Outline(RefOcclusion ro)
    {
        int n = 0;
        for (int s = 0; s < ro.Shapes.Count; s++)
        {
            var sh = ro.Shapes[s];
            var faces = new Dictionary<(int, int), (Vector3 N, int Count, bool Crease)>();
            for (int t = 0; t + 2 < sh.Tris.Length; t += 3)
            {
                Vector3 a = sh.Verts[sh.Tris[t]], b = sh.Verts[sh.Tris[t + 1]], c = sh.Verts[sh.Tris[t + 2]];
                var nrm = Vector3.Cross(b - a, c - a);
                if (nrm.LengthSquared() == 0) continue;
                nrm = Vector3.Normalize(nrm);
                for (int e = 0; e < 3; e++)
                {
                    int i = sh.Tris[t + e], j = sh.Tris[t + (e + 1) % 3];
                    var key = (Math.Min(i, j), Math.Max(i, j));
                    faces[key] = faces.TryGetValue(key, out var f) ? (f.N, f.Count + 1, f.Crease || Vector3.Dot(f.N, nrm) < CreaseCos) : (nrm, 1, false);
                }
            }
            foreach (var ((i, j), f) in faces)
            {
                if (f.Count != 1 && !f.Crease) continue;
                if (++n > MaxEdges) yield break;
                yield return (sh.Verts[i], sh.Verts[j], CutOut(sh));
            }
        }
    }

    static void AddTriangle(List<float> into, Vector3 a, Vector3 b, Vector3 c)
    {
        var n = Vector3.Cross(b - a, c - a);
        n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitY;
        foreach (var p in new[] { a, b, c }) { into.Add(p.X); into.Add(p.Y); into.Add(p.Z); into.Add(n.X); into.Add(n.Y); into.Add(n.Z); }
    }

    /// <summary>A shape cut out by its texture's alpha (leaves, grass cards, hair): its triangles are cards much larger
    /// than what is drawn, so a tint over them would paint the card; such a shape is marked by its outline only.</summary>
    internal static bool CutOut(PickShape sh) => sh.Mesh is { } m && (m.UseAlphaTest || m.HasAlphaBlend);

    static List<float> Triangles(RefOcclusion? ro)
    {
        var list = new List<float>();
        if (ro is null) return list;
        foreach (var sh in ro.Shapes.Where(x => !CutOut(x)))
            for (int t = 0; t + 2 < sh.Tris.Length; t += 3) AddTriangle(list, sh.Verts[sh.Tris[t]], sh.Verts[sh.Tris[t + 1]], sh.Verts[sh.Tris[t + 2]]);
        return list;
    }

    /// <summary>One reference's drawn shapes as one soup (Y-up), the frame the crossing is measured and drawn in.</summary>
    static (TriangleSoup Soup, bool[] CutOut) Soup(RefOcclusion ro)
    {
        var verts = new List<Vector3>(); var idx = new List<int>(); var cut = new List<bool>();
        foreach (var sh in ro.Shapes)
        {
            int at = verts.Count;
            verts.AddRange(sh.Verts);
            foreach (var i in sh.Tris) idx.Add(at + i);
            cut.AddRange(Enumerable.Repeat(CutOut(sh), sh.Tris.Length / 3));
        }
        return (new TriangleSoup(verts.ToArray(), idx.ToArray()), cut.ToArray());
    }

    /// <summary>Set the pair the marks show; the tints are rebuilt and the crossing measured again off the window's thread
    /// (the references may have moved: an edit calls this too).</summary>
    internal void Set(RefOcclusion? selected, RefOcclusion? partner)
    {
        Selected = selected; Partner = partner;
        Rebuild();
    }

    void Rebuild()
    {
        _builtSel = Selected; _builtPartner = Partner;
        _selTris = Triangles(Selected); _partnerTris = Triangles(Partner);
        _overlapTris = new(); _crossing = new(); Overlap = null;
        _measuring?.Cancel();
        if (Selected is not { } a || Partner is not { } b) return;
        int gen = ++_generation;
        var cts = _measuring = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (sa, cutA) = Soup(a); var (sb, cutB) = Soup(b);
        Task.Run(() =>
        {
            var (contact, pieces) = SurfaceVisibility.MeasureWithPieces(sa, null, sb, null, VolumeGeometry.Render, new SurfaceOptions(MaxTrianglePairs: 20_000_000), cts.Token);
            var tris = new List<float>();
            // the crossing triangles of solid shapes are tinted; a cut-out card's crossing shows as the yellow line only
            foreach (var t in pieces.SelectMany(p => p.TrianglesA).Distinct().Where(t => !cutA[t])) AddTriangle(tris, sa.Vertices[sa.Indices[3 * t]], sa.Vertices[sa.Indices[3 * t + 1]], sa.Vertices[sa.Indices[3 * t + 2]]);
            foreach (var t in pieces.SelectMany(p => p.TrianglesB).Distinct().Where(t => !cutB[t])) AddTriangle(tris, sb.Vertices[sb.Indices[3 * t]], sb.Vertices[sb.Indices[3 * t + 1]], sb.Vertices[sb.Indices[3 * t + 2]]);
            var segs = pieces.Select(p => (new Vector3((float)p.X0, (float)p.Y0, (float)p.Z0), new Vector3((float)p.X1, (float)p.Y1, (float)p.Z1))).ToList();
            string said = contact.Status != "measured" ? $"where they cross was not measured ({contact.Reason ?? contact.Status})"
                        : segs.Count == 0 ? "their surfaces do not cross" : $"their surfaces cross along {segs.Count:N0} segment(s)";
            return (tris, segs, said);
        }, cts.Token).ContinueWith(t =>
        {
            if (gen != _generation) return;
            if (t.IsCompletedSuccessfully) { (_overlapTris, _crossing, Overlap) = t.Result; }
            else Overlap = t.IsCanceled ? "where they cross was not measured in 20 s" : $"where they cross could not be measured: {t.Exception?.GetBaseException().Message}";
            Changed?.Invoke();
        }, TaskScheduler.Default);
    }

    public void Draw(GlRenderer renderer, in OverlayContext context)
    {
        if (!Enabled || (Selected is null && Partner is null)) return;
        if (!ReferenceEquals(Selected, _builtSel) || !ReferenceEquals(Partner, _builtPartner)) Rebuild();
        // the solid tints, on the objects' own visible surfaces
        if (renderer.BeginOverlaySurfacePass(context, out var st))
        {
            var none = Array.Empty<(int, int, GlVec3)>();
            if (_partnerTris.Count >= 18) renderer.DrawOverlayTranslucentTint(_partnerTris, PartnerColour, TintAlpha, none);
            if (_selTris.Count >= 18) renderer.DrawOverlayTranslucentTint(_selTris, SelectedColour, TintAlpha, none);
            var overlap = _overlapTris;
            if (overlap.Count >= 18) renderer.DrawOverlayTranslucentTint(overlap, OverlapColour, OverlapAlpha, none);
            renderer.EndOverlaySurfacePass(st);
        }
        // the outlines, dimmed, and where they cross, bright: both through anything in front
        _lines.Clear();
        void Add(RefOcclusion? ro, GlVec3 col)
        {
            if (ro is null) return;
            // a cut-out card has no tint, so its outline is its mark and is drawn bright; a tinted solid's outline is dimmed
            foreach (var (a, b, cut) in Outline(ro))
                _lines.Add(new OverlayLineSegment { A = new GlVec3(a.X, a.Y, a.Z), B = new GlVec3(b.X, b.Y, b.Z), Color = cut ? col : col * OutlineDim });
        }
        Add(Partner, PartnerColour);
        Add(Selected, SelectedColour);
        if (renderer.BeginOverlayLinePass(context, out var ls))
        {
            if (_lines.Count > 0) renderer.DrawOverlayLines(_lines, 1.2f, context);
            var crossing = _crossing;
            if (crossing.Count > 0)
            {
                _lines.Clear();
                foreach (var (a, b) in crossing) _lines.Add(new OverlayLineSegment { A = new GlVec3(a.X, a.Y, a.Z), B = new GlVec3(b.X, b.Y, b.Z), Color = OverlapColour });
                renderer.DrawOverlayLines(_lines, 3.5f, context);
            }
            renderer.EndOverlayLinePass(ls);
        }
    }
}
