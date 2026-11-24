using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Xunit;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;

namespace SkyWright.Engine.Tests;

/// <summary>
/// The NAVM edge-link Type (xEdit: 0 Portal, 1 Ledge Up, 2 Ledge Down, 3 Enable/Disable Portal; Mutagen calls it
/// EdgeLink.Unknown): read from the record and honoured by the walkable graph.
/// </summary>
[Trait("tier", "unit")]
public sealed class NavGraphLinkTypeTests
{
    static readonly ModKey Mod = new("HcNavType", ModType.Master);
    static FormKey K(uint id) => new(Mod, id);

    [Fact]
    public void Extraction_carries_the_edge_link_type()
    {
        var d = new NavigationMeshData { NavmeshVersion = 12, Parent = new CellNavmeshParent { Parent = new FormLink<ICellGetter>(K(0x800)) } };
        d.Vertices.Add(new P3Float(0, 0, 0)); d.Vertices.Add(new P3Float(100, 0, 0)); d.Vertices.Add(new P3Float(0, 100, 0));
        d.Triangles.Add(new NavmeshTriangle { Vertices = new P3Int16(0, 1, 2), EdgeLink_0_1 = 0, EdgeLink_1_2 = 1, EdgeLink_2_0 = -1,
                                              Flags = (NavmeshTriangle.Flag)0x03 });
        d.EdgeLinks.Add(new EdgeLink { Mesh = new FormLink<INavigationMeshGetter>(K(0x901)), TriangleIndex = 4, Unknown = 2 });
        d.EdgeLinks.Add(new EdgeLink { Mesh = new FormLink<INavigationMeshGetter>(K(0x902)), TriangleIndex = 5, Unknown = 0 });
        var cell = new SkCell(K(0x800), SkyrimRelease.SkyrimSE) { Flags = SkCell.Flag.IsInteriorCell };
        cell.NavigationMeshes.Add(new NavigationMesh(K(0x900), SkyrimRelease.SkyrimSE) { Data = d });

        var nav = Assert.Single(PlacementExtract.FromCell(cell, FormKey.Null).Navmeshes);
        Assert.Equal(new[] { new NavEdgeLink(K(0x901), 4, 2), new NavEdgeLink(K(0x902), 5, 0) }, nav.EdgeLinks);
    }

    // Two quads, L at z 0 and U at z `rise`, sharing L's top edge in plan, joined ONLY by one cross-mesh link each way:
    // L.tri1 -> U.tri0 with type `up`, U.tri0 -> L.tri1 with type `down`. Each quad is two triangles linked to each other.
    static Dictionary<FormKey, NavmeshGeometry> Pair(FormKey l, FormKey u, int up, int down, float x0 = 0f, float rise = 150f)
    {
        static NavmeshGeometry Quad(FormKey key, float x0, float y0, float z, short crossEdge01Tri0, short crossEdge12Tri1, NavEdgeLink link)
        {
            var v = new[] { new Vec3(x0, y0, z), new Vec3(x0 + 100, y0, z), new Vec3(x0 + 100, y0 + 100, z), new Vec3(x0, y0 + 100, z) };
            // tri0 (0,1,2): edge 2-0 joins tri1; tri1 (0,2,3): edge 0-1 joins tri0. Edge 0-1 of tri0 is the quad's bottom,
            // edge 1-2 of tri1 its top; the cross link sits on whichever the caller names (index 0 of the link table).
            var t0 = new NavTri(0, 1, 2, crossEdge01Tri0 >= 0 ? 0x01 : 0, crossEdge01Tri0, -1, 1);
            var t1 = new NavTri(0, 2, 3, crossEdge12Tri1 >= 0 ? 0x02 : 0, 0, crossEdge12Tri1, -1);
            return new NavmeshGeometry(key, v, new[] { t0, t1 }, v[0], v[2], new[] { link }, Array.Empty<NavDoorTriangle>());
        }
        return new()
        {
            [l] = Quad(l, x0, 0, 0, -1, 0, new NavEdgeLink(u, 0, up)),
            [u] = Quad(u, x0, 100, rise, 0, -1, new NavEdgeLink(l, 1, down)),
        };
    }

    static readonly NavGraph.LinkPolicy PortalsOnly = new(LedgeUp: false, LedgeDown: false, EnableDisablePortal: false);

    [Fact]
    public void A_ledge_pair_held_both_ways_is_not_walked_as_level_ground()
    {
        FormKey l = K(0xA01), u = K(0xA02);
        var (nodes, report) = NavGraph.Build(Pair(l, u, up: 1, down: 2), PortalsOnly);
        Assert.Null(NavGraph.FindChannel(nodes, new(l, 0), new(u, 1)));
        Assert.Null(NavGraph.FindChannel(nodes, new(u, 1), new(l, 0)));
        Assert.Equal(2, report.Components);
    }

