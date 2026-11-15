using System.Drawing;
using HarnessShared;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;

namespace ViewProbe;

// THE CAPTURE-FALLBACK FIXTURE (`viewprobe --write-capture-fixture DIR`, read by tests/test_capture_fallbacks.py, which runs
// the REAL viewprobe, capture_points, capture_acquire and shotplan on it). Test-only, written with Mutagen. One plugin,
// CfFixture.esp, no masters: its own STAT bases name VANILLA meshes, which the test resolves from a Skyrim SE game's own
// archives (the game folder comes from the environment or the harness config; absent, the test reports "not run").
//
// Every cell is interior, floored with a 7 x 7 tiling of ImpExtFloorChunk01 350 u apart (overlapping, top at z 0) and,
// except CfNoNav, navmeshed with a flat 64 u grid at z 0 from -1280 to 1280. The pair is A (crate) and B. A "wall" is the
// same crate at scale 1.9 (local box x -47.5..47.5, y -197.6..72.2, z -38..38): 95 u thick, so an eye inside one is
// within 96 u of both faces, and stacked in four tiers (centres z -4, 72, 148, 224; boundaries at 34, 110, 186), so an
// eye 120 u up inside a wall is 10 u above a tier's underside: the placement test calls it enclosed. (A single 150 u
// thick tier let an eye inside it pass with only two near faces, and the base planner accepted that camera: the first
// base runs, 2026-10-31.) Each side is two pieces per tier: wall index side*8 + tier*2 + piece, sides W, E, S, N.
// Expectations are stated by each test, worked out from this table.
//
//   cell        A                      B                                    else
//   CfCutaway   crate (0,30,20)        crate (30,10,20) crossing A          walls, inner faces 25 u clear of the pair 
//   CfTouch     crate (0,30,20)        crate (30,10,20)                     walls, inner faces on the pair's outer faces 
//   CfBuried    crate (0,0,20)         floor chunk at z -38 (z -38..-19): sunk under the floor, its top on the
//                                      floor's underside (z -19), so it touches the tiles over it
//   CfGlow      crate (0,0,20)         glow sphere (effect, additive) at (0,0,60)
//   CfFocus     crate (0,0,20)         glow sphere x6 at (900,0,1400): big, see-through, far above and aside
//   CfNoNav     crate (0,30,20)        crate (30,10,20)                     no navmesh: no station can stand
//   CfDisabled  crate (0,0,20)         glow sphere at (0,0,60), initially disabled
//   (CfGlow also stands in a room of walls about 800 u out, so the planner's clearance rays are bounded)
//   CfOpen      crate (0,30,20)        crate (30,10,20)                     nothing else: no obstruction stands behind any
//               camera, so the planner's nearest-obstruction margin is none
//   CfFocusLow  ImpExtWall03 with its origin at z -380 (drawn z about -394..193: its origin far below the floor it
//               stands through), and the glow x6 at (900,0,1400): the focus's floor must come from its drawn box,
//               not its origin (C1, the WRW rerun's fb173b065383)
//   CfVoid      crate (0,30,20)        crate (30,10,20)                     no floor and no navmesh: no storey at all
//   CfCutMod    crate (0,30,20)        crate (30,10,20) from CfFixtureMod.esp  the CfCutaway walls (B1/B2: the capture
//                                      states; without the mod plugin, B is not there)
//   CfThick     no pair: lone solids of the crate (drawn x +-25.5s, y -104s..37.75s, z +-20.5s at scale s), for the
//               inside test (tests/test_eye_inside_solid.py): T1 scale 3 at (-600,0,61.5), T2 scale 4 at (0,600,82), T3
//               scale 8 at (600,0,164), the thin wall scale 1.5 (76.5 u thick) at (0,-600,30.75); Walls[0..3] in that order
//   CfThickWalls crate (0,30,20)       crate (30,10,20)                     walls of the crate at scale 6 (about 300 u thick and
//               245 u tall, one tier centred z 115), inner faces 25 u clear of the pair: a station eye in the middle of a
//               wall has no face within 96 u (the planner gap of the 2026-10-31 base run 3, made wider than any reach)
//   CfTower     no pair, no floor: the vanilla Nordic tower ruin's outer shell and its interior top-floor piece, both at the
//               origin unrotated (Walls[0], Walls[1]): an eye on the top floor is inside the shell's one-sided faces but the
//               interior piece's walls stand nearer and face it, the view a player has there
//   CfCloud     no pair: the vanilla cloud mesh (one effect-shader shape plus an effect-shader editor marker) at scale 0.1,
//               its box about x -422..360, y -62..44, z 1..512 at (0,0,265) (Walls[0]); the vanilla burning campfire (lit
//               logs and stones, effect-shader flames, glow and smoke) at (600,0,0) (Walls[1]): effect shapes are not solid
//   CfRoofed    crate (0,30,20)        crate (30,10,20)                     no floor and no navmesh (CfVoid), under a roof:
//               one floor chunk at z 61..80 (Walls[0]), 20 u above the crates' tops (40.5) and under the top-down's cut
//               (the overlap's top + 64, about 104.5): it covers the pair from above and touches neither (F4)
//   CfRoofTouch the same, the chunk's underside on the crates' tops (z 40.5..59.5, Walls[0]): it touches both, so it may
//               not be hidden (F4)
//   CfFxLit     no pair: vanilla meshes under an effects\ folder, solid or not by their own shapes (C1720-C1729), each
//               base with a non-FX editor id unless said: Walls[0] the frozen Falmer statue (lit, collision) at
//               (-1050,0,0); Walls[1] the ice sheet (one lit shape) at (-350,0,100); Walls[2] the blue fire (its only lit
//               shape a refraction plane) at (350,0,0); Walls[3] the crater FX (effect shapes and hidden emitter geometry
//               only) at (1050,0,0); Walls[4] the thin waterfall (effect shapes and a shader-less plane), hung
//               above the floor at (0,700,2100); Walls[5] the ice sheet again under an FX editor id (the editor-id rule still drops it) at
//               (0,-700,100)
//   CfFxMissing no pair: one ref (Walls[0]) at the origin whose base names an effects\ mesh that does not exist: a physical
//               ref whose mesh cannot be read, so the inside and clear queries must count it (C1727)
//   CfTallFocus A: the CfTower shell (drawn box about z -512..1837) and B: the crate x4 at (0,0,150) (z 68..232),
//               overlapping only the tower's lower part; the tower's interior top floor (Walls[0], about z 1280); floor
//               tiles and navmesh at z 0. (A glow was tried first: an effect billboard's drawn box is flat.) The test
//               names A the request's focus. The ring's storey comes from where the two drawn boxes overlap, not from the
//               tower's whole height (F5)
public static class CaptureFixture
{
    public const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey Key = new("CfFixture", ModType.Plugin);
    static FormKey K(uint id) => new(Key, id);

