namespace RunControl;

/// <summary>verify-bundle's checks of evidence derived after measurement (bundleenrich.py's verify): sources present
/// and hashing as cited, every row citing its reads, names read from bytes the plan records, the disposition unchanged
/// since enrichment, and the names step's statement agreeing with the files.</summary>
public static class EnrichmentVerify
{
    static readonly string[] NamesStatuses = ["enriched", "partial", "refused", "skipped"];

    // (v or "") as text.
    static string Text(object? v) => PyValue.Truthy(v) ? Ev.Str(v) : "";

    public static void Verify(string d, PyDict manifest, PyDict plan, Action<string, object?, string> defect, Func<string, string?> sha)
    {
        var planShas = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in Ev.Items(plan.Get("arms")))
            foreach (var x in Ev.Items(Ev.OrEmpty(Ev.G(a, "requested")).Get("plugins")))
                planShas.Add(PyText.Lower(Text(Ev.G(x, "sha256"))));
        string dispFile = PyPath.Join(d, "disposition.json");
        string? dispNow = Ev.Exists(dispFile) ? sha(dispFile) : null;
        foreach (var e in Ev.Items(manifest.Get("evidence")).Select(PyValue.Dict))
        {
            if (!Ev.Eq(e.Get("kind"), "enrichment") || !Ev.Exists(PyPath.Join(d, Ev.Str(e["path"])))) continue;
            object? doc = PyJson.LoadEvalFile(PyPath.Join(d, Ev.Str(e["path"])));
            var dict = doc as PyDict;
            if (Ev.Eq(e.Get("role"), "record"))
            {
                if (dict == null || !Ev.Eq(dict.Get("dispositionSha256"), dispNow))
                    defect("enrichment-stale", e["path"],
                        "the disposition is not the one this enrichment was derived beside, so a count or claim changed after enrichment");
                continue;
            }
            if (Ev.Eq(e.Get("role"), "capture")) continue;
            object? kind = e.Get("enrichment");
            object? src = dict?.Get("derivedFrom");
            if (kind is "record-rows" or "order-variant")
            {
                if (!PyValue.Truthy(src))
                {
                    defect("enrichment-source-missing", e["path"], "the derived rows do not cite the bytes they came from");
                    continue;
                }
                foreach (var x in PyValue.Iter(src))
                {
                    string p = PyPath.Join(d, Text(Ev.G(x, "path")));
                    if (!PyValue.Truthy(Ev.G(x, "path")) || !File.Exists(p) || !Ev.Eq(sha(p), Ev.G(x, "sha256")))
                        defect("enrichment-source-changed", e["path"],
                            $"a cited source {Ev.Str(Ev.G(x, "path"))} is absent from the bundle or does not hash as cited");
                }
                long bare = 0;
                foreach (var c in Ev.Items(dict!.Get("Conflicts")))
                    foreach (var n in Ev.Items(Ev.G(c, "Necessary")))
                        if (!PyValue.Truthy(Ev.G(n, "Reads"))) bare++;
                // a reversed setup derived from a tree capture cites the transcripts, not single reads
                if (bare > 0 && !(Ev.Eq(kind, "order-variant") && Ev.Eq(dict.Get("readForm"), "tree")))
                    defect("enrichment-source-missing", e["path"], $"{bare} derived row(s) do not cite the reads they came from");
            }
            else if (Ev.Eq(kind, "order-geometry"))
            {
                foreach (var x in PyValue.Truthy(src) ? PyValue.Iter(src) : new object?[] { null })
                {
                    var row = Ev.OrEmpty(x);
                    string p = PyPath.Join(d, Text(row.Get("path")));
                    if (!PyValue.Truthy(x) || !PyValue.Truthy(row.Get("path")) || !File.Exists(p) || !Ev.Eq(sha(p), row.Get("sha256")))
                        defect("enrichment-source-changed", e["path"],
                            $"a cited emit {Ev.Str(row.Get("path"))} is absent from the bundle or does not hash as cited");
                }
            }
            else if (Ev.Eq(kind, "names"))
            {
                foreach (var x in Ev.Items(src))
                    if (!planShas.Contains(PyText.Lower(Text(Ev.G(x, "sha256")))))
                        defect("enrichment-source-foreign", e["path"],
                            $"names were read from {Ev.Str(Ev.G(x, "plugin"))} with bytes the plan does not record");
            }
        }
        VerifyNamesStatus(d, manifest, defect);
    }

    // The names step's statement against the files; a manifest with no statement predates the step.
    static void VerifyNamesStatus(string d, PyDict manifest, Action<string, object?, string> defect)
    {
        object? st = Ev.OrEmpty(manifest.Get("enrichmentStatus")).Get("names");
        if (st == null) return;
        var stated = st as PyDict;
        string sentence = stated != null ? Text(stated.Get("sentence")) : "";
        object? status = stated?.Get("status");
        if (status is not string word || !NamesStatuses.Contains(word) || PyValue.Strip(sentence).Length == 0 || sentence.Contains('\n'))
        {
            defect("enrichment-status-unstated", "manifest.json",
                $"the names step's statement has status {PyText.Repr(status)} and no one-sentence reason, so a reader cannot tell "
                + "why the bundle has or lacks names");
            return;
        }
        PyDict? row = null;
        foreach (var e in Ev.Items(manifest.Get("evidence")).Select(PyValue.Dict))
            if (Ev.Eq(e.Get("kind"), "enrichment") && Ev.Eq(e.Get("enrichment"), "names") && !PyValue.Truthy(e.Get("role"))) { row = e; break; }
        if ((word is "enriched" or "partial") != (row != null))
        {
            defect("enrichment-status-contradiction", "manifest.json",
                $"the names step says {word} but the bundle {(row != null && row.Count > 0 ? "lists" : "does not list")} the names files");
            return;
        }
        if (row == null) return;
        object? doc = PyJson.LoadEvalFile(PyPath.Join(d, Ev.Str(row["path"])));
        var unread = (PyValue.Truthy(doc) ? doc : new PyDict()) is PyDict names ? names.Get("unread") : null;
        var counts = Ev.OrEmpty(stated!.Get("counts"));
        if (unread is not PyDict lists)
        {
            defect("enrichment-status-mismatch", row["path"], "names.json does not list what it could not read");
            return;
        }
        long objects = PyValue.Len(Ev.OrList(lists.Get("objects"))), bases = PyValue.Len(Ev.OrList(lists.Get("bases")));
        string want = objects != 0 || bases != 0 ? "partial" : "enriched";
        if (word != want || !Ev.Eq(counts.Get("objectsUnread"), objects) || !Ev.Eq(counts.Get("basesUnread"), bases))
            defect("enrichment-status-mismatch", "manifest.json",
                $"the names step says {word} with objectsUnread {Ev.Str(counts.Get("objectsUnread"))}, basesUnread {Ev.Str(counts.Get("basesUnread"))}, "
                + $"names.json lists {objects} unread object(s) and {bases} unread base record(s)");
    }
}
