using System.Numerics;
using HousecarlCore;

namespace HarnessShared;

// ======================================================================
//  StationRule - the rule that chooses camera stations for an occlusion
//  primitive, and the record it publishes for every station it considers.
//
//  WHAT THIS FILE IS RIGHT NOW: a faithful reproduction of the station rule
//  as it stood on 2026-09-19/20, when it lived in run-local scripts. That
//  rule produced views no player can have, in four diagnosed ways, and each
//  of the four is reproduced here ON PURPOSE so that the cases in
//  proofprobe/StationRuleProof.cs fail on a WRONG ANSWER rather than on a
//  missing member. The corrected behaviour is specified by those cases and
//  lands in a later commit; until then every defect below is marked DEFECT n
//  and nothing in the tree calls this file except the proofs.
//
//  The four, as diagnosed:
//    1  the camera aimed at the primitive's CENTRE, which on an exterior
//       primitive thousands of units tall sits far below any walkable
//       surface, so the view pointed at the ground;
//    2  a station could stand under water, because there was no water test;
//    3  a cell whose WaterHeight is the engine's no-value sentinel was read
//       as "no water" instead of inheriting the worldspace default;
//    4  the eye was taken from the navmesh triangle rather than from the
//       physical floor a body settles on.
//
//  The services this rule is meant to stand on already exist and are named
//  where each defect is marked: DetectorModel.SurfaceHeight / HeightKind for
//  water, HousecarlCore.NavmeshFloor + FloorField (fed by FloorFeed) for the
//  floor. The composer does not re-derive either.
// ======================================================================

public enum PrimitiveKind : byte { PortalBox, Box }

/// <summary>Why a candidate station was refused. A closed set: nothing is refused for a reason
/// outside it, and an accepted pose carries <see cref="StationRejection.None"/>.</summary>
public enum StationRejection : byte
{
    None = 0,
    /// <summary>The navmesh was asked about this XY and returned nothing at all - an instrument
    /// failure, never a geometric miss.</summary>
    NoNavmeshAnswer,
    NoCoveringTriangle,
    AllCoveringTrianglesDeleted,
    /// <summary>A navmesh answer exists but no physical floor was found under it. Never falls
    /// back to the navmesh height.</summary>
    FloorUnknown,
    StationUnderwater,
    /// <summary>The applicable water surface could not be resolved. Refused, never read as dry.</summary>
    WaterUnknown,
    EyeOutsidePlaneExtent,
    DegeneratePlane,
    /// <summary>A guard. Level gaze is by construction, so this should never appear on a composed
    /// pose; it exists so a later change cannot reintroduce defect 1 in silence.</summary>
    NonLevelGaze,
}

/// <summary>An occlusion primitive's geometry, as the renderer already models it.</summary>
public readonly record struct PrimitiveGeometry(
    string Key, PrimitiveKind Kind, Vector3 ExtentFull, Vector3 Position, Vector3 RotationRad,
    string Worldspace, string Cell);

/// <summary>What the navmesh and the floor service answer at one station XY.
/// <paramref name="Answered"/> false means the instrument returned nothing.</summary>
public readonly record struct GroundAnswer(
    bool Answered, bool Covered, bool Drawn, float NavZ, FloorReading Floor);

/// <summary>The two record values the water rule needs: the cell's own WaterHeight and the
/// worldspace's DefaultWaterHeight, each as read, sentinels and all.</summary>
public readonly record struct WaterFacts(float? CellHeight, float? WorldDefault);

/// <summary>One candidate station, accepted or refused, with the aim geometry published so a
/// pose file can be judged before anything is rendered.</summary>
public sealed record StationPose
{
    public required string Primitive { get; init; }
    public required PrimitiveKind Kind { get; init; }
    public required string Setup { get; init; }
    public required string Worldspace { get; init; }
    public required string Cell { get; init; }
    public required int StationIndex { get; init; }
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

    // the water
    public float? WaterCellHeight { get; init; }
    public WaterHeightKind WaterHeightKind { get; init; }
    public float? WaterWorldDefault { get; init; }
    public float? WaterSurfaceZ { get; init; }
    public string WaterHow { get; init; } = "";

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

    /// <summary>Local +Y in the world for a Z-only rotation, as SpatialOverlap.RefrRotation gives
    /// it: (sin rotZ, cos rotZ, 0).</summary>
    public static Vector3 Normal(Vector3 rotationRad)
        => new(MathF.Sin(rotationRad.Z), MathF.Cos(rotationRad.Z), 0f);

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

