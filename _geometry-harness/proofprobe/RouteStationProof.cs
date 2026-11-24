using System.Numerics;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

// The station rule's ROUTE mode (g5-route-sampling/1, with AMENDMENT-01's readings): known answers
// stated as synthetic worlds in the fixture's own frame. Written RED against an API stub first, as
// the observing mode's M1-M9 were (c95b9966): each case fails on a WRONG ANSWER the stub gives - the
// area as every triangle, walkable as every triangle, X_W from the winner's position only, one
// accepted station per triangle centroid, coverage by triangle count, no per-candidate refusal.
//
// Every fixture world's navmesh graph is stated with its link fields, because the rule's graph comes
// from the record and nothing else. The environment helper reads the navmesh through NavCover.At -
// the function the route mode's own environment reads it through - over EVERY winning navmesh of
// the world (AMENDMENT-01 reading 8), puts the floor at the navmesh height and the water at a surface
// the rule knows is absent, unless a case states otherwise.
static class RouteStationProof
{
    /// <summary>Cases run below, excluding this count's own check.</summary>
    public const int DeclaredCases = 52;

    static readonly ModKey Master = ModKey.FromNameAndExtension("Master.esm");
    static readonly ModKey City = ModKey.FromNameAndExtension("City.esp");
    static readonly ModKey Fixture = ModKey.FromNameAndExtension("Fixture.esm");
    static FormKey FK(uint id) => new(Master, id);
    static FormKey CK(uint id) => new(City, id);
    const string WS = "WS-ROUTE";
    const string CELL = "CELL-ROUTE";

    // ---- navmesh builders ------------------------------------------------------------------------

    /// <summary>A mesh under construction: vertices, triangles with their raw link fields, the
    /// EdgeLinks table and the DoorTriangles table.</summary>
    sealed class MeshB
    {
        public readonly List<Vec3> V = new();
        public readonly List<NavTri> T = new();
        public readonly List<NavEdgeLink> L = new();
        public readonly List<NavDoorTriangle> D = new();
        public NavmeshGeometry Build(FormKey key)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
            foreach (var v in V) { x0 = MathF.Min(x0, v.X); y0 = MathF.Min(y0, v.Y); z0 = MathF.Min(z0, v.Z); x1 = MathF.Max(x1, v.X); y1 = MathF.Max(y1, v.Y); z1 = MathF.Max(z1, v.Z); }
            return new NavmeshGeometry(key, V.ToArray(), T.ToArray(), new Vec3(x0, y0, z0), new Vec3(x1, y1, z1), L.ToArray(), D.ToArray());
        }

        /// <summary>A standalone triangle with no links; returns its index.</summary>
        public int Tri(Vector2 a, Vector2 b, Vector2 c, int flags = 0, float z = 0f)
        {
            int i = V.Count;
            V.Add(new Vec3(a.X, a.Y, z)); V.Add(new Vec3(b.X, b.Y, z)); V.Add(new Vec3(c.X, c.Y, z));
            T.Add(new NavTri((short)i, (short)(i + 1), (short)(i + 2), flags));
            return T.Count - 1;
        }

