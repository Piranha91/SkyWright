using System.Diagnostics;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using HousecarlCore.Progress;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

// recordprobe — the record checks: clashes between two mods that houseCARL's record and check tools find, called in this
// process (HousecarlCalls; no houseCARL program is started).
//
// Each run is one check (one registry row, one child process). The checks and their rules: RecordChecks.cs; the between-mods
// rule: SkyWright.Engine.RecordClashRule. Every fact houseCARL reports that is not a clash between two mods is counted in the
// emit's coverage under its reason, never a row; a read houseCARL marks incomplete is "not judged", or the check refuses.
//
// usage: recordprobe --self-test
//        recordprobe --check ID [--order CANDIDATE.json] --emit OUT.json [--population FILE] [--instrument] --instance DIR --profile NAME --game DIR
//   --check       the registry id: loadability.dangling-references, loadability.missing-assets, records.contested-fields,
//                 records.cell-attributes, records.registration-and-dialogue
//   --instrument  the lost-edit checks only: the earlier Python capture's rule (the selection's plugins as the only losers,
//                 each against the plugin beneath it, no between-mods clause), a known answer; never in the registry, never a scan
//   --population  the plan's population file: its plugins and mod folders are the selection; without it every plugin is
//   --emit        the conflict file; beside it OUT.json.view\ (the order as houseCARL reads it), OUT.json.housecarl\ and, for
//                 a check that has them, OUT.json.rows.json (each row's values in full); the missing-assets check also
//                 OUT.json.own-N\ (the order with only one plugin's own mods enabled) and its store

using var __run = RunLog.BeginProcess("recordprobe", args);
if (args.Length == 1 && args[0] == "--self-test") return RecordSelfTest.Run();
// --write-self-test-emit DIR: the cross-mod known answer's emit, for runcontrol's self-test (a test fixture, not a check run)
if (args.Length == 2 && args[0] == "--write-self-test-emit") return RecordSelfTest.WriteEmit(args[1]);

string Instance = HarnessTarget.Instance, GamePath = HarnessTarget.Game, profileName = HarnessTarget.Profile;
string? check = null, orderFile = null, emitPath = null, populationPath = null;
bool instrument = false;
for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
    try
    {
        switch (a)
        {
            case "--check": check = Next(); break;
            case "--instance": Instance = Next(); break;
            case "--game": GamePath = Next(); break;
            case "--profile": profileName = Next(); break;
            case "--order": orderFile = Next(); break;
            case "--emit": emitPath = Next(); break;
            case "--population": populationPath = Next(); break;
            case "--instrument": instrument = true; break;
            default: Console.Error.WriteLine($"recordprobe: unknown argument {a}; see the usage at the top of Program.cs."); return 2;
        }
    }
    catch (ArgumentException ex) { Console.Error.WriteLine($"recordprobe: {ex.Message}."); return 2; }
}
if (check is null || !RecordChecks.ById.TryGetValue(check, out var run))
{ Console.Error.WriteLine($"recordprobe: --check must name one of {string.Join(", ", RecordChecks.ById.Keys)}; name the check to run."); return 2; }
if (instrument && check is not (LostEditCheck.FieldsId or LostEditCheck.CellsId or RegistrationCheck.Id))
{ Console.Error.WriteLine($"recordprobe: --instrument is a mode of {LostEditCheck.FieldsId}, {LostEditCheck.CellsId} and {RegistrationCheck.Id} only; drop it or name one of them."); return 2; }
if (emitPath is null) { Console.Error.WriteLine("recordprobe: no --emit OUT.json was given, so the rows would go nowhere; name the file to write."); return 2; }
Instance = HarnessTarget.Require(Instance, "MO2 instance directory", "instance", "--instance");
GamePath = HarnessTarget.Require(GamePath, "game directory", "game", "--game");
profileName = HarnessTarget.Require(profileName, "MO2 profile name", "profile", "--profile");
string profileDir = Path.Combine(Instance, "profiles", profileName), modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite"), dataDir = Path.Combine(GamePath, "Data");

