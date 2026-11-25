using RunControl;

namespace EvalControl;

/// <summary>What `evalcontrol verify` judges of the rows runcontrol's verify-bundle walks and hashes but does not judge,
/// because they are this module's: a record import's receipt, and the rows derived from a record import after
/// measurement (the record-rows and order-variant enrichments, checked here as runcontrol's EnrichmentVerify checked them
/// before they moved). Every other row runcontrol did not judge is a defect here too: nothing in an overlay bundle goes
/// unjudged.</summary>
public static class RecordEvidence
{
    /// <summary>The enrichment kinds derived from a record import.</summary>
    public static readonly string[] EnrichmentKinds = ["record-rows", "order-variant"];

    /// <summary>The adapter kind of a record import's receipt.</summary>
    public const string ImportKind = "mcp-import";

    // (v or "") as text.
    static string Text(object? v) => PyValue.Truthy(v) ? Ev.Str(v) : "";

    /// <summary>runcontrol's verdict on a bundle (verify.json's `bundle` block, changed in place) with the rows it did
    /// not judge judged here: its notChecked rows and its defects of a kind it does not know are taken out, and they and
    /// every accepted record import of the ledger are judged by Judge, each fault to `defect`. Returns runcontrol's
    /// other defects (left in the block) and the paths judged (the block's judgedByTheModule).</summary>
    public static (List<object?> kept, List<string> judged) Rejudge(string d, PyDict block, PyDict manifest, PyDict plan,
        Action<string, object?, string> defect)
    {
        var judged = Ev.Items(block.Get("notChecked")).Select(x => Ev.Str(Ev.G(x, "path"))).ToList();
        var kept = new List<object?>();
        foreach (var x in Ev.Items(block.Get("defects")))
            if (Ev.Eq(Ev.G(x, "kind"), "evidence-kind-unknown")) judged.Add(Ev.Str(Ev.G(x, "path")));
            else kept.Add(x);
        // every accepted record import, whether or not runcontrol named it (it names a receipt's kind only in a bundle
        // older than its registry)
        foreach (var (k, entry) in Ev.OrEmpty(Ev.OrEmpty(PyJson.LoadEvalFile(PyPath.Join(d, "ledger.json"))).Get("entries")))
            if (EvalStore.IsTerminalOk(Ev.G(entry, "status"))
                && Ev.Eq(Ev.OrEmpty(Ev.OrEmpty(PyJson.LoadEvalFile(PyPath.Join(d, "receipts", k + ".json"))).Get("adapter")).Get("kind"), ImportKind))
                judged.Add($"receipts/{k}.json");
        judged = judged.Distinct(StringComparer.Ordinal).ToList();
        foreach (string path in judged) Judge(d, manifest, plan, path, defect);
        block["defects"] = kept;
        block.Pop("notChecked");
        block["judgedByTheModule"] = judged.Cast<object?>().ToList();
        return (kept, judged);
    }

