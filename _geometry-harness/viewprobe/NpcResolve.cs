using CharacterViewer.Rendering;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Path = System.IO.Path;

namespace ViewProbe;

// ======================================================================
//  NpcResolve — NPC record → everything the character renderer needs.
//
//  ADAPTED from NPC Plugin Chooser 2's NpcMeshResolver.cs +
//  OutfitDisplayResolver.cs + SynthEBD's HeadPartShapeNames.cs, minus the
//  NPC2-specific machinery (mod scoping / NpcResolutionContext, wig & antler
//  handling, per-mod asset rebasing): viewprobe renders the WINNING load-order
//  state, so the plain link-cache winner is always the right record and asset
//  paths resolve through viewprobe's own MO2-precedence GameAssetResolver.
//
//  The record walk (all verified against the NPC2 implementation):
//    NPC_ → Traits template terminus (a Traits-templated NPC has no appearance
//    of its own — the game renders the terminus's face) → WornArmor (or
//    Race.Skin) → per-slot ARMA race-filtered by Race/ArmorRace →
//    sex-appropriate WorldModel, SkinTexture TXST, AlternateTextures →
//    FaceGen mesh+tint from the FormKey (HousecarlCore.FaceGenPath — the
//    defining-master keystone) → RACE skeleton → QNAM / HCLR / weight /
//    height. Outfit expansion honors LVLI Use-All vs first-yielding-entry.
// ======================================================================

/// <summary>One resolved NPC, ready to hand to the character pipeline.</summary>
sealed record ResolvedNpc(
    FormKey NpcKey,
    FormKey AppearanceKey,          // Traits template terminus (== NpcKey when untemplated)
    string Name,
    INpcGetter Winner,
    ResolvedNpcMeshPaths Paths,
    NpcRuntimeFacts Facts,
    float TotalHeight);             // race height (sex) x NPC height — bake this, not ModelScale

static class NpcEyeShapes
{
    /// <summary>EditorIDs of the NPC's effective head parts of one type (own parts
    /// + race defaults for unoccupied slot types, Extra Parts recursed) — the
    /// renderer's authoritative IsEye input. Adapted from SynthEBD's
    /// HeadPartShapeNames.CollectFromNpcRecord.</summary>
    public static HashSet<string> Collect(INpcGetter npc, ILinkCache linkCache, HeadPart.TypeEnum type)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<FormKey>();

        var npcSlotTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (npc.HeadParts != null)
        {
            foreach (var link in npc.HeadParts)
            {
                if (link == null || link.IsNull) continue;
                if (!linkCache.TryResolve<IHeadPartGetter>(link.FormKey, out var hp)) continue;
                var slot = hp.Type.ToString();
                if (!string.IsNullOrEmpty(slot)) npcSlotTypes.Add(slot);
                if (hp.Type == type) CollectPart(hp, linkCache, names, visited);
            }
        }

        if (!npc.Race.IsNull && linkCache.TryResolve<IRaceGetter>(npc.Race.FormKey, out var race))
        {
            bool female = npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
            var headData = female ? race.HeadData?.Female : race.HeadData?.Male;
            if (headData?.HeadParts != null)
            {
                foreach (var hpRef in headData.HeadParts)
                {
                    if (hpRef.Head.IsNull) continue;
                    if (!linkCache.TryResolve<IHeadPartGetter>(hpRef.Head.FormKey, out var hp)) continue;
                    var slot = hp.Type.ToString();
                    if (!string.IsNullOrEmpty(slot) && npcSlotTypes.Contains(slot)) continue;
                    if (hp.Type == type) CollectPart(hp, linkCache, names, visited);
                }
            }
        }
        return names;
    }

    static void CollectPart(IHeadPartGetter hp, ILinkCache linkCache, HashSet<string> names, HashSet<FormKey> visited)
    {
        if (!visited.Add(hp.FormKey)) return;
        if (!string.IsNullOrEmpty(hp.EditorID)) names.Add(hp.EditorID!);
        if (hp.ExtraParts == null) return;
        foreach (var ep in hp.ExtraParts)
        {
            if (ep == null || ep.IsNull) continue;
            if (linkCache.TryResolve<IHeadPartGetter>(ep.FormKey, out var extra))
                CollectPart(extra, linkCache, names, visited);
        }
    }
}

sealed class NpcRecordResolver
{
    // Biped-slot bit constants (bit = slot - 30). Values match NPC2's.
    const int HeadFaceSlotBit = 0x1;            // slot 30 — carries the FaceGen face; never hidden
    const int HairSlotBit = 0x2;                // slot 31
    const int HeadSlotMask = 0x1 | 0x1000;      // slots 30 + 42 (circlet)
    const int HairSlots = 0x2 | 0x800;          // slots 31 + 41 (wig detection parity)

    readonly IReadOnlyList<ISkyrimModGetter> _modsLowestFirst;
    readonly IReadOnlyList<string> _pluginNamesLowestFirst;
    readonly ILinkCache _linkCache;
    readonly HashSet<string> _installedPlugins;
    Dictionary<string, (FormKey Key, SpidFilterFormType Type)>? _editorIdIndex;
    readonly Dictionary<string, FormKey?> _skyIdCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ResolvedFilterForm?> _spidFormCache = new();