    public static readonly FormKey FloorBase = K(0x801), CrateBase = K(0x802), GlowBase = K(0x803), WallBase = K(0x804),
        TowerBase = K(0x805), TowerTopBase = K(0x806), CloudBase = K(0x807), CampfireBase = K(0x808),
        FalmerBase = K(0x809), IceBase = K(0x80A), BlueFireBase = K(0x80B), CraterBase = K(0x80C), WaterfallBase = K(0x80D),
        IceFxBase = K(0x80E), MissingFxBase = K(0x80F);
    public const string FalmerMesh = @"DLC01\Effects\DLC1Falmer_VampFrozenPose01.nif";
    public const string IceMesh = @"DLC01\Effects\DLC1IceFlat01.nif";
    public const string BlueFireMesh = @"Effects\C06FXfireBlueWithEmbersHeavy.nif";
    public const string CraterMesh = @"DLC01\Effects\CraterBehaviors\sc_crater01FX.nif";
    public const string WaterfallMesh = @"Effects\FXWaterfallThin2048x128.nif";
    public const string CloudMesh = @"Sky\CloudShape05_50.nif";
    public const string CampfireMesh = @"Clutter\WoodFires\Campfire01Burning.nif";
    public const string TowerMesh = @"Dungeons\Nordic\Exterior\Towers\NorTowerRuins01.nif";
    public const string TowerTopMesh = @"Dungeons\Nordic\Exterior\Towers\NorTowerRuinsIntTopFloor01.nif";
    public const string WallMesh = "Dungeons\\Imperial\\Exterior\\ImpExtWall03.nif";
    public const string FloorMesh = "Dungeons\\Imperial\\Exterior\\ImpExtFloorChunk01.nif";
    public const string CrateMesh = "Clutter\\Common\\CrateSmallLong02.nif";
    public const string GlowMesh = "Effects\\Ambient\\FXGlowFillRndLitBrt.nif";

