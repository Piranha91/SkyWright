using System.Globalization;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Path = System.IO.Path;
using Directory = System.IO.Directory;
using File = System.IO.File;

namespace ViewProbe;

// ======================================================================
//  NpcRuntimeOutfits — record-level simulation of the two RUNTIME outfit
//  distributors, so "--npc" renders the outfit the GAME will put on the actor,
//  not merely the record's DefaultOutfit field:
//
//    • SkyPatcher  (Data\SKSE\Plugins\SkyPatcher\npc\**, `outfitDefault=`)
//    • SPID        (Data\*_DISTR.ini, `Outfit =` / `FinalOutfit =` / `Form =`)
//
//  ADAPTED from NPC Plugin Chooser 2's OutfitDistribution folder
//  (OutfitDistributionModels.cs / SkyPatcherOutfitConfigParser.cs /
//  SpidOutfitConfigParser.cs / OutfitDisplayResolver.cs), whose parsing and
//  matching were verified against the actual tool sources, not their docs:
//    - SkyPatcher: github.com/Zzyxz/SkyPatcher @ main (npc.cpp readConfig /
//      process_patch_instructions / findObject, utility.cpp).
//    - SPID: github.com/powerof3/Spell-Perk-Item-Distributor @ main
//      (LookupConfigs / Parser.h / LookupNPC.cpp / Distribute.h) + CLibUtil.
//  Faithful quirks preserved (direct filterByNpcs bypasses vetoes; the sweep
//  gate that makes a filterByNpcs+filterByRaces line's race filter dead; SPID's
//  for_first_form; SPID suspension when SkyPatcher moved the outfit).
//
//  Deliberate deviations from the NPC2 copy, each grounded in the bundled
//  houseCARL grammar references (spid-authoring / skypatcher-authoring):
//    1. CONFIG DISCOVERY runs over viewprobe's reconstructed MO2 VFS (data dir
//       + enabled mod folders + overwrite, loose files only — both tools walk
//       the real filesystem via std::filesystem, so BSA-packed INIs are
//       invisible to them and to this) instead of one live Data folder.
//    2. SPID `Form =` lines are accepted too: the generic Form key infers
//       Outfit when the named record is an OTFT (spid-authoring
//       references/form-types.md "The generic Form type"). NPC2 reads only
//       Outfit/FinalOutfit and would miss those lines.
//    3. SkyPatcher `hasPlugins` / `hasPluginsOr` line gates are evaluated
//       against the load order (skypatcher-authoring grammar-core §6: "the
//       filename gate decides whether the FILE is read; hasPlugins decides
//       whether a LINE applies"). NPC2 ignores both keys entirely.
//    4. The SPID chance gate can be bypassed for DIAGNOSTICS ONLY, so a
//       sub-100%-chance entry that would otherwise hit is reported as a
//       "possible at runtime" note instead of vanishing silently. Application
//       behaviour is unchanged: sub-100 entries never apply (NPC2 semantics —
//       the roll is per-actor at runtime and cannot be depicted).
//
//  FinalOutfit note: it postdates the bundled SPID 7.3.0 reference; handling
//  mirrors NPC2's source-verified simulation exactly (Final and regular
//  entries evaluated in one interleaved config-order list, first passing entry
//  wins). No semantics were invented beyond that.
// ======================================================================

/// <summary>How a raw config-file form reference identifies its target.</summary>
enum RuntimeFormIdentifierKind
{
    /// <summary>"Plugin.esp|1B1D3" (SkyPatcher) or "0x1B1D3~Plugin.esp" (SPID).</summary>
    ModAndLocalId,
    /// <summary>SPID bare hex ("0x14012345"): a RUNTIME FormID whose top byte is a
    /// load-order slot. SPID's sanitizer turns zero-padded vanilla IDs into this.</summary>
    RuntimeFormId,
    /// <summary>A bare EditorID.</summary>
    EditorId,
    /// <summary>SPID only: a value containing ".es" with no '~' — a plugin-name filter.</summary>
    ModName,
}

sealed record RuntimeFormIdentifier
{
    public RuntimeFormIdentifierKind Kind { get; init; }
    public string? ModName { get; init; }
    public uint LocalOrRuntimeId { get; init; }
    public string? EditorId { get; init; }
    public string Raw { get; init; } = string.Empty;
    public override string ToString() => Raw;
}

/// <summary>One SkyPatcher npc-ini line carrying an <c>outfitDefault=</c> directive.</summary>
sealed class SkyPatcherOutfitInstruction
{
    public string SourceFile { get; init; } = string.Empty;   // relative to the npc root
    public int LineNumber { get; init; }
    public string OutfitIdentifier { get; init; } = string.Empty;

    public List<string> FilterByNpcs { get; init; } = new();
    public List<string> FilterByKeywords { get; init; } = new();           // AND within
    public List<string> FilterByKeywordsOr { get; init; } = new();         // OR within
    public List<string> FilterByRaces { get; init; } = new();
    public List<string> FilterByDefaultOutfits { get; init; } = new();
    public List<string> FilterByClass { get; init; } = new();
    public List<string> FilterByCombatStyle { get; init; } = new();
    public List<string> FilterByFactions { get; init; } = new();           // AND within
    public List<string> FilterByFactionsOr { get; init; } = new();         // OR within
    public List<string> FilterByEditorIdContains { get; init; } = new();   // AND within
    public List<string> FilterByEditorIdContainsOr { get; init; } = new(); // OR within
    public List<string> FilterByModNames { get; init; } = new();

    public string? FilterByGender { get; init; }
    public string? RestrictToGender { get; init; }
    public List<string> FilterByNpcsExcluded { get; init; } = new();
    public List<string> FilterByKeywordsExcluded { get; init; } = new();
    public List<string> FilterByFactionsExcluded { get; init; } = new();
    public List<string> FilterByEditorIdContainsExcluded { get; init; } = new();
    public List<string> FilterByClassExclude { get; init; } = new();
    public List<string> RestrictToRaces { get; init; } = new();
    public List<string> RestrictToVoiceType { get; init; } = new();

    /// <summary>Line gates on plugin presence (grammar-core §6) — evaluated here,
    /// against the load order, unlike the NPC2 original which ignored them.</summary>
    public List<string> HasPlugins { get; init; } = new();
    public List<string> HasPluginsOr { get; init; } = new();

    /// <summary>Filter keys present but not evaluated (runtime-only state);
    /// treated as passing and surfaced in provenance.</summary>
    public List<string> UnevaluatedFilterKeys { get; init; } = new();
}

/// <summary>SPID trait filter (Traits section). Null = unconstrained.</summary>
sealed record SpidTraits
{
    public bool? Female { get; init; }
    public bool? Unique { get; init; }
    public bool? Summonable { get; init; }
    public bool? Child { get; init; }
    public bool? Leveled { get; init; }
    public bool? Teammate { get; init; }
    public bool? StartsDead { get; init; }
}

