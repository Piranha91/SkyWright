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
//  Two rules, each held down by a case recorded red at 34c5261d:
//    P1  the closure is asserted AT WRITE TIME, per bucket: accepted plus
//        the refusals must equal composed, and a bucket that does not close
//        refuses the write. The table is generated from one keyed
//        structure and checked, never typed - it was typed wrongly twice.
//    C4  a number nobody measured is published as null, never as a
//        sentinel. A refused pose has no eye (its height is NaN by
//        construction, because there was no floor to stand it on) and no
//        pitch either; a writer that throws on those poses loses exactly
//        what the refusal accounting exists to show.
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
        foreach (var y in yields)
            if (!y.Closes)
                throw new InvalidOperationException(
                    $"the yield for {y.Primitive} under {y.Setup} does not close: {y.Accepted} "
                    + $"accepted + {y.RejectedTotal} refused != {y.Composed} composed. Refusing to "
                    + "write a table whose total is wrong.");
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
    /// up by hand - and which were added up wrongly twice before this header existed. Every bucket
    /// is checked, aggregates included: the aggregate is what was wrong both times.</summary>
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
        {
            Close(v, "setup " + k);
            setups[k] = v.Json(withAStation.TryGetValue(k, out var w) ? w.Count : 0);
        }
        var prims = new Dictionary<string, object?>();
        foreach (var (k, m) in byPrimitive)
        {
            var inner = new Dictionary<string, object?>();
            foreach (var (s, b) in m) { Close(b, $"{k} under {s}"); inner[s] = b.Json(null); }
            prims[k] = inner;
        }
        return new Dictionary<string, object?> { ["bySetup"] = setups, ["byPrimitive"] = prims };
    }

    static void Close(Bucket b, string what)
    {
        if (!b.Closes)
            throw new InvalidOperationException(
                $"the yield bucket for {what} does not close: {b.Accepted} accepted + "
                + $"{b.RejectedTotal} refused != {b.Composed} composed. Refusing to write a table "
                + "whose total is wrong.");
    }

    sealed class Bucket
    {
        public int Composed, Accepted, Buried;
        public readonly Dictionary<string, int> Rejected = new();
        public void Add(StationYield y)
        {
            Composed += y.Composed; Accepted += y.Accepted;
            // DEFECT G3: the stations standing on ground the navmesh is buried beneath are not
            // counted, so their share is invisible in the table.
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
                ["stationsWithBuriedNavmesh"] = Buried,
            };
            if (primitivesWithAStation is { } p) d["primitivesWithAStation"] = p;
            return d;
        }
    }

    /// <summary>One pose as it is published. Every measured quantity goes through
    /// <see cref="N"/>: a refused pose's eye, pitch and offsets were never measured, and null says
    /// so where a sentinel number would be read as a height.</summary>
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
        ["standoff"] = N(p.Standoff),
        ["eye"] = new[] { N(p.Eye.X), N(p.Eye.Y), N(p.Eye.Z) },
        ["lookAt"] = new[] { N(p.LookAt.X), N(p.LookAt.Y), N(p.LookAt.Z) },
        ["pitchDeg"] = N(p.PitchDeg),
        ["bearingDeg"] = N(p.BearingDeg),
        ["angleToPlaneNormalDeg"] = N(p.AngleToPlaneNormalDeg),
        ["fovDeg"] = N(p.FovDeg),
        ["navZ"] = N(p.NavZ),
        ["floorZ"] = N(p.FloorZ),
        ["floorSource"] = p.FloorSource.ToString(),
        ["floorOffset"] = N(p.FloorOffset),
        ["floorAnchor"] = p.FloorAnchor.ToString(),
        ["navmeshBurial"] = N(p.NavmeshBurial),
        ["navmeshBuried"] = p.NavmeshBuried,
        ["eyeHeight"] = N(p.EyeHeight),
        ["feetAboveSurface"] = p.FeetAboveSurface is { } fa ? N(fa) : null,
        ["planeZLow"] = N(p.PlaneZLow),
        ["planeZHigh"] = N(p.PlaneZHigh),
        ["water"] = Water(p.Water),
        ["accepted"] = p.Accepted,
        ["rejectionReason"] = p.RejectionReason.ToString(),
        ["rejectionDetail"] = p.RejectionDetail,
    };

    static Dictionary<string, object?> Water(WaterResolution w) => new()
    {
        ["verdict"] = w.Verdict.ToString(),
        ["surfaceZ"] = w.SurfaceZ is { } sz ? N(sz) : null,
        // The cell's height as READ, sentinels and all: 3.4028235E+38 is a finite float and stays a
        // number here on purpose, because the raw value is what a reader checks the verdict against.
        ["cellHeight"] = w.CellHeight is { } ch ? N(ch) : null,
        ["kind"] = w.Kind.ToString(),
        ["worldDefault"] = w.WorldDefault is { } wd ? N(wd) : null,
        ["fromWorldspace"] = w.FromWorldspace,
        ["how"] = w.How,
    };

    /// <summary>A number a reader can trust, or null for one that was never measured. A refused
    /// pose has no eye, and a sentinel number in its place would be read as a height.</summary>
    internal static float? N(float v) => float.IsFinite(v) ? v : null;
}
