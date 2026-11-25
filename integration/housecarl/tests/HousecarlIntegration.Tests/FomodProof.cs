using System.Xml.Linq;
using HarnessShared;

// proofprobe's FOMOD decision-model section, moved verbatim with fomodprobe and FomodModel.cs to the houseCARL
// integration module in step 3 of the SkyWright / houseCARL boundary (work/move_fomodproof_9d2.py); its cases run
// in FomodProofTests under the names proofprobe printed.
static class FomodProof
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine("PROOF — the FOMOD decision model (_shared/FomodModel.cs)");
        Console.WriteLine("   Five corrections, one case each. No game data, no archives, no MO2 instance.\n");

        // ---------------------------------------------------------------------------------------
        // Rule 1 — destination="" is the Data ROOT; an ABSENT destination defaults to the source.
        // ---------------------------------------------------------------------------------------
        // This is what broke path matching for 74 of 83 real FOMODs.
        {
            var xml = Fomod("<requiredInstallFiles><folder source=\"00 Core\" destination=\"\"/></requiredInstallFiles>");
            var p = Run(xml, new[] { "SomeMod.esp" }, Array.Empty<string>());
            Check("rule1: destination=\"\" resolves to the Data root", p.RequiredFound == 1,
                  $"required {p.RequiredFiles}, found {p.RequiredFound} (expect 1)");

            var xml2 = Fomod("<requiredInstallFiles><folder source=\"00 Core\"/></requiredInstallFiles>");
            var p2 = Run(xml2, new[] { "SomeMod.esp" }, Array.Empty<string>());
            Check("rule1: ABSENT destination falls back to the source path", p2.RequiredFound == 0,
                  $"found {p2.RequiredFound} (expect 0 — '00 Core' is not on disk)");

            var p3 = Run(xml2, new[] { "00 Core\\SomeMod.esp" }, Array.Empty<string>());
            Check("rule1: source-path fallback matches when it IS on disk", p3.RequiredFound == 1,
                  $"found {p3.RequiredFound} (expect 1)");
        }

        // ---------------------------------------------------------------------------------------
        // Rule 2 — a NotUsable pattern's fileDependency means the OPPOSITE of a recommendation.
        // ---------------------------------------------------------------------------------------
        {
            var rec = Fomod(Steps(Step("s", Group("g", "SelectAny",
                Plugin("opt", "<file source=\"a.esp\" destination=\"a.esp\"/>", "Trigger.esp", "Recommended")))));
            var pr = Run(rec, Array.Empty<string>(), new[] { "Trigger.esp" });
            Check("rule2: Recommended pattern IS read as a condition", pr.Options[0].Conditions.Count == 1,
                  $"conditions {pr.Options[0].Conditions.Count} (expect 1)");

            var notUsable = Fomod(Steps(Step("s", Group("g", "SelectAny",
                Plugin("opt", "<file source=\"a.esp\" destination=\"a.esp\"/>", "Trigger.esp", "NotUsable")))));
            var pn = Run(notUsable, Array.Empty<string>(), new[] { "Trigger.esp" });
            Check("rule2: NotUsable pattern is NOT read as a condition", pn.Options[0].Conditions.Count == 0,
                  $"conditions {pn.Options[0].Conditions.Count} (expect 0 — it says do NOT take this)");
        }

        // ---------------------------------------------------------------------------------------
        // Rule 3 — in an exclusive group, a taken sibling rules the others out.
        // ---------------------------------------------------------------------------------------
        {
            var xml = Fomod(Steps(Step("s", Group("g", "SelectExactlyOne",
                Plugin("taken", "<file source=\"a.esp\" destination=\"a.esp\"/>", "Trigger.esp", "Recommended") +
                Plugin("other", "<file source=\"b.esp\" destination=\"b.esp\"/>", "Trigger.esp", "Recommended")))));
            var p = Run(xml, new[] { "a.esp" }, new[] { "Trigger.esp" });
            var other = p.Options.Single(o => o.Name == "other");
            Check("rule3: exclusive group — untaken sibling is RULED OUT", other.RuledOut && !other.WrongVariant,
                  $"ruledOut={other.RuledOut} wrongVariant={other.WrongVariant}");

            var xmlAny = Fomod(Steps(Step("s", Group("g", "SelectAny",
                Plugin("taken", "<file source=\"a.esp\" destination=\"a.esp\"/>") +
                Plugin("other", "<file source=\"b.esp\" destination=\"b.esp\"/>")))));
            var pa = Run(xmlAny, new[] { "a.esp" }, Array.Empty<string>());
            Check("rule3: SelectAny group does NOT rule siblings out",
                  !pa.Options.Single(o => o.Name == "other").RuledOut, "ruledOut=false as expected");
        }

        // ---------------------------------------------------------------------------------------
        // Rule 4 — a step whose visibility flag does not hold was NEVER OFFERED.
        // ---------------------------------------------------------------------------------------
        // The case: an installer that offers a patch set only when a mod it depends on is chosen; with that
        // mod not chosen, reading its options as "not selected" produces findings for a mod that is not installed.
        {
            var xml = Fomod(Steps(
                Step("main", Group("g", "SelectExactlyOne",
                    Plugin("pick-A", "<file source=\"a.esp\" destination=\"a.esp\"/>", null, null, "BRANCH_A", "On") +
                    Plugin("pick-B", "<file source=\"b.esp\" destination=\"b.esp\"/>", null, null, "BRANCH_B", "On"))) +
                Step("branchA", Group("g2", "SelectAny",
                    Plugin("a-extra", "<file source=\"ax.esp\" destination=\"ax.esp\"/>", "Trigger.esp", "Recommended")), "BRANCH_A", "On") +
                Step("branchB", Group("g3", "SelectAny",
                    Plugin("b-extra", "<file source=\"bx.esp\" destination=\"bx.esp\"/>", "Trigger.esp", "Recommended")), "BRANCH_B", "On")));

            var p = Run(xml, new[] { "a.esp" }, new[] { "Trigger.esp" });      // branch A was taken
            Check("rule4: the unshown branch's options are NEVER OFFERED", p.OptionsNotOffered == 1,
                  $"notOffered={p.OptionsNotOffered} (expect 1 — b-extra)");
            Check("rule4: the shown branch's options ARE enumerated",
                  p.Options.Any(o => o.Name == "a-extra") && !p.Options.Any(o => o.Name == "b-extra"),
                  "a-extra present, b-extra absent");

            var p2 = Run(xml, new[] { "b.esp" }, new[] { "Trigger.esp" });      // branch B was taken
            Check("rule4: flags are replayed from the option ACTUALLY taken",
                  p2.Options.Any(o => o.Name == "b-extra") && !p2.Options.Any(o => o.Name == "a-extra"),
                  "the other branch shows when the other option is on disk");
        }

        // ---------------------------------------------------------------------------------------
        // Rule 5 — ...unless the taken sibling's OWN condition does not hold and an untaken one's does.
        // ---------------------------------------------------------------------------------------
        // The Midden Expanded ESM/ESP case, frozen. Rule 3 was suppressing this and nothing said so.
        {
            var xml = Fomod(Steps(Step("s", Group("Midden Expanded", "SelectAtMostOne",
                Plugin("ESM variant", "<file source=\"esm.esp\" destination=\"esm.esp\"/>", "middenexpandedSSE.esm", "Recommended") +
                Plugin("ESP variant", "<file source=\"esp.esp\" destination=\"esp.esp\"/>", "middenexpandedSSE.esp", "Recommended")))));

            // The ESM variant is on disk; the load order runs the ESP version of the mod.
            var wrong = Run(xml, new[] { "esm.esp" }, new[] { "middenexpandedSSE.esp" });
            var espOpt = wrong.Options.Single(o => o.Name == "ESP variant");
            Check("rule5: taken sibling's condition fails -> untaken is WRONG-VARIANT",
                  espOpt.WrongVariant && !espOpt.RuledOut,
                  $"wrongVariant={espOpt.WrongVariant} ruledOut={espOpt.RuledOut}");

            // The counter-case that keeps rule 5 from swallowing rule 3: the taken one DOES hold up.
            var right = Run(xml, new[] { "esm.esp" }, new[] { "middenexpandedSSE.esm" });
            var espOpt2 = right.Options.Single(o => o.Name == "ESP variant");
            Check("rule5: taken sibling's condition holds -> untaken is merely RULED OUT",
                  espOpt2.RuledOut && !espOpt2.WrongVariant,
                  $"ruledOut={espOpt2.RuledOut} wrongVariant={espOpt2.WrongVariant}");
        }

        // ---------------------------------------------------------------------------------------
        // Rule 6 — a files-less GATE option makes its flags UNDECIDABLE, and undecidable gates
        // fall back to visible.
        // ---------------------------------------------------------------------------------------
        // The real case: Czasior-style patch-collection hubs open with an "I'VE READ AND UNDERSTAND"
        // checkbox that ships no files and only sets flags; every later step is gated on them. Deciding
        // taken-ness from delivered files, that option can never be "taken", so whole hubs read as
        // NEVER OFFERED — 8 of the trust test's 9 refusals were exactly this shape.
        {
            var xml = Fomod(Steps(
                Step("gate", Group("g", "SelectAny",
                    "<plugin name=\"i-have-read\"><description>d</description><files/>" +
                    "<conditionFlags><flag name=\"GATE\">On</flag></conditionFlags></plugin>")) +
                Step("payload", Group("g2", "SelectAny",
                    Plugin("real-option", "<file source=\"a.esp\" destination=\"a.esp\"/>")), "GATE", "On")));

            var p = Run(xml, new[] { "a.esp" }, Array.Empty<string>());
            Check("rule6: gated step is VISIBLE when the gate flag is undecidable",
                  p.Options.Any(o => o.Name == "real-option"), "the hub's payload steps are judged");
            Check("rule6: ...and its on-disk option reads as taken (trust passes)",
                  p.Options.Single(o => o.Name == "real-option").Selected && p.PathsTrusted,
                  "8 unjudgeable patch collections come back");

            // The counter-case that keeps rule 6 from swallowing rule 4: a flag set by an option that
            // DOES ship files stays decidable, and its untaken branch stays NEVER OFFERED.
            var decidable = Fomod(Steps(
                Step("main", Group("g", "SelectExactlyOne",
                    Plugin("pick-A", "<file source=\"a.esp\" destination=\"a.esp\"/>", null, null, "BRANCH_A", "On") +
                    Plugin("pick-B", "<file source=\"b.esp\" destination=\"b.esp\"/>", null, null, "BRANCH_B", "On"))) +
                Step("branchB", Group("g3", "SelectAny",
                    Plugin("b-extra", "<file source=\"bx.esp\" destination=\"bx.esp\"/>")), "BRANCH_B", "On")));
            var p2 = Run(decidable, new[] { "a.esp" }, Array.Empty<string>());
            Check("rule6: a flag from a FILE-carrying option stays decidable (rule 4 intact)",
                  p2.OptionsNotOffered == 1 && !p2.Options.Any(o => o.Name == "b-extra"),
                  "the untaken branch is still NEVER OFFERED");
        }

        // ---------------------------------------------------------------------------------------
        // The trust test — the self-check that refuses to report when path matching is broken.
        // ---------------------------------------------------------------------------------------
        {
            var xml = Fomod("<requiredInstallFiles><file source=\"core.esp\" destination=\"core.esp\"/></requiredInstallFiles>" + Steps(Step("s", Group("g", "SelectAny", Plugin("opt", "<file source=\"a.esp\" destination=\"a.esp\"/>")))));
            Check("trust: required files found -> TRUSTED", Run(xml, new[] { "core.esp" }, Array.Empty<string>()).PathsTrusted, "trusted");
            Check("trust: required files MISSING -> NOT trusted", !Run(xml, new[] { "other.esp" }, Array.Empty<string>()).PathsTrusted,
                  "refuses to judge this mod");

            var noRequired = Fomod(Steps(Step("s", Group("g", "SelectAny",
                Plugin("opt", "<file source=\"a.esp\" destination=\"a.esp\"/>")))));
            Check("trust: no required files, an option matched -> TRUSTED",
                  Run(noRequired, new[] { "a.esp" }, Array.Empty<string>()).PathsTrusted, "fallback arm");
            Check("trust: no required files, nothing matched -> NOT trusted",
                  !Run(noRequired, new[] { "zz.esp" }, Array.Empty<string>()).PathsTrusted, "fallback arm refuses");

            // The threshold itself, pinned with n >= 2. Every earlier case used a single required file,
            // for which "all found", "half found" and "any found" are indistinguishable — three different
            // implementations passed the same suite, and the shipped code silently drifted to "half"
            // while every document said "must be found". These three cases make the semantics ALL, and
            // make any future drift fail by name.
            var three = Fomod("<requiredInstallFiles>"
                + "<file source=\"r1.esp\" destination=\"r1.esp\"/>"
                + "<file source=\"r2.esp\" destination=\"r2.esp\"/>"
                + "<file source=\"r3.esp\" destination=\"r3.esp\"/>"
                + "</requiredInstallFiles>");
            Check("trust threshold: 3 of 3 required found -> TRUSTED",
                  Run(three, new[] { "r1.esp", "r2.esp", "r3.esp" }, Array.Empty<string>()).PathsTrusted, "all present");
            Check("trust threshold: 2 of 3 required found -> NOT trusted",
                  !Run(three, new[] { "r1.esp", "r2.esp" }, Array.Empty<string>()).PathsTrusted,
                  "one unconditional file missing = matching is broken (kills a HALF threshold)");
            Check("trust threshold: 1 of 3 required found -> NOT trusted",
                  !Run(three, new[] { "r1.esp" }, Array.Empty<string>()).PathsTrusted,
                  "kills an ANY threshold");
        }

        // ---------------------------------------------------------------------------------------
        // Judgeability — a root-only option cannot be told apart from one that was taken.
        // ---------------------------------------------------------------------------------------
        {
            var xml = Fomod(Steps(Step("s", Group("g", "SelectAny",
                Plugin("root-only", "<folder source=\"00 Core\" destination=\"\"/>") +
                Plugin("named", "<file source=\"a.esp\" destination=\"a.esp\"/>")))));
            var p = Run(xml, new[] { "something.esp" }, Array.Empty<string>());
            Check("judgeable: root-only option is marked UNJUDGEABLE",
                  !p.Options.Single(o => o.Name == "root-only").Judgeable, "counted, never read as installed");
            Check("judgeable: a named destination IS judgeable",
                  p.Options.Single(o => o.Name == "named").Judgeable, "judgeable");
        }
    }

    // ---------------------------------------------------------------------------------------
    // Helpers — a FOMOD in a string, and the two predicates the model needs.
    // ---------------------------------------------------------------------------------------
    static FomodParse Run(string xml, IEnumerable<string> onDisk, IEnumerable<string> activePlugins)
    {
        var installed = new HashSet<string>(onDisk.Select(FomodModel.Norm), StringComparer.OrdinalIgnoreCase);
        var active = new HashSet<string>(activePlugins, StringComparer.OrdinalIgnoreCase);
        bool Present(string dest)
        {
            var d = FomodModel.Norm(dest);
            if (d.Length == 0) return installed.Count > 0;          // "" is the Data root
            if (installed.Contains(d)) return true;
            foreach (var f in installed) if (f.StartsWith(d + "\\", StringComparison.Ordinal)) return true;
            return false;
        }
        return FomodModel.Parse(XDocument.Parse(xml), Present, active.Contains);
    }

    static string Fomod(string body) =>
        $"<config><moduleName>t</moduleName>{body}</config>";

    static string Steps(string body) => $"<installSteps order=\"Explicit\">{body}</installSteps>";

    static string Step(string name, string groups, string? visibleFlag = null, string? visibleValue = null) =>
        $"<installStep name=\"{name}\">" +
        (visibleFlag is null ? "" : $"<visible><dependencies><flagDependency flag=\"{visibleFlag}\" value=\"{visibleValue}\"/></dependencies></visible>") +
        $"<optionalFileGroups>{groups}</optionalFileGroups></installStep>";

    static string Group(string name, string type, string plugins) =>
        $"<group name=\"{name}\" type=\"{type}\"><plugins>{plugins}</plugins></group>";

    static string Plugin(string name, string files, string? condFile = null, string? condType = null, string? setFlag = null, string? flagValue = null) =>
        $"<plugin name=\"{name}\"><description>d</description><files>{files}</files>" +
        (setFlag is null ? "" : $"<conditionFlags><flag name=\"{setFlag}\">{flagValue}</flag></conditionFlags>") +
        (condFile is null ? "" :
            "<typeDescriptor><dependencyType><defaultType name=\"Optional\"/><patterns><pattern>" +
            $"<dependencies operator=\"And\"><fileDependency file=\"{condFile}\" state=\"Active\"/></dependencies>" +
            $"<type name=\"{condType}\"/></pattern></patterns></dependencyType></typeDescriptor>") +
        "</plugin>";
}
