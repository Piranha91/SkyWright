using HousecarlCore.Progress;

namespace RunControl;

/// <summary>A run started from a copy of its entry file (an AI-driven evaluation hands this program its measuring
/// entry): a stage binds the entry's keys, never the file's path or name, so the copy reuses the folder.</summary>
public static partial class SelfTest
{
    static void EntryCopyCases()
    {
        Case("stages: a run continued from another entry file with the same keys reuses every stage", () =>
        {
            // the AI-driven evaluation hands this program a copy of the entry (its measuring entry); a stage binds the
            // entry's keys, never the file's path or name, so a copy with the same keys reuses the folder
            using var t = new Temp();
            string outDir = t.At("run");
            const string text = "{\"schema\": 1, \"id\": \"m\", \"source\": {\"instance\": \"S:/src\", \"profile\": \"P\"}, \"lab\": {\"instance\": \"L:/lab\", \"game\": \"G:/game\"}, "
                                + "\"evaluation\": {\"budgetMinutes\": 5}, \"modNames\": {\"S.esp\": \"S\"}}";
            Directory.CreateDirectory(t.At("a"));
            Directory.CreateDirectory(t.At("run"));
            File.WriteAllText(t.At("a/entry.json"), text);
            // the same keys written in another form (indented, keys in another order)
            File.WriteAllText(t.At("run/measuring-entry.json"), "{\n  \"modNames\": {\"S.esp\": \"S\"},\n  \"schema\": 1,\n  \"id\": \"m\",\n"
                + "  \"lab\": {\"game\": \"G:/game\", \"instance\": \"L:/lab\"},\n  \"source\": {\"instance\": \"S:/src\", \"profile\": \"P\"},\n  \"evaluation\": {\"budgetMinutes\": 5}\n}\n");
            var ran = new List<string>();
            var bodies = new Dictionary<string, StageBody>();
            foreach (string name in Stages.Ported)
                bodies[name] = (ctx, _, sdir) =>
                {
                    ran.Add(name);
                    string f = PyPath.Join(sdir, name + ".json");
                    File.WriteAllText(f, "{}");
                    return (new PyDict(), [f]);
                };
            var driver = new Driver
            {
                Bodies = bodies, OpenRun = QuietRun, SetsRunLogFolder = false,
                MakeContext = a => new RunContext(a.Entry!, null, a.Out!) { ResolveWith = () => Json("{\"arms\": [], \"subject\": [\"S.esp\"], \"keys\": []}") },
            };
            var (rc1, _, err1) = Drive(driver, new PipelineArgs(Entry: t.At("a/entry.json"), Out: outDir, Through: "bundle"));
            if (rc1 != Exit.Ok || ran.Count != Stages.Ported.Length) return $"the first run: exit {rc1}, ran {string.Join(" ", ran)}: {err1}";
            ran.Clear();
            var (rc2, out2, err2) = Drive(driver, new PipelineArgs(Entry: t.At("run/measuring-entry.json"), Out: outDir, Through: "bundle"));
            return rc2 == Exit.Ok && ran.Count == 0 ? null : $"the run from the copy: exit {rc2}, ran {string.Join(" ", ran)}: {out2} {err2}";
        });
    }
}
