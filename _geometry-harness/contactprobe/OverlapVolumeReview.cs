using System.Security.Cryptography;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

/// <summary>contactprobe --volume OUTPUT (with --overlap-json INPUT): the measured overlap volume of each supplied pair
/// (patch-miss packet, GATE-A §5 and §8). For each pair and each geometry — render, then collision, never mixed — both
/// members are read as model-space solids (<see cref="MeshVolume.ModelSolid"/>: closed, validated, the material gate for
/// render), placed by their own winning transforms and measured (<see cref="MeshVolume.MeasurePlaced"/>). A member that is
/// not a closed solid refuses the measurement with its reason; no volume is estimated for it. Every measured result is an
/// interval; no midpoint is written. The file is separate from the pair review's --json, which is unchanged.</summary>
static class OverlapVolumeReview
{
    sealed record Member(string Status, string? Reason, byte[]? Bytes, Snap? Pose, string? Base, string? Mesh);

    public static int Run(string inputPath, string outputPath, AssetResolver assets, OverlapIndex index, EnableArms arms,
                          ConfigurationIdentity configuration, Func<FormKey, Snap?> winningPose, MotionReview.Cache? motion = null)
    {
        var inputBytes = File.ReadAllBytes(inputPath);
        using var input = JsonDocument.Parse(inputBytes);
        var pairs = OverlapPairReview.ReadPairs(input.RootElement);
        var cache = new Dictionary<string, Member>();
        var winds = new Dictionary<string, WindLabel?>();
        var json = new JsonSerializerOptions { WriteIndented = true };
        WindLabel? WindOf(string formKey, Member m)
        {
            if (m.Bytes is null) return null;
            if (!winds.TryGetValue(formKey, out var w)) winds[formKey] = w = NifWind.Label(m.Bytes);
            return w;
        }

        Member Load(string formKey)
        {
            if (cache.TryGetValue(formKey, out var hit)) return hit;
            Member m;
            if (!FormKey.TryFactory(formKey, out var key)) m = new("invalid_reference", "not a FormKey", null, null, null, null);
            else
            {
                var pose = winningPose(key);
                var en = arms.Winner(key);
                string? mesh = null;
                FormKey basis = default;
                if (pose is null) m = new("absent", "reference has no winning placement in this order", null, null, null, null);
                else if (index.IsDeletedNow(key) || pose.Value.Deleted) m = new("absent", "winning reference is deleted", null, pose, null, null);
                else if (!en.Resolved) m = new("unmeasurable", "enable chain is unresolved", null, pose, null, null);
                else if (!en.Enabled) m = new("absent", "reference is disabled in the declared state", null, pose, null, null);
                else if (!index.RefBase.TryGetValue(key, out basis)) m = new("unmeasurable", "winning reference has no base", null, pose, null, null);
                else if (!index.BaseMesh.TryGetValue(basis, out mesh)) m = new("unmeasurable", "winning base has no model path", null, pose, basis.ToString(), null);
                else
                {
                    var rel = mesh.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? mesh : Path.Combine("meshes", mesh);
                    var res = assets.ResolveForPlacement(rel);
                    if (res.Sources.Count == 0) m = new("unmeasurable", "missing_asset: " + rel, null, pose, basis.ToString(), rel);
                    else
                    {
                        var (bytes, error) = AssetResolver.ReadPlacementSource(res.Sources[0]);
                        m = bytes is null ? new("unmeasurable", "asset read failed: " + error, null, pose, basis.ToString(), rel)
                                          : new("read", null, bytes, pose, basis.ToString(), rel);
                    }
                }
            }
            cache[formKey] = m;
            return m;
        }

        var records = new List<object>();
        int measured = 0, refused = 0, unavailable = 0;
        bool solidApplied = false;   // a member read as a solid, or a motion object, went through the solid rule
        foreach (var p in pairs)
        {
            var a = Load(p.A);
            var b = Load(p.B);
            if (p.ADz is not null) a = a with { Pose = OverlapPairReview.Shift(a.Pose, p.ADz) };
            if (p.BDz is not null) b = b with { Pose = OverlapPairReview.Shift(b.Pose, p.BDz) };
            var geoms = new List<object>();
            foreach (var g in new[] { VolumeGeometry.Render, VolumeGeometry.Collision })
            {
                string gname = g.ToString().ToLowerInvariant();
                // E1: a render entry of a member with a visible Tree_Anim shape carries windA / windB (values unchanged)
                void Add(object entry) => geoms.Add(g == VolumeGeometry.Render ? WindNotes.Attach(entry, WindOf(p.A, a), WindOf(p.B, b), json) : entry);
                if (a.Bytes is null || b.Bytes is null)
                {
                    unavailable++;
                    geoms.Add(new { geometry = gname, status = "unavailable",
                                    reason = a.Bytes is null ? $"A: {a.Status}: {a.Reason}" : $"B: {b.Status}: {b.Reason}" });
                    continue;
                }
                var sa = MeshVolume.ModelSolid(a.Bytes, g);
                var sb = MeshVolume.ModelSolid(b.Bytes, g);
                // a member read as a solid passed the solid rule (a member the rule refused is not told apart from one refused earlier)
                solidApplied |= sa.Status == "solid" || sb.Status == "solid";
                if ((sa.Status != "solid" || sb.Status != "solid") && motion is not null
                    && MotionReview.Pair(motion, g, p.A, a.Bytes, a.Pose!.Value, p.B, b.Bytes, b.Pose!.Value,
                                         bytes => { var s = MeshVolume.ModelSolid(bytes, g); return s.Status == "solid" ? (s.Soup, null, null) : (null, null, s.Reason); },
                                         volume: true) is { } mr)
                {
                    solidApplied |= MotionReview.NamesRule(mr, json);
                    Add(mr);
                    continue;
                }
                if (sa.Status != "solid" || sb.Status != "solid")
                {
                    refused++;
                    Add(new { geometry = gname, status = "refused",
                                    reason = string.Join("; ", new[] { sa.Status != "solid" ? $"A: {sa.Reason}" : null, sb.Status != "solid" ? $"B: {sb.Reason}" : null }.Where(s => s is not null)) });
                    continue;
                }
                var pa = a.Pose!.Value; var pb = b.Pose!.Value;
                var (r, frame) = MeshVolume.MeasurePlacedFramed(sa.Soup!, pa.Pos, pa.Rot, pa.Scale, sb.Soup!, pb.Pos, pb.Rot, pb.Scale, g);
                if (r.Status == "measured") measured++; else refused++;
                Add(ContactNotes.Attach(new { geometry = gname, status = r.Status, reason = r.Reason, volumeA = r.VolumeA, volumeB = r.VolumeB,
                                intersection = r.Intersection, aInB = r.AInB, bInA = r.BInA, converged = r.Converged, cells = r.Cells, depth = r.Depth,
                                policy = r.PolicyVersion }, json, ("pairFrame", ContactNotes.Frame(frame))));
            }
            records.Add(p.ADz is null && p.BDz is null
                ? new { pairKey = p.PairKey, a = p.A, b = p.B, baseA = a.Base, baseB = b.Base, meshA = a.Mesh, meshB = b.Mesh, geometries = geoms }
                : (object)new { pairKey = p.PairKey, a = p.A, b = p.B, aPose = OverlapPairReview.PoseEcho(p.ADz), bPose = OverlapPairReview.PoseEcho(p.BDz),
                                baseA = a.Base, baseB = b.Base, meshA = a.Mesh, meshB = b.Mesh, geometries = geoms });
        }
        var doc = new
        {
            schema = "housecarl.overlap-volume/1", policy = MeshVolume.PolicyVersion, solidPolicy = MotionReview.SolidPolicy(solidApplied),
            inputPath = Path.GetFullPath(inputPath), inputSha256 = Convert.ToHexString(SHA256.HashData(inputBytes)).ToLowerInvariant(),
            configuration = new { configuration.Fingerprint, configuration.Source, configuration.Population, configuration.State },
            notes = new[] { "Render and collision are measured separately and never mixed.",
                            "Only validated closed solids are measured; a refused member names its reason, and no volume is estimated for it.",
                            "Every value is an interval [Lower, Upper] guaranteed for the stated geometry; no midpoint is reported." },
            pairs = pairs.Count, measured, refused, unavailable, records,
        };
        var full = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, arms.StampForced(JsonSerializer.Serialize(doc, json)));
        Console.WriteLine($"--volume: {pairs.Count} pair(s): {measured} geometry measurement(s), {refused} refused, {unavailable} unavailable -> {full}");
        return 0;
    }
}