    /// <summary>One row runcontrol did not judge (its path in the bundle), judged here; `defect` takes each fault.</summary>
    public static void Judge(string d, PyDict manifest, PyDict plan, string path, Action<string, object?, string> defect)
    {
        string? Sha(string p) => Hashing.Sha256FileOrNull(p);
        if (path.StartsWith("receipts/", StringComparison.Ordinal))
        {
            var rec = Ev.OrEmpty(PyJson.LoadEvalFile(PyPath.Join(d, path)));
            if (Ev.OrEmpty(rec.Get("adapter")).Get("kind") is string mk && ModuleRows.LaunchKinds.Contains(mk))
            {
                // a module probe's receipt (runcontrol checked its acceptance, outputs and chain, and the controller's
                // identity): the row it answers was planned for that probe on that arm, and the probe it rests on is the
                // one the plan froze
                var planned = Ev.Items(plan.Get("checks")).Select(PyValue.Dict).FirstOrDefault(c => Ev.Eq(c.Get("checkId"), rec.Get("checkId")));
                if (planned == null || !Ev.Eq(Ev.OrEmpty(planned.Get("adapter")).Get("kind"), mk)
                    || !Ev.Items(planned.Get("arms")).Select(PyValue.Dict).Any(a => Ev.Eq(a.Get("armId"), rec.Get("armId")) && Ev.Eq(a.Get("status"), "planned")))
                    defect("receipt-not-planned", path, $"the receipt of {mk} answers {Ev.Str(rec.Get("checkId"))} on {Ev.Str(rec.Get("armId"))}, which the overlay plan did not plan for that probe");
                foreach (var x in IdentityChecks.Defects(plan, rec, ModuleRows.LaunchKinds).Where(x => x.Role != "controller"))
                    defect(x.Kind, path, $"the {x.Role} {x.Program} is bound as {Ev.Str(x.Found)}; the overlay plan froze {Ev.Str(x.Recorded)}");
                return;
            }
            if (!Ev.Eq(Ev.OrEmpty(rec.Get("adapter")).Get("kind"), ImportKind))
            {
                defect("evidence-kind-unknown", path, $"a receipt of adapter kind {Ev.Str(Ev.OrEmpty(rec.Get("adapter")).Get("kind"))}, which neither SkyWright nor this module writes");
                return;
            }
            // the row it credits is one the overlay plan left open for an import
            var check = Ev.Items(plan.Get("checks")).Select(PyValue.Dict).FirstOrDefault(c => Ev.Eq(c.Get("checkId"), rec.Get("checkId")));
            if (check == null || !Ev.Eq(Ev.OrEmpty(check.Get("adapter")).Get("kind"), "mcp-only")
                || !Ev.Items(check.Get("arms")).Select(PyValue.Dict).Any(a => Ev.Eq(a.Get("armId"), rec.Get("armId")) && Ev.Eq(a.Get("status"), "mcp-only")))
            {
                defect("import-not-open", path, $"the import credits {Ev.Str(rec.Get("checkId"))} on {Ev.Str(rec.Get("armId"))}, which the plan did not leave open for an import");
                return;
            }
            // the import document it names is the copy the bundle carries
            object? resultOut = Ev.Items(rec.Get("outputs")).Select(o => Ev.G(o, "path")).FirstOrDefault(o => Ev.Str(o).EndsWith("/result.json", StringComparison.Ordinal));
            string copy = PyPath.Join(d, VerifyBundle.ReceiptEvidenceRel(Text(resultOut)));
            if (!PyValue.Truthy(resultOut) || !Ev.Eq(Sha(copy), Ev.OrEmpty(rec.Get("imported")).Get("sourceSha256")))
                defect("import-source-changed", path, "the import document the receipt was accepted on is absent from the bundle or does not hash as recorded");
            return;
        }
        var e = Ev.Items(manifest.Get("evidence")).Select(PyValue.Dict).FirstOrDefault(x => Ev.Eq(x.Get("path"), path));
        object? kind = e?.Get("enrichment");
        if (e == null || !Ev.Eq(e.Get("kind"), "enrichment") || kind is not string k || !EnrichmentKinds.Contains(k))
        {
            defect("evidence-kind-unknown", path, $"an evidence row of kind {Ev.Str(e?.Get("kind"))} {Ev.Str(kind)}, which neither SkyWright nor this module checks");
            return;
        }
        var dict = PyJson.LoadEvalFile(PyPath.Join(d, path)) as PyDict;
        object? src = dict?.Get("derivedFrom");
        if (!PyValue.Truthy(src))
        {
            defect("enrichment-source-missing", path, "the derived rows do not cite the bytes they came from");
            return;
        }
        foreach (var x in PyValue.Iter(src))
        {
            string p = PyPath.Join(d, Text(Ev.G(x, "path")));
            if (!PyValue.Truthy(Ev.G(x, "path")) || !File.Exists(p) || !Ev.Eq(Sha(p), Ev.G(x, "sha256")))
                defect("enrichment-source-changed", path,
                    $"a cited source {Ev.Str(Ev.G(x, "path"))} is absent from the bundle or does not hash as cited");
        }
        long bare = 0;
        foreach (var c in Ev.Items(dict!.Get("Conflicts")))
            foreach (var n in Ev.Items(Ev.G(c, "Necessary")))
                if (!PyValue.Truthy(Ev.G(n, "Reads"))) bare++;
        // a reversed setup derived from a tree capture cites the transcripts, not single reads
        if (bare > 0 && !(Ev.Eq(kind, "order-variant") && Ev.Eq(dict.Get("readForm"), "tree")))
            defect("enrichment-source-missing", path, $"{bare} derived row(s) do not cite the reads they came from");
    }
}
