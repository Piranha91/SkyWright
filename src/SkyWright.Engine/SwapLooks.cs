using Mutagen.Bethesda.Plugins;

using HousecarlCore;

namespace SkyWright.Engine;

// ======================================================================
//  SwapLooks — shapes 2 and 3 over Base Object Swapper's inis, read with the proven reader (BaseObjectSwaps.cs).
//
//  WHAT THE INSTALLED BUILD DOES, cited at its own tag. The pilot order's DLL is BOS VR 3.1.1.1 (PE version; the VR
//  build appends ".1", CMakeLists.txt VR_VERSION), source tag v3.1.1 (fcb195c0, 2024-06-18); the reader was
//  transcribed at master = v3.5.0 (c0b9c093). The behaviours these looks rely on, at v3.1.1:
//    ~ lookup     Util.cpp GetFormID: "0xID~Plugin" -> TESDataHandler::LookupFormID(id, plugin) -> LookupModByName,
//                 which matches the WHOLE filename, case-insensitively and by length (po3 CommonLibSSE 7e55b89;
//                 alandtse CommonLibVR branches vr and ng the same). "X.esl" never finds a loaded "X.esp".
//                 A failed lookup returns 0: "SWAP formID not found" / "BASE formID not found" (SwapData.cpp
//                 GetForms) and the line is dropped. Unchanged to v3.5.0 apart from the logging macro.
//    no record    SwapData.cpp GetSwapBase: TESForm::LookupByID of the swap id; a plugin that is loaded but
//                 does not define the record gives null, and GetSwapData moves on to the next line. Unchanged.
//    sections     Manager.cpp LoadForms: a plain section that is not "Transforms"/"Properties" goes to swapRefs
//                 unless it is exactly "Forms" — the empty section of keys before any header included. swapRefs
//                 is looked up by the REFERENCE (GetSwapData get_swap_base(a_ref, swapRefs)), so a base id there
//                 matches nothing. Unchanged.
//    order        LoadForms appends each base's lines (push_back / emplace_back) in ini order (ordinal names);
//                 GetSwapData walks References -> conditional Forms -> plain Forms, each vector last-first; the
//                 CONFLICTS log names back() the winner and skips a winner below chance 100. Unchanged.
//    VERSION GAP  v3.1.1 has no ordered-set path for "a,b|x" originals (added by v3.5.0's GetFormIDOrderedSet): a
//                 comma list of originals fails there as one EditorID. CSimpleIni's version at v3.1.1 is not pinned
//                 in its manifest, so the byte-order-mark and key-before-header rules are game-logged with 3.4.1.1
//                 and 3.5.0 only.
// ======================================================================

/// <summary>One id token on one BOS line that fails or names nothing — the per-ini reconciliation table.</summary>
public sealed record SwapTokenLine(string Ini, string? Provider, int Line, string Section, string Text, string Field, string Token,
                                   string Cause, string Fate, string Landing, string? Note);

public sealed class SwapPointerResult
{
    public LookResult Look { get; } = new(SwapPointerLook.Shape);
    public List<SwapTokenLine> Table { get; } = new();
    /// <summary>Tokens naming a record in a loaded EMPTY plugin — they belong to the dummy look (shape 5).</summary>
    public List<ConfigFormRef> DummyRefs { get; } = new();
}

/// <summary>Shape 2 — a swap file pointing at a plugin or record the order does not supply, read line by line.</summary>
public static class SwapPointerLook
{
    public const string Shape = "swapPointer";

    // cause classes, each with where it lands
    public const string Renamed = "plugin-renamed";                 // 2a: row
    public const string PluginOnDisk = "plugin-on-disk-not-loaded";  // 2b: aside
    public const string PluginAbsent = "plugin-not-in-order";        // 2b: aside
    public const string RecordAbsent = "record-absent";              // 2c: row
    public const string RuntimeIdNothing = "runtime-id-names-nothing"; // 2c: row
    public const string NamesDummy = "names-empty-dummy";            // shape 5
    public const string HeaderAsKey = "header-read-as-key";          // 2d: row
    public const string BaseInRefTable = "base-id-in-reference-table"; // 2d: row
    public const string EditorIdUnresolved = "editorid-unresolved";  // aside
    public const string NoSeparator = "no-separator";                // aside

