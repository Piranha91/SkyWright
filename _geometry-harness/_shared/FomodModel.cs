using System.Xml.Linq;

namespace HarnessShared;

// THE FOMOD DECISION MODEL — extracted so it can be PROVEN.
//
// This logic took five corrections in one session, each one found by reading real output and each
// one changing the answer materially:
//
//   1. destination=""  is the Data ROOT, not an absent attribute defaulting to the source path.
//                      Collapsing them broke path matching for 74 of 83 FOMODs.
//   2. <type name="NotUsable"/> patterns carry fileDependency too, and mean the OPPOSITE.
//                      Reading all of them inverted the advice.
//   3. SelectExactlyOne / SelectAtMostOne — a taken sibling rules the others out.
//   4. installStep <visible><flagDependency> — a step whose branch was not taken was NEVER OFFERED.
//   5. ...except when the taken sibling's OWN condition does not hold and an untaken one's does.
//      That is a wrong choice at install time, and rule 3 was hiding it.
//
// While it lived inside a function that shells out to 7-Zip and stats a mod folder, none of it could
// be tested: proving rule 5 needed a real archive containing a real FOMOD in a real load order. As a
// pure function of (XML, what is on disk, what is active) every rule is one small case, and
// `proofprobe` runs all of them on a fresh checkout with no game data — the shape CONTRIBUTING.md
// asks proof to take, and the shape `crossprobe` already set for the geometry work.
//
// The caller owns the I/O and the two predicates; this owns the rules.

/// <param name="Files">Leaf names of what the option delivers — reporting only.</param>
/// <param name="Conditions">Plugins this option's own RECOMMENDED/REQUIRED patterns name as Active.</param>
/// <param name="Judgeable">False when every destination is the bare Data root, which cannot be told
/// apart from having been taken. Counted, never silently read as installed.</param>
public sealed record FomodOption(
    string Name, string Step, string Group, string GroupType, string Description,
    IReadOnlyList<string> Files, IReadOnlyList<string> Conditions,
    bool Selected, bool Judgeable, bool RuledOut, bool WrongVariant);

/// <param name="PathsTrusted">The self-test. False means this code could not match archive paths to
/// installed paths for this mod, and none of its verdicts may be believed.</param>
public sealed record FomodParse(
    IReadOnlyList<FomodOption> Options,
    int RequiredFiles, int RequiredFound, int OptionsNotOffered, bool PathsTrusted);

