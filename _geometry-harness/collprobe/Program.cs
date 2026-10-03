using System.Diagnostics;
using System.Numerics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore.Progress;

// collprobe — roadmap 4.7 Part A: PRICE the Havok collision decoder over the vanilla mesh set, and MEASURE the
// Havok→Skyrim unit scale against the render geometry of the same meshes instead of trusting the reputed 69.99.
//
// Three questions, answered on every mesh a Skyrim.esm base record names, read through the profile's VFS:
//   1. coverage — how many meshes now carry a decoded solid collision soup, by shape type, with the unreadable ones
//      counted and NAMED (a decoder is only as good as the files it meets);
//   2. self-consistency — every compressed shape states its own AABB; the decoded vertices must lie inside it, and
//      must fill it. A decode that puts a chunk in the wrong place fails this before it fails anything else;
//   3. the scale — for meshes whose solid collision decoded completely with no node transform above it, the ratio of
//      render extent to collision extent (Havok units) per axis. The median is the factor, the spread its uncertainty.
//
// usage: collprobe [--limit N] [--dump file.tsv] [--mesh <relative path> ...]
//   --mesh   print one mesh's decode in detail (bodies, layers, shapes, counters, extents); repeatable, no sweep
//   --dump   per-mesh TSV of the sweep (path, record type, render extents, collision extents, family, layers, unread)

using var __run = RunLog.BeginProcess("collprobe", args);

string Instance = HarnessTarget.Require(HarnessTarget.Instance, "MO2 instance directory", "instance");
string GamePath = HarnessTarget.Require(HarnessTarget.Game, "game directory", "game");
string profileDir = Path.Combine(Instance, "profiles", HarnessTarget.Require(HarnessTarget.Profile, "MO2 profile name", "profile"));
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");
string vanilla = Path.Combine(dataDir, "Skyrim.esm");

int limit = int.MaxValue;
string? dump = null;
var detail = new List<string>();
var convexDump = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--limit" && i + 1 < args.Length) limit = int.Parse(args[++i]);
    else if (args[i] == "--dump" && i + 1 < args.Length) dump = args[++i];
    else if (args[i] == "--mesh" && i + 1 < args.Length) detail.Add(args[++i]);
    else if (args[i] == "--convex-dump" && i + 1 < args.Length) convexDump.Add(args[++i]);
    else if (args[i] == "--chunk-mode" && i + 1 < args.Length) HavokCollision.ChunkTransformMode = int.Parse(args[++i]);
    else if (args[i] == "--transform-transposed") HavokCollision.TransformRotationTransposed = true;
}
if (HavokCollision.ChunkTransformMode != 0) Console.WriteLine($"EXPERIMENT: chunk transform mode {HavokCollision.ChunkTransformMode} (1 = NifSkope order, 2 = conjugate quaternion, 3 = both)");
if (HavokCollision.TransformRotationTransposed) Console.WriteLine("EXPERIMENT: bhkTransformShape rotations read TRANSPOSED");

var sw = Stopwatch.StartNew();
var comp = Mo2LoadOrder.ReadComposition(profileDir);
var arch = ArchiveDiscovery.Discover(profileDir, modsDir, dataDir, overwriteDir, GamePath);
using var assets = AssetResolver.Build(overwriteDir, modsDir, dataDir, comp.EnabledMods, arch.Archives);
Console.WriteLine($"assets: {arch.Archives.Count} BSAs indexed in {sw.ElapsedMilliseconds} ms");

byte[]? lastBytes = null;   // the bytes Load last read (the convex census re-reads their blocks)
NifGeometryOutcome? Load(string rel, out string physical)
{
    physical = "";
    var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
    var res = assets.ResolveForPlacement(full);
    if (res.Sources.Count == 0) return null;
    var src = res.Sources[0];
    physical = src.LooseFilePath ?? src.ArchivePath ?? src.ProviderName;
    var (bytes, _) = AssetResolver.ReadPlacementSource(src);
    if (bytes is null) return null;
    lastBytes = bytes;
    // Unit scale 1: everything collision comes back in HAVOK units, which is what the measurement needs.
    return NifGeometry.Measure(bytes, includeTriangles: true, havokUnitScale: 1f);
}

static string Ext(Aabb b) => b.IsEmpty ? "<empty>" : $"{b.Size.X:0.###} x {b.Size.Y:0.###} x {b.Size.Z:0.###}";
static string Ctr(Aabb b) => b.IsEmpty ? "-" : $"({b.Center.X:0.###}, {b.Center.Y:0.###}, {b.Center.Z:0.###})";

