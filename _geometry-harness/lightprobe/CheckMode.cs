using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using HarnessShared;
using HousecarlCore;
using HousecarlCore.Progress;
using Mutagen.Bethesda.Plugins;
using SkyWright.Engine;

// lightprobe --check (the lighting check, 2026-11-26, Gate B; run folder C:/hc-runs/2026-11-26-lighting-check): the lighting clashes only
// the combination of the selection and the rest makes, from the load order alone.
//   lightprobe --check --dependencies D.json --selection S.json --rest R.json --combined C.json --json OUT
//              [--dump-arrangements DIR] [--scenes-file FILE] [--max-mb N] [--instance DIR] [--game DIR] [--profile NAME]
// The four order files are the arms runcontrol writes (one owner of the arrangement lists): the dependencies, the selection over them,
// the rest over them, and the combination. Which plugin is on which side is read from them (LightCheckSceneIndex.Sides). Each arrangement
// is read as itself (its own plugins, its own asset view, its own enable chain) by the code the single-order mode uses (OrderReading),
// one after another, sharing one model cache keyed by the resolved source. The scenes are the ones both sides touch in the combination
// (LightCheckSceneIndex.TwoSide), or the ones a --scenes-file names (interior editor ids, or WORLDSPACE-FORMKEY@x,y). Scenes in which a
// Base Object Swapper file names a reference are not judged (it is not applied). --dump-arrangements writes each arrangement's readings
// as housecarl.light/1 (the single-order mode's schema) for comparing against separate runs. Output: skywright.light-check/1.
static class CheckMode
{
    static readonly string[] Roles = { "combined", "dependencies", "selection", "rest" };
    /// <summary>The class of a row that stands for a whole scene not judged (Base Object Swapper); not one of the six lighting classes.</summary>
    public const string SceneClass = "scene-not-judged";
    /// <summary>Below this much memory the runtime may use, the model cache is emptied before each arrangement is indexed.</summary>
    const long LeanBelow = 6L << 30;

