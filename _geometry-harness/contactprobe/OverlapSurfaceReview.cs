using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

/// <summary>contactprobe --surface OUTPUT (with --overlap-json INPUT): how each supplied pair's placed surfaces meet
/// (patch-miss packet, session 6) — the crossing length, the coplanar areas split by facing, and the area of one surface
/// buried in the other where that other is a validated closed solid (<see cref="MeshSurface"/>). Render (every visible
/// shape, tagged by material) and collision (solid bodies) are measured separately, never mixed. A member whose live pose
/// is not established, or whose geometry cannot be read, refuses that geometry with its reason; nothing is estimated
/// from bounds or a stored pose. The live-pose rule is LivePoseDefault.Policy (targeted) unless --live-pose names one.
/// The file is separate from the pair review's --json and the --volume file, which are unchanged.</summary>
static class OverlapSurfaceReview
{
    sealed record Member(string Status, string? Reason, byte[]? Bytes, Snap? Pose, string? Base, string? Mesh);

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static int Run(string inputPath, string outputPath, LivePosePolicy pose, AssetResolver assets, OverlapIndex index, EnableArms arms,
                          ConfigurationIdentity configuration, Func<FormKey, Snap?> winningPose, MotionReview.Cache? motion = null,
                          AlphaVisibility? alpha = null)
    {
        var inputBytes = File.ReadAllBytes(inputPath);
        using var input = JsonDocument.Parse(inputBytes);
        var pairs = OverlapPairReview.ReadPairs(input.RootElement);
        var members = new Dictionary<string, Member>();
        var surfaces = new Dictionary<(string, VolumeGeometry), TaggedSurface>();
        var winds = new Dictionary<string, WindLabel?>();
        WindLabel? WindOf(string formKey, Member m)
        {
            if (m.Bytes is null) return null;
            if (!winds.TryGetValue(formKey, out var w)) winds[formKey] = w = NifWind.Label(m.Bytes);
            return w;
        }

        Member Load(string formKey)
        {
            if (members.TryGetValue(formKey, out var hit)) return hit;
            Member m;
            if (!FormKey.TryFactory(formKey, out var key)) m = new("invalid_reference", "not a FormKey", null, null, null, null);
            else
            {
                var p = winningPose(key);
                var en = arms.Winner(key);
                if (p is null) m = new("absent", "reference has no winning placement in this order", null, null, null, null);
                else if (index.IsDeletedNow(key) || p.Value.Deleted) m = new("absent", "winning reference is deleted", null, p, null, null);
                else if (!en.Resolved) m = new("unmeasurable", "enable chain is unresolved", null, p, null, null);
                else if (!en.Enabled) m = new("absent", "reference is disabled in the declared state", null, p, null, null);
                else if (!index.RefBase.TryGetValue(key, out var basis)) m = new("unmeasurable", "winning reference has no base", null, p, null, null);
                else if (!index.BaseMesh.TryGetValue(basis, out var mesh)) m = new("unmeasurable", "winning base has no model path", null, p, basis.ToString(), null);
                else
                {
                    var rel = mesh.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? mesh : Path.Combine("meshes", mesh);
                    var res = assets.ResolveForPlacement(rel);
                    if (res.Sources.Count == 0) m = new("unmeasurable", "missing_asset: " + rel, null, p, basis.ToString(), rel);
                    else
                    {
                        var (bytes, error) = AssetResolver.ReadPlacementSource(res.Sources[0]);
                        m = bytes is null ? new("unmeasurable", "asset read failed: " + error, null, p, basis.ToString(), rel)
                                          : new("read", null, bytes, p, basis.ToString(), rel);
                    }
                }
            }
            members[formKey] = m;
            return m;
        }
        TaggedSurface Surface(string formKey, Member m, VolumeGeometry g)
        {
            if (!surfaces.TryGetValue((formKey, g), out var s)) surfaces[(formKey, g)] = s = MeshSurface.ModelSurface(m.Bytes!, g, pose);
            return s;
        }

        var records = new List<object>();
        int measured = 0, incomplete = 0, refused = 0, unavailable = 0;
        bool solidApplied = false;   // a measured row's buried area, or a motion object, went through the solid rule
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
                void Add(object entry) => geoms.Add(g == VolumeGeometry.Render ? WindNotes.Attach(entry, WindOf(p.A, a), WindOf(p.B, b), Json) : entry);
                if (a.Bytes is null || b.Bytes is null)
                {
                    unavailable++;
                    geoms.Add(new { geometry = gname, status = "unavailable",
                                    reason = a.Bytes is null ? $"A: {a.Status}: {a.Reason}" : $"B: {b.Status}: {b.Reason}" });
                    continue;
                }
                var sa = Surface(p.A, a, g);
                var sb = Surface(p.B, b, g);
                if ((sa.Status != "measured" || sb.Status != "measured") && motion is not null
                    && MotionReview.Pair(motion, g, p.A, a.Bytes, a.Pose!.Value, p.B, b.Bytes, b.Pose!.Value,
                                         bytes => { var s = MeshSurface.ModelSurface(bytes, g, pose); return s.Status == "measured" ? (s.Soup, s.Tags, null) : (null, null, s.Reason); },
                                         volume: false, alpha: alpha) is { } mr)
                {
                    solidApplied |= MotionReview.NamesRule(mr, Json);
                    Add(mr);
                    continue;
                }
                if (sa.Status != "measured" || sb.Status != "measured")
                {
                    refused++;
                    Add(new { geometry = gname, status = "refused",
                                    reason = string.Join("; ", new[] { sa.Status != "measured" ? $"A: {sa.Reason}" : null, sb.Status != "measured" ? $"B: {sb.Reason}" : null }.Where(s => s is not null)) });
                    continue;
                }
                var pa = a.Pose!.Value; var pb = b.Pose!.Value;
                SurfaceContactResult r; Vec3 frame; object? visibility = null;
                if (alpha is not null && g == VolumeGeometry.Render)
                {
                    // --alpha-visibility: the same measure with its crossing pieces; textures are read only for a piece on an alpha triangle
                    var (v, f) = SurfaceVisibility.MeasurePlaced(sa.Soup!, sa.Tags, alpha.Source(p.A, a.Bytes, sa.Soup!), pa.Pos, pa.Rot, pa.Scale,
                                                                 sb.Soup!, sb.Tags, alpha.Source(p.B, b.Bytes, sb.Soup!), pb.Pos, pb.Rot, pb.Scale, alpha.Options);
                    (r, frame) = (v.Contact!, f);
                    if (AlphaVisibility.HasAlphaCrossing(r.CrossingLengthByTags)) visibility = alpha.Block(v, p.A, p.B);
                }
                else (r, frame) = MeshSurface.MeasurePlacedFramed(sa.Soup!, sa.Tags, pa.Pos, pa.Rot, pa.Scale, sb.Soup!, sb.Tags, pb.Pos, pb.Rot, pb.Scale, g);
                if (r.Status == "measured") { measured++; solidApplied = true; } else if (r.Status == "incomplete") incomplete++; else refused++;
                // lane 3b: the pair frame when placement collapsed a triangle; the particle systems set aside from either render surface;
                // Group 6 D2: render shapes read as empty (no vertices, no triangles)
                Add(ContactNotes.Attach(r, Json, ("pairFrame", ContactNotes.Frame(frame)), ("particlesA", sa.Labels), ("particlesB", sb.Labels),
                                        ("emptyShapesA", sa.EmptyShapes), ("emptyShapesB", sb.EmptyShapes), ("visibility", visibility)));
            }
            records.Add(p.ADz is null && p.BDz is null
                ? new { pairKey = p.PairKey, a = p.A, b = p.B, baseA = a.Base, baseB = b.Base, meshA = a.Mesh, meshB = b.Mesh, geometries = geoms }
                : (object)new { pairKey = p.PairKey, a = p.A, b = p.B, aPose = OverlapPairReview.PoseEcho(p.ADz), bPose = OverlapPairReview.PoseEcho(p.BDz),
                                baseA = a.Base, baseB = b.Base, meshA = a.Mesh, meshB = b.Mesh, geometries = geoms });
        }
        var doc = new
        {
            schema = "housecarl.overlap-surface/1", policy = MeshSurface.PolicyVersion, solidPolicy = MotionReview.SolidPolicy(solidApplied), livePose = pose.ToString().ToLowerInvariant(),
            inputPath = Path.GetFullPath(inputPath), inputSha256 = Convert.ToHexString(SHA256.HashData(inputBytes)).ToLowerInvariant(),
            configuration = new { configuration.Fingerprint, configuration.Source, configuration.Population, configuration.State },
            notes = new[] { "Render and collision are measured separately and never mixed.",
                            "Crossing length is the curve along which the surfaces cross: an interval whose upper bound adds edge lengths the side test could not decide. Coplanar areas are split same-facing (z-fighting) and opposite-facing (resting contact).",
                            "Every render triangle carries its shape's material class; only opaque x opaque contributions may decide an attribution.",
                            "Buried area is given only against a validated closed solid, as an interval and a fraction; never a midpoint.",
                            "A member whose live pose is not established or whose geometry cannot be read refuses with its reason; nothing is estimated." },
            pairs = pairs.Count, measured, incomplete, refused, unavailable, records,
        };
        var full = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, arms.StampForced(JsonSerializer.Serialize(doc, Json)));
        Console.WriteLine($"--surface: {pairs.Count} pair(s): {measured} geometry measurement(s), {incomplete} incomplete, {refused} refused, {unavailable} unavailable -> {full}");
        return 0;
    }
}
