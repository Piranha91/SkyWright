namespace RunControl;

/// <summary>`runcontrol status --run RUN` (evalrun.py's status): the ledger as a table.</summary>
public static class EvalStatus
{
    public static int Run(string runFolder)
    {
        string run = PyPath.Str(runFolder);
        var (plan, why) = EvalStore.LoadPlan(run);
        if (plan == null) throw new Refusal(why!);
        var ledger = new Ledger(run, plan);
        if (ledger.Refusal() is { } bad) throw new Refusal(bad);
        Print(plan, ledger);
        return Exit.Ok;
    }

    /// <summary>print_status: one line per ledger entry, sorted by check, arm and part, then the counts.</summary>
    public static void Print(PyDict plan, Ledger ledger)
    {
        static string Part(PyDict e) => PyValue.Truthy(e.Get("part")) ? (string)e["part"]! : "";
        var rows = ledger.Entries.Select(kv => PyValue.Dict(kv.Value))
            .OrderBy(e => (string)e["checkId"]!, PyOrder.CodePoint).ThenBy(e => (string)e["armId"]!, PyOrder.CodePoint)
            .ThenBy(Part, PyOrder.CodePoint).ToList();
        int w1 = rows.Select(r => PyValue.Len(r["checkId"])).Append(5).Max();
        int w2 = rows.Select(r => PyValue.Len(r["armId"])).Append(3).Max();
        Console.Out.WriteLine($"plan {Ev.Str(plan["planId"])}   {rows.Count} ledger entr{(rows.Count == 1 ? "y" : "ies")}");
        Console.Out.WriteLine($"{"check".PadRight(w1)}  {"arm".PadRight(w2)}  {"part",-8}  {"status",-34}  detector");
        Console.Out.WriteLine($"{new string('-', w1)}  {new string('-', w2)}  {new string('-', 8)}  {new string('-', 34)}  {new string('-', 10)}");
        foreach (var r in rows)
        {
            string status = PyValue.Truthy(r.Get("status")) ? (string)r["status"]! : "?";
            if (status.Length > 34) status = status[..34];
            Console.Out.WriteLine($"{((string)r["checkId"]!).PadRight(w1)}  {((string)r["armId"]!).PadRight(w2)}  "
                + $"{(Part(r).Length > 0 ? Part(r) : "-"),-8}  {status,-34}  {(PyValue.Truthy(r.Get("detector")) ? Ev.Str(r["detector"]) : "-")}");
        }
        Console.Out.WriteLine("   " + string.Join(", ", Disposition.Counts(ledger).Select(kv => $"{kv.Key}: {Ev.Str(kv.Value)}")));
    }
}