// ---- convex dump: the raw vertices / planes / radius of every convex-vertices shape in a mesh ----
if (convexDump.Count > 0)
{
    foreach (var rel in convexDump)
    {
        Console.WriteLine($"\n==== convex shapes in {rel} ====");
        var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
        var res = assets.ResolveForPlacement(full);
        if (res.Sources.Count == 0) { Console.WriteLine("   not resolvable"); continue; }
        var (bytes, _) = AssetResolver.ReadPlacementSource(res.Sources[0]);
        if (bytes is null) { Console.WriteLine("   unreadable"); continue; }
        var nif = new NiflySharp.NifFile();
        using (var ms = new MemoryStream(bytes, false)) nif.Load(ms);
        foreach (var blk in nif.Blocks)
        {
            if (blk is NiflySharp.Blocks.bhkRigidBodyT rbt)
            {
                var info = rbt.RigidBodyInfo_bRBCI2010;
                Console.WriteLine($"   bhkRigidBodyT translation ({info.Translation.X:0.####}, {info.Translation.Y:0.####}, {info.Translation.Z:0.####}, {info.Translation.W:0.####})  rotation ({info.Rotation.X:0.####}, {info.Rotation.Y:0.####}, {info.Rotation.Z:0.####}, {info.Rotation.W:0.####})  center ({info.Center.X:0.####}, {info.Center.Y:0.####}, {info.Center.Z:0.####})  layer {rbt.HavokFilter.Layer_SL}");
            }
            if (blk is NiflySharp.Blocks.bhkCompressedMeshShapeData cd)
            {
                Console.WriteLine($"   bhkCompressedMeshShapeData error={cd.Error} stated AABB ({cd.AABB.Min.X:0.###}, {cd.AABB.Min.Y:0.###}, {cd.AABB.Min.Z:0.###})..({cd.AABB.Max.X:0.###}, {cd.AABB.Max.Y:0.###}, {cd.AABB.Max.Z:0.###})  bigVerts={cd.BigVerts?.Count} bigTris={cd.BigTris?.Count} chunks={cd.Chunks?.Count} transforms={cd.ChunkTransforms?.Count}");
                if (cd.ChunkTransforms is not null)
                    foreach (var t in cd.ChunkTransforms)
                        Console.WriteLine($"      transform t=({t.Translation.X:0.####}, {t.Translation.Y:0.####}, {t.Translation.Z:0.####}, {t.Translation.W:0.####}) q=({t.Rotation.X:0.####}, {t.Rotation.Y:0.####}, {t.Rotation.Z:0.####}, {t.Rotation.W:0.####})");
                if (cd.Chunks is not null)
                    foreach (var c in cd.Chunks)
                    {
                        float mnx = float.MaxValue, mny = float.MaxValue, mnz = float.MaxValue, mxx = float.MinValue, mxy = float.MinValue, mxz = float.MinValue;
                        if (c.Vertices is not null) foreach (var q in c.Vertices) { mnx = MathF.Min(mnx, q.X); mny = MathF.Min(mny, q.Y); mnz = MathF.Min(mnz, q.Z); mxx = MathF.Max(mxx, q.X); mxy = MathF.Max(mxy, q.Y); mxz = MathF.Max(mxz, q.Z); }
                        Console.WriteLine($"      chunk offset=({c.Translation.X:0.####}, {c.Translation.Y:0.####}, {c.Translation.Z:0.####}, {c.Translation.W:0.####}) transformIndex={c.TransformIndex} reference={c.Reference} material={c.MaterialIndex} verts={c.Vertices?.Count} quantized range x {mnx}..{mxx} y {mny}..{mxy} z {mnz}..{mxz} indices={c.Indices?.Count} strips={c.Strips?.Count}");
                    }
            }
            if (blk is not NiflySharp.Blocks.bhkConvexVerticesShape cv) continue;
            Console.WriteLine($"   bhkConvexVerticesShape radius={cv.Radius} vertices={cv.Vertices?.Count} normals={cv.Normals?.Count}");
            if (cv.Vertices is not null) foreach (var v in cv.Vertices) Console.WriteLine($"      v ({v.X:0.####}, {v.Y:0.####}, {v.Z:0.####}, {v.W:0.####})");
            if (cv.Normals is not null)
                foreach (var n in cv.Normals)
                {
                    var nn = new Vector3(n.X, n.Y, n.Z); float nl = nn.Length();
                    float max = float.NegativeInfinity;
                    if (cv.Vertices is not null) foreach (var v in cv.Vertices) max = MathF.Max(max, Vector3.Dot(nn, new Vector3(v.X, v.Y, v.Z)));
                    Console.WriteLine($"      n ({n.X:0.####}, {n.Y:0.####}, {n.Z:0.####}, w {n.W:0.####})  |n|={nl:0.####}  max n.v={max:0.####}  max+w={max + n.W:0.####}  max+w+r={max + n.W + cv.Radius:0.####}");
                }
        }
    }
    return 0;
}

