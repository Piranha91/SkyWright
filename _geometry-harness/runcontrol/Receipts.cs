namespace RunControl;

/// <summary>Receipts (evalrun.py): writing one, reading them all in Python's order, the roles a receipt's result
/// rests on, and the ledger keys an entry depends on.</summary>
public static class Receipts
{
    /// <summary>The fields that say which invocation wrote an accounting receipt, not what it states.</summary>
    public static readonly string[] InvocationFields = ["receiptId", "endedUtc", "parentRunId"];

    /// <summary>Adapter kinds whose check launches its probe as a child.</summary>
    public static readonly HashSet<string> LaunchKinds = new(StringComparer.Ordinal)
        { "scopeprobe", "ordersweep-probe", "probe-emit", "probe-json", "probe-target", "patchprobe" };

    public const string Controller = "controller";
    public const string Comparator = "helper:conflictdiff";

    /// <summary>Whether a check of this adapter kind launches its probe as a child: runcontrol's own kinds, plus a
    /// caller's.</summary>
    public static bool Launches(string kind, IReadOnlySet<string>? moreLaunchKinds) =>
        LaunchKinds.Contains(kind) || (moreLaunchKinds?.Contains(kind) ?? false);

    /// <summary>The program id of a probe exe: conflictdiff is the helper, every other is a probe.</summary>
    public static string ProgramId(object? exe) => Ev.Eq(exe, "conflictdiff") ? Comparator : $"probe:{Ev.Str(exe)}";

    /// <summary>A program id in words.</summary>
    public static string ProgramLabel(string pid)
    {
        if (pid == Controller) return "the controller (evalrun.py and the scripts it runs)";
        int colon = pid.IndexOf(':');
        string kind = colon < 0 ? pid : pid[..colon], name = colon < 0 ? "" : pid[(colon + 1)..];
        return $"{(kind == "helper" ? "helper" : "probe")} {name}";
    }

    /// <summary>role -> program a receipt's result rests on: a probe child's emitter, a swept diff's comparator, and
    /// always the controller. moreLaunchKinds: a caller's own child-launching adapter kinds, added to runcontrol's
    /// (a program using this library for a list of its own); runcontrol passes none.</summary>
    public static PyDict ReceiptRoles(PyDict receipt, IReadOnlySet<string>? moreLaunchKinds = null)
    {
        var ad = Ev.OrEmpty(receipt.Get("adapter"));
        if (Ev.Eq(ad.Get("kind"), "ordersweep-probe") && Ev.Eq(receipt.Get("part"), "diff"))
            return new PyDict { ["comparator"] = Comparator, ["controller"] = Controller };
        if (ad.Get("kind") is string kind && Launches(kind, moreLaunchKinds) && PyValue.Truthy(ad.Get("exe")))
            return new PyDict { ["emitter"] = ProgramId(ad["exe"]), ["controller"] = Controller };
        return new PyDict { ["controller"] = Controller };
    }

    /// <summary>The argv with volatile absolute paths reduced to a shape: RUN-relative, or the bare name.</summary>
    public static List<object?> ArgvShape(IEnumerable<string> argv, string run)
    {
        var shaped = new List<object?>();
        foreach (string tok in argv)
        {
            if (Ev.OsIsAbs(tok) || tok.StartsWith("./", StringComparison.Ordinal) || tok.StartsWith(".\\", StringComparison.Ordinal))
            {
                string r = PyPath.RelTo(run, tok);
                shaped.Add(!Ev.OsIsAbs(r) && !r.Contains("..", StringComparison.Ordinal) ? $"<RUN>/{r}" : $"<abs>/{PyPath.Name(tok)}");
            }
            else shaped.Add(tok);
        }
        return shaped;
    }

    // st_mtime as Python's float: whole seconds plus the nanoseconds as a fraction.
    public static double MtimeSeconds(string path)
    {
        long ns = Hashing.MtimeNs(path);
        long sec = Math.DivRem(ns, 1_000_000_000L, out long rem);
        if (rem < 0) { sec--; rem += 1_000_000_000L; }
        return sec + rem * 1e-9;
    }

