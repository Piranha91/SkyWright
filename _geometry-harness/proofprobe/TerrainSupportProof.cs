using System.Numerics;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

// PROOF BY CONSTRUCTION — TERRAIN SUPPORT (_shared/TerrainContactIndex.cs, DetectorModel.TerrainSupport; design
// Research/TERRAIN-SUPPORT-DESIGN-2026-10-18.md §8). Cases K1..K17 (numbers fixed by the design) and the variants K2-b, K5-b,
// K6-b, K10-u, K15-b (Gate A, 2026-10-20). One synthetic order written with Mutagen: a master NAMED Skyrim.esm (PluginKinds
// decides "Bethesda" by file name) defines the worldspace, every case's cell (k, 0) with flat LAND at z 0, and the bases; each
// case adds its own plugins. Geometry is injected per base by construction: collision boxes, a render-only box, a movable crate.
// Every case's expected answer was written before the rule (GATE-A.md §2).
static class TerrainSupportProof
{
    const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    static readonly ModKey SkyK = new("Skyrim", ModType.Master);
    static readonly ModKey DgK = new("Dawnguard", ModType.Master);
    static FormKey S(uint id) => new(SkyK, id);
    static readonly FormKey World = S(0x800), Top = S(0x801);
    static FormKey C(int k) => S((uint)(0x900 + k));
    static FormKey L(int k) => S((uint)(0xA00 + k));
    // bases (model space: centred on x, y; z from 0 up)
    static readonly FormKey BoxC = S(0xB01), RockC = S(0xB02), RockR = S(0xB03), Plank = S(0xB04), Sign = S(0xB05), Crate = S(0xB06),
                            Small = S(0xB07), Wide = S(0xB08), MarkerP = S(0xB09), Big = S(0xB0A), EffectR = S(0xB0B), FoliageR = S(0xB0C);
    static readonly Dictionary<FormKey, (Vector3 Half, float Height, char Src, string Motion)> Shapes = new()
    {
        [BoxC] = (new(16, 16, 0), 32, 'c', "MO_SYS_FIXED"),
        [RockC] = (new(128, 128, 0), 32, 'c', "MO_SYS_FIXED"),
        [RockR] = (new(128, 128, 0), 32, 'r', ""),
        [Plank] = (new(128, 8, 0), 8, 'c', "MO_SYS_FIXED"),
        [Sign] = (new(16, 4, 0), 32, 'c', "MO_SYS_FIXED"),
        [Crate] = (new(16, 16, 0), 32, 'c', "MO_SYS_BOX_INERTIA"),
        [Small] = (new(4, 4, 0), 8, 'c', "MO_SYS_FIXED"),
        [Wide] = (new(100, 16, 0), 32, 'c', "MO_SYS_FIXED"),
        [Big] = (new(1024, 1024, 0), 256, 'c', "MO_SYS_FIXED"),
        [EffectR] = (new(16, 16, 0), 32, 'r', ""),
        [FoliageR] = (new(16, 16, 0), 32, 'r', ""),
    };
    // the drawn render shapes' material classes of the render-only bases (what MeshSurface.ModelSurface reports for a real mesh)
    static readonly Dictionary<FormKey, string[]> Classes = new() { [RockR] = new[] { "opaque" }, [EffectR] = new[] { "effect" }, [FoliageR] = new[] { "alpha_test" } };
    static IReadOnlyCollection<string>? ClassesOf(FormKey b) => Classes.TryGetValue(b, out var c) ? c : null;
    static float CX(int k) => k * 4096f + 2048f;
    const float CY = 2048f;
    static readonly Vec3 PlankRot = new(20f * MathF.PI / 180f, 0f, 30f * MathF.PI / 180f);

    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nPROOF — TERRAIN SUPPORT (_shared/TerrainContactIndex.cs, DetectorModel.TerrainSupport)");
        Facts(Check);
        var root = Path.Combine(Path.GetTempPath(), "proofprobe-ts-" + Environment.ProcessId);
        Directory.CreateDirectory(root);
        try { Order(Check, root); }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // ---- the rule on facts ------------------------------------------------------------------------------------------
    static void Facts(Action<string, bool, string> Check)
    {
        var sight = DetectorModel.TerrainProvenance(new(1, 2, false, false, false));
        Check("TS-facts: unrelated owner and winner -> Blind", sight == TerrainSight.Blind, sight.ToString());
        sight = DetectorModel.TerrainProvenance(new(1, 2, false, true, false));
        Check("TS-facts: the owner in the winner's closure -> Seen", sight == TerrainSight.Seen, sight.ToString());
        sight = DetectorModel.TerrainProvenance(new(1, 2, false, false, true));
        Check("TS-facts: a carrier saw the winner's LAND -> Seen", sight == TerrainSight.Seen, sight.ToString());
        sight = DetectorModel.TerrainProvenance(new(2, 1, true, false, false));
        Check("TS-facts: the winner in the owner's closure -> Unchanged", sight == TerrainSight.Unchanged, sight.ToString());
        sight = DetectorModel.TerrainProvenance(new(2, 2, false, false, false));
        Check("TS-facts: the owner is the winner -> Unchanged", sight == TerrainSight.Unchanged, sight.ToString());
        Check("TS-facts: Bethesda on Bethesda -> Inherited", DetectorModel.TerrainPopulationOf(true, true, false, false) == TerrainPopulation.Inherited, "");
        Check("TS-facts: either side USSEP -> Ussep", DetectorModel.TerrainPopulationOf(true, false, false, true) == TerrainPopulation.Ussep
                                                    && DetectorModel.TerrainPopulationOf(false, true, true, false) == TerrainPopulation.Ussep, "");
        Check("TS-facts: a mod on either side -> Mod", DetectorModel.TerrainPopulationOf(true, false, false, false) == TerrainPopulation.Mod, "");

        var rest = new DetectorModel.TerrainDiagonal(true, 0f, true, 0f, 0f);
        DetectorModel.TerrainSupportFacts F(DetectorModel.TerrainDiagonal a, DetectorModel.TerrainDiagonal b, bool onRef = false, float refGap = float.PositiveInfinity, string? refusal = null)
            => new(a, b, onRef, refGap, refusal);
        (TerrainVerdict V, float G, float B, string W) V(DetectorModel.TerrainSupportFacts f) => DetectorModel.TerrainSupport(f, 4f, 8f, 8f, 0.25f);
        var fl = rest with { WinningClearance = 64f };
        Check("TS-facts: 64 u over the ground under both diagonals -> Floating 64", V(F(fl, fl)) is { V: TerrainVerdict.Floating } r1 && MathF.Abs(r1.G - 64f) < 0.01f, V(F(fl, fl)).W);
        var bu = rest with { WinningClearance = -64f, Burial = 64f, Cover = DetectorModel.TerrainCover.Uniform(64f) };
        Check("TS-facts: covered 64 u deeper -> Buried 64", V(F(bu, bu)) is { V: TerrainVerdict.Buried } r2 && MathF.Abs(r2.B - 64f) < 0.01f, V(F(bu, bu)).W);
        var part = new DetectorModel.TerrainCover(); part.Add(0.1, 64f); part.Add(0.9, 0f);
        var bp = rest with { WinningClearance = -64f, Burial = 64f, Cover = part };
        Check("TS-facts: covered 64 u deep over 0.10 of the surface in view, share 0.25 -> not Buried", V(F(bp, bp)).V != TerrainVerdict.Buried, V(F(bp, bp)).W);
        string Throws(Action a) { try { a(); return "no refusal"; } catch (Exception ex) { return ex.GetType().Name; } }
        var noProfile = rest with { WinningClearance = -64f, Burial = 64f };
        Check("TS-facts: a burial over the threshold without its surface profile -> refused, never guessed",
              Throws(() => V(F(noProfile, noProfile))) == nameof(InvalidOperationException), Throws(() => V(F(noProfile, noProfile))));
        Check("TS-facts: the burial share is never defaulted (NaN refused)",
              Throws(() => DetectorModel.TerrainSupport(F(bu, bu), 4f, 8f, 8f, float.NaN)) == nameof(ArgumentOutOfRangeException), "");
        Check("TS-facts: effect only -> not an object; effect + opaque, alpha test, nothing -> an object",
              DetectorModel.TerrainEffectOnly(new[] { "effect" }) && DetectorModel.TerrainEffectOnly(new[] { "alpha_blend", "no_depth_write" })
              && !DetectorModel.TerrainEffectOnly(new[] { "effect", "opaque" }) && !DetectorModel.TerrainEffectOnly(new[] { "alpha_test" })
              && !DetectorModel.TerrainEffectOnly(Array.Empty<string>()), "");
        var sunk = rest with { WinningClearance = -20f, Burial = 0f };
        Check("TS-facts: below the ground but no deeper than authored -> Resting", V(F(sunk, sunk)).V == TerrainVerdict.Resting, V(F(sunk, sunk)).W);
        Check("TS-facts: the diagonals disagree -> CannotTell", V(F(rest, fl)).V == TerrainVerdict.CannotTell, V(F(rest, fl)).W);
        Check("TS-facts: floating over the ground, held by a reference -> Resting", V(F(fl, fl, onRef: true)).V == TerrainVerdict.Resting, V(F(fl, fl, true)).W);
        Check("TS-facts: a reference 30 u below, float threshold 8 -> Floating 30", V(F(fl, fl, refGap: 30f)) is { V: TerrainVerdict.Floating } r3 && MathF.Abs(r3.G - 30f) < 0.01f, V(F(fl, fl, refGap: 30f)).W);
        var none = rest with { WinningGround = false, WinningClearance = float.PositiveInfinity };
        Check("TS-facts: no winning ground and nothing else -> Floating over no ground", V(F(none, none)) is { V: TerrainVerdict.Floating } r4 && float.IsPositiveInfinity(r4.G), V(F(none, none)).W);
        Check("TS-facts: an unreadable LAND -> Refused by name", V(F(rest, rest, refusal: "LAND unreadable")) is { V: TerrainVerdict.Refused } r5 && r5.W.Contains("unreadable"), V(F(rest, rest, refusal: "LAND unreadable")).W);
        var auth = DetectorModel.TerrainAuthoredRest(F(rest, rest), 4f);
        Check("TS-facts: within ε of the authored ground under both diagonals -> rested as authored", auth == TerrainAuthored.Resting, auth.ToString());
        auth = DetectorModel.TerrainAuthoredRest(F(rest with { AuthoredClearance = 200f }, rest with { AuthoredClearance = 200f }), 4f);
        Check("TS-facts: 200 u above the authored ground -> not resting as authored", auth == TerrainAuthored.NotResting, auth.ToString());
        auth = DetectorModel.TerrainAuthoredRest(F(rest, rest with { AuthoredClearance = 30f }), 4f);
        Check("TS-facts: the authored diagonals disagree -> CannotTell as authored", auth == TerrainAuthored.CannotTell, auth.ToString());
        auth = DetectorModel.TerrainAuthoredRest(F(rest with { AuthoredGround = false, AuthoredClearance = float.PositiveInfinity }, rest with { AuthoredGround = false, AuthoredClearance = float.PositiveInfinity }), 4f);
        Check("TS-facts: no authored ground -> not resting as authored", auth == TerrainAuthored.NotResting, auth.ToString());
    }

