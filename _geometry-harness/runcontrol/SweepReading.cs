using System.Numerics;
using System.Text.RegularExpressions;

namespace RunControl;

/// <summary>What the controller reads back from a sweep launch folder (evalrun.py): its job records, and
/// ordersweep's classification word for an arm.</summary>
public static class SweepReading
{
    /// <summary>A launch folder's name: inv-NNNN.</summary>
    public static readonly Regex SweepFolderRe = new(@"\Ainv-(\d{4,})\z", RegexOptions.CultureInvariant);

    /// <summary>job id -> the record ordersweep appended for it in this launch's jobs.jsonl. A line that does not
    /// parse gives its job no record.</summary>
    public static PyDict ReadSweepJobs(string inv)
    {
        var jobs = new PyDict();
        string p = PyPath.Join(inv, "jobs.jsonl");
        if (!Ev.Exists(p)) return jobs;
        foreach (string ln in PyText.SplitLines(Ev.ReadLoose(p)))
        {
            object? rec;
            try { rec = PyJson.Loads(ln); }
            catch (FormatException) { continue; }
            if (rec is PyDict d && PyValue.Truthy(d.Get("job"))) jobs[Ev.Str(d["job"])] = d;
        }
        return jobs;
    }

    // Path(folder).name of a recorded launch folder, when it is a launch folder's name.
    static BigInteger? LaunchNumber(object? folder)
    {
        if (!PyValue.Truthy(folder)) return null;
        var m = SweepFolderRe.Match(PyPath.Name(Ev.Str(folder)));
        return m.Success ? PyValue.IntText(m.Groups[1].Value) : null;
    }

    /// <summary>ordersweep's classification word for an arm, only from one launch that measured every accepted diff
    /// it rests on; otherwise no word, the reason, and every launch's word as history. Equal sort keys are put in a
    /// fixed order here (Python leaves them in set order).</summary>
    public static PyDict OrderClassification(string run, PyDict arm, IEnumerable<PyDict> receiptRows)
    {
        var rows = receiptRows.Where(r => PyValue.Truthy(r.Get("orderEvaluation"))).ToList();
        if (rows.Count == 0) return new PyDict { ["classification"] = null };
        static object? Folder(PyDict r) => Ev.OrEmpty(r.Get("sweep")).Get("folder");
        static object? Word(PyDict r) => Ev.OrEmpty(r.Get("orderEvaluation")).Get("classification");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var history = rows.Select(r => (folder: Folder(r), word: Word(r)))
            .Where(x => seen.Add(PyValue.Hash(x.folder) + "\0" + PyValue.Hash(x.word)))
            .OrderBy(x => Ev.Str(x.folder), PyOrder.CodePoint).ThenBy(x => PyText.Repr(x.word), PyOrder.CodePoint).ToList();
        var historical = new List<object?>();
        foreach (var (f, c) in history)
        {
            var probes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in rows.Where(r => Ev.Eq(Folder(r), f) && Ev.Eq(Word(r), c)))
                foreach (var p in Ev.Items(Ev.OrEmpty(r.Get("orderEvaluation")).Get("probes"))) probes.Add((string)p!);
            historical.Add(new PyDict { ["launch"] = f, ["classification"] = c, ["probes"] = PyOrder.SortedByCodePoint(probes).Cast<object?>().ToList() });
        }
        var launches = history.Where(x => LaunchNumber(x.folder) != null).Select(x => Ev.Str(x.folder)).Distinct(StringComparer.Ordinal)
            .OrderBy(f => LaunchNumber(f)!.Value).ThenBy(f => f, PyOrder.CodePoint).ToList();
        if (launches.Count == 0)
        {
            var words = history.Select(x => x.word).GroupBy(PyValue.Hash).Select(g => g.First()).ToList();
            if (words.Count == 1) return new PyDict { ["classification"] = words[0] };
            return new PyDict
            {
                ["classification"] = null, ["historicalClassifications"] = historical,
                ["classificationNote"] = "the accepted diffs carry different ordersweep classifications and no launch "
                    + "record shows one measurement of all of them, so none is given",
            };
        }
        string newest = launches[^1];
        string folder = PyPath.Join(run, newest);
        string stem = Ev.Stem(Ev.Str(arm["candidateFile"]));
        var summary = PyJson.LoadEvalFile(PyPath.Join(folder, "summary.json")) is { } s && PyValue.Truthy(s) ? PyValue.Dict(s) : new PyDict();
        var cand = Ev.OrEmpty(Ev.OrEmpty(summary.Get("candidates")).Get(stem));
        var jobs = ReadSweepJobs(folder);
        var memo = new HashMemo();
        var unconfirmed = new List<string>();
        foreach (var r in rows)
        {
            var oe = PyValue.Dict(r["orderEvaluation"]);
            foreach (var probe in Ev.Items(oe.Get("probes")))
            {
                var rec = Ev.OrEmpty(jobs.Get($"diff-{Ev.Str(probe)}-{stem}"));
                PyDict? row = null;
                foreach (var x in Ev.Items(cand.Get("rows")))
                    if (Ev.Eq(Ev.G(x, "probe"), probe)) { row = PyValue.Dict(x); break; }
                object? wantArm = null, wantReverse = null, gotArm = null, gotReverse = null;
                foreach (var x in Ev.Items(Ev.OrEmpty(r.Get("consumed")).Get("inputs")))
                {
                    if (Ev.Eq(Ev.G(x, "part"), "arm")) wantArm = Ev.G(x, "sha256");
                    if (Ev.Eq(Ev.G(x, "part"), "reverse")) wantReverse = Ev.G(x, "sha256");
                }
                foreach (var (k, v) in Ev.OrEmpty(rec.Get("inputs")))
                {
                    if (k == "arm" && PyValue.Truthy(v)) gotArm = memo.Sha256(Ev.Str(v));
                    if (k == "reverse" && PyValue.Truthy(v)) gotReverse = memo.Sha256(Ev.Str(v));
                }
                bool same = Ev.Eq(rec.Get("outcome"), "ran") && Ev.Eq(rec.Get("exitCode"), 0L) && PyValue.Truthy(rec.Get("emitWritten"))
                    && Ev.Eq(gotArm, wantArm) && Ev.Eq(gotReverse, wantReverse)
                    && row != null && Ev.Eq(row.Get("diff"), oe.Get("diff")) && Ev.Eq(row.Get("created"), oe.Get("created"));
                if (!same)
                    unconfirmed.Add($"{Ev.Str(probe)} (accepted from {(PyValue.Truthy(Folder(r)) ? Ev.Str(Folder(r)) : "an older layout")})");
            }
        }
        if (unconfirmed.Count == 0 && PyValue.Truthy(cand.Get("classification")))
        {
            var word = new PyDict
            {
                ["classification"] = cand["classification"],
                ["classificationSource"] = new PyDict
                {
                    ["launch"] = newest,
                    ["note"] = "the newest launch among the accepted diffs measured every one of them again with the same emits and the same rows",
                },
            };
            if (history.Count > 1) word["historicalClassifications"] = historical;
            return word;
        }
        return new PyDict
        {
            ["classification"] = null, ["historicalClassifications"] = historical,
            ["classificationNote"] = $"no single launch measured every accepted diff of this arm (not in {newest}: "
                + string.Join(", ", unconfirmed.Count > 0 ? unconfirmed : ["its summary carries no classification"])
                + "), so ordersweep's word for one launch would describe other evidence; the per-probe numbers stand",
        };
    }
}