    /// <summary>Run the check; the arguments are parsed by Program.cs (one parser for every mode).</summary>
    public static int Run(IReadOnlyDictionary<string, string> orders, string? jsonPath, string? dumpDir, string? scenesFile, long maxMb,
                          string instance, string gamePath, string profileName, Run run)
    {
        long peakPrivate = 0;
        if (Roles.FirstOrDefault(r => !orders.ContainsKey(r)) is { } missing)
        { Console.Error.WriteLine($"--check needs all four arrangements and --{missing} is missing: pass the order files runcontrol writes (--dependencies, --selection, --rest, --combined)."); return 2; }
        if (jsonPath is null) { Console.Error.WriteLine("--check needs --json OUT, the file the lighting clashes are written to."); return 2; }
        instance = HarnessTarget.Require(instance, "MO2 instance directory", "instance", "--instance");
        gamePath = HarnessTarget.Require(gamePath, "game directory", "game", "--game");
        profileName = HarnessTarget.Require(profileName, "MO2 profile name", "profile", "--profile");
        string profileDir = Path.Combine(instance, "profiles", profileName), modsDir = Path.Combine(instance, "mods"),
               overwriteDir = Path.Combine(instance, "overwrite"), dataDir = Path.Combine(gamePath, "Data");
        var sw = Stopwatch.StartNew();
        void Cap(string where)
        {
            var pr = Process.GetCurrentProcess();
            long priv = pr.PrivateMemorySize64 / 1048576, ws = pr.WorkingSet64 / 1048576;
            peakPrivate = Math.Max(peakPrivate, priv);
            if (maxMb > 0 && priv > maxMb) { Console.Error.WriteLine($"memory cap: private bytes {priv} MB (working set {ws} MB) > --max-mb {maxMb} at {where}; stopped"); Environment.Exit(3); }
        }

        // ---- the four configurations and the sides, read from them
        var cfgs = new Dictionary<string, ResolvedConfiguration>();
        foreach (var role in Roles)
        {
            var cfg = HarnessOrderCli.Resolve(profileName, profileDir, modsDir, dataDir, overwriteDir, gamePath, orders[role], null, null);
            if (cfg.Refused) { Console.Error.WriteLine($"The {role} arrangement ({orders[role]}) was refused: {cfg.RefusedReason}"); return 2; }
            cfgs[role] = cfg;
        }
        List<string> Names(string role) => cfgs[role].OrderedPaths.Select(p => Path.GetFileName(p)).ToList();
        var modByPlugin = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cfg in cfgs.Values)
        {
            var mods = cfg.Providers.Where(p => p.Kind == "mod").Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var p in cfg.Plugins) if (mods.Contains(p.Provider)) modByPlugin[p.Name] = p.Provider;
        }
        var sides = LightCheckSceneIndex.Sides(Names("dependencies"), Names("selection"), Names("rest"), Names("combined"), modByPlugin, out var sideRefusal);
        if (sides is null) { Console.Error.WriteLine(sideRefusal); return 2; }
        Console.WriteLine($"sides: selection {string.Join(", ", sides.PluginsOn(LightSide.Selection))}; rest {string.Join(", ", sides.PluginsOn(LightSide.Rest))}");

        var cache = new LightMeshCache();
        long usable = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        bool lean = usable < LeanBelow;
        Console.WriteLine($"memory the runtime may use: {usable >> 20:N0} MB; model cache budget {cache.Budget >> 20:N0} MB; "
                        + (lean ? $"lean (under {LeanBelow >> 20:N0} MB): models are not kept between arrangements" : "models are shared across the arrangements"));
        var readings = Roles.ToDictionary(r => r, _ => new Dictionary<string, LightCheckReading>());
        var cellOf = new Dictionary<string, string>(StringComparer.Ordinal);   // the combined arrangement's cell of each scene member
        var scenes = new List<LightCheckSceneKey>();
        var display = new Dictionary<string, string>();
        var notJudged = new Dictionary<string, string>();
        int oneSide = 0, noLight = 0;
        object? llfCombined = null;
        var timing = new List<object>();
        var jsonOpts = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                                                   NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        if (dumpDir is not null) Directory.CreateDirectory(dumpDir);

        // Each arrangement is read in its own frame, so nothing of its index stays reachable once it returns (measured 2026-11-26: read in
        // one long frame, the first arrangement's index outlived it and a 2.5 GiB heap ran out of memory indexing the second).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        int ReadOne(string role)
        {
            double t0 = sw.Elapsed.TotalSeconds;
            // fast when the memory exists, else lean: an arrangement's index takes 2.4-3.5 GB (Gate A), so below LeanBelow of usable
            // memory the models are not kept while the next arrangement is indexed (each is then read again, as separate runs would)
            if (lean && role != Roles[0]) cache.Clear();
            long reads0 = cache.Reads, hits0 = cache.Hits;
            using (var rd = OrderReading.Open(cfgs[role], modsDir, dataDir, overwriteDir, run, Cap, sw, cache, trackLand: true, bos: true, out var refusal))
            {
                if (rd is null) { Console.Error.WriteLine($"The {role} arrangement could not be read: {refusal}"); return 2; }
                double tLights = sw.Elapsed.TotalSeconds;
                var lightBases = role == "combined" ? rd.LightBases() : null;
                tLights = sw.Elapsed.TotalSeconds - tLights;
                var keyOf = new Dictionary<string, FormKey>(StringComparer.Ordinal);
                var index = new LightCheckSceneIndex(Placements(rd, lightBases, keyOf));
                if (role == "combined")
                {
                    if (scenesFile is not null)
                    {
                        var named = NamedScenes(rd, File.ReadAllLines(scenesFile), out var why);
                        if (named is null) { Console.Error.WriteLine(why); return 2; }
                        scenes.AddRange(named);
                    }
                    else scenes.AddRange(index.TwoSide(sides, out oneSide, out noLight));
                    foreach (var s in scenes)
                        display[s.Id] = s.Exterior ? $"{s.Space} cell {s.X}, {s.Y}"
                            : rd.Idx.CellName.FirstOrDefault(kv => kv.Key.ToString() == s.Space).Value.Edid ?? s.Space;
                    Console.WriteLine($"scenes: {scenes.Count} read ({scenes.Count(s => !s.Exterior)} interior, {scenes.Count(s => s.Exterior)} exterior); "
                                    + $"{oneSide} touched by one side only and {noLight} with no light, not read");
                }
                var members = scenes.ToDictionary(s => s.Id, s => index.Members(s));
                if (role == "combined")
                    foreach (var m in members.Values.SelectMany(x => x))
                        if (rd.Idx.RefCell.TryGetValue(keyOf[m.P.Key], out var rc)) cellOf[m.P.Key] = rc.Cell.ToString();
                var scope = members.Values.SelectMany(m => m.Select(x => keyOf[x.P.Key])).ToHashSet();
                rd.ReadBases(scope, sw);
                var (llfJson, llfOn) = rd.Llf();
                if (role == "combined") llfCombined = llfJson;
                double tScenes = sw.Elapsed.TotalSeconds;
                var dumpCells = new List<object>();
                int pis = 0;
                run.Stage("cells", $"measuring scenes ({role})", scenes.Count, "scenes");
                int nth = 0;
                foreach (var s in scenes)
                {
                    run.Advance();
                    // progress per scene (Gate B review finding 3): exterior checks read a hundred or more scenes per arrangement
                    Console.WriteLine($"scene {++nth} of {scenes.Count} ({role}): {display[s.Id]} at {sw.Elapsed.TotalSeconds:0.0}s");
                    var mem = members[s.Id];
                    if (!notJudged.ContainsKey(s.Id) && BosRefusal(mem.Select(m => keyOf[m.P.Key]), rd.BosNames, role) is { } bosWhy)
                        notJudged[s.Id] = bosWhy;
                    var subjects = mem.Where(m => m.Subject).Select(m => m.P.Key).ToHashSet(StringComparer.Ordinal);
                    FormKey cellKey = s.Exterior ? FormKey.Null : keyOf.GetValueOrDefault(s.Space, FormKey.Null);
                    FormKey wsKey = s.Exterior ? keyOf.GetValueOrDefault(s.Space, FormKey.Null) : FormKey.Null;
                    var (cellJson, reading) = rd.ReadScene(display[s.Id], cellKey, mem.Select(m => keyOf[m.P.Key]).ToList(), k => subjects.Contains(k.ToString()), true,
                                                           wsKey, LightCheckSceneIndex.TerrainGrids(s), llfOn, ref pis);
                    readings[role][s.Id] = reading;
                    if (dumpDir is not null) dumpCells.Add(cellJson);
                }
                tScenes = sw.Elapsed.TotalSeconds - tScenes;
                if (dumpDir is not null)
                    File.WriteAllText(Path.Combine(dumpDir, role + ".json"), JsonSerializer.Serialize(new { identity = LightProbeIdentity.Of(rd.Cfg, rd.PluginName, llfJson, pis), geometry = true, cells = dumpCells }, jsonOpts));
                timing.Add(new { role, plugins = rd.PluginName.Length, indexSeconds = Math.Round(rd.IndexSeconds, 2), lightBasesSeconds = Math.Round(tLights, 2),
                                 basesSeconds = Math.Round(rd.BasesSeconds, 2), scenesSeconds = Math.Round(tScenes, 2), modelReads = cache.Reads - reads0, modelCacheHits = cache.Hits - hits0,
                                 totalSeconds = Math.Round(sw.Elapsed.TotalSeconds - t0, 2) });
            }
            return 0;
        }
        foreach (var role in Roles)
        {
            Console.WriteLine($"---- {role}: {orders[role]} ({sw.Elapsed.TotalSeconds:0.0}s)");
            if (ReadOne(role) is int rc and not 0) return rc;
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            Cap("after " + role);
            Console.WriteLine($"after {role}: managed heap {GC.GetTotalMemory(false) >> 20:N0} MB, private bytes {Process.GetCurrentProcess().PrivateMemorySize64 >> 20:N0} MB");
        }

        // ---- the rules, per scene
        var rows = new List<object>();
        var counts = LightCheck.Classes.ToDictionary(k => k, _ => new LightCheckCount());
        var sceneRows = new List<object>();
        // a scene not judged is a row a modder sees, not only a scenes[] entry (Gate B review finding 1)
        rows.AddRange(RefusedSceneRows(scenes, notJudged, display));
        LightCheckReading Of(string role, string id) => readings[role].TryGetValue(id, out var x) ? x : new LightCheckReading(Names(role));
        foreach (var s in scenes)
        {
            var name = display[s.Id];
            if (notJudged.TryGetValue(s.Id, out var why))
            {
                sceneRows.Add(new { scene = name, sceneId = s.Id, kind = s.Exterior ? "exterior" : "interior", judged = false, reason = why });
                continue;
            }
            var res = LightCheck.Evaluate(name, s.Exterior, Of("dependencies", s.Id), Of("selection", s.Id), Of("rest", s.Id), Of("combined", s.Id), sides);
            foreach (var (k, v) in res.Counts) counts[k].Add(v);
            sceneRows.Add(new { scene = name, sceneId = s.Id, kind = s.Exterior ? "exterior" : "interior", judged = true, rows = res.Rows.Count,
                                lights = Of("combined", s.Id).Lights.Count, gaps = Of("combined", s.Id).SurfaceGaps });
            rows.AddRange(res.Rows.Select(r => (object)new
            {
                @class = r.Class, title = LightCheck.ClassTitles[r.Class], verdict = r.Verdict, scene = r.Scene, sceneId = s.Id, kind = s.Exterior ? "exterior" : "interior",
                space = s.Space, x = s.Exterior ? s.X : (int?)null, y = s.Exterior ? s.Y : (int?)null,
                cell = cellOf.GetValueOrDefault(r.Subject) ?? (s.Exterior ? null : s.Space),
                subject = r.Subject, subjectEdid = r.SubjectEdid, fixture = r.Fixture, fixtureEdid = r.FixtureEdid, shape = r.ShapeIndex, shapeName = r.ShapeName,
                subjectMod = r.SubjectMod, otherMods = r.OtherMods, summary = r.Summary, reason = r.Reason, at = new[] { r.At.X, r.At.Y, r.At.Z },
                fixtureAt = r.FixtureAt is { } fa ? new[] { fa.X, fa.Y, fa.Z } : null, shadow = r.Shadow,
                readings = r.Readings,
            }));
        }
        var outDoc = new
        {
            identity = new
            {
                schema = "skywright.light-check/1", policy = LightCheck.PolicyVersion, lightPolicy = LightReach.PolicyVersion, hostRadius = LightCheck.HostRadius,
                emitThreshold = LightReach.EmitThreshold, implausibleRadius = LightCheck.ImplausibleRadius, exteriorReach = LightCheck.ExteriorReach,
                bos = "not applied: a scene in which a Base Object Swapper file names a reference is not judged", population = "load-time", state = "baseline",
                sides = new { selection = sides.PluginsOn(LightSide.Selection).ToList(), rest = sides.PluginsOn(LightSide.Rest).ToList() },
                mods = modByPlugin.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(kv => kv.Key, kv => kv.Value),
                arrangements = Roles.ToDictionary(r => r, r => (object)new { order = orders[r], configuration = cfgs[r].Identity("load-time", "baseline").Short, plugins = Names(r) }),
                llf = llfCombined, scenesFile,
            },
            classes = LightCheck.Classes.Select(c => new { id = c, title = LightCheck.ClassTitles[c] }).ToList(),
            counts = LightCheck.Classes.ToDictionary(c => c, c => new { subjects = counts[c].Subjects, listed = counts[c].Listed, notThePair = counts[c].NotThePair,
                                                                        noClash = counts[c].NoClash, notJudged = counts[c].NotJudged, alsoLightsNothing = counts[c].AlsoLightsNothing }),
            // the totals runcontrol reads through the registry's adapter.result (every class, admitted or held: admission is the app's)
            summary = new
            {
                listed = LightCheck.Classes.Where(c => c != LightCheck.OverLightLimit).Sum(c => counts[c].Listed),
                cannotTell = counts[LightCheck.OverLightLimit].Listed,
                judged = LightCheck.Classes.Sum(c => counts[c].Subjects - counts[c].NotJudged),
                notJudged = LightCheck.Classes.Sum(c => counts[c].NotJudged) + notJudged.Count,
                scenesNotJudged = notJudged.Count,
            },
            population = new { scenesRead = scenes.Count, interiors = scenes.Count(s => !s.Exterior), exteriors = scenes.Count(s => s.Exterior),
                               scenesNotJudged = notJudged.Count, oneSideNotRead = oneSide, noLightNotRead = noLight },
            scenes = sceneRows,
            rows,
            cost = new { seconds = Math.Round(sw.Elapsed.TotalSeconds, 2), arrangements = timing, modelReads = cache.Reads, modelCacheHits = cache.Hits, cacheBudgetMb = cache.Budget >> 20, cachePeakMb = cache.PeakBytes >> 20, cacheEmptied = cache.Evictions, lean, usableMemoryMb = usable >> 20,
                         peakWorkingSetMb = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576, largestPrivateMb = peakPrivate },
        };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(outDoc, jsonOpts));
        Cap("end");
        Console.WriteLine($"{rows.Count} rows over {scenes.Count} scenes ({notJudged.Count} not judged) -> {jsonPath}; {sw.Elapsed.TotalSeconds:0.0}s; "
                        + $"model reads {cache.Reads:N0}, cache hits {cache.Hits:N0}; peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:N0} MB");
        return 0;
    }

    /// <summary>One row per scene not judged (Gate B review finding 1), in scene order: class scene-not-judged, its one-sentence reason,
    /// where it opens (an interior's cell; an exterior's worldspace and grid, looking at the centre cell's middle).</summary>
    public static List<Dictionary<string, object?>> RefusedSceneRows(IEnumerable<LightCheckSceneKey> scenes, IReadOnlyDictionary<string, string> notJudged,
                                                                    IReadOnlyDictionary<string, string> display)
    {
        var o = new List<Dictionary<string, object?>>();
        foreach (var s in scenes)
        {
            if (!notJudged.TryGetValue(s.Id, out var why)) continue;
            o.Add(new Dictionary<string, object?>
            {
                ["class"] = SceneClass, ["title"] = "A scene not judged", ["verdict"] = "not judged", ["scene"] = display.GetValueOrDefault(s.Id, s.Id),
                ["sceneId"] = s.Id, ["kind"] = s.Exterior ? "exterior" : "interior", ["space"] = s.Space,
                ["x"] = s.Exterior ? (int?)s.X : null, ["y"] = s.Exterior ? (int?)s.Y : null, ["cell"] = s.Exterior ? null : s.Space,
                ["summary"] = why, ["reason"] = why,
                ["at"] = s.Exterior ? new[] { (s.X + 0.5f) * LightCheckSceneIndex.Cell, (s.Y + 0.5f) * LightCheckSceneIndex.Cell, 0f } : null,
            });
        }
        return o;
    }

    /// <summary>A scene is not judged when a Base Object Swapper file names one of its members or a member's base (decision 7: BOS is
    /// not applied). The one-sentence reason names the first such member; null when no swap file names any.</summary>
    public static string? BosRefusal(IEnumerable<FormKey> members, Func<FormKey, bool> named, string role)
    {
        foreach (var m in members)
            if (named(m))
                return $"Not judged: a Base Object Swapper file changes {m} in this scene ({role} arrangement), and this check does not apply Base Object Swapper; look at the scene in game.";
        return null;
    }

    /// <summary>Every placed reference of an arrangement as the population sees it (touchers and light flags only where asked). keyOf
    /// gains every reference's, interior cell's and worldspace's key string.</summary>
    static List<LightCheckPlacement> Placements(OrderReading rd, HashSet<FormKey>? lightBases, Dictionary<string, FormKey> keyOf)
    {
        var idx = rd.Idx;
        var o = new List<LightCheckPlacement>(idx.RefCell.Count);
        foreach (var (r, (c, ws)) in idx.RefCell)
        {
            if (idx.WinningGeometryPose(r) is not { } pose) continue;
            var key = r.ToString();
            keyOf[key] = r;
            var b = idx.RefBase.GetValueOrDefault(r);
            float scale = pose.Scale == 0 ? 1 : pose.Scale;
            float reach = idx.BaseObnd.TryGetValue(b, out var ob) ? new Vector3(ob.X, ob.Y, ob.Z).Length() / 2 * scale : 0;
            IReadOnlyList<string> touchers = lightBases is null || !idx.Touchers.TryGetValue(r, out var t) ? Array.Empty<string>()
                : t.Where(p => p >= 0 && p < rd.PluginName.Length).Select(p => rd.PluginName[p]).ToList();
            bool light = lightBases?.Contains(b) == true;
            var at = new Vector3(pose.Pos.X, pose.Pos.Y, pose.Pos.Z);
            if (ws.IsNull) keyOf.TryAdd(c.ToString(), c); else keyOf.TryAdd(ws.ToString(), ws);
            o.Add(ws.IsNull ? new LightCheckPlacement(key, c.ToString(), null, at, reach, touchers, light)
                            : new LightCheckPlacement(key, null, ws.ToString(), at, reach, touchers, light));
        }
        return o;
    }

    /// <summary>The scenes a --scenes-file names: interior cells by editor id, exterior cells as WORLDSPACE-FORMKEY@x,y.</summary>
    static List<LightCheckSceneKey>? NamedScenes(OrderReading rd, IEnumerable<string> lines, out string? why)
    {
        why = null;
        var o = new List<LightCheckSceneKey>();
        var byEdid = rd.Idx.CellName.Where(kv => kv.Value.Interior && kv.Value.Edid is not null)
                       .GroupBy(kv => kv.Value.Edid!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            var at = raw.LastIndexOf('@');
            if (at > 0 && raw.Contains(':'))
            {
                var xy = raw[(at + 1)..].Split(',');
                if (xy.Length != 2 || !int.TryParse(xy[0], out var x) || !int.TryParse(xy[1], out var y))
                { why = $"The scenes file names '{raw}', which is not WORLDSPACE-FORMKEY@x,y; write an exterior cell as, for example, 00003C:Skyrim.esm@5,-2."; return null; }
                o.Add(new LightCheckSceneKey(true, raw[..at], x, y));
                continue;
            }
            if (!byEdid.TryGetValue(raw, out var cell)) { why = $"The scenes file names '{raw}', which is not an interior cell of the combined order; check its editor id."; return null; }
            o.Add(new LightCheckSceneKey(false, cell.ToString()));
        }
        return o;
    }
}
