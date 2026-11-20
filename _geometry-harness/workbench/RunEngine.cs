using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Workbench;

/// <summary>Which run controller the workbench starts: Python (<c>evalpipeline.py</c> and <c>evalrun.py</c>, the default)
/// or the built-in one (<c>runcontrol.exe</c>, no Python). This is the one place that knows either's command lines: the
/// run, the bundle check, a probe build's digest, and a patch's population. With the built-in engine nothing here starts
/// Python or reads a <c>.py</c> path.
///
/// Variables: <c>WB_CONTROLLER</c> (python or csharp) overrides the saved setting for a test or a scripted run.
/// <c>WB_RUNCONTROL</c> names runcontrol.exe (default: this checkout's _geometry-harness/runcontrol/bin/Release/net9.0).
/// <c>WB_HARNESS</c> names the harness folder the probes are built under (default: this checkout's _geometry-harness;
/// with Python, the folder of <c>WB_EVALRUN</c>'s evalrun.py when only that is set).
/// <c>WB_PIPELINE_CMD</c> is the test seam for the run under both engines: with Python it replaces the script Python
/// runs (evalpipeline.py); with the built-in engine it replaces the program started (runcontrol.exe). Either way the
/// stand-in gets the same arguments, <c>run --entry … --machine … --out … --through bundle --reuse-preparation-from …</c>.
/// <c>WB_PIPELINE</c> and <c>WB_EVALRUN</c> name evalpipeline.py and evalrun.py, and are read with Python only.</summary>
internal sealed class RunEngine
{
    internal const string Python = "python", CSharp = "csharp";

    internal string Kind { get; }
    readonly string? _evalrunPy, _exe;
    readonly string _python;

    RunEngine(string kind, string? evalrunPy, string python, string? exe) { Kind = kind; _evalrunPy = evalrunPy; _python = python; _exe = exe; }

    internal bool BuiltIn => Kind == CSharp;

    /// <summary>The Python engine; a test names its own evalrun.py and interpreter.</summary>
    internal static RunEngine OfPython(string? evalrunPy = null, string python = "python") => new(Python, evalrunPy, python, null);

    /// <summary>The built-in engine; a test names its own runcontrol.exe.</summary>
    internal static RunEngine OfBuiltIn(string? exe = null) => new(CSharp, null, "python", exe);

    /// <summary>The engine a setting's value names; anything but python or csharp is refused.</summary>
    internal static RunEngine Of(string? kind) => Name(kind) == CSharp ? OfBuiltIn() : OfPython();

    /// <summary>A setting's value as written (any case), or a one-sentence refusal.</summary>
    internal static string Name(string? kind) => (kind ?? "").Trim().ToLowerInvariant() switch
    {
        Python => Python,
        CSharp or "built-in" or "builtin" => CSharp,
        _ => throw new InvalidOperationException($"'{kind}' is not a run engine; use python or csharp."),
    };

    /// <summary>WB_CONTROLLER's value, or null when it is not set.</summary>
    internal static string? FromEnv() => Environment.GetEnvironmentVariable("WB_CONTROLLER") is { Length: > 0 } v ? v : null;

    /// <summary>The engine's name as the Start tab shows it.</summary>
    internal static string Label(string kind) => kind == CSharp ? "Built-in (no Python needed)" : "Python (old pipeline)";

    /// <summary>The verbs' names in a refusal: which program said no.</summary>
    internal string Controller => BuiltIn ? "runcontrol" : "evalrun";

    string EvalrunPy() => _evalrunPy ?? Program.FromCheckoutPublic("WB_EVALRUN", "evaluation controller evalrun.py", "_geometry-harness", "evalrun.py");

    /// <summary>runcontrol.exe, or one sentence saying where it should be.</summary>
    internal string ControllerExe()
    {
        var exe = _exe ?? Program.FromCheckoutPublic("WB_RUNCONTROL", "built-in run engine runcontrol.exe", "_geometry-harness", "runcontrol", "bin", "Release", "net9.0", "runcontrol.exe");
        if (!File.Exists(exe))
            throw new InvalidOperationException($"the built-in run engine is not at '{exe}'; build it (dotnet build _geometry-harness/runcontrol -c Release), set WB_RUNCONTROL to runcontrol.exe, or switch the run engine to Python.");
        return exe;
    }

    /// <summary>Refuse now, in one sentence, when the program a run would start is not there.</summary>
    internal void NeedProgram()
    {
        if (BuiltIn && Environment.GetEnvironmentVariable("WB_PIPELINE_CMD") is not { Length: > 0 }) ControllerExe();
    }

