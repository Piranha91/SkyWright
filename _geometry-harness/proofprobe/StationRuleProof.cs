using System.Numerics;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;

// The station rule's known answers, stated as synthetic geometry in the fixture's own frame.
//
// Most of these cases are diagnosed defects of the 2026-09-19/20 rule, and each one fails on a
// WRONG ANSWER rather than on a missing member: StationRule.cs currently reproduces that rule, so
// the case meets a composer that really has the defect. Refusal REASONS are asserted, not only
// refusals, and a refusal case also asserts that no ACCEPTED pose came out and that the refused
// poses were COUNTED.
//
// Cases the reproduced rule already answers correctly are kept and marked INHERITED. They are not
// filler: a report in which every case is red proves nothing about the harness, and these are what
// shows it can also report green.
//
// The geometry cases stand on a water fact that is KNOWN DRY BY THE RULE - proven by the first
// water case below, not assumed - so that a correct water fix cannot turn a geometry case red for
// a water reason.
static class StationRuleProof
{
    /// <summary>Cases run below, excluding this count's own check. A case added without moving
    /// this number fails the last check rather than passing unnoticed.</summary>
    public const int DeclaredCases = 152;

    /// <summary>M7: SHA-256 of the face-on pose file over eight fixtures, recorded at the API stub
    /// over the unchanged face-on rule.</summary>
    const string M7FaceOnSha256 = "CC4BA5F72ADF1A9E13C7073217EA630EC698CFFE72168F713851CE43EA83E100";

    /// <summary>M9b: SHA-256 of the observing pose file over the M6 ring, measured identical at
    /// ca0059b2 and a2ac1440 before this pin was added (route mode's red-first commit).</summary>
    const string M9ObservingSha256 = "E22B3E4CFFD6C384FA6A5C376D0DB6A29B1FF25056C0EA52EF7F26F4D821D320";

    const string WS = "WS-FIXTURE";
    const string CELL_A = "CELL-A";
    const string CELL_B = "CELL-B";

    // ---- the fixture the XY -> cell -> water lookup is proven on -------------------------------
    // Six exterior grid squares in a row, chosen so that each way a READ can fail has a square of
    // its own and the two sides of the x = 0 border have DIFFERENT real surfaces - a lookup that
    // attributes a station to the wrong side cannot pass by accident.
    const string CITY = "CITY";
    const string TAMRIEL = "TAMRIEL";
    const string ORPHANWS = "ORPHAN-WS";      // no record: the read FAILED
    const string CHILDWS = "CHILD-WS";        // a record, whose PARENT link cannot be read
    const string GHOSTWS = "GHOST-WS";

    static GridCellFacts? Squares((int X, int Y) g) => g switch
    {
        (-1, 0) => new GridCellFacts("CELL-WEST", CITY, Tuple(true, 60f)),
        (0, 0) => new GridCellFacts("CELL-EAST", CITY, Tuple(true, -100f)),
        (1, 0) => new GridCellFacts("CELL-DEFER", CITY, Tuple(true, float.MaxValue)),
        (2, 0) => new GridCellFacts("CELL-ORPHAN", ORPHANWS, Tuple(false, float.MaxValue)),
        (3, 0) => new GridCellFacts("CELL-CHILD", CHILDWS, Tuple(false, float.MaxValue)),
        (4, 0) => new GridCellFacts("CELL-SOLO", ORPHANWS, Tuple(true, 60f)),
        _ => null,                            // the order defines no cell here
    };

    static WorldFacts? Worlds(string ws) => ws switch
    {
        CITY => new WorldFacts(float.MaxValue, TAMRIEL),   // defers to its parent
        TAMRIEL => new WorldFacts(-14000f, null),
        CHILDWS => new WorldFacts(float.MaxValue, GHOSTWS),
        _ => null,                            // ORPHAN-WS and GHOST-WS could not be READ
    };

    /// <summary>The middle of grid square (gx, 0), so a case names a square rather than a number.</summary>
    static float MidOf(int gx) => gx * StationLookup.GridSize + StationLookup.GridSize / 2f;

    static DetectorModel.WaterTuple Tuple(bool hasWater, float? height)
        => new(hasWater, false, null, height, null, null, null, null);

    static WorldWaterSnap Snap(float? defaultHeight)
        => new(0, null, null, null, defaultHeight, false);

    /// <summary>A cell with no water at all, as the one resolver reads it.</summary>
    static WaterResolution Dry()
        => StationRule.Classify(Tuple(false, null), null, exterior: true, fromWorldspace: WS);

    // A tall exterior primitive: 400 u wide, 3,000 u tall, centre 1,000 u below the ground a
    // station stands on - the geometry that made the historical camera point at the dirt.
    static PrimitiveGeometry Tall() => new(
        Key: "PRIM-TALL", Kind: PrimitiveKind.PortalBox,
        ExtentFull: new Vector3(400f, 8f, 3000f),
        Position: new Vector3(0f, 0f, -1000f),
        RotationRad: Vector3.Zero,
        Worldspace: WS, Cell: CELL_A);

    // A Box whose X and Y extents differ, so a face-on station on one axis cannot be mistaken for
    // one on the other.
    static PrimitiveGeometry Boxy() => new(
        Key: "PRIM-BOX", Kind: PrimitiveKind.Box,
        ExtentFull: new Vector3(400f, 900f, 3000f),
        Position: new Vector3(0f, 0f, -1000f),
        RotationRad: Vector3.Zero,
        Worldspace: WS, Cell: CELL_A);

    static FloorReading Floor(float navZ, float floorZ)
        => new(navZ, floorZ, FloorSource.Mesh, NoFloorReason.Found, false, 1);

    static FloorReading NoFloor(float navZ)
        => new(navZ, 0f, FloorSource.None, NoFloorReason.NoSurface, false, 0);

    /// <summary>Flat ground everywhere, in one cell, over a surface the rule knows is absent.</summary>
    static Func<float, float, StationEnvironment> Flat(float navZ, float floorZ)
        => (_, _) => new StationEnvironment(true, true, true, navZ, Floor(navZ, floorZ),
                                            CELL_A, WS, Dry());

    static Func<float, float, StationEnvironment> FlatWater(float navZ, float floorZ, WaterResolution w)
        => (_, _) => new StationEnvironment(true, true, true, navZ, Floor(navZ, floorZ),
                                            CELL_A, WS, w);

    static int Count(StationYield y, StationRejection r)
        => y.Rejected.TryGetValue(r, out int n) ? n : 0;

    static int AcceptedPoses(StationYield y)
    {
        int n = 0;
        foreach (var p in y.Poses) if (p.Accepted) n++;
        return n;
    }

    /// <summary>Every station composed came back refused, each carrying <paramref name="r"/> and
    /// numbers. Requires seen == Composed, so it cannot pass on a run with no refusals.</summary>
    static bool EveryRefusalCarries(StationYield y, StationRejection r)
    {
        int seen = 0;
        foreach (var p in y.Poses)
        {
            if (p.Accepted) continue;
            if (p.RejectionReason != r) return false;
            if (p.RejectionDetail.Length == 0) return false;
            seen++;
        }
        return seen == y.Composed;
    }

