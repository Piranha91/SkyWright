using System.Diagnostics;
using HarnessShared;

namespace SkyWright.Guards;

/// <summary>
/// SkyWright's run-coverage and tool-index guards. Their rules have one owner, houseCARL's generator
/// (<c>RunCoverageProbe</c> and <c>ToolIndex</c>): these run the pinned generator over SkyWright's manifest
/// (<see cref="Manifest"/>) and its question index (<see cref="Index"/>) with <c>--root</c>, so no rule is copied here.
/// The manifest took the harness rows from houseCARL's in the split's M3.
///
/// The generator is the one <c>scripts\build.ps1</c> builds inside the submodule when it makes the rulebook, and
/// <c>generated/corpus.pin</c> names the pin it was built from. One built from another pin is refused, never run, so a
/// guard cannot pass on an older rule.
/// </summary>
internal static class RegistryGuards
{
    internal const string Manifest = "docs/run-coverage.json";
    internal const string Index = "docs/TOOLS.md";

    /// <summary><c>run-coverage</c>: every owned entry point in the tree has a manifest row, and each row's adapter call,
    /// exemption and metadata hold.</summary>
    public static int RunCoverage(string[] args)
    {
        if (args.Length > 0) { Console.Error.WriteLine("run-coverage takes no options."); return 2; }
        return RunGenerator(repo => ["run-coverage-guard", "--root", repo, "--manifest", Manifest]);
    }

    /// <summary><c>tool-index [--write]</c>: the index is what the manifest renders to (checked), or is rewritten.</summary>
    public static int ToolIndex(string[] args)
    {
        if (args is not ([] or ["--write"])) { Console.Error.WriteLine("tool-index takes only --write, to rewrite docs/TOOLS.md."); return 2; }
        var mode = args.Length == 1 ? "--write" : "--check";
        return RunGenerator(repo => ["tool-index", mode, "--root", repo, "--manifest", Manifest, "--index", Index]);
    }

    static int RunGenerator(Func<string, string[]> argsFor)
    {
        var repo = CheckoutLayout.FindRoot();
        if (repo is null)
        {
            Console.Error.WriteLine($"no SkyWright checkout ({CheckoutLayout.Marker}) above {AppContext.BaseDirectory}; run the guard from a checkout's build.");
            return 1;
        }
        var sub = Path.Combine(repo, "external", "houseCARL");
        var exe = Path.Combine(sub, "src", "housecarl-generator", "bin", "Release", "net9.0", "housecarl-generator.exe");
        var stamp = Path.Combine(sub, "generated", "corpus.pin");
        var pin = GitHead(sub);
        if (pin is null)
        {
            Console.Error.WriteLine($"the houseCARL submodule at {sub} is not checked out; run git submodule update --init.");
            return 1;
        }
        var built = File.Exists(stamp) ? File.ReadAllText(stamp).Trim() : "";
        if (!File.Exists(exe) || built != pin)
        {
            Console.Error.WriteLine(File.Exists(exe)
                ? $"houseCARL's generator in the submodule was built from {(built.Length == 0 ? "an unknown pin" : built)}, not the pin {pin}; run powershell -File scripts\\build.ps1 -Rulebook."
                : $"houseCARL's generator is not built at {exe}; run powershell -File scripts\\build.ps1 -Rulebook.");
            return 1;
        }
        var start = new ProcessStartInfo(exe) { WorkingDirectory = repo, UseShellExecute = false };
        foreach (var a in argsFor(repo)) start.ArgumentList.Add(a);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("could not start " + exe);
        child.WaitForExit();
        return child.ExitCode;
    }

    static string? GitHead(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        var start = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-C", dir, "rev-parse", "HEAD" }) start.ArgumentList.Add(a);
        using var git = Process.Start(start);
        if (git is null) return null;
        var head = git.StandardOutput.ReadToEnd().Trim();
        git.WaitForExit();
        return git.ExitCode == 0 && head.Length == 40 ? head : null;
    }
}