// ---- detail mode ---------------------------------------------------------------
if (detail.Count > 0)
{
    foreach (var rel in detail)
    {
        Console.WriteLine($"\n==== {rel} ====");
        var o = Load(rel, out var phys);
        if (o is null) { Console.WriteLine("   not resolvable through the VFS"); continue; }
        if (o.Model is null) { Console.WriteLine($"   unreadable: {o.Error}"); continue; }
        var m = o.Model;
        Console.WriteLine($"   source          : {phys}");
        Console.WriteLine($"   stream version  : {m.StreamVersion}   (unit scale applied: {m.HavokUnitScale})");
        Console.WriteLine($"   render          : {m.Shapes.Count} shapes, {m.TotalTriangles:N0} tris, extents {Ext(m.Bounds)} centre {Ctr(m.Bounds)}");
        Console.WriteLine($"   collision       : {(m.HasCollision ? "yes" : "NONE")}  solid={m.HasSolidCollision} complete={m.CollisionComplete}  "
                        + $"solid bounds (Havok u) extents {Ext(m.CollisionBounds)} centre {Ctr(m.CollisionBounds)}");
        foreach (var b in m.CollisionBodies ?? Array.Empty<CollisionBodyInfo>())
            Console.WriteLine($"     body {b.BodyType,-22} layer {b.Layer,-24} {(b.Solid ? "solid  " : "PHANTOM")} T={b.HasTransform} "
                            + $"frame t=({b.FrameTranslation.X:0.###}, {b.FrameTranslation.Y:0.###}, {b.FrameTranslation.Z:0.###}) rotated={b.FrameRotated} chain-identity={b.ChainIdentity}\n"
                            + $"          verts {b.VertexCount,6} tris {b.IndexCount / 3,6}  extents {Ext(b.Bounds)} centre {Ctr(b.Bounds)}  shapes [{string.Join(", ", b.ShapeTypes)}]"
                            + (b.Unread.Count > 0 ? $"  UNREAD [{string.Join("; ", b.Unread)}]" : ""));
        var s = m.CollisionStats!;
        Console.WriteLine($"   counters        : objects {s.CollisionObjects} phantoms {s.Phantoms} unattached {s.Unattached} bodiesT {s.BodiesWithTransform} plain {s.BodiesPlain}");
        Console.WriteLine($"                     CMS {s.CompressedShapes} chunks {s.Chunks} chunkTris {s.ChunkTriangles} bigTris {s.BigTriangles} chunkRot {s.ChunksWithRotation} chunkRef {s.ChunksReferencing}/{s.ChunksReferenceBroken} cmsScale≠1 {s.CompressedNonUnitScale} moppScale≠1 {s.MoppNonUnitScale}");
        Console.WriteLine($"                     stated-AABB max excess {s.StatedAabbMaxExcess:0.####} Havok u, min coverage {s.StatedAabbMinCoverage:0.###}, violations {s.StatedAabbViolations}");
        Console.WriteLine($"                     transform shapes {s.TransformShapes} (translation in column {s.TransformTranslationInColumn}, row {s.TransformTranslationInRow}, ambiguous {s.TransformTranslationAmbiguous}, rotated {s.TransformRotated})");
        Console.WriteLine($"                     convex {s.ConvexShapes} (planes dropped {s.ConvexPlanesDropped}, without normals {s.ConvexWithoutNormals}; stored w sign: minus {s.ConvexPlaneSignMinus} plus {s.ConvexPlaneSignPlus} neither {s.ConvexPlaneSignNeither}) boxes {s.Boxes} spheres {s.Spheres} capsules {s.Capsules} cylinders {s.Cylinders}");
        if (!m.Bounds.IsEmpty && !m.CollisionBounds.IsEmpty)
        {
            var r = m.Bounds.Size; var c = m.CollisionBounds.Size;
            Console.WriteLine($"   render / collision extent ratio : X {(c.X > 1e-3f ? (r.X / c.X).ToString("0.00") : "-")}  Y {(c.Y > 1e-3f ? (r.Y / c.Y).ToString("0.00") : "-")}  Z {(c.Z > 1e-3f ? (r.Z / c.Z).ToString("0.00") : "-")}");
        }
    }
    return 0;
}

// ---- the sweep: every mesh a Skyrim.esm base record names ----------------------
sw.Restart();
using var mod = SkyrimMod.CreateFromBinaryOverlay(vanilla, SkyrimRelease.SkyrimSE);
var meshes = new Dictionary<string, (string Type, int Records)>(StringComparer.OrdinalIgnoreCase);
foreach (var rec in mod.EnumerateMajorRecords())
    if (rec is IModeledGetter mg && mg.Model?.File is { } f && !string.IsNullOrWhiteSpace(f.GivenPath))
    {
        meshes.TryGetValue(f.GivenPath, out var prev);
        meshes[f.GivenPath] = (rec.GetType().Name.Replace("BinaryOverlay", ""), prev.Records + 1);
    }
Console.WriteLine($"vanilla base records name {meshes.Count:N0} distinct meshes  ({sw.Elapsed.TotalSeconds:0.0}s)");

