using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

/// <summary>contactprobe --box-contact OUTPUT (with --overlap-json INPUT; lane 3b, gap e): for each input <c>boxContact</c> row
/// {pairKey, a, b, boxOwner "A"|"B", boxes [{label, min [x,y,z], max [x,y,z]}]}, whether each world box meets the OTHER member's
/// placed render surface (exact triangle-box test, <see cref="MeshSurfaceBox.BoxContact"/>): opaque triangles, and any material.
/// The boxes come from the caller (for a particle region: the overlapprobe manifest's reach parts), so there is one particle model,
/// not a second one here. A member that is absent, disabled or whose surface refuses is refused with its reason. A separate file; no
/// other output changes.</summary>
static class BoxContactReview
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    // a box read into single precision is rounded outward, never shrunk
    static float Down(double v) { float f = (float)v; return f > v ? MathF.BitDecrement(f) : f; }
    static float Up(double v) { float f = (float)v; return f < v ? MathF.BitIncrement(f) : f; }

    public static int Run(string inputPath, string outputPath, AssetResolver assets, OverlapIndex index, EnableArms arms,
                          ConfigurationIdentity configuration, Func<FormKey, Snap?> winningPose, MotionReview.Cache? motion = null)
    {
        var inputBytes = File.ReadAllBytes(inputPath);
        using var input = JsonDocument.Parse(inputBytes);
        if (!input.RootElement.TryGetProperty("boxContact", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("--box-contact needs a boxContact array in the --overlap-json input");
        var surfaces = new Dictionary<string, (TaggedSurface? S, Snap? Pose, string? Mesh, string? Why)>();
        (TaggedSurface? S, Snap? Pose, string? Mesh, string? Why) Surface(string formKey)
        {
            if (surfaces.TryGetValue(formKey, out var hit)) return hit;
            (TaggedSurface? S, Snap? Pose, string? Mesh, string? Why) r;
            if (!FormKey.TryFactory(formKey, out var key)) r = (null, null, null, "not a FormKey");
            else
            {
                var p = winningPose(key); var en = arms.Winner(key);
                if (p is null) r = (null, null, null, "absent: reference has no winning placement in this order");
                else if (index.IsDeletedNow(key) || p.Value.Deleted) r = (null, p, null, "absent: winning reference is deleted");
                else if (!en.Resolved) r = (null, p, null, "unmeasurable: enable chain is unresolved");
                else if (!en.Enabled) r = (null, p, null, "absent: reference is disabled in the declared state");
                else if (!index.RefBase.TryGetValue(key, out var basis)) r = (null, p, null, "unmeasurable: winning reference has no base");
                else if (!index.BaseMesh.TryGetValue(basis, out var mesh)) r = (null, p, null, "unmeasurable: winning base has no model path");
                else
                {
                    var rel = mesh.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? mesh : Path.Combine("meshes", mesh);
                    var res = assets.ResolveForPlacement(rel);
                    if (res.Sources.Count == 0) r = (null, p, rel, "missing_asset: " + rel);
                    else
                    {
                        var (bytes, error) = AssetResolver.ReadPlacementSource(res.Sources[0]);
                        if (bytes is null) r = (null, p, rel, "asset read failed: " + error);
                        else
                        {
                            var s = MeshSurface.ModelSurface(bytes, VolumeGeometry.Render, LivePoseDefault.Policy);
                            r = s.Status == "measured" ? (s, p, rel, null) : (null, p, rel, s.Reason);
                        }
                    }
                }
            }
            surfaces[formKey] = r;
            return r;
        }

        var records = new List<object>();
        int measured = 0, refused = 0;
        foreach (var row in rows.EnumerateArray())
        {
            string pairKey = row.GetProperty("pairKey").GetString() ?? "";
            string a = row.GetProperty("a").GetString() ?? "", b = row.GetProperty("b").GetString() ?? "";
            string owner = row.GetProperty("boxOwner").GetString() ?? "";
            if (owner is not ("A" or "B")) throw new InvalidDataException($"boxContact row '{pairKey}': boxOwner must be A or B");
            string other = owner == "A" ? b : a, otherName = owner == "A" ? "B" : "A";
            var boxes = row.GetProperty("boxes").EnumerateArray().Select(x =>
            {
                double[] mn = x.GetProperty("min").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                double[] mx = x.GetProperty("max").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                if (mn.Length != 3 || mx.Length != 3) throw new InvalidDataException($"boxContact row '{pairKey}': a box needs min and max of 3");
                return (Label: x.TryGetProperty("label", out var l) ? l.GetString() : null,
                        Box: new Aabb(new Vec3(Down(mn[0]), Down(mn[1]), Down(mn[2])), new Vec3(Up(mx[0]), Up(mx[1]), Up(mx[2])), false), Min: mn, Max: mx);
            }).ToList();
            var (surf, pose, mesh, why) = Surface(other);
            // --motion swept: a member refused only because its live pose is not its stored pose is tested through its swept region
            if (surf is null && motion is { Options.Mode: "swept" } && pose is { } mp && mesh is not null && (why ?? "").StartsWith("live pose: ", StringComparison.Ordinal))
            {
                var bytes = motion.Read(mesh);
                // lane M: a member with a mechanism beyond clips is decided per box by the shared loop
                if (bytes is not null && motion.Model(other, bytes) is { Status: "moving" } member && !MemberMotion.IsClipOnly(bytes, member, motion.Read))
                {
                    var decided = boxes.Select(x =>
                    {
                        var cap = new MemberCapture();
                        var md = MemberMotion.Decide(bytes, member, VolumeGeometry.Render, mp.Pos, mp.Rot, mp.Scale, motion.Sweep, MemberPredicates.Box(x.Box, cap), motion.Read);
                        string dec = cap.Refusal is not null ? "refused" : md.Decision;
                        bool? op = dec switch { "witness" => true, "never" => false, _ => null };
                        return (object)new { x.Label, min = x.Min, max = x.Max, opaqueMeets = op, anyMeets = (bool?)null, reason = cap.Refusal ?? md.Reason,
                                             motion = new { decision = dec, origin = md.Origin, witnessLabel = md.Label, note = md.Note, animationId = md.AnimationId, md.Time,
                                                            animationB = md.AnimationB, timeB = md.TimeB, weights = md.Weights, angles = md.Angles, width = md.Width,
                                                            converged = md.Converged, evaluations = md.Evaluations, budget = motion.Sweep.Budget } };
                    }).ToList();
                    measured++;
                    records.Add(new { pairKey, a, b, boxOwner = owner, mesh, status = "measured", reason = (string?)null, particles = (IReadOnlyList<string>?)null,
                                      motion = new { mode = "swept", policy = MeshSurfaceBox.MotionPolicyVersion + "|" + MotionReview.Policy + "|" + MemberMotion.PolicyVersion, solidPolicy = MotionReview.SolidPolicy(true), member = otherName,
                                                     label = string.Join("; ", member.Labels), mechanisms = member.Mechanisms.Select(m => m.Kind).ToArray() },
                                      boxes = decided });
                    continue;
                }
                var region = bytes is null ? null : motion.Region(other, bytes, VolumeGeometry.Render);
                if (region is not { Status: "built" })
                {
                    refused++;
                    // lane M: a member reading that refuses for its own reason (not the stored-pose reading's) is named alone
                    string reason = region is { Status: "refused" } && bytes is not null && motion.OwnRefusal(other, bytes)
                        ? $"{otherName}: live pose: {region.Reason}"
                        : $"{otherName}: {why}; motion: {(region is null ? "the mesh could not be re-read" : region.Status + " " + region.Reason)}";
                    records.Add(new { pairKey, a, b, boxOwner = owner, mesh, status = "refused", reason, boxes = Array.Empty<object>() });
                    continue;
                }
                var placed = SweptGeometry.Place(region, mp.Pos, mp.Rot, mp.Scale);
                var swept = boxes.Select(x =>
                {
                    var r = MeshSurfaceBox.BoxContactSwept(placed, x.Box);
                    bool? op = r.Decision switch { "witness" => true, "never" => false, _ => null };
                    return (object)new { x.Label, min = x.Min, max = x.Max, opaqueMeets = op, anyMeets = (bool?)null, reason = r.Reason,
                                         motion = new { r.Decision, r.AnimationId, r.Time, r.Converged, r.Samples } };
                }).ToList();
                measured++;
                records.Add(new { pairKey, a, b, boxOwner = owner, mesh, status = "measured", reason = (string?)null, particles = (IReadOnlyList<string>?)null,
                                  motion = new { mode = "swept", policy = MeshSurfaceBox.MotionPolicyVersion + "|" + MotionReview.Policy, solidPolicy = MotionReview.SolidPolicy(true), member = otherName, livePose = why },
                                  boxes = swept });
                continue;
            }
            if (surf is null && why == "unmeasurable: winning base has no model path" && BoxContactActor.Boxes(other, pose, index, boxes.Select(x => (x.Label, x.Box, x.Min, x.Max))) is { } bodyRows)
            {
                measured++;
                records.Add(new { pairKey, a, b, boxOwner = owner, mesh, status = "measured", reason = (string?)null, particles = (IReadOnlyList<string>?)null,
                                  actorBody = BoxContactActor.Note, boxes = bodyRows });
                continue;
            }
            if (surf is null)
            {
                refused++;
                records.Add(new { pairKey, a, b, boxOwner = owner, mesh, status = "refused", reason = $"{otherName}: {why}", boxes = Array.Empty<object>() });
                continue;
            }
            var p = pose!.Value;
            var world = SpatialOverlap.TransformSoup(surf.Soup!, p.Pos, p.Rot, p.Scale);
            var results = boxes.Select(x =>
            {
                var r = MeshSurfaceBox.BoxContact(world, surf.Tags, x.Box);
                return (object)new { x.Label, min = x.Min, max = x.Max, opaqueMeets = r.OpaqueMeets, anyMeets = r.AnyMeets, reason = r.Reason };
            }).ToList();
            measured++;
            records.Add(new { pairKey, a, b, boxOwner = owner, mesh, status = "measured", reason = (string?)null, particles = surf.Labels, boxes = results });
        }
        var doc = new
        {
            schema = "housecarl.box-contact/1", livePose = LivePoseDefault.Policy.ToString().ToLowerInvariant(),
            inputPath = Path.GetFullPath(inputPath), inputSha256 = Convert.ToHexString(SHA256.HashData(inputBytes)).ToLowerInvariant(),
            configuration = new { configuration.Fingerprint, configuration.Source, configuration.Population, configuration.State },
            notes = new[] { "Each box is a world box supplied by the caller; it is tested against the other member's placed render surface (exact triangle-box test).",
                            "opaqueMeets counts opaque triangles only; anyMeets every material. Particle systems are not part of a render surface (labelled)." },
            rows = records.Count, measured, refused, records,
        };
        File.WriteAllText(outputPath, arms.StampForced(JsonSerializer.Serialize(doc, Json)));
        Console.WriteLine($"wrote box contact: {records.Count} row(s), {measured} measured, {refused} refused -> {outputPath}");
        return 0;
    }
}
