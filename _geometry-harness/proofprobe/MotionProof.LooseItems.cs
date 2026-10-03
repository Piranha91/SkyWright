using System.Numerics;
using HousecarlCore;
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
        // LI-K4: the decoder's default leaves the radius off (in-game calibrations read the core hull): collision bounds [-5,5],
        // ConvexRadiusApplied 0, while the planes are still checked (matched 1). Rejects the radius applied by default.
        Case(check, "loose-items LI-K4: default decode — radius not applied (bounds [-5,5]^3, applied 0), planes still checked (matched 1)", () =>
        {
            var hc = HavokCollision.Decode(Load(FreeItemRadius(2f, 2f)), true).Stats;
            var bb = SpatialOverlap.SoupBounds(PhysicsReach.ModelBodies(FreeItemRadius(2f, 2f)).Single().Model);
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
