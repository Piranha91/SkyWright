using System.Numerics;
using System.Xml.Linq;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

// SYNTHETIC PROOF for the load-order-integrity probes.
//
// NO GAME DATA, NO ARCHIVES, NO MO2 INSTANCE. Every case is hand-built and its right answer is known
// by construction, so this runs on a fresh checkout — the shape CONTRIBUTING.md asks proof to take,
// and the shape `crossprobe` (37 cases) already set for the geometry work.
//
// WHY IT EXISTS. The FOMOD decision model took FIVE corrections in one session. Four killed
// false-positive classes; the fifth put back a real finding that a correct-sounding rule had hidden,
// and it was found only because a second probe disagreed with the first. Nothing in the output said
// a rule had broken, and nothing would have said it if a later edit undid one. Each case below is
// one of those corrections, frozen: if someone re-collapses `destination=""` onto the absent case,
// or reads `NotUsable` patterns as recommendations again, a case fails by name rather than a number
// quietly moving in a 30,000-line report.
//
// The same goes for the synthetic load order: it is now the thing every other finding will be
// validated against, so its own precedence and refusal rules had better be nailed down first.
//
// usage: proofprobe

int pass = 0, fail = 0;
var failures = new List<string>();

void Check(string name, bool ok, string detail)
{
    if (ok) { pass++; Console.WriteLine($"   PASS  {name,-56} {detail}"); }
    else { fail++; failures.Add(name); Console.WriteLine($"   FAIL  {name,-56} {detail}"); }
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
// The real case: this load order took OCW, so every ICOW option was never shown, and reading them
// as "not selected" produced 13 findings for a mod that is not installed.
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

// ---------------------------------------------------------------------------------------
// The detector cores (_shared/DetectorModel.cs) — the decisions the probes run on, frozen.
// ---------------------------------------------------------------------------------------
// Two of these rules were WRONG in the wild before they had cases: the clobber check used direct
// masters only (3 real false positives), and "material" used counts only (11 real re-cuts
// invisible). Each case below is one of those failure modes, by construction.
Console.WriteLine("\nPROOF — the detector cores (_shared/DetectorModel.cs)\n");
{
    // Master closure: direct, transitive chain, and diamond.
    var direct = new IReadOnlySet<int>[]
    {
        new HashSet<int>(),          // 0
        new HashSet<int> { 0 },      // 1 <- 0
        new HashSet<int> { 1 },      // 2 <- 1 (so transitively 0)
        new HashSet<int> { 1, 2 },   // 3 <- 1,2 (diamond over 0)
    };
    var clo = DetectorModel.MasterClosure(direct);
    Check("core: closure keeps direct masters", clo[1].Contains(0), "1 -> 0");
    Check("core: closure adds the transitive chain", clo[2].Contains(0), "2 -> 1 -> 0");
    Check("core: diamond closes to the full ancestor set",
          clo[3].SetEquals(new[] { 0, 1, 2 }), $"[{string.Join(",", clo[3].OrderBy(x => x))}]");

    // FindClobber over a provider history [definer=0, A=1, B=2] with winner B.
    var provs = new[] { 0, 1, 2 };
    (int V, int T, int H) size0 = (10, 5, 111), sizeA = (12, 6, 222), sizeB = (12, 6, 333);
    (int, int, bool)? Clob(Func<int, int, bool> dep, Func<int, (int, int, int)> size)
        => DetectorModel.FindClobber(provs, dep, size);
    (int, int, int) SizeOf(int p) => p == 1 ? sizeA : p == 2 ? sizeB : size0;

    var hit = Clob((w, l) => false, SizeOf);
    Check("core: unmastered later re-cut -> CLOBBER, material",
          hit == (1, 2, true), $"{hit}");
    Check("core: winner declares the loser (direct) -> no clobber",
          Clob((w, l) => w == 2 && l == 1, SizeOf) is null, "declared dependency");
    // The transitive case is the same call — the CLOSURE is the dependency function, which is
    // exactly why the two rules are proven separately and composed.
    Check("core: two providers only -> no clobber (definer + one editor)",
          DetectorModel.FindClobber(new[] { 0, 2 }, (w, l) => false, SizeOf) is null, "nothing to revert");
    Check("core: byte-identical forward -> NOT material",
          Clob((w, l) => false, p => p == 0 ? (10, 5, 111) : (12, 6, 222)) == (1, 2, false),
          "same verts, tris AND hash");
    Check("core: same counts, moved geometry -> material (the hash class)",
          Clob((w, l) => false, p => p == 0 ? (10, 5, 111) : p == 1 ? (12, 6, 222) : (12, 6, 999)) == (1, 2, true),
          "counts tie, content hash differs — invisible to the count-only rule");
    Check("core: winner masters the LATEST editor but not an earlier one -> earlier is the loser",
          DetectorModel.FindClobber(new[] { 0, 1, 2, 3 }, (w, l) => w == 3 && l == 2, SizeOf) is { } e && e.Loser == 1,
          "the hunt continues past declared dependencies");

    // Populations.
    Check("core: Bethesda-on-Bethesda clobber -> FLOOR",
          DetectorModel.ClassifyClobber(true, true, false) == ClobberPopulation.BethesdaFloor, "class A");
    Check("core: USSEP-as-loser -> its own class, not floor",
          DetectorModel.ClassifyClobber(false, false, true) == ClobberPopulation.UssepDiscarded, "class B");
    Check("core: dangle with neither end mod-touched -> FLOOR",
          DetectorModel.ClassifyDangle(false, false) == DanglePopulation.BethesdaFloor, "class A");
    Check("core: dangle with a mod-broken target -> finding",
          DetectorModel.ClassifyDangle(false, true) == DanglePopulation.ModBrokeTarget, "class B");

    // CameApart — the CHECK 2 decision.
    Check("core: together then, drawn far apart now -> SPLIT",
          DetectorModel.CameApart(true, 300, true, 2000, 600), "300u -> 2000u");
    Check("core: moved TOGETHER -> no split",
          !DetectorModel.CameApart(true, 300, true, 350, 600), "the whole scene relocated");
    Check("core: different cells now -> SPLIT",
          DetectorModel.CameApart(true, 300, false, float.PositiveInfinity, 600), "cells diverged");
    Check("core: never near at authoring -> not a pair",
          !DetectorModel.CameApart(true, 900, true, 9000, 600), "was > near");
    Check("core: different cells at authoring -> not a pair",
          !DetectorModel.CameApart(false, float.PositiveInfinity, false, float.PositiveInfinity, 600), "never together");

    // ClassifyReach — the filter the 2026-08-31 field test sent back, and the numbers it has to
    // reproduce. The three actors it was derived from are named in each case, because the whole
    // rule exists to tell them apart: two priests whose placements predicted their runtime
    // positions to within ~15u, and a third who stood 1,632u from hers in both arms of the A/B.
    DetectorModel.PackageAnchor AtPlacement(uint reach, bool sched = false, int mins = 0)
        => new(true, true, false, null, reach, sched, mins);
    DetectorModel.PackageAnchor AtRef(uint reach, bool sched = false, int mins = 0)
        => new(true, false, false, null, reach, sched, mins);
    DetectorModel.PackageAnchor Away(float d, bool sched = false, int mins = 0)
        => new(true, false, false, d, 0, sched, mins);
    DetectorModel.PackageAnchor Unplaceable(bool sched = false, int mins = 0)
        => new(true, false, true, null, 0, sched, mins);
    var inert = new DetectorModel.PackageAnchor(false, false, false, null, 0, false, 0);

    var rorlund = DetectorModel.ClassifyReach(new DetectorModel.PackageAnchor?[]
        { AtPlacement(512), Unplaceable(true, 180), AtRef(916, true, 1080) }, false);
    Check("reach: every slot on the placement -> BOUND (Rorlund)",
          rorlund.Verdict == ReachVerdict.PlacementBound && rorlund.Radius == 512, "reach 512u");
    Check("reach: the HIGHEST-PRIORITY positional package governs",
          DetectorModel.ClassifyReach(new DetectorModel.PackageAnchor?[] { AtPlacement(512), Away(1600) }, false)
              .Verdict == ReachVerdict.PlacementBound, "a lower-priority package that would move it does not decide");
    Check("reach: a package with no location slot is not the governing one",
          DetectorModel.ClassifyReach(new DetectorModel.PackageAnchor?[] { inert, AtPlacement(512) }, false)
              .Verdict == ReachVerdict.PlacementBound, "skipped, not treated as an anchor");
    var nearby = DetectorModel.ClassifyReach(new DetectorModel.PackageAnchor?[] { AtRef(916) }, false);
    Check("reach: anchor inside its own radius -> BOUND, reach widened",
          nearby.Verdict == ReachVerdict.PlacementBound && nearby.Radius == 916, "404u away, r=512 -> 916u");
    Check("reach: anchor OUTSIDE its own radius -> unassessable (Freir)",
          DetectorModel.ClassifyReach(new DetectorModel.PackageAnchor?[] { Away(1617) }, false)
              .Verdict == ReachVerdict.AnchorAwayFromPlacement, "stands her 1,617u from her placement");
    Check("reach: an anchor this order cannot place -> unassessable",
          DetectorModel.ClassifyReach(new DetectorModel.PackageAnchor?[] { Unplaceable() }, false)
              .Verdict == ReachVerdict.UnresolvedAnchor, "the player, an alias, another cell");
    Check("reach: nothing positions the actor -> unassessable",
          DetectorModel.ClassifyReach(new DetectorModel.PackageAnchor?[] { inert }, false)
              .Verdict == ReachVerdict.NoAnchor, "no location slot anywhere");
    Check("reach: a templated actor is unassessable",
          DetectorModel.ClassifyReach(new DetectorModel.PackageAnchor?[] { AtPlacement(512) }, true)
              .Verdict == ReachVerdict.Templated, "its packages are its template's");
    Check("reach: no packages -> unassessable",
          DetectorModel.ClassifyReach(Array.Empty<DetectorModel.PackageAnchor?>(), false)
              .Verdict == ReachVerdict.NoPackages, "nothing to read");
    Check("reach: a package record missing from the order -> unassessable",
          DetectorModel.ClassifyReach(new DetectorModel.PackageAnchor?[] { null }, false)
              .Verdict == ReachVerdict.PackageMissing, "never 'assume it does not move her'");

    // The second reading of "governing". It is not a refinement, it is a different answer, and the
    // probe reports how often the two disagree instead of picking one silently.
    var silanaByPriority = new DetectorModel.PackageAnchor?[] { AtPlacement(512), Away(1449, true, 1080) };
    Check("reach: most-of-the-day reading takes the longest SCHEDULED package (Silana)",
          DetectorModel.ClassifyReach(silanaByPriority, false).Verdict == ReachVerdict.PlacementBound
       && DetectorModel.ClassifyReach(silanaByPriority, false, byCoverage: true).Verdict == ReachVerdict.AnchorAwayFromPlacement,
          "bound by priority, 1,449u away by coverage");
    var covPick = DetectorModel.ClassifyReach(new DetectorModel.PackageAnchor?[]
        { AtPlacement(512), AtRef(900, true, 600) }, false, byCoverage: true);
    Check("reach: an unscheduled fallback does not outrank a scheduled package by coverage",
          covPick.Verdict == ReachVerdict.PlacementBound && covPick.Radius == 900, "no window of its own scores 0");
    var covTie = DetectorModel.ClassifyReach(new DetectorModel.PackageAnchor?[]
        { AtRef(100, true, 600), AtRef(900, true, 600) }, false, byCoverage: true);
    Check("reach: equal coverage keeps the earlier package",
          covTie.Radius == 100, "ties are settled by priority, as the engine does");

    // BubblesDisjoint — the pair decision, against the two arms that were measured in game.
    Check("reach: 137u apart with 512+512 of reach -> they can meet",
          !DetectorModel.BubblesDisjoint(137, 512, 512), "vanilla: the scene fires");
    Check("reach: 1,800u apart with 512+512 of reach -> they cannot",
          DetectorModel.BubblesDisjoint(1800, 512, 512), "patched: the scene does not fire");
    Check("reach: bubbles that exactly touch can still meet",
          !DetectorModel.BubblesDisjoint(1024, 512, 512), "boundary is not a split");
    Check("reach: two things that never move are split by any gap",
          DetectorModel.BubblesDisjoint(640, 0, 0), "a marker and a bed have no reach");

    // ClassifyIntent — WHY a finding happened (roadmap 3.5.3). The rule table from
    // RECOMMENDATIONS-2026-08-30, one case per row, built by construction: the live population is
    // 3 findings and one of them is the worked example the rule was derived from, so the live runs
    // are a sanity check on top of these, never the validation.
    DetectorModel.IntentFacts Facts(bool narr = false, int narrCount = 0, int placed = 16,
                                    int inCell = 4, int moved = 1, bool interior = true, bool extOnly = false)
        => new(narr, narrCount, placed, inCell, moved, interior, extOnly);

    Check("intent: touches the pair's own narrative -> DELIBERATE (informational)",
          DetectorModel.ClassifyIntent(Facts(narr: true, narrCount: 6)).Verdict == IntentVerdict.Deliberate,
          "the author rewrote the scene around the move");
    Check("intent: placements only -> OVERSIGHT (a finding)",
          DetectorModel.ClassifyIntent(Facts()).Verdict == IntentVerdict.Oversight,
          "nothing accounts for the scene the move breaks");
    var pilgrimShape = DetectorModel.ClassifyIntent(Facts(extOnly: true));
    Check("intent: interior actors moved, all other work exterior -> SCOPE MISMATCH (highest confidence)",
          pilgrimShape.Verdict == IntentVerdict.OversightScopeMismatch && !pilgrimShape.WildEditCandidate,
          "the worked example's shape: 4 edits in the temple, 12 in an exterior");
    Check("intent: an exterior pair cannot scope-mismatch",
          DetectorModel.ClassifyIntent(Facts(interior: false, extOnly: true)).Verdict == IntentVerdict.Oversight,
          "the mismatch row is interior-actors-moved specifically");
    // The NARROW reading of "touches the pair's quest", frozen as chosen: narrative work on OTHER
    // quests is not awareness of this one — the looser reading would classify any large mod as
    // "aware" on the strength of an unrelated quest edit.
    Check("intent: narrative work on OTHER quests is still OVERSIGHT (narrow reading)",
          DetectorModel.ClassifyIntent(Facts(narrCount: 40)).Verdict == IntentVerdict.Oversight,
          "an unrelated quest edit is not awareness of this scene");
    // The wild-edit axis (Piranha, 2026-09-01): a lone displaced ref with no accompanying
    // re-layout raises the QUESTION of an accidental edit — a candidate flag, never a verdict,
    // and never asserted from records alone.
    var loneMove = DetectorModel.ClassifyIntent(Facts(inCell: 1));
    Check("intent: lone displaced ref, nothing else in the cell -> wild-edit CANDIDATE, verdict unchanged",
          loneMove.Verdict == IntentVerdict.Oversight && loneMove.WildEditCandidate,
          "a flag that asks for corroboration, not a verdict");
    Check("intent: a re-layout (more cell edits than moved refs) is NOT a wild-edit candidate",
          !DetectorModel.ClassifyIntent(Facts()).WildEditCandidate,
          "4 edits in the cell, 1 moved ref — the room was reworked on purpose");
    Check("intent: DELIBERATE never carries the wild-edit flag",
          !DetectorModel.ClassifyIntent(Facts(narr: true, inCell: 1)).WildEditCandidate,
          "an author rewriting the scene did not drag the actor by accident");
    // The missing-description path: corroboration is a SEPARATE axis the verdict never reads —
    // ClassifyIntent takes no description at all, by signature — and unavailable is COUNTED
    // (rule 6), never treated as evidence.
    Check("intent: no meta.ini -> corroboration NoMetaIni (counted, not evidence)",
          DetectorModel.CorroborationOf(false, null) == IntentCorroboration.NoMetaIni,
          "a vanilla/base plugin has no MO2 mod folder");
    Check("intent: meta.ini without nexusDescription -> NoDescription (unjudgeable, ~1 in 4 mods)",
          DetectorModel.CorroborationOf(true, null) == IntentCorroboration.NoDescription,
          "missing is unjudgeable, never 'no intent'");
    Check("intent: description on file -> corroboration available",
          DetectorModel.CorroborationOf(true, "[b]placement fixes[/b]") == IntentCorroboration.DescriptionOnFile,
          "the records decide; the description corroborates");

    // BoxOverlap — milestone 5's broad-phase decision.
    var u = new System.Numerics.Vector3(0, 0, 0); var one = new System.Numerics.Vector3(1, 1, 1);
    Check("core: disjoint boxes -> no overlap",
          DetectorModel.BoxOverlap(u, one, new(2, 0, 0), new(3, 1, 1)) is null, "gap on X");
    Check("core: exact contact -> no overlap (touch is how architecture is authored)",
          DetectorModel.BoxOverlap(u, one, new(1, 0, 0), new(2, 1, 1)) is null, "shared face");
    Check("core: half-overlapped unit cubes -> depth 0.5, fraction 0.5",
          DetectorModel.BoxOverlap(u, one, new(0.5f, 0, 0), new(1.5f, 1, 1)) is { } o
              && MathF.Abs(o.MinDepth - 0.5f) < 1e-5f && MathF.Abs(o.VolumeFraction - 0.5f) < 1e-5f,
          "penetration measured");
    Check("core: small box inside big box -> fraction 1",
          DetectorModel.BoxOverlap(new(-5, -5, -5), new(5, 5, 5), new(-1, -1, -1), one) is { } c
              && MathF.Abs(c.VolumeFraction - 1f) < 1e-5f,
          "containment maxes the fraction");

    // Verify verdicts.
    Check("core: verdict all/some/none/alt/unreadable",
          DetectorModel.Verdict(true, 3, 3, 0) == "CONFIRMED"
       && DetectorModel.Verdict(true, 1, 3, 0) == "PARTIAL"
       && DetectorModel.Verdict(true, 0, 3, 0) == "UNRELATED"
       && DetectorModel.Verdict(true, 0, 3, 2) == "ALTERNATIVE"
       && DetectorModel.Verdict(false, 0, 3, 0) == "UNREADABLE",
          "all five arms");

    // CoverageLoss — 3.5.5's candidate selector. The rule exists because E5's 26-target null
    // proved nothing: every target sat on ground where the two arms were geometrically identical.
    // Candidates must be chosen where the winning geometry actually changes, by MORE than the
    // oracle's ~120u resolution — and each case here is a mesh pair whose right answer is known
    // by construction. Clearance 120 / spacing 64, the values the driver runs at.
    {
        List<(int A, int B, int C, int Flags)> Quad(List<System.Numerics.Vector3> verts,
            float x0, float y0, float x1, float y1, int flags = 0,
            List<(int A, int B, int C, int Flags)>? into = null)
        {
            int b = verts.Count;
            verts.Add(new(x0, y0, 0)); verts.Add(new(x1, y0, 0));
            verts.Add(new(x1, y1, 0)); verts.Add(new(x0, y1, 0));
            var tris = into ?? new List<(int, int, int, int)>();
            tris.Add((b, b + 1, b + 2, flags));
            tris.Add((b, b + 2, b + 3, flags));
            return tris;
        }
        const float CL = 120f, SP = 64f;

        // Identical meshes: no candidate in either direction.
        var idV = new List<System.Numerics.Vector3>();
        var idT = Quad(idV, 0, 0, 256, 256);
        Check("cover: identical meshes -> no candidates",
              DetectorModel.CoverageLoss(idV, idT, idV, idT, CL, SP).Count == 0, "the E5 shape, by construction");

        // A removed island: the loser has a far pad the winner does not. Candidates appear on the
        // island, in the victim direction only.
        var withIslandV = new List<System.Numerics.Vector3>();
        var withIslandT = Quad(withIslandV, 0, 0, 256, 256);
        Quad(withIslandV, 1000, 0, 1256, 256, 0, withIslandT);
        var baseOnlyV = new List<System.Numerics.Vector3>();
        var baseOnlyT = Quad(baseOnlyV, 0, 0, 256, 256);
        var lost = DetectorModel.CoverageLoss(withIslandV, withIslandT, baseOnlyV, baseOnlyT, CL, SP);
        Check("cover: island removed -> victim-direction candidates on the island",
              lost.Count > 0 && lost.All(s => s.Point.X > 900), $"{lost.Count} samples, all on the island");
        Check("cover: island removed -> candidate clearance is the real gap",
              lost.Max(s => s.Clearance) > 700, $"max clearance {(lost.Count > 0 ? lost.Max(s => s.Clearance) : 0):0}u (island is ~744u out)");
        Check("cover: the mirror direction is empty (nothing the winner has that the loser lacks)",
              DetectorModel.CoverageLoss(baseOnlyV, baseOnlyT, withIslandV, withIslandT, CL, SP).Count == 0,
              "a subset loses nothing");

        // Moved-but-overlapping: a 64u shift never opens 120u of clearance.
        var shiftedV = new List<System.Numerics.Vector3>();
        var shiftedT = Quad(shiftedV, 64, 64, 320, 320);
        Check("cover: geometry moved 64u -> no candidate at 120u clearance",
              DetectorModel.CoverageLoss(baseOnlyV, baseOnlyT, shiftedV, shiftedT, CL, SP).Count == 0
           && DetectorModel.CoverageLoss(shiftedV, shiftedT, baseOnlyV, baseOnlyT, CL, SP).Count == 0,
              "moved geometry that still overlaps is not a coverage difference");

        // Deleted-flag triangles are not coverage — a winner whose island survives only as
        // deleted triangles has still removed the ground.
        var delIslandV = new List<System.Numerics.Vector3>();
        var delIslandT = Quad(delIslandV, 0, 0, 256, 256);
        Quad(delIslandV, 1000, 0, 1256, 256, DetectorModel.NavTriDeleted, delIslandT);
        Check("cover: a Deleted-flag island is NOT coverage on the against side",
              DetectorModel.CoverageLoss(withIslandV, withIslandT, delIslandV, delIslandT, CL, SP).Count > 0,
              "flag 0x08 removes ground even when the triangles remain");
        Check("cover: a Deleted-flag island is never SAMPLED on the from side",
              DetectorModel.CoverageLoss(delIslandV, delIslandT, baseOnlyV, baseOnlyT, CL, SP).Count == 0,
              "no probe point on ground the record itself deletes");

        // Water: never a probe point, but it IS reachable ground to the oracle (an actor swims).
        var waterIslandV = new List<System.Numerics.Vector3>();
        var waterIslandT = Quad(waterIslandV, 0, 0, 256, 256);
        Quad(waterIslandV, 1000, 0, 1256, 256, DetectorModel.NavTriWater, waterIslandT);
        Check("cover: a Water island is never sampled as a probe point",
              DetectorModel.CoverageLoss(waterIslandV, waterIslandT, baseOnlyV, baseOnlyT, CL, SP).Count == 0,
              "flag 0x200 is not standable");
        Check("cover: a Water island still COUNTS as coverage on the against side",
              DetectorModel.CoverageLoss(withIslandV, withIslandT, waterIslandV, waterIslandT, CL, SP).Count == 0,
              "a target beside water reads as reachable — NEG-void's lesson");

        // Subdivision: one huge triangle whose centroid the winner still covers, but whose far
        // corner it does not. A centroid-only sampler returns the vacuous answer.
        var hugeV = new List<System.Numerics.Vector3> { new(0, 0, 0), new(2048, 0, 0), new(0, 2048, 0) };
        var hugeT = new List<(int, int, int, int)> { (0, 1, 2, 0) };
        var centroidPatchV = new List<System.Numerics.Vector3>();
        var centroidPatchT = Quad(centroidPatchV, 555, 555, 811, 811);
        var corner = DetectorModel.CoverageLoss(hugeV, hugeT, centroidPatchV, centroidPatchT, CL, SP);
        Check("cover: subdivision finds a lost corner the centroid would miss",
              corner.Count > 0 && corner.Any(s => s.Point.X > 1500 || s.Point.Y > 1500),
              $"{corner.Count} samples beyond a patch that covers the centroid");

        // An emptied winner: everything is lost, clearance unbounded.
        var emptyT = new List<(int, int, int, int)>();
        var allLost = DetectorModel.CoverageLoss(baseOnlyV, baseOnlyT, baseOnlyV, emptyT, CL, SP);
        Check("cover: an against side with no coverage -> every sample a candidate, clearance infinite",
              allLost.Count > 0 && allLost.All(s => float.IsPositiveInfinity(s.Clearance)),
              "the EMPTIED shape");

        // DistanceToCoverage — the measure itself, pinned at the three Voronoi regions, plus the
        // degenerate arms (zero-area and out-of-range triangles are lines/garbage, not ground).
        Check("cover: distance above the face is the height",
              MathF.Abs(DetectorModel.DistanceToCoverage(new(128, 128, 100), idV, idT) - 100f) < 0.01f,
              "face region");
        Check("cover: distance beyond an edge is to the edge",
              MathF.Abs(DetectorModel.DistanceToCoverage(new(300, 128, 0), idV, idT) - 44f) < 0.01f,
              "edge region, 300 - 256");
        Check("cover: distance beyond a corner is to the corner",
              MathF.Abs(DetectorModel.DistanceToCoverage(new(356, 356, 0), idV, idT) - MathF.Sqrt(2f) * 100f) < 0.01f,
              "vertex region");
        var degenV = new List<System.Numerics.Vector3> { new(0, 0, 0), new(100, 0, 0), new(200, 0, 0) };
        Check("cover: a zero-area triangle is not coverage",
              float.IsPositiveInfinity(DetectorModel.DistanceToCoverage(new(50, 10, 0), degenV,
                  new List<(int, int, int, int)> { (0, 1, 2, 0) })), "collinear vertices are a line, not ground");
        Check("cover: out-of-range indices are skipped, not crashed on",
              float.IsPositiveInfinity(DetectorModel.DistanceToCoverage(new(50, 10, 0), degenV,
                  new List<(int, int, int, int)> { (0, 1, 99, 0) })), "garbage contributes nothing");
    }

    // NeedsWitness — 3.5.5's named tail. Coverage loss alone is not a finding (Bethesda ships
    // the shape; a winner deleting ground under new architecture is doing its job); a finding
    // needs a records-side witness that still needs the lost ground. Each case is a witness whose
    // right answer is known by construction, measured against the SAME meshes the selector cases
    // use above, so the numbers the rule sees are real point-to-mesh distances, not typed in.
    {
        List<(int A, int B, int C, int Flags)> Quad(List<System.Numerics.Vector3> verts,
            float x0, float y0, float x1, float y1, int flags = 0,
            List<(int A, int B, int C, int Flags)>? into = null)
        {
            int b = verts.Count;
            verts.Add(new(x0, y0, 0)); verts.Add(new(x1, y0, 0));
            verts.Add(new(x1, y1, 0)); verts.Add(new(x0, y1, 0));
            var tris = into ?? new List<(int, int, int, int)>();
            tris.Add((b, b + 1, b + 2, flags));
            tris.Add((b, b + 2, b + 3, flags));
            return tris;
        }
        const float CL = 120f;
        // The victim's mesh: a base quad plus an island 744 u east of it. The winner's: the base
        // quad alone — the island is the ground the winner lost.
        var victimV = new List<System.Numerics.Vector3>();
        var victimT = Quad(victimV, 0, 0, 256, 256);
        Quad(victimV, 1000, 0, 1256, 256, 0, victimT);
        var winnerV = new List<System.Numerics.Vector3>();
        var winnerT = Quad(winnerV, 0, 0, 256, 256);
        float DV(System.Numerics.Vector3 p) => DetectorModel.DistanceToCoverage(p, victimV, victimT);
        float DW(System.Numerics.Vector3 p) => DetectorModel.DistanceToCoverage(p, winnerV, winnerT);
        DetectorModel.WitnessFacts At(System.Numerics.Vector3 p, float r = 0, bool touched = false,
                                      WitnessBlocker blocker = WitnessBlocker.None,
                                      WitnessKind kind = WitnessKind.PackageAnchor)
            => new(kind, r, DV(p), DW(p), blocker, touched);
        var onIsland = new System.Numerics.Vector3(1128, 128, 0);
        var onBase = new System.Numerics.Vector3(128, 128, 0);
        var nowhere = new System.Numerics.Vector3(5000, 5000, 0);

        // The island's centre is 872 u from the base quad's nearest edge (744 u edge to edge,
        // plus 128 u into the island) — measured through the same function the driver uses.
        var stranded = DetectorModel.NeedsWitness(At(onIsland), CL);
        Check("witness: anchor on the removed island -> STRANDED, victim 0u, winner 872u",
              stranded.Verdict == WitnessVerdict.Stranded && DV(onIsland) < 0.01f && MathF.Abs(DW(onIsland) - 872f) < 0.01f,
              stranded.Why);
        Check("witness: the same anchor with a radius reaching surviving coverage is NOT stranded (the − r)",
              DetectorModel.NeedsWitness(At(onIsland, r: 800), CL).Verdict == WitnessVerdict.Covered,
              "872 − 800 = 72u ≤ 120: the sandbox disc overlaps winner ground");
        Check("witness: a radius that still leaves the disc beyond clearance stays STRANDED",
              DetectorModel.NeedsWitness(At(onIsland, r: 400), CL).Verdict == WitnessVerdict.Stranded,
              "872 − 400 = 472u > 240");
        Check("witness: anchor on ground both arms cover -> nothing (Covered)",
              DetectorModel.NeedsWitness(At(onBase), CL).Verdict == WitnessVerdict.Covered, "the base quad is in both arms");
        Check("witness: anchor on ground neither arm covers -> nothing (Uncovered) — the vanilla-cave shape",
              DetectorModel.NeedsWitness(At(nowhere), CL).Verdict == WitnessVerdict.Uncovered,
              "dV > clearance too: not this record's doing");
        // The victim condition is the POINT, not the disc: an anchor hanging off victim coverage
        // by more than clearance had no ground under it before either, whatever its radius.
        var offVictim = new System.Numerics.Vector3(1128, 456, 0);   // 200u north of the island's edge
        Check("witness: dV is judged at the point — a radius does not manufacture victim ground",
              MathF.Abs(DV(offVictim) - 200f) < 0.01f
           && DetectorModel.NeedsWitness(At(offVictim, r: 300), CL).Verdict == WitnessVerdict.Uncovered,
              "200u off the island with r=300: Uncovered, never Stranded");

        // The resolution band, (120, 240]: the oracle's walk-target resolution. A package anchor
        // (W1) and a targeted ref (W3) ARE walk targets — a lost margin inside the band is reported,
        // not called.
        DetectorModel.WitnessFacts Lost(WitnessKind kind, float dW, float r = 0) => new(kind, r, 0, dW, WitnessBlocker.None, false);
        var band = DetectorModel.NeedsWitness(Lost(WitnessKind.PackageAnchor, 200), CL);
        Check("witness: W1 anchor in the (120, 240] band -> UNJUDGEABLE, counted by reason",
              band.Verdict == WitnessVerdict.Unjudgeable && band.Reason == WitnessBlocker.WithinResolutionBand, band.Why);
        Check("witness: W1 anchor at 1.2× clearance -> still the band",
              DetectorModel.NeedsWitness(Lost(WitnessKind.PackageAnchor, 1.2f * CL), CL).Reason == WitnessBlocker.WithinResolutionBand, "144: band");
        Check("witness: W3 targeted ref at 1.2× clearance -> still the band",
              DetectorModel.NeedsWitness(Lost(WitnessKind.TargetedRef, 1.2f * CL), CL).Reason == WitnessBlocker.WithinResolutionBand, "144: band");
        Check("witness: exactly 2× resolution is still the band for a walk target; one unit past it is a call",
              DetectorModel.NeedsWitness(Lost(WitnessKind.PackageAnchor, 240), CL).Reason == WitnessBlocker.WithinResolutionBand
           && DetectorModel.NeedsWitness(Lost(WitnessKind.PackageAnchor, 241), CL).Verdict == WitnessVerdict.Stranded,
              "240 -> band, 241 -> Stranded");
        Check("witness: the band applies to the disc margin, not the raw distance",
              DetectorModel.NeedsWitness(Lost(WitnessKind.PackageAnchor, 300, r: 100), CL).Reason == WitnessBlocker.WithinResolutionBand,
              "300 − 100 = 200: band");

        // The band fold (2026-09-01 confirmer): a placed actor (W2) is NOT a walk target — one set
        // down inside the band was displaced in game exactly like one well beyond it. For W2 the
        // band collapses: Stranded from clearance up, no upper test; the floor stays inclusive and
        // is the stated blind band; the victim direction only — the mirror is untouched.
        var folded = DetectorModel.NeedsWitness(Lost(WitnessKind.PlacedActor, 1.2f * CL), CL);
        Check("witness: W2 actor at 1.2× clearance -> STRANDED (the band fold), and its why names the in-game displacement",
              folded.Verdict == WitnessVerdict.Stranded && folded.Reason == WitnessBlocker.None && folded.Why.Contains("displaced in game"), folded.Why);
        Check("witness: W2 actor at exactly clearance -> Covered (the floor is inclusive; below it a placement is the blind band)",
              DetectorModel.NeedsWitness(Lost(WitnessKind.PlacedActor, CL), CL).Verdict == WitnessVerdict.Covered, "120: Covered, not Stranded");
        Check("witness: W2 actor at exactly 2× clearance -> STRANDED (no upper test for a placement)",
              DetectorModel.NeedsWitness(Lost(WitnessKind.PlacedActor, 2f * CL), CL).Verdict == WitnessVerdict.Stranded, "240: Stranded");
        var clear = DetectorModel.NeedsWitness(Lost(WitnessKind.PlacedActor, 3f * CL), CL);
        Check("witness: W2 actor at 3× clearance -> STRANDED as before, why without the band-fold clause",
              clear.Verdict == WitnessVerdict.Stranded && !clear.Why.Contains("displaced in game"), clear.Why);
        var foldMirror = DetectorModel.NeedsWitness(new(WitnessKind.PlacedActor, 0, 1.2f * CL, 0, WitnessBlocker.None, false), CL);
        Check("witness: W2 mirror at 1.2× clearance -> MIRROR, unchanged (the fold touches the victim direction only)",
              foldMirror.Verdict == WitnessVerdict.Mirror, foldMirror.Why);

        // Unjudgeable by I/O reason — each counted, none cleared, and every one outranks the numbers.
        foreach (var (blocker, label) in new[] {
            (WitnessBlocker.UnreadableAnchor, "alias/cell/keyword/object-type/player arm"),
            (WitnessBlocker.Templated, "templated NPC"),
            (WitnessBlocker.LeveledBase, "leveled-base actor"),
            (WitnessBlocker.PackageMissing, "package record missing") })
        {
            var blocked = DetectorModel.NeedsWitness(At(onIsland, blocker: blocker), CL);
            Check($"witness: {label} -> UNJUDGEABLE even on stranded numbers",
                  blocked.Verdict == WitnessVerdict.Unjudgeable && blocked.Reason == blocker, blocked.Why);
        }
        var noGeom = DetectorModel.NeedsWitness(new(WitnessKind.PlacedActor, 0, float.NaN, float.NaN, WitnessBlocker.None, false), CL);
        Check("witness: geometry that failed to extract -> UNJUDGEABLE (GeometryMissing)",
              noGeom.Verdict == WitnessVerdict.Unjudgeable && noGeom.Reason == WitnessBlocker.GeometryMissing, noGeom.Why);
        Check("witness: an emptied winner arm strands with infinite margin",
              DetectorModel.NeedsWitness(new(WitnessKind.PlacedActor, 0, 0, float.PositiveInfinity, WitnessBlocker.None, false), CL).Verdict == WitnessVerdict.Stranded,
              "inf − r is still inf");

        // The authorship split: identical geometry, the only difference is who touched the witness.
        Check("witness: identical geometry, witness overridden by the winner's side -> DELIBERATE",
              DetectorModel.NeedsWitness(At(onIsland, touched: true), CL) is { Verdict: WitnessVerdict.Stranded, Authorship: WitnessAuthorship.Deliberate },
              "HearthFires placing its own homestead actors");
        Check("witness: identical geometry, witness untouched by the winner's side -> OVERSIGHT",
              DetectorModel.NeedsWitness(At(onIsland, touched: false), CL) is { Verdict: WitnessVerdict.Stranded, Authorship: WitnessAuthorship.Oversight },
              "a city overhaul reverting USSEP's mesh under a vanilla guard's marker");

        // The mirror direction: winner-only ground with a witness exists, reports as the mirror,
        // and is never a finding. Same meshes, arms swapped.
        var mirror = DetectorModel.NeedsWitness(new(WitnessKind.PlacedActor, 0, DW(onIsland), DV(onIsland), WitnessBlocker.None, false), CL);
        Check("witness: winner-only ground under a witness -> MIRROR, never Stranded",
              mirror.Verdict == WitnessVerdict.Mirror, mirror.Why);

        // The horizontal reading beside the frozen 3-D one: the temple shape — an anchor 328 u
        // above the floor reads 328 in 3-D and 0 horizontally; beyond an edge both agree.
        var elevated = new System.Numerics.Vector3(128, 128, 328);
        Check("witness: horizontal distance ignores an anchor's height above the floor (the 328u marker)",
              MathF.Abs(DetectorModel.DistanceToCoverage(elevated, winnerV, winnerT) - 328f) < 0.01f
           && DetectorModel.DistanceToCoverageXY(elevated, winnerV, winnerT) < 0.01f,
              "3-D 328u, horizontal 0u");
        Check("witness: horizontal distance beyond an edge is the edge distance",
              MathF.Abs(DetectorModel.DistanceToCoverageXY(new(300, 128, 500), winnerV, winnerT) - 44f) < 0.01f,
              "300 − 256, height irrelevant");
        var wallV = new List<System.Numerics.Vector3> { new(0, 0, 0), new(256, 0, 0), new(256, 0, 256) };
        Check("witness: a vertical triangle is not floor to the horizontal reading",
              float.IsPositiveInfinity(DetectorModel.DistanceToCoverageXY(new(128, 10, 0), wallV,
                  new List<(int, int, int, int)> { (0, 1, 2, 0) })), "zero XY area");
    }
}

// ---------------------------------------------------------------------------------------
// Placement dependency — the records-layer pass for stranded support / orphaned attachment
// (roadmap 4.5/4.6). The rule is pure and gets fact-level cases; then a SYNTHETIC ORDER written
// with Mutagen — a master placing hosts and dependents with XESP/XLKR/XAPR edges, and three
// plugins moving, re-placing, deleting and forwarding them — is read back through the SAME
// extraction the real probe uses (PlacementExtract -> DependencyIndex -> the rule), so a break in
// the plumbing (an edge kind not extracted, an author mis-assigned, a deletion missed) fails by
// name here rather than as a number quietly moving on the live order.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\nPROOF — placement dependency (_shared/DetectorModel.cs + _shared/DependencyIndex.cs)\n");
{
    const float T = 16f;
    DetectorModel.DependencyFacts F(HostEdgeKind kind = HostEdgeKind.EnableParent, bool resolved = true, bool hostDeleted = false,
        bool hostDisabled = false, bool sameCell = true, bool cellChanged = false, float moved = 200f,
        bool depDeleted = false, bool rePlaced = false, bool byMover = false, HostClass host = HostClass.Physical, bool player = false)
        => new(kind, player, resolved, hostDeleted, hostDisabled, sameCell, cellChanged, moved, depDeleted, rePlaced, byMover, host);
    var lb = DetectorModel.PlacementDependency(F(), T);
    Check("dep: host moved 200u, dependent untouched -> LEFT BEHIND",
          lb.Verdict == DependencyVerdict.LeftBehind && lb.Reason == DependencyBlocker.None, lb.Why);
    Check("dep: host moved 10u -> INTACT (under the 16u threshold)",
          DetectorModel.PlacementDependency(F(moved: 10), T).Verdict == DependencyVerdict.Intact, "jitter, not a move");
    Check("dep: host moved exactly 16u -> INTACT; 17u -> LEFT BEHIND (the threshold is inclusive, as driftprobe's)",
          DetectorModel.PlacementDependency(F(moved: 16), T).Verdict == DependencyVerdict.Intact
       && DetectorModel.PlacementDependency(F(moved: 17), T).Verdict == DependencyVerdict.LeftBehind, "16 -> Intact, 17 -> LeftBehind");
    Check("dep: host left its cell or worldspace at 0u -> LEFT BEHIND (a relocation counts whatever the distance)",
          DetectorModel.PlacementDependency(F(moved: 0, cellChanged: true), T).Verdict == DependencyVerdict.LeftBehind, "space change");
    Check("dep: dependent re-placed by the mover's side -> MOVED TOGETHER, whatever the offset",
          DetectorModel.PlacementDependency(F(rePlaced: true, byMover: true), T).Verdict == DependencyVerdict.MovedTogether, "a deliberate re-arrangement");
    var third = DetectorModel.PlacementDependency(F(rePlaced: true, byMover: false), T);
    Check("dep: dependent re-placed by an unrelated third plugin -> UNJUDGEABLE (nobody's fault to name)",
          third.Verdict == DependencyVerdict.Unjudgeable && third.Reason == DependencyBlocker.DependentMovedByThirdParty, third.Why);
    Check("dep: dependent deleted -> DEPENDENT REMOVED (nothing left behind)",
          DetectorModel.PlacementDependency(F(depDeleted: true), T).Verdict == DependencyVerdict.DependentRemoved, "deleted");
    var ply = DetectorModel.PlacementDependency(F(player: true), T);
    Check("dep: host is the player reference -> UNJUDGEABLE (HostIsPlayer), outranking every other fact",
          ply.Verdict == DependencyVerdict.Unjudgeable && ply.Reason == DependencyBlocker.HostIsPlayer
       && DetectorModel.PlacementDependency(F(player: true, resolved: false), T).Reason == DependencyBlocker.HostIsPlayer, ply.Why);
    var unres = DetectorModel.PlacementDependency(F(resolved: false), T);
    Check("dep: host unresolved -> UNJUDGEABLE even on moved numbers",
          unres.Verdict == DependencyVerdict.Unjudgeable && unres.Reason == DependencyBlocker.HostUnresolved, unres.Why);
    var del = DetectorModel.PlacementDependency(F(hostDeleted: true), T);
    Check("dep: host deleted -> UNJUDGEABLE (HostDeleted), counted apart",
          del.Verdict == DependencyVerdict.Unjudgeable && del.Reason == DependencyBlocker.HostDeleted, del.Why);
    var nav = DetectorModel.PlacementDependency(F(moved: float.NaN), T);
    Check("dep: no authoring view -> UNJUDGEABLE (NoAuthoringView)",
          nav.Verdict == DependencyVerdict.Unjudgeable && nav.Reason == DependencyBlocker.NoAuthoringView, nav.Why);
    var dis = DetectorModel.PlacementDependency(F(hostDisabled: true), T);
    Check("dep: initially-disabled host that moved -> UNJUDGEABLE (a toggle); one that did not move -> INTACT",
          dis.Verdict == DependencyVerdict.Unjudgeable && dis.Reason == DependencyBlocker.HostDisabled
       && DetectorModel.PlacementDependency(F(hostDisabled: true, moved: 5), T).Verdict == DependencyVerdict.Intact, dis.Why);
    var cross = DetectorModel.PlacementDependency(F(sameCell: false), T);
    Check("dep: host and dependent in different spaces at authoring, host moved -> UNJUDGEABLE (CrossCell)",
          cross.Verdict == DependencyVerdict.Unjudgeable && cross.Reason == DependencyBlocker.CrossCell, cross.Why);
    Check("dep: unresolved outranks deleted outranks the numbers (blocker order)",
          DetectorModel.PlacementDependency(F(resolved: false, hostDeleted: true), T).Reason == DependencyBlocker.HostUnresolved
       && DetectorModel.PlacementDependency(F(hostDeleted: true, moved: float.NaN), T).Reason == DependencyBlocker.HostDeleted, "first blocker wins");
    var mk = DetectorModel.PlacementDependency(F(host: HostClass.Marker), T);
    Check("dep: a marker host is still LEFT BEHIND by the rule — the split is the caller's, and the why names the class",
          mk.Verdict == DependencyVerdict.LeftBehind && mk.Why.Contains("Marker host"), mk.Why);
    Check("dep: XLKR and XAPR edges take the same rule",
          DetectorModel.PlacementDependency(F(kind: HostEdgeKind.LinkedRef), T).Verdict == DependencyVerdict.LeftBehind
       && DetectorModel.PlacementDependency(F(kind: HostEdgeKind.ActivateParent), T).Verdict == DependencyVerdict.LeftBehind, "all three kinds");

    // Host class — the split that keeps toggles out of 4.5/4.6.
    Check("dep: Static / MoveableStatic / Furniture / Container / Door -> PHYSICAL",
          new[] { "Static", "MoveableStatic", "Furniture", "Container", "Door" }.All(t => DetectorModel.ClassifyHost(t, "Table01") == HostClass.Physical), "the things to rest on or hang from");
    Check("dep: a Static named XMarker / XMarkerHeading -> MARKER (the editor id decides, not the record type)",
          DetectorModel.ClassifyHost("Static", "XMarker") == HostClass.Marker
       && DetectorModel.ClassifyHost("Static", "XMarkerHeading") == HostClass.Marker
       && DetectorModel.ClassifyHost("Activator", "DefaultEnableMarker") == HostClass.Marker, "marker by name");
    Check("dep: Activator / Light / Npc / unresolved base -> OTHER",
          DetectorModel.ClassifyHost("Activator", "Lever01") == HostClass.Other
       && DetectorModel.ClassifyHost("Light", "Candle01") == HostClass.Other
       && DetectorModel.ClassifyHost("Npc", "Guard") == HostClass.Other
       && DetectorModel.ClassifyHost(null, null) == HostClass.Other, "reported apart");

    // Dependent class — the split the first live run forced: every physical-host LeftBehind row had an actor
    // or a trigger as the dependent, and neither rests on anything.
    Check("dep: a placed NPC, or an Npc / LeveledNpc base -> ACTOR",
          DetectorModel.ClassifyDependent("PlacedNpc", "Npc", "ElisifTheFair") == DependentClass.Actor
       && DetectorModel.ClassifyDependent("PlacedObject", "LeveledNpc", "LvlBandit") == DependentClass.Actor, "walks to its link wherever it is");
    Check("dep: a trigger / marker / FX / idle-marker base -> CONTROL",
          DetectorModel.ClassifyDependent("PlacedObject", "Activator", "MGR02FocalPointTrigger") == DependentClass.Control
       && DetectorModel.ClassifyDependent("PlacedObject", "Static", "XMarkerHeading") == DependentClass.Control
       && DetectorModel.ClassifyDependent("PlacedObject", "MoveableStatic", "FXMistLow01Adjust") == DependentClass.Control
       && DetectorModel.ClassifyDependent("PlacedObject", "IdleMarker", "PatrolIdleMarker") == DependentClass.Control, "marks a spot, rests on nothing");
    Check("dep: clutter, furniture, lights, fixtures, activators by name -> PHYSICAL",
          DetectorModel.ClassifyDependent("PlacedObject", "Ingestible", "FoodApple") == DependentClass.Physical
       && DetectorModel.ClassifyDependent("PlacedObject", "Light", "CandleHornTable01") == DependentClass.Physical
       && DetectorModel.ClassifyDependent("PlacedObject", "Static", "WallSconce01") == DependentClass.Physical
       && DetectorModel.ClassifyDependent("PlacedObject", "Activator", "Lever01") == DependentClass.Physical
       && DetectorModel.ClassifyDependent("PlacedObject", "MiscItem", "Basket01") == DependentClass.Physical, "the apples and the sconce");
    Check("dep: an unresolved base -> OTHER",
          DetectorModel.ClassifyDependent("PlacedObject", null, null) == DependentClass.Other, "counted apart");

    // Population — rule 12 for (mover, author).
    Check("dep: vanilla mover, vanilla author -> FLOOR",
          DetectorModel.ClassifyDependencyPopulation(true, true, false) == DependencyPopulation.BethesdaFloor, "class A");
    Check("dep: USSEP mover -> its own row",
          DetectorModel.ClassifyDependencyPopulation(false, true, true) == DependencyPopulation.UssepMover, "USSEP moving a vanilla host");
    Check("dep: vanilla mover under a MOD's edge -> kept apart (the authoring-view assumption is weakest here)",
          DetectorModel.ClassifyDependencyPopulation(true, false, false) == DependencyPopulation.VanillaMoverUnderModEdge, "Update.esm under a mod's XESP");
    Check("dep: mod mover, any author -> MOD MOVER (the class)",
          DetectorModel.ClassifyDependencyPopulation(false, true, false) == DependencyPopulation.ModMover
       && DetectorModel.ClassifyDependencyPopulation(false, false, false) == DependencyPopulation.ModMover, "author split in the report");

    // ---- The synthetic order: four plugins written with Mutagen, read back through the real extraction. ----
    var root = Path.Combine(Path.GetTempPath(), "proofprobe-dep-" + Environment.ProcessId);
    Directory.CreateDirectory(root);
    var release = SkyrimRelease.SkyrimSE;
    var mKey = new ModKey("DepMaster", ModType.Master);
    var aKey = new ModKey("DepA", ModType.Plugin);
    var bKey = new ModKey("DepB", ModType.Plugin);
    var cKey = new ModKey("DepC", ModType.Plugin);
    FormKey M(uint id) => new(mKey, id);
    var tableK = M(0x801); var markerK = M(0x802); var cell1K = M(0x810); var cell2K = M(0x811);
    FormKey H(int n) => M((uint)(0x900 + n));     // hosts
    FormKey D(int n) => M((uint)(0xA00 + n));     // dependents
    var aStaticK = new FormKey(aKey, 0x801);       // a record only A defines — what makes B master A

    static void FileInterior(SkyrimMod mod, Cell cell)
    {
        uint id = cell.FormKey.ID;
        int blockN = (int)(id % 10), subN = (int)((id / 10) % 10);
        var records = mod.Cells.Records;
        var block = records.FirstOrDefault(b => b.BlockNumber == blockN);
        if (block is null) { block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock }; records.Add(block); }
        var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subN);
        if (sub is null) { sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock }; block.SubBlocks.Add(sub); }
        sub.Cells.Add(cell);
    }
    static PlacedObject Ref(FormKey key, FormKey baseKey, float x, float y = 0, float z = 0, string? edid = null, bool disabled = false)
    {
        var r = new PlacedObject(key, SkyrimRelease.SkyrimSE) { EditorID = edid, Placement = new Placement { Position = new P3Float(x, y, z), Rotation = new P3Float(0, 0, 0) } };
        r.Base.SetTo(baseKey);
        if (disabled) r.SkyrimMajorRecordFlags |= SkyrimMajorRecord.SkyrimMajorRecordFlag.InitiallyDisabled;
        return r;
    }
    static PlacedObject Xesp(PlacedObject r, FormKey host) { r.EnableParent = new EnableParent(); r.EnableParent.Reference.SetTo(host); return r; }
    static PlacedObject Xlkr(PlacedObject r, FormKey host) { var l = new LinkedReferences(); l.Reference.SetTo(host); r.LinkedReferences.Add(l); return r; }
    static PlacedObject Xapr(PlacedObject r, FormKey host) { r.ActivateParents = new ActivateParents(); var ap = new ActivateParent { Delay = 0 }; ap.Reference.SetTo(host); r.ActivateParents.Parents.Add(ap); return r; }

    string Write(SkyrimMod mod, params ISkyrimModGetter[] masters)
    {
        var path = Path.Combine(root, mod.ModKey.FileName.String);
        mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
        return path;
    }

    // The MASTER: two interior cells, a table static, a marker static, and the host/dependent pairs.
    // Host n sits at x = 1000·n; its dependent sits 10u beside it (40u up — an apple on a table).
    var master = new SkyrimMod(mKey, release);
    master.Statics.Add(new Static(tableK, release) { EditorID = "DepTable" });
    master.Statics.Add(new Static(markerK, release) { EditorID = "XMarkerHeading" });
    var cell1 = new Cell(cell1K, release) { EditorID = "DepCell1", Flags = Cell.Flag.IsInteriorCell };
    var cell2 = new Cell(cell2K, release) { EditorID = "DepCell2", Flags = Cell.Flag.IsInteriorCell };
    foreach (int n in new[] { 1, 2, 3, 4, 6, 7, 8, 9, 10, 11, 14, 15, 16, 17, 18 })
        cell1.Temporary.Add(Ref(H(n), tableK, 1000f * n, edid: $"Host{n}", disabled: n == 12));
    cell1.Temporary.Add(Ref(H(12), tableK, 12000f, edid: "Host12", disabled: true));
    cell1.Temporary.Add(Ref(H(5), markerK, 5000f, edid: "Host5Marker"));
    foreach (int n in new[] { 1, 2, 3, 4, 5, 9, 10, 11, 12, 14, 16, 17, 18 })
        cell1.Temporary.Add(Xesp(Ref(D(n), tableK, 1000f * n + 10, 0, 40, $"Dep{n}"), H(n)));
    cell2.Temporary.Add(Xesp(Ref(D(6), tableK, 6010, 0, 40, "Dep6"), H(6)));            // cross-cell edge
    cell1.Temporary.Add(Xlkr(Ref(D(7), tableK, 7010, 0, 40, "Dep7"), H(7)));            // XLKR
    cell1.Temporary.Add(Xapr(Ref(D(8), tableK, 8010, 0, 40, "Dep8"), H(8)));            // XAPR
    cell1.Temporary.Add(Xesp(Ref(D(13), tableK, 13010, 0, 40, "Dep13"), M(0xFFF)));     // host that exists nowhere
    cell1.Temporary.Add(Ref(D(15), tableK, 15010, 0, 40, "Dep15"));                     // no edge until A adds one
    cell1.Temporary.Add(Xlkr(Xesp(Ref(D(19), tableK, 1020, 0, 40, "Dep19"), H(1)), H(2))); // two edges, two hosts
    cell1.Temporary.Add(Xesp(Ref(D(20), tableK, 1030, 0, 40, "Dep20"), H(1)));          // A will drop this edge
    cell1.Temporary.Add(Xesp(Ref(D(21), tableK, 1040, 0, 40, "Dep21"), M(0x14)));        // enable parent = the player (the permanently-disabled idiom)
    FileInterior(master, cell1); FileInterior(master, cell2);
    var wKey = M(0x830); var topK = M(0x831); var gridK = M(0x832);
    static Worldspace World(FormKey wKey, FormKey topK, FormKey gridK, PlacedObject? persistent, PlacedObject? temporary)
    {
        var ws = new Worldspace(wKey, SkyrimRelease.SkyrimSE) { EditorID = "DepWorld" };
        ws.TopCell = new Cell(topK, SkyrimRelease.SkyrimSE) { Flags = default };
        if (persistent is not null) ws.TopCell.Persistent.Add(persistent);
        if (temporary is not null)
        {
            var gc = new Cell(gridK, SkyrimRelease.SkyrimSE) { Flags = default, Grid = new CellGrid { Point = new P2Int(0, 0) } };
            gc.Temporary.Add(temporary);
            var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
            var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
            sub.Items.Add(gc); blk.Items.Add(sub); ws.SubCells.Add(blk);
        }
        return ws;
    }
    master.Worldspaces.Add(World(wKey, topK, gridK,
        Ref(H(22), tableK, 500, 500, 0, "Host22"),                               // persistent: lives in the worldspace's persistent cell
        Xesp(Ref(D(22), tableK, 510, 500, 40, "Dep22"), H(22))));                // temporary: lives in grid cell (0,0)
    var mPath = Write(master);

    // PLUGIN A (masters DepMaster): the mover.
    using var mOv = SkyrimMod.CreateFromBinaryOverlay(mPath, release);
    var a = new SkyrimMod(aKey, release);
    a.Statics.Add(new Static(aStaticK, release) { EditorID = "DepAStatic" });
    var a1 = new Cell(cell1K, release) { Flags = Cell.Flag.IsInteriorCell };
    var a2 = new Cell(cell2K, release) { Flags = Cell.Flag.IsInteriorCell };
    foreach (int n in new[] { 1, 3, 4, 5, 6, 7, 8, 10, 11, 12, 14, 15 })                 // moved 200u
        a1.Temporary.Add(Ref(H(n), n == 5 ? markerK : tableK, 1000f * n + 200, edid: n == 5 ? "Host5Marker" : $"Host{n}", disabled: n == 12));
    a1.Temporary.Add(Ref(H(2), tableK, 2010, edid: "Host2"));                          // moved 10u
    a1.Temporary.Add(Ref(H(17), tableK, 17016, edid: "Host17"));                       // exactly the threshold
    a1.Temporary.Add(Ref(H(18), tableK, 18017, edid: "Host18"));                       // one past it
    a2.Temporary.Add(Ref(H(16), tableK, 16000, edid: "Host16"));                       // relocated to cell 2, same coordinates
    a1.Temporary.Add(Xesp(Ref(D(3), tableK, 3210, 0, 40, "Dep3"), H(3)));               // A moves the dependent too
    a1.Temporary.Add(new PlacedObject(H(9), release) { IsDeleted = true });             // host deleted, placement stripped
    a1.Temporary.Add(new PlacedObject(D(11), release) { IsDeleted = true });            // dependent deleted
    a1.Temporary.Add(Xesp(Ref(D(14), tableK, 14010, 0, 40, "Dep14"), H(14)));           // forwarded unchanged: the edge's author stays the master
    a1.Temporary.Add(Xesp(Ref(D(15), tableK, 15010, 0, 40, "Dep15"), H(15)));           // A SETS this edge — its own view of H15 is the moved one
    a1.Temporary.Add(Ref(D(20), tableK, 1030, 0, 40, "Dep20"));                         // edge dropped
    FileInterior(a, a1); FileInterior(a, a2);
    a.Worldspaces.Add(World(wKey, topK, gridK, Ref(H(22), tableK, 700, 500, 0, "Host22"), null));   // A moves the persistent host 200u
    var aPath = Write(a, mOv);

    // PLUGIN B (masters DepMaster AND DepA): re-places Dep4 after A moved Host4 — the patch shape.
    using var aOv = SkyrimMod.CreateFromBinaryOverlay(aPath, release);
    var b = new SkyrimMod(bKey, release);
    var b1 = new Cell(cell1K, release) { Flags = Cell.Flag.IsInteriorCell };
    b1.Temporary.Add(Xesp(Ref(D(4), tableK, 4210, 0, 40, "Dep4"), H(4)));
    b1.Temporary.Add(Ref(new FormKey(bKey, 0x801), aStaticK, 99000, edid: "DepBRef"));   // references A -> masters A
    FileInterior(b, b1);
    var bPath = Write(b, mOv, aOv);

    // PLUGIN C (masters DepMaster only): re-places Dep10 without ever seeing A — the third party.
    var c = new SkyrimMod(cKey, release);
    var c1 = new Cell(cell1K, release) { Flags = Cell.Flag.IsInteriorCell };
    c1.Temporary.Add(Xesp(Ref(D(10), tableK, 10050, 0, 40, "Dep10"), H(10)));
    FileInterior(c, c1);
    var cPath = Write(c, mOv);

    // Read the order back the way depprobe does.
    var paths = new[] { mPath, aPath, bPath, cPath };
    var idx = new DependencyIndex();
    var keys = new ModKey[paths.Length];
    var masterIdx = new HashSet<int>[paths.Length];
    for (int p = 0; p < paths.Length; p++)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], release);
        keys[p] = mod.ModKey;
        var names = mod.ModHeader.MasterReferences.Select(x => x.Master).ToHashSet();
        masterIdx[p] = Enumerable.Range(0, p).Where(q => names.Contains(keys[q])).ToHashSet();
        idx.Ingest(p, mod);
    }
    var closure = DetectorModel.MasterClosure(masterIdx);
    Check("dep-synth: the written order has the declared masters it was built to have",
          masterIdx[1].SetEquals(new[] { 0 }) && masterIdx[2].SetEquals(new[] { 0, 1 }) && masterIdx[3].SetEquals(new[] { 0 }),
          $"A<-[{string.Join(",", masterIdx[1])}] B<-[{string.Join(",", masterIdx[2])}] C<-[{string.Join(",", masterIdx[3])}]");
    var rows = idx.Edges.Select(e => idx.Judge(e, closure))
                  .ToDictionary(r => (r.Edge.Dependent, r.Edge.Kind), r => (Row: r, V: DetectorModel.PlacementDependency(r.Facts, T)));
    (DependencyVerdict Verdict, DependencyBlocker Reason, string Why) V(int n, HostEdgeKind k = HostEdgeKind.EnableParent)
        => rows.TryGetValue((D(n), k), out var x) ? x.V : (DependencyVerdict.Intact, DependencyBlocker.None, "(edge not extracted)");
    string W(int n, HostEdgeKind k = HostEdgeKind.EnableParent) => V(n, k).Why;

    Check("dep-synth: all three edge kinds are extracted (XESP, XLKR, XAPR), and the dropped edge is gone",
          rows.ContainsKey((D(1), HostEdgeKind.EnableParent)) && rows.ContainsKey((D(7), HostEdgeKind.LinkedRef))
       && rows.ContainsKey((D(8), HostEdgeKind.ActivateParent)) && !rows.ContainsKey((D(20), HostEdgeKind.EnableParent)),
          $"{idx.EdgeCount} edges");
    Check("dep-synth: A moves Host1 200u, Dep1 untouched -> LEFT BEHIND, physical host",
          V(1).Verdict == DependencyVerdict.LeftBehind && rows[(D(1), HostEdgeKind.EnableParent)].Row.Facts.Host == HostClass.Physical, W(1));
    Check("dep-synth: A moves Host2 10u -> INTACT", V(2).Verdict == DependencyVerdict.Intact, W(2));
    Check("dep-synth: A moves Host3 and Dep3 -> MOVED TOGETHER", V(3).Verdict == DependencyVerdict.MovedTogether, W(3));
    Check("dep-synth: A moves Host4, B (masters A) re-places Dep4 -> MOVED TOGETHER (the patch shape)",
          V(4).Verdict == DependencyVerdict.MovedTogether, W(4));
    Check("dep-synth: A moves the marker host -> LEFT BEHIND with Host = MARKER (the toggle row)",
          V(5).Verdict == DependencyVerdict.LeftBehind && rows[(D(5), HostEdgeKind.EnableParent)].Row.Facts.Host == HostClass.Marker, W(5));
    Check("dep-synth: dependent in the other cell -> UNJUDGEABLE (CrossCell)",
          V(6).Reason == DependencyBlocker.CrossCell, W(6));
    Check("dep-synth: XLKR edge, host moved -> LEFT BEHIND", V(7, HostEdgeKind.LinkedRef).Verdict == DependencyVerdict.LeftBehind, W(7, HostEdgeKind.LinkedRef));
    Check("dep-synth: XAPR edge, host moved -> LEFT BEHIND", V(8, HostEdgeKind.ActivateParent).Verdict == DependencyVerdict.LeftBehind, W(8, HostEdgeKind.ActivateParent));
    Check("dep-synth: A deletes Host9 (placement stripped) -> UNJUDGEABLE (HostDeleted), caught off the record walk",
          V(9).Reason == DependencyBlocker.HostDeleted && idx.DeletedBy.ContainsKey(H(9)), W(9));
    Check("dep-synth: A moves Host10, C (does not master A) re-places Dep10 -> UNJUDGEABLE (third party)",
          V(10).Reason == DependencyBlocker.DependentMovedByThirdParty, W(10));
    Check("dep-synth: A moves Host11 and deletes Dep11 -> DEPENDENT REMOVED",
          V(11).Verdict == DependencyVerdict.DependentRemoved, W(11));
    Check("dep-synth: initially-disabled Host12 moved -> UNJUDGEABLE (HostDisabled)",
          V(12).Reason == DependencyBlocker.HostDisabled, W(12));
    Check("dep-synth: edge to a host no plugin places -> UNJUDGEABLE (HostUnresolved)",
          V(13).Reason == DependencyBlocker.HostUnresolved, W(13));
    Check("dep-synth: enable parent = the player reference -> UNJUDGEABLE (HostIsPlayer), matched on the raw id",
          V(21).Reason == DependencyBlocker.HostIsPlayer, W(21));
    Check("dep-synth: a table-based dependent on a table host is PHYSICAL on both sides — the support-shaped row",
          rows[(D(1), HostEdgeKind.EnableParent)].Row.Dependent == DependentClass.Physical
       && rows[(D(1), HostEdgeKind.EnableParent)].Row.Facts.Host == HostClass.Physical, "Dep1 on Host1");
    Check("dep-synth: A forwards Dep14's edge unchanged -> the master stays the author, Host14's move is LEFT BEHIND",
          rows[(D(14), HostEdgeKind.EnableParent)].Row.Edge.Author == 0 && V(14).Verdict == DependencyVerdict.LeftBehind, W(14));
    Check("dep-synth: A SETS Dep15's edge itself -> A is the author, and its own view of Host15 is the moved one: INTACT",
          rows[(D(15), HostEdgeKind.EnableParent)].Row.Edge.Author == 1 && V(15).Verdict == DependencyVerdict.Intact, W(15));
    Check("dep-synth: Host16 relocated to the other cell at the same coordinates -> LEFT BEHIND (space change)",
          V(16).Verdict == DependencyVerdict.LeftBehind && rows[(D(16), HostEdgeKind.EnableParent)].Row.Facts.HostChangedSpace, W(16));
    Check("dep-synth: EXTERIOR — persistent host in the worldspace's persistent cell, dependent in a grid cell: same SPACE, host moved -> LEFT BEHIND, not CrossCell",
          V(22).Verdict == DependencyVerdict.LeftBehind && rows[(D(22), HostEdgeKind.EnableParent)].Row.Facts.SameSpaceAtAuthoring
       && rows[(D(22), HostEdgeKind.EnableParent)].Row.HostThen!.Value.Cell != rows[(D(22), HostEdgeKind.EnableParent)].Row.DepThen!.Value.Cell, W(22));
    Check("dep-synth: A forwarded Dep14's record unchanged -> the row says the mover's side OVERRODE it (seen, left); Dep1's does not",
          rows[(D(14), HostEdgeKind.EnableParent)].Row.DependentTouchedByMoverSide && !rows[(D(1), HostEdgeKind.EnableParent)].Row.DependentTouchedByMoverSide,
          "reported beside the verdict, never folded into it");
    Check("dep-synth: the authored host-dependent offset is measured in the author's view (Dep1: 10u across, 40u up)",
          MathF.Abs(rows[(D(1), HostEdgeKind.EnableParent)].Row.AuthoredOffset - MathF.Sqrt(10f * 10f + 40f * 40f)) < 0.01f
       && float.IsNaN(rows[(D(6), HostEdgeKind.EnableParent)].Row.AuthoredOffset),
          $"{rows[(D(1), HostEdgeKind.EnableParent)].Row.AuthoredOffset:0.00}u; cross-space is NaN");
    Check("dep-synth: Host17 moved exactly 16u -> INTACT; Host18 moved 17u -> LEFT BEHIND",
          V(17).Verdict == DependencyVerdict.Intact && V(18).Verdict == DependencyVerdict.LeftBehind, $"{W(17)} | {W(18)}");
    Check("dep-synth: a dependent with two edges gets two rows, judged apart",
          V(19, HostEdgeKind.EnableParent).Verdict == DependencyVerdict.LeftBehind && V(19, HostEdgeKind.LinkedRef).Verdict == DependencyVerdict.Intact,
          "XESP->Host1 left behind, XLKR->Host2 intact");
    Check("dep-synth: the mover is A for every moved host, and the placement count is the written one",
          rows.Values.Where(x => x.V.Verdict == DependencyVerdict.LeftBehind).All(x => x.Row.Mover == 1) && idx.History.Count == 41,   // 18 hosts + 22 dependents + B's own ref
          $"{idx.History.Count} refs in the history");
    try { Directory.Delete(root, true); } catch { }
}