var agg = new HavokDecodeStats();
int measured = 0, unresolved = 0, unreadable = 0, noCollision = 0, phantomOnly = 0, solidComplete = 0, solidPartial = 0, solidEmpty = 0, nonVanillaSource = 0;
var byShape = new Dictionary<string, int>();               // meshes containing this shape type in a SOLID body
var unreadReasons = new Dictionary<string, int>();
var layerBodies = new Dictionary<string, int>();
var layerMeshes = new Dictionary<string, int>();
var layerByType = new Dictionary<(string Type, string Layer), int>();   // record type x layer, solid bodies
var versions = new Dictionary<uint, int>();
var families = new Dictionary<string, int>();
float aabbExcessMax = 0, aabbCoverMin = float.NaN; int aabbViolations = 0; var aabbWorst = new List<(float Excess, string Mesh)>();
var aabbCover = new List<(float Cover, string Mesh)>();
var rotatedRows = new List<ScaleRow>();
var rows = new List<Row>();
var scaleRows = new List<ScaleRow>();
var chunkRotMeshes = new List<string>();
var transformRowMeshes = new List<string>();
var refChunkMeshes = new List<string>();

void Bump(Dictionary<string, int> d, string k) { d.TryGetValue(k, out var n); d[k] = n + 1; }
// convex census: each bhkConvexVerticesShape by whether its stored planes drop a face (the decoder rebuilds it from its
// vertices) and whether it has a convex radius (read only when a caller opts in): a shape's decode changes by the rebuild,
// by the radius alone, by both, or not at all
int cvRebuiltRadius = 0, cvRebuiltOnly = 0, cvRadiusOnly = 0, cvNeither = 0, cvNoNormals = 0;
var cvRebuiltByType = new Dictionary<string, int>(); var cvRebuiltMeshes = new List<string>();
void ConvexCensus(byte[] bytes, string type, string rel)
{
    var nif = new NiflySharp.NifFile();
    using (var ms = new MemoryStream(bytes, false)) if (nif.Load(ms) != 0) return;
    foreach (var cv in nif.Blocks.OfType<NiflySharp.Blocks.bhkConvexVerticesShape>())
    {
        if (cv.Normals is not { Count: > 0 }) { cvNoNormals++; continue; }
        var pts = (cv.Vertices ?? new()).Select(v => new Vector3(v.X, v.Y, v.Z)).ToList();
        HavokCollision.ConvexHullFromPlanes(pts, cv.Normals, out int dropped);
        bool r = cv.Radius > 0, d = dropped > 0;
        if (d && r) cvRebuiltRadius++; else if (d) cvRebuiltOnly++; else if (r) cvRadiusOnly++; else cvNeither++;
        if (d) { cvRebuiltByType[type] = cvRebuiltByType.GetValueOrDefault(type) + 1; if (!cvRebuiltMeshes.Contains(rel)) cvRebuiltMeshes.Add(rel); }
    }
}
var Wrappers = new[] { "bhkMoppBvTreeShape", "bhkListShape", "bhkConvexListShape", "bhkTransformShape", "bhkConvexTransformShape" };

