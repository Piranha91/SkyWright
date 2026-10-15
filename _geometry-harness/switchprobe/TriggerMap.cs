using System.Diagnostics;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

// THE TRIGGER MAP (Part B), the decisions of section 4 of Program.cs: which records bind a switch. Held in its own class
// so --self-test runs exactly the code a real run runs; Program.cs keeps the console text and the --json.
static class TriggerMap
{
    /// <summary>4a. Every record whose winning VMAD names a switch, and how (winner-takes-all per record); then the bindings
    /// by switch. Fills the dictionaries handed in; returns the number of records with a binding.</summary>
    public static int Bindings(IReadOnlyList<string> paths, ModKey[] pluginKey, SwitchIndex sx, Stopwatch sw,
                               Dictionary<FormKey, List<Binding>> recordBindings,
                               Dictionary<(FormKey Quest, string Fragment), int> questFragmentStage,
                               Dictionary<(FormKey Quest, int Alias), string> questAliasName,
                               Dictionary<FormKey, string?> questEdid, Dictionary<FormKey, string?> recordEdid,
                               Dictionary<FormKey, string> recordKind, Dictionary<FormKey, List<Binding>> bindings)
    {
        int recordsWithBindings = 0;
        for (int p = 0; p < paths.Count; p++)
        {
            if (pluginKey[p].IsNull) continue;
            ISkyrimModGetter mod;
            try { mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], SkyrimRelease.SkyrimSE); } catch { continue; }
            using (mod as IDisposable)
            {
                foreach (var rec in mod.EnumerateMajorRecords())
                {
                    if (rec is not IHaveVirtualMachineAdapterGetter have || have.VirtualMachineAdapter is not { } vmad) continue;
                    var found = new List<Binding>();
                    string kind = rec switch
                    {
                        IQuestGetter => "quest", ISceneGetter => "scene", IPlacedGetter => "ref", IPackageGetter => "package",
                        IPerkGetter => "perk", IDialogResponsesGetter => "dialogue", _ => rec.GetType().Name.Replace("BinaryOverlay", "").ToLowerInvariant(),
                    };
                    void Prop(IScriptEntryGetter entry, IScriptPropertyGetter prop, string via)
                    {
                        if (prop is IScriptObjectPropertyGetter op)
                        {
                            var fk = op.Object.FormKeyNullable;
                            if (fk is { } k && sx.Switches.ContainsKey(k)) found.Add(new Binding(rec.FormKey, kind, entry.Name ?? "", prop.Name ?? "", k, via, null));
                            else if (op.Alias >= 0 && rec is IQuestGetter qq2)
                            {
                                var al = qq2.Aliases.FirstOrDefault(a => a.ID == (uint)op.Alias);
                                if (al?.ForcedReference?.FormKeyNullable is { } fr && sx.Switches.ContainsKey(fr))
                                    found.Add(new Binding(rec.FormKey, kind, entry.Name ?? "", prop.Name ?? "", fr, via + " (alias-bound property)", al.Name));
                            }
                        }
                        else if (prop is IScriptObjectListPropertyGetter lp)
                            foreach (var o in lp.Objects)
                                if (o.Object.FormKeyNullable is { } k2 && sx.Switches.ContainsKey(k2)) found.Add(new Binding(rec.FormKey, kind, entry.Name ?? "", prop.Name ?? "", k2, via + " (list)", null));
                    }
                    foreach (var entry in vmad.Scripts) foreach (var prop in entry.Properties) Prop(entry, prop, "script");
                    if (vmad is IQuestAdapterGetter qa)
                    {
                        foreach (var fr in qa.Fragments)
                            if (!string.IsNullOrEmpty(fr.FragmentName)) questFragmentStage[(rec.FormKey, fr.FragmentName!)] = fr.Stage;
                        foreach (var al in qa.Aliases)
                            foreach (var entry in al.Scripts) foreach (var prop in entry.Properties) Prop(entry, prop, "alias script");
                    }
                    if (rec is IQuestGetter q)
                    {
                        questEdid[q.FormKey] = q.EditorID;
                        foreach (var al in q.Aliases)
                        {
                            if (!string.IsNullOrEmpty(al.Name)) questAliasName[(q.FormKey, (int)al.ID)] = al.Name!;
                            if (al.ForcedReference?.FormKeyNullable is { } fr && sx.Switches.ContainsKey(fr))
                            {
                                // The alias route: every script on the quest can reach the marker as Alias_<name>; the alias's own
                                // scripts as GetReference() / GetRef() on Self. Recorded once per (quest, alias).
                                string aliasProp = "Alias_" + al.Name;
                                foreach (var entry in vmad.Scripts) found.Add(new Binding(rec.FormKey, kind, entry.Name ?? "", aliasProp, fr, "forced-reference alias", al.Name));
                                if (vmad is IQuestAdapterGetter qa2)
                                    foreach (var fa in qa2.Aliases)
                                        if (fa.Property?.Alias == al.ID) foreach (var entry in fa.Scripts) found.Add(new Binding(rec.FormKey, kind, entry.Name ?? "", "Self", fr, "forced-reference alias script", al.Name));
                            }
                        }
                    }
                    // A script on the switch ITSELF: Self.Enable() / Disable() with no property at all.
                    if (rec is IPlacedGetter && sx.Switches.ContainsKey(rec.FormKey))
                        foreach (var entry in vmad.Scripts) found.Add(new Binding(rec.FormKey, kind, entry.Name ?? "", "Self", rec.FormKey, "script on the switch itself", null));
                    // The linked-ref route: a scripted reference whose script calls GetLinkedRef().Enable() flips the switch it
                    // links to (XLKR) — the HearthFires room-finish idiom. The keyword filter, if any, is not read.
                    if (rec is IPlacedObjectGetter po)
                        foreach (var l in po.LinkedReferences)
                            if (l.Reference.FormKeyNullable is { } lk && sx.Switches.ContainsKey(lk))
                                foreach (var entry in vmad.Scripts) found.Add(new Binding(rec.FormKey, kind, entry.Name ?? "", "GetLinkedRef", lk, "linked reference of the scripted ref", null));
                    recordEdid[rec.FormKey] = (rec as IMajorRecordGetter)?.EditorID;
                    recordKind[rec.FormKey] = kind;
                    if (found.Count > 0) recordBindings[rec.FormKey] = found; else recordBindings.Remove(rec.FormKey);
                }
            }
            if ((p + 1) % 150 == 0) Console.WriteLine($"   ... bindings: {p + 1}/{paths.Count} plugins, {recordBindings.Count:N0} records, {sw.Elapsed.TotalSeconds:0}s");
        }
        foreach (var (rk, bl) in recordBindings)
        {
            recordsWithBindings++;
            foreach (var b in bl) { if (!bindings.TryGetValue(b.Switch, out var l)) bindings[b.Switch] = l = new List<Binding>(); l.Add(b); }
        }
        return recordsWithBindings;
    }

    /// <summary>4b. Each bound script resolved through the VFS and decompiled once, its Enable/Disable calls parsed
    /// (ScriptCalls); a script that cannot be read maps to null, counted by reason.</summary>
    public static void Decompile(AssetResolver.AssetView av, List<string> scripts, Dictionary<string, ScriptCalls?> source,
                                 Dictionary<string, int> unreadWhy, ref int pexUnreadable, ref int pexDecompileFailed, ref int scriptsDecompiled)
    {
        foreach (var name in scripts)
        {
            var rel = $@"Scripts\{name.Replace(':', '\\')}.pex";
            var res = av.ResolveForPlacement(rel);
            if (res.Sources.Count == 0) { source[name] = null; pexUnreadable++; unreadWhy["pex not in the VFS"] = unreadWhy.GetValueOrDefault("pex not in the VFS") + 1; continue; }
            PexFile? pex = null;
            try
            {
                var src = res.Sources[0];
                if (src.LooseFilePath is not null) pex = PexFile.CreateFromFile(src.LooseFilePath, GameCategory.Skyrim);
                else if (src.ArchivePath is not null && AssetResolver.TryReadArchiveEntry(src.ArchivePath, src.EntryPath) is { } bytes)
                { using var ms = new MemoryStream(bytes); pex = PexFile.CreateFromStream(ms, GameCategory.Skyrim); }
            }
            catch { pex = null; }
            if (pex is null) { source[name] = null; pexUnreadable++; unreadWhy["pex unreadable"] = unreadWhy.GetValueOrDefault("pex unreadable") + 1; continue; }
            PapyrusDecompiler.Result dec;
            try { dec = PapyrusDecompiler.DecompileFile(pex); }
            catch { source[name] = null; pexDecompileFailed++; unreadWhy["decompiler threw"] = unreadWhy.GetValueOrDefault("decompiler threw") + 1; continue; }
            scriptsDecompiled++;
            if (dec.FunctionsFailed > 0) pexDecompileFailed++;
            source[name] = ScriptCalls.Parse(dec.Source, dec.FunctionsFailed);
        }
    }
}