    // per cell: the cell, its navmesh, A, B, 32 wall pieces and 49 floor tiles; FormIDs are stable so the tests can name them
    public sealed record CellIds(string EditorId, FormKey Cell, FormKey Nav, FormKey A, FormKey B, FormKey[] Walls, FormKey[] Floor);
    public static readonly CellIds Cutaway = Ids("CfCutaway", 0x810), Touch = Ids("CfTouch", 0x820), Buried = Ids("CfBuried", 0x830),
        Glow = Ids("CfGlow", 0x840), Focus = Ids("CfFocus", 0x850), NoNav = Ids("CfNoNav", 0x860), Disabled = Ids("CfDisabled", 0x870),
        CutMod = Ids("CfCutMod", 0x880), Open = Ids("CfOpen", 0x890), FocusLow = Ids("CfFocusLow", 0x8A0),
        Void = Ids("CfVoid", 0x8B0), Thick = Ids("CfThick", 0x8C0), ThickWalls = Ids("CfThickWalls", 0x8D0),
        Tower = Ids("CfTower", 0x8E0), Cloud = Ids("CfCloud", 0x8F0), Roofed = Ids("CfRoofed", 0x900),
        RoofTouch = Ids("CfRoofTouch", 0x910), TallFocus = Ids("CfTallFocus", 0x920), FxLit = Ids("CfFxLit", 0x930),
        FxMissing = Ids("CfFxMissing", 0x940), Ledge = Ids("CfLedge", 0x950),
        LedgeGap = Ids("CfLedgeGap", 0x960);
    /// <summary>CfLedge's upper and side navmeshes (its lower one is <c>Ledge.Nav</c>): LedgeLinkFixture.cs.</summary>
    public static readonly FormKey LedgeUpper = K(0x958), LedgeSide = K(0x959);
    /// <summary>CfLedgeGap's upper and portal navmeshes (its lower one is <c>LedgeGap.Nav</c>): LedgeLinkFixture.GapMeshes.</summary>
    public static readonly FormKey LedgeGapUpper = K(0x968), LedgeGapPortal = K(0x969);
    /// <summary>The second plugin (master CfFixture.esp): it places CfCutMod's B, so a setup without it has no B.</summary>
    public static readonly ModKey ModKeyB = new("CfFixtureMod", ModType.Plugin);
    public static readonly FormKey CutModB = new(ModKeyB, 0x801);
    public static readonly CellIds[] All = { Cutaway, Touch, Buried, Glow, Focus, NoNav, Disabled, CutMod, Open, FocusLow, Void, Thick, ThickWalls, Tower, Cloud, Roofed, RoofTouch, TallFocus, FxLit, FxMissing, Ledge, LedgeGap };

    static CellIds Ids(string edid, uint b)
        => new(edid, K(b), K(b + 1), K(b + 2), K(b + 3), Enumerable.Range(0, 32).Select(n => K(0x2000 + (b - 0x810) / 0x10 * 0x40 + (uint)n)).ToArray(),
               Enumerable.Range(0, 49).Select(i => K(0x1000 + (b - 0x810) / 0x10 * 0x40 + (uint)i)).ToArray());

    static PlacedObject Ref(FormKey key, FormKey baseKey, float x, float y, float z, float rx = 0, float ry = 0, float rz = 0, float scale = 1f)
    {
        var r = new PlacedObject(key, Rel)
        {
            Placement = new Placement { Position = new(x, y, z), Rotation = new(rx, ry, rz) },
            Scale = scale == 1f ? null : scale,
        };
        r.Base.SetTo(baseKey);
        return r;
    }

