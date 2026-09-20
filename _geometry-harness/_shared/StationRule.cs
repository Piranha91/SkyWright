using System.Numerics;
using HousecarlCore;

namespace HarnessShared;

// ======================================================================
//  StationRule - the rule that chooses camera stations for an occlusion
//  primitive, and the record it publishes for every station it considers.
//
//  WHAT THIS FILE IS RIGHT NOW: a faithful reproduction of the station rule
//  as it stood on 2026-09-19/20, when it lived in run-local scripts. That
//  rule produced views no player can have, and each of its defects is
//  reproduced here ON PURPOSE so that the cases in
//  proofprobe/StationRuleProof.cs fail on a WRONG ANSWER rather than on a
//  missing member. The corrected behaviour is specified by those cases and
//  lands in later commits; until then every defect is marked DEFECT and
//  nothing in the tree calls this file except the proofs.
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

    /// <summary>The reason a station is counted under, when it fails several.
    /// DEFECT B3: this returns whichever was offered first, not the most decisive.</summary>
    public static StationRejection FirstByPrecedence(IEnumerable<StationRejection> reasons)
    {
        foreach (var r in reasons)
            if (r != StationRejection.None) return r;   // DEFECT B3: input order, not Precedence
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

    /// <summary>The faces a primitive of this kind is composed around.
    /// DEFECT A3: a Box has four upright faces and is predicted by its silhouette, but this
    /// returns the single authored axis for both kinds.</summary>
    public static FaceAxis[] FacesOf(PrimitiveKind kind)
    {
        _ = kind;
        return new[] { FaceAxis.Y };                    // DEFECT A3: a Box gets one face, not two axes
    }

    /// <summary>Whether an eye lies inside or on the primitive's own volume, where
    /// <c>ConvexShadow.FromBox</c> yields no shadow and the station cannot be ranked.
    /// DEFECT: the historical rule had no such test, so this answers "never inside".</summary>
    public static bool EyeInside(PrimitiveGeometry p, Vector3 eye)
    {
        _ = p; _ = eye;
        return false;                                   // DEFECT A3: no inside test existed
    }

    /// <summary>Horizontal bearing of a direction, 0 = +Y, increasing clockwise, in [0, 360).</summary>
    public static float Bearing(float dx, float dy)
    {
        float d = MathF.Atan2(dx, dy) * 180f / MathF.PI;
        return d < 0f ? d + 360f : d;
    }

    /// <summary>The level-gaze guard. A pose whose look-at height differs from its eye height is
    /// not a level gaze, whatever composed it.
    /// DEFECT 1's guard: the historical rule had none, so this returns None unconditionally.</summary>
    public static StationRejection CheckLevel(StationPose pose)
    {
        _ = pose;
        return StationRejection.None;          // DEFECT 1 (guard): no level-gaze check existed
    }

    /// <summary>A worldspace's usable default water height, following the parent chain when the
    /// worldspace itself defers - a city worldspace commonly takes its water from Tamriel.
    /// <paramref name="lookup"/> answers (this worldspace's own DefaultWaterHeight, its parent).
    /// DEFECT: this reads the worldspace's own value and never follows the parent.</summary>
    public static (float? Value, string From, int Hops, bool Cycle) ResolveWorldDefault(
        string worldspace, Func<string, (float? Default, string? Parent)> lookup, int maxHops = 8)
    {
        _ = maxHops;
        var (own, _parent) = lookup(worldspace);        // DEFECT: no parent walk
        return (DetectorModel.HeightKind(own) == WaterHeightKind.Real ? own : null, worldspace, 0, false);
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

        float w = p.ExtentFull.X, h = p.ExtentFull.Z;
        float hw = w / 2f, hh = h / 2f;
        float basis = MathF.Min(hw, hh);
        var c = p.Position;
        float zLow = c.Z - hh, zHigh = c.Z + hh;

        int index = 0;
        foreach (var axis in FacesOf(p.Kind))
        {
            var n = axis == FaceAxis.Y ? Normal(p.RotationRad)
                                       : new Vector3(-Normal(p.RotationRad).Y, Normal(p.RotationRad).X, 0f);
            var right = new Vector3(-n.Y, n.X, 0f);
            foreach (float k in kValues)
            {
                float d = MathF.Max(StandoffFloor, MathF.Min(StandoffCap, k * basis));
                foreach (int side in new[] { 1, -1 })
                {
                    foreach (float lat in laterals)
                    {
                        y.Composed++;
                        int thisIndex = index++;
                        float x = c.X + n.X * d * side + right.X * lat * hw;
                        float yy = c.Y + n.Y * d * side + right.Y * lat * hw;

                        var env = environment(x, yy);
                        if (!env.Answered) continue;               // DEFECT: dropped, never counted
                        if (!env.Covered) continue;                // DEFECT: dropped, never counted
                        if (!env.Drawn) continue;                  // DEFECT: dropped, never counted

                        // DEFECT 4: the eye is taken from the navmesh triangle. The physical floor
                        // (env.Floor.FloorZ, from HousecarlCore.FloorField) is read and published
                        // but is not what the eye stands on, and a missing floor does not refuse.
                        float eyeZ = env.NavZ + EyeHeight;

                        if (eyeZ < zLow || eyeZ > zHigh) continue; // DEFECT: dropped, never counted

                        // DEFECT 1: the aim point is the primitive's CENTRE, so the gaze dips to it.
                        var eye = new Vector3(x, yy, eyeZ);
                        var lookAt = new Vector3(c.X, c.Y, c.Z);

                        float dx = lookAt.X - eye.X, dy = lookAt.Y - eye.Y, dz = lookAt.Z - eye.Z;
                        float dxy = MathF.Sqrt(dx * dx + dy * dy);
                        float pitch = MathF.Atan2(dz, dxy) * 180f / MathF.PI;
                        float bearing = Bearing(dx, dy);

                        var view = dxy > 0f ? new Vector3(dx / dxy, dy / dxy, 0f) : Vector3.Zero;
                        var inward = new Vector3(-n.X * side, -n.Y * side, 0f);
                        float dot = Math.Clamp(Vector3.Dot(view, inward), -1f, 1f);
                        float toNormal = MathF.Acos(dot) * 180f / MathF.PI;

                        // DEFECTS 2, 3 and B1: no water test at all, and the pose names the
                        // PRIMITIVE's cell and worldspace rather than the station's own.
                        y.Accepted++;
                        y.Poses.Add(new StationPose
                        {
                            Primitive = p.Key, Kind = p.Kind, Setup = setup,
                            Cell = p.Cell, Worldspace = p.Worldspace,
                            StationIndex = thisIndex, FaceAxis = axis,
                            K = k, Side = side, Lateral = lat, Standoff = d,
                            Eye = eye, LookAt = lookAt, PitchDeg = pitch, BearingDeg = bearing,
                            AngleToPlaneNormalDeg = toNormal,
                            NavZ = env.NavZ, FloorZ = env.Floor.FloorZ,
                            FloorSource = env.Floor.Source,
                            FloorOffset = env.NavZ - env.Floor.FloorZ,
                            FeetAboveSurface = null,
                            PlaneZLow = zLow, PlaneZHigh = zHigh,
                            Water = WaterResolution.NotTested,
                            Accepted = true,
                        });
                    }
                }
            }
        }
        return y;
    }
}