    public ILinkCache LinkCache => _linkCache;

    public NpcRecordResolver(IReadOnlyList<ISkyrimModGetter> modsLowestFirst, IReadOnlyList<string> pluginNamesLowestFirst, ILinkCache linkCache)
    {
        _modsLowestFirst = modsLowestFirst;
        _pluginNamesLowestFirst = pluginNamesLowestFirst;
        _linkCache = linkCache;
        _installedPlugins = new HashSet<string>(pluginNamesLowestFirst, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsPluginInstalled(string name) => _installedPlugins.Contains(name);

    // ─────────────────────────────────────────────────────────────────────
    //  Config-identifier resolution (shared by the SkyPatcher and SPID stacks)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Case-insensitive EditorID → (FormKey, type) over the record types
    /// the distributors reference. Built lazily once, by walking each plugin's
    /// TYPED groups in load order (later plugin overwrites — the highest-priority
    /// plugin's record wins an EditorID, matching a winning-override index).</summary>
    Dictionary<string, (FormKey Key, SpidFilterFormType Type)> EditorIdIndex()
    {
        if (_editorIdIndex != null) return _editorIdIndex;
        var index = new Dictionary<string, (FormKey, SpidFilterFormType)>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in _modsLowestFirst)
        {
            void Add(IEnumerable<IMajorRecordGetter> records, SpidFilterFormType type)
            {
                foreach (var rec in records)
                {
                    if (string.IsNullOrEmpty(rec.EditorID)) continue;
                    index[rec.EditorID] = (rec.FormKey, type);
                }
            }
            try
            {
                Add(m.Outfits, SpidFilterFormType.Outfit);
                Add(m.Npcs, SpidFilterFormType.Npc);
                Add(m.Keywords, SpidFilterFormType.Keyword);
                Add(m.Factions, SpidFilterFormType.Faction);
                Add(m.Races, SpidFilterFormType.Race);
                Add(m.Classes, SpidFilterFormType.Class);
                Add(m.CombatStyles, SpidFilterFormType.CombatStyle);
                Add(m.VoiceTypes, SpidFilterFormType.VoiceType);
                Add(m.FormLists, SpidFilterFormType.FormList);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"   [npc] EditorID index: {m.ModKey} skipped ({ex.Message})");
            }
        }
        return _editorIdIndex = index;
    }

    /// <summary>SkyPatcher identifier ("Plugin|hexID" or EditorID) → FormKey.
    /// Local IDs try the full 24-bit mask first, then the light-plugin 12-bit
    /// mask, mirroring SkyPatcher's own resolver.</summary>
    public FormKey? ResolveSkyPatcherIdentifier(string raw)
    {
        if (_skyIdCache.TryGetValue(raw, out var cached)) return cached;
        var id = SkyPatcherOutfitParser.ParseIdentifier(raw);
        FormKey? result = id.Kind switch
        {
            RuntimeFormIdentifierKind.ModAndLocalId => ResolveModLocalId(id.ModName!, id.LocalOrRuntimeId),
            RuntimeFormIdentifierKind.EditorId => EditorIdIndex().TryGetValue(id.EditorId!, out var hit) ? hit.Key : null,
            _ => null,
        };
        _skyIdCache[raw] = result;
        return result;
    }

    FormKey? ResolveModLocalId(string modName, uint localId)
    {
        if (!ModKey.TryFromNameAndExtension(modName, out var modKey)) return null;
        var full = new FormKey(modKey, localId & 0xFFFFFF);
        if (_linkCache.TryResolve(full, out IMajorRecordGetter _)) return full;
        var light = new FormKey(modKey, localId & 0xFFF);
        if (!light.Equals(full) && _linkCache.TryResolve(light, out IMajorRecordGetter _)) return light;
        // Not resolvable — the nominal key, so equality filters simply never
        // match (mirrors a missing form); null when the plugin isn't present.
        return _installedPlugins.Contains(modKey.FileName.String) ? full : null;
    }

    public ResolvedFilterForm? ResolveSpidFilterForm(RuntimeFormIdentifier id)
    {
        var cacheKey = id.Raw + "" + id.Kind;
        if (_spidFormCache.TryGetValue(cacheKey, out var cached)) return cached;
        var fk = ResolveSpidFormIdentifier(id);
        var result = fk == null ? null : ClassifyForm(fk.Value, depth: 0);
        _spidFormCache[cacheKey] = result;
        return result;
    }

