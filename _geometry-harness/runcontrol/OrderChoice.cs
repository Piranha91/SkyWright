namespace RunControl;

/// <summary>Whether the order of an evaluated pair is a choice (bundleenrich.order_choice and is_master_file), the
/// controller's one spelling of the pair rule: a plugin listing the other as a master loads after it; else exactly
/// one master file loads first; else the order is free. The header facts are the owner's (PluginHeader, or the
/// same facts as a probe emit recorded them). The shared OrderRuleSet.OrderFixedBy states a wider rule (masters
/// followed transitively, the base-game masters, pinned plugins) under other names, so it is not the rule the
/// bundle's `order` statement was written by and is not called here.</summary>
public static class OrderChoice
{
    /// <summary>One plugin's header facts: its direct masters, and the master flag as recorded (any JSON value).</summary>
    public sealed record Head(IReadOnlyList<string> Masters, object? Master);

    /// <summary>forced: true, false, or null when a header fact is missing (then missing says which).</summary>
    public sealed record Choice(bool? Forced, string? Rule, string? LoadsFirst, string? Missing);

    /// <summary>A master file as the game loads it: the ESM flag, or the .esm/.esl extension (an ESL flag alone does not).</summary>
    public static bool IsMasterFile(string name, Head? head)
    {
        string low = PyText.Lower(name);
        return PyValue.Truthy(head?.Master) || low.EndsWith(".esm", StringComparison.Ordinal) || low.EndsWith(".esl", StringComparison.Ordinal);
    }

    /// <summary>heads: plugin name lower-cased to its facts.</summary>
    public static Choice Decide(string a, string b, IReadOnlyDictionary<string, Head> heads)
    {
        string la = PyText.Lower(a), lb = PyText.Lower(b);
        heads.TryGetValue(la, out var ha);
        heads.TryGetValue(lb, out var hb);
        if (ha == null || hb == null)
        {
            var gone = new List<string>();
            if (ha == null) gone.Add(a);
            if (hb == null) gone.Add(b);
            return new Choice(null, null, null, $"no recorded master list or flags for {string.Join(" or ", gone)}, and the file is not at hand at "
                + "the recorded bytes, so whether the order is a choice is not said");
        }
        var ma = ha.Masters.Select(PyText.Lower).ToHashSet(StringComparer.Ordinal);
        var mb = hb.Masters.Select(PyText.Lower).ToHashSet(StringComparer.Ordinal);
        if (ma.Contains(lb) || mb.Contains(la)) return new Choice(true, "master", ma.Contains(lb) ? b : a, null);
        bool fa = IsMasterFile(a, ha), fb = IsMasterFile(b, hb);
        if (fa != fb) return new Choice(true, "ESM flag", fa ? a : b, null);
        return new Choice(false, fa ? "free (both ESM-flagged)" : "free (both non-master)", null, null);
    }
}
