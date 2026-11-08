using HarnessShared;

// PROOF BY CONSTRUCTION — reading an author's stated load order off a mod page
// (_shared/OrderAdvice.cs; Q39, a roadmap 4.20 follow-up feeding 4.21's catalogue, 2026-09-11).
//
// The fixture is HAND-WRITTEN page text and a HAND-WRITTEN world. Every expected answer below was worked out from
// those two by hand — which mention names which mod, which mod owns which plugin, which date is newer — never by
// running the extractor and writing down what it said. A proof that derived its expectation the way the code does
// would only prove the code equals itself.
//
// The cases follow the four ways this layer can be quietly wrong, which are not the same four as the search's:
//   invented   a rule the page does not state — the generic "patches load after what they patch" turned into a
//              constraint, a conditional taken as unconditional, a disagreement silently resolved one way;
//   guessed    a mention resolved to the wrong mod (substring matching, an edition suffix fuzzed on the wrong side)
//              or to the wrong plugin (a mod with ten plugins in its folder and the first one picked);
//   lost       a real statement read as "no order stated", which is the failure that looks like success;
//   laundered  a rule that reaches the search without its provenance, or as `required` rather than `advice`.
static class AdviceProof
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nPROOF — author-stated load orders (_shared/OrderAdvice.cs, Q39, a roadmap 4.20 follow-up)");
        Console.WriteLine("   Hand-written page text against a hand-written world; every expectation worked out by hand.\n");

        // -------------------------------------------------------------------------------------------------
        //  W1 — the Dragonsreach shape: a bulleted list, a self-referring last item, and a symptom sentence.
        //  Known names, by hand: the page's own "JK's and Redbag's Dragonsreach Patch" (45686), and two
        //  requirements whose page names differ from the way the description spells them — "RedBag's
        //  Dragonsreach - SE" (100) written as "Redbag's Dragonsreach", and "JK's Dragonsreach" (200).
        // -------------------------------------------------------------------------------------------------
        var w1 = World(
            45686, "JK's and Redbag's Dragonsreach Patch", "https://www.nexusmods.com/skyrimspecialedition/mods/45686",
            read: "2026-09-11", updated: "2021-02-02", mainVer: "1.0", mainDate: "2021-01-15",
            masters: new[] { "Skyrim.esm", "RedBagsDragonsreach.esp", "JKs Dragonsreach.esp" },
            mods: new[]
            {
                Mod(45686, "JK's and Redbag's Dragonsreach Patch", "JKRB Patch", new[] { "JKs RedBag Dragonsreach Patch.esp" }, "1.0", "2021-01-15"),
                Mod(100, "RedBag's Dragonsreach - SE", "RedBags Dragonsreach", new[] { "RedBagsDragonsreach.esp" }, "1.2", "2020-05-05"),
                Mod(200, "JK's Dragonsreach", "JKs Dragonsreach", new[] { "JKs Dragonsreach.esp" }, "2.0", "2020-06-06"),
            });

        const string symptom = "If the mods are loaded the other way around, Numinex's skull will be invisible when you use the activator.";
        var t1 = "[b]Load the mods in this order:[/b]\n"
               + "[list][*]Redbag's Dragonsreach[*]JK's Dragonsreach[*]JK's and Redbag's Dragonsreach Patch (this mod)[/list]\n"
               + symptom;
        var r1 = OrderAdvice.Extract(t1, w1);

        Check("advice/S1: a bulleted 'Load the mods in this order:' list yields n-1 ADJACENT rules, in the list's order",
              r1.Rules.Count == 2
              && r1.Rules[0].Before == "RedBagsDragonsreach.esp" && r1.Rules[0].After == "JKs Dragonsreach.esp"
              && r1.Rules[1].Before == "JKs Dragonsreach.esp" && r1.Rules[1].After == "JKs RedBag Dragonsreach Patch.esp",
              Rules(r1));
        Check("advice/S1: the list is ONE statement, and the three items are carried as its items",
              r1.Statements.Count == 1 && r1.Statements[0].Shape == AdviceShape.OrderList && r1.Statements[0].Items.Count == 3,
              $"{r1.Statements.Count} statement(s): {string.Join(" | ", r1.Statements.Select(s => s.Text))}");
        Check("advice/S1: the symptom sentence after the list is carried VERBATIM into every rule's provenance note",
              r1.Rules.Count == 2 && r1.Rules.All(x => x.Note is not null && x.Note.Contains($"symptom stated: \"{symptom}\"", StringComparison.Ordinal)),
              r1.Rules.Count == 0 ? "(no rules)" : r1.Rules[0].Note ?? "(no note)");
        Check("advice/D2: 'Redbag's Dragonsreach' matches the requirement 'RedBag's Dragonsreach - SE' with the KNOWN name's edition suffix dropped",
              r1.Resolutions.Any(x => x.ModId == 100 && x.Plugin == "RedBagsDragonsreach.esp"),
              string.Join(" | ", r1.Resolutions.Select(x => $"{x.ModId}->{x.Plugin ?? "(none)"}")));
        Check("advice/D2: the '(this mod)' item resolves to the PAGE's own mod, not to a requirement",
              r1.Resolutions.Any(x => x.ModId == 45686 && x.Plugin == "JKs RedBag Dragonsreach Patch.esp"),
              string.Join(" | ", r1.Resolutions.Select(x => $"{x.ModId}->{x.Plugin ?? "(none)"}")));
        Check("advice/S1: a list that resolved cleanly produces NO refusal note",
              r1.Notes.Count == 0 && r1.NothingStated is null, $"{r1.Notes.Count} note(s)");

        // The fragment is the thing the search actually reads, so it is checked through the reader the search uses.
        var parsed1 = OrderRules.Parse(OrderAdvice.ToRulesJson(r1), "fixture 45686");
        Check("advice/fragment: the generated rules file parses through OrderRules.Parse with no refusal, 2 rules, nothing fixed",
              parsed1.Rules.Count == 2 && parsed1.Fixed.Count == 0, $"{parsed1.Rules.Count} rule(s), {parsed1.Fixed.Count} fixed, {parsed1.Notes.Count} note(s)");
        Check("advice/fragment: EVERY rule comes back as advice — never required, never preference",
              parsed1.Rules.All(x => x.Hardness == RuleHardness.Advice),
              string.Join(", ", parsed1.Rules.Select(x => x.Hardness.ToString())));
        Check("advice/fragment: provenance survives the round trip — the page URL, the read date and the version scope",
              parsed1.Rules.All(x => x.Provenance is not null
                                  && x.Provenance!.Source == "https://www.nexusmods.com/skyrimspecialedition/mods/45686"
                                  && x.Provenance.Checked == "2026-09-11"
                                  && x.Provenance.VersionScope.Contains("newest MAIN file v1.0", StringComparison.Ordinal)),
              parsed1.Rules.Count == 0 ? "(none)" : parsed1.Rules[0].Provenance?.Line ?? "(no provenance)");
        Check("advice/fragment: the word 'required' never appears as a hardness in the generated file",
              !OrderAdvice.ToRulesJson(r1).Contains("\"hardness\": \"required\"", StringComparison.OrdinalIgnoreCase),
              "hardness values: " + string.Join(", ", parsed1.Rules.Select(x => x.Hardness.ToString())));
        Check("advice/fragment: the notes ride along as $comment, and OrderRules.Parse ignores them rather than refusing",
              OrderAdvice.ToRulesJson(r1).Contains("$comment", StringComparison.Ordinal) && parsed1.Notes.Count == 0,
              $"{parsed1.Notes.Count} parse note(s)");

        // D3 on the same fixture: both installed dates predate the page's last update.
        Check("advice/D3: an installed file older than the page's last update gives the 'checked ... not newer' line and NO stale note",
              r1.StalenessLines.Count(l => l.StartsWith("checked:", StringComparison.Ordinal)) == 3
              && r1.Rules.All(x => x.Note is null || !x.Note.Contains("may be stale", StringComparison.Ordinal)),
              string.Join(" | ", r1.StalenessLines));

        // -------------------------------------------------------------------------------------------------
        //  D3, the other two ways. Same text, same world, only the dates moved.
        // -------------------------------------------------------------------------------------------------
        var w1Stale = w1 with
        {
            Installed = Dict(new[]
            {
                Mod(45686, "JK's and Redbag's Dragonsreach Patch", "JKRB Patch", new[] { "JKs RedBag Dragonsreach Patch.esp" }, "1.0", "2021-01-15"),
                Mod(100, "RedBag's Dragonsreach - SE", "RedBags Dragonsreach", new[] { "RedBagsDragonsreach.esp" }, "1.2", "2020-05-05"),
                Mod(200, "JK's Dragonsreach", "JKs Dragonsreach", new[] { "JKs Dragonsreach.esp" }, "3.1", "2024-08-08"),
            }),
        };
        var rStale = OrderAdvice.Extract(t1, w1Stale);
        Check("advice/D3: an installed file NEWER than the page's last update annotates the rule 'statement may be stale', naming version and both dates",
              rStale.Rules.Count == 2
              && rStale.Rules.All(x => x.Note is not null && x.Note!.Contains("statement may be stale: JK's Dragonsreach installed 3.1 (uploaded 2024-08-08 UTC) is newer than the page's last update (2021-02-02 UTC)", StringComparison.Ordinal)),
              rStale.Rules.Count == 0 ? "(no rules)" : rStale.Rules[0].Note ?? "(no note)");
        Check("advice/D3: staleness ANNOTATES — the two rules are still emitted, still advice, unchanged either side",
              rStale.Rules.Count == 2 && rStale.Rules[0].Before == "RedBagsDragonsreach.esp" && rStale.Rules[1].After == "JKs RedBag Dragonsreach Patch.esp",
              Rules(rStale));

        var w1Unknown = w1 with
        {
            Installed = Dict(new[]
            {
                Mod(45686, "JK's and Redbag's Dragonsreach Patch", "JKRB Patch", new[] { "JKs RedBag Dragonsreach Patch.esp" }, "1.0", "2021-01-15"),
                Mod(100, "RedBag's Dragonsreach - SE", "RedBags Dragonsreach", new[] { "RedBagsDragonsreach.esp" }, "1.2", null),
                Mod(200, "JK's Dragonsreach", "JKs Dragonsreach", new[] { "JKs Dragonsreach.esp" }, "2.0", "2020-06-06"),
            }),
        };
        var rUnknown = OrderAdvice.Extract(t1, w1Unknown);
        Check("advice/D3: an unknown installed date is reported as NOT CHECKED — never read as 'not newer'",
              rUnknown.StalenessLines.Any(l => l.Contains("staleness not checked for RedBag's Dragonsreach - SE: installed version and date unknown", StringComparison.Ordinal))
              && rUnknown.Rules[0].Note!.Contains("staleness not checked for RedBag's Dragonsreach - SE", StringComparison.Ordinal),
              string.Join(" | ", rUnknown.StalenessLines));

        // -------------------------------------------------------------------------------------------------
        //  W2 — the Blue Palace shape: a bare list, blank lines between the items, and a last item that names
        //  the page's own mod in full AND flags itself "(this mod)".
        // -------------------------------------------------------------------------------------------------
        var w2 = World(
            45485, "JK's Blue Palace - Enhanced Solitude Patch", "https://www.nexusmods.com/skyrimspecialedition/mods/45485",
            read: "2026-09-11", updated: "2021-03-03", mainVer: "1.1", mainDate: "2021-03-03",
            masters: Array.Empty<string>(),
            mods: new[]
            {
                Mod(45485, "JK's Blue Palace - Enhanced Solitude Patch", "JKBP ES Patch", new[] { "JKBluePalaceEnhancedSolitude.esp" }, "1.1", "2021-03-03"),
                Mod(300, "Enhanced Solitude SSE", "Enhanced Solitude", new[] { "Enhanced Solitude.esp" }, "3.0", "2020-01-01"),
                Mod(400, "JK's Blue Palace", "JKs Blue Palace", new[] { "JKs Blue Palace.esp" }, "1.0", "2020-02-02"),
            });
        var t2 = "Load Order:\n\nEnhanced Solitude\n\nJK's Blue Palace\n\nJK's Blue Palace - Enhanced Solitude patch (this mod)\n";
        var r2 = OrderAdvice.Extract(t2, w2);
        Check("advice/S1: BARE item lines with blank lines between them are one list, and yield the same n-1 rules",
              r2.Rules.Count == 2
              && r2.Rules[0].Before == "Enhanced Solitude.esp" && r2.Rules[0].After == "JKs Blue Palace.esp"
              && r2.Rules[1].Before == "JKs Blue Palace.esp" && r2.Rules[1].After == "JKBluePalaceEnhancedSolitude.esp",
              Rules(r2));
        Check("advice/D2: 'Enhanced Solitude' matches the requirement 'Enhanced Solitude SSE' with the suffix dropped from the KNOWN name",
              r2.Resolutions.Any(x => x.ModId == 300 && x.Plugin == "Enhanced Solitude.esp"), string.Join(" | ", r2.Resolutions.Select(x => $"{x.ModId}->{x.Plugin}")));
        Check("advice/S1: no symptom sentence follows this list, so no rule claims one",
              r2.Rules.All(x => x.Note is null || !x.Note!.Contains("symptom stated", StringComparison.Ordinal)),
              r2.Rules.Count == 0 ? "(no rules)" : r2.Rules[0].Note ?? "(no note)");

        // -------------------------------------------------------------------------------------------------
        //  Where a BARE list ends. This is the one place the definition had to be sharpened, and it is sharp in
        //  the direction that refuses: only a line that reads as a HEADING or as PROSE ends the list. A bare line
        //  that is short, unpunctuated and names nothing known was meant as an item, and a list with such an item
        //  is refused whole — the alternative silently emits the first two thirds of a three-item sequence.
        // -------------------------------------------------------------------------------------------------
        var tBareBad = "Load Order:\n\nEnhanced Solitude\n\nJK's Blue Palace\n\nSolitude Skyway\n\nJK's Blue Palace - Enhanced Solitude patch (this mod)\n";
        var rBareBad = OrderAdvice.Extract(tBareBad, w2);
        Check("advice/S1: a bare list with a SHORT unresolved line is refused WHOLE — never the first two items as a partial list",
              rBareBad.Rules.Count == 0 && rBareBad.Notes.Count == 1
              && rBareBad.Notes[0].Contains("Solitude Skyway", StringComparison.Ordinal)
              && rBareBad.Notes[0].Contains("whole list is refused", StringComparison.Ordinal),
              $"{rBareBad.Rules.Count} rule(s); {string.Join(" | ", rBareBad.Notes)}");

        var tBareHeading = "Load Order:\n\nEnhanced Solitude\n\nJK's Blue Palace\n\nJK's Blue Palace - Enhanced Solitude patch (this mod)\n\nRequirements:\n\nEnhanced Solitude\n";
        var rBareHeading = OrderAdvice.Extract(tBareHeading, w2);
        Check("advice/S1: a following HEADING line ('Requirements:') ends a bare list cleanly — 3 items, 2 rules",
              rBareHeading.Rules.Count == 2 && rBareHeading.Notes.Count == 0
              && rBareHeading.Rules[0].Before == "Enhanced Solitude.esp" && rBareHeading.Rules[1].After == "JKBluePalaceEnhancedSolitude.esp",
              Rules(rBareHeading) + $"; {rBareHeading.Notes.Count} note(s)");

        var tBareProse = "Load Order:\n\nEnhanced Solitude\n\nJK's Blue Palace\n\nJK's Blue Palace - Enhanced Solitude patch (this mod)\n\nIf I've missed anything, please let me know!\n";
        var rBareProse = OrderAdvice.Extract(tBareProse, w2);
        Check("advice/S1: a following PROSE line ending in '!' ends a bare list cleanly — 3 items, 2 rules",
              rBareProse.Rules.Count == 2 && rBareProse.Notes.Count == 0
              && rBareProse.Rules[0].Before == "Enhanced Solitude.esp" && rBareProse.Rules[1].After == "JKBluePalaceEnhancedSolitude.esp",
              Rules(rBareProse) + $"; {rBareProse.Notes.Count} note(s)");

        var tBareLong = "Load Order:\n\nEnhanced Solitude\n\nJK's Blue Palace\n\nJK's Blue Palace - Enhanced Solitude patch (this mod)\n\nthe rest of this page is about what the patch actually changes\n";
        var rBareLong = OrderAdvice.Extract(tBareLong, w2);
        Check("advice/S1: a following line of more than eight words ends a bare list too — it is prose, not a mod name",
              rBareLong.Rules.Count == 2 && rBareLong.Notes.Count == 0, Rules(rBareLong) + $"; {rBareLong.Notes.Count} note(s)");

        var tBareVersion = "Load Order:\n\nEnhanced Solitude SSE v2.8\n\nJK's Blue Palace\n\nJK's Blue Palace - Enhanced Solitude patch (this mod)\n";
        var rBareVersion = OrderAdvice.Extract(tBareVersion, w2);
        Check("advice/D2: an item that carries a version ('Enhanced Solitude SSE v2.8') resolves to nothing — D2 has no version arm — so the list is refused whole",
              rBareVersion.Rules.Count == 0 && rBareVersion.Notes.Count == 1
              && rBareVersion.Notes[0].Contains("Enhanced Solitude SSE v2.8", StringComparison.Ordinal),
              $"{rBareVersion.Rules.Count} rule(s); {string.Join(" | ", rBareVersion.Notes)}");

        // -------------------------------------------------------------------------------------------------
        //  A list with an item that names nothing known: refused WHOLE. The first item resolves and the third
        //  would too, so a partial reading would emit "Redbag's before JK's" — a rule the page does not state.
        // -------------------------------------------------------------------------------------------------
        var tBad = "Load the mods in this order:\n[*]Redbag's Dragonsreach[*]Some Mod That Is Not A Requirement[*]JK's Dragonsreach\n";
        var rBad = OrderAdvice.Extract(tBad, w1);
        Check("advice/S1: a list with an item that matches no known mod is refused WHOLE — no partial list, no rules",
              rBad.Rules.Count == 0 && rBad.Notes.Count == 1
              && rBad.Notes[0].Contains("Some Mod That Is Not A Requirement", StringComparison.Ordinal)
              && rBad.Notes[0].Contains("whole list is refused", StringComparison.Ordinal),
              $"{rBad.Rules.Count} rule(s); notes: {string.Join(" | ", rBad.Notes)}");
        Check("advice/S1: a refused list is NOT 'no load order stated' — the page said something and the report says what",
              rBad.NothingStated is null, rBad.NothingStated ?? "(null, as expected)");

        // -------------------------------------------------------------------------------------------------
        //  W3 — a plain three-requirement world for the sentence shapes.
        // -------------------------------------------------------------------------------------------------
        var w3 = World(
            900, "Test Patch", "https://www.nexusmods.com/skyrimspecialedition/mods/900",
            read: "2026-09-11", updated: "2026-01-01", mainVer: "2.0", mainDate: "2026-01-01",
            masters: Array.Empty<string>(),
            mods: new[]
            {
                Mod(900, "Test Patch", "Test Patch", new[] { "TestPatch.esp" }, "2.0", "2026-01-01"),
                Mod(100, "Alpha Mod", "Alpha", new[] { "Alpha.esp" }, "1.0", "2025-01-01"),
                Mod(200, "Beta Mod", "Beta", new[] { "Beta.esp" }, "1.0", "2025-01-01"),
                Mod(300, "Gamma Mod", "Gamma", new[] { "Gamma.esp" }, "1.0", "2025-01-01"),
            });

        void Pair(string label, string sentence, string before, string after)
        {
            var r = OrderAdvice.Extract(sentence, w3);
            Check($"advice/S2: {label}",
                  r.Rules.Count == 1 && r.Rules[0].Before == before && r.Rules[0].After == after,
                  $"\"{sentence}\" -> {Rules(r)}");
        }
        Pair("'Load X after Y' puts Y first", "Load Alpha Mod after Beta Mod.", "Beta.esp", "Alpha.esp");
        Pair("'Load X before Y' puts X first", "Load Alpha Mod before Beta Mod.", "Alpha.esp", "Beta.esp");
        Pair("'X must be loaded before Y' puts X first", "Alpha Mod must be loaded before Beta Mod.", "Alpha.esp", "Beta.esp");
        Pair("'X loads after Y' puts Y first", "Alpha Mod loads after Beta Mod.", "Beta.esp", "Alpha.esp");
        Pair("'X should go BELOW Y' means later in the order, so Y first", "Alpha Mod should go below Beta Mod.", "Beta.esp", "Alpha.esp");
        Pair("'X should go ABOVE Y' means earlier in the order, so X first", "Alpha Mod should go above Beta Mod.", "Alpha.esp", "Beta.esp");
        Pair("a self-reference is one of the two names", "Load this patch after Alpha Mod.", "Alpha.esp", "TestPatch.esp");

        var rShape = OrderAdvice.Extract("Alpha Mod was updated before Beta Mod was released.", w3);
        Check("advice/S2: a sentence with two mods and a direction word that is NOT the S2 shape is a note, never a rule",
              rShape.Rules.Count == 0 && rShape.Notes.Count == 1 && rShape.Notes[0].Contains("not in the shape S2 describes", StringComparison.Ordinal),
              $"{rShape.Rules.Count} rule(s); {string.Join(" | ", rShape.Notes)}");

        // -------------------------------------------------------------------------------------------------
        //  S3 — the trap. The sentence says "load after", and it must NOT become a rule, and it must NOT be
        //  reported as "no order stated" either.
        // -------------------------------------------------------------------------------------------------
        var tGeneric = "Patches should have their appropriate masters set, and should be loaded after any mods which they are patching.";
        var rGeneric = OrderAdvice.Extract(tGeneric, w3);
        Check("advice/S3: 'patches ... loaded after any mods which they are patching' produces NO rule",
              rGeneric.Rules.Count == 0, Rules(rGeneric));
        Check("advice/S3: it IS found — one statement of the generic shape, and a note saying the engine's master rule already enforces it",
              rGeneric.Statements.Count == 1 && rGeneric.Statements[0].Shape == AdviceShape.GenericPatchAfterMasters
              && rGeneric.Notes.Count == 1 && rGeneric.Notes[0].Contains(OrderAdvice.GenericNote, StringComparison.Ordinal),
              $"{rGeneric.Statements.Count} statement(s), {rGeneric.Notes.Count} note(s)");
        Check("advice/S3: the generic statement is NOT 'no load order stated' — the distinction the negative control rests on",
              rGeneric.NothingStated is null, rGeneric.NothingStated ?? "(null, as expected)");
        var rGeneric2 = OrderAdvice.Extract("This patch should be loaded after its masters.", w3);
        Check("advice/S3: the same shape written about 'this patch' and 'its masters' is still generic, not a pair sentence",
              rGeneric2.Rules.Count == 0 && rGeneric2.Statements.Count == 1 && rGeneric2.Statements[0].Shape == AdviceShape.GenericPatchAfterMasters,
              $"{rGeneric2.Rules.Count} rule(s), {rGeneric2.Statements.Count} statement(s)");

        // -------------------------------------------------------------------------------------------------
        //  Refusals.
        // -------------------------------------------------------------------------------------------------
        var rCond = OrderAdvice.Extract("If you use Beta Mod, load Alpha Mod after Beta Mod.", w3);
        Check("advice/refusal: a conditional statement is a note carrying the sentence, never a rule",
              rCond.Rules.Count == 0 && rCond.Notes.Count == 1 && rCond.Notes[0].Contains("conditional 'if you use'", StringComparison.Ordinal),
              $"{rCond.Rules.Count} rule(s); {string.Join(" | ", rCond.Notes)}");

        // The conditional after the pair, which is the shape that actually needs the conditional check: with the
        // names and the direction in S2 order, nothing else stops this sentence becoming an unconditional rule.
        var rCondTrailing = OrderAdvice.Extract("Load Alpha Mod after Beta Mod if you use the ELFX version.", w3);
        Check("advice/refusal: a conditional AFTER the pair is still a conditional — the S2 shape would otherwise parse it straight into a rule",
              rCondTrailing.Rules.Count == 0 && rCondTrailing.Notes.Count == 1
              && rCondTrailing.Notes[0].Contains("conditional 'if you use'", StringComparison.Ordinal),
              $"{rCondTrailing.Rules.Count} rule(s); {string.Join(" | ", rCondTrailing.Notes)}");

        var rThree = OrderAdvice.Extract("Load Alpha Mod after Beta Mod and Gamma Mod.", w3);
        Check("advice/refusal: three known names in one sentence that is not a list is a note, never a rule",
              rThree.Rules.Count == 0 && rThree.Notes.Count == 1 && rThree.Notes[0].Contains("names 3 known mods in one sentence", StringComparison.Ordinal),
              $"{rThree.Rules.Count} rule(s); {string.Join(" | ", rThree.Notes)}");

        var rOver = OrderAdvice.Extract("Alpha Mod should overwrite Beta Mod.", w3);
        Check("advice/refusal: 'overwrite' is ASSET priority — noted as such, never an order rule",
              rOver.Rules.Count == 0 && rOver.Notes.Count == 1 && rOver.Notes[0].Contains("ASSET overwrite priority", StringComparison.Ordinal),
              $"{rOver.Rules.Count} rule(s); {string.Join(" | ", rOver.Notes)}");

        var rDis = OrderAdvice.Extract("Load Alpha Mod after Beta Mod.\nLoad Beta Mod after Alpha Mod.", w3);
        Check("advice/refusal: two statements that disagree about a pair produce NO rule for that pair, and a note for each",
              rDis.Rules.Count == 0 && rDis.Notes.Count == 2 && rDis.Notes.All(n => n.Contains("disagree about the order", StringComparison.Ordinal)),
              $"{rDis.Rules.Count} rule(s); {string.Join(" | ", rDis.Notes)}");
        Check("advice/refusal: both disagreeing statements are still REPORTED, so a reader can settle it",
              rDis.Statements.Count == 2, $"{rDis.Statements.Count} statement(s)");

        var rNone = OrderAdvice.Extract("This mod adds a new house to Whiterun. Enjoy!", w3);
        Check("advice/nothing: a page that states no order says so explicitly, naming the page and the read date",
              rNone.Rules.Count == 0 && rNone.Notes.Count == 0 && rNone.Statements.Count == 0
              && rNone.NothingStated == "no load order stated on https://www.nexusmods.com/skyrimspecialedition/mods/900 (read 2026-09-11)",
              rNone.NothingStated ?? "(null)");
        Check("advice/nothing: the 'nothing stated' file is still a real file — the explicit line reaches the rules fragment",
              OrderAdvice.ToRulesJson(rNone).Contains("no load order stated on", StringComparison.Ordinal)
              && OrderRules.Parse(OrderAdvice.ToRulesJson(rNone), "fixture empty").Rules.Count == 0,
              "fragment carries the line and parses to 0 rules");

        // -------------------------------------------------------------------------------------------------
        //  D2's hard edges: no substring matching, in the direction that actually bites.
        // -------------------------------------------------------------------------------------------------
        var rShort = OrderAdvice.Extract("Load Order:\nAlpha\nBeta Mod\n", w3);
        Check("advice/D2: a SHORTER item ('Alpha') does not match the known 'Alpha Mod' — no substring matching, so the list is refused",
              rShort.Rules.Count == 0 && rShort.Notes.Count == 1
              && rShort.Notes[0].Contains("names 'Alpha', which matches no known mod", StringComparison.Ordinal),
              $"{rShort.Rules.Count} rule(s); {string.Join(" | ", rShort.Notes)}");
        var rLongItem = OrderAdvice.Extract("Load Order:\nAlpha Mod Redux\nBeta Mod\n", w3);
        Check("advice/D2: a LONGER item ('Alpha Mod Redux') does not match the known 'Alpha Mod' either — an item line resolves by whole-line equality",
              rLongItem.Rules.Count == 0 && rLongItem.Notes.Count == 1
              && rLongItem.Notes[0].Contains("names 'Alpha Mod Redux', which matches no known mod", StringComparison.Ordinal),
              $"{rLongItem.Rules.Count} rule(s); {string.Join(" | ", rLongItem.Notes)}");
        var rLongProse = OrderAdvice.Extract("Load Alpha Mod Redux after Beta Mod.", w3);
        Check("advice/D2: in PROSE 'Alpha Mod Redux' does contain the known 'Alpha Mod', and the S2 shape test is what stops it becoming a rule about the wrong mod",
              rLongProse.Rules.Count == 0 && rLongProse.Notes.Count == 1
              && rLongProse.Notes[0].Contains("not in the shape S2 describes", StringComparison.Ordinal),
              $"{rLongProse.Rules.Count} rule(s); {string.Join(" | ", rLongProse.Notes)}");

        // -------------------------------------------------------------------------------------------------
        //  An ordering sentence this tool cannot follow is a NOTE, not silence. "no order stated" would be a
        //  wrong answer here, and a wrong answer is worse than a refusal.
        // -------------------------------------------------------------------------------------------------
        var rUnknownBoth = OrderAdvice.Extract("Load ICOW after MLM, tho (or clean MLM).", w3);
        Check("advice/refusal: an ordering sentence naming nothing resolvable (acronyms) is a note quoting it, never silence",
              rUnknownBoth.Rules.Count == 0 && rUnknownBoth.Notes.Count == 1
              && rUnknownBoth.Notes[0].Contains("an ordering sentence names something this tool cannot resolve", StringComparison.Ordinal),
              $"{rUnknownBoth.Rules.Count} rule(s); {string.Join(" | ", rUnknownBoth.Notes)}");
        var rUnknownOne = OrderAdvice.Extract("Load this patch after Foo Bar.", w3);
        Check("advice/refusal: one known mod and one unresolvable name in an ordering sentence is the same note",
              rUnknownOne.Rules.Count == 0 && rUnknownOne.Notes.Count == 1
              && rUnknownOne.Notes[0].Contains("an ordering sentence names something this tool cannot resolve", StringComparison.Ordinal),
              $"{rUnknownOne.Rules.Count} rule(s); {string.Join(" | ", rUnknownOne.Notes)}");
        var rNoDirection = OrderAdvice.Extract("This patch is flagged as an ESL, so it won't take up a plugin slot.", w3);
        Check("advice/refusal: a sentence with no direction word is NOT an ordering sentence — no note, no noise",
              rNoDirection.Rules.Count == 0 && rNoDirection.Notes.Count == 0,
              $"{rNoDirection.Notes.Count} note(s)");

        // -------------------------------------------------------------------------------------------------
        //  The three-names refusal is gated on ordering language, or it buries the real refusals under prose.
        // -------------------------------------------------------------------------------------------------
        var rThreeDescriptive = OrderAdvice.Extract(
            "This patch makes some minor changes so that Alpha Mod is compatible with the Beta Mod overhaul.", w3);
        Check("advice/refusal: three known names with NO ordering language is prose — nothing is reported",
              rThreeDescriptive.Rules.Count == 0 && rThreeDescriptive.Notes.Count == 0 && rThreeDescriptive.Statements.Count == 0,
              $"{rThreeDescriptive.Notes.Count} note(s)");
        var rThreeInline = OrderAdvice.Extract("Load order: Alpha Mod, Beta Mod, Gamma Mod.", w3);
        Check("advice/refusal: three known names WITH a header phrase written inline is the three-name note — not a list, not a rule",
              rThreeInline.Rules.Count == 0 && rThreeInline.Notes.Count == 1
              && rThreeInline.Notes[0].Contains("names 3 known mods in one sentence", StringComparison.Ordinal),
              $"{rThreeInline.Rules.Count} rule(s); {string.Join(" | ", rThreeInline.Notes)}");

        // -------------------------------------------------------------------------------------------------
        //  Supersession: a note, and the page's stated order is still reported.
        // -------------------------------------------------------------------------------------------------
        var tSuper = "Please download Janquel's patch for JK's Blue Palace and Enhanced Solitude instead of this one!\n" + t2;
        var rSuper = OrderAdvice.Extract(tSuper, w2);
        Check("advice/supersession: 'download X instead of this one' is a note naming the sentence",
              rSuper.Notes.Count == 1 && rSuper.Notes[0].Contains("the author recommends something else instead of this mod", StringComparison.Ordinal),
              string.Join(" | ", rSuper.Notes));
        Check("advice/supersession: the note changes NOTHING about the rules — the page's stated order is still the page's stated order",
              rSuper.Rules.Count == 2 && rSuper.Rules[0].Before == "Enhanced Solitude.esp"
              && rSuper.Rules[1].After == "JKBluePalaceEnhancedSolitude.esp", Rules(rSuper));
        var rInsteadNoSelf = OrderAdvice.Extract("Use Alpha Mod instead of Beta Mod.", w3);
        Check("advice/supersession: 'use X instead of Y' with no self-reference is not a supersession notice — nothing is reported",
              rInsteadNoSelf.Notes.Count == 0 && rInsteadNoSelf.Rules.Count == 0, $"{rInsteadNoSelf.Notes.Count} note(s)");

        var rMissing = OrderAdvice.Extract("Load Alpha Mod after Delta Mod.", w3 with
        {
            Known = w3.Known.Append(new AdviceKnownName(400, "Delta Mod", false)).ToList(),
        });
        Check("advice/D2: a named mod that is not installed is a note naming it, never a rule and never a guess",
              rMissing.Rules.Count == 0 && rMissing.Notes.Any(n => n.Contains("'Delta Mod' (mod 400) is not installed", StringComparison.Ordinal)),
              $"{rMissing.Rules.Count} rule(s); {string.Join(" | ", rMissing.Notes)}");

        var w3NoPlugin = w3 with
        {
            Installed = Dict(new[]
            {
                Mod(900, "Test Patch", "Test Patch", new[] { "TestPatch.esp" }, "2.0", "2026-01-01"),
                Mod(100, "Alpha Mod", "Alpha", Array.Empty<string>(), "1.0", "2025-01-01"),
                Mod(200, "Beta Mod", "Beta", new[] { "Beta.esp" }, "1.0", "2025-01-01"),
            }),
        };
        var rNoPlugin = OrderAdvice.Extract("Load Alpha Mod after Beta Mod.", w3NoPlugin);
        Check("advice/D2: a mod installed but contributing no plugin to the LOADED order is a note, never a rule",
              rNoPlugin.Rules.Count == 0 && rNoPlugin.Notes.Any(n => n.Contains("is installed but contributes no plugin to the order", StringComparison.Ordinal)),
              $"{rNoPlugin.Rules.Count} rule(s); {string.Join(" | ", rNoPlugin.Notes)}");

        var w3TwoFolders = w3 with
        {
            Installed = Dict(new[]
            {
                Mod(900, "Test Patch", "Test Patch", new[] { "TestPatch.esp" }, "2.0", "2026-01-01"),
                Mod(100, "Alpha Mod", new[] { "Alpha", "Alpha (reinstall)" }, new[] { "Alpha.esp" }, "1.0", "2025-01-01"),
                Mod(200, "Beta Mod", "Beta", new[] { "Beta.esp" }, "1.0", "2025-01-01"),
            }),
        };
        var rTwoFolders = OrderAdvice.Extract("Load Alpha Mod after Beta Mod.", w3TwoFolders);
        Check("advice/D2: two ENABLED mod folders carrying one Nexus id is an ambiguity, not a folder to pick",
              rTwoFolders.Rules.Count == 0 && rTwoFolders.Notes.Any(n => n.Contains("2 enabled mod folders carry that Nexus id", StringComparison.Ordinal)),
              $"{rTwoFolders.Rules.Count} rule(s); {string.Join(" | ", rTwoFolders.Notes)}");

        // -------------------------------------------------------------------------------------------------
        //  W4 — the College shape, which is where "one mod, one plugin" stops being true. Obscure's College of
        //  Winterhold ships its main plugin plus compatibility patches in the same folder; three of them are in
        //  this profile's loaded order. Both directions of the master-list tiebreak are proved, because a
        //  resolver that picks the first file would pass the second case and fail nobody.
        // -------------------------------------------------------------------------------------------------
        var collegeMods = new[]
        {
            Mod(67820, "JK's College of Winterhold combo patches", "JK CoW combo", new[] { "JKs College Combo Patch.esp" }, "1.0", "2022-04-04"),
            Mod(500, "Obscure's College of Winterhold", "OCW",
                new[] { "OCW_Obscures_CollegeofWinterhold.esp", "OCW_HM_Patch.esp", "OCW_ISC_Patch.esp" }, "6.0", "2022-01-01"),
            Mod(600, "JK's College of Winterhold", "JK CoW", new[] { "JKs College of Winterhold.esp" }, "1.0", "2022-02-02"),
        };
        var sentence = "Load Obscure's College of Winterhold before JK's College of Winterhold.";

        var w4NoTie = World(67820, "JK's College of Winterhold combo patches",
            "https://www.nexusmods.com/skyrimspecialedition/mods/67820", read: "2026-09-11", updated: "2022-04-04",
            mainVer: "1.0", mainDate: "2022-04-04", masters: Array.Empty<string>(), mods: collegeMods);
        var rNoTie = OrderAdvice.Extract(sentence, w4NoTie);
        Check("advice/D2: a mod with THREE plugins in the order and no master list to choose is AMBIGUOUS — never the first file",
              rNoTie.Rules.Count == 0
              && rNoTie.Notes.Any(n => n.Contains("it puts 3 plugins in the order", StringComparison.Ordinal)
                                    && n.Contains("master list is not known", StringComparison.Ordinal)),
              $"{rNoTie.Rules.Count} rule(s); {string.Join(" | ", rNoTie.Notes)}");

        var w4Tie = w4NoTie with { PagePluginMasters = new[] { "Skyrim.esm", "OCW_Obscures_CollegeofWinterhold.esp", "JKs College of Winterhold.esp" } };
        var rTie = OrderAdvice.Extract(sentence, w4Tie);
        Check("advice/D2: the same three plugins, but the page's own plugin masters exactly one of them — the master list chooses it",
              rTie.Rules.Count == 1 && rTie.Rules[0].Before == "OCW_Obscures_CollegeofWinterhold.esp"
              && rTie.Rules[0].After == "JKs College of Winterhold.esp"
              && rTie.Resolutions.Any(x => x.ModId == 500 && x.How.Contains("the master list chose", StringComparison.Ordinal)),
              Rules(rTie));

        var w4TwoTie = w4NoTie with { PagePluginMasters = new[] { "OCW_Obscures_CollegeofWinterhold.esp", "OCW_HM_Patch.esp" } };
        var rTwoTie = OrderAdvice.Extract(sentence, w4TwoTie);
        Check("advice/D2: a master list naming TWO of the three does not break the tie either — still a note",
              rTwoTie.Rules.Count == 0 && rTwoTie.Notes.Any(n => n.Contains("names 2 of them, not exactly one", StringComparison.Ordinal)),
              $"{rTwoTie.Rules.Count} rule(s); {string.Join(" | ", rTwoTie.Notes)}");

        // -------------------------------------------------------------------------------------------------
        //  Markup: the cleaner is the first thing between a page and every rule above, and it has no cap.
        // -------------------------------------------------------------------------------------------------
        var longPage = new string('x', 40_000) + "\nLoad Order:\n[*]Alpha Mod[*]Beta Mod\n";
        var rLong2 = OrderAdvice.Extract(longPage, w3);
        Check("advice/markup: a statement 40,000 characters into the page is still read — the cleaner has NO length cap",
              rLong2.Rules.Count == 1 && rLong2.Rules[0].Before == "Alpha.esp" && rLong2.Rules[0].After == "Beta.esp",
              Rules(rLong2));
        var cleaned = OrderAdvice.Clean("[size=5][b]Load Order:[/b][/size]<br />[url=https://x.invalid]Alpha Mod[/url]<br />Beta &amp; Co");
        Check("advice/markup: BBCode and HTML are stripped, [url=...]label[/url] keeps its label, entities decode",
              cleaned == "Load Order:\nAlpha Mod\nBeta & Co", cleaned.Replace("\n", "\\n"));

        // -------------------------------------------------------------------------------------------------
        //  S4 — a clash the author states, in a compatibility section, against a named mod. Hand-written text:
        //  a heading, a lead-in that says the items are incompatibilities, and dash items glued to the name
        //  ("-Name: what goes wrong"), one naming an unknown mod and one naming the pair's other mod (9002).
        // -------------------------------------------------------------------------------------------------
        var wClash = World(9001, "Alpha Halls Expanded", "https://www.nexusmods.com/skyrimspecialedition/mods/9001",
            read: "2026-11-08", updated: "2022-04-05", mainVer: "1.0", mainDate: "2022-01-06", masters: Array.Empty<string>(),
            mods: new[]
            {
                Mod(9001, "Alpha Halls Expanded", "Alpha Halls", new[] { "AlphaHalls.esp" }, "1.0", "2022-01-06"),
                Mod(9002, "Beta - A Shrine Overhaul", "Beta Shrine", new[] { "Beta.esp" }, "1.0", "2023-01-01"),
            });
        const string clashSymptom = "Trapdoor entrance to the catacombs is blocked";
        var tClash = "[b]Compatibility:[/b]\n\n"
                   + "This mod should be compatible with almost everything. The following mods, however, do have some minor incompatibilities:\n\n"
                   + "-Gamma College: Two placed notes will not spawn.\n\n"
                   + "-Beta - A Shrine Overhaul: " + clashSymptom + "\n\n"
                   + "____________________\n\nInstallation:\n\nExtract the archive into your data folder.";
        var rClash = OrderAdvice.Extract(tClash, wClash);
        Check("advice/S4: a '-Name: symptom' item under a compatibility heading whose lead-in states incompatibilities is ONE statement naming that mod, with the symptom — never 'no order stated'",
              rClash.Statements.Count == 1 && rClash.Statements[0].Items.SequenceEqual(new[] { "Beta - A Shrine Overhaul" })
              && rClash.Statements[0].Symptom == clashSymptom && rClash.NothingStated is null,
              $"{rClash.Statements.Count} statement(s): {string.Join(" | ", rClash.Statements.Select(s => $"{s.Text} [{string.Join(", ", s.Items)}] symptom={s.Symptom}"))}; nothing={rClash.NothingStated}");
        Check("advice/S4: a stated clash is NEVER an order rule — the fragment parses with 0 rules",
              rClash.Rules.Count == 0 && OrderRules.Parse(OrderAdvice.ToRulesJson(rClash), "fixture 9001").Rules.Count == 0,
              Rules(rClash));
        Check("advice/S4: an item naming a mod that is not a known name (Gamma College) gives no statement",
              !rClash.Statements.Any(s => s.Text.Contains("Gamma", StringComparison.Ordinal)),
              string.Join(" | ", rClash.Statements.Select(s => s.Text)));

        var tCompatible = "Compatibility:\n\nThe following mods were tested with this one:\n\n"
                        + "-Beta - A Shrine Overhaul: Fully compatible, no patch needed.\n";
        var rCompatible = OrderAdvice.Extract(tCompatible, wClash);
        Check("advice/S4: '-Name: Fully compatible' under a lead-in with no clash word is NOT a clash",
              rCompatible.Statements.Count == 0,
              string.Join(" | ", rCompatible.Statements.Select(s => s.Text)));

        var tSelf = "Known issues:\n\n-Alpha Halls Expanded: the surface teleporter is blocked\n";
        var rSelf = OrderAdvice.Extract(tSelf, wClash);
        Check("advice/S4: an item naming the page's OWN mod is not a clash with another mod",
              rSelf.Statements.Count == 0,
              string.Join(" | ", rSelf.Statements.Select(s => s.Text)));
    }

    // -----------------------------------------------------------------------------------------------------

    static string Rules(AdviceReport r) =>
        r.Rules.Count == 0 ? "(no rules)" : string.Join(" | ", r.Rules.Select(x => $"'{x.Before}' before '{x.After}'"));

    static AdviceWorldMod Mod(int id, string name, string folder, IReadOnlyList<string> plugins, string? version, string? date) =>
        new(id, name, new[] { folder }, plugins, version, date, date is null ? null : "fixture");

    static AdviceWorldMod Mod(int id, string name, IReadOnlyList<string> folders, IReadOnlyList<string> plugins, string? version, string? date) =>
        new(id, name, folders, plugins, version, date, date is null ? null : "fixture");

    static Dictionary<int, AdviceWorldMod> Dict(IEnumerable<AdviceWorldMod> mods) => mods.ToDictionary(m => m.ModId);

    static AdviceWorld World(int pageId, string pageName, string url, string read, string updated, string mainVer,
                             string mainDate, IReadOnlyList<string> masters, AdviceWorldMod[] mods)
    {
        var known = new List<AdviceKnownName>();
        foreach (var m in mods) known.Add(new AdviceKnownName(m.ModId, m.Name, m.ModId == pageId));
        return new AdviceWorld(pageId, pageName, url, read, known, Dict(mods), masters, updated, mainVer, mainDate);
    }
}
