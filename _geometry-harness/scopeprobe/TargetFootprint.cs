using System.Reflection;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

/// <summary>What pass 1 accumulates over the targets: the walk failures and unreadable records (which decide whether the
/// scope is complete), and each found target's footprint.</summary>
sealed class FootprintLists
{
    public readonly List<string> WalkFailures = new();
    public readonly List<object> UnreadableRecords = new();          // records a target holds that cannot be read, each named
    public readonly List<object> TargetJson = new();
    public readonly List<TargetSummary> Summaries = new();
    public readonly List<PlacedReport> TargetPlacedReports = new();
    public readonly HashSet<FormKey> DestinationDoors = new();        // teleport destinations, resolved in pass 2
    public readonly List<(string Target, List<(FormKey From, FormKey To)> Links)> PerTargetDoors = new();
}

// Pass 1 of scopeprobe, out of the top-level statements so --self-test calls the code the run calls: one target's own
// footprint, or the one-sentence walk failure that makes the scope incomplete (absent, unopenable,
// unwalkable, or holding a record that cannot be read).
static class TargetFootprint
{
    public static void Walk(string wanted, IReadOnlyDictionary<string, ConfigPlugin> byName, int configPlugins, object configSource,
                            IReadOnlyDictionary<string, string> providerKind, string dataDir, string modsDir, int assetsListed,
                            FootprintLists o)
    {
        if (!byName.TryGetValue(wanted, out var plugin))
        {
            o.WalkFailures.Add($"'{wanted}' is not in the resolved configuration ({configPlugins} plugins, {configSource}); "
                             + "its footprint was not measured, so nothing below describes it");
            return;
        }

        // A target missing a master never reaches here: the shared reader refused the whole configuration (ruling 12).

        ISkyrimModGetter mod;
        try { mod = LoadOrderResolver.OpenOverlay(plugin.Path, dataDir); }
        catch (Exception ex)
        {
            o.WalkFailures.Add($"'{wanted}' could not be opened — {Concise(ex)}; its footprint was not measured");
            return;
        }

        IMajorRecordGetter? current = null;   // the record being read, named when a read fails and the bytes name none
        try
        {
            var self = mod.ModKey;
            var types = new SortedDictionary<string, long[]>(StringComparer.Ordinal);   // sig -> [new, override]
            var classNames = new Dictionary<string, string>(StringComparer.Ordinal);
            var linksByMaster = new SortedDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var assetPaths = new List<string>();
            long assetCount = 0, scriptCarriers = 0, records = 0;
            int signatureUnresolved = 0;

            foreach (var rec in mod.EnumerateMajorRecords())
            {
                current = rec;
                records++;
                var (sig, cls, resolved) = SignatureOf(rec.GetType());
                if (!resolved) signatureUnresolved++;
                if (!types.TryGetValue(sig, out var counts)) types[sig] = counts = new long[2];
                counts[rec.FormKey.ModKey == self ? 0 : 1]++;
                classNames[sig] = cls;

                if (rec is IHaveVirtualMachineAdapterGetter { VirtualMachineAdapter: not null }) scriptCarriers++;
                if (rec is IModeledGetter { Model: { } model } && model.File.ToString() is { Length: > 0 } path)
                {
                    assetCount++;
                    if (assetPaths.Count < assetsListed) assetPaths.Add(path);
                }
                foreach (var link in rec.EnumerateFormLinks())
                {
                    if (link.FormKey.IsNull) continue;
                    var master = link.FormKey.ModKey.FileName.String;
                    linksByMaster[master] = linksByMaster.TryGetValue(master, out var n) ? n + 1 : 1;
                }
            }

            // The group walk PlaceIndex makes, over this one plugin: what it FILES, and where.
            var placed = new PlacedWalk();
            var doors = new List<(FormKey From, FormKey To)>();
            try { placed.Walk(mod, doors); }
            catch (Exception ex)
            {
                o.WalkFailures.Add($"'{wanted}' could not be walked for its placements — {Concise(ex)}; its placement "
                                 + "footprint is not reported, so no placement trigger reads it");
                return;
            }
            foreach (var d in doors) o.DestinationDoors.Add(d.To);
            o.PerTargetDoors.Add((plugin.Name, doors));

            int nexusId = 0;
            string? providerNote = null;
            providerKind.TryGetValue(plugin.Provider, out var kind);
            if (kind == "mod")
            {
                var meta = Mo2ModMeta.Read(Path.Combine(modsDir, plugin.Provider, "meta.ini"));
                if (meta is null) providerNote = "the provider mod folder has no readable meta.ini";
                else if (meta.ModId <= 0) providerNote = "the provider mod folder's meta.ini records no Nexus mod id";
                else nexusId = meta.ModId;
            }
            else providerNote = $"the provider is '{plugin.Provider}' ({kind ?? "unknown kind"}), not a mod folder, "
                              + "so there is no meta.ini to read a Nexus id from";

            var report = placed.Report();
            o.TargetPlacedReports.Add(report);
            o.Summaries.Add(new TargetSummary(
                plugin.Name, plugin.Sha256, plugin.Provider, kind ?? "unknown", records, types.Count,
                plugin.Masters.Count, scriptCarriers, assetCount, nexusId > 0 ? nexusId : null,
                report.Total, report.Interior, report.Exterior, report.Persistent, report.Navmesh, report.Land,
                report.Cells.Count, report.ExteriorCells, report.Worldspaces.Count));

            o.TargetJson.Add(new
            {
                name = plugin.Name,
                sha256 = plugin.Sha256,
                bytes = plugin.Bytes,
                provider = plugin.Provider,
                providerKind = kind ?? "unknown",
                masterFlag = plugin.MasterFlag,
                lightFlag = plugin.LightFlag,
                footprint = new
                {
                    records,
                    recordTypes = types.ToDictionary(
                        t => t.Key,
                        t => (object)new { @new = t.Value[0], @override = t.Value[1], className = classNames[t.Key] }),
                    signatureUnresolved,
                    placed = report.ToJson(),
                    linksByMaster,
                    masters = plugin.Masters,
                    scripts = new { carriers = scriptCarriers },
                    assets = new { modelPaths = assetCount, listed = assetPaths },
                    nexusId = nexusId > 0 ? nexusId : (int?)null,
                    nexusIdNote = providerNote,
                },
            });
        }
        catch (Exception ex)
        {
            // A record Mutagen cannot read. The footprint is not measured, the record is named, and the run goes on to
            // write an incomplete scope (exit 1), so every trigger over it reads unknown rather than the probe crashing.
            var bad = FrameCheck.Unreadable(plugin.Path, mod.ModKey);
            string how = "frame";
            if (bad.Count == 0)
            {
                how = "in-progress";
                bad.Add(current is null
                    ? new BadRecord(null, "?", "the read failed before the first record was reached")
                    : new BadRecord(current.FormKey.ToString(), SignatureOf(current.GetType()).Sig,
                                    "the read of this record, or of a record nested in it, failed"));
            }
            foreach (var b in bad)
                o.UnreadableRecords.Add(new { plugin = plugin.Name, formKey = b.FormKey, signature = b.Signature,
                                              reason = b.Reason, located = how });
            string names = string.Join(", ", bad.Take(5).Select(b => $"{b.Signature} {b.FormKey ?? "(no FormID)"}"))
                         + (bad.Count > 5 ? $" and {bad.Count - 5} more" : "");
            o.WalkFailures.Add($"'{wanted}' has a record that cannot be read ({names}: {Concise(ex).TrimEnd('.')}); its footprint was "
                             + "not measured, so no trigger reads it");
        }
        finally { (mod as IDisposable)?.Dispose(); }
    }

