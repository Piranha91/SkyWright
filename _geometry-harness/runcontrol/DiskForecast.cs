namespace RunControl;

/// <summary>The disk a run still needs, from per-record figures earlier receipts measured.</summary>
public static class DiskForecast
{
    const double GiB = 1L << 30;

    /// <summary>Bytes in the largest binary unit that keeps them above 1 (GiB, MiB or KiB), to one decimal.</summary>
    public static string Size(double bytes)
    {
        if (bytes >= GiB) return $"{PyText.Fixed(bytes / GiB, 1)} GiB";
        if (bytes >= 1 << 20) return $"{PyText.Fixed(bytes / (1 << 20), 1)} MiB";
        return $"{PyText.Fixed(bytes / 1024, 1)} KiB";
    }

    /// <summary>The records an evaluation reads: each arm's non-baseline plugins' header record counts, summed over
    /// the arms. Null when a header cannot be read.</summary>
    public static long? EvaluatedRecords(PyDict res)
    {
        var headers = Py.DictOrEmpty(res.Get("headers"));
        var baseline = Py.Strs(res.Get("baseline")).ToHashSet(StringComparer.Ordinal);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        long total = 0;
        try
        {
            foreach (var a in Py.ListOrEmpty(res.Get("arms")).Select(Py.D))
                foreach (string p in Py.Strs(a.Get("plugins")))
                {
                    if (baseline.Contains(p)) continue;
                    if (!counts.ContainsKey(p)) counts[p] = PluginFiles.Records(Py.S(Py.D(headers[p])["path"])) ?? 0;
                    total += counts[p];
                }
        }
        catch (Exception ex) when (ex is KeyNotFoundException or IOException or UnauthorizedAccessException or Refusal) { return null; }
        return total;
    }

    // The drive of a path as ntpath.splitdrive(abspath(p))[0] gives it: "C:".
    static string Drive(string path) => (Path.GetPathRoot(Path.GetFullPath(path)) ?? "").TrimEnd('\\', '/');

    /// <summary>The GiB the machine slot keeps free on the run's volume: the slot command's `--min-disk VOLUME=GIB`
    /// for that volume, else 0.</summary>
    public static double Floor(PyDict machine, string outDir)
    {
        var prefix = Py.ListOrEmpty(machine.Get("heavyPrefix")).Select(PyText.Str).ToList();
        string drive = Drive(outDir).ToUpperInvariant();
        double floor = 0;
        for (int i = 0; i + 1 < prefix.Count; i++)
        {
            if (prefix[i] != "--min-disk" || !prefix[i + 1].Contains('=')) continue;
            int cut = prefix[i + 1].LastIndexOf('=');
            string vol = prefix[i + 1][..cut], gib = prefix[i + 1][(cut + 1)..];
            if (Drive(vol).ToUpperInvariant() == drive
                && double.TryParse(gib.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double g))
                floor = Math.Max(floor, g);
        }
        return floor;
    }

