using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace WorkbenchTests;

/// <summary>runcontrol's names read with houseCARL's code compiled in (the bundle step's reader whenever no reader program is
/// named): on one finished run, the bundle names the same objects as the build's server reader
/// does, says truthfully that it read them in process and with which houseCARL build, and verifies; a named reader
/// program is still the one used, and recorded as before. Set WB_EVIDENCE_DIR to keep the two bundles.</summary>
[Trait("tier", "integration")]
public sealed class InProcessNamesTests
{
    static (int Exit, string Text) Runcontrol(params string[] args)
    {
        var psi = new ProcessStartInfo(R3Bundle.Runcontrol) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Remove("HOUSECARL_RECORD_READER_CMD");           // no reader program named: houseCARL in process
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, o.Result + e.Result);
    }

    /// <summary>A finished in-place run through its bundle, its names read by the reader the machine file names.</summary>
    static string Run(TiltedRefWorld w, object machine, string work)
    {
        InPlaceRunTests.SelWorld(w);
        var game = Path.GetDirectoryName(w.DataDir)!;
        var entry = Path.Combine(w.Root, "entry.json");
        File.WriteAllText(entry, JsonSerializer.Serialize(new
        {
            schema = 1, id = "wb-names", inPlace = new { instance = w.Instance, profile = "Default", game },
            selection = new { mods = new[] { "SelMod" } },
            evaluation = new { select = new[] { "geometry.object-overlap" }, depth = "standard" },
        }));
        var mf = Path.Combine(w.Root, "machine.json");
        File.WriteAllText(mf, JsonSerializer.Serialize(machine));
        var (exit, text) = Runcontrol("run", "--entry", entry, "--machine", mf, "--out", work, "--through", "bundle");
        Assert.True(exit == 0, $"runcontrol run --through bundle exited {exit}: {text[^Math.Min(1200, text.Length)..]}");
        return Path.Combine(work, "bundle", "evidence");
    }

    static JsonNode Json(string path) => JsonNode.Parse(File.ReadAllText(path))!;
    static JsonNode NamesStatus(string bundle) => Json(Path.Combine(bundle, "manifest.json"))["enrichmentStatus"]!["names"]!;
    static string NamesDir(string bundle) => Path.Combine(bundle, "enrichment", "names");

    /// <summary>Each tools/call answer's text in the response transcript, render_ms set to 0 (a timing).</summary>
    static List<string> Answers(string bundle) => File.ReadAllLines(Path.Combine(NamesDir(bundle), "02-response.jsonl"))
        .Select(l => JsonNode.Parse(l)!).Where(m => m["result"]?["content"] is JsonArray)
        .Select(m => System.Text.RegularExpressions.Regex.Replace(string.Concat(m["result"]!["content"]!.AsArray().Select(c => (string?)c!["text"])), "\"render_ms\": *[0-9]+", "\"render_ms\": 0"))
        .ToList();

    static string? ServerName(string bundle) => File.ReadAllLines(Path.Combine(NamesDir(bundle), "02-response.jsonl"))
        .Select(l => JsonNode.Parse(l)!).Select(m => (string?)m["result"]?["serverInfo"]?["name"]).FirstOrDefault(n => n is not null);

    [Fact]
    public void On_one_run_the_in_process_names_read_matches_the_server_reader_says_what_read_it_and_verifies()
    {
        using var w = new TiltedRefWorld();
        var work = Path.Combine(w.Root, "work");
        // houseCARL's server program from the pin's build output (the tests' own entry), reading the rulebook the
        // in-process read uses (the build's Data\corpus.json), as it read the copy beside it in the old layout
        var corpus = TestBuild.Layout.PathOf(HarnessShared.CheckoutLayout.Piece.Corpus);
        var served = Run(w, new { readerCmd = "\"" + TestBuild.Server + "\" \"--HouseCarl:CorpusPath=" + corpus + "\"" }, work);
        // the same finished run bundled again, its names read in process from the same private instance
        var inproc = Path.Combine(work, "bundle-inproc");
        // the private instance the run's bundle stage made, spelled as that stage spells it (forward slashes: houseCARL's
        // epoch hashes the resolved path's spelling)
        var inst = Path.Combine(work, "names-instance").Replace(Path.DirectorySeparatorChar, '/');
        var (exit, text) = Runcontrol("bundle", "--run", Path.Combine(work, "evaluation"), "--out", inproc,
            "--reader-env", "HOUSECARL_DATA_DIR=" + inst + "/data", "--reader-env", "HouseCarl__DataDir=" + inst + "/Data",
            "--reader-env", "HouseCarl__ModsDir=" + inst + "/mods", "--reader-env", "HouseCarl__ProfileDir=" + inst + "/profile");
        Assert.True(exit == 0, $"runcontrol bundle (no reader named) exited {exit}: {text}");
        var (vexit, vtext) = Runcontrol("verify-bundle", inproc);
        Assert.True(vexit == 0, $"the in-process bundle does not verify (exit {vexit}): {vtext}");
        if (Environment.GetEnvironmentVariable("WB_EVIDENCE_DIR") is { Length: > 0 } keep)
            foreach (var (b, name) in new[] { (served, "names-server"), (inproc, "names-inproc") })
                CopyTree(b, Path.Combine(keep, name));

        // the same names, by the same reads (names.json also hashes the two transcripts, which differ in what read them)
        JsonNode Names(string b) { var n = Json(Path.Combine(NamesDir(b), "names.json")); n.AsObject().Remove("capture"); return n; }
        Assert.Equal(Names(served).ToJsonString(), Names(inproc).ToJsonString());
        var a = Answers(served); var b2 = Answers(inproc);
        Assert.True(a.Count > 0, "the server reader's transcript holds no answer");
        Assert.Equal(a.Count, b2.Count);
        for (int i = 0; i < a.Count; i++) Assert.True(a[i] == b2[i], $"answer {i} differs:\n server:  {a[i]}\n inproc:  {b2[i]}");

        // what read them, truthfully: a program for the server reader, houseCARL in process (by its build) for the other
        Assert.Equal("--reader-cmd", (string?)NamesStatus(served)["reader"]);
        Assert.Null(NamesStatus(served)["readerIdentity"]);
        Assert.Equal("houseCARL", ServerName(served));
        Assert.Equal("in-process", (string?)NamesStatus(inproc)["reader"]);
        Assert.Equal(HarnessShared.HousecarlCalls.Identity, (string?)NamesStatus(inproc)["readerIdentity"]);
        Assert.Equal("houseCARL, in process (no program started)", ServerName(inproc));
    }

    [Fact]
    public void A_run_whose_machine_file_names_no_reader_reads_the_names_in_process_and_verifies()
    {
        using var w = new TiltedRefWorld();
        var bundle = Run(w, new { }, Path.Combine(w.Root, "work"));
        Assert.Equal("enriched", (string?)NamesStatus(bundle)["status"]);
        Assert.Equal("in-process", (string?)NamesStatus(bundle)["reader"]);
        var (vexit, vtext) = Runcontrol("verify-bundle", bundle);
        Assert.True(vexit == 0, $"the bundle does not verify (exit {vexit}): {vtext}");
    }

    static void CopyTree(string from, string to)
    {
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, true);
        }
    }
}