    public static string Concise(Exception ex) => ex.Message.Split('\n')[0].Trim();

    // The record's own signature, from the registration Mutagen carries. No list of record types: the corpus generator
    // reads TriggeringRecordType off StaticRegistration and cross-checks GrupRecordType, and this is the same read,
    // cached per runtime type. A type whose signature will not resolve is reported under its class name and counted in
    // signatureUnresolved — visible, never silently folded into another bucket.
    public static (string Sig, string Class, bool Resolved) SignatureOf(Type runtime)
    {
        if (SigCache.Map.TryGetValue(runtime, out var hit)) return hit;
        var cls = runtime.Name.EndsWith("BinaryOverlay", StringComparison.Ordinal)
            ? runtime.Name[..^"BinaryOverlay".Length]
            : runtime.Name;
        string? sig = null;
        try
        {
            var reg = runtime.GetProperty("StaticRegistration", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var field = reg?.GetType().GetField("TriggeringRecordType",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (field?.GetValue(reg) is RecordType rt) sig = rt.ToString();
        }
        catch { /* fall through to GrupRecordType */ }
        if (sig is null)
        {
            var grup = runtime.GetField("GrupRecordType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (grup?.GetValue(null) is RecordType g) sig = g.ToString();
        }
        var answer = sig is null ? (cls, cls, false) : (sig, cls, true);
        SigCache.Map[runtime] = answer;
        return answer;
    }
}
