using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace SkyWright.Engine.Tests;

/// <summary>evalcontrol, the AI-side checks' controller (step 3 of the SkyWright / houseCARL boundary): its own known
/// answers (the rules that moved from runcontrol, under their names there) and what it says of a bundle made before.</summary>
public class EvalcontrolTests
{
    // the program, where the module's layout marker says its tools are
    static string Exe()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "housecarl-integration-layout.json")));
        string tool = marker.RootElement.GetProperty("tool").GetString()!.Replace("{name}", "evalcontrol");
        return Path.Combine(root, tool.Replace('/', Path.DirectorySeparatorChar));
    }

    static string Checkout()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "SkyWright.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("no checkout above the test folder");
    }

    static (int exit, string stdout, string stderr) Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Exe()) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, o.Result, e.Result);
    }

    [Fact]
    public void Its_known_answers_are_all_green()
    {
        var (exit, stdout, stderr) = Run("--self-test");
        Assert.True(exit == 0, $"evalcontrol --self-test exit {exit}: {stdout}{stderr}");
        Assert.Contains(" 0 red, 0 skipped", stdout);
        Assert.DoesNotContain("\nRED ", "\n" + stdout);
    }

    [Fact]
    public void A_bundle_made_before_the_AI_side_rows_left_gets_one_sentence()
    {
        // a lab bundle made by runcontrol and evalrun before the change (registry version 4, a record import inside)
        string fixture = Path.Combine(Checkout(), "_geometry-harness", "tests", "fixtures", "old-bundles", "lab-record-rows");
        string copy = Path.Combine(Path.GetTempPath(), "evalcontrol-old-bundle-" + Guid.NewGuid().ToString("N")[..8]);
        foreach (string f in Directory.GetFiles(fixture, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(copy, Path.GetRelativePath(fixture, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        try
        {
            var (exit, stdout, stderr) = Run("verify", copy);
            Assert.Equal(2, exit);
            string line = stderr.Trim();
            Assert.DoesNotContain("\n", line);
            Assert.Contains("made before the AI-side rows left SkyWright's check registry", line);
            Assert.Contains("runcontrol verify-bundle", line);
            Assert.False(File.Exists(Path.Combine(copy, "verify.json")), "an old bundle got a verify.json from evalcontrol");
        }
        finally { Directory.Delete(copy, recursive: true); }
    }
}
