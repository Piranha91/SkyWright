// Self-contained header: these files are LINKED into probes whose projects do not all
// enable ImplicitUsings or nullable, so the file carries its own context instead of
// inheriting one from whichever project happens to compile it.
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using HousecarlCore.Progress;


namespace SkyWright.Engine;

/// <summary>One child process to run. <paramref name="Env"/> entries are added to the inherited
/// environment; an empty value reaches the child as an empty variable.</summary>
public sealed record ChildJob(string Id, IReadOnlyList<string> Argv, string? Cwd = null,
                              IReadOnlyDictionary<string, string>? Env = null, string? LogicalId = null);

/// <summary>What one job did. <paramref name="Stopped"/> is null for a job that ran to its own end,
/// else "deadline", "before-launch" or "uncontained". <paramref name="Refused"/> is the reason a
/// process could not be contained, else null.</summary>
public sealed record ChildResult(string Id, int? ExitCode, double Seconds, string? RunId, int Attempt,
                                 string? Stopped = null, bool? TreeStopped = null,
                                 int? LaunchedExitCode = null, string? Refused = null)
{
    /// <summary>The exit code as Python reports it: unsigned, so a killed process reads 4294967295, not -1.</summary>
    public long? ExitCodeUnsigned => ExitCode is { } c ? unchecked((uint)c) : null;
    /// <summary>The launched process's own exit code as Python reports it (unsigned).</summary>
    public long? LaunchedExitCodeUnsigned => LaunchedExitCode is { } c ? unchecked((uint)c) : null;
}

/// <summary>A job whose command line Windows would not take; its message is the one sentence to show, and the job
/// was not started. A caller turns it into its own refusal.</summary>
public sealed class CommandLineTooLongException(string sentence) : Exception(sentence);

/// <summary>
/// Runs child processes one at a time and records each in the run log: the C# equal of
/// <c>scripts/runlog.py</c>'s <c>Runner</c> (serial path, no retries). Every call to Run opens a
/// parent run (mode <c>runner</c>) and one child run per job (mode <c>child</c>), and writes
/// <c>&lt;outDir&gt;/&lt;job id&gt;.txt</c> in the Runner's format.
///
/// With a deadline the launched process tree is the unit of work: the process is created suspended,
/// put in a kill-on-close Job Object, then resumed; a tree not empty at the deadline is terminated
/// whole and must be confirmed gone inside the cleanup allowance. Without a deadline the child is
/// run plainly, its output read through pipes. Windows only.
/// </summary>
public sealed class ChildRunner
{
    /// <summary>How long a deadline stop may take to confirm the tree is gone.</summary>
    public const double DeadlineCleanupSeconds = 15.0;

    readonly string _op;
    readonly string? _outDir;
    readonly double? _deadline;
    readonly double _cleanupSeconds;

    /// <summary>Test seam: name a containment step ("create", "limit", "assign", "resume") to make it fail.</summary>
    internal static string? FailContainmentForGuard;

    /// <param name="deadline">An absolute reading of <see cref="Now"/>, or null for no deadline.</param>
    public ChildRunner(string op, string? outDir = null, double? deadline = null, double? cleanupSeconds = null)
    {
        _op = op;
        _outDir = string.IsNullOrEmpty(outDir) ? null : outDir;
        _deadline = deadline;
        _cleanupSeconds = cleanupSeconds ?? DeadlineCleanupSeconds;
    }

    /// <summary>The monotonic clock deadlines are read on, in seconds.</summary>
    public static double Now() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    /// <summary>Run one job.</summary>
    public ChildResult Run(ChildJob job, bool echo = false) => Run(new[] { job }, echo)[0];

