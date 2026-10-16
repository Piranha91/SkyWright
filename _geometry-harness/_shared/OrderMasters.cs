using HousecarlCore;

namespace HarnessShared;

// AN ORDER WHOSE PLUGIN LACKS A MASTER IS REFUSED, BY EVERY PROBE, IN ONE SENTENCE (ruling 12, 2026-10-15).
//
// The game will not start on a missing master, so nothing measured over such an order describes a game anyone can run.
// Every probe that reads a load order refuses it: the shared reader (HarnessOrderCli.Resolve, all three sources) and the
// probes that build the order from Mo2LoadOrder.Build themselves call this file. The caller prints the sentence and exits
// 2, writing nothing. The sentence starts "MISSING MASTER" so evalrun's refusal marker finds it, names the first offending
// plugin in load order and its absent masters, and ends with what to try, chosen from the data (installed or not, which
// switch caused it), never guessed.
//
// Not in Mo2LoadOrder.Build: that is core, shared with the MCP server, whose errors check loads such an order on purpose
// and reports the missing master as its finding. Exempt probes, each for its own reason in its own header: floorgapprobe
// --plugin without --winning (one plugin by path), floorprobe (a missing-master finder), patchprobe and poolprobe (patch
// applicability is their output), adviceprobe (metadata only).

/// <summary>One loaded plugin whose declared masters are not all in the loaded set.</summary>
public sealed record MasterGap(string Plugin, IReadOnlyList<string> Masters);

public static class OrderMasters
{
    /// <summary>The plugins in <paramref name="loaded"/> (load order) that declare a master the set does not hold. A null
    /// master list is an unreadable header: it makes no claim either way, and the caller has already warned about it.</summary>
    public static List<MasterGap> Gaps(IReadOnlyList<(string Name, IReadOnlyList<string>? Masters)> loaded)
    {
        var present = new HashSet<string>(loaded.Select(l => l.Name), StringComparer.OrdinalIgnoreCase);
        var gaps = new List<MasterGap>();
        foreach (var (name, masters) in loaded)
        {
            if (masters is null) continue;
            var missing = masters.Where(m => !present.Contains(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0) gaps.Add(new MasterGap(name, missing));
        }
        return gaps;
    }

    /// <summary>The one sentence for <paramref name="gaps"/> (non-empty): the first gap named in full with its remedy, the
    /// rest counted.</summary>
    public static string Sentence(IReadOnlyList<MasterGap> gaps, Func<MasterGap, string> remedy)
    {
        var g = gaps[0];
        var more = gaps.Count == 1 ? ""
            : $" (and {gaps.Count - 1} more plugin(s) lack a master, first '{gaps[1].Plugin}' needing {Names(gaps[1].Masters)})";
        return $"MISSING MASTER - '{g.Plugin}' masters {Names(g.Masters)}, which this order does not load{more}, and the game "
             + $"refuses to start on a missing master; {remedy(g)}, then run again.";
    }

    /// <summary>The remedy for a gap in the profile's own order: switch the master on when a layer of the install holds
    /// it, install it when none does, or switch the dependent off.</summary>
    public static Func<MasterGap, string> ProfileRemedy(Mo2Composition comp, string modsDir, string dataDir, string overwriteDir)
    {
        IReadOnlyCollection<string>? installed = null;
        return g =>
        {
            installed ??= Mo2LoadOrder.AllPluginFileNames(comp, modsDir, dataDir, overwriteDir);
            var (notInstalled, inactive) = Mo2LoadOrder.SplitUnsatisfiedMasters(installed, g.Masters);
            var steps = new List<string>();
            if (notInstalled.Count > 0) steps.Add($"install {Names(notInstalled)}");
            if (inactive.Count > 0) steps.Add($"switch {Names(inactive)} on in MO2");
            return $"{string.Join(" and ", steps)}, or switch '{g.Plugin}' off";
        };
    }

    /// <summary>For a probe that built the profile's order itself (Mo2LoadOrder.Build): the refusal sentence, or null when
    /// every plugin's masters are loaded. Header reads only.</summary>
    public static string? RefusalForProfile(string profileDir, string modsDir, string dataDir, string overwriteDir,
                                            IReadOnlyList<string> orderedPaths)
    {
        var gaps = Gaps(orderedPaths.Select(p => (Path.GetFileName(p), PluginHeader.TryRead(p)?.Masters)).ToList());
        if (gaps.Count == 0) return null;
        return Sentence(gaps, ProfileRemedy(Mo2LoadOrder.ReadComposition(profileDir), modsDir, dataDir, overwriteDir));
    }

    public static string Names(IReadOnlyList<string> names)
    {
        var q = names.Select(n => $"'{n}'").ToList();
        return q.Count == 1 ? q[0] : string.Join(", ", q.Take(q.Count - 1)) + " and " + q[^1];
    }
}
