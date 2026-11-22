using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

// BORDER LINKS BY THEIR GEOMETRY (task 2026-11-16-navmesh-repair). DanglingLinks judges a link's table entry: does the navmesh
// it names exist, and is the triangle index in range. This judges where the link lies, over the winning navmeshes'
// geometry (NavBorderMatch.cs):
//
//   EDGE-NOMATCH  a source triangle edge links a triangle that exists, but no edge of that triangle lies along it within the
//                 tolerance: a re-cut renumbered the far side and left the index in range, naming the wrong triangle.
//   EDGE-ONEWAY   the far triangle's edge lies along the source edge, but it does not link back to the source triangle (it
//                 links nothing, or another triangle). Also when the far edge lies along it only within the unproven band:
//                 a link with no way back is one way whatever the gap (2026-11-22 lab), never hidden as "unproven".
//
// Every broken link (these two, and DanglingLinks' EDGE-IDX and EDGE-NAV) also gets its CANDIDATES: the triangles of other
// navmeshes with an open edge along the source edge. One is the match a relink can take; two or more are ambiguous and none
// is taken; none means nothing on the far side meets the edge. A link whose flag bit is set but whose field is past the
// edge-link table is counted, never judged. A LEDGE link (edge-link Type 1 or 2, xEdit's Ledge Up / Ledge Down) is counted and
// not judged either: on the base game's own records nearly every ledge link has no far edge along the source edge even in plan
// (the vanilla floor, 2026-11-16), so edge geometry is not how a ledge link is made and cannot say one is broken.
sealed class NavBorderLinks
{
    /// <summary>A judged row. <paramref name="Tier"/> is "finding", or "unproven" for an EDGE-NOMATCH whose far edge meets the
    /// source edge within the wider tolerance the lab has not yet walked (Gate B review finding 2): shown apart, never a
    /// finding, never offered a repair.</summary>
    public sealed record Row(string Kind, FormKey Src, int SrcTri, int Edge, FormKey Target, int TargetTri, EdgeGap Gap, string Why, string Tier = "finding");
    /// <summary>A broken link's candidate search: unique (Nav, Tri), ambiguous (all of them), or none.</summary>
    public sealed record CandidateResult(IReadOnlyList<(FormKey Nav, int Tri, EdgeGap Gap)> All)
    {
        public bool Unique => All.Count == 1;
        public bool Ambiguous => All.Count > 1;
        public string Sentence(FormKey named) => All.Count switch
        {
            0 => "no matching edge across the border",
            1 => $"a matching edge: {All[0].Nav} triangle {All[0].Tri}" + (All[0].Nav != named ? ", in another navmesh" : ""),
            _ => "ambiguous: " + string.Join(" and ", All.Select(c => $"{c.Nav} triangle {c.Tri}")) + " both match within the tolerance; neither is taken",
        };
    }
    /// <summary>One judged link, for the vanilla floor's gap census.</summary>
    public readonly record struct Census(FormKey Src, int Tri, int Edge, FormKey Target, int TargetTri, EdgeGap Gap, bool Matched, bool Reciprocal, int Type);

    public readonly List<Row> Rows = new();
    public readonly List<Census> Judged = new();
    /// <summary>(source, target, target triangle) -> the candidates for every broken link the table names, by the first
    /// triangle edge that uses it.</summary>
    public readonly Dictionary<(FormKey Src, FormKey Target, int Tri), CandidateResult> Candidates = new();
    public long LinkFieldPastTable, LedgeLinks, OpenEdges;