// ---------------------------------------------------------------------------------------
// Overlap provenance — the differential framing of milestone 5 (roadmap 4.1). The rule
// (DetectorModel.OverlapProvenance, ClassifyOverlapPopulation, ClassifyOverlapScale) at fact level,
// then a synthetic five-plugin order written with Mutagen and read back through OverlapIndex — the
// same ingest, candidate build, pair sweep and arm construction the live probe uses, with OBND boxes
// standing in for meshes (the proof has no NIFs; the fallback is labelled 'o' either way).
// ---------------------------------------------------------------------------------------
Console.WriteLine("\nPROOF — overlap provenance (_shared/DetectorModel.cs + _shared/OverlapIndex.cs)\n");
{
    static DetectorModel.OverlapFacts OF(bool hadPrev = true, bool present = true, bool without = false, bool seen = false)
        => new(hadPrev, present, without, seen);

    var ib = DetectorModel.OverlapProvenance(OF(hadPrev: false, present: false));
    Check("ovl: new ref into a box set outside the closure -> INTRODUCED + BLIND",
          ib.Origin == OverlapOrigin.Introduced && ib.Sight == OverlapSight.Blind && ib.Why.Contains("introduced"), ib.Why);
    var isn = DetectorModel.OverlapProvenance(OF(hadPrev: false, present: false, seen: true));
    Check("ovl: new ref into a box the masters set -> INTRODUCED + SEEN",
          isn.Origin == OverlapOrigin.Introduced && isn.Sight == OverlapSight.Seen, isn.Why);
    var mv = DetectorModel.OverlapProvenance(OF(hadPrev: true, present: true, without: false));
    Check("ovl: existing ref moved into the box (stood clear before) -> INTRODUCED, says 'moved'",
          mv.Origin == OverlapOrigin.Introduced && mv.Why.Contains("moved"), mv.Why);
    var en = DetectorModel.OverlapProvenance(OF(hadPrev: true, present: false));
    Check("ovl: previous snap disabled / other space -> INTRODUCED, says 'enabled or relocated'",
          en.Origin == OverlapOrigin.Introduced && en.Why.Contains("enabled or relocated"), en.Why);
    var ihb = DetectorModel.OverlapProvenance(OF(without: true));
    var ihs = DetectorModel.OverlapProvenance(OF(without: true, seen: true));
    Check("ovl: overlap already there without the later plugin -> INHERITED, Blind or Seen by the closure alone",
          ihb.Origin == OverlapOrigin.Inherited && ihb.Sight == OverlapSight.Blind
       && ihs.Origin == OverlapOrigin.Inherited && ihs.Sight == OverlapSight.Seen, ihb.Why);
    Check("ovl: sight is decided by the closure fact alone, whatever the origin",
          DetectorModel.OverlapProvenance(OF(hadPrev: false, present: false, seen: true)).Sight == OverlapSight.Seen
       && DetectorModel.OverlapProvenance(OF(without: true, seen: false)).Sight == OverlapSight.Blind
       && DetectorModel.OverlapProvenance(OF(without: false, seen: false)).Sight == OverlapSight.Blind, "four quadrants");

    Check("ovl: population — both Bethesda -> BETHESDA FLOOR; USSEP on either side -> USSEP INVOLVED; else MOD-ON-MOD",
          DetectorModel.ClassifyOverlapPopulation(true, true, false, false) == OverlapPopulation.BethesdaFloor
       && DetectorModel.ClassifyOverlapPopulation(false, true, false, true) == OverlapPopulation.UssepInvolved
       && DetectorModel.ClassifyOverlapPopulation(true, false, true, false) == OverlapPopulation.UssepInvolved
       && DetectorModel.ClassifyOverlapPopulation(true, false, false, false) == OverlapPopulation.ModOnMod
       && DetectorModel.ClassifyOverlapPopulation(false, false, false, false) == OverlapPopulation.ModOnMod, "rule 12");
    Check("ovl: scale — 256u and up architecture, 64u to 256u furniture, under 64u clutter",
          DetectorModel.ClassifyOverlapScale(256f) == OverlapScale.Architecture
       && DetectorModel.ClassifyOverlapScale(255.9f) == OverlapScale.Furniture
       && DetectorModel.ClassifyOverlapScale(64f) == OverlapScale.Furniture
       && DetectorModel.ClassifyOverlapScale(63.9f) == OverlapScale.Clutter, "boundaries inclusive at the top");

    // ---- The synthetic order: five plugins written with Mutagen, read back through OverlapIndex. ----
    // Load order M, X, B, A, Y. X, B, A and Y each master M only — so nothing X, B or Y did is in A's
    // closure, and nothing A did is in Y's. Every base is one 64u crate (OBND ±32, z 0..64).
    var root = Path.Combine(Path.GetTempPath(), "proofprobe-ovl-" + Environment.ProcessId);
    Directory.CreateDirectory(root);
    var release = SkyrimRelease.SkyrimSE;
    var mKey = new ModKey("OvlMaster", ModType.Master);
    var xKey = new ModKey("OvlX", ModType.Plugin);
    var bKey = new ModKey("OvlB", ModType.Plugin);
    var aKey = new ModKey("OvlA", ModType.Plugin);
    var yKey = new ModKey("OvlY", ModType.Plugin);
    FormKey M(uint id) => new(mKey, id);
    var crateK = M(0x801); var markerK = M(0x802); var cell1K = M(0x810); var cell2K = M(0x811);
    FormKey R(int n) => M((uint)(0x900 + n));

    static void FileInterior(SkyrimMod mod, Cell cell)
    {
        uint id = cell.FormKey.ID;
        int blockN = (int)(id % 10), subN = (int)((id / 10) % 10);
        var records = mod.Cells.Records;
        var block = records.FirstOrDefault(b => b.BlockNumber == blockN);
        if (block is null) { block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock }; records.Add(block); }
        var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subN);
        if (sub is null) { sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock }; block.SubBlocks.Add(sub); }
        sub.Cells.Add(cell);
    }
    static PlacedObject Ref(FormKey key, FormKey baseKey, float x, float y = 0, float z = 0, string? edid = null, bool disabled = false, float rotZ = 0)
    {
        var r = new PlacedObject(key, SkyrimRelease.SkyrimSE) { EditorID = edid, Placement = new Placement { Position = new P3Float(x, y, z), Rotation = new P3Float(0, 0, rotZ) } };
        r.Base.SetTo(baseKey);
        if (disabled) r.SkyrimMajorRecordFlags |= SkyrimMajorRecord.SkyrimMajorRecordFlag.InitiallyDisabled;
        return r;
    }
    static Cell Interior(FormKey key, string? edid = null) => new(key, SkyrimRelease.SkyrimSE) { EditorID = edid, Flags = Cell.Flag.IsInteriorCell };
    string Write(SkyrimMod mod, params ISkyrimModGetter[] masters)
    {
        var path = Path.Combine(root, mod.ModKey.FileName.String);
        mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
        return path;
    }

    // THE MASTER: the crate, an editor marker, two interiors, and the placements each case starts from.
    var master = new SkyrimMod(mKey, release);
    master.Statics.Add(new Static(crateK, release) { EditorID = "OvlCrate", ObjectBounds = new ObjectBounds { First = new P3Int16(-32, -32, 0), Second = new P3Int16(32, 32, 64) } });
    master.Statics.Add(new Static(markerK, release) { EditorID = "XMarkerHeading", ObjectBounds = new ObjectBounds { First = new P3Int16(-32, -32, 0), Second = new P3Int16(32, 32, 64) } });
    var m1 = Interior(cell1K, "OvlCell1"); var m2 = Interior(cell2K, "OvlCell2");
    m1.Temporary.Add(Ref(R(1), crateK, 0, edid: "a1"));                       // X will move it to 100; A places b1 into it
    m1.Temporary.Add(Ref(R(2), crateK, 1000, edid: "a2"));                    // A places b2 into it (M is in A's closure)
    m1.Temporary.Add(Ref(R(3), crateK, 2000, edid: "a3"));                    // a3/b3 overlap in M; A nudges b3 (still overlapping)
    m1.Temporary.Add(Ref(R(4), crateK, 2010, edid: "b3"));
    m1.Temporary.Add(Ref(R(6), crateK, 3000, edid: "a6"));                    // a6/b6 overlap in M; X moves a6 5u; A nudges b6 (still overlapping)
    m1.Temporary.Add(Ref(R(7), crateK, 3010, edid: "b6"));
    m1.Temporary.Add(Ref(R(8), crateK, 4000, edid: "a7"));                    // a7/b7 overlap in M; A moves b7 OUT — the fix
    m1.Temporary.Add(Ref(R(9), crateK, 4010, edid: "b7"));
    m1.Temporary.Add(Ref(R(10), crateK, 5000, edid: "a8"));                   // B places b8 at 5500; A moves a8 INTO it
    m1.Temporary.Add(Ref(R(11), crateK, 9000, edid: "a11", disabled: true));  // X places b11 on it; A ENABLES a11
    m2.Temporary.Add(Ref(R(12), crateK, 0, edid: "a12"));                     // in cell 2; X places b12 in cell 1; A RELOCATES a12 onto it
    m1.Temporary.Add(Ref(R(13), crateK, 11000, edid: "a13"));                 // A overrides with a rotation only — a silent re-placement
    FileInterior(master, m1); FileInterior(master, m2);
    var mPath = Write(master);
    using var mOv = SkyrimMod.CreateFromBinaryOverlay(mPath, release);

    // X (masters M): moves a1 and a6, places b11 and b12.
    var x = new SkyrimMod(xKey, release);
    var x1 = Interior(cell1K);
    x1.Temporary.Add(Ref(R(1), crateK, 100, edid: "a1"));
    x1.Temporary.Add(Ref(R(6), crateK, 3005, edid: "a6"));
    x1.Temporary.Add(Ref(new FormKey(xKey, 0x900), crateK, 9000, edid: "b11"));
    x1.Temporary.Add(Ref(new FormKey(xKey, 0x901), crateK, 10000, edid: "b12"));
    FileInterior(x, x1);
    var xPath = Write(x, mOv);

    // B (masters M): places b8 — B loads BEFORE A and is outside A's closure.
    var b = new SkyrimMod(bKey, release);
    var b1 = Interior(cell1K);
    b1.Temporary.Add(Ref(new FormKey(bKey, 0x900), crateK, 5500, edid: "b8"));
    FileInterior(b, b1);
    var bPath = Write(b, mOv);

    // A (masters M): the later plugin in every case but the last.
    var a = new SkyrimMod(aKey, release);
    var a1 = Interior(cell1K);
    a1.Temporary.Add(Ref(new FormKey(aKey, 0x900), crateK, 100, edid: "b1"));         // new ref into a1@X       -> Introduced + Blind
    a1.Temporary.Add(Ref(new FormKey(aKey, 0x901), crateK, 1000, edid: "b2"));        // new ref into a2@M       -> Introduced + Seen
    a1.Temporary.Add(Ref(R(4), crateK, 2015, edid: "b3"));                            // nudge, still in a3@M    -> Inherited + Seen
    a1.Temporary.Add(Ref(R(7), crateK, 3020, edid: "b6"));                            // nudge, still in a6@X    -> Inherited + Blind
    a1.Temporary.Add(Ref(R(9), crateK, 4500, edid: "b7"));                            // moved OUT of a7         -> no hit
    a1.Temporary.Add(Ref(R(10), crateK, 5500, edid: "a8"));                           // moved INTO b8@B         -> Introduced + Blind, says 'moved'
    a1.Temporary.Add(Ref(new FormKey(aKey, 0x902), crateK, 7000, edid: "b10"));       // Y will place a10 into it -> Introduced + Blind with L = Y
    a1.Temporary.Add(Ref(new FormKey(aKey, 0x903), crateK, 8000, edid: "c1"));        // A's own two crates overlapping -> the floor
    a1.Temporary.Add(Ref(new FormKey(aKey, 0x904), crateK, 8010, edid: "c2"));
    a1.Temporary.Add(Ref(R(11), crateK, 9000, edid: "a11"));                          // ENABLED onto b11@X      -> Introduced + Blind, 'enabled or relocated'
    a1.Temporary.Add(Ref(R(12), crateK, 10000, edid: "a12"));                         // RELOCATED onto b12@X    -> Introduced + Blind, 'enabled or relocated'
    a1.Temporary.Add(Ref(R(13), crateK, 11000, edid: "a13", rotZ: 1.0f));             // rotation only           -> silent, not responsible
    a1.Temporary.Add(Ref(new FormKey(aKey, 0x905), markerK, 2000, edid: "mk1"));      // an editor marker into a3 -> set aside, never a candidate
    FileInterior(a, a1);
    var aPath = Write(a, mOv);

    // Y (masters M, loads after A, outside its closure): places a10 into A's b10.
    var y = new SkyrimMod(yKey, release);
    var y1 = Interior(cell1K);
    y1.Temporary.Add(Ref(new FormKey(yKey, 0x900), crateK, 7000, edid: "a10"));
    FileInterior(y, y1);
    var yPath = Write(y, mOv);

    // Read the order back the way overlapprobe does.
    var paths = new[] { mPath, xPath, bPath, aPath, yPath };
    var idx = new OverlapIndex();
    var keys = new ModKey[paths.Length];
    var masterIdx = new HashSet<int>[paths.Length];
    for (int p = 0; p < paths.Length; p++)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], release);
        keys[p] = mod.ModKey;
        var names = mod.ModHeader.MasterReferences.Select(q => q.Master).ToHashSet();
        masterIdx[p] = Enumerable.Range(0, p).Where(q => names.Contains(keys[q])).ToHashSet();
        idx.Ingest(p, mod);
    }
    var closure = DetectorModel.MasterClosure(masterIdx);
    const int Mi = 0, Xi = 1, Bi = 2, Ai = 3, Yi = 4;
    Check("ovl-synth: the written order has the declared masters it was built to have (everyone masters M only)",
          masterIdx[Xi].SetEquals(new[] { Mi }) && masterIdx[Bi].SetEquals(new[] { Mi }) && masterIdx[Ai].SetEquals(new[] { Mi }) && masterIdx[Yi].SetEquals(new[] { Mi }),
          $"X<-[{string.Join(",", masterIdx[Xi])}] B<-[{string.Join(",", masterIdx[Bi])}] A<-[{string.Join(",", masterIdx[Ai])}] Y<-[{string.Join(",", masterIdx[Yi])}]");

    (Aabb, char) BoxOf(FormKey k) { var bx = idx.ObndBox(k); return bx.IsEmpty ? (Aabb.Empty, '-') : (bx, 'o'); }
    var obstructing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Static" };
    var cands = idx.Candidates(BoxOf, obstructing, _ => false, 1024f, out var skips);
    Check("ovl-synth: the editor marker is set aside, counted, and nothing else is",
          skips.MarkerMesh == 1 && skips.NoGeometry == 0 && skips.NonObstructingType == 0 && skips.Disabled == 0,
          $"markers {skips.MarkerMesh}, no geometry {skips.NoGeometry}, disabled {skips.Disabled} (a11 was enabled by A)");
    Check("ovl-synth: one silent re-placement — A's rotation-only override of a13",
          idx.SilentRePlacements == 1 && idx.History.Winning(R(13))!.Value.Plugin == Mi, $"silent {idx.SilentRePlacements}, a13 responsible = plugin {idx.History.Winning(R(13))!.Value.Plugin}");
    var sweep = OverlapIndex.Pairs(cands, closure, 16f, 0.25f);
    var rows = sweep.CrossDeep.Select(h => idx.Judge(h, closure, BoxOf, 16f, 0.25f)).ToList();
    OverlapRow? Row(FormKey p, FormKey q) => rows.FirstOrDefault(r => (r.Hit.A.Key == p && r.Hit.B.Key == q) || (r.Hit.A.Key == q && r.Hit.B.Key == p));
    string W(OverlapRow? r) => r is null ? "(no row)" : $"{r.Origin} + {r.Sight}, later = plugin {r.Later}: {r.Why}";
    FormKey Xk(uint id) => new(xKey, id); FormKey Bk(uint id) => new(bKey, id); FormKey Ak(uint id) => new(aKey, id); FormKey Yk(uint id) => new(yKey, id);

    var r1 = Row(R(1), Ak(0x900));
    Check("ovl-synth: A places b1 into a1 that X moved (X outside A's closure) -> INTRODUCED + BLIND, L = A",
          r1 is { Origin: OverlapOrigin.Introduced, Sight: OverlapSight.Blind, Later: Ai } && !r1.Facts.LaterHadPreviousPlacement, W(r1));
    var r2 = Row(R(2), Ak(0x901));
    Check("ovl-synth: A places b2 into a2 where M left it (M in A's closure) -> INTRODUCED + SEEN", r2 is { Origin: OverlapOrigin.Introduced, Sight: OverlapSight.Seen, Later: Ai }, W(r2));
    var r3 = Row(R(3), R(4));
    Check("ovl-synth: M placed a3/b3 overlapping, A nudges b3 -> INHERITED + SEEN (the intersection pre-dates A)",
          r3 is { Origin: OverlapOrigin.Inherited, Sight: OverlapSight.Seen, Later: Ai } && r3.Facts.WithoutLaterOverlaps && r3.WithoutDepth > 16f, W(r3));
    var r6 = Row(R(6), R(7));
    Check("ovl-synth: M placed a6/b6 overlapping, X moved a6, A nudges b6 -> INHERITED + BLIND", r6 is { Origin: OverlapOrigin.Inherited, Sight: OverlapSight.Blind, Later: Ai }, W(r6));
    Check("ovl-synth: A moves b7 OUT of an inherited overlap -> no hit at all (the differential rewards a fix)",
          Row(R(8), R(9)) is null && !sweep.CrossDeep.Any(h => h.A.Key == R(9) || h.B.Key == R(9)), "b7 appears in no cross hit");
    var r8 = Row(R(10), Bk(0x900));
    Check("ovl-synth: A moves a8 INTO B's b8 (B outside A's closure) -> INTRODUCED + BLIND with L = A, says 'moved'",
          r8 is { Origin: OverlapOrigin.Introduced, Sight: OverlapSight.Blind, Later: Ai } && r8.Why.Contains("moved") && r8.LaterRef.Key == R(10) && r8.WithoutDepth == 0f, W(r8));
    var r10 = Row(Ak(0x902), Yk(0x900));
    Check("ovl-synth: Y (loads after A, outside its closure) places a10 into A's b10 -> INTRODUCED + BLIND with L = Y, Y's ref is the later ref",
          r10 is { Origin: OverlapOrigin.Introduced, Sight: OverlapSight.Blind, Later: Yi } && r10.LaterRef.Key == Yk(0x900), W(r10));
    var r11 = Row(R(11), Xk(0x900));
    Check("ovl-synth: A ENABLES a11 under X's b11 -> INTRODUCED + BLIND, 'enabled or relocated' (the disabled snap is not a placement)",
          r11 is { Origin: OverlapOrigin.Introduced, Sight: OverlapSight.Blind, Later: Ai } && r11.Facts.LaterHadPreviousPlacement && !r11.Facts.PreviousPlacementPresent && r11.Why.Contains("enabled or relocated"), W(r11));
    var r12 = Row(R(12), Xk(0x901));
    Check("ovl-synth: A RELOCATES a12 from cell 2 onto X's b12 in cell 1 -> INTRODUCED + BLIND, 'enabled or relocated' (another space is no box)",
          r12 is { Origin: OverlapOrigin.Introduced, Sight: OverlapSight.Blind, Later: Ai } && !r12.Facts.PreviousPlacementPresent && r12.LaterPrevious!.Value.Cell == cell2K, W(r12));
    Check("ovl-synth: A's own two crates are the FLOOR — a same-responsible deep pair, never judged",
          sweep.SameDeep == 1 && !rows.Any(r => r.Hit.A.Key == Ak(0x903) || r.Hit.B.Key == Ak(0x903)), $"same-responsible deep {sweep.SameDeep}");
    Check("ovl-synth: the marker A dropped into a3 is in no hit (set aside before the sweep)",
          !sweep.CrossDeep.Any(h => h.A.Key == Ak(0x905) || h.B.Key == Ak(0x905)) && sweep.SameDeep == 1, "mk1 absent");
    Check("ovl-synth: plumbing — SEEN rows equal the declared-dependency deep count, and every Blind row is undeclared",
          rows.Count(r => r.Sight == OverlapSight.Seen) == sweep.CrossDeclaredDeep && rows.Where(r => r.Sight == OverlapSight.Blind).All(r => !r.Hit.Declared),
          $"seen {rows.Count(r => r.Sight == OverlapSight.Seen)}, declared deep {sweep.CrossDeclaredDeep}");
    Check("ovl-synth: the whole population — 8 cross-responsible deep hits, no more",
          rows.Count == 8, $"rows {rows.Count}: " + string.Join(" | ", rows.Select(r => $"{idx.BaseInfo.GetValueOrDefault(r.LaterRef.Base).Edid}? {r.LaterRef.Key}/{r.OtherRef.Key} {r.Origin}+{r.Sight}")));
    Check("ovl-synth: the ranker reads the buried crate as furniture-scale (64u), and depth × fraction orders the nudge below the full burial",
          r1!.Scale == OverlapScale.Furniture && r1.Rank > r3!.Rank, $"r1 rank {r1.Rank:0} ({r1.Scale}), r3 rank {r3.Rank:0}");

    try { Directory.Delete(root, true); } catch { }
}

