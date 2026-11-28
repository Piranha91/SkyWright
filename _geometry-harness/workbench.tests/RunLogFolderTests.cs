using System.Text.RegularExpressions;
using HarnessShared;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>Every program the app starts keeps its run records beside what it writes, never in the user's shared run history
/// (%LOCALAPPDATA%\houseCARL\runs\history.jsonl), which a program started without a run log reads whole at start-up and
/// appends to. The owner is <see cref="RunLogFolder"/>.</summary>
public sealed class RunLogFolderTests
{
    static string Contactprobe => CheckoutLayout.ProgramExe(TestBuild.Layout.Root, "contactprobe");

    static int Lines(string f) => File.Exists(f) ? File.ReadLines(f).Count(l => l.Length > 0) : 0;

    static string NewFolder()
    {
        var d = Path.Combine(Path.GetTempPath(), "hc-wb-runlog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // contactprobe opens its run record at start-up and refuses a missing --base at once: a cheap real program to start
    static string[] Refused(string folder) => new[] { "--base", Path.Combine(folder, "no-such-base.json"), "--emit", Path.Combine(folder, "e.json") };

    [Fact]
    public void A_recheck_program_keeps_its_run_record_beside_its_log()
    {
        var d = NewFolder();
        try
        {
            var log = Path.Combine(d, "step.log");
            var (exit, text) = Recheck.Run(Contactprobe, Refused(d), log);
            Assert.True(exit == 2, $"contactprobe exit {exit}: {text}");
            Assert.True(Lines(Path.Combine(d, "runlog", "history.jsonl")) >= 1, "no run record beside the step's log, so it went to another store");
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void A_program_started_by_the_owner_leaves_a_planted_shared_store_unchanged()
    {
        var d = NewFolder();
        try
        {
            // the user's store moved to a planted one (HOUSECARL_DATA_DIR) for this start only, holding three records
            var shared = Path.Combine(d, "user-data", "runs", "history.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
            File.WriteAllLines(shared, new[] { "{}", "{}", "{}" });
            foreach (var (label, runlog) in new[] { ("a re-check step", RunLogFolder.Beside(Path.Combine(d, "step.log"))),
                                                    ("the preload", RunLogFolder.In(Path.Combine(d, "preload"))),
                                                    ("a map tile run", RunLogFolder.In(Path.Combine(d, "runs"))) })
            {
                var psi = RunLogFolder.Start(Contactprobe, runlog);
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                foreach (var a in Refused(d)) psi.ArgumentList.Add(a);
                psi.Environment["HOUSECARL_DATA_DIR"] = Path.Combine(d, "user-data");
                var (exit, text) = Recheck.Run(psi, null);
                Assert.True(exit == 2, $"{label}: contactprobe exit {exit}: {text}");
                Assert.True(Lines(shared) == 3, $"{label}: the planted shared store has {Lines(shared)} records, want the 3 planted");
                Assert.True(Lines(Path.Combine(runlog, "history.jsonl")) >= 1, $"{label}: no run record in {runlog}");
            }
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void Every_program_the_app_starts_gets_its_start_from_the_owner()
    {
        var app = Path.Combine(CheckoutLayout.FindRoot()!, "_geometry-harness", "workbench");
        // a ProcessStartInfo made anywhere but the owner, and Process.Start given a program by name, would skip the run log
        var made = new Regex(@"new\s+(System\.Diagnostics\.)?ProcessStartInfo\s*\(|Process\.Start\s*\(\s*(""|@""|\$""|Path\.)");
        // opening a text file in the user's viewer (UseShellExecute) starts no program of ours
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "RunLogFolder.cs", "SessionLog.cs" };
        var found = Directory.GetFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => !allowed.Contains(Path.GetFileName(f)))
            .SelectMany(f => File.ReadLines(f).Select((l, i) => (f, l, i)).Where(x => made.IsMatch(x.l)).Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}"))
            .ToList();
        Assert.True(found.Count == 0, $"{found.Count} program start(s) made outside RunLogFolder: {string.Join(", ", found)}");
        Assert.Contains("UseShellExecute = true", File.ReadAllText(Path.Combine(app, "SessionLog.cs")));
    }
}
