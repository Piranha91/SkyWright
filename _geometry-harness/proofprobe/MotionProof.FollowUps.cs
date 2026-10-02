using System.Numerics;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

/// <summary>Known answers for the animated motion follow-ups (task 2026-10-01-animated-motion-follow-ups, decisions E1-E4,
/// Gate A review F1-F4): MW* the wind label (item 1), MR* the rest pose from the placement (item 3), MB*/MHP*/MCP* two moving
/// members and held poses (item 4). Fixtures reuse MotionProof's rigs; each case names the wrong answer it rejects.
/// "fixture control" cases hold on the base; every other case is red until the follow-up code exists.</summary>
static partial class MotionProof
{
    static readonly ModKey FxMod = new("MotionFollowUps", ModType.Master);
    static FormKey Fx(uint id) => new(FxMod, id);
    static readonly FormKey FxCell = Fx(0x800), FxDoorBase = Fx(0x801), RA = Fx(0x810), RB = Fx(0x811), RL = Fx(0x812), RT = Fx(0x813), RC = Fx(0x814);

    static PlacedRef FxRef(FormKey key, FormKey basis, bool open = false, FormKey? enableParent = null,
                           IReadOnlyList<PlacedLink>? links = null, IReadOnlyList<PlacedActivateParent>? parents = null) =>
        new(key, "PlacedObject", null, default, default, 1, basis, false, false, false,
            EnableParent: enableParent, LinkedReferences: links, ActivateParents: parents, OpenByDefault: open);

    static OverlapIndex FxIndex(params (int Plugin, PlacedRef Ref)[] records)
    {
        var idx = new OverlapIndex();
        idx.BaseInfo[FxDoorBase] = ("Door", "FixtureDoor");
        foreach (var (plugin, r) in records) idx.Record(plugin, FxCell, FormKey.Null, r);
        return idx;
    }

    /// <summary>The Frame shape's shader (lighting or effect), Tree_Anim on or off, optional vertex colours whose alpha is
    /// the given list (red = 1 - alpha, green 0.5, blue 0.25: no other channel matches the alpha count).</summary>
    static void WindShader(NifFile f, string shapeName, bool treeAnim, bool effect = false, float[]? alphas = null, bool hidden = false)
    {
        var shape = f.GetShapes().First(x => x.Name?.String == shapeName);
        var flags = SkyrimShaderPropertyFlags2.ZBuffer_Write | (treeAnim ? SkyrimShaderPropertyFlags2.Tree_Anim : 0);
        int si = effect ? f.AddBlock(new BSEffectShaderProperty { ShaderFlags_SSPF2 = flags })
                        : f.AddBlock(new BSLightingShaderProperty { ShaderFlags_SSPF2 = flags });
        if (shape is BSTriShape bt)
        {
            bt.ShaderPropertyRef = new NiBlockRef<BSShaderProperty>(si);
            if (alphas is not null) bt.SetVertexColors(alphas.Select(a => new Color4 { R = 1 - a, G = 0.5f, B = 0.25f, A = a }).ToList());
            if (hidden) bt.Flags_ui |= 1;
        }
        else if (shape is NiTriShape nt)
        {
            nt.ShaderPropertyRef = new NiBlockRef<BSShaderProperty>(si);
            if (alphas is not null && nt.GeometryData is { } gd) gd.VertexColors = alphas.Select(a => new Color4 { R = 1 - a, G = 0.5f, B = 0.25f, A = a }).ToList();
            if (hidden) nt.Flags_ui |= 1;
        }
    }

    // b0445359's SweptGeometry.Volume on the MV1 fixture (recorded at Gate B; see MV1)
    static readonly (double Lower, double Upper) MV1Expected20k = (0, 165023.55337107647), MV1Expected200k = (0, 156681.9184578296);

    static readonly float[] FrameAlphas = { 0, 0, 0, 1, 0.5f, 0, 0, 0 };   // 8 box vertices: 2 above 0, largest 1

