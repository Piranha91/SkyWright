// motionprobe — what moves a mesh, and where it goes (animated-object swept volume).
//
// Reads a NIF's motion through housecarl-core (NifMotion, HavokBehaviour, HavokAnimation, SweptGeometry) and prints
// JSON. Assets come from a load order (--instance/--profile/--game/--order, the same resolution as contactprobe) or
// from a loose folder laid out like Data (--loose-root DIR, e.g. extracted copies). Nothing is written but --out.
//
// usage:
//   motionprobe ASSETS inspect REL                                  animations, named poses, shapes, skins, graph
//   motionprobe ASSETS pose REL (--animation ID --t T | --pose NAME) [--geometry render|collision] [--vertices]
//   motionprobe ASSETS node REL --animation ID --node NAME (--t T | --times T1,T2,..)   a node's local transform
//   motionprobe ASSETS clip REL.hkx                                  decoded clip, every frame of every track
//   motionprobe ASSETS boundcheck REL.hkx [--samples N] [--split K]    every track's motion bound against dense samples
//   motionprobe ASSETS deltacheck REL [--animation ID]... [--geometry G] [--delta U] [--samples N]   every swept interval's
//                                                                    Delta against the mesh posed at N dense times inside it
//   motionprobe ASSETS graph GRAPH.hkx                               behaviour-graph reading (path as a NIF names it)
//   motionprobe ASSETS sweep REL [--animation ID]... [--geometry render|collision] [--delta U] [--budget N] [--octree-threads N]
//                                                                    (the volume octree's classification threads; default
//                                                                    the processor count; the answer does not depend on it.
//                                                                    stderr: "octree: cells N, near tests N, triangle tests N,
//                                                                    threads T")
//   ASSETS = --loose-root DIR | --instance DIR --profile P --game DIR --order CANDIDATE.json
//   common: [--out FILE] [--placement BASETYPE[,open]]
// exit: 0 printed, 1 the reading refused or failed (JSON says why), 2 usage.

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using HousecarlCore.Progress;

using var __run = RunLog.BeginProcess("motionprobe", args);

string? loose = null, instance = null, profile = null, game = null, order = null, outPath = null, placementArg = null;
string? anim = null, pose = null, node = null, geometryArg = null;
var anims = new List<string>();
float? t = null; List<float>? times = null; double? delta = null; int? budget = null; int samplesN = 64, split = 4; bool vertices = false;
int octreeThreads = 0;
var positional = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    string Need() => i + 1 < args.Length ? args[++i] : throw new ArgumentException(args[i] + " needs a value");
    switch (args[i])
    {
        case "--loose-root": loose = Need(); break;
        case "--instance": instance = Need(); break;
        case "--profile": profile = Need(); break;
        case "--game": game = Need(); break;
        case "--order": order = Need(); break;
        case "--out": outPath = Need(); break;
        case "--placement": placementArg = Need(); break;
        case "--animation": anim = Need(); anims.Add(anim); break;
        case "--pose": pose = Need(); break;
        case "--node": node = Need(); break;
        case "--geometry": geometryArg = Need(); break;
        case "--t": t = float.Parse(Need(), CultureInfo.InvariantCulture); break;
        case "--times": times = Need().Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToList(); break;
        case "--delta": delta = double.Parse(Need(), CultureInfo.InvariantCulture); break;
        case "--budget": budget = int.Parse(Need(), CultureInfo.InvariantCulture); break;
        case "--vertices": vertices = true; break;
        case "--samples": samplesN = int.Parse(Need(), CultureInfo.InvariantCulture); break;
        case "--split": split = int.Parse(Need(), CultureInfo.InvariantCulture); break;
        case "--octree-threads": octreeThreads = int.Parse(Need(), CultureInfo.InvariantCulture); break;
        default:
            if (args[i].StartsWith("--")) { Console.Error.WriteLine("unknown option " + args[i]); return 2; }
            positional.Add(args[i]); break;
    }
}
if (positional.Count < 2 || (loose is null && (instance is null || profile is null || game is null || order is null)))
{
    Console.Error.WriteLine("usage: motionprobe (--loose-root DIR | --instance D --profile P --game D --order C) inspect|pose|node|clip|graph|sweep REL ...");
    return 2;
}
string verb = positional[0], rel = positional[1];
var geometry = (geometryArg ?? "render").ToLowerInvariant() switch
{
    "render" => VolumeGeometry.Render, "collision" => VolumeGeometry.Collision,
    _ => throw new ArgumentException("--geometry takes render|collision"),
};
MotionPlacement? placement = null;
if (placementArg is not null)
{
    var parts = placementArg.Split(',');
    placement = new MotionPlacement(parts[0], parts.Skip(1).Any(p => p.Equals("open", StringComparison.OrdinalIgnoreCase)));
}

