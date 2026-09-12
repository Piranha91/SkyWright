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
        Plugin("G.esp", 0, "C.esp");                              // G declares C, which declares A: A -> C -> G
        File.WriteAllText(Path.Combine(root, "Bad.esp"), "this is not a TES4 plugin");   // header unreadable on purpose

        var names = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "Big.esm", "Flag.esp",
                            "A.esp", "B.esp", "C.esp", "D.esp", "E.esp", "F.esp", "G.esp" };
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

        // The grouping test is LINEAR (one pass for the master-flagged positions, a binary search per plain plugin), and
        // the sentence it writes is the part a reader acts on: the FIRST master-flagged plugin after the offender, its
        // position, and how many more there are. A.esp before Big.esm puts it ahead of Big.esm AND Flag.esp — both
        // numbers are hand-read off the order, not off the code.
        r = plain.Check(OrderSearch.Move(baseline, "A.esp", "Big.esm", before: true));
        Check("legality: one plain .esp ahead of TWO master-flagged plugins names the first and says 'and 1 more'",
              r.Violations.Count == 1 && r.Violations[0].Rule == EngineRule.Groups
              && r.Violations[0].Detail.Contains("(not master-flagged, position 3)")
              && r.Violations[0].Detail.Contains("master-flagged 'Big.esm' (position 4)")
              && r.Violations[0].Detail.Contains("and 1 more"),
              r.Violations.FirstOrDefault()?.ToString() ?? "(no violation)");

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
              && fixedReport.Search.OrderFixedPairs == 1
              && fixedReport.Search.OrderFixedByRule.Any(x => x.Rule == "fixed: 'B.esp'")
              && fixedReport.Search.HeaviestOrderFixed.Any(h => h.Contains("fixed: 'B.esp'")),
              fixedReport.Search.HeaviestOrderFixed.FirstOrDefault() ?? "(nothing dropped)");

        // ---- pairs whose order is already fixed ----------------------------------------------------------------
        // The question is not "is this order legal" but "could ANY legal candidate reverse these two". Every answer
        // below is read off the fixture by hand: the base masters' own block, the pins in the rules file, the ESM flag
        // bit, and the master tables A <- C <- G.
        Check("order-fixed: two base-game masters are fixed against each other, by the base-master rule",
              plain.OrderFixedBy("Skyrim.esm", "Update.esm") == EngineRule.BaseMasters,
              plain.OrderFixedBy("Skyrim.esm", "Update.esm") ?? "(movable)");
        Check("order-fixed: a base-game master against a plain .esp is fixed — the base masters lead everything",
              plain.OrderFixedBy("Skyrim.esm", "A.esp") == EngineRule.BaseMasters
              && plain.OrderFixedBy("A.esp", "Skyrim.esm") == EngineRule.BaseMasters,
              "and the answer does not depend on which way round the caller asked");
        Check("order-fixed: a master-flagged .esp against a plain .esp is fixed by the GROUPING rule, not by a master table",
              plain.OrderFixedBy("Flag.esp", "A.esp") == EngineRule.Groups,
              plain.OrderFixedBy("Flag.esp", "A.esp") ?? "(movable)");
        Check("order-fixed: a plugin's declared master is fixed against it",
              plain.OrderFixedBy("A.esp", "C.esp") == EngineRule.Masters,
              plain.OrderFixedBy("A.esp", "C.esp") ?? "(movable)");
        Check("order-fixed: the master closure is TRANSITIVE — G declares C, C declares A, so A and G are fixed too",
              plain.OrderFixedBy("A.esp", "G.esp") == EngineRule.Masters
              && plain.OrderFixedBy("G.esp", "A.esp") == EngineRule.Masters
              && !headers["G.esp"]!.Masters.Contains("A.esp"),
              "G.esp's own master table names only C.esp, so a pair-by-pair test would have called this movable");
        Check("order-fixed: the negative control — two plain .esps with no rule between them are MOVABLE",
              plain.OrderFixedBy("A.esp", "B.esp") is null && plain.Check(OrderSearch.Move(baseline, "B.esp", "A.esp", before: true)).Legal,
              "and Check agrees: the reversed order is legal");
        Check("order-fixed: the second negative control — two master-flagged plugins with no master table between them are MOVABLE",
              plain.OrderFixedBy("Big.esm", "Flag.esp") is null,
              "both load in the master block; the grouping rule is a rank, and a rank does not order within itself");

        Check("order-fixed: a pinned plugin is fixed against everything, and the rule names it",
              fixedSet.OrderFixedBy("A.esp", "B.esp") == "fixed: 'B.esp'" && fixedSet.OrderFixedBy("B.esp", "E.esp") == "fixed: 'B.esp'",
              fixedSet.OrderFixedBy("A.esp", "B.esp") ?? "(movable)");
        var twoPinned = OrderRuleSet.Build(baseline, headers, new[] { "E.esp", "B.esp" }, Array.Empty<OrderRule>());
        Check("order-fixed: with BOTH pinned the rule named is the same whichever way round the pair is asked",
              twoPinned.OrderFixedBy("E.esp", "B.esp") == "fixed: 'B.esp'" && twoPinned.OrderFixedBy("B.esp", "E.esp") == "fixed: 'B.esp'",
              twoPinned.OrderFixedBy("E.esp", "B.esp") ?? "(movable)");

        var requiredDE = OrderRuleSet.Build(baseline, headers, Array.Empty<string>(),
            new[] { new OrderRule("D.esp", "E.esp", RuleHardness.Required, "t #0", null, "D must lose to E") });
        Check("order-fixed: a REQUIRED input rule fixes its pair, and the answer names that rule",
              requiredDE.OrderFixedBy("D.esp", "E.esp") == "required: 'D.esp' before 'E.esp' [t #0]"
              && requiredDE.OrderFixedBy("E.esp", "D.esp") == "required: 'D.esp' before 'E.esp' [t #0]",
              requiredDE.OrderFixedBy("D.esp", "E.esp") ?? "(movable)");
        Check("order-fixed: a required rule fixes ITS pair only — D against F is still movable",
              requiredDE.OrderFixedBy("D.esp", "F.esp") is null, "no chain of rules or masters reaches from one to the other");

        // An unreadable header makes NO claim. It must not be read as "not master-flagged" (which would fix it against
        // every master) and it declares nothing. The rules that do not need a header still apply.
        var badNames = baseline.Append("Bad.esp").ToList();
        var badHeaders = new Dictionary<string, PluginHeader?>(headers, StringComparer.OrdinalIgnoreCase)
        { ["Bad.esp"] = PluginHeader.TryRead(Path.Combine(root, "Bad.esp")) };
        var badSet = OrderRuleSet.Build(badNames, badHeaders, Array.Empty<string>(), Array.Empty<OrderRule>());
        Check("order-fixed: an UNREADABLE header fixes nothing by the grouping or master rules, and Check agrees",
              badHeaders["Bad.esp"] is null && badSet.OrderFixedBy("Bad.esp", "Flag.esp") is null
              && badSet.Check(OrderSearch.Move(badNames, "Bad.esp", "Flag.esp", before: true)).Legal,
              "a null header is not 'not master-flagged', and the move it would forbid is in fact legal");
        Check("order-fixed: the rules that need no header still fire against an unreadable one",
              badSet.OrderFixedBy("Bad.esp", "Skyrim.esm") == EngineRule.BaseMasters,
              badSet.OrderFixedBy("Bad.esp", "Skyrim.esm") ?? "(movable)");

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

        // The path orderprobe takes when nobody passed --rules. Pinned here rather than left to a literal at the call
        // site: without a rules file nothing holds the user's hand-sorted positions, and a run that does not SAY so
        // hands back candidates that move plugins the user meant to stay put.
        var noFile = ParsedRules.NoFile();
        Check("rules: the no-file path carries the warning that nothing pins the user's hand-sorted positions",
              noFile.Rules.Count == 0 && noFile.Fixed.Count == 0
              && noFile.Notes.Any(n => n == OrderRules.NoRulesWarning)
              && OrderRules.NoRulesWarning.Contains("fixed"),
              noFile.Notes.FirstOrDefault() ?? "(no note)");

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
              && negative.Search.OrderFixedByRule.Any(x => x.Rule.Contains("t #0")),
              negative.Search.HeaviestOrderFixed.FirstOrDefault() ?? "(nothing dropped)");

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

        // Weight still comes from the caller's edges and nowhere else — and a pair the caller gave no edge for is now
        // not built at all rather than built at weight 0. E and F are in the group only through D, so a move between
        // them would change no winner between them; the count of such pairs is reported rather than hidden.
        Check("search: contention comes from the edges the CALLER supplies — a pair with no edge is not built, and is counted",
              outranks.Search.NoContentionPairsNotBuilt == 1
              && !outranks.Candidates.Any(c => c.Moves.Any(m => PairIs(m, "E.esp", "F.esp")))
              && outranks.Notes.Any(n => n.Contains(OrderSearch.NoContentionNotBuilt)),
              $"E.esp and F.esp share no edge; they are in the group only through D.esp — {outranks.Search.NoContentionPairsNotBuilt} pair(s) not built, " +
              $"{outranks.Search.WeightedPairs} weighted pair(s) walked");

        // One direction of a pair can be illegal on its own: A sits at 5 and D at 8, and C declares A, so moving A to
        // sit after D would put it behind its own dependent. The search must take the other direction and carry THAT
        // order's legality — the candidate's own Legality is what the manifest and the console column report, and since
        // the legality is computed by the caller and handed to the candidate, a swap would tell a reader the order being
        // offered is illegal.
        var oneWay = OrderSearch.Run(pairSet, new[] { new ContentionEdge("A.esp", "D.esp", "record", 30) }, 8);
        var oneWayCand = oneWay.Candidates.Skip(1).FirstOrDefault();
        Check("search: when only one direction is legal, that one is the candidate and the other way is named as refused",
              oneWayCand is not null && oneWayCand.Moves[0].Moved == "D.esp" && oneWayCand.Moves[0].Placement == "before"
              && oneWayCand.Legality.Legal && pairSet.Check(oneWayCand.Plugins).Legal
              && !pairSet.Check(OrderSearch.Move(baseline, "A.esp", "D.esp", before: false)).Legal
              && oneWay.RejectedMoves.Any(m => m.Contains("A.esp") && m.Contains(EngineRule.Masters)),
              $"{oneWayCand?.Changes[0] ?? "(no candidate)"} — {oneWay.RejectedMoves.FirstOrDefault() ?? "(nothing rejected)"}");
        Check("search: every emitted candidate's Legality is the legality of the order it actually ships",
              new[] { oneWay, trioReport, tie, outranks }.All(rep => rep.Candidates.All(c =>
                  c.Legality.Legal == pairSet.Check(c.Plugins).Legal
                  && c.Legality.Violations.Count == pairSet.Check(c.Plugins).Violations.Count)),
              "checked against a fresh Check of each candidate's own plugin list, not against the one the search held");

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

        // ---- the prune, and what the enumeration spends its cap on ---------------------------------------------
        // A pair a hard rule already orders has no search value — both moves are illegal, always — and on a real order
        // those pairs carry the heaviest weight, because a master is contested by everything that overrides it. Here
        // the heavy pair is A <- C (a declared master at 500) and the Skyrim.esm pair (a base master, 3 + 2 over two
        // edges); the only movable pair weighs 20, and it is the one the search must spend its moves on.
        var pruneEdges = new[]
        {
            new ContentionEdge("A.esp", "C.esp", "record", 500),
            new ContentionEdge("Skyrim.esm", "A.esp", "record", 3),
            new ContentionEdge("Skyrim.esm", "A.esp", "asset", 2),
            new ContentionEdge("D.esp", "E.esp", "record", 20),
        };
        var pruned = OrderSearch.Run(pairSet, pruneEdges, 8);
        var byMasters = pruned.Search.OrderFixedByRule.FirstOrDefault(x => x.Rule == EngineRule.Masters);
        var byBase = pruned.Search.OrderFixedByRule.FirstOrDefault(x => x.Rule == EngineRule.BaseMasters);
        Check("prune: the pairs a hard rule already orders are dropped before grouping, counted by rule and by weight",
              pruned.Search.EdgesSupplied == 4 && pruned.Search.PairsSupplied == 3
              && pruned.Search.OrderFixedPairs == 2 && pruned.Search.OrderFixedEdges == 3 && pruned.Search.OrderFixedContention == 505
              && byMasters is { Pairs: 1, Edges: 1, Contention: 500 } && byBase is { Pairs: 1, Edges: 2, Contention: 5 },
              $"{pruned.Search.OrderFixedPairs} pair(s) / {pruned.Search.OrderFixedEdges} edge(s) / {pruned.Search.OrderFixedContention} contention; " +
              string.Join(" | ", pruned.Search.OrderFixedByRule.Select(x => $"{x.Rule} = {x.Pairs}/{x.Edges}/{x.Contention}")));
        Check("prune: the heaviest dropped pair is named with its weight and its rule, so the weight is not silently gone",
              pruned.Search.HeaviestOrderFixed.Count == 2
              && pruned.Search.HeaviestOrderFixed[0] == $"A.esp | C.esp | 500 | {EngineRule.Masters}",
              string.Join(" ;; ", pruned.Search.HeaviestOrderFixed));
        Check("prune: a dropped pair enters no interaction group and no candidate — the 20 pair is candidate 1",
              pruned.Groups.Count == 1 && pruned.Groups[0].Members.SequenceEqual(new[] { "D.esp", "E.esp" })
              && pruned.Candidates.Count >= 2 && pruned.Candidates[1].Contention == 20
              && Index(pruned.Candidates[1].Plugins, "E.esp") < Index(pruned.Candidates[1].Plugins, "D.esp")
              && pruned.Candidates.Skip(1).All(c => c.Moves.All(m => m.Moved != "A.esp" && m.Moved != "C.esp" && m.Moved != "Skyrim.esm")),
              $"groups [{string.Join(",", pruned.Groups.Select(g => g.Key))}]; candidate 1: {pruned.Candidates.ElementAtOrDefault(1)?.Changes[0] ?? "(none)"}");

        // Heaviest-first, globally. 50, 20 and 5 over three movable pairs: with a cap that allows exactly one pair's two
        // constructions, the 50 pair is the one that gets built — under a per-group walk in index order it would have
        // been D|E, because D sorts first. The reached contention says where the walk stopped.
        var weighted = new[]
        {
            new ContentionEdge("E.esp", "F.esp", "record", 50),
            new ContentionEdge("D.esp", "E.esp", "record", 20),
            new ContentionEdge("D.esp", "F.esp", "record", 5),
        };
        var capOne = OrderSearch.Run(pairSet, weighted, 8, enumerationCap: 2);
        Check("heaviest-first: with room for one pair only, the HEAVIEST pair is the one built",
              capOne.Search.WeightedPairs == 3 && capOne.Search.PairsEnumerated == 1
              && capOne.Emitted == 1 && capOne.Candidates[1].Contention == 50
              && capOne.Candidates[1].Moves[0].Moved == "F.esp" && capOne.Candidates[1].Moves[0].Reference == "E.esp"
              && !capOne.Candidates.Any(c => c.Moves.Any(m => m.Moved == "D.esp" || m.Reference == "D.esp")),
              $"{capOne.Search.PairsEnumerated} of {capOne.Search.WeightedPairs} pair(s) enumerated: {capOne.Candidates.ElementAtOrDefault(1)?.Changes[0] ?? "(none)"}");
        Check("heaviest-first: the cut-short walk reports the weight it REACHED, and the rest as unknown rather than absent",
              capOne.Search.ReachedContention == 50 && capOne.EnumerationCutShort && capOne.Unexplored == "unknown",
              $"reached {capOne.Search.ReachedContention}, unexplored {capOne.Unexplored}");
        var capNone = OrderSearch.Run(pairSet, weighted, 8);
        Check("heaviest-first: a walk that completes reaches the LIGHTEST pair, and then nothing is unexplored",
              capNone.Search.PairsEnumerated == 3 && capNone.Search.ReachedContention == 5
              && !capNone.EnumerationCutShort && capNone.Unexplored == "0" && capNone.Candidates.Count >= 4
              && capNone.Candidates[1].Contention == 50 && capNone.Candidates[2].Contention == 20 && capNone.Candidates[3].Contention == 5,
              $"reached {capNone.Search.ReachedContention}; emitted contention " +
              string.Join(", ", capNone.Candidates.Skip(1).Take(3).Select(c => c.Contention)));

        // The threshold. It is a statement about what is worth a move, so what it dropped is reported in the same three
        // numbers as the rule prune — and a pair a RULE names is exempt, because a rule is the caller saying the pair
        // matters whatever the files weigh.
        var thresholded = OrderSearch.Run(pairSet, weighted, 8, minContention: 10, minContentionSource: "--min-contention");
        Check("threshold: a pair below --min-contention is dropped, counted, and its weight reported",
              thresholded.Search.MinContention == 10 && thresholded.Search.MinContentionSource == "--min-contention"
              && thresholded.Search.BelowThresholdPairs == 1 && thresholded.Search.BelowThresholdEdges == 1
              && thresholded.Search.BelowThresholdContention == 5 && thresholded.Search.WeightedPairs == 2
              && !thresholded.Candidates.Any(c => c.Moves.Any(m => PairIs(m, "D.esp", "F.esp"))),
              $"{thresholded.Search.BelowThresholdPairs} pair(s) / {thresholded.Search.BelowThresholdContention} contention dropped, " +
              $"{thresholded.Search.WeightedPairs} pair(s) left");
        var noThreshold = OrderSearch.Run(pairSet, weighted, 8, minContention: 0);
        Check("threshold: 0 means no threshold — every pair survives and the source of the number is reported",
              noThreshold.Search.BelowThresholdPairs == 0 && noThreshold.Search.WeightedPairs == 3
              && noThreshold.Search.MinContention == 0 && noThreshold.Search.MinContentionSource == "default",
              $"{noThreshold.Search.WeightedPairs} weighted pair(s)");
        var namedPair = OrderSearch.Run(pairSet, new[]
        {
            new ContentionEdge("E.esp", "F.esp", "record", 50),
            new ContentionEdge("A.esp", "B.esp", "explicit", 1),
        }, 8, minContention: 10);
        Check("threshold: a pair a RULE names survives the threshold it is under, and is counted as kept by that rule",
              namedPair.Search.KeptByRulePairs == 1 && namedPair.Search.BelowThresholdPairs == 0
              && namedPair.Search.WeightedPairs == 2
              && namedPair.Candidates.Skip(1).Any(c => c.Moves.Count == 1 && PairIs(c.Moves[0], "A.esp", "B.esp")),
              $"{namedPair.Search.KeptByRulePairs} pair(s) kept by a rule; candidates " +
              string.Join(" | ", namedPair.Candidates.Skip(1).Select(c => c.Changes[0])));

        // ---- determinism ---------------------------------------------------------------------------------------
        var m1 = BuildManifest(root, baseline, headers, trio, parsed);
        var m2 = BuildManifest(root, baseline, headers, trio, parsed);
        Check("determinism: the same inputs twice produce a byte-identical manifest",
              m1 == m2 && m1.Length > 0, $"{m1.Length} bytes, identical: {m1 == m2}");
        var shuffled = Enumerable.Reverse(trio).ToArray();
        Check("determinism: the same inputs in a different ARRIVAL order still produce the identical manifest",
              BuildManifest(root, baseline, headers, shuffled, parsed) == m1, "groups and candidates do not depend on edge order");

        // The prune and the walk are only reportable if they reach the file a consumer reads. ordersweep reads this JSON
        // as a dictionary, so the proof is on the bytes, not on the record in memory.
        string searchSection = "(unreadable)";
        bool searchOk = false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(m1);
            var sec = doc.RootElement.GetProperty("Search");
            searchSection = sec.ToString().Replace("\n", " ").Replace("\r", "");
            searchOk = sec.GetProperty("MinContention").GetInt32() == 0
                    && sec.GetProperty("MinContentionSource").GetString() == "default"
                    && sec.GetProperty("WeightedPairs").GetInt32() == 3
                    && sec.GetProperty("PairsEnumerated").GetInt32() == 3
                    && sec.GetProperty("ReachedContention").GetInt32() == 1
                    && sec.GetProperty("NoContentionPairsNotBuilt").GetInt64() == 0
                    && sec.GetProperty("OrderFixedPairs").GetInt32() == 0;
        }
        catch (Exception ex) { searchSection = $"threw: {ex.Message}"; }
        Check("manifest: the Search section reaches the written JSON, with the threshold, the walk and the prune in it",
              searchOk, searchSection.Length > 400 ? searchSection[..400] : searchSection);

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

    /// <summary>Whether a move is the one that reverses {x, y} — the move's own pair, whichever of the two it moved.</summary>
    static bool PairIs(CandidateMove m, string x, string y) =>
        (m.Moved.Equals(x, StringComparison.OrdinalIgnoreCase) && m.Reference.Equals(y, StringComparison.OrdinalIgnoreCase))
     || (m.Moved.Equals(y, StringComparison.OrdinalIgnoreCase) && m.Reference.Equals(x, StringComparison.OrdinalIgnoreCase));

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
            report.Search,
            report.RejectedMoves, report.Notes, new[] { "synthetic fixture" });
        return manifest.Serialize();
    }
}