/// <summary>One parsed <c>Outfit =</c> / <c>FinalOutfit =</c> / outfit-shaped
/// <c>Form =</c> line from a *_DISTR.ini.</summary>
sealed class SpidOutfitEntry
{
    public string SourceFile { get; init; } = string.Empty;
    public int LineNumber { get; init; }
    public bool IsFinal { get; init; }
    /// <summary>Came from the generic <c>Form =</c> key; only an outfit if the
    /// named record resolves as OTFT (checked at stack time).</summary>
    public bool FromGenericFormKey { get; init; }

    public RuntimeFormIdentifier OutfitForm { get; init; } = new();

    public List<string> StringsMatch { get; init; } = new();
    public List<string> StringsAny { get; init; } = new();
    public List<string> StringsNot { get; init; } = new();
    public List<string> StringsAll { get; init; } = new();

    public List<RuntimeFormIdentifier> FormsMatch { get; init; } = new();
    public List<RuntimeFormIdentifier> FormsNot { get; init; } = new();
    public List<RuntimeFormIdentifier> FormsAll { get; init; } = new();

    public ushort? MinLevel { get; init; }
    public ushort? MaxLevel { get; init; }
    public SpidTraits Traits { get; init; } = new();
    public double ChancePercent { get; init; } = 100.0;
    public List<string> UnevaluatedFilters { get; init; } = new();
}

/// <summary>The form types SPID's has_form check distinguishes for NPC filters.</summary>
enum SpidFilterFormType
{
    Keyword, Faction, Race, Class, CombatStyle, VoiceType, Npc, Outfit,
    Spell, Perk, Armor, FormList, OtherItem, Unsupported,
}

/// <summary>A form filter resolved against the load order; FormList members pre-expanded.</summary>
sealed record ResolvedFilterForm(
    FormKey Key,
    SpidFilterFormType Type,
    IReadOnlyList<ResolvedFilterForm>? ListMembers = null);

/// <summary>Record-level facts about the winning NPC — what the runtime
/// distributors' filters are matched against.</summary>
sealed class NpcRuntimeFacts
{
    public FormKey NpcFormKey { get; init; }
    public string? EditorId { get; init; }
    public string? Name { get; init; }
    public bool IsFemale { get; init; }
    public bool IsUnique { get; init; }
    public bool IsSummonable { get; init; }
    public bool IsChild { get; init; }
    public bool IsLeveled { get; init; }
    public bool StartsDead { get; init; }
    public bool HasPcLevelMult { get; init; }
    public ushort Level { get; init; }

    public FormKey? RaceFormKey { get; init; }
    public FormKey? ClassFormKey { get; init; }
    public FormKey? CombatStyleFormKey { get; init; }
    public FormKey? VoiceTypeFormKey { get; init; }
    public FormKey? SkinFormKey { get; init; }
    public FormKey? DefaultOutfitFormKey { get; init; }

    public HashSet<string> KeywordEditorIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<FormKey> KeywordFormKeys { get; init; } = new();
    public HashSet<FormKey> FactionFormKeys { get; init; } = new();
    public HashSet<FormKey> SpellFormKeys { get; init; } = new();
    public HashSet<FormKey> PerkFormKeys { get; init; } = new();
    public HashSet<FormKey> InventoryItemFormKeys { get; init; } = new();
    public HashSet<FormKey> SelfAndTemplateFormKeys { get; init; } = new();
    public HashSet<string> SelfAndTemplateEditorIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> OriginPluginNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

// ======================================================================
//  VFS-union config discovery. Both tools walk the REAL filesystem of the
//  virtual Data folder; viewprobe reconstructs that folder as a union of
//  roots with MO2 precedence (winner = highest root that has the file).
//  Loose files only, faithfully: neither tool can read an INI out of a BSA.
// ======================================================================
static class VfsConfigDiscovery
{
    /// <summary>Roots lowest-priority-first: game Data, then enabled mod folders
    /// lowest→highest, then MO2 overwrite.</summary>
    public static List<string> RootsLowestFirst(string dataDir, IReadOnlyList<string> modFoldersHighestFirst, string overwriteDir)
    {
        var roots = new List<string> { dataDir };
        for (int i = modFoldersHighestFirst.Count - 1; i >= 0; i--) roots.Add(modFoldersHighestFirst[i]);
        if (!string.IsNullOrWhiteSpace(overwriteDir) && Directory.Exists(overwriteDir)) roots.Add(overwriteDir);
        return roots;
    }

    /// <summary>SPID discovery (CLibUtil get_configs): virtual Data\*.ini, top
    /// level only, name must contain "_DISTR" and end ".ini" (both
    /// case-sensitive), processing order = ordinal sort. In the virtual folder
    /// every candidate shares the same directory, so sorting the file names is
    /// exactly SPID's full-path sort. Returns (file name, winning absolute path).</summary>
    public static List<(string Name, string AbsPath)> SpidConfigs(IReadOnlyList<string> rootsLowestFirst)
    {
        var winner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // name -> abs (later roots overwrite)
        var casing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // name -> winner's exact casing
        foreach (var root in rootsLowestFirst)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            string[] files;
            try { files = Directory.GetFiles(root); } catch { continue; }
            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                if (!name.EndsWith(".ini", StringComparison.Ordinal)) continue;
                if (!name.Contains("_DISTR", StringComparison.Ordinal)) continue;
                winner[name] = f;
                casing[name] = name;
            }
        }
        return winner
            .Select(kv => (Name: casing[kv.Key], AbsPath: kv.Value))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>SkyPatcher npc-config discovery (npc.cpp readConfig): BFS over the
    /// virtual <c>SKSE\Plugins\SkyPatcher\npc</c> tree — every file in a folder
    /// before any subfolder, per-directory case-insensitive name order (NTFS
    /// enumeration order, which the real tool inherits). A file participates when
    /// its name CONTAINS ".ini" (case-sensitive), and a name whose pre-".ini"
    /// prefix contains ".esp"/".esl"/".esm" is plugin-gated. Returns
    /// (relative path, winning absolute path) in processing order.</summary>
    public static List<(string RelPath, string AbsPath)> SkyPatcherNpcConfigs(
        IReadOnlyList<string> rootsLowestFirst, Func<string, bool> isPluginInstalled)
    {
        const string npcRoot = @"SKSE\Plugins\SkyPatcher\npc";
        var results = new List<(string, string)>();
        var pending = new Queue<string>();  // virtual paths relative to npcRoot ("" = the root)
        pending.Enqueue("");

        while (pending.Count > 0)
        {
            var relDir = pending.Dequeue();

            // Union this virtual directory across every root. Entry identity is
            // case-insensitive (Windows filesystem); a file's winner is the
            // HIGHEST root that has it, so content precedence matches MO2.
            var dirs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // name -> winning abs path
            foreach (var root in rootsLowestFirst)
            {
                var abs = Path.Combine(root, npcRoot, relDir);
                if (!Directory.Exists(abs)) continue;
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(abs); } catch { continue; }
                foreach (var e in entries)
                {
                    var name = Path.GetFileName(e);
                    if (Directory.Exists(e)) dirs.Add(name);
                    else files[name] = e;                    // later (higher) root overwrites
                }
            }

            // One sorted pass over the union, files processed inline and
            // subfolders enqueued — the relative order the real BFS produces.
            var names = dirs.Concat(files.Keys).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var name in names)
            {
                var rel = relDir.Length == 0 ? name : Path.Combine(relDir, name);
                if (dirs.Contains(name) && !files.ContainsKey(name)) { pending.Enqueue(rel); continue; }
                if (dirs.Contains(name)) pending.Enqueue(rel);   // name is a dir in one root AND a file in another — walk both

                if (!files.TryGetValue(name, out var absFile)) continue;
                int iniPos = name.IndexOf(".ini", StringComparison.Ordinal);
                if (iniPos < 0) continue;
                var modName = name.Substring(0, iniPos);
                if ((modName.Contains(".esp", StringComparison.Ordinal) ||
                     modName.Contains(".esl", StringComparison.Ordinal) ||
                     modName.Contains(".esm", StringComparison.Ordinal)) &&
                    !isPluginInstalled(modName))
                {
                    continue;
                }
                results.Add((rel, absFile));
            }
        }
        return results;
    }
}