    /// <summary>A pre-SSE (stream 83) mesh: one NiTriShape "Bush" with NiTriShapeData, 4 vertices, alphas 0, 0.25, 0, 0.75.</summary>
    static byte[] LeBush(bool treeAnim)
    {
        var f = new NifFile(); f.Create(VerLE, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Flags_ui = 0xE;
        var verts = new List<Vector3> { new(0, 0, 0), new(10, 0, 0), new(10, 10, 0), new(0, 10, 5) };
        var geom = new NiTriShapeData { Vertices = verts, HasVertices = true, Triangles = new List<Triangle> { new(0, 1, 2), new(0, 2, 3) } };
        geom.VertexColors = new[] { 0f, 0.25f, 0f, 0.75f }.Select(a => new Color4 { R = 1 - a, G = 0.5f, B = 0.25f, A = a }).ToList();
        var tri = new NiTriShape { Name = new NiStringRef("Bush"), Flags_ui = 0xE, Scale = 1f };
        tri.DataRef = new(f.AddBlock(geom));
        root.Children.AddBlockRef(f.AddBlock(tri));
        WindShader(f, "Bush", treeAnim);   // colours are set on the data block above (GeometryData is not linked before save)
        return Save(f, "le bush");
    }

    // ---- item 4 fixtures: translating boxes (CLAMP 0..1 s unless LOOP) ----
    static NiTransformData Slide(Vector3 to) => Xyz(NoKeys(), NoKeys(), NoKeys(), VG(KeyType.LINEAR_KEY, (0f, Vector3.Zero), (1f, to)));
    // A: box x 0..10, y 0..10, z 0..10 moving +x by 100; placed at the origin.
    static byte[] MoverA(CycleType cycle = CycleType.CYCLE_CLAMP) => Rig(Box(0, 0, 0, 10, 10, 10), Vector3.Zero, Slide(new(100, 0, 0)), cycle: cycle);
    // B is placed at (1000, 0, 0) so its static Frame cube stays far from A; its part is defined 1000 u back in x.
    static readonly Vec3 BAt = new(1000, 0, 0);
    // B2: world column x 50..60 moving +y from y -100..-90 to 100..110 (crosses A's lane only mid-way, as A does).
    static byte[] MoverBCross(CycleType cycle = CycleType.CYCLE_CLAMP) => Rig(Box(-950, -100, 0, -940, -90, 10), Vector3.Zero, Slide(new(0, 200, 0)), cycle: cycle);
    // B2b: the same column moving +y by 100 only: its end pose (held) sits in A's lane at y 0..10.
    static byte[] MoverBEnter() => Rig(Box(-950, -100, 0, -940, -90, 10), Vector3.Zero, Slide(new(0, 100, 0)));
    // B5: a box beside A's lane, 0.6 u away in y (y 10.6..20.6), moving +x by 100 too: never crosses.
    static byte[] MoverBBeside() => Rig(Box(-1000, 10.6f, 0, -990, 20.6f, 10), Vector3.Zero, Slide(new(100, 0, 0)));
    // B1: far away (y 500).
    static byte[] MoverBFar() => Rig(Box(-1000, 500, 0, -990, 510, 10), Vector3.Zero, Slide(new(100, 0, 0)));

    sealed record Placed(SweptRegion Region, IReadOnlyList<HeldSoup> Held);
    static Placed PlaceMover(byte[] nif, Vec3 at, double delta = 0.5)
    {
        var m = NifMotion.Read(nif, null);
        var r = SweptGeometry.Build(nif, m, null, VolumeGeometry.Render, new MotionOptions(DeltaTarget: delta, SampleBudget: 65536));
        return new(SweptGeometry.Place(r, at, default, 1), SweptGeometry.HeldSoups(nif, m, VolumeGeometry.Render, at, default, 1));
    }
    static SweptPairDecision Both(Placed a, Placed b, string? coupling = null, double delta = 0.5, long budget = SweptGeometry.DefaultPairBudget) =>
        SweptGeometry.ContactBoth(a.Region, a.Held, b.Region, b.Held, coupling, new MotionOptions(DeltaTarget: delta), budget);
    static string D(SweptPairDecision d) =>
        $"{d.Decision} A {d.AnimationA}@{d.TimeA} pose {d.PoseA}; B {d.AnimationB}@{d.TimeB} pose {d.PoseB}; width {d.Width:0.###}; coupling {d.Coupling}; reason {d.Reason}";

    public static void RunFollowUps(Action<string, bool, string> check)
    {
        var pivot = new Vector3(5, 0, 0);
        var leaf = Box(0, -2, 0, 40, 2, 80);

        // ======================= item 1: wind label (E1) =======================
        byte[] windRig = Rig(leaf, pivot, null, extra: (f, root) => WindShader(f, "Frame", true, alphas: FrameAlphas));
        byte[] plainRig = Rig(leaf, pivot, null, extra: (f, root) => WindShader(f, "Frame", false, alphas: FrameAlphas));
        Case(check, "follow-up fixture control (wind): Frame round-trips Tree_Anim and 8 colours (alpha bytes 0,0,0,255,128,0,0,0; red differs)", () =>
        {
            var n = Load(windRig);
            var fr = (BSTriShape)n.GetShapes().First(x => x.Name?.String == "Frame");
            bool flag = n.GetShader(fr) is BSLightingShaderProperty l && l.ShaderFlags_SSPF2.HasFlag(SkyrimShaderPropertyFlags2.Tree_Anim);
            var a = fr.VertexColors.Select(c => (int)Math.Round(c.A * 255)).ToArray();
            var r = fr.VertexColors.Select(c => (int)Math.Round(c.R * 255)).ToArray();
            var le = Load(LeBush(true)).GetShapes().OfType<NiTriShape>().Single();
            var leA = le.GeometryData?.VertexColors?.Select(c => c.A).ToArray() ?? Array.Empty<float>();
            return (flag && fr.HasVertexColors && a.SequenceEqual(new[] { 0, 0, 0, 255, 128, 0, 0, 0 }) && !r.SequenceEqual(a) && leA.Length == 4 && leA[3] > 0.7f,
                    $"flag {flag} colours {fr.HasVertexColors} alpha [{string.Join(",", a)}] red [{string.Join(",", r)}] LE alphas [{string.Join(",", leA)}]");
        });
        Case(check, "MW1 wind: the Tree_Anim Frame is listed with 8 vertices, 2 with alpha > 0, largest alpha 1 (rejects the red channel: 7 above 0)", () =>
        {
            var s = NifWind.Shapes(windRig);
            var fr = s.SingleOrDefault();
            return (s.Count == 1 && fr is { Name: "Frame", Shader: "BSLightingShaderProperty", Vertices: 8, Colours: true, VerticesAlphaAboveZero: 2 } && Math.Abs(fr.AlphaMax - 1) < 1e-6,
                    string.Join("; ", s));
        });
        Case(check, "MW1c wind: the same rig without Tree_Anim lists nothing and has no label (rejects labelling every coloured shape)", () =>
        {
            var s = NifWind.Shapes(plainRig);
            return (s.Count == 0 && NifWind.Label(plainRig) is null, string.Join("; ", s));
        });
        Case(check, "MW2 wind: Tree_Anim on a BSEffectShaderProperty is listed with its shader class (rejects reading only the lighting shader)", () =>
        {
            var s = NifWind.Shapes(Rig(leaf, pivot, null, extra: (f, root) => WindShader(f, "Frame", true, effect: true, alphas: FrameAlphas)));
            return (s.Count == 1 && s[0].Shader == "BSEffectShaderProperty" && s[0].VerticesAlphaAboveZero == 2, string.Join("; ", s));
        });
        Case(check, "MW4 wind: with Part unflagged and Frame flagged only Frame is named, at its GetShapes index (rejects labelling the whole mesh)", () =>
        {
            var s = NifWind.Shapes(windRig);
            int frameIndex = Load(windRig).GetShapes().ToList().FindIndex(x => x.Name?.String == "Frame");
            return (s.Count == 1 && s[0].Name == "Frame" && s[0].ShapeIndex == frameIndex, $"{string.Join("; ", s)} frame index {frameIndex}");
        });
        Case(check, "MW5 wind: a flagged shape with no vertex colours is listed with Colours false and 0 / 0 (rejects dropping it or inventing alpha)", () =>
        {
            var s = NifWind.Shapes(Rig(leaf, pivot, null, extra: (f, root) => WindShader(f, "Frame", true)));
            return (s.Count == 1 && !s[0].Colours && s[0].VerticesAlphaAboveZero == 0 && s[0].AlphaMax == 0, string.Join("; ", s));
        });
        Case(check, "MW6 wind: a hidden flagged shape (the static readers leave it out) is not listed", () =>
        {
            var s = NifWind.Shapes(Rig(leaf, pivot, null, extra: (f, root) => WindShader(f, "Frame", true, alphas: FrameAlphas, hidden: true)));
            return (s.Count == 0, string.Join("; ", s));
        });
        Case(check, "MW7 wind: pre-SSE NiTriShape colours are read from NiTriShapeData (2 of 4 above 0, largest 0.75)", () =>
        {
            var s = NifWind.Shapes(LeBush(true));
            return (s.Count == 1 && s[0].Colours && s[0].VerticesAlphaAboveZero == 2 && Math.Abs(s[0].AlphaMax - 0.75) < 1e-6, string.Join("; ", s));
        });
        Case(check, "MW8 wind label (F4): flag Tree_Anim, the shapes, and the fixed note; the note states file facts only (no number, no 'still', 'static', 'does not move')", () =>
        {
            var l = NifWind.Label(windRig);
            string note = NifWind.Note;
            bool factsOnly = !note.Any(char.IsDigit) && !note.Contains("still", StringComparison.OrdinalIgnoreCase) && !note.Contains("static", StringComparison.OrdinalIgnoreCase)
                             && !note.Contains("does not move", StringComparison.OrdinalIgnoreCase) && note.Contains("Tree_Anim", StringComparison.Ordinal);
            return (l is { Flag: "Tree_Anim" } && l.Shapes.Count == 1 && l.Note == note && factsOnly, $"{l?.Flag} {l?.Shapes.Count} '{l?.Note}'");
        });
        Case(check, "MW9 the motion reader and the wind reader agree on the flag (NifWind.IsTreeAnim on the Frame's shader; NifMotion refuses 'wind:')", () =>
        {
            var n = Load(windRig);
            bool flag = NifWind.IsTreeAnim(n.GetShader(n.GetShapes().First(x => x.Name?.String == "Frame")));
            var m = NifMotion.Read(windRig, null);
            return (flag && m.Status == "refused" && (m.Reason ?? "").Contains("wind", StringComparison.Ordinal), $"flag {flag} motion {m.Status} {m.Reason}");
        });

        // ======================= item 2: the non-solid octree's answer is unchanged by the cost fix (E2) =======================
        // An open plate (two triangles) swinging 90 deg about Z: the region is not a closed solid, so the octree refines near
        // its surface to the cell budget. The expected intervals are b0445359's own output for these exact inputs (a
        // regression oracle, recorded at Gate B): a faster near test must give the same doubles.
        var plateOpen = new TriangleSoup(new[] { new Vector3(10, 0, 0), new Vector3(60, 0, 0), new Vector3(60, 0, 50), new Vector3(10, 0, 50) }, new[] { 0, 1, 2, 0, 2, 3 });
        byte[] swingOpen = Rig(plateOpen, Vector3.Zero, Xyz(FG(KeyType.LINEAR_KEY, FK(0, 0)), FG(KeyType.LINEAR_KEY, FK(0, 0)), FG(KeyType.LINEAR_KEY, FK(0, 0), FK(1, Pi / 2))));
        foreach (long cellsBudget in new long[] { 20_000, 200_000 })
        {
            long cb = cellsBudget;
            Case(check, $"MV1 non-solid octree identity: the open swinging plate at MaxCells {cb:N0} gives b0445359's interval exactly (rejects a near test that changes any cell's answer)", () =>
            {
                var m = NifMotion.Read(swingOpen, null);
                var r = SweptGeometry.Build(swingOpen, m, null, VolumeGeometry.Render);
                var v = SweptGeometry.Volume(r, null, new VolumeOptions(MaxCells: cb));
                var want = cb == 20_000 ? MV1Expected20k : MV1Expected200k;
                return (!r.Solid && v.Swept is { } s && s.Lower == want.Lower && s.Upper == want.Upper,
                        $"solid {r.Solid} [{v.Swept?.Lower:R}, {v.Swept?.Upper:R}] want [{want.Lower:R}, {want.Upper:R}] {v.Reason}");
            });
        }

        // ======================= item 3: rest pose from the placement (E3) =======================
        byte[] door = Rig(leaf, pivot, Xyz(FG(KeyType.LINEAR_KEY, FK(0, 0)), FG(KeyType.LINEAR_KEY, FK(0, 0)), FG(KeyType.QUADRATIC_KEY, FK(0, 0), FK(1, -Pi / 2))),
                          second: Xyz(FG(KeyType.LINEAR_KEY, FK(0, 0)), FG(KeyType.LINEAR_KEY, FK(0, 0)), FG(KeyType.QUADRATIC_KEY, FK(0, -Pi / 2), FK(1, 0))));
        Case(check, "MR1 rest from the placement: a door reference with IsOpenByDefault gives MotionPlacement(DOOR, true) and rest = open, labelled per D3 (rejects the flag ignored)", () =>
        {
            var p = MotionPlacements.Of(FxIndex((0, FxRef(RA, FxDoorBase, open: true))), RA);
            var rest = p is null ? null : NifMotion.Read(door, null, p).Poses.SingleOrDefault(x => x.Rest);
            return (p is { BaseRecordType: "DOOR", IsOpenByDefault: true } && rest?.Name == "open" && rest.Label.Contains("IsOpenByDefault", StringComparison.Ordinal)
                    && rest.Label.Contains("a script can change it at load", StringComparison.Ordinal), $"{p} rest {rest?.Name} '{rest?.Label}'");
        });
        Case(check, "MR2 rest from the placement: without the flag, MotionPlacement(DOOR, false) and rest = closed (rejects rest always open)", () =>
        {
            var p = MotionPlacements.Of(FxIndex((0, FxRef(RA, FxDoorBase))), RA);
            var rest = p is null ? null : NifMotion.Read(door, null, p).Poses.SingleOrDefault(x => x.Rest);
            return (p is { BaseRecordType: "DOOR", IsOpenByDefault: false } && rest?.Name == "closed", $"{p} rest {rest?.Name}");
        });
        Case(check, "MR3 the winning record decides: master open, override closed -> closed; master closed, override open -> open (rejects first-plugin-wins)", () =>
        {
            var p1 = MotionPlacements.Of(FxIndex((0, FxRef(RA, FxDoorBase, open: true)), (1, FxRef(RA, FxDoorBase))), RA);
            var p2 = MotionPlacements.Of(FxIndex((0, FxRef(RA, FxDoorBase)), (1, FxRef(RA, FxDoorBase, open: true))), RA);
            return (p1 is { IsOpenByDefault: false } && p2 is { IsOpenByDefault: true }, $"{p1} / {p2}");
        });
        Case(check, "MR4 PlacementExtract reads ONAM: of three written doors only the IsOpenByDefault one reads open (one persistent, one initially disabled; rejects another header bit)", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "proofprobe-rest-" + Environment.ProcessId);
            Directory.CreateDirectory(dir);
            var mod = new SkyrimMod(FxMod, SkyrimRelease.SkyrimSE);
            mod.Doors.Add(new Door(FxDoorBase, SkyrimRelease.SkyrimSE) { EditorID = "FixtureDoor" });
            var cell = new Cell(FxCell, SkyrimRelease.SkyrimSE) { EditorID = "FixtureCell", Flags = Cell.Flag.IsInteriorCell };
            PlacedObject P(FormKey k, float x) { var r = new PlacedObject(k, SkyrimRelease.SkyrimSE) { Placement = new Placement { Position = new(x, 0, 0), Rotation = new(0, 0, 0) } }; r.Base.SetTo(FxDoorBase); return r; }
            var open = P(RA, 0); open.IsOpenByDefault = true;
            var pers = P(RB, 100);   // placed in the persistent group
            var dis = P(RC, 200); dis.SkyrimMajorRecordFlags |= SkyrimMajorRecord.SkyrimMajorRecordFlag.InitiallyDisabled;
            cell.Temporary.Add(open); cell.Temporary.Add(dis); cell.Persistent.Add(pers);
            var block = new CellBlock { BlockNumber = (int)(FxCell.ID % 10), GroupType = GroupTypeEnum.InteriorCellBlock };
            var sub = new CellSubBlock { BlockNumber = (int)((FxCell.ID / 10) % 10), GroupType = GroupTypeEnum.InteriorCellSubBlock };
            sub.Cells.Add(cell); block.SubBlocks.Add(sub); mod.Cells.Records.Add(block);
            var path = Path.Combine(dir, FxMod.FileName.String);
            mod.BeginWrite.ToPath(path).WithNoLoadOrder().Write();
            using var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            var refs = PlacementExtract.FromMod(ov).SelectMany(c => c.Refs).ToDictionary(r => r.FormKey);
            bool ok = refs.Count == 3 && refs[RA].OpenByDefault && !refs[RB].OpenByDefault && !refs[RC].OpenByDefault && refs[RC].InitiallyDisabled && refs[RB].Persistent;
            return (ok, string.Join("; ", refs.Values.Select(r => $"{r.FormKey.ID:X} open {r.OpenByDefault} pers {r.Persistent} dis {r.InitiallyDisabled}")));
        });

