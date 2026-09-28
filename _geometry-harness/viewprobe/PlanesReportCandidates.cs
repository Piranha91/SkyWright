using System.Text.Json;
using System.Text.Json.Nodes;

namespace ViewProbe;

/// <summary>
/// The candidate rows of the planes report, schema <c>occlusion-view-research-v2</c>.
///
/// v1 wrote a complete description of every scene shape inside every plane: a city view has 84
/// planes and about 4,000 shapes, and nearly every field of a shape's row is the same in all 84
/// copies. v2 writes each shape's plane-independent BODY once, in <c>candidateBodies</c>, and each
/// plane carries an OVERLAY per shape: the body's id plus the only five values that can differ by
/// plane (<see cref="PlaneKeys"/>). Order is unchanged: bodies follow <c>os.Refs</c> then each ref's
/// <c>Parts</c>, exactly as v1's rows did, and overlay N of every plane is body N.
///
/// Materialising an overlay (body minus <c>id</c>, plus overlay minus <c>candidate</c>) gives back the
/// v1 row key for key, null for null. A ref with no source shapes has a sparse body and an overlay
/// that is only <c>{ "candidate": N }</c>: it gains no relation, shape or <c>predictedCulled</c>.
/// A shape whose relation begins <c>unassessed: </c> is still an assessed-shape body with a plane
/// result - the Box refusal tally counts those, and they must not collapse into the sparse case.
/// </summary>
static class PlanesReportCandidates
{
    public const string SchemaV1 = "occlusion-view-research-v1";
    public const string Schema = "occlusion-view-research-v2";

    /// <summary>The values that can differ by plane. Nothing else may sit in an overlay.</summary>
    public static readonly string[] PlaneKeys = { "relation", "predictedCulled", "visibility", "visibilityMask", "isolatedDepthSha256" };

    /// <summary>Each candidate body once, with its report-local id, in v1 row order; none when the view has no planes.</summary>
    public static List<object> Bodies(IReadOnlyList<RefOcclusion> refs, int planeCount)
    {
        var bodies = new List<object>();
        // No plane, no overlay to reference a body - and v1 wrote no rows. A plane-less view's
        // report stays as small as it was rather than gaining every scene shape.
        if (planeCount == 0) return bodies;
        foreach (var r in refs)
        {
            if (r.Parts.Count == 0)
            {
                bodies.Add(new { id = bodies.Count, subject = r.Ref.RefKey.ToString(), mesh = r.Ref.MeshRelPath,
                    assetSha256 = r.AssetHash,
                    status = "unassessed", reason = r.Issue ?? "no source shapes" });
                continue;
            }
            foreach (var s in r.Parts)
                bodies.Add(new {
                    id = bodies.Count,
                    subject = r.Ref.RefKey.ToString(), baseForm = r.Ref.BaseKey.ToString(), mesh = r.Ref.MeshRelPath,
                    constructed = r.Ref.Constructed,
                    shape = s.Name, block = s.Block, assetSha256 = s.AssetHash,
                    storedBound = s.Stored,
                    worldSphere = s.World is { } w ? new { center = new[] { w.Center.X, w.Center.Y, w.Center.Z }, radius = w.Radius } : null,
                    allCullingPlanes = s.CulledBy.Select(cp => cp.Key.ToString()).ToArray(),
                    status = "research prediction; inspect relation, coverage and supplied rule evidence",
                    reason = r.Issue ?? s.Issue,
                    sourceIssue = s.Issue,
                    refPosition = new[] { r.Ref.Position.X, r.Ref.Position.Y, r.Ref.Position.Z },
                    refRotation = new[] { r.Ref.Rotation.X, r.Ref.Rotation.Y, r.Ref.Rotation.Z },
                    refScale = r.Ref.Scale,
                });
        }
        return bodies;
    }

    /// <summary>A no-source ref's overlay: the reference and nothing else.</summary>
    sealed record SparseOverlay(int candidate);

