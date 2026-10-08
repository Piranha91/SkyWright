using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HarnessShared;

// TARGET TRIGGERS — switchprobe's --targets mode: for named references, the load-time enable state and whether anything in
// the load order can change it later, including disabled singletons (an initially-disabled reference no other reference
// names as its enable parent). The census's trigger map (switchprobe Part B) reads bindings only on switches, so a
// singleton got no trigger evidence either way; this reads them for any named reference.
//
// Ported from the enable-path lane's run-folder reader (2026-10-04) and read from the records and scripts of one
// configuration, winner-takes-all per record:
//   R1  own record: initially-disabled flag, deleted flag, enable parent and its opposite flag
//   R2  enable-parent chain with parity to its root (the player is a fixed root, never disabled: stated assumption)
//   R3  script properties naming a node (object and list properties; record scripts and quest-alias scripts)
//   R4  quest aliases whose forced reference is a node: Alias_<name> on the quest's scripts, Self on the alias's own scripts
//   R5  linked references: a scripted reference linking to a node (GetLinkedRef), and EnableLinkChain from any reference
//       whose linked chain reaches a node
//   R6  FormLists holding a node; a binding naming that list is BOUND, LIST NOT DECODED (never cleared)
//   R7  every .pex in the view that names GetFormFromFile, decompiled; a target's plugin name with its file-local id is a lead
//   R8  residual classes, listed never cleared: console, SKSE DLLs in the view, runtime-filled quest aliases naming a target's
//       base or location ref type, scripts that walk references (GetNthRef / Find*ReferenceOfType)
// A bound script is decompiled with core's PapyrusDecompiler; its calls are classified by TargetScriptCalls (switchprobe's
// ScriptCalls rules with the call name kept, plus EnableLinkChain / DisableLinkChain and TryToEnable / TryToDisable).
// Engine fact used: Papyrus Enable / Disable on an enable-parented reference is inert, so a node with a parent changes state
// only through its parent.
//
// Verdicts per target: ENABLED AT LOAD; N0 (never enabled: the chain roots at the player with the opposite flag, or the
// record is deleted); N1 (a disabled reference no binding in the order enables, R3-R7 measured empty); Y (something in the
// order enables it); Y? (a binding could not be read); ABSENT (not placed in this order). Records only; nothing in game.

/// <summary>The --targets reader: load-time enable state and enablers for named references.</summary>
public static class TargetTriggers
{
    public static readonly FormKey Player = FormKey.Factory("000014:Skyrim.esm");

