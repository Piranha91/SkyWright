using System.Diagnostics;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using HousecarlCore.Progress;
using SkyWright.Engine;

// recordprobe — the record checks: clashes between two mods that houseCARL's record and check tools find, called in this
// process (HousecarlCalls; no houseCARL program is started).
//
// Each run is one check (one registry row, one child process). The checks and their rules: RecordChecks.cs; the between-mods
// rule: SkyWright.Engine.RecordClashRule. Every fact houseCARL reports that is not a clash between two mods is counted in the
// emit's coverage under its reason, never a row; a read houseCARL marks incomplete is "not judged", or the check refuses.
//
// usage: recordprobe --self-test
//        recordprobe --check ID [--order CANDIDATE.json] --emit OUT.json [--population FILE] --instance DIR --profile NAME --game DIR
//   --check       the registry id: loadability.dangling-references
//   --population  the plan's population file: its plugins and mod folders are the selection; without it every plugin is
//   --emit        the conflict file; beside it OUT.json.view\ (the order as houseCARL reads it) and OUT.json.housecarl\

using var __run = RunLog.BeginProcess("recordprobe", args);
if (args.Length == 1 && args[0] == "--self-test") return RecordSelfTest.Run();
// --write-self-test-emit DIR: the cross-mod known answer's emit, for runcontrol's self-test (a test fixture, not a check run)
if (args.Length == 2 && args[0] == "--write-self-test-emit") return RecordSelfTest.WriteEmit(args[1]);

string Instance = HarnessTarget.Instance, GamePath = HarnessTarget.Game, profileName = HarnessTarget.Profile;
string? check = null, orderFile = null, emitPath = null, populationPath = null;
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
            default: Console.Error.WriteLine($"recordprobe: unknown argument {a}; see the usage at the top of Program.cs."); return 2;
        }
    }
    catch (ArgumentException ex) { Console.Error.WriteLine($"recordprobe: {ex.Message}."); return 2; }
}
if (check is null || !RecordChecks.ById.TryGetValue(check, out var run))
{ Console.Error.WriteLine($"recordprobe: --check must name one of {string.Join(", ", RecordChecks.ById.Keys)}; name the check to run."); return 2; }
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
    return new OrderPlugin(name, mod, RecordChecks.IsGameFile(name), selected);
}).ToList();
Console.WriteLine($"recordprobe: {check}; {order.Count} plugins, {order.Count(p => p.Selected)} in the selection");

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
try
{
    using var svc = HousecarlCalls.Open(dataDir, modsDir, view, hcData, CorpusRulebook.CorpusPath);
    result = run(new RecordContext((tool, a) => HousecarlCalls.Invoke(svc, tool, a), order));
}
catch (CheckRefused ex) { Console.Error.WriteLine($"recordprobe: {ex.Message}"); return 2; }
try { Directory.Delete(Path.Combine(hcData, "results"), true); } catch (IOException) { } catch (UnauthorizedAccessException) { }

foreach (var c in result.Coverage)
{
    Console.WriteLine($"recordprobe: {c.Population}: judged {c.Judged:N0}; {c.Note}");
    foreach (var (why, n) in c.SetAside ?? new Dictionary<string, long>()) Console.WriteLine($"   {n,8:N0}  no row: {why}");
    foreach (var (why, n) in c.Unjudgeable ?? new Dictionary<string, long>()) Console.WriteLine($"   {n,8:N0}  not judged: {why}");
}
ConflictIo.Write(emitPath, new ConflictFile("recordprobe", Instance, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), result.Rows, result.Coverage,
    null, null, population, "baseline", cfg.Identity(population, "baseline")));
Console.WriteLine($"wrote {result.Rows.Count:N0} row(s) -> {emitPath}; elapsed {sw.Elapsed.TotalSeconds:0.0} s");
return 0;