// ---------------------------------------------------------------------------------------
// The synthetic load order — the thing everything else will now be validated against.
// ---------------------------------------------------------------------------------------
// ---------------------------------------------------------------------------------------
// Placement contact — the geometry half of 4.5 / 4.6 (charter milestone 8 over milestone 6's primitive).
// The rule (DetectorModel.PlacementContact, ClassifyMotion) at fact level, then a synthetic four-plugin
// order written with Mutagen and read back through ContactIndex over OverlapIndex — the same ingest,
// host census, two hashes, arm construction and contact primitive the live probe uses, with OBND boxes
// turned into twelve-triangle box soups standing in for meshes (labelled 'o' either way).
// ---------------------------------------------------------------------------------------
Console.WriteLine("\nPROOF — placement contact (_shared/DetectorModel.cs + _shared/ContactIndex.cs)\n");
{
    static DetectorModel.ContactFacts CF(bool depGeom = true, bool hostGeom = true, bool authMeasured = true,
        float aDist = 0f, float aPen = 0f, float aUp = 1f,
        bool deleted = false, bool disabled = false, bool moverSide = false,
        bool winMeasured = true, float wDist = 128f, float wPen = 0f, bool wHost = false, DependentMotion motion = DependentMotion.Fixed,
        bool viaParent = false, bool unresolved = false, float wExtent = float.NaN)
        => new(depGeom, hostGeom, authMeasured, aDist, aPen, aUp, deleted, disabled, moverSide, winMeasured, wDist, wPen, wHost, motion, viaParent, unresolved, wExtent);
    static string W((ContactRelation Relation, ContactVerdict Verdict, ContactBlocker Reason, string Why) r) => $"{r.Relation} / {r.Verdict} / {r.Reason}: {r.Why}";

    var s1 = DetectorModel.PlacementContact(CF());
    Check("contact: rested on the host, nothing under it now -> SUPPORTED / STRANDED", s1 is { Relation: ContactRelation.Supported, Verdict: ContactVerdict.Stranded }, W(s1));
    var at1 = DetectorModel.PlacementContact(CF(aUp: 0f));
    Check("contact: mounted against the host (normal horizontal), nothing near it now -> ATTACHED / ORPHANED", at1 is { Relation: ContactRelation.Attached, Verdict: ContactVerdict.Orphaned }, W(at1));
    var h1 = DetectorModel.PlacementContact(CF(aUp: -1f));
    Check("contact: hung from the host (normal down) -> HUNG / ORPHANED", h1 is { Relation: ContactRelation.Hung, Verdict: ContactVerdict.Orphaned }, W(h1));
    var sl = DetectorModel.PlacementContact(CF(aUp: 0.7f));
    Check("contact: contact on a 45° slope -> no relationship (between the bands)", sl is { Relation: ContactRelation.None, Verdict: ContactVerdict.NoRelation }, W(sl));
    var fl = DetectorModel.PlacementContact(CF(aDist: 10f));
    Check("contact: 10 u apart as authored -> no relationship", fl is { Verdict: ContactVerdict.NoRelation } && fl.Why.Contains("not in contact"), W(fl));
    var sk = DetectorModel.PlacementContact(CF(aPen: 10f));
    Check("contact: already sunk 10 u as authored -> no relationship (the absolute floor's business)", sk is { Verdict: ContactVerdict.NoRelation } && sk.Why.Contains("already sunk"), W(sk));
    var e4 = DetectorModel.PlacementContact(CF(aDist: 4f, aPen: 4f));
    var e41 = DetectorModel.PlacementContact(CF(aDist: 4.1f));
    Check("contact: ε is inclusive — 4 u apart / 4 u sunk still rests; 4.1 u apart does not", e4.Verdict == ContactVerdict.Stranded && e41.Verdict == ContactVerdict.NoRelation, $"{e4.Verdict} / {e41.Verdict}");
    var nn = DetectorModel.PlacementContact(CF(aUp: float.NaN));
    Check("contact: in contact but no readable normal -> no relationship, says so", nn.Verdict == ContactVerdict.NoRelation && nn.Why.Contains("normal"), W(nn));

    var su = DetectorModel.PlacementContact(CF(wDist: 0f, wPen: 10f));
    Check("contact: rested, and 10 u inside something under the winner -> SUNK", su.Verdict == ContactVerdict.Sunk, W(su));
    var rs = DetectorModel.PlacementContact(CF(wDist: 2f));
    Check("contact: rested, and touching another reference under the winner -> RE-SUPPORTED, names 'another physical reference'", rs.Verdict == ContactVerdict.ReSupported && rs.Why.Contains("another physical reference"), W(rs));
    var rh = DetectorModel.PlacementContact(CF(wDist: 0f, wHost: true));
    Check("contact: rested, still touching the host at its new place -> RE-SUPPORTED, names the host", rh.Verdict == ContactVerdict.ReSupported && rh.Why.Contains("host at its new place"), W(rh));
    var sh = DetectorModel.PlacementContact(CF(wDist: 0f, wPen: 20f, wHost: true));
    Check("contact: rested, the host moved up into it -> SUNK, names the host", sh.Verdict == ContactVerdict.Sunk && sh.Why.Contains("host at its new place"), W(sh));
    var de = DetectorModel.PlacementContact(CF(deleted: true, wDist: 0f, wPen: 20f));
    var di = DetectorModel.PlacementContact(CF(disabled: true));
    Check("contact: dependent deleted or disabled under the winner -> REMOVED, whatever the winner geometry says", de.Verdict == ContactVerdict.Removed && di.Verdict == ContactVerdict.Removed, $"{de.Verdict} / {di.Verdict}");
    var mt = DetectorModel.PlacementContact(CF(moverSide: true, wDist: 128f));
    Check("contact: re-placed by the mover's side -> MOVED TOGETHER, even though it touches nothing now", mt.Verdict == ContactVerdict.MovedTogether, W(mt));
    var mv = DetectorModel.PlacementContact(CF(motion: DependentMotion.Movable));
    Check("contact: a MOVABLE dependent reads the same verdict, and the why says physics will drop it", mv.Verdict == ContactVerdict.Stranded && mv.Why.Contains("MOVABLE"), W(mv));
    var ng = DetectorModel.PlacementContact(CF(depGeom: false, hostGeom: false));
    var nh = DetectorModel.PlacementContact(CF(hostGeom: false));
    var na = DetectorModel.PlacementContact(CF(authMeasured: false));
    var nw = DetectorModel.PlacementContact(CF(winMeasured: false));
    Check("contact: no geometry / no authoring arm / no winner arm -> UNJUDGEABLE by reason, dependent outranking host",
          ng is { Verdict: ContactVerdict.Unjudgeable, Reason: ContactBlocker.NoDependentGeometry } && nh.Reason == ContactBlocker.NoHostGeometry
       && na.Reason == ContactBlocker.AuthoringArmMissing && nw is { Verdict: ContactVerdict.Unjudgeable, Reason: ContactBlocker.WinnerArmMissing, Relation: ContactRelation.Supported },
          $"{ng.Reason} / {nh.Reason} / {na.Reason} / {nw.Reason}");
    var lo = DetectorModel.PlacementContact(CF(aDist: 3f, aUp: 0.9f), epsilon: 2f);
    var lo2 = DetectorModel.PlacementContact(CF(aDist: 3f, aUp: 0.9f), epsilon: 2f, supportCos: 0.95f);
    Check("contact: ε and the angle bands are parameters — 3 u apart fails ε = 2; up 0.9 fails a 0.95 support band", lo.Verdict == ContactVerdict.NoRelation && lo2.Verdict == ContactVerdict.NoRelation, $"{lo.Verdict} / {lo2.Verdict}");

    // The 2026-09-03 folds at fact level (R1, R4). R2 and R3 are index rules, proven on the synthetic order below.
    var rd = DetectorModel.PlacementContact(CF(disabled: true, viaParent: true, wDist: 0f, wPen: 20f));
    Check("contact R1: dependent disabled at runtime THROUGH its enable parent under the winner -> UNJUDGEABLE / RuntimeDisabled, whatever the winner geometry says (absent, not a defect)",
          rd is { Verdict: ContactVerdict.Unjudgeable, Reason: ContactBlocker.RuntimeDisabled, Relation: ContactRelation.Supported } && rd.Why.Contains("absent"), W(rd));
    var rf = DetectorModel.PlacementContact(CF(disabled: true, viaParent: false));
    Check("contact R1: disabled by its OWN flag (no parent) stays REMOVED — the −30000 idiom is the fix", rf.Verdict == ContactVerdict.Removed && rf.Reason == ContactBlocker.None, W(rf));
    var ru = DetectorModel.PlacementContact(CF(unresolved: true));
    var ru2 = DetectorModel.PlacementContact(CF(disabled: true, viaParent: true, unresolved: true));
    Check("contact R1: an enable chain the order cannot follow -> UNJUDGEABLE / EnableChainUnresolved, and it outranks the runtime-disabled reading it could not make",
          ru is { Verdict: ContactVerdict.Unjudgeable, Reason: ContactBlocker.EnableChainUnresolved } && ru2.Reason == ContactBlocker.EnableChainUnresolved, $"{W(ru)} | {W(ru2)}");
    var rpe = DetectorModel.PlacementContact(CF(deleted: true, unresolved: true, disabled: true, viaParent: true));
    Check("contact R1: a deleted dependent is REMOVED before any enable reading is consulted", rpe.Verdict == ContactVerdict.Removed, W(rpe));
    var rj = DetectorModel.PlacementContact(CF(wDist: 0f, wPen: 100f, wExtent: 30f));
    var rw = DetectorModel.PlacementContact(CF(wDist: 0f, wPen: 100f, wExtent: 300f));
    var rn = DetectorModel.PlacementContact(CF(wDist: 0f, wPen: 100f));
    Check("contact R4: 100 u inside a thing 30 u across along the contact normal -> UNJUDGEABLE / DepthExceedsObject, the depth kept in the facts; the same 100 u into a 300 u wall -> SUNK; no extent read -> SUNK as before",
          rj is { Verdict: ContactVerdict.Unjudgeable, Reason: ContactBlocker.DepthExceedsObject } && rj.Why.Contains("refused") && rw.Verdict == ContactVerdict.Sunk && rn.Verdict == ContactVerdict.Sunk,
          $"{W(rj)} | {rw.Verdict} | {rn.Verdict}");
    var rj2 = DetectorModel.PlacementContact(CF(wDist: 0f, wPen: 3f, wExtent: 2f));
    var rj3 = DetectorModel.PlacementContact(CF(wDist: 0f, wPen: 30f, wExtent: 30f));
    Check("contact R4: the extent test applies only past ε (3 u into a 2 u sliver is still touching at ε = 4), and a depth EQUAL to the extent is not refused",
          rj2.Verdict == ContactVerdict.ReSupported && rj3.Verdict == ContactVerdict.Sunk, $"{rj2.Verdict} / {rj3.Verdict}");

    Check("classify R2: the Is-Marker flag is the rule when the caller read it — a Furniture with the flag and an ordinary name is a Marker host / Control dependent; a Static named XMarkerHeading WITHOUT the flag is Physical; a caller passing no flag keeps the name heuristic",
          DetectorModel.ClassifyHost("Furniture", "XJKCoWCommonChair01F", markerFlag: true) == HostClass.Marker
       && DetectorModel.ClassifyDependent("PlacedObject", "Furniture", "XJKCoWCommonChair01F", markerFlag: true) == DependentClass.Control
       && DetectorModel.ClassifyHost("Static", "XMarkerHeading", markerFlag: false) == HostClass.Physical
       && DetectorModel.ClassifyDependent("PlacedObject", "Static", "XMarkerHeading", markerFlag: false) == DependentClass.Physical
       && DetectorModel.ClassifyHost("Static", "XMarkerHeading") == HostClass.Marker
       && DetectorModel.ClassifyDependent("PlacedObject", "Activator", "MGR02FocalPointTrigger", markerFlag: false) == DependentClass.Control, "flag over name; trigger/FX names untouched");

    // The shared enable-state resolver (housecarl-core, moved out of viewprobe 2026-09-03): the chain rule at fact level.
    {
        var pk = new ModKey("Ct", ModType.Plugin);
        FormKey K(uint id) => new(pk, id);
        var facts = new Dictionary<FormKey, EnableFacts>
        {
            [K(1)] = new(false, null, false),                 // plain, enabled
            [K(2)] = new(true, null, false),                  // plain, disabled
            [K(3)] = new(true, K(1), false),                  // own flag disabled, parent enabled -> ENABLED (the flag is bookkeeping)
            [K(4)] = new(false, K(2), false),                 // own flag enabled, parent disabled -> DISABLED
            [K(5)] = new(false, K(2), true),                  // opposite of a disabled parent -> ENABLED
            [K(6)] = new(false, K(4), false),                 // a chain: 6 -> 4 -> 2 -> DISABLED
            [K(7)] = new(false, K(8), false), [K(8)] = new(false, K(7), false),   // a cycle
            [K(9)] = new(false, K(99), false),                // a parent nobody places
            [K(10)] = new(false, new FormKey(pk, 0x14), true), // parent = the player, opposite: permanently disabled
            [K(11)] = new(true, new FormKey(pk, 0x14), false), // parent = the player: always enabled despite the flag
        };
        EnableFacts? F(FormKey k) => facts.TryGetValue(k, out var f) ? f : null;
        var memo = new Dictionary<FormKey, EnableResolution>();
        EnableResolution E(uint id) => EnableState.Resolve(K(id), F, memo);
        Check("enable: no parent -> the flag; a parent -> the parent's state XOR opposite, the own flag ignored",
              E(1).Enabled && !E(2).Enabled && E(3) is { Enabled: true, ViaParent: true, Resolved: true } && E(4) is { Enabled: false, ViaParent: true } && E(5).Enabled && !E(6).Enabled,
              $"1 {E(1).Enabled} 2 {E(2).Enabled} 3 {E(3).Enabled} 4 {E(4).Enabled} 5 {E(5).Enabled} 6 {E(6).Enabled}");
        Check("enable: the player is always enabled — parent=player+opposite is permanently disabled, parent=player overrides a disabled flag",
              !E(10).Enabled && E(11).Enabled && E(10).Resolved, $"10 {E(10).Enabled} 11 {E(11).Enabled}");
        Check("enable: a cycle falls back to the own flag and reads UNRESOLVED; a parent nobody places reads enabled and UNRESOLVED",
              E(7) is { Enabled: true, Resolved: false } && E(9) is { Enabled: true, Resolved: false } && E(1).Resolved, $"7 {E(7)} 9 {E(9)}");
    }

    Check("motion: item types are MOVABLE by record type; statics, furniture, containers, doors, activators, trees, flora, lights FIXED",
          new[] { "MiscItem", "Ingredient", "Ingestible", "Book", "Weapon", "Armor", "Ammunition", "SoulGem", "Key", "Scroll", "LeveledItem" }.All(t => DetectorModel.ClassifyMotion(t, null) == DependentMotion.Movable)
       && new[] { "Static", "Furniture", "Container", "Door", "Activator", "Tree", "Flora", "Light" }.All(t => DetectorModel.ClassifyMotion(t, null) == DependentMotion.Fixed), "");
    Check("motion: a MoveableStatic defers to its bodies — DYNAMIC movable, FIXED / KEYFRAMED fixed, CLUTTER layer with no motion read movable, no bodies unknown",
          DetectorModel.ClassifyMotion("MoveableStatic", new[] { ("SKYL_STATIC", "MO_SYS_DYNAMIC") }) == DependentMotion.Movable
       && DetectorModel.ClassifyMotion("MoveableStatic", new[] { ("SKYL_CLUTTER", "MO_SYS_FIXED") }) == DependentMotion.Fixed
       && DetectorModel.ClassifyMotion("MoveableStatic", new[] { ("SKYL_STATIC", "MO_SYS_KEYFRAMED") }) == DependentMotion.Fixed
       && DetectorModel.ClassifyMotion("MoveableStatic", new[] { ("SKYL_CLUTTER", "") }) == DependentMotion.Movable
       && DetectorModel.ClassifyMotion("MoveableStatic", new[] { ("SKYL_STATIC", "") }) == DependentMotion.Fixed
       && DetectorModel.ClassifyMotion("MoveableStatic", Array.Empty<(string, string)>()) == DependentMotion.Unknown
       && DetectorModel.ClassifyMotion("MoveableStatic", null) == DependentMotion.Unknown
       && DetectorModel.ClassifyMotion(null, null) == DependentMotion.Unknown, "");

    // ---- The synthetic order: four plugins written with Mutagen, read back through ContactIndex. ----
    // Boxes: OverlapIndex.ObndBox centres the OBND extents on the origin, so a table with OBND z 0..64 is ±32 about
    // its placement and its top is at z + 32. Every placement below is written in that frame.
    var root = Path.Combine(Path.GetTempPath(), "proofprobe-ct-" + Environment.ProcessId);
    Directory.CreateDirectory(root);
    var release = SkyrimRelease.SkyrimSE;
    var mKey = new ModKey("CtMaster", ModType.Master);
    var aKey = new ModKey("CtA", ModType.Plugin);
    var bKey = new ModKey("CtB", ModType.Plugin);
    var cKey = new ModKey("CtC", ModType.Plugin);
    FormKey M(uint id) => new(mKey, id);
    var tableK = M(0x801); var tallK = M(0x802); var bowlK = M(0x803); var appleK = M(0x804); var wallK = M(0x805); var sconceK = M(0x806);
    var floorK = M(0x807); var markerK = M(0x808); var fxK = M(0x809); var cell1K = M(0x810); var cell2K = M(0x811);
    FormKey H(int n) => M((uint)(0x900 + n));     // hosts
    FormKey D(int n) => M((uint)(0xA00 + n));     // dependents
    var aStaticK = new FormKey(aKey, 0x801);
    const float TableTop = 32f, TallTop = 42f, BowlHalf = 5f, AppleHalf = 8f, WallHalfY = 10f, SconceHalfY = 6f;

    static void FileInterior(SkyrimMod mod, Cell cell)
    {
        uint id = cell.FormKey.ID;
        int blockN = (int)(id % 10), subN = (int)((id / 10) % 10);
        var records = mod.Cells.Records;
        var block = records.FirstOrDefault(b => b.BlockNumber == blockN);
        if (block is null) { block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock }; records.Add(block); }
        var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subN);
        if (sub is null) { sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock }; block.SubBlocks.Add(sub); }
        sub.Cells.Add(cell);
    }
    static PlacedObject Ref(FormKey key, FormKey baseKey, float x, float y = 0, float z = 0, string? edid = null, bool disabled = false,
                            FormKey? parent = null, bool opposite = false)
    {
        var r = new PlacedObject(key, SkyrimRelease.SkyrimSE) { EditorID = edid, Placement = new Placement { Position = new P3Float(x, y, z), Rotation = new P3Float(0, 0, 0) } };
        r.Base.SetTo(baseKey);
        if (disabled) r.SkyrimMajorRecordFlags |= SkyrimMajorRecord.SkyrimMajorRecordFlag.InitiallyDisabled;
        if (parent is { } pk)
        {
            r.EnableParent = new EnableParent { Flags = opposite ? EnableParent.Flag.SetEnableStateToOppositeOfParent : 0 };
            r.EnableParent.Reference.SetTo(pk);
        }
        return r;
    }
    static Cell Interior(FormKey key, string? edid = null) => new(key, SkyrimRelease.SkyrimSE) { EditorID = edid, Flags = Cell.Flag.IsInteriorCell };
    static Static Stat(FormKey k, string edid, int x, int y, int z, bool marker = false, string? mesh = null) => new(k, SkyrimRelease.SkyrimSE)
        { EditorID = edid, Model = new Model { File = mesh ?? $"meshes\\ct\\{edid}.nif" }, ObjectBounds = new ObjectBounds { First = new P3Int16((short)-x, (short)-y, 0), Second = new P3Int16((short)x, (short)y, (short)z) },
          MajorRecordFlagsRaw = marker ? (int)EnableState.IsMarkerFlag : 0 };
    static Furniture Furn(FormKey k, string edid, int x, int y, int z, bool marker, string mesh) => new(k, SkyrimRelease.SkyrimSE)
        { EditorID = edid, Model = new Model { File = mesh }, ObjectBounds = new ObjectBounds { First = new P3Int16((short)-x, (short)-y, 0), Second = new P3Int16((short)x, (short)y, (short)z) },
          MajorRecordFlagsRaw = marker ? (int)EnableState.IsMarkerFlag : 0 };
    string Write(SkyrimMod mod, params ISkyrimModGetter[] masters)
    {
        var path = Path.Combine(root, mod.ModKey.FileName.String);
        mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
        return path;
    }

    // THE MASTER: the bases, two interiors, a worldspace, and the arrangement each case starts from. Host n at x = 1000 n.
    var master = new SkyrimMod(mKey, release);
    master.Statics.Add(Stat(tableK, "CtTable", 100, 100, 64));                 // furniture-scale host, 200 x 200 x 64
    master.Statics.Add(Stat(tallK, "CtTableTall", 100, 100, 84));              // the same table, 20 u taller (a base swap)
    master.Statics.Add(Stat(bowlK, "CtBowl", 12, 12, 10));                     // a fixed clutter dependent
    master.MiscItems.Add(new MiscItem(appleK, release) { EditorID = "CtApple", Model = new Model { File = "meshes\\ct\\CtApple.nif" }, ObjectBounds = new ObjectBounds { First = new P3Int16(-8, -8, 0), Second = new P3Int16(8, 8, 16) } });
    master.Statics.Add(Stat(wallK, "CtWall", 200, 10, 300));                   // architecture-scale host, its faces at y = ±10
    master.Statics.Add(Stat(sconceK, "CtSconce", 8, 6, 16));                   // a fixed fixture, 12 u deep
    master.Statics.Add(Stat(floorK, "CtFloor", 500, 500, 20));                 // architecture-scale, 1000 across
    master.Statics.Add(Stat(markerK, "XMarkerHeading", 100, 100, 64));         // a marker the size of a table
    master.Statics.Add(Stat(fxK, "FXSmokeCt", 12, 12, 10));                    // FX by editor id, bowl-sized
    var poleK = M(0x80A);
    master.Statics.Add(Stat(poleK, "CtPole", 4, 4, 8));                         // an 8 u post — nothing rests on it
    // The 2026-09-03 folds' bases. A switch is an enable marker: the Is-Marker flag, a mesh, a tiny box. The sit-chair is
    // JK's shape — Furniture, an ordinary chair mesh, the flag set. The stand is the reverse — "Marker" in its name, no
    // flag. The shell is a whole-tower piece over the moved-host cap. The jar is a 30 u box the proof's geometry
    // resolver turns INSIDE OUT (the inverted-sliver artefact R4 refuses); the block is a 300 u solid a case can be
    // honestly inside of.
    var switchK = M(0x80B); var sitK = M(0x80C); var standK = M(0x80D); var shellK = M(0x80E); var jarK = M(0x80F); var caseK = M(0x812); var blockK = M(0x813);
    master.Statics.Add(Stat(switchK, "CtSwitch", 2, 2, 4, marker: true));
    master.Furniture.Add(Furn(sitK, "CtSitChair", 15, 15, 40, marker: true, mesh: "meshes\\ct\\CommonChair01.nif"));
    master.Statics.Add(Stat(standK, "CtBowlMarkerStand", 12, 12, 10));
    master.Statics.Add(Stat(shellK, "CtShell", 2600, 2600, 64));
    master.Statics.Add(Stat(jarK, "CtJarInverted", 15, 15, 30));
    master.Statics.Add(Stat(caseK, "CtCase", 30, 30, 200));
    master.Statics.Add(Stat(blockK, "CtBlock", 150, 150, 300));
    const float CaseHalf = 100f;
    FormKey S(int n) => M((uint)(0xB00 + n));     // switches (enable parents)
    var m1 = Interior(cell1K, "CtCell1"); var m2 = Interior(cell2K, "CtCell2");
    foreach (int n in new[] { 1, 2, 3, 4, 5, 6, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 31, 32, 33, 34, 35, 40, 41, 60, 70 })
        m1.Temporary.Add(Ref(H(n), tableK, 1000f * n, edid: $"Table{n}"));
    m1.Temporary.Add(Ref(H(7), wallK, 7000, 0, 150, "Wall7"));
    m1.Temporary.Add(Ref(H(17), markerK, 17000, edid: "Marker17"));
    m1.Temporary.Add(Ref(H(21), floorK, 21000, 0, -20, "Floor21"));
    foreach (int n in new[] { 1, 3, 4, 5, 6, 10, 11, 12, 13, 14, 15, 16, 17, 30, 32, 60, 70 })
        m1.Temporary.Add(Ref(D(n), bowlK, 1000f * n, 0, TableTop + BowlHalf, $"Bowl{n}"));           // resting on its table
    // R1: Table30 follows Switch30 (enabled in M; A will disable the switch, never touching the table). Table36 follows
    // Switch36, disabled from birth — a host that never exists at runtime. Bowl31 follows Switch31 (A disables it).
    // Bowl33 is the OPPOSITE of Switch33 (disabled from birth -> the bowl is enabled); Bowl34 the opposite of Switch34
    // (enabled -> the bowl never exists).
    foreach (int n in new[] { 30, 31, 33, 34 }) m1.Temporary.Add(Ref(S(n), switchK, 1000f * n, 500, 0, $"Switch{n}", disabled: n == 33));
    m1.Temporary.Add(Ref(S(36), switchK, 36000, 500, 0, "Switch36", disabled: true));
    m1.Temporary.Add(Ref(H(30), tableK, 30000, edid: "Table30", parent: S(30)));
    m1.Temporary.Add(Ref(H(36), tableK, 36000, edid: "Table36", parent: S(36)));
    m1.Temporary.Add(Ref(D(31), bowlK, 31000, 0, TableTop + BowlHalf, "Bowl31", parent: S(31)));
    m1.Temporary.Add(Ref(D(33), bowlK, 33000, 0, TableTop + BowlHalf, "Bowl33", parent: S(33), opposite: true));
    m1.Temporary.Add(Ref(D(34), bowlK, 34000, 0, TableTop + BowlHalf, "Bowl34", parent: S(34), opposite: true));
    m1.Temporary.Add(Ref(D(36), bowlK, 36000, 0, TableTop + BowlHalf, "Bowl36"));
    // R2: a flagged sit-chair on Table35 (excluded on the flag); the name-only stand on Table37 (admitted).
    m1.Temporary.Add(Ref(D(35), sitK, 35000, 0, TableTop + 20f, "Sit35"));
    m1.Temporary.Add(Ref(H(37), tableK, 37000, edid: "Table37"));
    m1.Temporary.Add(Ref(D(37), standK, 37000, 0, TableTop + BowlHalf, "Stand37"));
    // R3: Bowl60 rests on Table60 AND on the top of a 5,200 u shell at the same height — the shell is over the cap.
    m1.Temporary.Add(Ref(H(61), shellK, 60000, 0, 0, "Shell60"));
    // R4: two bookcase-sized cases on tables A will move; A puts an inside-out jar beside Case40 and a block around Case41.
    m1.Temporary.Add(Ref(D(40), caseK, 40000, 0, TableTop + CaseHalf, "Case40"));
    m1.Temporary.Add(Ref(D(41), caseK, 41000, 0, TableTop + CaseHalf, "Case41"));
    m1.Temporary.Add(Ref(D(2), appleK, 2000, 0, TableTop + AppleHalf, "Apple2"));                   // a movable one
    m1.Temporary.Add(Ref(D(18), appleK, 18000, 0, TableTop + AppleHalf, "Apple18"));
    m1.Temporary.Add(Ref(D(7), sconceK, 7000, WallHalfY + SconceHalfY, 150, "Sconce7"));            // against the wall's +Y face
    m1.Temporary.Add(Ref(D(8), bowlK, 8000, 0, TableTop + BowlHalf + 5, "Bowl8"));                  // 5 u above its table: inside the box band, outside ε
    m1.Temporary.Add(Ref(D(9), bowlK, 9000, 0, TableTop + BowlHalf - 10, "Bowl9"));                 // sunk 10 u into its table
    m1.Temporary.Add(Ref(D(20), fxK, 20000, 0, TableTop + BowlHalf, "FX20"));                       // FX on a table — not a dependent
    m1.Temporary.Add(Ref(D(21), wallK, 21000, 0, 150, "Wall21"));                                   // a wall on the floor — too big to be a dependent
    m1.Temporary.Add(Ref(H(27), tableK, 27000, edid: "Table27"));                                   // A will remove it
    m1.Temporary.Add(Ref(D(27), bowlK, 27000, 0, TableTop + BowlHalf, "Bowl27"));
    m1.Temporary.Add(Ref(H(28), poleK, 27000, 0, 0, "Ref28"));                                   // X will turn it into a table by base swap, never moving it
    FileInterior(master, m1); FileInterior(master, m2);
    var wKey = M(0x830); var topK = M(0x831); var gridK = M(0x832);
    static Worldspace World(FormKey wKey, FormKey topK, FormKey gridK, PlacedObject? persistent, PlacedObject? temporary)
    {
        var ws = new Worldspace(wKey, SkyrimRelease.SkyrimSE) { EditorID = "CtWorld" };
        ws.TopCell = new Cell(topK, SkyrimRelease.SkyrimSE) { Flags = default };
        if (persistent is not null) ws.TopCell.Persistent.Add(persistent);
        if (temporary is not null)
        {
            var gc = new Cell(gridK, SkyrimRelease.SkyrimSE) { Flags = default, Grid = new CellGrid { Point = new P2Int(0, 0) } };
            gc.Temporary.Add(temporary);
            var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
            var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
            sub.Items.Add(gc); blk.Items.Add(sub); ws.SubCells.Add(blk);
        }
        return ws;
    }
    master.Worldspaces.Add(World(wKey, topK, gridK, Ref(H(23), tableK, 500, 500, 0, "Table23"), Ref(D(23), bowlK, 500, 500, TableTop + BowlHalf, "Bowl23")));
    var mPath = Write(master);
    using var mOv = SkyrimMod.CreateFromBinaryOverlay(mPath, release);

    // PLUGIN X (masters M, loads before A): swaps Ref28's base to a table — the base changes, the placement does not
    // (an OCW-shaped override: a vanilla shell reference turned into the mod's own floor, position untouched).
    var xKey = new ModKey("CtX", ModType.Plugin);
    var xm = new SkyrimMod(xKey, release);
    var xc1 = Interior(cell1K);
    xc1.Temporary.Add(Ref(H(28), tableK, 27000, 0, 0, "Ref28"));
    FileInterior(xm, xc1);
    var xPath = Write(xm, mOv);

    // PLUGIN A (masters M): the mover.
    var a = new SkyrimMod(aKey, release);
    a.Statics.Add(new Static(aStaticK, release) { EditorID = "CtAStatic" });
    var a1 = Interior(cell1K); var a2 = Interior(cell2K);
    foreach (int n in new[] { 1, 2, 3, 4, 5, 6, 8, 9, 14, 15, 22, 31, 32, 33, 34, 35, 36, 37, 40, 41, 60, 70 })
        a1.Temporary.Add(Ref(H(n), tableK, 1000f * n + 200, edid: $"Table{n}"));                    // moved 200 u along X
    a1.Temporary.Add(Ref(S(30), switchK, 30000, 500, 0, "Switch30", disabled: true));               // Table30 leaves through its parent
    a1.Temporary.Add(Ref(S(31), switchK, 31000, 500, 0, "Switch31", disabled: true));               // Bowl31 leaves through its parent
    a1.Temporary.Add(Ref(new FormKey(aKey, 0x902), tableK, 32000, edid: "NewTable32", parent: S(31)));  // A's table under Bowl32 — parented to the switch A disabled
    a1.Temporary.Add(Ref(new FormKey(aKey, 0x903), jarK, 40050, 0, TableTop + CaseHalf, "Jar40"));       // 5 u beside Case40, inside out
    a1.Temporary.Add(Ref(new FormKey(aKey, 0x904), blockK, 41000, 0, TableTop + CaseHalf - 150f, "Block41"));  // around Case41
    a1.Temporary.Add(Ref(D(3), bowlK, 3200, 0, TableTop + BowlHalf, "Bowl3"));                      // A moves Bowl3 with Table3
    a1.Temporary.Add(Ref(new FormKey(aKey, 0x900), tableK, 5000, edid: "NewTable5"));               // A's own table under Bowl5
    a1.Temporary.Add(new PlacedObject(D(6), release) { IsDeleted = true });                         // Bowl6 deleted (placement stripped)
    a1.Temporary.Add(Ref(H(7), wallK, 7000, 200, 150, "Wall7"));                                    // the wall moved 200 u along Y
    a1.Temporary.Add(Ref(H(10), tableK, 10000, 0, 20, "Table10"));                                  // moved UP 20 u — into the bowl
    a1.Temporary.Add(Ref(H(11), tableK, 11020, edid: "Table11"));                                   // moved 20 u along X — the bowl is still on it
    a1.Temporary.Add(Ref(H(12), tableK, 12000, edid: "Table12", disabled: true));                   // disabled
    a1.Temporary.Add(Ref(H(27), tableK, 27000, edid: "Table27", disabled: true));                   // removed — Ref28 (a table since X) should re-support Bowl27
    a2.Temporary.Add(Ref(H(13), tableK, 13000, edid: "Table13"));                                   // relocated to cell 2
    a1.Temporary.Add(Ref(D(15), bowlK, 15000, 0, TableTop + BowlHalf, "Bowl15"));                   // forwarded UNCHANGED — touched, not moved
    a1.Temporary.Add(Ref(H(16), tableK, 16010, edid: "Table16"));                                   // moved 10 u — under the threshold
    a1.Temporary.Add(Ref(H(17), markerK, 17200, edid: "Marker17"));                                 // a marker moved — not a host
    a1.Temporary.Add(Ref(H(18), tallK, 18200, edid: "Table18"));                                    // moved AND base swapped to the taller table
    a1.Temporary.Add(Ref(H(20), tableK, 20200, edid: "Table20"));
    a1.Temporary.Add(Ref(H(21), floorK, 21200, 0, -20, "Floor21"));
    a1.Temporary.Add(Ref(new FormKey(aKey, 0x901), bowlK, 22200, 0, TableTop + BowlHalf, "Bowl22"));  // A introduces a bowl on the table it moved
    FileInterior(a, a1); FileInterior(a, a2);
    a.Worldspaces.Add(World(wKey, topK, gridK, Ref(H(23), tableK, 700, 500, 0, "Table23"), null));  // the persistent host moved 200 u
    var aPath = Write(a, mOv);
    using var aOv = SkyrimMod.CreateFromBinaryOverlay(aPath, release);

    // PLUGIN B (masters M and A): re-places Bowl4 onto Table4's new position — the patch shape.
    var b = new SkyrimMod(bKey, release);
    var b1 = Interior(cell1K);
    b1.Temporary.Add(Ref(D(4), bowlK, 4200, 0, TableTop + BowlHalf, "Bowl4"));
    b1.Temporary.Add(Ref(new FormKey(bKey, 0x801), aStaticK, 99000, edid: "CtBRef"));                // references A -> masters A
    FileInterior(b, b1);
    var bPath = Write(b, mOv, aOv);

    // PLUGIN C (masters M only, loads after A): nudges Bowl14 by 1 u on the table's OLD position — the third party.
    var c = new SkyrimMod(cKey, release);
    var c1 = Interior(cell1K);
    c1.Temporary.Add(Ref(D(14), bowlK, 14001, 0, TableTop + BowlHalf, "Bowl14"));
    FileInterior(c, c1);
    var cPath = Write(c, mOv);

    // PLUGIN D (masters M AND A, loads last): nudges Bowl70 by 1 u on Table70's OLD line — the SHAM shape (handoff
    // 2026-09-12): on the mover's side, so the rule reads MovedTogether, while the bowl still floats where the table was.
    var dKey = new ModKey("CtD", ModType.Plugin);
    var dmod = new SkyrimMod(dKey, release);
    var d1 = Interior(cell1K);
    d1.Temporary.Add(Ref(D(70), bowlK, 70001, 0, TableTop + BowlHalf, "Bowl70"));
    d1.Temporary.Add(Ref(new FormKey(dKey, 0x801), aStaticK, 99100, edid: "CtDRef"));                // references A -> masters A
    FileInterior(dmod, d1);
    var dPath = Write(dmod, mOv, aOv);

    // Read the order back the way contactprobe does.
    var paths = new[] { mPath, xPath, aPath, bPath, cPath, dPath };
    ContactIndex? cidx = null;
    static TriangleSoup Inverted(TriangleSoup s) => new(s.Vertices, s.Indices.Chunk(3).SelectMany(t => new[] { t[0], t[2], t[1] }).ToArray());
    cidx = new ContactIndex(k => cidx!.Index.ObndBox(k) is { IsEmpty: false } bx
        ? new RefGeometry(bx, k == jarK ? Inverted(ContactIndex.BoxSoup(bx)) : ContactIndex.BoxSoup(bx), ContactIndex.BoxSoup(bx), 'o', 'o', HostUsable: true, Array.Empty<(string, string)>(), false) : null);
    var keys = new ModKey[paths.Length];
    var masterIdx = new HashSet<int>[paths.Length];
    for (int p = 0; p < paths.Length; p++)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], release);
        keys[p] = mod.ModKey;
        var names = mod.ModHeader.MasterReferences.Select(q => q.Master).ToHashSet();
        masterIdx[p] = Enumerable.Range(0, p).Where(q => names.Contains(keys[q])).ToHashSet();
        cidx.Index.Ingest(p, mod);
    }
    var closure = DetectorModel.MasterClosure(masterIdx);
    const int Mi = 0, Xi = 1, Ai = 2, Bi = 3, Ci = 4, Di = 5;
    Check("ct-synth: the written order has the declared masters it was built to have",
          masterIdx[Xi].SetEquals(new[] { Mi }) && masterIdx[Ai].SetEquals(new[] { Mi }) && masterIdx[Bi].SetEquals(new[] { Mi, Ai }) && masterIdx[Ci].SetEquals(new[] { Mi }) && masterIdx[Di].SetEquals(new[] { Mi, Ai }),
          $"X<-[{string.Join(",", masterIdx[Xi])}] A<-[{string.Join(",", masterIdx[Ai])}] B<-[{string.Join(",", masterIdx[Bi])}] C<-[{string.Join(",", masterIdx[Ci])}] D<-[{string.Join(",", masterIdx[Di])}]");

    var skips = new ContactSkips();
    var hosts = cidx.MovedHosts(skips, closure);
    MovedHost? Host(int n) => hosts.FirstOrDefault(h => h.Key == H(n));
    Check("ct-synth: the host census — every moved, relocated or removed table and the wall; not the 10 u nudge, not the four bowls that moved or left (too small to host — Bowl31 leaves through its parent); Marker17 IS a host (the name says marker, the flag does not — R2); Table36 never exists at runtime (R1, counted); Table70 (the sham shape) is a host",
          hosts.Count == 33 && skips.HostTooSmall == 4 && Host(16) is null && Host(17) is { Change: HostChange.Moved } && Host(12) is { Change: HostChange.Removed } && Host(13) is { Change: HostChange.Relocated }
       && Host(1) is { Change: HostChange.Moved, Mover: Ai } && Host(23) is not null && Host(36) is null && skips.HostRuntimeDisabled == 1 && Host(61) is null,
          $"{hosts.Count} hosts (runtime-disabled {skips.HostRuntimeDisabled}, too small {skips.HostTooSmall}): " + string.Join(" ", hosts.OrderBy(h => h.Key.ID).Select(h => $"{h.Key.ID - 0x900}:{h.Change}")));
    Check("ct-synth: a base swap is read at the right snap — Table18's previous base is the short table, its winning base the tall one",
          Host(18) is { } h18 && h18.PrevBase == tableK && h18.WinBase == tallK, Host(18) is { } x ? $"{x.PrevBase} -> {x.WinBase}" : "(no host)");
    Check("ct-synth R1: Table30, never re-placed, leaves the world when A disables its enable parent -> REMOVED via the parent, mover A, previous snap the master's",
          Host(30) is { Change: HostChange.Removed, RemovedViaParent: true, Mover: Ai, PrevIndex: 0 } && Host(12) is { RemovedViaParent: false },
          Host(30) is { } h30 ? $"{h30.Change} viaParent {h30.RemovedViaParent} mover {h30.Mover} prevIndex {h30.PrevIndex}" : "(no host)");
    var (flagOnly, nameOnly) = cidx.AuditMarkers();
    Check("ct-synth R2: the marker audit — the switch and the sit-chair are flagged under ordinary names; XMarkerHeading and the stand are named markers without the flag",
          flagOnly.Contains(switchK) && flagOnly.Contains(sitK) && nameOnly.Contains(markerK) && nameOnly.Contains(standK) && !flagOnly.Contains(tableK) && !nameOnly.Contains(tableK),
          $"flag-only {flagOnly.Count}, name-only {nameOnly.Count}");

    var depHash = cidx.DependentSnaps(skips);
    var winHash = cidx.PhysicalWinners(null, skips);
    var rows = hosts.SelectMany(h => cidx.Judge(h, depHash, winHash, closure, skips, keepNoRelation: true)).ToList();
    ContactRow? Row(int n) => rows.FirstOrDefault(r => r.Host.Key == H(n) && r.Dependent == D(n));
    string R(int n) => Row(n) is { } r ? $"{r.Relation} / {r.Verdict} / {r.Reason} [{r.Motion}, seen {r.Seen}, after {r.AuthorAfterMover}, touched {r.DepTouchedByMoverSide}]: {r.Why}" : "(no row)";

    Check("ct-synth: Table1 moved 200 u, Bowl1 left -> SUPPORTED / STRANDED, fixed, seen (M is in A's closure)",
          Row(1) is { Relation: ContactRelation.Supported, Verdict: ContactVerdict.Stranded, Motion: DependentMotion.Fixed, Seen: true, AuthorAfterMover: false }, R(1));
    Check("ct-synth: Table2 moved, Apple2 (a MiscItem) left -> STRANDED, MOVABLE", Row(2) is { Verdict: ContactVerdict.Stranded, Motion: DependentMotion.Movable }, R(2));
    Check("ct-synth: A moved Table3 and Bowl3 together -> MOVED TOGETHER", Row(3) is { Relation: ContactRelation.Supported, Verdict: ContactVerdict.MovedTogether }, R(3));
    Check("ct-synth: A moved Table4, B (masters A) re-placed Bowl4 -> MOVED TOGETHER (the patch shape)", Row(4) is { Verdict: ContactVerdict.MovedTogether }, R(4));
    Check("ct-synth: A moved Table5 and put its own table under Bowl5 -> RE-SUPPORTED by another reference",
          Row(5) is { Verdict: ContactVerdict.ReSupported } r5 && r5.Winner!.Against == new FormKey(aKey, 0x900) && !r5.Facts.WinnerContactIsHost, R(5));
    Check("ct-synth: A moved Table6 and deleted Bowl6 (placement stripped) -> REMOVED, caught off the record walk", Row(6) is { Verdict: ContactVerdict.Removed }, R(6));
    Check("ct-synth: Wall7 moved 200 u, Sconce7 on its face left -> ATTACHED / ORPHANED", Row(7) is { Relation: ContactRelation.Attached, Verdict: ContactVerdict.Orphaned }, R(7));
    Check("ct-synth: Bowl8 was 5 u above Table8 as authored -> no relationship at ε = 4, and the near miss still carries a measured winner arm",
          Row(8) is { Verdict: ContactVerdict.NoRelation } r8 && r8.Why.Contains("not in contact") && r8.Facts.WinnerMeasured
       && DetectorModel.PlacementContact(r8.Facts, epsilon: 8f).Verdict == ContactVerdict.Stranded, R(8));
    Check("ct-synth: Bowl9 was sunk 10 u into Table9 as authored -> no relationship (already sunk)", Row(9) is { Verdict: ContactVerdict.NoRelation } r9 && r9.Why.Contains("already sunk"), R(9));
    Check("ct-synth: Table10 moved UP 20 u into Bowl10 -> SUNK, into the host", Row(10) is { Verdict: ContactVerdict.Sunk } r10 && r10.Facts.WinnerContactIsHost && r10.Facts.WinnerPenetration > 15f, R(10));
    Check("ct-synth: Table11 moved 20 u along X, Bowl11 still on it -> RE-SUPPORTED by the host itself", Row(11) is { Verdict: ContactVerdict.ReSupported } r11 && r11.Facts.WinnerContactIsHost, R(11));
    Check("ct-synth: Table12 DISABLED under Bowl12 -> host Removed, bowl STRANDED", Row(12) is { Verdict: ContactVerdict.Stranded, Host.Change: HostChange.Removed }, R(12));
    Check("ct-synth: Table13 RELOCATED to cell 2 under Bowl13 -> host Relocated, bowl STRANDED", Row(13) is { Verdict: ContactVerdict.Stranded, Host.Change: HostChange.Relocated }, R(13));
    Check("ct-synth: C (after A, outside its closure) nudged Bowl14 on the old table line -> STRANDED, author C after the mover, blind",
          Row(14) is { Verdict: ContactVerdict.Stranded, DepAuthor: Ci, AuthorAfterMover: true, Seen: false }, R(14));
    Check("ct-synth: A forwarded Bowl15 unchanged and moved Table15 -> STRANDED, and the row says the mover's side touched it",
          Row(15) is { Verdict: ContactVerdict.Stranded, DepTouchedByMoverSide: true } && Row(1) is { DepTouchedByMoverSide: false }, R(15));
    Check("ct-synth: Table18 moved and base-swapped to the tall table -> the authoring arm uses the SHORT table: STRANDED, not 'already sunk'",
          Row(18) is { Relation: ContactRelation.Supported, Verdict: ContactVerdict.Stranded }, R(18));
    Check("ct-synth: FX on Table20 and the wall on Floor21 are not dependents; Bowl22 introduced by the mover is skipped; no row for 16; Bowl17 on the name-only marker host IS a row now (R2)",
          Row(20) is null && Row(21) is null && !rows.Any(r => r.Dependent == new FormKey(aKey, 0x901)) && skips.DepIntroducedByMover >= 1 && Row(16) is null && Row(17) is { Verdict: ContactVerdict.Stranded },
          $"introduced-by-mover {skips.DepIntroducedByMover}; 17: {R(17)}");
    Check("ct-synth: EXTERIOR — persistent Table23 in the worldspace's persistent cell, Bowl23 in a grid cell: same SPACE, host moved -> STRANDED",
          Row(23) is { Verdict: ContactVerdict.Stranded }, R(23));
    Check("ct-synth: every row's geometry is the labelled OBND box on both sides",
          rows.All(r => r.Authoring is null || (r.Authoring.DepSrc == 'o' && r.Authoring.HostSrc == 'o')), "");
    Check("ct-synth: Table27 removed by A; Ref28, a pole X turned into a table WITHOUT moving it, is under Bowl27 -> RE-SUPPORTED by Ref28 (the winner's base, not the placement-setter's)",
          Row(27) is { Verdict: ContactVerdict.ReSupported } r27 && r27.Winner!.Against == H(28), R(27));

    // The 2026-09-03 folds on the synthetic order.
    Check("ct-synth R1: Table30 removed through its parent -> Bowl30 STRANDED (the host is not in the winner population)", Row(30) is { Verdict: ContactVerdict.Stranded }, R(30));
    Check("ct-synth R1: Table31 moved, Bowl31 disabled at runtime through the switch A disabled -> UNJUDGEABLE / RuntimeDisabled",
          Row(31) is { Verdict: ContactVerdict.Unjudgeable, Reason: ContactBlocker.RuntimeDisabled, Relation: ContactRelation.Supported }, R(31));
    Check("ct-synth R1: A's NewTable32 under Bowl32 follows the disabled switch -> not a re-supporter; Bowl32 STRANDED (compare Bowl5, re-supported by an enabled one)",
          Row(32) is { Verdict: ContactVerdict.Stranded } && Row(5) is { Verdict: ContactVerdict.ReSupported }, R(32));
    Check("ct-synth R1: the opposite-of-parent flag inverts the chain — Bowl33 (opposite of a disabled switch) exists and reads STRANDED; Bowl34 (opposite of an enabled one) never exists in either arm: no row, counted",
          Row(33) is { Verdict: ContactVerdict.Stranded } && Row(34) is null && skips.DepAbsentInArm >= 1, $"33: {R(33)} | 34: {R(34)} | absent-in-arm {skips.DepAbsentInArm}");
    Check("ct-synth R1: Table36 followed a switch disabled from birth -> no host, no row for Bowl36", Host(36) is null && Row(36) is null, R(36));
    Check("ct-synth R2: the flagged sit-chair on Table35 is not a dependent (no row); the name-only stand on Table37 is one -> STRANDED",
          Row(35) is null && Row(37) is { Verdict: ContactVerdict.Stranded }, $"35: {R(35)} | 37: {R(37)}");
    Check("ct-synth R3: Table60 moved from under Bowl60, which also rests on a 5,200 u shell -> RE-SUPPORTED by the shell (over the moved-host cap), and the row says how big it is",
          Row(60) is { Verdict: ContactVerdict.ReSupported } r60 && r60.Winner!.Against == H(61) && r60.Winner.AgainstMaxDim > cidx.MaxHostDim && !r60.Facts.WinnerContactIsHost, R(60));
    Check("ct-synth: the winner contact carries the least RAW surface distance in the population and to what — 0 u to the shell under Bowl60, 5 u to the jar beside Case40 (reported, never judged)",
          Row(60) is { Winner: { } w60 } && w60.RawDistance < 0.01f && w60.RawAgainst == H(61)
       && Row(40) is { Winner: { } w40 } && MathF.Abs(w40.RawDistance - 5f) < 0.01f && w40.RawAgainst == new FormKey(aKey, 0x903),
          $"60: raw {Row(60)?.Winner?.RawDistance:0.##} to {Row(60)?.Winner?.RawAgainst} | 40: raw {Row(40)?.Winner?.RawDistance:0.##} to {Row(40)?.Winner?.RawAgainst}");
    Check("ct-synth R4: Case40 beside an inside-out 30 u jar reads far 'inside' it -> UNJUDGEABLE / DepthExceedsObject, the depth kept and the extent ~30 u; Case41 inside a 300 u block -> SUNK",
          Row(40) is { Verdict: ContactVerdict.Unjudgeable, Reason: ContactBlocker.DepthExceedsObject } r40 && r40.Facts.WinnerPenetration > 30f && MathF.Abs(r40.Facts.WinnerAgainstExtent - 30f) < 0.5f
       && Row(41) is { Verdict: ContactVerdict.Sunk } r41 && r41.Facts.WinnerPenetration > 4f && r41.Facts.WinnerAgainstExtent >= 299f,
          $"40: {R(40)} [pen {Row(40)?.Facts.WinnerPenetration:0.#} extent {Row(40)?.Facts.WinnerAgainstExtent:0.#}] | 41: {R(41)} [pen {Row(41)?.Facts.WinnerPenetration:0.#} extent {Row(41)?.Facts.WinnerAgainstExtent:0.#}]");
    // The sham shape (handoff 2026-09-12): a plugin on the mover's side that re-places the dependent by 1 u passes the rule's
    // provenance test and reads MovedTogether — the class definition is not weakened for it; the CREDIT rule downstream is
    // what refuses it, and it needs the winner arm measured for MovedTogether rows, which is what this pins.
    Check("ct-synth SHAM: D (masters M and A) nudged Bowl70 1 u on Table70's OLD line -> MOVED TOGETHER by the rule, and the winner arm is measured anyway: the bowl still touches nothing (the differential's predicate holds); Bowl3 and Bowl4, re-placed onto their tables, measure as touching",
          Row(70) is { Verdict: ContactVerdict.MovedTogether, DepAuthor: Mi } r70 && r70.Facts.WinnerMeasured && r70.Facts.WinnerDistance > cidx.Epsilon && r70.Facts.WinnerPenetration <= cidx.Epsilon
       && Row(3) is { Verdict: ContactVerdict.MovedTogether } r3 && r3.Facts.WinnerMeasured && r3.Facts.WinnerDistance <= cidx.Epsilon
       && Row(4) is { Verdict: ContactVerdict.MovedTogether } r4 && r4.Facts.WinnerMeasured && r4.Facts.WinnerDistance <= cidx.Epsilon,
          $"70: {R(70)} [winner measured {Row(70)?.Facts.WinnerMeasured}, nearest {Row(70)?.Facts.WinnerDistance:0.#}u] | 3: measured {Row(3)?.Facts.WinnerMeasured}, nearest {Row(3)?.Facts.WinnerDistance:0.#}u | 4: measured {Row(4)?.Facts.WinnerMeasured}, nearest {Row(4)?.Facts.WinnerDistance:0.#}u");
    Check("ct-synth: the whole population — 29 rows (the 19 of 2026-09-02, Bowl17 admitted by R2, eight from the folds' cases, and the sham-shaped Bowl70), and the verdict tally is the one the order was built to give",
          rows.Count == 29
       && rows.Count(r => r.Verdict == ContactVerdict.Stranded) == 13 && rows.Count(r => r.Verdict == ContactVerdict.Orphaned) == 1
       && rows.Count(r => r.Verdict == ContactVerdict.MovedTogether) == 3 && rows.Count(r => r.Verdict == ContactVerdict.ReSupported) == 4
       && rows.Count(r => r.Verdict == ContactVerdict.Sunk) == 2 && rows.Count(r => r.Verdict == ContactVerdict.Removed) == 1
       && rows.Count(r => r.Verdict == ContactVerdict.NoRelation) == 3 && rows.Count(r => r.Verdict == ContactVerdict.Unjudgeable) == 2,
          $"rows {rows.Count}: " + string.Join(" | ", rows.OrderBy(r => r.Dependent.ID).Select(r => $"{r.Dependent.ID - 0xA00}:{r.Verdict}")));

    try { Directory.Delete(root, true); } catch { }
}