    public static void Run(Action<string, bool, string> check)
    {
        int ran = 0;
        void C(string name, bool ok, string detail)
        {
            ran++;
            check("stationrule: " + name, ok, detail);
        }

        // ------------------------------------------------- the background is dry BY THE RULE --
        var dry = Dry();
        C("B2 the geometry cases' background water is KNOWN DRY by the rule",
          dry.Verdict == StationWaterVerdict.KnownDryNoSurface, $"{dry.Verdict}: {dry.How}");

        // ---------------------------------------------------------------- INHERITED geometry --
        var n0 = StationRule.Normal(Vector3.Zero);
        C("INHERITED normal of an unrotated primitive is +Y",
          MathF.Abs(n0.X) < 1e-6f && MathF.Abs(n0.Y - 1f) < 1e-6f && MathF.Abs(n0.Z) < 1e-6f,
          $"{n0}");
        var n90 = StationRule.Normal(new Vector3(0f, 0f, MathF.PI / 2f));
        C("INHERITED a quarter turn about Z sends the normal to +X",
          MathF.Abs(n90.X - 1f) < 1e-6f && MathF.Abs(n90.Y) < 1e-6f, $"{n90}");
        C("INHERITED bearing 0 is +Y", MathF.Abs(StationRule.Bearing(0f, 10f)) < 1e-4f,
          $"{StationRule.Bearing(0f, 10f)}");
        C("INHERITED bearing 90 is +X", MathF.Abs(StationRule.Bearing(10f, 0f) - 90f) < 1e-4f,
          $"{StationRule.Bearing(10f, 0f)}");
        C("INHERITED bearing is clockwise and wraps at 360",
          MathF.Abs(StationRule.Bearing(-10f, 0f) - 270f) < 1e-4f,
          $"{StationRule.Bearing(-10f, 0f)}");

        var tall = Tall();
        var ladder = StationRule.Compose(tall, "A1", Flat(0f, 0f));

        // ----------------------------------------------------------- A5 the ladder is the rule --
        C("A5 k values are exactly (0.25, 0.5, 1.0)",
          StationRule.DefaultK.Length == 3 && StationRule.DefaultK[0] == 0.25f
          && StationRule.DefaultK[1] == 0.5f && StationRule.DefaultK[2] == 1.0f,
          string.Join(", ", StationRule.DefaultK));
        C("A5 laterals are exactly (-0.35, 0.0, 0.35)",
          StationRule.DefaultLaterals.Length == 3 && StationRule.DefaultLaterals[0] == -0.35f
          && StationRule.DefaultLaterals[1] == 0.0f && StationRule.DefaultLaterals[2] == 0.35f,
          string.Join(", ", StationRule.DefaultLaterals));
        C("A5 the standoff floor is 250 and the cap 3000",
          StationRule.StandoffFloor == 250f && StationRule.StandoffCap == 3000f,
          $"{StationRule.StandoffFloor} .. {StationRule.StandoffCap}");
        C("A5 the eye height is 120.5", StationRule.EyeHeight == 120.5f, $"{StationRule.EyeHeight}");
        C("A5 the standoff is k x min(halfWidth, halfHeight), floored and capped",
          ladder.Poses.Count > 0 && MathF.Abs(ladder.Poses[0].Standoff
              - MathF.Max(250f, MathF.Min(3000f, ladder.Poses[0].K * MathF.Min(200f, 1500f)))) < 1e-3f,
          ladder.Poses.Count > 0 ? $"{ladder.Poses[0].Standoff}" : "no poses");

        C("INHERITED the ladder composes one station per (face, k, side, lateral)",
          ladder.Composed == StationRule.DefaultK.Length * 2 * StationRule.DefaultLaterals.Length,
          $"{ladder.Composed} composed");
        bool floored = true;
        foreach (var p in ladder.Poses) if (p.Standoff < StationRule.StandoffFloor) floored = false;
        C("INHERITED the standoff never falls below its floor", floored,
          $"floor {StationRule.StandoffFloor}");
        bool spanned = true;
        foreach (var p in ladder.Poses)
            if (MathF.Abs(p.PlaneZHigh - p.PlaneZLow - tall.ExtentFull.Z) > 1e-3f) spanned = false;
        C("INHERITED the published vertical extent is the primitive's own height", spanned, "");

        // ------------------------------------------------------------------ DEFECT 1, the aim --
        bool level = ladder.Poses.Count > 0;
        float worstPitch = 0f;
        foreach (var p in ladder.Poses)
        {
            if (MathF.Abs(p.LookAt.Z - p.Eye.Z) > 1e-4f) level = false;
            worstPitch = MathF.Max(worstPitch, MathF.Abs(p.PitchDeg));
        }
        C("D1 the aim point stands at the eye's own height", level,
          $"worst |lookAt.z - eye.z| over {ladder.Poses.Count} poses");
        C("D1 the published pitch is level to 1e-4 deg", worstPitch <= 1e-4f,
          $"worst |pitch| {worstPitch:0.###} deg");
        bool facing = ladder.Poses.Count > 0;
        foreach (var p in ladder.Poses)
            if (p.AngleToPlaneNormalDeg > 90f) facing = false;
        C("D1 every station looks at the primitive, not away from it", facing, "");

        // ----------------------------------------------------- DEFECT 4, the eye's own ground --
        var raised = StationRule.Compose(tall, "A1", Flat(-40f, 0f));
        bool onFloor = raised.Poses.Count > 0, onNav = raised.Poses.Count > 0;
        foreach (var p in raised.Poses)
        {
            if (MathF.Abs(p.Eye.Z - (0f + StationRule.EyeHeight)) > 1e-3f) onFloor = false;
            if (MathF.Abs(p.Eye.Z - (-40f + StationRule.EyeHeight)) > 1e-3f) onNav = false;
        }
        C("D4 the eye stands on the physical floor", onFloor,
          $"expected {0f + StationRule.EyeHeight}, first pose "
          + (raised.Poses.Count > 0 ? raised.Poses[0].Eye.Z.ToString() : "none"));
        C("D4 the eye is NOT taken from the navmesh triangle", !onNav,
          "a rule that answers both is not discriminating");
        bool publishedOffset = raised.Poses.Count > 0;
        foreach (var p in raised.Poses)
            if (MathF.Abs(p.FloorOffset - (p.NavZ - p.FloorZ)) > 1e-3f) publishedOffset = false;
        C("D4 every pose publishes the navmesh-to-floor offset", publishedOffset, "");

        var noFloor = StationRule.Compose(
            tall, "A1", (_, _) => new StationEnvironment(true, true, true, 0f, NoFloor(0f),
                                                         CELL_A, WS, Dry()));
        C("D4b no physical floor refuses with FloorUnknown",
          Count(noFloor, StationRejection.FloorUnknown) == noFloor.Composed,
          $"{Count(noFloor, StationRejection.FloorUnknown)} of {noFloor.Composed}");
        C("D4b no physical floor accepts nothing", AcceptedPoses(noFloor) == 0,
          $"{AcceptedPoses(noFloor)} accepted");
        C("D4b every refusal carries FloorUnknown and its numbers",
          EveryRefusalCarries(noFloor, StationRejection.FloorUnknown), "");

        // ------------------------------------ B2, the one resolver, pinned against this rule --
        void Pin(string name, bool hasWater, float? height, float? worldDefault, bool exterior,
                 StationWaterVerdict expect)
        {
            var t = Tuple(hasWater, height);
            var ws = worldDefault is null ? (WorldWaterSnap?)null : Snap(worldDefault);
            var got = StationRule.Classify(t, ws, exterior, WS);
            var resolver = CellBoundaryIndex.Resolve(t, ws, exterior);
            bool agrees = got.Verdict switch
            {
                StationWaterVerdict.KnownDryNoSurface =>
                    !resolver.Has || DetectorModel.HeightKind(height) == WaterHeightKind.NegativeWrapped,
                StationWaterVerdict.Unresolved => resolver.Has && resolver.Z is null,
                _ => resolver.Has && resolver.Z is not null
                     && got.SurfaceZ is { } z && MathF.Abs(z - resolver.Z.Value) < 1e-3f,
            };
            C(name, got.Verdict == expect && agrees,
              $"{got.Verdict} (expected {expect}); resolver Has={resolver.Has} Z={resolver.Z}");
        }

        Pin("B2 a real per-cell height is Real", true, 60f, null, true, StationWaterVerdict.Real);
        Pin("B2 the no-value sentinel takes the worldspace default", true, float.MaxValue, 60f, true,
            StationWaterVerdict.WorldspaceDefault);
        Pin("B2 the unsigned-wrapped sentinel takes the worldspace default too",
            true, 4.2949532E+09f, 60f, true, StationWaterVerdict.WorldspaceDefault);
        Pin("B2 a sentinel with no usable worldspace default is Unresolved",
            true, float.MaxValue, float.MaxValue, true, StationWaterVerdict.Unresolved);
        Pin("B2 the negative-wrapped sentinel is known dry, not unresolved",
            true, -2.1474836E+09f, 60f, true, StationWaterVerdict.KnownDryNoSurface);
        Pin("B2 an unmodelled sentinel is Unresolved, never dry",
            true, -3.4028235E+38f, 60f, true, StationWaterVerdict.Unresolved);
        Pin("B2 an interior with no water is known dry", false, null, null, false,
            StationWaterVerdict.KnownDryNoSurface);
        Pin("B2 an EXTERIOR cell that clears HasWater but defers still has the sea",
            false, float.MaxValue, 60f, true, StationWaterVerdict.WorldspaceDefault);
        Pin("B2 an interior that clears HasWater does not inherit a worldspace default",
            false, float.MaxValue, 60f, false, StationWaterVerdict.KnownDryNoSurface);

        // ------------------------------------------- B2b, a worldspace that defers to a parent --
        (float? Default, string? Parent) Chain(string ws) => ws switch
        {
            "CITY" => (float.MaxValue, "TAMRIEL"),
            "TAMRIEL" => (-14000f, null),
            "ORPHAN" => (float.MaxValue, null),
            "LOOP-A" => (float.MaxValue, "LOOP-B"),
            "LOOP-B" => (float.MaxValue, "LOOP-A"),
            _ => (null, null),
        };
        var fromParent = StationRule.ResolveWorldDefault("CITY", Chain);
        C("B2b a city worldspace that defers takes its default from its parent",
          fromParent.Value is { } v && MathF.Abs(v + 14000f) < 1e-3f && fromParent.From == "TAMRIEL"
          && fromParent.Hops == 1,
          $"value {fromParent.Value}, from {fromParent.From}, hops {fromParent.Hops}");
        var orphan = StationRule.ResolveWorldDefault("ORPHAN", Chain);
        C("B2b a chain that ends without a usable default resolves to nothing",
          orphan.Value is null, $"{orphan.Value}");
        var loop = StationRule.ResolveWorldDefault("LOOP-A", Chain);
        C("B2b a cycle in the parent chain is reported, not followed forever",
          loop.Value is null && loop.Cycle, $"value {loop.Value}, cycle {loop.Cycle}");

        // --------------------------------------------------------------- DEFECT 2, under water --
        var wReal60 = StationRule.Classify(Tuple(true, 60f), null, true, WS);
        var drowned = StationRule.Compose(tall, "A1", FlatWater(0f, 0f, wReal60));
        C("D2 a station below a real water height refuses with StationUnderwater",
          Count(drowned, StationRejection.StationUnderwater) == drowned.Composed,
          $"{Count(drowned, StationRejection.StationUnderwater)} of {drowned.Composed}");
        C("D2 an underwater station accepts nothing", AcceptedPoses(drowned) == 0,
          $"{AcceptedPoses(drowned)} accepted");
        C("D2 every refusal carries StationUnderwater and its numbers",
          EveryRefusalCarries(drowned, StationRejection.StationUnderwater), "");

        // --------------------------------------------- DEFECT 3, the engine's no-value sentinel --
        var wSentinel = StationRule.Classify(Tuple(true, float.MaxValue), Snap(60f), true, WS);
        var sentinel = StationRule.Compose(tall, "A1", FlatWater(0f, 0f, wSentinel));
        C("D3a a sentinel cell height inherits the worldspace default, not 'dry'",
          Count(sentinel, StationRejection.StationUnderwater) == sentinel.Composed,
          $"{Count(sentinel, StationRejection.StationUnderwater)} of {sentinel.Composed}");
        C("D3a the sentinel case accepts nothing", AcceptedPoses(sentinel) == 0,
          $"{AcceptedPoses(sentinel)} accepted");

        var wUnknown = StationRule.Classify(Tuple(true, float.MaxValue), Snap(float.MaxValue), true, WS);
        var unknown = StationRule.Compose(tall, "A1", FlatWater(0f, 0f, wUnknown));
        C("D3b an unresolvable surface refuses with WaterUnknown",
          Count(unknown, StationRejection.WaterUnknown) == unknown.Composed,
          $"{Count(unknown, StationRejection.WaterUnknown)} of {unknown.Composed}");
        C("D3b an unknown surface is never read as dry", AcceptedPoses(unknown) == 0,
          $"{AcceptedPoses(unknown)} accepted");
        C("D3b every refusal carries WaterUnknown and its numbers",
          EveryRefusalCarries(unknown, StationRejection.WaterUnknown), "");
        bool carriedRaw = unknown.Poses.Count > 0;
        foreach (var p in unknown.Poses)
            if (p.Accepted || !p.RejectionDetail.Contains("3.4028235E+38",
                                                          StringComparison.OrdinalIgnoreCase))
                carriedRaw = false;
        C("D3b the refusal carries the raw value that could not be resolved", carriedRaw,
          unknown.Poses.Count > 0 ? unknown.Poses[0].RejectionDetail : "no poses");

        // A8: the verdict is asserted on the enum, not on the prose sentence.
        var wDry = StationRule.Classify(Tuple(true, -2.1474836E+09f), Snap(60f), true, WS);
        var dryStations = StationRule.Compose(tall, "A1", FlatWater(0f, 0f, wDry));
        C("D3c a negative-wrapped height is dry, and every station is accepted",
          AcceptedPoses(dryStations) == dryStations.Composed,
          $"{AcceptedPoses(dryStations)} of {dryStations.Composed}");
        bool saidWhy = dryStations.Poses.Count > 0;
        foreach (var p in dryStations.Poses)
        {
            if (p.Water.Verdict != StationWaterVerdict.KnownDryNoSurface) saidWhy = false;
            if (p.Water.Kind != WaterHeightKind.NegativeWrapped) saidWhy = false;
            if (p.Water.SurfaceZ is not null) saidWhy = false;
        }
        C("A8 the pose's water VERDICT says which reading made it dry", saidWhy,
          dryStations.Poses.Count > 0
            ? $"verdict {dryStations.Poses[0].Water.Verdict}, kind {dryStations.Poses[0].Water.Kind}"
            : "no poses");

        var wLow = StationRule.Classify(Tuple(true, -100f), null, true, WS);
        var shore = StationRule.Compose(tall, "A1", FlatWater(0f, 0f, wLow));
        C("D2b a station above a real water height is accepted",
          AcceptedPoses(shore) == shore.Composed, $"{AcceptedPoses(shore)} of {shore.Composed}");
        bool clearance = shore.Poses.Count > 0;
        foreach (var p in shore.Poses)
            if (p.FeetAboveSurface is not { } f || MathF.Abs(f - 100f) > 1e-3f) clearance = false;
        C("D2b the pose publishes how far the feet clear the surface", clearance,
          shore.Poses.Count > 0 ? $"{shore.Poses[0].FeetAboveSurface}" : "no poses");

        // ------------------------------- B1, one ladder, several cells, several water surfaces --
        // The +Y half of the ladder stands in a flooded cell; the -Y half in a dry one.
        var split = StationRule.Compose(tall, "A1",
            (_, yy) => yy > 0f
                ? new StationEnvironment(true, true, true, 0f, Floor(0f, 0f), CELL_B, WS, wReal60)
                : new StationEnvironment(true, true, true, 0f, Floor(0f, 0f), CELL_A, WS, Dry()));
        C("B1 two stations of one ladder over different surfaces get different verdicts",
          AcceptedPoses(split) == split.Composed / 2
          && Count(split, StationRejection.StationUnderwater) == split.Composed / 2,
          $"{AcceptedPoses(split)} accepted, "
          + $"{Count(split, StationRejection.StationUnderwater)} under water, "
          + $"{split.Composed} composed");
        bool ownCell = split.Poses.Count > 0;
        foreach (var p in split.Poses)
        {
            string want = p.Eye.Y > 0f ? CELL_B : CELL_A;
            if (p.Cell != want) ownCell = false;
        }
        C("B1 every pose names the cell the STATION stands in, not the primitive's", ownCell,
          split.Poses.Count > 0 ? $"first pose cell {split.Poses[0].Cell}, primitive cell {tall.Cell}"
                                : "no poses");
        bool ownWater = split.Poses.Count > 0;
        foreach (var p in split.Poses)
        {
            var want = p.Eye.Y > 0f ? StationWaterVerdict.Real : StationWaterVerdict.KnownDryNoSurface;
            if (p.Water.Verdict != want) ownWater = false;
        }
        C("B1 every pose carries its own station's water resolution", ownWater,
          split.Poses.Count > 0 ? $"{split.Poses[0].Water.Verdict}" : "no poses");

        // ---------------------------------------------------------------- A3, a Box's faces --
        C("A3 a PortalBox is composed around one face", StationRule.FacesOf(PrimitiveKind.PortalBox).Length == 1,
          $"{StationRule.FacesOf(PrimitiveKind.PortalBox).Length}");
        C("A3 a Box is composed around both upright axes",
          StationRule.FacesOf(PrimitiveKind.Box).Length == 2,
          $"{StationRule.FacesOf(PrimitiveKind.Box).Length}");
        var boxy = Boxy();
        var boxLadder = StationRule.Compose(boxy, "A1", Flat(0f, 0f));
        C("A3 a Box composes twice a PortalBox's stations",
          boxLadder.Composed == ladder.Composed * 2,
          $"{boxLadder.Composed} against {ladder.Composed}");
        bool bothAxes = false, faceOn = boxLadder.Poses.Count > 0;
        var axesSeen = new HashSet<FaceAxis>();
        foreach (var p in boxLadder.Poses)
        {
            axesSeen.Add(p.FaceAxis);
            if (MathF.Abs(p.Lateral) < 1e-6f && p.AngleToPlaneNormalDeg > 1e-3f) faceOn = false;
        }
        bothAxes = axesSeen.Count == 2;
        C("A3 a Box's stations cover both faces", bothAxes,
          string.Join(", ", axesSeen));
        C("A3 a face-on Box station reads zero to ITS OWN face normal", faceOn, "");
        var fx = StationRule.FaceNormal(Vector3.Zero, FaceAxis.X, +1);
        var fy = StationRule.FaceNormal(Vector3.Zero, FaceAxis.Y, +1);
        C("A3 the X face's normal is perpendicular to the Y face's",
          MathF.Abs(Vector3.Dot(fx, fy)) < 1e-6f, $"{fx} . {fy}");

        // An eye inside or on a primitive has no view shadow to be ranked by: ConvexShadow.FromBox
        // returns no volume there. The predicate is tested directly, because a station composed
        // from a FACE outwards should never be inside - which is itself asserted below.
        var fat = new PrimitiveGeometry("PRIM-FAT", PrimitiveKind.Box,
            new Vector3(4000f, 4000f, 3000f), new Vector3(0f, 0f, -1000f), Vector3.Zero, WS, CELL_A);
        C("A3 an eye in the middle of a Box is inside it",
          StationRule.EyeInside(fat, new Vector3(0f, 0f, -1000f)),
          "");
        C("A3 an eye beyond a Box's face is not inside it",
          !StationRule.EyeInside(fat, new Vector3(0f, 2500f, -1000f)), "");
        C("A3 an eye above a Box's vertical extent is not inside it",
          !StationRule.EyeInside(fat, new Vector3(0f, 0f, 3000f)), "");
        bool composedOutside = boxLadder.Poses.Count > 0;
        foreach (var p in boxLadder.Poses)
            if (StationRule.EyeInside(boxy, p.Eye)) composedOutside = false;
        C("A3 a Box station composed from a face outwards is never inside the Box", composedOutside,
          "the standoff is measured from the face, not the centre - so, like NonLevelGaze, "
          + "EyeInsidePrimitive is a guard against a later ladder change and cannot fire today");

        // ------------------------------------------------------------------- INHERITED refusals --
        var shallow = new PrimitiveGeometry("PRIM-SHORT", PrimitiveKind.PortalBox,
            new Vector3(400f, 8f, 100f), new Vector3(0f, 0f, -5000f), Vector3.Zero, WS, CELL_A);
        var outside = StationRule.Compose(shallow, "A1", Flat(0f, 0f));
        C("INHERITED an eye outside the primitive's vertical extent yields no accepted pose",
          AcceptedPoses(outside) == 0, $"{AcceptedPoses(outside)} accepted");
        C("B2 the extent refusal is counted, with its own reason",
          Count(outside, StationRejection.EyeOutsidePlaneExtent) == outside.Composed,
          $"{Count(outside, StationRejection.EyeOutsidePlaneExtent)} of {outside.Composed}");
        C("B2 every extent refusal carries its reason and its numbers",
          EveryRefusalCarries(outside, StationRejection.EyeOutsidePlaneExtent), "");

        // --------------------------------------------------------------- the accounting closes --
        var half = StationRule.Compose(
            tall, "A2", (_, yy) => yy > 0f
                ? new StationEnvironment(true, true, true, 0f, Floor(0f, 0f), CELL_A, WS, Dry())
                : new StationEnvironment(true, false, false, 0f, NoFloor(0f), CELL_A, WS, Dry()));
        C("Y1 the yield closes: accepted + refused == composed", half.Closes,
          $"{half.Accepted} + {half.RejectedTotal} vs {half.Composed}");
        C("Y1 an uncovered station is counted under NoCoveringTriangle",
          Count(half, StationRejection.NoCoveringTriangle) == half.Composed - AcceptedPoses(half),
          $"{Count(half, StationRejection.NoCoveringTriangle)} counted, "
          + $"{half.Composed - AcceptedPoses(half)} dropped");
        C("Y1 a silent navmesh is counted under NoNavmeshAnswer, not as a miss",
          Count(StationRule.Compose(tall, "A2",
                    (_, _) => new StationEnvironment(false, false, false, 0f, NoFloor(0f),
                                                     CELL_A, WS, Dry())),
                StationRejection.NoNavmeshAnswer) == ladder.Composed,
          "an instrument failure is not a geometric miss");
        C("Y1 the accounting is kept per primitive and per setup",
          half.Primitive == tall.Key && half.Setup == "A2", $"{half.Primitive} / {half.Setup}");

        // ------------------------------------------------------------- B3, reason precedence --
        C("B3 the precedence lists every reason but None exactly once",
          StationRule.Precedence.Length == Enum.GetValues<StationRejection>().Length - 1
          && new HashSet<StationRejection>(StationRule.Precedence).Count == StationRule.Precedence.Length,
          $"{StationRule.Precedence.Length} of {Enum.GetValues<StationRejection>().Length - 1}");
        C("B3 an instrument failure outranks a geometry refusal",
          StationRule.FirstByPrecedence(new[] { StationRejection.EyeOutsidePlaneExtent,
                                                StationRejection.NoNavmeshAnswer })
          == StationRejection.NoNavmeshAnswer,
          $"{StationRule.FirstByPrecedence(new[] { StationRejection.EyeOutsidePlaneExtent, StationRejection.NoNavmeshAnswer })}");
        C("B3 an unresolved surface outranks being under one",
          StationRule.FirstByPrecedence(new[] { StationRejection.StationUnderwater,
                                                StationRejection.WaterUnknown })
          == StationRejection.WaterUnknown,
          $"{StationRule.FirstByPrecedence(new[] { StationRejection.StationUnderwater, StationRejection.WaterUnknown })}");
        C("B3 a missing floor outranks a water verdict",
          StationRule.FirstByPrecedence(new[] { StationRejection.StationUnderwater,
                                                StationRejection.FloorUnknown })
          == StationRejection.FloorUnknown,
          $"{StationRule.FirstByPrecedence(new[] { StationRejection.StationUnderwater, StationRejection.FloorUnknown })}");
        C("B3 nothing wrong is None",
          StationRule.FirstByPrecedence(Array.Empty<StationRejection>()) == StationRejection.None,
          "");
        // A station that is BOTH under water and outside the extent is counted under the water.
        var both = StationRule.Compose(shallow, "A1", FlatWater(0f, 0f, wReal60));
        C("B3 a station failing two conditions is counted under the more decisive one",
          Count(both, StationRejection.StationUnderwater) == both.Composed
          && Count(both, StationRejection.EyeOutsidePlaneExtent) == 0,
          $"underwater {Count(both, StationRejection.StationUnderwater)}, "
          + $"extent {Count(both, StationRejection.EyeOutsidePlaneExtent)}, of {both.Composed}");

        // ----------------------------------------------------------- the level-gaze guard fires --
        if (ladder.Poses.Count > 0)
        {
            var p0 = ladder.Poses[0];
            var tilted = p0 with { LookAt = new Vector3(p0.LookAt.X, p0.LookAt.Y, p0.Eye.Z - 500f) };
            C("G1 the guard refuses a pose whose gaze is not level",
              StationRule.CheckLevel(tilted) == StationRejection.NonLevelGaze,
              $"{StationRule.CheckLevel(tilted)}");
            var flat = p0 with { LookAt = new Vector3(p0.LookAt.X, p0.LookAt.Y, p0.Eye.Z) };
            C("G1 the guard passes a level pose",
              StationRule.CheckLevel(flat) == StationRejection.None,
              $"{StationRule.CheckLevel(flat)}");
        }
        else
        {
            C("G1 the guard refuses a pose whose gaze is not level", false, "no pose to tilt");
            C("G1 the guard passes a level pose", false, "no pose to level");
        }

        // ------------------------------------------ C3, the pitch is measured, not asserted --
        float pLevel = StationRule.Pitch(new Vector3(0f, 0f, 0f), new Vector3(0f, 100f, 0f));
        C("INHERITED the pitch of a level eye and aim point is zero", MathF.Abs(pLevel) < 1e-4f,
          $"{pLevel:0.####} deg");
        // 50 up over 100 along is atan(0.5) = 26.565 deg, computed here from the geometry rather
        // than copied from the rule - a constant copied from the code under test proves nothing.
        float pUp = StationRule.Pitch(new Vector3(0f, 0f, 0f), new Vector3(0f, 100f, 50f));
        float pWant = MathF.Atan2(50f, 100f) * 180f / MathF.PI;
        C("C3 the pitch of a raised aim point is the angle the eye and that point give",
          MathF.Abs(pUp - pWant) < 1e-3f, $"{pUp:0.###} deg, expected {pWant:0.###} deg");
        bool pitchPublished = ladder.Poses.Count > 0;
        foreach (var p in ladder.Poses)
            if (MathF.Abs(p.PitchDeg - StationRule.Pitch(p.Eye, p.LookAt)) > 1e-4f) pitchPublished = false;
        C("C3b every composed pose publishes the pitch its own eye and aim point give",
          pitchPublished, "a pose whose published pitch is a constant is not a measurement");

        // -------------------------------------- the XY -> cell -> water lookup (L1 - L7) --
        // L1: the lookup's grid square has to be the one the FLOOR is filed by, or a station gets
        // one cell's water and another cell's floor. FloorFeed.GridOf floors toward minus infinity.
        var gNeg = StationLookup.GridOf(-10f, -10f);
        var gFeed = FloorFeed.GridOf(new Vec3(-10f, -10f, 0f));
        C("L1 the lookup's grid square is the one the floor is filed by, west of the origin",
          gNeg == gFeed && gNeg == (-1, -1), $"lookup {gNeg}, FloorFeed {gFeed}");
        var gPos = StationLookup.GridOf(5000f, 9000f);
        C("INHERITED the lookup's grid square is right east and north of the origin",
          gPos == (1, 2) && gPos == FloorFeed.GridOf(new Vec3(5000f, 9000f, 0f)), $"{gPos}");

        // L2: a station 10 u west of the border stands in the WEST cell and takes ITS surface.
        var west = StationLookup.At(-10f, 100f, Squares, Worlds, TAMRIEL);
        C("L2 a station across a cell border takes the NEIGHBOUR's cell and its water",
          west.Cell == "CELL-WEST" && west.Water.Verdict == StationWaterVerdict.Real
          && west.Water.SurfaceZ is { } ws1 && MathF.Abs(ws1 - 60f) < 1e-3f,
          $"cell {west.Cell}, {west.Water.Verdict}, surface {west.Water.SurfaceZ}");
        var east = StationLookup.At(10f, 100f, Squares, Worlds, TAMRIEL);
        C("INHERITED a station inside its own cell takes that cell's water",
          east.Cell == "CELL-EAST" && east.Water.SurfaceZ is { } ws2 && MathF.Abs(ws2 + 100f) < 1e-3f,
          $"cell {east.Cell}, surface {east.Water.SurfaceZ}");

        // L4: a city worldspace that defers takes its default from its parent, and the pose says
        // WHICH worldspace the height came from, so an inherited surface is visible not implied.
        var defer = StationLookup.At(MidOf(1), 100f, Squares, Worlds, TAMRIEL);
        C("L4 a cell that defers takes the worldspace chain's default, naming where it came from",
          defer.Water.Verdict == StationWaterVerdict.WorldspaceDefault
          && defer.Water.SurfaceZ is { } ws3 && MathF.Abs(ws3 + 14000f) < 1e-3f
          && defer.Water.FromWorldspace == TAMRIEL,
          $"{defer.Water.Verdict}, surface {defer.Water.SurfaceZ}, from {defer.Water.FromWorldspace}");

        // L3: the C1 row. HasWater clear, a sentinel height, and a worldspace whose record could
        // not be READ - which the one resolver, handed a null snap, answers "dry" for.
        var unread = StationLookup.At(MidOf(2), 100f, Squares, Worlds, TAMRIEL);
        C("L3 a worldspace that could not be READ is Unresolved, never dry",
          unread.Water.Verdict == StationWaterVerdict.Unresolved,
          $"{unread.Water.Verdict}: {unread.Water.How}");
        C("L3 the refusal names the worldspace that could not be read",
          unread.Water.How.Contains(ORPHANWS, StringComparison.Ordinal), unread.Water.How);

        // L5: the record is there; the PARENT it points at is not.
        var child = StationLookup.At(MidOf(3), 100f, Squares, Worlds, TAMRIEL);
        C("L5 a parent link that could not be read is Unresolved, and names the link",
          child.Water.Verdict == StationWaterVerdict.Unresolved
          && child.Water.How.Contains(GHOSTWS, StringComparison.Ordinal),
          $"{child.Water.Verdict}: {child.Water.How}");

        // L6: the other arm of the same condition. An unreadable worldspace cannot change a cell
        // that states its own real height, so it must NOT refuse one.
        var solo = StationLookup.At(MidOf(4), 100f, Squares, Worlds, TAMRIEL);
        C("INHERITED an unreadable worldspace does not refuse a cell that states its own height",
          solo.Water.Verdict == StationWaterVerdict.Real
          && solo.Water.SurfaceZ is { } ws4 && MathF.Abs(ws4 - 60f) < 1e-3f,
          $"{solo.Water.Verdict}, surface {solo.Water.SurfaceZ}");

        // L7: a grid square the order defines no cell for.
        var nowhere = StationLookup.At(MidOf(9), 100f, Squares, Worlds, TAMRIEL);
        C("L7 a grid square the order does not define is Unresolved, never dry",
          nowhere.Water.Verdict == StationWaterVerdict.Unresolved,
          $"{nowhere.Water.Verdict}: {nowhere.Water.How}");
        C("L7 the refusal names the grid square that is not defined",
          nowhere.Water.How.Contains("9,0", StringComparison.Ordinal), nowhere.Water.How);

        // The two rows the reviewer asked to be PINNED with the verdict and the reason, because
        // both are reachable only when the caller has already refused: the mode never hands the
        // resolver a null snap for a worldspace it failed to read (L3 above), and it carries the
        // raw value in the refusal detail. What the ONE resolver does with them is recorded here
        // rather than argued about, and the README states it.
        Pin("B2 HasWater clear with a sentinel height and NO worldspace snap is read as dry",
            false, float.MaxValue, null, true, StationWaterVerdict.KnownDryNoSurface);
        Pin("B2 HasWater clear with an unmodelled sentinel is read as dry, not refused",
            false, -3.4028235E+38f, 60f, true, StationWaterVerdict.KnownDryNoSurface);

        // ------------------------------------------------------- the pose file's own writer --
        var hdr = new Dictionary<string, object?> { ["label"] = "proof" };
        string why4 = ""; bool eyeNull = false;
        try
        {
            using var d4 = JsonDocument.Parse(StationPoseFile.Render(hdr, new[] { noFloor }));
            eyeNull = d4.RootElement.GetProperty("poses").GetArrayLength() > 0;
            foreach (var pe in d4.RootElement.GetProperty("poses").EnumerateArray())
                if (pe.GetProperty("eye")[2].ValueKind != JsonValueKind.Null) eyeNull = false;
        }
        catch (Exception ex) { why4 = ex.GetType().Name + ": " + ex.Message; }
        C("C4 the writer publishes null for an eye a refused pose never had", eyeNull,
          why4.Length > 0 ? why4 : "every refused pose's eye z is null");

        string why4b = ""; bool eyeNumber = false;
        try
        {
            using var d5 = JsonDocument.Parse(StationPoseFile.Render(hdr, new[] { ladder }));
            var first = d5.RootElement.GetProperty("poses")[0].GetProperty("eye")[2];
            eyeNumber = first.ValueKind == JsonValueKind.Number
                        && MathF.Abs(first.GetSingle() - StationRule.EyeHeight) < 1e-3f;
        }
        catch (Exception ex) { why4b = ex.GetType().Name + ": " + ex.Message; }
        C("INHERITED the writer publishes an accepted pose's eye as a number", eyeNumber,
          why4b.Length > 0 ? why4b : "");

        var bad = new StationYield { Primitive = "PRIM-BAD", Setup = "A1" };
        bad.Composed = 3; bad.Accepted = 1;            // 1 + 0 != 3: the table would be wrong
        bool refusedBad = false; string badDetail = "";
        try { badDetail = StationPoseFile.Render(hdr, new[] { bad })[..40]; }
        catch (InvalidOperationException ex) { refusedBad = true; badDetail = ex.Message; }
        C("P1 the writer refuses a yield whose accounting does not close", refusedBad, badDetail);

        string why4c = ""; bool headerOk = false;
        try
        {
            var second = StationRule.Compose(tall, "A2", Flat(0f, 0f));
            using var d6 = JsonDocument.Parse(StationPoseFile.Render(hdr, new[] { ladder, second }));
            var yj = d6.RootElement.GetProperty("yield");
            headerOk = yj.GetProperty("bySetup").TryGetProperty("A1", out var a1)
                       && yj.GetProperty("bySetup").TryGetProperty("A2", out _)
                       && a1.GetProperty("composed").GetInt32() == ladder.Composed
                       && a1.GetProperty("primitivesWithAStation").GetInt32() == 1
                       && yj.GetProperty("byPrimitive").GetProperty(tall.Key)
                            .GetProperty("A2").GetProperty("accepted").GetInt32() == second.Accepted;
        }
        catch (Exception ex) { why4c = ex.GetType().Name + ": " + ex.Message; }
        C("INHERITED the writer publishes the yield per primitive and per setup", headerOk, why4c);

        // ------------------------------------- G1, WHERE the floor read is anchored --
        // The floor rule itself is core's and unchanged; what is under test is the height the band
        // is opened around. The fixture drives the REAL NavmeshFloor.Read with synthetic surfaces,
        // so a case cannot pass against a restatement of the rule.
        FloorReading Ground(float anchorZ, (float Z, bool Up)[] surfaces, float? landZ)
            => NavmeshFloor.Read(anchorZ, surfaces, landZ);

        // A navmesh authored 60 u UNDER the landscape, with a placed mesh 100 u below the ground.
        var buriedSurfaces = new[] { (-100f, true) };
        var buried = StationGround.Read(z => Ground(z, buriedSurfaces, 0f), -60f, 0f);
        C("G1 a surface below the terrain is never chosen as the floor",
          buried.Floor.Found && MathF.Abs(buried.Floor.FloorZ) < 1e-3f
          && buried.Floor.Source == FloorSource.Land,
          $"{buried.Floor.Source} at {buried.Floor.FloorZ:0.###} (the terrain is at 0, "
          + "the only mesh at -100)");
        C("G1 a buried navmesh anchors the floor read on the terrain",
          buried.Anchor == FloorAnchor.Terrain && MathF.Abs(buried.AnchorZ) < 1e-3f
          && MathF.Abs(buried.NavmeshBurial - 60f) < 1e-3f && !buried.TerrainIsAnotherStorey,
          $"anchor {buried.Anchor} at {buried.AnchorZ:0.###}, burial {buried.NavmeshBurial:0.###}, "
          + $"another storey {buried.TerrainIsAnotherStorey}");

        // A street mesh standing ON that ground, within the step reach: it must still win.
        var onGround = StationGround.Read(z => Ground(z, new[] { (30f, true) }, 0f), -60f, 0f);
        C("G1 a mesh within the step reach above the terrain still wins",
          onGround.Floor.Found && MathF.Abs(onGround.Floor.FloorZ - 30f) < 1e-3f
          && onGround.Floor.Source == FloorSource.Mesh,
          $"{onGround.Floor.Source} at {onGround.Floor.FloorZ:0.###}");

        // The terrain a little above the navmesh is not a BURIED navmesh - it is the ordinary
        // within-cell texture - but the read still stands on the higher of the two.
        var shallowBury = StationGround.Read(z => Ground(z, new[] { (-5f, true) }, 10f), 0f, 10f);
        C("G1 a terrain a few units above the navmesh is not another storey",
          shallowBury.Anchor == FloorAnchor.Terrain && !shallowBury.TerrainIsAnotherStorey
          && MathF.Abs(shallowBury.NavmeshBurial - 10f) < 1e-3f,
          $"anchor {shallowBury.Anchor}, burial {shallowBury.NavmeshBurial:0.###}, "
          + $"another storey {shallowBury.TerrainIsAnotherStorey}");

        var normal = StationGround.Read(z => Ground(z, new[] { (-5f, true) }, -20f), 0f, -20f);
        C("INHERITED an unburied station reads the same floor it always did",
          normal.Anchor == FloorAnchor.Navmesh && normal.NavmeshBurial == 0f
          && normal.Floor.Found && MathF.Abs(normal.Floor.FloorZ + 5f) < 1e-3f,
          $"anchor {normal.Anchor}, floor {normal.Floor.FloorZ:0.###}");
        var noLand = StationGround.Read(z => Ground(z, new[] { (-5f, true) }, null), 0f, null);
        C("INHERITED a station with no terrain reading is anchored on the navmesh",
          noLand.Anchor == FloorAnchor.Navmesh && noLand.Floor.Found
          && MathF.Abs(noLand.Floor.FloorZ + 5f) < 1e-3f,
          $"anchor {noLand.Anchor}, floor {noLand.Floor.FloorZ:0.###}");

        // The pose and the table have to carry it, or a whole class of station is invisible.
        var buriedLadder = StationRule.Compose(tall, "A3",
            (_, _) => new StationEnvironment(true, true, true, -60f, Floor(-60f, 0f),
                                             CELL_A, WS, Dry(), FloorAnchor.Terrain, 60f, true));
        bool carried = buriedLadder.Poses.Count > 0;
        foreach (var p in buriedLadder.Poses)
            if (p.FloorAnchor != FloorAnchor.Terrain || MathF.Abs(p.NavmeshBurial - 60f) > 1e-3f
                || !p.TerrainIsAnotherStorey) carried = false;
        C("G1 every composed pose publishes the anchor its ground was read at",
          carried, buriedLadder.Poses.Count > 0
            ? $"{buriedLadder.Poses[0].FloorAnchor}, burial {buriedLadder.Poses[0].NavmeshBurial}"
            : "no poses");

        string whyG = ""; bool countedBuried = false;
        try
        {
            using var dG = JsonDocument.Parse(StationPoseFile.Render(hdr, new[] { buriedLadder }));
            var yG = dG.RootElement.GetProperty("yield");
            countedBuried =
                yG.GetProperty("bySetup").GetProperty("A3")
                  .GetProperty("stationsAnchoredOnTerrain").GetInt32() == buriedLadder.Composed
                && yG.GetProperty("byPrimitive").GetProperty(tall.Key).GetProperty("A3")
                     .GetProperty("stationsAnchoredOnTerrain").GetInt32() == buriedLadder.Composed;
        }
        catch (Exception ex) { whyG = ex.GetType().Name + ": " + ex.Message; }
        C("G1 the yield counts the stations whose navmesh is buried, per primitive and per setup",
          countedBuried, whyG.Length > 0 ? whyG : $"of {buriedLadder.Composed} composed");

        // -------------------------- G2, the anchor needs a BOUND: one storey, not two --
        // A navmesh a whole storey below the landscape is not ground the landscape was raised
        // over. Measured on the real order (the run record's storey diagnosis): a city mod's
        // navmesh override about 3,080 u under the ground, in empty space - a ray probe there
        // found nothing within reach in any direction. The terrain above it says nothing about
        // whether a body can stand where the navmesh is.
        C("G2 the bound is the engine-measured standing height, not the step reach",
          MathF.Abs(StationGround.StandingHeight
                    - ActorBand.ControllerTop(ActorBand.ScaleOneHeight)) < 1e-3f
          && StationGround.StandingHeight > DetectorModel.StepHeight,
          $"{StationGround.StandingHeight:0.###} u, against a step of {DetectorModel.StepHeight:0.###}");

        var storey = StationGround.Read(z => Ground(z, new[] { (-3050f, true) }, 0f), -3000f, 0f);
        C("G2 a navmesh a whole storey below the landscape does not anchor on the terrain",
          storey.Anchor == FloorAnchor.Navmesh && MathF.Abs(storey.AnchorZ + 3000f) < 1e-3f,
          $"anchor {storey.Anchor} at {storey.AnchorZ:0.###}");
        C("G2 the storey gap is published, and the flag says the terrain is another storey",
          storey.TerrainIsAnotherStorey && MathF.Abs(storey.NavmeshBurial - 3000f) < 1e-3f,
          $"another storey {storey.TerrainIsAnotherStorey}, gap {storey.NavmeshBurial:0.###}");
        C("G2 the floor beside a storey-deep navmesh is read at the navmesh, not at the ground",
          storey.Floor.Found && MathF.Abs(storey.Floor.FloorZ + 3050f) < 1e-3f
          && storey.Floor.Source == FloorSource.Mesh,
          $"{storey.Floor.Source} at {storey.Floor.FloorZ:0.###}");

        // The other arm of the same condition, one unit under the bound.
        float justUnder = StationGround.StandingHeight - 1f;
        var nearBound = StationGround.Read(z => Ground(z, new[] { (-5f, true) }, justUnder), 0f, justUnder);
        C("G2 a burial just under an actor's height still anchors on the terrain",
          nearBound.Anchor == FloorAnchor.Terrain && !nearBound.TerrainIsAnotherStorey,
          $"anchor {nearBound.Anchor}, another storey {nearBound.TerrainIsAnotherStorey}, "
          + $"gap {nearBound.NavmeshBurial:0.###}");

        string whyS = ""; bool countedStorey = false;
        var storeyLadder = StationRule.Compose(tall, "A4",
            (_, _) => new StationEnvironment(true, true, true, -60f, Floor(-60f, 0f),
                                             CELL_A, WS, Dry(), FloorAnchor.Navmesh, 3000f, true));
        try
        {
            using var dS = JsonDocument.Parse(StationPoseFile.Render(hdr, new[] { storeyLadder }));
            var yS = dS.RootElement.GetProperty("yield");
            countedStorey =
                yS.GetProperty("bySetup").GetProperty("A4")
                  .GetProperty("stationsWhereTerrainIsAnotherStorey").GetInt32() == storeyLadder.Composed
                && yS.GetProperty("byPrimitive").GetProperty(tall.Key).GetProperty("A4")
                     .GetProperty("stationsWhereTerrainIsAnotherStorey").GetInt32() == storeyLadder.Composed;
        }
        catch (Exception ex) { whyS = ex.GetType().Name + ": " + ex.Message; }
        C("G2 the yield counts the stations where the terrain is another storey",
          countedStorey, whyS.Length > 0 ? whyS : $"of {storeyLadder.Composed} composed");

        // ================================================ M, the observing mode (GATE-A §3.2) ==
        // Stations that can observe a MISS: a ring of bearings around a clean primitive, the
        // player constraints kept exactly, and content inside the predicted shadow counted. The
        // known answers for M1, M3 and M5 come from an INDEPENDENT numpy oracle
        // (_claude/runs/2026-09-25-occlusion-arming-miss-tilted/partB/oracle_m5.py, which samples
        // eye->surface segments through the rectangle and shares no code with ConvexShadow); the
        // numbers below are copied from its output file oracle-m5.json and say so.
        //
        // Recorded RED at the API stub (ComposeObserving returning the face-on rule's output), so
        // each case below fails on a WRONG ANSWER, not on a missing member.
        var wall = new PrimitiveGeometry("PRIM-WALL", PrimitiveKind.PortalBox,
            new Vector3(400f, 8f, 400f), new Vector3(0f, 0f, 200f), Vector3.Zero, WS, CELL_A);
        ContentBound Ball(string key, float x, float y, float z, float r)
            => new(key, new SphereBound(new Vector3(x, y, z), r));
        var cIn = Ball("IN", 150f, -300f, 150f, 30f);
        var cFront = Ball("FRONT", 60f, 100f, 125f, 20f);
        var cBeside = Ball("BESIDE", -900f, -300f, 150f, 30f);
        var cStraddle = Ball("STRADDLE", -615f, -300f, 150f, 60f);
        var cFree = Ball("FREE", 900f, -500f, 150f, 30f);
        bool Holds(StationPose p, string key) => p.ContentKeys is { } ks && ks.Contains(key);
        int Oblique(StationYield y, Func<StationPose, bool> also)
        {
            int n = 0;
            foreach (var p in y.Poses) if (p.AngleToPlaneNormalDeg >= 30f && also(p)) n++;
            return n;
        }
        StationPose? AtBearing(StationYield y, float bearing, float lat, float k)
        {
            foreach (var p in y.Poses)
                if (p.RingBearingDeg is { } b && MathF.Abs(b - bearing) < 1e-3f
                    && MathF.Abs(p.Lateral - lat) < 1e-6f && MathF.Abs(p.K - k) < 1e-6f) return p;
            return null;
        }
        float[] One(float v) => new[] { v };

        // ---- M1 the wall panel: face-on sees nothing oblique; the ring does ------------------
        var wallFace = StationRule.Compose(wall, "M1", Flat(0f, 0f));
        float worstFace = 0f;
        foreach (var p in wallFace.Poses) worstFace = MathF.Max(worstFace, p.AngleToPlaneNormalDeg);
        C("M1 the face-on rule composes no pose 30 deg or more off the normal",
          wallFace.Poses.Count > 0 && worstFace < 30f, $"worst {worstFace:0.###} deg over {wallFace.Poses.Count}");
        var wallRing = StationRule.ComposeObserving(wall, "M1", Flat(0f, 0f), new[] { cFree });
        int obliqueFree = Oblique(wallRing, p => p.Accepted && Holds(p, "FREE"));
        // oracle-m5.json M1.obliqueAcceptedContaining = 12 (bearings 300 x3 laterals, 330 at +0.35,
        // each at the three k, which all floor to the same 250 u standoff here)
        C("M1 the observing mode accepts the 12 oblique poses whose shadow holds the free-edge ref",
          obliqueFree == 12 && wallRing.Accepted == 12,
          $"{obliqueFree} oblique accepted holding FREE, {wallRing.Accepted} accepted; oracle 12 and 12");
        bool nearNormalRefused = true; int nearNormal = 0;
        foreach (var p in wallRing.Poses)
            if (p.AngleToPlaneNormalDeg < 30f)
            {
                nearNormal++;
                if (p.Accepted || p.RejectionReason != StationRejection.NoContentInShadow) nearNormalRefused = false;
            }
        // oracle-m5.json M1.under30degContaining = 0
        C("M1 every ring pose under 30 deg is refused NoContentInShadow (oracle: none holds FREE)",
          nearNormal > 0 && nearNormalRefused, $"{nearNormal} pose(s) under 30 deg");

        // ---- M2 the two geometric refusals, and the content refusal -------------------------
        var edge = StationRule.ComposeObserving(wall, "M2", Flat(0f, 0f), new[] { cIn },
                                                One(1f), One(0f), One(85f));
        var e0 = edge.Poses.Count == 1 ? edge.Poses[0] : null;
        C("M2 a pose 85 deg off the normal is refused EdgeOn, with its numbers",
          e0 is { Accepted: false, RejectionReason: StationRejection.EdgeOn }
          && e0.RejectionDetail.Contains("85", StringComparison.Ordinal),
          e0 is null ? $"{edge.Poses.Count} poses composed, expected 1"
                     : $"{e0.RejectionReason}: {e0.RejectionDetail}");
        C("M2 the EdgeOn refusal is counted and the yield closes",
          Count(edge, StationRejection.EdgeOn) == 1 && edge.Composed == 1 && edge.Closes,
          $"EdgeOn {Count(edge, StationRejection.EdgeOn)}, composed {edge.Composed}");
        var notEdge = StationRule.ComposeObserving(wall, "M2", Flat(0f, 0f), new[] { cIn },
                                                   One(1f), One(0f), One(75f));
        var n0p = notEdge.Poses.Count == 1 ? notEdge.Poses[0] : null;
        C("M2 the other arm: a pose 75 deg off the normal is not refused EdgeOn",
          n0p is { RingBearingDeg: { } b75 } && MathF.Abs(b75 - 75f) < 1e-3f
          && n0p.RejectionReason != StationRejection.EdgeOn,
          n0p is null ? $"{notEdge.Poses.Count} poses composed, expected 1"
                      : $"bearing {n0p.RingBearingDeg}, {n0p.RejectionReason}");
        var high = new PrimitiveGeometry("PRIM-HIGH", PrimitiveKind.PortalBox,
            new Vector3(400f, 8f, 400f), new Vector3(0f, 0f, 1200f), Vector3.Zero, WS, CELL_A);
        var above = StationRule.ComposeObserving(high, "M2", Flat(0f, 0f),
            new[] { Ball("HIGH-IN", 150f, -300f, 1400f, 30f) }, One(1f), One(0f), One(30f));
        C("M2 a panel 880 u above the eye at a 250 u standoff is refused ShadowOutOfFrame",
          above.Composed == 1 && Count(above, StationRejection.ShadowOutOfFrame) == 1
          && above.Poses[0].RejectionDetail.Length > 0,
          above.Poses.Count > 0 ? $"{above.Poses[0].RejectionReason}: {above.Poses[0].RejectionDetail}"
                                : "no poses");
        var besideOnly = StationRule.ComposeObserving(wall, "M2", Flat(0f, 0f), new[] { cBeside },
                                                      One(1f), One(0f), One(30f));
        C("M2 a pose whose shadow holds no content is refused NoContentInShadow",
          besideOnly.Composed == 1 && Count(besideOnly, StationRejection.NoContentInShadow) == 1,
          besideOnly.Poses.Count > 0
            ? $"{besideOnly.Poses[0].RejectionReason}: {besideOnly.Poses[0].RejectionDetail}" : "no poses");

        // ---- M3 raised: a station standing above the panel's top ------------------------------
        var low = new PrimitiveGeometry("PRIM-LOW", PrimitiveKind.PortalBox,
            new Vector3(400f, 8f, 200f), new Vector3(0f, 0f, 100f), Vector3.Zero, WS, CELL_A);
        var cLow = Ball("LOWIN", 150f, -200f, 60f, 15f);
        var lowFace = StationRule.Compose(low, "M3", Flat(150f, 150f));
        C("M3 the face-on rule refuses the raised ground EyeOutsidePlaneExtent",
          lowFace.Composed > 0 && Count(lowFace, StationRejection.EyeOutsidePlaneExtent) == lowFace.Composed,
          $"{Count(lowFace, StationRejection.EyeOutsidePlaneExtent)} of {lowFace.Composed}");
        var lowRing = StationRule.ComposeObserving(low, "M3", Flat(150f, 150f), new[] { cLow });
        // oracle-m5.json M3.posesContainingNotEdgeOnInFrame = 42 (14 bearing/lateral keys x 3 k)
        C("M3 the observing mode accepts the raised ground (oracle 42) and refuses none of it for the extent",
          lowRing.Accepted == 42 && Count(lowRing, StationRejection.EyeOutsidePlaneExtent) == 0,
          $"{lowRing.Accepted} accepted (oracle 42), "
          + $"{Count(lowRing, StationRejection.EyeOutsidePlaneExtent)} refused for the extent");
        var lowFace0 = lowFace.Poses.Find(p => p.Side == 1 && p.Lateral == 0f && p.K == 1f);
        var lowRing0 = AtBearing(lowRing, 0f, 0f, 1f);
        C("M3 it is the same station: bearing 0 is the face-on +1 pose's eye, accepted in one mode only",
          lowFace0 is not null && lowRing0 is not null
          && Vector3.Distance(lowFace0.Eye, lowRing0.Eye) < 1e-3f
          && !lowFace0.Accepted && lowRing0.Accepted,
          lowRing0 is null ? "no bearing-0 pose in the observing yield"
                           : $"face-on {lowFace0?.Eye} {lowFace0?.RejectionReason}, observing {lowRing0.Eye} {lowRing0.Accepted}");

        // ---- M4 the player constraints hold for oblique poses, reason and precedence ----------
        void Player(string name, Func<float, float, StationEnvironment> env, StationRejection want)
        {
            var face = StationRule.Compose(wall, "M4", env);
            var ring = StationRule.ComposeObserving(wall, "M4", env, new[] { cIn });
            int oblique = Oblique(ring, _ => true);
            C("M4 " + name + ": oblique poses are refused as face-on ones are, for the same reason",
              oblique > 0 && Count(face, want) == face.Composed
              && Count(ring, want) == ring.Composed && AcceptedPoses(ring) == 0 && EveryRefusalCarries(ring, want),
              $"{oblique} oblique; face-on {Count(face, want)}/{face.Composed}, "
              + $"observing {Count(ring, want)}/{ring.Composed} {want}");
        }
        Player("under water", FlatWater(0f, 0f, wReal60), StationRejection.StationUnderwater);
        Player("unknown water", FlatWater(0f, 0f, wUnknown), StationRejection.WaterUnknown);
        Player("no physical floor",
               (_, _) => new StationEnvironment(true, true, true, 0f, NoFloor(0f), CELL_A, WS, Dry()),
               StationRejection.FloorUnknown);
        Player("every covering triangle deleted",
               (_, _) => new StationEnvironment(true, true, false, 0f, NoFloor(0f), CELL_A, WS, Dry()),
               StationRejection.AllCoveringTrianglesDeleted);

        // ---- M5 the content test, against the independent oracle ------------------------------
        var m5 = StationRule.ComposeObserving(wall, "M5", Flat(0f, 0f),
            new[] { cIn, cFront, cBeside, cStraddle }, One(1f), One(0f), One(30f));
        var m5p = m5.Poses.Count == 1 ? m5.Poses[0] : null;
        // oracle-m5.json M5.pose.eye = [125.0, 216.5064, 120.5], angle 30.0
        C("M5 the pose is the oracle's eye at bearing 30",
          m5p is { RingBearingDeg: { } b30 } && MathF.Abs(b30 - 30f) < 1e-3f
          && Vector3.Distance(m5p.Eye, new Vector3(125f, 216.5064f, 120.5f)) < 1e-2f
          && MathF.Abs(m5p.AngleToPlaneNormalDeg - 30f) < 1e-2f,
          m5p is null ? $"{m5.Poses.Count} poses, expected 1"
                      : $"bearing {m5p.RingBearingDeg}, eye {m5p.Eye}, angle {m5p.AngleToPlaneNormalDeg:0.###}");
        C("M5 a ref inside the shadow counts (oracle: Inside, 40000 of 40000 segments blocked)",
          m5p is not null && Holds(m5p, "IN"), m5p?.ContentKeys is { } k1 ? string.Join(",", k1) : "no content keys");
        C("M5 a ref in front of the panel does not count (oracle: Outside)",
          m5p?.ContentKeys is not null && !Holds(m5p, "FRONT"),
          m5p?.ContentKeys is { } k2 ? string.Join(",", k2) : "no content keys");
        C("M5 a ref beside the silhouette does not count (oracle: Outside)",
          m5p?.ContentKeys is not null && !Holds(m5p, "BESIDE"),
          m5p?.ContentKeys is { } k3 ? string.Join(",", k3) : "no content keys");
        C("M5 a straddling ref does not count, though its centre's segment is blocked (oracle: Crossing)",
          m5p?.ContentKeys is not null && !Holds(m5p, "STRADDLE"),
          m5p?.ContentKeys is { } k4 ? string.Join(",", k4) : "no content keys");
        C("M5 the pose counts exactly the oracle's one",
          m5p is { ContentInShadow: 1, Accepted: true }, $"{m5p?.ContentInShadow} counted, accepted {m5p?.Accepted}");

        // ---- M6 the accounting closes over the new refusals ------------------------------------
        C("M6 the observing yield closes over 12 bearings x 3 k x 3 laterals, new refusals included",
          wallRing.Closes && wallRing.Composed == 108
          && Count(wallRing, StationRejection.EdgeOn) > 0
          && Count(wallRing, StationRejection.NoContentInShadow) > 0,
          $"{wallRing.Accepted} + {wallRing.RejectedTotal} vs {wallRing.Composed}; "
          + $"EdgeOn {Count(wallRing, StationRejection.EdgeOn)}, "
          + $"NoContentInShadow {Count(wallRing, StationRejection.NoContentInShadow)}");
        var badRing = new StationYield { Primitive = "PRIM-WALL", Setup = "M6" };
        badRing.Composed = 108; badRing.Accepted = 12;
        badRing.Rejected[StationRejection.EdgeOn] = 24;       // 12 + 24 != 108
        bool refusedRing = false;
        try { StationPoseFile.Render(hdr, new[] { badRing }); }
        catch (InvalidOperationException) { refusedRing = true; }
        C("INHERITED M6 the writer refuses an observing yield that does not close", refusedRing, "");

        // ---- M7 the face-on output is pinned to the byte ---------------------------------------
        string faceOnDoc = StationPoseFile.Render(hdr, new[] { ladder, boxLadder, split, raised,
                                                               noFloor, drowned, unknown, outside });
        string faceOnSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(faceOnDoc)));
        // Recorded at the API stub over the unchanged face-on rule; a change to the face-on
        // output - a new pose field included - moves it.
        C("M7 the face-on pose file over eight fixtures is byte-identical to the base",
          faceOnSha == M7FaceOnSha256, $"{faceOnSha} ({faceOnDoc.Length} chars)");

        // ---- M8 ranking ------------------------------------------------------------------------
        var ranked = StationRule.ComposeObserving(wall, "M8", Flat(0f, 0f), new[] { cIn, cFree });
        var acc = ranked.Poses.FindAll(p => p.Accepted);
        acc.Sort((a, b) => (a.ObservingRank ?? int.MaxValue).CompareTo(b.ObservingRank ?? int.MaxValue));
        bool rankOk = acc.Count > 1;
        for (int i = 0; i < acc.Count; i++)
        {
            if (acc[i].ObservingRank != i + 1) { rankOk = false; break; }
            if (i == 0) continue;
            int ca = acc[i - 1].ContentInShadow ?? -1, cb = acc[i].ContentInShadow ?? -1;
            if (ca < cb || (ca == cb && acc[i - 1].AngleToPlaneNormalDeg > acc[i].AngleToPlaneNormalDeg + 1e-4f))
            { rankOk = false; break; }
        }
        bool rankedMixed = acc.Exists(p => p.ContentInShadow == 2) && acc.Exists(p => p.ContentInShadow == 1);
        C("M8 accepted poses are ranked 1..n by content, ties to the smaller angle",
          rankOk && rankedMixed, $"{acc.Count} accepted; first {acc.FirstOrDefault()?.ContentInShadow} "
          + $"at {acc.FirstOrDefault()?.AngleToPlaneNormalDeg:0.#} deg");
        bool refusedUnranked = true;
        foreach (var p in ranked.Poses) if (!p.Accepted && p.ObservingRank is not null) refusedUnranked = false;
        C("M8 a refused pose carries no rank", refusedUnranked && ranked.Poses.Count > 0, "");

        // ---- M9 the pose file publishes the observing fields, and only for observing poses -----
        string whyM9 = ""; bool obsFields = false, faceClean = false;
        string[] obsKeys = { "mode", "ringBearingDeg", "contentInShadow", "contentKeys", "observingRank" };
        try
        {
            using var dO = JsonDocument.Parse(StationPoseFile.Render(hdr, new[] { wallRing }));
            obsFields = dO.RootElement.GetProperty("poses").GetArrayLength() > 0;
            foreach (var pe in dO.RootElement.GetProperty("poses").EnumerateArray())
                foreach (var k in obsKeys) if (!pe.TryGetProperty(k, out _)) obsFields = false;
            using var dF = JsonDocument.Parse(faceOnDoc);
            faceClean = true;
            foreach (var pe in dF.RootElement.GetProperty("poses").EnumerateArray())
                foreach (var k in obsKeys) if (pe.TryGetProperty(k, out _)) faceClean = false;
        }
        catch (Exception ex) { whyM9 = ex.GetType().Name + ": " + ex.Message; }
        C("M9 every observing pose publishes its mode, bearing, content and rank", obsFields,
          whyM9.Length > 0 ? whyM9 : string.Join(", ", obsKeys));
        C("M9 no face-on pose publishes an observing field", faceClean, whyM9);
        string obsDoc = StationPoseFile.Render(hdr, new[] { wallRing });
        string obsSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(obsDoc)));
        C("M9b the observing pose file over the M6 ring is byte-identical to the base",
          obsSha == M9ObservingSha256, $"{obsSha} ({obsDoc.Length} chars)");

        // ======================== H, the opt-in horizontal PortalBox class (GATE-B-CODE §3) ==
        // SkyWright.Engine.PortalBoxOrientation decides which PortalBox orientations the occlusion
        // rule (OcclusionRule) predicts. Kept here because it shares this file's geometry
        // (StationRule.MidQuad, the renderer's quad). The fixture rotations are the
        // in-situ form measured at Gate A: X about 90 or 270 with a Y yaw.
        const float Tol = 0.001f;
        (Vector3 Quad, Vector3 Authored, Vector3 Centre) Orient(Vector3 rotDeg, Vector3 pos)
        {
            var rad = rotDeg * (MathF.PI / 180f);
            var g = new PrimitiveGeometry("H", PrimitiveKind.PortalBox, new Vector3(400f, 2f, 300f), pos, rad, WS, CELL_A);
            var q = StationRule.MidQuad(g);
            var qn = Vector3.Normalize(Vector3.Cross(q[1] - q[0], q[3] - q[0]));   // as ShapeCullPolicy computes it
            var an = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY,
                SpatialOverlap.RefrRotation(new Vec3(rad.X, rad.Y, rad.Z))));
            return (qn, an, pos);
        }
        // pick the X (90 or 270) that puts the authored normal DOWN, and the other one for UP
        var d90 = Orient(new Vector3(90f, 35f, 0f), Vector3.Zero);
        var d270 = Orient(new Vector3(270f, 35f, 0f), Vector3.Zero);
        var down = d90.Authored.Z < 0f ? d90 : d270;
        var up = d90.Authored.Z < 0f ? d270 : d90;
        float xDown = d90.Authored.Z < 0f ? 90f : 270f;
        C("H0 the fixture: one in-situ rotation puts the normal down (-Z), the other up (+Z)",
          down.Authored.Z < -0.9999f && up.Authored.Z > 0.9999f
          && MathF.Abs(down.Quad.Z) > 0.9999f,
          $"X {xDown}: authored {down.Authored}, quad {down.Quad}; other: authored {up.Authored}");
        var hAbove = new Vector3(300f, 200f, 150f);
        var hBelow = new Vector3(300f, 200f, -150f);
        string? V(( Vector3 Quad, Vector3 Authored, Vector3 Centre) o, Vector3 eye, bool on)
            => PortalBoxOrientation.Check(o.Quad, o.Authored, o.Centre, eye, Tol, Tol, on);
        C("H1 (a) the sealed rule refuses a horizontal normal-down panel seen from above",
          V(down, hAbove, false) == PortalBoxOrientation.OutsideScope, $"{V(down, hAbove, false) ?? "admitted"}");
        C("H2 (a) with the opt-in, the same panel from above is admitted",
          V(down, hAbove, true) is null, $"{V(down, hAbove, true) ?? "admitted"}");
        var tilt30 = Orient(new Vector3(30f, 0f, 20f), Vector3.Zero);
        C("H3 (b) a panel tilted 30 deg is still refused with the opt-in, for orientation",
          V(tilt30, hAbove, true) == PortalBoxOrientation.OutsideScope, $"{V(tilt30, hAbove, true) ?? "admitted"}");
        C("H4 (b) a horizontal normal-UP (+Z) panel is refused with the opt-in, and says why",
          V(up, hAbove, true) == PortalBoxOrientation.HorizontalUp, $"{V(up, hAbove, true) ?? "admitted"}");
        C("H5 (b) the normal-down panel seen from BELOW is refused with the opt-in, and says why",
          V(down, hBelow, true) == PortalBoxOrientation.HorizontalEyeBelow, $"{V(down, hBelow, true) ?? "admitted"}");
        var nearly = Orient(new Vector3(xDown + 0.2f, 35f, 0f), Vector3.Zero);   // |n.x|,|n.y| ~ 0.0035
        C("H6 (b) a panel 0.2 deg off horizontal is outside the tolerance and refused with the opt-in",
          MathF.Max(MathF.Abs(nearly.Authored.X), MathF.Abs(nearly.Authored.Y)) > Tol
          && V(nearly, hAbove, true) == PortalBoxOrientation.OutsideScope,
          $"authored {nearly.Authored}: {V(nearly, hAbove, true) ?? "admitted"}");
        var upright = Orient(new Vector3(0f, 0f, 40f), new Vector3(0f, 0f, 150f));
        C("INHERITED H7 an upright panel is admitted with the opt-in on or off",
          V(upright, hAbove, true) is null && V(upright, hAbove, false) is null, "");
        C("INHERITED H8 the default refusal is the sealed text, word for word",
          PortalBoxOrientation.OutsideScope == "plane orientation outside the supplied rule's evidence scope", "");
        // The admitted class predicts by the same shadow: a bound below the panel inside the eye's cone
        // through it is Inside, one beside it Outside.
        var hq = StationRule.MidQuad(new PrimitiveGeometry("H", PrimitiveKind.PortalBox, new Vector3(400f, 2f, 300f),
            Vector3.Zero, new Vector3(xDown, 35f, 0f) * (MathF.PI / 180f), WS, CELL_A));
        var hs = ConvexShadow.FromRectangle(hq, new Vector3(0f, 0f, 400f));
        C("INHERITED H9 the horizontal panel's shadow holds a bound straight below it and not one beside it",
          hs is not null && hs.Classify(new SphereBound(new Vector3(0f, 0f, -200f), 30f)) == ShadowRelation.Inside
          && hs.Classify(new SphereBound(new Vector3(900f, 0f, -200f), 30f)) == ShadowRelation.Outside,
          hs is null ? "no shadow" : "");

        // ======================== U, the upright tolerance as its own rule field (cases 6016-6020) ==
        // Lab block 3 (runs/2026-10-14-lab-block-1/RESULT.md): planes tilted up to |n.z| 0.021 cull as their true shape
        // predicts, so the upright class widens to 0.021 while the horizontal class keeps 0.001.
        const float Up = 0.021f;
        (Vector3 Quad, Vector3 Authored, Vector3 Centre) Lean(float t)
            => Orient(new Vector3(MathF.Asin(t) * (180f / MathF.PI), 0f, 0f), new Vector3(0f, 0f, 150f));
        var lean0205 = Lean(0.0205f);
        var lean0215 = Lean(0.0215f);
        C("6016 upright 0.021, horizontal 0.001: a PortalBox at |n.z| 0.0205 is admitted, one at 0.0215 refused",
          PortalBoxOrientation.Check(lean0205.Quad, lean0205.Authored, lean0205.Centre, hAbove, Up, Tol, true) is null
          && PortalBoxOrientation.Check(lean0215.Quad, lean0215.Authored, lean0215.Centre, hAbove, Up, Tol, true) == PortalBoxOrientation.OutsideScope,
          $"|n.z| {MathF.Abs(lean0205.Quad.Z)} and {MathF.Abs(lean0215.Quad.Z)}");
        C("6018 upright 0.021, horizontal 0.001: H6's panel 0.2 deg off horizontal stays refused",
          PortalBoxOrientation.Check(nearly.Quad, nearly.Authored, nearly.Centre, hAbove, Up, Tol, true) == PortalBoxOrientation.OutsideScope,
          $"{PortalBoxOrientation.Check(nearly.Quad, nearly.Authored, nearly.Centre, hAbove, Up, Tol, true) ?? "admitted"}");
        var boxX = Vector3.Normalize(new Vector3(1f, 0f, 0.0174f));
        var boxXOut = Vector3.Normalize(new Vector3(1f, 0f, 0.0216f));
        C("6020 BoxUpright: |axis.z| 0.0174 admitted at 0.021 and refused at 0.001; 0.0215 refused at 0.021",
          PortalBoxOrientation.BoxUpright(boxX, Vector3.UnitY, Up) is null
          && PortalBoxOrientation.BoxUpright(boxX, Vector3.UnitY, Tol) == PortalBoxOrientation.BoxOutsideScope
          && PortalBoxOrientation.BoxUpright(boxXOut, Vector3.UnitY, Up) == PortalBoxOrientation.BoxOutsideScope,
          $"axis z {boxX.Z} and {boxXOut.Z}");

        check("stationrule: declared case count", ran == DeclaredCases,
              $"{ran} cases ran, {DeclaredCases} declared");
    }
}
