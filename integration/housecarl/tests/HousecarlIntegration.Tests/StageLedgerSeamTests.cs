using System.Diagnostics;
using System.Text;
using RunControl;
using Xunit;

namespace SkyWright.Engine.Tests;

/// <summary>The stage ledger both controllers write on one run folder (RUN/stages.json): runcontrol's StageLedger (C#,
/// referenced from the app's build) and the module's evalpipeline.StageLedger (Python, started here). Each must reuse a
/// receipt the other recorded, and run again once an output changes. Moved from runcontrol's self-test ("live: Python
/// reuses a stage this program recorded", "live: this program reuses a stage Python recorded") when its live
/// cross-checks with the module's Python left it.</summary>
public sealed class StageLedgerSeamTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-ledger-seam-" + Guid.NewGuid().ToString("N")[..12]);
    string At(string rel) => Path.Combine(_root, rel);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string Module()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "evalpipeline.py")) && File.Exists(Path.Combine(d.FullName, "skywright_paths.py"))) return d.FullName;
        throw new InvalidOperationException($"the integration module's evalpipeline.py is not above {AppContext.BaseDirectory}");
    }

    // Python's last printed line, or the failure in one sentence (a failure is an assertion here, never a skip).
    static string Python(string code, params string[] args)
    {
        var psi = new ProcessStartInfo("python")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (string a in new[] { "-X", "utf8", "-c", "import sys, json; sys.path.insert(0, sys.argv[1]); " + code, Module() }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("python did not start: put Python 3 on PATH.");
        var err = p.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"python exited {p.ExitCode}: {err.Result}");
        return stdout.TrimEnd().Split('\n').Last().TrimEnd('\r');
    }

    const string Decide = "import evalpipeline as EP; print(EP.StageLedger(sys.argv[2]).decide(sys.argv[3], json.load(open(sys.argv[4], encoding='utf-8'))))";
    const string Record = "import evalpipeline as EP; EP.StageLedger(sys.argv[2]).record(sys.argv[3], json.load(open(sys.argv[4], encoding='utf-8')), json.loads(sys.argv[5]), 0, json.load(open(sys.argv[6], encoding='utf-8'))); print('recorded')";

    // The self-test's inputs: a non-ASCII key and a float, so both hash the same JSON the same way.
    static PyDict Inputs() => new()
    {
        ["entry"] = new string('a', 64), ["upstream"] = new PyDict { ["census"] = null }, ["tools"] = new PyDict { ["café"] = 1.5 },
    };

    void Outputs()
    {
        Directory.CreateDirectory(At("run/scope/deep"));
        File.WriteAllText(At("run/scope/scopes.json"), "{}");
        File.WriteAllText(At("run/scope/deep/B.txt"), "b");
        File.WriteAllText(At("run/scope/deep/a-1.txt"), "a");
        PyJson.WritePipelineFile(At("inputs.json"), Inputs());
        PyJson.WritePipelineFile(At("extra.json"), new PyDict { ["bound"] = null, ["seconds"] = 2.5, ["driverSha256"] = new string('d', 64) });
    }

    [Fact]
    public void Python_reuses_a_stage_runcontrol_recorded_and_runs_it_again_once_an_output_changes()
    {
        Outputs();
        new StageLedger(At("run")).Record("scope", Inputs(), [At("run/scope/scopes.json"), At("run/scope")], 0,
            new PyDict { ["bound"] = null, ["seconds"] = 2.5, ["driverSha256"] = new string('d', 64) });
        Assert.Equal("reuse", Python(Decide, At("run"), "scope", At("inputs.json")));
        File.WriteAllText(At("run/scope/deep/B.txt"), "changed");
        Assert.Equal("run", Python(Decide, At("run"), "scope", At("inputs.json")));
    }

    [Fact]
    public void Runcontrol_reuses_a_stage_Python_recorded_and_Python_reuses_it_after_runcontrol_rewrote_the_ledger()
    {
        Outputs();
        string outs = PyJson.Dumps(new List<object?> { At("run/scope/scopes.json"), At("run/scope") });
        Assert.Equal("recorded", Python(Record, At("run"), "scope", At("inputs.json"), outs, At("extra.json")));
        var ledger = new StageLedger(At("run"));
        Assert.Equal("reuse", ledger.Decide("scope", Inputs()));
        ledger.Save();
        Assert.Equal("reuse", Python(Decide, At("run"), "scope", At("inputs.json")));
        File.WriteAllText(At("run/scope/scopes.json"), "{ }");
        Assert.Equal("run", ledger.Decide("scope", Inputs()));
    }
}