    const float HalfPi = MathF.PI / 2f;
    // a wall: the crate at scale 3 standing on the floor; the long axis along Y, or along X when turned a quarter
    static PlacedObject WallY(FormKey k, float x, float y, float z) => Ref(k, CrateBase, x, y, z, scale: 1.9f);
    static PlacedObject WallX(FormKey k, float x, float y, float z) => Ref(k, CrateBase, x, y, z, 0, 0, HalfPi, 1.9f);
    static readonly float[] TierZ = { -4f, 72f, 148f, 224f };
    // four walls around the pair (union x -25..55, y -94..68): w and e are the W and E walls' centre x, s and n the S and N
    // walls' centre y. W/E pieces centred at y -16 and 116 cover y -213.6..188.2; S/N pieces centred at x -80 and 110 cover
    // x -145..175 whichever way the quarter turn swings the crate's long axis.
    static void Walls(SkCell cell, FormKey[] p, float w, float e, float s, float n)
    {
        for (int t = 0; t < 4; t++)
            for (int k = 0; k < 2; k++)
            {
                float z = TierZ[t];
                cell.Temporary.Add(WallY(p[0 * 8 + t * 2 + k], w, k == 0 ? -16f : 116f, z));
                cell.Temporary.Add(WallY(p[1 * 8 + t * 2 + k], e, k == 0 ? -16f : 116f, z));
                cell.Temporary.Add(WallX(p[2 * 8 + t * 2 + k], k == 0 ? -80f : 110f, s, z));
                cell.Temporary.Add(WallX(p[3 * 8 + t * 2 + k], k == 0 ? -80f : 110f, n, z));
            }
    }

    // walls of the crate at scale 6 (x +-153, y -624..226, z +-123 about the piece), one tier at z 115; W/E pieces at
    // y 100 and 200 (y -524..426), S/N pieces at x -130 and 160 with a quarter turn (x -356..386 either way it swings);
    // piece side*8 + k
    static void ThickWalls6(SkCell cell, FormKey[] p, float w, float e, float s, float n)
    {
        for (int k = 0; k < 2; k++)
        {
            cell.Temporary.Add(Ref(p[0 * 8 + k], CrateBase, w, k == 0 ? 100f : 200f, 115f, scale: 6f));
            cell.Temporary.Add(Ref(p[1 * 8 + k], CrateBase, e, k == 0 ? 100f : 200f, 115f, scale: 6f));
            cell.Temporary.Add(Ref(p[2 * 8 + k], CrateBase, k == 0 ? -130f : 160f, s, 115f, 0, 0, HalfPi, 6f));
            cell.Temporary.Add(Ref(p[3 * 8 + k], CrateBase, k == 0 ? -130f : 160f, n, 115f, 0, 0, HalfPi, 6f));
        }
    }

    // a room around an open cell: four walls of the crate at scale 4 (200 u thick, 160 u tall, two tiers to z 320) about
    // 700 u out, so the camera planner's horizontal clearance rays meet geometry on every bearing (it refuses a bearing
    // whose rays leave the cell). Pieces 0x3000 + cell index * 0x40 + n.
    static void Room(SkCell cell, uint b)
    {
        uint ci = (b - 0x810) / 0x10, n = 0;
        foreach (var z in new[] { 80f, 240f })
            foreach (var c in new[] { -560f, 0f, 560f })
            {
                cell.Temporary.Add(Ref(K(0x3000 + ci * 0x40 + n++), CrateBase, -800f, c, z, scale: 4f));
                cell.Temporary.Add(Ref(K(0x3000 + ci * 0x40 + n++), CrateBase, 800f, c, z, scale: 4f));
                cell.Temporary.Add(Ref(K(0x3000 + ci * 0x40 + n++), CrateBase, c, -800f, z, 0, 0, HalfPi, 4f));
                cell.Temporary.Add(Ref(K(0x3000 + ci * 0x40 + n++), CrateBase, c, 800f, z, 0, 0, HalfPi, 4f));
            }
    }

