using System.Diagnostics;
using System.Numerics;
using HarnessShared;
using HousecarlCore;
using HousecarlCore.Progress;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyWright.Engine;

// One load order as lightprobe reads it (moved out of Program.cs 2026-11-26 for the --check mode, unchanged in what it reads): the
// configuration and its asset view, the shared index (pass 1), the bases and light overrides of the references in scope (pass 2), and
// per scene the references, lights and (with geometry) the readings. The single-order mode writes one of these as housecarl.light/1;
// --check reads four, each as itself, sharing only the model cache (keyed by the resolved source, LightMeshCache).
sealed class OrderReading : IDisposable
{
    public ResolvedConfiguration Cfg { get; }
    public AssetResolver Assets { get; }
    public OverlapIndex Idx { get; } = new();
    public EnableArms Arms { get; }
    public string[] PluginName { get; }
    public BosLayer? Bos { get; private set; }
    public readonly Dictionary<FormKey, (string Type, string? Edid)> BaseType = new();
    public readonly Dictionary<FormKey, LightBase> LightBase = new();
    public readonly Dictionary<FormKey, float?> RefRadius = new();
    public double IndexSeconds { get; private set; }
    public double BasesSeconds { get; private set; }

    readonly Run _run;
    readonly Action<string> _cap;
    readonly LightMeshCache _cache;
    readonly Dictionary<string, (LightModel? Model, string? Why)> _models = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, EmitterModel> _emitters = new(StringComparer.OrdinalIgnoreCase);

    OrderReading(ResolvedConfiguration cfg, AssetResolver assets, int plugins, Run run, Action<string> cap, LightMeshCache cache)
    {
        Cfg = cfg; Assets = assets; PluginName = new string[plugins]; _run = run; _cap = cap; _cache = cache;
        Arms = new EnableArms(Idx);
    }

    public void Dispose() => Assets.Dispose();

    /// <summary>Resolve the order and read pass 1 (the shared index). Null with the reason when the order is refused or a plugin will not
    /// read. <paramref name="trackLand"/> keeps each exterior cell's terrain; <paramref name="bos"/> reads the Base Object Swapper files
    /// (only to know which scenes they touch: they are never applied).</summary>
    public static OrderReading? Open(string profileName, string profileDir, string modsDir, string dataDir, string overwriteDir, string gamePath,
                                     string? orderFile, Run run, Action<string> cap, Stopwatch sw, LightMeshCache cache, bool trackLand, bool bos,
                                     out string? refusal, bool print = true)
    {
        refusal = null;
        var cfg = HarnessOrderCli.Resolve(profileName, profileDir, modsDir, dataDir, overwriteDir, gamePath, orderFile, null, null);
        if (cfg.Refused) { refusal = cfg.RefusedReason; return null; }
        return Open(cfg, modsDir, dataDir, overwriteDir, run, cap, sw, cache, trackLand, bos, out refusal, print);
    }