// ---------------------------------------------------------------------------------------
// The differential credit rule (handoff 2026-09-12, board Q9). A sham patch that masters both plugins of a pair and
// re-states the winner's record leaves every class on provenance alone; the diff used to credit that as repair (four of
// nine lab cells, 2026-09-03). Credit is given only for a physical predicate that went Holds -> Gone.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\nPROOF — the differential credit rule (_shared/DetectorModel.cs: Credit / DifferentialVerdict / CoverageRefusals)\n");
{
    const PhysicalState H = PhysicalState.Holds, G = PhysicalState.Gone, U = PhysicalState.Unmeasured;
    static CreditOutcome Cr(bool inB, PhysicalState? b, bool inC, PhysicalState? c) => DetectorModel.Credit(inB, b, inC, c);
    static string DV(int rep, int cre, int rec, int unm, int refused) => DetectorModel.DifferentialVerdict(rep, cre, rec, unm, refused);
    Check("credit: in both arms -> UNCHANGED, whatever the predicates did (a flip is reported beside, never credited)",
          Cr(true, H, true, G) == CreditOutcome.Unchanged && Cr(true, G, true, H) == CreditOutcome.Unchanged && Cr(true, U, true, U) == CreditOutcome.Unchanged, "");
    Check("credit: left the class, predicate Holds -> Gone -> REPAIRED", Cr(true, H, false, G) == CreditOutcome.Repaired, "");
    Check("credit: left the class with the predicate still holding -> RECLASSIFIED (the sham: nothing physical changed)", Cr(true, H, false, H) == CreditOutcome.Reclassified, "");
    Check("credit: left the class with no physical defect to begin with -> RECLASSIFIED, whatever the changed arm says",
          Cr(true, G, false, G) == CreditOutcome.Reclassified && Cr(true, G, false, H) == CreditOutcome.Reclassified && Cr(true, G, false, U) == CreditOutcome.Reclassified && Cr(true, G, false, null) == CreditOutcome.Reclassified, "");
    Check("credit: left the class on a predicate the credit rests on that is unmeasured or absent -> UNMEASURED, counted apart",
          Cr(true, H, false, U) == CreditOutcome.Unmeasured && Cr(true, H, false, null) == CreditOutcome.Unmeasured && Cr(true, U, false, G) == CreditOutcome.Unmeasured && Cr(true, null, false, G) == CreditOutcome.Unmeasured, "");
    Check("credit: entered the class with the predicate holding -> CREATED", Cr(false, null, true, H) == CreditOutcome.Created, "");
    Check("credit: entered the class reading Gone -> RECLASSIFIED (a class row with nothing physical behind it)", Cr(false, null, true, G) == CreditOutcome.Reclassified, "");
    Check("credit: entered the class unmeasured -> UNMEASURED", Cr(false, null, true, U) == CreditOutcome.Unmeasured && Cr(false, null, true, null) == CreditOutcome.Unmeasured, "");
    Check("credit: in neither arm -> ABSENT", Cr(false, null, false, null) == CreditOutcome.Absent, "");
    var sham = Enumerable.Range(0, 7).Select(_ => Cr(true, H, false, H)).ToList();
    Check("verdict: the sham cell — 7 rows gone with their predicates still holding -> 0 repaired, 7 reclassified -> DIFFERENTIAL-RECLASSIFIED, never VERIFIED",
          sham.Count(o => o == CreditOutcome.Repaired) == 0 && sham.Count(o => o == CreditOutcome.Reclassified) == 7
       && DV(sham.Count(o => o == CreditOutcome.Repaired), 0, sham.Count(o => o == CreditOutcome.Reclassified), 0, 0) == "DIFFERENTIAL-RECLASSIFIED", "");
    Check("verdict: repaired > created -> VERIFIED; equal -> NEUTRAL; fewer -> HARMFUL; reclassified rides along without deciding",
          DV(5, 1, 10, 0, 0) == "DIFFERENTIAL-VERIFIED" && DV(3, 3, 0, 0, 0) == "DIFFERENTIAL-NEUTRAL" && DV(1, 4, 0, 0, 0) == "DIFFERENTIAL-HARMFUL"
       && DV(0, 1, 0, 0, 0) == "DIFFERENTIAL-HARMFUL" && DV(1, 0, 0, 0, 0) == "DIFFERENTIAL-VERIFIED", "");
    Check("verdict: nothing credited either way -> INERT; only unmeasured rows moved -> UNMEASURED", DV(0, 0, 0, 0, 0) == "DIFFERENTIAL-INERT" && DV(0, 0, 0, 3, 0) == "DIFFERENTIAL-UNMEASURED", "");
    Check("verdict: a coverage refusal outranks every count", DV(100, 0, 0, 0, 1) == "DIFFERENTIAL-UNJUDGED", "");
    var refusals = DetectorModel.CoverageRefusals(new[] { ("rows", 10L, 0L) }, new[] { ("rows", 0L, 10L) }, 0.01);
    Check("coverage: 10 judged / 0 unjudgeable -> 0 judged / 10 unjudgeable is REFUSED, and the verdict is UNJUDGED, never VERIFIED",
          refusals.Count == 1 && DV(0, 0, 0, 0, refusals.Count) == "DIFFERENTIAL-UNJUDGED", string.Join("; ", refusals));
    Check("coverage: the same shares -> nothing refused; a rise within tolerance -> nothing (9.09 % -> 9.91 %); just over -> refused (-> 11.50 %); a population only one side has -> ignored",
          DetectorModel.CoverageRefusals(new[] { ("rows", 100L, 10L) }, new[] { ("rows", 100L, 10L) }, 0.01).Count == 0
       && DetectorModel.CoverageRefusals(new[] { ("rows", 1000L, 100L) }, new[] { ("rows", 1000L, 110L) }, 0.01).Count == 0
       && DetectorModel.CoverageRefusals(new[] { ("rows", 1000L, 100L) }, new[] { ("rows", 1000L, 130L) }, 0.01).Count == 1
       && DetectorModel.CoverageRefusals(new[] { ("rows", 100L, 0L) }, new[] { ("other", 0L, 100L) }, 0.01).Count == 0, "");
    Check("coverage: a share that FELL is never a refusal", DetectorModel.CoverageRefusals(new[] { ("rows", 100L, 50L) }, new[] { ("rows", 100L, 0L) }, 0.01).Count == 0, "");
}

