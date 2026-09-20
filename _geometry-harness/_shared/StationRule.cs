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
    /// <summary>A guard. Level gaze is by construction, so this should never appear on a composed
    /// pose; it exists so a later change cannot reintroduce defect 1 in silence.</summary>
    NonLevelGaze,
}

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
    string Cell, string Worldspace, WaterResolution Water);

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
        StationRejection.NonLevelGaze,
    };

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
    public static StationRejection CheckLevel(StationPose pose)
        => MathF.Abs(pose.LookAt.Z - pose.Eye.Z) > 1e-4f
            ? StationRejection.NonLevelGaze : StationRejection.None;

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
                        bool ground = env.Answered && env.Covered && env.Drawn;
                        if (!env.Answered) reasons.Add(StationRejection.NoNavmeshAnswer);
                        else if (!env.Covered) reasons.Add(StationRejection.NoCoveringTriangle);
                        else if (!env.Drawn) reasons.Add(StationRejection.AllCoveringTrianglesDeleted);
                        else if (!env.Floor.Found) reasons.Add(StationRejection.FloorUnknown);

                        // The eye stands on the PHYSICAL FLOOR. There is no fallback to the
                        // navmesh height: falling back is the defect, and it would hide it.
                        bool floorKnown = ground && env.Floor.Found;
                        float floorZ = env.Floor.FloorZ;
                        float eyeZ = floorKnown ? floorZ + EyeHeight : float.NaN;

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
                        float? feetAbove = null;
                        if (env.Water.Verdict == StationWaterVerdict.Unresolved)
                            reasons.Add(StationRejection.WaterUnknown);
                        else if (env.Water.SurfaceZ is { } surfaceZ && floorKnown)
                        {
                            feetAbove = floorZ - surfaceZ;
                            if (floorZ < surfaceZ) reasons.Add(StationRejection.StationUnderwater);
                        }

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
