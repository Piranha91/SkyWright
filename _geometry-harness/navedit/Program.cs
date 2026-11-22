using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

// navedit — NAVMESH EDITING from the command line (task 2026-11-16-navmesh-repair; the surface houseCARL drives, chosen
// 2026-11-21: C:/hc-runs/2026-11-21-navgrid/PLAN.md). Two steps, so a caller can judge the plan before anything is written:
//
//   navedit plan  --edits EDITS.json --out PLAN.json [--instance DIR --game DIR --profile NAME]
//       Reads the winning version of each named navmesh on the profile's load order and plans the edits (_shared/NavEdit.cs):
//       per navmesh "planned" with the write ops, or "refused" with why. Nothing is written but PLAN.json. Exit 0 when every
//       navmesh is planned, 1 when any is refused, 2 when the inputs cannot be read.
//   navedit write --plan PLAN.json --out PATCH.esp
//       Writes the planned ops into a NEW patch plugin through houseCARL core's write engine, in this process (no server),
//       forwards each carried cell and worldspace from its winner, and reads the patch back: the navmesh must hold exactly
//       the planned vertices and the grid NavGrid rebuilds from them, and every carried record must equal its winner.
//       Refused when the load order differs from the plan's, or PATCH.esp exists. Exit 0 on a read-back that holds.
//   navedit --selftest
//       Known answers for the edit model on small in-memory navmeshes (tests/test_navedit.py runs it).
//
// EDITS.json: {"edits": [{"navmesh": "0E6430:Skyrim.esm", "move": [{"vertex": 517, "to": [x, y, z]} | {"vertex": 517, "by": [dx, dy, dz]}]}]}
// The profile comes from the flags, then the environment and harness.local.json (_shared/HarnessTarget.cs), as for the
// other probes.

if (args.Length >= 1 && args[0] == "--selftest") return NavEditSelfTest.Run();
if (args.Length < 1 || args[0] is not ("plan" or "write")) { Console.Error.WriteLine("usage: navedit plan --edits EDITS.json --out PLAN.json [--instance DIR --game DIR --profile NAME] | navedit write --plan PLAN.json --out PATCH.esp | navedit --selftest"); return 2; }

var flags = new Dictionary<string, string>(StringComparer.Ordinal);
for (int i = 1; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) { Console.Error.WriteLine($"navedit: '{args[i]}' is not a flag with a value; see navedit with no arguments for the usage."); return 2; }
    flags[args[i]] = args[++i];
}
string[] known = args[0] == "plan" ? new[] { "--edits", "--out", "--instance", "--game", "--profile" } : new[] { "--plan", "--out" };
foreach (var k in flags.Keys)
    if (!known.Contains(k)) { Console.Error.WriteLine($"navedit {args[0]}: '{k}' is not one of its flags ({string.Join(", ", known)})."); return 2; }

try { return args[0] == "plan" ? NavEditCli.Plan(flags) : NavEditCli.Write(flags); }
catch (NavEditCli.Refusal r) { Console.Error.WriteLine(r.Message); return 2; }

static class NavEditCli
{
    public sealed class Refusal(string message) : Exception(message);

    static string Need(Dictionary<string, string> f, string k, string what) => f.TryGetValue(k, out var v) ? v : throw new Refusal($"navedit: give {k} {what}.");

    sealed record Target(string Instance, string Game, string Profile)
    {
        public string ProfileDir => Path.Combine(Instance, "profiles", Profile);
        public IReadOnlyList<string> Order()
        {
            var o = Mo2LoadOrder.Build(ProfileDir, Path.Combine(Instance, "mods"), Path.Combine(Game, "Data"), Path.Combine(Instance, "overwrite"));
            if (o.OrderedPaths.Count == 0) throw new Refusal($"navedit: the profile '{Profile}' in {Instance} has no plugins in its load order; check the instance and profile.");
            return o.OrderedPaths;
        }
    }

