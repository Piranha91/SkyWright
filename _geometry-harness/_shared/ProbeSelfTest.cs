namespace HarnessShared;

// The frame a probe's --self-test runs in: a fixture written to a folder, a PASS/FAIL line per case, the tally, exit 0
// when every case passes. PD_FIXTURE_KEEP=<dir> keeps the fixture there (the folder must be empty or absent), so a
// probe run can read the same bytes the self-test checked.
public sealed class ProbeSelfTest
{
    int _pass, _fail;
    public string Root { get; private set; } = "";

    public void Check(string name, bool ok, string detail)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"   {(ok ? "PASS" : "FAIL"),-5} {name,-92} {detail}");
    }

    /// <summary>Make the fixture folder, run <paramref name="body"/> in it, print the tally.</summary>
    public static int Run(string probe, string what, Action<ProbeSelfTest> body)
    {
        var t = new ProbeSelfTest();
        Console.WriteLine($"{probe} --self-test: {what}");
        string? keep = Environment.GetEnvironmentVariable("PD_FIXTURE_KEEP") is { Length: > 0 } k ? Path.GetFullPath(k) : null;
        if (keep is not null && Directory.Exists(keep) && Directory.EnumerateFileSystemEntries(keep).Any())
        { Console.WriteLine($"PD_FIXTURE_KEEP folder '{keep}' is not empty; refusing"); return 2; }
        t.Root = keep ?? Path.Combine(Path.GetTempPath(), $"pd-{probe}-{Environment.ProcessId}");
        Directory.CreateDirectory(t.Root);
        try { body(t); }
        catch (Exception ex) { t.Check("fixture/body", false, $"threw {ex.GetType().Name}: {ex.Message}"); }
        finally { if (keep is null) { try { Directory.Delete(t.Root, true); } catch { } } }
        Console.WriteLine($"{probe} --self-test: {t._pass} passed, {t._fail} failed");
        return t._fail == 0 ? 0 : 1;
    }
}
