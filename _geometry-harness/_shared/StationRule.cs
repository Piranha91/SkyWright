using System.Numerics;
using HousecarlCore;

namespace HarnessShared;

// ======================================================================
//  StationRule - the rule that chooses camera stations for an occlusion
//  primitive, and the record it publishes for every station it considers.
//
//  WHERE IT CAME FROM. The rule began as a faithful reproduction of the one
//  that stood on 2026-09-19/20, when it lived in run-local scripts and
//  produced views no player can have. Each of its defects was reproduced
//  here on purpose, recorded RED by proofprobe/StationRuleProof.cs from the
//  commit that introduced it, and then fixed - so every case fails on a
//  WRONG ANSWER rather than on a missing member. The DEFECT marks below are
//  kept as history: each names what the historical rule did, beside the
//  code that no longer does it. viewprobe's --stations mode calls this file.
//
//  The defects held down by the proofs:
//    1  the camera aimed at the primitive's CENTRE, which on an exterior
//       primitive thousands of units tall sits far below any walkable
//       surface, so the view pointed at the ground;
//    2  a station could stand under water, because there was no water test;
//    3  a cell whose WaterHeight is the engine's no-value sentinel was read
//       as "no water" instead of inheriting the worldspace default;
//    4  the eye was taken from the navmesh triangle rather than from the
//       physical floor a body settles on;
//    B1 water and the published cell were taken from the PRIMITIVE, while
//       one ladder's stations reach 3,000 u either side and an exterior
//       cell is 4,096 u across - so a station over the border was judged by
//       another cell's surface and named the wrong cell to the driver;
//    A3 a Box was composed as if it had one normal;
//    B3 a station failing several conditions was counted under whichever
//       was tested first, not under a stated precedence.
//
//  A second mode, ComposeObserving, composes a RING of stations around a
//  clean primitive so that content inside its predicted shadow can be seen
//  (GATE-A §3.1, 2026-09-25). It keeps the player constraints above exactly
//  and adds three enumerated refusals; its cases are proofprobe's M1-M9.
//
//  The services this rule stands on, named where each defect is marked:
//  CellBoundaryIndex.Resolve (THE water resolver - one, not two) reading
//  DetectorModel.HeightKind / SurfaceHeight; HousecarlCore.NavmeshFloor and
//  FloorField, fed by FloorFeed, for the physical floor. The composer does
//  not re-derive either.
// ======================================================================

public enum PrimitiveKind : byte { PortalBox, Box }

/// <summary>Which upright face of a primitive a station stands off. A PortalBox is a rectangle
/// with one authored normal and uses <see cref="FaceAxis.Y"/> alone; a Box has four upright faces
/// and is predicted by its silhouette, so a station has to name the face it faces.</summary>
public enum FaceAxis : byte { Y, X }

/// <summary>What the one water resolver said about a station's own cell.</summary>
public enum StationWaterVerdict : byte
{
    /// <summary>The cell states a usable height of its own.</summary>
    Real,
    /// <summary>The cell defers, and a worldspace in its chain states a usable default.</summary>
    WorldspaceDefault,
    /// <summary>There is no surface here, and that is known rather than assumed.</summary>
    KnownDryNoSurface,
    /// <summary>The surface could not be resolved. Refused - never read as dry.</summary>
    Unresolved,
}

/// <summary>Why a candidate station was refused. A closed set: nothing is refused for a reason
/// outside it, and an accepted pose carries <see cref="StationRejection.None"/>.</summary>
public enum StationRejection : byte
{
    None = 0,
    DegeneratePlane,
    /// <summary>The navmesh was asked about this XY and returned nothing at all - an instrument
    /// failure, never a geometric miss.</summary>
    NoNavmeshAnswer,
    NoCoveringTriangle,
    AllCoveringTrianglesDeleted,
    /// <summary>A navmesh answer exists but no physical floor was found under it. Never falls
    /// back to the navmesh height.</summary>
    FloorUnknown,
    /// <summary>The applicable water surface could not be resolved. Refused, never read as dry.</summary>
    WaterUnknown,
    StationUnderwater,
    /// <summary>The eye lies inside or on the primitive, where it has no view shadow to be
    /// ranked by.</summary>
    EyeInsidePrimitive,
    EyeOutsidePlaneExtent,
    /// <summary>Observing mode only: the view is within <see cref="StationRule.EdgeOnDeg"/> of the
    /// primitive's plane, so its rectangle projects to almost nothing.</summary>
    EdgeOn,
    /// <summary>Observing mode only: no part of the shadow volume's near face lies inside the
    /// frame at the level gaze.</summary>
    ShadowOutOfFrame,
    /// <summary>Observing mode only: no content bound lies inside the shadow at this eye.</summary>
    NoContentInShadow,
    /// <summary>A guard. Level gaze is by construction, so this should never appear on a composed
    /// pose; it exists so a later change cannot reintroduce defect 1 in silence.</summary>
    NonLevelGaze,
}

/// <summary>Which station rule composed a pose. <see cref="FaceOn"/> is the sealed rule and the
/// default; <see cref="Observing"/> composes a ring of bearings for clean primitives so that
/// content inside the primitive's predicted shadow can be seen from the eye (GATE-A §3.1).</summary>
public enum StationComposeMode : byte { FaceOn, Observing }

/// <summary>One piece of placed content, by its world sphere, that the observing mode counts when
/// the bound lies inside the primitive's shadow at an eye.</summary>
public readonly record struct ContentBound(string Key, SphereBound Bound);

/// <summary>An occlusion primitive's geometry, as the renderer already models it.</summary>
public readonly record struct PrimitiveGeometry(
    string Key, PrimitiveKind Kind, Vector3 ExtentFull, Vector3 Position, Vector3 RotationRad,
    string Worldspace, string Cell);

/// <summary>One resolver's answer about one cell's water, with everything it was decided from.</summary>
public readonly record struct WaterResolution(
    StationWaterVerdict Verdict, float? SurfaceZ, float? CellHeight, WaterHeightKind Kind,
    float? WorldDefault, string FromWorldspace, string How)
{
    public static WaterResolution NotTested =>
        new(StationWaterVerdict.Unresolved, null, null, WaterHeightKind.Unset, null, "", "no water test");
}