        /// <summary>A rectangle of nx x ny cells, each split into A = (p00, p10, p11) and B = (p00, p11,
        /// p01), with every intra-grid neighbour linked across the edge it shares (edge i joins corner
        /// i to corner i+1). Returns the index of the grid's first triangle: cell (i, j)'s A is
        /// first + 2(j nx + i), its B the next.</summary>
        public int Grid(float x0, float y0, float cw, float ch, int nx, int ny, float z = 0f)
        {
            int vb = V.Count, tb = T.Count;
            for (int j = 0; j <= ny; j++)
                for (int i = 0; i <= nx; i++) V.Add(new Vec3(x0 + i * cw, y0 + j * ch, z));
            short Vx(int i, int j) => (short)(vb + j * (nx + 1) + i);
            short A(int i, int j) => (short)(tb + 2 * (j * nx + i));
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    short p00 = Vx(i, j), p10 = Vx(i + 1, j), p11 = Vx(i + 1, j + 1), p01 = Vx(i, j + 1);
                    T.Add(new NavTri(p00, p10, p11, 0,
                        j > 0 ? (short)(A(i, j - 1) + 1) : (short)-1,
                        i < nx - 1 ? (short)(A(i + 1, j) + 1) : (short)-1,
                        (short)(A(i, j) + 1)));
                    T.Add(new NavTri(p00, p11, p01, 0,
                        A(i, j),
                        j < ny - 1 ? A(i, j + 1) : (short)-1,
                        i > 0 ? A(i - 1, j) : (short)-1));
                }
            return tb;
        }

        public void Door(int tri, uint door = 0xD00) => D.Add(new NavDoorTriangle(FK(door + (uint)D.Count), (short)tri));

        /// <summary>Set one edge's raw link field (0 = edge 0-1, 1 = 1-2, 2 = 2-0) and its flag bit.</summary>
        public void Link(int tri, int edge, short value, bool crossMesh)
        {
            var t = T[tri];
            int bit = edge == 0 ? 0x01 : edge == 1 ? 0x02 : 0x04;
            int flags = crossMesh ? t.Flags | bit : t.Flags & ~bit;
            T[tri] = edge switch
            {
                0 => t with { EdgeLink01 = value, Flags = flags },
                1 => t with { EdgeLink12 = value, Flags = flags },
                _ => t with { EdgeLink20 = value, Flags = flags },
            };
        }

        /// <summary>Link two triangles of this mesh across the given edges, both directions.</summary>
        public void Join(int t1, int e1, int t2, int e2) { Link(t1, e1, (short)t2, false); Link(t2, e2, (short)t1, false); }

        /// <summary>Add a cross-mesh link from (tri, edge) to (mesh, targetTri) through the table.</summary>
        public void Cross(int tri, int edge, FormKey mesh, short targetTri)
        {
            L.Add(new NavEdgeLink(mesh, targetTri));
            Link(tri, edge, (short)(L.Count - 1), true);
        }
    }

    static Vector2 P(float x, float y) => new(x, y);

    // PROOF_ROUTE_DUMP=1: every station of a sampled world, for a before/after list of verdicts (test output only).
    static void Dump(string tag, RouteResult r)
    {
        if (Environment.GetEnvironmentVariable("PROOF_ROUTE_DUMP") != "1") return;
        foreach (var p in r.Paths)
            Console.WriteLine($"   [dump {tag}] path {p.Id} kept {p.Kept} length {p.Length:0.###}: " + string.Join(" ", p.Corners.Select(c => $"{c.X:0.###},{c.Y:0.###}")));
        foreach (var st in r.Stations)
            Console.WriteLine($"   [dump {tag}] {st.Id} {st.Family} {st.X:0.###},{st.Y:0.###} accepted {st.Accepted} drop {st.Drop} "
                            + $"reason {st.Verdict?.Reason.ToString() ?? "-"} on {st.Verdict?.Covering?.Index.ToString() ?? "-"} z {st.Verdict?.Env.NavZ:0.###}");
    }

    static (int, int) SquareOf(NavmeshGeometry m)
    {
        double sx = 0, sy = 0;
        foreach (var v in m.Vertices) { sx += v.X; sy += v.Y; }
        return ((int)Math.Floor(sx / m.Vertices.Count / 4096.0), (int)Math.Floor(sy / m.Vertices.Count / 4096.0));
    }

    static RoutePlaced Defined(uint id, float x, float y, bool model = true)
        => new(CK(id), FK(0xB00), model, new[] { new RoutePlacedVersion("City.esp", new Vector3(x, y, 0f), 0) });

    static RouteWorld World(IEnumerable<NavmeshGeometry> masterMeshes, IEnumerable<RoutePlaced> placed,
                            IEnumerable<NavmeshGeometry>? cityMeshes = null,
                            IReadOnlyList<PrimitiveGeometry>? prims = null,
                            IReadOnlyList<NavmeshGeometry>? child = null,
                            IReadOnlyDictionary<FormKey, (int, int)>? filedUnder = null)
    {
        var navs = new List<RouteNavmesh>();
        foreach (var m in masterMeshes)
            navs.Add(new RouteNavmesh(m, new[] { "Master.esm" },
                filedUnder is not null && filedUnder.TryGetValue(m.FormKey, out var sq) ? sq : SquareOf(m)));
        var cityKeys = new List<FormKey>();
        foreach (var m in cityMeshes ?? Array.Empty<NavmeshGeometry>())
        {
            navs.Add(new RouteNavmesh(m, new[] { "City.esp" }, SquareOf(m)));
            cityKeys.Add(m.FormKey);
        }
        return new RouteWorld
        {
            Worldspace = FK(0x3C), WorldspaceEditorId = "Fixture",
            ArmPlugins = new[] { "Master.esm", "City.esp" }, CityPlugin = "City.esp",
            Navmeshes = navs, CityPlaced = placed.ToList(), CityNavmeshes = cityKeys,
            Primitives = prims ?? Array.Empty<PrimitiveGeometry>(), ChildFootprint = child,
        };
    }

    static WaterResolution Dry()
        => StationRule.Classify(new DetectorModel.WaterTuple(false, false, null, null, null, null, null, null),
                                null, exterior: true, fromWorldspace: WS);

    static WaterResolution Surface(float z)
        => StationRule.Classify(new DetectorModel.WaterTuple(true, false, null, z, null, null, null, null),
                                null, exterior: true, fromWorldspace: WS);

    /// <summary>The environment the route mode would read, over every winning navmesh of the world.</summary>
    static Func<float, float, float?, RouteEnv> Env(RouteArea a, WaterResolution? water = null)
        => (x, y, storeyZ) =>
        {
            var r = NavCover.At(a.Winning.Values, x, y, storeyZ);
            var floor = r.Drawn
                ? new FloorReading(r.Z, r.Z, FloorSource.Mesh, NoFloorReason.Found, false, 1)
                : new FloorReading(r.Z, 0f, FloorSource.None, NoFloorReason.NoSurface, false, 0);
            return new RouteEnv(new StationEnvironment(r.Answered, r.Covered, r.Drawn, r.Z, floor, CELL, WS,
                                                       water ?? Dry()), r.Tri);
        };

    static RouteTriangle? TriAt(RouteArea a, float x, float y)
    {
        foreach (var t in a.Triangles.Values)
        {
            var c = (t.A + t.B + t.C) / 3f;
            if (MathF.Abs(c.X - x) < 1f && MathF.Abs(c.Y - y) < 1f) return t;
        }
        return null;
    }

    static float AngDiff(float a, float b)
    {
        float d = MathF.Abs(a - b) % 360f;
        return d > 180f ? 360f - d : d;
    }

    /// <summary>A 1,024 u square pad of 128 u cells with a door on its first triangle.</summary>
    static MeshB Pad(float x0 = 0f, float y0 = 0f)
    {
        var b = new MeshB();
        b.Grid(x0, y0, 128f, 128f, 8, 8);
        b.Door(0);
        return b;
    }

    public static void Run(Action<string, bool, string> check)
    {
        int ran = 0;
        void C(string name, bool ok, string detail)
        {
            ran++;
            check("stationrule: " + name, ok, detail);
        }

        C("RT0 the sealed parameters: R_area 2048, S 512, D 512, dedup 128, M 6, offset 256",
          RouteStations.RArea == 2048f && RouteStations.Spacing == 512f && RouteStations.CoverageDistance == 512f
          && RouteStations.CandidateDedup == 128f && RouteStations.RetryAlternates == 6 && RouteStations.StartOffset == 256f,
          "");

        // ---- RT1 area radius ---------------------------------------------------------------------
        {
            var m = new MeshB();
            int near = m.Tri(P(950, 0), P(1050, 0), P(1000, 100));
            int far = m.Tri(P(2950, 0), P(3050, 0), P(3000, 100));
            m.Door(near); m.Door(far);
            var a = RouteStations.Classify(World(new[] { m.Build(FK(0x801)) }, new[] { Defined(0x10, 0, 0) }));
            var tn = TriAt(a, 1000f, 33.333f); var tf = TriAt(a, 3000f, 33.333f);
            C("RT1 a walkable triangle 1,000 u from the changed ref is in A_W, whole",
              tn is { InArea: true } && Math.Abs(tn.AreaClipped - tn.AreaFull) < 1e-6,
              tn is null ? "triangle not classified" : $"inArea {tn.InArea}, clipped {tn.AreaClipped:0.###} of {tn.AreaFull:0.###}");
            C("RT1 one 3,000 u away, loaded and walkable in the same grid square, is not in A_W",
              tf is { Walkable: true, InArea: false } && tf.AreaClipped == 0.0,
              tf is null ? "triangle not classified (not loaded)" : $"walkable {tf.Walkable}, clipped {tf.AreaClipped:0.###}");
        }

        // ---- RT2 moved override, and a deleted override ------------------------------------------
        {
            var m = new MeshB();
            int old = m.Tri(P(-1550, 0), P(-1450, 0), P(-1500, 100));
            int nu = m.Tri(P(3450, 0), P(3550, 0), P(3500, 100));
            int far = m.Tri(P(-3950, 0), P(-3850, 0), P(-3900, 100));
            m.Door(old); m.Door(nu); m.Door(far);
            var moved = new RoutePlaced(FK(0x20), FK(0xB00), true, new[]
            {
                new RoutePlacedVersion("Master.esm", new Vector3(0, 0, 0), 0),
                new RoutePlacedVersion("City.esp", new Vector3(2000, 0, 0), 0),
            });
            var a = RouteStations.Classify(World(new[] { m.Build(FK(0x802)) }, new[] { moved }));
            C("RT2 a moved override puts two points in X_W: the city's position and the master's",
              a.XPoints.Count == 2 && a.XPoints.Any(p => Vector2.Distance(p.P, P(0, 0)) < 1e-3f)
              && a.XPoints.Any(p => Vector2.Distance(p.P, P(2000, 0)) < 1e-3f),
              string.Join("; ", a.XPoints.Select(p => $"{p.P.X:0},{p.P.Y:0} {p.Why}")));
            var to = TriAt(a, -1500f, 33.333f); var tn = TriAt(a, 3500f, 33.333f); var tf = TriAt(a, -3900f, 33.333f);
            C("RT2 ground near the master's old position is in A_W", to is { InArea: true },
              to is null ? "not classified" : $"clipped {to.AreaClipped:0.###}");
            C("RT2 ground near the city's new position is in A_W", tn is { InArea: true },
              tn is null ? "not classified" : $"clipped {tn.AreaClipped:0.###}");
            C("RT2 ground beyond R_area of both positions is not", tf is null || !tf.InArea,
              tf is null ? "not loaded" : $"clipped {tf.AreaClipped:0.###}");

            var m2 = new MeshB();
            int t2 = m2.Tri(P(950, 0), P(1050, 0), P(1000, 100));
            m2.Door(t2);
            var deleted = new RoutePlaced(FK(0x21), FK(0xB00), true, new[]
            {
                new RoutePlacedVersion("Master.esm", new Vector3(0, 0, 0), 0),
                new RoutePlacedVersion("City.esp", null, RouteStations.FlagDeleted),
            });
            var a2 = RouteStations.Classify(World(new[] { m2.Build(FK(0x803)) }, new[] { deleted }));
            var td = TriAt(a2, 1000f, 33.333f);
            C("RT2 an override the city deletes (no position) adds the master's position, so its ground is in A_W",
              a2.XPoints.Count == 1 && td is { InArea: true },
              $"{a2.XPoints.Count} X_W point(s); triangle {(td is null ? "not classified" : $"clipped {td.AreaClipped:0.###}")}");
        }

        // ---- RT3 the city's own navmesh -----------------------------------------------------------
        {
            var own = new MeshB();
            int t = own.Tri(P(5000, 5000), P(5100, 5000), P(5050, 5100));
            own.Door(t);
            var mm = new MeshB();
            int u = mm.Tri(P(5100, 5500), P(5200, 5500), P(5150, 5600));
            mm.Door(u);
            var a = RouteStations.Classify(World(new[] { mm.Build(FK(0x804)) }, new[] { Defined(0x30, 0, 0) },
                                                 cityMeshes: new[] { own.Build(CK(0x900)) }));
            var to = TriAt(a, 5050f, 5033.333f); var tm = TriAt(a, 5150f, 5533.333f);
            C("RT3 a triangle of the city's own navmesh 7,000 u from any ref is in A_W, whole",
              to is { InArea: true, CityOwn: true } && Math.Abs(to.AreaClipped - to.AreaFull) < 1e-6,
              to is null ? "not classified" : $"cityOwn {to.CityOwn}, clipped {to.AreaClipped:0.###} of {to.AreaFull:0.###}");
            C("RT3 a master-owned triangle beside it, as far from any ref, is not",
              tm is { InArea: false }, tm is null ? "not loaded" : $"clipped {tm.AreaClipped:0.###}");
        }

        // ---- RT4 cross-mesh reachability across a cell border (the loading design's proof) --------
        {
            var ma = new MeshB();
            int ta = ma.Tri(P(1000, 1000), P(1100, 1000), P(1050, 1100));
            ma.Door(ta);
            var outside = FK(0x8FF);                           // a navmesh this world does not hold
            ma.Cross(ta, 0, outside, 5);
            var keyA = FK(0x805);
            var mb = new MeshB();
            int tb = mb.Tri(P(4200, 1000), P(4300, 1000), P(4250, 1100));   // square (1, 0)
            mb.Cross(tb, 0, keyA, (short)ta);                  // B -> A only, through B's EdgeLinks table
            var a = RouteStations.Classify(World(new[] { ma.Build(keyA), mb.Build(FK(0x806)) },
                                                 new[] { Defined(0x40, 2600, 1000) }));
            var rA = TriAt(a, 1050f, 1033.333f); var rB = TriAt(a, 4250f, 1033.333f);
            C("RT4 B, with no door and no exit of its own, is reachable through its EdgeLinks entry into A's door component",
              rB is { Walkable: true, Reachable: true } && rA is { } && rB.Component == rA.Component,
              rB is null ? "B not classified" : $"B walkable {rB.Walkable} comp {rB.Component}; A comp {rA?.Component}");
            C("RT4 the cross-cell link is counted as traversed, and only the link to the unloaded mesh as external",
              a.Report.CrossLinks == 1 && a.Report.ExternalLinks == 1,
              $"cross {a.Report.CrossLinks}, external {a.Report.ExternalLinks}");
            C("RT4 A's link to a navmesh outside the loaded set makes A an exit",
              rA is { Exit: true, Door: true }, rA is null ? "A not classified" : $"exit {rA.Exit}, door {rA.Door}");
            var v = RouteStations.Test(a, Env(a), 4250f, 1033.333f);
            C("RT4 a candidate on B is not refused Unreachable", v.Reason != StationRejection.Unreachable,
              $"{v.Reason}: {v.Detail}");
        }

        // ---- RT5 an island with no door and no exit ------------------------------------------------
        {
            var m = Pad();
            int b0 = m.V.Count;
            m.V.Add(new Vec3(2000, 0, 0)); m.V.Add(new Vec3(2100, 0, 0)); m.V.Add(new Vec3(2050, 100, 0));
            m.V.Add(new Vec3(2150, 100, 0)); m.V.Add(new Vec3(2100, 200, 0));
            int t0 = m.T.Count;
            m.T.Add(new NavTri((short)b0, (short)(b0 + 1), (short)(b0 + 2), 0));
            m.T.Add(new NavTri((short)(b0 + 1), (short)(b0 + 3), (short)(b0 + 2), 0));
            m.T.Add(new NavTri((short)(b0 + 2), (short)(b0 + 3), (short)(b0 + 4), 0));
            m.Join(t0, 1, t0 + 1, 2); m.Join(t0 + 1, 1, t0 + 2, 0);
            var a = RouteStations.Classify(World(new[] { m.Build(FK(0x807)) }, new[] { Defined(0x50, 512, 512) }));
            var isl = new[] { a.Triangles[new(FK(0x807), t0)], a.Triangles[new(FK(0x807), t0 + 1)], a.Triangles[new(FK(0x807), t0 + 2)] };
            C("RT5 the 3-triangle island is one component, and none of it is walkable",
              isl[0].Component == isl[1].Component && isl[1].Component == isl[2].Component
              && isl.All(t => !t.Walkable && !t.Reachable),
              string.Join(", ", isl.Select(t => $"comp {t.Component} walkable {t.Walkable}")));
            var c1 = (isl[1].A + isl[1].B + isl[1].C) / 3f;
            var v = RouteStations.Test(a, Env(a), c1.X, c1.Y);
            C("RT5 a candidate on the island is refused Unreachable", v.Reason == StationRejection.Unreachable,
              $"{v.Reason}: {v.Detail}");
        }

        // ---- RT6 deleted -------------------------------------------------------------------------
        {
            var m = Pad();
            int d = m.Tri(P(1450, 1450), P(1550, 1450), P(1500, 1550), flags: 0x08);
            var a = RouteStations.Classify(World(new[] { m.Build(FK(0x808)) }, new[] { Defined(0x60, 512, 512) }));
            var td = a.Triangles[new(FK(0x808), d)];
            C("RT6 a deleted triangle is not a node and not walkable", !td.Node && !td.Walkable,
              $"node {td.Node}, walkable {td.Walkable}");
            var v = RouteStations.Test(a, Env(a), 1500f, 1483.333f);
            C("RT6 a candidate covered only by a deleted triangle is refused AllCoveringTrianglesDeleted",
              v.Reason == StationRejection.AllCoveringTrianglesDeleted, $"{v.Reason}: {v.Detail}");
        }

        // ---- RT7 spacing by area -------------------------------------------------------------------
        {
            var m = new MeshB();
            int left = m.Grid(0, 0, 512, 256, 2, 1);           // 4 huge triangles
            int right = m.Grid(1024, 0, 10.24f, 256, 100, 1);  // 200 tiny triangles
            m.Join(left + 2, 1, right + 1, 2);                 // the seam at x = 1024
            m.Door(left);
            var a = RouteStations.Classify(World(new[] { m.Build(FK(0x809)) }, new[] { Defined(0x70, 1024, 128) }));
            var r = RouteStations.Sample(a, Env(a));
            Dump("RT7", r);
            var acc = r.Stations.Where(s => s.Accepted).ToList();
            int rightHalf = acc.Count(s => s.X > 1024f);
            float minGap = float.MaxValue;
            for (int i = 0; i < acc.Count; i++)
                for (int j = i + 1; j < acc.Count; j++)
                    minGap = MathF.Min(minGap, Vector2.Distance(P(acc[i].X, acc[i].Y), P(acc[j].X, acc[j].Y)));
            C("RT7 a 2,048 x 256 corridor of 4 huge and 200 tiny triangles gets at most 5 accepted stations",
              acc.Count >= 1 && acc.Count <= 5, $"{acc.Count} accepted");
            C("RT7 the tiny-triangle half gets at most 3, not one per triangle", rightHalf <= 3 && acc.Count > rightHalf,
              $"{rightHalf} of {acc.Count} in the right half");
            C("RT7 accepted stations stand at least S = 512 u apart", acc.Count < 2 || minGap >= 512f - 1e-3f,
              $"closest pair {minGap:0.###} u");
        }

        // ---- RT8 route gaze, and RT13 the yield on the same world ----------------------------------
        {
            // A coarse L whose graph is a chain, so A*'s channel is unique and the rule's solved path
            // can be worked out by hand: start door T2 -> T1 -> T4 -> T5 -> end door T6 (T3 a dead end).
            // The straight line between the doors crosses the wall at x < 1536, y > 512, so the taut path
            // bends once, at the inner corner (1536, 512). (Until 2026-11-15 a 128 u grid did NOT give that path:
            // A* over centroid distances picks a staircase channel, and the funnel was taut only within it.
            // NavGraph.TautPath now straightens such a channel; the chain keeps this answer worked out by hand.)
            var m = new MeshB();
            int T1 = m.Tri(P(0, 0), P(1536, 0), P(1536, 512));
            int T2 = m.Tri(P(0, 0), P(1536, 512), P(0, 512));
            int T3 = m.Tri(P(1536, 0), P(2048, 0), P(2048, 512));
            int T4 = m.Tri(P(1536, 0), P(2048, 512), P(1536, 512));
            int T5 = m.Tri(P(1536, 512), P(2048, 512), P(2048, 2560));
            int T6 = m.Tri(P(1536, 512), P(2048, 2560), P(1536, 2560));
            m.Join(T1, 2, T2, 0);       // diagonal (0,0)-(1536,512)
            m.Join(T1, 1, T4, 2);       // x = 1536, y 0..512
            m.Join(T3, 2, T4, 0);       // diagonal (1536,0)-(2048,512)
            m.Join(T4, 1, T5, 0);       // y = 512, x 1536..2048
            m.Join(T5, 2, T6, 0);       // diagonal (1536,512)-(2048,2560)
            m.Door(T2);                 // centroid (512, 341.33)
            m.Door(T6);                 // centroid (1706.67, 1877.33)
            var a = RouteStations.Classify(World(new[] { m.Build(FK(0x80A)) }, new[] { Defined(0x80, 1024, 1024) }));
            var r = RouteStations.Sample(a, Env(a));
            Dump("RT8-RT13", r);
            C("RT8 the L-shaped world has two door endpoints and keeps one solved path",
              r.DoorEndpoints == 2 && r.Paths.Count(p => p.Kept) == 1,
              $"{r.DoorEndpoints} door endpoint(s), {r.Paths.Count(p => p.Kept)} kept path(s) of {r.Paths.Count}");
            float leg2Bearing = StationRule.Bearing(1706.6667f - 1536f, 1877.3333f - 512f);
            float chord = StationRule.Bearing(1706.6667f - 512f, 1877.3333f - 341.3333f);
            var onLeg2 = r.Stations.FirstOrDefault(s => s.Family == RouteFamily.Route && s.Accepted && s.Y > 1200f);
            var fwd = onLeg2?.Gazes.FirstOrDefault(g => g.Kind == RouteGazeKind.Fwd);
            var back = onLeg2?.Gazes.FirstOrDefault(g => g.Kind == RouteGazeKind.Back);
            C("RT8 a route station on leg 2 looks along leg 2's funnel segment (within 1 deg), not along the endpoint chord",
              fwd is not null && MathF.Min(AngDiff(fwd.BearingDeg, leg2Bearing), AngDiff(fwd.BearingDeg, leg2Bearing + 180f)) <= 1f
              && AngDiff(leg2Bearing, chord) > 5f,
              (fwd is null ? "no accepted route station on leg 2" : $"fwd {fwd.BearingDeg:0.###} at {onLeg2!.X:0.#},{onLeg2.Y:0.#}, leg 2 {leg2Bearing:0.###}, chord {chord:0.###}")
              + "; path " + string.Join(" ", r.Paths.Where(p => p.Kept).SelectMany(p => p.Corners).Select(c => $"{c.X:0.#},{c.Y:0.#}")));
            C("RT8 its back gaze is the fwd gaze turned 180 deg",
              fwd is not null && back is not null && MathF.Abs(AngDiff(fwd.BearingDeg, back.BearingDeg) - 180f) < 1e-3f,
              back is null ? "no back gaze" : $"fwd {fwd!.BearingDeg:0.###}, back {back.BearingDeg:0.###}");
            C("RT8 every gaze is level at the eye",
              r.Stations.Where(s => s.Accepted).SelectMany(s => s.Gazes).All(g => StationRule.CheckLevel(
                  new Vector3(0, 0, g.LookAt.Z), g.LookAt) == StationRejection.None)
              && r.Stations.Where(s => s.Accepted).All(s => s.Gazes.All(g => MathF.Abs(g.LookAt.Z - s.Verdict!.EyeZ) <= 1e-4f)),
              "");

            // RT13: the yield closes per family, and it is mixed
            var ry = r.Yields.TryGetValue(RouteFamily.Route, out var yr) ? yr : null;
            C("RT13 every family's yield closes: accepted + refused + dropped = composed",
              r.Yields.Count == 3 && r.Yields.Values.All(y => y.Closes),
              string.Join("; ", r.Yields.Values.Select(y => $"{y.Family} {y.Accepted}+{y.RejectedTotal}+{y.DroppedTotal} vs {y.Composed}")));
            C("RT13 the route family is mixed: accepted stations and TightClearance refusals where the path hugs the inner corner",
              ry is not null && ry.Accepted >= 1 && ry.Rejected.TryGetValue(StationRejection.TightClearance, out int tight) && tight >= 1,
              ry is null ? "no route yield" : $"accepted {ry.Accepted}, " + string.Join(", ", ry.Rejected.Select(kv => $"{kv.Key} {kv.Value}")));
            var bad = new RouteResult();
            var by = new RouteYield { Family = RouteFamily.Fill, Composed = 10, Accepted = 3 };
            by.Rejected[StationRejection.TightClearance] = 2;               // 3 + 2 != 10
            bad.Yields[RouteFamily.Fill] = by;
            bool refused = false;
            try { StationPoseFile.RenderRoute(new Dictionary<string, object?>(), bad); }
            catch (InvalidOperationException) { refused = true; }
            C("RT13 the route pose writer refuses a yield that does not close", refused, "");
            string doc = "";
            try { doc = StationPoseFile.RenderRoute(new Dictionary<string, object?>(), r); } catch (Exception ex) { doc = ex.Message; }
            int rows = 0;
            try { using var jd = System.Text.Json.JsonDocument.Parse(doc); rows = jd.RootElement.GetProperty("poses").GetArrayLength(); } catch { }
            C("RT13 the route pose file publishes a row per tested candidate and per accepted gaze",
              rows > 0 && rows >= r.Stations.Count(s => s.Verdict is not null && !s.Accepted)
                                   + r.Stations.Where(s => s.Accepted).Sum(s => s.Gazes.Count),
              $"{rows} row(s)");
        }

        // ---- RT8b on a staircase path: the gaze is the kept path's own segment ---------------------
        {
            var m = new MeshB();
            int leg1 = m.Grid(0, 0, 128, 128, 16, 4);          // x 0..2048, y 0..512
            int leg2 = m.Grid(1536, 512, 128, 128, 4, 16);     // x 1536..2048, y 512..2560
            for (int i = 0; i < 4; i++)
                m.Join(leg1 + 2 * (3 * 16 + 12 + i) + 1, 1, leg2 + 2 * i, 0);
            m.Door(leg1 + 1);
            m.Door(leg2 + 2 * (15 * 4 + 2) + 1);
            var a = RouteStations.Classify(World(new[] { m.Build(FK(0x80F)) }, new[] { Defined(0x81, 1024, 1024) }));
            var r = RouteStations.Sample(a, Env(a));
            Dump("RT8b", r);
            var path = r.Paths.FirstOrDefault(p => p.Kept);
            int checkedN = 0; float worst = 0f; bool ok = path is not null;
            foreach (var s in r.Stations.Where(s => s.Family == RouteFamily.Route && s.Accepted))
            {
                // the segment containing the station's walked position, found here from the path's corners
                float walked = s.WalkedAt ?? -1f, sum = 0f; int seg = -1;
                for (int i = 0; path is not null && i + 1 < path.Corners.Count; i++)
                {
                    float l = Vector2.Distance(path.Corners[i], path.Corners[i + 1]);
                    if (l > 0f && sum + l >= walked) { seg = i; break; }
                    sum += l;
                }
                var fwd = s.Gazes.FirstOrDefault(g => g.Kind == RouteGazeKind.Fwd);
                if (seg < 0 || fwd is null) { ok = false; break; }
                var d = path!.Corners[seg + 1] - path.Corners[seg];
                float want = StationRule.Bearing(d.X, d.Y);
                worst = MathF.Max(worst, AngDiff(fwd.BearingDeg, want));
                checkedN++;
            }
            C("RT8b on the 128 u grid L's staircase path, every accepted route station's G-fwd is its own segment's bearing",
              ok && checkedN >= 2 && worst <= 1e-3f,
              $"{checkedN} station(s) checked, worst difference {worst:0.######} deg; path "
              + (path is null ? "none" : string.Join(" ", path.Corners.Select(c => $"{c.X:0.#},{c.Y:0.#}"))));
            C("RT8b every route station publishes its G-fwd's deviation from the path 512 u ahead",
              r.Stations.Where(s => s.Family == RouteFamily.Route && s.Accepted).All(s => s.FwdVsAheadDeg is { } v && v >= 0f && v <= 180f),
              string.Join(" ", r.Stations.Where(s => s.Family == RouteFamily.Route && s.Accepted).Select(s => $"{s.FwdVsAheadDeg:0.#}")));
        }

        // ---- RT9 content gaze ---------------------------------------------------------------------
        {
            var eye = P(0, 0);
            var pts = new List<Vector2>();
            for (int k = 0; k <= 19; k++)                       // bearings 52..128, 1,000 u out
            {
                float b = (52f + k * 4f) * MathF.PI / 180f;
                pts.Add(P(1000f * MathF.Sin(b), 1000f * MathF.Cos(b)));
            }
            pts.Add(P(0, 800)); pts.Add(P(30, 900)); pts.Add(P(-30, 900));   // a smaller cluster due north
            pts.Add(P(9000, 0));                                             // beyond the content reach
            float bearing = RouteStations.ContentBearing(eye, pts, null);
            C("RT9 the content gaze turns to the cluster due east (90 +- 5 deg), not a fixed or path gaze",
              AngDiff(bearing, 90f) <= 5f, $"bearing {bearing:0.###}");
            float withFwd = RouteStations.ContentBearing(eye, pts, 0f);
            C("RT9 a path direction due north does not pull it off the larger cluster",
              AngDiff(withFwd, 90f) <= 5f, $"bearing {withFwd:0.###} with fwd 0");
        }

        // ---- RT10 water in route mode, RT11 clearance, RT14 child footprint, RT16 primitives -------
        {
            var m = Pad();
            var child = new MeshB();
            child.Tri(P(550, 550), P(700, 550), P(600, 700), flags: 0x08);   // deleted, still a footprint
            var prim = new PrimitiveGeometry("PRIM-H", PrimitiveKind.PortalBox, new Vector3(400f, 2f, 300f),
                                             new Vector3(800f, 800f, StationRule.EyeHeight),
                                             new Vector3(MathF.PI / 2f, 0f, 0f), WS, CELL);
            var w = World(new[] { m.Build(FK(0x80B)) }, new[] { Defined(0x90, 512, 512) },
                          prims: new[] { prim }, child: new[] { child.Build(new FormKey(Fixture, 0x80C)) });
            var a = RouteStations.Classify(w);
            var ok = RouteStations.Test(a, Env(a), 300f, 300f);
            C("RT10-16 control: a clear point on the pad, off the child footprint and the primitive, is accepted",
              ok.Reason == StationRejection.None, $"{ok.Reason}: {ok.Detail}");
            var wet = RouteStations.Test(a, Env(a, Surface(10f)), 300f, 300f);
            C("RT10 feet 10 u below the cell's water: refused StationUnderwater through the shared water check",
              wet.Reason == StationRejection.StationUnderwater && wet.FeetAboveSurface is { } fa && MathF.Abs(fa + 10f) < 1e-3f,
              $"{wet.Reason}, feet above surface {wet.FeetAboveSurface}");
            var tight = RouteStations.Test(a, Env(a), 30f, 512f);
            C("RT11 a point 30 u from the mesh boundary is refused TightClearance",
              tight.Reason == StationRejection.TightClearance, $"{tight.Reason}: {tight.Detail}");
            var inChild = RouteStations.Test(a, Env(a), 600f, 600f);
            C("RT14 a point under a child-worldspace triangle (deleted, any flags) is refused InChildWorldspaceFootprint",
              inChild.Reason == StationRejection.InChildWorldspaceFootprint, $"{inChild.Reason}: {inChild.Detail}");
            var inPrim = RouteStations.Test(a, Env(a), 800f, 900f);
            C("RT16 an eye inside a horizontal PortalBox (full rotation, which the Z-only test misses) is refused EyeInsidePrimitive",
              inPrim.Reason == StationRejection.EyeInsidePrimitive
              && !StationRule.EyeInside(prim, new Vector3(800f, 900f, StationRule.EyeHeight)),
              $"{inPrim.Reason}: {inPrim.Detail}");
        }

        // ---- RT12 coverage by area ----------------------------------------------------------------
        {
            var own = new MeshB();
            int t0 = own.Grid(0, 0, 1024, 1024, 1, 1);
            own.Door(t0);
            var a = RouteStations.Classify(World(Array.Empty<NavmeshGeometry>(), Array.Empty<RoutePlaced>(),
                                                 cityMeshes: new[] { own.Build(CK(0x901)) }));
            double quarter = Math.PI * 512.0 * 512.0 / 4.0;
            C("RT12 A_W of a 1,024 u city-owned square is its whole area", Math.Abs(a.AreaA - 1024.0 * 1024.0) < 1e-3,
              $"{a.AreaA:0.###}");
            var two = RouteStations.Coverage(a, new[] { P(0, 0), P(1024, 1024) });
            C("RT12 stations at two corners cover two quarter discs of radius D, exactly",
              Math.Abs(two.Covered - 2.0 * quarter) < 0.5, $"{two.Covered:0.###} vs {2.0 * quarter:0.###}");
            var seg = RouteStations.Coverage(a, new[] { P(512, -256) });
            double segment = 512.0 * 512.0 * Math.Acos(0.5) - 256.0 * Math.Sqrt(512.0 * 512.0 - 256.0 * 256.0);
            C("RT12 a station 256 u outside an edge covers the circular segment, exactly",
              Math.Abs(seg.Covered - segment) < 0.5, $"{seg.Covered:0.###} vs {segment:0.###}");
            var res = new RouteResult();
            res.Stations.Add(new RouteStation { Id = "a", Family = RouteFamily.Fill, X = 0, Y = 0, Accepted = true });
            res.Stations.Add(new RouteStation { Id = "b", Family = RouteFamily.Fill, X = 1024, Y = 1024, Accepted = false });
            var l1 = RouteStations.CoverageL1(a, res);
            C("RT12 L1 counts accepted stations only, not a refused candidate",
              Math.Abs(l1.Covered - quarter) < 0.5, $"{l1.Covered:0.###} vs {quarter:0.###}");
            // a later level: station "b" dropped out of it for a named reason; its uncovered piece carries that reason
            var lv = RouteStations.Coverage(a, new[] { P(0, 0) }, res.Stations,
                s => s.Id == "b" ? "cannot assess" : null);
            C("RT12 a later level's coverage uses its own set, and its uncovered piece carries the drop-out's reason",
              Math.Abs(lv.Covered - quarter) < 0.5 && lv.Pieces.Count == 1 && lv.Pieces[0].Reason == "cannot assess"
              && Math.Abs(lv.UncoveredByReason["cannot assess"] - (1024.0 * 1024.0 - quarter)) < 0.5,
              $"covered {lv.Covered:0.###}, pieces {lv.Pieces.Count}, reason {(lv.Pieces.Count > 0 ? lv.Pieces[0].Reason : "none")}");
        }

        // ---- RT17 the covering triangle is searched over every winning navmesh (AMENDMENT-01 r8) --
        {
            var pad = Pad();
            var up = new MeshB();
            int hi = up.Tri(P(200, 200), P(450, 200), P(300, 450), z: 200f);   // over the pad, 200 u up
            up.Door(hi);
            var keyUp = FK(0x80E);
            // filed under a grid square far outside the loaded set, though its geometry lies over the pad
            var a = RouteStations.Classify(World(new[] { pad.Build(FK(0x80D)), up.Build(keyUp) },
                                                 new[] { Defined(0xA0, 512, 512) },
                                                 filedUnder: new Dictionary<FormKey, (int, int)> { [keyUp] = (9, 9) }));
            C("RT17 fixture: the higher navmesh is winning but outside the loaded set",
              a.Winning.ContainsKey(keyUp) && !a.Loaded.ContainsKey(keyUp), $"loaded {a.Loaded.ContainsKey(keyUp)}");
            var v = RouteStations.Test(a, Env(a), 300f, 283.333f);
            C("RT17 a point whose highest covering node triangle is in an unloaded navmesh is refused Unreachable, "
              + "not accepted on the lower loaded triangle",
              v.Reason == StationRejection.Unreachable && v.Covering is { } ct && ct.Mesh == keyUp,
              $"{v.Reason} on {v.Covering}: {v.Detail}");
        }

        // ---- RT15 the seeded fill order and retry alternates --------------------------------------
        {
            var mk = new FormKey(Fixture, 0x801);
            C("INHERITED RT15 a FormKey prints as the rule's key text", mk.ToString() == "000801:Fixture.esm", mk.ToString());
            string key = RouteStations.FillKey(mk, 3, "021");
            C("RT15 the fill key is sha256(\"g5-fill:20260925:<mesh>:<tri>:<subPath>\") in lower-case hex (Python oracle)",
              key == "56d2fbaa0f24753e8e7dab9c577b15904bb3ee0655e681636c16d7befa58d398", key);
            var expect = new (double X, double Y)[]
            {
                (798.3135358896106, 255.4156315047294), (540.9291266929358, 221.57695032656193),
                (837.2159936930984, 216.11855067312717), (211.4862496033311, 265.21421927027404),
                (154.262864170596, 372.89806297048926), (557.0955417584628, 344.3049665540457),
            };
            bool all = true; var got = new List<string>();
            for (int j = 1; j <= 6; j++)
            {
                var p = RouteStations.RetryPoint(mk, 3, "021", j, P(100, 200), P(1100, 200), P(100, 700));
                got.Add($"{p.X:0.###},{p.Y:0.###}");
                if (Math.Abs(p.X - expect[j - 1].X) > 1e-2 || Math.Abs(p.Y - expect[j - 1].Y) > 1e-2) all = false;
            }
            C("RT15 retry alternates 1-6 fall where the Python oracle puts them, folds included", all, string.Join(" ", got));
        }

        // ---- RT18 a candidate is tested on its own storey under a deck (C2307-C2309) -----------------
        // A pad (z 0, 1,024 u square, a door in its south-west corner) and a deck (z 300, x/y 256..768, or over the whole pad).
        // Coverage and the candidate dedup are plan measures, so each answer gets its own world.
        {
            static string Say(RouteStation s) => $"{s.Id} at {s.X:0.#},{s.Y:0.#} on {s.Verdict?.Covering} z {s.Verdict?.Env.NavZ:0.#} {s.Verdict?.Reason}";
            static bool UnderDeck(RouteStation s, float lo, float hi) => s.X > lo && s.X < hi && s.Y > lo && s.Y < hi;
            (RouteArea A, RouteResult R, FormKey Pad, FormKey Deck) DeckWorld(uint id, bool padExit, bool deckDoor, bool wholePad = false)
            {
                var pad = Pad();
                if (padExit) pad.Door(127);                  // a second door, north-east: a solved path crosses the pad
                var deck = new MeshB();
                int d0 = wholePad ? deck.Grid(0, 0, 128, 128, 8, 8, z: 300f) : deck.Grid(256, 256, 128, 128, 4, 4, z: 300f);
                if (deckDoor) deck.Door(d0, 0xE00);          // walkable ground of its own; without it, not walkable
                FormKey kp = FK(id), kd = FK(id + 1);
                var a = RouteStations.Classify(World(new[] { pad.Build(kp), deck.Build(kd) }, new[] { Defined(0xB0, 512, 512) }));
                return (a, RouteStations.Sample(a, Env(a)), kp, kd);
            }
            bool OnPad(RouteStation s, FormKey kp) => s.Verdict!.Covering is { } c && c.Mesh == kp && MathF.Abs(s.Verdict.Env.NavZ) < 1e-3f;

            // C2307: the path between the pad's two doors runs under an unwalkable deck.
            var w1 = DeckWorld(0x820, padExit: true, deckDoor: false);
            var routeUnder = w1.R.Stations.Where(s => s.Family == RouteFamily.Route && s.Verdict is not null && UnderDeck(s, 256f, 768f)).ToList();
            C("RT18 fixture: a route candidate on the pad's solved path lies under the deck", routeUnder.Count >= 1,
              $"{routeUnder.Count} route candidate(s) under the deck; paths " + string.Join(" | ", w1.R.Paths.Where(p => p.Kept)
                  .Select(p => string.Join(" ", p.Corners.Select(c => $"{c.X:0.#},{c.Y:0.#}")))));
            C("RT18 (C2307) a route candidate under a deck is tested on the path's own storey: the pad triangle, navmesh z 0",
              routeUnder.Count >= 1 && routeUnder.All(s => OnPad(s, w1.Pad)), string.Join("; ", routeUnder.Select(Say)));

            // C2308: one door, so no solved path; the pad's ground under an unwalkable deck over the whole pad is filled.
            var w2 = DeckWorld(0x822, padExit: false, deckDoor: false, wholePad: true);
            var padFillsUnder = w2.R.Stations.Where(s => s.Family != RouteFamily.Route && s.Verdict is not null
                                                        && s.SourceTri is { } st && st.Mesh == w2.Pad && UnderDeck(s, 0f, 1024f)).ToList();
            C("RT18 (C2308) a fill candidate from a pad triangle under a deck is tested on that triangle's storey, z 0",
              padFillsUnder.Count >= 1 && padFillsUnder.All(s => OnPad(s, w2.Pad)),
              $"{padFillsUnder.Count} candidate(s): " + string.Join("; ", padFillsUnder.Take(6).Select(Say)));

            // C2309 (preservation): a walkable deck's own fill candidates are tested on the deck.
            var w3 = DeckWorld(0x824, padExit: false, deckDoor: true);
            var deckFills = w3.R.Stations.Where(s => s.Family != RouteFamily.Route && s.Verdict is not null
                                                    && s.SourceTri is { } st && st.Mesh == w3.Deck).ToList();
            C("RT18 (C2309) a fill candidate from a deck triangle is tested on the deck, z 300",
              deckFills.Count >= 1 && deckFills.All(s => s.Verdict!.Covering is { } c && c.Mesh == w3.Deck
                                                         && MathF.Abs(s.Verdict.Env.NavZ - 300f) < 1e-3f),
              $"{deckFills.Count} candidate(s): " + string.Join("; ", deckFills.Take(6).Select(Say)));
        }

        check("stationrule: route declared case count", ran == DeclaredCases,
              $"{ran} cases ran, {DeclaredCases} declared");
    }
}
