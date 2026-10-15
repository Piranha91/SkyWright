using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

// PASS 1's PER-PLUGIN WALK (Program.cs): placement history, quest graphs, packages, master sets, the intent census. Held in
// its own class so --self-test builds the index a real run builds; Program.cs keeps the open, the unreadable list and the
// progress line, and hands every table in (they are Program.cs's own, filled here).
sealed class DriftIndex
{
    readonly ModKey[] pluginKey; readonly HashSet<int>[] masterIdx; readonly BosFeed bos; readonly AliasTargets aliasTargets;
    readonly Dictionary<FormKey, FormKey> refBase; readonly Dictionary<FormKey, List<FormKey>> npcPlaced;
    readonly Dictionary<FormKey, (string Type, string? Edid)> baseName; readonly Dictionary<FormKey, (string? Edid, bool Interior)> cellName;
    readonly Dictionary<FormKey, string> refArm; readonly Dictionary<FormKey, FormKey> refEz;
    readonly Dictionary<FormKey, QuestInfo> quests; readonly Dictionary<FormKey, (FormKey Quest, int[] Actors)> scenes;
    readonly Dictionary<FormKey, int[]> packageTargets; readonly Dictionary<FormKey, PackFacts> packFacts;
    readonly Dictionary<FormKey, FormKey[]> npcPackages; readonly HashSet<FormKey> npcTemplated;
    readonly Dictionary<int, HashSet<FormKey>> narrativeQuests; readonly int[] narrativeCount;
    readonly Dictionary<int, Dictionary<string, int>> recordCensus; readonly Dictionary<int, Dictionary<FormKey, int>> cellEdits;

    public DriftIndex(ModKey[] pluginKey, HashSet<int>[] masterIdx, BosFeed bos, AliasTargets aliasTargets,
                      Dictionary<FormKey, FormKey> refBase, Dictionary<FormKey, List<FormKey>> npcPlaced,
                      Dictionary<FormKey, (string Type, string? Edid)> baseName, Dictionary<FormKey, (string? Edid, bool Interior)> cellName,
                      Dictionary<FormKey, string> refArm, Dictionary<FormKey, FormKey> refEz,
                      Dictionary<FormKey, QuestInfo> quests, Dictionary<FormKey, (FormKey Quest, int[] Actors)> scenes,
                      Dictionary<FormKey, int[]> packageTargets, Dictionary<FormKey, PackFacts> packFacts,
                      Dictionary<FormKey, FormKey[]> npcPackages, HashSet<FormKey> npcTemplated,
                      Dictionary<int, HashSet<FormKey>> narrativeQuests, int[] narrativeCount,
                      Dictionary<int, Dictionary<string, int>> recordCensus, Dictionary<int, Dictionary<FormKey, int>> cellEdits)
    {
        this.pluginKey = pluginKey; this.masterIdx = masterIdx; this.bos = bos; this.aliasTargets = aliasTargets;
        this.refBase = refBase; this.npcPlaced = npcPlaced; this.baseName = baseName; this.cellName = cellName;
        this.refArm = refArm; this.refEz = refEz; this.quests = quests; this.scenes = scenes;
        this.packageTargets = packageTargets; this.packFacts = packFacts; this.npcPackages = npcPackages; this.npcTemplated = npcTemplated;
        this.narrativeQuests = narrativeQuests; this.narrativeCount = narrativeCount; this.recordCensus = recordCensus; this.cellEdits = cellEdits;
    }

