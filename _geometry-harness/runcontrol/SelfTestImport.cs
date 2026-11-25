using HousecarlCore.Progress;
using System.Reflection;
using System.Text;

namespace RunControl;

// The records stage's known answers (known-answers/records-stage.json, evalrun.py's own answer): the stage starts no
// child. The record import's known answers left with the record checks for the houseCARL integration module's
// evalcontrol self-test, under the names they had here. No Python runs here.
public static partial class SelfTestFinalize
{
    static Action<string, string> Skip = (_, _) => { };

    public static void RunImport(Action<string, Func<string?>> @case, Action<string, string> skip)
    {
        Case = @case;
        Skip = skip;
        PyDict ka;
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("records-stage.json")!)
        using (var r = new StreamReader(s, Encoding.UTF8))
            ka = D(PyJson.Loads(r.ReadToEnd()));
        // the record capture is the AI-driven evaluation's (the module's own suite holds its recorded rows); this
        // program's records stage starts no child: the in-place row, and the open rows of a lab run
        foreach (var stage in L(ka["recordsStage"]).Select(D)) RecordsStageCase(stage);
        OpenRowsCases();
    }

    // One recorded run of the records stage that starts no child.
    static void RecordsStageCase(PyDict c)
    {
        Case($"records stage: {S(c["name"])}", () =>
        {
            using var t = new Temp();
            string run = t.At("run"), sdir = t.At("run/records");
            t.Write("run/evaluation/ledger.json", PyJson.Utf8(PyJson.Dumps(new PyDict { ["entries"] = c["entries"] })));
            Directory.CreateDirectory(sdir);
            for (int i = 0; i < (long)c["earlier"]!; i++) Directory.CreateDirectory(PyPath.Join(sdir, $"capture-{i + 1:00}"));
            var ctx = new RunContext(D(Py.Copy(c["entry"])), new PyDict(D(c["machine"])), run);
            var started = new List<object?>();
            string? refusal = null;
            (PyDict? bound, List<string> outputs) got = (null, []);
            var saved = ChildLaunch.RunJob;
            var savedOut = Report.Out;
            var printed = new StringWriter();
            Report.Out = printed;
            ChildLaunch.RunJob = (op, dir, deadline, job) =>
            {
                started.Add(new PyDict { ["op"] = job.Id });
                return new ChildResult(job.Id, 0, 0.1, null, 1);
            };
            try { got = StageRecords.Records(ctx, new PyDict(), sdir); }
            catch (Refusal r) { refusal = t.Mask(r.Message); }
            finally
            {
                ChildLaunch.RunJob = saved;
                Report.Out = savedOut;
            }
            var folders = PyOrder.SortedByCodePoint(Directory.GetDirectories(sdir, "*", SearchOption.AllDirectories)
                .Select(p => PyOrder.Fwd(Path.GetRelativePath(run, p)))).Cast<object?>().ToList();
            return Same("refusal", refusal, (string?)c["refusal"])
                   ?? SameJson("children", started, c["started"])
                   ?? Same("printed", t.Mask(printed.ToString().Replace("\r\n", "\n")), S(c["printed"]))
                   ?? Same("records.json", t.Mask(Lines(PyPath.Join(sdir, "records.json"))), (string?)c["record"])
                   ?? SameJson("folders", folders, PyOrder.SortedByCodePoint(Strings(c["folders"])).Cast<object?>().ToList())
                   ?? (refusal != null ? null
                       : SameJson("bound", got.bound, c["bound"]) ?? SameJson("outputs", t.Mask(got.outputs.Cast<object?>().ToList()), c["outputs"]));
        });
    }

    // A lab run's records stage after the evaluation's own capture (or without one): it starts no child, leaves the
    // capture's records.json alone, and lists every mcp-only row still open.
    static void OpenRowsCases()
    {
        (PyDict? bound, List<string> outputs, string printed, List<string> jobs, Temp t) Stage(PyDict entries, string? captureRecord)
        {
            var t = new Temp();
            t.Write("run/evaluation/ledger.json", PyJson.Utf8(PyJson.Dumps(new PyDict { ["entries"] = entries })));
            Directory.CreateDirectory(t.At("run/records"));
            if (captureRecord != null) t.Write("run/records/records.json", PyJson.Utf8(captureRecord));
            var entry = D(PyJson.Loads("{\"schema\": 1, \"id\": \"m\", \"subject\": {\"plugin\": \"S.esp\", \"mod\": \"MS\"}, "
                + "\"source\": {\"instance\": \"S:/src\", \"profile\": \"P\"}, \"lab\": {\"instance\": \"L:/lab\", \"game\": \"G:/game\"}}"));
            var ctx = new RunContext(entry, new PyDict { ["readerCmd"] = "reader" }, t.At("run"));
            var jobs = new List<string>();
            var saved = ChildLaunch.RunJob;
            var savedOut = Report.Out;
            var printed = new StringWriter();
            Report.Out = printed;
            ChildLaunch.RunJob = (op, dir, deadline, job) => { jobs.Add(job.Id); return new ChildResult(job.Id, 0, 0.1, null, 1); };
            try
            {
                var (bound, outputs) = StageRecords.Records(ctx, new PyDict(), t.At("run/records"));
                return (bound, outputs, printed.ToString(), jobs, t);
            }
            finally { ChildLaunch.RunJob = saved; Report.Out = savedOut; }
        }
        Case("records stage: a lab run's open mcp-only rows are listed by arm, and nothing is started", () =>
        {
            var entries = D(PyJson.Loads("{\"r.fields__baseline\": {\"checkId\": \"r.fields\", \"status\": \"mcp-only\"}, "
                + "\"r.fields__candidate-01\": {\"checkId\": \"r.fields\", \"status\": \"accepted\"}, "
                + "\"r.cells__candidate-01\": {\"checkId\": \"r.cells\", \"status\": \"mcp-only\"}, \"g.overlap__baseline\": {\"checkId\": \"g.overlap\", \"status\": \"accepted\"}}"));
            const string capture = "{\"capture\": {}, \"captureExit\": 0}";
            var (bound, outputs, printed, jobs, t) = Stage(entries, capture);
            using (t)
            {
                var doc = D(PyJson.ReadPipelineFile(t.At("run/records/" + StageRecords.OpenRowsFile)));
                return (jobs.Count == 0 ? null : $"the stage started {string.Join(", ", jobs)}")
                       ?? SameJson("open rows", doc["open"], PyJson.Loads("{\"baseline\": [\"r.fields\"], \"candidate-01\": [\"r.cells\"]}"))
                       ?? SameJson("bound", bound, PyJson.Loads("{\"openRows\": 2}"))
                       ?? SameJson("outputs", t.Mask(outputs.Cast<object?>().ToList()), PyJson.Loads("[\"<ROOT\\\\>\\\\run\\\\records\\\\open-rows.json\"]"))
                       ?? Same("the capture's record", File.ReadAllText(t.At("run/records/records.json")), capture)
                       ?? (printed.Contains("2 mcp-only row(s) on arm(s) baseline, candidate-01 have no imported record check") ? null : $"said: {printed}");
            }
        });
        Case("records stage: a lab run whose mcp-only rows were all imported lists none and says nothing", () =>
        {
            var entries = D(PyJson.Loads("{\"r.fields__baseline\": {\"checkId\": \"r.fields\", \"status\": \"accepted\"}}"));
            var (bound, _, printed, jobs, t) = Stage(entries, null);
            using (t)
            {
                var doc = D(PyJson.ReadPipelineFile(t.At("run/records/" + StageRecords.OpenRowsFile)));
                return (jobs.Count == 0 ? null : $"the stage started {string.Join(", ", jobs)}")
                       ?? SameJson("open rows", doc["open"], new PyDict())
                       ?? SameJson("bound", bound, PyJson.Loads("{\"openRows\": 0}"))
                       ?? (printed.Length == 0 ? null : $"said: {printed}")
                       ?? (File.Exists(t.At("run/records/records.json")) ? "the stage wrote records.json, which is the capture's" : null);
            }
        });
    }
}
