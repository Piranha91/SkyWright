using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using HousecarlCore.Progress;
using SkyWright.Engine;

namespace SkyWright.Guards;

/// <summary>
/// Known answers for the child-process runner (<see cref="ChildRunner"/>), the guard
/// <c>SkyWright.Guards child-runner</c> (moved from houseCARL's run-log guard, section 12, in step 5 of the boundary). Each case starts a small real child (cmd.exe, ping, PowerShell) against a temp
/// store and checks the result, the log file's bytes and the run records. The expected values are
/// what scripts/runlog.py's Runner produces for the same child.
/// </summary>
internal static class ChildRunnerGuard
{
    const string NestedFlag = "--nested";

    public static bool IsNestedCall(string[] args) => args.Length > 0 && args[0] == NestedFlag;

    /// <summary><c>child-runner</c>: the 31 known answers, exit 0 when every one passes; <c>--nested</c> is the child half of the nested-job case.</summary>
    public static int RunGuard(string[] args)
    {
        if (IsNestedCall(args)) return NestedChild();
        int fail = RunCases();
        Console.WriteLine(fail == 0 ? "================ CHILD-RUNNER GUARD PASS ================" : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }

    public static int RunCases()
    {
        Console.WriteLine();
        Console.WriteLine("-- 12  child runner: launch, capture, deadline stop, records");
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("  SKIP  the child runner is Windows only");
            return 0;
        }
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var sw = Stopwatch.StartNew();
        var dir = Path.Combine(Path.GetTempPath(), "hc-childrunner-guard-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(dir, "logs");
        Directory.CreateDirectory(dir);
        var savedOverride = RunPaths.Override;
        var savedOut = Console.Out;
        Environment.SetEnvironmentVariable("HC_CRG_Inherited", "from-parent");
        try
        {
            RunPaths.Override = Path.Combine(dir, "store");
            string Log(string id) => Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(logs, id + ".txt")));
            List<RunRecord> History() => RunStore.ReadHistory(out _, out _);
            RunRecord Row(string op) => History().Single(r => r.Op == op);

            // ---- the command line, against Python's list2cmdline
            var table = new (string[] Argv, string Line)[]
            {
                (new[] { "cmd.exe", "/c", "echo", "hello" }, "cmd.exe /c echo hello"),
                (new[] { "a b" }, "\"a b\""),
                (new[] { "q\"uote" }, "q\\\"uote"),
                (new[] { "trail\\" }, "trail\\"),
                (new[] { "sp trail\\" }, "\"sp trail\\\\\""),
                (new[] { "" }, "\"\""),
                (new[] { "a\\\"b" }, "a\\\\\\\"b"),
                (new[] { "tab\there" }, "\"tab\there\""),
                (new[] { "\\\\share\\x y\\" }, "\"\\\\share\\x y\\\\\""),
                (new[] { "x\\\\\"y z" }, "\"x\\\\\\\\\\\"y z\""),
                (new[] { "\"" }, "\\\""),
                (new[] { "\\" }, "\\"),
                (new[] { "plain", "", "two words", "end\\\\" }, "plain \"\" \"two words\" end\\\\"),
            };
            Check(table.All(t => ChildRunner.CommandLine(t.Argv) == t.Line),
                  "an argument list becomes the command line Python builds (spaces, quotes, trailing backslashes, empty)");

            // ---- plain success
            var plain = new ChildRunner("crg-plain", logs);
            var ok = plain.Run(new ChildJob("crg-ok", new[] { "cmd.exe", "/c", "echo", "hello" }));
            Check(ok is { ExitCode: 0, Stopped: null, Attempt: 1, TreeStopped: null, Refused: null } && ok.RunId is not null,
                  "a plain child returns its exit code 0 and no stop");
            Check(Regex.IsMatch(Log("crg-ok"), @"\A\$ cmd\.exe /c echo hello\r\n\(exit 0, \d+\.\ds\)\r\n\r\nhello\r\n\z"),
                  "its log is the command, the exit trailer, a blank line and stdout, with no stderr section");
            var okParent = Row("crg-plain");
            var okChild = Row("crg-ok");
            Check(okParent is { Mode: "runner", StageSource: StageSource.Counted, ParentRunId: null, Status: RunStatus.Completed }
                  && okParent.Features["jobs"].Value == 1 && okParent.Stages.Single() is { Id: "jobs", Processed: 1, Total: 1 }
                  && okParent.Counts is { Ok: 1, Failed: 0, Retries: 0 },
                  "the runner's own run is op/mode runner with a counted jobs stage and no parent");
            Check(okChild is { Mode: "child", LogicalId: "crg-ok", Attempt: 1, StageSource: StageSource.ExternalOpaque,
                               Status: RunStatus.Completed, Outcome: RunOutcome.Ok, ExitCode: 0, StoppedBy: null }
                  && okChild.ParentRunId == okParent.RunId && okChild.RunId == ok.RunId
                  && okChild.Pid == Environment.ProcessId
                  && okChild.Artifacts.Single().EndsWith("crg-ok.txt", StringComparison.Ordinal),
                  "the job's run is a child of it, named for the job, with the log as its artifact");

            // ---- non-zero exit, stderr section
            var bad = plain.Run(new ChildJob("crg-bad", new[] { "cmd.exe", "/c", "echo oops 1>&2 & exit /b 3" }));
            Check(bad is { ExitCode: 3, Stopped: null }, "a non-zero exit code comes back as it is");
            Check(Regex.IsMatch(Log("crg-bad"), @"\A\$ cmd\.exe /c echo oops 1>&2 & exit /b 3\r\n\(exit 3, \d+\.\ds\)\r\n\r\n\r\n---- stderr ----\r\noops  \r\n\z"),
                  "stderr follows stdout under the stderr rule only when there is some");
            Check(Row("crg-bad") is { Status: RunStatus.Completed, Outcome: RunOutcome.Refused, ExitCode: 3, Error: "exit code 3" }
                  && History().Last(r => r.Op == "crg-plain").Counts is { Failed: 1, Retries: 1 },
                  "a non-zero exit is recorded as a refusal with its code (and one retry counted, as Python counts it)");

            // ---- argv and environment, read back by a child
            var script = Path.Combine(dir, "argv.ps1");
            File.WriteAllText(script,
                "$a = [Environment]::GetCommandLineArgs()\n" +
                "$i = [Array]::IndexOf($a, '-File') + 2\n" +
                "for (; $i -lt $a.Length; $i++) { [Console]::Out.WriteLine('[' + [BitConverter]::ToString([Text.Encoding]::UTF8.GetBytes($a[$i])) + ']') }\n" +
                "$e = [Environment]::GetEnvironmentVariables()\n" +
                "foreach ($n in 'HC_CRG_EMPTY','HC_CRG_ADD','HC_CRG_INHERITED','HC_CRG_ABSENT') { [Console]::Out.WriteLine($n + '=' + $e.Contains($n) + '[' + $e[$n] + ']') }\n" +
                "[Console]::Out.WriteLine('cwd=' + (Get-Location).Path)\n");
            var tricky = new[] { "a b", "q\"uote", "trail\\", "sp trail\\", "", "a\\\"b", "tab\there", "plain" };
            var argv = new List<string> { "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script };
            argv.AddRange(tricky);
            var cwd = Path.Combine(dir, "work dir");
            Directory.CreateDirectory(cwd);
            var seen = plain.Run(new ChildJob("crg-argv", argv, cwd,
                                              new Dictionary<string, string> { ["HC_CRG_EMPTY"] = "", ["HC_CRG_ADD"] = "v1" }));
            var seenLines = Log("crg-argv").Split("\r\n").Skip(3).ToArray();
            var wantArgs = tricky.Select(t => "[" + BitConverter.ToString(Encoding.UTF8.GetBytes(t)) + "]").ToArray();
            Check(seen.ExitCode == 0 && seenLines.Take(tricky.Length).SequenceEqual(wantArgs),
                  "a child receives each argument exactly (spaces, quotes, trailing backslashes, empty, tab)");
            Check(seenLines.Contains("HC_CRG_EMPTY=True[]"), "an empty value in the job's environment reaches the child as an empty variable");
            Check(seenLines.Contains("HC_CRG_ADD=True[v1]") && seenLines.Contains("HC_CRG_INHERITED=True[from-parent]")
                  && seenLines.Contains("HC_CRG_ABSENT=False[]"),
                  "the job's entries are added to the inherited environment");
            Check(seenLines.Contains("cwd=" + cwd), "the child starts in the job's working directory");

            // ---- invalid UTF-8
            var raw = plain.Run(new ChildJob("crg-bytes", new[] { "powershell", "-NoProfile", "-Command",
                "$o=[Console]::OpenStandardOutput(); $b=[byte[]](0x61,0xFF,0x62,0xE2,0x82,0x0D,0x0A); $o.Write($b,0,$b.Length)" }));
            var rawBytes = File.ReadAllBytes(Path.Combine(logs, "crg-bytes.txt"));
            var wantTail = new byte[] { 0x61, 0xEF, 0xBF, 0xBD, 0x62, 0xEF, 0xBF, 0xBD, 0x0D, 0x0A };
            Check(raw.ExitCode == 0 && rawBytes.Length > wantTail.Length && rawBytes[^wantTail.Length..].SequenceEqual(wantTail),
                  "bytes that are not UTF-8 are written as the replacement character, one per bad sequence");

            // ---- echo lines
            var echoed = new StringWriter();
            Console.SetOut(echoed);
            try { new ChildRunner("crg-echo", logs).Run(new ChildJob("crg-echo-job", new[] { "cmd.exe", "/c", "exit", "0" }), echo: true); }
            finally { Console.SetOut(savedOut); }
            Check(Regex.IsMatch(echoed.ToString(), @"\A   crg-echo-job {32} exit 0 {6}\d\.\ds\r?\n\z"),
                  "echo prints the job's id, exit code and seconds in the Runner's columns");

            // ---- a missing program
            Exception? thrown = null;
            try { new ChildRunner("crg-missing", logs).Run(new ChildJob("crg-missing-job", new[] { "hc-no-such-program.exe", "x" })); }
            catch (Exception ex) { thrown = ex; }
            Check(thrown is System.ComponentModel.Win32Exception { NativeErrorCode: 2 }
                  && Row("crg-missing-job") is { Status: RunStatus.Failed, Outcome: RunOutcome.Error,
                                                 Error: "FileNotFoundError: [WinError 2] The system cannot find the file specified" }
                  && Row("crg-missing").Status == RunStatus.Failed && !File.Exists(Path.Combine(logs, "crg-missing-job.txt")),
                  "a program that cannot be started throws, and both runs are recorded as failed with Python's wording");

            // ---- a command line Windows would not take (more than 32,766 characters): refused in one sentence, before anything starts
            var longArgv = new[] { "cmd.exe", "/c", "exit", "0" }.Concat(Enumerable.Range(0, 3000).Select(i => $"argument-{i:0000}")).ToArray();
            int longLength = ChildRunner.CommandLine(longArgv).Length;
            Exception? tooLong = null;
            try { new ChildRunner("crg-long", logs).Run(new ChildJob("crg-long-job", longArgv)); }
            catch (Exception ex) { tooLong = ex; }
            Check(longLength > ChildRunner.MaxCommandLine && ChildRunner.CommandLineTooLong(new ChildJob("x", longArgv[..4])) is null
                  && tooLong is CommandLineTooLongException
                  && tooLong.Message == $"the command line for crg-long-job would be {longLength.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture)} characters, "
                                        + "more than the 32,767 Windows allows, so it was not started; this is a fault in SkyWright, not in your mods: report it with the run folder."
                  && Row("crg-long-job").Status == RunStatus.Failed && !File.Exists(Path.Combine(logs, "crg-long-job.txt")),
                  "a command line past Windows' limit is refused in one sentence before anything starts, and its run is recorded as failed");

            // ---- before launch
            var late = new ChildRunner("crg-late", logs, ChildRunner.Now() - 1).Run(new ChildJob("crg-late-job", new[] { "cmd.exe", "/c", "echo", "no" }));
            Check(late is { Stopped: "before-launch", ExitCode: null, RunId: null, Attempt: 0, Seconds: 0.0 }
                  && !History().Any(r => r.Op == "crg-late-job") && !File.Exists(Path.Combine(logs, "crg-late-job.txt"))
                  && Row("crg-late").Notes.Contains("deadline: job crg-late-job was not started — the deadline had already passed")
                  && Row("crg-late").Counts.Skipped == 1,
                  "a deadline already passed starts nothing: before-launch, no child run, no log");

            // ---- under a deadline, finished in time
            var quick = new ChildRunner("crg-quick", logs, ChildRunner.Now() + 30).Run(new ChildJob("crg-quick-job", new[] { "cmd.exe", "/c", "echo", "hi" }));
            Check(quick is { ExitCode: 0, Stopped: null, TreeStopped: null }
                  && Regex.IsMatch(Log("crg-quick-job"), @"\A\$ cmd\.exe /c echo hi\r\n\(exit 0, \d+\.\ds\)\r\n\r\nhi\r\r\n\z"),
                  "a child that ends before the deadline completes normally (its \\r\\n is written \\r\\r\\n, as Python writes it)");

            // ---- the tree outlives the launched process but ends in time
            var t0 = ChildRunner.Now();
            var tree = new ChildRunner("crg-tree", logs, ChildRunner.Now() + 30)
                .Run(new ChildJob("crg-tree-job", new[] { "cmd.exe", "/c", "start /b ping -n 2 127.0.0.1 >nul & exit /b 0" }));
            Check(tree is { ExitCode: 0, Stopped: null } && tree.Seconds >= 0.7 && ChildRunner.Now() - t0 < 20,
                  "a job is finished only when its whole tree has ended, not when the launched process exits");

            // ---- deadline stop of a running child
            var started = DateTime.Now.AddSeconds(-1);
            var stop = new ChildRunner("crg-stop", logs, ChildRunner.Now() + 1.0)
                .Run(new ChildJob("crg-stop-job", new[] { "cmd.exe", "/c", "ping -n 40 127.0.0.1" }));
            Check(stop is { Stopped: "deadline", TreeStopped: true, ExitCode: null, LaunchedExitCode: null } && stop.Seconds is > 0.9 and < 10,
                  "a child still running at the deadline is stopped: deadline, tree confirmed gone, no exit code");
            Check(Regex.IsMatch(Log("crg-stop-job"), @"\A\$ cmd\.exe /c ping -n 40 127\.0\.0\.1\r\n\(stopped at the deadline after \d+\.\ds: launched process tree terminated and confirmed gone \(job object terminated; 0 active processes\); no completion\)\r\n\r\n"),
                  "its log carries the stop trailer, never an exit trailer");
            var stopRow = Row("crg-stop-job");
            Check(stopRow is { Status: RunStatus.Cancelled, Outcome: RunOutcome.Unknown, ExitCode: null, StoppedBy: "deadline",
                               TreeStopped: true, LaunchedExitCode: null }
                  && stopRow.Error!.StartsWith("deadline-exceeded: the caller's deadline passed while crg-stop-job was running; process tree stopped (", StringComparison.Ordinal)
                  && Row("crg-stop").Counts is { Skipped: 1, Failed: 0 }
                  && Row("crg-stop").Notes.Any(n => Regex.IsMatch(n, @"\Adeadline: job crg-stop-job stopped after \d+\.\ds \(tree confirmed gone\)\z")),
                  "the attempt is recorded cancelled with stoppedBy deadline and treeStopped true");
            var historyText = File.ReadAllText(RunPaths.HistoryPath);
            var stopLine = historyText.Split('\n').Single(l => l.Contains("\"op\":\"crg-stop-job\""));
            var okLine = historyText.Split('\n').Single(l => l.Contains("\"op\":\"crg-ok\""));
            Check(stopLine.Contains("\"stoppedBy\":\"deadline\"") && stopLine.Contains("\"treeStopped\":true")
                  && !stopLine.Contains("launchedExitCode") && !okLine.Contains("stoppedBy") && !okLine.Contains("treeStopped"),
                  "the stop fields are written under Python's names and left out when they are null");

            // ---- deadline stop when only a descendant is left
            var orphan = new ChildRunner("crg-orphan", logs, ChildRunner.Now() + 1.0)
                .Run(new ChildJob("crg-orphan-job", new[] { "cmd.exe", "/c", "start /b ping -n 40 127.0.0.1 >nul & exit /b 7" }));
            Check(orphan is { Stopped: "deadline", TreeStopped: true, ExitCode: null, LaunchedExitCode: 7 }
                  && Log("crg-orphan-job").Contains("(the launched process had exited 7, but its descendants were still running at the deadline; job object terminated; 0 active processes)")
                  && Row("crg-orphan-job") is { StoppedBy: "deadline", LaunchedExitCode: 7, TreeStopped: true },
                  "a descendant that outlives the launched process is still stopped at the deadline, and the launched code is kept apart");
            Thread.Sleep(200);
            Check(!PingsStartedSince(started).Any(), "no process of either stopped tree is left running");

            // ---- a stop that cannot be confirmed in the allowance
            var unsure = new ChildRunner("crg-unsure", logs, ChildRunner.Now() + 0.5, cleanupSeconds: 0)
                .Run(new ChildJob("crg-unsure-job", new[] { "cmd.exe", "/c", "ping -n 40 127.0.0.1" }));
            Check(unsure is { Stopped: "deadline", TreeStopped: false }
                  && Log("crg-unsure-job").Contains("launched process tree NOT confirmed gone (job object terminated; ")
                  && Row("crg-unsure-job") is { TreeStopped: false } && Row("crg-unsure-job").Error!.Contains("NOT confirmed stopped"),
                  "a stop not confirmed inside the cleanup allowance says treeStopped false, never a clean stop");

            // ---- containment refused
            ChildRunner.FailContainmentForGuard = "assign";
            ChildResult loose;
            try { loose = new ChildRunner("crg-loose", logs, ChildRunner.Now() + 30).Run(new ChildJob("crg-loose-job", new[] { "cmd.exe", "/c", "echo", "ran" })); }
            finally { ChildRunner.FailContainmentForGuard = null; }
            Check(loose is { Stopped: "uncontained", ExitCode: null, LaunchedExitCode: null, TreeStopped: true }
                  && loose.Refused!.StartsWith("AssignProcessToJobObject refused the launched process (Windows error ", StringComparison.Ordinal)
                  && Regex.IsMatch(Log("crg-loose-job"), @"\A\$ cmd\.exe /c echo ran\r\n\(not run: containment-failed: crg-loose-job was not run — AssignProcessToJobObject refused the launched process \(Windows error \d+\); the process was created suspended, never ran, and was terminated \(confirmed exited\); no completion\)\r\n\z")
                  && Row("crg-loose-job") is { Status: RunStatus.Cancelled, StoppedBy: "containment", TreeStopped: true, ExitCode: null }
                  && Row("crg-loose-job").Error!.StartsWith("containment-failed: ", StringComparison.Ordinal),
                  "a process that cannot be put in the job never runs: uncontained, with the reason, and its output is not there");

            // ---- killed from outside while the runner waits
            foreach (var bounded in new[] { false, true })
            {
                var id = bounded ? "crg-kill-bounded" : "crg-kill-plain";
                var pidFile = Path.Combine(dir, id + ".pid");
                var killer = new Thread(() => KillWhenWritten(pidFile)) { IsBackground = true };
                killer.Start();
                var killed = new ChildRunner(id + "-runner", logs, bounded ? ChildRunner.Now() + 60 : null)
                    .Run(new ChildJob(id, new[] { "powershell", "-NoProfile", "-Command",
                                                  $"$PID | Out-File -Encoding ascii '{pidFile}'; Start-Sleep 60" }));
                killer.Join();
                Check(killed is { Stopped: null, ExitCode: -1, ExitCodeUnsigned: 4294967295 } && killed.Seconds < 30
                      && Log(id).Contains("\r\n(exit 4294967295, ")
                      && Row(id) is { Status: RunStatus.Completed, Outcome: RunOutcome.Refused, ExitCode: -1, StoppedBy: null },
                      $"a child killed from outside ({(bounded ? "under a deadline" : "plain")}) returns normally with its exit code and no stop");
            }

            // ---- inside another job
            var self = SelfCommand();
            self.Add("child-runner");
            self.Add(NestedFlag);
            var nested = new ChildRunner("crg-nested", logs, ChildRunner.Now() + 60)
                .Run(new ChildJob("crg-nested-job", self, null,
                                  new Dictionary<string, string> { ["HOUSECARL_RUNLOG_DIR"] = Path.Combine(dir, "nested-store") }));
            Check(nested is { ExitCode: 0, Stopped: null } && Log("crg-nested-job").Contains("nested stop confirmed"),
                  "a runner that is itself inside a job still contains and stops its own child's tree");
            Check(File.Exists(Path.Combine(dir, "nested-store", "history.jsonl")),
                  "a child inherits the job's HOUSECARL_RUNLOG_DIR and writes its own runs there");

            Check(!Directory.EnumerateFiles(RunPaths.ActiveDir).Any(), "no run is left active after its job returned");
            Console.WriteLine($"        (child runner cases took {sw.Elapsed.TotalSeconds:N1}s; this process "
                            + $"{(InJob() ? "was already" : "was not")} inside a job)");
        }
        catch (Exception ex)
        {
            Console.SetOut(savedOut);
            Check(false, $"the child runner cases threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            RunPaths.Override = savedOverride;
            Environment.SetEnvironmentVariable("HC_CRG_Inherited", null);
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
        return fail;
    }

    /// <summary>The child half of the nested-job case: stops a tree of its own while running inside the outer runner's job.</summary>
    public static int NestedChild()
    {
        var res = new ChildRunner("crg-nested-inner", null, ChildRunner.Now() + 1.0)
            .Run(new ChildJob("crg-nested-inner-job", new[] { "cmd.exe", "/c", "ping -n 40 127.0.0.1" }));
        var ok = res is { Stopped: "deadline", TreeStopped: true };
        Console.WriteLine(ok ? "nested stop confirmed" : $"nested stop FAILED: {res}");
        return ok ? 0 : 1;
    }

    static List<string> SelfCommand()
    {
        var exe = Environment.ProcessPath!;
        var list = new List<string> { exe };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            list.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
        return list;
    }

    static void KillWhenWritten(string pidFile)
    {
        for (var i = 0; i < 600; i++)
        {
            Thread.Sleep(50);
            try
            {
                if (!File.Exists(pidFile) || !int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid)) continue;
                Thread.Sleep(300);
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                return;
            }
            catch (IOException) { /* still being written */ }
            catch (ArgumentException) { return; }
        }
    }

    static List<int> PingsStartedSince(DateTime since)
    {
        var left = new List<int>();
        foreach (var p in Process.GetProcessesByName("PING"))
        {
            try { if (!p.HasExited && p.StartTime >= since) left.Add(p.Id); }
            catch { /* gone, or not ours to read */ }
            finally { p.Dispose(); }
        }
        return left;
    }

    static bool InJob() => IsProcessInJob(Process.GetCurrentProcess().Handle, IntPtr.Zero, out var inJob) && inJob;

    [DllImport("kernel32.dll")]
    static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
}
