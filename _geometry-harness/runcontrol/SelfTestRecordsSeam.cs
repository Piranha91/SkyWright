using HousecarlCore.Progress;

namespace RunControl;

/// <summary>The seam a lab run crosses between this program's two runs: the AI-driven evaluation runs it through
/// evaluate, imports the record documents its capture wrote (which rewrites the evaluation's ledger and adds receipts),
/// then runs it on through the bundle. The second run must not evaluate again.</summary>
public static partial class SelfTest
{
    static void RecordsSeamCases()
    {
        Case("records seam: a run on through the bundle after a record import reuses census to evaluate and starts no probe", () =>
        {
            using var t = new Temp();
            string outDir = t.At("run"), run = PyPath.Join(outDir, "evaluation");
            var ran = new List<string>();
            var evaluations = new List<string>();
            var bodies = StageBodies.Built();
            foreach (string name in new[] { "census", "prepare", "scope", "plan", "scenes", "bundle" })
                bodies[name] = (ctx, _, sdir) =>
                {
                    ran.Add(name);
                    if (name == "plan")
                    {
                        Directory.CreateDirectory(run);
                        File.WriteAllText(PyPath.Join(run, "plan.json"), "{\"planId\": \"p1\"}");
                        PyJson.WriteEvalFile(PyPath.Join(run, "ledger.json"), D(PyJson.Loads(
                            "{\"planId\": \"p1\", \"runIds\": [], \"entries\": {\"r.fields__baseline\": {\"checkId\": \"r.fields\", \"armId\": \"baseline\", \"status\": \"mcp-only\"}}}")));
                    }
                    string f = PyPath.Join(sdir, name + ".json");
                    File.WriteAllText(f, "{}");
                    return (new PyDict(), [f]);
                };
            var saved = ChildLaunch.RunJob;
            ChildLaunch.RunJob = (op, dir, deadline, job) =>
            {
                // the evaluate stage's child: this program's own run or resume over the plan
                evaluations.Add(job.Argv.Count > 1 ? job.Argv[1] : job.Id);
                var led = D(PyJson.ReadPipelineFile(PyPath.Join(run, "ledger.json")));
                led["runIds"] = new List<object?> { "r1" };
                PyJson.WriteEvalFile(PyPath.Join(run, "ledger.json"), led);
                return new ChildResult(job.Id, 0, 0.1, null, 1);
            };
            var entry = Json("{\"schema\": 1, \"id\": \"m\", \"evaluation\": {\"budgetMinutes\": 5}}");
            var driver = new Driver
            {
                Bodies = bodies, OpenRun = QuietRun, SetsRunLogFolder = false,
                MakeContext = a => new RunContext(entry, new PyDict(), a.Out!, "m") { ResolveWith = () => Json("{\"arms\": [], \"subject\": [\"S.esp\"], \"keys\": []}") },
            };
            try
            {
                var (rc1, out1, err1) = Drive(driver, new PipelineArgs(Entry: "e.json", Out: outDir, Through: "evaluate"));
                if (rc1 != Exit.Ok || evaluations.Count != 1) return $"the first run: exit {rc1}, {evaluations.Count} evaluation(s): {out1} {err1}";
                // the evaluation's record import: the row accepted in the ledger and a receipt beside it
                var led = D(PyJson.ReadPipelineFile(PyPath.Join(run, "ledger.json")));
                D(D(led["entries"])["r.fields__baseline"])["status"] = "accepted";
                PyJson.WriteEvalFile(PyPath.Join(run, "ledger.json"), led);
                Directory.CreateDirectory(PyPath.Join(run, "receipts"));
                File.WriteAllText(PyPath.Join(run, "receipts", "r.fields__baseline.json"), "{\"planId\": \"p1\"}");
                ran.Clear();
                var (rc2, out2, err2) = Drive(driver, new PipelineArgs(Entry: "e.json", Out: outDir, Through: "bundle"));
                if (rc2 != Exit.Ok) return $"the second run: exit {rc2}: {out2} {err2}";
                if (evaluations.Count != 1) return $"the second run evaluated again ({string.Join(", ", evaluations)}): {out2}";
                foreach (string s in new[] { "census", "prepare", "scope", "plan", "evaluate" })
                    if (!out2.Contains($"{s}: reused (inputs and outputs unchanged)")) return $"{s} was not reused: {out2}";
                if (string.Join(" ", ran) != "scenes bundle") return $"the second run ran the stand-ins {string.Join(" ", ran)}";
                var open = D(PyJson.ReadPipelineFile(PyPath.Join(outDir, "records", StageRecords.OpenRowsFile)));
                return Same("the records stage's open rows after the import", PyJson.Dumps(open["open"]), PyJson.Dumps(new PyDict()));
            }
            finally { ChildLaunch.RunJob = saved; }
        });
    }
}
