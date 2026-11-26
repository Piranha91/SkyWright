using HousecarlCore.Progress;
using RunControl;

namespace EvalControl;

/// <summary>`evalcontrol evaluate --run AI`: every row of the overlay plan settled, in runcontrol's schedule order: a
/// row an earlier invocation completed is re-checked and reused; a row the plan already answered is accounted by
/// runcontrol's own rule (EvaluateRun.AccountPlanStatus); the rest by this module's rule for its kind.</summary>
public static class OverlayEvaluate
{
    /// <summary>The sentence a media row is accounted with: it is assessed at bundle, never here.</summary>
    public const string NoMedia = "no supplied media identified this check's configuration on this arm: media is "
        + "assessed by `evalcontrol bundle --media`, and an item whose fingerprint does not match an "
        + "accepted receipt of the app scan is unassessed, not an illustration of a verified result";

    /// <summary>rebuildLedger: the ledger is rebuilt from the journal and the receipts first (runcontrol's own
    /// Ledger.Rebuild, as `runcontrol resume --rebuild-ledger` does).</summary>
    public static int Run(string aiRun, bool rebuildLedger = false)
    {
        string run = PyPath.Resolve(aiRun);
        EvalStore.SetRunStore(run);
        var (plan, why) = EvalStore.LoadPlan(run);
        if (plan == null) throw new Refusal(why!);
        if (!plan.ContainsKey(OverlayPlan.Key)) throw new Refusal($"{run} is not an AI-side overlay (its plan has no {OverlayPlan.Key} block); runcontrol evaluates an app scan.");
        if (EvalStore.RequestRefusal(plan) is { } outdated) throw new Refusal(outdated);
        if (IdentityChecks.ContractRefusal(plan) is { } noContract) throw new Refusal(noContract);
        string probeBuild = ModuleRows.ProbeBuild();
        var (changed, measured) = ToolPreflight.Check(run, plan, null, ModuleRows.LaunchKinds, probeBuild);
        if (changed != null) throw new Refusal(changed);
        if (EvalStore.PremiseRefusal(plan) is { } premise) throw new Refusal(premise);
        var ledger = ImportRows.OverlayLedger(run, plan);
        if (rebuildLedger) ledger.Rebuild();
        if (ledger.Refusal() is { } bad) throw new Refusal(bad);

        using (var parent = RunLog.Begin("evalcontrol-checks", new RunOptions { StageSource = StageSource.Counted }))
        {
            ledger.OpenInvocation("run", parent.RunId, null);
            var ex = new Executor(run, plan, ledger, false)
            {
                ParentRunId = parent.RunId, Preflight = measured, ProbeHarness = probeBuild, MoreLaunchKinds = ModuleRows.LaunchKinds,
                MoreCatalogueKinds = AiList.Vocabulary.ResultKinds,
            };
            ledger.HealInterrupted();
            if (ledger.Entries.Any(kv => EvalStore.IsTerminalOk(Ev.G(kv.Value, "status")))) Revalidate.Run(run, plan, ledger);
            var planned = Ev.Items(plan["checks"]).Select(PyValue.Dict).ToList();
            parent.Stage("checks", total: planned.Sum(c => PyValue.Len(c["arms"])), unit: "check-arms");
            Rows(ex, run, plan, ledger, () => parent.Advance());
        }
        EvalStatus.Print(plan, ledger);
        return Exit.Ok;
    }