    static readonly HashSet<string> RowCauses = new(StringComparer.Ordinal) { Renamed, RecordAbsent, RuntimeIdNothing, HeaderAsKey, BaseInRefTable };

    /// <summary>Read every ini as the game reads it and classify each failing token. <paramref name="loaded"/>: every
    /// loaded plugin filename; <paramref name="onDisk"/>: every plugin filename on disk; <paramref name="dummies"/>:
    /// loaded plugins that are empty dummies; <paramref name="isReference"/>: true for a placed reference, false for
    /// another record, null when unknown; <paramref name="providerOfPlugin"/>: a loaded plugin's mod folder.</summary>
    public static SwapPointerResult Evaluate(IReadOnlyList<BosIniFile> inis, BosFormIndex index, IReadOnlyCollection<string> loaded,
        IReadOnlySet<string> onDisk, IReadOnlySet<string> dummies, Func<FormKey, bool?> isReference,
        IReadOnlyDictionary<string, string?> providerOfPlugin, Func<string, string>? readFile = null)
    {
        readFile ??= BosIniReader.ReadText;
        var res = new SwapPointerResult();
        var stems = loaded.GroupBy(n => Path.GetFileNameWithoutExtension(n), StringComparer.OrdinalIgnoreCase)
                          .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var ini in inis)
        {
            BosIniDocument doc;
            try { doc = BosIniReader.Read(readFile(ini.Path)); }
            catch { res.Look.SetAside("the ini could not be read", ini.Name); continue; }
            var mine = new List<SwapTokenLine>();
            foreach (var sec in doc.Sections)
            {
                foreach (var key in sec.Keys)
                {
                    void Add(string field, string token, string cause, string fate, string? note = null)
                    {
                        string landing = cause == NamesDummy ? "shape 5" : RowCauses.Contains(cause) ? "row" : "set aside";
                        mine.Add(new SwapTokenLine(ini.Name, ini.Provider, key.Line, sec.Name, key.Text, field, token, cause, fate, landing, note));
                    }
                    var text = key.Text;
                    var bare = text.TrimStart('\uFEFF');
                    if (bare.StartsWith('[') && bare.Contains(']'))
                    {
                        int lost = sec.Keys.Count(k => k.Line > key.Line && !doc.Sections.Any(s => s.Line > key.Line && s.Line < k.Line));
                        Add("key", text, HeaderAsKey, "line dropped (BASE formID not found)",
                            $"the header '{bare}' is read as a key in [{sec.Name}] because a second byte-order mark precedes it; {lost} line(s) after it fall into [{sec.Name}]");
                        continue;
                    }
                    var fields = text.Split('|');
                    if (fields.Length < 2) { Add("key", text, NoSeparator, "line dropped (no '|')"); continue; }
                    bool props = sec.Kind == BosSectionKind.Properties;
                    string? versionNote = !props && fields[0].Contains(',')
                        ? "BOS 3.1.1 (the installed VR build) has no comma-list path for originals and fails this line as one EditorID; 3.5.0 reads the list"
                        : null;

                    var baseTokens = fields[0].Contains(',') ? fields[0].Split(',') : new[] { fields[0] };
                    int baseFail = 0;
                    foreach (var t in baseTokens)
                    {
                        var c = Classify(t, index, stems, onDisk, dummies, out var named);
                        if (c is null)
                        {
                            // A base id in a reference table: the line is stored but no reference carries that id.
                            if (!props && sec.Kind == BosSectionKind.References && index.Resolve(t) is { Key: { } k }
                                && isReference(k) == false)
                                Add("original", t, BaseInRefTable, "line read; it matches no reference, so it swaps nothing",
                                    sec.Head == "References" ? null : $"[{sec.Name}] is read as a References table");
                            continue;
                        }
                        baseFail++;
                        if (c == NamesDummy) res.DummyRefs.Add(DummyRef(ini, key, t, named));
                        Add("original", t, c, baseTokens.Length > 1 ? "member dropped" : "line dropped (BASE formID not found)", versionNote ?? Detail(c, named));
                    }
                    if (props || baseFail == baseTokens.Length) continue;
                    var swapTokens = fields[1].Contains(',') ? fields[1].Split(',') : new[] { fields[1] };
                    int swapFail = 0;
                    var pending = new List<(string T, string C, string? Named)>();
                    foreach (var t in swapTokens)
                    {
                        var c = Classify(t, index, stems, onDisk, dummies, out var named);
                        if (c is null) continue;
                        swapFail++;
                        pending.Add((t, c, named));
                    }
                    foreach (var (t, c, named) in pending)
                    {
                        if (c == NamesDummy) res.DummyRefs.Add(DummyRef(ini, key, t, named));
                        // A token BOS accepts at load but finds nothing for at runtime keeps the line; one it cannot
                        // resolve at all is dropped (the whole line when no member resolves).
                        bool acceptedAtLoad = c is RecordAbsent or RuntimeIdNothing or NamesDummy;
                        string fate = acceptedAtLoad ? "line read; picking this target swaps nothing at runtime"
                                    : swapFail == swapTokens.Length ? "line dropped (SWAP formID not found)" : "member dropped";
                        Add("swap", t, c, fate, versionNote ?? Detail(c, named));
                    }
                }
            }
            res.Table.AddRange(mine);
            var rowCauses = mine.Where(m => m.Landing == "row").ToList();
            foreach (var m in mine.Where(m => m.Landing == "set aside")) res.Look.SetAside(Aside(m.Cause), $"{m.Ini}:{m.Line} {m.Token}");
            if (rowCauses.Count == 0) continue;
            res.Look.Rows.Add(RowFor(ini, rowCauses, providerOfPlugin, stems));
        }
        res.Look.Number();
        return res;
    }

    static ConfigFormRef DummyRef(BosIniFile ini, BosKey key, string token, string? plugin)
    {
        var parts = token.Split('~');
        uint id = BosGrammar.TryHexPrefix(parts[0], out var v) ? v : 0;
        return new ConfigFormRef(ini.Name, ini.Provider, key.Line, "BOS", plugin ?? "", id & 0x00FFFFFF, token);
    }

    /// <summary>Why a token fails, or null when it names a record the order defines.</summary>
    static string? Classify(string token, BosFormIndex index, Dictionary<string, string> stems, IReadOnlySet<string> onDisk,
                            IReadOnlySet<string> dummies, out string? named)
    {
        named = null;
        var id = index.Resolve(token);
        var parts = token.Split('~');
        if (parts.Length == 2) named = parts[1];
        if (id.Found && id.Exists) return null;
        if (id.Found)
        {
            if (id.Kind == BosIdKind.PluginLocal) return named is not null && dummies.Contains(named) ? NamesDummy : RecordAbsent;
            if (id.Kind == BosIdKind.RuntimeHex) return RuntimeIdNothing;
            return null;
        }
        if (parts.Length == 2)
        {
            var stem = Path.GetFileNameWithoutExtension(parts[1]);
            if (stems.TryGetValue(stem, out var other) && !other.Equals(parts[1], StringComparison.OrdinalIgnoreCase)) { named = parts[1] + " -> " + other; return Renamed; }
            return onDisk.Contains(parts[1]) ? PluginOnDisk : PluginAbsent;
        }
        return EditorIdUnresolved;
    }

    static string? Detail(string cause, string? named) => cause == Renamed ? $"names {named}" : null;

    static string Aside(string cause) => cause switch
    {
        PluginOnDisk => "a swap line names a plugin that is on disk but not loaded (support for a mod not active)",
        PluginAbsent => "a swap line names a plugin not in this order (support for a mod not installed)",
        EditorIdUnresolved => "a swap line names an EditorID no loaded record carries",
        NoSeparator => "a line with no '|' (a heading or note without ';')",
        _ => cause,
    };

    static LookRow RowFor(BosIniFile ini, List<SwapTokenLine> lines, IReadOnlyDictionary<string, string?> providerOfPlugin, Dictionary<string, string> stems)
    {
        var says = new List<string>();
        var cons = new List<string>();
        var est = new List<string>();
        var mods = new List<string> { ini.Provider };
        var plugins = new List<string>();
        foreach (var g in lines.GroupBy(l => l.Cause))
        {
            var n = g.Count();
            var where = string.Join(", ", g.Select(l => l.Line).Distinct().Take(6)) + (g.Select(l => l.Line).Distinct().Count() > 6 ? ", …" : "");
            switch (g.Key)
            {
                case Renamed:
                    var pairs = g.Select(l => l.Note!.Replace("names ", "")).Distinct().ToList();
                    foreach (var p in pairs)
                    {
                        var loadedName = p.Split(" -> ")[1];
                        plugins.Add(loadedName);
                        if (providerOfPlugin.TryGetValue(loadedName, out var pm) && pm is not null) mods.Add(pm);
                    }
                    says.Add($"{n} id(s) name {string.Join("; ", pairs.Select(p => p.Split(" -> ")[0]))}, which this order does not load; it loads {string.Join(", ", pairs.Select(p => p.Split(" -> ")[1]))} (lines {where})");
                    cons.Add("BOS drops each such line at load ('SWAP formID not found'), so those swaps never apply");
                    est.Add("source: BOS v3.1.1 Util.cpp GetFormID -> TESDataHandler::LookupFormID -> LookupModByName matches the whole filename (CommonLib 7e55b89; CommonLibVR vr, ng)");
                    break;
                case RecordAbsent:
                case RuntimeIdNothing:
                    foreach (var t in g.Select(l => l.Token)) { var parts = t.Split('~'); if (parts.Length == 2) { plugins.Add(parts[1]); if (providerOfPlugin.TryGetValue(parts[1], out var pm2) && pm2 is not null) mods.Add(pm2); } }
                    says.Add($"{n} id(s) name a record their plugin does not define (lines {where})");
                    cons.Add("BOS accepts the line at load and finds nothing at runtime, so picking it swaps nothing and the next line is tried");
                    est.Add("source: BOS v3.1.1 SwapData.cpp GetSwapBase (LookupByID null) and Manager.cpp GetSwapData; not seen in game");
                    break;
                case HeaderAsKey:
                    says.Add($"a section header is read as a key: {g.First().Note}");
                    cons.Add("the header fails as a key ('BASE formID not found'); its lines, if any, are read under the section before it");
                    est.Add("game-log: the same file's header read as a key under [] in a VR game log (BOS 3.4.1.1, another instance, 2026-05-24) and SimpleIni's one-BOM rule on SUP; version gap: SimpleIni at BOS 3.1.1 is not pinned");
                    break;
                case BaseInRefTable:
                    var secs = g.Select(l => l.Section).Distinct().Select(s => $"[{s}]").ToList();
                    says.Add($"{n} line(s) name a base object in {string.Join(", ", secs)}, which BOS reads as a table keyed by REFERENCE (lines {where})");
                    cons.Add("no reference carries those ids, so those lines swap nothing");
                    est.Add("source: BOS v3.1.1 Manager.cpp LoadForms (non-Forms plain sections go to swapRefs) and GetSwapData (swapRefs looked up by the reference); game-log: 'reading []' for keys before any header (SUP, VR 2026-05-24)");
                    break;
            }
        }
        return new LookRow(Shape, string.Join("+", lines.Select(l => l.Cause).Distinct()), LookKind.Finding,
            mods.Where(m => m is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            plugins.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            new[] { $"{ini.Provider}\\{ini.Name}" },
            $"{ini.Name} ({ini.Provider}): " + string.Join("; ", says) + ".",
            string.Join("; ", cons) + ".",
            string.Join(" | ", est.Distinct()));
    }
}