    /// <summary>Run jobs in order under one parent run.</summary>
    public IReadOnlyList<ChildResult> Run(IReadOnlyList<ChildJob> jobs, bool echo = false)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("ChildRunner runs on Windows only.");
        var results = new List<ChildResult>(jobs.Count);
        var parent = RunLog.Begin(_op, new RunOptions
        {
            Mode = "runner", StageSource = StageSource.Counted, Heartbeat = true,
            PredictAtStart = false, Detached = true,
        });
        try
        {
            parent.Feature("jobs", jobs.Count, "jobs");
            parent.Stage("jobs", "running jobs", jobs.Count, "jobs");
            foreach (var job in jobs)
            {
                var res = One(parent, job, echo);
                // Python counts a retry for every failed attempt, even when none is allowed; kept the same.
                if (res.Stopped is null && res.ExitCode != 0) parent.Tally(retries: 1);
                results.Add(res);
                parent.Advance();
                if (res.Stopped is not null) parent.Tally(skipped: 1);
                else parent.Tally(ok: res.ExitCode == 0 ? 1 : 0, failed: res.ExitCode == 0 ? 0 : 1);
            }
            parent.Complete();
        }
        catch (Exception ex)
        {
            parent.Fail(ErrorText(ex));
            throw;
        }
        finally { parent.Dispose(); }
        return results;
    }

    ChildResult One(Run parent, ChildJob job, bool echo)
    {
        if (_deadline is { } d && Now() >= d)
        {
            parent.Note($"deadline: job {job.Id} was not started — the deadline had already passed");
            if (echo) Console.WriteLine($"   {job.Id,-44} not started: deadline passed");
            return new ChildResult(job.Id, null, 0.0, null, 0, "before-launch");
        }
        var child = parent.Child(job.Id, new RunOptions
        {
            Mode = "child", LogicalId = job.LogicalId ?? job.Id, Attempt = 1,
            StageSource = StageSource.ExternalOpaque, PredictAtStart = false,
        });
        try
        {
            return _deadline is null ? Plain(child, job, echo) : Bounded(parent, child, job, echo, _deadline.Value);
        }
        catch (Exception ex)
        {
            child.Fail(ErrorText(ex));
            throw;
        }
        finally { child.Dispose(); }
    }

    // A plain run: no job object, output through pipes, finished when both pipes close and the process exits.
    ChildResult Plain(Run child, ChildJob job, bool echo)
    {
        var t0 = Now();
        byte[] outBytes, errBytes;
        uint code;
        using (var proc = Launched.Start(job, pipes: true, suspended: false, null, null))
        {
            var outBuf = new MemoryStream();
            var errBuf = new MemoryStream();
            var readOut = new Thread(() => Drain(proc.StdoutRead!, outBuf)) { IsBackground = true };
            var readErr = new Thread(() => Drain(proc.StderrRead!, errBuf)) { IsBackground = true };
            readOut.Start(); readErr.Start();
            readOut.Join(); readErr.Join();
            code = proc.WaitForExit(null)!.Value;
            outBytes = outBuf.ToArray();
            errBytes = errBuf.ToArray();
        }
        var secs = Now() - t0;
        // Python reads these pipes in text mode: every \r\n and lone \r becomes \n.
        var stdout = UniversalNewlines(Decode(outBytes));
        var stderr = UniversalNewlines(Decode(errBytes));
        WriteLog(child, job, $"(exit {code}, {Fixed1(secs)}s)\n\n" + Joined(stdout, stderr));
        if (echo) Console.WriteLine($"   {job.Id,-44} exit {code}  {Fixed1(secs),7}s");
        Finish(child, code);
        return new ChildResult(job.Id, unchecked((int)code), secs, child.RunId, 1);
    }

    // One job under the deadline: output to temporary files, the whole launched tree stopped at the deadline.
    ChildResult Bounded(Run parent, Run child, ChildJob job, bool echo, double deadline)
    {
        var t0 = Now();
        using var outFile = TempFile();
        using var errFile = TempFile();
        using var proc = Launched.Start(job, pipes: false, suspended: true, outFile.SafeFileHandle, errFile.SafeFileHandle);
        if (proc.Refused is not null) return Uncontained(parent, child, job, echo, proc, Now() - t0);

        bool stopped = false;
        bool? treeStopped = null;
        string? stopNote = null;
        uint? code = null;
        try
        {
            code = proc.WaitForExit(Math.Max(0.0, deadline - Now()));
            if (code is null)
            {
                stopped = true;
                (treeStopped, stopNote) = proc.Stop(_cleanupSeconds);
            }
            else if (!proc.WaitEmpty(deadline))
            {
                stopped = true;
                (treeStopped, stopNote) = proc.Stop(_cleanupSeconds);
                stopNote = $"the launched process had exited {code}, but its descendants were "
                         + $"still running at the deadline; {stopNote}";
            }
        }
        catch (Exception ex)
        {
            bool gone; string how;
            try { (gone, how) = proc.Stop(_cleanupSeconds); }
            catch (Exception again) { gone = false; how = $"the stop itself raised {again.GetType().Name}: {again.Message}"; }
            child.Record.StoppedBy = "exception";
            child.Record.TreeStopped = gone;
            child.Record.LaunchedExitCode = code is { } c0 ? unchecked((int)c0) : null;
            child.Note($"{ex.GetType().Name} inside the bounded wait for {job.Id}: launched process "
                     + $"tree terminated and {(gone ? "confirmed gone" : "NOT confirmed gone")} ({how})");
            throw;
        }
        finally { proc.CloseJob(); }

        var secs = Now() - t0;
        // Read as bytes and decoded as they are: Python does no newline translation on this path.
        var stdout = Decode(ReadAll(outFile));
        var stderr = Decode(ReadAll(errFile));
        var trailer = stopped
            ? $"(stopped at the deadline after {Fixed1(secs)}s: launched process tree "
              + $"{(treeStopped == true ? "terminated and confirmed gone" : "NOT confirmed gone")} ({stopNote}); no completion)"
            : $"(exit {code}, {Fixed1(secs)}s)";
        WriteLog(child, job, trailer + "\n\n" + Joined(stdout, stderr));
        if (echo)
            Console.WriteLine($"   {job.Id,-44} " + (stopped ? $"STOPPED at deadline {Fixed1(secs),7}s"
                                                               : $"exit {code}  {Fixed1(secs),7}s"));
        if (stopped)
        {
            int? launched = code is { } c1 ? unchecked((int)c1) : null;
            child.StopAtDeadline($"the caller's deadline passed while {job.Id} was running; process tree "
                               + $"{(treeStopped == true ? "stopped" : "NOT confirmed stopped")} ({stopNote})",
                                 launched, treeStopped);
            parent.Note($"deadline: job {job.Id} stopped after {Fixed1(secs)}s "
                      + $"(tree {(treeStopped == true ? "confirmed gone" : "NOT confirmed gone")})");
            return new ChildResult(job.Id, null, secs, child.RunId, 1, "deadline", treeStopped == true, launched);
        }
        Finish(child, code!.Value);
        return new ChildResult(job.Id, unchecked((int)code.Value), secs, child.RunId, 1);
    }

    // The process could not be put under the deadline's control, so it was ended before it ran.
    ChildResult Uncontained(Run parent, Run child, ChildJob job, bool echo, Launched proc, double secs)
    {
        var reason = $"{job.Id} was not run — {proc.Refused}; the process was created "
                   + "suspended, never ran, and was terminated "
                   + $"({(proc.RefusedGone ? "confirmed exited" : "exit NOT confirmed")})";
        WriteLog(child, job, $"(not run: containment-failed: {reason}; no completion)\n");
        if (echo) Console.WriteLine($"   {job.Id,-44} NOT RUN: containment could not be established");
        child.StopUncontained(reason, proc.RefusedGone);
        parent.Note($"deadline: job {job.Id} not run — {proc.Refused}");
        return new ChildResult(job.Id, null, secs, child.RunId, 1, "uncontained", proc.RefusedGone, null, proc.Refused);
    }

    static void Finish(Run child, uint code)
    {
        if (code == 0) { child.Tally(ok: 1); child.Complete(); }
        else child.Refuse($"exit code {code}", unchecked((int)code));
    }

    // ------------------------------------------------------------------ the log --

    // Writes "$ argv", then the given trailer and text, with Python's text-mode newlines (\n becomes \r\n).
    void WriteLog(Run child, ChildJob job, string afterCommandLine)
    {
        if (_outDir is null) return;
        Directory.CreateDirectory(_outDir);
        var path = Path.Combine(_outDir, job.Id + ".txt").Replace('/', '\\');
        var text = "$ " + string.Join(' ', job.Argv) + "\n" + afterCommandLine;
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text.Replace("\n", "\r\n")));
        child.Artifact(path);
    }

    static string Joined(string stdout, string stderr) =>
        stdout + (stderr.Length > 0 ? "\n---- stderr ----\n" + stderr : "");

    static string Decode(byte[] bytes) => new UTF8Encoding(false, false).GetString(bytes);

    static string UniversalNewlines(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');

    static string Fixed1(double seconds) => seconds.ToString("0.0", CultureInfo.InvariantCulture);

    static void Drain(Stream from, MemoryStream into)
    {
        try { from.CopyTo(into); }
        catch (IOException) { /* a broken pipe is the end of the output */ }
    }

    static byte[] ReadAll(FileStream f)
    {
        f.Seek(0, SeekOrigin.Begin);
        var all = new MemoryStream();
        f.CopyTo(all);
        return all.ToArray();
    }

    static FileStream TempFile() => new(
        Path.Combine(Path.GetTempPath(), "hc-runner-" + Guid.NewGuid().ToString("N")),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.DeleteOnClose);

    // An error as the run record states it; a failed launch is worded as Python words it.
    internal static string ErrorText(Exception ex)
    {
        if (ex is Win32Exception w)
        {
            var name = w.NativeErrorCode switch
            {
                2 or 3 => "FileNotFoundError",
                5 => "PermissionError",
                267 => "NotADirectoryError",
                _ => "OSError",
            };
            return $"{name}: [WinError {w.NativeErrorCode}] {w.Message.TrimEnd('.', ' ', '\r', '\n')}";
        }
        return $"{ex.GetType().Name}: {ex.Message}";
    }

    // ------------------------------------------------------------- command line --

    /// <summary>The Windows command line for an argument list, quoted as Python's
    /// <c>subprocess.list2cmdline</c> quotes it.</summary>
    public static string CommandLine(IReadOnlyList<string> argv)
    {
        var sb = new StringBuilder();
        foreach (var arg in argv)
        {
            if (sb.Length > 0) sb.Append(' ');
            var quote = arg.Length == 0 || arg.Contains(' ') || arg.Contains('\t');
            if (quote) sb.Append('"');
            var slashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { sb.Append('\\', slashes * 2); sb.Append("\\\""); }
                else { sb.Append('\\', slashes); sb.Append(c); }
                slashes = 0;
            }
            sb.Append('\\', slashes);
            if (quote) { sb.Append('\\', slashes); sb.Append('"'); }
        }
        return sb.ToString();
    }

    /// <summary>The most characters Windows takes in a command line (CreateProcessW's 32,767, its closing null included).</summary>
    public const int MaxCommandLine = 32766;

    /// <summary>The sentence for a job whose command line Windows would not take, or null when it fits.</summary>
    public static string? CommandLineTooLong(ChildJob job)
    {
        int length = CommandLine(job.Argv).Length;
        return length <= MaxCommandLine ? null
            : $"the command line for {job.Id} would be {length.ToString("#,0", CultureInfo.InvariantCulture)} characters, more than the 32,767 "
              + "Windows allows, so it was not started; this is a fault in SkyWright, not in your mods: report it with the run folder.";
    }

    // -------------------------------------------------------------- environment --

    /// <summary>The child's environment block: the inherited variables, names upper-cased as
    /// Python's <c>os.environ</c> holds them, with the job's entries replacing or joining them.</summary>
    internal static string EnvironmentBlock(IReadOnlyDictionary<string, string>? add)
    {
        var entries = new List<KeyValuePair<string, string>>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Set(string name, string value)
        {
            if (index.TryGetValue(name, out var at)) entries[at] = new(name, value);
            else { index[name] = entries.Count; entries.Add(new(name, value)); }
        }
        var block = Native.GetEnvironmentStringsW();
        if (block != IntPtr.Zero)
        {
            try
            {
                for (var p = block; ;)
                {
                    var entry = Marshal.PtrToStringUni(p);
                    if (string.IsNullOrEmpty(entry)) break;
                    p += (entry.Length + 1) * 2;
                    var eq = entry.IndexOf('=');
                    if (eq <= 0) continue;                      // "=C:=..." drive entries are not passed on
                    Set(entry[..eq].ToUpperInvariant(), entry[(eq + 1)..]);
                }
            }
            finally { Native.FreeEnvironmentStringsW(block); }
        }
        if (add is not null)
            foreach (var kv in add) Set(kv.Key, kv.Value);
        // Python sorts the block by name, case ignored.
        var sorted = new List<KeyValuePair<string, string>>(entries);
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++) order[entries[i].Key] = i;
        sorted.Sort((x, y) =>
        {
            var c = string.Compare(x.Key, y.Key, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : order[x.Key].CompareTo(order[y.Key]);
        });
        var sb = new StringBuilder();
        foreach (var kv in sorted) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        sb.Append('\0');
        return sb.ToString();
    }

    // ------------------------------------------------------- the launched process --

    sealed class Launched : IDisposable
    {
        IntPtr _process, _job;
        public FileStream? StdoutRead, StderrRead;
        public string? Refused;
        public bool RefusedGone;

        // Starts the job's process with stdin inherited and stdout/stderr on pipes or on the given files.
        public static Launched Start(ChildJob job, bool pipes, bool suspended, SafeFileHandle? outFile, SafeFileHandle? errFile)
        {
            // checked before anything is opened: Windows' own refusal (error 206) names a file, not the command line
            if (CommandLineTooLong(job) is { } tooLong) throw new CommandLineTooLongException(tooLong);
            var me = new Launched();
            var self = Native.GetCurrentProcess();
            IntPtr childIn = IntPtr.Zero, childOut = IntPtr.Zero, childErr = IntPtr.Zero;
            IntPtr attrs = IntPtr.Zero, handleList = IntPtr.Zero;
            try
            {
                childIn = InheritableStdin(self);
                if (pipes)
                {
                    me.StdoutRead = Pipe(self, out childOut);
                    me.StderrRead = Pipe(self, out childErr);
                }
                else
                {
                    childOut = Inheritable(self, outFile!.DangerousGetHandle());
                    childErr = Inheritable(self, errFile!.DangerousGetHandle());
                }

                // Only these three handles are inherited, as Python's close_fds does it.
                handleList = Marshal.AllocHGlobal(3 * IntPtr.Size);
                Marshal.WriteIntPtr(handleList, 0, childIn);
                Marshal.WriteIntPtr(handleList, IntPtr.Size, childOut);
                Marshal.WriteIntPtr(handleList, 2 * IntPtr.Size, childErr);
                var size = IntPtr.Zero;
                Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                attrs = Marshal.AllocHGlobal(size);
                if (!Native.InitializeProcThreadAttributeList(attrs, 1, 0, ref size)
                    || !Native.UpdateProcThreadAttribute(attrs, 0, (IntPtr)0x00020002, handleList,
                                                         (IntPtr)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                var si = new Native.STARTUPINFOEX();
                si.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
                si.StartupInfo.dwFlags = 0x00000100;            // STARTF_USESTDHANDLES
                si.StartupInfo.hStdInput = childIn;
                si.StartupInfo.hStdOutput = childOut;
                si.StartupInfo.hStdError = childErr;
                si.lpAttributeList = attrs;
                // CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT, and CREATE_SUSPENDED under a deadline
                uint flags = 0x00000400 | 0x00080000 | (suspended ? 0x00000004u : 0u);
                var env = Encoding.Unicode.GetBytes(EnvironmentBlock(job.Env));
                var commandLine = new StringBuilder(CommandLine(job.Argv));
                if (!Native.CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, true, flags, env,
                                           string.IsNullOrEmpty(job.Cwd) ? null : job.Cwd, ref si, out var pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                me._process = pi.hProcess;
                if (suspended) me.Contain(pi.hThread);
                Native.CloseHandle(pi.hThread);
                return me;
            }
            catch
            {
                me.Dispose();
                throw;
            }
            finally
            {
                if (attrs != IntPtr.Zero) { Native.DeleteProcThreadAttributeList(attrs); Marshal.FreeHGlobal(attrs); }
                if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
                foreach (var h in new[] { childIn, childOut, childErr })
                    if (h != IntPtr.Zero) Native.CloseHandle(h);
            }
        }

        // Puts the suspended process in a kill-on-close job and resumes it; on any failure ends it unrun.
        void Contain(IntPtr thread)
        {
            string? why = null;
            var fault = FailContainmentForGuard;
            var job = fault == "create" ? IntPtr.Zero : Native.CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
                why = $"CreateJobObject failed (Windows error {Marshal.GetLastWin32Error()})";
            else if (fault == "limit" || !SetKillOnClose(job))
                why = "SetInformationJobObject could not set JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE "
                    + $"(Windows error {Marshal.GetLastWin32Error()})";
            else if (fault == "assign" || !Native.AssignProcessToJobObject(job, _process))
                why = $"AssignProcessToJobObject refused the launched process (Windows error {Marshal.GetLastWin32Error()})";
            else
            {
                var previous = fault == "resume" ? 0xFFFFFFFF : Native.ResumeThread(thread);
                if (previous == 0xFFFFFFFF)
                    why = $"ResumeThread failed (Windows error {Marshal.GetLastWin32Error()})";
                else if (previous != 1)
                    why = $"the launched thread's suspend count was {previous}, not 1; it was not left running as expected";
            }
            if (why is null) { _job = job; return; }

            Native.TerminateProcess(_process, 1);
            RefusedGone = Native.WaitForSingleObject(_process, 10_000) == 0;
            if (job != IntPtr.Zero) Native.CloseHandle(job);
            Refused = why;
        }

        static bool SetKillOnClose(IntPtr job)
        {
            // JOBOBJECT_EXTENDED_LIMIT_INFORMATION, zeroed; LimitFlags sits at offset 16.
            var info = new byte[IntPtr.Size == 8 ? 144 : 112];
            BitConverter.GetBytes(0x00002000u).CopyTo(info, 16);
            return Native.SetInformationJobObject(job, 9, info, (uint)info.Length);
        }

        // Waits for the launched process itself; null when the wait timed out. A null timeout waits for ever.
        public uint? WaitForExit(double? seconds)
        {
            var ms = seconds is { } s ? (uint)Math.Min(int.MaxValue, (long)(s * 1000)) : 0xFFFFFFFF;
            if (Native.WaitForSingleObject(_process, ms) != 0) return null;
            return Native.GetExitCodeProcess(_process, out var code) ? code : null;
        }

        int? ActiveProcesses()
        {
            // JOBOBJECT_BASIC_ACCOUNTING_INFORMATION; ActiveProcesses sits at offset 40.
            var info = new byte[48];
            if (!Native.QueryInformationJobObject(_job, 1, info, 48, IntPtr.Zero)) return null;
            return BitConverter.ToInt32(info, 40);
        }

        // After the launched process exited: true once no process of its tree is left, false at the deadline.
        public bool WaitEmpty(double deadline)
        {
            while (true)
            {
                if (_job != IntPtr.Zero && ActiveProcesses() == 0) return true;
                var left = deadline - Now();
                if (left <= 0) return false;
                Thread.Sleep(TimeSpan.FromSeconds(Math.Min(0.05, left)));
            }
        }

        // Ends the whole tree and says whether it was confirmed gone inside the allowance.
        public (bool Gone, string How) Stop(double allowance)
        {
            var until = Now() + allowance;
            if (_job == IntPtr.Zero)
                return (false, "no job object: the launch was refused, so no tree was contained");
            Native.TerminateJobObject(_job, 124);
            while (Now() < until)
            {
                if (ActiveProcesses() == 0) return (true, "job object terminated; 0 active processes");
                Thread.Sleep(50);
            }
            var left = ActiveProcesses();
            return (false, $"job object terminated; {(left is { } n ? n.ToString(CultureInfo.InvariantCulture) : "None")} process(es) still active");
        }

        public void CloseJob()
        {
            if (_job != IntPtr.Zero) Native.CloseHandle(_job);
            _job = IntPtr.Zero;
        }

        public void Dispose()
        {
            CloseJob();
            if (_process != IntPtr.Zero) Native.CloseHandle(_process);
            _process = IntPtr.Zero;
            StdoutRead?.Dispose();
            StderrRead?.Dispose();
        }

        static IntPtr Inheritable(IntPtr self, IntPtr handle)
        {
            if (!Native.DuplicateHandle(self, handle, self, out var copy, 0, true, 2))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return copy;
        }

        // The parent's stdin for the child; when the parent has none, a pipe whose write end is already closed.
        static IntPtr InheritableStdin(IntPtr self)
        {
            var h = Native.GetStdHandle(-10);
            if (h != IntPtr.Zero && h != new IntPtr(-1)
                && Native.DuplicateHandle(self, h, self, out var copy, 0, true, 2))
                return copy;
            if (!Native.CreatePipe(out var read, out var write, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Native.CloseHandle(write);
            try { return Inheritable(self, read); }
            finally { Native.CloseHandle(read); }
        }

        // A pipe: the returned stream reads it here, childEnd is the write end the child inherits.
        static FileStream Pipe(IntPtr self, out IntPtr childEnd)
        {
            if (!Native.CreatePipe(out var read, out var write, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try { childEnd = Inheritable(self, write); }
            catch { Native.CloseHandle(read); throw; }
            finally { Native.CloseHandle(write); }
            return new FileStream(new SafeFileHandle(read, ownsHandle: true), FileAccess.Read, 1, isAsync: false);
        }
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        const string K = "kernel32.dll";

        [DllImport(K, SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessW(string? applicationName, StringBuilder commandLine,
            IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
            byte[] environment, string? currentDirectory, ref STARTUPINFOEX startupInfo,
            out PROCESS_INFORMATION processInformation);

        [DllImport(K, SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport(K, SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute,
            IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
        [DllImport(K)]
        public static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport(K, SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
        [DllImport(K, SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr job, int infoClass, byte[] info, uint length);
        [DllImport(K, SetLastError = true)]
        public static extern bool QueryInformationJobObject(IntPtr job, int infoClass, byte[] info, uint length, IntPtr returned);
        [DllImport(K, SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport(K, SetLastError = true)]
        public static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport(K, SetLastError = true)]
        public static extern uint ResumeThread(IntPtr thread);
        [DllImport(K, SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport(K, SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport(K, SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
        [DllImport(K)]
        public static extern IntPtr GetCurrentProcess();
        [DllImport(K, SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
        [DllImport(K, SetLastError = true)]
        public static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess,
            out IntPtr target, uint access, bool inherit, uint options);
        [DllImport(K, SetLastError = true)]
        public static extern bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr attributes, uint size);
        [DllImport(K, SetLastError = true)]
        public static extern IntPtr GetStdHandle(int which);
        [DllImport(K, CharSet = CharSet.Unicode)]
        public static extern IntPtr GetEnvironmentStringsW();
        [DllImport(K, CharSet = CharSet.Unicode)]
        public static extern bool FreeEnvironmentStringsW(IntPtr block);
    }
}
