using System.Diagnostics;
using System.Text.Json;
using HarnessShared;
using F = HarnessShared.RouteCutFixture;

// blockprobe --self-test, Gate E's part: the per-reference arm (--near) on RouteCutFixture's near fixture, laid out as an MO2
// instance and judged by THIS program run as a child process, as the app runs it. Profile P loads RnMaster.esm; profile Q adds
// RnMover.esp, which moves G onto the corridor. Each case against the fixture's table (RouteCutFixture.WriteNear):
//   NR-1  a whole-plugin run lists every reference it judged, a fine or unmeasured one too, so absent is never read as fine
//   NR-2  --near K judges K and the neighbour whose route window reaches K (N1), not F or G, and K's and N1's verdicts are the
//         whole run's
//   NR-3  --also-at widens the set to what K's box reaches at the other place (F at K moved to -400, 0)
//   NR-4  the created cut: G not measured under P, cut under Q, and the two runs (each --also-at the other's place) judge
//         the same set
//   NR-5  the rows file names what it was asked about
static class BlockNearSelfTest
{
    static string Fk(Mutagen.Bethesda.Plugins.FormKey k) => k.ToString();

    public static void Checks(ProbeSelfTest t)
    {
        var w = F.WriteNear(Path.Combine(t.Root, "near"));
        var inst = Path.Combine(t.Root, "near-instance"); var game = Path.Combine(t.Root, "near-game");
        Directory.CreateDirectory(Path.Combine(game, "Data"));
        Directory.CreateDirectory(Path.Combine(inst, "overwrite"));
        CopyDir(Path.Combine(w.ModsDir, F.NearKey.Name), Path.Combine(inst, "mods", F.NearKey.Name));
        File.Copy(w.Master, Path.Combine(inst, "mods", F.NearKey.Name, Path.GetFileName(w.Master)), true);
        Directory.CreateDirectory(Path.Combine(inst, "mods", F.NearMoverKey.Name));
        File.Copy(w.Mover, Path.Combine(inst, "mods", F.NearMoverKey.Name, Path.GetFileName(w.Mover)), true);
        foreach (var (prof, plugins) in new[] { ("P", new[] { "RnMaster.esm" }), ("Q", new[] { "RnMaster.esm", "RnMover.esp" }) })
        {
            var pd = Path.Combine(inst, "profiles", prof); Directory.CreateDirectory(pd);
            File.WriteAllLines(Path.Combine(pd, "plugins.txt"), plugins.Select(p => "*" + p));
            File.WriteAllLines(Path.Combine(pd, "loadorder.txt"), plugins);
            File.WriteAllLines(Path.Combine(pd, "modlist.txt"), plugins.Length == 1 ? new[] { "+RnMaster" } : new[] { "+RnMover", "+RnMaster" });
        }

        (int Exit, string Out, Dictionary<string, string>? Judged, JsonElement? Near) Probe(string profile, string label, params string[] args)
        {
            var rows = Path.Combine(t.Root, label + ".route-cut-rows.json");
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args.Concat(new[] { "--rows", rows })) psi.ArgumentList.Add(a);
            psi.Environment["HOUSECARL_MO2_INSTANCE"] = inst; psi.Environment["HOUSECARL_MO2_PROFILE"] = profile; psi.Environment["HOUSECARL_GAME_PATH"] = game;
            psi.Environment["HOUSECARL_RUNLOG_DIR"] = Path.Combine(t.Root, "runlog");
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            var text = o.Result + e.Result;
            if (p.ExitCode != 0 || !File.Exists(rows)) return (p.ExitCode, text, null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(rows));
            Dictionary<string, string>? judged = null;
            if (doc.RootElement.TryGetProperty("Judged", out var j) && j.ValueKind == JsonValueKind.Array)
                judged = j.EnumerateArray().ToDictionary(x => x.GetProperty("FormKey").GetString()!, x => x.GetProperty("Verdict").GetString()!, StringComparer.OrdinalIgnoreCase);
            JsonElement? near = doc.RootElement.TryGetProperty("Near", out var n) && n.ValueKind == JsonValueKind.Object ? n.Clone() : null;
            return (p.ExitCode, text, judged, near);
        }
        static string Show(Dictionary<string, string>? j) => j is null ? "no Judged list" : string.Join(", ", j.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}"));
        static string Last(string s) => s.Length <= 300 ? s.Trim() : s[^300..].Trim();