    static SkCell NewCell(CellIds c, bool nav, bool floor = true)
    {
        var cell = new SkCell(c.Cell, Rel)
        {
            EditorID = c.EditorId, Flags = SkCell.Flag.IsInteriorCell,
            Lighting = new Mutagen.Bethesda.Skyrim.CellLighting
            {
                AmbientColor = Color.FromArgb(255, 180, 180, 180), DirectionalColor = Color.FromArgb(255, 200, 200, 190),
                FogNearColor = Color.FromArgb(255, 0, 0, 0), FogFarColor = Color.FromArgb(255, 0, 0, 0),
                DirectionalRotationXY = 45, DirectionalRotationZ = 60, DirectionalFade = 1f, FogPower = 1f, FogMax = 0f,
                FogNear = 100000f, FogFar = 200000f, FogClipDistance = 300000f, LightFadeBegin = 100000f, LightFadeEnd = 200000f,
            },
        };
        // the floor: 7 x 7 chunks, 350 u apart (each about 530 u across, so they overlap), top at z 0 (the chunk stands
        // from z 0 to 19 in its own space)
        int f = 0;
        for (int j = -3; j <= 3 && floor; j++)
            for (int i = -3; i <= 3; i++)
                cell.Temporary.Add(Ref(c.Floor[f++], FloorBase, i * 350f, j * 350f, -19f));
        if (nav) cell.NavigationMeshes.Add(BlockFixture.Grid(c.Nav, c.Cell, 40, 40, 64f, -1280f, -1280f));
        return cell;
    }

