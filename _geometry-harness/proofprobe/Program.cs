using System.Xml.Linq;
using HarnessShared;

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
}

// ---------------------------------------------------------------------------------------
// The synthetic load order — the thing everything else will now be validated against.
// ---------------------------------------------------------------------------------------
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

Console.WriteLine();
Console.WriteLine($"==== {pass} passed, {fail} failed ====");
if (fail > 0) Console.WriteLine("   failed: " + string.Join(", ", failures));
return fail == 0 ? 0 : 1;