    /// <summary>The folder the probes are built under (conflictdiff, navownprobe, a re-check's local probe builds).</summary>
    internal string HarnessDir()
    {
        if (Environment.GetEnvironmentVariable("WB_HARNESS") is { Length: > 0 } h) return h;
        if (!BuiltIn) return Path.GetDirectoryName(Path.GetFullPath(EvalrunPy()))!;
        return _exe is not null ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_exe))!, "..", "..", "..", ".."))
            : Program.FromCheckoutPublic("WB_HARNESS", "harness folder (_geometry-harness)", "_geometry-harness");
    }

    static ProcessStartInfo Start(string program, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>The built-in engine's own command: it writes UTF-8, and its run records go where the caller keeps them.</summary>
    ProcessStartInfo BuiltInStart(string program, IEnumerable<string> args, string runlogDir)
    {
        var psi = Start(program, args);
        psi.StandardOutputEncoding = Encoding.UTF8; psi.StandardErrorEncoding = Encoding.UTF8;
        psi.Environment["HOUSECARL_RUNLOG_DIR"] = runlogDir;                         // set, not left to a default
        return psi;
    }

    /// <summary>The run of an entry through the bundle, its run log under <paramref name="runlogDir"/>.</summary>
    internal ProcessStartInfo Pipeline(string entry, string machine, string run, string workFolder, string runlogDir)
    {
        var args = new[] { "run", "--entry", entry, "--machine", machine, "--out", run, "--through", "bundle", "--reuse-preparation-from", workFolder };
        var seam = Environment.GetEnvironmentVariable("WB_PIPELINE_CMD") is { Length: > 0 } pc ? pc : null;
        if (BuiltIn) return BuiltInStart(seam ?? ControllerExe(), args, runlogDir);
        var script = seam ?? Program.FromCheckoutPublic("WB_PIPELINE", "evaluation pipeline evalpipeline.py", "_geometry-harness", "evalpipeline.py");
        var psi = Start(_python, new[] { "-I", script }.Concat(args));
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["HOUSECARL_RUNLOG_DIR"] = runlogDir;                         // set, not left to a default
        return psi;
    }

    /// <summary>The bundle check: <c>verify-bundle DIR --out FILE</c>, which prints a <c>verify:</c> line and writes FILE.</summary>
    internal ProcessStartInfo Verify(string dir, string verifyOut)
    {
        if (BuiltIn) return BuiltInStart(ControllerExe(), new[] { "verify-bundle", dir, "--out", verifyOut }, RunlogBeside(verifyOut));
        var psi = Start(_python, new[] { EvalrunPy(), "verify-bundle", dir, "--out", verifyOut });
        psi.Environment["PYTHONIOENCODING"] = "utf-8"; psi.Environment["PYTHONUTF8"] = "1";
        return psi;
    }

    /// <summary>What to say when the engine's program cannot be started at all.</summary>
    internal string CannotStart => BuiltIn ? "the built-in run engine could not be started" : $"'{_python}' could not be started; put Python on PATH";

    /// <summary>A patch's population file: <c>population --plugin … --mod … --instance … --out …</c>.</summary>
    internal ProcessStartInfo Population(string plugin, string mod, string instance, string outFile)
    {
        var args = new[] { "population", "--plugin", plugin, "--mod", mod, "--instance", instance, "--out", outFile };
        return BuiltIn ? BuiltInStart(ControllerExe(), args, RunlogBeside(outFile)) : Start(_python, new[] { EvalrunPy() }.Concat(args));
    }

    /// <summary>The package digest of a probe's build folder (<c>folder:…</c>), by the engine's own identity rule; null
    /// when it gave none. The built-in engine's run record goes under <paramref name="workDir"/>.</summary>
    internal string? PackageDigest(string folder, string workDir)
    {
        if (!BuiltIn)
        {
            var (exit, text) = Recheck.Run(Start(_python, new[] { "-c", "import sys; sys.path.insert(0, sys.argv[1]); import toolidentity; print(toolidentity.package(sys.argv[2])['digest'])", HarnessDir(), folder }), null);
            var line = text.Trim().Split('\n').LastOrDefault()?.Trim();
            return exit == 0 && line is not null && line.StartsWith("folder:") ? line : null;
        }
        var psi = BuiltInStart(ControllerExe(), new[] { "identity", folder }, Path.Combine(workDir, "runlog"));
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

    static string RunlogBeside(string file) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, "runlog");
}