// ======================================================================
//  SPID *_DISTR.ini parsing + record-level matching (NPC2 SpidOutfitConfigParser).
// ======================================================================
sealed class SpidOutfitParser
{
    // SPID's config sanitizer (LookupConfigs.cpp detail::sanitize).
    static readonly Regex ReBareFormId = new(@"\b00+([0-9a-fA-F]{1,6})\b", RegexOptions.Compiled);
    static readonly Regex ReLeadingZeros = new(@"(0x00+)([0-9a-fA-F]+)", RegexOptions.Compiled);

    static string Sanitize(string value)
    {
        var v = ReBareFormId.Replace(value, "0x$1");
        v = ReLeadingZeros.Replace(v, "0x$2");
        return v;
    }

    /// <summary>Parses one _DISTR.ini's lines: Outfit / FinalOutfit entries, plus
    /// generic <c>Form =</c> entries (type inferred at stack time — an OTFT form
    /// makes it an outfit distribution, anything else is some other type's line).</summary>
    public List<SpidOutfitEntry> ParseFile(IReadOnlyList<string> lines, string fileName)
    {
        var result = new List<SpidOutfitEntry>();
        bool inNamedSection = false;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line[0] == ';' || line[0] == '#') continue;
            if (line[0] == '[') { inNamedSection = true; continue; }   // SPID reads only the section-less head
            if (inNamedSection) continue;

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line.Substring(0, eq).Trim();
            bool isFinal = false;
            if (key.StartsWith("Final", StringComparison.Ordinal)) { isFinal = true; key = key.Substring(5); }
            bool generic = key.Equals("Form", StringComparison.Ordinal) && !isFinal;
            if (!generic && !key.Equals("Outfit", StringComparison.Ordinal)) continue;