    public static int Plan(Dictionary<string, string> f)
    {
        var editsPath = Need(f, "--edits", "EDITS.json, the edits to plan");
        var outPath = Need(f, "--out", "PLAN.json, where the plan goes");
        var t = new Target(
            HarnessTarget.Require(f.GetValueOrDefault("--instance") ?? HarnessTarget.Instance, "MO2 instance directory", "instance", "--instance"),
            HarnessTarget.Require(f.GetValueOrDefault("--game") ?? HarnessTarget.Game, "game directory", "game", "--game"),
            HarnessTarget.Require(f.GetValueOrDefault("--profile") ?? HarnessTarget.Profile, "MO2 profile name", "profile", "--profile"));
        JsonNode edits;
        try { edits = JsonNode.Parse(File.ReadAllText(editsPath))!; }
        catch (Exception ex) { throw new Refusal($"navedit: the edits file '{editsPath}' could not be read ({ex.Message}); see the usage for its shape."); }

        var order = t.Order();
        using var resolver = LoadOrderResolver.Build(order);
        var view = resolver.Capture();
        var (navi, naviOpen) = NaviView.Read(order);
        var results = new JsonArray(); bool allPlanned = true;
        try
        {
            foreach (var e in edits["edits"]?.AsArray() ?? throw new Refusal("navedit: the edits file has no \"edits\" list."))
            {
                var key = FormKey.Factory((string)e!["navmesh"]!);
                var row = new JsonObject { ["navmesh"] = key.ToString() };
                results.Add(row);
                NavEditResult r;
                var w = view.ResolveWinner(key);
                var path = w is null ? null : order.FirstOrDefault(p => string.Equals(Path.GetFileName(p), w.Value.WinnerPlugin, StringComparison.OrdinalIgnoreCase));
                var geom = path is null ? null : Winning(path, key);
                if (w is null || path is null) r = NavEditResult.Refused($"{key} is in no plugin of this load order; check the FormKey and the profile.");
                else if (geom is null) r = NavEditResult.Refused($"{key} has no geometry in its winner {w.Value.WinnerPlugin} (deleted or empty), so there is nothing to edit.");
                else if (navi.Problem is { } np) r = NavEditResult.Refused($"The navmesh info map cannot be read ({np}), so whether {key} is an island is not known.");
                else if (navi.Effective.TryGetValue(key, out var ent) && ent.Src.Map.MapInfos[ent.Index].Island is not null)
                    r = NavEditResult.Refused($"{key} is an island in the navmesh info map, which keeps its own copy of the geometry; editing islands is not supported yet.");
                else
                {
                    var moves = new List<VertexMove>();
                    foreach (var m in e["move"]?.AsArray() ?? new JsonArray())
                    {
                        int v = (int)m!["vertex"]!;
                        Vec3 to;
                        if (m["to"] is JsonArray a) to = new Vec3((float)a[0]!, (float)a[1]!, (float)a[2]!);
                        else if (m["by"] is JsonArray b && v >= 0 && v < geom.Vertices.Count) { var p0 = geom.Vertices[v]; to = new Vec3(p0.X + (float)b[0]!, p0.Y + (float)b[1]!, p0.Z + (float)b[2]!); }
                        else to = new Vec3(float.NaN, float.NaN, float.NaN);
                        if (float.IsNaN(to.X)) { moves.Clear(); row["parse"] = $"a move of vertex {v} needs \"to\" or \"by\" with three numbers, and a vertex in range"; break; }
                        moves.Add(new VertexMove(v, to));
                    }
                    r = row["parse"] is not null ? NavEditResult.Refused($"The edits file's move for {key} is malformed: {row["parse"]}.") : NavEdit.MoveVertices(geom, moves);
                }
                row["winner"] = w is null ? null : new JsonObject { ["plugin"] = w.Value.WinnerPlugin, ["path"] = path };
                row["status"] = r.Status;
                row["why"] = new JsonArray(r.Why.Select(x => (JsonNode)x).ToArray());
                row["moved"] = new JsonArray(r.Moved.Select(x => (JsonNode)x).ToArray());
                if (r.Grid is { } g) row["grid"] = new JsonObject { ["divisor"] = g.Divisor, ["sectionX"] = g.SectionX, ["sectionY"] = g.SectionY, ["cellBytes"] = g.CellBytes.Length };
                row["ops"] = new JsonArray(r.Ops.Select(o => (JsonNode)new JsonObject { ["formid"] = o.FormId, ["field_path"] = o.FieldPath, ["op"] = o.Op, ["key"] = o.Key, ["value"] = o.Value }).ToArray());
                allPlanned &= r.Status == "planned";
            }
        }
        finally { foreach (var o in naviOpen) o.Dispose(); }
        var doc = new JsonObject
        {
            ["tool"] = "navedit plan", ["instance"] = t.Instance, ["game"] = t.Game, ["profile"] = t.Profile,
            ["order"] = new JsonArray(order.Select(p => (JsonNode)p).ToArray()), ["edits"] = results,
        };
        File.WriteAllText(outPath, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        foreach (var row in results)
            Console.WriteLine($"{row!["navmesh"]}: {row["status"]}" + ((string?)row["status"] == "refused" ? " - " + string.Join(" ", row["why"]!.AsArray().Select(x => (string)x!)) : $" ({row["moved"]!.AsArray().Count} vertices, {row["ops"]!.AsArray().Count} ops)"));
        Console.WriteLine($"plan written to {outPath}");
        return allPlanned ? 0 : 1;
    }

    /// <summary>The navmesh as its winning plugin has it, through the one NAVM reader (PlacementExtract); null when absent.</summary>
    static NavmeshGeometry? Winning(string pluginPath, FormKey key)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
        foreach (var cp in PlacementExtract.FromMod(mod))
            foreach (var n in cp.Navmeshes)
                if (n.FormKey == key) return n.Deleted ? null : n;
        return null;
    }