    /// <summary>The loop itself: every row of the overlay plan, in runcontrol's schedule order, each counted through
    /// `advance` once it is settled.</summary>
    public static void Rows(Executor ex, string run, PyDict plan, Ledger ledger, Action advance)
    {
        foreach (var check in EvaluateRun.Schedule(plan))
        {
            string kind = Ev.Str(Ev.OrEmpty(check["adapter"]).Get("kind"));
            foreach (var armRow in Ev.Items(check["arms"]).Select(PyValue.Dict))
            {
                var arm = ex.Arm(armRow["armId"]);
                object? st = armRow["status"];
                if (ImportRows.AlreadyDone(ledger, check["checkId"], arm["armId"]))
                {
                    // only an import credits an import row, and it stays credited only while its gate still holds on this arm
                    if (!(ImportRows.IsImport(st) && ImportRows.BlockReused(ex, run, plan, check, arm))) EvaluateRun.MarkReused(ledger, check["checkId"], arm["armId"], null);
                }
                else if (EvaluateRun.AccountPlanStatus(ex, check, arm, st)) { }
                // an import no longer credited (its evidence changed, its reuse refused) keeps its cause
                else if (ImportRows.IsImport(st) && ImportRows.AccountUncredited(ex, run, plan, check, arm)) { }
                else if (kind == "media") ex.Account(check, arm, "blocked(no-identified-media)", null, NoMedia);
                else if (Ev.Eq(st, "mcp-only"))
                    // open until `evalcontrol import` binds a record reader's evidence to it
                    ex.Account(check, arm, "mcp-only", null,
                        PyValue.Truthy(check.Get("unsupportedReason")) ? (string)check["unsupportedReason"]! : "the list records no command-line adapter for this check");
                else if (Ev.Eq(st, "live-import")) AccountLive(ex, run, plan, ledger, check, arm);
                else if (Ev.Eq(st, "planned") && ModuleRows.LaunchKinds.Contains(kind)) LaunchRow(ex, run, plan, check, arm);
                else throw new Refusal($"{Ev.Str(check["checkId"])}: adapter kind {PyText.Repr(kind)} is not one evalcontrol evaluates.");
                advance();
            }
        }
    }

    /// <summary>A planned row of a module probe: held until its app prerequisites are settled on this arm in the
    /// bound app bundle (by runcontrol's own rule), and its own prerequisites in this overlay; then launched by
    /// runcontrol's generic row with this module's rules (ModuleRows).</summary>
    static void LaunchRow(Executor ex, string run, PyDict plan, PyDict check, PyDict arm)
    {
        if (ImportRows.Gate(ex, run, plan, check, arm["armId"]) is { } g)
            ex.Account(check, arm, g.status, "blocked-prerequisite", g.detail);
        else ModuleRows.Run(ex, check, arm);
    }

    /// <summary>An opted-in live row (moved from runcontrol's evaluate with the check): not applicable when the arm's
    /// accepted census in the app scan answers it (nothing a script drives touches a target), else open until the live
    /// driver's export is imported.</summary>
    static void AccountLive(Executor ex, string run, PyDict plan, Ledger ledger, PyDict check, PyDict arm)
    {
        var scopeRead = Ev.Eq(check["checkId"], LiveScope.CheckId) ? LiveScope.Read(run, plan, ledger, arm["armId"]) : null;
        if (PyValue.Truthy(scopeRead) && Ev.Eq(scopeRead!["status"], "not-applicable"))
            ex.Account(check, arm, "not-applicable", null, SwitchFlipScope.Sentence(scopeRead),
                extra: new PyDict { ["switchScope"] = LiveScope.Record(scopeRead) });
        else
            ex.Account(check, arm, "live-import", null,
                "opted in and waiting for a live receipt: run the live switch check for this arm, then "
                + "`evalcontrol import --file` its export; the controller itself never launches the game"
                + (PyValue.Truthy(scopeRead) ? $" ({SwitchFlipScope.Sentence(scopeRead!)})" : ""));
    }
}

/// <summary>`evalcontrol finalize --run AI`: runcontrol's finalizer over the overlay, with the blocks only AI-side rows
/// have added before the disposition is written.</summary>
public static class OverlayFinalize
{
    public const string MediaSentence = "media is identified and assessed by `evalcontrol bundle --media`; see the overlay bundle's manifest";

    /// <summary>STATUS.md's sections only an overlay has (runcontrol's finalizer adds them where they belong).</summary>
    public static readonly StatusMdSections Sections = new(AfterCatalogues: ImportedEvidence.Section, Limitations: MediaLine);

    // the media sentence Amend sets, among STATUS.md's limitations (moved from runcontrol's STATUS.md with the media row)
    static IEnumerable<string> MediaLine(PyDict disp) =>
        PyValue.Dict(disp["unassessed"]) is var un && un.ContainsKey("mediaUnassessed") ? [$"- media: {PyText.Str(un["mediaUnassessed"])}"] : [];