            var value = Sanitize(line.Substring(eq + 1).Trim());
            var entry = ParseEntry(value, fileName, i + 1, isFinal, generic);
            if (entry != null) result.Add(entry);
        }
        return result;
    }

    public SpidOutfitEntry? ParseEntry(string value, string fileName, int lineNumber, bool isFinal, bool fromGenericForm)
    {
        var sections = value.Split('|');
        if (sections.Length > 7) return null;                       // "Too many sections" — invalid
        string Section(int idx) => idx < sections.Length ? sections[idx].Trim() : string.Empty;

        static bool IsValid(string s) => s.Length > 0 && !s.Equals("NONE", StringComparison.OrdinalIgnoreCase);
        static List<string> SplitEntry(string s) =>
            IsValid(s) ? s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList() : new List<string>();

        var formSection = Section(0);
        if (!IsValid(formSection)) return null;

        var unevaluated = new List<string>();

        // Strings: '+' combine (ALL), '-' exclusion (NOT), '*' contains (ANY).
        var stringsMatch = new List<string>(); var stringsAny = new List<string>();
        var stringsNot = new List<string>(); var stringsAll = new List<string>();
        foreach (var str in SplitEntry(Section(1)))
        {
            if (str.Contains('+')) stringsAll.AddRange(str.Split('+').Select(x => x.Trim()).Where(x => x.Length > 0));
            else if (str.StartsWith('-')) stringsNot.Add(str.Substring(1));
            else if (str.StartsWith('*')) stringsAny.Add(str.Substring(1));
            else stringsMatch.Add(str);
        }

        // Form filters: '+' combine (ALL), '-' exclusion (NOT).
        var formsMatch = new List<RuntimeFormIdentifier>(); var formsNot = new List<RuntimeFormIdentifier>();
        var formsAll = new List<RuntimeFormIdentifier>();
        foreach (var ids in SplitEntry(Section(2)))
        {
            if (ids.Contains('+'))
                formsAll.AddRange(ids.Split('+').Select(x => x.Trim()).Where(x => x.Length > 0).Select(ParseIdentifier));
            else if (ids.StartsWith('-')) formsNot.Add(ParseIdentifier(ids.Substring(1)));
            else formsMatch.Add(ParseIdentifier(ids));
        }

        // Levels: plain chunks are actor level "min" or "min/max"; skill chunks are runtime state.
        ushort? minLevel = null, maxLevel = null;
        foreach (var chunk in SplitEntry(Section(3)))
        {
            if (chunk.Contains('(')) { unevaluated.Add("skill-level filter"); continue; }
            var parts = chunk.Split('/');
            if (parts.Length > 1)
            {
                if (ushort.TryParse(parts[0], out var mn)) minLevel = mn;
                if (ushort.TryParse(parts[1], out var mx)) maxLevel = mx;
            }
            else if (ushort.TryParse(chunk, out var exact)) { minLevel = exact; maxLevel = exact; }
        }

        // Traits: '/'-separated single-letter flags with '-' negation.
        bool? female = null, unique = null, summonable = null, child = null,
            leveled = null, teammate = null, startsDead = null;
        var traitsSection = Section(4);
        if (IsValid(traitsSection))
        {
            foreach (var trait in traitsSection.Split('/').Select(t => t.Trim()).Where(t => t.Length > 0))
            {
                switch (trait)
                {
                    case "M": case "-F": female = false; break;
                    case "F": case "-M": female = true; break;
                    case "U": unique = true; break;
                    case "-U": unique = false; break;
                    case "S": summonable = true; break;
                    case "-S": summonable = false; break;
                    case "C": child = true; break;
                    case "-C": child = false; break;
                    case "L": leveled = true; break;
                    case "-L": leveled = false; break;
                    case "T": teammate = true; break;
                    case "-T": teammate = false; break;
                    case "D": startsDead = true; break;
                    case "-D": startsDead = false; break;
                }
            }
        }

        // Section 5 (IdxOrCount) is irrelevant for outfits. Section 6: chance percent,
        // optional trailing '!' (deterministic per-NPC seed — still a roll from here).
        double chance = 100.0;
        var chanceSection = Section(6);
        if (IsValid(chanceSection))
        {
            var numeric = chanceSection.EndsWith('!') ? chanceSection[..^1] : chanceSection;
            if (double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                chance = parsed;
        }

        return new SpidOutfitEntry
        {
            SourceFile = fileName,
            LineNumber = lineNumber,
            IsFinal = isFinal,
            FromGenericFormKey = fromGenericForm,
            OutfitForm = ParseIdentifier(formSection),
            StringsMatch = stringsMatch, StringsAny = stringsAny, StringsNot = stringsNot, StringsAll = stringsAll,
            FormsMatch = formsMatch, FormsNot = formsNot, FormsAll = formsAll,
            MinLevel = minLevel, MaxLevel = maxLevel,
            Traits = new SpidTraits
            {
                Female = female, Unique = unique, Summonable = summonable,
                Child = child, Leveled = leveled, Teammate = teammate, StartsDead = startsDead,
            },
            ChancePercent = chance,
            UnevaluatedFilters = unevaluated,
        };
    }

    /// <summary>Parses a SPID form reference (CLibUtil get_record): "0x123~Plugin.esp"
    /// → local ID in plugin; a value containing ".es" (case-sensitive) → plugin-name
    /// filter; pure hex → runtime FormID; anything else → EditorID.</summary>
    public static RuntimeFormIdentifier ParseIdentifier(string raw)
    {
        var str = raw.Trim();
        int tilde = str.IndexOf('~');
        if (tilde >= 0)
        {
            var idPart = str.Substring(0, tilde).Trim();
            var modPart = str.Substring(tilde + 1).Trim();
            if (idPart.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) idPart = idPart.Substring(2);
            if (uint.TryParse(idPart, NumberStyles.HexNumber, null, out var localId))
            {
                return new RuntimeFormIdentifier
                {
                    Kind = RuntimeFormIdentifierKind.ModAndLocalId,
                    ModName = modPart,
                    LocalOrRuntimeId = localId & 0xFFFFFF,
                    Raw = raw,
                };
            }
            return new RuntimeFormIdentifier { Kind = RuntimeFormIdentifierKind.EditorId, EditorId = str, Raw = raw };
        }

        if (str.Contains(".es", StringComparison.Ordinal))
            return new RuntimeFormIdentifier { Kind = RuntimeFormIdentifierKind.ModName, ModName = str, Raw = raw };

        var hexBody = str.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? str.Substring(2) : str;
        if (hexBody.Length > 0 && hexBody.All(Uri.IsHexDigit) &&
            uint.TryParse(hexBody, NumberStyles.HexNumber, null, out var runtimeId))
        {
            return new RuntimeFormIdentifier
            {
                Kind = RuntimeFormIdentifierKind.RuntimeFormId,
                LocalOrRuntimeId = runtimeId,
                Raw = raw,
            };
        }
        return new RuntimeFormIdentifier { Kind = RuntimeFormIdentifierKind.EditorId, EditorId = str, Raw = raw };
    }

    public sealed record MatchResult(bool Applies, IReadOnlyList<string> Approximations);

    /// <summary>Record-level filter evaluation (LookupFilters.cpp / LookupNPC.cpp).
    /// Unresolvable MATCH/ALL forms make the entry unmatchable (conservative);
    /// unresolvable NOT forms don't veto. <paramref name="applyChanceGate"/> false
    /// is DIAGNOSTIC ONLY: it reports whether the filters would pass so a sub-100%
    /// entry can be surfaced as "possible at runtime" — never to apply it.</summary>
    public MatchResult MatchesNpc(
        SpidOutfitEntry entry,
        NpcRuntimeFacts facts,
        Func<RuntimeFormIdentifier, ResolvedFilterForm?> resolveFilterForm,
        bool applyChanceGate = true)
    {
        var noMatch = new MatchResult(false, Array.Empty<string>());
        var approximations = new List<string>(entry.UnevaluatedFilters);

        if (applyChanceGate && entry.ChancePercent < 100.0) return noMatch;

        bool HasStringExact(string s) =>
            facts.KeywordEditorIds.Contains(s) ||
            (facts.Name != null && facts.Name.Equals(s, StringComparison.OrdinalIgnoreCase)) ||
            facts.SelfAndTemplateEditorIds.Contains(s);
        bool HasStringContains(string s) =>
            (facts.Name != null && facts.Name.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
            facts.SelfAndTemplateEditorIds.Any(id => id.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
            facts.KeywordEditorIds.Any(k => k.Contains(s, StringComparison.OrdinalIgnoreCase));

        if (entry.StringsAll.Count > 0 && !entry.StringsAll.All(HasStringExact)) return noMatch;
        if (entry.StringsNot.Count > 0 && entry.StringsNot.Any(HasStringExact)) return noMatch;
        if (entry.StringsMatch.Count > 0 && !entry.StringsMatch.Any(HasStringExact)) return noMatch;
        if (entry.StringsAny.Count > 0 && !entry.StringsAny.Any(HasStringContains)) return noMatch;

        bool HasForm(ResolvedFilterForm form)
        {
            switch (form.Type)
            {
                case SpidFilterFormType.Keyword: return facts.KeywordFormKeys.Contains(form.Key);
                case SpidFilterFormType.Faction: return facts.FactionFormKeys.Contains(form.Key);
                case SpidFilterFormType.Race: return facts.RaceFormKey != null && facts.RaceFormKey.Value.Equals(form.Key);
                case SpidFilterFormType.Class: return facts.ClassFormKey != null && facts.ClassFormKey.Value.Equals(form.Key);
                case SpidFilterFormType.CombatStyle: return facts.CombatStyleFormKey != null && facts.CombatStyleFormKey.Value.Equals(form.Key);
                case SpidFilterFormType.VoiceType: return facts.VoiceTypeFormKey != null && facts.VoiceTypeFormKey.Value.Equals(form.Key);
                case SpidFilterFormType.Npc: return facts.SelfAndTemplateFormKeys.Contains(form.Key);
                case SpidFilterFormType.Outfit: return facts.DefaultOutfitFormKey != null && facts.DefaultOutfitFormKey.Value.Equals(form.Key);
                case SpidFilterFormType.Spell: return facts.SpellFormKeys.Contains(form.Key);
                case SpidFilterFormType.Perk: return facts.PerkFormKeys.Contains(form.Key);
                case SpidFilterFormType.Armor:
                    return (facts.SkinFormKey != null && facts.SkinFormKey.Value.Equals(form.Key)) ||
                           facts.InventoryItemFormKeys.Contains(form.Key);
                case SpidFilterFormType.OtherItem: return facts.InventoryItemFormKeys.Contains(form.Key);
                case SpidFilterFormType.FormList:
                    return form.ListMembers != null && form.ListMembers.Any(HasForm);
                case SpidFilterFormType.Unsupported:
                    approximations.Add("unsupported filter form type");
                    return false;
                default: return false;
            }
        }

        bool HasFormOrFile(RuntimeFormIdentifier id)
        {
            if (id.Kind == RuntimeFormIdentifierKind.ModName)
                return id.ModName != null && facts.OriginPluginNames.Contains(id.ModName);
            var form = resolveFilterForm(id);
            return form != null && HasForm(form);
        }

        foreach (var id in entry.FormsAll)
            if (!HasFormOrFile(id)) return noMatch;                 // unresolved ALL form ⇒ unmatchable
        foreach (var id in entry.FormsNot)
            if (HasFormOrFile(id)) return noMatch;                  // unresolved NOT forms simply don't veto
        if (entry.FormsMatch.Count > 0 && !entry.FormsMatch.Any(HasFormOrFile)) return noMatch;

        if (entry.MinLevel != null || entry.MaxLevel != null)
        {
            if (facts.HasPcLevelMult)
                approximations.Add("level filter on a PC-level-mult NPC");
            else
            {
                if (entry.MinLevel != null && facts.Level < entry.MinLevel.Value) return noMatch;
                if (entry.MaxLevel != null && facts.Level > entry.MaxLevel.Value) return noMatch;
            }
        }

        var t = entry.Traits;
        if (t.Female != null && t.Female.Value != facts.IsFemale) return noMatch;
        if (t.Unique != null && t.Unique.Value != facts.IsUnique) return noMatch;
        if (t.Summonable != null && t.Summonable.Value != facts.IsSummonable) return noMatch;
        if (t.Child != null && t.Child.Value != facts.IsChild) return noMatch;
        if (t.Leveled != null && t.Leveled.Value != facts.IsLeveled) return noMatch;
        if (t.StartsDead != null && t.StartsDead.Value != facts.StartsDead) return noMatch;
        if (t.Teammate != null)
        {
            approximations.Add("teammate trait (record-level approximation)");
            bool isTeammate = facts.FactionFormKeys.Contains(PotentialFollowerFaction);
            if (t.Teammate.Value != isTeammate) return noMatch;
        }

        return new MatchResult(true, approximations);
    }

    /// <summary>Skyrim.esm PotentialFollowerFaction — SPID's record-level teammate signal.</summary>
    public static readonly FormKey PotentialFollowerFaction = FormKey.Factory("05C84D:Skyrim.esm");
}

// ======================================================================
//  SkyPatcher npc-ini parsing + matching (NPC2 SkyPatcherOutfitConfigParser).
// ======================================================================
sealed class SkyPatcherOutfitParser
{
    static readonly RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled;
    static Regex Key(string key) => new($@"{key}\s*=([^:]+)", Opts);

    static readonly Regex ReOutfitDefault = Key("outfitDefault");
    static readonly Regex ReNpcs = Key("filterByNpcs");
    static readonly Regex ReNpcsExcluded = Key("filterByNpcsExcluded");
    static readonly Regex ReKeywords = Key("filterByKeywords");
    static readonly Regex ReKeywordsOr = Key("filterByKeywordsOr");
    static readonly Regex ReKeywordsExcluded = Key("filterByKeywordsExcluded");
    static readonly Regex ReRaces = Key("filterByRaces");
    static readonly Regex ReDefaultOutfits = Key("filterByDefaultOutfits");
    static readonly Regex ReClass = Key("filterByClass");
    static readonly Regex ReClassExclude = Key("filterByClassExclude");
    static readonly Regex ReCombatStyle = Key("filterByCombatStyle");
    static readonly Regex ReFactions = Key("filterByFactions");
    static readonly Regex ReFactionsOr = Key("filterByFactionsOr");
    static readonly Regex ReFactionsExcluded = Key("filterByFactionsExcluded");
    static readonly Regex ReEdidContains = Key("filterByEditorIdContains");
    static readonly Regex ReEdidContainsOr = Key("filterByEditorIdContainsOr");
    static readonly Regex ReEdidContainsExcluded = Key("filterByEditorIdContainsExcluded");
    static readonly Regex ReModNames = Key("filterByModNames");
    static readonly Regex ReGender = Key("filterByGender");
    static readonly Regex ReRestrictGender = Key("restrictToGender");
    static readonly Regex ReRestrictRaces = Key("restrictToRaces");
    static readonly Regex ReRestrictVoice = Key("restrictToVoiceType");
    static readonly Regex ReHasPlugins = Key("hasPlugins");
    static readonly Regex ReHasPluginsOr = Key("hasPluginsOr");

    /// <summary>Filter keys treated as "pass" (runtime- or niche-specific);
    /// presence recorded so provenance can flag the approximation.</summary>
    static readonly (string Name, Regex Re)[] UnevaluatedKeys =
    {
        ("restrictToSkill", Key("restrictToSkill")),
        ("restrictToFlags", Key("restrictToFlags")),
        ("restrictToTemplateFlags", Key("restrictToTemplateFlags")),
        ("restrictToKeywords", Key("restrictToKeywords")),
        ("restrictToMaleModelContains", Key("restrictToMaleModelContains")),
        ("restrictToCombatStyle", Key("restrictToCombatStyle")),
        ("filterByPCLevelMult", Key("filterByPCLevelMult")),
        ("filterByAutoCalc", Key("filterByAutoCalc")),
        ("filterByEssential", Key("filterByEssential")),
        ("filterByProtected", Key("filterByProtected")),
    };

    public List<SkyPatcherOutfitInstruction> ParseFile(IReadOnlyList<string> lines, string relativePath)
    {
        var result = new List<SkyPatcherOutfitInstruction>();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrEmpty(line)) continue;
            if (line[0] == ';') continue;

            var outfit = ExtractSingle(ReOutfitDefault, line);
            if (string.IsNullOrEmpty(outfit)) continue;

            var unevaluated = new List<string>();
            foreach (var (name, re) in UnevaluatedKeys)
                if (re.IsMatch(line)) unevaluated.Add(name);

            result.Add(new SkyPatcherOutfitInstruction
            {
                SourceFile = relativePath,
                LineNumber = i + 1,
                OutfitIdentifier = outfit,
                FilterByNpcs = ExtractList(ReNpcs, line),
                FilterByNpcsExcluded = ExtractList(ReNpcsExcluded, line),
                FilterByKeywords = ExtractList(ReKeywords, line),
                FilterByKeywordsOr = ExtractList(ReKeywordsOr, line),
                FilterByKeywordsExcluded = ExtractList(ReKeywordsExcluded, line),
                FilterByRaces = ExtractList(ReRaces, line),
                FilterByDefaultOutfits = ExtractList(ReDefaultOutfits, line),
                FilterByClass = ExtractList(ReClass, line),
                FilterByClassExclude = ExtractList(ReClassExclude, line),
                FilterByCombatStyle = ExtractList(ReCombatStyle, line),
                FilterByFactions = ExtractList(ReFactions, line),
                FilterByFactionsOr = ExtractList(ReFactionsOr, line),
                FilterByFactionsExcluded = ExtractList(ReFactionsExcluded, line),
                FilterByEditorIdContains = ExtractList(ReEdidContains, line),
                FilterByEditorIdContainsOr = ExtractList(ReEdidContainsOr, line),
                FilterByEditorIdContainsExcluded = ExtractList(ReEdidContainsExcluded, line),
                FilterByModNames = ExtractList(ReModNames, line),
                FilterByGender = ExtractSingle(ReGender, line),
                RestrictToGender = ExtractSingle(ReRestrictGender, line),
                RestrictToRaces = ExtractList(ReRestrictRaces, line),
                RestrictToVoiceType = ExtractList(ReRestrictVoice, line),
                // "hasPlugins" does NOT false-match a "hasPluginsOr=" key: the
                // regex requires '=' right after the key name (same property the
                // NPC2 original relies on for Keywords vs KeywordsOr).
                HasPlugins = ExtractList(ReHasPlugins, line),
                HasPluginsOr = ExtractList(ReHasPluginsOr, line),
                UnevaluatedFilterKeys = unevaluated,
            });
        }
        return result;
    }

    static string? ExtractSingle(Regex re, string line)
    {
        var m = re.Match(line);
        if (!m.Success) return null;
        var v = m.Groups[1].Value.Trim();
        if (v.Length == 0 || v.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        return v;
    }

    static List<string> ExtractList(Regex re, string line)
    {
        var result = new List<string>();
        var m = re.Match(line);
        if (!m.Success) return result;
        foreach (var raw in m.Groups[1].Value.Split(','))
        {
            var v = raw.Trim();
            if (v.Length == 0 || v.Equals("none", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(v);
        }
        return result;
    }

    /// <summary>Parses a SkyPatcher form identifier: "Plugin|hexID" (local ID) or a
    /// bare EditorID.</summary>
    public static RuntimeFormIdentifier ParseIdentifier(string raw)
    {
        int sep = raw.IndexOf('|');
        if (sep >= 0)
        {
            var modName = raw.Substring(0, sep).Trim();
            var idPart = raw.Substring(sep + 1).Trim();
            idPart = idPart.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? idPart.Substring(2) : idPart;
            if (uint.TryParse(idPart, NumberStyles.HexNumber, null, out uint id))
            {
                return new RuntimeFormIdentifier
                {
                    Kind = RuntimeFormIdentifierKind.ModAndLocalId,
                    ModName = modName,
                    LocalOrRuntimeId = id & 0xFFFFFF,
                    Raw = raw,
                };
            }
            return new RuntimeFormIdentifier { Kind = RuntimeFormIdentifierKind.EditorId, EditorId = raw, Raw = raw };
        }
        return new RuntimeFormIdentifier { Kind = RuntimeFormIdentifierKind.EditorId, EditorId = raw.Trim(), Raw = raw };
    }

    public sealed record MatchResult(bool Applies, bool ViaDirectNpcList, IReadOnlyList<string> Approximations);
    static readonly MatchResult NoMatch = new(false, false, Array.Empty<string>());

    /// <summary>Mirrors process_patch_instructions + findObject, quirks intact:
    /// direct filterByNpcs entries bypass every veto; the whole-array sweep only
    /// runs when a broad gate filter is present or filterByNpcs is empty (so a
    /// filterByNpcs+filterByRaces line's race filter is dead); a line with no
    /// selection filters at all patches every NPC. The hasPlugins/hasPluginsOr
    /// line gates run FIRST (grammar-core §6) — the one check the NPC2 original
    /// did not evaluate.</summary>
    public MatchResult MatchesNpc(
        SkyPatcherOutfitInstruction instruction,
        NpcRuntimeFacts facts,
        Func<string, FormKey?> resolveIdentifier,
        Func<string, bool> isPluginInstalled)
    {
        if (instruction.HasPlugins.Count > 0 && !instruction.HasPlugins.All(isPluginInstalled)) return NoMatch;
        if (instruction.HasPluginsOr.Count > 0 && !instruction.HasPluginsOr.Any(isPluginInstalled)) return NoMatch;

        // Pass 1 — direct filterByNpcs list: unconditional patch, no vetoes.
        foreach (var npcIdentifier in instruction.FilterByNpcs)
        {
            var fk = resolveIdentifier(npcIdentifier);
            if (fk != null && fk.Value.Equals(facts.NpcFormKey))
                return new MatchResult(true, true, instruction.UnevaluatedFilterKeys);
        }

        // Sweep gate (process_patch_instructions:387): filterByRaces /
        // filterByModNames are NOT in the gate list (faithful quirk).
        bool hasBroadGateFilter =
            instruction.FilterByKeywords.Count > 0 || instruction.FilterByKeywordsOr.Count > 0 ||
            instruction.FilterByClass.Count > 0 || instruction.FilterByCombatStyle.Count > 0 ||
            instruction.FilterByFactions.Count > 0 || instruction.FilterByFactionsOr.Count > 0 ||
            instruction.FilterByDefaultOutfits.Count > 0 ||
            instruction.FilterByEditorIdContains.Count > 0 || instruction.FilterByEditorIdContainsOr.Count > 0;
        if (instruction.FilterByNpcs.Count > 0 && !hasBroadGateFilter) return NoMatch;

        if (instruction.FilterByModNames.Count > 0 &&
            !instruction.FilterByModNames.Any(m => facts.OriginPluginNames.Contains(m)))
        {
            return NoMatch;
        }

        bool found = FindObject(instruction, facts, resolveIdentifier);
        return found
            ? new MatchResult(true, false, instruction.UnevaluatedFilterKeys)
            : NoMatch;
    }

    /// <summary>npc.cpp findObject: categories OR in source order, then gender and
    /// the exclusion / restrictTo filters veto.</summary>
    bool FindObject(SkyPatcherOutfitInstruction line, NpcRuntimeFacts facts, Func<string, FormKey?> resolve)
    {
        bool found = false;

        bool keywordAnd = line.FilterByKeywords.Count == 0 ||
                          line.FilterByKeywords.All(k => HasKeyword(facts, k, resolve));
        bool keywordOr = line.FilterByKeywordsOr.Count == 0 ||
                         line.FilterByKeywordsOr.Any(k => HasKeyword(facts, k, resolve));
        if ((line.FilterByKeywords.Count > 0 || line.FilterByKeywordsOr.Count > 0) && keywordAnd && keywordOr)
            found = true;

        if (!found && line.FilterByRaces.Count > 0 && facts.RaceFormKey != null)
            found = line.FilterByRaces.Any(r => resolve(r) is { } fk && fk.Equals(facts.RaceFormKey.Value));

        if (!found && line.FilterByDefaultOutfits.Count > 0 && facts.DefaultOutfitFormKey != null)
            found = line.FilterByDefaultOutfits.Any(o => resolve(o) is { } fk && fk.Equals(facts.DefaultOutfitFormKey.Value));

        if (!found && line.FilterByClass.Count > 0 && facts.ClassFormKey != null)
            found = line.FilterByClass.Any(c => resolve(c) is { } fk && fk.Equals(facts.ClassFormKey.Value));

        if (!found && line.FilterByCombatStyle.Count > 0 && facts.CombatStyleFormKey != null)
            found = line.FilterByCombatStyle.Any(c => resolve(c) is { } fk && fk.Equals(facts.CombatStyleFormKey.Value));

        if (!found)
        {
            bool factionAnd = line.FilterByFactions.Count == 0 ||
                              line.FilterByFactions.All(f => resolve(f) is { } fk && facts.FactionFormKeys.Contains(fk));
            bool factionOr = line.FilterByFactionsOr.Count == 0 ||
                             line.FilterByFactionsOr.Any(f => resolve(f) is { } fk && facts.FactionFormKeys.Contains(fk));
            if ((line.FilterByFactions.Count > 0 || line.FilterByFactionsOr.Count > 0) && factionAnd && factionOr)
                found = true;
        }

        if (!found)
        {
            string edid = facts.EditorId ?? string.Empty;
            bool contains = line.FilterByEditorIdContains.Count == 0 ||
                            line.FilterByEditorIdContains.All(s => edid.Contains(s, StringComparison.OrdinalIgnoreCase));
            bool containsOr = line.FilterByEditorIdContainsOr.Count == 0 ||
                              line.FilterByEditorIdContainsOr.Any(s => edid.Contains(s, StringComparison.OrdinalIgnoreCase));
            if ((line.FilterByEditorIdContains.Count > 0 || line.FilterByEditorIdContainsOr.Count > 0) && contains && containsOr)
                found = true;
        }

        // "Patch everything" fallback: no selection filters at all.
        if (!found &&
            line.FilterByNpcs.Count == 0 && line.FilterByRaces.Count == 0 &&
            line.FilterByKeywords.Count == 0 && line.FilterByKeywordsOr.Count == 0 &&
            line.FilterByClass.Count == 0 && line.FilterByCombatStyle.Count == 0 &&
            line.FilterByFactions.Count == 0 && line.FilterByFactionsOr.Count == 0 &&
            line.FilterByDefaultOutfits.Count == 0 &&
            line.FilterByEditorIdContains.Count == 0 && line.FilterByEditorIdContainsOr.Count == 0)
        {
            found = true;
        }

        if (found && line.FilterByGender != null)
            found = MatchesGender(line.FilterByGender, facts.IsFemale);

        if (found && line.FilterByKeywordsExcluded.Count > 0 &&
            line.FilterByKeywordsExcluded.Any(k => HasKeyword(facts, k, resolve)))
            found = false;

        if (found && line.FilterByFactionsExcluded.Count > 0 &&
            line.FilterByFactionsExcluded.Any(f => resolve(f) is { } fk && facts.FactionFormKeys.Contains(fk)))
            found = false;

        if (found && line.FilterByEditorIdContainsExcluded.Count > 0)
        {
            string edid = facts.EditorId ?? string.Empty;
            if (line.FilterByEditorIdContainsExcluded.Any(s => edid.Contains(s, StringComparison.OrdinalIgnoreCase)))
                found = false;
        }

        if (found && line.FilterByClassExclude.Count > 0 && facts.ClassFormKey != null &&
            line.FilterByClassExclude.Any(c => resolve(c) is { } fk && fk.Equals(facts.ClassFormKey.Value)))
            found = false;

        if (found && line.FilterByNpcsExcluded.Count > 0 &&
            line.FilterByNpcsExcluded.Any(n => resolve(n) is { } fk && fk.Equals(facts.NpcFormKey)))
            found = false;

        if (found && line.RestrictToGender != null)
            found = MatchesGender(line.RestrictToGender, facts.IsFemale);

        if (found && line.RestrictToRaces.Count > 0 && facts.RaceFormKey != null &&
            !line.RestrictToRaces.Any(r => resolve(r) is { } fk && fk.Equals(facts.RaceFormKey.Value)))
            found = false;

        if (found && line.RestrictToVoiceType.Count > 0 && facts.VoiceTypeFormKey != null &&
            !line.RestrictToVoiceType.Any(v => resolve(v) is { } fk && fk.Equals(facts.VoiceTypeFormKey.Value)))
            found = false;

        return found;
    }

    static bool MatchesGender(string genderValue, bool npcIsFemale)
    {
        if (genderValue.Equals("female", StringComparison.OrdinalIgnoreCase)) return npcIsFemale;
        if (genderValue.Equals("male", StringComparison.OrdinalIgnoreCase)) return !npcIsFemale;
        return true;   // unknown value — SkyPatcher leaves found untouched
    }

    static bool HasKeyword(NpcRuntimeFacts facts, string identifier, Func<string, FormKey?> resolve)
    {
        if (!identifier.Contains('|')) return facts.KeywordEditorIds.Contains(identifier);
        return resolve(identifier) is { } fk && facts.KeywordFormKeys.Contains(fk);
    }
}

/// <summary>Where the effective outfit came from, for the caption/console.</summary>
enum NpcOutfitSource { None, Record, InventoryTemplate, SkyPatcher, Spid }

sealed record NpcOutfitResult(
    FormKey? OutfitKey,
    NpcOutfitSource Source,
    string? SourceDetail,                       // "SkyPatcher: sub\x.ini (line 12)" etc.
    IReadOnlyList<string> Approximations,       // filter aspects only approximated at record level
    IReadOnlyList<string> RuntimeNotes);        // sub-100% SPID chances, suspended SPID, ...

/// <summary>The runtime stack, resolved the way the game layers it:
/// SkyPatcher (last matching line wins) &gt; SPID (first matching entry wins,
/// suspended when SkyPatcher moved the outfit away from the record's initial
/// value) &gt; the record winner (or its Inventory-template terminus — that flag
/// makes the record's own DefaultOutfit a dead field).</summary>
sealed class NpcOutfitStack
{
    readonly List<(SkyPatcherOutfitInstruction Instr, string File)> _sky = new();
    readonly List<SpidOutfitEntry> _spid = new();
    readonly SkyPatcherOutfitParser _skyParser = new();
    readonly SpidOutfitParser _spidParser = new();
    readonly Func<string, bool> _isPluginInstalled;
    readonly Func<string, FormKey?> _resolveSkyId;
    readonly Func<RuntimeFormIdentifier, ResolvedFilterForm?> _resolveSpidForm;
    readonly Func<RuntimeFormIdentifier, FormKey?> _resolveSpidKey;
    readonly ILinkCache _linkCache;

    public int SkyPatcherLineCount => _sky.Count;
    public int SpidEntryCount => _spid.Count;
    public int SkyPatcherFileCount { get; }
    public int SpidFileCount { get; }

    public NpcOutfitStack(
        string dataDir, IReadOnlyList<string> modFoldersHighestFirst, string overwriteDir,
        Func<string, bool> isPluginInstalled,
        Func<string, FormKey?> resolveSkyId,
        Func<RuntimeFormIdentifier, ResolvedFilterForm?> resolveSpidForm,
        Func<RuntimeFormIdentifier, FormKey?> resolveSpidKey,
        ILinkCache linkCache)
    {
        _isPluginInstalled = isPluginInstalled;
        _resolveSkyId = resolveSkyId;
        _resolveSpidForm = resolveSpidForm;
        _resolveSpidKey = resolveSpidKey;
        _linkCache = linkCache;

        var roots = VfsConfigDiscovery.RootsLowestFirst(dataDir, modFoldersHighestFirst, overwriteDir);

        var skyFiles = VfsConfigDiscovery.SkyPatcherNpcConfigs(roots, isPluginInstalled);
        SkyPatcherFileCount = skyFiles.Count;
        foreach (var (rel, abs) in skyFiles)
        {
            string[] lines;
            try { lines = File.ReadAllLines(abs); }
            catch (Exception ex) { Console.Error.WriteLine($"   [npc] could not read '{abs}': {ex.Message}"); continue; }
            foreach (var instr in _skyParser.ParseFile(lines, rel)) _sky.Add((instr, rel));
        }

        var spidFiles = VfsConfigDiscovery.SpidConfigs(roots);
        SpidFileCount = spidFiles.Count;
        foreach (var (name, abs) in spidFiles)
        {
            string[] lines;
            try { lines = File.ReadAllLines(abs); }
            catch (Exception ex) { Console.Error.WriteLine($"   [npc] could not read '{abs}': {ex.Message}"); continue; }
            _spid.AddRange(_spidParser.ParseFile(lines, name));
        }
    }

    /// <summary>The record-level ("plugin level") outfit: the winner's own
    /// DefaultOutfit — unless the Inventory template flag routes the whole
    /// inventory (outfit included) to the template chain, making the record's own
    /// field dead. Walks the chain the way the engine resolves it.</summary>
    public (FormKey? Outfit, FormKey? TemplateSource) RecordLevelOutfit(INpcGetter winner)
    {
        var current = winner;
        FormKey? inheritedFrom = null;
        var visited = new HashSet<FormKey> { winner.FormKey };
        int guard = 0;
        while (guard++ < 10)
        {
            if (!current.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Inventory)) break;
            if (current.Template is not { IsNull: false } templateLink) break;
            if (!visited.Add(templateLink.FormKey)) break;                          // cycle
            if (!_linkCache.TryResolve<INpcGetter>(templateLink.FormKey, out var next)) break;  // dangling / leveled
            inheritedFrom = next.FormKey;
            current = next;
        }
        var outfit = current.DefaultOutfit != null && !current.DefaultOutfit.IsNull
            ? current.DefaultOutfit.FormKey : (FormKey?)null;
        return (outfit, inheritedFrom);
    }

    public NpcOutfitResult Resolve(INpcGetter winner, NpcRuntimeFacts facts)
    {
        var (pluginLevel, templateSource) = RecordLevelOutfit(winner);
        var approximations = new List<string>();
        var notes = new List<string>();

        // SkyPatcher — instructions run in file order as each file is read; a
        // later matching assignment overwrites an earlier one (last wins). An
        // unresolvable outfit value is skipped, leaving the previous assignment.
        FormKey? skyOutfit = null;
        string? skyDetail = null;
        foreach (var (instr, file) in _sky)
        {
            var match = _skyParser.MatchesNpc(instr, facts, _resolveSkyId, _isPluginInstalled);
            if (!match.Applies) continue;
            var outfitKey = _resolveSkyId(instr.OutfitIdentifier);
            if (outfitKey == null || !_linkCache.TryResolve<IOutfitGetter>(outfitKey.Value, out _))
            {
                notes.Add($"SkyPatcher {file}:{instr.LineNumber} matches but its outfit '{instr.OutfitIdentifier}' is not in the load order — skipped");
                continue;
            }
            skyOutfit = outfitKey;
            skyDetail = $"SkyPatcher: {file} (line {instr.LineNumber})";
            foreach (var a in match.Approximations) approximations.Add($"SkyPatcher {file}: {a}");
        }

        // SPID — for_first_form: the FIRST entry (config order) whose filters
        // pass wins; Final and regular entries interleave exactly as NPC2's
        // source-verified simulation has them. Suspended for display when
        // SkyPatcher changed the outfit away from the record's initial value
        // (OutfitManager::IsSuspendedReplacement).
        FormKey? spidOutfit = null;
        string? spidDetail = null;
        bool spidSuspended = skyOutfit != null && !Equals(skyOutfit, pluginLevel);
        var spidApprox = new List<string>();
        foreach (var entry in _spid)
        {
            var match = _spidParser.MatchesNpc(entry, facts, _resolveSpidForm);
            if (!match.Applies)
            {
                // Diagnostic only: a sub-100% entry whose FILTERS pass is a real
                // possibility in game — surface it instead of losing it.
                if (entry.ChancePercent < 100.0 &&
                    _spidParser.MatchesNpc(entry, facts, _resolveSpidForm, applyChanceGate: false).Applies)
                {
                    notes.Add($"SPID {entry.SourceFile}:{entry.LineNumber} would match at {entry.ChancePercent:0.#}% chance — a runtime roll this render cannot depict; not applied");
                }
                continue;
            }
            var outfitKey = _resolveSpidKey(entry.OutfitForm);
            if (outfitKey == null || !_linkCache.TryResolve<IOutfitGetter>(outfitKey.Value, out _))
            {
                if (!entry.FromGenericFormKey)
                    notes.Add($"SPID {entry.SourceFile}:{entry.LineNumber} matches but its outfit '{entry.OutfitForm.Raw}' is not in the load order — skipped");
                continue;   // a generic Form= line that isn't an OTFT was never an outfit entry
            }
            spidOutfit = outfitKey;
            spidDetail = $"SPID: {entry.SourceFile} (line {entry.LineNumber}"
                       + (entry.IsFinal ? ", FinalOutfit" : "")
                       + (entry.FromGenericFormKey ? ", via generic Form=" : "") + ")";
            foreach (var a in match.Approximations) spidApprox.Add($"SPID {entry.SourceFile}: {a}");
            break;   // for_first_form
        }
        if (spidSuspended && spidOutfit != null)
            notes.Add($"{spidDetail} matches but stands down: SkyPatcher moved the outfit away from the record's initial value (SPID suspension)");
        if (!spidSuspended) approximations.AddRange(spidApprox);

        // Final pick: SkyPatcher > SPID > plugin level.
        if (skyOutfit != null)
            return new NpcOutfitResult(skyOutfit, NpcOutfitSource.SkyPatcher, skyDetail, approximations, notes);
        if (spidOutfit != null && !spidSuspended)
            return new NpcOutfitResult(spidOutfit, NpcOutfitSource.Spid, spidDetail, approximations, notes);
        if (pluginLevel != null)
        {
            return templateSource != null
                ? new NpcOutfitResult(pluginLevel, NpcOutfitSource.InventoryTemplate,
                    $"inventory template {templateSource} (the record's own DefaultOutfit is a dead field)", approximations, notes)
                : new NpcOutfitResult(pluginLevel, NpcOutfitSource.Record, "record winner's DefaultOutfit", approximations, notes);
        }
        return new NpcOutfitResult(null, NpcOutfitSource.None, null, approximations, notes);
    }
}