// ---- asset access ----
AssetResolver? assets = null;
Func<string, byte[]?> read;
if (loose is not null)
{
    string root = loose;
    read = r => { var p = Path.Combine(root, r.Replace('\\', Path.DirectorySeparatorChar)); return File.Exists(p) ? File.ReadAllBytes(p) : null; };
}
else
{
    string profileDir = Path.Combine(instance!, "profiles", profile!), modsDir = Path.Combine(instance!, "mods"),
           overwriteDir = Path.Combine(instance!, "overwrite"), dataDir = Path.Combine(game!, "Data");
    var cfg = HarnessOrderCli.Resolve(profile!, profileDir, modsDir, dataDir, overwriteDir, game!, order!, null, null);
    if (cfg.Refused) { Console.Error.WriteLine(cfg.RefusedReason); return 2; }
    assets = AssetResolver.Build(overwriteDir, modsDir, dataDir, cfg.EnabledMods, cfg.Archives.Archives);
    var a = assets;
    read = r =>
    {
        try
        {
            var res = a.ResolveForPlacement(r);
            if (res.Sources.Count == 0) return null;
            var (bytes, _) = AssetResolver.ReadPlacementSource(res.Sources[0]);
            return bytes;
        }
        catch (ArgumentException) { return null; }   // a path the resolver refuses is an absent asset here
    };
}

var json = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };
int Emit(object o, bool ok)
{
    var text = JsonSerializer.Serialize(o, json);
    if (outPath is not null) File.WriteAllText(outPath, text); else Console.WriteLine(text);
    return ok ? 0 : 1;
}
static float[] V(Vector3 v) => new[] { v.X, v.Y, v.Z };
static float[] Q(Quaternion q) => new[] { q.X, q.Y, q.Z, q.W };
static float[] M(Matrix4x4 m) => new[] { m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33 };
static object Bounds(TriangleSoup? s)
{
    if (s is null || s.Vertices.Length == 0) return new { empty = true };
    var lo = s.Vertices.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
    var hi = s.Vertices.Aggregate(new Vector3(float.MinValue), Vector3.Max);
    return new { min = V(lo), max = V(hi), vertices = s.Vertices.Length, triangles = s.TriangleCount };
}