    /// <summary>The applicable water surface at a cell, with the engine's own sentinel
    /// precedence, and whether it could be resolved at all.
    /// DEFECT 2 and 3: the historical rule never called this - there was no water term in the
    /// composer, and "no per-cell height logged" was later read as dry.</summary>
    public static (float? SurfaceZ, bool Known, string How) Surface(WaterFacts water)
    {
        var kind = DetectorModel.HeightKind(water.CellHeight);
        switch (kind)
        {
            case WaterHeightKind.Real:
                return (water.CellHeight, true, "per-cell WaterHeight");
            case WaterHeightKind.Unset:
            case WaterHeightKind.UnsignedWrapped:
            {
                var z = DetectorModel.SurfaceHeight(water.CellHeight, water.WorldDefault);
                return z is null
                    ? (null, false, "the cell defers to its worldspace and the worldspace default is not a real height")
                    : (z, true, "cell WaterHeight is the engine's no-value sentinel; worldspace default applied");
            }
            case WaterHeightKind.NegativeWrapped:
                return (null, true, "the engine reads this value as no surface; the station is dry");
            default:
                return (null, false, "the cell's WaterHeight is a sentinel this rule does not interpret");
        }
    }

    /// <summary>Candidate stations for one primitive under one setup, from its geometry, the
    /// ground answer at each station XY, and the cell's water facts.</summary>
    public static StationYield Compose(
        PrimitiveGeometry p, string setup, Func<float, float, GroundAnswer> ground,
        WaterFacts water, float[]? kValues = null, float[]? laterals = null)
    {
        var y = new StationYield { Primitive = p.Key, Setup = setup };
        kValues ??= DefaultK;
        laterals ??= DefaultLaterals;

        float w = p.ExtentFull.X, h = p.ExtentFull.Z;
        float hw = w / 2f, hh = h / 2f;
        var n = Normal(p.RotationRad);
        var right = new Vector3(-n.Y, n.X, 0f);
        float basis = MathF.Min(hw, hh);
        var c = p.Position;
        float zLow = c.Z - hh, zHigh = c.Z + hh;

        int index = 0;
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

                    var g = ground(x, yy);
                    if (!g.Answered) continue;                 // DEFECT: dropped, never counted
                    if (!g.Covered) continue;                  // DEFECT: dropped, never counted
                    if (!g.Drawn) continue;                    // DEFECT: dropped, never counted

                    // DEFECT 4: the eye is taken from the navmesh triangle. The physical floor
                    // (g.Floor.FloorZ, from HousecarlCore.FloorField) is read and published but
                    // is not what the eye stands on, and a missing floor does not refuse.
                    float eyeZ = g.NavZ + EyeHeight;

                    if (eyeZ < zLow || eyeZ > zHigh) continue; // DEFECT: dropped, never counted

                    // DEFECT 1: the aim point is the primitive's CENTRE, so the gaze dips to it.
                    var eye = new Vector3(x, yy, eyeZ);
                    var lookAt = new Vector3(c.X, c.Y, c.Z);

                    float dx = lookAt.X - eye.X, dy = lookAt.Y - eye.Y, dz = lookAt.Z - eye.Z;
                    float dxy = MathF.Sqrt(dx * dx + dy * dy);
                    float pitch = MathF.Atan2(dz, dxy) * 180f / MathF.PI;
                    float bearing = Bearing(dx, dy);

                    // the horizontal view direction against the primitive's inward normal
                    var view = dxy > 0f ? new Vector3(dx / dxy, dy / dxy, 0f) : Vector3.Zero;
                    var inward = new Vector3(-n.X * side, -n.Y * side, 0f);
                    float dot = Math.Clamp(Vector3.Dot(view, inward), -1f, 1f);
                    float toNormal = MathF.Acos(dot) * 180f / MathF.PI;

                    // DEFECTS 2 and 3: no water test. Nothing is resolved and nothing is refused.
                    y.Accepted++;
                    y.Poses.Add(new StationPose
                    {
                        Primitive = p.Key, Kind = p.Kind, Setup = setup,
                        Worldspace = p.Worldspace, Cell = p.Cell,
                        StationIndex = thisIndex, K = k, Side = side, Lateral = lat, Standoff = d,
                        Eye = eye, LookAt = lookAt, PitchDeg = pitch, BearingDeg = bearing,
                        AngleToPlaneNormalDeg = toNormal,
                        NavZ = g.NavZ, FloorZ = g.Floor.FloorZ, FloorSource = g.Floor.Source,
                        FloorOffset = g.NavZ - g.Floor.FloorZ,
                        FeetAboveSurface = null,
                        PlaneZLow = zLow, PlaneZHigh = zHigh,
                        WaterCellHeight = water.CellHeight,
                        WaterHeightKind = DetectorModel.HeightKind(water.CellHeight),
                        WaterWorldDefault = water.WorldDefault,
                        WaterSurfaceZ = null,
                        WaterHow = "no water test",
                        Accepted = true,
                    });
                }
            }
        }
        return y;
    }
}