    /// <summary>stage -> (bytes per evaluated record, where it was measured): the largest figure among the accepted or
    /// set-aside receipts of that stage in this run, the run folders the machine file lists under diskFigures, else
    /// the run folders beside this one. Stages come in the order their first figure was found.</summary>
    public static List<(string stage, double perRecord, string where)> Figures(RunContext ctx, IReadOnlyList<string> stages)
    {
        List<string> others;
        if (Py.Truthy(ctx.Machine.Get("diskFigures"))) others = Py.Strs(ctx.Machine["diskFigures"]).Select(PyPath.Str).ToList();
        else
        {
            try
            {
                others = PyOrder.SortedPaths(Directory.GetDirectories(Path.GetDirectoryName(Path.GetFullPath(ctx.Out)) ?? ctx.Out)
                    .Where(p => !PyOrder.SamePath(p, ctx.Out) && File.Exists(Path.Combine(p, "stages.json"))));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { others = []; }
        }
        var best = new List<(string stage, double perRecord, string where)>();
        foreach (string src in others.Prepend(ctx.Out))
        {
            PyDict doc;
            try
            {
                string f = PyPath.Join(src, "stages.json");
                doc = File.Exists(f) ? Py.D(PyJson.ReadPipelineFile(f)) : new PyDict();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException) { continue; }
            foreach (string s in stages)
                foreach (var rec in new[] { Py.DictOrEmpty(doc.Get("stages")).Get(s), Py.DictOrEmpty(doc.Get("kept")).Get(s) })
                {
                    var d = Py.DictOrEmpty(Py.DictOrEmpty(rec).Get("disk"));
                    object? n = d.Get("evaluatedRecords"), b = d.Get("addedBytes");
                    if (!Py.Truthy(n) || b == null) continue;
                    double added = Math.Max(Py.Float(b), 0.0), per = added / Py.Float(n);
                    int at = best.FindIndex(x => x.stage == s);
                    if (at >= 0 && per <= best[at].perRecord) continue;
                    var row = (s, per, $"{PyOrder.Fwd(src)} {s} ({PyText.Fixed(added / GiB, 2)} GiB over {PyText.Str(n)} records)");
                    if (at >= 0) best[at] = row;
                    else best.Add(row);
                }
        }
        return best;
    }

    /// <summary>(the forecast line, a refusal sentence or null) for the stages about to run, from measured per-record
    /// figures, else the machine file's diskPerRecord {bytes, source}; the floor is the slot's --min-disk.</summary>
    public static (string line, string? refusal) Forecast(RunContext ctx, long? records, IReadOnlyList<string> stages)
    {
        string span = stages.Count > 1 ? $"{stages[0]}..{stages[^1]}" : stages[0];
        if (records is not { } count)
            return ($"disk not forecast for {span}: the run's evaluated records could not be counted from its plugin headers.", null);
        var figs = Figures(ctx, stages);
        var known = stages.Where(s => figs.Any(f => f.stage == s)).Select(s => figs.First(f => f.stage == s)).ToList();
        var unknown = stages.Where(s => figs.All(f => f.stage != s)).ToList();
        var perRec = Py.DictOrEmpty(ctx.Machine.Get("diskPerRecord"));
        double? whole = Py.Truthy(perRec.Get("bytes")) ? Py.Float(perRec["bytes"]) : null;
        if (known.Count == 0 && whole == null)
            return ($"disk not forecast for {span}: no measured per-record figure exists (no earlier receipt of these "
                    + "stages carries one, and the machine file names no diskPerRecord).", null);
        double sum = 0;
        foreach (var f in known) sum += f.perRecord;
        double need = count * sum;
        var sources = known.Select(f => f.where).ToList();
        if (whole is { } w && (unknown.Count > 0 || count * w > need))
        {
            need = Math.Max(need, count * w);
            sources.Add($"diskPerRecord {PyText.Fixed(w / 1024, 1)} KiB per record ({(Py.Truthy(perRec.Get("source")) ? PyText.Str(perRec["source"]) : "source not named")})");
        }
        string gaps = unknown.Count > 0 && whole == null ? $"; no figure for {string.Join(", ", unknown)}" : "";
        double free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(ctx.Out))!).AvailableFreeSpace;
        double floor = Floor(ctx.Machine, ctx.Out);
        string drive = Drive(ctx.Out);
        if (drive.Length == 0) drive = PyOrder.Fwd(ctx.Out);
        double each = count != 0 ? need / count / 1024 : 0.0;
        string measured = $"({count} evaluated records at {PyText.Fixed(each, 1)} KiB each, measured at: {string.Join("; ", sources)})";
        string line = $"disk forecast for {span}: about {Size(need)} more on {drive} {measured}{gaps}; {drive} has {PyText.Fixed(free / GiB, 1)} GiB "
                      + $"free and keeps a {PyText.General(floor)} GiB floor.";
        if (free - floor * GiB < need)
            return (line, $"the run needs about {Size(need)} more on {drive} for {span} {measured}, and {drive} has "
                          + $"{PyText.Fixed(free / GiB, 1)} GiB free, {PyText.Fixed(Math.Max(free / GiB - floor, 0), 1)} GiB above its "
                          + $"{PyText.General(floor)} GiB floor; free space there or move the run folder, then run again.");
        return (line, null);
    }
}