/// <summary>Everything the load order answers about one station's XY: the navmesh, the physical
/// floor, WHICH CELL the station is actually in, and that cell's own water. One ladder's stations
/// can stand in several cells, so this is looked up per station and never per primitive.</summary>
public readonly record struct StationEnvironment(
    bool Answered, bool Covered, bool Drawn, float NavZ, FloorReading Floor,
    string Cell, string Worldspace, WaterResolution Water,
    FloorAnchor FloorAnchor = FloorAnchor.Navmesh, float NavmeshBurial = 0f,
    bool TerrainIsAnotherStorey = false);

/// <summary>One candidate station, accepted or refused, with the aim geometry published so a
/// pose file can be judged before anything is rendered.</summary>
public sealed record StationPose
{
    public required string Primitive { get; init; }
    public required PrimitiveKind Kind { get; init; }
    public required string Setup { get; init; }
    /// <summary>The cell the STATION stands in, not the primitive's.</summary>
    public required string Cell { get; init; }
    public required string Worldspace { get; init; }
    public required int StationIndex { get; init; }
    public required FaceAxis FaceAxis { get; init; }
    public required float K { get; init; }
    public required int Side { get; init; }
    public required float Lateral { get; init; }
    public required float Standoff { get; init; }

    // the aim geometry
    public required Vector3 Eye { get; init; }
    public required Vector3 LookAt { get; init; }
    public required float PitchDeg { get; init; }
    public required float BearingDeg { get; init; }
    public required float AngleToPlaneNormalDeg { get; init; }
    public float FovDeg { get; init; } = StationRule.DefaultFovDeg;

    // the ground
    public required float NavZ { get; init; }
    public required float FloorZ { get; init; }
    public required FloorSource FloorSource { get; init; }
    public required float FloorOffset { get; init; }
    /// <summary>Which height the floor read was anchored at, and how far the landscape stands over
    /// the navmesh under this station. Published on EVERY pose: a station standing on ground the
    /// navmesh is buried beneath is a different kind of answer, and its share has to be visible
    /// rather than discovered later.</summary>
    public FloorAnchor FloorAnchor { get; init; } = FloorAnchor.Navmesh;
    public float NavmeshBurial { get; init; }
    /// <summary>The landscape stands a whole actor's height or more above the navmesh here: the two
    /// are different storeys, the navmesh anchor stays, and the gap is published so such stations
    /// are countable.</summary>
    public bool TerrainIsAnotherStorey { get; init; }
    public float EyeHeight { get; init; } = StationRule.EyeHeight;
    public float? FeetAboveSurface { get; init; }
    public required float PlaneZLow { get; init; }
    public required float PlaneZHigh { get; init; }

    // the water, as the one resolver answered it for THIS station's cell
    public required WaterResolution Water { get; init; }

    // the verdict
    public required bool Accepted { get; init; }
    public StationRejection RejectionReason { get; init; } = StationRejection.None;
    public string RejectionDetail { get; init; } = "";

    // observing mode only; null on a face-on pose, which the pose file publishes as it always did
    public StationComposeMode Mode { get; init; } = StationComposeMode.FaceOn;
    /// <summary>The ring bearing, degrees clockwise in plan from the face's outward normal.</summary>
    public float? RingBearingDeg { get; init; }
    /// <summary>How many content bounds lie inside the shadow at this eye.</summary>
    public int? ContentInShadow { get; init; }
    public IReadOnlyList<string>? ContentKeys { get; init; }
    /// <summary>1-based rank among the primitive's accepted observing poses: most content first,
    /// ties to the smaller angle to the normal.</summary>
    public int? ObservingRank { get; init; }
}

/// <summary>One primitive's stations under one setup, with the accounting that has to close.</summary>
public sealed class StationYield
{
    public required string Primitive { get; init; }
    public required string Setup { get; init; }
    public int Composed { get; set; }
    public int Accepted { get; set; }
    public Dictionary<StationRejection, int> Rejected { get; } = new();
    public List<StationPose> Poses { get; } = new();

    public int RejectedTotal { get { int n = 0; foreach (var v in Rejected.Values) n += v; return n; } }

    /// <summary>Every station composed is either accepted or refused for exactly one enumerated
    /// reason. A yield that does not close is a miscount, and the writer refuses it.</summary>
    public bool Closes => Accepted + RejectedTotal == Composed;
}

public static class StationRule
{
    /// <summary>The eye's height above the ground, carried from the sealed 2026-09-19 constant.
    /// DEFECT 4 is that the historical rule added it to the NAVMESH height; it belongs on the
    /// physical floor.</summary>
    public const float EyeHeight = 120.5f;

    public const float DefaultFovDeg = 80.0f;
    public const float StandoffFloor = 250.0f;
    public const float StandoffCap = 3000.0f;
    public static readonly float[] DefaultK = { 0.25f, 0.5f, 1.0f };
    public static readonly float[] DefaultLaterals = { -0.35f, 0.0f, 0.35f };

    /// <summary>The order a station's failures are counted in, most decisive first. A station can
    /// fail several conditions at once and the yield table depends on which one is counted, so the
    /// order is stated rather than left to the order the tests happen to run in.
    ///
    /// Instrument failures come before geometry, because an instrument that said nothing has not
    /// measured a miss. The ground comes before the water, because a surface can only be compared
    /// with feet that have a floor. "Unknown" comes before "under water", because a station whose
    /// surface cannot be resolved is not known to be under anything. The primitive-relative tests
    /// come last, because they need a finished eye. The level-gaze guard is last of all: it cannot
    /// fire on a composed pose and exists only to catch a later change.
    /// DEFECT B3: the historical rule had no stated order.</summary>
    public static readonly StationRejection[] Precedence =
    {
        StationRejection.DegeneratePlane,
        StationRejection.NoNavmeshAnswer,
        StationRejection.NoCoveringTriangle,
        StationRejection.AllCoveringTrianglesDeleted,
        StationRejection.FloorUnknown,
        StationRejection.WaterUnknown,
        StationRejection.StationUnderwater,
        StationRejection.EyeInsidePrimitive,
        StationRejection.EyeOutsidePlaneExtent,
        StationRejection.EdgeOn,
        StationRejection.ShadowOutOfFrame,
        StationRejection.NoContentInShadow,
        StationRejection.NonLevelGaze,
    };