sw.Restart();
int seen = 0;
foreach (var (rel, info) in meshes.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
{
    if (seen++ >= limit) break;
    var o = Load(rel, out var phys);
    if (o is null) { unresolved++; continue; }
    if (o.Model is null) { unreadable++; Bump(unreadReasons, "unreadable: " + (o.Error ?? "?").Split(' ', 4)[0]); continue; }
    var m = o.Model;
    measured++;
    if (lastBytes is not null) ConvexCensus(lastBytes, info.Type, rel);
    if (!phys.StartsWith(dataDir, StringComparison.OrdinalIgnoreCase)) nonVanillaSource++;
    versions.TryGetValue(m.StreamVersion, out var vn); versions[m.StreamVersion] = vn + 1;

    var s = m.CollisionStats!;
    agg.CollisionObjects += s.CollisionObjects; agg.Phantoms += s.Phantoms; agg.Unattached += s.Unattached;
    agg.BodiesWithTransform += s.BodiesWithTransform; agg.BodiesPlain += s.BodiesPlain;
    agg.Chunks += s.Chunks; agg.ChunkTriangles += s.ChunkTriangles; agg.BigTriangles += s.BigTriangles;
    agg.ChunksWithRotation += s.ChunksWithRotation; agg.ChunksReferencing += s.ChunksReferencing; agg.ChunksReferenceBroken += s.ChunksReferenceBroken;
    agg.CompressedShapes += s.CompressedShapes; agg.CompressedNonUnitScale += s.CompressedNonUnitScale; agg.CompressedZeroScale += s.CompressedZeroScale; agg.MoppNonUnitScale += s.MoppNonUnitScale; agg.PackedNonUnitScale += s.PackedNonUnitScale;
    agg.TransformShapes += s.TransformShapes; agg.TransformTranslationInColumn += s.TransformTranslationInColumn; agg.TransformTranslationInRow += s.TransformTranslationInRow;
    agg.TransformTranslationAmbiguous += s.TransformTranslationAmbiguous; agg.TransformRotated += s.TransformRotated;
    agg.ConvexShapes += s.ConvexShapes; agg.ConvexPlanesDropped += s.ConvexPlanesDropped; agg.ConvexWithoutNormals += s.ConvexWithoutNormals;
    agg.ConvexPlaneSignMinus += s.ConvexPlaneSignMinus; agg.ConvexPlaneSignPlus += s.ConvexPlaneSignPlus; agg.ConvexPlaneSignNeither += s.ConvexPlaneSignNeither;
    agg.ConvexPlaneSignMinusRadius += s.ConvexPlaneSignMinusRadius; agg.ConvexVertices += s.ConvexVertices; agg.ConvexShapesWithDroppedPlanes += s.ConvexShapesWithDroppedPlanes;
    agg.Boxes += s.Boxes; agg.Spheres += s.Spheres; agg.Capsules += s.Capsules; agg.Cylinders += s.Cylinders;
    agg.ConvexRebuiltFromVertices += s.ConvexRebuiltFromVertices; agg.ConvexPlanesMatchHull += s.ConvexPlanesMatchHull; agg.ConvexPlanesMismatch += s.ConvexPlanesMismatch;
    if (s.ChunksWithRotation > 0 && chunkRotMeshes.Count < 20) chunkRotMeshes.Add(rel);
    if (s.TransformTranslationInRow > 0 && transformRowMeshes.Count < 20) transformRowMeshes.Add(rel);
    if (s.ChunksReferencing > 0 && refChunkMeshes.Count < 20) refChunkMeshes.Add(rel);
    if (s.CompressedShapes > 0)
    {
        if (s.StatedAabbMaxExcess > aabbExcessMax) aabbExcessMax = s.StatedAabbMaxExcess;
        if (!float.IsNaN(s.StatedAabbMinCoverage) && (float.IsNaN(aabbCoverMin) || s.StatedAabbMinCoverage < aabbCoverMin)) aabbCoverMin = s.StatedAabbMinCoverage;
        aabbViolations += s.StatedAabbViolations;
        if (s.StatedAabbViolations > 0) aabbWorst.Add((s.StatedAabbMaxExcess, rel));
        if (!float.IsNaN(s.StatedAabbMinCoverage) && s.StatedAabbMinCoverage < 0.9f) aabbCover.Add((s.StatedAabbMinCoverage, rel));
    }

    var bodies = m.CollisionBodies ?? Array.Empty<CollisionBodyInfo>();
    var solid = bodies.Where(b => b.Solid).ToList();
    string state;
    if (!m.HasCollision) { noCollision++; state = "none"; }
    else if (solid.Count == 0) { phantomOnly++; state = "phantom-only"; }
    else if (solid.Any(b => b.Unread.Count > 0)) { solidPartial++; state = "partial"; }
    else if (solid.All(b => b.IndexCount == 0)) { solidEmpty++; state = "empty"; }
    else { solidComplete++; state = "complete"; }

    var solidTypes = solid.SelectMany(b => b.ShapeTypes).Distinct().ToList();
    foreach (var t in solidTypes) Bump(byShape, t);
    foreach (var u in bodies.SelectMany(b => b.Unread).Distinct()) Bump(unreadReasons, u);
    var meshLayers = new HashSet<string>();
    foreach (var b in solid)
    {
        Bump(layerBodies, b.Layer);
        meshLayers.Add(b.Layer);
        layerByType.TryGetValue((info.Type, b.Layer), out var ln); layerByType[(info.Type, b.Layer)] = ln + 1;
    }
    foreach (var l in meshLayers) Bump(layerMeshes, l);

    var leaves = solidTypes.Where(t => !Wrappers.Contains(t)).Distinct().ToList();
    string family = leaves.Count == 0 ? "(none)" : leaves.Count == 1 ? leaves[0] : "Mixed";
    if (state == "complete") Bump(families, family);

    rows.Add(new Row(rel, info.Type, state, family, m.Bounds, m.CollisionBounds, string.Join("|", meshLayers.OrderBy(x => x)), string.Join("; ", bodies.SelectMany(b => b.Unread).Distinct()),
                     solid.Count > 0 && solid.All(b => b.ChainIdentity), s.BodiesWithTransform, s.TransformRotated));

    // Scale measurement: complete solid decode, every solid body directly under an identity chain, both boxes real.
    if (state == "complete" && solid.All(b => b.ChainIdentity) && !m.Bounds.IsEmpty && !m.CollisionBounds.IsEmpty)
    {
        scaleRows.Add(new ScaleRow(rel, info.Type, family, m.Bounds, m.CollisionBounds));
        if (s.TransformRotated > 0) rotatedRows.Add(new ScaleRow(rel, info.Type, family, m.Bounds, m.CollisionBounds));
    }

    if (measured % 2000 == 0) Console.WriteLine($"   ... {measured:N0} meshes, {sw.Elapsed.TotalSeconds:0}s");
}

Console.WriteLine($"\n==== HAVOK COLLISION DECODER — vanilla mesh set ====");
Console.WriteLine($"meshes named by Skyrim.esm : {meshes.Count:N0}   measured {measured:N0}   unresolved {unresolved:N0}   unreadable {unreadable:N0}   from a non-vanilla source {nonVanillaSource:N0}");
Console.WriteLine($"elapsed                    : {sw.Elapsed.TotalSeconds:0.0}s");
Console.WriteLine($"stream versions            : {string.Join(", ", versions.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}: {kv.Value:N0}"))}");
Console.WriteLine();
double Pct(int n, int d) => d == 0 ? 0 : 100.0 * n / d;
Console.WriteLine($"   {"collision state",-30} {"meshes",9} {"share",8}");
Console.WriteLine($"   {"no collision object",-30} {noCollision,9:N0} {Pct(noCollision, measured),7:0.00}%");
Console.WriteLine($"   {"phantom bodies only",-30} {phantomOnly,9:N0} {Pct(phantomOnly, measured),7:0.00}%");
Console.WriteLine($"   {"solid, decoded COMPLETE",-30} {solidComplete,9:N0} {Pct(solidComplete, measured),7:0.00}%");
Console.WriteLine($"   {"solid, PARTIAL (unread part)",-30} {solidPartial,9:N0} {Pct(solidPartial, measured),7:0.00}%");
Console.WriteLine($"   {"solid, no triangles produced",-30} {solidEmpty,9:N0} {Pct(solidEmpty, measured),7:0.00}%");
int withColl = measured - noCollision;
Console.WriteLine($"   => of {withColl:N0} meshes with collision, {solidComplete:N0} ({Pct(solidComplete, withColl):0.00}%) have a complete solid soup");

