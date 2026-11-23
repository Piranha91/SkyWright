using System.Diagnostics;
using System.Text.RegularExpressions;
using HarnessShared;

namespace SkyWrightPublish;

/// <summary><c>skywright-publish</c>: builds SkyWright's installed layout from one commit, in a folder off the checkout,
/// so nothing a running evaluation or an open workbench uses is touched by a build in the checkout.
/// <code>
/// skywright-publish [--commit REV] [--to DIR] [--src DIR] [--keep-src]   publish REV (default HEAD) into DIR/&lt;commit&gt;
/// skywright-publish --verify DIR                                         re-hash an install against its manifest
/// </code>
/// The layout and the manifest are <see cref="CheckoutLayout"/>'s; this program only fills them.</summary>
internal static class Program
{
    const string DefaultTo = "C:/hc-runs/skywright-install";
    const string DefaultSrc = "C:/hc-runs/skywright-install-src";

    /// <summary>The projects under _geometry-harness that are not programs of the install.</summary>
    static readonly HashSet<string> NotInstalled = new(StringComparer.OrdinalIgnoreCase) { "workbench.tests", "publish" };

    sealed class Refused(string message) : Exception(message);

    static int Main(string[] args)
    {
        try
        {
            if (args is ["--verify", var dir]) return VerifyCommand(dir);
            string rev = "HEAD", to = DefaultTo, src = DefaultSrc; bool keep = false;
            for (int i = 0; i < args.Length; i++)
            {
                string Value() => i + 1 < args.Length ? args[++i] : throw new Refused($"{args[i]} needs a value.");
                switch (args[i])
                {
                    case "--commit": rev = Value(); break;
                    case "--to": to = Value(); break;
                    case "--src": src = Value(); break;
                    case "--keep-src": keep = true; break;
                    default: throw new Refused($"'{args[i]}' is not an option; use --commit REV, --to DIR, --src DIR, --keep-src, or --verify DIR.");
                }
            }
            return Publish(rev, Path.GetFullPath(to), Path.GetFullPath(src), keep);
        }
        catch (Refused r) { Console.Error.WriteLine("skywright-publish: " + r.Message); return 2; }
    }

    static int VerifyCommand(string dir)
    {
        var root = Path.GetFullPath(dir);
        if (!File.Exists(Path.Combine(root, CheckoutLayout.ManifestName)))
            throw new Refused($"{Fwd(root)} holds no {CheckoutLayout.ManifestName}, so it is not a SkyWright install.");
        var (problems, unlisted) = CheckoutLayout.Verify(root);
        foreach (var u in unlisted) Console.WriteLine($"note: {u} is in the install but not in its manifest; no lookup uses it.");
        if (problems.Count > 0)
        {
            foreach (var p in problems) Console.Error.WriteLine("skywright-publish: " + p);
            Console.Error.WriteLine($"skywright-publish: the install at {Fwd(root)} does not match its manifest ({problems.Count} problem(s)); publish it again.");
            return 1;
        }
        var m = CheckoutLayout.Manifest.Read(Path.Combine(root, CheckoutLayout.ManifestName));
        Console.WriteLine($"verified: {Fwd(root)}, commit {m.Commit}, {m.Files.Count} files match the manifest.");
        return 0;
    }