        var whole = Probe("P", "nr-whole", "RnMaster.esm");
        t.Check("NR-1 a whole run lists every reference it judged, the unmeasured G too (absent is never fine)",
                whole.Exit == 0 && whole.Judged is { } wj && F.NearBoxes.All(b => wj.ContainsKey(Fk(b.Key))) && wj.GetValueOrDefault(Fk(F.G)) == "NotMeasured",
                whole.Exit == 0 ? Show(whole.Judged) : $"exit {whole.Exit}: {Last(whole.Out)}");

        var near = Probe("P", "nr-near", "RnMaster.esm", "--near", Fk(F.K));
        bool sameAsWhole(string k) => near.Judged is { } nj && whole.Judged is { } wj2 && nj.TryGetValue(k, out var a) && wj2.TryGetValue(k, out var b) && a == b;
        t.Check("NR-2 --near K judges K and N1 (its window reaches K), not F or G",
                near.Exit == 0 && near.Judged is { } n2 && n2.Keys.OrderBy(x => x).SequenceEqual(new[] { Fk(F.K), Fk(F.N1) }.OrderBy(x => x), StringComparer.OrdinalIgnoreCase),
                near.Exit == 0 ? Show(near.Judged) : $"exit {near.Exit}: {Last(near.Out)}");
        t.Check("NR-2b K's and N1's verdicts under --near are the whole run's",
                sameAsWhole(Fk(F.K)) && sameAsWhole(Fk(F.N1)) && whole.Judged?.GetValueOrDefault(Fk(F.K)) is "Blocked" or "LikelyBlocked",
                $"near {Show(near.Judged)} / whole {Show(whole.Judged)}");

        var also = Probe("P", "nr-also", "RnMaster.esm", "--near", Fk(F.K), "--also-at", "-400,0,0");
        t.Check("NR-3 --also-at -400,0,0 widens the set to F, which K's box reaches there",
                also.Exit == 0 && also.Judged is { } n3 && n3.ContainsKey(Fk(F.F)) && n3.ContainsKey(Fk(F.K)) && !n3.ContainsKey(Fk(F.G)),
                also.Exit == 0 ? Show(also.Judged) : $"exit {also.Exit}: {Last(also.Out)}");

        var before = Probe("P", "nr-g-before", "RnMaster.esm", "--near", Fk(F.G), "--also-at", FormattableString.Invariant($"{F.GMoved.X},{F.GMoved.Y},0"));
        var after = Probe("Q", "nr-g-after", "RnMaster.esm", "--near", Fk(F.G), "--also-at", "-300,700,0");
        string? gb = before.Judged?.GetValueOrDefault(Fk(F.G)), ga = after.Judged?.GetValueOrDefault(Fk(F.G));
        t.Check("NR-4 the created cut: G not measured where RnMaster places it, Blocked or LikelyBlocked where RnMover moves it",
                gb == "NotMeasured" && ga is "Blocked" or "LikelyBlocked", $"before {gb ?? "absent"} ({(before.Exit == 0 ? "exit 0" : Last(before.Out))}), after {ga ?? "absent"} ({(after.Exit == 0 ? "exit 0" : Last(after.Out))})");
        t.Check("NR-4b the two runs, each also at the other's place, judge the same references",
                before.Judged is { } bj && after.Judged is { } aj && bj.Keys.OrderBy(x => x).SequenceEqual(aj.Keys.OrderBy(x => x), StringComparer.OrdinalIgnoreCase) && bj.Count >= 2,
                $"before {Show(before.Judged)} / after {Show(after.Judged)}");

        t.Check("NR-5 the rows file names the reference it was asked about",
                near.Near is { } nn && nn.TryGetProperty("Reference", out var r) && string.Equals(r.GetString(), Fk(F.K), StringComparison.OrdinalIgnoreCase),
                near.Near?.ToString() ?? "no Near");
    }

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, true);
        }
    }
}