    public static int Write(string dir)
    {
        Directory.CreateDirectory(dir);
        var m = new SkyrimMod(Key, Rel);
        m.Statics.Add(new Static(FloorBase, Rel) { EditorID = "CfFloorChunk", Model = new Model { File = FloorMesh },
            ObjectBounds = new ObjectBounds { First = new P3Int16(-280, -246, 0), Second = new P3Int16(256, 286, 19) } });
        m.Statics.Add(new Static(CrateBase, Rel) { EditorID = "CfCrate", Model = new Model { File = CrateMesh },
            ObjectBounds = new ObjectBounds { First = new P3Int16(-25, -104, -20), Second = new P3Int16(25, 38, 20) } });
        m.Statics.Add(new Static(WallBase, Rel) { EditorID = "CfWall", Model = new Model { File = WallMesh },
            ObjectBounds = new ObjectBounds { First = new P3Int16(-201, -272, -14), Second = new P3Int16(191, 289, 573) } });
        m.Statics.Add(new Static(TowerBase, Rel) { EditorID = "CfTower", Model = new Model { File = TowerMesh },
            ObjectBounds = new ObjectBounds { First = new P3Int16(-700, -700, -100), Second = new P3Int16(700, 700, 2400) } });
        m.Statics.Add(new Static(TowerTopBase, Rel) { EditorID = "CfTowerTop", Model = new Model { File = TowerTopMesh },
            ObjectBounds = new ObjectBounds { First = new P3Int16(-650, -650, 900), Second = new P3Int16(650, 650, 2400) } });
        m.Statics.Add(new Static(CloudBase, Rel) { EditorID = "CfCloud", Model = new Model { File = CloudMesh },
            ObjectBounds = new ObjectBounds { First = new P3Int16(-4223, -623, -2642), Second = new P3Int16(3604, 442, 2466) } });
        m.Statics.Add(new Static(CampfireBase, Rel) { EditorID = "CfCampfire", Model = new Model { File = CampfireMesh },
            ObjectBounds = new ObjectBounds { First = new P3Int16(-140, -196, -97), Second = new P3Int16(140, 140, 267) } });
        // CfFxLit's bases: generous record boxes (the queries nominate by the mesh's own bounds; the box is only a fallback)
        foreach (var (k, ed, mesh) in new[] { (FalmerBase, "CfFrozenFalmer", FalmerMesh), (IceBase, "CfIceSheet", IceMesh),
                     (BlueFireBase, "CfBlueFire", BlueFireMesh), (CraterBase, "CfCraterFx", CraterMesh),
                     (WaterfallBase, "CfThinWaterfall", WaterfallMesh), (IceFxBase, "FXCfIceSheet", IceMesh) })
            m.Statics.Add(new Static(k, Rel) { EditorID = ed, Model = new Model { File = mesh },
                ObjectBounds = new ObjectBounds { First = new P3Int16(-600, -600, -600), Second = new P3Int16(600, 600, 2400) } });
        m.Statics.Add(new Static(MissingFxBase, Rel) { EditorID = "CfMissingFx", Model = new Model { File = @"Effects\CfNoSuchMesh.nif" },
            ObjectBounds = new ObjectBounds { First = new P3Int16(-100, -100, 0), Second = new P3Int16(100, 100, 200) } });
        m.Statics.Add(new Static(GlowBase, Rel) { EditorID = "CfGlow", Model = new Model { File = GlowMesh },
            ObjectBounds = new ObjectBounds { First = new P3Int16(-258, -258, -258), Second = new P3Int16(258, 258, 258) } });

        void Pair(SkCell cell, CellIds c)
        {
            cell.Temporary.Add(Ref(c.A, CrateBase, 0, 30, 20));
            cell.Temporary.Add(Ref(c.B, CrateBase, 30, 10, 20));
        }
        // pair union: x -25..55, y -94..68
        var cut = NewCell(Cutaway, true); Pair(cut, Cutaway);
        Walls(cut, Cutaway.Walls, -97.5f, 127.5f, -166.5f, 140.5f);      // inner faces 25 u clear of the pair
        var touch = NewCell(Touch, true); Pair(touch, Touch);
        Walls(touch, Touch.Walls, -72.5f, 102.5f, -141.5f, 115.5f);      // inner faces on the pair's outer faces
        var buried = NewCell(Buried, true);
        buried.Temporary.Add(Ref(Buried.A, CrateBase, 0, 0, 20)); buried.Temporary.Add(Ref(Buried.B, FloorBase, 0, 0, -38));
        var glow = NewCell(Glow, true);
        glow.Temporary.Add(Ref(Glow.A, CrateBase, 0, 0, 20)); glow.Temporary.Add(Ref(Glow.B, GlowBase, 0, 0, 60));
        Room(glow, 0x840);
        var focus = NewCell(Focus, true);
        focus.Temporary.Add(Ref(Focus.A, CrateBase, 0, 0, 20)); focus.Temporary.Add(Ref(Focus.B, GlowBase, 900, 0, 1400, scale: 6f));
        var nonav = NewCell(NoNav, false); Pair(nonav, NoNav);
        var dis = NewCell(Disabled, true);
        dis.Temporary.Add(Ref(Disabled.A, CrateBase, 0, 0, 20));
        var off = Ref(Disabled.B, GlowBase, 0, 0, 60);
        off.MajorRecordFlagsRaw |= 0x800;   // initially disabled
        dis.Temporary.Add(off);
        var cutMod = NewCell(CutMod, true);
        cutMod.Temporary.Add(Ref(CutMod.A, CrateBase, 0, 30, 20));
        Walls(cutMod, CutMod.Walls, -97.5f, 127.5f, -166.5f, 140.5f);
        var open = NewCell(Open, true); Pair(open, Open);
        var focusLow = NewCell(FocusLow, true);
        focusLow.Temporary.Add(Ref(FocusLow.A, WallBase, 0, 0, -380)); focusLow.Temporary.Add(Ref(FocusLow.B, GlowBase, 900, 0, 1400, scale: 6f));
        var voidCell = NewCell(Void, false, floor: false); Pair(voidCell, Void);
        var thick = NewCell(Thick, true);
        thick.Temporary.Add(Ref(Thick.Walls[0], CrateBase, -600, 0, 61.5f, scale: 3f));
        thick.Temporary.Add(Ref(Thick.Walls[1], CrateBase, 0, 600, 82, scale: 4f));
        thick.Temporary.Add(Ref(Thick.Walls[2], CrateBase, 600, 0, 164, scale: 8f));
        thick.Temporary.Add(Ref(Thick.Walls[3], CrateBase, 0, -600, 30.75f, scale: 1.5f));
        var thickWalls = NewCell(ThickWalls, true); Pair(thickWalls, ThickWalls);
        ThickWalls6(thickWalls, ThickWalls.Walls, -203f, 233f, -272f, 246f);   // inner faces 25 u clear of the pair
        var tower = NewCell(Tower, false, floor: false);
        tower.Temporary.Add(Ref(Tower.Walls[0], TowerBase, 0, 0, 0));
        tower.Temporary.Add(Ref(Tower.Walls[1], TowerTopBase, 0, 0, 0));
        var cloud = NewCell(Cloud, true);
        cloud.Temporary.Add(Ref(Cloud.Walls[0], CloudBase, 0, 0, 265, scale: 0.1f));
        cloud.Temporary.Add(Ref(Cloud.Walls[1], CampfireBase, 600, 0, 0));
        var roofed = NewCell(Roofed, false, floor: false); Pair(roofed, Roofed);
        roofed.Temporary.Add(Ref(Roofed.Walls[0], FloorBase, 15, -10, 61f));
        var roofTouch = NewCell(RoofTouch, false, floor: false); Pair(roofTouch, RoofTouch);
        roofTouch.Temporary.Add(Ref(RoofTouch.Walls[0], FloorBase, 15, -10, 40.5f));
        var tall = NewCell(TallFocus, true);
        tall.Temporary.Add(Ref(TallFocus.A, TowerBase, 0, 0, 0));
        tall.Temporary.Add(Ref(TallFocus.B, CrateBase, 0, 0, 150, scale: 4f));
        tall.Temporary.Add(Ref(TallFocus.Walls[0], TowerTopBase, 0, 0, 0));
        var fxLit = NewCell(FxLit, true);
        fxLit.Temporary.Add(Ref(FxLit.Walls[0], FalmerBase, -1050, 0, 0));
        fxLit.Temporary.Add(Ref(FxLit.Walls[1], IceBase, -350, 0, 100));
        fxLit.Temporary.Add(Ref(FxLit.Walls[2], BlueFireBase, 350, 0, 0));
        fxLit.Temporary.Add(Ref(FxLit.Walls[3], CraterBase, 1050, 0, 0));
        fxLit.Temporary.Add(Ref(FxLit.Walls[4], WaterfallBase, 0, 700, 2100));
        fxLit.Temporary.Add(Ref(FxLit.Walls[5], IceFxBase, 0, -700, 100));
        var fxMissing = NewCell(FxMissing, true);
        fxMissing.Temporary.Add(Ref(FxMissing.Walls[0], MissingFxBase, 0, 0, 0));
        // CfLedge: no floor, three navmeshes joined only by typed links (LedgeLinkFixture.cs)
        var ledge = NewCell(Ledge, false, floor: false);
        foreach (var nav in LedgeLinkFixture.Meshes(Ledge.Nav, LedgeUpper, LedgeSide,
                     () => new CellNavmeshParent { Parent = new FormLink<ICellGetter>(Ledge.Cell) }))
            ledge.NavigationMeshes.Add(nav);
        // CfLedgeGap: no floor, a ledge whose two sides stand 48 u apart in plan, and a portal across a gap (GapMeshes)
        var ledgeGap = NewCell(LedgeGap, false, floor: false);
        foreach (var nav in LedgeLinkFixture.GapMeshes(LedgeGap.Nav, LedgeGapUpper, LedgeGapPortal,
                     () => new CellNavmeshParent { Parent = new FormLink<ICellGetter>(LedgeGap.Cell) }))
            ledgeGap.NavigationMeshes.Add(nav);
        foreach (var c in new[] { cut, touch, buried, glow, focus, nonav, dis, cutMod, open, focusLow, voidCell, thick, thickWalls, tower, cloud,
                                  roofed, roofTouch, tall, fxLit, fxMissing, ledge, ledgeGap })
            MovedRefFixture.FileInterior(m, c);

        var path = Path.Combine(dir, Key.FileName.String);
        m.BeginWrite.ToPath(path).WithNoLoadOrder().Write();
        // the second plugin: CfCutMod's B, placed by an override of that cell
        using (var mOv = SkyrimMod.CreateFromBinaryOverlay(path, Rel))
        {
            var mb = new SkyrimMod(ModKeyB, Rel);
            var ov = new SkCell(CutMod.Cell, Rel) { EditorID = CutMod.EditorId, Flags = SkCell.Flag.IsInteriorCell };
            ov.Temporary.Add(Ref(CutModB, CrateBase, 30, 10, 20));
            MovedRefFixture.FileInterior(mb, ov);
            mb.BeginWrite.ToPath(Path.Combine(dir, ModKeyB.FileName.String)).WithLoadOrder(mOv).Write();
        }
        Console.WriteLine($"capture fixture: {path} ({All.Length} cells)");
        foreach (var c in All)
            Console.WriteLine($"  {c.EditorId}: A {c.A}  B {c.B}  walls {c.Walls[0]}..{c.Walls[^1]}");
        return 0;
    }
}
