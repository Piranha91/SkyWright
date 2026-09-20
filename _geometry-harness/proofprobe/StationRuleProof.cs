using System.Numerics;
using HarnessShared;
using HousecarlCore;

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
    public const int DeclaredCases = 76;

    const string WS = "WS-FIXTURE";
    const string CELL_A = "CELL-A";
    const string CELL_B = "CELL-B";

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

        check("stationrule: declared case count", ran == DeclaredCases,
              $"{ran} cases ran, {DeclaredCases} declared");
    }
}