Console.WriteLine($"\n   ---- shape types met in SOLID bodies (meshes containing each) ----");
foreach (var kv in byShape.OrderByDescending(kv => kv.Value)) Console.WriteLine($"   {kv.Value,7:N0}  {kv.Key}");
Console.WriteLine($"\n   ---- complete decodes by leaf-shape family ----");
foreach (var kv in families.OrderByDescending(kv => kv.Value)) Console.WriteLine($"   {kv.Value,7:N0}  {kv.Key}");
Console.WriteLine($"\n   ---- UNREAD, by reason (meshes) ----");
if (unreadReasons.Count == 0) Console.WriteLine("   (none)");
foreach (var kv in unreadReasons.OrderByDescending(kv => kv.Value)) Console.WriteLine($"   {kv.Value,7:N0}  {kv.Key}");

Console.WriteLine($"\n   ---- decoder counters, whole sweep ----");
Console.WriteLine($"   collision objects {agg.CollisionObjects:N0}   phantoms {agg.Phantoms:N0}   unattached {agg.Unattached:N0}   bodies with a transform (T) {agg.BodiesWithTransform:N0}   plain {agg.BodiesPlain:N0}");
Console.WriteLine($"   compressed shapes {agg.CompressedShapes:N0}   chunks {agg.Chunks:N0}   chunk triangles {agg.ChunkTriangles:N0}   big triangles {agg.BigTriangles:N0}");
Console.WriteLine($"   chunks with a non-identity rotation {agg.ChunksWithRotation:N0}   instanced (reference) chunks {agg.ChunksReferencing:N0} (broken {agg.ChunksReferenceBroken:N0})");
Console.WriteLine($"   non-unit scale: compressed {agg.CompressedNonUnitScale:N0} (zero, ignored: {agg.CompressedZeroScale:N0})   mopp {agg.MoppNonUnitScale:N0}   packed {agg.PackedNonUnitScale:N0}");
Console.WriteLine($"   transform shapes {agg.TransformShapes:N0}: translation in the last COLUMN {agg.TransformTranslationInColumn:N0}, last ROW {agg.TransformTranslationInRow:N0}, ambiguous {agg.TransformTranslationAmbiguous:N0}; rotated {agg.TransformRotated:N0}");
Console.WriteLine($"   convex hulls {agg.ConvexShapes:N0} ({agg.ConvexVertices:N0} vertices; planes dropped {agg.ConvexPlanesDropped:N0} in {agg.ConvexShapesWithDroppedPlanes:N0} shapes, without normals {agg.ConvexWithoutNormals:N0}; stored w: minus {agg.ConvexPlaneSignMinus:N0} plus {agg.ConvexPlaneSignPlus:N0} minus-with-radius {agg.ConvexPlaneSignMinusRadius:N0} neither {agg.ConvexPlaneSignNeither:N0})   boxes {agg.Boxes:N0}   spheres {agg.Spheres:N0}   capsules {agg.Capsules:N0}   cylinders {agg.Cylinders:N0}");
Console.WriteLine($"   convex decode: faces rebuilt from vertices {agg.ConvexRebuiltFromVertices:N0}; stored planes within the hull's radius window {agg.ConvexPlanesMatchHull:N0}, outside it {agg.ConvexPlanesMismatch:N0}");
Console.WriteLine($"   convex census (distinct blocks per mesh read): rebuilt and with a radius {cvRebuiltRadius:N0}, rebuilt only {cvRebuiltOnly:N0}, radius only (changes only when the radius is read) {cvRadiusOnly:N0}, neither {cvNeither:N0}, no normals {cvNoNormals:N0}");
Console.WriteLine($"   rebuilt shapes by the naming record type: {string.Join(", ", cvRebuiltByType.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}"))}; meshes {cvRebuiltMeshes.Count}");
if (dump is not null) File.WriteAllLines(Path.ChangeExtension(dump, ".convex-rebuilt.txt"), cvRebuiltMeshes);
Console.WriteLine($"\n   ---- compressed-mesh SELF-CHECK against the AABB each shape states ----");
Console.WriteLine($"   max overshoot of the stated box : {aabbExcessMax:0.####} Havok units   (violations > 0.02: {aabbViolations:N0})");
Console.WriteLine($"   min per-axis coverage of it     : {aabbCoverMin:0.###}");
foreach (var w in aabbWorst.OrderByDescending(w => w.Excess).Take(8)) Console.WriteLine($"      {w.Excess,8:0.####}  {w.Mesh}");
Console.WriteLine($"   meshes whose decoded box covers < 90 % of the stated box on some axis: {aabbCover.Count:N0}");
foreach (var c in aabbCover.OrderBy(c => c.Cover).Take(8)) Console.WriteLine($"      {c.Cover,8:0.###}  {c.Mesh}");
if (chunkRotMeshes.Count > 0) Console.WriteLine($"   meshes with rotated chunk transforms (first {chunkRotMeshes.Count}): {string.Join(", ", chunkRotMeshes)}");
if (transformRowMeshes.Count > 0) Console.WriteLine($"   meshes with a row-translation transform shape (first {transformRowMeshes.Count}): {string.Join(", ", transformRowMeshes)}");
if (refChunkMeshes.Count > 0) Console.WriteLine($"   meshes with instanced chunks (first {refChunkMeshes.Count}): {string.Join(", ", refChunkMeshes)}");