    public static List<FormKey> ParseTargets(string arg)
    {
        var text = arg.StartsWith("@") ? File.ReadAllText(arg[1..]) : arg;
        return text.Split(new[] { ',', '\n', '\r', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(t => FormKey.Factory(t.Trim())).ToList();
    }

    /// <summary>Reads every plugin in <paramref name="paths"/> twice (facts, then bindings) and prints one line per target;
    /// writes the full reading to <paramref name="jsonPath"/> when given. Returns the per-target rows.</summary>
    public static List<Row> Run(IReadOnlyList<string> paths, AssetResolver.AssetView av, IReadOnlyList<FormKey> targets, bool pexScan,
                                string? jsonPath, object? configuration)
    {
        var sw = Stopwatch.StartNew();
        var names = paths.Select(p => Path.GetFileName(p)).ToList();
        var pexCache = new Dictionary<string, (string? Source, int Failed, string? Why)>(StringComparer.OrdinalIgnoreCase);
        (string? Source, int Failed, string? Why) Decompile(string script)
        {
            if (pexCache.TryGetValue(script, out var c)) return c;
            var res = av.ResolveForPlacement($@"Scripts\{script.Replace(':', '\\')}.pex");
            if (res.Sources.Count == 0) return pexCache[script] = (null, 0, "pex not in the VFS");
            var (bytes, err) = AssetResolver.ReadPlacementSource(res.Sources[0]);
            if (bytes is null) return pexCache[script] = (null, 0, "pex unreadable: " + err);
            try
            {
                using var ms = new MemoryStream(bytes);
                var dec = PapyrusDecompiler.DecompileFile(PexFile.CreateFromStream(ms, GameCategory.Skyrim));
                return pexCache[script] = (dec.Source, dec.FunctionsFailed, null);
            }
            catch (Exception ex) { return pexCache[script] = (null, 0, "decompiler threw: " + ex.Message); }
        }

        // Pass A: every placed record's enable facts and links (winner = the last plugin that carries the record).
        var facts = new Dictionary<FormKey, RefFact>();
        for (int p = 0; p < paths.Count; p++)
        {
            ISkyrimModGetter mod;
            try { mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], SkyrimRelease.SkyrimSE); } catch { continue; }
            using (mod as IDisposable)
                foreach (var rec in mod.EnumerateMajorRecords())
                {
                    if (rec is not IPlacedGetter pg) continue;
                    var f = new RefFact { Plugin = names[p] };
                    f.Disabled = pg is ISkyrimMajorRecordGetter smr && smr.SkyrimMajorRecordFlags.HasFlag(SkyrimMajorRecord.SkyrimMajorRecordFlag.InitiallyDisabled);
                    f.Deleted = pg.IsDeleted;
                    if (pg.EnableParent is { } ep && !ep.Reference.FormKey.IsNull)
                    { f.Parent = ep.Reference.FormKey; f.Opposite = ep.Flags.HasFlag(EnableParent.Flag.SetEnableStateToOppositeOfParent); }
                    if (pg is IPlacedObjectGetter po)
                    {
                        f.Base = po.Base.FormKeyNullable;
                        foreach (var l in po.LinkedReferences) if (l.Reference.FormKeyNullable is { } lk) (f.Linked ??= new()).Add(lk);
                        if (po.LocationRefTypes is { } lrt) f.LocRefTypes = lrt.Select(x => x.FormKey).ToList();
                    }
                    else if (pg is IPlacedNpcGetter pn)
                    {
                        f.Base = pn.Base.FormKeyNullable;
                        foreach (var l in pn.LinkedReferences) if (l.Reference.FormKeyNullable is { } lk) (f.Linked ??= new()).Add(lk);
                        if (pn.LocationRefTypes is { } lrt) f.LocRefTypes = lrt.Select(x => x.FormKey).ToList();
                    }
                    facts[pg.FormKey] = f;
                }
        }

        // The node closure: each target, its parent chain, and every reference whose linked chain reaches a node.
        var reverseLinks = new Dictionary<FormKey, List<FormKey>>();
        foreach (var (k, f) in facts) if (f.Linked is { } ls) foreach (var l in ls) { if (!reverseLinks.TryGetValue(l, out var lst)) reverseLinks[l] = lst = new(); lst.Add(k); }
        var children = new Dictionary<FormKey, List<FormKey>>();
        foreach (var (k, f) in facts) if (f.Parent is { } pp) { if (!children.TryGetValue(pp, out var lst)) children[pp] = lst = new(); lst.Add(k); }
        var nodes = new HashSet<FormKey>();
        foreach (var t in targets)
        {
            var cur = t; int depth = 0;
            while (nodes.Add(cur) && depth++ < 16 && facts.TryGetValue(cur, out var f) && f.Parent is { } pk) cur = pk;
        }
        var chainHeads = new Dictionary<FormKey, HashSet<FormKey>>();
        foreach (var n in nodes)
        {
            var seen = new HashSet<FormKey> { n }; var q = new Queue<FormKey>(); q.Enqueue(n);
            while (q.Count > 0) { var x = q.Dequeue(); if (reverseLinks.TryGetValue(x, out var srcs)) foreach (var s in srcs) if (seen.Add(s)) q.Enqueue(s); }
            seen.Remove(n); chainHeads[n] = seen;
        }
        var watched = new HashSet<FormKey>(nodes);
        foreach (var hs in chainHeads.Values) watched.UnionWith(hs);
        var targetBases = targets.Where(facts.ContainsKey).Select(t => facts[t].Base).Where(b => b is not null).Select(b => b!.Value).ToHashSet();
        var targetLrts = targets.Where(facts.ContainsKey).SelectMany(t => facts[t].LocRefTypes ?? new()).ToHashSet();

        // Pass B: bindings naming a watched reference (winner per referring record), FormLists, alias residuals.
        var recordBindings = new Dictionary<FormKey, List<Binding>>();
        var flstHolding = new Dictionary<FormKey, List<FormKey>>();
        var aliasResidual = new Dictionary<FormKey, List<string>>();
        var questStage = new Dictionary<(FormKey, string), int>();
        for (int p = 0; p < paths.Count; p++)
        {
            ISkyrimModGetter mod;
            try { mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], SkyrimRelease.SkyrimSE); } catch { continue; }
            using (mod as IDisposable)
                foreach (var rec in mod.EnumerateMajorRecords())
                {
                    if (rec is IFormListGetter fl)
                    {
                        var hit = fl.Items.Select(x => x.FormKey).Where(watched.Contains).ToList();
                        if (hit.Count > 0) flstHolding[fl.FormKey] = hit; else flstHolding.Remove(fl.FormKey);
                    }
                    if (rec is IQuestGetter qq)
                    {
                        var res = new List<string>();
                        foreach (var al in qq.Aliases)
                        {
                            if (al.ForcedReference.FormKeyNullable is not null) continue;
                            var links = (al as IFormLinkContainerGetter)?.EnumerateFormLinks().Select(l => l.FormKey).ToHashSet() ?? new();
                            var hb = links.Where(targetBases.Contains).ToList(); var hl = links.Where(targetLrts.Contains).ToList();
                            if (hb.Count + hl.Count > 0) res.Add($"alias '{al.Name}' ({al.ID}) names {(hb.Count > 0 ? "base " + string.Join(",", hb) : "")}{(hl.Count > 0 ? " loc ref type " + string.Join(",", hl) : "")}");
                        }
                        if (res.Count > 0) aliasResidual[qq.FormKey] = res; else aliasResidual.Remove(qq.FormKey);
                    }
                    var found = new List<Binding>();
                    string kind = rec switch
                    {
                        IQuestGetter => "quest", ISceneGetter => "scene", IPlacedGetter => "ref", IPackageGetter => "package", IPerkGetter => "perk",
                        IDialogResponsesGetter => "dialogue", _ => rec.GetType().Name.Replace("BinaryOverlay", "").ToLowerInvariant(),
                    };
                    if (rec is IHaveVirtualMachineAdapterGetter have && have.VirtualMachineAdapter is { } vmad)
                    {
                        void Prop(IScriptEntryGetter entry, IScriptPropertyGetter prop, string via)
                        {
                            if (prop is IScriptObjectPropertyGetter op)
                            {
                                var fk = op.Object.FormKeyNullable;
                                if (fk is { } k && (watched.Contains(k) || flstHolding.ContainsKey(k)))
                                    found.Add(new Binding(rec.FormKey, kind, entry.Name ?? "", prop.Name ?? "", k, flstHolding.ContainsKey(k) ? via + " (FormList holding a node)" : via, null));
                                else if (op.Alias >= 0 && rec is IQuestGetter qq2)
                                {
                                    var al = qq2.Aliases.FirstOrDefault(a => a.ID == (uint)op.Alias);
                                    if (al?.ForcedReference.FormKeyNullable is { } fr && watched.Contains(fr))
                                        found.Add(new Binding(rec.FormKey, kind, entry.Name ?? "", prop.Name ?? "", fr, via + " (alias-bound property)", al.Name));
                                }
                            }
                            else if (prop is IScriptObjectListPropertyGetter lp)
                                foreach (var o in lp.Objects)
                                    if (o.Object.FormKeyNullable is { } k2 && (watched.Contains(k2) || flstHolding.ContainsKey(k2)))
                                        found.Add(new Binding(rec.FormKey, kind, entry.Name ?? "", prop.Name ?? "", k2, via + " (list)", null));
                        }
                        foreach (var entry in vmad.Scripts) foreach (var prop in entry.Properties) Prop(entry, prop, "script");
                        if (vmad is IQuestAdapterGetter qa)
                        {
                            foreach (var fr in qa.Fragments) if (!string.IsNullOrEmpty(fr.FragmentName)) questStage[(rec.FormKey, fr.FragmentName!)] = fr.Stage;
                            foreach (var al in qa.Aliases) foreach (var entry in al.Scripts) foreach (var prop in entry.Properties) Prop(entry, prop, "alias script");
                        }
                        if (rec is IQuestGetter q)
                            foreach (var al in q.Aliases)
                                if (al.ForcedReference.FormKeyNullable is { } fr && watched.Contains(fr))
                                {
                                    foreach (var entry in vmad.Scripts) found.Add(new Binding(rec.FormKey, kind, entry.Name ?? "", "Alias_" + al.Name, fr, "forced-reference alias", al.Name));
                                    if (vmad is IQuestAdapterGetter qa2)
                                        foreach (var fa in qa2.Aliases)
                                            if (fa.Property?.Alias == al.ID) foreach (var entry in fa.Scripts) found.Add(new Binding(rec.FormKey, kind, entry.Name ?? "", "Self", fr, "forced-reference alias script", al.Name));
                                }
                        if (rec is IPlacedGetter && watched.Contains(rec.FormKey))
                            foreach (var entry in vmad.Scripts) found.Add(new Binding(rec.FormKey, kind, entry.Name ?? "", "Self", rec.FormKey, "script on the reference itself", null));
                        if (rec is IPlacedGetter && facts.TryGetValue(rec.FormKey, out var rf) && rf.Linked is { } lks)
                            foreach (var lk in lks.Where(watched.Contains))
                                foreach (var entry in vmad.Scripts) found.Add(new Binding(rec.FormKey, kind, entry.Name ?? "", "GetLinkedRef", lk, "linked reference of the scripted ref", null));
                    }
                    else if (rec is IQuestGetter qn)
                        foreach (var al in qn.Aliases)
                            if (al.ForcedReference.FormKeyNullable is { } fr && watched.Contains(fr))
                                found.Add(new Binding(rec.FormKey, kind, "", "Alias_" + al.Name, fr, "forced-reference alias, quest has no scripts", al.Name));
                    if (found.Count > 0) recordBindings[rec.FormKey] = found; else recordBindings.Remove(rec.FormKey);
                }
        }
        var allBindings = recordBindings.Values.SelectMany(x => x).ToList();

        // Each binding's calls on its property.
        var classified = new List<BindingRow>();
        foreach (var b in allBindings)
        {
            var row = new BindingRow { B = b };
            if (b.Script.Length == 0) { row.Status = "BOUND, NO SCRIPT"; classified.Add(row); continue; }
            var (src, failed, why) = Decompile(b.Script);
            if (src is null) { row.Status = "UNREAD"; row.Why = why; classified.Add(row); continue; }
            var calls = TargetScriptCalls.Parse(src).CallsOn(b.Property).ToList();
            row.Calls = calls.Select(c => $"{c.Function}: {c.Text}").ToList();
            row.Enables = calls.Any(c => c.Call is "enable" or "enablenowait" or "trytoenable" or "trytoenablenowait");
            row.Disables = calls.Any(c => c.Call is "disable" or "disablenowait" or "trytodisable" or "trytodisablenowait");
            row.EnablesLinkChain = calls.Any(c => c.Call == "enablelinkchain");
            row.DisablesLinkChain = calls.Any(c => c.Call == "disablelinkchain");
            if (b.Via.Contains("FormList")) row.Status = "BOUND, LIST NOT DECODED";
            else if (row.Enables || row.Disables || row.EnablesLinkChain || row.DisablesLinkChain)
                row.Status = row.Enables ? "ENABLES" : row.EnablesLinkChain ? "ENABLES LINK CHAIN" : "ONLY DISABLES";
            else row.Status = failed > 0 ? "UNREAD (a function the decompiler refused)" : "BOUND, NO CALL";
            if (b.Kind == "quest" && calls.Count > 0)
                row.Stages = calls.Select(c => questStage.TryGetValue((b.Record, c.Function), out var st) ? st : (int?)null).Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
            classified.Add(row);
        }

        // R7 / R8 over the view's scripts.
        var gffHits = new List<GffHit>();
        var gffScripts = new List<object>();
        var walkers = new List<string>();
        var gffUnread = new List<string>();
        int pexTotal = 0, pexGff = 0, pexRead = 0;
        if (pexScan)
        {
            var all = av.EnumerateUnder("scripts").Where(x => x.EndsWith(".pex", StringComparison.OrdinalIgnoreCase)).ToList();
            pexTotal = all.Count;
            var targetIds = targets.Select(t => (t, local: t.ID, plugin: t.ModKey.FileName.String)).ToList();
            foreach (var rel in all)
            {
                var res = av.ResolveForPlacement(rel);
                if (res.Sources.Count == 0) continue;
                var (bytes, err) = AssetResolver.ReadPlacementSource(res.Sources[0]);
                if (bytes is null) { gffUnread.Add(rel + ": " + err); continue; }
                pexRead++;
                var ascii = Encoding.Latin1.GetString(bytes);
                string scriptName = Path.GetFileNameWithoutExtension(rel);
                if (Regex.IsMatch(ascii, "GetNthRef|FindClosestReferenceOfType|FindRandomReferenceOfType|FindClosestReferenceOfAnyTypeInList|FindRandomReferenceOfAnyTypeInList|FindAllReferences", RegexOptions.IgnoreCase))
                    walkers.Add(scriptName);
                if (ascii.IndexOf("GetFormFromFile", StringComparison.OrdinalIgnoreCase) < 0) continue;
                pexGff++;
                string src;
                try { using var ms = new MemoryStream(bytes); src = PapyrusDecompiler.DecompileFile(PexFile.CreateFromStream(ms, GameCategory.Skyrim)).Source; }
                catch (Exception ex) { gffUnread.Add(rel + ": decompiler threw " + ex.Message); continue; }
                gffScripts.Add(new { script = scriptName, provider = res.Sources[0].ProviderName, namesTargetPlugins = targetIds.Select(x => x.plugin).Distinct().Where(pl => src.IndexOf(pl, StringComparison.OrdinalIgnoreCase) >= 0).ToList() });
                foreach (var (t, local, plugin) in targetIds)
                {
                    uint low = local & 0xFFFFFF;
                    bool lit = Regex.IsMatch(src, $@"(?<![\w]){low}(?![\w])") || src.IndexOf($"0x{low:X}", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (src.IndexOf(plugin, StringComparison.OrdinalIgnoreCase) >= 0 && lit) gffHits.Add(new GffHit(t, scriptName, rel));
                }
            }
        }
        var skse = av.EnumerateUnder("skse").Where(x => x.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x).ToList();

        // Decide: can node n be in state `enabled` at some time after load? yes / no / unknown.
        var memo = new Dictionary<(FormKey, bool), (string State, string Why)>();
        (string State, string Why) CanBe(FormKey n, bool enabled, int depth)
        {
            if (memo.TryGetValue((n, enabled), out var m)) return m;
            if (depth > 16) return memo[(n, enabled)] = ("unknown", "chain deeper than 16 or a cycle");
            memo[(n, enabled)] = ("unknown", "cycle");
            (string, string) r;
            if (n == Player)
                r = enabled ? ("yes", "the player (enabled at load)") : ("no", "the player is never disabled (stated assumption)");
            else if (!facts.TryGetValue(n, out var f))
                r = ("unknown", "not placed by any plugin in this order");
            else if (f.Deleted)
                r = enabled ? ("no", "deleted record") : ("yes", "deleted record");
            else if (f.Parent is { } pk)
            {
                var need = f.Opposite ? !enabled : enabled;
                var pr = CanBe(pk, need, depth + 1);
                r = (pr.State, $"via parent {pk}{(f.Opposite ? " (opposite)" : "")}: parent can be {(need ? "enabled" : "disabled")} = {pr.State} [{pr.Why}]");
            }
            else if (!f.Disabled == enabled) r = ("yes", $"{(enabled ? "enabled" : "disabled")} at load");
            else
            {
                var rows = classified.Where(c => c.B.Switch == n).ToList();
                var lc = chainHeads.GetValueOrDefault(n) ?? new();
                var lcRows = classified.Where(c => lc.Contains(c.B.Switch)).ToList();
                int gff = gffHits.Count(h => h.Target == n);
                bool yes = enabled ? rows.Any(c => c.Enables) || lcRows.Any(c => c.EnablesLinkChain)
                                   : rows.Any(c => c.Disables) || lcRows.Any(c => c.DisablesLinkChain);
                bool unk = rows.Any(c => c.Status.StartsWith("UNREAD") || c.Status.StartsWith("BOUND, LIST")) || gff > 0 || lcRows.Any(c => c.Status.StartsWith("UNREAD"));
                string ev = $"{rows.Count} binding(s) on it [{string.Join("; ", rows.Select(c => c.Status + " " + c.B.Script + "." + c.B.Property + " on " + c.B.Record))}]"
                          + (lcRows.Count > 0 ? $"; {lcRows.Count} on link-chain heads [{string.Join("; ", lcRows.Select(c => c.Status + " " + c.B.Script + " on " + c.B.Record))}]" : "")
                          + (gff > 0 ? $"; {gff} GetFormFromFile lead(s)" : "");
                r = yes ? ("yes", ev) : unk ? ("unknown", ev) : ("no", "singleton, " + ev);
            }
            return memo[(n, enabled)] = r;
        }
        bool? LoadState(FormKey n)
        {
            var cur = n; bool flip = false; int d = 0;
            while (d++ < 17)
            {
                if (cur == Player) return !flip;
                if (!facts.TryGetValue(cur, out var f)) return null;
                if (f.Deleted) return flip;
                if (f.Parent is { } pk) { if (f.Opposite) flip = !flip; cur = pk; continue; }
                bool en = !f.Disabled; return flip ? !en : en;
            }
            return null;
        }
        string RootKind(FormKey t)
        {
            var cur = t; int d = 0;
            while (d++ < 17 && facts.TryGetValue(cur, out var f) && f.Parent is { } pk) cur = pk;
            return cur == Player ? "player" : cur == t ? "self" : "ref " + cur;
        }

        var outRows = new List<Row>();
        foreach (var t in targets)
        {
            facts.TryGetValue(t, out var f);
            string verdict, why; bool? load = null;
            if (f is null) { verdict = "ABSENT"; why = "not placed in this order"; }
            else if (f.Deleted) { verdict = "N0"; why = "deleted record"; load = false; }
            else
            {
                load = LoadState(t);
                var en = CanBe(t, true, 0);
                if (load == true) { verdict = "ENABLED AT LOAD"; why = en.Why; }
                else if (en.State == "no") { verdict = RootKind(t) == "player" ? "N0" : "N1"; why = en.Why; }
                else if (en.State == "yes") { verdict = "Y"; why = en.Why; }
                else { verdict = "Y?"; why = en.Why; }
            }
            outRows.Add(new Row(t, verdict, why, load, f, f is null ? null : RootKind(t),
                classified.Where(c => c.B.Switch == t).ToList(), (chainHeads.GetValueOrDefault(t) ?? new()).ToList(),
                (children.GetValueOrDefault(t) ?? new()).ToList()));
            Console.WriteLine($"   target {t}: {verdict} — load {(load is null ? "unknown" : load.Value ? "enabled" : "disabled")}; {why}");
        }
        Console.WriteLine($"   targets: {string.Join(", ", outRows.GroupBy(r => r.Verdict).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"))}; "
                        + $"{allBindings.Count} binding(s) name a node; R7 {(pexScan ? $"{pexTotal} pex, {pexGff} with GetFormFromFile, {gffHits.Count} lead(s)" : "off")}  ({sw.Elapsed.TotalSeconds:0.0}s)");

        if (jsonPath is not null)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(jsonPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var doc = new
            {
                schema = "switchprobe.targets/1", configuration, plugins = names,
                assumptions = new[]
                {
                    "the player reference is never disabled (stated, not measured here)",
                    "Papyrus Enable/Disable on an enable-parented reference is inert (measured live 2026-09-10)",
                },
                tally = outRows.GroupBy(r => r.Verdict).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
                rows = outRows.Select(r => new
                {
                    target = r.Target.ToString(), verdict = r.Verdict, why = r.Why,
                    loadEnable = r.Load is null ? null : r.Load.Value ? "enabled" : "disabled", root = r.Root,
                    own = r.Own is null ? null : new { r.Own.Plugin, r.Own.Disabled, r.Own.Deleted, parent = r.Own.Parent?.ToString(), r.Own.Opposite, @base = r.Own.Base?.ToString() },
                    chain = Chain(facts, r.Target), bindings = r.Bindings.Select(c => c.ToJson()).ToList(),
                    linkChainHeads = r.LinkChainHeads.Select(x => x.ToString()).ToList(), isParentOf = r.IsParentOf.Select(x => x.ToString()).ToList(),
                    residual = new
                    {
                        aliasesByBaseOrLocRefType = aliasResidual.Where(kv => kv.Value.Any(v => (r.Own?.Base is { } bb && v.Contains(bb.ToString())) || (r.Own?.LocRefTypes?.Any(l => v.Contains(l.ToString())) ?? false)))
                                                                 .Select(kv => kv.Key + ": " + string.Join("; ", kv.Value)).ToList(),
                    },
                }).ToList(),
                allBindings = classified.Select(c => c.ToJson()).ToList(),
                flstHolding = flstHolding.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.Select(x => x.ToString()).ToList()),
                r7 = new { scanned = pexScan, pexTotal, pexRead, pexWithGetFormFromFile = pexGff, gffScripts, hits = gffHits.Select(h => new { target = h.Target.ToString(), h.Script, h.Rel }), unread = gffUnread },
                r8 = new { console = "not readable from records", sksePlugins = skse, referenceWalkingScripts = walkers.OrderBy(x => x).ToList(),
                           aliasesByBaseOrLocRefType = aliasResidual.Select(kv => kv.Key + ": " + string.Join("; ", kv.Value)).ToList() },
                seconds = sw.Elapsed.TotalSeconds,
            };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"   wrote {jsonPath}");
        }
        return outRows;
    }

    static List<object> Chain(Dictionary<FormKey, RefFact> facts, FormKey t)
    {
        var chain = new List<object>(); var cur = t; int d = 0;
        while (d++ < 17 && facts.TryGetValue(cur, out var cf))
        {
            chain.Add(new { node = cur.ToString(), cf.Plugin, cf.Disabled, cf.Deleted, parent = cf.Parent?.ToString(), cf.Opposite });
            if (cf.Parent is { } pk) cur = pk; else break;
        }
        return chain;
    }

    public sealed class RefFact
    {
        public string Plugin = "";
        public bool Disabled, Deleted, Opposite;
        public FormKey? Parent, Base;
        public List<FormKey>? Linked;
        public List<FormKey>? LocRefTypes;
    }
    public sealed record Row(FormKey Target, string Verdict, string Why, bool? Load, RefFact? Own, string? Root, List<BindingRow> Bindings,
                             List<FormKey> LinkChainHeads, List<FormKey> IsParentOf);
    public sealed record Binding(FormKey Record, string Kind, string Script, string Property, FormKey Switch, string Via, string? AliasName);
    sealed record GffHit(FormKey Target, string Script, string Rel);
    public sealed class BindingRow
    {
        public Binding B = null!;
        public string Status = "";
        public string? Why;
        public List<string> Calls = new();
        public List<int>? Stages;
        public bool Enables, Disables, EnablesLinkChain, DisablesLinkChain;
        public object ToJson() => new { record = B.Record.ToString(), kind = B.Kind, script = B.Script, property = B.Property, target = B.Switch.ToString(), via = B.Via, alias = B.AliasName, status = Status, why = Why, calls = Calls, stages = Stages };
    }
}