Console.WriteLine("\nPROOF — the synthetic load order (_shared/SyntheticOrder.cs)\n");
{
    var root = Path.Combine(Path.GetTempPath(), "proofprobe-" + Environment.ProcessId);
    var profile = Path.Combine(root, "profiles", "P");
    var mods = Path.Combine(root, "mods");
    var data = Path.Combine(root, "Data");
    var overwrite = Path.Combine(root, "overwrite");
    foreach (var d in new[] { profile, data, overwrite }) Directory.CreateDirectory(d);
    foreach (var m in new[] { "ModA", "ModB", "ModOff" }) Directory.CreateDirectory(Path.Combine(mods, m));

    // A minimal but REAL TES4 header: 24-byte record header, then HEDR + one MAST/DATA pair per
    // master. This is what lets the missing-master rule be tested by construction — an empty file
    // deliberately stays in the fixture too, because "cannot read the header" must mean "make no
    // claim", never "assume no masters".
    static byte[] PluginBytes(params string[] masters)
    {
        var body = new List<byte>();
        void Sub(string type, byte[] payload)
        {
            body.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
            body.AddRange(BitConverter.GetBytes((ushort)payload.Length));
            body.AddRange(payload);
        }
        Sub("HEDR", new byte[12]);
        foreach (var m in masters) { Sub("MAST", System.Text.Encoding.ASCII.GetBytes(m + "\0")); Sub("DATA", new byte[8]); }
        var full = new List<byte>();
        full.AddRange(System.Text.Encoding.ASCII.GetBytes("TES4"));
        full.AddRange(BitConverter.GetBytes(body.Count));
        full.AddRange(new byte[16]);            // flags, form id, vc info, version — 24-byte header total
        full.AddRange(body);
        return full.ToArray();
    }

    File.WriteAllText(Path.Combine(mods, "ModA", "A.esp"), "");
    File.WriteAllBytes(Path.Combine(mods, "ModB", "B.esp"), PluginBytes("A.esp"));   // B depends on A
    File.WriteAllText(Path.Combine(mods, "ModOff", "Off.esp"), "");
    File.WriteAllText(Path.Combine(mods, "ModOff", "A.esp"), "");     // duplicate name in a DISABLED mod
    File.WriteAllBytes(Path.Combine(mods, "ModOff", "Dep.esp"), PluginBytes("Ghost.esp"));  // masters a plugin that exists nowhere
    File.WriteAllText(Path.Combine(data, "Skyrim.esm"), "");

    // modlist.txt: TOP = highest priority.
    File.WriteAllText(Path.Combine(profile, "modlist.txt"), "+ModB\n+ModA\n-ModOff\n");
    // loadorder.txt lists EVERY plugin in order, inactive included.
    File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "Skyrim.esm\nA.esp\nOff.esp\nDep.esp\nB.esp\n");
    // plugins.txt omits the implicit master and leaves Off.esp and Dep.esp unchecked.
    File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*A.esp\nOff.esp\nDep.esp\n*B.esp\n");

    var baseline = SyntheticOrder.Build(profile, mods, data, overwrite);
    Check("synth: baseline drops the unchecked plugin", baseline.LoadedCount == 3,
          $"loaded {baseline.LoadedCount} (expect 3 — Skyrim.esm implicit + A + B)");
    Check("synth: the implicit master (in loadorder, not in plugins.txt) stays loaded",
          baseline.OrderedPaths.Any(p => p.EndsWith("Skyrim.esm", StringComparison.OrdinalIgnoreCase)),
          "implicit masters are loaded, matching Mo2LoadOrder.Build");

    var on = SyntheticOrder.Build(profile, mods, data, overwrite, enable: new[] { "Off.esp" });
    Check("synth: --enable loads an inactive plugin", on.LoadedCount == 4, $"loaded {on.LoadedCount} (expect 4)");
    Check("synth: it lands in its loadorder.txt POSITION, not appended",
          on.OrderedPaths.ToList().FindIndex(p => p.EndsWith("Off.esp", StringComparison.OrdinalIgnoreCase)) == 2,
          "index 2 — between A.esp and B.esp, where MO2 put it");
    Check("synth: the change is reported", on.Applied.Any(a => a.StartsWith("ENABLED") && a.Contains("Off.esp")),
          on.Applied.FirstOrDefault() ?? "(none)");

    var off = SyntheticOrder.Build(profile, mods, data, overwrite, disable: new[] { "A.esp" });
    Check("synth: --disable removes an active plugin", off.LoadedCount == 2, $"loaded {off.LoadedCount} (expect 2)");

    // Precedence: A.esp exists in enabled ModA and in DISABLED ModOff. The enabled copy must win, or
    // switching a mod on could silently outrank one that is genuinely installed.
    Check("synth: an enabled mod outranks a disabled one for a duplicate filename",
          baseline.OrderedPaths.Any(p => p.Contains("ModA") && p.EndsWith("A.esp", StringComparison.OrdinalIgnoreCase)),
          "A.esp resolved to ModA, not ModOff");

    // The refusal that matters: a name with no load-order position would have to be given one, and
    // position decides which plugin wins a record.
    var ghost = SyntheticOrder.Build(profile, mods, data, overwrite, enable: new[] { "NotListed.esp" });
    Check("synth: REFUSES a plugin loadorder.txt does not list",
          ghost.Warnings.Any(w => w.Contains("REFUSED")) && ghost.LoadedCount == 3,
          "no invented position; load count unchanged");

    // The missing-master rules (2026-08-27). The review enabled a patch whose master exists
    // nowhere in the order; the model happily 'repaired' 9 records in a state the game cannot
    // load. These cases make that impossible to reintroduce silently.
    var dep = SyntheticOrder.Build(profile, mods, data, overwrite, enable: new[] { "Dep.esp" });
    Check("synth: REFUSES to enable a plugin whose master is absent",
          dep.Warnings.Any(w => w.Contains("REFUSED") && w.Contains("Ghost.esp")) && dep.LoadedCount == 3,
          "an impossible game state is not modelled");

    var orphan = SyntheticOrder.Build(profile, mods, data, overwrite, disable: new[] { "A.esp" });
    Check("synth: disabling a master WARNS about its orphaned dependents",
          orphan.Warnings.Any(w => w.Contains("MISSING MASTER") && w.Contains("B.esp")) && orphan.LoadedCount == 2,
          "the state is built as asked, but loudly marked unloadable");

    var unreadable = SyntheticOrder.Build(profile, mods, data, overwrite, enable: new[] { "Off.esp" });
    Check("synth: an unreadable header SKIPS the check and says so",
          unreadable.LoadedCount == 4 && unreadable.Warnings.Any(w => w.Contains("unreadable")),
          "no header = no claim, never 'assume no masters'");

    try { Directory.Delete(root, true); } catch { }
}