Console.WriteLine($"\n   ---- collision LAYERS of solid bodies ----");
Console.WriteLine($"   {"layer",-32} {"bodies",9} {"meshes",9}");
foreach (var kv in layerBodies.OrderByDescending(kv => kv.Value))
    Console.WriteLine($"   {kv.Key,-32} {kv.Value,9:N0} {layerMeshes[kv.Key],9:N0}");
Console.WriteLine($"\n   ---- layer by record type (solid bodies; the types the navmesh floor is made of) ----");
foreach (var type in new[] { "Static", "Tree", "Flora", "MoveableStatic", "Activator", "Furniture", "Container", "Door" })
{
    var part = layerByType.Where(kv => kv.Key.Type == type).OrderByDescending(kv => kv.Value).ToList();
    if (part.Count == 0) continue;
    Console.WriteLine($"   {type,-16} {string.Join("   ", part.Take(8).Select(kv => $"{kv.Key.Layer} {kv.Value:N0}"))}");
}

// ---- the scale --------------------------------------------------------------------
Console.WriteLine($"\n==== HAVOK → SKYRIM UNIT SCALE, measured ====");
Console.WriteLine($"   render extent / collision extent (Havok units), per axis, on {scaleRows.Count:N0} meshes whose solid collision decoded");
Console.WriteLine($"   completely under an identity node chain. Axes with collision extent < 0.05 Havok u or render extent < 5 u are skipped.");
static float Q(List<float> xs, double q) { if (xs.Count == 0) return float.NaN; int i = (int)Math.Clamp(Math.Round(q * (xs.Count - 1)), 0, xs.Count - 1); return xs[i]; }
var ratiosByFamily = new Dictionary<string, List<float>>();
var allRatios = new List<float>();
foreach (var r in scaleRows)
{
    var rs = r.Render.Size; var cs = r.Coll.Size;
    foreach (var (re, ce) in new[] { (rs.X, cs.X), (rs.Y, cs.Y), (rs.Z, cs.Z) })
    {
        if (ce < 0.05f || re < 5f) continue;
        float ratio = re / ce;
        if (!ratiosByFamily.TryGetValue(r.Family, out var list)) ratiosByFamily[r.Family] = list = new List<float>();
        list.Add(ratio); allRatios.Add(ratio);
    }
}
allRatios.Sort();
Console.WriteLine($"\n   {"family",-28} {"ratios",8} {"median",9} {"p25",9} {"p75",9} {"p5",9} {"p95",9} {"within 2% of median",20}");
foreach (var kv in ratiosByFamily.OrderByDescending(kv => kv.Value.Count))
{
    var xs = kv.Value; xs.Sort();
    float med = Q(xs, 0.5);
    int near = xs.Count(x => MathF.Abs(x - med) <= 0.02f * med);
    Console.WriteLine($"   {kv.Key,-28} {xs.Count,8:N0} {med,9:0.000} {Q(xs, 0.25),9:0.000} {Q(xs, 0.75),9:0.000} {Q(xs, 0.05),9:0.000} {Q(xs, 0.95),9:0.000} {near,8:N0} ({Pct(near, xs.Count):0.0}%)");
}
float medAll = Q(allRatios, 0.5);
Console.WriteLine($"   {"ALL",-28} {allRatios.Count,8:N0} {medAll,9:0.000} {Q(allRatios, 0.25),9:0.000} {Q(allRatios, 0.75),9:0.000} {Q(allRatios, 0.05),9:0.000} {Q(allRatios, 0.95),9:0.000}");