    /// <summary>An assessed shape's overlay: the reference plus the five plane-dependent values.
    /// A record rather than an anonymous type so the five keys have one declaration to drift from.</summary>
    sealed record ShapeOverlay(int candidate, string relation, bool predictedCulled,
        HousecarlCore.ShapeVisibility? visibility, string? visibilityMask, string? isolatedDepthSha256);

    /// <summary>One plane's overlays, in body order; overlay N references body N.</summary>
    public static object[] Overlays(IReadOnlyList<RefOcclusion> refs, PlaneRender p, int bodyCount)
    {
        var rows = new object[bodyCount];
        int n = 0;
        foreach (var r in refs)
        {
            if (r.Parts.Count == 0) { Put(new SparseOverlay(n)); continue; }
            foreach (var s in r.Parts)
            {
                bool culled = s.CulledBy.Contains(p);
                Put(new ShapeOverlay(n, s.Relations.GetValueOrDefault(p, "unassessed: not evaluated"), culled,
                    culled ? s.Visibility : null, culled ? s.VisibilityMask : null, culled ? s.IsolatedDepthHash : null));
            }
        }
        if (n != bodyCount) throw new InvalidOperationException($"plane {p.Key} has {n} candidate(s) but the report has {bodyCount} bodies; the scene changed between them");
        return rows;

        void Put(object o)
        {
            if (n >= bodyCount) throw new InvalidOperationException($"plane {p.Key} has more candidates than the report's {bodyCount} bodies; the scene changed between them");
            rows[n++] = o;
        }
    }

    /// <summary>TEST ORACLE ONLY: the v1 rows for one plane, exactly as the v1 writer built them.</summary>
    internal static object[] V1Rows(IReadOnlyList<RefOcclusion> refs, PlaneRender p)
        => refs.SelectMany(r => r.Parts.Count == 0
            ? new object[] { new { subject = r.Ref.RefKey.ToString(), mesh = r.Ref.MeshRelPath,
                assetSha256 = r.AssetHash,
                status = "unassessed", reason = r.Issue ?? "no source shapes" } }
            : r.Parts.Select(s => (object)new {
                subject = r.Ref.RefKey.ToString(), baseForm = r.Ref.BaseKey.ToString(), mesh = r.Ref.MeshRelPath,
                constructed = r.Ref.Constructed,
                shape = s.Name, block = s.Block, assetSha256 = s.AssetHash,
                storedBound = s.Stored,
                worldSphere = s.World is { } w ? new { center = new[] { w.Center.X, w.Center.Y, w.Center.Z }, radius = w.Radius } : null,
                relation = s.Relations.GetValueOrDefault(p, "unassessed: not evaluated"),
                predictedCulled = s.CulledBy.Contains(p),
                allCullingPlanes = s.CulledBy.Select(cp => cp.Key.ToString()).ToArray(),
                status = "research prediction; inspect relation, coverage and supplied rule evidence",
                reason = r.Issue ?? s.Issue,
                sourceIssue = s.Issue,
                visibility = s.CulledBy.Contains(p) ? s.Visibility : null,
                visibilityMask = s.CulledBy.Contains(p) ? s.VisibilityMask : null,
                isolatedDepthSha256 = s.CulledBy.Contains(p) ? s.IsolatedDepthHash : null,
                refPosition = new[] { r.Ref.Position.X, r.Ref.Position.Y, r.Ref.Position.Z },
                refRotation = new[] { r.Ref.Rotation.X, r.Ref.Rotation.Y, r.Ref.Rotation.Z },
                refScale = r.Ref.Scale,
            })).ToArray();