var sw = Stopwatch.StartNew();
var cfg = HarnessOrderCli.Resolve(profileName, profileDir, modsDir, dataDir, overwriteDir, GamePath, orderFile, null, null);
if (cfg.Refused) { Console.Error.WriteLine(cfg.RefusedReason); return 2; }
HarnessOrderCli.Print(cfg);

HashSet<string>? selPlugins = null, selMods = null;
// the emit's population: the load-time state, scoped by the population file when one is given (PopulationRules owns the name)
string population = "load-time";
if (populationPath is not null)
{
    try
    {
        using var pd = JsonDocument.Parse(File.ReadAllText(populationPath));
        IEnumerable<string> Names(string k) => pd.RootElement.TryGetProperty(k, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(x => x.GetProperty("name").GetString() ?? "") : Enumerable.Empty<string>();
        selPlugins = Names("plugins").ToHashSet(StringComparer.OrdinalIgnoreCase);
        selMods = Names("mods").ToHashSet(StringComparer.OrdinalIgnoreCase);
        population = PopulationRules.Identity(population, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(populationPath))));
    }
    catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
    { Console.Error.WriteLine($"recordprobe: the population file {populationPath} could not be read ({ex.Message}); plan the scan again."); return 2; }
}
var order = cfg.OrderedPaths.Select(p =>
{
    string name = Path.GetFileName(p), mod = RecordClashRule.ModOf(p, modsDir);
    bool selected = selPlugins is null || selPlugins.Contains(name) || (selMods?.Contains(RecordClashRule.ModName(mod)) == true && mod.StartsWith("folder:", StringComparison.Ordinal));
    return new OrderPlugin(name, mod, RecordChecks.IsGameFile(name), selected, PluginHeader.TryRead(p)?.Masters);
}).ToList();
Console.WriteLine($"recordprobe: {check}; {order.Count} plugins, {order.Count(p => p.Selected)} in the selection{(instrument ? "; INSTRUMENT mode (a known answer, not a scan)" : "")}");

// the order as houseCARL reads it: plugins.txt, loadorder.txt and modlist.txt of this configuration, the profile's INIs
string view = emitPath + ".view", hcData = emitPath + ".housecarl";
Directory.CreateDirectory(view);
File.WriteAllLines(Path.Combine(view, "plugins.txt"), order.Select(p => "*" + p.Name));
File.WriteAllLines(Path.Combine(view, "loadorder.txt"), order.Select(p => p.Name));
File.WriteAllLines(Path.Combine(view, "modlist.txt"), cfg.EnabledMods.Select(m => "+" + m));
foreach (var ini in Directory.Exists(profileDir) ? Directory.EnumerateFiles(profileDir, "*.ini") : Array.Empty<string>())
    File.Copy(ini, Path.Combine(view, Path.GetFileName(ini)), overwrite: true);

