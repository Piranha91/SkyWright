using HousecarlCore;

namespace SkyWright.Engine;

/// <summary>A loaded plugin mastering an empty dummy: how many of its records sit in the dummy's id space (overrides of
/// forms the dummy does not define, which load as new records) and how many links point into it.</summary>
public sealed record DummyMasterUse(string Plugin, string? Provider, string Dummy, int RecordsInDummySpace, int LinksIntoDummy);

/// <summary>Shape 5 — a master or a config naming an empty dummy plugin. The dummy stands in for a mod that is off;
/// whatever names its forms names nothing. 5a: a plugin masters a dummy. 5b: a runtime config names a form in one,
/// with the framework's handling cited where it has been read at the installed version and unasserted otherwise.</summary>
public static class DummyTargetLook
{
    public const string Shape = "dummyTarget";

    /// <summary>What each framework does with a form in a loaded plugin that does not define it, and how that is known.</summary>
    public static (string Consequence, string Established, LookKind Kind) Framework(string framework) => framework switch
    {
        "BOS" => ("BOS accepts the line at load and finds nothing at runtime, so that swap never applies.",
                  "source: BOS v3.1.1 SwapData.cpp GetSwapBase (LookupByID null) and Manager.cpp GetSwapData; not seen in game", LookKind.Finding),
        "KID" => ("KID drops a keyword that does not resolve ('FAIL - keyword doesn't exist'); a filter that does not resolve is dropped ('SKIP - form doesn't exist'), and the whole line is skipped when that leaves an all-filter list short or an exclusion or match list empty ('Invalid/missing filters'); with other match filters left, the line distributes on those alone.",
                  "source: KID v3.4.0 (the installed 3.4.0.1 VR build) KeywordData.h formID_to_form and LookupKeywords; not seen in game", LookKind.Finding),
        "SPID" => ("The file names a form in an empty plugin. SPID logs such a filter 'SKIP - formID doesn't exist'; what that does to the line is untested.",
                   "unasserted: SPID 7.1.3 (installed) has no source tag to cite; the message is from a VR game log of another instance (2026-05-24)", LookKind.Look),
        "OAR" => ("The condition names a form in an empty plugin. What OAR does with an unresolved form (the condition fails, or the submod is marked invalid) is untested.",
                  "unasserted: OAR 2.3.6 (installed) has no tagged source to cite; the skill's corpus is 3.0.0 and covers a missing condition addon, not a missing form", LookKind.Look),
        "SkyPatcher" => ("The file names a form in an empty plugin; a dummy passes SkyPatcher's file-name gate. What SkyPatcher does with the form is untested.",
                         "unasserted: SkyPatcher is closed source; its log shows only the file gate ('not found or is not a valid plugin file, skipping config file')", LookKind.Look),
        _ => ("The file names a form in an empty plugin; what the SKSE plugin that reads it does is untested.", "unasserted: the framework is not identified", LookKind.Look),
    };

    public static LookResult Evaluate(IReadOnlyCollection<string> dummies, IEnumerable<DummyMasterUse> masterUses, IEnumerable<ConfigFormRef> refs,
                                      IReadOnlyDictionary<string, string?> providerOfPlugin)
    {
        var r = new LookResult(Shape);
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in masterUses.OrderBy(u => u.Plugin, StringComparer.OrdinalIgnoreCase))
        {
            named.Add(u.Dummy);
            var dprov = providerOfPlugin.GetValueOrDefault(u.Dummy);
            r.Rows.Add(new LookRow(Shape, "masters-dummy", LookKind.Look, new[] { u.Provider ?? "?", dprov ?? "?" }, new[] { u.Plugin, u.Dummy },
                new[] { u.Plugin },
                $"{u.Plugin} ({u.Provider}) masters {u.Dummy}, an empty dummy plugin ({dprov}); {u.RecordsInDummySpace} of its records sit in the dummy's id space and {u.LinksIntoDummy} of its links point into it.",
                "Its records in the dummy's space load as new records that nothing else names, and its links into the dummy point at nothing, so what it changes for that mod does not reach the game.",
                "source: xEdit's injected-record model as the errors family transcribes it (docs/architecture/check-families.md); not tested in game"));
        }
        foreach (var g in refs.Where(x => dummies.Contains(x.Plugin)).GroupBy(x => (x.File, x.Provider, x.Framework, x.Plugin))
                              .OrderBy(g => g.Key.File, StringComparer.OrdinalIgnoreCase))
        {
            named.Add(g.Key.Plugin);
            var (cons, est, kind) = Framework(g.Key.Framework);
            var lines = string.Join(", ", g.Select(x => x.Line).Distinct().Take(8)) + (g.Select(x => x.Line).Distinct().Count() > 8 ? ", …" : "");
            var dprov = providerOfPlugin.GetValueOrDefault(g.Key.Plugin);
            r.Rows.Add(new LookRow(Shape, "config-names-dummy", kind, new[] { g.Key.Provider ?? "?", dprov ?? "?" }, new[] { g.Key.Plugin },
                new[] { $"{g.Key.Provider}\\{g.Key.File}" },
                $"{g.Key.File} ({g.Key.Provider}, {g.Key.Framework}) names {g.Count()} form(s) in {g.Key.Plugin}, an empty dummy plugin ({dprov}) (lines {lines}).",
                cons, est));
        }
        foreach (var d in dummies.Where(d => !named.Contains(d)).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            r.SetAside("an empty dummy nothing names", d);
        r.Number();
        return r;
    }
}