// ---------------------------------------------------------------------------------------
// The route-blockage rule (roadmap 4.7 Part B, frozen 2026-09-02) — DetectorModel.RouteBlockage.
// The facts come from RouteConnectivity (proven on synthetic corridors in crossprobe); this pins
// what the rule makes of them. Ladder 0/32/64/96/128; rung indices as the measurement reports them.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\nPROOF — the route-blockage rule (DetectorModel.RouteBlockage)");
{
    var ladder = new[] { 0f, 32f, 64f, 96f, 128f };
    var r = DetectorModel.RouteBlockage(judged: false, touched: 5, maxIntactRung: 2, maxIntactRungWithout: 3, ladder, threshold: 64f);
    Check("route: unmeasured window -> Unjudgeable, never clear", r.Verdict == RouteVerdict.Unjudgeable, r.Why);

    r = DetectorModel.RouteBlockage(true, touched: 0, maxIntactRung: 4, maxIntactRungWithout: 4, ladder, 64f);
    Check("route: no overlap with walkable ground -> None", r.Verdict == RouteVerdict.None, r.Why);

    r = DetectorModel.RouteBlockage(true, touched: 40, maxIntactRung: -1, maxIntactRungWithout: 3, ladder, 64f);
    Check("route: split at zero clearance -> Blocked", r.Verdict == RouteVerdict.Blocked, r.Why);

    // The 64 u gap: intact at 32, split at 64. Below a 64 u threshold and below the corridor's own 96 -> Narrowed.
    r = DetectorModel.RouteBlockage(true, touched: 12, maxIntactRung: 1, maxIntactRungWithout: 3, ladder, 64f);
    Check("route: 32 u left of a 96 u corridor, threshold 64 -> Narrowed", r.Verdict == RouteVerdict.Narrowed, r.Why);

    // The same gap against a 32 u threshold clears it.
    r = DetectorModel.RouteBlockage(true, touched: 12, maxIntactRung: 1, maxIntactRungWithout: 3, ladder, 32f);
    Check("route: ...threshold 32 -> Clipped", r.Verdict == RouteVerdict.Clipped, r.Why);

    // The with-vs-without arm: a corridor that was ALREADY only 32 u wide is not narrowed by the object.
    r = DetectorModel.RouteBlockage(true, touched: 12, maxIntactRung: 1, maxIntactRungWithout: 1, ladder, 64f);
    Check("route: corridor already 32 u without the object -> Clipped, not Narrowed", r.Verdict == RouteVerdict.Clipped, r.Why);

    // The shelf in the corner: 64 u left, threshold 64 -> Clipped; threshold 96 -> Narrowed.
    r = DetectorModel.RouteBlockage(true, touched: 6, maxIntactRung: 2, maxIntactRungWithout: 3, ladder, 64f);
    Check("route: 64 u left at threshold 64 -> Clipped", r.Verdict == RouteVerdict.Clipped, r.Why);
    r = DetectorModel.RouteBlockage(true, touched: 6, maxIntactRung: 2, maxIntactRungWithout: 3, ladder, 96f);
    Check("route: 64 u left at threshold 96 -> Narrowed", r.Verdict == RouteVerdict.Narrowed, r.Why);
}

// ---------------------------------------------------------------------------------------
// The route-CUT rule (roadmap 4.7's closing step, frozen 2026-09-02) — DetectorModel.RouteCut, the rule Part C
// returned. The facts (line found, cut, passage) come from RouteConnectivity's sweep, proven on synthetic corridors
// in crossprobe; this pins what the rule makes of them. Thresholds: BLOCKED under 48 u of passage (measured: full
// width 0 of 5, ≤ 44 u 3 of 11), LIKELY-BLOCKED from 48 up (measured from 64: 12 of 22); 48–64 unmeasured and
// assigned to the lower-severity class on purpose.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\nPROOF — the route-cut rule (DetectorModel.RouteCut)");
{
    var c = DetectorModel.RouteCut(judged: false, touched: 5, lineFound: true, lineWhy: "", cut: true, passage: 0f);
    Check("cut: unmeasured window -> Unjudgeable, never clear", c.Verdict == RouteCutVerdict.Unjudgeable, c.Why);

    c = DetectorModel.RouteCut(true, touched: 0, lineFound: false, lineWhy: "not asked", cut: false, passage: float.NaN);
    Check("cut: no overlap with walkable ground -> None", c.Verdict == RouteCutVerdict.None, c.Why);

    c = DetectorModel.RouteCut(true, touched: 12, lineFound: false, lineWhy: "3 ends: a junction", cut: false, passage: float.NaN);
    Check("cut: on ground, no line -> NoLine (counted, never cleared)", c.Verdict == RouteCutVerdict.NoLine && c.Why.Contains("junction"), c.Why);

    c = DetectorModel.RouteCut(true, touched: 12, lineFound: true, lineWhy: "", cut: false, passage: float.NaN);
    Check("cut: on ground beside the line -> Clipped", c.Verdict == RouteCutVerdict.Clipped, c.Why);

    c = DetectorModel.RouteCut(true, touched: 40, lineFound: true, lineWhy: "", cut: true, passage: 0f);
    Check("cut: full width, nothing beside -> Blocked", c.Verdict == RouteCutVerdict.Blocked, c.Why);

    c = DetectorModel.RouteCut(true, touched: 16, lineFound: true, lineWhy: "", cut: true, passage: 32f);
    Check("cut: line cut, 32 u beside -> Blocked (Part C: 3 of 11 got through at ≤ 44)", c.Verdict == RouteCutVerdict.Blocked, c.Why);

    c = DetectorModel.RouteCut(true, touched: 16, lineFound: true, lineWhy: "", cut: true, passage: 47.9f);
    Check("cut: 47.9 u beside -> Blocked (the boundary is exclusive)", c.Verdict == RouteCutVerdict.Blocked, c.Why);

    c = DetectorModel.RouteCut(true, touched: 16, lineFound: true, lineWhy: "", cut: true, passage: 48f);
    Check("cut: 48 u beside -> LikelyBlocked (the unmeasured band goes to the lower class)", c.Verdict == RouteCutVerdict.LikelyBlocked, c.Why);

    c = DetectorModel.RouteCut(true, touched: 16, lineFound: true, lineWhy: "", cut: true, passage: 64f);
    Check("cut: 64 u beside -> LikelyBlocked (Part C: 7 of 11 got through)", c.Verdict == RouteCutVerdict.LikelyBlocked, c.Why);

    c = DetectorModel.RouteCut(true, touched: 16, lineFound: true, lineWhy: "", cut: true, passage: 288f);
    Check("cut: 288 u beside (the scan's cap) -> still LikelyBlocked, never Clipped", c.Verdict == RouteCutVerdict.LikelyBlocked, c.Why);

    // The threshold is a parameter: the same 32 u passage against a 24 u boundary is the coin.
    c = DetectorModel.RouteCut(true, touched: 16, lineFound: true, lineWhy: "", cut: true, passage: 32f, blockedBelow: 24f);
    Check("cut: 32 u beside against a 24 u boundary -> LikelyBlocked", c.Verdict == RouteCutVerdict.LikelyBlocked, c.Why);

    // Precedence: a cut line on ground with no line found cannot happen, but the rule's order says NoLine wins over
    // cut — a line that was not found was not swept.
    c = DetectorModel.RouteCut(true, touched: 16, lineFound: false, lineWhy: "dead end", cut: true, passage: 0f);
    Check("cut: no line outranks a stray cut flag -> NoLine", c.Verdict == RouteCutVerdict.NoLine, c.Why);

    // Q23 (2026-09-05): the band's bottom is measured, and the verdict names the step band above it — the "which actor"
    // clause: inside the step band a walking humanoid gets over the object by size and pace; the CLASS does not move on it.
    // The rise is above the FEET since Q24 (the physical floor): the band is (58.4, 70.4], the measured (48, 60] above the
    // navmesh plus the station's 10.4 u offset.
    Check("band: the physical step is the measured 48 u + the station's 10.4 u offset = 58.4, the band's top 70.4",
          MathF.Abs(DetectorModel.PhysicalStep - 58.4f) < 1e-4f && MathF.Abs(DetectorModel.PhysicalStepBandTop - 70.4f) < 1e-4f && DetectorModel.FallbackOffset == DetectorModel.StationOffset,
          $"{DetectorModel.PhysicalStep} / {DetectorModel.PhysicalStepBandTop}");
    c = DetectorModel.RouteCut(true, touched: 16, lineFound: true, lineWhy: "", cut: true, passage: 32f, cutRise: 62.4f);
    Check("cut: a 62 u rise above the feet (52 above the station's navmesh) -> still Blocked, and the why names the step band", c.Verdict == RouteCutVerdict.Blocked && c.Why.Contains("step band"), c.Why);
    c = DetectorModel.RouteCut(true, touched: 16, lineFound: true, lineWhy: "", cut: true, passage: 64f, cutRise: 70.4f);
    Check("cut: a 70.4 u rise (the band's top, inclusive) -> LikelyBlocked with the band clause", c.Verdict == RouteCutVerdict.LikelyBlocked && c.Why.Contains("step band"), c.Why);
    c = DetectorModel.RouteCut(true, touched: 16, lineFound: true, lineWhy: "", cut: true, passage: 64f, cutRise: 80f);
    Check("cut: an 80 u rise -> no band clause (above the band: no walker measured got over)", !c.Why.Contains("step band"), c.Why);
    c = DetectorModel.RouteCut(true, touched: 16, lineFound: true, lineWhy: "", cut: true, passage: 32f, cutRise: 58.4f);
    Check("cut: a rise AT the bottom (58.4) -> no band clause (every walker cleared it; the band is exclusive at the bottom)", !c.Why.Contains("step band"), c.Why);
    Check("cut: the navmesh-relative clause still reads at the station's numbers when asked (52 inside (48, 60])",
          DetectorModel.StepBandClause(52f, DetectorModel.StepHeight, DetectorModel.StepBandTop).Contains("step band") && DetectorModel.StepBandClause(52f).Length == 0, "");
    c = DetectorModel.RouteCut(true, touched: 16, lineFound: true, lineWhy: "", cut: true, passage: 32f);
    Check("cut: no rise known -> no band clause", !c.Why.Contains("step band"), c.Why);
}

// ---------------------------------------------------------------------------------------
// THE ACTOR BAND (Q23, measured 2026-09-05 — Research/ACTOR-BAND-BOTTOM-2026-09-05.md): the bottom is the measured
// constant, and the top's derivation counts the race's Height but NOT the placement's scale, because the character
// controller was measured to follow the one and ignore the other (a 0.7 and a 1.3 clone had the adult's top before and
// after a 3D rebuild; a race with Height 0.8 had a lower one).
// ---------------------------------------------------------------------------------------
Console.WriteLine("\nPROOF — the actor band (ActorBand: the measured bottom, the top's derivation)");
{
    Check("band: the bottom is the measured 48 u and the step band's top 60 u (2026-09-05), not the assumed 32",
          ActorBand.StepHeight == 48f && ActorBand.StepBandTop == 60f, $"{ActorBand.StepHeight} / {ActorBand.StepBandTop}");
    var nord = FormKey.Factory("013746:Skyrim.esm"); var child = FormKey.Factory("02C65B:Skyrim.esm");
    var npcA = FormKey.Factory("000001:Test.esp"); var npcC = FormKey.Factory("000002:Test.esp");
    var races = new Dictionary<FormKey, ActorBand.RaceFacts> { [nord] = new(1.03f, 1.03f, true, "NordRace"), [child] = new(0.8f, 0.8f, false, "NordRaceChild") };
    var npcs = new Dictionary<FormKey, ActorBand.NpcFacts> { [npcA] = new(nord, false, 1f, false), [npcC] = new(child, true, 1f, false) };
    PlacedRef Placed(FormKey b, float scale) => new(FormKey.Factory(b == npcA ? "000010:Test.esp" : "000011:Test.esp"), "PlacedNpc", null,
                                                    new Vec3(0f, 0f, 0f), new Vec3(0f, 0f, 0f), scale, b, false, false, false);
    var one = ActorBand.ForCell(new[] { Placed(npcA, 1f) }, npcs, races, 138f, "fallback");
    Check("band: a scale-1 Nord -> top 128 x 1.03 = 132 u", MathF.Abs(one.Top - 131.84f) < 0.1f, one.Source);
    var scaled = ActorBand.ForCell(new[] { Placed(npcA, 1.5f) }, npcs, races, 138f, "fallback");
    Check("band: the same Nord at placement scale 1.5 -> the SAME top, and the source says the scale was not counted",
          MathF.Abs(scaled.Top - one.Top) < 0.1f && scaled.Source.Contains("not counted"), scaled.Source);
    var kid = ActorBand.ForCell(new[] { Placed(npcC, 1f) }, npcs, races, 138f, "fallback");
    Check("band: a child-race actor -> top 128 x 0.8 = 102 u (the race's Height counts)", MathF.Abs(kid.Top - 102.4f) < 0.1f, kid.Source);
    var both = ActorBand.ForCell(new[] { Placed(npcC, 1f), Placed(npcA, 0.7f) }, npcs, races, 138f, "fallback");
    Check("band: a child and a 0.7-scaled Nord in one cell -> the Nord's unscaled 132 u wins", MathF.Abs(both.Top - 131.84f) < 0.1f, both.Source);
    var none = ActorBand.ForCell(Array.Empty<PlacedRef>(), npcs, races, 138f, "tallest playable race");
    Check("band: no actors -> the fallback, named", MathF.Abs(none.Top - 138f) < 0.1f && none.Source.Contains("no actors"), none.Source);
}


// ---------------------------------------------------------------------------------------
// THE NAVMESH-TO-FLOOR OFFSET (Q24, 2026-09-05 — HousecarlCore.NavmeshFloor): the floor under a navmesh point is the
// HIGHEST up-facing actor-solid surface within the depth bound below and the step reach above; the terrain competes
// on the same rule; nothing within reach is "no floor" with its reason; the navmesh inside a solid is a diagnostic,
// not a refusal. Calibrated on the Ratway corridor against 718 recorded walker positions (feet − floor within 2 u
// for 88 %) before these cases were written; they pin the arithmetic, not the calibration.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\nPROOF — the navmesh-to-floor offset (NavmeshFloor: the floor rule, the samples, the terrain, the reductions)");
{
    static List<(float Z, bool Up)> S(params (float Z, bool Up)[] s) => new(s);
    var r = NavmeshFloor.Read(47f, S((37f, true)), null);
    Check("floor: an up-facing surface 10 u below -> found on the mesh, δ +10", r.Found && r.Source == FloorSource.Mesh && MathF.Abs(r.Offset - 10f) < 1e-3f && !r.Embedded, $"{r.Source} δ {r.Offset}");
    r = NavmeshFloor.Read(47f, S((17f, true), (37f, true), (-60f, true)), null);
    Check("floor: several up-facing surfaces below -> the HIGHEST is the floor (+10, not +30)", r.Found && MathF.Abs(r.Offset - 10f) < 1e-3f, $"δ {r.Offset}");
    r = NavmeshFloor.Read(47f, S((67f, true), (37f, true)), null);
    Check("floor: an up-facing surface 20 u ABOVE, within the 48 u reach -> it is the floor (a crate the walker steps onto), δ −20",
          r.Found && MathF.Abs(r.Offset + 20f) < 1e-3f, $"δ {r.Offset}");
    r = NavmeshFloor.Read(47f, S((107f, true), (37f, true)), null);
    Check("floor: an up-facing surface 60 u above is past the reach -> the one below is the floor (+10)", r.Found && MathF.Abs(r.Offset - 10f) < 1e-3f, $"δ {r.Offset}");
    r = NavmeshFloor.Read(47f, S((107f, true)), null);
    Check("floor: only a surface past the reach -> no floor, OnlyHigher", !r.Found && r.Reason == NoFloorReason.OnlyHigher && r.Surfaces == 1, $"{r.Reason}");
    r = NavmeshFloor.Read(47f, S((-153f, true)), null);
    Check("floor: only a surface 200 u below (past the 128 u depth) -> no floor, OnlyDeeper", !r.Found && r.Reason == NoFloorReason.OnlyDeeper, $"{r.Reason}");
    r = NavmeshFloor.Read(47f, S((37f, false)), null);
    Check("floor: only a DOWN-facing surface within reach -> no floor, OnlyDownFacing", !r.Found && r.Reason == NoFloorReason.OnlyDownFacing, $"{r.Reason}");
    r = NavmeshFloor.Read(47f, S(), null);
    Check("floor: nothing in the column -> NoSurface, 0 surfaces", !r.Found && r.Reason == NoFloorReason.NoSurface && r.Surfaces == 0, $"{r.Reason} {r.Surfaces}");
    r = NavmeshFloor.Read(47f, S((32f, true)), 42f);
    Check("floor: terrain 5 u below beats a mesh 15 u below -> Land, δ +5", r.Found && r.Source == FloorSource.Land && MathF.Abs(r.Offset - 5f) < 1e-3f, $"{r.Source} δ {r.Offset}");
    r = NavmeshFloor.Read(47f, S((32f, true)), 27f);
    Check("floor: a mesh 15 u below beats terrain 20 u below -> Mesh, δ +15 (a street over its ground)", r.Found && r.Source == FloorSource.Mesh && MathF.Abs(r.Offset - 15f) < 1e-3f, $"{r.Source} δ {r.Offset}");
    r = NavmeshFloor.Read(47f, S(), -200f);
    Check("floor: only terrain past the depth -> OnlyDeeper, and the terrain counted as a surface", !r.Found && r.Reason == NoFloorReason.OnlyDeeper && r.Surfaces == 1, $"{r.Reason} {r.Surfaces}");
    r = NavmeshFloor.Read(47f, S((44f, false), (27f, true)), null);
    Check("floor: a down-facing face 3 u below the navmesh over an up-facing floor 20 u below -> found (+20) and EMBEDDED (the navmesh is inside a solid)",
          r.Found && MathF.Abs(r.Offset - 20f) < 1e-3f && r.Embedded, $"δ {r.Offset} embedded {r.Embedded}");
    r = NavmeshFloor.Read(47f, S((37f, false), (37f, true)), null);
    Check("floor: a down-facing underside coincident with the up-facing floor it stands on -> found, NOT embedded (the tie goes to up)", r.Found && !r.Embedded, $"embedded {r.Embedded}");
    r = NavmeshFloor.Read(47f, S((37f, true)), null, maxDepth: 8f);
    Check("floor: the depth bound is a parameter — 10 u below is past an 8 u bound", !r.Found && r.Reason == NoFloorReason.OnlyDeeper, $"{r.Reason}");
    r = NavmeshFloor.Read(47f, S((67f, true), (37f, true)), null, upReach: 16f);
    Check("floor: the reach is a parameter — 20 u above is past a 16 u reach, so the floor below wins (+10)", r.Found && MathF.Abs(r.Offset - 10f) < 1e-3f, $"δ {r.Offset}");

    // samples: the centroid alone on a small triangle, four interior points on a long one
    Span<Vector3> pts = stackalloc Vector3[4];
    int n = NavmeshFloor.SamplePoints(new Vec3(0, 0, 10), new Vec3(100, 0, 10), new Vec3(0, 100, 10), 192f, pts);
    Check("samples: a 100 u triangle -> one point, the centroid, at the navmesh height", n == 1 && MathF.Abs(pts[0].X - 100f / 3f) < 1e-3f && MathF.Abs(pts[0].Y - 100f / 3f) < 1e-3f && pts[0].Z == 10f, $"{n} {pts[0]}");
    n = NavmeshFloor.SamplePoints(new Vec3(0, 0, 0), new Vec3(400, 0, 0), new Vec3(0, 400, 40), 192f, pts);
    bool inside = true;
    for (int k = 0; k < n; k++) inside &= pts[k].X > 0 && pts[k].Y > 0 && pts[k].X + pts[k].Y < 400;
    Check("samples: a 400 u triangle -> four points, every one strictly inside it (never on an edge a wall may stand on)", n == 4 && inside, $"{n} inside {inside}");
    Check("samples: the extra points interpolate the navmesh height (the (½,¼,¼) point on a tilted triangle)", n == 4 && MathF.Abs(pts[3].Z - 20f) < 1e-3f, $"{pts[3]}");

    // terrain: bilinear over the heightmap, with the triangulation error bound stated at the point
    var flat = new float[LandHeight.Side, LandHeight.Side];
    for (int x = 0; x < LandHeight.Side; x++) for (int y = 0; y < LandHeight.Side; y++) flat[x, y] = 100f;
    var h = NavmeshFloor.LandHeightAt(flat, 2, -3, 2 * 4096f + 1000f, -3 * 4096f + 777f, out float saddle);
    Check("terrain: a flat heightmap reads its height anywhere in the cell, error bound 0", h == 100f && saddle == 0f, $"{h} ±{saddle}");
    Check("terrain: a point outside the cell reads null", NavmeshFloor.LandHeightAt(flat, 2, -3, 2 * 4096f - 1f, -3 * 4096f, out _) is null, "");
    var sad = new float[LandHeight.Side, LandHeight.Side];
    sad[1, 1] = 40f;   // one raised vertex: the first quad is a saddle (0, 0, 0, 40)
    h = NavmeshFloor.LandHeightAt(sad, 0, 0, 64f, 64f, out saddle);
    Check("terrain: the middle of a (0,0,0,40) quad reads the bilinear 10 with a ±10 bound (a quarter of the saddle: either diagonal)", MathF.Abs(h!.Value - 10f) < 1e-3f && MathF.Abs(saddle - 10f) < 1e-3f, $"{h} ±{saddle}");
    h = NavmeshFloor.LandHeightAt(sad, 0, 0, 128f, 128f, out saddle);
    Check("terrain: a vertex reads exactly, bound 0", h == 40f && saddle == 0f, $"{h} ±{saddle}");

    // reductions
    var reads = new FloorReading[]
    {
        new(47f, 37f, FloorSource.Mesh, NoFloorReason.Found, false, 1), new(47f, 35f, FloorSource.Mesh, NoFloorReason.Found, false, 1),
        new(47f, 17f, FloorSource.Land, NoFloorReason.Found, true, 2), new(47f, float.NaN, FloorSource.None, NoFloorReason.OnlyDeeper, false, 1),
    };
    var red = NavmeshFloor.Reduce(reads);
    Check("reduce: 3 of 4 samples floored -> found, the median offset (12), the land and embedded flags carried", red.Found && MathF.Abs(red.Offset - 12f) < 1e-3f && red.AnyLand && red.AnyEmbedded && red.FoundSamples == 3 && red.Samples == 4, $"{red}");
    red = NavmeshFloor.Reduce(new FloorReading[] { reads[0], reads[3], reads[3], new(47f, float.NaN, FloorSource.None, NoFloorReason.NoSurface, false, 0) });
    Check("reduce: 1 of 4 floored -> not found, the commonest reason (OnlyDeeper)", !red.Found && red.Reason == NoFloorReason.OnlyDeeper && float.IsNaN(red.Offset), $"{red.Reason}");
    red = NavmeshFloor.Reduce(new FloorReading[] { reads[0], reads[3] });
    Check("reduce: 1 of 2 floored -> found (at least half)", red.Found && MathF.Abs(red.Offset - 10f) < 1e-3f, $"{red.Offset}");
    Check("reduce: no samples -> not found", !NavmeshFloor.Reduce(Array.Empty<FloorReading>()).Found, "");
    Check("quantile: [1..5] -> median 3, p10 1.4; empty -> NaN", NavmeshFloor.Quantile(new List<float> { 5, 1, 4, 2, 3 }, 0.5f) == 3f && MathF.Abs(NavmeshFloor.Quantile(new List<float> { 5, 1, 4, 2, 3 }, 0.1f) - 1.4f) < 1e-4f && float.IsNaN(NavmeshFloor.Quantile(new List<float>(), 0.5f)), "");
    var (st, sc) = NavmeshFloor.Storeys(new float[] { 300f, 0f, 5f, 305f, 10f });
    Check("storeys: heights 0/5/10 and 300/305 -> two storeys, labelled in input order", sc == 2 && st[0] == 1 && st[1] == 0 && st[2] == 0 && st[3] == 1 && st[4] == 0, string.Join(",", st));
    (st, sc) = NavmeshFloor.Storeys(new float[] { 0f, 100f, 200f, 300f });
    Check("storeys: a ramp of 100 u steps is ONE storey (every gap within the 128 u tolerance)", sc == 1, $"{sc}");
    Check("storeys: no heights -> 0 storeys", NavmeshFloor.Storeys(Array.Empty<float>()).Count == 0, "");
}