    /// <summary>Read pass 1 of an order already resolved.</summary>
    public static OrderReading? Open(ResolvedConfiguration cfg, string modsDir, string dataDir, string overwriteDir, Run run, Action<string> cap,
                                     Stopwatch sw, LightMeshCache cache, bool trackLand, bool bos, out string? refusal, bool print = true)
    {
        refusal = null;
        var paths = cfg.OrderedPaths;
        var assets = AssetResolver.Build(overwriteDir, modsDir, dataDir, cfg.EnabledMods, cfg.Archives.Archives);
        if (print) HarnessOrderCli.Print(cfg);
        var o = new OrderReading(cfg, assets, paths.Count, run, cap, cache);
        if (trackLand) o.Idx.TrackLand = true;
        if (bos) o.Idx.Bos = new BosFormIndex();
        var lightness = new List<(ModKey, bool)>();
        var t0 = sw.Elapsed.TotalSeconds;
        // ---- pass 1: the shared index (placements, bases with models, enable chain facts, deletions) ----
        run.Feature("plugins", paths.Count, "plugins").Stage("scan", "reading plugins", paths.Count, "plugins");
        for (int p = 0; p < paths.Count; p++)
        {
            run.Advance();
            ISkyrimModGetter mod;
            try { mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], SkyrimRelease.SkyrimSE); }
            catch (Exception ex) { refusal = $"{Path.GetFileName(paths[p])} unreadable: {ex.Message}"; o.Dispose(); return null; }
            using (mod as IDisposable)
            {
                o.PluginName[p] = mod.ModKey.FileName.String;
                lightness.Add((mod.ModKey, BosHook.IsLight(mod)));
                o.Idx.Ingest(p, mod);
            }
            cap("plugin " + o.PluginName[p]);
        }
        if (bos)
        {
            o.Idx.Bos!.SetLoadOrder(lightness);
            o.Bos = BosHook.Load(assets.LooseRoots, o.Idx.Bos);
        }
        // The plugin overlays are released; compact the heap so the working-set cap reads what is live, not what was committed.
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        cap("after indexing");
        o.IndexSeconds = sw.Elapsed.TotalSeconds - t0;
        Console.WriteLine($"indexed {o.Idx.History.Count:N0} refs over {paths.Count} plugins ({sw.Elapsed.TotalSeconds:0.0}s)");
        return o;
    }

    /// <summary>Every light base (LIGH record) any plugin of the order carries: the population's "this scene holds a light".</summary>
    public HashSet<FormKey> LightBases()
    {
        var o = new HashSet<FormKey>();
        foreach (var path in Cfg.OrderedPaths)
        {
            using var mod0 = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE) as IDisposable;
            foreach (var lg in ((ISkyrimModGetter)mod0!).EnumerateMajorRecords<ILightGetter>()) o.Add(lg.FormKey);
        }
        return o;
    }

    // ---- pass 2: every base the selected refs name (type, editor id, light data) and the refs' own light overrides ----
    public void ReadBases(HashSet<FormKey> scopeRefs, Stopwatch sw)
    {
        var t0 = sw.Elapsed.TotalSeconds;
        var paths = Cfg.OrderedPaths;
        var wantBases = scopeRefs.Where(Idx.RefBase.ContainsKey).Select(r => Idx.RefBase[r]).ToHashSet();
        _run.Stage("bases", "reading bases and light overrides", paths.Count, "plugins");
        for (int p = 0; p < paths.Count; p++)
        {
            _run.Advance();
            using var mod0 = SkyrimMod.CreateFromBinaryOverlay(paths[p], SkyrimRelease.SkyrimSE) as IDisposable;
            var mod = (ISkyrimModGetter)mod0!;
            foreach (var rec in mod.EnumerateMajorRecords())
            {
                if (rec is IPlacedObjectGetter po)
                {
                    if (scopeRefs.Contains(po.FormKey)) RefRadius[po.FormKey] = po.Radius;   // winner wins: the last record of the ref
                    continue;
                }
                if (!wantBases.Contains(rec.FormKey)) continue;
                BaseType[rec.FormKey] = (rec.GetType().Name.Replace("BinaryOverlay", ""), rec.EditorID);
                if (rec is ILightGetter lg)
                {
                    var f = lg.Flags;
                    LightBase[rec.FormKey] = new LightBase(lg.Radius, f.ToString().Split(", ", StringSplitOptions.RemoveEmptyEntries),
                        f.HasFlag(Light.Flag.ShadowSpotlight) || f.HasFlag(Light.Flag.ShadowHemisphere) || f.HasFlag(Light.Flag.ShadowOmnidirectional),
                        f.HasFlag(Light.Flag.PortalStrict) || lg.MajorFlags.HasFlag(Light.MajorFlag.PortalStrict),
                        f.HasFlag(Light.Flag.OffByDefault), f.HasFlag(Light.Flag.SpotLight) || f.HasFlag(Light.Flag.ShadowSpotlight),
                        lg.FadeValue, new[] { (int)lg.Color.R, (int)lg.Color.G, (int)lg.Color.B }, lg.FOV);
                }
                else LightBase.Remove(rec.FormKey);   // a later winner of another type
            }
            _cap("bases " + Path.GetFileName(paths[p]));
        }
        BasesSeconds = sw.Elapsed.TotalSeconds - t0;
    }

    // Light Limit Fix in the evaluated instance (decides which C4 reading the fold uses): the standalone SKSE plugin, or Community
    // Shaders with its Light Limit Fix feature, resolved through this order's virtual file system (enabled mods + game Data).
    public (object Json, bool Installed) Llf()
    {
        string[] probes = { "SKSE/Plugins/LightLimitFix.dll", "SKSE/Plugins/CommunityShaders.dll", "Shaders/Features/LightLimitFix.ini" };
        var hits = probes.Select(p => (p, h: Assets.Resolve(p))).Where(x => x.h.Exists).Select(x => new { path = x.p, provider = x.h.Winner?.Source }).ToList();
        bool standalone = hits.Any(h => h.path.EndsWith("LightLimitFix.dll", StringComparison.OrdinalIgnoreCase));
        bool cs = hits.Any(h => h.path.EndsWith("CommunityShaders.dll", StringComparison.OrdinalIgnoreCase))
                  && hits.Any(h => h.path.EndsWith("LightLimitFix.ini", StringComparison.OrdinalIgnoreCase));
        return (new { installed = standalone || cs, how = standalone ? "SKSE/Plugins/LightLimitFix.dll" : cs ? "Community Shaders + Shaders/Features/LightLimitFix.ini" : "none of " + string.Join(", ", probes),
                      found = hits }, standalone || cs);
    }

    /// <summary>Does a Base Object Swapper file name this reference or its base (any swap or property line, whatever its filters)?</summary>
    public bool BosNames(FormKey r)
    {
        if (Bos is not { } b) return false;
        var bk = Idx.RefBase.GetValueOrDefault(r);
        return b.SwapRefs.ContainsKey(r) || b.Properties.ContainsKey(r)
               || (!bk.IsNull && (b.SwapForms.ContainsKey(bk) || b.SwapFormsConditional.ContainsKey(bk) || b.Properties.ContainsKey(bk) || b.PropertiesConditional.ContainsKey(bk)));
    }

    string MeshRel(string mesh) => mesh.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? mesh : Path.Combine("meshes", mesh);

    (LightModel? Model, string? Why) Model(FormKey b)
    {
        if (!Idx.BaseMesh.TryGetValue(b, out var mesh)) return (null, "no model");
        if (_models.TryGetValue(mesh, out var hit)) return hit;
        var rel = MeshRel(mesh);
        var res = Assets.ResolveForPlacement(rel);
        if (res.Sources.Count == 0) return _models[mesh] = (null, "missing_asset: " + rel);
        return _models[mesh] = _cache.Get<(LightModel?, string?)>("model", res.Sources[0], src =>
        {
            var (bytes, err) = AssetResolver.ReadPlacementSource(src);
            if (bytes is null) return (null, "asset read failed: " + err);
            try
            {
                var m = LightReach.ReadModel(bytes);
                return m.Status == "refused" ? (null, m.Reason) : (m, null);
            }
            catch (Exception ex) { return (null, "read threw: " + ex.Message); }
        }, r => ModelBytes(r.Item1));
    }

    /// <summary>About how many bytes a model reading holds (its placed-soup source data: vertices, indices, tags and shape map).</summary>
    static long ModelBytes(LightModel? m) => m?.Soup is not { } s ? 0
        : (long)s.Vertices.Length * 12 + (long)s.Indices.Length * 4 + (m.Tags?.Length ?? 0) * 8L + (m.ShapeOfTriangle?.Length ?? 0) * 4L + m.Shapes.Count * 96L;

    // emitting parts (lane A, 2026-10-13): read per mesh, separately from the render model (a particle-only mesh still has emitters)
    EmitterModel? Emitters(FormKey b)
    {
        if (!Idx.BaseMesh.TryGetValue(b, out var mesh)) return null;
        if (_emitters.TryGetValue(mesh, out var hit)) return hit;
        var rel = MeshRel(mesh);
        var res = Assets.ResolveForPlacement(rel);
        if (res.Sources.Count == 0) return _emitters[mesh] = new EmitterModel("refused", "missing_asset: " + rel, Array.Empty<EmittingPart>());
        return _emitters[mesh] = _cache.Get<EmitterModel>("emitters", res.Sources[0], src =>
        {
            var (bytes, err) = AssetResolver.ReadPlacementSource(src);
            if (bytes is null) return new EmitterModel("refused", "asset read failed: " + err, Array.Empty<EmittingPart>());
            try { return LightReach.ReadEmitters(bytes); }
            catch (Exception ex) { return new EmitterModel("refused", "read threw: " + ex.Message, Array.Empty<EmittingPart>()); }
        }, e => e.Parts.Count * 64L);
    }

    static double[] V(Vec3 v) => new double[] { v.X, v.Y, v.Z };
    static double[] W(Vector3 v) => new double[] { v.X, v.Y, v.Z };
    static Vector3 P(Vec3 v) => new(v.X, v.Y, v.Z);

    string? Plugin(int p) => p >= 0 && p < PluginName.Length ? PluginName[p] : null;
    IReadOnlyList<string> TouchersOf(FormKey r) =>
        Idx.Touchers.TryGetValue(r, out var t) ? t.Where(p => p >= 0 && p < PluginName.Length).Select(p => PluginName[p]).ToList() : Array.Empty<string>();

    /// <summary>One scene: its references, lights and (with geometry) the readings, as the housecarl.light/1 cell object, and as the check's
    /// reading. <paramref name="subject"/> says which references are the scene's subjects (all, for an interior); <paramref name="terrain"/>
    /// the exterior cells whose ground is a surface (worldspace and grid).</summary>
    public (object Cell, LightCheckReading Reading) ReadScene(string cedid, FormKey ck, List<FormKey> refs, Func<FormKey, bool> subject, bool geometry,
                                                              FormKey terrainWs, IEnumerable<(int X, int Y)> terrain, bool llf, ref int pointInSolidRows)
    {
        var reading = new LightCheckReading(PluginName) { LlfInstalled = llf };
        var refRows = new List<object>();
        var placed = new List<(FormKey Key, Snap Pose, FormKey Base, bool On)>();
        foreach (var r in refs.OrderBy(r => r.ToString(), StringComparer.Ordinal))
        {
            var pose = Idx.WinningGeometryPose(r);
            if (pose is null) continue;
            var b = Idx.RefBase.GetValueOrDefault(r);
            var en = Arms.Winner(r);
            bool deleted = Idx.IsDeletedNow(r) || pose.Value.Deleted;
            bool on = !deleted && en.Resolved && en.Enabled;
            var (bt, bed) = BaseType.GetValueOrDefault(b, ("?", null));
            List<object>? emitters = null; string? emitterStatus = null;
            List<LightCheckEmitter>? checkEmitters = null;
            if (geometry && on && LightReach.BaseDrawn(Idx.BaseIsMarker.Contains(b)) && Emitters(b) is { } em)
            {
                if (em.Status != "read") emitterStatus = em.Status + ": " + em.Reason;
                else if (em.Parts.Count > 0)
                {
                    checkEmitters = new();
                    emitters = em.Parts.Select(pt =>
                    {
                        var (o, ex, ey, ez) = LightReach.PlaceBox(pt.Min, pt.Max, pose.Value.Pos, pose.Value.Rot, pose.Value.Scale);
                        checkEmitters.Add(new LightCheckEmitter(pt.Part, pt.Kind, pt.Value, o, ex, ey, ez));
                        return (object)new { part = pt.Part, kind = pt.Kind, value = pt.Value, box = new[] { W(o), W(ex), W(ey), W(ez) } };
                    }).ToList();
                }
            }
            var mesh = Idx.BaseMesh.GetValueOrDefault(b);
            var responsible = Plugin(pose.Value.Plugin);
            Vec3? obnd = Idx.BaseObnd.TryGetValue(b, out var ob) ? ob : null;
            refRows.Add(new { key = r.ToString(), @base = b.IsNull ? null : b.ToString(), type = bt, edid = bed, mesh,
                              emitters, emitterStatus,
                              pos = V(pose.Value.Pos), rot = V(pose.Value.Rot), scale = pose.Value.Scale, enabled = en.Enabled, resolved = en.Resolved, viaParent = en.ViaParent,
                              deleted, responsible,
                              obnd = obnd is { } o2 ? V(o2) : null });
            IReadOnlyList<string> parentTouchers = Array.Empty<string>();
            if (Idx.EnableChanges.TryGetValue(r, out var ec) && ec.Count > 0 && ec[^1].Parent is { } parent) parentTouchers = TouchersOf(parent);
            reading.Add(new LightCheckRef(r.ToString(), bt, bed, !string.IsNullOrEmpty(mesh), P(pose.Value.Pos), pose.Value.Scale, on)
            {
                Obnd = obnd is { } o3 ? P(o3) : null, Deleted = deleted, Emitters = checkEmitters, EmitterStatus = emitterStatus, Responsible = responsible,
                Touchers = TouchersOf(r), ParentTouchers = parentTouchers, Subject = subject(r),
            });
            placed.Add((r, pose.Value, b, on));
        }
        var lights = placed.Where(x => LightBase.ContainsKey(x.Base)).ToList();
        var lightRow = new Dictionary<FormKey, Dictionary<string, object?>>();
        foreach (var l in lights)
        {
            var lb = LightBase[l.Base];
            float? rr = RefRadius.GetValueOrDefault(l.Key);
            lightRow[l.Key] = new Dictionary<string, object?>
            {
                ["key"] = l.Key.ToString(), ["base"] = l.Base.ToString(), ["edid"] = BaseType.GetValueOrDefault(l.Base).Edid, ["enabled"] = l.On,
                ["pos"] = V(l.Pose.Pos), ["radiusBase"] = lb.Radius, ["radiusRef"] = rr, ["radius"] = LightReach.EffectiveRadius(lb.Radius, rr),
                ["flags"] = lb.Flags, ["shadow"] = lb.Shadow, ["strict"] = lb.Strict, ["offByDefault"] = lb.OffByDefault, ["spot"] = lb.Spot,
                ["fade"] = lb.Fade, ["color"] = lb.Color, ["fov"] = lb.Fov,
            };
        }
        var checkLight = lights.ToDictionary(l => l.Key, l => new LightCheckLight(l.Key.ToString(), BaseType.GetValueOrDefault(l.Base).Edid, l.On, P(l.Pose.Pos),
                                                                                   (float)lightRow[l.Key]["radius"]!, LightBase[l.Base].Shadow) { Subject = subject(l.Key) });
        object? geom = null;
        var shapeRows = new List<object>();
        if (geometry)
        {
            var parts = new List<(TriangleSoup, string[]?, int)>();
            var refused = new List<(FormKey Key, string Why, Vector3 At, float Reach)>();
            var bounds = new List<(int Owner, Aabb Box, TriangleSoup World, string[]? Tags, Snap Pose, TriangleSoup ModelSoup)>();
            var shapes = new List<(int Owner, LightShape Shape, Vector3 Center, float Radius, Vector3[]? Box)>();
            var on = placed.Where(x => x.On).ToList();
            for (int o = 0; o < on.Count; o++)
            {
                var x = on[o];
                if (!Idx.BaseMesh.ContainsKey(x.Base)) continue;   // model-less (lights, markers): nothing to draw
                if (!LightReach.BaseDrawn(Idx.BaseIsMarker.Contains(x.Base))) continue;   // an Is-Marker base: never drawn
                var (m, why) = Model(x.Base);
                if (m is null || m.Soup is null)
                {
                    var ext = Idx.BaseObnd.TryGetValue(x.Base, out var ob) ? new Vector3(ob.X, ob.Y, ob.Z).Length() / 2 * x.Pose.Scale : 0f;
                    refused.Add((x.Key, why ?? m?.Reason ?? "no triangles", new Vector3(x.Pose.Pos.X, x.Pose.Pos.Y, x.Pose.Pos.Z), ext));
                    continue;
                }
                var world = SpatialOverlap.TransformSoup(m.Soup, x.Pose.Pos, x.Pose.Rot, x.Pose.Scale);
                parts.Add((world, m.Tags, o));
                bounds.Add((o, SpatialOverlap.SoupBounds(world), world, m.Tags, x.Pose, m.Soup));
                foreach (var s in m.Shapes.Where(s => s.Lit && s.ModelRadius > 0))
                {
                    var c = SpatialOverlap.TransformSoup(new TriangleSoup(new[] { s.ModelCenter }, Array.Empty<int>()), x.Pose.Pos, x.Pose.Rot, x.Pose.Scale).Vertices[0];
                    Vector3[]? box = null;
                    if (s.BoxMin is Vector3 mn && s.BoxMax is Vector3 mx)
                    {
                        var w = SpatialOverlap.TransformSoup(new TriangleSoup(new[] { mn, new Vector3(mx.X, mn.Y, mn.Z), new Vector3(mn.X, mx.Y, mn.Z), new Vector3(mn.X, mn.Y, mx.Z) },
                                                                              Array.Empty<int>()), x.Pose.Pos, x.Pose.Rot, x.Pose.Scale).Vertices;
                        box = new[] { w[0], w[1] - w[0], w[2] - w[0], w[3] - w[0] };
                    }
                    shapes.Add((o, s, c, s.ModelRadius * x.Pose.Scale, box));
                }
                if (m.Status == "partial") refused.Add((x.Key, "partial: " + string.Join("; ", m.Labels), new Vector3(x.Pose.Pos.X, x.Pose.Pos.Y, x.Pose.Pos.Z), 0f));
            }
            // the ground of an exterior scene (the check's finding 4): a surface owned by "LAND:<worldspace>@x,y", never a counted shape
            var groundKeys = new List<string>();
            foreach (var (gx, gy) in terrain)
            {
                if (Idx.Land.TryGetValue((terrainWs, gx, gy), out var h))
                {
                    parts.Add((LightCheckSceneIndex.TerrainSoup(gx, gy, h), null, on.Count + groundKeys.Count));
                    groundKeys.Add($"LAND:{terrainWs}@{gx},{gy}");
                }
                else if (Idx.LandUnreadable.TryGetValue((terrainWs, gx, gy), out var why))
                    reading.SurfaceGaps.Add($"the landscape of cell {gx}, {gy} could not be read: {why}");
            }
            string OwnerKey(int o) => o < on.Count ? on[o].Key.ToString() : groundKeys[o - on.Count];
            var scene = new LightScene(parts);
            geom = new { triangles = scene.Triangles, degenerateSkipped = scene.DegenerateSkipped, nonOpaqueSkipped = scene.NonOpaqueSkipped,
                         unreadable = refused.Select(r => new { key = r.Key.ToString(), reason = r.Why }).ToList() };
            var litLights = lights.Where(l => l.On).ToList();
            foreach (var l in litLights)
            {
                var row = lightRow[l.Key];
                var at = new Vector3(l.Pose.Pos.X, l.Pose.Pos.Y, l.Pose.Pos.Z);
                float r = (float)row["radius"]!;
                var facing = scene.Facing(at, r);
                var leak = scene.Leak(at, r);
                row["facing"] = new { facing.Rays, facing.Front, facing.Back, facing.None };
                row["leak"] = new { leak.Lit, leak.Leak, leak.Samples, leak.ReceiverTriangles,
                                    leakByRef = leak.LeakByOwner.ToDictionary(kv => OwnerKey(kv.Key), kv => kv.Value),
                                    litByRef = leak.LitByOwner.ToDictionary(kv => OwnerKey(kv.Key), kv => kv.Value) };
                var pis = new List<object>();
                foreach (var bx in bounds)
                {
                    if (at.X < bx.Box.Min.X || at.Y < bx.Box.Min.Y || at.Z < bx.Box.Min.Z || at.X > bx.Box.Max.X || at.Y > bx.Box.Max.Y || at.Z > bx.Box.Max.Z) continue;
                    try
                    {
                        var ar = MeshAnchor.TestPlaced(bx.ModelSoup, bx.Tags, bx.Pose.Pos, bx.Pose.Rot, bx.Pose.Scale, at, new BodyExtent(0, 10, 1));
                        pis.Add(new { @ref = on[bx.Owner].Key.ToString(), point = ar.Point, reason = ar.PointReason });
                    }
                    catch (Exception ex) { pis.Add(new { @ref = on[bx.Owner].Key.ToString(), point = "refused", reason = ex.Message }); }
                }
                row["pointInSolid"] = pis;
                pointInSolidRows += pis.Count;
                var inc = refused.Where(u => Vector3.Distance(u.At, at) <= r + u.Reach).Select(u => u.Key.ToString()).ToList();
                if (inc.Count > 0) row["incomplete"] = inc;
                checkLight[l.Key] = checkLight[l.Key] with
                {
                    Facing = facing, Lit = leak.Lit, Leak = leak.Leak, Incomplete = inc,
                    BackByRef = facing.BackByOwner.ToDictionary(kv => OwnerKey(kv.Key), kv => kv.Value),
                    LitByRef = leak.LitByOwner.ToDictionary(kv => OwnerKey(kv.Key), kv => kv.Value),
                };
            }
            foreach (var s in shapes)
            {
                // assignment (sixth fold, rule a): a light counts toward a shape's limit only if its radius reaches the shape's placed
                // box; the bound-sphere count is printed beside it. A shape with no vertex box falls back to the sphere, labelled.
                var sphere = litLights.Where(l => LightReach.MeetsShape(new Vector3(l.Pose.Pos.X, l.Pose.Pos.Y, l.Pose.Pos.Z), (float)lightRow[l.Key]["radius"]!, s.Center, s.Radius)).ToList();
                if (sphere.Count == 0) continue;
                var met = s.Box is { } bx4
                    ? sphere.Where(l => LightReach.MeetsShapeBox(new Vector3(l.Pose.Pos.X, l.Pose.Pos.Y, l.Pose.Pos.Z), (float)lightRow[l.Key]["radius"]!, bx4[0], bx4[1], bx4[2], bx4[3])).ToList()
                    : sphere;
                if (met.Count == 0) continue;
                shapeRows.Add(new { @ref = on[s.Owner].Key.ToString(), shape = s.Shape.Index, name = s.Shape.Name, center = W(s.Center), radius = s.Radius, boundSource = s.Shape.BoundSource,
                                    assign = s.Box is null ? "sphere (no vertex box)" : "box", countSphere = sphere.Count,
                                    count = met.Count, shadow = met.Count(l => LightBase[l.Base].Shadow), strict = met.Count(l => LightBase[l.Base].Strict),
                                    lights = met.Select(l => l.Key.ToString()).ToList() });
                reading.Add(new LightCheckShape(on[s.Owner].Key.ToString(), s.Shape.Index, s.Shape.Name, met.Count, met.Count(l => LightBase[l.Base].Shadow),
                                                met.Count(l => LightBase[l.Base].Strict), met.Select(l => l.Key.ToString()).ToList()) { Subject = subject(on[s.Owner].Key) });
            }
        }
        foreach (var cl in checkLight.Values) reading.Add(cl);
        _cap("cell " + cedid);
        return (new { edid = cedid, cell = ck.ToString(), refs = refRows, lights = lightRow.Values.ToList(), geometry = geom, shapes = shapeRows }, reading);
    }
}

sealed record LightBase(uint Radius, string[] Flags, bool Shadow, bool Strict, bool OffByDefault, bool Spot, float Fade, int[] Color, float Fov);