    /// <summary>
    /// A parsed report's candidate rows per plane, as v1 rows, whichever schema wrote it. Refuses an
    /// unknown schema, a non-contiguous or duplicate id, a dangling reference, and a key present in
    /// both a body and its overlay. The reference materialiser the Python readers mirror.
    /// </summary>
    public static List<List<JsonObject>> Materialise(JsonObject doc)
    {
        string? schema = doc["schema"]?.GetValue<string>();
        var planes = doc["planes"] as JsonArray ?? throw new InvalidDataException("report has no planes array");
        var result = new List<List<JsonObject>>();
        if (schema == SchemaV1)
        {
            foreach (var p in planes)
                result.Add(((JsonArray)p!["candidates"]!).Select(c => (JsonObject)c!.DeepClone()).ToList());
            return result;
        }
        if (schema != Schema) throw new InvalidDataException($"unknown planes-report schema '{schema}'; this reader knows {SchemaV1} and {Schema}");
        var bodies = doc["candidateBodies"] as JsonArray ?? throw new InvalidDataException("v2 report has no candidateBodies array");
        for (int i = 0; i < bodies.Count; i++)
        {
            int id = bodies[i]?["id"]?.GetValue<int>() ?? throw new InvalidDataException($"candidate body {i} has no id");
            if (id != i) throw new InvalidDataException($"candidate body at index {i} has id {id}; ids must be unique, contiguous and equal to their index");
        }
        foreach (var p in planes)
        {
            var rows = new List<JsonObject>();
            foreach (var o in (JsonArray)p!["candidates"]!)
            {
                var overlay = (JsonObject)o!;
                int id = overlay["candidate"]?.GetValue<int>() ?? throw new InvalidDataException("overlay has no candidate id");
                if (id < 0 || id >= bodies.Count) throw new InvalidDataException($"overlay references candidate {id}; there are {bodies.Count} bodies");
                var row = (JsonObject)bodies[id]!.DeepClone();
                row.Remove("id");
                foreach (var kv in overlay)
                {
                    if (kv.Key == "candidate") continue;
                    if (row.ContainsKey(kv.Key)) throw new InvalidDataException($"key '{kv.Key}' is in both candidate body {id} and its overlay");
                    row[kv.Key] = kv.Value?.DeepClone();
                }
                rows.Add(row);
            }
            result.Add(rows);
        }
        return result;
    }

    /// <summary>Object keys sorted at every depth, so two rows compare as JSON values, not as key order.</summary>
    public static string Canonical(JsonNode? n) => n switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(",", o.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                               .Select(kv => JsonSerializer.Serialize(kv.Key) + ":" + Canonical(kv.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]",
        _ => n.ToJsonString(),
    };
}

/// <summary>
/// <c>--planes-report-selftest</c>: the v2 candidate factoring against the v1 oracle on a small
/// fabricated scene, and the materialiser's refusals. Needs no MO2 instance, game or load order.
/// </summary>
static class PlanesReportCandidatesSelfTest
{
    static PlaneRender Plane(string key) => new(Mutagen.Bethesda.Plugins.FormKey.Factory(key), PlaneKind.PortalBox,
        new System.Numerics.Vector3(512, 0, 256), new System.Numerics.Vector3(1, 2, 3), new HousecarlCore.Vec3(0, 0, 0.5f),
        1f, PlaneProvenance.Vanilla, "Skyrim.esm", null, new[] { "Skyrim.esm" }, "FF000001");

    static RefOcclusion Ref(string refKey, string mesh, bool constructed = false, string? issue = null) => new()
    {
        Ref = new SceneRef(mesh, new HousecarlCore.Vec3(10, -20, 30.5f), new HousecarlCore.Vec3(0, 0.25f, 1), 1.5f, false,
            RefKey: Mutagen.Bethesda.Plugins.FormKey.Factory(refKey), BaseKey: Mutagen.Bethesda.Plugins.FormKey.Factory("000100:Skyrim.esm"),
            Constructed: constructed),
        AssetHash = "AA" + refKey[..6], Issue = issue,
    };

    static ShapeOcclusion Shape(string name, int block, string? issue = null) => new()
    {
        Name = name, Block = block, AssetHash = "SH" + block, Issue = issue,
        Stored = new HousecarlCore.NifShapeBound(new HousecarlCore.Vec3(1, 2, 3), 40, new HousecarlCore.Vec3(1, 2, 3), 40, new[] { 0, 3 }, -1),
        World = new HousecarlCore.SphereBound(new System.Numerics.Vector3(5, 6, 7), 44),
    };

