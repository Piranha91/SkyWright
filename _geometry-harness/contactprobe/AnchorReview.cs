using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

/// <summary>contactprobe --anchor OUTPUT (with --overlap-json INPUT; G1 build list, Stage A, row 2): for each supplied pair,
/// member A is an ANCHOR — an editor marker, idle marker, placed actor or furniture whose model is not the obstacle — and
/// member B is a partner whose render surface the actor's body could meet. The anchor is A's placement position, or, for
/// furniture, each entry position the NIF's BSFurnitureMarker declares, placed by A's transform. Each anchor is tested with
/// <see cref="MeshAnchor"/>: point, line and body readings, the body deciding; only opaque surfaces decide. For Lean
/// furniture the half-space behind the marker mesh's own front face (its model-space max Y) is excluded from the body
/// test (red-gate review ruling) and the detail names it. The body is the humanoid one from the route constants
/// (ActorBand.PhysicalStep, ActorBand.PhysicalControllerTop(ScaleOneHeight), RouteConnectivity.DefaultCapsuleRadius); a
/// race's own height is not applied (stated in the file). B's render surface follows the live-pose default. A separate
/// file; the pair review's --json is unchanged.</summary>
static class AnchorReview
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    static (byte[]? Bytes, string? Mesh, string? Why) ReadBase(AssetResolver assets, OverlapIndex index, FormKey basis)
    {
        if (!index.BaseMesh.TryGetValue(basis, out var mesh)) return (null, null, "winning base has no model path");
        var rel = mesh.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? mesh : Path.Combine("meshes", mesh);
        var res = assets.ResolveForPlacement(rel);
        if (res.Sources.Count == 0) return (null, rel, "missing_asset: " + rel);
        var (bytes, error) = AssetResolver.ReadPlacementSource(res.Sources[0]);
        return bytes is null ? (null, rel, "asset read failed: " + error) : (bytes, rel, null);
    }

    public static int Run(string inputPath, string outputPath, AssetResolver assets, OverlapIndex index, EnableArms arms,
                          ConfigurationIdentity configuration, Func<FormKey, Snap?> winningPose)
    {
        var inputBytes = File.ReadAllBytes(inputPath);
        using var input = JsonDocument.Parse(inputBytes);
        var pairs = OverlapPairReview.ReadPairs(input.RootElement);
        if (pairs.Any(p => p.ADz is not null || p.BDz is not null)) throw new InvalidDataException("--anchor does not read aPose/bPose (only --surface and --volume do)");
        var body = new BodyExtent(ActorBand.PhysicalStep, ActorBand.PhysicalControllerTop(ActorBand.ScaleOneHeight), RouteConnectivity.DefaultCapsuleRadius);
        var records = new List<object>();
        int decided = 0, undecided = 0, refused = 0;

        (Snap? Pose, FormKey Base, string? Why) Member(string formKey)
        {
            if (!FormKey.TryFactory(formKey, out var key)) return (null, FormKey.Null, "not a FormKey");
            var p = winningPose(key);
            var en = arms.Winner(key);
            if (p is null) return (null, FormKey.Null, "absent: reference has no winning placement in this order");
            if (index.IsDeletedNow(key) || p.Value.Deleted) return (p, FormKey.Null, "absent: winning reference is deleted");
            if (!en.Resolved) return (p, FormKey.Null, "unmeasurable: enable chain is unresolved");
            if (!en.Enabled) return (p, FormKey.Null, "absent: reference is disabled in the declared state");
            if (!index.RefBase.TryGetValue(key, out var basis)) return (p, FormKey.Null, "unmeasurable: winning reference has no base");
            return (p, basis, null);
        }

        foreach (var pr in pairs)
        {
            var (pa, baseA, whyA) = Member(pr.A);
            var (pb, baseB, whyB) = Member(pr.B);
            string? typeA = !baseA.IsNull && index.BaseInfo.TryGetValue(baseA, out var ia) ? ia.Type : null;
            object Rec(object? partner, IEnumerable<object> anchors, string? reason) =>
                new { pairKey = pr.PairKey, a = pr.A, b = pr.B, baseA = baseA.IsNull ? null : baseA.ToString(), typeA, baseB = baseB.IsNull ? null : baseB.ToString(),
                      partner, anchors = anchors.ToList(), reason };
            if (whyA is not null || whyB is not null)
            {
                refused++;
                records.Add(Rec(null, Array.Empty<object>(), whyA is not null ? "A: " + whyA : "B: " + whyB));
                continue;
            }
            var (bBytes, bMesh, bWhy) = ReadBase(assets, index, baseB);
            if (bBytes is null) { refused++; records.Add(Rec(new { mesh = bMesh }, Array.Empty<object>(), "B: " + bWhy)); continue; }
            var surf = MeshSurface.ModelSurface(bBytes, VolumeGeometry.Render, LivePoseDefault.Policy);
            if (surf.Status != "measured") { refused++; records.Add(Rec(new { mesh = bMesh }, Array.Empty<object>(), "B: " + surf.Reason)); continue; }

            // the anchors: furniture entry positions, else the placement position
            var a = pa!.Value; var b = pb!.Value;
            var anchors = new List<(Vector3 At, string Kind, FurnitureEntry? Entry, HalfSpace? Exclude)>();
            string? anchorRefusal = null;
            if (typeA == "Furniture")
            {
                var (aBytes, _, aWhy) = ReadBase(assets, index, baseA);
                if (aBytes is null) anchorRefusal = "A (furniture): " + aWhy + "; its entry positions cannot be read";
                else
                {
                    IReadOnlyList<FurnitureEntry> entries;
                    try { entries = MeshAnchor.FurnitureEntries(aBytes); }
                    catch (Exception ex) { entries = Array.Empty<FurnitureEntry>(); anchorRefusal = "A (furniture): " + ex.Message; }
                    if (anchorRefusal is null && entries.Count == 0) anchorRefusal = "A (furniture): the NIF declares no furniture marker position";
                    HalfSpace? lean = null;
                    if (anchorRefusal is null && entries.Any(e => e.AnimationType == "Lean"))
                    {
                        var g = NifGeometry.MeasureVisibleRender(aBytes);
                        if (g.Model is { Bounds: { IsEmpty: false } mb })
                        {
                            var pts = SpatialOverlap.TransformSoup(new TriangleSoup(new[] { new Vector3(0, mb.Max.Y, 0), new Vector3(0, mb.Max.Y + 1, 0) }, Array.Empty<int>()), a.Pos, a.Rot, a.Scale).Vertices;
                            var n = Vector3.Normalize(pts[1] - pts[0]);
                            lean = new HalfSpace(n, Vector3.Dot(n, pts[0]));
                        }
                        else anchorRefusal = "A (Lean furniture): the marker mesh's front face cannot be read";
                    }
                    if (anchorRefusal is null)
                        foreach (var e in entries)
                            anchors.Add((MeshAnchor.EntryWorld(e, a.Pos, a.Rot, a.Scale), "furniture entry", e, e.AnimationType == "Lean" ? lean : null));
                }
            }
            else anchors.Add((new Vector3(a.Pos.X, a.Pos.Y, a.Pos.Z), "placement", null, null));
            if (anchorRefusal is not null) { refused++; records.Add(Rec(new { mesh = bMesh }, Array.Empty<object>(), anchorRefusal)); continue; }

            var results = new List<object>();
            foreach (var (at, kind, entry, ex) in anchors)
            {
                var r = MeshAnchor.TestPlaced(surf.Soup!, surf.Tags, b.Pos, b.Rot, b.Scale, at, body, ex);
                if (r.Decision == "undecided") undecided++; else decided++;
                results.Add(new
                {
                    kind, at = new[] { (double)at.X, at.Y, at.Z },
                    entry = entry is null ? null : new { entry.AnimationType, entry.EntryPoints, heading = entry.Heading },
                    exclusion = ex is { } h ? new { normal = new[] { (double)h.Normal.X, h.Normal.Y, h.Normal.Z }, offset = (double)h.Offset } : null,
                    result = r,
                });
            }
            records.Add(Rec(new { mesh = bMesh }, results, null));
        }
        var doc = new
        {
            schema = "housecarl.anchor/1", policy = MeshAnchor.PolicyVersion, livePose = LivePoseDefault.Policy.ToString().ToLowerInvariant(),
            body = new { bottom = body.Bottom, top = body.Top, radius = body.Radius,
                         note = "humanoid body from the route constants (PhysicalStep, PhysicalControllerTop(ScaleOneHeight), DefaultCapsuleRadius); a race's own height is not applied" },
            inputPath = Path.GetFullPath(inputPath), inputSha256 = Convert.ToHexString(SHA256.HashData(inputBytes)).ToLowerInvariant(),
            configuration = new { configuration.Fingerprint, configuration.Source, configuration.Population, configuration.State },
            pairs = pairs.Count, decided, undecided, refused, records,
        };
        File.WriteAllText(outputPath, arms.StampForced(JsonSerializer.Serialize(doc, Json)));
        Console.WriteLine($"wrote anchor review: {pairs.Count} pair(s), {decided} decided, {undecided} undecided, {refused} refused -> {outputPath}");
        return 0;
    }
}
