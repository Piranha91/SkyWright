using System.Numerics;
using HarnessShared;
using HousecarlCore;

// The station rule's known answers, stated as synthetic geometry in the fixture's own frame.
//
// Four of these cases are the four diagnosed defects of the 2026-09-19/20 rule, and each one
// fails on a WRONG ANSWER rather than on a missing member: StationRule.cs currently reproduces
// that rule, so the case meets a composer that really has the defect. Refusal REASONS are
// asserted, not only refusals, and a refusal case also asserts that no ACCEPTED pose came out.
//
// Cases that the reproduced rule already answers correctly are kept and marked INHERITED. They
// are not filler: a report in which every case is red proves nothing about the harness, and these
// are what shows it can also report green.
static class StationRuleProof
{
    /// <summary>Cases run below, excluding this count's own check. A case added without moving
    /// this number fails the last check rather than passing unnoticed.</summary>
    public const int DeclaredCases = 36;

    // A tall exterior primitive: 400 u wide, 3,000 u tall, centre 1,000 u below the ground a
    // station stands on - the geometry that made the historical camera point at the dirt.
    static PrimitiveGeometry Tall() => new(
        Key: "PRIM-TALL", Kind: PrimitiveKind.PortalBox,
        ExtentFull: new Vector3(400f, 8f, 3000f),
        Position: new Vector3(0f, 0f, -1000f),
        RotationRad: Vector3.Zero,
        Worldspace: "WS-FIXTURE", Cell: "CELL-FIXTURE");

    static FloorReading Floor(float navZ, float floorZ)
        => new(navZ, floorZ, FloorSource.Mesh, NoFloorReason.Found, false, 1);

    static FloorReading NoFloor(float navZ)
        => new(navZ, 0f, FloorSource.None, NoFloorReason.NoSurface, false, 0);

    /// <summary>Flat ground everywhere: a winning, drawn triangle at navZ, a physical floor at
    /// floorZ.</summary>
    static Func<float, float, GroundAnswer> Flat(float navZ, float floorZ)
        => (_, _) => new GroundAnswer(true, true, true, navZ, Floor(navZ, floorZ));

    static int Count(StationYield y, StationRejection r)
        => y.Rejected.TryGetValue(r, out int n) ? n : 0;