    /// <summary>Observing mode: bearings on the ring, degrees clockwise from the face normal.</summary>
    public static readonly float[] RingBearingsDeg =
        { 0f, 30f, 60f, 90f, 120f, 150f, 180f, 210f, 240f, 270f, 300f, 330f };
    /// <summary>Observing mode: a view within this many degrees of the plane is refused EdgeOn.</summary>
    public const float EdgeOnDeg = 10f;
    /// <summary>Observing mode: the frame the shadow must reach, horizontal FOV at this aspect.</summary>
    public const float FrameAspect = 16f / 9f;

    /// <summary>The near plane the frame test clips at, in world units.</summary>
    public const float FrameNearClip = 15f;

    /// <summary>Observing mode (GATE-A §3.1): stations on a RING of bearings around the primitive,
    /// so that content inside the primitive's predicted shadow can be seen from the eye - where a
    /// face-on station of a panel set in a wall looks into the wall.
    ///
    /// (a) The player constraints are the face-on rule's, kept exactly: the physical floor + the
    ///     sealed eye height with no navmesh fallback, the water through the one resolver, the
    ///     navmesh answer and cover, the eye outside the primitive, the level-gaze guard, one
    ///     closed refusal set and one precedence. DROPPED for this mode only:
    ///     <see cref="StationRejection.EyeOutsidePlaneExtent"/>, a primitive-relative test, which
    ///     is what lets a station stand above a panel's top.
    /// (b) Bearing θ, clockwise in plan from the face's outward normal n, puts the station at
    ///     centre + (n cosθ + cw(n) sinθ)(faceHalf + d) + right·lateral·lateralHalf, with d and the
    ///     lateral axis exactly the face-on ladder's - so bearings 0 and 180 ARE the face-on +1 and
    ///     -1 stations. A view within <see cref="EdgeOnDeg"/> of the plane is refused
    ///     <see cref="StationRejection.EdgeOn"/>; a near face with no part inside the frame is
    ///     refused <see cref="StationRejection.ShadowOutOfFrame"/>.
    /// (c) The shadow is <see cref="ShadowOf"/> - the renderer's construction - and a content bound
    ///     counts when <c>ConvexShadow.Classify</c> calls it Inside, the renderer's own test. A pose
    ///     with none is refused <see cref="StationRejection.NoContentInShadow"/>. Accepted poses
    ///     are ranked by that count, ties to the smaller angle to the normal.
    /// (d) Whether that content is DRAWN and VISIBLE is decided outside this rule, from the pose's
    ///     render; this rule renders nothing.</summary>
    public static StationYield ComposeObserving(
        PrimitiveGeometry p, string setup, Func<float, float, StationEnvironment> environment,
        IReadOnlyList<ContentBound> content, float[]? kValues = null, float[]? laterals = null,
        float[]? bearingsDeg = null, float fovDeg = DefaultFovDeg)
    {
        var y = new StationYield { Primitive = p.Key, Setup = setup };
        kValues ??= DefaultK;
        laterals ??= DefaultLaterals;
        bearingsDeg ??= RingBearingsDeg;

        var c = p.Position;
        float hz = p.ExtentFull.Z / 2f;
        float zLow = c.Z - hz, zHigh = c.Z + hz;
        bool degenerate = Degenerate(p);
        var reasons = new List<StationRejection>(6);

        int index = 0;
        foreach (var axis in FacesOf(p.Kind))
        {
            var (ax, ay) = LocalAxes(p.RotationRad);
            var n = axis == FaceAxis.Y ? ay : ax;       // the face's outward direction, unsided
            var right = new Vector3(-n.Y, n.X, 0f);     // the face-on ladder's lateral axis
            var cw = new Vector3(n.Y, -n.X, 0f);        // n turned 90 deg clockwise in plan
            var (faceHalf, lateralHalf, _) = Halves(p, axis);
            float basis = MathF.Min(lateralHalf, hz);
            foreach (float k in kValues)
            {
                float d = MathF.Max(StandoffFloor, MathF.Min(StandoffCap, k * basis));
                foreach (float bearing in bearingsDeg)
                {
                    float th = bearing * MathF.PI / 180f;
                    var dir = n * MathF.Cos(th) + cw * MathF.Sin(th);
                    foreach (float lat in laterals)
                    {
                        y.Composed++;
                        int thisIndex = index++;
                        float offset = faceHalf + d;
                        float x = c.X + dir.X * offset + right.X * lat * lateralHalf;
                        float yy = c.Y + dir.Y * offset + right.Y * lat * lateralHalf;

                        reasons.Clear();
                        if (degenerate) reasons.Add(StationRejection.DegeneratePlane);

                        // ---- (a) the player constraints, exactly as the face-on rule has them --
                        var env = environment(x, yy);
                        var (floorKnown, floorZ, eyeZ) = CheckGround(env, reasons);
                        var eye = new Vector3(x, yy, eyeZ);
                        var lookAt = new Vector3(c.X, c.Y, eyeZ);

                        // EyeOutsidePlaneExtent is NOT tested in this mode (see the summary).
                        if (floorKnown && EyeInside(p, eye)) reasons.Add(StationRejection.EyeInsidePrimitive);

                        float? feetAbove = CheckWater(env, floorKnown, floorZ, reasons);

                        float dx = lookAt.X - eye.X, dy = lookAt.Y - eye.Y;
                        float dxy = MathF.Sqrt(dx * dx + dy * dy);
                        float pitch = Pitch(eye, lookAt);
                        float worldBearing = Bearing(dx, dy);
                        var view = dxy > 0f ? new Vector3(dx / dxy, dy / dxy, 0f) : Vector3.Zero;
                        float along = Vector3.Dot(view, n);
                        float toNormal = MathF.Acos(Math.Clamp(MathF.Abs(along), 0f, 1f)) * 180f / MathF.PI;
                        int side = (x - c.X) * n.X + (yy - c.Y) * n.Y >= 0f ? 1 : -1;

                        // ---- (b) the geometric refusals ----------------------------------------
                        if (dxy <= 0f || toNormal > 90f - EdgeOnDeg) reasons.Add(StationRejection.EdgeOn);

                        // ---- (c) content in the shadow ----------------------------------------
                        int? inShadow = null;
                        List<string>? keys = null;
                        bool inFrame = false, shadowBuilt = false;
                        if (floorKnown && dxy > 0f)
                        {
                            inFrame = NearFaceInFrame(p, eye, lookAt, fovDeg);
                            if (!inFrame) reasons.Add(StationRejection.ShadowOutOfFrame);
                            var shadow = ShadowOf(p, eye);
                            shadowBuilt = shadow is not null;
                            keys = new List<string>();
                            if (shadow is not null)
                                foreach (var cb in content)
                                    if (shadow.Classify(cb.Bound) == ShadowRelation.Inside) keys.Add(cb.Key);
                            inShadow = keys.Count;
                            if (keys.Count == 0) reasons.Add(StationRejection.NoContentInShadow);
                        }

                        var pose = new StationPose
                        {
                            Primitive = p.Key, Kind = p.Kind, Setup = setup,
                            Cell = env.Cell, Worldspace = env.Worldspace,
                            StationIndex = thisIndex, FaceAxis = axis,
                            K = k, Side = side, Lateral = lat, Standoff = d,
                            Eye = eye, LookAt = lookAt, PitchDeg = pitch, BearingDeg = worldBearing,
                            AngleToPlaneNormalDeg = toNormal,
                            NavZ = env.NavZ, FloorZ = floorZ,
                            FloorSource = env.Floor.Source,
                            FloorOffset = env.NavZ - floorZ,
                            FloorAnchor = env.FloorAnchor,
                            NavmeshBurial = env.NavmeshBurial,
                            TerrainIsAnotherStorey = env.TerrainIsAnotherStorey,
                            FeetAboveSurface = feetAbove,
                            PlaneZLow = zLow, PlaneZHigh = zHigh,
                            Water = env.Water,
                            Accepted = false,
                            Mode = StationComposeMode.Observing,
                            RingBearingDeg = bearing,
                            ContentInShadow = inShadow,
                            ContentKeys = keys,
                        };

                        var guard = CheckLevel(pose);
                        if (guard != StationRejection.None) reasons.Add(guard);

                        var reason = FirstByPrecedence(reasons);
                        if (reason == StationRejection.None)
                        {
                            y.Accepted++;
                            y.Poses.Add(pose with { Accepted = true });
                        }
                        else
                        {
                            y.Rejected[reason] = (y.Rejected.TryGetValue(reason, out int m) ? m : 0) + 1;
                            y.Poses.Add(pose with
                            {
                                RejectionReason = reason,
                                RejectionDetail = reason switch
                                {
                                    StationRejection.EdgeOn =>
                                        $"the view is {toNormal:0.###} deg off the normal, within "
                                        + $"{EdgeOnDeg:0.#} deg of the primitive's plane (bearing {bearing:0.###})",
                                    StationRejection.ShadowOutOfFrame =>
                                        $"no part of the near face lies inside the {fovDeg:0.#} deg x 16:9 frame "
                                        + $"at the level gaze from eye z {eyeZ:0.###}; primitive z "
                                        + $"[{zLow:0.###}, {zHigh:0.###}]",
                                    StationRejection.NoContentInShadow =>
                                        shadowBuilt
                                            ? $"none of {content.Count} content bound(s) lies inside the shadow at this eye"
                                            : "no shadow volume could be built from this eye (degenerate or on the plane)",
                                    _ => Detail(reason, env, floorZ, eyeZ, zLow, zHigh, p),
                                },
                            });
                        }
                    }
                }
            }
        }

        // Rank the accepted poses: most content first, ties to the smaller angle, then the index.
        var order = new List<int>();
        for (int i = 0; i < y.Poses.Count; i++) if (y.Poses[i].Accepted) order.Add(i);
        order.Sort((a, b) =>
        {
            var pa = y.Poses[a]; var pb = y.Poses[b];
            int byCount = (pb.ContentInShadow ?? 0).CompareTo(pa.ContentInShadow ?? 0);
            if (byCount != 0) return byCount;
            int byAngle = pa.AngleToPlaneNormalDeg.CompareTo(pb.AngleToPlaneNormalDeg);
            return byAngle != 0 ? byAngle : pa.StationIndex.CompareTo(pb.StationIndex);
        });
        for (int r = 0; r < order.Count; r++)
            y.Poses[order[r]] = y.Poses[order[r]] with { ObservingRank = r + 1 };
        return y;
    }

