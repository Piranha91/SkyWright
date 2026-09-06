using HousecarlCore;
using HousecarlCore.Progress;

// FLOOR MEASUREMENT for the dangling-reference split (roadmap 2.5).
//
// The 862 → 398 base / 242 CC / 222 third-party split classifies by the SOURCE plugin and asserts
// the Bethesda buckets are intrinsic ("shipped that way"). That was plausible, not measured — the
// same one-end blindness the dangling-LINK split was built to fix. This measures it: run the same
// check_errors sweep over a BARE order (base masters + Creation Club only, in their real
// loadorder.txt positions) and compare per-plugin dangling counts with the full 490-plugin run.
//
//   identical counts  -> the bucket is intrinsic; "74% floor" is measured, not assumed
//   any delta         -> a mod-caused dangle was hiding in a Bethesda bucket (or a mod was FIXING
//                        one, if the bare count is higher) — either way, misassigned
//
// usage: floorprobe

using var __run = RunLog.BeginProcess("floorprobe", args);

string Instance = HarnessTarget.Require(HarnessTarget.Instance, "MO2 instance directory", "instance");
string GamePath = HarnessTarget.Require(HarnessTarget.Game, "game directory", "game");
string profileDir = Path.Combine(Instance, "profiles", HarnessTarget.Require(HarnessTarget.Profile, "MO2 profile name", "profile"));
string modsDir = Path.Combine(Instance, "mods");
string dataDir = Path.Combine(GamePath, "Data");

var loadorder = File.ReadAllLines(Path.Combine(profileDir, "loadorder.txt"))
    .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();

bool IsBare(string name) =>
    ErrorCheck.IsBaseMaster(name)
    || name.StartsWith("cc", StringComparison.OrdinalIgnoreCase)
    || name.Equals("_ResourcePack.esl", StringComparison.OrdinalIgnoreCase);

string? Resolve(string name)
{
    var p = Path.Combine(dataDir, name);
    if (File.Exists(p)) return p;
    // CC content is installed as a mod folder in this instance; any folder holding the file works,
    // because the file is the same plugin wherever MO2 mounts it from.
    foreach (var dir in Directory.EnumerateDirectories(modsDir))
    {
        var q = Path.Combine(dir, name);
        if (File.Exists(q)) return q;
    }
    return null;
}

var bare = new List<string>();
var missing = new List<string>();
foreach (var name in loadorder.Where(IsBare))
{
    if (Resolve(name) is { } path) bare.Add(path);
    else missing.Add(name);
}

Console.WriteLine($"BARE order: {bare.Count} plugins (base masters + Creation Club + resource pack), " +
                  $"positions as in loadorder.txt");
foreach (var m in missing) Console.WriteLine($"   ⚠ '{m}' matched the bare filter but no file was found — excluded, counted here.");

using var resolver = LoadOrderResolver.Build(bare);
var res = ErrorCheck.Run(resolver, null, int.MaxValue);
if (res.Error is not null) { Console.Error.WriteLine(res.Error); return 1; }

Console.WriteLine($"\nscanned {res.PluginsScanned} plugins · {res.TotalDangling:N0} dangling ref(s) · " +
                  $"{res.TotalMissingMasters:N0} missing master(s) · {res.TotalUnscannableRecords:N0} unscannable");
Console.WriteLine();
Console.WriteLine("   ---- dangling refs per plugin, BARE order ----");
Console.WriteLine("   (compare each against the full-490 run: identical = intrinsic, measured floor;");
Console.WriteLine("    a lower bare count = mods were BREAKING refs assigned to a Bethesda bucket;");
Console.WriteLine("    a higher bare count = mods were FIXING some — both are misassignments the");
Console.WriteLine("    by-source split cannot see)");
foreach (var r in res.Reports.OrderByDescending(r => r.Dangling.Count))
    Console.WriteLine($"   {r.Dangling.Count,6:N0}  {r.Plugin}");
return 0;
