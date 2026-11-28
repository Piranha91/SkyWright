using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Workbench;

/// <summary>The run controller the workbench starts: the built-in one, <c>runcontrol.exe</c>, which needs no Python.
/// This is the one place that knows its command lines: the run, the bundle check, a probe build's digest, and a patch's
/// population.
///
/// Variables: <c>WB_RUNCONTROL</c> names runcontrol.exe, and <c>WB_HARNESS</c> the build folder the programs sit under;
/// by default both come from the build the workbench sits in (see HarnessShared.CheckoutLayout).
/// <c>WB_PIPELINE_CMD</c> is the test seam for the run: it replaces the program started, which gets the same arguments,
/// <c>run --entry … --machine … --out … --through bundle --reuse-preparation-from …</c>.</summary>
internal sealed class RunEngine
{
    readonly string? _exe;

    RunEngine(string? exe) { _exe = exe; }

    /// <summary>The built-in engine; a test names its own runcontrol.exe.</summary>
    internal static RunEngine OfBuiltIn(string? exe = null) => new(exe);

    /// <summary>The verbs' names in a refusal: which program said no.</summary>
    internal string Controller => "runcontrol";

    /// <summary>runcontrol.exe, or one sentence saying where it should be.</summary>
    internal string ControllerExe()
    {
        var exe = _exe ?? Program.Piece(HarnessShared.CheckoutLayout.Piece.Runcontrol, "built-in run engine runcontrol.exe");
        if (!File.Exists(exe))
            throw new InvalidOperationException($"the built-in run engine is not at '{exe}'; build the solution (scripts\\build.ps1), or set WB_RUNCONTROL to runcontrol.exe.");
        return exe;
    }

    /// <summary>Refuse now, in one sentence, when the program a run would start is not there.</summary>
    internal void NeedProgram()
    {
        if (Environment.GetEnvironmentVariable("WB_PIPELINE_CMD") is not { Length: > 0 }) ControllerExe();
    }

    /// <summary>The folder the probes are built under (conflictdiff, navownprobe, a re-check's local probe builds).</summary>
    internal string HarnessDir()
    {
        if (Environment.GetEnvironmentVariable(HarnessShared.CheckoutLayout.OverrideVariable(HarnessShared.CheckoutLayout.Piece.Harness)) is { Length: > 0 } h) return h;
        return _exe is not null ? HarnessShared.CheckoutLayout.HarnessOf(_exe)
            : Program.Piece(HarnessShared.CheckoutLayout.Piece.Harness, "build folder the programs sit under (bin\\<Configuration>\\<.NET version>)");
    }

    /// <summary>The engine's own command: it writes UTF-8, and its run records go where the caller keeps them.</summary>
    static ProcessStartInfo Start(string program, IEnumerable<string> args, string runlogDir)
    {
        var psi = RunLogFolder.Start(program, runlogDir);
        psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.StandardOutputEncoding = Encoding.UTF8; psi.StandardErrorEncoding = Encoding.UTF8;
        return psi;
    }

    /// <summary>The run of an entry through the bundle, its run log under <paramref name="runlogDir"/>.</summary>
    internal ProcessStartInfo Pipeline(string entry, string machine, string run, string workFolder, string runlogDir)
    {
        var args = new[] { "run", "--entry", entry, "--machine", machine, "--out", run, "--through", "bundle", "--reuse-preparation-from", workFolder };
        var seam = Environment.GetEnvironmentVariable("WB_PIPELINE_CMD") is { Length: > 0 } pc ? pc : null;
        return Start(seam ?? ControllerExe(), args, runlogDir);
    }

    /// <summary>The bundle check: <c>verify-bundle DIR --out FILE</c>, which prints a <c>verify:</c> line and writes FILE.</summary>
    internal ProcessStartInfo Verify(string dir, string verifyOut) =>
        Start(ControllerExe(), new[] { "verify-bundle", dir, "--out", verifyOut }, RunlogBeside(verifyOut));

    /// <summary>What to say when the engine's program cannot be started at all.</summary>
    internal string CannotStart => "the built-in run engine could not be started";

    /// <summary>A patch's population file: <c>population --plugin … --mod … --instance … --out …</c>.</summary>
    internal ProcessStartInfo Population(string plugin, string mod, string instance, string outFile) =>
        Start(ControllerExe(), new[] { "population", "--plugin", plugin, "--mod", mod, "--instance", instance, "--out", outFile }, RunlogBeside(outFile));

    /// <summary>The package digest of a probe's build folder (<c>folder:…</c>), by the engine's own identity rule; null
    /// when it gave none. The engine's run record goes under <paramref name="workDir"/>.</summary>
    internal string? PackageDigest(string folder, string workDir)
    {
        var psi = Start(ControllerExe(), new[] { "identity", folder }, Path.Combine(workDir, "runlog"));
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();                                 // drained, so a full pipe cannot stall it
        var said = p.StandardOutput.ReadToEnd();
        p.WaitForExit(); err.Wait();
        if (p.ExitCode != 0) return null;
        try
        {
            using var d = JsonDocument.Parse(said);
            return d.RootElement.TryGetProperty("digest", out var g) && g.GetString() is { } s && s.StartsWith("folder:") ? s : null;
        }
        catch (JsonException) { return null; }
    }

    static string RunlogBeside(string file) => RunLogFolder.Beside(file);
}