// ---------------------------------------------------------------------------------------
// THE BAND ON THE PHYSICAL FLOOR (Q24, 2026-09-05): a FloorField answers the floor under a navmesh point from placed solids
// and terrain, falls back to the navmesh minus the station's offset where nothing is within reach, and
// RouteConnectivity.Measure stands the band, the capsule slab and the cut rise on it when given floorAt. The arithmetic the
// handoff asked to pin: a cell at δ 0, at δ 10, at δ 20, and no floor → the fallback. The strip is crossprobe's corridor
// (128 u wide, navmesh at z 0), the obstacles are hand-built boxes, the answers are known by construction.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\nPROOF — the band on the physical floor (FloorField; RouteConnectivity.Measure with floorAt)");
{
    static MeshColumnIndex BoxColumns(Vector3 lo, Vector3 hi)
    {
        var (v, i) = HavokCollision.BoxSoup((hi - lo) / 2f);
        var c = (lo + hi) / 2f;
        return MeshColumnIndex.Build(v.Select(p => p + c).ToArray(), i.ToArray());
    }
    static Aabb BoxOf(Vector3 lo, Vector3 hi) => new(new Vec3(lo.X, lo.Y, lo.Z), new Vec3(hi.X, hi.Y, hi.Z), false);
    var slabId = FormKey.Factory("000001:Test.esp"); var crateId = FormKey.Factory("000002:Test.esp");

    // the field: a floor slab whose top is 10 u under a navmesh at z 0 (the station's shape), and a 40 u crate standing on it
    var field = new FloorField(fallbackOffset: 10.4f);
    field.Add(slabId, BoxOf(new Vector3(-700, -50, -30), new Vector3(700, 200, -10)), () => BoxColumns(new Vector3(-700, -50, -30), new Vector3(700, 200, -10)));
    field.Add(crateId, BoxOf(new Vector3(100, 40, -10), new Vector3(140, 80, 30)), () => BoxColumns(new Vector3(100, 40, -10), new Vector3(140, 80, 30)));
    var fr = field.Read(0f, 60f, 0f);
    Check("field: the slab's top 10 u under the navmesh -> the floor at −10, δ +10, on a mesh", fr.Found && fr.Source == FloorSource.Mesh && MathF.Abs(fr.FloorZ + 10f) < 1e-3f && MathF.Abs(fr.Offset - 10f) < 1e-3f, $"{fr}");
    Check("field: FloorAt returns that floor", MathF.Abs(field.FloorAt(0f, 60f, 0f) + 10f) < 1e-3f, "");
    float far = field.FloorAt(5000f, 5000f, 0f);
    Check("field: a point off every candidate -> the fallback, the navmesh − 10.4, counted", MathF.Abs(far + 10.4f) < 1e-3f && field.Fallbacks == 1, $"{far} fallbacks {field.Fallbacks}");
    fr = field.Read(120f, 60f, 0f);
    Check("field: under the crate its top (30 u above the navmesh, within the step reach) is the floor: δ −30", fr.Found && MathF.Abs(fr.Offset + 30f) < 1e-3f, $"{fr}");
    fr = field.Read(120f, 60f, 0f, out _, exclude: crateId);
    Check("field: the same point with the crate excluded -> the slab under it (+10): the floor a walker meets the crate from", fr.Found && MathF.Abs(fr.Offset - 10f) < 1e-3f, $"{fr}");
    var landField = new FloorField(fallbackOffset: 10.4f);
    var flatLand = new float[LandHeight.Side, LandHeight.Side];
    for (int x = 0; x < LandHeight.Side; x++) for (int y = 0; y < LandHeight.Side; y++) flatLand[x, y] = -4f;
    landField.AddLand((0, 0), flatLand);
    landField.Add(slabId, BoxOf(new Vector3(0, 0, -30), new Vector3(700, 200, -10)), () => BoxColumns(new Vector3(0, 0, -30), new Vector3(700, 200, -10)));
    Check("field: terrain 4 u under the navmesh beats a slab 10 u under -> Land, δ +4", landField.Read(100f, 100f, 0f) is { Found: true, Source: FloorSource.Land } lr && MathF.Abs(lr.Offset - 4f) < 1e-3f, $"{landField.Read(100f, 100f, 0f)}");
    Check("field: the terrain counts as a floor found, not a fallback", landField.Fallbacks == 0 && landField.LandFloors >= 1, $"{landField.Fallbacks} {landField.LandFloors}");

    // the route rule on the corridor with a floor: a beam across it whose top is 55 u above the navmesh
    // the strip's two triangles LINKED as a record links them (crossprobe's route-cut strip): the line pathfinder reads
    // the record's edge links, never shared vertices
    var strip = new NavmeshGeometry(new FormKey(ModKey.FromNameAndExtension("synthetic.esp"), 7),
        new[] { new Vec3(-600, 3, 0), new Vec3(600, 3, 0), new Vec3(600, 131, 0), new Vec3(-600, 131, 0) },
        new[] { new NavTri(0, 1, 2, 0, -1, -1, 1), new NavTri(0, 2, 3, 0, 0, -1, -1) }, new Vec3(-600, 3, 0), new Vec3(600, 131, 0),
        Array.Empty<NavEdgeLink>(), Array.Empty<NavDoorTriangle>());
    var navs = NavmeshIndex.Build(new[] { strip }, out _);
    var ladder = new[] { 0f, 32f, 64f, 96f, 128f };
    float top = ActorBand.PhysicalControllerTop(131.84f);
    RouteFacts Route(Vector3 lo, Vector3 hi, Func<float, float, float, float>? floorAt) =>
        RouteConnectivity.Measure(navs, BoxColumns(lo, hi), BoxOf(lo, hi), 256f, 16f, ActorBand.PhysicalStep, top, ladder, floorAt: floorAt);
    static RouteCutVerdict Verdict(RouteFacts f) =>
        DetectorModel.RouteCut(f.Judged, f.Touched, f.Line?.Found ?? false, f.Line?.Why ?? "", f.Line?.Cut ?? false, f.Line?.Passage ?? float.NaN, cutRise: f.Line?.CutRise ?? float.NaN).Verdict;
    static string Why(RouteFacts f) =>
        DetectorModel.RouteCut(f.Judged, f.Touched, f.Line?.Found ?? false, f.Line?.Why ?? "", f.Line?.Cut ?? false, f.Line?.Passage ?? float.NaN, cutRise: f.Line?.CutRise ?? float.NaN).Why;
    var beamLo = new Vector3(0, -20, 0); var beamHi = new Vector3(16, 148, 55);
    Check("band: the physical band above the feet is 58.4 .. 122.5 u for the 1.03 Nord (the measured 48..112 above the navmesh + 10.4)",
          MathF.Abs(ActorBand.PhysicalStep - 58.4f) < 1e-3f && MathF.Abs(top - (131.84f * 0.85f + 10.4f)) < 1e-2f, $"{ActorBand.PhysicalStep} .. {top}");
    var d0 = Route(beamLo, beamHi, (x, y, z) => z);
    Check("floor δ 0: a 55 u beam is under the 58.4 u physical step -> never in the band, None", Verdict(d0) == RouteCutVerdict.None && d0.Touched == 0, $"{Verdict(d0)} touched {d0.Touched}");
    var d10 = Route(beamLo, beamHi, (x, y, z) => z - 10f);
    Check("floor δ 10 (the station): the same beam is 65 u above the feet -> Blocked (full width) with the step band clause", Verdict(d10) == RouteCutVerdict.Blocked && Why(d10).Contains("step band") && MathF.Abs(d10.Line!.CutRise - 65f) < 1e-2f, $"{Verdict(d10)} rise {d10.Line?.CutRise} {Why(d10)}");
    var d20 = Route(beamLo, beamHi, (x, y, z) => z - 20f);
    Check("floor δ 20: 75 u above the feet -> Blocked, above the step band, no clause", Verdict(d20) == RouteCutVerdict.Blocked && !Why(d20).Contains("step band") && MathF.Abs(d20.Line!.CutRise - 75f) < 1e-2f, $"{Verdict(d20)} rise {d20.Line?.CutRise}");
    var empty = new FloorField(fallbackOffset: DetectorModel.FallbackOffset);
    var dFb = Route(beamLo, beamHi, empty.FloorAt);
    Check("no floor within reach: the field falls back to the navmesh − 10.4 -> 65.4 u above the feet, Blocked with the clause, every raster read counted as a fallback",
          Verdict(dFb) == RouteCutVerdict.Blocked && Why(dFb).Contains("step band") && empty.Fallbacks > 0 && empty.Fallbacks == empty.Reads, $"{Verdict(dFb)} rise {dFb.Line?.CutRise} fallbacks {empty.Fallbacks}/{empty.Reads}");
    var dNull = Route(beamLo, beamHi, null);
    Check("no floorAt at all: the navmesh is the floor (as every run before) -> the 55 u beam is under the band, None", Verdict(dNull) == RouteCutVerdict.None, $"{Verdict(dNull)}");
    var overLo = new Vector3(-100, -20, 115); var overHi = new Vector3(100, 148, 135);
    var o0 = Route(overLo, overHi, (x, y, z) => z);
    Check("floor δ 0: an underside 115 u above the feet is under the 122.5 u top -> in the band (touched)", o0.Touched > 0, $"touched {o0.Touched}");
    var o10 = Route(overLo, overHi, (x, y, z) => z - 10f);
    Check("floor δ 10: the same underside is 125 u above the feet -> over the top, None", o10.Touched == 0 && Verdict(o10) == RouteCutVerdict.None, $"touched {o10.Touched}");
    // the physical floor under a real field: the slab field above (δ 10 everywhere on the strip) reproduces the δ 10 arm
    var dField = Route(beamLo, beamHi, field.FloorAt);
    Check("a FloorField with the slab under the whole strip reproduces the δ 10 arm (Blocked with the clause, rise 65)", Verdict(dField) == RouteCutVerdict.Blocked && MathF.Abs(dField.Line!.CutRise - 65f) < 1e-2f, $"{Verdict(dField)} rise {dField.Line?.CutRise}");
}

// ---------------------------------------------------------------------------------------
// CELL-BOUNDARY WATER (roadmap 4.9 Part A, frozen 2026-09-04) — DetectorModel.HeightKind / WaterDiff /
// ClassifyWater / WaterSeam / MissingWater, then a synthetic order through CellBoundaryIndex.JudgeWater.
// The plugins are NAMED for the truth rule (Skyrim.esm, Update.esm, the USSEP filename), because the
// rule is by filename; they live in a temp directory and are not a game.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\nPROOF — cell water: height kinds, the tuple diff, the verdict (DetectorModel.ClassifyWater)");
{
    Check("water: null height -> Unset", DetectorModel.HeightKind(null) == WaterHeightKind.Unset, "");
    Check("water: 3.4028235E+38 -> Unset", DetectorModel.HeightKind(float.MaxValue) == WaterHeightKind.Unset, "");
    Check("water: 4.2949532E+09 -> UnsignedWrapped", DetectorModel.HeightKind(4.2949532E+09f) == WaterHeightKind.UnsignedWrapped, "Winterhold's sea cells");
    Check("water: -2.1474836E+09 -> NegativeWrapped", DetectorModel.HeightKind(-2.1474836E+09f) == WaterHeightKind.NegativeWrapped, "Whiterun (0,0)");
    Check("water: -3.4028235E+38 -> OtherSentinel", DetectorModel.HeightKind(float.MinValue) == WaterHeightKind.OtherSentinel, "Blackreach");
    Check("water: -14000 -> Real", DetectorModel.HeightKind(-14000f) == WaterHeightKind.Real, "");
    Check("water: 99,999 -> Real; 100,000 -> OtherSentinel (the world's edge)", DetectorModel.HeightKind(99_999f) == WaterHeightKind.Real && DetectorModel.HeightKind(100_000f) == WaterHeightKind.OtherSentinel, "");
    // The engine's reading, measured 2026-09-04 (Part C): one 'use the default' value, the rest literals after an int32 conversion.
    Check("water: the surface — Unset defers to the worldspace default", DetectorModel.SurfaceHeight(float.MaxValue, -14000f) == -14000f && DetectorModel.SurfaceHeight(null, -14000f) == -14000f, "");
    Check("water: the surface — Unset with no real default is no surface", DetectorModel.SurfaceHeight(float.MaxValue, null) is null, "");
    Check("water: the surface — 4.2949532E+09 saturates to the default (Winterhold's swimmer at the default's depth)", DetectorModel.SurfaceHeight(4.2949532E+09f, -14000f) == -14000f, $"{DetectorModel.SurfaceHeight(4.2949532E+09f, -14000f)}");
    Check("water: the surface — -2.1474836E+09 and -3.4028235E+38 place no surface (int-min in the engine)", DetectorModel.SurfaceHeight(-2.1474836E+09f, 100f) is null && DetectorModel.SurfaceHeight(float.MinValue, 100f) is null, "Lux's warehouse cell: the player stood on the floor in vanilla");
    Check("water: the surface — a real height is itself", DetectorModel.SurfaceHeight(-988f, -14000f) == -988f, "");
    Check("water: Unset and the wrapped 4.29e9 are equal statements (both the default); the default and a real are not", DetectorModel.HeightsEqual(float.MaxValue, null) && DetectorModel.HeightsEqual(float.MaxValue, 4.2949532E+09f) && !DetectorModel.HeightsEqual(4.2949532E+09f, -14000f), "");
    Check("water: two no-surface sentinels are equal to each other", DetectorModel.HeightsEqual(-2.1474836E+09f, float.MinValue), "");
    Check("water: a no-surface sentinel and a real height are not", !DetectorModel.HeightsEqual(float.MinValue, 0f) && !DetectorModel.HeightsEqual(float.MaxValue, -14000f), "");
    Check("water: reals within 0.01 u are equal; 0.02 apart are not", DetectorModel.HeightsEqual(-14000f, -14000.005f) && !DetectorModel.HeightsEqual(-14000f, -14000.02f), "");

    var upd = new DetectorModel.WaterTuple(true, false, "001232:Update.esm", null, new Vector3(1, 0, 0), new Vector3(0, 0, 0), "noise.dds", null);
    var pre = new DetectorModel.WaterTuple(true, false, null, null, null, null, null, null);
    Check("water: identical tuples diff on nothing", DetectorModel.WaterDiff(upd, upd).Count == 0, "");
    var d = DetectorModel.WaterDiff(upd, pre);
    Check("water: the SE additions diff on Water, Velocity, Noise", d.SequenceEqual(new[] { "Water", "Velocity", "Noise" }), string.Join(",", d));
    Check("water: an empty path equals a null path", DetectorModel.WaterDiff(upd, upd with { EnvMap = "" }).Count == 0, "");
    Check("water: a velocity 0.005 off is equal; 0.02 off is not", DetectorModel.WaterDiff(upd, upd with { VelocityAngle = new Vector3(1.005f, 0, 0) }).Count == 0
                                                                 && DetectorModel.WaterDiff(upd, upd with { VelocityAngle = new Vector3(1.02f, 0, 0) }).Count == 1, "");

    DetectorModel.WaterFacts F(DetectorModel.WaterTuple winner, PluginKind kind = PluginKind.Other, DetectorModel.WaterTuple? wm = null, bool dup = false, bool author = false)
        => new(upd, pre, wm, winner, kind, author, dup);
    var r = DetectorModel.ClassifyWater(F(upd));
    Check("water: winner equals the truth -> Forwarded", r.Verdict == WaterVerdict.Forwarded, r.Why);
    r = DetectorModel.ClassifyWater(F(pre));
    Check("water: winner equals the earliest tuple -> Reverted (the Oldrim-port signature)", r.Verdict == WaterVerdict.Reverted && r.Why.Contains("Water"), r.Why);
    r = DetectorModel.ClassifyWater(F(upd with { HasWater = false }));
    Check("water: HasWater cleared -> Dropped", r.Verdict == WaterVerdict.Dropped && r.Why.Contains("HasWater"), r.Why);
    r = DetectorModel.ClassifyWater(F(upd with { Water = null }));
    Check("water: the WATR link nulled -> Dropped", r.Verdict == WaterVerdict.Dropped && r.Why.Contains("link"), r.Why);
    r = DetectorModel.ClassifyWater(F(upd with { Height = -14000f }));
    Check("water: the default -> a stated real height -> Altered (a surface either way; measured 2026-09-04)", r.Verdict == WaterVerdict.Altered && r.Why.Contains("Height"), r.Why);
    var real = upd with { Height = -14000f };
    r = DetectorModel.ClassifyWater(new DetectorModel.WaterFacts(real, pre, null, real with { Height = float.MaxValue }, PluginKind.Other, false, false));
    Check("water: a real height -> the default -> Altered, not Dropped", r.Verdict == WaterVerdict.Altered, r.Why);
    r = DetectorModel.ClassifyWater(new DetectorModel.WaterFacts(real, pre, null, real with { Height = -2.1474836E+09f }, PluginKind.Other, false, false));
    Check("water: a real height -> int-min -> Dropped (the surface removed)", r.Verdict == WaterVerdict.Dropped && r.Why.Contains("removed"), r.Why);
    r = DetectorModel.ClassifyWater(F(upd with { Height = -2.1474836E+09f }));
    Check("water: the default -> int-min -> Dropped (the default's surface removed)", r.Verdict == WaterVerdict.Dropped, r.Why);
    var none = upd with { Height = float.MinValue };
    r = DetectorModel.ClassifyWater(new DetectorModel.WaterFacts(none, pre, null, none with { Height = 0f }, PluginKind.Other, false, false));
    Check("water: no surface -> a real height -> Altered (Lux's warehouse cell: a surface where the truth had none)", r.Verdict == WaterVerdict.Altered && r.Why.Contains("none"), r.Why);
    r = DetectorModel.ClassifyWater(F(upd with { Water = "000018:Skyrim.esm" }));
    Check("water: a different WATR -> Retyped", r.Verdict == WaterVerdict.Retyped, r.Why);
    r = DetectorModel.ClassifyWater(F(upd with { Water = "000018:Skyrim.esm" }, PluginKind.WaterMod));
    Check("water: Retyped by a water mod is still Retyped (the population says deliberate)", r.Verdict == WaterVerdict.Retyped, r.Why);
    var wmT = upd with { Water = "000801:WaterMod.esp" };
    r = DetectorModel.ClassifyWater(F(wmT, PluginKind.Other, wmT));
    Check("water: winner equals the water mod's tuple -> Forwarded (the second truth)", r.Verdict == WaterVerdict.Forwarded && r.Why.Contains("water mod"), r.Why);
    r = DetectorModel.ClassifyWater(new DetectorModel.WaterFacts(real, pre, null, real with { Height = -13000f }, PluginKind.Other, false, false));
    Check("water: a real height moved to another real height -> Altered, never Dropped", r.Verdict == WaterVerdict.Altered && r.Why.Contains("Height"), r.Why);
    r = DetectorModel.ClassifyWater(F(upd with { VelocityAngle = new Vector3(0, 1, 0) }));
    Check("water: a velocity change alone -> Altered", r.Verdict == WaterVerdict.Altered, r.Why);
    r = DetectorModel.ClassifyWater(new DetectorModel.WaterFacts(pre with { HasWater = false }, pre with { HasWater = false }, null, pre, PluginKind.Other, false, false));
    Check("water: HasWater added where the truth had none -> Altered", r.Verdict == WaterVerdict.Altered, r.Why);
    var dry = pre with { HasWater = false, Height = -2.1474836E+09f };
    r = DetectorModel.ClassifyWater(new DetectorModel.WaterFacts(dry, dry, null, dry with { Height = 0f }, PluginKind.Other, false, false));
    Check("water: no water in either arm, the dormant height re-saved -> Inert, never Dropped (Lux's interiors)", r.Verdict == WaterVerdict.Inert && r.Why.Contains("Height"), r.Why);
    r = DetectorModel.ClassifyWater(new DetectorModel.WaterFacts(dry, dry, null, dry with { Water = "000018:Skyrim.esm" }, PluginKind.Other, false, false));
    Check("water: no water in either arm, a dormant WATR link added -> Inert", r.Verdict == WaterVerdict.Inert, r.Why);
    var m0 = DetectorModel.MissingWater(WaterVerdict.Inert, false, null, false, null, 0.05f);
    Check("missing: Inert is never a candidate", !m0.Candidate, m0.Why);
    r = DetectorModel.ClassifyWater(F(pre, PluginKind.Generated));
    Check("water: a generated winner -> Unjudgeable(GeneratedWinner), even reverted", r.Verdict == WaterVerdict.Unjudgeable && r.Reason == WaterBlocker.GeneratedWinner, r.Why);
    r = DetectorModel.ClassifyWater(F(upd, dup: true));
    Check("water: a duplicate grid point -> Unjudgeable(DuplicateCell), even forwarded", r.Verdict == WaterVerdict.Unjudgeable && r.Reason == WaterBlocker.DuplicateCell, r.Why);
    r = DetectorModel.ClassifyWater(F(upd, author: true));
    Check("water: an author-truth cell forwarded says so", r.Verdict == WaterVerdict.Forwarded && r.Why.Contains("definer"), r.Why);
    Check("water: populations — Bethesda floor / USSEP / water mod / other",
          DetectorModel.ClassifyWaterPopulation(PluginKind.Bethesda) == WaterPopulation.BethesdaFloor && DetectorModel.ClassifyWaterPopulation(PluginKind.Ussep) == WaterPopulation.Ussep
          && DetectorModel.ClassifyWaterPopulation(PluginKind.WaterMod) == WaterPopulation.WaterMod && DetectorModel.ClassifyWaterPopulation(PluginKind.Generated) == WaterPopulation.Other, "");
}

Console.WriteLine("\nPROOF — cell water: the seam and the missing-water rules (DetectorModel.WaterSeam / MissingWater)");
{
    var flow = new DetectorModel.ResolvedWater(true, "001232:Update.esm", -14000f);
    var deflt = new DetectorModel.ResolvedWater(true, "000018:Skyrim.esm", -14000f);
    var none = new DetectorModel.ResolvedWater(false, null, null);
    var high = flow with { Z = -13000f };
    Check("seam: joined = same water, heights within eps, both have water", DetectorModel.WaterJoined(flow, flow, 0.5f) && !DetectorModel.WaterJoined(flow, deflt, 0.5f) && !DetectorModel.WaterJoined(flow, high, 0.5f) && !DetectorModel.WaterJoined(flow, none, 0.5f), "");
    Check("seam: a side without a surface height is never joined", !DetectorModel.WaterJoined(flow, flow with { Z = null }, 0.5f), "");
    var s = DetectorModel.WaterSeam(flow, flow, flow, flow, WaterVerdict.Forwarded, WaterVerdict.Forwarded, 0.5f);
    Check("seam: joined in both arms -> None", s.Verdict == WaterSeamVerdict.None, s.Why);
    s = DetectorModel.WaterSeam(none, none, none, none, WaterVerdict.Forwarded, WaterVerdict.Forwarded, 0.5f);
    Check("seam: no water anywhere -> None", s.Verdict == WaterSeamVerdict.None, s.Why);
    s = DetectorModel.WaterSeam(flow, flow, flow, deflt, WaterVerdict.Forwarded, WaterVerdict.Reverted, 0.5f);
    Check("seam: truth joined, winner retyped on B -> Seam attributed to side B", s.Verdict == WaterSeamVerdict.Seam && s.Why.Contains("side B"), s.Why);
    s = DetectorModel.WaterSeam(flow, flow, none, flow, WaterVerdict.Dropped, WaterVerdict.Forwarded, 0.5f);
    Check("seam: truth joined, winner dropped on A -> Seam attributed to side A", s.Verdict == WaterSeamVerdict.Seam && s.Why.Contains("side A"), s.Why);
    s = DetectorModel.WaterSeam(flow, flow, deflt, none, WaterVerdict.Reverted, WaterVerdict.Dropped, 0.5f);
    Check("seam: both sides changed -> attributed to both", s.Verdict == WaterSeamVerdict.Seam && s.Why.Contains("both sides"), s.Why);
    s = DetectorModel.WaterSeam(flow, flow, flow, high, WaterVerdict.Forwarded, WaterVerdict.Forwarded, 0.5f);
    Check("seam: both forward yet the heights differ -> attributed to the worldspace record", s.Verdict == WaterSeamVerdict.Seam && s.Why.Contains("worldspace"), s.Why);
    s = DetectorModel.WaterSeam(flow, high, flow, high, WaterVerdict.Forwarded, WaterVerdict.Forwarded, 0.5f);
    Check("seam: apart in both arms -> Inherited (Bethesda ships it)", s.Verdict == WaterSeamVerdict.Inherited, s.Why);
    s = DetectorModel.WaterSeam(flow, high, flow, flow, WaterVerdict.Forwarded, WaterVerdict.Altered, 0.5f);
    Check("seam: apart under the truth, joined under the winner -> Joined (a fixer's shape)", s.Verdict == WaterSeamVerdict.Joined, s.Why);
    s = DetectorModel.WaterSeam(flow, flow with { Z = -14000.4f }, flow, flow with { Z = -14000.6f }, WaterVerdict.Forwarded, WaterVerdict.Altered, 0.5f);
    Check("seam: eps is inclusive on the truth side and exclusive past it", s.Verdict == WaterSeamVerdict.Seam, s.Why);

    var m = DetectorModel.MissingWater(WaterVerdict.Forwarded, true, 1f, true, 1f, 0.05f);
    Check("missing: Forwarded is never a candidate (predicate Gone)", !m.Candidate && m.Physical == PhysicalState.Gone, m.Why);
    m = DetectorModel.MissingWater(WaterVerdict.Unjudgeable, true, 1f, true, 1f, 0.05f);
    Check("missing: Unjudgeable -> not a candidate, Unmeasured", !m.Candidate && m.Physical == PhysicalState.Unmeasured, m.Why);
    m = DetectorModel.MissingWater(WaterVerdict.Altered, false, null, false, null, 0.05f);
    Check("missing: the truth has no water -> nothing to lose", !m.Candidate && m.Physical == PhysicalState.Gone, m.Why);
    m = DetectorModel.MissingWater(WaterVerdict.Dropped, true, 0.02f, false, null, 0.05f);
    Check("missing: the truth's own surface is under the ground -> the control, never a candidate", !m.Candidate && m.Why.Contains("control"), m.Why);
    m = DetectorModel.MissingWater(WaterVerdict.Dropped, true, 0.6f, false, null, 0.05f);
    Check("missing: HasWater cleared where the truth showed water -> candidate, Holds", m.Candidate && m.Physical == PhysicalState.Holds, m.Why);
    m = DetectorModel.MissingWater(WaterVerdict.Dropped, true, null, false, null, 0.05f);
    Check("missing: no LAND and no water at all -> still a candidate on the Dropped clause", m.Candidate && m.Physical == PhysicalState.Holds, m.Why);
    m = DetectorModel.MissingWater(WaterVerdict.Reverted, true, null, true, null, 0.05f);
    Check("missing: no LAND to measure against -> Unmeasured", !m.Candidate && m.Physical == PhysicalState.Unmeasured, m.Why);
    m = DetectorModel.MissingWater(WaterVerdict.Altered, true, 0.51f, true, 0.0f, 0.05f);
    Check("missing: the winner's surface under the ground everywhere -> candidate", m.Candidate && m.Physical == PhysicalState.Holds && m.Why.Contains("0%"), m.Why);
    m = DetectorModel.MissingWater(WaterVerdict.Altered, true, 0.51f, true, 0.05f, 0.05f);
    Check("missing: exactly the floor is not under it", !m.Candidate && m.Physical == PhysicalState.Gone, m.Why);
    m = DetectorModel.MissingWater(WaterVerdict.Reverted, true, 0.9f, true, 0.9f, 0.05f);
    Check("missing: Reverted with the surface still showing -> not a candidate (a seam, not missing water)", !m.Candidate && m.Physical == PhysicalState.Gone, m.Why);
}