    /// <summary>Two planes; a shape culled by both; one culled by one with null visibility; a real
    /// "unassessed: " relation; a ref with no source shapes; a ref-level issue.</summary>
    static (List<RefOcclusion> Refs, List<PlaneRender> Planes) Scene()
    {
        var pa = Plane("000A01:Skyrim.esm"); var pb = Plane("000A02:Skyrim.esm");
        var both = Shape("Wall", 7);
        both.CulledBy.Add(pa); both.CulledBy.Add(pb);
        both.Relations[pa] = "Inside"; both.Relations[pb] = "Inside";
        both.Visibility = new HousecarlCore.ShapeVisibility(100, 12, 88, null);
        both.VisibilityMask = @"C:\evidence\shape-0001-block-7.png"; both.IsolatedDepthHash = "D1";
        var oneNullVis = Shape("Roof", 2);
        oneNullVis.CulledBy.Add(pa); oneNullVis.Relations[pa] = "Inside"; oneNullVis.Relations[pb] = "Outside";
        var refusal = Shape("Door", 4, issue: "skinned");
        refusal.Relations[pa] = "unassessed: skinned shape"; // pb left unevaluated -> the default relation
        var r1 = Ref("000B01:Mod.esp", @"architecture\wall.nif", constructed: true); r1.Parts.Add(both); r1.Parts.Add(oneNullVis);
        var r2 = Ref("000B02:Mod.esp", @"clutter\door.nif", issue: "ref-level issue"); r2.Parts.Add(refusal);
        var r3 = Ref("000B03:Mod.esp", @"markers\empty.nif"); // no source shapes
        var r4 = Ref("000B04:Mod.esp", @"markers\bad.nif", issue: "unreadable mesh"); // no source shapes, own reason
        return (new List<RefOcclusion> { r1, r3, r2, r4 }, new List<PlaneRender> { pa, pb });
    }

    static JsonObject Doc(string schema, List<object>? bodies, IEnumerable<object> planeRows)
    {
        var doc = bodies is null ? (object)new { schema, planes = planeRows } : new { schema, candidateBodies = bodies, planes = planeRows };
        return (JsonObject)JsonNode.Parse(JsonSerializer.Serialize(doc, ReportSink.Options()))!;
    }

    static int Tally(IEnumerable<JsonObject> rows) => rows.Count(r => r["relation"]?.GetValue<string>() is { } s && s.StartsWith("unassessed: ", StringComparison.Ordinal));

    public static int Run()
    {
        int fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) fails++; }
        void Refuses(string what, Action a)
        {
            try { a(); Check(false, what + " (accepted)"); }
            catch (InvalidDataException ex) { Check(true, what + " -> " + ex.Message); }
        }

        var (refs, planes) = Scene();
        var v1 = Doc(PlanesReportCandidates.SchemaV1, null,
            planes.Select(p => (object)new { formKey = p.Key.ToString(), candidates = PlanesReportCandidates.V1Rows(refs, p) }));
        JsonObject v2;
        try
        {
            var bodies = PlanesReportCandidates.Bodies(refs, planes.Count);
            v2 = Doc(PlanesReportCandidates.Schema, bodies,
                planes.Select(p => (object)new { formKey = p.Key.ToString(), candidates = PlanesReportCandidates.Overlays(refs, p, bodies.Count) }));
        }
        catch (Exception ex) { Console.WriteLine("  FAIL  the v2 writer threw: " + ex.Message); return 1; }

        // Structure: one body per source shape plus one per no-source ref, overlays slim.
        var jb = (JsonArray)v2["candidateBodies"]!;
        int expectBodies = refs.Sum(r => Math.Max(1, r.Parts.Count));
        Check(jb.Count == expectBodies, $"one body per source shape or no-source ref ({jb.Count} of {expectBodies})");
        Check(jb.All(b => PlanesReportCandidates.PlaneKeys.All(k => !((JsonObject)b!).ContainsKey(k))), "no body carries a plane-dependent key");
        var allowed = new HashSet<string>(PlanesReportCandidates.PlaneKeys.Append("candidate"));
        var overlays = ((JsonArray)v2["planes"]!).SelectMany(p => ((JsonArray)p!["candidates"]!).Select(o => (JsonObject)o!)).ToList();
        bool OverlayOk(JsonObject o)
        {
            var body = (JsonObject)jb[o["candidate"]!.GetValue<int>()]!;
            bool sparse = !body.ContainsKey("shape");
            return sparse ? o.Count == 1 : o.Select(kv => kv.Key).ToHashSet().SetEquals(allowed);
        }
        Check(overlays.All(OverlayOk), "assessed overlays hold exactly candidate + the five plane keys; no-source overlays hold only candidate");
        Check(((JsonArray)v2["planes"]!).All(p => ((JsonArray)p!["candidates"]!).Select((o, i) => o!["candidate"]!.GetValue<int>() == i).All(x => x)),
              "overlay N of every plane references body N");

