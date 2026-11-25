using RunControl;

namespace EvalControl;

// The discovery rows' cases, moved by name from runcontrol's self-test with patch.discovery-installed, -fomod and -pool
// (work/9d2/move_discovery_ka_9d2.py): the plan's pairs (SelfTestFinalizeRuns.cs's PairCases, finalize.json `pairs`),
// the {pair} command lines (evaluate.json's "patchprobe: ..." generic cases, the kind now named patchfind), and the
// qualification of discovery hits composed with the app's qualification of supplied files (rows.json's candidate
// qualification case: its recorded hit rows, notRelevant, notSearched, inconclusive and relevance, unchanged).
public static partial class SelfTest
{
    static void DiscoveryCases(PyDict ka)
    {
        PairCases(D(ka["pairs"]), ka["pairsTree"]);
        AdviceGenericCases(new PyDict { ["generic"] = ka["generic"] });
        QualificationCase(D(ka["qualification"]));
        // finalize.json's receipts.roles row 5, moved with the kind (work/9d2/move_roles_row_9d2.py): a module launch
        // kind whose exe names a helper binds it as the emitter, through the kinds this module adds; runcontrol's own
        // roles give the same receipt the controller only (a caller's kind gives the app no role)
        Case("model: receipt_roles of a module launch kind", () => Rows(L(ka["roles"]), row =>
            SameJson(PyJson.Dumps(row["receipt"]),
                Receipts.ReceiptRoles(D(row["receipt"]), ModuleRows.LaunchKinds).Select(kv => (object?)new List<object?> { kv.Key, kv.Value }).ToList(), row["want"])
            ?? SameJson("runcontrol's own roles", Receipts.ReceiptRoles(D(row["receipt"])).Select(kv => (object?)new List<object?> { kv.Key, kv.Value }).ToList(),
                new List<object?> { new List<object?> { "controller", "controller" } })));
    }

    static List<object?> PairRows(IEnumerable<(string, string)> pairs) =>
        pairs.Select(p => (object?)new List<object?> { p.Item1, p.Item2 }).ToList();

    static void PairCases(PyDict ka, object? reviewTree)
    {
        var plans = D(ka["plans"]);
        Case("finalize: target_pairs", () => Rows(L(ka["target"]), row =>
        {
            var plan = D(plans[S(row["plan"])]);
            var arm = L(plan["arms"]).Select(D).First(a => S(a["armId"]) == S(row["arm"]));
            return SameJson($"{S(row["plan"])} / {S(row["arm"])}", PairRows(ModuleDiscovery.TargetPairs(plan, arm)), row["want"]);
        }));
        Case("finalize: review_pairs, all_plan_pairs, relevant_plugins", () =>
        {
            using var t = new Temp("b");
            BuildTree(t, reviewTree);
            var plan = D(PyJson.LoadEvalFile(t.At("plan.json")));
            return SameJson("review", PairRows(ModuleDiscovery.ReviewPairs(t.Root, plan)), ka["review"])
                ?? SameJson("all", PairRows(ModuleDiscovery.AllPlanPairs(t.Root, plan)), ka["all"])
                ?? SameJson("relevant", PyOrder.SortedByCodePoint(ModuleDiscovery.RelevantPlugins(t.Root, plan)).Cast<object?>().ToList(), ka["relevant"]);
        });
    }

    // The app's qualification (runcontrol's DerivedRows: supplied files and catalogued order files, its recorded rows) and
    // this module's (ModuleDiscovery.DiscoveryHitRows over the same tree's discovery receipts, qualified by runcontrol's
    // byte rule DerivedRows.Qualify over the scan's consumed plugins) together give the row's recorded answer before the
    // split: the same candidates in the same order, the same notRelevant, notSearched and inconclusive; and the relevance
    // finalize recorded of the same receipts (workbench case b8i).
    static void QualificationCase(PyDict c)
    {
        Case($"discovery: {S(c["name"])}", () =>
        {
            using var t = new Temp();
            BuildTree(t, c["tree"]);
            var plan = D(PyJson.LoadEvalFile(t.At("plan.json")));
            var receipts = Receipts.ReadReceipts(t.Root);
            var (hits, notRelevant, unreadable, notSearched) = ModuleDiscovery.DiscoveryHitRows(t.Root, plan, receipts, DerivedRows.ConsumedBy(receipts));
            foreach (var (arm, a) in D(c["arms"]))
            {
                var rec = D(D(a)["recorded"]);
                var composed = L(D(a)["app"]).Concat(hits).ToList();
                string? why = SameJson($"{arm}: candidates", composed, rec["candidates"])
                              ?? SameJson($"{arm}: notRelevant", notRelevant, rec["notRelevant"])
                              ?? SameJson($"{arm}: notSearched", notSearched, rec["notSearched"])
                              ?? SameJson($"{arm}: inconclusive", unreadable, rec["inconclusive"]);
                if (why != null) return why;
            }
            var bare = new PyDict(plan) { ["targets"] = new List<object?>(), ["arms"] = L(plan["arms"]).Take(1).ToList() };
            return SameJson("discovery relevance", ModuleDiscovery.Relevance(t.Root, plan, receipts), c["relevance"])
                   ?? SameJson("discovery relevance of a plan with no target and no pair", ModuleDiscovery.Relevance(t.Root, bare, receipts), c["relevanceNoTargets"]);
        });
    }
}
