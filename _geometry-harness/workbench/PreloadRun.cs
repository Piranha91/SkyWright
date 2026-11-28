using System.Diagnostics;

namespace Workbench;

/// <summary>The background preload's viewprobe process (WorkbenchHost.MaybePreload): started with its run log, run to its
/// end. Its area file and its words (<c>area_X_Y.log</c>) are the host's to read and keep. Its run records last only as long
/// as the process: nothing reads them afterwards, and a run log left in place would grow with every preload and be read
/// whole by the next one at start-up (PreloadRunTests).</summary>
internal static class PreloadRun
{
    /// <summary>Where the preload of <paramref name="file"/> keeps its run records.</summary>
    internal static string RunLogOf(string dir, string file) => Path.ChangeExtension(Path.GetFullPath(file), ".runlog");

    /// <summary>Remove a preload's run records; one plain sentence when they cannot be removed, else null.</summary>
    internal static string? Remove(string runlog)
    {
        try { if (Directory.Exists(runlog)) Directory.Delete(runlog, recursive: true); return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return $"the preload's run records at {runlog} could not be removed ({ex.Message}); delete that folder when SkyWright is closed"; }
    }

    /// <summary>The preload's start: <paramref name="program"/> with <paramref name="args"/>, working in <paramref name="dir"/>.</summary>
    internal static ProcessStartInfo Start(string program, string dir, string file, IEnumerable<string> args)
    {
        var psi = RunLogFolder.Start(program, RunLogOf(dir, file));
        psi.CreateNoWindow = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.WorkingDirectory = dir;
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Run the preload to its end; <paramref name="running"/> is told the process while it runs, then null. Its run
    /// records are removed before it starts (an earlier preload's, left by one that ended early, would be read whole) and
    /// when it ends (a removal that fails then is added to the error text; one that fails before it starts refuses it).</summary>
    internal static (int Exit, string Out, string Err) Run(ProcessStartInfo psi, Action<Process?> running)
    {
        var runlog = psi.Environment[RunLogFolder.Variable]!;
        if (Remove(runlog) is { } before) throw new IOException(before);
        var (exit, o, e) = RunToEnd(psi, running);
        return Remove(runlog) is { } stuck ? (exit, o, e + Environment.NewLine + stuck) : (exit, o, e);
    }

    static (int Exit, string Out, string Err) RunToEnd(ProcessStartInfo psi, Action<Process?> running)
    {
        using var p = Process.Start(psi)!;
        running(p);
        try
        {
            var err = p.StandardError.ReadToEndAsync();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, output, err.Result);
        }
        finally { running(null); }
    }
}
