using System.Diagnostics;

namespace Workbench;

/// <summary>The background preload's viewprobe process (WorkbenchHost.MaybePreload): started with its run log, run to its
/// end. Its area file and its words (<c>area_X_Y.log</c>) are the host's to read and keep.</summary>
internal static class PreloadRun
{
    /// <summary>Where the preload of <paramref name="file"/> keeps its run records.</summary>
    internal static string RunLogOf(string dir, string file) => RunLogFolder.In(dir);

    /// <summary>The preload's start: <paramref name="program"/> with <paramref name="args"/>, working in <paramref name="dir"/>.</summary>
    internal static ProcessStartInfo Start(string program, string dir, string file, IEnumerable<string> args)
    {
        var psi = RunLogFolder.Start(program, RunLogOf(dir, file));
        psi.CreateNoWindow = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.WorkingDirectory = dir;
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Run the preload to its end; <paramref name="running"/> is told the process while it runs, then null.</summary>
    internal static (int Exit, string Out, string Err) Run(ProcessStartInfo psi, Action<Process?> running)
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