    static int AcceptedPoses(StationYield y)
    {
        int n = 0;
        foreach (var p in y.Poses) if (p.Accepted) n++;
        return n;
    }

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
        var ladder = StationRule.Compose(tall, "A1", Flat(0f, 0f), new WaterFacts(null, null));
        C("INHERITED the ladder composes one station per (k, side, lateral)",
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
        // The physical floor sits 40 u ABOVE the navmesh triangle the station stands on.
        var raised = StationRule.Compose(tall, "A1", Flat(-40f, 0f), new WaterFacts(null, null));
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

        // A navmesh answer with no physical floor under it refuses; it does not fall back.
        var noFloor = StationRule.Compose(
            tall, "A1", (_, _) => new GroundAnswer(true, true, true, 0f, NoFloor(0f)),
            new WaterFacts(null, null));
        C("D4b no physical floor refuses with FloorUnknown",
          Count(noFloor, StationRejection.FloorUnknown) == noFloor.Composed,
          $"{Count(noFloor, StationRejection.FloorUnknown)} of {noFloor.Composed}");
        C("D4b no physical floor accepts nothing", AcceptedPoses(noFloor) == 0,
          $"{AcceptedPoses(noFloor)} accepted");
        C("D4b every refusal carries FloorUnknown and its numbers",
          EveryRefusalCarries(noFloor, StationRejection.FloorUnknown), "");

        // --------------------------------------------------------------- DEFECT 2, under water --
        // A real per-cell water height 60 u above the floor the station stands on.
        var drowned = StationRule.Compose(tall, "A1", Flat(0f, 0f), new WaterFacts(60f, null));
        C("D2 a station below a real water height refuses with StationUnderwater",
          Count(drowned, StationRejection.StationUnderwater) == drowned.Composed,
          $"{Count(drowned, StationRejection.StationUnderwater)} of {drowned.Composed}");
        C("D2 an underwater station accepts nothing", AcceptedPoses(drowned) == 0,
          $"{AcceptedPoses(drowned)} accepted");
        C("D2 every refusal carries StationUnderwater and its numbers",
          EveryRefusalCarries(drowned, StationRejection.StationUnderwater), "");

        // --------------------------------------------- DEFECT 3, the engine's no-value sentinel --
        // The cell logs no height of its own; its worldspace's default puts the surface overhead.
        var sentinel = StationRule.Compose(tall, "A1", Flat(0f, 0f),
                                           new WaterFacts(float.MaxValue, 60f));
        C("D3a a sentinel cell height inherits the worldspace default, not 'dry'",
          Count(sentinel, StationRejection.StationUnderwater) == sentinel.Composed,
          $"{Count(sentinel, StationRejection.StationUnderwater)} of {sentinel.Composed}");
        C("D3a the sentinel case accepts nothing", AcceptedPoses(sentinel) == 0,
          $"{AcceptedPoses(sentinel)} accepted");

        // Neither the cell nor its worldspace states a usable height: unknown, therefore refused.
        var unknown = StationRule.Compose(tall, "A1", Flat(0f, 0f),
                                          new WaterFacts(float.MaxValue, float.MaxValue));
        C("D3b an unresolvable surface refuses with WaterUnknown",
          Count(unknown, StationRejection.WaterUnknown) == unknown.Composed,
          $"{Count(unknown, StationRejection.WaterUnknown)} of {unknown.Composed}");
        C("D3b an unknown surface is never read as dry", AcceptedPoses(unknown) == 0,
          $"{AcceptedPoses(unknown)} accepted");
        C("D3b every refusal carries WaterUnknown and its numbers",
          EveryRefusalCarries(unknown, StationRejection.WaterUnknown), "");

        // The value the engine saturates to int-min: no surface at all, so the station is dry -
        // and the pose has to say WHY it is dry, or a right answer for a wrong reason passes.
        var dry = StationRule.Compose(tall, "A1", Flat(0f, 0f),
                                      new WaterFacts(-2.1474836E+09f, 60f));
        C("D3c a negative-wrapped height is dry, and every station is accepted",
          AcceptedPoses(dry) == dry.Composed, $"{AcceptedPoses(dry)} of {dry.Composed}");
        bool saidWhy = dry.Poses.Count > 0;
        foreach (var p in dry.Poses)
        {
            if (p.WaterHeightKind != WaterHeightKind.NegativeWrapped) saidWhy = false;
            if (p.WaterSurfaceZ is not null) saidWhy = false;
            if (!p.WaterHow.Contains("no surface", StringComparison.OrdinalIgnoreCase)) saidWhy = false;
        }
        C("D3c the pose says which reading made it dry", saidWhy,
          dry.Poses.Count > 0 ? $"kind {dry.Poses[0].WaterHeightKind}, how '{dry.Poses[0].WaterHow}'" : "no poses");

        // A real height BELOW the floor is water the station stands above; it is accepted, and
        // the clearance is published rather than left implicit.
        var shore = StationRule.Compose(tall, "A1", Flat(0f, 0f), new WaterFacts(-100f, null));
        C("D2b a station above a real water height is accepted",
          AcceptedPoses(shore) == shore.Composed, $"{AcceptedPoses(shore)} of {shore.Composed}");
        bool clearance = shore.Poses.Count > 0;
        foreach (var p in shore.Poses)
            if (p.FeetAboveSurface is not { } f || MathF.Abs(f - 100f) > 1e-3f) clearance = false;
        C("D2b the pose publishes how far the feet clear the surface", clearance,
          shore.Poses.Count > 0 ? $"{shore.Poses[0].FeetAboveSurface}" : "no poses");

        // ------------------------------------------------------------------- INHERITED refusals --
        // An eye far above the primitive's own vertical extent yields nothing.
        var shallow = new PrimitiveGeometry("PRIM-SHORT", PrimitiveKind.PortalBox,
            new Vector3(400f, 8f, 100f), new Vector3(0f, 0f, -5000f), Vector3.Zero,
            "WS-FIXTURE", "CELL-FIXTURE");
        var outside = StationRule.Compose(shallow, "A1", Flat(0f, 0f), new WaterFacts(null, null));
        C("INHERITED an eye outside the primitive's vertical extent yields no accepted pose",
          AcceptedPoses(outside) == 0, $"{AcceptedPoses(outside)} accepted");

        // --------------------------------------------------------------- the accounting closes --
        // Half the ladder stands where no winning triangle covers the XY.
        var half = StationRule.Compose(
            tall, "A2", (_, yy) => yy > 0f
                ? new GroundAnswer(true, true, true, 0f, Floor(0f, 0f))
                : new GroundAnswer(true, false, false, 0f, NoFloor(0f)),
            new WaterFacts(null, null));
        C("Y1 the yield closes: accepted + refused == composed", half.Closes,
          $"{half.Accepted} + {half.RejectedTotal} vs {half.Composed}");
        C("Y1 an uncovered station is counted under NoCoveringTriangle",
          Count(half, StationRejection.NoCoveringTriangle) == half.Composed - AcceptedPoses(half),
          $"{Count(half, StationRejection.NoCoveringTriangle)} counted, "
          + $"{half.Composed - AcceptedPoses(half)} dropped");
        C("Y1 a silent navmesh is counted under NoNavmeshAnswer, not as a miss",
          Count(StationRule.Compose(tall, "A2",
                    (_, _) => new GroundAnswer(false, false, false, 0f, NoFloor(0f)),
                    new WaterFacts(null, null)),
                StationRejection.NoNavmeshAnswer) == ladder.Composed,
          "an instrument failure is not a geometric miss");
        C("Y1 the accounting is kept per primitive and per setup",
          half.Primitive == tall.Key && half.Setup == "A2", $"{half.Primitive} / {half.Setup}");

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