// The most precise cells: the largest compressed-mesh statics, where a padding of a few units is a tiny fraction.
Console.WriteLine($"\n   ---- the 15 largest complete compressed-mesh statics: per-axis ratios ----");
foreach (var r in scaleRows.Where(r => r.Family == "bhkCompressedMeshShape").OrderByDescending(r => r.Render.Size.X + r.Render.Size.Y + r.Render.Size.Z).Take(15))
{
    var rs = r.Render.Size; var cs = r.Coll.Size;
    string R(float re, float ce) => ce < 0.05f ? "   -  " : (re / ce).ToString("0.000");
    Console.WriteLine($"   {R(rs.X, cs.X),7} {R(rs.Y, cs.Y),7} {R(rs.Z, cs.Z),7}   render {Ext(r.Render),-28} {r.Mesh}");
}

// Centre agreement at the measured factor: a decode that mirrors or misplaces a body shows up as an offset.
if (!float.IsNaN(medAll))
{
    Console.WriteLine($"\n   ---- centre offset |render centre − {medAll:0.00} × collision centre| in game units, per family ----");
    Console.WriteLine($"   {"family",-28} {"meshes",8} {"median",9} {"p90",9} {"max",9}");
    foreach (var grp in scaleRows.GroupBy(r => r.Family).OrderByDescending(g => g.Count()))
    {
        var ds = grp.Select(r =>
        {
            var rc = r.Render.Center; var cc = r.Coll.Center;
            return new Vector3(rc.X - medAll * cc.X, rc.Y - medAll * cc.Y, rc.Z - medAll * cc.Z).Length();
        }).OrderBy(x => x).ToList();
        Console.WriteLine($"   {grp.Key,-28} {ds.Count,8:N0} {Q(ds, 0.5),9:0.0} {Q(ds, 0.9),9:0.0} {ds[^1],9:0.0}");
    }
}

// The meshes whose transform shapes carry a ROTATION — the only ones that can tell a transposed reading apart.
if (rotatedRows.Count > 0 && !float.IsNaN(medAll))
{
    var rr = new List<float>(); var dd = new List<float>();
    foreach (var r in rotatedRows)
    {
        var rs = r.Render.Size; var cs = r.Coll.Size;
        foreach (var (re, ce) in new[] { (rs.X, cs.X), (rs.Y, cs.Y), (rs.Z, cs.Z) }) if (ce >= 0.05f && re >= 5f) rr.Add(re / ce);
        var rc = r.Render.Center; var cc = r.Coll.Center;
        dd.Add(new Vector3(rc.X - medAll * cc.X, rc.Y - medAll * cc.Y, rc.Z - medAll * cc.Z).Length());
    }
    rr.Sort(); dd.Sort();
    Console.WriteLine($"\n   ---- subset: {rotatedRows.Count:N0} meshes with a ROTATED transform shape (the test of the rotation convention) ----");
    Console.WriteLine($"   extent ratio median {Q(rr, 0.5):0.000}  p25 {Q(rr, 0.25):0.000}  p75 {Q(rr, 0.75):0.000}  p95 {Q(rr, 0.95):0.000}   centre offset median {Q(dd, 0.5):0.0} u  p90 {Q(dd, 0.9):0.0} u  max {dd[^1]:0.0} u");
    foreach (var r in rotatedRows.Take(6))
    {
        var cs = r.Coll.Size;
        Console.WriteLine($"      {r.Mesh,-60} render {Ext(r.Render),-26} coll*{medAll:0} {cs.X * medAll:0.#} x {cs.Y * medAll:0.#} x {cs.Z * medAll:0.#}  ctr {Ctr(r.Render)} vs ({r.Coll.Center.X * medAll:0.#}, {r.Coll.Center.Y * medAll:0.#}, {r.Coll.Center.Z * medAll:0.#})");
    }
}

if (dump is not null)
{
    using var w = new StreamWriter(dump);
    w.WriteLine("mesh\ttype\tstate\tfamily\trender_ext_x\trender_ext_y\trender_ext_z\tcoll_ext_x\tcoll_ext_y\tcoll_ext_z\trender_ctr\tcoll_ctr\tlayers\tunread\tchain_identity\tbodies_T\ttransform_rotated");
    foreach (var r in rows)
    {
        var rs = r.Render.IsEmpty ? default : r.Render.Size; var cs = r.Coll.IsEmpty ? default : r.Coll.Size;
        w.WriteLine($"{r.Mesh}\t{r.Type}\t{r.State}\t{r.Family}\t{rs.X:0.###}\t{rs.Y:0.###}\t{rs.Z:0.###}\t{cs.X:0.####}\t{cs.Y:0.####}\t{cs.Z:0.####}\t{Ctr(r.Render)}\t{Ctr(r.Coll)}\t{r.Layers}\t{r.Unread}\t{r.ChainIdentity}\t{r.BodiesT}\t{r.TransformRotated}");
    }
    Console.WriteLine($"\nper-mesh rows -> {dump}");
}
return 0;

record Row(string Mesh, string Type, string State, string Family, Aabb Render, Aabb Coll, string Layers, string Unread, bool ChainIdentity, int BodiesT, int TransformRotated);
record ScaleRow(string Mesh, string Type, string Family, Aabb Render, Aabb Coll);