/// <summary>Shape 3 — two mods' swaps on one base (or a reference and its base), with the winner BOS's own order
/// gives. A patch built to win (its plugin masters the loser's, or its line names the loser's forms) is set aside.</summary>
public static class SwapContestLook
{
    public const string Shape = "swapContest";

    /// <summary><paramref name="providerOfIni"/>: ini filename → mod folder. <paramref name="pluginsOf"/>: mod folder → its
    /// loaded plugins. <paramref name="mastersOf"/>: plugin → its masters. <paramref name="baseOfRef"/>: a reference's
    /// winning base, or null. <paramref name="label"/>: a record's EditorID for the row text, or null.</summary>
    public static LookResult Evaluate(BosLayer layer, BosFormIndex index, IReadOnlyDictionary<string, string> providerOfIni,
        Func<string, IReadOnlyCollection<string>> pluginsOf, Func<string, IReadOnlyCollection<string>> mastersOf,
        Func<FormKey, FormKey?> baseOfRef, Func<FormKey, string?> label)
    {
        var r = new LookResult(Shape);
        var groups = new Dictionary<(string Win, string Lose, string Class), List<string>>();
        string Prov(BosSwapEntry e) => providerOfIni.TryGetValue(e.Where.Ini, out var p) ? p : e.Where.Ini;
        string Name(FormKey k) => label(k) is { Length: > 0 } l ? $"{l} ({k})" : k.ToString();

        bool BuiltToWin(string win, string lose, BosSwapEntry winLine)
        {
            var losePlugins = pluginsOf(lose);
            if (losePlugins.Count == 0) return false;
            if (pluginsOf(win).Any(p => mastersOf(p).Any(m => losePlugins.Contains(m, StringComparer.OrdinalIgnoreCase)))) return true;
            if (losePlugins.Any(lp => winLine.Where.Text.Contains("~" + lp, StringComparison.OrdinalIgnoreCase))) return true;
            // A target the winner swaps to that the loser's own plugin defines (an EditorID resolves to it).
            return winLine.Targets.Any(t => t.Key is { } k && losePlugins.Contains(k.ModKey.FileName.String, StringComparer.OrdinalIgnoreCase));
        }

        void Contest(string what, BosSwapEntry win, IEnumerable<BosSwapEntry> losers, string cls)
        {
            var wp = Prov(win);
            foreach (var lp in losers.Select(Prov).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (lp.Equals(wp, StringComparison.OrdinalIgnoreCase)) continue;
                if (BuiltToWin(wp, lp, win)) { r.SetAside("the winner is a patch built for the loser (it masters the loser's plugin or names its forms)", $"{what}: {wp} over {lp}"); continue; }
                var key = (wp, lp, cls);
                if (!groups.TryGetValue(key, out var l)) groups[key] = l = new List<string>();
                l.Add(what);
            }
        }

        static string ClassOf(BosSwapEntry win) =>
            !win.Chance.Certain ? "partial-chance" : win.Targets.Any(t => !t.Id.Exists) ? "winner-names-nothing" : "certain";

        // (a) plain [Forms] lines on one base from two mods — the last loaded wins.
        foreach (var (b, lines) in layer.SwapForms)
        {
            var provs = lines.Select(Prov).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (provs.Count < 2) { if (lines.Count > 1) r.SetAside("one mod's own lines on one base", Name(b)); continue; }
            Contest(Name(b), lines[^1], lines.Take(lines.Count - 1), ClassOf(lines[^1]));
        }
        // (b) a filtered [Forms|…] section from one mod over another mod's lines on the same base.
        foreach (var (b, secs) in layer.SwapFormsConditional)
        {
            var all = secs.Reverse().SelectMany(kv => ((IEnumerable<BosSwapEntry>)kv.Value.Lines).Reverse()).ToList();   // BOS's walk order
            var plain = layer.SwapForms.TryGetValue(b, out var pl) ? pl : new List<BosSwapEntry>();
            if (all.Count == 0) continue;
            Contest(Name(b), all[0], all.Skip(1).Concat(plain), "filtered");
        }
        // (d) two mods' lines on one REFERENCE — the last loaded wins, as for a base.
        foreach (var (rf, lines) in layer.SwapRefs)
        {
            var provs = lines.Select(Prov).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (provs.Count < 2) { if (lines.Count > 1) r.SetAside("one mod's own lines on one reference", Name(rf)); continue; }
            Contest($"reference {Name(rf)}", lines[^1], lines.Take(lines.Count - 1), ClassOf(lines[^1]));
        }
        // (c) one mod's line by REFERENCE over another mod's line on that reference's base.
        foreach (var (rf, lines) in layer.SwapRefs)
        {
            if (baseOfRef(rf) is not { } b) { if (index.Exists(rf)) r.SetAside("a reference line whose base the order could not name (not assessed)", rf.ToString()); continue; }
            var baseLines = (layer.SwapForms.TryGetValue(b, out var pl) ? pl : new List<BosSwapEntry>())
                .Concat(layer.SwapFormsConditional.TryGetValue(b, out var cs) ? cs.Values.SelectMany(v => v.Lines) : Enumerable.Empty<BosSwapEntry>()).ToList();
            if (baseLines.Count == 0) continue;
            Contest($"reference {Name(rf)} on base {Name(b)}", lines[^1], baseLines, "reference-over-base");
        }

        foreach (var ((win, lose, cls), whats) in groups.OrderBy(g => g.Key.Win, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Key.Lose, StringComparer.OrdinalIgnoreCase))
        {
            string list = string.Join("; ", whats.Take(6)) + (whats.Count > 6 ? $"; and {whats.Count - 6} more" : "");
            var (cons, kind) = cls switch
            {
                "certain" => ($"{win}'s line is tried first and always swaps, so {lose}'s line never applies to these bases (except where a filtered section or a line by reference applies first).", LookKind.Finding),
                "partial-chance" => ($"{win}'s line is tried first at a chance under 100; {lose}'s applies only where it passes over the reference.", LookKind.Look),
                "winner-names-nothing" => ($"{win}'s line is tried first but a target names no record, so where it picks that target BOS moves on to {lose}'s line.", LookKind.Look),
                "filtered" => ($"{win}'s filtered section is tried first wherever its filters admit the reference; {lose}'s line applies elsewhere.", LookKind.Look),
                _ => ($"{win}'s line by reference wins for that reference; {lose}'s base line still applies to the base's other references.", LookKind.Finding),
            };
            r.Rows.Add(new LookRow(Shape, cls, kind, new[] { win, lose }, pluginsOf(win).Concat(pluginsOf(lose)).ToList(),
                Array.Empty<string>(),
                $"{win} and {lose} both swap {whats.Count} base(s) or reference(s): {list}.",
                cons,
                "source: BOS v3.1.1 Manager.cpp LoadForms (lines appended per base in ini order) and GetSwapData (References, then conditional, then plain Forms, each last-first), unchanged to v3.5.0; game-log: BOS writes such pairs as CONFLICTS 'winning swap' (VR 3.4.1.1 and SE 3.5.0 logs, other orders); in game: Part D 2026-09-04 (filtered over a later plain line, reference over base)"));
        }
        foreach (var (id, lines) in layer.Properties)
            if (lines.Select(e => providerOfIni.GetValueOrDefault(e.Where.Ini, e.Where.Ini)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                r.SetAside("property overrides ([Transforms]/[Properties]) from two mods on one id (not assessed)", Name(id));
        r.Number();
        return r;
    }
}

/// <summary>The game's CONFLICTS block, parsed, and compared per id with the reader's <see cref="BosLayer.ConflictLogLines"/>.</summary>
public static class BosConflictLog
{
    public sealed record Entry(string Table, string Id, IReadOnlyList<string> Lines)
    {
        /// <summary>Every ini the entry names.</summary>
        public IEnumerable<string> Inis => Lines.Select(l => System.Text.RegularExpressions.Regex.Match(l, @"\(Data\\([^)]*)\)$")).Where(m => m.Success).Select(m => m.Groups[1].Value);
    }

    /// <summary>The block between "CONFLICTS" and "END" of a po3_BaseObjectSwapper.log, timestamps stripped.</summary>
    public static IReadOnlyList<string> FromGameLog(IEnumerable<string> raw)
    {
        var lines = new List<string>(); bool on = false;
        foreach (var r in raw)
        {
            var l = System.Text.RegularExpressions.Regex.Replace(r, @"^\[\d\d:\d\d:\d\d:\d\d\d\] |^\[\d\d-\d\d \d\d:\d\d:\d\d\.\d\d\d\] \[[A-Z]\] ", "");
            if (l.Contains("*CONFLICTS*")) { on = true; continue; }
            if (l.Contains("*END*")) break;
            if (on) lines.Add(l);
        }
        return lines;
    }

    public static IReadOnlyList<Entry> Parse(IEnumerable<string> block)
    {
        var res = new List<Entry>(); string table = ""; string? id = null; List<string>? cur = null;
        foreach (var l in block)
        {
            if (l.StartsWith('[') && l.EndsWith(']')) { table = l[1..^1]; id = null; continue; }
            if (l == "\tNo conflicts found") continue;
            if (l.StartsWith('\t') && !l.StartsWith("\t\t")) { id = l[1..]; cur = new List<string>(); res.Add(new Entry(table, id, cur)); continue; }
            cur?.Add(l);
        }
        return res;
    }

    /// <summary>Per id: same lines, different lines, only in the game, only in the reader. An id only on one side
    /// whose inis are not all present on the other side is counted apart, as a difference between the two orders.</summary>
    public static (int Same, int Differ, int OnlyGame, int OnlyReader, int OrderDiffers, List<string> Examples) Compare(
        IReadOnlyList<Entry> game, IReadOnlyList<Entry> reader, IReadOnlySet<string> gameInis, IReadOnlySet<string> readerInis)
    {
        var g = game.GroupBy(e => (e.Table, e.Id)).ToDictionary(x => x.Key, x => x.First());
        var m = reader.GroupBy(e => (e.Table, e.Id)).ToDictionary(x => x.Key, x => x.First());
        int same = 0, differ = 0, onlyG = 0, onlyM = 0, order = 0; var ex = new List<string>();
        foreach (var (k, e) in g)
        {
            if (m.TryGetValue(k, out var me))
            {
                if (e.Lines.SequenceEqual(me.Lines)) same++;
                else
                {
                    if (!e.Inis.Concat(me.Inis).All(i => gameInis.Contains(i) && readerInis.Contains(i))) order++;
                    else { differ++; if (ex.Count < 20) ex.Add($"differ [{k.Table}] {k.Id}: game {string.Join(" / ", e.Lines)} | reader {string.Join(" / ", me.Lines)}"); }
                }
            }
            else if (e.Inis.All(readerInis.Contains)) { onlyG++; if (ex.Count < 20) ex.Add($"only game [{k.Table}] {k.Id}: {string.Join(" / ", e.Lines)}"); }
            else order++;
        }
        foreach (var (k, e) in m)
        {
            if (g.ContainsKey(k)) continue;
            if (e.Inis.All(gameInis.Contains)) { onlyM++; if (ex.Count < 20) ex.Add($"only reader [{k.Table}] {k.Id}: {string.Join(" / ", e.Lines)}"); }
            else order++;
        }
        return (same, differ, onlyG, onlyM, order, ex);
    }
}