/// <summary>switchprobe's ScriptCalls rules with the call name kept as a group and the call set extended by
/// EnableLinkChain / DisableLinkChain and ReferenceAlias.TryToEnable / TryToDisable (NoWait).</summary>
public sealed class TargetScriptCalls
{
    public readonly List<(string Function, string Object, string Call, string Text)> Calls = new();
    const string Names = "TryToEnableNoWait|TryToDisableNoWait|TryToEnable|TryToDisable|EnableLinkChain|DisableLinkChain|EnableNoWait|DisableNoWait|Enable|Disable";
    static readonly Regex Header = new(@"^\s*(?:[\w\[\]:]+\s+)?(?:Function|Event)\s+([\w:]+)\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex End = new(@"^\s*End(?:Function|Event)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Call = new(@"(?<![\w.])(?:Self\.)?(?<obj>[A-Za-z_]\w*)(?:\[[^\]]*\])?(?:\.(?:GetReference|GetRef)\(\))?\.(?<call>" + Names + @")\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex LinkedCall = new(@"(?<![\w.])(?:Self\.)?GetLinkedRef\([^)]*\)\.(?<call>" + Names + @")\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex SelfCallRx = new(@"(?<![\w.])(?:Self\.)?(?<call>" + Names + @")\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Alias = new(@"^\s*(?:[\w\[\]:]+\s+)?(?<local>[A-Za-z_]\w*)\s*=\s*(?<src>[A-Za-z_]\w*)(?:\[[^\]]*\])?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Raw = new(@"^\s*;\s+CallMethod\s+(?<call>" + Names + @")\s+(?<obj>\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static TargetScriptCalls Parse(string source)
    {
        var sc = new TargetScriptCalls();
        string fn = "(top)";
        var alias = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> Resolve(string obj)
        {
            var frontier = new List<string> { obj };
            for (int hop = 0; hop < 3; hop++)
            {
                var next = new List<string>();
                foreach (var o in frontier)
                    if (alias.TryGetValue(o, out var srcs)) foreach (var src in srcs) if (!string.Equals(src, o, StringComparison.OrdinalIgnoreCase)) next.Add(src);
                if (next.Count == 0) break;
                frontier = next;
            }
            return frontier.Distinct(StringComparer.OrdinalIgnoreCase);
        }
        foreach (var raw in source.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var h = Header.Match(line);
            if (h.Success) { fn = h.Groups[1].Value; alias.Clear(); continue; }
            if (End.IsMatch(line)) { fn = "(top)"; alias.Clear(); continue; }
            int semi = line.IndexOf(';');
            var code = semi >= 0 && !line.TrimStart().StartsWith(";") ? line[..semi] : line;
            var a = Alias.Match(code);
            if (a.Success && !a.Groups["src"].Value.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                if (!alias.TryGetValue(a.Groups["local"].Value, out var set)) alias[a.Groups["local"].Value] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(a.Groups["src"].Value);
            }
            bool any = false;
            foreach (Match m in Call.Matches(code))
            { foreach (var obj in Resolve(m.Groups["obj"].Value)) sc.Calls.Add((fn, obj, m.Groups["call"].Value.ToLowerInvariant(), m.Value.Trim())); any = true; }
            foreach (Match m in LinkedCall.Matches(code))
            { sc.Calls.Add((fn, "GetLinkedRef", m.Groups["call"].Value.ToLowerInvariant(), m.Value.Trim())); any = true; }
            var r = Raw.Match(line);
            if (r.Success) sc.Calls.Add((fn, r.Groups["obj"].Value.Trim(':'), r.Groups["call"].Value.ToLowerInvariant(), "raw bytecode " + r.Value.Trim().TrimStart(';').Trim()));
            if (!any && SelfCallRx.Match(code) is { Success: true } sm) sc.Calls.Add((fn, "Self", sm.Groups["call"].Value.ToLowerInvariant(), "Self." + sm.Groups["call"].Value + "()"));
        }
        return sc;
    }

    public IEnumerable<(string Function, string Call, string Text)> CallsOn(string property)
    {
        foreach (var (fn, obj, call, text) in Calls)
            if (string.Equals(obj, property, StringComparison.OrdinalIgnoreCase)) yield return (fn, call, text);
    }
}