    static void Bump(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;

    /// <summary>One readable plugin <paramref name="p"/>, in load order. Returns the placements and quest records it added
    /// to the run's two counters.</summary>
    public (long Placements, long QuestRecords) Plugin(int p, ISkyrimModGetter mod)
    {
        long placements = 0, questRecords = 0;
        pluginKey[p] = mod.ModKey;
        bos.Plugin(mod);
        var mset = new HashSet<int>();
        try
        {
            var names = mod.ModHeader.MasterReferences.Select(m => m.Master).ToHashSet();
            for (int q = 0; q < p; q++) if (names.Contains(pluginKey[q])) mset.Add(q);
        }
        catch { /* an unreadable header means we cannot claim what it saw; leave the set empty */ }
        masterIdx[p] = mset;

        foreach (var cp in AliasTargets.Cells(mod))
        {
            cellName[cp.Cell] = (cp.CellEditorId, cp.Interior);
            bos.Cell(cp, withRefs: false);
            foreach (var r in cp.Refs)
            {
                placements++;
                bos.Placed(r);
                refArm[r.FormKey] = r.RecordType;
                if (r.EncounterZone is { } rez) refEz[r.FormKey] = rez; else refEz.Remove(r.FormKey);
                if (!cellEdits.TryGetValue(p, out var ce)) cellEdits[p] = ce = new Dictionary<FormKey, int>();
                ce[cp.Cell] = ce.GetValueOrDefault(cp.Cell) + 1;
                // Record only a plugin that actually CHANGED something. An override that leaves the
                // placement alone is not a step in the story and would swamp the history. Rotation and scale ride
                // along (not part of SameAs — a rotation-only override is still no step) so the BOS hook's
                // "rotation/scale-only" tally compares an override against the record's values, not against zero.
                aliasTargets.Placed(p, cp, r);

                if (r.Base is { } bk)
                {
                    refBase[r.FormKey] = bk;
                    if (r.RecordType.Contains("PlacedNpc", StringComparison.Ordinal))
                    {
                        if (!npcPlaced.TryGetValue(bk, out var l)) npcPlaced[bk] = l = new List<FormKey>(1);
                        if (!l.Contains(r.FormKey)) l.Add(r.FormKey);
                    }
                }
            }
        }

        void Narrative(FormKey? quest)
        {
            narrativeCount[p]++;
            if (quest is { } qk && !qk.IsNull)
            {
                if (!narrativeQuests.TryGetValue(p, out var nq)) narrativeQuests[p] = nq = new HashSet<FormKey>();
                nq.Add(qk);
            }
        }
        foreach (var rec in mod.EnumerateMajorRecords())
        {
            bos.Record(rec);
            aliasTargets.Record(p, rec);
            // The census counts every record the plugin carries, by Mutagen type name — the "all
            // 19 records: 13 REFR, 3 ACHR, 2 CELL, 1 WRLD" evidence line, read instead of recited.
            var census = recordCensus.TryGetValue(p, out var cd) ? cd : recordCensus[p] = new Dictionary<string, int>();
            Bump(census, rec.GetType().Name.Replace("BinaryOverlay", ""));

            if (rec is IQuestGetter q) { quests[q.FormKey] = QuestInfo.From(q, p); questRecords++; Narrative(q.FormKey); }
            else if (rec is ISceneGetter sc)
            {
                scenes[sc.FormKey] = (sc.Quest.FormKeyNullable ?? FormKey.Null,
                                      sc.Actors.Select(a => (int)a.ID).Distinct().ToArray());
                Narrative(sc.Quest.FormKeyNullable);
            }
            // A topic points UP at its quest; INFO lines carry no quest of their own, but the
            // plugin holding them must also carry their parent DIAL, so the quest attribution
            // rides on the topic and the INFO still counts as narrative work.
            else if (rec is IDialogTopicGetter dt) Narrative(dt.Quest.FormKeyNullable);
            else if (rec is IDialogResponsesGetter) Narrative(null);
            else if (rec is IPackageGetter pk)
            {
                // A package's data bag is keyed by slot; the arms that matter are the TARGET ones,
                // and of those only the alias arm names a thing this analysis can follow.
                var t = pk.Data.Values.OfType<IPackageDataTargetGetter>()
                          .Select(d => d.Target).OfType<IPackageTargetAliasGetter>()
                          .Select(x => x.Alias).Distinct().ToArray();
                if (t.Length > 0) packageTargets[pk.FormKey] = t;
                packFacts[pk.FormKey] = PackFacts.From(pk);      // winner wins: later plugins overwrite
            }
            else if (rec is INpcGetter npc)
            {
                baseName[npc.FormKey] = ("Npc", npc.EditorID);
                npcPackages[npc.FormKey] = npc.Packages.Select(x => x.FormKey).ToArray();
                // A templated actor's package list is its TEMPLATE's, and this probe does not walk
                // the template chain — such an actor is unassessable, counted rather than guessed.
                if (!npc.Template.IsNull && npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.AIPackages))
                    npcTemplated.Add(npc.FormKey);
                else npcTemplated.Remove(npc.FormKey);
            }
            else if (rec is IModeledGetter && rec is ISkyrimMajorRecordGetter sm)
                baseName[sm.FormKey] = (rec.GetType().Name.Replace("BinaryOverlay", ""), sm.EditorID);
        }
        return (placements, questRecords);
    }
}