    // The default follows the in-game test (2026-11-08): a walker drops down a ledge but never climbs one.
    [Fact]
    public void The_default_drops_down_a_ledge_and_never_climbs_it()
    {
        FormKey l = K(0xA05), u = K(0xA06);
        var (nodes, _) = NavGraph.Build(Pair(l, u, up: 1, down: 2));
        Assert.Null(NavGraph.FindChannel(nodes, new(l, 0), new(u, 1)));
        Assert.Equal(new NavGraph.TriRef[] { new(u, 1), new(u, 0), new(l, 1), new(l, 0) }, NavGraph.FindChannel(nodes, new(u, 1), new(l, 0)));
    }

    // A one-way step joins two pieces whichever triangle the count starts from (a one-way search split them when it
    // started on the lower side).
    [Fact]
    public void A_one_way_ledge_joins_two_pieces_in_either_order()
    {
        FormKey l = K(0xA07), u = K(0xA08);
        var lowFirst = Pair(l, u, up: 1, down: 2);
        var highFirst = new Dictionary<FormKey, NavmeshGeometry> { [u] = lowFirst[u], [l] = lowFirst[l] };
        Assert.Equal(1, NavGraph.Build(lowFirst).Report.Components);
        Assert.Equal(1, NavGraph.Build(highFirst).Report.Components);
    }

    [Fact]
    public void A_portal_pair_is_walked()
    {
        FormKey l = K(0xB01), u = K(0xB02);
        var (nodes, report) = NavGraph.Build(Pair(l, u, up: 0, down: 0));
        Assert.Equal(new NavGraph.TriRef[] { new(l, 0), new(l, 1), new(u, 0), new(u, 1) }, NavGraph.FindChannel(nodes, new(l, 0), new(u, 1)));
        Assert.Equal(1, report.Components);
        Assert.Equal(2, report.CrossLinks);
    }

    [Fact]
    public void The_default_records_the_ledge_pair_and_holds_the_way_up()
    {
        FormKey l = K(0xA11), u = K(0xA12);
        var (_, r) = NavGraph.Build(Pair(l, u, up: 1, down: 2));
        Assert.Equal((1, 1, 0, 0, 1, 1), (r.LedgeUpLinks, r.LedgeDownLinks, r.EnableDisableLinks, r.UnknownTypeLinks, r.HeldLinks, r.CrossLinks));
        Assert.Equal(NavGraph.LinkPolicy.Default, r.Policy);
    }

    [Fact]
    public void AllLinks_walks_the_ledge_pair_like_the_portal_pair()
    {
        FormKey l = K(0xA21), u = K(0xA22);
        var (nodes, r) = NavGraph.Build(Pair(l, u, up: 1, down: 2), NavGraph.LinkPolicy.AllLinks);
        Assert.Equal(new NavGraph.TriRef[] { new(l, 0), new(l, 1), new(u, 0), new(u, 1) }, NavGraph.FindChannel(nodes, new(l, 0), new(u, 1)));
        Assert.Equal((1, 2, 0), (r.Components, r.CrossLinks, r.HeldLinks));
    }

    [Fact]
    public void A_one_way_policy_gives_a_one_way_step()
    {
        FormKey l = K(0xA31), u = K(0xA32);
        var (nodes, _) = NavGraph.Build(Pair(l, u, up: 1, down: 2), new NavGraph.LinkPolicy(LedgeUp: false, LedgeDown: true, EnableDisablePortal: false));
        Assert.NotNull(NavGraph.FindChannel(nodes, new(u, 1), new(l, 0)));
        Assert.Null(NavGraph.FindChannel(nodes, new(l, 0), new(u, 1)));
    }

    [Fact]
    public void Enable_disable_and_unknown_types_are_held_by_default_and_counted()
    {
        FormKey l = K(0xA41), u = K(0xA42);
        var (nodes, r) = NavGraph.Build(Pair(l, u, up: 3, down: 7));
        Assert.Null(NavGraph.FindChannel(nodes, new(l, 0), new(u, 1)));
        Assert.Equal((1, 1, 2), (r.EnableDisableLinks, r.UnknownTypeLinks, r.HeldLinks));
        // AllLinks walks Type 3 but never an unknown type.
        var (all, ra) = NavGraph.Build(Pair(l, u, up: 3, down: 7), NavGraph.LinkPolicy.AllLinks);
        Assert.NotNull(NavGraph.FindChannel(all, new(l, 0), new(u, 1)));
        Assert.Null(NavGraph.FindChannel(all, new(u, 1), new(l, 0)));
        Assert.Equal(1, ra.HeldLinks);
    }
}
