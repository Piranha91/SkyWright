using System.Text;
using HarnessShared;

// PROOF BY CONSTRUCTION — constrained ordering and the bounded candidate search
// (_shared/OrderRules.cs, _shared/OrderSearch.cs; roadmap 4.20, 2026-09-11).
//
// The fixture is a handful of real TES4 headers in a temp folder — a master table, and the ESM flag bit written at
// header offset 8 so a master-flagged .esp is a real file and not a mocked boolean. Every expected answer below is
// worked out from those files by hand: which plugin masters which, which group each loads in, and what the base
// masters' own order is. A proof that derived its expectation the way the code does would only prove the code
// equals itself.
//
// The cases follow the four things this layer can get quietly wrong:
//   legality      an order the engine would not load, accepted — every finding computed from it is then fiction;
//   rules         advice accepted without a source, or a rule dropped to make an order sortable;
//   search        a candidate that is not a permutation of the user's order, or an order of candidates that is not
//                 reproducible, or a budget whose cost is not reported;
//   identity      two different histories that fingerprint the same, which is how a cache serves the wrong answer.
static class OrderProof
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nPROOF — constrained ordering and the bounded search (_shared/OrderRules.cs, _shared/OrderSearch.cs, roadmap 4.20)");
        Console.WriteLine("   Real TES4 headers in a temp folder; every master table and flag bit known by hand.\n");
        var root = Path.Combine(Path.GetTempPath(), "proofprobe-order-" + Environment.ProcessId);
        Directory.CreateDirectory(root);
        try { Body(Check, root); }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    /// <summary>A minimal but REAL TES4 header: the 24-byte record header with the FLAGS word at offset 8, then
    /// HEDR and one MAST/DATA pair per declared master. The flags word is what makes a master-flagged .esp a file
    /// fact rather than a test double — <see cref="PluginHeader.TryRead"/> reads the same bit the engine does.</summary>
    static byte[] PluginBytes(uint flags, params string[] masters)
    {
        var body = new List<byte>();
        void Sub(string type, byte[] payload)
        {
            body.AddRange(Encoding.ASCII.GetBytes(type));
            body.AddRange(BitConverter.GetBytes((ushort)payload.Length));
            body.AddRange(payload);
        }
        Sub("HEDR", new byte[12]);
        foreach (var m in masters) { Sub("MAST", Encoding.ASCII.GetBytes(m + "\0")); Sub("DATA", new byte[8]); }
        var full = new List<byte>();
        full.AddRange(Encoding.ASCII.GetBytes("TES4"));
        full.AddRange(BitConverter.GetBytes(body.Count));
        full.AddRange(BitConverter.GetBytes(flags));      // offset 8 — the ESM bit lives here
        full.AddRange(new byte[12]);                      // form id, vc info, version — 24-byte header total
        full.AddRange(body);
        return full.ToArray();
    }

    const uint EsmBit = 0x1;

    static void Body(Action<string, bool, string> Check, string root)
    {
        void Plugin(string name, uint flags, params string[] masters) =>
            File.WriteAllBytes(Path.Combine(root, name), PluginBytes(flags, masters));

        Plugin("Skyrim.esm", 0);
        Plugin("Update.esm", 0, "Skyrim.esm");
        Plugin("Dawnguard.esm", 0, "Skyrim.esm");
        Plugin("Big.esm", 0, "Skyrim.esm");
        Plugin("Flag.esp", EsmBit, "Skyrim.esm");                 // master-flagged .esp — loads in the master block
        Plugin("A.esp", 0, "Skyrim.esm");
        Plugin("B.esp", 0, "Skyrim.esm");
        Plugin("C.esp", 0, "Skyrim.esm", "A.esp");                // C declares A
        Plugin("D.esp", 0, "Skyrim.esm");
        Plugin("E.esp", 0, "Skyrim.esm");
        Plugin("F.esp", 0, "Skyrim.esm");

        var names = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "Big.esm", "Flag.esp",
                            "A.esp", "B.esp", "C.esp", "D.esp", "E.esp", "F.esp" };
        var headers = new Dictionary<string, PluginHeader?>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names) headers[n] = PluginHeader.TryRead(Path.Combine(root, n));
        var baseline = names.ToList();

        Check("order/fixture: the flag bit at offset 8 is read — Flag.esp is master-flagged, A.esp is not",
              headers["Flag.esp"]!.MasterFlag && !headers["A.esp"]!.MasterFlag && headers["Skyrim.esm"]!.MasterFlag,
              "PluginHeader.TryRead: ESM bit OR an .esm/.esl extension");
        Check("order/fixture: C.esp's master table is read off the file",
              headers["C.esp"]!.Masters.Contains("A.esp") && headers["C.esp"]!.Masters.Contains("Skyrim.esm"),
              string.Join(", ", headers["C.esp"]!.Masters));

        var plain = OrderRuleSet.Build(baseline, headers, Array.Empty<string>(), Array.Empty<OrderRule>());

        // ---- legality ------------------------------------------------------------------------------------------
        var ok = plain.Check(baseline);
        Check("legality: the hand-built legal order is accepted", ok.Legal, ok.Summary);

        var masterAfter = OrderSearch.Move(baseline, "A.esp", "C.esp", before: false);   // A after the plugin that masters it
        var r = plain.Check(masterAfter);
        Check("legality: a master loading AFTER the plugin that declares it is refused, naming the pair",
              !r.Legal && r.Violations.Any(v => v.Rule == EngineRule.Masters && v.Detail.Contains("A.esp") && v.Detail.Contains("C.esp")),
              r.Violations.FirstOrDefault()?.ToString() ?? "(no violation)");

        var espAhead = OrderSearch.Move(baseline, "A.esp", "Flag.esp", before: true);    // a plain .esp ahead of a master-flagged one
        r = plain.Check(espAhead);
        Check("legality: a plain .esp ahead of a master-flagged plugin is refused, naming both",
              !r.Legal && r.Violations.Any(v => v.Rule == EngineRule.Groups && v.Detail.Contains("A.esp") && v.Detail.Contains("Flag.esp")),
              r.Violations.FirstOrDefault(v => v.Rule == EngineRule.Groups)?.ToString() ?? "(no grouping violation)");

        var movedBase = OrderSearch.Move(baseline, "Update.esm", "Dawnguard.esm", before: false);
        r = plain.Check(movedBase);
        Check("legality: a moved engine-fixed base master is refused, naming both",
              !r.Legal && r.Violations.Any(v => v.Rule == EngineRule.BaseMasters && v.Detail.Contains("Update.esm") && v.Detail.Contains("Dawnguard.esm")),
              r.Violations.FirstOrDefault(v => v.Rule == EngineRule.BaseMasters)?.ToString() ?? "(no base-master violation)");

        r = plain.Check(baseline.Where(n => n != "A.esp").ToList());
        Check("legality: dropping a plugin a candidate is supposed to reorder is refused (one plugin, one position)",
              !r.Legal && r.Violations.Any(v => v.Rule == EngineRule.OnePosition), r.Violations.FirstOrDefault()?.ToString() ?? "");

        // ---- fixed positions -----------------------------------------------------------------------------------
        var fixedSet = OrderRuleSet.Build(baseline, headers, new[] { "B.esp" }, Array.Empty<OrderRule>());
        Check("fixed: the user's own order still satisfies a fixed plugin", fixedSet.Check(baseline).Legal, "");
        r = fixedSet.Check(OrderSearch.Move(baseline, "B.esp", "A.esp", before: true));
        Check("fixed: moving the fixed plugin itself is refused, naming it",
              !r.Legal && r.Violations.Any(v => v.Rule == "fixed: 'B.esp'"), r.Violations.FirstOrDefault()?.ToString() ?? "");
        r = fixedSet.Check(OrderSearch.Move(baseline, "A.esp", "B.esp", before: false));
        Check("fixed: moving ANOTHER plugin past the fixed one is refused too — 'relative to every other plugin'",
              !r.Legal && r.Violations.Any(v => v.Rule == "fixed: 'B.esp'"), r.Violations.FirstOrDefault()?.ToString() ?? "");

        var fixedReport = OrderSearch.Run(fixedSet, new[] { new ContentionEdge("A.esp", "B.esp", "record", 4) }, 8);
        Check("fixed: a candidate that would move a fixed plugin is NEVER generated, and the refusal says which rule",
              fixedReport.Candidates.Count == 1 && fixedReport.Candidates[0].Name == "00-baseline"
              && fixedReport.RejectedMoves.Any(m => m.Contains("fixed: 'B.esp'")),
              fixedReport.RejectedMoves.FirstOrDefault() ?? "(nothing rejected)");

        // ---- the rules file ------------------------------------------------------------------------------------
        const string anon = """
        { "schemaVersion": 1, "rules": [ { "before": "A.esp", "after": "B.esp", "hardness": "advice" } ] }
        """;
        string anonMsg = "";
        try { OrderRules.Parse(anon, "t"); }
        catch (InvalidDataException ex) { anonMsg = ex.Message; }
        Check("rules: advice WITHOUT provenance is refused at read time, not accepted as anonymous",
              anonMsg.Contains("provenance"), anonMsg);

        const string sourced = """
        { "schemaVersion": 1,
          "rules": [ { "before": "A.esp", "after": "B.esp", "hardness": "advice", "reason": "A's cell edits should lose",
                       "provenance": { "source": "https://example.invalid/mod/1", "checked": "2026-09-11",
                                       "versionScope": "1.2.3", "note": "provisional" } } ] }
        """;
        var parsed = OrderRules.Parse(sourced, "t");
        var adv = parsed.Rules[0];
        Check("rules: advice WITH provenance parses, and the provenance is carried verbatim",
              adv.Hardness == RuleHardness.Advice && adv.Provenance is { Source: "https://example.invalid/mod/1", Checked: "2026-09-11", VersionScope: "1.2.3", Note: "provisional" },
              adv.Provenance?.Line ?? "(none)");
        var mp = OrderManifest.Prov(adv.Provenance);
        Check("rules: the manifest carries that provenance verbatim, field for field",
              mp is { Source: "https://example.invalid/mod/1", Checked: "2026-09-11", VersionScope: "1.2.3", Note: "provisional" },
              $"{mp?.Source} / {mp?.Checked} / {mp?.VersionScope} / {mp?.Note}");

        string hardnessMsg = "";
        try { OrderRules.Parse("""{ "schemaVersion": 1, "rules": [ { "before": "A.esp", "after": "B.esp" } ] }""", "t"); }
        catch (InvalidDataException ex) { hardnessMsg = ex.Message; }
        Check("rules: a rule with no hardness is refused rather than guessed at", hardnessMsg.Contains("hardness"), hardnessMsg);

        string schemaMsg = "";
        try { OrderRules.Parse("""{ "schemaVersion": 2, "rules": [] }""", "t"); }
        catch (InvalidDataException ex) { schemaMsg = ex.Message; }
        Check("rules: a future schema is refused rather than read with today's meanings", schemaMsg.Contains("schema"), schemaMsg);

        var anonRequired = OrderRules.Parse("""{ "schemaVersion": 1, "rules": [ { "before": "A.esp", "after": "B.esp", "hardness": "required" } ] }""", "t");
        Check("rules: an anonymous REQUIRED rule is accepted but noted as anonymous, never silently equal to a sourced one",
              anonRequired.Rules.Count == 1 && anonRequired.Notes.Any(n => n.Contains("anonymous")),
              anonRequired.Notes.FirstOrDefault() ?? "(no note)");

        // ---- contradictions ------------------------------------------------------------------------------------
        var prov = new RuleProvenance("https://example.invalid/mod/2", "2026-09-11", "2.0");
        var cycleRules = new[]
        {
            new OrderRule("A.esp", "B.esp", RuleHardness.Required, "t #0", null, "A first"),
            new OrderRule("B.esp", "A.esp", RuleHardness.Required, "t #1", null, "B first"),
        };
        var cyc = OrderRuleSet.Build(baseline, headers, Array.Empty<string>(), cycleRules);
        var cycExplain = string.Join(" ", cyc.Contradictions.Select(c => c.Explanation));
        Check("contradiction: two required rules in a cycle make the constraints UNSATISFIABLE, with BOTH rules named",
              !cyc.Satisfiable && cyc.Contradictions.Any(c => c.Kind == ContradictionKind.HardCycle)
              && cycExplain.Contains("t #0") && cycExplain.Contains("t #1"),
              cyc.Contradictions.FirstOrDefault()?.Explanation.Replace("\n", " ") ?? "(none)");

        var cycReport = OrderSearch.Run(cyc, new[] { new ContentionEdge("A.esp", "B.esp", "record", 1) }, 8);
        Check("contradiction: the search REFUSES rather than picking a winner among rules the user called required",
              cycReport.Result == SearchResult.ConstraintsUnsatisfiable && cycReport.Emitted == 0
              && cycReport.Outcome.Contains("ENCODED CONSTRAINTS"),
              cycReport.Outcome);

        var mixed = new[]
        {
            new OrderRule("A.esp", "B.esp", RuleHardness.Required, "t #0", null, "A first"),
            new OrderRule("B.esp", "A.esp", RuleHardness.Advice, "t #1", prov, "the author says B first"),
        };
        var mix = OrderRuleSet.Build(baseline, headers, Array.Empty<string>(), mixed);
        var mixReport = OrderSearch.Run(mix, new[]
        {
            new ContentionEdge("A.esp", "B.esp", "explicit", 1),
            new ContentionEdge("B.esp", "C.esp", "record", 7),
        }, 8);
        Check("contradiction: advice the required rules make impossible is reported unsatisfiable WITH its provenance, never dropped",
              mix.Satisfiable && mix.UnsatisfiableSoft.Any(c => c.Rules.Any(x => x.Hardness == RuleHardness.Advice))
              && mix.UnsatisfiableSoft.Any(c => c.Explanation.Contains("example.invalid/mod/2")),
              mix.UnsatisfiableSoft.FirstOrDefault()?.Explanation.Replace("\n", " ") ?? "(none)");
        Check("contradiction: candidates are STILL generated when only the advice is unsatisfiable",
              mixReport.Result != SearchResult.ConstraintsUnsatisfiable && mixReport.Emitted > 0,
              $"{mixReport.Result}, {mixReport.Emitted} emitted");
        Check("contradiction: an advice rule that would put a plain .esp ahead of a master-flagged plugin is unsatisfiable, not obeyed",
              OrderRuleSet.Build(baseline, headers, Array.Empty<string>(),
                  new[] { new OrderRule("A.esp", "Flag.esp", RuleHardness.Advice, "t #0", prov) })
                .UnsatisfiableSoft.Any(c => c.Explanation.Contains(EngineRule.Groups)), "");

        // ---- the search ----------------------------------------------------------------------------------------
        var pairEdges = new[] { new ContentionEdge("B.esp", "C.esp", "record", 12) };
        var pairSet = OrderRuleSet.Build(baseline, headers, Array.Empty<string>(), Array.Empty<OrderRule>());
        var pairReport = OrderSearch.Run(pairSet, pairEdges, 8);
        var reversed = pairReport.Candidates.FirstOrDefault(c => Index(c.Plugins, "C.esp") < Index(c.Plugins, "B.esp"));
        Check("search: the REVERSED legal order of a two-mod group is among the candidates",
              reversed is not null && pairSet.Check(reversed!.Plugins).Legal,
              reversed is null ? "(absent)" : string.Join("; ", reversed.Changes));

        var requiredBC = OrderRuleSet.Build(baseline, headers, Array.Empty<string>(),
            new[] { new OrderRule("B.esp", "C.esp", RuleHardness.Required, "t #0", null, "B must lose to C") });
        var negative = OrderSearch.Run(requiredBC, pairEdges, 8);
        Check("search: the negative control — with B before C REQUIRED, the reversed order is illegal and never a candidate",
              !requiredBC.Check(OrderSearch.Move(baseline, "C.esp", "B.esp", before: true)).Legal
              && !negative.Candidates.Any(c => Index(c.Plugins, "C.esp") < Index(c.Plugins, "B.esp"))
              && negative.RejectedMoves.Any(m => m.Contains("t #0")),
              negative.RejectedMoves.FirstOrDefault() ?? "(nothing rejected)");

        // D, E, F sit adjacent at the end of the baseline, so the pair distances are 1, 1 and 2 by construction.
        // Every edge weighs the SAME here, so displacement is the only thing left to sort on — the two cases after
        // this one hand contention a real difference and check that it outranks displacement.
        var trio = new[]
        {
            new ContentionEdge("D.esp", "E.esp", "record", 1),
            new ContentionEdge("E.esp", "F.esp", "record", 1),
            new ContentionEdge("D.esp", "F.esp", "asset", 1),
        };
        var trioReport = OrderSearch.Run(pairSet, trio, 8);
        var emittedTrio = trioReport.Candidates.Skip(1).ToList();
        Check("search: candidates sort by how many plugins they displace — the 2-index moves come before the 3-index one",
              emittedTrio.Count >= 4 && emittedTrio[0].Displaced == 2 && emittedTrio[1].Displaced == 2
              && emittedTrio.Take(3).All(c => c.Phase == 1) && emittedTrio[2].Displaced == 3,
              string.Join(", ", emittedTrio.Take(5).Select(c => $"{c.Displaced}/ph{c.Phase}")));
        int firstPair = emittedTrio.FindIndex(c => c.Moves.Count == 2);
        Check("search: a one-plugin move sorts before a two-plugin move — every single move is emitted first",
              firstPair > 0 && emittedTrio.Take(firstPair).All(c => c.Moves.Count == 1)
              && emittedTrio.Skip(firstPair).All(c => c.Moves.Count == 2),
              $"first composed candidate at index {firstPair} of {emittedTrio.Count}");
        Check("search: every emitted candidate is a permutation of the user's order — one plugin, one position",
              trioReport.Candidates.All(c => c.Plugins.Count == baseline.Count
                                          && c.Plugins.Distinct(StringComparer.OrdinalIgnoreCase).Count() == baseline.Count
                                          && c.Plugins.OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                                               .SequenceEqual(baseline.OrderBy(s => s, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)),
              $"{trioReport.Candidates.Count} candidate(s) checked");

        // opposite demands on ONE plugin. E sits before D and D before F is false in the baseline (D, E, F), so the
        // pair (D,E) asks for E above D and the pair (D,F) asks for F above D: two different global positions for D.
        var eAboveD = trioReport.Candidates.FirstOrDefault(c => Index(c.Plugins, "E.esp") < Index(c.Plugins, "D.esp"));
        var fAboveD = trioReport.Candidates.FirstOrDefault(c => Index(c.Plugins, "F.esp") < Index(c.Plugins, "D.esp") && c.Moves.Count == 1);
        Check("search: two contention pairs demanding opposite moves of the SAME plugin give two separate candidates, each one flat order",
              eAboveD is not null && fAboveD is not null
              && !eAboveD!.Plugins.SequenceEqual(fAboveD!.Plugins, StringComparer.OrdinalIgnoreCase)
              && Index(eAboveD.Plugins, "E.esp") < Index(eAboveD.Plugins, "D.esp")
              && Index(fAboveD.Plugins, "F.esp") < Index(fAboveD.Plugins, "D.esp")
              && eAboveD.Plugins.Count(n => n.Equals("D.esp", StringComparison.OrdinalIgnoreCase)) == 1
              && fAboveD.Plugins.Count(n => n.Equals("D.esp", StringComparison.OrdinalIgnoreCase)) == 1,
              $"E above D: [{string.Join(",", eAboveD!.Plugins.Skip(8))}]; F above D: [{string.Join(",", fAboveD!.Plugins.Skip(8))}] " +
              "— two orders, never one with a per-group opinion. A shared plugin cannot make two GROUPS at all: " +
              "connected components merge them, which is the same property one level up.");

        // Contention is the "affects a relevant winner" term. Both cases below are decided by hand: E|F contests 50
        // and D|E contests 1, so the heavy pair must come first even though the two cost the user the same; and in
        // the second, the heavy pair costs MORE to move and must still come first, because a change that touches
        // nothing is cheap and useless.
        var tie = OrderSearch.Run(pairSet, new[]
        {
            new ContentionEdge("D.esp", "E.esp", "record", 1),
            new ContentionEdge("E.esp", "F.esp", "record", 50),
        }, 8);
        var tieFirst = tie.Candidates[1];
        Check("search: with the displacement tied, the MORE CONTESTED pair is emitted first",
              tieFirst.Moves[0].Moved == "F.esp" && tieFirst.Moves[0].Reference == "E.esp"
              && tieFirst.Contention == 50 && tieFirst.Displaced == 2 && tie.Candidates[2].Contention == 1,
              $"first: {tieFirst.Changes[0]} (contested {tieFirst.Contention}); second contested {tie.Candidates[2].Contention}");

        var outranks = OrderSearch.Run(pairSet, new[]
        {
            new ContentionEdge("D.esp", "E.esp", "record", 1),
            new ContentionEdge("D.esp", "F.esp", "record", 50),
        }, 8);
        var outranksFirst = outranks.Candidates[1];
        Check("search: a heavily contested pair is emitted first even when moving it displaces MORE plugins",
              outranksFirst.Moves[0].Moved == "F.esp" && outranksFirst.Moves[0].Reference == "D.esp"
              && outranksFirst.Contention == 50 && outranksFirst.Displaced == 3
              && outranks.Candidates[2].Displaced == 2 && outranks.Candidates[2].Contention == 1,
              $"first: {outranksFirst.Changes[0]} (contested {outranksFirst.Contention}, displaced {outranksFirst.Displaced}); " +
              $"second displaced {outranks.Candidates[2].Displaced}, contested {outranks.Candidates[2].Contention}");

        Check("search: contention comes from the edges the CALLER supplies — no edge between a pair means weight 0, never a guess",
              outranks.Candidates.Skip(1).Any(c => c.Moves.Count == 1 && c.Contention == 0),
              "E.esp and F.esp share no edge; they are in the group only through D.esp");

        // ---- the budget ----------------------------------------------------------------------------------------
        var tight = OrderSearch.Run(pairSet, trio, 1);
        Check("budget: three legal single moves against a budget of 1 -> BUDGET EXHAUSTED, 2 generated and held back",
              tight.Result == SearchResult.BudgetExhausted && tight.Emitted == 1 && tight.GeneratedNotEmitted == 2,
              $"{tight.Result}, emitted {tight.Emitted}, held back {tight.GeneratedNotEmitted}");
        Check("budget: the composition phase was skipped, so the unexplored count is 'unknown' and never 0",
              tight.EnumerationCutShort && tight.Unexplored == "unknown", tight.Unexplored);
        Check("budget: the outcome SAYS that no working order found within the budget is not proof that none exists",
              tight.Outcome.Contains("is not proof that none exists"), tight.Outcome);
        var roomy = OrderSearch.Run(pairSet, trio, 500);
        Check("budget: with room to spare the result is CandidatesEmitted, nothing is held back, and enumeration ran out",
              roomy.Result == SearchResult.CandidatesEmitted && roomy.GeneratedNotEmitted == 0
              && !roomy.EnumerationCutShort && roomy.Unexplored == "0",
              $"{roomy.Result}, {roomy.Emitted} emitted, cut short {roomy.EnumerationCutShort}");
        var capped = OrderSearch.Run(pairSet, trio, 500, enumerationCap: 2);
        Check("budget: hitting the enumeration cap makes the unexplored count unknown, and says so",
              capped.EnumerationCutShort && capped.Unexplored == "unknown"
              && capped.Notes.Any(n => n.Contains("unknown")), capped.Notes.FirstOrDefault() ?? "");

        // ---- determinism ---------------------------------------------------------------------------------------
        var m1 = BuildManifest(root, baseline, headers, trio, parsed);
        var m2 = BuildManifest(root, baseline, headers, trio, parsed);
        Check("determinism: the same inputs twice produce a byte-identical manifest",
              m1 == m2 && m1.Length > 0, $"{m1.Length} bytes, identical: {m1 == m2}");
        var shuffled = Enumerable.Reverse(trio).ToArray();
        Check("determinism: the same inputs in a different ARRIVAL order still produce the identical manifest",
              BuildManifest(root, baseline, headers, shuffled, parsed) == m1, "groups and candidates do not depend on edge order");

        // ---- the configuration identity ------------------------------------------------------------------------
        ConfigPlugin P(string n) => new(n, Path.Combine(root, n), ConfigurationIdentity.Sha256File(Path.Combine(root, n)),
                                        new FileInfo(Path.Combine(root, n)).Length, "mod",
                                        headers[n]!.MasterFlag, headers[n]!.LightFlag, headers[n]!.Masters);
        var provs = new[] { new ConfigProvider("Data", "data") };
        var iAB = ConfigurationIdentity.Compute("t", new[] { P("D.esp"), P("E.esp") }, provs, Array.Empty<ConfigOverlay>(), "load-time", "baseline");
        var iBA = ConfigurationIdentity.Compute("t", new[] { P("E.esp"), P("D.esp") }, provs, Array.Empty<ConfigOverlay>(), "load-time", "baseline");
        Check("identity: two orders of the same plugins — equal winners, different history — FINGERPRINT DIFFERENTLY",
              iAB.Fingerprint != iBA.Fingerprint,
              "D.esp and E.esp override nothing of each other's, so the winners are identical and the history is not");
        Check("identity: the delta between them names the flipped pair rather than 'nothing changed'",
              iAB.DeltaTo(iBA).Any(d => d.Contains("order changed") && d.Contains("D.esp")),
              string.Join("; ", iAB.DeltaTo(iBA)));
        var again = ConfigurationIdentity.Compute("t", new[] { P("D.esp"), P("E.esp") }, provs, Array.Empty<ConfigOverlay>(), "load-time", "baseline");
        Check("identity: the same configuration twice fingerprints identically", iAB.Fingerprint == again.Fingerprint, iAB.Short);
    }

    static int Index(IReadOnlyList<string> order, string name)
    {
        for (int i = 0; i < order.Count; i++) if (order[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>The manifest orderprobe writes, built from the same parts, so determinism is proven on the bytes a
    /// consumer actually reads rather than on a hash of the search's internals.</summary>
    static string BuildManifest(string root, IReadOnlyList<string> baseline, Dictionary<string, PluginHeader?> headers,
                                IReadOnlyList<ContentionEdge> edges, ParsedRules parsed)
    {
        var rules = OrderRuleSet.Build(baseline, headers, parsed.Fixed, Array.Empty<OrderRule>());
        var report = OrderSearch.Run(rules, edges, 8);
        var plugins = baseline.Select(n => new ConfigPlugin(n, Path.Combine(root, n),
                                                            ConfigurationIdentity.Sha256File(Path.Combine(root, n)),
                                                            new FileInfo(Path.Combine(root, n)).Length, "mod",
                                                            headers[n]!.MasterFlag, headers[n]!.LightFlag, headers[n]!.Masters)).ToList();
        var identity = ConfigurationIdentity.Compute("profile:t", plugins, new[] { new ConfigProvider("Data", "data") },
                                                     Array.Empty<ConfigOverlay>(), "load-time", "baseline");
        var manifest = new OrderManifest(
            ConfigurationIdentity.CurrentSchema, "orderprobe", root, "t", identity, rules.Fixed,
            parsed.Rules.Select(r => new ManifestRule(r.Before, r.After, r.Hardness.ToString().ToLowerInvariant(), r.Origin,
                                                      r.Reason, OrderManifest.Prov(r.Provenance), true)).ToList(),
            parsed.Notes.Concat(rules.Notes).ToList(),
            rules.Contradictions.Select(OrderManifest.Of).ToList(),
            rules.UnsatisfiableSoft.Select(OrderManifest.Of).ToList(),
            report.Groups.Select(g => new ManifestGroup(g.Key, g.Members, g.Contention,
                g.Edges.Select(e => $"{e.A} | {e.B} | {e.Kind} | {e.Count}").ToList())).ToList(),
            report.Candidates.Select(c => new ManifestCandidate(c.Name, $"candidate-{c.Name}.json", c.Changes, c.Displaced,
                c.Contention, c.Moves.Count, c.Phase, c.Groups, c.Legality.Legal,
                c.Legality.Violations.Select(v => v.ToString()).ToList(),
                c.SatisfiesAdvice.Select(x => x.Full).ToList(),
                c.ViolatesAdvice.Select(x => x.Full).ToList())).ToList(),
            new ManifestBudget(8, report.Result.ToString(), report.Emitted, report.GeneratedNotEmitted,
                               report.Unexplored, report.EnumerationCutShort, report.Outcome),
            report.RejectedMoves, report.Notes, new[] { "synthetic fixture" });
        return manifest.Serialize();
    }
}