try
{
    if (verb == "graph")
    {
        // REL is the path as a NIF's BSBehaviorGraphExtraData names it (relative to meshes\); a meshes\ prefix is accepted
        var graphFile = rel.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) ? rel[7..] : rel;
        var g = HavokBehaviour.Resolve(graphFile, read);
        return Emit(new { verb, rel, graph = g }, g.Status == "read");
    }
    var bytes = read(rel);
    if (bytes is null) return Emit(new { verb, rel, status = "refused", reason = "missing_asset: " + rel }, false);
    switch (verb)
    {
        case "inspect":
        {
            var m = NifMotion.Read(bytes, read, placement);
            return Emit(new { verb, rel, policy = NifMotion.PolicyVersion, keys = NifMotionKeys.Convention, model = m }, m.Status != "refused");
        }
        case "pose":
        {
            var m = NifMotion.Read(bytes, read, placement);
            var p = pose is not null ? NifMotion.Pose(bytes, m, pose, geometry, read)
                  : NifMotion.Pose(bytes, m, anim ?? throw new ArgumentException("pose needs --animation and --t, or --pose"),
                                   t ?? throw new ArgumentException("pose needs --t"), geometry, read);
            return Emit(new { verb, rel, p.Status, p.Reason, p.AnimationId, p.Time, bounds = Bounds(p.Soup),
                              vertices = vertices && p.Soup is not null ? p.Soup.Vertices.Select(V).ToArray() : null }, p.Status == "posed");
        }
        case "node":
        {
            var m = NifMotion.Read(bytes, read, placement);
            var at = times ?? new List<float> { t ?? throw new ArgumentException("node needs --t or --times") };
            string animId = anim ?? throw new ArgumentException("node needs --animation"), nodeName = node ?? throw new ArgumentException("node needs --node");
            var rows = at.Select(time =>
            {
                var (r, s, tr) = NifMotion.NodeLocal(bytes, m, animId, nodeName, time, read);
                return new { time, rotationRows = M(r), scale = s, translation = V(tr) };
            });
            // an animation or node the model does not have is a refusal naming what the model offers, not a crash
            try { return Emit(new { verb, rel, animation = anim, node, samples = rows.ToList() }, true); }
            catch (ArgumentException ex)
            {
                return Emit(new { verb, rel, status = "refused", reason = $"{ex.Message}; the model's animations: {string.Join(", ", m.Animations.Select(x => x.Id))} (or nifkeys:<node>)" }, false);
            }
        }
        case "clip":
        {
            var c = HavokAnimation.Decode(bytes);
            if (c.Status != "decoded") return Emit(new { verb, rel, c.Status, c.Reason }, false);
            var frames = new List<object>();
            for (int f = 0; f < c.Frames; f++)
            {
                float time = Math.Min(f * c.FrameDuration, c.Duration);
                frames.Add(new { f, time, tracks = Enumerable.Range(0, c.TransformTracks).Select(k => { var x = c.Sample(k, time); return new { T = V(x.Translation), R = Q(x.Rotation), S = V(x.Scale) }; }).ToArray() });
            }
            return Emit(new { verb, rel, c.Status, c.Duration, c.FrameDuration, c.Frames, c.Blocks, c.TransformTracks, c.FloatTracks,
                              c.OriginalSkeletonName, c.TrackToBone, c.RotationQuantizations, frames }, true);
        }
        case "boundcheck":
        {
            // For every track and every interval a sampler would use (consecutive knot times, each split in K), the
            // certified Bound() must contain the actual change from t0 at N dense samples. Reports counts and violations.
            var c = HavokAnimation.Decode(bytes);
            if (c.Status != "decoded") return Emit(new { verb, rel, c.Status, c.Reason }, false);
            var knots = c.KnotTimes();
            long intervals = 0, checks = 0; var bad = new List<object>(); double worstSlackA = double.MaxValue;
            for (int k = 0; k < c.TransformTracks; k++)
                for (int i = 0; i + 1 < knots.Count; i++)
                    for (int part = 0; part < split; part++)
                    {
                        float a = knots[i] + (knots[i + 1] - knots[i]) * part / split, b = knots[i] + (knots[i + 1] - knots[i]) * (part + 1) / split;
                        var bound = c.Bound(k, a, b); var s0 = c.Sample(k, a);
                        intervals++;
                        for (int j = 0; j <= samplesN; j++)
                        {
                            float tt = a + (b - a) * j / samplesN; var s1 = c.Sample(k, tt); checks++;
                            float dT = Vector3.Distance(s1.Translation, s0.Translation), dS = Vector3.Distance(s1.Scale, s0.Scale);
                            float dA = HavokClip.AngleBetween(s1.Rotation, s0.Rotation);
                            worstSlackA = Math.Min(worstSlackA, bound.Angle - dA);
                            if (dT > bound.Translation || dA > bound.Angle || dS > bound.Scale)
                                bad.Add(new { track = k, t0 = a, t1 = b, t = tt, dT, dA, dS, bound = new { bound.Translation, bound.Angle, bound.Scale } });
                        }
                    }
            return Emit(new { verb, rel, tracks = c.TransformTracks, knots = knots.Count, intervals, checks, violations = bad.Count, worstAngleSlack = worstSlackA, first = bad.Take(5) }, bad.Count == 0);
        }
        case "deltacheck":
        {
            // For every interval between consecutive samples of the swept region, pose the mesh at N dense times inside it
            // and require every vertex's displacement from its pose at the interval's start to be <= that interval's Delta.
            var m = NifMotion.Read(bytes, read, placement);
            var opts = new MotionOptions(DeltaTarget: delta ?? 0.5, SampleBudget: budget ?? 4096);
            var r = SweptGeometry.Build(bytes, m, anims.Count > 0 ? anims : null, geometry, opts, read);
            if (r.Status != "built") return Emit(new { verb, rel, r.Status, r.Reason }, false);
            long intervals = 0, checks = 0; double worstSlack = double.MaxValue, worstRatio = 0; var bad = new List<object>();
            for (int i = 0; i + 1 < r.Samples.Count; i++)
            {
                var a = r.Samples[i]; var b = r.Samples[i + 1];
                if (a.AnimationId != b.AnimationId || b.Time <= a.Time) continue;
                intervals++;
                for (int j = 1; j <= samplesN; j++)
                {
                    float tt = a.Time + (b.Time - a.Time) * j / (samplesN + 1);
                    var p = NifMotion.Pose(bytes, m, a.AnimationId, tt, geometry, read);
                    if (p.Soup is null || p.Soup.Vertices.Length != a.Soup.Vertices.Length) return Emit(new { verb, rel, status = "error", reason = $"pose at {tt} differs in shape: {p.Reason}" }, false);
                    double worst = 0;
                    for (int v = 0; v < p.Soup.Vertices.Length; v++) worst = Math.Max(worst, Vector3.Distance(p.Soup.Vertices[v], a.Soup.Vertices[v]));
                    checks += p.Soup.Vertices.Length;
                    worstSlack = Math.Min(worstSlack, a.Delta - worst);
                    if (a.Delta > 0) worstRatio = Math.Max(worstRatio, worst / a.Delta);
                    if (worst > a.Delta) bad.Add(new { animation = a.AnimationId, t0 = a.Time, t1 = b.Time, t = tt, displacement = worst, a.Delta });
                }
            }
            return Emit(new { verb, rel, geometry = geometry.ToString().ToLowerInvariant(), samples = r.Samples.Count, intervals, vertexTimeChecks = checks,
                              worstSlack, worstDisplacementOverDelta = worstRatio, violations = bad.Count, first = bad.Take(5) }, bad.Count == 0);
        }
        case "sweep":
        {
            var m = NifMotion.Read(bytes, read, placement);
            var opts = new MotionOptions(DeltaTarget: delta ?? 0.5, SampleBudget: budget ?? 4096);
            var r = SweptGeometry.Build(bytes, m, anims.Count > 0 ? anims : null, geometry, opts, read);
            var octree = new SweptVolumeRun { Threads = octreeThreads };
            var v = r.Status == "built" ? SweptGeometry.Volume(r, null, run: octree) : null;
            if (v is not null)
            {
                Console.Error.WriteLine($"octree: cells {octree.Cells}, near tests {octree.NearTests}, triangle tests {octree.TriangleTests}, threads {octree.ThreadsUsed}");
                __run.Feature("octree_cells", octree.Cells, "cells", FeatureAt.Final).Feature("octree_near_tests", octree.NearTests, "tests", FeatureAt.Final)
                     .Feature("octree_triangle_tests", octree.TriangleTests, "tests", FeatureAt.Final).Feature("octree_threads", octree.ThreadsUsed, "threads", FeatureAt.Final);
            }
            return Emit(new { verb, rel, policy = SweptGeometry.PolicyVersion, r.Status, r.Reason, r.Geometry, r.MaxDelta, r.Converged, r.Solid,
                              animations = r.Animations, samples = r.Samples.Count,
                              outerBounds = Bounds(r.Samples.Count == 0 ? null : new TriangleSoup(r.Samples.SelectMany(x => x.Soup.Vertices).ToArray(), Array.Empty<int>())),
                              volume = v }, r.Status == "built");
        }
        default:
            Console.Error.WriteLine("unknown verb " + verb); return 2;
    }
}
catch (Exception ex) when (ex is not ArgumentException)
{
    return Emit(new { verb, rel, status = "error", reason = ex.GetType().Name + ": " + ex.Message }, false);
}
finally { assets?.Dispose(); }