    /// <summary>The primitive's eight box corners in the world, indexed by axis bits (1 = +x,
    /// 2 = +y, 4 = +z) - the renderer's <c>PlaneGeometry.BoxCorners</c>, from the full rotation.
    /// <see cref="PrimitiveGeometry.ExtentFull"/> is already scaled.</summary>
    public static Vector3[] Corners(PrimitiveGeometry p)
    {
        var r = SpatialOverlap.RefrRotation(new Vec3(p.RotationRad.X, p.RotationRad.Y, p.RotationRad.Z));
        var h = p.ExtentFull * 0.5f;
        var c = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            var l = new Vector3((i & 1) == 0 ? -h.X : h.X, (i & 2) == 0 ? -h.Y : h.Y, (i & 4) == 0 ? -h.Z : h.Z);
            c[i] = Vector3.Transform(l, r) + p.Position;
        }
        return c;
    }

    /// <summary>A PortalBox's mid-plane quad (local y = 0), ring-ordered - the renderer's
    /// <c>PlaneGeometry.MidQuad</c>, from the full rotation.</summary>
    public static Vector3[] MidQuad(PrimitiveGeometry p)
    {
        var r = SpatialOverlap.RefrRotation(new Vec3(p.RotationRad.X, p.RotationRad.Y, p.RotationRad.Z));
        var h = p.ExtentFull * 0.5f;
        return new[]
        {
            Vector3.Transform(new Vector3(-h.X, 0f, -h.Z), r) + p.Position,
            Vector3.Transform(new Vector3(h.X, 0f, -h.Z), r) + p.Position,
            Vector3.Transform(new Vector3(h.X, 0f, h.Z), r) + p.Position,
            Vector3.Transform(new Vector3(-h.X, 0f, h.Z), r) + p.Position,
        };
    }

    /// <summary>The primitive's view shadow from <paramref name="eye"/>, built as the renderer builds
    /// it (<c>ShapeCullPolicy.Shadow</c>): a PortalBox by its mid-quad, a Box by its silhouette. The
    /// renderer's rule-specific refusals (orientation scope, near-eye distance) are NOT applied
    /// here; the render decides those, and a pose they refuse is dropped at (d).</summary>
    public static ConvexShadow? ShadowOf(PrimitiveGeometry p, Vector3 eye)
        => p.Kind == PrimitiveKind.Box ? ConvexShadow.FromBox(Corners(p), eye)
                                       : ConvexShadow.FromRectangle(MidQuad(p), eye);

    static readonly int[][] BoxFaceRings =
    {
        new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 }, new[] { 0, 1, 5, 4 },
        new[] { 2, 6, 7, 3 }, new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 },
    };

    /// <summary>Whether any part of the primitive's near face lies inside the frame of a level
    /// camera at <paramref name="eye"/> aimed at <paramref name="lookAt"/>: horizontal FOV
    /// <paramref name="fovDeg"/> at <see cref="FrameAspect"/>, near plane
    /// <see cref="FrameNearClip"/>. The near face is a PortalBox's quad, or a Box's front-facing
    /// faces; each polygon is clipped against the five frustum planes and any remainder is in
    /// frame.</summary>
    public static bool NearFaceInFrame(PrimitiveGeometry p, Vector3 eye, Vector3 lookAt, float fovDeg)
    {
        var f = lookAt - eye;
        if (!float.IsFinite(f.X) || !float.IsFinite(f.Y) || !float.IsFinite(f.Z) || f.LengthSquared() <= 0f)
            return false;
        f = Vector3.Normalize(f);
        var rt = Vector3.Cross(f, Vector3.UnitZ);
        if (rt.LengthSquared() < 1e-12f) return false;
        rt = Vector3.Normalize(rt);
        var up = Vector3.Cross(rt, f);
        float th = MathF.Tan(fovDeg * MathF.PI / 360f), tv = th / FrameAspect;
        // Inward half-spaces, each as g(q) = a·(q - eye) + b >= 0.
        var planes = new (Vector3 A, float B)[]
        {
            (f, -FrameNearClip),
            (th * f - rt, 0f), (th * f + rt, 0f),
            (tv * f - up, 0f), (tv * f + up, 0f),
        };
        var polys = new List<Vector3[]>();
        if (p.Kind == PrimitiveKind.Box)
        {
            var cs = Corners(p);
            var centre = Vector3.Zero;
            foreach (var v in cs) centre += v;
            centre /= 8f;
            foreach (var ring in BoxFaceRings)
            {
                var poly = new[] { cs[ring[0]], cs[ring[1]], cs[ring[2]], cs[ring[3]] };
                var fc = (poly[0] + poly[1] + poly[2] + poly[3]) / 4f;
                if (Vector3.Dot(fc - centre, eye - fc) > 0f) polys.Add(poly);   // faces the eye
            }
        }
        else polys.Add(MidQuad(p));
        foreach (var poly in polys)
        {
            var cur = new List<Vector3>(poly);
            foreach (var (a, b) in planes)
            {
                if (cur.Count == 0) break;
                var next = new List<Vector3>(cur.Count + 2);
                for (int i = 0; i < cur.Count; i++)
                {
                    var s = cur[i]; var e = cur[(i + 1) % cur.Count];
                    float gs = Vector3.Dot(a, s - eye) + b, ge = Vector3.Dot(a, e - eye) + b;
                    if (gs >= 0f) next.Add(s);
                    if ((gs >= 0f) != (ge >= 0f)) next.Add(s + (e - s) * (gs / (gs - ge)));
                }
                cur = next;
            }
            if (cur.Count > 0) return true;
        }
        return false;
    }

    /// <summary>The reason a station is counted under, when it fails several.</summary>
    public static StationRejection FirstByPrecedence(IEnumerable<StationRejection> reasons)
    {
        var seen = new HashSet<StationRejection>();
        foreach (var r in reasons)
            if (r != StationRejection.None) seen.Add(r);
        foreach (var r in Precedence)
            if (seen.Contains(r)) return r;
        return StationRejection.None;
    }

    /// <summary>Local +Y in the world for a Z-only rotation, as SpatialOverlap.RefrRotation gives
    /// it: (sin rotZ, cos rotZ, 0).</summary>
    public static Vector3 Normal(Vector3 rotationRad)
        => new(MathF.Sin(rotationRad.Z), MathF.Cos(rotationRad.Z), 0f);

    /// <summary>The outward normal of one upright face: the authored +Y for
    /// <see cref="FaceAxis.Y"/>, and the local +X for <see cref="FaceAxis.X"/>, each times the
    /// side.</summary>
    public static Vector3 FaceNormal(Vector3 rotationRad, FaceAxis axis, int side)
    {
        var n = Normal(rotationRad);
        var v = axis == FaceAxis.Y ? n : new Vector3(-n.Y, n.X, 0f);
        return new Vector3(v.X * side, v.Y * side, 0f);
    }

    /// <summary>The faces a primitive of this kind is composed around. A PortalBox is a rectangle
    /// with one authored normal; a Box is a volume predicted by its silhouette, so its four upright
    /// faces are two axes times two sides.</summary>
    public static FaceAxis[] FacesOf(PrimitiveKind kind)
        => kind == PrimitiveKind.Box ? new[] { FaceAxis.Y, FaceAxis.X } : new[] { FaceAxis.Y };

    /// <summary>The primitive's local +X and +Y in the world, for a Z-only rotation.</summary>
    static (Vector3 X, Vector3 Y) LocalAxes(Vector3 rotationRad)
    {
        var y = Normal(rotationRad);
        return (new Vector3(-y.Y, y.X, 0f), y);
    }

    /// <summary>Half-extents: the face's own outward half-depth, the half-width a lateral offset
    /// runs along, and the half-height. A PortalBox is a rectangle - its authored thickness is
    /// nominal and a station stands off its MID-plane, which is what reproduces the sealed
    /// 2026-09-19 station set. A Box is a volume, so a station stands off its FACE.</summary>
    static (float FaceHalf, float LateralHalf, float HalfHeight) Halves(PrimitiveGeometry p, FaceAxis axis)
    {
        float hx = p.ExtentFull.X / 2f, hy = p.ExtentFull.Y / 2f, hz = p.ExtentFull.Z / 2f;
        float faceHalf = p.Kind == PrimitiveKind.Box ? (axis == FaceAxis.Y ? hy : hx) : 0f;
        float lateralHalf = axis == FaceAxis.Y ? hx : hy;
        return (faceHalf, lateralHalf, hz);
    }

    /// <summary>Whether an eye lies inside or on the primitive's own volume, where
    /// <c>ConvexShadow.FromBox</c> yields no shadow and the station cannot be ranked.</summary>
    public static bool EyeInside(PrimitiveGeometry p, Vector3 eye)
    {
        if (!float.IsFinite(eye.X) || !float.IsFinite(eye.Y) || !float.IsFinite(eye.Z)) return false;
        var (ax, ay) = LocalAxes(p.RotationRad);
        var d = eye - p.Position;
        float lx = Vector3.Dot(d, ax), ly = Vector3.Dot(d, ay), lz = d.Z;
        return MathF.Abs(lx) <= p.ExtentFull.X / 2f
            && MathF.Abs(ly) <= p.ExtentFull.Y / 2f
            && MathF.Abs(lz) <= p.ExtentFull.Z / 2f;
    }

    /// <summary>Horizontal bearing of a direction, 0 = +Y, increasing clockwise, in [0, 360).</summary>
    public static float Bearing(float dx, float dy)
    {
        float d = MathF.Atan2(dx, dy) * 180f / MathF.PI;
        return d < 0f ? d + 360f : d;
    }

    /// <summary>The pose's pitch, in degrees, MEASURED from the eye and the aim point: positive
    /// looking up. A composed pose is level by construction, so this is expected to read zero - but
    /// it is published as a measurement so that <see cref="CheckLevel"/> is the backstop rather
    /// than the only witness, and so a later change that tilts the aim shows up as a number.
    /// A pose with no eye (no floor was found) has no pitch either: the arithmetic carries the NaN
    /// through, and the pose file publishes null rather than a number nobody measured.</summary>
    public static float Pitch(Vector3 eye, Vector3 lookAt)
    {
        float dx = lookAt.X - eye.X, dy = lookAt.Y - eye.Y, dz = lookAt.Z - eye.Z;
        // Atan2 needs no guard for a vertical gaze: Atan2(dz, 0) is +/- 90 deg and Atan2(0, 0) is 0,
        // which is exactly what a straight-up and a degenerate aim should read.
        return MathF.Atan2(dz, MathF.Sqrt(dx * dx + dy * dy)) * 180f / MathF.PI;
    }

    /// <summary>The level-gaze guard. A pose whose look-at height differs from its eye height is
    /// not a level gaze, whatever composed it.
    /// DEFECT 1's guard: the historical rule had none, so this returns None unconditionally.</summary>
    public static StationRejection CheckLevel(StationPose pose) => CheckLevel(pose.Eye, pose.LookAt);

    /// <summary>The level-gaze guard on one eye and aim point (C1). The pose overload asks this.</summary>
    public static StationRejection CheckLevel(Vector3 eye, Vector3 lookAt)
        => MathF.Abs(lookAt.Z - eye.Z) > 1e-4f
            ? StationRejection.NonLevelGaze : StationRejection.None;

    /// <summary>C5 then C2, the per-candidate ground checks every composer runs - face-on, observing
    /// and route - on the load order's answer at the station's own XY: the navmesh answered, a
    /// triangle covers the XY, not every covering triangle is deleted, and a physical floor was
    /// found. Adds at most one reason. The eye stands on the PHYSICAL FLOOR plus
    /// <see cref="EyeHeight"/>; with no floor there is no eye (NaN), never a navmesh fallback.</summary>
    internal static (bool FloorKnown, float FloorZ, float EyeZ) CheckGround(
        StationEnvironment env, List<StationRejection> reasons)
    {
        bool ground = env.Answered && env.Covered && env.Drawn;
        if (!env.Answered) reasons.Add(StationRejection.NoNavmeshAnswer);
        else if (!env.Covered) reasons.Add(StationRejection.NoCoveringTriangle);
        else if (!env.Drawn) reasons.Add(StationRejection.AllCoveringTrianglesDeleted);
        else if (!env.Floor.Found) reasons.Add(StationRejection.FloorUnknown);

        bool floorKnown = ground && env.Floor.Found;
        float floorZ = env.Floor.FloorZ;
        float eyeZ = floorKnown ? floorZ + EyeHeight : float.NaN;
        return (floorKnown, floorZ, eyeZ);
    }

    /// <summary>C4 then C3, the per-candidate water checks every composer runs: the surface of the
    /// station's own cell, as the one resolver answered it, is known (never read as dry), and the
    /// feet are not below it. Returns the feet's height above the surface when both are known.</summary>
    internal static float? CheckWater(StationEnvironment env, bool floorKnown, float floorZ,
                                      List<StationRejection> reasons)
    {
        float? feetAbove = null;
        if (env.Water.Verdict == StationWaterVerdict.Unresolved)
            reasons.Add(StationRejection.WaterUnknown);
        else if (env.Water.SurfaceZ is { } surfaceZ && floorKnown)
        {
            feetAbove = floorZ - surfaceZ;
            if (floorZ < surfaceZ) reasons.Add(StationRejection.StationUnderwater);
        }
        return feetAbove;
    }

    /// <summary>A worldspace's usable default water height, following the parent chain when the
    /// worldspace itself defers - a city worldspace commonly takes its water from Tamriel.
    /// <paramref name="lookup"/> answers (this worldspace's own DefaultWaterHeight, its parent).
    /// A chain that ends, loops, or runs past <paramref name="maxHops"/> without a usable height
    /// resolves to nothing, which the caller refuses as unknown rather than reading as dry.</summary>
    public static (float? Value, string From, int Hops, bool Cycle) ResolveWorldDefault(
        string worldspace, Func<string, (float? Default, string? Parent)> lookup, int maxHops = 8)
    {
        var seen = new HashSet<string>();
        string at = worldspace;
        for (int hops = 0; hops <= maxHops; hops++)
        {
            if (!seen.Add(at)) return (null, at, hops, true);
            var (own, parent) = lookup(at);
            if (DetectorModel.HeightKind(own) == WaterHeightKind.Real) return (own, at, hops, false);
            if (parent is null) return (null, at, hops, false);
            at = parent;
        }
        return (null, at, maxHops, false);
    }

    /// <summary>Classify ONE resolver's answer about a cell into the verdict the station rule acts
    /// on. <see cref="CellBoundaryIndex.Resolve"/> decides whether there is a surface and where it
    /// is - this does not decide it again, it only says what the rule should do about it, and the
    /// proofs pin the two against each other across the value kinds, the flag and interior versus
    /// exterior.
    ///
    /// The one reading this adds on top of <c>Resolve</c> is the negative-wrapped sentinel: the
    /// engine reads int-min as NO surface (measured 2026-09-04), so that is known dry rather than
    /// a height that failed to resolve.
    /// DEFECT 2 and 3: the historical composer never called this - there was no water term.</summary>
    public static WaterResolution Classify(
        DetectorModel.WaterTuple tuple, WorldWaterSnap? ws, bool exterior,
        string fromWorldspace)
    {
        var kind = DetectorModel.HeightKind(tuple.Height);
        var resolved = CellBoundaryIndex.Resolve(tuple, ws, exterior);
        if (kind == WaterHeightKind.NegativeWrapped)
            return new WaterResolution(StationWaterVerdict.KnownDryNoSurface, null, tuple.Height, kind,
                                       ws?.DefaultWaterHeight, fromWorldspace,
                                       "the engine reads this value as no surface; the station is dry");
        if (!resolved.Has)
            return new WaterResolution(StationWaterVerdict.KnownDryNoSurface, null, tuple.Height, kind,
                                       ws?.DefaultWaterHeight, fromWorldspace,
                                       "the resolver says this cell has no surface");
        if (resolved.Z is not { } z)
            return new WaterResolution(StationWaterVerdict.Unresolved, null, tuple.Height, kind,
                                       ws?.DefaultWaterHeight, fromWorldspace,
                                       "the cell has a surface whose height cannot be resolved");
        return kind == WaterHeightKind.Real
            ? new WaterResolution(StationWaterVerdict.Real, z, tuple.Height, kind,
                                  ws?.DefaultWaterHeight, fromWorldspace, "per-cell WaterHeight")
            : new WaterResolution(StationWaterVerdict.WorldspaceDefault, z, tuple.Height, kind,
                                  ws?.DefaultWaterHeight, fromWorldspace,
                                  "cell WaterHeight is the engine's no-value sentinel; "
                                  + "worldspace default applied");
    }

    /// <summary>Candidate stations for one primitive under one setup, from its geometry and the
    /// load order's answer at each station's own XY.</summary>
    public static StationYield Compose(
        PrimitiveGeometry p, string setup, Func<float, float, StationEnvironment> environment,
        float[]? kValues = null, float[]? laterals = null)
    {
        var y = new StationYield { Primitive = p.Key, Setup = setup };
        kValues ??= DefaultK;
        laterals ??= DefaultLaterals;

        var c = p.Position;
        float hz = p.ExtentFull.Z / 2f;
        float zLow = c.Z - hz, zHigh = c.Z + hz;
        bool degenerate = Degenerate(p);
        var reasons = new List<StationRejection>(4);

        int index = 0;
        foreach (var axis in FacesOf(p.Kind))
        {
            var (ax, ay) = LocalAxes(p.RotationRad);
            var n = axis == FaceAxis.Y ? ay : ax;       // the face's outward direction, unsided
            var right = new Vector3(-n.Y, n.X, 0f);
            var (faceHalf, lateralHalf, _) = Halves(p, axis);
            float basis = MathF.Min(lateralHalf, hz);
            foreach (float k in kValues)
            {
                float d = MathF.Max(StandoffFloor, MathF.Min(StandoffCap, k * basis));
                foreach (int side in new[] { 1, -1 })
                {
                    foreach (float lat in laterals)
                    {
                        y.Composed++;
                        int thisIndex = index++;
                        float offset = faceHalf + d;
                        float x = c.X + n.X * offset * side + right.X * lat * lateralHalf;
                        float yy = c.Y + n.Y * offset * side + right.Y * lat * lateralHalf;

                        reasons.Clear();
                        if (degenerate) reasons.Add(StationRejection.DegeneratePlane);

                        var env = environment(x, yy);
                        // The eye stands on the PHYSICAL FLOOR. There is no fallback to the
                        // navmesh height: falling back is the defect, and it would hide it.
                        var (floorKnown, floorZ, eyeZ) = CheckGround(env, reasons);

                        // A LEVEL gaze: the aim point stands at the eye's own height, so the
                        // camera never dips to a centre a thousand units under the ground.
                        var eye = new Vector3(x, yy, eyeZ);
                        var lookAt = new Vector3(c.X, c.Y, eyeZ);

                        // The primitive-relative tests are gathered BEFORE the water, although
                        // they rank BELOW it. The order is deliberate: it is what makes the stated
                        // precedence load-bearing here rather than an accident of the order the
                        // checks happen to run in, and a sabotage arm that drops the precedence
                        // has to be able to show it.
                        if (floorKnown)
                        {
                            if (EyeInside(p, eye)) reasons.Add(StationRejection.EyeInsidePrimitive);
                            if (eyeZ < zLow || eyeZ > zHigh)
                                reasons.Add(StationRejection.EyeOutsidePlaneExtent);
                        }

                        // The water, as the ONE resolver answered it for THIS station's cell.
                        float? feetAbove = CheckWater(env, floorKnown, floorZ, reasons);

                        float dx = lookAt.X - eye.X, dy = lookAt.Y - eye.Y;
                        float dxy = MathF.Sqrt(dx * dx + dy * dy);
                        float pitch = Pitch(eye, lookAt);   // measured, not asserted; CheckLevel guards it
                        float bearing = Bearing(dx, dy);

                        var view = dxy > 0f ? new Vector3(dx / dxy, dy / dxy, 0f) : Vector3.Zero;
                        var inward = new Vector3(-n.X * side, -n.Y * side, 0f);
                        float dot = Math.Clamp(Vector3.Dot(view, inward), -1f, 1f);
                        float toNormal = MathF.Acos(dot) * 180f / MathF.PI;

                        var pose = new StationPose
                        {
                            Primitive = p.Key, Kind = p.Kind, Setup = setup,
                            Cell = env.Cell, Worldspace = env.Worldspace,
                            StationIndex = thisIndex, FaceAxis = axis,
                            K = k, Side = side, Lateral = lat, Standoff = d,
                            Eye = eye, LookAt = lookAt, PitchDeg = pitch, BearingDeg = bearing,
                            AngleToPlaneNormalDeg = toNormal,
                            NavZ = env.NavZ, FloorZ = floorZ,
                            FloorSource = env.Floor.Source,
                            FloorOffset = env.NavZ - floorZ,
                            FloorAnchor = env.FloorAnchor,
                            NavmeshBurial = env.NavmeshBurial,
                            TerrainIsAnotherStorey = env.TerrainIsAnotherStorey,
                            FeetAboveSurface = feetAbove,
                            PlaneZLow = zLow, PlaneZHigh = zHigh,
                            Water = env.Water,
                            Accepted = false,
                        };

                        var guard = CheckLevel(pose);
                        if (guard != StationRejection.None) reasons.Add(guard);

                        var reason = FirstByPrecedence(reasons);
                        if (reason == StationRejection.None)
                        {
                            y.Accepted++;
                            y.Poses.Add(pose with { Accepted = true });
                        }
                        else
                        {
                            y.Rejected[reason] = (y.Rejected.TryGetValue(reason, out int m) ? m : 0) + 1;
                            y.Poses.Add(pose with
                            {
                                RejectionReason = reason,
                                RejectionDetail = Detail(reason, env, floorZ, eyeZ, zLow, zHigh, p),
                            });
                        }
                    }
                }
            }
        }
        return y;
    }

    static bool Degenerate(PrimitiveGeometry p)
    {
        foreach (float v in new[] { p.ExtentFull.X, p.ExtentFull.Y, p.ExtentFull.Z })
            if (!float.IsFinite(v) || v <= 0f) return true;
        foreach (float v in new[] { p.Position.X, p.Position.Y, p.Position.Z,
                                    p.RotationRad.X, p.RotationRad.Y, p.RotationRad.Z })
            if (!float.IsFinite(v)) return true;
        return false;
    }

    /// <summary>The numbers that decided a refusal, so a pose file can be judged without rerunning
    /// the rule. Never empty: a refusal with no numbers is a refusal nobody can check.</summary>
    static string Detail(StationRejection reason, StationEnvironment env, float floorZ, float eyeZ,
                         float zLow, float zHigh, PrimitiveGeometry p) => reason switch
    {
        StationRejection.DegeneratePlane =>
            $"extent {p.ExtentFull}, position {p.Position}, rotation {p.RotationRad}",
        StationRejection.NoNavmeshAnswer =>
            "the navmesh was asked about this XY and returned nothing at all",
        StationRejection.NoCoveringTriangle =>
            $"no winning triangle covers the XY; nearest navmesh height read {env.NavZ:0.###}",
        StationRejection.AllCoveringTrianglesDeleted =>
            $"every covering triangle is flagged deleted; navmesh height {env.NavZ:0.###}",
        StationRejection.FloorUnknown =>
            $"navmesh {env.NavZ:0.###}, no physical floor: {env.Floor.Reason}, "
            + $"{env.Floor.Surfaces} surface(s) in the column",
        StationRejection.WaterUnknown =>
            $"cell WaterHeight {env.Water.CellHeight:R} ({env.Water.Kind}), worldspace default "
            + $"{env.Water.WorldDefault:R} from {env.Water.FromWorldspace}: {env.Water.How}",
        StationRejection.StationUnderwater =>
            $"floor {floorZ:0.###} is below the surface {env.Water.SurfaceZ:0.###} "
            + $"({env.Water.Verdict}: {env.Water.How})",
        StationRejection.EyeInsidePrimitive =>
            $"eye z {eyeZ:0.###} lies inside the primitive's own volume {p.ExtentFull}",
        StationRejection.EyeOutsidePlaneExtent =>
            $"eye z {eyeZ:0.###} outside the primitive's vertical extent [{zLow:0.###}, {zHigh:0.###}]",
        StationRejection.NonLevelGaze =>
            $"the composed look-at left the eye's height {eyeZ:0.###}",
        _ => "no reason recorded",
    };
}