    public static void Amend(PyDict disposition, string run, PyDict plan, PyDict receipts, Ledger ledger, PyDict badKeys)
    {
        ImportedEvidence.Add(disposition, ledger, receipts, badKeys);
        var un = PyValue.Dict(disposition["unassessed"]);
        un["familiesUnsupportedOrManual"] = Families(un, plan);
        un["mediaUnassessed"] = MediaSentence;
        un["runtimeObservations"] = LiveScope.RuntimeObservations(receipts);
        disposition["discovery"] = DiscoveryBlock(disposition, run, plan, receipts);
        disposition[OverlayPlan.Key] = plan[OverlayPlan.Key];
    }

    /// <summary>The families unsupported or manual of an overlay: runcontrol's (its unsupported and manual checks)
    /// and the families of the rows only this module has, the record imports (mcp-only) and the media row, which no
    /// command line measures.</summary>
    public static List<object?> Families(PyDict unassessed, PyDict plan)
    {
        var families = Ev.Items(unassessed.Get("familiesUnsupportedOrManual")).Select(Ev.Str).ToHashSet(StringComparer.Ordinal);
        foreach (var c in Ev.Items(plan["checks"]).Select(PyValue.Dict))
            if (Ev.OrEmpty(c["adapter"]).Get("kind") is "media" or "mcp-only") families.Add(Ev.Str(c["family"]));
        return PyOrder.SortedByCodePoint(families).Cast<object?>().ToList();
    }

    /// <summary>The overlay's discovery block (moved from runcontrol's disposition with the discovery rows): what the
    /// module's probes searched and found, each relevant hit qualified by runcontrol's byte rule over the plugins the
    /// app scan's accepted receipts consumed (DerivedRows.Qualify, never a second rule), the app's supplied files as its
    /// qualification receipt states them, and the author's advice. A report reads it on a joined bundle.</summary>
    static PyDict DiscoveryBlock(PyDict disposition, string run, PyDict plan, PyDict receipts)
    {
        var (app, output) = ModuleDiscovery.AppSource(run, plan);
        var appReceipts = Receipts.ReadReceipts(app);
        var relevant = ModuleDiscovery.RelevantPlugins(app, plan, output);
        var searched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in receipts.Select(kv => PyValue.Dict(kv.Value)))
        {
            var ad = Ev.OrEmpty(r.Get("adapter"));
            if (ad.Get("kind") is string k && ModuleRows.LaunchKinds.Contains(k) && ad.Get("exe") is string exe
                && PyValue.Truthy(Ev.OrEmpty(r.Get("validation")).Get("accepted")))
                searched.Add(exe);
        }
        // the app's qualification receipt: the first one whose derived block carries candidates (the supplied files)
        var qualified = appReceipts.Select(kv => PyValue.Dict(kv.Value)).FirstOrDefault(r => PyValue.Truthy(Ev.OrEmpty(r.Get("derived")).Get("candidates")));
        var (hitRows, notRelevant, unreadable, hitsNotSearched) = ModuleDiscovery.DiscoveryHitRows(run, plan, receipts, DerivedRows.ConsumedBy(appReceipts), relevant);
        var notSearched = new List<object?> { "the Nexus network API (this runner never fetches; advice is read offline from the catalogue snapshot)" };
        notSearched.AddRange(hitsNotSearched);
        var candidates = qualified != null ? Ev.Items(Ev.OrEmpty(qualified.Get("derived")).Get("candidates")).ToList() : new List<object?>();
        candidates.AddRange(hitRows);
        var catalogues = Ev.Items(disposition.Get("catalogues"));
        var block = new PyDict
        {
            ["sourcesSearched"] = PyOrder.SortedByCodePoint(searched).Cast<object?>().ToList(),
            ["sourcesNotSearched"] = notSearched,
            ["notRelevant"] = notRelevant,
            ["candidates"] = candidates,
            ["advice"] = ModuleRows.Advice(run, receipts),
            ["listings"] = catalogues.Where(c => Ev.Eq(Ev.G(c, "kind"), "discovery")).ToList(),
            ["relevance"] = ModuleDiscovery.Relevance(run, plan, receipts, relevant),
            ["note"] = "a Nexus hit is a lead: a downloadable file stays unqualified until its actual payload was in an evaluated arm",
        };
        // a hit list that cannot be read as written: said, never read as no hit
        if (unreadable.Count > 0) block["inconclusive"] = unreadable;
        return block;
    }
}
