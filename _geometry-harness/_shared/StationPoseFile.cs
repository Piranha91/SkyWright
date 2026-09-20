using System.Text.Json;

namespace HarnessShared;

// ======================================================================
//  StationPoseFile - the pose file StationRule.Compose's yields are
//  published as, with the yield header and its closure assertion.
//
//  It is shared rather than private to the probe that writes it for one
//  reason: the two things that can go wrong here are only visible from a
//  test, and a writer inside a render path cannot be given one. A refused
//  pose has no eye - its height is NaN by construction, because there was
//  no floor to stand it on - and a writer that throws on that pose loses
//  exactly the poses the refusal accounting exists to show.
//
//  WHAT THIS FILE IS RIGHT NOW: the naive writer. Both defects are
//  reproduced ON PURPOSE so the cases in proofprobe/StationRuleProof.cs
//  fail on a WRONG ANSWER rather than on a missing member.
//
//  The defects held down by the proofs:
//    P1  the yield's closure (accepted + sum(refused) == composed) is not
//        asserted at write time, so a miscount is published as a table;
//    P2  a pose's eye is written as a raw float, so the ONE pose kind that
//        has no eye - a refused one - takes the whole file down with it.
// ======================================================================

public static class StationPoseFile
{
    public const string Schema = "occl-transfer/station-poses/1";

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    /// <summary>The pose file as a string, so a test can read it without a filesystem.</summary>
    public static string Render(IReadOnlyDictionary<string, object?> header,
                                IReadOnlyList<StationYield> yields)
    {
        var doc = new Dictionary<string, object?> { ["schema"] = Schema };
        foreach (var (k, v) in header) doc[k] = v;
        doc["yield"] = YieldHeader(yields);
        var poses = new List<object>();
        foreach (var y in yields)
            foreach (var p in y.Poses) poses.Add(Dto(p));
        doc["poses"] = poses;
        return JsonSerializer.Serialize(doc, Opts);
    }

    /// <summary>The pose file on disk. Returns the path written.</summary>
    public static string Write(string path, IReadOnlyDictionary<string, object?> header,
                               IReadOnlyList<StationYield> yields)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, Render(header, yields));
        return Path.GetFullPath(path);
    }

    /// <summary>Per primitive and per setup, with the totals a reader would otherwise have to add
    /// up by hand - and which were added up wrongly twice before this header existed.
    /// DEFECT P1: nothing here checks that a bucket closes.</summary>
    static Dictionary<string, object?> YieldHeader(IReadOnlyList<StationYield> yields)
    {
        var bySetup = new Dictionary<string, Bucket>();
        var byPrimitive = new Dictionary<string, Dictionary<string, Bucket>>();
        var withAStation = new Dictionary<string, HashSet<string>>();
        foreach (var y in yields)
        {
            if (!bySetup.TryGetValue(y.Setup, out var s)) bySetup[y.Setup] = s = new Bucket();
            s.Add(y);
            if (!byPrimitive.TryGetValue(y.Primitive, out var m))
                byPrimitive[y.Primitive] = m = new Dictionary<string, Bucket>();
            if (!m.TryGetValue(y.Setup, out var b)) m[y.Setup] = b = new Bucket();
            b.Add(y);
            if (!withAStation.TryGetValue(y.Setup, out var set))
                withAStation[y.Setup] = set = new HashSet<string>();
            if (y.Accepted > 0) set.Add(y.Primitive);
        }
        var setups = new Dictionary<string, object?>();
        foreach (var (k, v) in bySetup)
            setups[k] = v.Json(withAStation.TryGetValue(k, out var w) ? w.Count : 0);
        var prims = new Dictionary<string, object?>();
        foreach (var (k, m) in byPrimitive)
        {
            var inner = new Dictionary<string, object?>();
            foreach (var (s, b) in m) inner[s] = b.Json(null);
            prims[k] = inner;
        }
        return new Dictionary<string, object?> { ["bySetup"] = setups, ["byPrimitive"] = prims };
    }

    sealed class Bucket
    {
        public int Composed, Accepted;
        public readonly Dictionary<string, int> Rejected = new();
        public void Add(StationYield y)
        {
            Composed += y.Composed; Accepted += y.Accepted;
            foreach (var (r, n) in y.Rejected)
                Rejected[r.ToString()] = (Rejected.TryGetValue(r.ToString(), out int m) ? m : 0) + n;
        }
        public int RejectedTotal { get { int n = 0; foreach (var v in Rejected.Values) n += v; return n; } }
        public bool Closes => Accepted + RejectedTotal == Composed;
        public Dictionary<string, object?> Json(int? primitivesWithAStation)
        {
            var d = new Dictionary<string, object?>
            {
                ["composed"] = Composed, ["accepted"] = Accepted, ["rejected"] = Rejected,
            };
            if (primitivesWithAStation is { } p) d["primitivesWithAStation"] = p;
            return d;
        }
    }

    /// <summary>One pose as it is published.
    /// DEFECT P2: the eye, the aim point and the heights are written as raw floats, and a REFUSED
    /// pose's eye is NaN by construction.</summary>
    static Dictionary<string, object?> Dto(StationPose p) => new()
    {
        ["primitive"] = p.Primitive,
        ["kind"] = p.Kind.ToString(),
        ["setup"] = p.Setup,
        ["cell"] = p.Cell,
        ["worldspace"] = p.Worldspace,
        ["stationIndex"] = p.StationIndex,
        ["faceAxis"] = p.FaceAxis.ToString(),
        ["k"] = p.K,
        ["side"] = p.Side,
        ["lateral"] = p.Lateral,
        ["standoff"] = p.Standoff,
        ["eye"] = new[] { p.Eye.X, p.Eye.Y, p.Eye.Z },
        ["lookAt"] = new[] { p.LookAt.X, p.LookAt.Y, p.LookAt.Z },
        ["pitchDeg"] = p.PitchDeg,
        ["bearingDeg"] = p.BearingDeg,
        ["angleToPlaneNormalDeg"] = p.AngleToPlaneNormalDeg,
        ["fovDeg"] = p.FovDeg,
        ["navZ"] = p.NavZ,
        ["floorZ"] = p.FloorZ,
        ["floorSource"] = p.FloorSource.ToString(),
        ["floorOffset"] = p.FloorOffset,
        ["eyeHeight"] = p.EyeHeight,
        ["feetAboveSurface"] = p.FeetAboveSurface,
        ["planeZLow"] = p.PlaneZLow,
        ["planeZHigh"] = p.PlaneZHigh,
        ["water"] = Water(p.Water),
        ["accepted"] = p.Accepted,
        ["rejectionReason"] = p.RejectionReason.ToString(),
        ["rejectionDetail"] = p.RejectionDetail,
    };

    static Dictionary<string, object?> Water(WaterResolution w) => new()
    {
        ["verdict"] = w.Verdict.ToString(),
        ["surfaceZ"] = w.SurfaceZ,
        ["cellHeight"] = w.CellHeight,
        ["kind"] = w.Kind.ToString(),
        ["worldDefault"] = w.WorldDefault,
        ["fromWorldspace"] = w.FromWorldspace,
        ["how"] = w.How,
    };

    /// <summary>A number a reader can trust, or null for one that was never measured. A refused
    /// pose has no eye, and a sentinel number in its place would be read as a height.</summary>
    internal static float? N(float v) => float.IsFinite(v) ? v : null;
}