        // Equivalence: materialised v2 equals v1 row for row, as JSON values.
        var m1 = PlanesReportCandidates.Materialise(v1);
        var m2 = PlanesReportCandidates.Materialise(v2);
        bool same = m1.Count == m2.Count && m1.Zip(m2).All(z => z.First.Count == z.Second.Count
                    && z.First.Zip(z.Second).All(r => PlanesReportCandidates.Canonical(r.First) == PlanesReportCandidates.Canonical(r.Second)));
        Check(same, "materialised v2 rows equal v1 rows key for key, null for null, in order");
        var sparse1 = m1[0].Where(r => !r.ContainsKey("shape")).Select(PlanesReportCandidates.Canonical).ToList();
        var sparse2 = m2[0].Where(r => !r.ContainsKey("shape")).Select(PlanesReportCandidates.Canonical).ToList();
        Check(sparse1.Count == 2 && sparse1.SequenceEqual(sparse2) && m2[0].Where(r => !r.ContainsKey("shape")).All(r => !r.ContainsKey("predictedCulled") && !r.ContainsKey("relation")),
              "no-source rows stay sparse: no shape, relation or predictedCulled invented");
        int t1 = m1.Sum(Tally), t2 = m2.Sum(Tally);
        Check(t1 == 2 && t2 == t1, $"Box refusal tally ('unassessed: ' relations) equal: v1 {t1}, v2 {t2}");

        // A view with no planes: v1 wrote no candidate rows, so v2 writes no bodies either.
        var none = new List<PlaneRender>();
        var v2None = Doc(PlanesReportCandidates.Schema, PlanesReportCandidates.Bodies(refs, none.Count), Array.Empty<object>());
        Check(((JsonArray)v2None["candidateBodies"]!).Count == 0 && PlanesReportCandidates.Materialise(v2None).Count == 0,
              "a view with no planes writes no candidate bodies (v1 wrote no rows)");

        // Refusals.
        Refuses("unknown schema", () => { var d = (JsonObject)v2.DeepClone(); d["schema"] = "occlusion-view-research-v3"; PlanesReportCandidates.Materialise(d); });
        Refuses("missing schema", () => { var d = (JsonObject)v2.DeepClone(); d.Remove("schema"); PlanesReportCandidates.Materialise(d); });
        Refuses("duplicate id", () => { var d = (JsonObject)v2.DeepClone(); d["candidateBodies"]![1]!["id"] = 0; PlanesReportCandidates.Materialise(d); });
        Refuses("non-contiguous ids", () => { var d = (JsonObject)v2.DeepClone(); ((JsonArray)d["candidateBodies"]!).RemoveAt(1); PlanesReportCandidates.Materialise(d); });
        Refuses("dangling reference", () => { var d = (JsonObject)v2.DeepClone(); d["planes"]![0]!["candidates"]![0]!["candidate"] = 99; PlanesReportCandidates.Materialise(d); });
        Refuses("body/overlay duplicate key", () => { var d = (JsonObject)v2.DeepClone(); d["candidateBodies"]![0]!["relation"] = "Inside"; PlanesReportCandidates.Materialise(d); });

        Console.WriteLine(fails == 0 ? "planes-report selftest: all checks passed" : $"planes-report selftest: {fails} check(s) FAILED");
        return fails == 0 ? 0 : 1;
    }
}
