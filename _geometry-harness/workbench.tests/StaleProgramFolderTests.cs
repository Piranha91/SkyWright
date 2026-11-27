using System.Diagnostics;
using Xunit;

namespace WorkbenchTests;

/// <summary>A program the build no longer makes leaves no folder behind: after the solution builds, a folder under the
/// layout's Probes\, Tools\ or Tests\ that no project builds into is removed (after.SkyWright.sln.targets, with the
/// folders the projects report through SkyWrightBuildFolder in Directory.Build.props). Each case runs the solution step
/// on a small driver project that stands in for the solution's generated project: two or three empty projects import
/// the checkout's real Directory.Build.props and set their own output folder in a temporary layout. A file-destination
/// rule, so a strict test: the folders left afterwards are named exactly.</summary>
public sealed class StaleProgramFolderTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "wb-stale-" + Guid.NewGuid().ToString("N")[..8]);
    static readonly string Checkout = HarnessShared.CheckoutLayout.FindRoot()!;
    static readonly string RealStep = Path.Combine(Checkout, "after.SkyWright.sln.targets");

    public StaleProgramFolderTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    string Layout => Path.Combine(_tmp, "lay");

    /// <summary>The layout before the step: built Probes\liveprobe, Tools\livetool, Tests\livetests; stale
    /// Probes\movedprobe, Tools\oldtool, Tests\oldtests (each with a program in it); Data\ and a loose file.</summary>
    void Plant()
    {
        foreach (var d in new[] { "Probes/liveprobe", "Tools/livetool", "Tests/livetests", "Probes/movedprobe", "Tools/oldtool", "Tests/oldtests", "Data/sub" })
            Directory.CreateDirectory(Path.Combine(Layout, d));
        foreach (var f in new[] { "Probes/movedprobe/movedprobe.exe", "Tools/oldtool/oldtool.exe", "Tests/oldtests/oldtests.dll", "Probes/liveprobe/liveprobe.exe", "Probes/readme.txt", "Data/corpus.json" })
            File.WriteAllText(Path.Combine(Layout, f), "x");
    }

    /// <summary>Runs the solution step at <paramref name="step"/> on a driver whose projects build into
    /// <paramref name="built"/> (paths relative to the layout); returns the exit code and the output.</summary>
    (int Exit, string Output) RunStep(string step, params string[] built)
    {
        var refs = new List<string>();
        for (int i = 0; i < built.Length; i++)
        {
            var proj = Path.Combine(_tmp, $"p{i}.proj");
            File.WriteAllText(proj, $"""
                <Project>
                  <Import Project="{Path.Combine(Checkout, "Directory.Build.props")}" />
                  <PropertyGroup><OutDir>{Path.Combine(Layout, built[i])}\</OutDir></PropertyGroup>
                </Project>
                """);
            refs.Add($"<ProjectReference Include=\"{proj}\" />");
        }
        var driver = Path.Combine(_tmp, "driver.proj");
        File.WriteAllText(driver, $"""
            <Project>
              <ItemGroup>{string.Join("", refs)}</ItemGroup>
              <Target Name="Build" />
              <Import Project="{step}" Condition="Exists('{step}')" />
            </Project>
            """);
        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = _tmp };
        foreach (var a in new[] { "msbuild", driver, "-t:Build", "-nologo", "-v:m", "-nodeReuse:false", $"-p:SkyWrightLayoutRoot={Layout}\\" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, so.Result + se.Result);
    }

    /// <summary>Every folder and file left under the layout, relative, with forward slashes, sorted.</summary>
    string[] Left() => Directory.EnumerateFileSystemEntries(Layout, "*", SearchOption.AllDirectories)
        .Select(e => Path.GetRelativePath(Layout, e).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();

    static readonly string[] Expected =
    [
        "Data", "Data/corpus.json", "Data/sub",
        "Probes", "Probes/liveprobe", "Probes/liveprobe/liveprobe.exe", "Probes/readme.txt",
        "Tests", "Tests/livetests",
        "Tools", "Tools/livetool",
    ];

    /// <summary>The check the first case makes, as a sentence (null when the layout is as expected), so the sabotage
    /// arm can show the same check fail on its own words.</summary>
    string? StaleCheck(string step)
    {
        Plant();
        var (exit, output) = RunStep(step, "Probes/liveprobe", "Tools/livetool", "Tests/livetests");
        if (exit != 0) return $"the solution step failed (exit {exit}): {output[^Math.Min(600, output.Length)..]}";
        var left = Left();
        var stale = left.Except(Expected).ToArray();
        var lost = Expected.Except(left).ToArray();
        if (stale.Length == 0 && lost.Length == 0) return null;
        return $"after the solution built, {stale.Length} entries no project builds were left ({string.Join(", ", stale)}) and {lost.Length} that should stay were gone ({string.Join(", ", lost)})";
    }

    [Fact]
    public void A_folder_no_project_builds_into_is_removed_and_nothing_else()
    {
        var why = StaleCheck(RealStep);
        Assert.True(why is null, why);
    }

    [Fact]
    public void A_stale_folder_a_running_program_holds_fails_the_build_with_one_sentence_naming_it()
    {
        Plant();
        var held = Path.Combine(Layout, "Probes", "movedprobe", "movedprobe.exe");
        string output;
        int exit;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            (exit, output) = RunStep(RealStep, "Probes/liveprobe", "Tools/livetool", "Tests/livetests");
        Assert.True(exit != 0, $"a stale folder that could not be removed must fail the build: {output}");
        var want = $"the build no longer makes {Path.Combine(Layout, "Probes", "movedprobe")}, and that old folder could not be removed";
        Assert.True(output.Contains(want, StringComparison.OrdinalIgnoreCase), $"the failure must name the folder ('{want}'): {output}");
        Assert.True(File.Exists(held), "the held program must still be there");
        Assert.True(Directory.Exists(Path.Combine(Layout, "Probes", "liveprobe")), "a built folder must never be removed");
    }

    [Fact]
    public void When_no_folder_is_a_built_one_nothing_is_removed()
    {
        Plant();
        var before = Left();
        // the projects report folders outside Probes\, Tools\ and Tests\: the step cannot tell built from stale
        var (exit, output) = RunStep(RealStep, "Elsewhere/liveprobe");
        Assert.True(exit != 0, $"a layout with no built folder in it must fail the build: {output}");
        Assert.True(output.Contains("so none was removed", StringComparison.Ordinal), $"the failure must say nothing was removed: {output}");
        Assert.Equal(before, Left());
    }

    /// <summary>Sabotage: the same step without its removal (its one RemoveDir taken out) fails the first case's check,
    /// on that check's own sentence, naming the three stale folders.</summary>
    [Fact]
    public void Sabotage_the_step_without_its_removal_leaves_the_stale_folders_and_the_check_says_so()
    {
        var text = File.ReadAllText(RealStep);
        const string removal = "<RemoveDir Directories=\"@(_SwStale)\" ContinueOnError=\"WarnAndContinue\" />";
        int count = (text.Length - text.Replace(removal, "").Length) / removal.Length;
        Assert.True(count == 1, $"the sabotage expects the step's removal exactly once, found {count}");
        var sabotaged = Path.Combine(_tmp, "after.sabotaged.targets");
        File.WriteAllText(sabotaged, text.Replace(removal, ""));
        var why = StaleCheck(sabotaged);
        Assert.True(why is not null && why.StartsWith("after the solution built, 6 entries no project builds were left", StringComparison.Ordinal)
            && why.Contains("Probes/movedprobe") && why.Contains("Tools/oldtool") && why.Contains("Tests/oldtests"),
            $"without its removal the check must report the stale folders on its own sentence: {why}");
    }

    /// <summary>The step runs only if the solution build imports it: MSBuild imports after.&lt;solution name&gt;.sln.targets
    /// from beside the solution, so the file must be named after SkyWright.sln and sit next to it.</summary>
    [Fact]
    public void The_step_sits_beside_the_solution_under_the_name_the_solution_build_imports()
    {
        var sln = Directory.GetFiles(Checkout, "*.sln").Select(Path.GetFileNameWithoutExtension).Single();
        Assert.True(File.Exists(Path.Combine(Checkout, $"after.{sln}.sln.targets")), $"after.{sln}.sln.targets must sit beside {sln}.sln");
        Assert.Contains("AfterTargets=\"Build\"", File.ReadAllText(RealStep));
    }
}