    /// <summary>Builds a receipt and writes it to receipts/KEY.json. keepSame: an accounting receipt equal to the one
    /// on disk but for the invocation fields is not rewritten, and the one on disk is returned.</summary>
    public static PyDict WriteReceipt(string run, PyDict plan, PyDict check, PyDict arm, string? part, object? adapter,
        IReadOnlyList<string>? argv, string? runId, string? parentRunId, string? started, string? ended, object? exitCode,
        IEnumerable<string> outputs, string? log, object? consumed, PyDict validation, object? result,
        PyDict? extra = null, bool keepSame = false)
    {
        string key = Ledger.Key(check["checkId"], arm["armId"], part);
        var rows = new List<object?>();
        foreach (string p in outputs)
        {
            if (File.Exists(p))
                rows.Add(new PyDict
                {
                    ["path"] = PyPath.RelTo(run, p), ["sha256"] = Hashing.Sha256FileOrNull(p),
                    ["bytes"] = new FileInfo(p).Length, ["mtimeUtc"] = PyText.IsoOf(MtimeSeconds(p)),
                });
            else if (Directory.Exists(p))
                rows.Add(new PyDict
                {
                    ["path"] = PyPath.RelTo(run, p), ["sha256"] = null,
                    ["bytes"] = 0L, ["mtimeUtc"] = PyText.IsoOf(MtimeSeconds(p)),
                });
            else
                rows.Add(new PyDict { ["path"] = PyPath.RelTo(run, p), ["sha256"] = null, ["bytes"] = null, ["mtimeUtc"] = null });
        }
        string id = Hashing.Sha256Text($"{Ev.Str(plan["planId"])}|{key}|{Ev.Str(runId)}|{Ev.Str(started)}|{EvalStore.UtcNow()}")[..16];
        var receipt = new PyDict
        {
            ["schemaVersion"] = (long)EvalStore.SchemaVersion,
            ["receiptId"] = id,
            ["planId"] = plan["planId"], ["checkId"] = check["checkId"], ["armId"] = arm["armId"], ["part"] = part,
            ["adapter"] = adapter, ["argvShape"] = argv is { Count: > 0 } ? ArgvShape(argv, run) : null,
            ["runId"] = runId, ["parentRunId"] = parentRunId,
            ["startedUtc"] = started, ["endedUtc"] = ended, ["exitCode"] = exitCode,
            ["outputs"] = rows, ["log"] = log != null && Ev.Exists(log) ? PyPath.RelTo(run, log) : null,
            ["consumed"] = consumed,
            ["validation"] = new PyDict
            {
                ["accepted"] = PyValue.Truthy(validation.Get("accepted")),
                ["rejections"] = Ev.G(validation, "rejections", new List<object?>()),
                ["identityLimits"] = Ev.OrList(validation.Get("identityLimits")),
            },
            ["result"] = result,
        };
        if (extra != null) receipt.Update(extra);
        string path = PyPath.Join(run, "receipts", $"{key}.json");
        if (keepSame)
        {
            object? prev = Ev.Exists(path) ? PyJson.LoadEvalFile(path) : null;
            if (SameAccount(prev, receipt)) return (PyDict)prev!;
        }
        PyJson.WriteEvalFile(path, receipt);
        return receipt;
    }

    /// <summary>The receipt on disk states exactly what the new one states, for this plan and (check, arm, part).</summary>
    public static bool SameAccount(object? prev, PyDict now)
    {
        if (prev is not PyDict p) return false;
        foreach (string k in new[] { "planId", "checkId", "armId", "part" })
            if (!Ev.Eq(p.Get(k), now.Get(k))) return false;
        static PyDict Strip(PyDict d) => new(d.Where(kv => !InvocationFields.Contains(kv.Key)));
        return PyJson.Canonical(Strip(p)) == PyJson.Canonical(Strip(now));
    }

    /// <summary>Every readable receipt, keyed by its file's stem, in Python's sorted path order.</summary>
    public static PyDict ReadReceipts(string run)
    {
        var found = new PyDict();
        foreach (string p in Ev.SortedJson(PyPath.Join(run, "receipts")))
            if (PyJson.LoadEvalFile(p) is PyDict doc) found[Ev.Stem(p)] = doc;
        return found;
    }

    /// <summary>The ledger keys an entry's result rests on: reverse and diff follow the arm emit, a diff follows its
    /// reverse, every part of a non-baseline arm follows the same check's baseline arm, a derived check follows each
    /// receipt it consumed, and any check follows its prerequisites on this arm and on the baseline.</summary>
    public static List<string> DependencyKeys(PyDict plan, PyDict receipts, PyDict entry)
    {
        object? baselineId = "baseline";
        foreach (var a in PyValue.Iter(plan["arms"]))
            if (Ev.Eq(PyValue.Dict(a)["kind"], "baseline")) { baselineId = PyValue.Dict(a)["armId"]; break; }
        object? cid = entry["checkId"], aid = entry["armId"], part = entry.Get("part");
        bool sweepPart = part is "arm" or "reverse" or "diff";
        var deps = new List<string>();
        if (part is "reverse" or "diff") deps.Add(Ledger.Key(cid, aid, "arm"));
        if (part is "diff") deps.Add(Ledger.Key(cid, aid, "reverse"));
        if (!Ev.Eq(aid, baselineId) && sweepPart) deps.Add(Ledger.Key(cid, baselineId, "arm"));
        var r = Ev.OrEmpty(receipts.Get(Ledger.Key(cid, aid, part)));
        foreach (var rid in Ev.Items(r.Get("derivedFrom")))
            foreach (var (k2, r2) in receipts)
                if (Ev.Eq(Ev.G(r2, "receiptId"), rid)) deps.Add(k2);
        PyDict? check = null;
        foreach (var c in PyValue.Iter(plan["checks"]))
            if (Ev.Eq(PyValue.Dict(c)["checkId"], cid)) { check = PyValue.Dict(c); break; }
        if (check != null && check.ContainsKey("prerequisites"))
            foreach (var pre in PyValue.Iter(check["prerequisites"]))
            {
                deps.Add(Ledger.Key(pre, aid, null));
                deps.Add(Ledger.Key(pre, baselineId, null));
            }
        return deps;
    }
}