        // ======================= item 4: held poses and two moving members (E4, F1) =======================
        Case(check, "MHP1 held poses of a CLAMP door: rest (closed), open, and each CLAMP sequence's end; no time strictly inside an animation (rejects mid-animation 'held')", () =>
        {
            var m = NifMotion.Read(door, null, new MotionPlacement("DOOR", false));
            var h = NifMotion.HeldPoses(m);
            bool hasRest = h.Any(p => p.Rest && p.Name == "closed"), hasOpen = h.Any(p => p.Name == "open"), hasEnds = h.Any(p => p.Name == "seq:Open@end") && h.Any(p => p.Name == "seq:Close@end");
            bool noMid = h.All(p => p.Time <= 0 || p.Time >= 1);
            return (hasRest && hasOpen && hasEnds && noMid, string.Join(", ", h.Select(p => $"{p.Name}={p.AnimationId}@{p.Time}")));
        });
        Case(check, "MHP2 held poses of a LOOP mover: none (a looping animation never holds a time; rejects @start/@end of a loop as held)", () =>
        {
            var h = NifMotion.HeldPoses(NifMotion.Read(MoverA(CycleType.CYCLE_LOOP), null));
            return (h.Count == 0, string.Join(", ", h.Select(p => p.Name)));
        });
        Case(check, "follow-up fixture control (two movers): A@0.5 meets B-cross@0.5 and B-enter@1; A@0.5 misses B-cross@0 and @1; A@0 and A@1 miss B-cross@0.5; A@0.5 misses B-beside@0.5", () =>
        {
            TriangleSoup At(byte[] nif, float t, Vec3 at)
            {
                var m = NifMotion.Read(nif, null);
                var p = NifMotion.Pose(nif, m, "seq:Open", t, VolumeGeometry.Render);
                var part = new TriangleSoup(p.Soup!.Vertices, p.Soup.Indices);
                return SpatialOverlap.TransformSoup(part, at, default, 1);
            }
            bool Meets(TriangleSoup x, TriangleSoup y) => MeshVolume.Measure(x, y, VolumeGeometry.Render).Intersection is { Upper: > 0 };
            var a5 = At(MoverA(), 0.5f, default);
            bool ok = Meets(a5, At(MoverBCross(), 0.5f, BAt)) && Meets(a5, At(MoverBEnter(), 1f, BAt))
                      && !Meets(a5, At(MoverBCross(), 0f, BAt)) && !Meets(a5, At(MoverBCross(), 1f, BAt))
                      && !Meets(At(MoverA(), 0f, default), At(MoverBCross(), 0.5f, BAt)) && !Meets(At(MoverA(), 1f, default), At(MoverBCross(), 0.5f, BAt))
                      && !Meets(a5, At(MoverBBeside(), 0.5f, BAt));
            return (ok, "posed with the existing NifMotion.Pose, intersected with MeshVolume.Measure");
        });
        Case(check, "MB1 two movers far apart: never", () =>
        {
            var d = Both(PlaceMover(MoverA(), default), PlaceMover(MoverBFar(), BAt));
            return (d.Decision == "never", D(d));
        });
        Case(check, "MB2 two movers that cross only when both are mid-animation: cannot_tell naming tA and tB (about 0.5 each) and the clocks (rejects a shared clock's witness at t = 0.5)", () =>
        {
            var d = Both(PlaceMover(MoverA(), default), PlaceMover(MoverBCross(), BAt));
            return (d.Decision == "cannot_tell" && d.TimeA is > 0.35f and < 0.65f && d.TimeB is > 0.4f and < 0.6f
                    && (d.Reason ?? "").Contains("clocks", StringComparison.Ordinal), D(d));
        });
        Case(check, "MB2b B held at its end pose sits in A's lane: witness, A mid-way (t in 0.35..0.65), B at a held pose, labelled independent clocks (rejects B taken at its stored pose: never)", () =>
        {
            var d = Both(PlaceMover(MoverA(), default), PlaceMover(MoverBEnter(), BAt));
            return (d.Decision == "witness" && d.TimeA is > 0.35f and < 0.65f && d.PoseB is not null && d.PoseA is null
                    && (d.Reason ?? "").Contains(SweptGeometry.IndependentClocks, StringComparison.Ordinal), D(d));
        });
        Case(check, "MB4 coupling found in the files: the MB2b crossing is cannot_tell naming the coupling, not a witness (rejects links ignored)", () =>
        {
            var d = Both(PlaceMover(MoverA(), default), PlaceMover(MoverBEnter(), BAt), coupling: "both name activate parent X");
            return (d.Decision == "cannot_tell" && d.Coupling == "both name activate parent X" && (d.Reason ?? "").Contains("activate parent X", StringComparison.Ordinal), D(d));
        });
        Case(check, "MB4b never needs no link check: far-apart movers with a coupling are still never", () =>
        {
            var d = Both(PlaceMover(MoverA(), default), PlaceMover(MoverBFar(), BAt), coupling: "linked");
            return (d.Decision == "never", D(d));
        });
        Case(check, "MB5 a 0.6 u gap: cannot_tell at delta 0.5 (width > 0.6), never at delta 0.1 (rejects growing by one member's delta only)", () =>
        {
            var coarse = Both(PlaceMover(MoverA(), default, 0.5), PlaceMover(MoverBBeside(), BAt, 0.5));
            var fine = Both(PlaceMover(MoverA(), default, 0.1), PlaceMover(MoverBBeside(), BAt, 0.1), delta: 0.1);
            return (coarse.Decision == "cannot_tell" && coarse.Width > 0.6 && fine.Decision == "never", $"coarse {D(coarse)} | fine {D(fine)}");
        });
        Case(check, "MB6 symmetry: MB1, MB2, MB2b, MB5 with A and B swapped give the same decisions and swapped sides", () =>
        {
            var a = PlaceMover(MoverA(), default);
            var far = PlaceMover(MoverBFar(), BAt); var cross = PlaceMover(MoverBCross(), BAt); var enter = PlaceMover(MoverBEnter(), BAt);
            var a1 = PlaceMover(MoverA(), default, 0.5); var beside = PlaceMover(MoverBBeside(), BAt, 0.5);
            var s1 = Both(far, a); var s2 = Both(cross, a); var s3 = Both(enter, a); var s5 = Both(beside, a1);
            bool ok = s1.Decision == "never" && s2.Decision == "cannot_tell" && s2.TimeB is > 0.35f and < 0.65f
                      && s3.Decision == "witness" && s3.PoseA is not null && s3.TimeB is > 0.35f and < 0.65f && s5.Decision == "cannot_tell";
            return (ok, $"{D(s1)} | {D(s2)} | {D(s3)} | {D(s5)}");
        });
        Case(check, "MB7 two LOOP movers crossing mid-way: cannot_tell (no held pose); a LOOP A against B held at its end: witness", () =>
        {
            var loops = Both(PlaceMover(MoverA(CycleType.CYCLE_LOOP), default), PlaceMover(MoverBCross(CycleType.CYCLE_LOOP), BAt));
            var loopHeld = Both(PlaceMover(MoverA(CycleType.CYCLE_LOOP), default), PlaceMover(MoverBEnter(), BAt));
            return (loops.Decision == "cannot_tell" && loopHeld.Decision == "witness", $"{D(loops)} | {D(loopHeld)}");
        });
        Case(check, "MB9 the pair budget is deterministic: budget 10 on MB5's coarse pair is cannot_tell naming the budget, never 'never'", () =>
        {
            var d = Both(PlaceMover(MoverA(), default), PlaceMover(MoverBBeside(), BAt), budget: 10);
            return (d.Decision == "cannot_tell" && (d.Reason ?? "").Contains("budget", StringComparison.Ordinal) && d.PairsExamined <= 10, D(d));
        });
        // coupling (link scope, F1)
        Case(check, "MCP1 coupling: both references name one activate parent (a common lever) -> named, with the lever", () =>
        {
            var lever = new[] { new PlacedActivateParent(RL, 0) };
            var c = MotionPlacements.Coupling(FxIndex((0, FxRef(RA, FxDoorBase, parents: lever)), (0, FxRef(RB, FxDoorBase, parents: lever))), RA, RB);
            return (c is not null && c.Contains("activate parent", StringComparison.Ordinal) && c.Contains(RL.ToString(), StringComparison.Ordinal), c ?? "null");
        });
        Case(check, "MCP2 coupling: both link to one target (XLKR) -> named; a direct XLKR A->B -> named; XESP B under A -> named", () =>
        {
            var t = new[] { new PlacedLink(RT, null) };
            var c1 = MotionPlacements.Coupling(FxIndex((0, FxRef(RA, FxDoorBase, links: t)), (0, FxRef(RB, FxDoorBase, links: t))), RA, RB);
            var c2 = MotionPlacements.Coupling(FxIndex((0, FxRef(RA, FxDoorBase, links: new[] { new PlacedLink(RB, null) })), (0, FxRef(RB, FxDoorBase))), RA, RB);
            var c3 = MotionPlacements.Coupling(FxIndex((0, FxRef(RA, FxDoorBase)), (0, FxRef(RB, FxDoorBase, enableParent: RA))), RA, RB);
            return (c1 is not null && c1.Contains(RT.ToString(), StringComparison.Ordinal) && c2 is not null && c3 is not null, $"{c1} | {c2} | {c3}");
        });
        Case(check, "MCP3 coupling: different activate parents -> null; the winning override dropping the shared parent -> null (rejects any-version links)", () =>
        {
            var c1 = MotionPlacements.Coupling(FxIndex((0, FxRef(RA, FxDoorBase, parents: new[] { new PlacedActivateParent(RL, 0) })),
                                                       (0, FxRef(RB, FxDoorBase, parents: new[] { new PlacedActivateParent(RC, 0) }))), RA, RB);
            var lever = new[] { new PlacedActivateParent(RL, 0) };
            var c2 = MotionPlacements.Coupling(FxIndex((0, FxRef(RA, FxDoorBase, parents: lever)), (0, FxRef(RB, FxDoorBase, parents: lever)), (1, FxRef(RA, FxDoorBase))), RA, RB);
            return (c1 is null && c2 is null, $"{c1 ?? "null"} | {c2 ?? "null"}");
        });
    }
}
