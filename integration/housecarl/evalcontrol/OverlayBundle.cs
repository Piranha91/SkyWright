using RunControl;

namespace EvalControl;

/// <summary>`evalcontrol bundle --run AI --out DIR [--media FILE]`: the overlay's evidence bundle. runcontrol's bundle
/// assembles the copies and the manifest (BundleArgs.CopyOnly: the names and order steps are the app bundle's); this adds
/// the binding to the app bundle and, with --media, each supplied picture or clip whose identity the app scan's accepted
/// receipts measured, every other item listed unassessed with one closed reason (MediaRule).</summary>
public static class OverlayBundle
{
    public static int Run(string aiRun, string outDir, string? mediaFile, string? registry, string? scope, string? search)
    {
        string run = PyPath.Resolve(aiRun), dir = PyPath.Resolve(outDir);
        var (plan, why) = EvalStore.LoadPlan(run);
        if (plan == null) throw new Refusal(why!);
        if (plan.Get(OverlayPlan.Key) is not PyDict binding)
            throw new Refusal($"{run} is not an AI-side overlay (its plan has no {OverlayPlan.Key} block); runcontrol bundles an app scan.");
        string appBundle = PyPath.Resolve(PyPath.Join(run, Ev.Str(binding["appBundle"])));
        object? mediaDoc = null;
        if (mediaFile != null && (mediaDoc = PyJson.LoadEvalFile(PyPath.Str(mediaFile))) == null)
            throw new Refusal($"--media {mediaFile}: not readable as JSON");
        var items = mediaDoc is List<object?> list ? list : Ev.Items(Ev.OrEmpty(mediaDoc).Get("items")).ToList();
        // a media file that cannot be read item by item is refused before anything is written
        foreach (var itemRaw in items)
        {
            if (itemRaw is not PyDict item) throw new Refusal($"--media {mediaFile}: an item is not an object: {PyText.Repr(Ev.Head(itemRaw, 80))}");
            string src = PyPath.Str(PyValue.Truthy(item.Get("path")) ? (string)item["path"]! : "");
            if (Bundle.BinarySuffixes.Contains(PyText.Lower(PyPath.Suffix(src))))
                throw new Refusal($"--media {mediaFile}: {PyPath.Name(src)} has a suffix that is never copied by policy "
                    + $"({PyPath.Suffix(src)}); a media item is an image or video file");
        }

        // an import folder is written once and never changed, so its files are linked; a decision on a row only an import
        // settles never completes it
        int rc = Bundle.Run(new BundleArgs
        {
            Run = run, Out = dir, CopyOnly = true, Registry = registry, Scope = scope, Search = search,
            LinkedFolders = ["imports"], NeverCompletingKinds = [.. AiList.NeverCompletingKinds],
        });
        if (rc != Exit.Ok) return rc;

        string manifestPath = PyPath.Join(dir, "manifest.json");
        var manifest = PyValue.Dict(PyJson.LoadEvalFile(manifestPath));
        var bound = new PyDict(binding) { ["appBundleFromBundle"] = PyPath.AsPosix(Path.GetRelativePath(dir, appBundle)) };
        manifest[OverlayPlan.Key] = bound;
        var unassessed = new List<object?>();
        int added = 0;
        if (mediaDoc != null)
        {
            var evidence = (List<object?>)manifest["evidence"]!;
            var measured = AppMeasured(appBundle);
            foreach (var item in items.Cast<PyDict>())
            {
                string src = PyPath.Str(PyValue.Truthy(item.Get("path")) ? (string)item["path"]! : "");
                var (reason, detail) = MediaRule.Refusal(item, src, measured.Own, measured.Other, measured.State);
                if (reason != null)
                {
                    unassessed.Add(new PyDict(item) { ["reason"] = reason, ["detail"] = detail });
                    continue;
                }
                var subject = PyValue.Dict(item["subject"]);
                string sha = Hashing.Sha256File(src);
                string rel = $"media/{sha[..12]}-{PyPath.Name(src)}";
                if (evidence.Any(e => Ev.Eq(Ev.G(e, "path"), rel))) continue;
                string dst = PyPath.Join(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                Bundle.StoreFile(src, dst, link: false);
                var row = new PyDict
                {
                    ["id"] = Hashing.Sha256Text(rel)[..16], ["kind"] = "media", ["path"] = rel, ["sha256"] = sha,
                    ["bytes"] = new FileInfo(dst).Length, ["checkId"] = subject.Get("checkId"), ["armId"] = subject.Get("armId"),
                    ["strength"] = "supplied-media", ["provenance"] = item.Get("provenance"), ["recordedBy"] = null,
                };
                // the row carries every field of the item on top of its own keys, which are never overwritten
                foreach (var (k, v) in item)
                    if (k is not ("path" or "sha256" or "bytes" or "id" or "kind" or "checkId" or "armId" or "strength" or "recordedBy")) row[k] = v;
                evidence.Add(row);
                added++;
            }
        }
        manifest["unassessedMedia"] = unassessed;
        PyJson.WriteEvalFile(manifestPath, manifest);
        if (mediaDoc != null) Console.Out.WriteLine($"media: {added} item(s) bundled as evidence, {unassessed.Count} unassessed");
        return Exit.Ok;
    }

    /// <summary>What the bound app bundle's accepted receipts measured, read from the bundle's own copies.</summary>
    public static MediaRule.Measured AppMeasured(string appBundle) =>
        new(Receipts.ReadReceipts(appBundle), p => PyPath.Join(appBundle, VerifyBundle.ReceiptEvidenceRel(p)));
}
