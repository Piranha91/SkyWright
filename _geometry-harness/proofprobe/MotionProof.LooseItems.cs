using System.Numerics;
using HousecarlCore;
using SkyWright.Engine;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;

/// <summary>Known answers for the loose-item clashes rules (Gate B): (H) a convex-vertices shape's faces rebuilt from its
/// vertices when the stored planes drop a face — the shape IS the hull of its vertices; (F) a moving body with no faces is
/// refused by name, never read as "nothing below"; (R) the rest rule: a settling body with nothing below is `lost` (an
/// outcome), an exempt body carries its gap to the support below (or floats over nothing), and the enable-timing-unknown
/// body holds its placed box. Expected values are analytic and stated beside each case. Fixture controls hold on the base;
/// the rule cases are red until implemented (stubs throw or return today's answer). The canary is wrong by construction.</summary>
static partial class MotionProof
{
    /// <summary>The eight corners of [x0,x1]x[y0,y1]x[z0,z1] in Havok units with six face planes each turned by
    /// <paramref name="tiltDeg"/> about the axis (1,1,1)/√3: a turned face normal meets one or two corners at its maximum, so
    /// the decoder's three-vertices-per-plane test drops every face (the shape of a real shield mesh whose stored planes match
    /// none of its faces). tiltDeg 0 is the exact box.</summary>
    static bhkConvexVerticesShape ConvexBoxTilted(float x0, float y0, float z0, float x1, float y1, float z1, float tiltDeg)
    {
        var s = ConvexBox(x0, y0, z0, x1, y1, z1);
        var q = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 1, 1)), tiltDeg * MathF.PI / 180f);
        s.Normals = s.Normals.Select(n => { var r = Vector3.Transform(new Vector3(n.X, n.Y, n.Z), q); return new Vector4(r, n.W); }).ToList();
        return s;
    }

    /// <summary>A free item (render box [-5,5]^3) whose movable convex body is the box with its planes turned 10°.</summary>
    static byte[] FreeItemTiltedPlanes(float tiltDeg = 10f)
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var item = new NiNode { Name = new NiStringRef("Item"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33() };
        root.Children.AddBlockRef(f.AddBlock(item));
        item.Children.AddBlockRef(f.AddBlock(Shape("ItemMesh", Box(-5, -5, -5, 5, 5, 5))));
        AddBody(f, item, ConvexBoxTilted(-5, -5, -5, 5, 5, 5, tiltDeg), 5f, hkMotionType.MO_SYS_SPHERE_STABILIZED, SkyrimLayer.SKYL_CLUTTER);
        return Save(f, "free item, tilted planes");
    }

    /// <summary>A free item (render box [-5,5]^3) whose convex body is the box [-5,5]^3 with convex radius
    /// <paramref name="radiusU"/> (Skyrim units) and its six planes stored <paramref name="planeOffsetU"/> outside the
    /// vertices (Havok: the planes describe the radius shell when the offset equals the radius).</summary>
    static byte[] FreeItemRadius(float radiusU, float planeOffsetU)
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var item = new NiNode { Name = new NiStringRef("Item"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33() };
        root.Children.AddBlockRef(f.AddBlock(item));
        item.Children.AddBlockRef(f.AddBlock(Shape("ItemMesh", Box(-5, -5, -5, 5, 5, 5))));
        var s = ConvexBox(-5, -5, -5, 5, 5, 5);
        s.Radius = radiusU / HavokU;
        s.Normals = s.Normals.Select(n => new Vector4(n.X, n.Y, n.Z, n.W - planeOffsetU / HavokU)).ToList();
        AddBody(f, item, s, 5f, hkMotionType.MO_SYS_SPHERE_STABILIZED, SkyrimLayer.SKYL_CLUTTER);
        return Save(f, "free item, convex radius");
    }

    /// <summary>The free item with its render box reaching 3 u below its collision box ([-5,5]^2 x [-8,5]).</summary>
    static byte[] FreeItemDeepRender()
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var item = new NiNode { Name = new NiStringRef("Item"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33() };
        root.Children.AddBlockRef(f.AddBlock(item));
        item.Children.AddBlockRef(f.AddBlock(Shape("ItemMesh", Box(-5, -5, -8, 5, 5, 5))));
        AddBody(f, item, ConvexBox(-5, -5, -5, 5, 5, 5), 5f, hkMotionType.MO_SYS_SPHERE_STABILIZED, SkyrimLayer.SKYL_CLUTTER);
        return Save(f, "free item, deep render");
    }

    /// <summary>A free item (render box [-5,5]^3) whose movable body is a bhkBoxShape of half extents
    /// <paramref name="halfU"/> with Havok radius <paramref name="radiusU"/> (Skyrim units; Havok's box surface is the box grown by
    /// its radius).</summary>
    static byte[] FreeItemBoxRadius(float halfU, float radiusU)
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var item = new NiNode { Name = new NiStringRef("Item"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33() };
        root.Children.AddBlockRef(f.AddBlock(item));
        item.Children.AddBlockRef(f.AddBlock(Shape("ItemMesh", Box(-5, -5, -5, 5, 5, 5))));
        AddBody(f, item, new bhkBoxShape { Dimensions = new Vector3(halfU / HavokU), Radius = radiusU / HavokU }, 5f,
                hkMotionType.MO_SYS_SPHERE_STABILIZED, SkyrimLayer.SKYL_CLUTTER);
        return Save(f, "free item, box radius");
    }

    /// <summary>A static support whose collision is a bhkBoxShape of half extents (<paramref name="halfXY"/>, <paramref name="halfXY"/>,
    /// <paramref name="halfZ"/>) centred at the origin with Havok radius <paramref name="radiusU"/> (Skyrim units).</summary>
    static byte[] SupportBox(float halfXY, float halfZ, float radiusU)
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var n = new NiNode { Name = new NiStringRef("Support"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33() };
        root.Children.AddBlockRef(f.AddBlock(n));
        n.Children.AddBlockRef(f.AddBlock(Shape("SupportMesh", Box(-halfXY, -halfXY, -halfZ, halfXY, halfXY, halfZ))));
        AddBody(f, n, new bhkBoxShape { Dimensions = new Vector3(halfXY / HavokU, halfXY / HavokU, halfZ / HavokU), Radius = radiusU / HavokU }, 0f,
                hkMotionType.MO_SYS_SPHERE_STABILIZED, SkyrimLayer.SKYL_STATIC);
        return Save(f, "support, box radius");
    }

    /// <summary>A static support whose collision is a bhkCompressedMeshShape: one square of half size <paramref name="half"/> at z 0
    /// (two big triangles, facing up) with mesh radius <paramref name="radiusU"/> (Skyrim units).</summary>
    static byte[] SupportMeshPlate(float half, float radiusU) => SupportMeshRect(half, half, radiusU);

    /// <summary>As <see cref="SupportMeshPlate"/>, a rectangle of half sizes <paramref name="hx"/> x <paramref name="hy"/>.</summary>
    static byte[] SupportMeshRect(float hx, float hy, float radiusU)
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var n = new NiNode { Name = new NiStringRef("Support"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33() };
        root.Children.AddBlockRef(f.AddBlock(n));
        n.Children.AddBlockRef(f.AddBlock(Shape("SupportMesh", Box(-hx, -hy, -1, hx, hy, 0))));
        float x = hx / HavokU, y = hy / HavokU;
        var data = new bhkCompressedMeshShapeData
        {
            BigVerts = new() { new(-x, -y, 0, 0), new(x, -y, 0, 0), new(x, y, 0, 0), new(-x, y, 0, 0) },
            BigTris = new() { new() { Triangle = new NiflySharp.Structs.Triangle(0, 1, 2) }, new() { Triangle = new NiflySharp.Structs.Triangle(0, 2, 3) } },
        };
        AddBody(f, n, new bhkCompressedMeshShape { Data = new NiBlockRef<bhkCompressedMeshShapeData>(f.AddBlock(data)), Radius = radiusU / HavokU }, 0f,
                hkMotionType.MO_SYS_SPHERE_STABILIZED, SkyrimLayer.SKYL_STATIC);
        return Save(f, "support, compressed-mesh plate with radius");
    }

    /// <summary>A static support whose collision is a bhkCompressedMeshShape of the given triangles (big vertices, Skyrim units) with
    /// mesh radius <paramref name="radiusU"/> (Skyrim units). The render shape is the triangles' bounding box.</summary>
    static byte[] SupportMesh(Vector3[] vertsU, int[] tris, float radiusU)
    {
        var f = new NifFile(); f.Create(Ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var n = new NiNode { Name = new NiStringRef("Support"), Flags_ui = 0xE, Scale = 1f, Rotation = Identity33() };
        root.Children.AddBlockRef(f.AddBlock(n));
        n.Children.AddBlockRef(f.AddBlock(Shape("SupportMesh", Box(vertsU.Min(v => v.X), vertsU.Min(v => v.Y), vertsU.Min(v => v.Z),
                                                                     vertsU.Max(v => v.X), vertsU.Max(v => v.Y), vertsU.Max(v => v.Z)))));
        var data = new bhkCompressedMeshShapeData
        {
            BigVerts = vertsU.Select(v => new Vector4(v.X / HavokU, v.Y / HavokU, v.Z / HavokU, 0)).ToList(),
            BigTris = new(),
        };
        for (int k = 0; k + 2 < tris.Length; k += 3)
            data.BigTris.Add(new() { Triangle = new NiflySharp.Structs.Triangle((ushort)tris[k], (ushort)tris[k + 1], (ushort)tris[k + 2]) });
        AddBody(f, n, new bhkCompressedMeshShape { Data = new NiBlockRef<bhkCompressedMeshShapeData>(f.AddBlock(data)), Radius = radiusU / HavokU }, 0f,
                hkMotionType.MO_SYS_SPHERE_STABILIZED, SkyrimLayer.SKYL_STATIC);
        return Save(f, "support, compressed mesh with radius");
    }

    /// <summary>The cube [-h,h]^3 (ClosedBox's vertices and winding) turned <paramref name="deg"/> about x (or about y), raised by
    /// <paramref name="cz"/>, then the whole turned <paramref name="tiltXDeg"/> about x.</summary>
    static TriangleSoup TurnedCube(float h, bool aboutX, double deg, double cz, double tiltXDeg)
    {
        double c = Math.Cos(deg * Math.PI / 180), s = Math.Sin(deg * Math.PI / 180);
        double ct = Math.Cos(tiltXDeg * Math.PI / 180), st = Math.Sin(tiltXDeg * Math.PI / 180);
        var cube = ClosedBox(-h, -h, -h, h, h, h);
        var v = cube.Vertices.Select(p =>
        {
            double x = p.X, y = p.Y, z = p.Z;
            var (qx, qy, qz) = aboutX ? (x, y * c - z * s, y * s + z * c) : (x * c + z * s, y, -x * s + z * c);
            qz += cz;
            return new Vector3((float)qx, (float)(qy * ct - qz * st), (float)(qy * st + qz * ct));
        }).ToArray();
        return new TriangleSoup(v, cube.Indices);
    }

    static (int Tris, bool Closed, bool Outward) HullFacts(IReadOnlyList<Vector3> v, IReadOnlyList<int> idx, Vector3 inside)
    {
        var edges = new Dictionary<(int, int), int>();
        bool outward = true;
        for (int k = 0; k + 2 < idx.Count; k += 3)
        {
            int a = idx[k], b = idx[k + 1], c = idx[k + 2];
            var n = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
            if (Vector3.Dot(n, (v[a] + v[b] + v[c]) / 3 - inside) <= 0) outward = false;
            foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) }) { var e = p < q ? (p, q) : (q, p); edges[e] = edges.GetValueOrDefault(e) + 1; }
        }
        return (idx.Count / 3, edges.Count > 0 && edges.Values.All(x => x == 2), outward);
    }

    public static void RunLooseItems(Action<string, bool, string> check)
    {
        // ================= canary (wrong by construction; must stay RED) =================
        Case(check, "loose-items CANARY (must FAIL): the exact convex box decodes to 11 triangles", () =>
        {
            var b = PhysicsReach.ModelBodies(FreeItem()).Single();
            return (b.Model.Indices.Length / 3 == 11, $"tris {b.Model.Indices.Length / 3}");
        });

        // ================= fixture controls (existing code; hold on the base) =================
        // The exact box decodes through the plane route: 12 triangles, complete. Rejects a fixture that differs from ConvexBox.
        Case(check, "loose-items fixture control: the exact convex box decodes to 12 triangles, complete", () =>
        {
            var b = PhysicsReach.ModelBodies(FreeItem()).Single();
            return (b.Model.Indices.Length / 3 == 12 && b.Complete && b.Movable, $"tris {b.Model.Indices.Length / 3} complete {b.Complete}");
        });
        // The tilted fixture carries the defect's input: 8 vertices, and the plane route drops all 6 stored planes. (Until the fit
        // this control also asserted the base decoder's 0 triangles, which held on the base, red record e4ba295e; the rebuild
        // makes that half false by design, so the control now pins the input that triggers the rebuild.)
        Case(check, "loose-items fixture control: the 10°-tilted-plane box has 8 vertices and the plane route drops all 6 stored planes", () =>
        {
            var hc = HavokCollision.Decode(Load(FreeItemTiltedPlanes()), true).Stats;
            var b = PhysicsReach.ModelBodies(FreeItemTiltedPlanes()).Single();
            return (hc.ConvexVertices == 8 && hc.ConvexPlanesDropped == 6, $"verts {hc.ConvexVertices} dropped {hc.ConvexPlanesDropped} tris {b.Model.Indices.Length / 3}");
        });

        // ================= H: the hull from the vertices =================
        // LI-H1: cube corners plus its centre -> 12 triangles, closed, outward; the centre is not a vertex of any face.
        // Rejects missing faces, inward winding, an interior point used as a corner.
        Case(check, "loose-items LI-H1: hull of the 8 cube corners + centre — 12 outward triangles, closed, centre unused", () =>
        {
            var pts = new List<Vector3>();
            for (int k = 0; k < 8; k++) pts.Add(new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1));
            pts.Add(Vector3.Zero);
            var (idx, deg) = ConvexHull.FromVertices(pts);
            var f = HullFacts(pts, idx, Vector3.Zero);
            return (!deg && f.Tris == 12 && f.Closed && f.Outward && !idx.Contains(8), $"tris {f.Tris} closed {f.Closed} outward {f.Outward} degenerate {deg} usesCentre {idx.Contains(8)}");
        });
        // LI-H2: a flat rectangle (4 coplanar points: a plate shape real collision files carry) -> its two triangles in both
        // windings (4 triangles), area 2 x the rectangle's. Rejects zero faces for a plate, and one winding only.
        Case(check, "loose-items LI-H2: hull of a flat 4 x 6 rectangle — 4 triangles (both windings), total area 2 x 24", () =>
        {
            var pts = new List<Vector3> { new(-2f, 0, -3f), new(2f, 0, -3f), new(2f, 0, 3f), new(-2f, 0, 3f) };
            var (idx, deg) = ConvexHull.FromVertices(pts);
            double area = 0; var up = 0; var down = 0;
            for (int k = 0; k + 2 < idx.Count; k += 3)
            {
                var n = Vector3.Cross(pts[idx[k + 1]] - pts[idx[k]], pts[idx[k + 2]] - pts[idx[k]]);
                area += n.Length() / 2; if (n.Y > 0) up++; else if (n.Y < 0) down++;
            }
            double want = 2 * 4.0 * 6.0;
            return (!deg && idx.Count / 3 == 4 && up == 2 && down == 2 && NearA7(area, want, 1e-3), $"tris {idx.Count / 3} up {up} down {down} area {area:0.####} want {want:0.####} degenerate {deg}");
        });
        // LI-H3: collinear points -> no triangles, degenerate. Rejects an invented face.
        Case(check, "loose-items LI-H3: collinear points — no triangles, degenerate", () =>
        {
            var (idx, deg) = ConvexHull.FromVertices(new List<Vector3> { new(0, 0, 0), new(1, 1, 1), new(2, 2, 2), new(3, 3, 3) });
            return (deg && idx.Count == 0, $"tris {idx.Count / 3} degenerate {deg}");
        });
        // LI-H4: the decoder rebuilds the tilted box from its vertices: 12 triangles, closed, outward, complete, counted once.
        // Rejects today's 0 triangles (the defect), and a rebuild that is not counted.
        Case(check, "loose-items LI-H4: the 10°-tilted-plane box decodes to the box hull (12 outward triangles, closed), ConvexRebuiltFromVertices 1", () =>
        {
            var hc = HavokCollision.Decode(Load(FreeItemTiltedPlanes()), true).Stats;
            var b = PhysicsReach.ModelBodies(FreeItemTiltedPlanes()).Single();
            var f = HullFacts(b.Model.Vertices, b.Model.Indices, Vector3.Zero);
            return (f.Tris == 12 && f.Closed && f.Outward && b.Complete && hc.ConvexRebuiltFromVertices == 1,
                    $"tris {f.Tris} closed {f.Closed} outward {f.Outward} complete {b.Complete} rebuilt {hc.ConvexRebuiltFromVertices}");
        });
        // LI-H5: a shape whose planes all match keeps the plane route: ConvexRebuiltFromVertices 0. Rejects rebuilding clean shapes
        // (default output churn for every shape the decoder already reads whole).
        Case(check, "loose-items LI-H5: the exact box is not rebuilt (ConvexRebuiltFromVertices 0, 12 triangles)", () =>
        {
            var hc = HavokCollision.Decode(Load(FreeItem()), true).Stats;
            var b = PhysicsReach.ModelBodies(FreeItem()).Single();
            return (hc.ConvexRebuiltFromVertices == 0 && b.Model.Indices.Length / 3 == 12, $"rebuilt {hc.ConvexRebuiltFromVertices} tris {b.Model.Indices.Length / 3}");
        });

        // ================= K: the convex radius (Gate B review F1) =================
        // LI-K1: planes stored one radius (2 u) outside the vertices -> the planes match the hull's radius window (counted
        // matched, none mismatched) and the solid is the box grown by the radius: collision bounds [-7,7]^3 on the axes.
        // Rejects the radius ignored (bounds [-5,5]) and a shell plane read as a mismatch.
        Case(check, "loose-items LI-K1: planes one radius (2 u) outside the vertices — matched 1, mismatched 0, radius applied 1, collision bounds [-7,7]^3", () =>
        {
            var hc = HavokCollision.Decode(Load(FreeItemRadius(2f, 2f)), true, convexRadius: true).Stats;
            var b = PhysicsReach.ModelBodies(FreeItemRadius(2f, 2f), convexRadius: true).Single();
            var bb = SpatialOverlap.SoupBounds(b.Model);
            bool box7 = NearA7(bb.Min.X, -7, 1e-3) && NearA7(bb.Max.X, 7, 1e-3) && NearA7(bb.Min.Z, -7, 1e-3) && NearA7(bb.Max.Z, 7, 1e-3) && NearA7(bb.Max.Y, 7, 1e-3);
            return (hc.ConvexPlanesMatchHull == 1 && hc.ConvexPlanesMismatch == 0 && hc.ConvexRadiusApplied == 1 && box7 && b.Complete,
                    $"matched {hc.ConvexPlanesMatchHull} mismatched {hc.ConvexPlanesMismatch} radiusApplied {hc.ConvexRadiusApplied} bounds {bb.Min.X:0.###},{bb.Min.Y:0.###},{bb.Min.Z:0.###}..{bb.Max.X:0.###},{bb.Max.Y:0.###},{bb.Max.Z:0.###}");
        });
        // LI-K2: under the rest rule the radius item over a floor 100 u down drops 100 - 7 = 93 (the shell meets the floor), and
        // with physics alone it drops 93 too (2026-10-03 convex-radius-default: the physics drop reads the radius; was 95 while the
        // radius was opt-in behind the rest rule). Rejects the radius ignored under either.
        Case(check, "loose-items LI-K2: the radius-2 item over a floor 100 u down — rest on d 93; physics only d 93", () =>
        {
            var fl = new[] { new DropSupport("floor", 1, true, false, Plate(500, -100)) };
            var on = MeshReach.OfModel(FreeItemRadius(2f, 2f), Origin, Origin, 1f, null, new ReachOptions(0.5, 65536, new ReachRules(Physics: true, BaseRecordType: "MISC",
                Supports: _ => fl, Layers: RRLayers, Settle: new SettleDecision("settles", null), Rest: true)));
            var off = MeshReach.OfModel(FreeItemRadius(2f, 2f), Origin, Origin, 1f, null, RulesOn(physics: true, baseType: "MISC", supports: fl));
            return (on.Drop is { Status: "dropped" } d && NearA7(d.Distance, 93, 1e-3) && off.Drop is { Status: "dropped" } d2 && NearA7(d2.Distance, 93, 1e-3),
                    ShowRR(on) + " | off " + ShowRR(off));
        });
        // LI-K4: the decoder's default leaves the radius off (the NifGeometry reader's calibrations read the core hull): collision
        // bounds [-5,5] with the radius off, ConvexRadiusApplied 0, while the planes are still checked (matched 1). Rejects the radius
        // applied by the decoder's default. (PhysicsReach reads it by default since 2026-10-03: LI-K8.)
        Case(check, "loose-items LI-K4: default decode — radius not applied (bounds [-5,5]^3, applied 0), planes still checked (matched 1)", () =>
        {
            var hc = HavokCollision.Decode(Load(FreeItemRadius(2f, 2f)), true).Stats;
            var bb = SpatialOverlap.SoupBounds(PhysicsReach.ModelBodies(FreeItemRadius(2f, 2f), convexRadius: false).Single().Model);
            return (hc.ConvexRadiusApplied == 0 && hc.ConvexPlanesMatchHull == 1 && NearA7(bb.Max.Z, 5, 1e-3) && NearA7(bb.Min.X, -5, 1e-3),
                    $"applied {hc.ConvexRadiusApplied} matched {hc.ConvexPlanesMatchHull} bounds {bb.Min.X:0.###}..{bb.Max.Z:0.###}");
        });
        // LI-K3: planes stored 3 u INSIDE the vertices (the shield's shape: planes cutting through the hull) -> counted and named
        // as a mismatch; the faces still come from the vertices plus radius (bounds [-7,7]). Rejects a silent pass.
        Case(check, "loose-items LI-K3: planes 3 u inside the vertices — mismatched 1 and named, solid still the vertices + radius", () =>
        {
            var hc = HavokCollision.Decode(Load(FreeItemRadius(2f, -3f)), true, convexRadius: true).Stats;
            var b = PhysicsReach.ModelBodies(FreeItemRadius(2f, -3f), convexRadius: true).Single();
            var bb = SpatialOverlap.SoupBounds(b.Model);
            return (hc.ConvexPlanesMismatch == 1 && hc.ConvexPlanesMatchHull == 0 && hc.ConvexPlanesMismatchShapes.Count == 1 && NearA7(bb.Max.Z, 7, 1e-3),
                    $"matched {hc.ConvexPlanesMatchHull} mismatched {hc.ConvexPlanesMismatch} named [{string.Join("; ", hc.ConvexPlanesMismatchShapes)}] maxZ {bb.Max.Z:0.###}");
        });

        // ================= K (radius default, 2026-10-03 convex-radius-default): every Havok radius, read by the physics drop =================
        // LI-K6: a box shape's surface is the box grown by its radius (Havok's definition; the render-bottom census and the lab step,
        // RESULT). The decoder with the radius on reads half 5 + radius 2 = [-7,7]^3; off [-5,5]^3. Rejects the box radius ignored.
        Case(check, "loose-items LI-K6: box shape, half 5 u, radius 2 u - radius on [-7,7]^3, off [-5,5]^3", () =>
        {
            var on = SpatialOverlap.SoupBounds(PhysicsReach.ModelBodies(FreeItemBoxRadius(5f, 2f), convexRadius: true).Single().Model);
            var off = SpatialOverlap.SoupBounds(PhysicsReach.ModelBodies(FreeItemBoxRadius(5f, 2f), convexRadius: false).Single().Model);
            return (NearA7(on.Max.Z, 7, 1e-3) && NearA7(on.Min.X, -7, 1e-3) && NearA7(off.Max.Z, 5, 1e-3) && NearA7(off.Min.X, -5, 1e-3),
                    $"on {on.Min.X:0.###}..{on.Max.Z:0.###} off {off.Min.X:0.###}..{off.Max.Z:0.###}");
        });
        // LI-K7: a compressed mesh's surface is its triangles grown by the mesh radius (lab: two salt piles and a resting control on
        // compressed-mesh supports read 0.35 / 0.34 / 0.28 u above the core, RESULT). A plate at z 0 with radius 0.35 reads
        // z -0.35..+0.35 with the radius on and 0 with it off. Rejects the mesh radius ignored.
        Case(check, "loose-items LI-K7: compressed-mesh plate at z 0, radius 0.35 u - radius on z -0.35..+0.35, off z 0", () =>
        {
            var on = SpatialOverlap.SoupBounds(PhysicsReach.ModelBodies(SupportMeshPlate(100f, 0.35f), convexRadius: true).Single().Model);
            var off = SpatialOverlap.SoupBounds(PhysicsReach.ModelBodies(SupportMeshPlate(100f, 0.35f), convexRadius: false).Single().Model);
            return (NearA7(on.Max.Z, 0.35, 1e-3) && NearA7(on.Min.Z, -0.35, 1e-3) && NearA7(off.Max.Z, 0, 1e-3) && NearA7(off.Min.Z, 0, 1e-3),
                    $"on z {on.Min.Z:0.####}..{on.Max.Z:0.####} off z {off.Min.Z:0.####}..{off.Max.Z:0.####}");
        });
        // LI-K8: the physics drop reads the radius without the rest rule too (the same engine drop; lab boots residual 2.37 -> 0.12,
        // RESULT): the radius-2 convex item over a floor 100 u down drops 93 with physics alone. Rejects the radius kept behind the
        // rest rule.
        Case(check, "loose-items LI-K8: physics alone (no rest) - the radius-2 item over a floor 100 u down drops d 93", () =>
        {
            var r = MeshReach.OfModel(FreeItemRadius(2f, 2f), Origin, Origin, 1f, null, RulesOn(physics: true, baseType: "MISC",
                supports: new[] { new DropSupport("floor", 1, true, false, Plate(500, -100)) }));
            return (r.Drop is { Status: "dropped" } d && NearA7(d.Distance, 93, 1e-3), ShowRR(r));
        });
        // LI-K9: a support's radius is read by the drop's supports by default: the radius-0 free item ([-5,5]^3) at the origin over a
        // box support (half 50 x 50 x 10, radius 7) centred 100 u down drops 100 - 5 - 10 - 7 = 78; over a compressed-mesh plate
        // (radius 0.35) at z -100 it drops 95 - 0.35 = 94.65. Rejects a support read without its radius.
        Case(check, "loose-items LI-K9: supports read their radius - box support (radius 7) d 78; mesh plate (radius 0.35) d 94.65", () =>
        {
            var box = PhysicsReach.Supports("box", SupportBox(50f, 10f, 7f), new Vec3(0, 0, -100), new Vec3(0, 0, 0), 1f, movableBase: false);
            var mesh = PhysicsReach.Supports("mesh", SupportMeshPlate(500f, 0.35f), new Vec3(0, 0, -100), new Vec3(0, 0, 0), 1f, movableBase: false);
            var rb = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RulesOn(physics: true, baseType: "MISC", supports: box));
            var rm = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RulesOn(physics: true, baseType: "MISC", supports: mesh));
            return (rb.Drop is { Status: "dropped" } a && NearA7(a.Distance, 78, 1e-3) && rm.Drop is { Status: "dropped" } b && NearA7(b.Distance, 94.65, 1e-3),
                    "box " + ShowRR(rb) + " | mesh " + ShowRR(rm));
        });
        // LI-K11: an item that starts inside a support's radius shell is touching it (the engine pushes it out; lab: the station (a)
        // control lifted +0.283 u out of the table's shell), never read as falling through to a surface below. The radius-0 free item
        // ([-5,5]^3) at the origin: over a box support (half 50 x 50 x 10, radius 7) centred at z -16 (core top -6, shell top +1);
        // over a compressed-mesh plate (radius 0.35) at z -5.2 (shell top -4.85); each over a floor 100 u down. Both read touching
        // on their support. Rejects a shell crossing at the placed pose read as a drop to the floor.
        Case(check, "loose-items LI-K11: an item inside a support's shell reads touching it - box (6 u in) and mesh plate (0.15 u in)", () =>
        {
            var floor = new DropSupport("floor", 1, true, false, Plate(500, -100));
            var box = PhysicsReach.Supports("box", SupportBox(50f, 10f, 7f), new Vec3(0, 0, -16), new Vec3(0, 0, 0), 1f, movableBase: false).Append(floor).ToArray();
            var mesh = PhysicsReach.Supports("mesh", SupportMeshPlate(500f, 0.35f), new Vec3(0, 0, -5.2f), new Vec3(0, 0, 0), 1f, movableBase: false).Append(floor).ToArray();
            var rb = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RulesOn(physics: true, baseType: "MISC", supports: box));
            var rm = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RulesOn(physics: true, baseType: "MISC", supports: mesh));
            return (rb.Drop is { Status: "lifted", SupportId: "box" } && rm.Drop is { Status: "lifted", SupportId: "mesh" }, "box " + ShowRR(rb) + " | mesh " + ShowRR(rm));
        });
        // LI-K12: a crossing the vertex test cannot see is still a contact: a small support (a 2 x 2 u compressed-mesh plate at z -2,
        // radius 0) lies wholly inside the free item's box ([-5,5]^3): no item vertex stands over the plate, and the plate's own
        // vertices are inside the item. That is touching (0), not a drop of 7 (the gap from the plate up to the item's top face). On
        // real data the radius produced this shape of reading (a flour sack 0.02 u over a shelf read 4.35 u, digest/reach-digest.json).
        // Rejects an interpenetration detected only by moving vertices over one support triangle.
        Case(check, "loose-items LI-K12: a small support wholly inside the item reads touching, not dropped 7", () =>
        {
            var plate = PhysicsReach.Supports("plate", SupportMeshPlate(1f, 0f), new Vec3(0, 0, -2), new Vec3(0, 0, 0), 1f, movableBase: false)
                                    .Append(new DropSupport("floor", 1, true, false, Plate(500, -100))).ToArray();
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RulesOn(physics: true, baseType: "MISC", supports: plate));
            return (r.Drop is { Status: "lifted", SupportId: "plate" }, ShowRR(r));
        });
        // LI-K13: a crossing seen only edge through face: a plank (a 200 x 2 u compressed-mesh strip at z -2, radius 0) passes through
        // the free item's box ([-5,5]^3): no item vertex stands over the plank and no plank vertex is inside the item, but the plank's
        // long edges pass through the item's side faces. Touching (0) on the plank, not a drop of 7. Rejects a crossing read by
        // vertices alone.
        Case(check, "loose-items LI-K13: a plank through the item (edges only) reads touching, not dropped 7", () =>
        {
            var plank = PhysicsReach.Supports("plank", SupportMeshRect(100f, 1f, 0f), new Vec3(0, 0, -2), new Vec3(0, 0, 0), 1f, movableBase: false)
                                    .Append(new DropSupport("floor", 1, true, false, Plate(500, -100))).ToArray();
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RulesOn(physics: true, baseType: "MISC", supports: plank));
            return (r.Drop is { Status: "lifted", SupportId: "plank" }, ShowRR(r));
        });
        // LI-K14: no silent wrong answer when the crossing test misses a shell: a drop read with the radius that is LONGER than the
        // same drop without it is refused by name with the radius-off drop (lane 2, 2026-10-13: was a label). Synthetic: a support whose
        // radius soup sits at -100 while its core soup sits at -10 (a long radius drop over a short core one) is refused with the reduced
        // drop 5; a support whose core soup is its radius soup is not refused. Rejects the long reading passed through, a label, and a
        // refusal on every drop.
        Case(check, "loose-items LI-K14: a drop longer with the radius than without it is refused (shell crossing) with the core drop", () =>
        {
            var lying = new[] { new DropSupport("s", 1, true, false, Plate(500, -100), CoreWorld: Plate(500, -10)) };
            var honest = new[] { new DropSupport("s", 1, true, false, Plate(500, -100), CoreWorld: Plate(500, -100)) };
            var a = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RulesOn(physics: true, baseType: "MISC", supports: lying));
            var b = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RulesOn(physics: true, baseType: "MISC", supports: honest));
            bool la = a.Status == "refused" && (a.Reason ?? "").StartsWith(PhysicsReach.ShellCrossingReason) && a.Drop is { } ad && NearA7(ad.ShellCore, 5, 1e-3);
            bool lb = b.Status == "refused" || a.Labels.Concat(b.Labels).Any(x => x.StartsWith(PhysicsReach.ShellCrossingLabel));
            return (la && !lb && a.Drop is { Status: "dropped" } d && NearA7(d.Distance, 95, 1e-3), "lying " + ShowRR(a) + " | honest " + ShowRR(b));
        });
        // ================= K (drop crossing, 2026-10-03): a body that starts overlapping a support at the placed pose =================
        // The overlap is resolved per convex component pair by its minimum translation: pushed up (n_z >= 0.5) is held (touching);
        // pushed sideways or down, that pair is left out and the body falls past it. Mesh slabs are closed prisms (rims drawn) with
        // the radius on. Expected values are analytic and were checked against the frozen independent model of this design (numpy,
        // separating axes over face normals and edge x edge products; the lane's gb_expect.py), which gives the same numbers.
        static string ShowDrop(DropResult r) => $"{r.Status} {r.Reason} d {r.Distance:0.######} support {r.SupportId}";
        var floor100 = new DropSupport("floor", 1, true, false, Plate(500, -100));
        // CANARY (wrong by construction; must stay RED before and after the fix): the LI-K15 item reads dropped 99 to the floor.
        Case(check, "loose-items CANARY K15 (must FAIL): the item inside the box's shell reads dropped 99 to the floor", () =>
        {
            var sup = PhysicsReach.Supports("box", SupportBox(50f, 10f, 7f), new Vec3(0, 0, -14), new Vec3(0, 0, 0), 1f, movableBase: false).Append(floor100).ToArray();
            var r = PhysicsReach.Drop(ClosedBox(-1, -1, -1, 1, 1, 1), AB(-1, -1, -1, 1, 1, 1), 4, sup, RRLayers);
            return (r.Status == "dropped" && r.SupportId == "floor" && NearA7(r.Distance, 99, 1e-3), ShowDrop(r));
        });
        // LI-K15 containment: a radius-0 item [-1,1]^3 wholly inside a box support's 7 u shell (box half 50 x 50 x 10, radius 7,
        // centre z -14: core top -4, shell z -31..+3), floor 100 u down. No surface crosses the item, so no crossing test sees it;
        // the overlap pushes it up by 4 (held) -> touching on the box. Rejects dropped 30 (the exit cast from inside the shell to
        // its underside, -1 - -31) and dropped 99 (to the floor).
        Case(check, "loose-items LI-K15: an item wholly inside a box support's shell reads touching on the box, not dropped 30 to its underside", () =>
        {
            var sup = PhysicsReach.Supports("box", SupportBox(50f, 10f, 7f), new Vec3(0, 0, -14), new Vec3(0, 0, 0), 1f, movableBase: false).Append(floor100).ToArray();
            var r = PhysicsReach.Drop(ClosedBox(-1, -1, -1, 1, 1, 1), AB(-1, -1, -1, 1, 1, 1), 4, sup, RRLayers);
            return (r.Status == "lifted" && r.SupportId == "box", ShowDrop(r));
        });
        // LI-K16 side clip, wall reaching below the floor: the item [-5,5]^3 clips a wall's 2 u shell sideways by 0.5 u (wall core
        // x 6.5..26.5, y +-50, z -200..+40; shell x 4.5..28.5, z -202..+42), floor 100 u down. The overlap pushes the item -x by 0.5
        // (not held): the wall pair is left out and the item falls 95 to the floor. Rejects dropped 63.269231 onto the wall (the item's
        // bottom edge, from inside the shell, meets the diagonal of the wall's inner side face 63.27 u below: a shorter drop no label
        // sees) and touching on the wall (every overlap held).
        Case(check, "loose-items LI-K16: a side clip on a wall that reaches below the floor - dropped 95 on the floor, not 63.27 onto the wall", () =>
        {
            var sup = new[] { new DropSupport("wall", 1, true, false, ClosedBox(4.5f, -52, -202, 28.5f, 52, 42)), floor100 };
            var r = PhysicsReach.Drop(ClosedBox(-5, -5, -5, 5, 5, 5), AB(-5, -5, -5, 5, 5, 5), 4, sup, RRLayers);
            return (r.Status == "dropped" && r.SupportId == "floor" && NearA7(r.Distance, 95, 1e-3), ShowDrop(r));
        });
        // LI-K16b side clip, wall ending above the floor: as LI-K16 with the wall core z -60..+40 (shell z -62..+42). Dropped 95 on the
        // floor. Rejects touching on the wall (the item's bottom edge already meets the diagonal of the wall's inner side face at the
        // placed pose) and dropped 57 onto the wall (the exit cast from inside the shell to its underside, -5 - -62).
        Case(check, "loose-items LI-K16b: a side clip on a wall that ends above the floor - dropped 95 on the floor, not touching the wall", () =>
        {
            var sup = new[] { new DropSupport("wall", 1, true, false, ClosedBox(4.5f, -52, -62, 28.5f, 52, 42)), floor100 };
            var r = PhysicsReach.Drop(ClosedBox(-5, -5, -5, 5, 5, 5), AB(-5, -5, -5, 5, 5, 5), 4, sup, RRLayers);
            return (r.Status == "dropped" && r.SupportId == "floor" && NearA7(r.Distance, 95, 1e-3), ShowDrop(r));
        });
        // LI-K17 separated only on an edge x edge axis: support = cube half 5 turned 45 deg about x (top edge along x at z 5*sqrt2);
        // body = cube half 5 turned 45 deg about y, its bottom edge (along y) 2 u above the support's top edge; then both turned 40 deg
        // about x. Their bounds meet and every face normal of either overlaps; only the edge x edge axis separates them (2 u along an
        // axis 40 deg off vertical). Dropped 2 / cos 40 = 2.610815 on the support. Rejects touching (a face-normal-only overlap test
        // reads an overlap and pushes it up: held).
        Case(check, "loose-items LI-K17: two boxes separated only on an edge x edge axis - dropped 2.610815 (2 / cos 40), not touching", () =>
        {
            var body = TurnedCube(5, aboutX: false, 45, 10 * Math.Sqrt(2) + 2, 40);
            var sup = new[] { new DropSupport("s", 1, true, false, TurnedCube(5, aboutX: true, 45, 0, 40)), floor100 };
            var r = PhysicsReach.Drop(body, SpatialOverlap.SoupBounds(body), 4, sup, RRLayers);
            return (r.Status == "dropped" && r.SupportId == "s" && NearA7(r.Distance, 2 / Math.Cos(40 * Math.PI / 180), 1e-3), ShowDrop(r));
        });
        // LI-K18 one mesh holding a floor and a wall (one compressed mesh, radius 0.35): a floor square half 100 at z 0 and a wall
        // x 5.2, y +-50, z 0..100 (two triangles each). The item [-5,5]^3 centred z 15 (bottom 10 u above the floor core) clips the
        // wall's slab (x 4.85..5.55) by 0.15. The wall's prisms push it -x (not held) and are left out; the floor's prisms do not
        // overlap it: dropped 10 - 0.35 = 9.65 on the mesh. Rejects the mesh read as one convex component (its hull holds the item
        // and pushes it down: nothing left below), and a cast that meets the wall slab from inside it.
        Case(check, "loose-items LI-K18: one mesh, floor and wall - an item clipping the wall's slab 10 u up drops 9.65 onto the floor's shell", () =>
        {
            var mv = new[] { new Vector3(-100, -100, 0), new Vector3(100, -100, 0), new Vector3(100, 100, 0), new Vector3(-100, 100, 0),
                             new Vector3(5.2f, -50, 0), new Vector3(5.2f, 50, 0), new Vector3(5.2f, 50, 100), new Vector3(5.2f, -50, 100) };
            var sup = PhysicsReach.Supports("mesh", SupportMesh(mv, new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 }, 0.35f), new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f, movableBase: false);
            var r = PhysicsReach.Drop(ClosedBox(-5, -5, 10, 5, 5, 20), AB(-5, -5, 10, 5, 5, 20), 4, sup, RRLayers);
            return (r.Status == "dropped" && r.SupportId == "mesh" && NearA7(r.Distance, 9.65, 1e-3), ShowDrop(r));
        });
        // LI-K19 a crease: one compressed mesh (radius 0.35) whose two faces meet at a ridge along y at z 0 and fall 45 deg to
        // x = -50 and x = +50. A radius-0 square pyramid points down at the ridge: tip (0,0,5), base half 1 at z 15 (its edges run
        // along x = +-y). The core drop is 5. With the slabs closed (rims drawn) the tip meets the rims at the ridge line: dropped 5.
        // Rejects 5 + r / cos 45 = 5.494975: the tip falling through the gap between the two offset top faces onto a lower face.
        Case(check, "loose-items LI-K19: a point over a mesh crease reads the core drop 5, not 5.494975 through the gap between offset faces", () =>
        {
            var rv = new[] { new Vector3(0, -50, 0), new Vector3(0, 50, 0), new Vector3(-50, 0, -50), new Vector3(50, 0, -50) };
            var sup = PhysicsReach.Supports("ridge", SupportMesh(rv, new[] { 0, 1, 2, 0, 3, 1 }, 0.35f), new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f, movableBase: false);
            var pyr = new TriangleSoup(new[] { new Vector3(0, 0, 5), new Vector3(1, 1, 15), new Vector3(-1, 1, 15), new Vector3(-1, -1, 15), new Vector3(1, -1, 15) },
                                       new[] { 0, 2, 1, 0, 3, 2, 0, 4, 3, 0, 1, 4, 1, 3, 4, 1, 2, 3 });
            var r = PhysicsReach.Drop(pyr, SpatialOverlap.SoupBounds(pyr), 4, sup, RRLayers);
            return (r.Status == "dropped" && r.SupportId == "ridge" && NearA7(r.Distance, 5, 1e-3), ShowDrop(r));
        });
        // LI-K20 an internal mesh edge is not an edge of the surface: one compressed mesh (radius 0.35), a flat square floor at z 0 made of
        // two triangles whose shared edge runs along x = 0 (corners (0,-50), (50,0), (0,50), (-50,0)). A thin body x -0.05..0.05,
        // y -5..5, z 0.15..10.15 lies across that edge, sunk 0.2 into the slab's top. Each triangle's prism could push it out sideways
        // by 0.05 through the shared edge's rim, but that edge has a coplanar neighbour on the other side: the only real way out is up
        // by 0.2 (held) -> touching on the mesh. Rejects both prisms pushing it sideways and the body falling 100.15 to the floor below.
        Case(check, "loose-items LI-K20: a body across the internal edge of a flat two-triangle floor reads touching, not pushed sideways through it", () =>
        {
            var fv = new[] { new Vector3(0, -50, 0), new Vector3(50, 0, 0), new Vector3(0, 50, 0), new Vector3(-50, 0, 0) };
            var sup = PhysicsReach.Supports("mesh", SupportMesh(fv, new[] { 0, 1, 2, 0, 2, 3 }, 0.35f), new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f, movableBase: false)
                                   .Append(floor100).ToArray();
            var body = ClosedBox(-0.05f, -5, 0.15f, 0.05f, 5, 10.15f);
            var r = PhysicsReach.Drop(body, SpatialOverlap.SoupBounds(body), 4, sup, RRLayers);
            return (r.Status == "lifted" && r.SupportId == "mesh", ShowDrop(r));
        });
        // LI-K23 an internal edge whose two copies straddle a rounding boundary (reinstated rule, lab 4, 2026-10-04): as LI-K20, but the
        // two triangles have vertices of their own and the shared edge sits at x 0.0004 in one and x 0.0006 in the other (2e-4 apart:
        // the same edge to any tolerance of 1e-3, but on either side of a 1e-3 rounding step). The body across it is held (touching on
        // the mesh). Rejects an edge match by rounded position keys, which reads the edge as a border and pushes the body sideways
        // through it to the floor 100.15 below.
        Case(check, "loose-items LI-K23: an internal edge whose copies straddle a rounding boundary is still internal - touching, not dropped 100.15", () =>
        {
            var fv = new[] { new Vector3(0.0004f, -50, 0), new Vector3(50, 0, 0), new Vector3(0.0004f, 50, 0),
                             new Vector3(0.0006f, -50, 0), new Vector3(0.0006f, 50, 0), new Vector3(-50, 0, 0) };
            var sup = PhysicsReach.Supports("mesh", SupportMesh(fv, new[] { 0, 1, 2, 3, 4, 5 }, 0.35f), new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f, movableBase: false)
                                   .Append(floor100).ToArray();
            var body = ClosedBox(-0.05f, -5, 0.15f, 0.05f, 5, 10.15f);
            var r = PhysicsReach.Drop(body, SpatialOverlap.SoupBounds(body), 4, sup, RRLayers);
            return (r.Status == "lifted" && r.SupportId == "mesh", ShowDrop(r));
        });
        // LI-K25 an internal edge whose two copies sit in neighbouring 1e-3 grid cells: as LI-K23 with the shared edge at x 0.0009 in one
        // triangle and x 0.0011 in the other (2e-4 apart, on either side of the cell boundary at 0.001; a rounded key would match these,
        // LI-K23 rejects that). The body across it is held (touching on the mesh). Rejects an edge search confined to the end's own
        // grid cell, which misses the copy and drops the body 100.15 to the floor.
        Case(check, "loose-items LI-K25: an internal edge whose copies sit in neighbouring grid cells is still internal - touching, not dropped 100.15", () =>
        {
            var fv = new[] { new Vector3(0.0009f, -50, 0), new Vector3(50, 0, 0), new Vector3(0.0009f, 50, 0),
                             new Vector3(0.0011f, -50, 0), new Vector3(0.0011f, 50, 0), new Vector3(-50, 0, 0) };
            var sup = PhysicsReach.Supports("mesh", SupportMesh(fv, new[] { 0, 1, 2, 3, 4, 5 }, 0.35f), new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f, movableBase: false)
                                   .Append(floor100).ToArray();
            var body = ClosedBox(-0.05f, -5, 0.15f, 0.05f, 5, 10.15f);
            var r = PhysicsReach.Drop(body, SpatialOverlap.SoupBounds(body), 4, sup, RRLayers);
            return (r.Status == "lifted" && r.SupportId == "mesh", ShowDrop(r));
        });
        // LI-K24 a downward push out of a down-facing slab is lab-verified (lab 4 L4-1, 2026-10-04): flagged pushed off, not "down
        // through a thin slab, unverified". A ceiling plate (mesh square half 50 at z 0 wound to face down, radius 0.35: slab z
        // -0.35..0.35) over a body x,y -5..5, z -10..-0.2 whose top is 0.15 into the slab from below. Out down (along the slab's own
        // normal) 0.15: pushed off, it falls 90 to the floor. Rejects the downward-slab flag on a down-facing slab.
        Case(check, "loose-items LI-K24: a body pushed down out of a down-facing slab - dropped 90, pushed off, not flagged down through a slab", () =>
        {
            var cv = new[] { new Vector3(-50, -50, 0), new Vector3(50, -50, 0), new Vector3(50, 50, 0), new Vector3(-50, 50, 0) };
            var sup = PhysicsReach.Supports("ceil", SupportMesh(cv, new[] { 0, 2, 1, 0, 3, 2 }, 0.35f), new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f, movableBase: false)
                                   .Append(floor100).ToArray();
            var body = ClosedBox(-5, -5, -10, 5, 5, -0.2f);
            var r = PhysicsReach.Drop(body, SpatialOverlap.SoupBounds(body), 4, sup, RRLayers);
            return (r.Status == "dropped" && r.SupportId == "floor" && NearA7(r.Distance, 90, 1e-3) && r.PushedOff && !r.DownThroughSlab,
                    ShowDrop(r) + $" pushedOff {r.PushedOff} downThroughSlab {r.DownThroughSlab}");
        });
        // LI-K21 the shell-crossing guard reads only drops with nothing pushed off: the free item [-5,5]^3 clips a wall's shell sideways
        // (shell x 4.5..28.5, z -62..42, as LI-K16b) whose core soup is given as a plate under the item at z -10, over a floor 100 u down.
        // The item is pushed off the wall and falls 95 to the floor; the reduced reading (core on core) is 5. Pushed off, so it is refused
        // as pushed off (lane 2, 2026-10-13: was the pushed-off label), not as a shell crossing, and carries no label. Rejects the guard
        // firing on a drop lengthened by a push-off.
        Case(check, "loose-items LI-K21: a pushed-off drop is refused as pushed off, not as a shell crossing, with no label", () =>
        {
            var sup = new[] { new DropSupport("wall", 1, true, false, ClosedBox(4.5f, -52, -62, 28.5f, 52, 42), CoreWorld: Plate(500, -10)),
                              new DropSupport("floor", 1, true, false, Plate(500, -100), CoreWorld: Plate(500, -100)) };
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RulesOn(physics: true, baseType: "MISC", supports: sup));
            bool shell = r.Labels.Any(x => x.StartsWith(PhysicsReach.ShellCrossingLabel)) || (r.Reason ?? "").StartsWith(PhysicsReach.ShellCrossingReason);
            bool pushed = r.Status == "refused" && (r.Reason ?? "").StartsWith(PhysicsReach.PushedOffReason) && !r.Labels.Contains(PhysicsReach.PushedOffLabel);
            return (!shell && pushed && r.Drop is { Status: "dropped", SupportId: "floor" } d && NearA7(d.Distance, 95, 1e-3), ShowRR(r));
        });
        // LI-K22 a downward push through a thin mesh slab is kept as read and flagged: a thin body (10 x 10 x 0.1, z -0.3..-0.2) lies in
        // the lower half of a mesh plate's slab (radius 0.35, z -0.35..0.35). Out up is 0.65, out down 0.15: pushed down, it falls
        // 99.7 to the floor below, flagged pushed off and down through a slab. Rejects the flag missing (the label would not be added).
        Case(check, "loose-items LI-K22: a downward push through a thin mesh slab - dropped 99.7, flagged pushed off and down through a slab", () =>
        {
            var sup = PhysicsReach.Supports("mesh", SupportMeshPlate(50f, 0.35f), new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f, movableBase: false).Append(floor100).ToArray();
            var body = ClosedBox(-5, -5, -0.3f, 5, 5, -0.2f);
            var r = PhysicsReach.Drop(body, SpatialOverlap.SoupBounds(body), 4, sup, RRLayers);
            return (r.Status == "dropped" && r.SupportId == "floor" && NearA7(r.Distance, 99.7, 1e-3) && r.PushedOff && r.DownThroughSlab,
                    ShowDrop(r) + $" pushedOff {r.PushedOff} downThroughSlab {r.DownThroughSlab}");
        });
        // LI-K10: the geometry reader every other consumer uses (route, floors, contact, overlap's collision column, state) keeps the
        // core: no radius of any kind (their calibrations; contact's in-game acceptance got worse with it, RESULT). Measured in Havok
        // units (havokUnitScale 1). Rejects the radius leaking into NifGeometry by default.
        Case(check, "loose-items LI-K10: NifGeometry keeps the core - convex top 5, box top 5, mesh plate top 0", () =>
        {
            float Top(byte[] b) => SpatialOverlap.SoupBounds(NifGeometry.Measure(b, includeTriangles: true, havokUnitScale: HavokU).Model!.CollisionSoupFor(x => x.Solid)!).Max.Z;
            float cv = Top(FreeItemRadius(2f, 2f)), bx = Top(FreeItemBoxRadius(5f, 2f)), ms = Top(SupportMeshPlate(100f, 0.35f));
            return (NearA7(cv, 5, 1e-2) && NearA7(bx, 5, 1e-2) && NearA7(ms, 0, 1e-3), $"convex top {cv:0.###} box top {bx:0.###} mesh top {ms:0.####}");
        });

        // ================= F: drops with the rebuilt body; a faceless body refused by name =================
        // LI-F1: the tilted-plane free item over a floor 100 u down drops d 95 (as the exact box, reach-rules A3). Rejects today's
        // "nothing below" (in the lab the game dropped such a shield to the floor the tool said was not there).
        Case(check, "loose-items LI-F1: the tilted-plane free item over a floor 100 u down — dropped d 95, support floor", () =>
        {
            var r = MeshReach.OfModel(FreeItemTiltedPlanes(), Origin, Origin, 1f, null, RulesOn(physics: true, baseType: "MISC", supports: new[] { new DropSupport("floor", 1, true, false, Plate(500, -100)) }));
            return (r.Drop is { } d && d.Status == "dropped" && NearA7(d.Distance, 95, 1e-3) && d.SupportId == "floor", ShowRR(r));
        });
        // LI-F2: Drop of a moving soup with vertices and no faces -> refused with NoFacesReason, not NothingBelow. Rejects a
        // faceless body read as "nothing below".
        Case(check, "loose-items LI-F2: a moving body with vertices and no faces — refused '" + PhysicsReach.NoFacesReason + "'", () =>
        {
            var moving = new TriangleSoup(Box(-5, -5, 45, 5, 5, 55).Vertices, Array.Empty<int>());
            var r = PhysicsReach.Drop(moving, AB(-5, -5, 45, 5, 5, 55), 4, new[] { new DropSupport("floor", 1, true, false, Plate(500, 0)) }, RRLayers);
            return (r.Status == "refused" && r.Reason == PhysicsReach.NoFacesReason, $"{r.Status} {r.Reason}");
        });

        // ================= T: which bases are loose items =================
        // LI-T1: the item types are loose; a carryable Light only under the rest rule; a Light without the flag, Flora and a
        // Static never. Rejects every Light admitted, Flora admitted, and a change with the rule off.
        Case(check, "loose-items LI-T1: loose bases — item types yes; Light only with Can Be Carried under the rest rule; Flora, Static no", () =>
        {
            bool[] got =
            {
                LooseItems.IsLooseBase("Ingestible", false, false), LooseItems.IsLooseBase("Key", false, true),
                LooseItems.IsLooseBase("Light", true, true), LooseItems.IsLooseBase("Light", true, false), LooseItems.IsLooseBase("Light", false, true),
                LooseItems.IsLooseBase("Flora", false, true), LooseItems.IsLooseBase("Static", false, true), LooseItems.IsLooseBase(null, false, true),
            };
            bool[] want = { true, true, true, false, false, false, false, false };
            return (got.SequenceEqual(want), $"[{string.Join(",", got)}] want [{string.Join(",", want)}]");
        });

        // ================= R: the rest rule (Rest on) =================
        ReachOptions RestOn(SettleDecision? d, IEnumerable<DropSupport> s, bool rest = true) => new(0.5, 65536, new ReachRules(Physics: true, BaseRecordType: "MISC",
            Supports: _ => s, Layers: RRLayers, Settle: d, Rest: rest));
        var above = new[] { new DropSupport("above", 1, true, false, Plate(500, 100)) };          // only a ceiling: nothing below
        var floor30 = new[] { new DropSupport("floor", 1, true, false, Plate(500, -35)) };        // box bottom -5: gap 30
        var exFlag = new SettleExemption("refRecordFlag", "0x20000000", "lab");
        var exTime = new SettleExemption("enabledAfterLoad", "true", "lab");
        // LI-R1: settles, nothing below, Rest on -> no_geometry, drop "lost", the lost label. Rejects the refusal (today).
        Case(check, "loose-items LI-R1: settles with nothing below, rest on — no_geometry, drop 'lost', '" + PhysicsReach.LostLabel[..24] + "…'", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RestOn(new SettleDecision("settles", null), above));
            return (r.Status == "no_geometry" && r.Drop is { Status: "lost" } && (r.Reason == PhysicsReach.LostLabel || r.Labels.Contains(PhysicsReach.LostLabel)), ShowRR(r));
        });
        // LI-R1b: the same with Rest off -> today's refusal, unchanged. Rejects a change without the flag.
        Case(check, "loose-items LI-R1b: settles with nothing below, rest off — refused '" + PhysicsReach.NothingBelow + "' (unchanged)", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RestOn(new SettleDecision("settles", null), above, rest: false));
            return (r.Status == "refused" && r.Reason == PhysicsReach.NothingBelow, ShowRR(r));
        });
        // LI-R2: rests (Don't Havok Settle) 30 u over a floor, Rest on -> the placed box, rests label, drop "rest-gap" d 30 on the
        // floor (recorded, not applied). Rejects no gap, and the drop applied to an exempt item.
        Case(check, "loose-items LI-R2: an exempt item 30 u over a floor — placed box [-5,5]^3, rests label, drop 'rest-gap' d 30 support floor", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RestOn(new SettleDecision("rests", exFlag), floor30));
            return (r.Labels.Contains(LoadSettle.RestsLabel) && BoxIs(r.Box, -5, -5, -5, 5, 5, 5) && r.Drop is { Status: "rest-gap" } d && NearA7(d.Distance, 30, 1e-3) && d.SupportId == "floor", ShowRR(r));
        });
        // LI-R2b: the same with Rest off -> today's answer (no drop). Rejects a change without the flag.
        Case(check, "loose-items LI-R2b: the exempt item, rest off — no drop recorded (unchanged)", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RestOn(new SettleDecision("rests", exFlag), floor30, rest: false));
            return (r.Labels.Contains(LoadSettle.RestsLabel) && r.Drop is null && BoxIs(r.Box, -5, -5, -5, 5, 5, 5), ShowRR(r));
        });
        // LI-R3: rests with nothing below -> the placed box, drop "floats", the floats label. Rejects refused and lost (an exempt
        // item does not fall: in the lab a Don't Havok Settle item over empty space held its placed height).
        Case(check, "loose-items LI-R3: an exempt item over nothing — placed box, drop 'floats', '" + PhysicsReach.FloatsLabel[..22] + "…'", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RestOn(new SettleDecision("rests", exFlag), above));
            return (r.Status == "uncertified" && BoxIs(r.Box, -5, -5, -5, 5, 5, 5) && r.Drop is { Status: "floats" } && r.Labels.Contains(PhysicsReach.FloatsLabel), ShowRR(r));
        });
        // LI-R5 (Gate B review F2): a settling item over a floor 100 u down, rest on -> dropped d 95 with tilt margin
        // m = sqrt(3 x 5^2) - 5 = 3.6603; region = prism plus the landed box grown by m sideways and 2m upward (bottom on the
        // floor): x,y -8.6603..8.6603, z -100..max(5, -90 + 7.3205). Rejects no margin, and a margin pushed below the support.
        Case(check, "loose-items LI-R5: a settling item over a floor, rest on — d 95, tilt margin 3.6603, region x ±8.6603, bottom -100, tilt label", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RestOn(new SettleDecision("settles", null), new[] { new DropSupport("floor", 1, true, false, Plate(500, -100)) }));
            double m = Math.Sqrt(75) - 5;
            return (r.Drop is { Status: "dropped" } d && NearA7(d.Distance, 95, 1e-3) && NearA7(d.TiltMargin, m, 1e-3) && NearA7(r.Box.Max.X, 5 + m, 1e-3)
                    && NearA7(r.Box.Min.Y, -5 - m, 1e-3) && NearA7(r.Box.Min.Z, -100, 1e-3) && NearA7(r.Box.Max.Z, 5, 1e-3) && r.Labels.Contains(MeshReach.TiltLabel),
                    ShowRR(r) + $" tilt {r.Drop?.TiltMargin:0.####}");
        });
        // LI-R6: the rest rule records how far the render mesh reaches below the moving collision at the placed pose: the free
        // item's render and collision both bottom at -5 -> 0; the same item with its render box reaching 3 u lower -> 3.
        // Rejects the field missing (null) and a depth read from the union box (always 0).
        Case(check, "loose-items LI-R6: renderBelowCollision — 0 for the matching box, 3 for a render box 3 u deeper", () =>
        {
            var fl = new[] { new DropSupport("floor", 1, true, false, Plate(500, -100)) };
            var a = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RestOn(new SettleDecision("settles", null), fl));
            var b = MeshReach.OfModel(FreeItemDeepRender(), Origin, Origin, 1f, null, RestOn(new SettleDecision("settles", null), fl));
            return (a.Drop is { } da && NearA7(da.RenderBelowCollision, 0, 1e-3) && b.Drop is { } db && NearA7(db.RenderBelowCollision, 3, 1e-3),
                    $"a {a.Drop?.RenderBelowCollision} b {b.Drop?.RenderBelowCollision}");
        });
        // LI-R4: enable timing unknown with nothing below -> the placed box (enabled after load, it rests there), drop "lost",
        // the lost and timing labels. Rejects one reading only.
        Case(check, "loose-items LI-R4: enable timing unknown over nothing — placed box, drop 'lost', lost + timing labels", () =>
        {
            var r = MeshReach.OfModel(FreeItem(), Origin, Origin, 1f, null, RestOn(new SettleDecision("timing-unknown", exTime), above));
            return (r.Status == "uncertified" && BoxIs(r.Box, -5, -5, -5, 5, 5, 5) && r.Drop is { Status: "lost" } && r.Labels.Contains(PhysicsReach.LostLabel)
                    && r.Labels.Contains(LoadSettle.TimingUnknownLabel), ShowRR(r));
        });
    }
}