    static int Publish(string rev, string to, string srcRoot, bool keepSrc)
    {
        var checkout = CheckoutLayout.FindRoot()
            ?? throw new Refused($"this publisher is not inside a SkyWright checkout (no {CheckoutLayout.Marker} above {Fwd(AppContext.BaseDirectory)}); run it with dotnet run --project _geometry-harness/publish from the checkout to publish.");
        var sub = CheckoutLayout.Housecarl(checkout);

        // 1. a clean tree, a commit that resolves, a destination not yet there
        if (Git(checkout, "status", "--porcelain") is { Length: > 0 } dirty)
            throw new Refused($"the checkout {Fwd(checkout)} has uncommitted changes ({Lines(dirty).Length} path(s), first: {Lines(dirty)[0].Trim()}); commit or set them aside, then publish.");
        if (Directory.Exists(sub) && Git(sub, "status", "--porcelain") is { Length: > 0 } subDirty)
            throw new Refused($"the pinned houseCARL {Fwd(sub)} has uncommitted changes (first: {Lines(subDirty)[0].Trim()}); a publish takes the pin exactly, so clean it first.");
        string commit;
        try { commit = Git(checkout, "rev-parse", "--verify", rev + "^{commit}").Trim(); }
        catch (Refused) { throw new Refused($"'{rev}' is not a commit of the checkout {Fwd(checkout)}; name a branch, tag or commit."); }
        var pinLine = Git(checkout, "ls-tree", commit, "external/houseCARL").Trim();
        var pin = Regex.Match(pinLine, @"^160000 commit ([0-9a-f]{40})\t").Groups[1].Value;
        if (pin.Length == 0) throw new Refused($"commit {commit[..12]} pins no houseCARL submodule at external/houseCARL, so its server and rulebook cannot be published.");
        var dest = Path.Combine(to, commit);
        if (Directory.Exists(dest)) throw new Refused($"{Fwd(dest)} is already published; check it with --verify, or delete it to publish again.");

        var sw = Stopwatch.StartNew();
        Say($"publishing {commit} (houseCARL pin {pin[..12]}) into {Fwd(dest)}");

        // 2. the commit and its pin, exported off the checkout: nothing the checkout's bin holds is used or touched
        var src = Path.Combine(srcRoot, commit);
        if (Directory.Exists(src)) Directory.Delete(src, recursive: true);
        Directory.CreateDirectory(src);
        Export(checkout, commit, src);
        var srcSub = Path.Combine(src, "external", "houseCARL");
        Directory.CreateDirectory(srcSub);
        Export(sub, pin, srcSub);

        // 3. build the export, and the pin's generator for the rulebook
        Run(src, "dotnet", "build", "SkyWright.sln", "-c", "Release", "-nologo", "-v", "q");
        Run(src, "dotnet", "run", "--project", Path.Combine(srcSub, "src", "housecarl-generator"), "-c", "Release", "--no-build", "--",
            Path.Combine(srcSub, "generated"), Path.Combine(srcSub, "generated", "reference"));

        // 4. publish every program into its own folder of a temporary install
        Directory.CreateDirectory(to);
        var temp = dest + ".partial";
        if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        Directory.CreateDirectory(temp);
        var pieces = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, csproj) in Programs(src))
        {
            Run(src, "dotnet", "publish", csproj, "-c", "Release", "--no-build", "-nologo", "-v", "q", "-o", Path.Combine(temp, name));
            var key = name is "workbench" or "runcontrol" ? name : "program:" + name;
            pieces[key] = $"{name}/{name}.exe";
        }
        Run(src, "dotnet", "publish", Path.Combine(srcSub, "src", "housecarl-mcp", "housecarl-mcp.csproj"), "-c", "Release", "--no-build", "-nologo", "-v", "q",
            "-o", Path.Combine(temp, "housecarl-mcp"));
        var installed = new CheckoutLayout.Layout(CheckoutLayout.Kind.Installed, temp, null);
        var built = CheckoutLayout.InCheckout(src);
        foreach (var p in new[] { CheckoutLayout.Piece.Corpus, CheckoutLayout.Piece.ScenePolicy, CheckoutLayout.Piece.Registry, CheckoutLayout.Piece.Severity })
            if (File.Exists(built.PathOf(p))) Copy(built.PathOf(p), installed.PathOf(p));
        foreach (var f in Directory.EnumerateFiles(Path.Combine(src, "data")))
            Copy(f, Path.Combine(temp, "data", Path.GetFileName(f)));
        foreach (var p in new[] { CheckoutLayout.Piece.Server, CheckoutLayout.Piece.Corpus, CheckoutLayout.Piece.Registry, CheckoutLayout.Piece.Severity, CheckoutLayout.Piece.ScenePolicy })
            pieces[CheckoutLayout.PieceKey(p)] = CheckoutLayout.Relative(temp, installed.PathOf(p));

        // 5. nothing missing, and the app's own viewprobe is the one the install carries
        foreach (var (key, rel) in pieces.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (!File.Exists(Path.Combine(temp, rel)))
                throw new Refused($"the publish of {commit[..12]} is missing {key} ({rel}); the build of that commit did not make it, so nothing was installed.");
        foreach (var f in new[] { "viewprobe.exe", "viewprobe.dll" })
        {
            string a = Path.Combine(temp, "workbench", f), b = Path.Combine(temp, "viewprobe", f);
            if (!File.Exists(a) || !File.Exists(b) || CheckoutLayout.Hash(a) != CheckoutLayout.Hash(b))
                throw new Refused($"the workbench's {f} is not the same file as the install's viewprobe/{f}, so the app would draw with another viewprobe than the probes run; nothing was installed.");
        }

        // 6. the manifest, checked, then the folder renamed into place
        var files = Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories)
            .ToDictionary(f => CheckoutLayout.Relative(temp, f), CheckoutLayout.Hash, StringComparer.Ordinal);
        new CheckoutLayout.Manifest(commit, pin, DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), pieces, files)
            .Write(Path.Combine(temp, CheckoutLayout.ManifestName));
        var (problems, unlisted) = CheckoutLayout.Verify(temp);
        if (problems.Count > 0 || unlisted.Count > 0)
            throw new Refused($"the new install did not verify ({string.Join("; ", problems.Concat(unlisted.Select(u => u + " is not listed")).Take(3))}); nothing was installed.");
        Directory.Move(temp, dest);
        if (!keepSrc) Directory.Delete(src, recursive: true);
        long bytes = Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        Say($"published {commit} to {Fwd(dest)}: {pieces.Count(kv => kv.Key.StartsWith("program:") || kv.Key is "workbench" or "runcontrol")} programs, "
            + $"{files.Count} files, {bytes / 1048576.0:0} MiB, in {sw.Elapsed.TotalMinutes:0.0} min");
        return 0;
    }

    /// <summary>The install's programs: every project under _geometry-harness in the solution whose OutputType is Exe
    /// or WinExe, except the tests and this publisher. (folder name, project path); the folder name is the exe's name.</summary>
    static List<(string Name, string Csproj)> Programs(string src)
    {
        var list = new List<(string, string)>();
        foreach (Match m in Regex.Matches(File.ReadAllText(Path.Combine(src, CheckoutLayout.Marker)), @"""(_geometry-harness\\([^\\""]+)\\[^\\""]+\.csproj)"""))
        {
            string name = m.Groups[2].Value, csproj = Path.Combine(src, m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar));
            if (NotInstalled.Contains(name)) continue;
            var text = File.ReadAllText(csproj);
            if (!Regex.IsMatch(text, @"<OutputType>\s*(Win)?Exe\s*</OutputType>")) continue;
            var asm = Regex.Match(text, @"<AssemblyName>\s*([^<\s]+)\s*</AssemblyName>");
            if (asm.Success && asm.Groups[1].Value != name)
                throw new Refused($"{name}'s exe is named {asm.Groups[1].Value}, but the install finds a program by its folder's name; name them the same.");
            list.Add((name, csproj));
        }
        if (list.Count == 0) throw new Refused($"no program projects were found in {CheckoutLayout.Marker} of the export.");
        return list.OrderBy(t => t.Item1, StringComparer.Ordinal).ToList();
    }

    static void Export(string repo, string commit, string into)
    {
        var tar = Path.Combine(into, "..", Path.GetFileName(into) + ".export.tar");
        Git(repo, "archive", "--format=tar", "-o", Path.GetFullPath(tar), commit);
        // Windows' own bsdtar: a GNU tar earlier on PATH reads "C:" in a path as a remote host
        var tarExe = Path.Combine(Environment.SystemDirectory, "tar.exe");
        try { Run(into, File.Exists(tarExe) ? tarExe : "tar", "-xf", Path.GetFullPath(tar), "-C", into); }
        finally { File.Delete(tar); }
    }

    static void Copy(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: true);
    }

    static string Git(string repo, params string[] args) => Capture(repo, "git", ["-C", repo, .. args]);

    static string Capture(string cwd, string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new Refused($"{exe} could not be started; is it on PATH?");
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new Refused($"{exe} {string.Join(' ', args)} failed (exit {p.ExitCode}): {First(err.Result + output)}");
        return output;
    }

    static void Run(string cwd, string exe, params string[] args)
    {
        Say($"  {exe} {string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");
        var output = Capture(cwd, exe, args);
        if (output.Contains(" error ", StringComparison.Ordinal)) Console.Write(output);
    }

    static string First(string text) => Lines(text).Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).DefaultIfEmpty(Lines(text).FirstOrDefault() ?? "").First().Trim();
    static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    static void Say(string line) => Console.WriteLine(line);
    static string Fwd(string p) => p.Replace('\\', '/');
}