    // ---- fixture writers --------------------------------------------------------------------------------------------
    static Landscape LandFrom(FormKey key, Func<int, int, float> height)
    {
        var g = new sbyte[33, 33];
        float prevRow = 0f;
        for (int y = 0; y < 33; y++)
        {
            float row0 = height(0, y) / 8f;
            g[0, y] = checked((sbyte)MathF.Round(row0 - prevRow)); prevRow = row0;
            float prev = row0;
            for (int x = 1; x < 33; x++) { float h = height(x, y) / 8f; g[x, y] = checked((sbyte)MathF.Round(h - prev)); prev = h; }
        }
        return new Landscape(key, Rel) { VertexHeightMap = new LandscapeVertexHeightMap { Offset = 0f, HeightMap = new Array2d<sbyte>(g) } };
    }
    static Landscape Flat(FormKey key, float h) =>
        new(key, Rel) { VertexHeightMap = new LandscapeVertexHeightMap { Offset = h / 8f, HeightMap = new Array2d<sbyte>(new sbyte[33, 33]) } };
    static Landscape NoGround(FormKey key) => new(key, Rel) { Flags = (Landscape.Flag)0 };
    static Landscape UnreadableLand(FormKey key) => new(key, Rel) { Flags = Landscape.Flag.VertexNormalsHeightMap };
    static Cell Cell(int k, Landscape? land, params PlacedObject[] refs)
    {
        var c = new Cell(C(k), Rel) { EditorID = $"TS{k}", Flags = default, Grid = new CellGrid { Point = new P2Int(k, 0) }, Landscape = land };
        foreach (var r in refs) c.Temporary.Add(r);
        return c;
    }
    static Worldspace WorldOf(params Cell[] cells)
    {
        var ws = new Worldspace(World, Rel) { EditorID = "TsWorld" };
        ws.TopCell = new Cell(Top, Rel) { Flags = default };
        var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
        var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
        foreach (var c in cells) sub.Items.Add(c);
        blk.Items.Add(sub); ws.SubCells.Add(blk);
        return ws;
    }
    static PlacedObject Ref(FormKey key, FormKey baseKey, float x, float y, float z, Vec3 rot = default, bool disabled = false, FormKey? parent = null)
    {
        var r = new PlacedObject(key, Rel) { Placement = new Placement { Position = new P3Float(x, y, z), Rotation = new P3Float(rot.X, rot.Y, rot.Z) } };
        r.Base.SetTo(baseKey);
        if (disabled) r.SkyrimMajorRecordFlags |= SkyrimMajorRecord.SkyrimMajorRecordFlag.InitiallyDisabled;
        if (parent is { } pk) { r.EnableParent = new EnableParent(); r.EnableParent.Reference.SetTo(pk); }
        return r;
    }
    /// <summary>proofprobe's MarkDeletedInPlace, for records an override carries under a later master index: the Deleted header
    /// flag set on REFR records whose low 24 FormID bits match, subrecords kept. Returns how many were marked.</summary>
    static int MarkDeletedInPlace(string path, params FormKey[] keys)
    {
        var buf = File.ReadAllBytes(path);
        var ids = keys.Select(k => k.ID).ToHashSet();
        int marked = 0;
        void Walk(int off, int end)
        {
            while (off + 24 <= end)
            {
                int size = (int)BitConverter.ToUInt32(buf, off + 4);
                if (buf[off] == (byte)'G' && buf[off + 1] == (byte)'R' && buf[off + 2] == (byte)'U' && buf[off + 3] == (byte)'P')
                { Walk(off + 24, off + size); off += size; continue; }
                if (buf[off] == (byte)'R' && buf[off + 1] == (byte)'E' && buf[off + 2] == (byte)'F' && buf[off + 3] == (byte)'R'
                    && ids.Contains(BitConverter.ToUInt32(buf, off + 12) & 0xFFFFFFu) && size > 0)
                { BitConverter.GetBytes(BitConverter.ToUInt32(buf, off + 8) | 0x20u).CopyTo(buf, off + 8); marked++; }
                off += 24 + size;
            }
        }
        Walk(0, buf.Length);
        File.WriteAllBytes(path, buf);
        return marked;
    }

