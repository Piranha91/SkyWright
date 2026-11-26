using RunControl;

namespace EvalControl;

/// <summary>`evalcontrol verify DIR [--app BUNDLE] [--out FILE]`: an AI-side overlay bundle, the app bundle it is bound
/// to, and the binding. (1) runcontrol's verify-bundle rule on the app bundle; (2) the same rule on the overlay (its
/// plan, ledger, receipts, premises, disposition and every listed file by hash); (3) the binding: the app bundle's
/// manifest and plan are the ones the overlay plan froze, and the list copy is the plan's; (4) every media row's
/// identity against what the app bundle's accepted receipts measured. Writes verify.json (or FILE) with the overlay's
/// own verdict, the app's, the binding's and aiEvaluation.complete, the one owner of that rule:
///     complete = app bundle complete and its evaluation complete, and overlay bundle complete and its evaluation complete.
/// Exit 0 when both bundles verify and the binding holds, else 3; 2 for a folder that is not an overlay bundle.</summary>
public static class OverlayVerify
{
    public const string OldBundle = "this bundle was made before the AI-side rows left SkyWright's check registry: its imported "
        + "and AI-side rows were checked by the runcontrol that made it (its receipts name that controller's digest), and "
        + "runcontrol verify-bundle still hashes them; there is no overlay to verify, so verify it with runcontrol verify-bundle.";