Console.WriteLine("\nPROOF — cell water: a synthetic order through CellBoundaryIndex.JudgeWater (Mutagen-written, kinds by filename)");
{
    var root = Path.Combine(Path.GetTempPath(), "proofprobe-water-" + Environment.ProcessId);
    Directory.CreateDirectory(root);
    var release = SkyrimRelease.SkyrimSE;
    var skyK = new ModKey("Skyrim", ModType.Master);
    var updK = new ModKey("Update", ModType.Master);
    var usK = ModKey.FromNameAndExtension(PluginKinds.Ussep);
    var wmK = new ModKey("WaterMod", ModType.Plugin);
    var portK = new ModKey("Port", ModType.Plugin);
    var laterK = new ModKey("Later", ModType.Plugin);
    var genK = new ModKey("DynDOLOD", ModType.Plugin);
    var modxK = new ModKey("ModX", ModType.Plugin);
    FormKey S(uint id) => new(skyK, id);
    var worldK = S(0x800); var topK = S(0x801);
    var defWater = S(0x810); var riverWater = S(0x811);
    var flowBlend = new FormKey(updK, 0x801);
    var wmWater = new FormKey(wmK, 0x801);
    FormKey C(int x) => S((uint)(0x900 + x));          // grid cell (x, 0)
    FormKey L(int x) => S((uint)(0xA00 + x));          // its LAND
    var c01 = S(0x920); var l01 = S(0xA20);            // grid cell (0, 1)
    var c30b = S(0x930); var l30b = S(0xA30);          // the duplicate at (3, 0)
    var c50 = new FormKey(modxK, 0x801); var l50 = new FormKey(modxK, 0x802);   // a mod-defined cell at (5, 0)

    // A flat LAND at world height h: offset h/8, gradients all zero.
    static Landscape Flat(FormKey key, float h, FormKey? baseTex = null)
    {
        var land = new Landscape(key, SkyrimRelease.SkyrimSE) { VertexHeightMap = new LandscapeVertexHeightMap { Offset = h / 8f, HeightMap = new Array2d<sbyte>(new sbyte[33, 33]) } };
        if (baseTex is { } bt)
            foreach (var q in new[] { Quadrant.BottomLeft, Quadrant.BottomRight, Quadrant.TopLeft, Quadrant.TopRight })
            { var bl = new BaseLayer { Header = new LayerHeader { Quadrant = q, LayerNumber = 0 } }; bl.Header.Texture.SetTo(bt); land.Layers.Add(bl); }
        return land;
    }
    static Cell Grid(FormKey key, int x, int y, bool hasWater, FormKey? water, float? height, Landscape? land, string? edid = null, Vector3? vel = null, string? noise = null)
    {
        var c = new Cell(key, SkyrimRelease.SkyrimSE) { EditorID = edid, Flags = hasWater ? Cell.Flag.HasWater : default, Grid = new CellGrid { Point = new P2Int(x, y) }, WaterHeight = height, Landscape = land };
        if (water is { } w) c.Water.SetTo(w);
        if (vel is { } v) c.WaterVelocity = new CellWaterVelocity { Angle = new P3Float(v.X, v.Y, v.Z), Offset = new P3Float(0, 0, 0) };
        if (noise is not null) c.WaterNoiseTexture = noise;
        return c;
    }
    static Worldspace WorldWith(FormKey wKey, FormKey topK, FormKey? water, float? defaultHeight, params Cell[] cells)
    {
        var ws = new Worldspace(wKey, SkyrimRelease.SkyrimSE) { EditorID = "WaterWorld" };
        if (water is { } w) ws.Water.SetTo(w);
        if (defaultHeight is { } dh) ws.LandDefaults = new WorldspaceLandDefaults { DefaultLandHeight = 0, DefaultWaterHeight = dh };
        ws.TopCell = new Cell(topK, SkyrimRelease.SkyrimSE) { Flags = default, Grid = new CellGrid { Point = new P2Int(0, 0) } };   // the persistent cell, stamped (0,0) like the CK does
        var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
        var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
        foreach (var c in cells) sub.Items.Add(c);
        blk.Items.Add(sub); ws.SubCells.Add(blk);
        return ws;
    }
    string Write(SkyrimMod mod, params ISkyrimModGetter[] masters)
    {
        var path = Path.Combine(root, mod.ModKey.FileName.String);
        mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
        return path;
    }

    // Skyrim.esm — the pre-Update state: HasWater, no WATR link, the sentinel; the sea floor at -15000 (visible), C11 dry land.
    var sky = new SkyrimMod(skyK, release);
    sky.Waters.Add(new Water(defWater, release) { EditorID = "DefaultWater" });
    sky.Waters.Add(new Water(riverWater, release) { EditorID = "RiverWater" });
    sky.Worldspaces.Add(WorldWith(worldK, topK, defWater, -14000f,
        Grid(C(0), 0, 0, true, null, float.MaxValue, Flat(L(0), -15000f, defWater), "C00"),
        Grid(C(1), 1, 0, true, null, float.MaxValue, Flat(L(1), -15000f), "C10"),
        Grid(C(2), 2, 0, true, null, -14000f, Flat(L(2), -15000f), "C20"),
        Grid(C(3), 3, 0, true, null, float.MaxValue, Flat(L(3), -15000f), "C30"),
        Grid(c30b, 3, 0, true, null, float.MaxValue, Flat(l30b, -15000f), "C30b"),
        Grid(C(4), 4, 0, true, null, float.MaxValue, Flat(L(4), 0f), "C40"),                 // the control: land above the sea everywhere
        Grid(C(6), 6, 0, true, null, float.MaxValue, Flat(L(6), -15000f), "C60"),
        Grid(C(7), 7, 0, true, null, float.MaxValue, Flat(L(7), -15000f), "C70"),
        Grid(c01, 0, 1, true, null, float.MaxValue, Flat(l01, -15000f), "C01"),
        Grid(S(0x921), 1, 1, false, null, null, Flat(S(0xA21), 0f), "C11")));
    var skyPath = Write(sky);
    using var skyOv = SkyrimMod.CreateFromBinaryOverlay(skyPath, release);

    // Update.esm — the SE water: a flow-blend WATR, velocity, noise, on every sea cell but C30/C30b (the wrinkle stays vanilla).
    var upd = new SkyrimMod(updK, release);
    upd.Waters.Add(new Water(flowBlend, release) { EditorID = "DefaultWaterFlowBlend" });
    var vel = new Vector3(1, 0, 0);
    upd.Worldspaces.Add(WorldWith(worldK, topK, defWater, -14000f,
        Grid(C(0), 0, 0, true, flowBlend, float.MaxValue, null, "C00", vel, "n.dds"),
        Grid(C(1), 1, 0, true, flowBlend, float.MaxValue, null, "C10", vel, "n.dds"),
        Grid(C(2), 2, 0, true, flowBlend, -14000f, null, "C20", vel, "n.dds"),
        Grid(C(4), 4, 0, true, flowBlend, float.MaxValue, null, "C40", vel, "n.dds"),
        Grid(C(6), 6, 0, true, flowBlend, float.MaxValue, null, "C60", vel, "n.dds"),
        Grid(C(7), 7, 0, true, flowBlend, float.MaxValue, null, "C70", vel, "n.dds"),
        Grid(c01, 0, 1, true, flowBlend, float.MaxValue, null, "C01", vel, "n.dds")));
    var updPath = Write(upd, skyOv);
    using var updOv = SkyrimMod.CreateFromBinaryOverlay(updPath, release);

    // USSEP — forwards C00 exactly (no snap; the truth is still Update's tuple).
    var us = new SkyrimMod(usK, release);
    us.Worldspaces.Add(WorldWith(worldK, topK, defWater, -14000f, Grid(C(0), 0, 0, true, flowBlend, float.MaxValue, null, "C00", vel, "n.dds")));
    var usPath = Write(us, skyOv, updOv);
    using var usOv = SkyrimMod.CreateFromBinaryOverlay(usPath, release);

    // WaterMod.esp — overrides a vanilla WATR (the rule's first clause) and retypes C01 to a WATR of its own.
    var wm = new SkyrimMod(wmK, release);
    wm.Waters.Add(new Water(defWater, release) { EditorID = "DefaultWater" });
    wm.Waters.Add(new Water(wmWater, release) { EditorID = "WaterModWater" });
    wm.Worldspaces.Add(WorldWith(worldK, topK, defWater, -14000f, Grid(c01, 0, 1, true, wmWater, float.MaxValue, null, "C01", vel, "n.dds")));
    var wmPath = Write(wm, skyOv, updOv);
    using var wmOv = SkyrimMod.CreateFromBinaryOverlay(wmPath, release);

    // Port.esp — an Oldrim port: C10 back to Skyrim.esm's tuple; C20 HasWater cleared; C60 the link nulled; C70 a real height where the sentinel was.
    var port = new SkyrimMod(portK, release);
    port.Worldspaces.Add(WorldWith(worldK, topK, defWater, -14000f,
        Grid(C(1), 1, 0, true, null, float.MaxValue, null, "C10"),
        Grid(C(2), 2, 0, false, flowBlend, -14000f, null, "C20", vel, "n.dds"),
        Grid(C(6), 6, 0, true, null, float.MaxValue, null, "C60", vel, "n.dds"),
        Grid(C(7), 7, 0, true, flowBlend, -14000f, null, "C70", vel, "n.dds")));
    var portPath = Write(port, skyOv, updOv);
    using var portOv = SkyrimMod.CreateFromBinaryOverlay(portPath, release);

    // Later.esp (masters WaterMod) — forwards the water mod's C01 exactly.
    var later = new SkyrimMod(laterK, release);
    later.Worldspaces.Add(WorldWith(worldK, topK, defWater, -14000f, Grid(c01, 0, 1, true, wmWater, float.MaxValue, null, "C01", vel, "n.dds")));
    var laterPath = Write(later, skyOv, updOv, wmOv);

    // DynDOLOD.esp — a generated plugin winning C40 (forwarded, but unjudgeable by kind).
    var gen = new SkyrimMod(genK, release);
    gen.Worldspaces.Add(WorldWith(worldK, topK, defWater, -14000f, Grid(C(4), 4, 0, true, flowBlend, float.MaxValue, null, "C40", vel, "n.dds")));
    var genPath = Write(gen, skyOv, updOv);

    // ModX.esp — defines its own cell at (5,0) with river water; Later2 (below) retypes it.
    var modx = new SkyrimMod(modxK, release);
    modx.Worldspaces.Add(WorldWith(worldK, topK, defWater, -14000f, Grid(c50, 5, 0, true, riverWater, float.MaxValue, Flat(l50, -15000f), "C50")));
    var modxPath = Write(modx, skyOv);
    using var modxOv = SkyrimMod.CreateFromBinaryOverlay(modxPath, release);
    var later2 = new SkyrimMod(new ModKey("Later2", ModType.Plugin), release);
    later2.Worldspaces.Add(WorldWith(worldK, topK, defWater, -14000f, Grid(c50, 5, 0, true, defWater, float.MaxValue, null, "C50")));
    var later2Path = Write(later2, skyOv, modxOv);

    var order = new[] { skyPath, updPath, usPath, wmPath, portPath, laterPath, genPath, modxPath, later2Path };
    var idx = new CellBoundaryIndex();
    for (int p = 0; p < order.Length; p++)
    {
        var mod = SkyrimMod.CreateFromBinaryOverlay(order[p], release);
        using (mod as IDisposable) idx.Ingest(p, mod, land: true);
    }
    PluginKind Kind(int p) => idx.KindOf(p, ownWatrCells: 10);
    {
        // The resolve rule as measured 2026-09-04: an exterior cell deferring to the worldspace default keeps the sea with
        // HasWater cleared; a cell with its own real height honours the flag (not measured; the rule's reading).
        var wsd = new WorldWaterSnap(0, "000018:Skyrim.esm", null, -14000f, -14000f, false);
        var cleared = new DetectorModel.WaterTuple(false, false, null, float.MaxValue, null, null, null, null);
        var rr = CellBoundaryIndex.Resolve(cleared, wsd, exterior: true);
        Check("water/resolve: exterior, HasWater cleared, height deferring to the default -> the sea stays (measured: the swimmer swam)", rr.Has && rr.Z == -14000f, $"{rr}");
        rr = CellBoundaryIndex.Resolve(cleared with { Height = 4.2949532E+09f }, wsd, exterior: true);
        Check("water/resolve: the same with the wrapped 4.29e9 (also the default) -> the sea stays", rr.Has && rr.Z == -14000f, $"{rr}");
        rr = CellBoundaryIndex.Resolve(cleared with { Height = -14000f }, wsd, exterior: true);
        Check("water/resolve: HasWater cleared with the cell's OWN real height -> no water (the flag honoured; not measured)", !rr.Has, $"{rr}");
        rr = CellBoundaryIndex.Resolve(cleared, wsd, exterior: false);
        Check("water/resolve: an interior never inherits the worldspace sea", !rr.Has, $"{rr}");
        rr = CellBoundaryIndex.Resolve(cleared with { Height = float.MinValue }, wsd, exterior: true);
        Check("water/resolve: HasWater cleared and a no-surface sentinel -> no water", !rr.Has && rr.Z is null, $"{rr}");
    }
    Check("water/index: kinds by filename — Skyrim/Update Bethesda, the USSEP name, DynDOLOD generated",
          Kind(0) == PluginKind.Bethesda && Kind(1) == PluginKind.Bethesda && Kind(2) == PluginKind.Ussep && Kind(6) == PluginKind.Generated, $"{Kind(0)} {Kind(1)} {Kind(2)} {Kind(6)}");
    Check("water/index: the water-mod rule — one foreign WATR override makes WaterMod; Port and Later are Other",
          Kind(3) == PluginKind.WaterMod && Kind(4) == PluginKind.Other && Kind(5) == PluginKind.Other, $"{Kind(3)} {idx.WaterModFacts(3)} {Kind(4)} {Kind(5)}");
    Check("water/index: the persistent cell is gridless; (3,0) is the only duplicate grid point",
          idx.DuplicateGridPoints().Count() == 1 && idx.DuplicateGridPoints().First().Grid.X == 3, string.Join(", ", idx.DuplicateGridPoints().Select(d => $"({d.Grid.X},{d.Grid.Y})")));
    Check("water/index: C00 has one changed-only snap after Skyrim.esm (Update's; USSEP's forward is not a step)",
          idx.Cells[C(0)].Water.Count == 2 && idx.Cells[C(0)].Winner == 2, $"snaps {idx.Cells[C(0)].Water.Count}, winner {idx.Cells[C(0)].Winner}");

    CellBoundaryIndex.CellWaterJudgement J(FormKey k) => idx.JudgeWater(idx.Cells[k], Kind, 0.05f)!;
    var j = J(C(0));
    Check("water/synthetic: C00 — USSEP forwards Update's tuple -> Forwarded, truth Update, population USSEP",
          j.Verdict == WaterVerdict.Forwarded && j.TruthPlugin == 1 && j.WinnerKind == PluginKind.Ussep && !j.AuthorTruth, $"{j.Verdict} truth {j.TruthPlugin} {j.WinnerKind}");
    Check("water/synthetic: C00 resolves to the flow-blend WATR at the worldspace default -14000, 100% visible over a -15000 floor",
          j.RWin.Water == flowBlend.ToString() && j.RWin.Z == -14000f && j.VisWin == 1f, $"{j.RWin} vis {j.VisWin}");
    j = J(C(1));
    Check("water/synthetic: C10 — Port re-states Skyrim.esm's tuple -> Reverted", j.Verdict == WaterVerdict.Reverted, j.Why);
    Check("water/synthetic: C10 under the winner resolves to the worldspace's DefaultWater at -14000 (still visible, so not missing)",
          j.RWin.Water == defWater.ToString() && j.RWin.Z == -14000f && !j.Missing && j.Physical == PhysicalState.Gone, $"{j.RWin} missing {j.Missing} {j.MissingWhy}");
    j = J(C(2));
    Check("water/synthetic: C20 — HasWater cleared -> Dropped, a missing-water candidate (truth visible 100%)", j.Verdict == WaterVerdict.Dropped && j.Missing && j.Physical == PhysicalState.Holds && j.VisTruth == 1f, $"{j.Verdict} {j.MissingWhy}");
    j = J(C(6));
    Check("water/synthetic: C60 — the WATR link nulled -> Dropped, not missing (the default water still shows)", j.Verdict == WaterVerdict.Dropped && j.Why.Contains("link") && !j.Missing, $"{j.Verdict} {j.Why} / {j.MissingWhy}");
    j = J(C(7));
    Check("water/synthetic: C70 — a real height where the default stood -> Altered (both place a surface; measured 2026-09-04)", j.Verdict == WaterVerdict.Altered && j.Why.Contains("Height"), j.Why);
    j = J(c01);
    Check("water/synthetic: C01 — Later forwards the water mod's retype -> Forwarded on the second truth, the water mod's tuple carried",
          j.Verdict == WaterVerdict.Forwarded && j.WaterMod is { } && j.Why.Contains("water mod"), $"{j.Verdict} {j.Why}");
    j = J(C(3));
    Check("water/synthetic: C30 — the duplicate grid point -> Unjudgeable(DuplicateCell)", j.Verdict == WaterVerdict.Unjudgeable && j.Blocker == WaterBlocker.DuplicateCell, j.Why);
    j = J(C(4));
    Check("water/synthetic: C40 — DynDOLOD wins -> Unjudgeable(GeneratedWinner)", j.Verdict == WaterVerdict.Unjudgeable && j.Blocker == WaterBlocker.GeneratedWinner, j.Why);
    Check("water/synthetic: C40's truth surface is under the ground everywhere (the control shape, visible 0%)", j.VisTruth == 0f, $"vis {j.VisTruth}");
    j = J(c50);
    Check("water/synthetic: C50 — a mod-defined cell retyped by a later plugin -> Retyped with author truth", j.Verdict == WaterVerdict.Retyped && j.AuthorTruth && j.TruthPlugin == 7, $"{j.Verdict} author {j.AuthorTruth} truth {j.TruthPlugin}");
    j = J(S(0x921));
    Check("water/synthetic: C11 — no flag, no height, land above the default -> Forwarded; the default sea resolves under the ground (the control), not a candidate", j.Verdict == WaterVerdict.Forwarded && !j.Missing && j.RTruth.Has && j.VisTruth == 0f, $"{j.MissingWhy} has={j.RTruth.Has} vis={j.VisTruth}");

    // Seams over the synthetic row: C00|C10 (forwarded | reverted) and C10|C20 (reverted | dropped).
    var a = J(C(0)); var b = J(C(1)); var c2 = J(C(2));
    var sm = DetectorModel.WaterSeam(a.RTruth, b.RTruth, a.RWin, b.RWin, a.Verdict, b.Verdict, 0.5f);
    Check("water/synthetic: C00|C10 — joined under the truth, apart under the winner (a different WATR) -> Seam on side B", sm.Verdict == WaterSeamVerdict.Seam && sm.Why.Contains("side B"), sm.Why);
    sm = DetectorModel.WaterSeam(b.RTruth, c2.RTruth, b.RWin, c2.RWin, b.Verdict, c2.Verdict, 0.5f);
    Check("water/synthetic: C10|C20 — both changed -> Seam on both sides", sm.Verdict == WaterSeamVerdict.Seam && sm.Why.Contains("both"), sm.Why);
    try { Directory.Delete(root, true); } catch { }
}

// ---------------------------------------------------------------------------------------
// LANDSCAPE SEAMS (roadmap 4.9 Part B, frozen 2026-09-04) — HousecarlCore.LandHeight (the decode the renderer and the
// detector share), DetectorModel.EdgeMismatch / LandSeam, then a synthetic order through CellBoundaryIndex.MeasurePair.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\nPROOF — landscape seams: the VHGT decode (LandHeight), the edge rule (DetectorModel.LandSeam)");
{
    var g = new sbyte[33, 33];
    var h = LandHeight.Decode(10f, g);
    Check("land: zero gradients -> every vertex at offset × 8", h[0, 0] == 80f && h[32, 32] == 80f && h[16, 7] == 80f, $"{h[0, 0]} {h[32, 32]}");
    g[0, 1] = 2; g[1, 0] = 3; g[2, 0] = -1;
    h = LandHeight.Decode(10f, g);
    Check("land: column 0 accumulates down the rows: row 1 starts at (10+2)×8", h[0, 1] == 96f && h[5, 1] == 96f, $"{h[0, 1]} {h[5, 1]}");
    Check("land: each row accumulates along x from its own start: (10+3)×8 then (10+3-1)×8", h[1, 0] == 104f && h[2, 0] == 96f && h[32, 0] == 96f, $"{h[1, 0]} {h[2, 0]} {h[32, 0]}");
    Check("land: the row-1 step does not leak back into row 0", h[0, 0] == 80f, $"{h[0, 0]}");
    var east = LandHeight.Edge(h, CellEdge.East); var west = LandHeight.Edge(h, CellEdge.West);
    var north = LandHeight.Edge(h, CellEdge.North); var south = LandHeight.Edge(h, CellEdge.South);
    Check("land: edges — east is x=32 down y, west x=0, north y=32 along x, south y=0",
          east[0] == h[32, 0] && east[1] == h[32, 1] && west[1] == h[0, 1] && north[3] == h[3, 32] && south[1] == h[1, 0] && south[2] == h[2, 0], "");
    var (mx, over) = DetectorModel.EdgeMismatch(new float[] { 0, 0, 8, 40 }, new float[] { 0, 8, 0, 0 }, 8f);
    Check("land: mismatch = max |Δ| and the count strictly over eps", mx == 40f && over == 1, $"max {mx} over {over}");

    var r = DetectorModel.LandSeam(new DetectorModel.LandSeamFacts(8f, 8f, 0f, true), 8f);
    Check("land: Δ at eps is not a seam", r.Verdict == LandSeamVerdict.None, r.Why);
    r = DetectorModel.LandSeam(new DetectorModel.LandSeamFacts(16f, 0f, null, false), 8f);
    Check("land: winners differ, the later side matched what it saw -> Introduced, Blind", r.Verdict == LandSeamVerdict.Seam && r.Origin == SeamOrigin.Introduced && r.Sight == SeamSight.Blind, r.Why);
    r = DetectorModel.LandSeam(new DetectorModel.LandSeamFacts(16f, 16f, 0f, true), 8f);
    Check("land: the later plugin broke an edge that matched before it -> Introduced, Seen", r.Verdict == LandSeamVerdict.Seam && r.Origin == SeamOrigin.Introduced && r.Sight == SeamSight.Seen, r.Why);
    r = DetectorModel.LandSeam(new DetectorModel.LandSeamFacts(16f, 16f, null, true), 8f);
    Check("land: no LAND before the later plugin on its side -> Introduced, Seen (the LAND is new here)", r.Verdict == LandSeamVerdict.Seam && r.Origin == SeamOrigin.Introduced && r.Sight == SeamSight.Seen && r.Why.Contains("new"), r.Why);
    r = DetectorModel.LandSeam(new DetectorModel.LandSeamFacts(16f, 16f, 24f, true), 8f);
    Check("land: the seam pre-dates the later plugin's edit -> Inherited", r.Verdict == LandSeamVerdict.Seam && r.Origin == SeamOrigin.Inherited && r.Sight == SeamSight.Seen, r.Why);
    r = DetectorModel.LandSeam(new DetectorModel.LandSeamFacts(16f, 16f, 8f, true), 8f);
    Check("land: a pre-existing Δ at eps counts as matching -> Introduced", r.Origin == SeamOrigin.Introduced, r.Why);
    r = DetectorModel.LandSeam(new DetectorModel.LandSeamFacts(16f, 0f, 0f, false, Judged: false), 8f);
    Check("land: an undecodable side -> Unjudgeable, never None", r.Verdict == LandSeamVerdict.Unjudgeable, r.Why);
    r = DetectorModel.LandSeam(new DetectorModel.LandSeamFacts(16f, 0f, 0f, true), 8f);
    Check("land: Blind with the other side in the closure is a counted disagreement, still Blind", r.Sight == SeamSight.Blind && r.Why.Contains("disagreement"), r.Why);
}

Console.WriteLine("\nPROOF — landscape seams: a synthetic order through CellBoundaryIndex.MeasurePair (Mutagen-written)");
{
    var root = Path.Combine(Path.GetTempPath(), "proofprobe-seam-" + Environment.ProcessId);
    Directory.CreateDirectory(root);
    var release = SkyrimRelease.SkyrimSE;
    var skyK = new ModKey("Skyrim", ModType.Master);
    var aK = new ModKey("SeamA", ModType.Plugin);
    var bK = new ModKey("SeamB", ModType.Plugin);
    var cK = new ModKey("SeamC", ModType.Plugin);
    FormKey S(uint id) => new(skyK, id);
    var worldK = S(0x800); var topK = S(0x801);
    var texA = S(0x810); var texB = S(0x811);
    // cells (x, y) for x in 0..2, y in 0..1
    FormKey C(int x, int y) => S((uint)(0x900 + y * 16 + x));
    FormKey L(int x, int y) => S((uint)(0xA00 + y * 16 + x));

    static Landscape Land(FormKey key, float h, FormKey tex, Action<sbyte[,]>? shape = null)
    {
        var g = new sbyte[33, 33];
        shape?.Invoke(g);
        var land = new Landscape(key, SkyrimRelease.SkyrimSE) { VertexHeightMap = new LandscapeVertexHeightMap { Offset = h / 8f, HeightMap = new Array2d<sbyte>(g) } };
        foreach (var q in new[] { Quadrant.BottomLeft, Quadrant.BottomRight, Quadrant.TopLeft, Quadrant.TopRight })
        { var bl = new BaseLayer { Header = new LayerHeader { Quadrant = q, LayerNumber = 0 } }; bl.Header.Texture.SetTo(tex); land.Layers.Add(bl); }
        return land;
    }
    static Cell Grid(FormKey key, int x, int y, Landscape land, string edid)
        => new(key, SkyrimRelease.SkyrimSE) { EditorID = edid, Flags = default, Grid = new CellGrid { Point = new P2Int(x, y) }, Landscape = land };
    static Worldspace WorldWith(FormKey wKey, FormKey topK, params Cell[] cells)
    {
        var ws = new Worldspace(wKey, SkyrimRelease.SkyrimSE) { EditorID = "SeamWorld" };
        ws.TopCell = new Cell(topK, SkyrimRelease.SkyrimSE) { Flags = default };
        var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
        var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
        foreach (var c in cells) sub.Items.Add(c);
        blk.Items.Add(sub); ws.SubCells.Add(blk);
        return ws;
    }
    string Write(SkyrimMod mod, params ISkyrimModGetter[] masters)
    {
        var path = Path.Combine(root, mod.ModKey.FileName.String);
        mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
        return path;
    }
    // Raise the INTERIOR of the east edge (x = 32, rows 1..31) by `units` heightmap units: a gradient at x=32 on those
    // rows. The corner vertices are left alone on purpose — a corner is shared by four cells, so a fixture that moved
    // one would put a step on two edges at once and the cases below could not name whose seam is whose.
    static Action<sbyte[,]> EastEdge(sbyte units) => g => { for (int y = 1; y < 32; y++) g[32, y] = units; };
    // Raise the interior of the west edge: rows 1..31 start `units` higher (column 0 accumulates down the rows: +units
    // at row 1, back at row 32), and each of those rows steps back down at x = 1.
    static Action<sbyte[,]> WestEdge(sbyte units) => g => { g[0, 1] = units; g[0, 32] = (sbyte)-units; for (int y = 1; y < 32; y++) g[1, y] = (sbyte)-units; };

    // Skyrim.esm: a 3×2 block of flat cells at 0, matching edges everywhere, except a vanilla seam between (2,0) and (2,1)
    // (the north edge of (2,0) 16 u up — Bethesda ships one).
    var sky = new SkyrimMod(skyK, release);
    sky.Worldspaces.Add(WorldWith(worldK, topK,
        Grid(C(0, 0), 0, 0, Land(L(0, 0), 0, texA), "S00"), Grid(C(1, 0), 1, 0, Land(L(1, 0), 0, texA), "S10"),
        Grid(C(2, 0), 2, 0, Land(L(2, 0), 0, texA, g => { g[1, 32] = 2; g[32, 32] = -2; }), "S20"),   // row 32, x = 1..31, 16 u up: a vanilla seam, corners untouched
        Grid(C(0, 1), 0, 1, Land(L(0, 1), 0, texA), "S01"), Grid(C(1, 1), 1, 1, Land(L(1, 1), 0, texB), "S11"),
        Grid(C(2, 1), 2, 1, Land(L(2, 1), 0, texA), "S21")));
    var skyPath = Write(sky);
    using var skyOv = SkyrimMod.CreateFromBinaryOverlay(skyPath, release);

    // SeamA (masters Skyrim): re-shapes (0,0) AND (1,0) consistently — the shared edge at +2 units (16 u) on both sides;
    // and (0,1)'s east edge alone (+4 units, 32 u) against a (1,1) it left alone — its own seam, Seen.
    var a = new SkyrimMod(aK, release);
    a.Worldspaces.Add(WorldWith(worldK, topK,
        Grid(C(0, 0), 0, 0, Land(L(0, 0), 0, texA, EastEdge(2)), "S00"),
        Grid(C(1, 0), 1, 0, Land(L(1, 0), 0, texA, WestEdge(2)), "S10"),
        Grid(C(0, 1), 0, 1, Land(L(0, 1), 0, texA, EastEdge(4)), "S01")));
    var aPath = Write(a, skyOv);
    using var aOv = SkyrimMod.CreateFromBinaryOverlay(aPath, release);

    // SeamB (masters Skyrim only — blind to A): re-shapes (1,0) from the VANILLA base: west edge back to 0, its own interior
    // texture changed to texB. Loads after A. Under B's view (1,0)'s west edge matches vanilla (0,0) (Δ 0); together: 16 u.
    var b = new SkyrimMod(bK, release);
    b.Worldspaces.Add(WorldWith(worldK, topK, Grid(C(1, 0), 1, 0, Land(L(1, 0), 0, texB), "S10")));
    var bPath = Write(b, skyOv);

    // SeamC (masters Skyrim and A): re-cuts (1,1)'s west edge to +4 units to meet A's (0,1) — the patch shape (joins the seam);
    // and re-states (2,1) with the vanilla seam against (2,0) untouched — Inherited.
    var c = new SkyrimMod(cK, release);
    c.Worldspaces.Add(WorldWith(worldK, topK,
        Grid(C(1, 1), 1, 1, Land(L(1, 1), 0, texB, WestEdge(4)), "S11"),
        Grid(C(2, 1), 2, 1, Land(L(2, 1), 0, texB), "S21")));
    var cPath = Write(c, skyOv, aOv);

    var order = new[] { skyPath, aPath, bPath, cPath };
    var idx = new CellBoundaryIndex();
    var masters = new HashSet<int>[] { new(), new() { 0 }, new() { 0 }, new() { 0, 1 } };
    for (int p = 0; p < order.Length; p++)
    {
        var mod = SkyrimMod.CreateFromBinaryOverlay(order[p], release);
        using (mod as IDisposable) idx.Ingest(p, mod, land: true);
    }
    var closure = DetectorModel.MasterClosure(masters);
    Check("land/index: the west-edge shape decodes as intended (S10 under A: x=0 at 16 u, x=1 back at 0)",
          idx.Cells[C(1, 0)].Land[1].Heights[0, 5] == 16f && idx.Cells[C(1, 0)].Land[1].Heights[1, 5] == 0f && idx.Cells[C(1, 0)].Land[1].Heights[32, 5] == 0f,
          $"{idx.Cells[C(1, 0)].Land[1].Heights[0, 5]} {idx.Cells[C(1, 0)].Land[1].Heights[1, 5]}");
    Check("land/index: S10 has three statements (vanilla, A, B) and S00 two", idx.Cells[C(1, 0)].Land.Count == 3 && idx.Cells[C(0, 0)].Land.Count == 2, $"{idx.Cells[C(1, 0)].Land.Count} {idx.Cells[C(0, 0)].Land.Count}");

    var m = CellBoundaryIndex.MeasurePair(idx.Cells[C(0, 0)], idx.Cells[C(1, 0)], CellEdge.East, closure, 8f)!;
    Check("land/synthetic: S00|S10 — A cut both sides to match, B re-cut S10 from vanilla blind to A -> Introduced, Blind, 16 u",
          m.Verdict == LandSeamVerdict.Seam && m.Origin == SeamOrigin.Introduced && m.Sight == SeamSight.Blind && m.MaxDelta == 16f && m.Later == 2 && !m.OtherSeen,
          $"{m.Verdict} {m.Origin}/{m.Sight} Δ {m.MaxDelta} later {m.Later} view {m.ViewDelta} prev {m.PrevDelta}");
    Check("land/synthetic: S00|S10 — the texture seam is reported apart (A texA | B texB)", m.TextureSeam && m.TexWhy.Contains(texA.ToString()) && m.TexWhy.Contains(texB.ToString()), m.TexWhy);
    m = CellBoundaryIndex.MeasurePair(idx.Cells[C(0, 1)], idx.Cells[C(1, 1)], CellEdge.East, closure, 8f)!;
    Check("land/synthetic: S01|S11 — C (masters A) re-cut S11's west edge to meet A's 32 u -> None (the patch joined it)",
          m.Verdict == LandSeamVerdict.None && m.MaxDelta == 0f && m.Later == 3, $"{m.Verdict} Δ {m.MaxDelta} later {m.Later}");
    // The same pair WITHOUT C: A's own seam against the vanilla neighbour it could see.
    var idx2 = new CellBoundaryIndex();
    for (int p = 0; p < 3; p++) { var mod = SkyrimMod.CreateFromBinaryOverlay(order[p], release); using (mod as IDisposable) idx2.Ingest(p, mod, land: true); }
    m = CellBoundaryIndex.MeasurePair(idx2.Cells[C(0, 1)], idx2.Cells[C(1, 1)], CellEdge.East, closure, 8f)!;
    Check("land/synthetic: S01|S11 without C — A raised its east edge 32 u against a vanilla S11 it could see -> Introduced, Seen",
          m.Verdict == LandSeamVerdict.Seam && m.Origin == SeamOrigin.Introduced && m.Sight == SeamSight.Seen && m.MaxDelta == 32f && m.OtherSeen && m.PrevDelta == 0f,
          $"{m.Verdict} {m.Origin}/{m.Sight} Δ {m.MaxDelta} prev {m.PrevDelta} view {m.ViewDelta}");
    m = CellBoundaryIndex.MeasurePair(idx.Cells[C(2, 0)], idx.Cells[C(2, 1)], CellEdge.North, closure, 8f)!;
    Check("land/synthetic: S20|S21 — C re-stated S21 over a vanilla 16 u seam it did not touch -> Inherited, 31 vertices over eps",
          m.Verdict == LandSeamVerdict.Seam && m.Origin == SeamOrigin.Inherited && m.MaxDelta == 16f && m.PrevDelta == 16f && m.OverEps == 31, $"{m.Verdict} {m.Origin} Δ {m.MaxDelta} prev {m.PrevDelta} over {m.OverEps}");
    m = CellBoundaryIndex.MeasurePair(idx.Cells[C(1, 0)], idx.Cells[C(2, 0)], CellEdge.East, closure, 8f)!;
    Check("land/synthetic: S10|S20 — B's re-cut leaves the untouched east edge matching vanilla S20 -> None", m.Verdict == LandSeamVerdict.None, $"{m.Verdict} Δ {m.MaxDelta}");
    m = CellBoundaryIndex.MeasurePair(idx.Cells[C(1, 0)], idx.Cells[C(1, 1)], CellEdge.North, closure, 8f)!;
    Check("land/synthetic: S10|S11 — heights match, base textures texB | texB -> no texture seam either", m.Verdict == LandSeamVerdict.None && !m.TextureSeam, m.TexWhy);
    var vanillaPair = CellBoundaryIndex.MeasurePair(idx.Cells[C(0, 0)], idx.Cells[C(0, 1)], CellEdge.North, closure, 8f)!;
    Check("land/synthetic: S00|S01 — A cut the east edges only; the shared north edge still matches -> None", vanillaPair.Verdict == LandSeamVerdict.None, $"Δ {vanillaPair.MaxDelta}");
    try { Directory.Delete(root, true); } catch { }
}

BosProof.Run(Check);
StateProof.Run(Check);

Console.WriteLine();
Console.WriteLine($"==== {pass} passed, {fail} failed ====");
if (fail > 0) Console.WriteLine("   failed: " + string.Join(", ", failures));
return fail == 0 ? 0 : 1;
