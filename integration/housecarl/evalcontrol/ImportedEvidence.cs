using RunControl;

namespace EvalControl;

/// <summary>The overlay disposition's `importedEvidence` block and its STATUS.md section (moved from runcontrol's
/// disposition with the record imports): where each credited import's evidence came from, as its receipt records it.
/// Only an import has an origin, and only this module imports.</summary>
public static class ImportedEvidence
{
    /// <summary>The origins an import may declare; any other reads unknown.</summary>
    public static readonly string[] Origins = ["fresh", "reused"];

    /// <summary>Where each credited import's evidence came from, as its receipt records it.</summary>
    public static List<object?> Rows(Ledger ledger, PyDict receipts, PyDict badKeys)
    {
        var rows = new List<object?>();
        var entries = ledger.Entries;
        foreach (string k in PyOrder.SortedByCodePoint(entries.Keys))
        {
            var e = PyValue.Dict(entries[k]);
            var r = Ev.OrEmpty(receipts.Get(k));
            if (!EvalStore.IsTerminalOk(e.Get("status")) || badKeys.ContainsKey(k) || r.Get("imported") is not PyDict imp
                || !PyValue.Truthy(Ev.OrEmpty(r.Get("validation")).Get("accepted"))) continue;
            string origin = imp.Get("origin") is string o && Origins.Contains(o) ? o : "unknown";
            var row = new PyDict
            {
                ["checkId"] = e["checkId"], ["armId"] = e["armId"], ["receiptId"] = r.Get("receiptId"), ["origin"] = origin,
                ["capturedUtc"] = imp.Get("capturedUtc"),
            };
            if (origin == "reused")
            {
                row["reusedFrom"] = imp.Get("reusedFrom");
                row["reusedFromVerified"] = PyValue.Truthy(imp.Get("reusedFromVerified"));
                // the state is read from the identity limit the import rule wrote, never derived again here
                PyDict? lim = null;
                foreach (var x in Ev.Items(Ev.OrEmpty(r.Get("validation")).Get("identityLimits")))
                    if (x is PyDict xd && PyValue.Truthy(xd.Get("armCandidate"))) { lim = xd; break; }
                if (lim != null)
                {
                    row["sourceDisclosure"] = PyValue.Truthy(lim.Get("capturedAgainst")) ? "other-file" : "unstated";
                    row["capturedAgainst"] = PyValue.Truthy(lim.Get("capturedAgainst")) ? lim["capturedAgainst"] : null;
                    row["armCandidate"] = lim.Get("armCandidate");
                }
                row["reusedWithin"] = imp.Get("reusedWithin") is "this-evaluation" or "another-evaluation" ? imp["reusedWithin"] : "unstated";
            }
            else if (origin == "unknown")
                row["note"] = "imported before an import had to declare its origin: whether this evidence was captured "
                    + "for this plan is not recorded";
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>The block added to a disposition runcontrol built, at the place it had there (before `ledgerCounts`),
    /// so an overlay's disposition keeps its bytes.</summary>
    public static void Add(PyDict disposition, Ledger ledger, PyDict receipts, PyDict badKeys)
    {
        bool counted = disposition.ContainsKey("ledgerCounts");
        object? counts = counted ? disposition.Pop("ledgerCounts") : null;
        disposition["importedEvidence"] = Rows(ledger, receipts, badKeys);
        if (counted) disposition["ledgerCounts"] = counts;
    }

    /// <summary>STATUS.md's section for the block, after the completed catalogues (StatusMdSections.AfterCatalogues);
    /// no lines when the disposition credits no import.</summary>
    public static IEnumerable<string> Section(PyDict disp)
    {
        var L = new List<string>();
        if (!PyValue.Truthy(disp.Get("importedEvidence"))) return L;
        L.AddRange([
            "", "## Imported evidence and where it came from", "",
            "Origin is checked for accidental relabelling only: a capture token and timestamps are public and "
            + "rewritable, so this is not proof of authenticity.", "",
            "| check | arm | origin |", "|---|---|---|"]);
        foreach (var x in PyValue.Iter(disp["importedEvidence"]).Select(PyValue.Dict))
        {
            string said;
            if (Ev.Eq(x["origin"], "fresh")) said = "fresh: captured for this plan's arm (capture token and time checked)";
            else if (Ev.Eq(x["origin"], "reused"))
            {
                var src = Ev.OrEmpty(x.Get("reusedFrom"));
                string where = Ev.Eq(x.Get("reusedWithin"), "this-evaluation")
                    ? $"reused from setup `{P(src.Get("armId"))}` of THIS plan; not measured for this setup"
                    : $"reused from plan `{P(src.Get("planId"))}` arm `{P(src.Get("armId"))}`; not measured for this plan";
                said = $"{where} (result `{Head(P(src.Get("resultSha256")), 12)}`, declared, not verified)"
                    + (!Ev.Eq(x.Get("reusedWithin"), "unstated") ? "" : "; the receipt does not say whether the source is this evaluation or another")
                    + (Ev.Eq(x.Get("sourceDisclosure"), "other-file")
                        ? "; the capture states it was measured against " + $"`{P(x.Get("capturedAgainst"))}`"
                        : Ev.Eq(x.Get("sourceDisclosure"), "unstated")
                            ? "; the capture names NO candidate file, so which file it was measured against is not stated at all"
                            : "");
            }
            else said = "origin not recorded: imported before origin was required";
            L.Add($"| `{P(x["checkId"])}` | {P(x["armId"])} | {said} |");
        }
        return L;
    }

    // str(v), as an f-string prints a value.
    static string P(object? v) => PyText.Str(v);

    // text[:n], counted in code points as Python counts.
    static string Head(string s, int n)
    {
        int i = 0;
        for (int count = 0; i < s.Length && count < n; count++)
            i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
        return s[..i];
    }
}