    public static int Run(string bundleDir, string? appOverride, string? outFile)
    {
        string d = PyPath.Resolve(bundleDir);
        if (PyJson.LoadEvalFile(PyPath.Join(d, "manifest.json")) is not PyDict manifest)
            throw new Refusal($"{d} holds no readable manifest.json: it is not a bundle.");
        if (manifest.Get(OverlayPlan.Key) is not PyDict bound)
        {
            object? version = Ev.OrEmpty(Ev.OrEmpty(PyJson.LoadEvalFile(PyPath.Join(d, "plan.json"))).Get("registry")).Get("registryVersion");
            if (PyValue.IsIntOrBool(version) && PyValue.Int(version) < 5) throw new Refusal(OldBundle);
            throw new Refusal($"{d} is an app bundle, not an AI-side overlay bundle (its manifest has no {OverlayPlan.Key} block); verify it with runcontrol verify-bundle.");
        }
        string app = appOverride != null ? PyPath.Resolve(appOverride) : PyPath.Resolve(PyPath.Join(d, Ev.Str(bound.Get("appBundleFromBundle"))));
        var defects = new List<object?>();
        void Defect(string kind, object? path, string detail) => defects.Add(new PyDict { ["kind"] = kind, ["path"] = path, ["detail"] = detail });

        // (1) the app bundle, by runcontrol's own rule; its verdict is written beside the overlay's
        string appVerifyFile = PyPath.Join(Path.GetDirectoryName(outFile != null ? PyPath.Resolve(outFile) : PyPath.Join(d, "verify.json"))!, "verify-app.json");
        bool appComplete = false;
        object? appEvaluation = null;
        if (!Ev.Exists(PyPath.Join(app, "manifest.json")))
            Defect("app-bundle-missing", null, $"the app bundle this overlay is bound to is not at {app}; name it with --app");
        else
        {
            appComplete = VerifyBundle.Run(app, appVerifyFile, quiet: true) == Exit.Ok;
            var appVerify = Ev.OrEmpty(PyJson.LoadEvalFile(appVerifyFile));
            appEvaluation = Ev.OrEmpty(appVerify.Get("evaluation")).Get("complete");
            if (!appComplete) Defect("app-bundle-incomplete", appVerifyFile, "runcontrol's verify-bundle does not find the app bundle complete; see verify-app.json");
            // (3) the binding: what the overlay plan froze of the app scan
            string? manifestSha = Hashing.Sha256FileOrNull(PyPath.Join(app, "manifest.json"));
            if (!Ev.Eq(manifestSha, bound.Get("appManifestSha256")))
                Defect("binding-app-manifest", "manifest.json", $"the app bundle's manifest hashes {Ev.Head(manifestSha, 12)}; the overlay was planned over {Ev.Head(bound.Get("appManifestSha256"), 12)}");
            var appPlan = Ev.OrEmpty(PyJson.LoadEvalFile(PyPath.Join(app, "plan.json")));
            if (!Ev.Eq(appPlan.Get("planId"), bound.Get("appPlanId")))
                Defect("binding-app-plan", "plan.json", $"the app bundle is of plan {Ev.Str(appPlan.Get("planId"))}; the overlay was planned over {Ev.Str(bound.Get("appPlanId"))}");
        }

        // (2) the overlay, by the same rule
        string overlayFile = PyPath.Join(Path.GetDirectoryName(appVerifyFile)!, "verify-overlay.json");
        VerifyBundle.Run(d, overlayFile, quiet: true);
        var verify = Ev.OrEmpty(PyJson.LoadEvalFile(overlayFile));
        File.Delete(overlayFile);
        var plan = Ev.OrEmpty(PyJson.LoadEvalFile(PyPath.Join(d, "plan.json")));
        // the rows runcontrol walked and hashed but did not judge are this module's: each is judged here
        var rb = Ev.OrEmpty(verify.Get("bundle"));
        var (kept, _) = RecordEvidence.Rejudge(d, rb, manifest, plan, Defect);
        bool overlayComplete = PyValue.Truthy(rb.Get("schemaSupported")) && kept.Count == 0
            && PyValue.Len(Ev.OrList(rb.Get("missing"))) == 0 && PyValue.Len(Ev.OrList(rb.Get("mismatched"))) == 0;
        rb["structurallyValid"] = PyValue.Truthy(rb.Get("schemaSupported")) && kept.Count == 0;
        if (!Ev.Eq(plan.Get(OverlayPlan.Key), new PyDict(bound.Where(kv => kv.Key != "appBundleFromBundle"))))
            Defect("binding-changed", "manifest.json", "the manifest's overlay block is not the one the plan froze");
        if (!Ev.Eq(Ev.OrEmpty(plan.Get("registry")).Get("sha256"), bound.Get("listSha256")))
            Defect("binding-list", "registry/check-registry.json", "the plan's list hash is not the one its overlay block binds");

        // (4) every media row ties to a configuration the app scan measured
        if (Ev.Exists(PyPath.Join(app, "receipts")))
        {
            var measured = OverlayBundle.AppMeasured(app);
            foreach (var e in Ev.Items(manifest.Get("evidence")).Select(PyValue.Dict).Where(e => Ev.Eq(e.Get("kind"), "media")))
                if (MediaRule.IdentityRefusal(e, measured.Own, measured.Other, measured.State) is { reason: { } reason } r)
                    Defect("media-" + reason, e.Get("path"), r.detail!);
        }

        var b = Ev.OrEmpty(verify.Get("bundle"));
        var allDefects = Ev.Items(b.Get("defects")).Concat(defects).ToList();
        // an overlay bound to an app bundle that does not verify is not a complete bundle either (a defect above)
        bool complete = overlayComplete && defects.Count == 0;
        b["defects"] = allDefects;
        b["structurallyValid"] = PyValue.Truthy(b.Get("structurallyValid")) && defects.Count == 0;
        b["complete"] = complete;
        var overlayEval = Ev.OrEmpty(verify.Get("evaluation"));
        var appEval = Ev.OrEmpty(Ev.OrEmpty(PyJson.LoadEvalFile(appVerifyFile)).Get("evaluation"));
        var ai = AiEvaluation(appComplete, appEvaluation, complete, overlayEval.Get("complete"));
        bool aiComplete = ai["complete"] is true;
        bool anyFinding = Ev.Eq(appEval.Get("claim"), "findings") || Ev.Eq(overlayEval.Get("claim"), "findings");
        verify["app"] = new PyDict { ["dir"] = app, ["complete"] = appComplete, ["evaluationComplete"] = appEvaluation, ["verify"] = PyPath.Name(appVerifyFile) };
        verify["overlayEvaluation"] = overlayEval;
        // the evaluation this bundle pair states: the AI-side evaluation, decided here and nowhere else
        verify["evaluation"] = new PyDict
        {
            ["complete"] = aiComplete, ["claim"] = !aiComplete ? "incomplete" : anyFinding ? "findings" : "no-findings-in-completed-checks",
            ["missing"] = Ev.Items(appEval.Get("missing")).Concat(Ev.Items(overlayEval.Get("missing"))).ToList(),
        };
        verify["aiEvaluation"] = ai;
        PyJson.WriteEvalFile(outFile != null ? PyPath.Resolve(outFile) : PyPath.Join(d, "verify.json"), verify);
        Console.Out.WriteLine($"verify: overlay {Ev.Str(b.Get("verified"))} file(s) verified, {PyValue.Len(Ev.OrList(b.Get("missing")))} missing, "
            + $"{PyValue.Len(Ev.OrList(b.Get("mismatched")))} mismatched, {allDefects.Count} defect(s); app bundle complete: {Ev.Str(appComplete)}; "
            + $"bundle.complete: {Ev.Str(complete)}; aiEvaluation.complete: {Ev.Str(aiComplete)}");
        foreach (var x in allDefects.Take(20).Select(PyValue.Dict))
            Console.Error.WriteLine($"   {Ev.Str(x["kind"])}: {(PyValue.Truthy(x["path"]) ? Ev.Str(x["path"]) : "-")} — {Ev.Str(x["detail"])}");
        return complete && appComplete ? Exit.Ok : Exit.Judgement;
    }

    /// <summary>aiEvaluation: complete only when the app bundle verifies and its evaluation is complete, and this
    /// overlay verifies and its evaluation is complete (the one owner of that rule; evalreport reads it).</summary>
    public static PyDict AiEvaluation(bool appBundle, object? appEvaluation, bool overlayBundle, object? overlayEvaluation) => new()
    {
        ["complete"] = appBundle && appEvaluation is true && overlayBundle && overlayEvaluation is true,
        ["rule"] = "the app bundle verifies and its evaluation is complete, and this overlay verifies and its evaluation is complete",
        ["parts"] = new PyDict
        {
            ["appBundle"] = appBundle, ["appEvaluation"] = appEvaluation, ["overlayBundle"] = overlayBundle, ["overlayEvaluation"] = overlayEvaluation,
        },
    };
}