if (HousecarlRulebook.Require("recordprobe", "no record can be read") is { } noRulebook) { Console.Error.WriteLine(noRulebook); return 2; }
CheckResult result;
// the missing-assets check's reads: each winning plugin's records (Mutagen, opened once each) and houseCARL's file tool over
// the same order with only some mod folders enabled (one view and store per set of mods, beside the emit)
var pathOf = cfg.OrderedPaths.ToDictionary(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);
var opened = new Dictionary<string, (IDisposable? Mod, Dictionary<FormKey, IMajorRecordGetter>? Records)>(StringComparer.OrdinalIgnoreCase);
var ownServices = new List<LoadOrderServiceHandle>();
var ownViews = new Dictionary<string, Func<string, IReadOnlyDictionary<string, object?>, string>>(StringComparer.OrdinalIgnoreCase);
IReadOnlyList<string>? AssetLinks(string formid, string plugin)
{
    if (!opened.TryGetValue(plugin, out var o))
    {
        o = (null, null);
        if (pathOf.TryGetValue(plugin, out var pp))
            try
            {
                var mod = SkyrimMod.CreateFromBinaryOverlay(pp, SkyrimRelease.SkyrimSE);
                var recs = new Dictionary<FormKey, IMajorRecordGetter>();
                foreach (var r in mod.EnumerateMajorRecords()) recs.TryAdd(r.FormKey, r);
                o = (mod, recs);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Console.WriteLine($"recordprobe: {plugin} could not be read ({ex.Message}); its records' files are not judged"); }
        opened[plugin] = o;
    }
    if (o.Records is null || !FormKey.TryFactory(formid, out var fk) || !o.Records.TryGetValue(fk, out var rec)) return null;
    try { return AssetLinkHarvest.HarvestAssetPaths(new[] { rec }); }
    catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
}
try
{
    using var svc = HousecarlCalls.Open(dataDir, modsDir, view, hcData, CorpusRulebook.CorpusPath);
    Func<string, IReadOnlyDictionary<string, object?>, string> OwnView(IReadOnlyList<string> mods)
    {
        string key = string.Join("|", mods);
        if (ownViews.TryGetValue(key, out var have)) return have;
        string dir = $"{emitPath}.own-{ownViews.Count + 1}";
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "plugins.txt"), order.Select(p => "*" + p.Name));
        File.WriteAllLines(Path.Combine(dir, "loadorder.txt"), order.Select(p => p.Name));
        File.WriteAllLines(Path.Combine(dir, "modlist.txt"), mods.Select(m => "+" + m));
        foreach (var ini in Directory.EnumerateFiles(view, "*.ini")) File.Copy(ini, Path.Combine(dir, Path.GetFileName(ini)), overwrite: true);
        var own = HousecarlCalls.Open(dataDir, modsDir, dir, dir + ".housecarl", CorpusRulebook.CorpusPath);
        ownServices.Add(new LoadOrderServiceHandle(own, dir + ".housecarl"));
        Console.WriteLine($"recordprobe: asked again with only {(mods.Count == 0 ? "the game's Data folder" : string.Join(", ", mods))} enabled ({Path.GetFileName(dir)})");
        return ownViews[key] = (tool, a) => HousecarlCalls.Invoke(own, tool, a);
    }
    result = run(new RecordContext((tool, a) => HousecarlCalls.Invoke(svc, tool, a), order, instrument,
        selPlugins is null ? null : new Selection(selPlugins, selMods ?? new HashSet<string>()))
    { AssetLinks = AssetLinks, OwnView = OwnView, EnabledMods = cfg.EnabledMods });
}
catch (CheckRefused ex) { Console.Error.WriteLine($"recordprobe: {ex.Message}"); return 2; }
finally
{
    foreach (var h in ownServices) h.Dispose();
    foreach (var o in opened.Values) o.Mod?.Dispose();
}
try { Directory.Delete(Path.Combine(hcData, "results"), true); } catch (IOException) { } catch (UnauthorizedAccessException) { }

foreach (var c in result.Coverage)
{
    Console.WriteLine($"recordprobe: {c.Population}: judged {c.Judged:N0}; {c.Note}");
    foreach (var (why, n) in c.SetAside ?? new Dictionary<string, long>()) Console.WriteLine($"   {n,8:N0}  no row: {why}");
    foreach (var (why, n) in c.Unjudgeable ?? new Dictionary<string, long>()) Console.WriteLine($"   {n,8:N0}  not judged: {why}");
}
ConflictIo.Write(emitPath, new ConflictFile("recordprobe", Instance, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), result.Rows, result.Coverage,
    null, null, population, "baseline", cfg.Identity(population, "baseline")));
if (result.Detail is not null)
    File.WriteAllText(emitPath + ".rows.json", JsonSerializer.Serialize(result.Detail, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"recordprobe: peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024):N0} MB");
Console.WriteLine($"wrote {result.Rows.Count:N0} row(s) -> {emitPath}; elapsed {sw.Elapsed.TotalSeconds:0.0} s");
return 0;

/// <summary>An own-view houseCARL service and its store, released together (its spilled results deleted).</summary>
sealed record LoadOrderServiceHandle(HousecarlMcp.LoadOrderService Service, string Store) : IDisposable
{
    public void Dispose()
    {
        Service.Dispose();
        try { Directory.Delete(Path.Combine(Store, "results"), true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