    public FormKey? ResolveSpidFormIdentifier(RuntimeFormIdentifier id)
    {
        switch (id.Kind)
        {
            case RuntimeFormIdentifierKind.ModAndLocalId:
                return ResolveModLocalId(id.ModName!, id.LocalOrRuntimeId);
            case RuntimeFormIdentifierKind.EditorId:
                return EditorIdIndex().TryGetValue(id.EditorId!, out var hit) ? hit.Key : null;
            case RuntimeFormIdentifierKind.RuntimeFormId:
            {
                // Runtime FormIDs carry a load-order slot in the top byte. These
                // come from SPID's sanitizer rewriting zero-padded vanilla IDs, so
                // only the base-game slots (0-4 — the five vanilla masters at the
                // head of any SE order) are mapped; anything else is guesswork.
                uint slot = id.LocalOrRuntimeId >> 24;
                if (slot <= 4 && _pluginNamesLowestFirst.Count > (int)slot &&
                    ModKey.TryFromNameAndExtension(_pluginNamesLowestFirst[(int)slot], out var mk))
                {
                    return new FormKey(mk, id.LocalOrRuntimeId & 0xFFFFFF);
                }
                return null;
            }
            default:
                return null;
        }
    }

    ResolvedFilterForm? ClassifyForm(FormKey fk, int depth)
    {
        if (_linkCache.TryResolve<IKeywordGetter>(fk, out _)) return new ResolvedFilterForm(fk, SpidFilterFormType.Keyword);
        if (_linkCache.TryResolve<IFactionGetter>(fk, out _)) return new ResolvedFilterForm(fk, SpidFilterFormType.Faction);
        if (_linkCache.TryResolve<IRaceGetter>(fk, out _)) return new ResolvedFilterForm(fk, SpidFilterFormType.Race);
        if (_linkCache.TryResolve<IClassGetter>(fk, out _)) return new ResolvedFilterForm(fk, SpidFilterFormType.Class);
        if (_linkCache.TryResolve<ICombatStyleGetter>(fk, out _)) return new ResolvedFilterForm(fk, SpidFilterFormType.CombatStyle);
        if (_linkCache.TryResolve<IVoiceTypeGetter>(fk, out _)) return new ResolvedFilterForm(fk, SpidFilterFormType.VoiceType);
        if (_linkCache.TryResolve<INpcGetter>(fk, out _)) return new ResolvedFilterForm(fk, SpidFilterFormType.Npc);
        if (_linkCache.TryResolve<IOutfitGetter>(fk, out _)) return new ResolvedFilterForm(fk, SpidFilterFormType.Outfit);
        if (_linkCache.TryResolve<ISpellGetter>(fk, out _)) return new ResolvedFilterForm(fk, SpidFilterFormType.Spell);
        if (_linkCache.TryResolve<IPerkGetter>(fk, out _)) return new ResolvedFilterForm(fk, SpidFilterFormType.Perk);
        if (_linkCache.TryResolve<IArmorGetter>(fk, out _)) return new ResolvedFilterForm(fk, SpidFilterFormType.Armor);
        if (_linkCache.TryResolve<IFormListGetter>(fk, out var formList))
        {
            var members = new List<ResolvedFilterForm>();
            if (depth < 5 && formList.Items != null)
            {
                foreach (var item in formList.Items)
                {
                    if (item == null || item.FormKey.IsNull) continue;
                    var member = ClassifyForm(item.FormKey, depth + 1);
                    if (member != null) members.Add(member);
                }
            }
            return new ResolvedFilterForm(fk, SpidFilterFormType.FormList, members);
        }
        if (_linkCache.TryResolve(fk, out IMajorRecordGetter _)) return new ResolvedFilterForm(fk, SpidFilterFormType.Unsupported);
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  NPC facts (what the runtime distributors' filters see)
    // ─────────────────────────────────────────────────────────────────────

    public NpcRuntimeFacts BuildFacts(INpcGetter npc)
    {
        var keywordEdids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keywordKeys = new HashSet<FormKey>();
        void AddKeywords(IReadOnlyList<IFormLinkGetter<IKeywordGetter>>? keywords)
        {
            if (keywords == null) return;
            foreach (var link in keywords)
            {
                if (link == null || link.IsNull) continue;
                keywordKeys.Add(link.FormKey);
                if (_linkCache.TryResolve<IKeywordGetter>(link.FormKey, out var kw) && !string.IsNullOrEmpty(kw.EditorID))
                    keywordEdids.Add(kw.EditorID);
            }
        }
        AddKeywords(npc.Keywords);

        IRaceGetter? race = null;
        if (npc.Race != null && !npc.Race.IsNull)
        {
            _linkCache.TryResolve<IRaceGetter>(npc.Race.FormKey, out race);
            if (race != null) AddKeywords(race.Keywords);
        }
        bool isChild = race != null &&
                       (race.Flags.HasFlag(Race.Flag.Child) ||
                        (race.EditorID?.Contains("RaceChild", StringComparison.OrdinalIgnoreCase) ?? false));

        var factions = new HashSet<FormKey>();
        if (npc.Factions != null)
            foreach (var rank in npc.Factions)
                if (rank?.Faction != null && !rank.Faction.IsNull) factions.Add(rank.Faction.FormKey);

        var spells = new HashSet<FormKey>();
        if (npc.ActorEffect != null)
            foreach (var link in npc.ActorEffect)
                if (link != null && !link.FormKey.IsNull) spells.Add(link.FormKey);

        var perks = new HashSet<FormKey>();
        if (npc.Perks != null)
            foreach (var perk in npc.Perks)
                if (perk?.Perk != null && !perk.Perk.IsNull) perks.Add(perk.Perk.FormKey);

        var items = new HashSet<FormKey>();
        if (npc.Items != null)
            foreach (var entry in npc.Items)
            {
                var itemLink = entry?.Item?.Item;
                if (itemLink != null && !itemLink.IsNull) items.Add(itemLink.FormKey);
            }

        // Self + template chain (SPID matches NPC form filters against template
        // bases too; a leveled template ends the walk).
        var selfKeys = new HashSet<FormKey> { npc.FormKey };
        var selfEdids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(npc.EditorID)) selfEdids.Add(npc.EditorID);
        bool isLeveled = false;
        var templateLink = npc.Template;
        int guard = 0;
        while (templateLink != null && !templateLink.IsNull && guard++ < 10)
        {
            var templateKey = templateLink.FormKey;
            if (!selfKeys.Add(templateKey)) break;
            if (_linkCache.TryResolve<INpcGetter>(templateKey, out var templateNpc))
            {
                if (!string.IsNullOrEmpty(templateNpc.EditorID)) selfEdids.Add(templateNpc.EditorID);
                templateLink = templateNpc.Template;
                continue;
            }
            if (_linkCache.TryResolve<ILeveledNpcGetter>(templateKey, out var lvln))
            {
                if (!string.IsNullOrEmpty(lvln.EditorID)) selfEdids.Add(lvln.EditorID);
                isLeveled = true;
            }
            break;
        }

        var originPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in selfKeys) originPlugins.Add(key.ModKey.FileName.String);

        var cfg = npc.Configuration;
        bool hasPcLevelMult = cfg.Level is IPcLevelMultGetter;
        ushort level = cfg.Level is INpcLevelGetter npcLevel ? (ushort)Math.Max((int)npcLevel.Level, 0) : (ushort)0;

        return new NpcRuntimeFacts
        {
            NpcFormKey = npc.FormKey,
            EditorId = npc.EditorID,
            Name = npc.Name?.String,
            IsFemale = IsFemale(npc),
            IsUnique = cfg.Flags.HasFlag(NpcConfiguration.Flag.Unique),
            IsSummonable = cfg.Flags.HasFlag(NpcConfiguration.Flag.Summonable),
            IsChild = isChild,
            IsLeveled = isLeveled,
            // StartsDead is a placed-reference flag; false keeps 'D'-trait
            // (corpse outfit) entries from matching a living render.
            StartsDead = false,
            HasPcLevelMult = hasPcLevelMult,
            Level = level,
            RaceFormKey = npc.Race != null && !npc.Race.IsNull ? npc.Race.FormKey : null,
            ClassFormKey = npc.Class != null && !npc.Class.IsNull ? npc.Class.FormKey : null,
            CombatStyleFormKey = npc.CombatStyle != null && !npc.CombatStyle.IsNull ? npc.CombatStyle.FormKey : null,
            VoiceTypeFormKey = npc.Voice != null && !npc.Voice.IsNull ? npc.Voice.FormKey : null,
            SkinFormKey = npc.WornArmor != null && !npc.WornArmor.IsNull ? npc.WornArmor.FormKey : null,
            DefaultOutfitFormKey = npc.DefaultOutfit != null && !npc.DefaultOutfit.IsNull ? npc.DefaultOutfit.FormKey : null,
            KeywordEditorIds = keywordEdids,
            KeywordFormKeys = keywordKeys,
            FactionFormKeys = factions,
            SpellFormKeys = spells,
            PerkFormKeys = perks,
            InventoryItemFormKeys = items,
            SelfAndTemplateFormKeys = selfKeys,
            SelfAndTemplateEditorIds = selfEdids,
            OriginPluginNames = originPlugins,
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Appearance resolution
    // ─────────────────────────────────────────────────────────────────────

    public static bool IsFemale(INpcGetter npc) => npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);

    /// <summary>The NPC whose appearance is actually drawn: itself, or the end of
    /// its Traits template chain. A chain that can't be followed (dangling link,
    /// Leveled-NPC terminus, cycle) yields the input unchanged.</summary>
    public FormKey ResolveAppearanceTerminus(FormKey npcFormKey, int maxDepth = 50)
    {
        if (!_linkCache.TryResolve<INpcGetter>(npcFormKey, out var current)) return npcFormKey;
        var visited = new HashSet<FormKey> { npcFormKey };
        var terminus = npcFormKey;
        for (int depth = 0; depth < maxDepth; depth++)
        {
            bool templated = current.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits)
                             && current.Template != null && !current.Template.IsNull;
            if (!templated) return terminus;
            var next = current.Template!.FormKey;
            if (!visited.Add(next)) return npcFormKey;                                  // cycle
            if (!_linkCache.TryResolve<INpcGetter>(next, out var nextNpc)) return npcFormKey;  // dangling / leveled
            terminus = next;
            current = nextNpc;
        }
        return npcFormKey;
    }