public static class FomodModel
{
    /// <param name="present">Did this destination land on disk? The caller owns the "" (Data root) rule.</param>
    /// <param name="active">Is this plugin filename active in the target profile?</param>
    public static FomodParse Parse(XDocument doc, Func<string, bool> present, Func<string, bool> active)
    {
        var options = new List<FomodOption>();
        int notOffered = 0;
        var root = doc.Root;
        if (root is null) return new FomodParse(options, 0, 0, 0, false);

        var required = Dests(root.Element("requiredInstallFiles")).ToList();
        int requiredFound = required.Count(present);

        // Flags are replayed in document order from the options that were ACTUALLY taken. Replaying
        // them from options nobody took would light up branches the installer never showed.
        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        bool DepsHold(XElement? deps)
        {
            if (deps is null) return true;
            var op = ((string?)deps.Attribute("operator") ?? "And").Trim();
            var tests = deps.Elements("flagDependency")
                .Select(d => string.Equals(flags.GetValueOrDefault((string?)d.Attribute("flag") ?? "", ""),
                                           (string?)d.Attribute("value") ?? "", StringComparison.OrdinalIgnoreCase))
                .ToList();
            // A visibility block this cannot fully evaluate is treated as VISIBLE, so the failure
            // mode is a reported option rather than a silently dropped one.
            if (tests.Count == 0) return true;
            return op.Equals("Or", StringComparison.OrdinalIgnoreCase) ? tests.Any(t => t) : tests.All(t => t);
        }

        foreach (var step in root.Element("installSteps")?.Elements("installStep") ?? Enumerable.Empty<XElement>())
        {
            var stepName = (string?)step.Attribute("name") ?? "";
            if (!DepsHold(step.Element("visible")?.Element("dependencies")))
            {
                notOffered += step.Descendants("plugin").Count();      // rule 4
                continue;
            }

            foreach (var grp in step.Element("optionalFileGroups")?.Elements("group") ?? Enumerable.Empty<XElement>())
            {
                var grpName = (string?)grp.Attribute("name") ?? "";
                var grpType = (string?)grp.Attribute("type") ?? "";
                var groupOpts = new List<(XElement El, FomodOption O)>();

                foreach (var pl in grp.Element("plugins")?.Elements("plugin") ?? Enumerable.Empty<XElement>())
                {
                    var dests = Dests(pl.Element("files")).ToList();

                    // rule 2 — only RECOMMENDED / REQUIRED patterns say "this option is for you".
                    var conds = new List<string>();
                    foreach (var pat in pl.Descendants("pattern"))
                    {
                        var typeName = (string?)pat.Element("type")?.Attribute("name") ?? "";
                        if (!typeName.Equals("Recommended", StringComparison.OrdinalIgnoreCase)
                         && !typeName.Equals("Required", StringComparison.OrdinalIgnoreCase)) continue;
                        foreach (var d in pat.Descendants("fileDependency"))
                        {
                            var st = (string?)d.Attribute("state") ?? "Active";
                            if (!st.Equals("Active", StringComparison.OrdinalIgnoreCase)) continue;
                            var f = (string?)d.Attribute("file");
                            if (!string.IsNullOrWhiteSpace(f)) conds.Add(f!.Trim());
                        }
                    }

                    groupOpts.Add((pl, new FomodOption(
                        (string?)pl.Attribute("name") ?? "", stepName, grpName, grpType,
                        (string?)pl.Element("description") ?? "",
                        dests.Select(d => Path.GetFileName(d.Replace('/', '\\').TrimEnd('\\')))
                             .Where(x => x.Length > 0).ToList(),
                        conds.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                        Selected: dests.Count > 0 && dests.Any(present),
                        Judgeable: dests.Any(d => Norm(d).Length > 0),
                        RuledOut: false, WrongVariant: false)));
                }

                bool exclusive = grpType.Equals("SelectExactlyOne", StringComparison.OrdinalIgnoreCase)
                              || grpType.Equals("SelectAtMostOne", StringComparison.OrdinalIgnoreCase);
                bool siblingTaken = exclusive && groupOpts.Any(x => x.O.Selected);
                // rule 5 — does the option that WAS taken actually match this load order?
                bool takenHoldsUp = groupOpts.Where(x => x.O.Selected)
                    .Any(x => x.O.Conditions.Count == 0 || x.O.Conditions.Any(active));

                foreach (var (el, o) in groupOpts)
                {
                    bool wrong = siblingTaken && !o.Selected && !takenHoldsUp && o.Conditions.Any(active);
                    options.Add(o with
                    {
                        RuledOut = siblingTaken && !o.Selected && !wrong && takenHoldsUp,   // rule 3
                        WrongVariant = wrong,
                    });
                    if (!o.Selected) continue;
                    foreach (var fl in el.Element("conditionFlags")?.Elements("flag") ?? Enumerable.Empty<XElement>())
                    {
                        var name = (string?)fl.Attribute("name");
                        if (!string.IsNullOrWhiteSpace(name)) flags[name!.Trim()] = fl.Value.Trim();
                    }
                }
            }
        }

        // Two arms, because the first does not always apply. A FOMOD with no requiredInstallFiles
        // installs everything through options, so there is nothing unconditional to check — but the
        // mod folder is full, so SOME option's destination must match something on disk.
        bool trusted = required.Count > 0
            ? requiredFound * 2 >= required.Count
            : options.Count > 0 && options.Any(o => o.Selected);

        return new FomodParse(options, required.Count, requiredFound, notOffered, trusted);
    }

    /// <summary>A file/folder element's destination. rule 1 — an ABSENT destination defaults to the
    /// source path; an EXPLICITLY EMPTY one means the Data root, and they are not the same.</summary>
    public static IEnumerable<string> Dests(XElement? files)
    {
        if (files is null) yield break;
        foreach (var e in files.Elements())
        {
            var name = e.Name.LocalName;
            if (name != "file" && name != "folder") continue;
            var chosen = (string?)e.Attribute("destination") ?? (string?)e.Attribute("source");
            if (chosen is not null) yield return chosen;
        }
    }

    public static string Norm(string p) => p.Replace('/', '\\').Trim('\\').ToLowerInvariant();
}