    sealed record Read(ContactIndex Cidx, CellBoundaryIndex Land, IReadOnlyList<HashSet<int>> Closure, List<string> Names);

    static Read ReadOrder(IReadOnlyList<string> paths)
    {
        ContactIndex? cidx = null;
        cidx = new ContactIndex(k => Geometry(cidx!, k));
        var land = new CellBoundaryIndex();
        var keys = new List<ModKey>(); var masterIdx = new List<IReadOnlySet<int>>(); var names = new List<string>();
        for (int p = 0; p < paths.Count; p++)
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], Rel);
            keys.Add(mod.ModKey); names.Add(mod.ModKey.FileName.String);
            var m = mod.ModHeader.MasterReferences.Select(x => x.Master).ToHashSet();
            masterIdx.Add(Enumerable.Range(0, p).Where(q => m.Contains(keys[q])).ToHashSet());
            cidx.Index.Ingest(p, mod);
            land.Ingest(p, mod);
        }
        return new Read(cidx, land, DetectorModel.MasterClosure(masterIdx), names);
    }

    static RefGeometry? Geometry(ContactIndex cidx, FormKey baseKey)
    {
        if (!Shapes.TryGetValue(baseKey, out var s)) return null;
        var box = new Aabb(new Vec3(-s.Half.X, -s.Half.Y, 0), new Vec3(s.Half.X, s.Half.Y, s.Height), false);
        var soup = ContactIndex.BoxSoup(box);
        var bodies = s.Src == 'c' ? new List<(string, string)> { (s.Motion == "MO_SYS_FIXED" ? "SKYL_STATIC" : "SKYL_CLUTTER", s.Motion) } : new List<(string, string)>();
        return new RefGeometry(box, soup, soup, s.Src, s.Src, HostUsable: s.Src == 'c', bodies, false);
    }

    // ---- the order --------------------------------------------------------------------------------------------------
    static void Order(Action<string, bool, string> Check, string root)
    {
        var opened = new Dictionary<ModKey, ISkyrimModGetter>();
        var paths = new List<string>();
        string Save(SkyrimMod mod, params ModKey[] masters)
        {
            var path = Path.Combine(root, mod.ModKey.FileName.String);
            var ms = masters.Select(k => opened[k]).ToArray();
            mod.BeginWrite.ToPath(path).WithLoadOrder(ms).WithExtraIncludedMasters(masters).Write();
            opened[mod.ModKey] = SkyrimMod.CreateFromBinaryOverlay(path, Rel);
            paths.Add(path);
            return path;
        }
        SkyrimMod Mod(string name) => new(new ModKey(name, ModType.Plugin), Rel);
        FormKey K(SkyrimMod m, uint n) => new(m.ModKey, 0x800 + n);
        var keys = new Dictionary<string, FormKey>();

        try
        {
            // Skyrim.esm: the world, cells 1..24 flat at z 0, the bases; K7's box placed by Bethesda itself.
            var sky = new SkyrimMod(SkyK, Rel);
            foreach (var (b, e) in new[] { (BoxC, "TsBox"), (RockC, "TsRock"), (RockR, "TsRockRender"), (Plank, "TsPlank"), (Sign, "TsSign"), (Small, "TsSmall"), (Wide, "TsWide"), (Big, "TsBig"), (EffectR, "TsEffectPlane"), (FoliageR, "TsFoliage") })
                sky.Statics.Add(new Static(b, Rel) { EditorID = e });
            sky.Statics.Add(new Static(MarkerP, Rel) { EditorID = "TsMarker", MajorRecordFlagsRaw = (int)EnableState.IsMarkerFlag });
            sky.MiscItems.Add(new MiscItem(Crate, Rel) { EditorID = "TsCrate" });
            var k7box = S(0xC07); keys["K7"] = k7box;
            var cells = Enumerable.Range(1, 28).Select(k => Cell(k, Flat(L(k), 0f), k == 10 ? new[] { Ref(k7box, BoxC, CX(10), CY, 0) } : Array.Empty<PlacedObject>())).ToArray();
            sky.Worldspaces.Add(WorldOf(cells));
            Save(sky);

            // Dawnguard.esm (masters Skyrim): K7 — a DLC lowers cell 10 by 64 under Bethesda's box.
            var dg = new SkyrimMod(DgK, Rel);
            dg.Worldspaces.Add(WorldOf(Cell(10, Flat(L(10), -64f))));
            Save(dg, SkyK);

            // K1: A edits cell 1 to z 16 and places a box on it; unrelated B raises it 64 (to z 80).
            var k1a = Mod("TsK1A"); keys["K1"] = K(k1a, 1);
            k1a.Worldspaces.Add(WorldOf(Cell(1, Flat(L(1), 16f), Ref(keys["K1"], BoxC, CX(1), CY, 16f)))); Save(k1a, SkyK);
            var k1b = Mod("TsK1B"); k1b.Worldspaces.Add(WorldOf(Cell(1, Flat(L(1), 80f)))); Save(k1b, SkyK);

            // K2: a box on Skyrim's ground in cell 2; unrelated B lowers it 64.
            var k2a = Mod("TsK2A"); keys["K2"] = K(k2a, 1);
            k2a.Worldspaces.Add(WorldOf(Cell(2, null, Ref(keys["K2"], BoxC, CX(2), CY, 0)))); Save(k2a, SkyK);
            var k2b = Mod("TsK2B"); k2b.Worldspaces.Add(WorldOf(Cell(2, Flat(L(2), -64f)))); Save(k2b, SkyK);

            // K2-b: as K2, lowered 6 u: the terrains agree within one heightmap unit.
            var k2ba = Mod("TsK2bA"); keys["K2b"] = K(k2ba, 1);
            k2ba.Worldspaces.Add(WorldOf(Cell(3, null, Ref(keys["K2b"], BoxC, CX(3), CY, 0)))); Save(k2ba, SkyK);
            var k2bb = Mod("TsK2bB"); k2bb.Worldspaces.Add(WorldOf(Cell(3, Flat(L(3), -6f)))); Save(k2bb, SkyK);

            // K3: as K2 plus a collision rock (top at z 0) under the box; K4: the rock is render only.
            var k3a = Mod("TsK3A"); keys["K3"] = K(k3a, 1); keys["K3rock"] = K(k3a, 2);
            k3a.Worldspaces.Add(WorldOf(Cell(4, null, Ref(keys["K3"], BoxC, CX(4), CY, 0), Ref(keys["K3rock"], RockC, CX(4), CY, -32f)))); Save(k3a, SkyK);
            var k3b = Mod("TsK3B"); k3b.Worldspaces.Add(WorldOf(Cell(4, Flat(L(4), -64f)))); Save(k3b, SkyK);
            var k4a = Mod("TsK4A"); keys["K4"] = K(k4a, 1);
            k4a.Worldspaces.Add(WorldOf(Cell(5, null, Ref(keys["K4"], BoxC, CX(5), CY, 0), Ref(K(k4a, 2), RockR, CX(5), CY, -32f)))); Save(k4a, SkyK);
            var k4b = Mod("TsK4B"); k4b.Worldspaces.Add(WorldOf(Cell(5, Flat(L(5), -64f)))); Save(k4b, SkyK);

            // K5: B masters A and lowers the cell.
            var k5a = Mod("TsK5A"); keys["K5"] = K(k5a, 1);
            k5a.Worldspaces.Add(WorldOf(Cell(6, null, Ref(keys["K5"], BoxC, CX(6), CY, 0)))); Save(k5a, SkyK);
            var k5b = Mod("TsK5B"); k5b.Worldspaces.Add(WorldOf(Cell(6, Flat(L(6), -64f)))); Save(k5b, SkyK, k5a.ModKey);

            // K5-b: A places, unrelated B lowers, C masters A and B and repeats the box's position (no placement step).
            var k5ba = Mod("TsK5bA"); keys["K5b"] = K(k5ba, 1);
            k5ba.Worldspaces.Add(WorldOf(Cell(7, null, Ref(keys["K5b"], BoxC, CX(7), CY, 0)))); Save(k5ba, SkyK);
            var k5bb = Mod("TsK5bB"); k5bb.Worldspaces.Add(WorldOf(Cell(7, Flat(L(7), -64f)))); Save(k5bb, SkyK);
            var k5bc = Mod("TsK5bC"); k5bc.Worldspaces.Add(WorldOf(Cell(7, null, Ref(keys["K5b"], BoxC, CX(7), CY, 0)))); Save(k5bc, SkyK, k5ba.ModKey, k5bb.ModKey);

            // K6: B raises cell 8 to 64; A masters B and places its box on B's ground.
            var k6b = Mod("TsK6B"); k6b.Worldspaces.Add(WorldOf(Cell(8, Flat(L(8), 64f)))); Save(k6b, SkyK);
            var k6a = Mod("TsK6A"); keys["K6"] = K(k6a, 1);
            k6a.Worldspaces.Add(WorldOf(Cell(8, null, Ref(keys["K6"], BoxC, CX(8), CY, 64f)))); Save(k6a, SkyK, k6b.ModKey);

            // K6-b: B lowers cell 9 and loads first; A does NOT master B and places its box on Skyrim's ground.
            var k6bb = Mod("TsK6bB"); k6bb.Worldspaces.Add(WorldOf(Cell(9, Flat(L(9), -64f)))); Save(k6bb, SkyK);
            var k6ba = Mod("TsK6bA"); keys["K6b"] = K(k6ba, 1);
            k6ba.Worldspaces.Add(WorldOf(Cell(9, null, Ref(keys["K6b"], BoxC, CX(9), CY, 0)))); Save(k6ba, SkyK);

            // K8: a plank tilted 20° about X and 30° about Z, its lowest corner 0.5 u above the ground under RefrRotation (computed
            // here from SpatialOverlap.Euler directly, so a sabotaged RefrRotation does not move the fixture with it).
            var plankSoup = ContactIndex.BoxSoup(new Aabb(new Vec3(-128, -8, 0), new Vec3(128, 8, 8), false));
            float MinZ(Matrix4x4 m) => plankSoup.Vertices.Min(v => Vector3.Transform(v, m).Z);
            float minRefr = MinZ(SpatialOverlap.Euler(new Vec3(-PlankRot.X, -PlankRot.Y, -PlankRot.Z), SpatialOverlap.EulerOrder.Zyx));
            float minTransposed = MinZ(SpatialOverlap.EulerZyx(PlankRot));
            Check("K8 (fixture): the transpose puts the plank's lowest corner more than 2ε away from RefrRotation's",
                  MathF.Abs(minTransposed - minRefr) > 8f, $"lowest corner {minRefr:0.##} (RefrRotation) vs {minTransposed:0.##} (EulerZyx)");
            var k8a = Mod("TsK8A"); keys["K8"] = K(k8a, 1);
            k8a.Worldspaces.Add(WorldOf(Cell(11, null, Ref(keys["K8"], Plank, CX(11), CY, 0.5f - minRefr, PlankRot)))); Save(k8a, SkyK);
            var k8b = Mod("TsK8B"); k8b.Worldspaces.Add(WorldOf(Cell(11, Flat(L(11), -64f)))); Save(k8b, SkyK);

            // K9: a small box at the centre of quad (16,16); B makes that quad a saddle: h00 = h11 = 0, h10 = h01 = -64.
            var k9a = Mod("TsK9A"); keys["K9"] = K(k9a, 1);
            k9a.Worldspaces.Add(WorldOf(Cell(12, null, Ref(keys["K9"], Small, 12 * 4096f + 16.5f * 128f, 16.5f * 128f, 0)))); Save(k9a, SkyK);
            var k9b = Mod("TsK9B"); k9b.Worldspaces.Add(WorldOf(Cell(12, LandFrom(L(12), (x, y) => (x, y) is (17, 16) or (16, 17) ? -64f : 0f)))); Save(k9b, SkyK);

            // K10: B's LAND declares no ground; K10-u: B's LAND says it has heights and gives none.
            var k10a = Mod("TsK10A"); keys["K10"] = K(k10a, 1);
            k10a.Worldspaces.Add(WorldOf(Cell(13, null, Ref(keys["K10"], BoxC, CX(13), CY, 0)))); Save(k10a, SkyK);
            var k10b = Mod("TsK10B"); k10b.Worldspaces.Add(WorldOf(Cell(13, NoGround(L(13))))); Save(k10b, SkyK);
            var k10ua = Mod("TsK10uA"); keys["K10u"] = K(k10ua, 1);
            k10ua.Worldspaces.Add(WorldOf(Cell(14, null, Ref(keys["K10u"], BoxC, CX(14), CY, 0)))); Save(k10ua, SkyK);
            var k10ub = Mod("TsK10uB"); k10ub.Worldspaces.Add(WorldOf(Cell(14, UnreadableLand(L(14))))); Save(k10ub, SkyK);

            // K11: B (masters Skyrim only) raises cell 15 to 64; C (masters Skyrim only, blind to B) restates B's heights byte for
            // byte and places its box on them. C's own statement is its view (the repeat trap, fixed in 154bc225).
            var k11b = Mod("TsK11B"); k11b.Worldspaces.Add(WorldOf(Cell(15, Flat(L(15), 64f)))); Save(k11b, SkyK);
            var k11c = Mod("TsK11C"); keys["K11"] = K(k11c, 1);
            k11c.Worldspaces.Add(WorldOf(Cell(15, Flat(L(15), 64f), Ref(keys["K11"], BoxC, CX(15), CY, 64f)))); Save(k11c, SkyK);

            // K12: a sign floating 200 u as authored; B lowers.
            var k12a = Mod("TsK12A"); keys["K12"] = K(k12a, 1);
            k12a.Worldspaces.Add(WorldOf(Cell(16, null, Ref(keys["K12"], Sign, CX(16), CY, 200f)))); Save(k12a, SkyK);
            var k12b = Mod("TsK12B"); k12b.Worldspaces.Add(WorldOf(Cell(16, Flat(L(16), -64f)))); Save(k12b, SkyK);

            // K13: a movable crate on the ground; B lowers.
            var k13a = Mod("TsK13A"); keys["K13"] = K(k13a, 1);
            k13a.Worldspaces.Add(WorldOf(Cell(17, null, Ref(keys["K13"], Crate, CX(17), CY, 0)))); Save(k13a, SkyK);
            var k13b = Mod("TsK13B"); k13b.Worldspaces.Add(WorldOf(Cell(17, Flat(L(17), -64f)))); Save(k13b, SkyK);

            // K14: a wide box (200 u) whose origin is 20 u west of the edge between cells 18 and 19; B raises cell 19 only by 64.
            var k14a = Mod("TsK14A"); keys["K14"] = K(k14a, 1);
            k14a.Worldspaces.Add(WorldOf(Cell(18, null, Ref(keys["K14"], Wide, 19 * 4096f - 20f, CY, 0)))); Save(k14a, SkyK);
            var k14b = Mod("TsK14B"); k14b.Worldspaces.Add(WorldOf(Cell(19, Flat(L(19), 64f)))); Save(k14b, SkyK);

            // K15: A places; D (masters A) moves it 10 u; B masters A, not D, and lowers the cell.
            var k15a = Mod("TsK15A"); keys["K15"] = K(k15a, 1);
            k15a.Worldspaces.Add(WorldOf(Cell(20, null, Ref(keys["K15"], BoxC, CX(20), CY, 0)))); Save(k15a, SkyK);
            var k15d = Mod("TsK15D"); k15d.Worldspaces.Add(WorldOf(Cell(20, null, Ref(keys["K15"], BoxC, CX(20) + 10f, CY, 0)))); Save(k15d, SkyK, k15a.ModKey);
            var k15b = Mod("TsK15B"); k15b.Worldspaces.Add(WorldOf(Cell(20, Flat(L(20), -64f)))); Save(k15b, SkyK, k15a.ModKey);

            // K15-b (Gate A review F1): A places; L lowers the cell; B masters L and A and moves the box 10 u (its step); C masters
            // A only and restates B's position (a repeat: no placement step). The winning record is C's, made blind to L.
            var k15ba = Mod("TsK15bA"); keys["K15b"] = K(k15ba, 1);
            k15ba.Worldspaces.Add(WorldOf(Cell(21, null, Ref(keys["K15b"], BoxC, CX(21), CY, 0)))); Save(k15ba, SkyK);
            var k15bl = Mod("TsK15bL"); k15bl.Worldspaces.Add(WorldOf(Cell(21, Flat(L(21), -64f)))); Save(k15bl, SkyK);
            var k15bb = Mod("TsK15bB"); k15bb.Worldspaces.Add(WorldOf(Cell(21, null, Ref(keys["K15b"], BoxC, CX(21) + 10f, CY, 0)))); Save(k15bb, SkyK, k15ba.ModKey, k15bl.ModKey);
            var k15bc = Mod("TsK15bC"); k15bc.Worldspaces.Add(WorldOf(Cell(21, null, Ref(keys["K15b"], BoxC, CX(21) + 10f, CY, 0)))); Save(k15bc, SkyK, k15ba.ModKey);

            // K16: a K2-shaped box (flag variant) and a second one behind a marker parent; B lowers; E disables the first, E2 the parent.
            var k16a = Mod("TsK16A"); keys["K16"] = K(k16a, 1); keys["K16p"] = K(k16a, 2); var parent = K(k16a, 3);
            k16a.Worldspaces.Add(WorldOf(Cell(22, null, Ref(keys["K16"], BoxC, CX(22), CY, 0), Ref(keys["K16p"], BoxC, CX(22) + 400f, CY, 0, parent: parent),
                                              Ref(parent, MarkerP, CX(22), CY + 400f, 0)))); Save(k16a, SkyK);
            var k16b = Mod("TsK16B"); k16b.Worldspaces.Add(WorldOf(Cell(22, Flat(L(22), -64f)))); Save(k16b, SkyK);
            var k16e = Mod("TsK16E"); k16e.Worldspaces.Add(WorldOf(Cell(22, null, Ref(keys["K16"], BoxC, CX(22), CY, 0, disabled: true)))); var k16ePath = Save(k16e, SkyK, k16a.ModKey);
            var k16e2 = Mod("TsK16E2"); k16e2.Worldspaces.Add(WorldOf(Cell(22, null, Ref(parent, MarkerP, CX(22), CY + 400f, 0, disabled: true)))); var k16e2Path = Save(k16e2, SkyK, k16a.ModKey);

            // K17: A places; B lowers; D (masters A) overrides the box keeping its placement, flagged deleted in place.
            var k17a = Mod("TsK17A"); keys["K17"] = new FormKey(k17a.ModKey, 0x917);
            k17a.Worldspaces.Add(WorldOf(Cell(23, null, Ref(keys["K17"], BoxC, CX(23), CY, 0)))); Save(k17a, SkyK);
            var k17b = Mod("TsK17B"); k17b.Worldspaces.Add(WorldOf(Cell(23, Flat(L(23), -64f)))); Save(k17b, SkyK);
            var k17d = Mod("TsK17D"); k17d.Worldspaces.Add(WorldOf(Cell(23, null, Ref(keys["K17"], BoxC, CX(23), CY, 0)))); var k17dPath = Save(k17d, SkyK, k17a.ModKey);
            ((IDisposable)opened[k17d.ModKey]).Dispose(); opened.Remove(k17d.ModKey);
            int marked = MarkDeletedInPlace(k17dPath, keys["K17"]);
            Check("K17 (fixture): D's override of the box is flagged deleted in place, placement kept", marked == 1, $"{marked} marked");

            // K18 (F0 try 5, PLAN §6): a 2048 x 2048 x 256 body sunk 128 u on A's flat ground; unrelated B raises only the ground
            // vertices at least one inside its plan edges by 64 u. Nothing in view as authored is covered: the top stays 64 u clear,
            // the ground at its sides is unchanged. Expected StillResting (no row); the try 4 reading: Buried 64.
            var k18a = Mod("TsK18A"); keys["K18"] = K(k18a, 1);
            k18a.Worldspaces.Add(WorldOf(Cell(25, Flat(L(25), 0f), Ref(keys["K18"], Big, CX(25), CY, -128f)))); Save(k18a, SkyK);
            var k18b = Mod("TsK18B"); k18b.Worldspaces.Add(WorldOf(Cell(25, LandFrom(L(25), (x, y) => x >= 9 && x <= 23 && y >= 9 && y <= 23 ? 64f : 0f)))); Save(k18b, SkyK);
            // K18-b: the same body resting on A's flat ground; unrelated B raises one ground vertex on its west edge by 64 u: a sliver of
            // one side, far under a quarter of what is in view, is covered. Expected StillResting; the try 4 reading: Buried 64.
            var k18ba = Mod("TsK18bA"); keys["K18b"] = K(k18ba, 1);
            k18ba.Worldspaces.Add(WorldOf(Cell(26, Flat(L(26), 0f), Ref(keys["K18b"], Big, CX(26), CY, 0f)))); Save(k18ba, SkyK);
            var k18bb = Mod("TsK18bB"); k18bb.Worldspaces.Add(WorldOf(Cell(26, LandFrom(L(26), (x, y) => x == 8 && y == 16 ? 64f : 0f)))); Save(k18bb, SkyK);
            // K19 (Gate B review H1): a render-only body whose only shape is an effect shader (a smoke or cloud card) rests on A's ground;
            // unrelated B raises it 64 u. Expected NoBody (not an object standing on the ground). K19-b: the same with an alpha-TEST
            // shape (a flower's cut-out) is an object: Introduced Blind, Buried 64.
            var k19a = Mod("TsK19A"); keys["K19"] = K(k19a, 1); keys["K19b"] = K(k19a, 2);
            k19a.Worldspaces.Add(WorldOf(Cell(27, Flat(L(27), 0f), Ref(keys["K19"], EffectR, CX(27), CY, 0f)),
                                         Cell(28, Flat(L(28), 0f), Ref(keys["K19b"], FoliageR, CX(28), CY, 0f)))); Save(k19a, SkyK);
            var k19b = Mod("TsK19B"); k19b.Worldspaces.Add(WorldOf(Cell(27, Flat(L(27), 64f)), Cell(28, Flat(L(28), 64f)))); Save(k19b, SkyK);
            foreach (var o in opened.Values) ((IDisposable)o).Dispose();
            opened.Clear();

            var full = ReadOrder(paths);
            int P(Read r, string name) => r.Names.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            foreach (var (F, B) in new[] { (8f, 8f), (32f, 32f) })
                Cases(Check, full, keys, P, F, B, paths.Where(p => p != k16ePath && p != k16e2Path).ToList());
        }
        finally { foreach (var o in opened.Values) ((IDisposable)o).Dispose(); }
    }

    static void Cases(Action<string, bool, string> Check, Read r, Dictionary<string, FormKey> keys, Func<Read, string, int> P, float F, float B,
                      List<string> withoutK16E)
    {
        var tci = new TerrainContactIndex(r.Cidx, r.Land, r.Closure) { Epsilon = 4f, FloatThreshold = F, BurialThreshold = B, BurialShare = 0.25f, RenderClasses = ClassesOf };
        string T = $" [F {F:0}, B {B:0}]";
        string D(TerrainRow x) => $"{x.Status} {x.Verdict} gap {x.Gap:0.##} burial {x.Burial:0.##} sight {x.Sight} pop {x.Population} owner {(x.Owner >= 0 && x.Owner < r.Names.Count ? r.Names[x.Owner] : "-")} W {(x.Winner >= 0 && x.Winner < r.Names.Count ? r.Names[x.Winner] : "-")} motion {x.Motion} auth {x.Authored} ({x.AuthoredD0:0.##}/{x.AuthoredD1:0.##}) win ({x.WinningD0:0.##}/{x.WinningD1:0.##}) — {x.Why}";
        bool Near(float a, float b, float tol) => MathF.Abs(a - b) <= tol;
        TerrainRow E(string k) => tci.Explain(keys[k]);
        string N(int p) => p >= 0 && p < r.Names.Count ? r.Names[p] : "-";

        var k1 = E("K1");
        Check("K1: unrelated B raises A's ground 64 u -> Introduced Blind, Buried 64" + T,
              k1 is { Status: TerrainStatus.Row, Verdict: TerrainVerdict.Buried, Sight: TerrainSight.Blind, Population: TerrainPopulation.Mod } && Near(k1.Burial, 64f, 0.01f)
              && N(k1.Owner) == "TsK1A.esp" && N(k1.Winner) == "TsK1B.esp" && TerrainContactIndex.Emitted(k1), "expected Buried 64 Blind; got " + D(k1));
        var k2 = E("K2");
        Check("K2: unrelated B lowers the ground 64 u -> Introduced Blind, Floating 64" + T,
              k2 is { Status: TerrainStatus.Row, Verdict: TerrainVerdict.Floating, Sight: TerrainSight.Blind } && Near(k2.Gap, 64f, 0.01f) && TerrainContactIndex.Emitted(k2),
              "expected Floating 64 Blind; got " + D(k2));
        var k2b = E("K2b");
        Check("K2-b: lowered 6 u, within one heightmap unit -> no row (terrains agree)" + T, k2b.Status == TerrainStatus.TerrainAgrees, "expected TerrainAgrees; got " + D(k2b));
        var k3 = E("K3");
        Check("K3: a collision rock under the box -> resting on the rock, no row" + T,
              k3.Status == TerrainStatus.StillResting && k3.Support == keys["K3rock"], "expected StillResting on the rock; got " + D(k3) + $" support {k3.Support}");
        var k4 = E("K4");
        Check("K4: the rock is render only -> Floating 64 (render never supports)" + T,
              k4 is { Status: TerrainStatus.Row, Verdict: TerrainVerdict.Floating, Sight: TerrainSight.Blind } && Near(k4.Gap, 64f, 0.01f), "expected Floating 64; got " + D(k4));
        var k5 = E("K5");
        Check("K5: B masters A -> Seen, counted, not emitted" + T,
              k5 is { Status: TerrainStatus.Row, Sight: TerrainSight.Seen } && !TerrainContactIndex.Emitted(k5), "expected Seen; got " + D(k5));
        var k5b = E("K5b");
        Check("K5-b: a later plugin repeating the position saw B's ground -> Seen, owner A" + T,
              k5b is { Status: TerrainStatus.Row, Sight: TerrainSight.Seen } && N(k5b.Owner) == "TsK5bA.esp" && !TerrainContactIndex.Emitted(k5b), "expected Seen owner TsK5bA; got " + D(k5b));
        var k6 = E("K6");
        Check("K6 (info: cannot see its named wrong answer, GATE-A §2): A masters B and stands on B's ground -> no row" + T,
              k6.Status == TerrainStatus.TerrainAgrees, "expected TerrainAgrees; got " + D(k6));
        var k6b = E("K6b");
        Check("K6-b: B loads first, A does not master it -> Blind, Floating 64 (loading later is not seeing)" + T,
              k6b is { Status: TerrainStatus.Row, Verdict: TerrainVerdict.Floating, Sight: TerrainSight.Blind } && Near(k6b.Gap, 64f, 0.01f), "expected Blind Floating 64; got " + D(k6b));
        var k7 = E("K7");
        Check("K7: Bethesda's box over a DLC's LAND change -> Inherited, never the class" + T,
              k7 is { Status: TerrainStatus.Row, Population: TerrainPopulation.Inherited } && !TerrainContactIndex.Emitted(k7), "expected Inherited; got " + D(k7));
        var k8 = E("K8");
        Check("K8: a tilted plank, its low corner 0.5 u up -> rested as authored; Floating 64.5 after B lowers" + T,
              k8 is { Status: TerrainStatus.Row, Authored: TerrainAuthored.Resting, Verdict: TerrainVerdict.Floating } && Near(k8.Gap, 64.5f, 0.1f),
              "expected authored Resting, Floating 64.5; got " + D(k8));
        var k9 = E("K9");
        Check("K9: on a saddle quad one diagonal rests and the other floats -> CannotTell" + T,
              k9 is { Status: TerrainStatus.Row, Verdict: TerrainVerdict.CannotTell } && k9.Saddle > 0f, "expected CannotTell; got " + D(k9) + $" saddle {k9.Saddle:0.##}");
        var k10 = E("K10");
        Check("K10: B's LAND declares no ground -> Blind, Floating over no ground" + T,
              k10 is { Status: TerrainStatus.Row, Verdict: TerrainVerdict.Floating, Sight: TerrainSight.Blind } && float.IsPositiveInfinity(k10.Gap), "expected Floating over no ground; got " + D(k10));
        var k10u = E("K10u");
        Check("K10-u: B's LAND is unreadable -> refused by name" + T,
              k10u.Status == TerrainStatus.Refused && k10u.Why.Contains("unreadable") && k10u.Why.Contains("TsK10uB.esp"), "expected Refused naming TsK10uB's LAND; got " + D(k10u));
        var k11 = E("K11");
        Check("K11: C masters A only and restates B's heights -> C saw its own ground (terrains agree)" + T,
              k11.Status == TerrainStatus.TerrainAgrees && k11.Cells is { Count: 1 } c11 && N(c11[0].AuthoredPlugin) == "TsK11C.esp",
              "expected TerrainAgrees with C's own view; got " + D(k11) + (k11.Cells is { Count: > 0 } cc ? $" authored plugin {N(cc[0].AuthoredPlugin)}" : ""));
        var k12 = E("K12");
        Check("K12: a sign floating 200 u as authored -> out of the population" + T, k12.Status == TerrainStatus.NotRestingAsAuthored, "expected NotRestingAsAuthored; got " + D(k12));
        var k13 = E("K13");
        Check("K13: a movable crate -> context list, landing 64 u below by the drop rule" + T,
              k13 is { Status: TerrainStatus.Row, Motion: DependentMotion.Movable } && k13.Landing is { } l13 && Near(l13, 64f, 0.01f) && !TerrainContactIndex.Emitted(k13),
              "expected Movable context row landing 64; got " + D(k13) + $" landing {k13.Landing}");
        var k14 = E("K14");
        var ch14 = k14.Cells?.Where(c => c.Changed).ToList();
        Check("K14: a body straddling a cell edge, only the east cell raised -> Buried 64, the row names (19,0) only" + T,
              k14 is { Status: TerrainStatus.Row, Verdict: TerrainVerdict.Buried } && Near(k14.Burial, 64f, 0.01f) && ch14 is { Count: 1 } && ch14[0].GX == 19,
              "expected Buried naming (19,0); got " + D(k14) + " changed cells " + string.Join(",", ch14?.Select(c => $"({c.GX},{c.GY})") ?? Array.Empty<string>()));
        var k15 = E("K15");
        Check("K15: D moved it later and cannot see B -> owner D, Blind" + T,
              k15 is { Status: TerrainStatus.Row, Sight: TerrainSight.Blind } && N(k15.Owner) == "TsK15D.esp", "expected owner TsK15D Blind; got " + D(k15));
        var k15b = E("K15b");
        Check("K15-b: the winning record restates another plugin's step blind to it -> owner C, Blind, Floating 64" + T,
              k15b is { Status: TerrainStatus.Row, Sight: TerrainSight.Blind, Verdict: TerrainVerdict.Floating } && N(k15b.Owner) == "TsK15bC.esp" && Near(k15b.Gap, 64f, 0.01f),
              "expected owner TsK15bC Blind Floating 64; got " + D(k15b));
        var k16 = E("K16"); var k16p = E("K16p");
        Check("K16: disabled at load by its flag -> absent from the population" + T, k16.Status == TerrainStatus.DisabledAtLoad, "expected DisabledAtLoad; got " + D(k16));
        Check("K16: disabled at load through its enable parent -> absent from the population" + T, k16p.Status == TerrainStatus.DisabledAtLoad, "expected DisabledAtLoad; got " + D(k16p));
        var k18 = E("K18");
        Check("K18: a body sunk 128 u; B raises the ground only under its hidden part -> no row (nothing in view is covered)" + T,
              k18.Status == TerrainStatus.StillResting, "expected StillResting; got " + D(k18));
        var k18b = E("K18b");
        Check("K18-b: a 2048 u body; B raises one edge vertex 64 u -> no row (a sliver of what is in view is covered)" + T,
              k18b.Status == TerrainStatus.StillResting, "expected StillResting; got " + D(k18b));
        var k19 = E("K19");
        Check("K19: a render body made only of an effect shape -> NoBody, never judged" + T,
              k19.Status == TerrainStatus.NoBody && k19.Why.Contains("effect"), "expected NoBody; got " + D(k19));
        var k19b = E("K19b");
        Check("K19-b: a render body of an alpha-test shape is an object -> Introduced Blind, Buried 64" + T,
              k19b is { Status: TerrainStatus.Row, Verdict: TerrainVerdict.Buried, Sight: TerrainSight.Blind } && Near(k19b.Burial, 64f, 0.01f), "expected Buried 64 Blind; got " + D(k19b));
        var k17 = E("K17");
        Check("K17: the owner's record is deleted with its placement kept -> unjudgeable, counted" + T,
              k17.Status == TerrainStatus.DeletedWithPlacement, "expected DeletedWithPlacement; got " + D(k17));

        // the class emit: exactly the Blind Fixed Floating/Buried rows of the cases above
        var emitted = tci.Judge(keepAll: false).Where(TerrainContactIndex.Emitted).Select(x => x.Key).ToHashSet();
        var wantEmit = new[] { "K1", "K2", "K4", "K6b", "K8", "K10", "K14", "K15", "K15b", "K19b" }.Select(k => keys[k]).ToHashSet();
        // the rocks of K3/K4 float too once the ground drops (32 u: a row at F 8, not at F 32); they are not case subjects
        var caseKeys = keys.Where(kv => kv.Key != "K3rock").Select(kv => kv.Value).ToHashSet();
        var otherRows = emitted.Where(k => !caseKeys.Contains(k)).ToList();
        Check("TS-emit: the emit holds every Blind Fixed case row and no other case's" + T,
              wantEmit.All(emitted.Contains) && emitted.Where(caseKeys.Contains).All(wantEmit.Contains) && otherRows.Count == (F < 32f ? 2 : 0),
              $"emitted {emitted.Count} (case rows {emitted.Count(caseKeys.Contains)}, the two rocks' rows {otherRows.Count}, expected {(F < 32f ? 2 : 0)}); missing {string.Join(",", keys.Where(kv => wantEmit.Contains(kv.Value) && !emitted.Contains(kv.Value)).Select(kv => kv.Key))}");

        // TS-cost (2026-10-20, the work limit): K3's box needs its rock read; with a limit of 1 triangle pair it is not judged
        if (F == 8f)
        {
            var tight = new TerrainContactIndex(r.Cidx, r.Land, r.Closure) { Epsilon = 4f, FloatThreshold = F, BurialThreshold = B, BurialShare = 0.25f, RenderClasses = ClassesOf, WorkLimit = 1 };
            var c3 = tight.Explain(keys["K3"]);
            Check("TS-cost: a support reading past the work limit -> UnjudgedCost, counted, never a verdict",
                  c3.Status == TerrainStatus.UnjudgedCost && c3.Why.Contains("work limit"), "expected UnjudgedCost; got " + D(c3));
            var k1t = tight.Explain(keys["K1"]);
            Check("TS-cost: a body the ground decides needs no support reading -> judged as before", k1t is { Status: TerrainStatus.Row, Verdict: TerrainVerdict.Buried },
                  "expected Buried; got " + D(k1t));
        }
        // K16, the other arm: without E and E2 both boxes are judged rows
        if (F == 8f)
        {
            var arm = ReadOrder(withoutK16E);
            var tci2 = new TerrainContactIndex(arm.Cidx, arm.Land, arm.Closure) { Epsilon = 4f, FloatThreshold = F, BurialThreshold = B, BurialShare = 0.25f, RenderClasses = ClassesOf };
            var a16 = tci2.Explain(keys["K16"]); var a16p = tci2.Explain(keys["K16p"]);
            Check("K16: in the arm without the disabling plugins both boxes are Floating rows",
                  a16 is { Status: TerrainStatus.Row, Verdict: TerrainVerdict.Floating } && a16p is { Status: TerrainStatus.Row, Verdict: TerrainVerdict.Floating },
                  $"flag box: {a16.Status} {a16.Verdict}; parent box: {a16p.Status} {a16p.Verdict}");
        }
    }
}