    /// <summary>Full resolution for one NPC: winner record, Traits terminus, mesh
    /// paths, runtime facts, world height. Null when the record can't be resolved.</summary>
    public ResolvedNpc? Resolve(FormKey npcFormKey)
    {
        if (!_linkCache.TryResolve<INpcGetter>(npcFormKey, out var targetWinner))
        {
            Console.Error.WriteLine($"   [npc] {npcFormKey}: no NPC record in the load order");
            return null;
        }

        var appearanceKey = ResolveAppearanceTerminus(npcFormKey);
        if (!_linkCache.TryResolve<INpcGetter>(appearanceKey, out var npc)) { npc = targetWinner; appearanceKey = npcFormKey; }
        if (!appearanceKey.Equals(npcFormKey))
            Console.WriteLine($"   [npc] {npcFormKey} inherits its traits from template {appearanceKey} — rendering the template's appearance");

        var sex = IsFemale(npc) ? Sex.Female : Sex.Male;
        string sexLabel = sex == Sex.Female ? "Female" : "Male";
        string npcName = targetWinner.Name?.String ?? targetWinner.EditorID ?? npcFormKey.ToString();

        FormKey? npcRaceKey = npc.Race != null && !npc.Race.IsNull ? npc.Race.FormKey : null;
        IRaceGetter? race = null;
        if (npcRaceKey != null) _linkCache.TryResolve<IRaceGetter>(npcRaceKey.Value, out race);
        FormKey? armorRaceKey = ArmorRaceKey(race);

        var (armorGetter, armorSource) = ResolveWornArmor(npc, npcName);

        string? bodyPath = null, handsPath = null, feetPath = null, hairPath = null, tailPath = null;
        var chains = new Dictionary<string, string>();
        var txstTextures = new Dictionary<string, Dictionary<int, string>>();

        if (armorGetter?.Armature != null)
        {
            foreach (var armaLink in armorGetter.Armature)
            {
                if (armaLink == null || armaLink.IsNull) continue;
                if (!_linkCache.TryResolve<IArmorAddonGetter>(armaLink.FormKey, out var arma)) continue;
                if (arma.BodyTemplate == null) continue;
                if (!ArmaNamesRace(arma, npcRaceKey, armorRaceKey)) continue;

                var flags = (int)arma.BodyTemplate.FirstPersonFlags;
                string? meshPath = WorldModelPath(arma, sex);
                if (meshPath == null) continue;

                string meshFileName = Path.GetFileName(meshPath);
                var txstPaths = ResolveTxstTextures(arma, sex, armaLink.FormKey);

                void Take(ref string? slotPath, string part)
                {
                    slotPath = meshPath;
                    chains[part] = $"{npcName} → {armorSource} → Armature({part}):{armaLink.FormKey} → {sexLabel} → {meshFileName}";
                    if (txstPaths.Count > 0) txstTextures[part] = txstPaths;
                }
                if (bodyPath == null && (flags & (int)BipedObjectFlag.Body) != 0) Take(ref bodyPath, "Body");
                if (handsPath == null && (flags & (int)BipedObjectFlag.Hands) != 0) Take(ref handsPath, "Hands");
                if (feetPath == null && (flags & (int)BipedObjectFlag.Feet) != 0) Take(ref feetPath, "Feet");
                // Hair slots 31/41: overhauls ship a "bald" FaceGen scalp + a wig
                // ARMO in the hair slot — without this the NPC renders hairless.
                if (hairPath == null && (flags & HairSlots) != 0) Take(ref hairPath, "Hair");
                // Tail slot 40: Khajiit / Argonian tails are armatures.
                if (tailPath == null && (flags & (int)BipedObjectFlag.Tail) != 0) Take(ref tailPath, "Tail");
            }
        }

        // FaceGen from the APPEARANCE key — the defining-master pure transform
        // (housecarl-core FaceGenPath, the dark-face keystone: folder = the
        // plugin in the FormKey, never the winner).
        string headPath = FaceGenPath.For(appearanceKey, FaceGenSlot.Mesh);
        string faceTintPath = FaceGenPath.For(appearanceKey, FaceGenSlot.Tint);
        chains["Head"] = $"{npcName} → FaceGen → {Path.GetFileName(headPath)}";

        string? skeletonPath = null;
        var skelModel = sex == Sex.Female ? race?.SkeletalModel?.Female : race?.SkeletalModel?.Male;
        if (skelModel?.File != null && !string.IsNullOrWhiteSpace(skelModel.File.GivenPath))
        {
            skeletonPath = skelModel.File.GivenPath;
            if (!skeletonPath.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) &&
                !skeletonPath.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase))
                skeletonPath = "meshes\\" + skeletonPath;
        }

        (float R, float G, float B)? textureLightingColor = null;
        var qnam = npc.TextureLighting;
        if (qnam != null)
            textureLightingColor = (qnam.Value.R / 255f, qnam.Value.G / 255f, qnam.Value.B / 255f);

        int weight = Math.Clamp((int)npc.Weight, 0, 100);
        float npcHeight = float.IsFinite(npc.Height) && npc.Height > 0f ? npc.Height : 1f;
        float raceHeight = 1f;
        if (race != null)
        {
            float rh = sex == Sex.Female ? race.Height.Female : race.Height.Male;
            if (float.IsFinite(rh) && rh > 0f) raceHeight = rh;
        }

        (float R, float G, float B)? hairRgb = null;
        if (npc.HairColor != null && !npc.HairColor.IsNull &&
            _linkCache.TryResolve<IColorRecordGetter>(npc.HairColor.FormKey, out var hclr))
        {
            var c = hclr.Color;
            hairRgb = (c.R / 255f, c.G / 255f, c.B / 255f);
        }

        var eyeShapeNames = NpcEyeShapes.Collect(npc, _linkCache, HeadPart.TypeEnum.Eyes);

        var paths = new ResolvedNpcMeshPaths
        {
            BodyMeshPath = bodyPath,
            HandsMeshPath = handsPath,
            FeetMeshPath = feetPath,
            HeadMeshPath = headPath,
            HairMeshPath = hairPath,
            TailMeshPath = tailPath,
            Sex = sex,
            SkeletonPath = skeletonPath,
            ResolutionChains = chains,
            TxstTextures = txstTextures,
            FaceTintPath = faceTintPath,
            TextureLightingColor = textureLightingColor,
            NpcWeight = weight,
            NpcBaseHeight = npcHeight,
            HairColorRgb = hairRgb,
            // Null = no RaceMenu hair-slot tint emulation — correct for a load
            // order without skee64's bEnableTintHairSlot; the renderer still
            // prefers the FaceGen's own baked hair colour for wig shapes.
            WornHairSlotTintRgb = null,
            EyeShapeNames = eyeShapeNames,
        };

        // Facts come from the TARGET's winning record — that is the record the
        // runtime distributors see, even when the appearance is a template's.
        var facts = BuildFacts(targetWinner);

        return new ResolvedNpc(npcFormKey, appearanceKey, npcName, targetWinner, paths, facts, raceHeight * npcHeight);
    }

    (IArmorGetter? armor, string source) ResolveWornArmor(INpcGetter npc, string npcName)
    {
        if (npc.WornArmor != null && !npc.WornArmor.IsNull &&
            _linkCache.TryResolve<IArmorGetter>(npc.WornArmor.FormKey, out var wornArmor))
            return (wornArmor, "WornArmor:" + npc.WornArmor.FormKey);

        if (npc.Race != null && !npc.Race.IsNull &&
            _linkCache.TryResolve<IRaceGetter>(npc.Race.FormKey, out var race) &&
            race.Skin != null && !race.Skin.IsNull &&
            _linkCache.TryResolve<IArmorGetter>(race.Skin.FormKey, out var raceSkin))
            return (raceSkin, "Race.Skin:" + race.Skin.FormKey);

        Console.WriteLine($"   [npc] {npcName}: no WornArmor or Race.Skin — body meshes unavailable");
        return (null, "(none)");
    }

    /// <summary>The engine matches ArmorAddons against the actor race's ArmorRace
    /// (RNAM), not just the actor's own race — custom-race followers depend on it.</summary>
    static FormKey? ArmorRaceKey(IRaceGetter? race)
    {
        if (race == null || race.ArmorRace.IsNull) return null;
        var armorRace = race.ArmorRace.FormKey;
        return armorRace.Equals(race.FormKey) ? null : armorRace;
    }

    static bool ArmaNamesRace(IArmorAddonGetter arma, FormKey? npcRaceKey, FormKey? armorRaceKey)
    {
        if (npcRaceKey == null) return true;
        if (arma.Race != null && !arma.Race.IsNull)
        {
            var armaRace = arma.Race.FormKey;
            if (armaRace.Equals(npcRaceKey.Value)) return true;
            if (armorRaceKey.HasValue && armaRace.Equals(armorRaceKey.Value)) return true;
        }
        if (arma.AdditionalRaces != null)
        {
            foreach (var addRace in arma.AdditionalRaces)
            {
                if (addRace == null || addRace.IsNull) continue;
                var addKey = addRace.FormKey;
                if (addKey.Equals(npcRaceKey.Value)) return true;
                if (armorRaceKey.HasValue && addKey.Equals(armorRaceKey.Value)) return true;
            }
        }
        return false;
    }

    static string? WorldModelPath(IArmorAddonGetter arma, Sex sex)
    {
        if (arma.WorldModel == null) return null;
        var model = sex == Sex.Female ? arma.WorldModel.Female : arma.WorldModel.Male;
        if (model?.File == null) return null;
        string path = model.File.GivenPath;
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!path.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase))
            path = "meshes\\" + path;
        return path;
    }

    Dictionary<int, string> ResolveTxstTextures(IArmorAddonGetter arma, Sex sex, FormKey armaFormKey)
    {
        var result = new Dictionary<int, string>();
        try
        {
            if (arma.SkinTexture == null) return result;
            var link = sex == Sex.Female ? arma.SkinTexture.Female : arma.SkinTexture.Male;
            if (link == null || link.IsNull) return result;
            if (!_linkCache.TryResolve<ITextureSetGetter>(link.FormKey, out var txst)) return result;
            PopulateTxstSlots(txst, result);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"   [npc] TXST resolution failed for ARMA {armaFormKey}: {ex.Message}");
        }
        return result;
    }

    /// <summary>TXST slot mapping in the renderer's NIF-order indices, including
    /// the dual-purpose TX02 (subsurface for skin, env mask for gear → both 2 and
    /// 5) and TX03→3 routing NPC2 settled empirically.</summary>
    static void PopulateTxstSlots(ITextureSetGetter txst, Dictionary<int, string> slots)
    {
        void TryAdd(int slot, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string fullPath = path;
            if (!fullPath.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase) &&
                !fullPath.StartsWith("textures/", StringComparison.OrdinalIgnoreCase))
                fullPath = "textures\\" + fullPath;
            slots[slot] = fullPath;
        }
        TryAdd(0, txst.Diffuse?.DataRelativePath.Path);
        TryAdd(1, txst.NormalOrGloss?.DataRelativePath.Path);
        TryAdd(2, txst.EnvironmentMaskOrSubsurfaceTint?.DataRelativePath.Path);
        TryAdd(5, txst.EnvironmentMaskOrSubsurfaceTint?.DataRelativePath.Path);
        TryAdd(3, txst.GlowOrDetailMap?.DataRelativePath.Path);
        TryAdd(3, txst.Height?.DataRelativePath.Path);
        TryAdd(4, txst.Environment?.DataRelativePath.Path);
        TryAdd(6, txst.Multilayer?.DataRelativePath.Path);
        TryAdd(7, txst.BacklightMaskOrSpecular?.DataRelativePath.Path);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Outfit → MeshOverride expansion
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Expands the effective outfit (plus the worn armor's rare headgear
    /// pieces) into renderer MeshOverrides — race-filtered ARMAs, slot-hide rules
    /// intact (a hood on slot 31 hides the hair; headgear never hides the FaceGen
    /// face bit; an open circlet leaves the hair visible).</summary>
    public List<MeshOverride> ResolveOutfitOverrides(INpcGetter npc, FormKey? outfitKey)
    {
        var result = new List<MeshOverride>();
        var seenKeys = new HashSet<string>();
        var sex = IsFemale(npc) ? Sex.Female : Sex.Male;
        FormKey? npcRaceKey = npc.Race != null && !npc.Race.IsNull ? npc.Race.FormKey : null;
        IRaceGetter? race = null;
        if (npcRaceKey != null) _linkCache.TryResolve<IRaceGetter>(npcRaceKey.Value, out race);
        FormKey? armorRaceKey = ArmorRaceKey(race);

        if (outfitKey is { } ok && _linkCache.TryResolve<IOutfitGetter>(ok, out var outfit) && outfit.Items != null)
        {
            var outfitArmors = new List<(IArmorGetter armor, string source)>();
            var seenArmorKeys = new HashSet<FormKey>();
            foreach (var itemLink in outfit.Items)
            {
                if (itemLink == null || itemLink.IsNull) continue;
                CollectOutfitItemArmors(itemLink.FormKey, "Outfit:" + ok, outfitArmors, seenArmorKeys, depth: 0);
            }
            foreach (var (armor, source) in outfitArmors)
                AppendArmorMeshOverrides(armor, source, sex, npcRaceKey, armorRaceKey,
                    includeBody: true, includeHeadgear: true, hairCountsAsHeadgear: true, result, seenKeys);
        }

        // Headgear worn via the skin/worn armor (rare). Body/hands/feet/hair
        // ARMAs there are already the base meshes — head pieces only, and a worn
        // slot-31 "wig" stays base hair (hairCountsAsHeadgear: false).
        var (wornArmor, wornSource) = ResolveWornArmor(npc, npc.EditorID ?? npc.FormKey.ToString());
        if (wornArmor != null)
            AppendArmorMeshOverrides(wornArmor, wornSource, sex, npcRaceKey, armorRaceKey,
                includeBody: false, includeHeadgear: true, hairCountsAsHeadgear: false, result, seenKeys);

        return result;
    }

    void CollectOutfitItemArmors(FormKey itemFormKey, string source,
        List<(IArmorGetter armor, string source)> armors, HashSet<FormKey> seen, int depth)
    {
        if (depth > 10) return;   // pathological leveled-list cycles

        if (_linkCache.TryResolve<IArmorGetter>(itemFormKey, out var armor))
        {
            if (seen.Add(itemFormKey)) armors.Add((armor, source));
            return;
        }

        if (_linkCache.TryResolve<ILeveledItemGetter>(itemFormKey, out var lvli))
        {
            var collected = new List<IArmorGetter>();
            CollectArmorsFromLeveledItem(lvli, depth, collected);
            foreach (var collectedArmor in collected)
                if (seen.Add(collectedArmor.FormKey))
                    armors.Add((collectedArmor, source + " -> LVLI:" + itemFormKey));
        }
        // Non-apparel items (weapons, gold, ...) contribute nothing to the render.
    }

    /// <summary>Use-All lists visit EVERY entry (the engine equips them all); a
    /// plain list stops at the first entry (declared order) yielding at least one
    /// armor — the deterministic stand-in for the engine's runtime roll.</summary>
    void CollectArmorsFromLeveledItem(ILeveledItemGetter lvli, int depth, List<IArmorGetter> collected)
    {
        if (depth > 10 || lvli.Entries == null) return;
        bool useAll = lvli.Flags.HasFlag(LeveledItem.Flag.UseAll);

        foreach (var entry in lvli.Entries)
        {
            var refLink = entry?.Data?.Reference;
            if (refLink == null || refLink.IsNull) continue;
            var fk = refLink.FormKey;

            int countBefore = collected.Count;
            if (_linkCache.TryResolve<IArmorGetter>(fk, out var armor)) collected.Add(armor);
            else if (_linkCache.TryResolve<ILeveledItemGetter>(fk, out var nested))
                CollectArmorsFromLeveledItem(nested, depth + 1, collected);

            if (!useAll && collected.Count > countBefore) return;   // first yielding entry
        }
    }

    void AppendArmorMeshOverrides(IArmorGetter armor, string source, Sex sex,
        FormKey? npcRaceKey, FormKey? armorRaceKey,
        bool includeBody, bool includeHeadgear, bool hairCountsAsHeadgear,
        List<MeshOverride> result, HashSet<string> seenKeys)
    {
        if (armor.Armature == null) return;
        foreach (var armaLink in armor.Armature)
        {
            if (armaLink == null || armaLink.IsNull) continue;
            if (!_linkCache.TryResolve<IArmorAddonGetter>(armaLink.FormKey, out var arma)) continue;
            if (arma.BodyTemplate == null) continue;
            if (!ArmaNamesRace(arma, npcRaceKey, armorRaceKey)) continue;

            int slots = (int)arma.BodyTemplate.FirstPersonFlags;
            if (slots == 0) continue;

            // A piece is headgear when it occupies Head (30) or Circlet (42), or
            // — outfit path only — the Hair slot (31), which is how hoods get
            // flagged. The worn-armor scan keeps hairCountsAsHeadgear false so a
            // worn slot-31 "wig" stays base hair.
            int headMask = HeadSlotMask | (hairCountsAsHeadgear ? HairSlotBit : 0);
            bool isHead = (slots & headMask) != 0;
            if (isHead && !includeHeadgear) continue;
            if (!isHead && !includeBody) continue;

            string? meshPath = WorldModelPath(arma, sex);
            if (meshPath == null) continue;

            string key = (isHead ? "Headgear:" : "Outfit:") + armaLink.FormKey + ":" + slots;
            if (!seenKeys.Add(key)) continue;

            var txst = ResolveTxstTextures(arma, sex, armaLink.FormKey);
            Dictionary<int, string>? textures = txst.Count > 0 ? txst : null;

            IReadOnlyList<AlternateTextureSpec>? alternateTextures = null;
            var model = sex == Sex.Female ? arma.WorldModel?.Female : arma.WorldModel?.Male;
            var alts = model?.AlternateTextures;
            if (alts is { Count: > 0 })
            {
                var specs = new List<AlternateTextureSpec>(alts.Count);
                foreach (var alt in alts)
                {
                    if (alt == null) continue;
                    string shapeName = alt.Name ?? string.Empty;
                    int shapeIndex = alt.Index;
                    if (string.IsNullOrWhiteSpace(shapeName) && shapeIndex < 0) continue;
                    if (alt.NewTexture.IsNull) continue;
                    if (!_linkCache.TryResolve<ITextureSetGetter>(alt.NewTexture.FormKey, out var altTxst)) continue;
                    var slotMap = new Dictionary<int, string>();
                    PopulateTxstSlots(altTxst, slotMap);
                    if (slotMap.Count > 0)
                        specs.Add(new AlternateTextureSpec { ShapeName = shapeName, ShapeIndex = shapeIndex, Textures = slotMap });
                }
                if (specs.Count > 0) alternateTextures = specs;
            }

            // Hide rule: body attire hides exactly the slots it occupies
            // (HidesSlots null = own BipedSlots). Headgear never hides the Head
            // slot bit — the FaceGen face/eyes/brows are tagged slot 30 — but a
            // helmet/hood on the head still hides the hair; an open circlet
            // (slot 42 only) occupies neither and leaves the hair visible.
            int? hides = null;
            if (isHead)
            {
                int headHides = slots & ~HeadFaceSlotBit;
                if ((slots & (HeadFaceSlotBit | HairSlotBit)) != 0)
                    headHides |= HairSlotBit;
                hides = headHides;
            }

            result.Add(new MeshOverride
            {
                Key = key,
                MeshPath = meshPath,
                BipedSlots = slots,
                HidesSlots = hides,
                Kind = isHead ? MeshOverrideKind.Headgear : MeshOverrideKind.Armor,
                Textures = textures,
                AlternateTextures = alternateTextures,
                // viewprobe resolves through its own MO2-precedence folder chain;
                // the broadcast archive fallback only widens where a piece's
                // textures live in a third mod's BSA.
                AllowLoadOrderFallback = true,
                HasWeightVariants = sex == Sex.Female ? arma.WeightSliderEnabled.Female : arma.WeightSliderEnabled.Male,
            });
        }
    }
}