    public static int Write(Dictionary<string, string> f)
    {
        var planPath = Need(f, "--plan", "PLAN.json, from navedit plan");
        var outPath = Path.GetFullPath(Need(f, "--out", "PATCH.esp, the new patch plugin"));
        if (File.Exists(outPath)) throw new Refusal($"navedit write: {outPath} already exists; give a new patch name (navedit writes new patches only).");
        JsonNode plan;
        try { plan = JsonNode.Parse(File.ReadAllText(planPath))!; }
        catch (Exception ex) { throw new Refusal($"navedit write: the plan '{planPath}' could not be read ({ex.Message}); write it with navedit plan."); }
        var t = new Target((string)plan["instance"]!, (string)plan["game"]!, (string)plan["profile"]!);
        var order = t.Order();
        var planned = plan["order"]!.AsArray().Select(x => (string)x!).ToList();
        if (!order.SequenceEqual(planned, StringComparer.OrdinalIgnoreCase))
            throw new Refusal($"navedit write: the load order of '{t.Profile}' changed since the plan was made; run navedit plan again.");
        var rows = plan["edits"]!.AsArray().Where(r => (string?)r!["status"] == "planned").ToList();
        if (rows.Count == 0) throw new Refusal("navedit write: the plan has no planned navmesh to write (every edit was refused).");
        if (rows.Count != plan["edits"]!.AsArray().Count) throw new Refusal("navedit write: the plan has refused edits; fix or drop them and plan again, so a patch never carries half an edit set.");

        var root = CheckoutLayout.FindRoot() ?? throw new Refusal("navedit write: the SkyWright checkout (SkyWright.sln) was not found above this program, so the rulebook cannot be located.");
        var corpus = CheckoutLayout.Corpus(root);
        if (!File.Exists(corpus)) throw new Refusal($"navedit write: the rulebook {corpus} is missing; generate it (see CLAUDE.md, Build and check).");
        var rulebook = CorpusRulebook.Load(corpus);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        var edits = rows.SelectMany(r => r!["ops"]!.AsArray()).Select(o => new WritePatchBuilder.PatchEdit
        {
            Target = FormKey.Factory((string)o!["formid"]!), Path = ((string)o["field_path"]!).Split('.'), Verb = (string)o["op"]!,
            Key = (string?)o["key"], Value = (string?)o["value"],
        }).ToList();
        using (var resolver = LoadOrderResolver.Build(order))
        {
            var applied = WritePatchBuilder.Apply(resolver, rulebook, edits, outPath, extend: false);
            if (!applied.Success) { Console.Error.WriteLine($"navedit write: the write engine refused the patch: {applied.Error}"); return 1; }
            foreach (var op in applied.Ops.Where(o => !o.Applied)) { Console.Error.WriteLine($"navedit write: {op.Label} was not applied: {op.Error}"); return 1; }

            // the containers the engine carried (the navmesh's cell and worldspace) must be their winners' versions
            JsonArray carried;
            using (var patch = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE)) carried = NavRepairReadBack.Carried(patch, order).Records;
            var specs = carried.Where(c => (bool?)c!["holds"] == false && c["winner"] is not null)
                .GroupBy(c => (string)c!["winner"]!, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var g in specs)
            {
                var fw = WritePatchBuilder.ForwardRecords(resolver,
                    g.Select(c => new WritePatchBuilder.ForwardSpec { Target = FormKey.Factory((string)c!["record"]!), FromPlugin = g.Key }).ToList(),
                    outPath, extend: true, sourceParam: g.Key);
                if (!fw.Success) { Console.Error.WriteLine($"navedit write: forwarding the carried records from {g.Key} failed: {fw.Error}"); return 1; }
            }
        }
        return ReadBack(outPath, order, rows);
    }

    /// <summary>The written patch against the plan: each planned navmesh's vertices, box and grid, and every carried record
    /// against its winner. Prints one line per check; 0 when all hold.</summary>
    static int ReadBack(string patchPath, IReadOnlyList<string> order, IReadOnlyList<JsonNode?> rows)
    {
        int failed = 0;
        using var patch = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
        var written = patch.EnumerateMajorRecords<INavigationMeshGetter>().ToDictionary(n => n.FormKey);
        foreach (var r in rows)
        {
            var key = FormKey.Factory((string)r!["navmesh"]!);
            var why = new List<string>();
            var winner = Winning((string)r["winner"]!["path"]!, key)!;
            if (!written.TryGetValue(key, out var nm) || nm.Data is not { } d) { Console.WriteLine($"FAIL {key}: not in the patch"); failed++; continue; }
            var want = winner.Vertices.ToArray();
            foreach (var o in r["ops"]!.AsArray().Where(o => (string?)o!["field_path"] == "Data.Vertices"))
            {
                var p = ((string)o!["value"]!).Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                want[int.Parse((string)o["key"]!, CultureInfo.InvariantCulture)] = new Vec3(p[0], p[1], p[2]);
            }
            if (d.Vertices.Count != want.Length) why.Add($"{d.Vertices.Count} vertices written, {want.Length} planned");
            else for (int i = 0; i < want.Length; i++) if (d.Vertices[i].X != want[i].X || d.Vertices[i].Y != want[i].Y || d.Vertices[i].Z != want[i].Z) { why.Add($"vertex {i} is ({d.Vertices[i]}), planned ({want[i]})"); break; }
            if (d.Triangles.Count != winner.Triangles.Count) why.Add("the triangle count changed");
            else for (int i = 0; i < d.Triangles.Count; i++)
                {
                    var (a, b) = (d.Triangles[i], winner.Triangles[i]);
                    if (a.Vertices.X != b.A || a.Vertices.Y != b.B || a.Vertices.Z != b.C || (int)a.Flags != b.Flags || a.EdgeLink_0_1 != b.EdgeLink01 || a.EdgeLink_1_2 != b.EdgeLink12 || a.EdgeLink_2_0 != b.EdgeLink20)
                    { why.Add($"triangle {i} changed"); break; }
                }
            if (d.EdgeLinks.Count != (winner.EdgeLinks?.Count ?? 0)) why.Add("the edge-link table changed");
            var g = NavGrid.Rebuild(want, d.Triangles.Select(x => ((int)x.Vertices.X, (int)x.Vertices.Y, (int)x.Vertices.Z)).ToList());
            if (d.NavmeshGridDivisor != g.Divisor || d.MaxDistanceX != g.SectionX || d.MaxDistanceY != g.SectionY || !d.NavmeshGrid.Span.SequenceEqual(g.CellBytes))
                why.Add("the grid is not the one NavGrid rebuilds from the written vertices");
            if (new Vec3(d.Min.X, d.Min.Y, d.Min.Z) != g.Min || new Vec3(d.Max.X, d.Max.Y, d.Max.Z) != g.Max) why.Add("the box is not the used vertices' extent");
            Console.WriteLine(why.Count == 0 ? $"ok   {key}: vertices, triangles, links, box and grid as planned" : $"FAIL {key}: {string.Join("; ", why)}");
            if (why.Count > 0) failed++;
        }
        var extra = written.Keys.Where(k => !rows.Any(r => FormKey.Factory((string)r!["navmesh"]!) == k)).ToList();
        if (extra.Count > 0) { Console.WriteLine($"FAIL the patch carries navmeshes the plan does not: {string.Join(", ", extra)}"); failed++; }
        var (recs, _, bad) = NavRepairReadBack.Carried(patch, order);
        foreach (var c in recs) Console.WriteLine($"{((bool)c!["holds"]! ? "ok  " : "FAIL")} carried {c["type"]} {c["record"]} equals its winner {c["winner"]}" + ((bool)c["holds"]! ? "" : ": " + string.Join("; ", c["differences"]!.AsArray().Select(x => (string)x!))));
        failed += bad;
        Console.WriteLine(failed == 0 ? $"patch written and read back: {patchPath}" : $"navedit write: {failed} check(s) failed on {patchPath}; do not enable it.");
        return failed == 0 ? 0 : 1;
    }
}