    public static NavBorderLinks Find(IReadOnlyDictionary<FormKey, WinningNav> nav, EdgeTolerance tol, EdgeTolerance? unproven = null)
    {
        var wide = unproven ?? tol;
        var r = new NavBorderLinks();
        var idx = new OpenEdgeIndex(nav);
        r.OpenEdges = idx.Count;
        CandidateResult Cands(WinningNav w, FormKey self, Vec3 a, Vec3 b)
            => new(idx.Candidates(w.Space, a, b, tol, self).Select(c => (c.Nav, c.Tri, c.Gap)).ToList());
        foreach (var (s, w) in nav)
        {
            var g = w.Geom;
            var links = g.EdgeLinks ?? Array.Empty<NavEdgeLink>();
            for (int t = 0; t < g.Triangles.Count; t++)
                for (int e = 0; e < 3; e++)
                {
                    var tr = g.Triangles[t];
                    if (!NavBorderMatch.EdgeIsLink(tr, e)) continue;
                    int k = NavBorderMatch.EdgeLink(tr, e);
                    if (k < 0 || k >= links.Count) { r.LinkFieldPastTable++; continue; }
                    var (vi, vj) = NavBorderMatch.EdgeVerts(tr, e);
                    if (vi < 0 || vj < 0 || vi >= g.Vertices.Count || vj >= g.Vertices.Count) continue;
                    Vec3 a = g.Vertices[vi], b = g.Vertices[vj];
                    var (m, j) = (links[k].Mesh, (int)links[k].TriangleIndex);
                    if (links[k].Type is 1 or 2) { r.LedgeLinks++; continue; }   // a ledge link: counted, not judged (header)
                    if (!nav.TryGetValue(m, out var far) || j < 0 || j >= far.Geom.Triangles.Count)
                    {
                        // EDGE-NAV / EDGE-IDX (DanglingLinks): only the candidates are this class's to say
                        r.Candidates.TryAdd((s, m, j), Cands(w, s, a, b));
                        continue;
                    }
                    var (fe, gap) = NavBorderMatch.BestEdge(a, b, far.Geom, j);
                    bool matched = fe >= 0 && gap.Within(tol);
                    bool recip = false;
                    if (fe >= 0)   // whether the far edge links back, for every edge that lies along it (the census reads it too)
                    {
                        var ft = far.Geom.Triangles[j];
                        var flinks = far.Geom.EdgeLinks ?? Array.Empty<NavEdgeLink>();
                        int fk = NavBorderMatch.EdgeLink(ft, fe);
                        recip = NavBorderMatch.EdgeIsLink(ft, fe) && fk >= 0 && fk < flinks.Count && flinks[fk].Mesh == s && flinks[fk].TriangleIndex == t;
                    }
                    r.Judged.Add(new Census(s, t, e, m, j, gap, matched, recip, links[k].Type));
                    if (!matched)
                    {
                        var c = Cands(w, s, a, b);
                        r.Candidates.TryAdd((s, m, j), c);
                        bool band = fe >= 0 && gap.Within(wide);
                        if (band)
                        {
                            // Inside the unproven band the far edge lies along this one: the link is "unproven" only while the
                            // far triangle links back. With no link back the border is one way whatever the gap, which the game
                            // honours (SkyWright overnight lab 2026-11-22, Ruunvald: 3 of 3 forward, 0 of 3 back): EDGE-ONEWAY.
                            var bt = far.Geom.Triangles[j];
                            var bl = far.Geom.EdgeLinks ?? Array.Empty<NavEdgeLink>();
                            int bk = NavBorderMatch.EdgeLink(bt, fe);
                            bool linksBack = NavBorderMatch.EdgeIsLink(bt, fe) && bk >= 0 && bk < bl.Count && bl[bk].Mesh == s && bl[bk].TriangleIndex == t;
                            // an open edge within the tolerance elsewhere is the link's true neighbour (the far side was re-cut):
                            // the link is stale, an EDGE-NOMATCH finding the relink re-points, not one way (review 3, item 8).
                            // While the far triangle links back the band row stays unproven: re-pointing would strand that link.
                            if (!linksBack && c.All.Count > 0) band = false;
                            else if (!linksBack)
                            {
                                string back = NavBorderMatch.EdgeIsLink(bt, fe) && bk >= 0 && bk < bl.Count
                                    ? $"links {bl[bk].Mesh} triangle {bl[bk].TriangleIndex} instead" : "links nothing back";
                                r.Rows.Add(new Row("EDGE-ONEWAY", s, t, e, m, j, gap, FormattableString.Invariant(
                                    $"triangle {t} links {m} triangle {j}, whose edge lies {gap.Plan:0.#} u off in plan and {gap.Height:0.#} u in height (the unproven band), and that edge {back}")));
                                continue;
                            }
                        }
                        r.Rows.Add(new Row("EDGE-NOMATCH", s, t, e, m, j, gap,
                            band ? FormattableString.Invariant($"triangle {t} links {m} triangle {j}, whose edge lies {gap.Plan:0.#} u off in plan and {gap.Height:0.#} u in height: within {wide} but not {tol}, a band not yet walked in game, so it is unproven, not a finding")
                                 : $"triangle {t} links {m} triangle {j}, whose edges do not lie along it within the tolerance; {c.Sentence(m)}",
                            band ? "unproven" : "finding"));
                    }
                    else if (!recip)
                    {
                        var ft = far.Geom.Triangles[j];
                        var flinks = far.Geom.EdgeLinks ?? Array.Empty<NavEdgeLink>();
                        int fk = NavBorderMatch.EdgeLink(ft, fe);
                        string back = NavBorderMatch.EdgeIsLink(ft, fe) && fk >= 0 && fk < flinks.Count
                            ? $"links {flinks[fk].Mesh} triangle {flinks[fk].TriangleIndex} instead" : "links nothing back";
                        r.Rows.Add(new Row("EDGE-ONEWAY", s, t, e, m, j, gap, $"triangle {t} links {m} triangle {j}, whose matching edge {back}"));
                    }
                }
        }
        return r;
    }

    /// <summary>The physical half under an arm: Holds while the source still links that triangle and the class still holds.</summary>
    public PhysicalState Predicate(string kind, FormKey src, FormKey target, int? tri)
        => Rows.Any(x => x.Kind == kind && x.Src == src && x.Target == target && (tri is null || x.TargetTri == tri)) ? PhysicalState.Holds : PhysicalState.Gone;
}
