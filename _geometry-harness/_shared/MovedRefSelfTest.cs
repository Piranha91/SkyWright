using System.Reflection;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HarnessShared;

// The frame every probe's --moved-ref-selftest runs in: the fixture (MovedRefFixture.cs) written to a temp folder, a
// PASS/FAIL line per case, a planted canary, and the checks more than one probe needs (the floor neighbourhood of grid
// (0,0), the LAND per grid, a plugin that cannot be opened). Each probe supplies its own site code and its own
// hand-derived expectations; nothing here decides what a probe should hold beyond the fixture's shared literal.
//
// MR_CANARY=1: the probe plants one wrong expectation on a passing control, which must fail.
// MR_FIXTURE_KEEP=<dir>: keep the plugins (the folder must be empty or absent).
public sealed partial class MovedRefSelfTest
{
    int _pass, _fail;
    public readonly bool Canary = Environment.GetEnvironmentVariable("MR_CANARY") == "1";
    public IReadOnlyList<string> Paths { get; private set; } = Array.Empty<string>();
    public string Root { get; private set; } = "";

    public void Check(string name, bool ok, string detail)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"   {(ok ? "PASS" : "FAIL"),-5} {name,-86} {detail}");
    }

    /// <summary>Write the fixture, run <paramref name="body"/>, print the tally. Exit 0 when every case passes.</summary>
    public static int Run(string probe, Action<MovedRefSelfTest> body)
    {
        var t = new MovedRefSelfTest();
        Console.WriteLine($"{probe} --moved-ref-selftest: MovedRefFixture read in load order through the probe's own walk code"
                        + (t.Canary ? " [CANARY]" : ""));
        string? keep = Environment.GetEnvironmentVariable("MR_FIXTURE_KEEP") is { Length: > 0 } k ? Path.GetFullPath(k) : null;
        t.Root = keep ?? Path.Combine(Path.GetTempPath(), $"mr-{probe}-{Environment.ProcessId}");
        if (keep is not null && Directory.Exists(keep) && Directory.EnumerateFileSystemEntries(keep).Any())
        { Console.WriteLine($"MR_FIXTURE_KEEP folder '{keep}' is not empty; refusing"); return 2; }
        try
        {
            t.Paths = MovedRefFixture.Write(t.Root);
            foreach (var (n, ok, d) in MovedRefFixture.Preconditions(t.Paths)) t.Check("precondition: " + n, ok, d);
            body(t);
        }
        catch (Exception ex) { t.Check("fixture/body", false, $"threw {ex.GetType().Name}: {ex.Message}"); }
        finally { if (keep is null) { try { Directory.Delete(t.Root, true); } catch { } } }
        Console.WriteLine($"{probe} --moved-ref-selftest: {t._pass} passed, {t._fail} failed");
        return t._fail == 0 ? 0 : 1;
    }

    public static string Name(FormKey key) => MovedRefFixture.Refs.FirstOrDefault(kv => kv.Value == key).Key ?? key.ToString();

    /// <summary>Every plugin of the fixture opened in load order, with its index.</summary>
    public IEnumerable<(int Index, ISkyrimModGetter Mod, string Path)> Plugins()
    {
        for (int i = 0; i < Paths.Count; i++)
        {
            using var m = SkyrimMod.CreateFromBinaryOverlay(Paths[i], MovedRefFixture.Rel);
            yield return (i, m, Paths[i]);
        }
    }

    /// <summary>The set a walk holds, against the hand-derived expectation: exact keys, then per key its cell (when given),
    /// its position within 0.01 u and its deleted flag.</summary>
    public void CheckSet(string name, IEnumerable<(FormKey Key, FormKey? Cell, PlacedRef Ref)> got,
                         IReadOnlyDictionary<FormKey, (FormKey? Cell, float X, float Y, bool Deleted)> expect)
    {
        var g = new Dictionary<FormKey, (FormKey? Cell, PlacedRef Ref)>();
        var twice = new List<string>();
        foreach (var (k, c, r) in got) if (!g.TryAdd(k, (c, r))) twice.Add(Name(k));
        var missing = expect.Keys.Where(k => !g.ContainsKey(k)).Select(Name).OrderBy(x => x).ToList();
        var extra = g.Keys.Where(k => !expect.ContainsKey(k)).Select(k => $"{Name(k)} at {g[k].Ref.Position.X:0},{g[k].Ref.Position.Y:0}").OrderBy(x => x).ToList();
        var wrong = expect.Where(kv => g.TryGetValue(kv.Key, out var v)
                                       && ((kv.Value.Cell is { } ec && v.Cell != ec) || MathF.Abs(v.Ref.Position.X - kv.Value.X) > 0.01f
                                           || MathF.Abs(v.Ref.Position.Y - kv.Value.Y) > 0.01f || v.Ref.Deleted != kv.Value.Deleted))
                          .Select(kv => $"{Name(kv.Key)} at {g[kv.Key].Ref.Position.X:0},{g[kv.Key].Ref.Position.Y:0} cell {g[kv.Key].Cell} deleted {g[kv.Key].Ref.Deleted}")
                          .OrderBy(x => x).ToList();
        Check(name, missing.Count == 0 && extra.Count == 0 && wrong.Count == 0 && twice.Count == 0,
              $"missing [{string.Join(", ", missing)}] extra [{string.Join(", ", extra)}] wrong [{string.Join(", ", wrong)}] twice [{string.Join(", ", twice)}]");
    }

    /// <summary>A member a fix adds, read by name so the test compiles before it exists: the grids of worldspace
    /// <paramref name="ws"/> whose LAND could not be read. Null when the object has no such member.</summary>
    public static ISet<(int X, int Y)>? UnreadableLandOf(object walk, FormKey ws)
    {
        var t = walk.GetType();
        var v = t.GetProperty("UnreadableLand", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)?.GetValue(walk)
             ?? t.GetField("UnreadableLand", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)?.GetValue(walk);
        if (v is not IEnumerable<(FormKey Ws, int X, int Y)> all) return null;
        return all.Where(x => x.Ws == ws).Select(x => (x.X, x.Y)).ToHashSet();
    }

    /// <summary>A copy of <paramref name="path"/> that keeps its TES4 header and cuts the next group header short, so the
    /// header reads and opening the plugin throws. The caller asserts both before relying on it.</summary>
    public string Damaged(string path)
    {
        var b = File.ReadAllBytes(path);
        int first = 24 + BitConverter.ToInt32(b, 4);
        var cut = new byte[first + 10];
        Buffer.BlockCopy(b, 0, cut, 0, cut.Length);
        var dir = Path.Combine(Root, "damaged"); Directory.CreateDirectory(dir);
        var outp = Path.Combine(dir, Path.GetFileName(path));
        File.WriteAllBytes(outp, cut);
        return outp;
    }

    /// <summary>A plugin that OPENS but fails while a walk reads it must be refused too, naming it: <paramref name="walk"/>
    /// runs the site's own walk over the damaged copy and must throw PluginUnreadableException carrying the file name. First
    /// the copy is shown to open and to throw while its placements are enumerated (the precondition).</summary>
    public void CheckRefusesMidWalk(string name, Action<string> walk)
    {
        var dir = Path.Combine(Root, "damaged-mid"); Directory.CreateDirectory(dir);
        var bad = Path.Combine(dir, Path.GetFileName(Paths[1]));
        File.WriteAllBytes(bad, MovedRefFixture.BreakFirstRefData(File.ReadAllBytes(Paths[1])));
        string pre;
        try
        {
            using var m = SkyrimMod.CreateFromBinaryOverlay(bad, MovedRefFixture.Rel);
            try { foreach (var cp in HousecarlCore.PlacementExtract.FromMod(m)) _ = cp.Refs.Count; pre = "opens; enumerating does NOT throw"; }
            catch (Exception ex) { pre = "opens; enumerating throws " + ex.GetType().Name; }
        }
        catch (Exception ex) { pre = "does not open: " + ex.GetType().Name; }
        Check($"{name} precondition: the damaged copy opens and throws while its placements are read", pre.StartsWith("opens; enumerating throws"), pre);
        try { walk(bad); Check($"{name} a plugin that fails mid-walk is refused, named", false, "no exception"); }
        catch (Exception ex)
        {
            Check($"{name} a plugin that fails mid-walk is refused, named",
                  ex.GetType().Name == "PluginUnreadableException" && ex.Message.Contains(Path.GetFileName(bad), StringComparison.OrdinalIgnoreCase),
                  $"{ex.GetType().Name}: {ex.Message[..Math.Min(160, ex.Message.Length)]}");
        }
    }

    /// <summary>A plugin that cannot be read must be refused, naming it: <paramref name="open"/> must throw, and its message
    /// must carry the file name. First the damaged copy is shown to throw on a plain open (the precondition).</summary>
    public void CheckRefusesUnreadable(string name, Func<string, object?> open)
    {
        var bad = Damaged(Paths[1]);
        string pre = "opened";
        try { using var m = SkyrimMod.CreateFromBinaryOverlay(bad, MovedRefFixture.Rel); } catch (Exception ex) { pre = ex.GetType().Name; }
        Check($"{name} precondition: the damaged copy throws on a plain open", pre != "opened", pre);
        try
        {
            var r = open(bad);
            Check($"{name} a plugin that cannot be read is refused, named", false, r is null ? "returned null: skipped silently" : "opened");
        }
        catch (Exception ex)
        {
            Check($"{name} a plugin that cannot be read is refused, named", ex.Message.Contains(Path.GetFileName(bad), StringComparison.OrdinalIgnoreCase),
                  $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
