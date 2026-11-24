using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

using HousecarlCore;

namespace SkyWright.Engine;

// ======================================================================
//  BaseObjectSwaps — the Base Object Swapper reader: what base object, at what transform, the
//  running game gives a placed reference once BOS's *_SWAP.ini files have been applied. One
//  function for the renderer and every geometry detector, the way EnableState is for the runtime
//  enable state.
//
//  BOS (powerofthree's Base Object Swapper, Nexus 60805) is an SKSE plugin that, when a reference
//  initialises, replaces its base object with another and/or overrides its position, rotation,
//  scale and record flags, from ini files. Every rule here is transcribed from the plugin's own
//  source (github.com/powerof3/BaseObjectSwapper, master, read 2026-09-04: Manager.cpp, SwapData.cpp,
//  ConditionalData.cpp, ObjectProperties.cpp, RNG.cpp, Util.cpp, Hooks.cpp) and from the two
//  libraries it reads inis with (CLibUtil distribution.hpp / string.hpp; CSimpleIni with
//  SetUnicode, SetMultiKey and SetAllowKeyOnly). Where the mod page and the source disagree, the
//  source wins and the note says so. The transcription is proven two ways: by construction
//  (proofprobe, a synthetic order and temp inis) and against the game's own log, which the reader
//  reproduces line for line (BosLayer.GameLogLines).
//
//  WHAT THE GAME DOES, as the source says it:
//    discovery   every file directly in Data\ (NOT recursive) whose extension is exactly ".ini" and
//                whose name contains "_SWAP" (both case-sensitive), sorted ordinally. Under MO2 the
//                Data root is the VFS: overwrite, then the enabled mods by priority, then the game's
//                Data, first provider of a filename wins.
//    reading     CSimpleIni: ';' and '#' comment lines; a line starting '[' is a section header up
//                to the first ']' (unterminated: skipped); every other line is a key, split at the
//                first '=' if any, trailing whitespace trimmed; sections with the same name merge
//                (case-insensitively, first spelling kept), duplicate keys in a section collapse to
//                the first occurrence; a leading UTF-8 BOM is skipped, a BOM anywhere else is text.
//    sections    "[Forms]" swaps by base; "[References]" swaps by reference; "[Transforms]" and
//                "[Properties]" property overrides; "[Forms|f1,f2,…]" swaps by base gated by
//                filters; any OTHER "[X|…]" is property overrides gated by filters (References
//                included); any other plain "[X]" is read as References (the else branch) — so
//                "[Reference]" and "[forms]" both are. The section-name compare is case-SENSITIVE.
//    lines       origBase|swapBase|properties|chance  /  orig1,orig2|swap  /  orig|swap1,swap2  /
//                origRef|swapBase|…  /  (property lines) id|properties|chance.
//                An id is "0xID~Plugin.esp" (the plugin must be loaded — the record need not
//                exist), a bare "0x…" runtime FormID, or an EditorID (case-insensitive, every form
//                type: powerofthree's Tweaks caches them all). Failures are logged by BOS with
//                fixed messages, which this reader reproduces.
//    filters     each filter is an id (or "-id" to exclude); a resolved form matches by type —
//                Location: the ref's location or an ancestor of it; Region: in the cell's region
//                list; Keyword: on the location or on the ref's base; Cell: the ref's cell;
//                Worldspace: the ref's or its parent; other types never match. An unresolved id
//                is a string matched against the cell's EditorID, the location's keyword
//                EditorIDs and the base's keyword EditorIDs. A section applies when NO exclusion
//                matches and (if any inclusions) SOME inclusion matches.
//    order       swap: References (by ref) → filtered Forms → plain Forms. Filtered sections are
//                visited in REVERSE ORDINAL order of "IniName|conditions" (a std::map keyed by
//                that string — not file order); within a table, lines LAST FIRST; the first line
//                whose chance passes and whose target resolves wins. Then properties: the winning
//                swap line's own, else References transforms (by ref) → filtered → plain (by the
//                ORIGINAL base), same reverse walk, first line whose chance passes and that sets
//                something.
//    randomness  chance(p) with suffix S (seeded per ref: fixed across sessions, the default),
//                L (per location + base), R (per session when no seed; fixed when a seed is given);
//                "orig|a,b,c" picks one member per ref, seeded the same way; "a,b|x,y,z" assigns
//                each original one member at LOAD (per game start); a swap to a leveled item
//                resolves an entry at runtime (no encounter zone) or does not swap (one present).
//                This reader never rolls: it returns the SET of outcomes with probabilities, and
//                the First (BOS's own order; a set's lowest runtime FormID) as the documented
//                deterministic convenience, flagged Random.
//    transforms  posR/posA, rotR/rotA (degrees, converted to radians), scale(a/b) multiplicative /
//                scaleA absolute in the engine's percent-scale (100 = 1.0), flags/flagsC set/clear
//                record flags; each component may be a "min/max" range drawn at runtime. Applied
//                relative (+=) or absolute (=). The swapped line's own properties win over any
//                [Transforms] line.
//    scope       the hook is on the TESObjectREFR, Hazard and ArrowProjectile vtables — a placed
//                ACTOR (Character) is never swapped, and a swap needs a parent cell.
// ======================================================================

/// <summary>One *_SWAP.ini the game will read: its filename (the winning provider's spelling), the winning
/// path, who provides it, and who else ships the same name and is shadowed by the VFS.</summary>
public sealed record BosIniFile(string Name, string Path, string Provider, IReadOnlyList<string> ShadowedProviders);

public static class BosDiscovery
{
    public const string Marker = "_SWAP";

    /// <summary>The inis BOS finds over a VFS of loose roots in precedence order (overwrite → mods by priority →
    /// Data): top-level files only, extension exactly ".ini" and a name containing "_SWAP" (both case-sensitive, as
    /// the plugin compares them), the first provider of a filename winning (the VFS is case-insensitive), sorted
    /// ordinally by name (std::ranges::sort on "Data\name"). A root that will not enumerate contributes nothing.</summary>
    public static IReadOnlyList<BosIniFile> Discover(IReadOnlyList<(string Name, string Dir)> looseRoots)
    {
        var winners = new Dictionary<string, (string Name, string Path, string Provider, List<string> Shadowed)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (provider, dir) in looseRoots)
        {
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly); }
            catch { continue; }
            foreach (var f in files)
            {
                var name = System.IO.Path.GetFileName(f);
                if (!Matches(name)) continue;
                if (winners.TryGetValue(name, out var w)) w.Shadowed.Add(provider);
                else winners[name] = (name, f, provider, new List<string>());
            }
        }
        return winners.Values
            .OrderBy(w => w.Name, StringComparer.Ordinal)
            .Select(w => new BosIniFile(w.Name, w.Path, w.Provider, w.Shadowed))
            .ToList();
    }

    /// <summary>The filename test BOS applies (CLibUtil get_configs): extension == ".ini" and the name contains
    /// "_SWAP", both ordinal. "X_swap.ini" and "X_SWAP.INI" are NOT read by the game.</summary>
    public static bool Matches(string fileName)
        => System.IO.Path.GetExtension(fileName) == ".ini" && fileName.Contains(Marker, StringComparison.Ordinal);
}

// ---------------------------------------------------------------------------------------------
//  The ini as CSimpleIni hands it to BOS
// ---------------------------------------------------------------------------------------------

public enum BosSectionKind
{
    /// <summary>[Forms] — swaps keyed by base.</summary>
    Forms,
    /// <summary>[References] and every other plain section name — swaps keyed by reference (the else branch).</summary>
    References,
    /// <summary>[Transforms] / [Properties] — property overrides.</summary>
    Properties,
}

/// <summary>One key line as SimpleIni yields it: the text (trailing whitespace trimmed, everything before a first
/// '=' when there is one) and the 1-based line it first appeared on.</summary>
public readonly record struct BosKey(string Text, int Line);

/// <summary>One merged section. <paramref name="Name"/> is the whole header text between the brackets as first
/// spelled; <paramref name="Head"/> the part before a '|' (or the whole name); <paramref name="Condition"/> the text
/// after the first '|' (null when none — the section is unconditional). Conditional sections whose head is not
/// exactly "Forms" are property overrides; unconditional "Transforms"/"Properties" are property overrides and every
/// other unconditional name is a References table.</summary>
public sealed record BosSection(string Name, string Head, string? Condition, int Line, IReadOnlyList<BosKey> Keys)
{
    public bool Conditional => Condition is not null;
    public BosSectionKind Kind => Conditional
        ? (Head == "Forms" ? BosSectionKind.Forms : BosSectionKind.Properties)
        : Head is "Transforms" or "Properties" ? BosSectionKind.Properties
        : Head == "Forms" ? BosSectionKind.Forms : BosSectionKind.References;
    /// <summary>The filters as BOS splits them: on ',' keeping empty tokens, nothing trimmed.</summary>
    public string[] Conditions => Condition is null ? Array.Empty<string>() : Condition.Split(',');
}

/// <summary>A whole ini as read: the merged sections in first-appearance order, and what the reading rules did
/// with lines a human would not expect them to (counted, never silent).</summary>
public sealed record BosIniDocument(IReadOnlyList<BosSection> Sections, IReadOnlyList<string> Anomalies);

public static class BosIniReader
{
    /// <summary>Read a file's bytes the way CSimpleIni::LoadData does: ONE leading UTF-8 byte-order mark is skipped;
    /// a second one, or one anywhere else, is text — a line beginning with it is a key, not a header (an ini in the
    /// wild begins with two, and the game read its whole [Forms|…] header as a key under the empty section; the
    /// reader must too). Decoded as UTF-8 without any further BOM handling.</summary>
    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int off = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return new UTF8Encoding(false, false).GetString(bytes, off, bytes.Length - off);
    }

    /// <summary>Read ini text the way CSimpleIni (SetUnicode, SetMultiKey, SetAllowKeyOnly) does for BOS. The text
    /// is taken as given — see <see cref="ReadText"/> for the one-BOM rule.</summary>
    public static BosIniDocument Read(string text)
    {
        var sections = new List<(string Name, int Line, List<BosKey> Keys, HashSet<string> Seen)>();
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var anomalies = new List<string>();
        int cur = -1;
        int lineNo = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = raw.TrimEnd('\r');
            var s = line.TrimStart(' ', '\t', '\r', '\n');
            if (s.Length == 0) continue;
            if (s[0] == ';' || s[0] == '#') continue;
            if (s[0] == '[')
            {
                int close = s.IndexOf(']');
                if (close < 0) { anomalies.Add($"line {lineNo}: '[' without ']' — SimpleIni skips the line"); continue; }
                var name = s[1..close].Trim(' ', '\t');
                if (close + 1 < s.Length && s[(close + 1)..].Trim().Length > 0)
                    anomalies.Add($"line {lineNo}: text after ']' on a section header is ignored");
                if (byName.TryGetValue(name, out var idx))
                {
                    if (sections[idx].Name != name) anomalies.Add($"line {lineNo}: section [{name}] merges into [{sections[idx].Name}] (case-insensitive)");
                    else anomalies.Add($"line {lineNo}: a second [{name}] header — its keys merge into the first section");
                    cur = idx;
                }
                else { byName[name] = cur = sections.Count; sections.Add((name, lineNo, new List<BosKey>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase))); }
                continue;
            }
            if (cur < 0)
            {
                // SimpleIni files a key before any header under the section named "" — and BOS reads that section
                // like any other ("reading []"), as a References table (the else branch).
                anomalies.Add($"line {lineNo}: a key before any section header — read as the section [] (a References table)");
                byName[""] = cur = sections.Count; sections.Add(("", lineNo, new List<BosKey>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
            }
            int eq = s.IndexOf('=');
            string key = (eq >= 0 ? s[..eq] : s).TrimEnd(' ', '\t');
            if (eq >= 0) anomalies.Add($"line {lineNo}: contains '=' — SimpleIni keeps only the text before it as the key");
            if (key.Length == 0) continue;
            if (s.Contains('﻿')) anomalies.Add($"line {lineNo}: a byte-order mark inside the file is part of the key text");
            var sec = sections[cur];
            if (!sec.Seen.Add(key)) { anomalies.Add($"line {lineNo}: duplicate key in [{sec.Name}] — collapses onto its first occurrence"); continue; }
            sec.Keys.Add(new BosKey(key, lineNo));
        }
        var result = new List<BosSection>(sections.Count);
        foreach (var (name, line, keys, _) in sections)
        {
            int bar = name.IndexOf('|');
            result.Add(bar < 0
                ? new BosSection(name, name, null, line, keys)
                : new BosSection(name, name[..bar], name[(bar + 1)..], line, keys));
        }
        return new BosIniDocument(result, anomalies);
    }
}

// ---------------------------------------------------------------------------------------------
//  The token grammar
// ---------------------------------------------------------------------------------------------

public enum BosChanceType
{
    /// <summary>S (the default): seeded by the reference — the same answer every session.</summary>
    RefHash,
    /// <summary>L: seeded by the location (or cell) and the original base — every such object in a place swaps alike.</summary>
    LocationHash,
    /// <summary>R: unseeded when no seed is given (re-rolled each session); fixed when one is.</summary>
    Random,
}

/// <summary>The fourth field. Value in percent (100 = always); a chance below 100 is rolled per reference.</summary>
public readonly record struct BosChance(float Value, BosChanceType Type, ulong Seed)
{
    public static readonly BosChance Always = new(100f, BosChanceType.RefHash, 0);
    public bool Certain => Value >= 100f;
    /// <summary>The pass probability the reader reports for this line.</summary>
    public double Pass => Value >= 100f ? 1.0 : Value <= 0f ? 0.0 : Value / 100.0;
    /// <summary>True when the roll is not fixed per reference across sessions (R without a seed).</summary>
    public bool PerSession => Type == BosChanceType.Random && Seed == 0;

    /// <summary>Chance::Chance — nothing unless the text is a valid entry containing "chance"; type by the presence
    /// of 'R' then 'L' anywhere in the text; value and seed from inside the first parentheses.</summary>
    public static BosChance Parse(string? text, List<string>? notes = null)
    {
        if (string.IsNullOrEmpty(text) || text.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return Always;
        if (!text.Contains("chance", StringComparison.Ordinal))
        {
            notes?.Add($"chance field '{text}' has no 'chance(' — BOS reads it as 100 %");
            return Always;
        }
        var type = text.Contains('R') ? BosChanceType.Random : text.Contains('L') ? BosChanceType.LocationHash : BosChanceType.RefHash;
        float value = 100f; ulong seed = 0;
        var m = BosGrammar.Generic.Match(text);
        if (m.Success)
        {
            var opts = m.Groups[1].Value.Split(',');
            value = BosGrammar.StoF(opts[0], notes, "chance");
            if (opts.Length > 1) seed = BosGrammar.StoUL(opts[1], notes, "chance seed");
        }
        else notes?.Add($"chance field '{text}' has no parentheses — BOS keeps 100 %");
        return new BosChance(value, type, seed);
    }
}

/// <summary>A "min/max" range as BOS parses one; a single value has Min == Max.</summary>
public readonly record struct BosRange(float Min, float Max)
{
    public bool Exact => Min == Max;
    public override string ToString() => Exact ? Min.ToString("0.###", CultureInfo.InvariantCulture)
        : $"{Min.ToString("0.###", CultureInfo.InvariantCulture)}/{Max.ToString("0.###", CultureInfo.InvariantCulture)}";
}

/// <summary>pos/rot: three ranges, relative (+=) when the token contains 'R', absolute otherwise; rotation ranges
/// are already in radians. <paramref name="Malformed"/>: the transform regex did not match, and BOS applies zeros.</summary>
public sealed record BosPoint3Range(bool Relative, BosRange X, BosRange Y, BosRange Z, bool Malformed)
{
    public bool Exact => X.Exact && Y.Exact && Z.Exact;
    public Vec3 Apply(Vec3 v, bool max = false)
    {
        float x = max ? X.Max : X.Min, y = max ? Y.Max : Y.Min, z = max ? Z.Max : Z.Min;
        return Relative ? new Vec3(v.X + x, v.Y + y, v.Z + z) : new Vec3(x, y, z);
    }
}

/// <summary>scale(a/b): multiplies the reference's scale; scaleA(a/b): sets it, in the engine's percent domain
/// (refScale is a uint16, 100 = 1.0). The drawn value is clamped to [0, 1000] and the result truncated to a uint16.</summary>
public sealed record BosScaleRange(bool Absolute, BosRange Value, bool Malformed)
{
    public float Apply(float scale, bool max = false)
    {
        float v = Math.Clamp(max ? Value.Max : Value.Min, 0f, 1000f);
        ushort refScale = (ushort)Math.Clamp(scale * 100f, 0f, 65535f);
        ushort result = Absolute ? (ushort)Math.Clamp(v, 0f, 65535f) : (ushort)Math.Clamp(refScale * v, 0f, 65535f);
        return result / 100f;
    }
}

/// <summary>The third field parsed: ObjectProperties. <paramref name="UnknownTokens"/> are comma-separated calls BOS
/// silently ignores (no "pos"/"rot"/"scale"/"flags" in them); counted here per rule 6.</summary>
public sealed record BosProperties(BosPoint3Range? Position, BosPoint3Range? Rotation, BosScaleRange? Scale,
                                   uint FlagsSet, uint FlagsUnset, IReadOnlyList<string> UnknownTokens, IReadOnlyList<string> Notes)
{
    public static readonly BosProperties None = new(null, null, null, 0, 0, Array.Empty<string>(), Array.Empty<string>());
    /// <summary>ObjectProperties::IsValid — something is set.</summary>
    public bool IsValid => Position is not null || Rotation is not null || Scale is not null || FlagsSet != 0 || FlagsUnset != 0;
    public bool HasTransform => Position is not null || Rotation is not null || Scale is not null;
    /// <summary>Any component is a range the game draws at runtime.</summary>
    public bool Random => (Position is { Exact: false }) || (Rotation is { Exact: false }) || (Scale is { Value.Exact: false });
    public const uint InitiallyDisabledFlag = 0x00000800;
    public const uint DontHavokSettleFlag = 0x20000000;

    /// <summary>ObjectProperties::ObjectProperties — nothing for an empty or "NONE" text; else split on commas
    /// outside parentheses, each token dispatched on the substrings BOS looks for.</summary>
    public static BosProperties Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return None;
        BosPoint3Range? pos = null, rot = null; BosScaleRange? scale = null; uint set = 0, unset = 0;
        var unknown = new List<string>(); var notes = new List<string>();
        foreach (var tok in BosGrammar.SplitProperties(text))
        {
            if (tok.Length == 0) continue;
            if (tok.Contains("pos", StringComparison.Ordinal)) pos = ParsePoint3(tok, false, notes);
            else if (tok.Contains("rot", StringComparison.Ordinal)) rot = ParsePoint3(tok, true, notes);
            else if (tok.Contains("scale", StringComparison.Ordinal)) scale = ParseScale(tok, notes);
            else if (tok.Contains("flags", StringComparison.Ordinal))
            {
                // BOS: `assign_record_flags(propStr, propStr.contains("C"))` — an uppercase 'C' ANYWHERE in the
                // token, hex digits included, makes it a CLEAR. Mirrored, and named when the digits did it.
                bool clear = tok.Contains('C');
                if (clear && !tok.Contains("flagsC", StringComparison.Ordinal)) notes.Add($"'{tok}' reads as a CLEAR because a hex digit is 'C' (BOS tests the whole token)");
                var m = BosGrammar.Generic.Match(tok);
                if (m.Success)
                    foreach (var f in m.Groups[1].Value.Split(','))
                    {
                        uint v = BosGrammar.StoUL32Hex(f, notes, "flags");
                        if (clear) unset |= v; else set |= v;
                    }
                else notes.Add($"'{tok}' has no parentheses — no flags read");
            }
            else unknown.Add(tok);
        }
        return new BosProperties(pos, rot, scale, set, unset, unknown, notes);
    }

    static BosPoint3Range ParsePoint3(string tok, bool toRadians, List<string> notes)
    {
        bool relative = tok.Contains('R');
        var m = BosGrammar.Transform.Match(tok);
        if (!m.Success)
        {
            notes.Add($"'{tok}' does not match BOS's three-value pattern — it applies (0,0,0)");
            return new BosPoint3Range(relative, default, default, default, true);
        }
        var x = BosGrammar.ParseRange(m.Groups[1].Value, notes, tok);
        var y = BosGrammar.ParseRange(m.Groups[2].Value, notes, tok);
        var z = BosGrammar.ParseRange(m.Groups[3].Value, notes, tok);
        if (toRadians)
        {
            x = new BosRange(Deg(x.Min), Deg(x.Max)); y = new BosRange(Deg(y.Min), Deg(y.Max)); z = new BosRange(Deg(z.Min), Deg(z.Max));
        }
        return new BosPoint3Range(relative, x, y, z, false);
        static float Deg(float d) => d * (MathF.PI / 180f);
    }

    static BosScaleRange ParseScale(string tok, List<string> notes)
    {
        bool absolute = tok.Contains('A');
        var m = BosGrammar.Generic.Match(tok);
        if (!m.Success) { notes.Add($"'{tok}' has no parentheses — BOS keeps a zero range (scale 0)"); return new BosScaleRange(absolute, default, true); }
        return new BosScaleRange(absolute, BosGrammar.ParseRange(m.Groups[1].Value, notes, tok), false);
    }
}

/// <summary>The regexes and number readers BOS uses, transcribed (Util.h; CLibUtil string::to_num over std::stof /
/// std::stoul, which read a leading number and stop at the first character they cannot use).</summary>
public static class BosGrammar
{
    /// <summary>regex::generic — the text inside the first parentheses.</summary>
    public static readonly Regex Generic = new(@"\((.*?)\)", RegexOptions.Compiled);
    /// <summary>regex::transform — three comma-separated values inside parentheses (lazy: a fourth lands in the third).</summary>
    public static readonly Regex Transform = new(@"\((.*?),(.*?),(.*?)\)", RegexOptions.Compiled);
    /// <summary>regex::string — split on commas that are not inside parentheses.</summary>
    static readonly Regex PropertySplit = new(@",\s*(?![^()]*\))", RegexOptions.Compiled);

    public static string[] SplitProperties(string text) => PropertySplit.Split(text);

    public static BosRange ParseRange(string text, List<string>? notes, string where)
    {
        var parts = text.Split('/');
        float min = StoF(parts[0], notes, where);
        float max = parts.Length > 1 ? StoF(parts[1], notes, where) : min;
        return new BosRange(min, max);
    }

    /// <summary>std::stof: skip leading whitespace, read the longest float prefix; nothing readable is an exception
    /// in the game (an uncaught one at ini load). Reported as a note; 0 here.</summary>
    public static float StoF(string s, List<string>? notes, string where)
    {
        var m = FloatPrefix.Match(s);
        if (m.Success && float.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
        notes?.Add($"{where}: '{s}' is not a number — std::stof throws here in the game");
        return 0f;
    }
    static readonly Regex FloatPrefix = new(@"^\s*[+-]?(\d+\.?\d*([eE][+-]?\d+)?|\.\d+([eE][+-]?\d+)?)", RegexOptions.Compiled);

    /// <summary>std::stoull base 10 (the seed).</summary>
    public static ulong StoUL(string s, List<string>? notes, string where)
    {
        var m = Regex.Match(s, @"^\s*\+?(\d+)");
        if (m.Success && ulong.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return v;
        notes?.Add($"{where}: '{s}' is not an integer — std::stoull throws here in the game");
        return 0;
    }

    /// <summary>std::stoul base 16: optional whitespace, optional "0x", the longest hex prefix.</summary>
    public static uint StoUL32Hex(string s, List<string>? notes, string where)
    {
        if (TryHexPrefix(s, out var v)) return v;
        notes?.Add($"{where}: '{s}' is not a hex number — std::stoul throws here in the game");
        return 0;
    }

    public static bool TryHexPrefix(string s, out uint value)
    {
        var m = Regex.Match(s, @"^\s*(?:0[xX])?([0-9A-Fa-f]{1,8})");
        value = 0;
        return m.Success && uint.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>CLibUtil string::is_only_hex with the prefix required: "0x"/"0X" then one or more hex digits, nothing else.</summary>
    public static bool IsOnlyHex(string s)
    {
        if (s.Length < 3 || s[0] != '0' || (s[1] != 'x' && s[1] != 'X')) return false;
        for (int i = 2; i < s.Length; i++) if (!Uri.IsHexDigit(s[i])) return false;
        return true;
    }
}

// ---------------------------------------------------------------------------------------------
//  Form identity — resolving BOS's three id spellings against a load order
// ---------------------------------------------------------------------------------------------

public enum BosIdKind { PluginLocal, RuntimeHex, EditorId, Unresolved }

/// <summary>One id token resolved. <paramref name="Found"/> is BOS's own verdict (util::GetFormID != 0): true for a
/// "~" id whose plugin is loaded (whether or not the record exists), for any bare hex, and for an EditorID that
/// resolves. <paramref name="Key"/> is the record it names when the order can name one; <paramref name="Exists"/>
/// whether that record is defined (and not deleted) in the order — a Found id that does not Exist is one the game
/// accepts at load and finds nothing for at runtime. <paramref name="Loaded"/>: the game holds the form when it reads
/// the inis — false for a TEMPORARY placed reference, which exists only while its cell is loaded (a bare-hex or
/// EditorID lookup of one fails at that moment; a "~" id needs no lookup).</summary>
public sealed record BosId(string Token, BosIdKind Kind, bool Found, FormKey? Key, bool Exists, uint RuntimeId, string? Note, bool Loaded = true)
{
    public static BosId NotFound(string token, string? note = null) => new(token, BosIdKind.Unresolved, false, null, false, 0, note, false);
}

/// <summary>A cell's facts the filters read: its EditorID, location (XLCN), regions (XCLR), worldspace (Null for an
/// interior), and encounter zone.</summary>
public sealed record BosCellFacts(FormKey Cell, string? EditorId, FormKey? Location, IReadOnlyList<FormKey> Regions, FormKey Worldspace, FormKey? EncounterZone, bool Interior);

/// <summary>The load-order facts the reader resolves against. Filled during the caller's own plugin walk
/// (<see cref="Note"/> per major record, <see cref="NoteCell"/> per cell extracted, <see cref="SetLoadOrder"/> once),
/// so no second pass over the order is needed. Everything is winner-takes-all in load order.</summary>
public sealed class BosFormIndex
{
    /// <summary>EditorID → record, case-insensitive; every spelling ever given to a record stays (the game's cache
    /// is insert-only). A second record claiming a name the first already holds keeps the first (the game's map
    /// does not overwrite) — noted.</summary>
    readonly Dictionary<string, FormKey> _byEditorId = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Further records carrying an EditorID the first holder already has (vanilla statics and the temporary
    /// references placed with them sometimes share a name — one such name is both a STAT and a REFR in Skyrim.esm).</summary>
    readonly Dictionary<string, List<FormKey>> _editorIdOthers = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<FormKey> _exists = new();
    /// <summary>Placed references in a cell's TEMPORARY list under the winner: not in memory when BOS reads its inis.</summary>
    readonly HashSet<FormKey> _temporary = new();
    /// <summary>Records whose winning override carries the Deleted flag — still loaded, noted for the caller.</summary>
    readonly HashSet<FormKey> _deleted = new();
    readonly Dictionary<FormKey, string> _typeOf = new();          // the filterable and leveled types only
    readonly Dictionary<FormKey, string> _keywordEdid = new();
    readonly Dictionary<FormKey, (FormKey? Parent, FormKey[] Keywords, string? Edid)> _locations = new();
    readonly Dictionary<FormKey, (FormKey? Parent, FormKey? Location)> _worldspaces = new();
    readonly Dictionary<FormKey, FormKey[]> _baseKeywords = new();
    readonly Dictionary<FormKey, FormKey[]> _leveledEntries = new();
    readonly Dictionary<FormKey, (string? Edid, FormKey? Location, FormKey[] Regions, FormKey? Ez, bool Interior)> _cells = new();
    readonly Dictionary<FormKey, FormKey> _cellWorldspace = new();
    readonly Dictionary<string, (ModKey Key, int Index, bool Light, int LightIndex)> _plugins = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, ModKey> _fullByIndex = new();
    readonly Dictionary<int, ModKey> _lightByIndex = new();
    public int EditorIdCollisions { get; private set; }
    public long RecordsSeen { get; private set; }

    /// <summary>The runtime FormID table: full plugins take sequential indices, light ones the shared 0xFE index
    /// with a 12-bit light index, both in load order — the game's own assignment. Call once with every loaded plugin.</summary>
    public void SetLoadOrder(IEnumerable<(ModKey Key, bool Light)> order)
    {
        _plugins.Clear(); _fullByIndex.Clear(); _lightByIndex.Clear();
        int full = 0, light = 0;
        foreach (var (key, isLight) in order)
        {
            if (key.IsNull) { full++; continue; }              // an unreadable plugin still holds its slot
            if (isLight) { _plugins[key.FileName.String] = (key, 0xFE, true, light); _lightByIndex[light] = key; light++; }
            else { _plugins[key.FileName.String] = (key, full, false, -1); _fullByIndex[full] = key; full++; }
        }
    }

    public bool PluginLoaded(string name) => _plugins.ContainsKey(name);

    /// <summary>One major record seen during the caller's walk. Cheap: a set insert, and a few dictionaries for the
    /// handful of types the filters and the leveled rule read.</summary>
    public void Note(IMajorRecordGetter rec)
    {
        RecordsSeen++;
        var key = rec.FormKey;
        // A record an override marks Deleted STAYS a loaded form the game can look up (a DLC master deletes a vanilla
        // static and the game still resolved that EditorID for an ini naming it, 2026-09-04); it is noted, not
        // removed. What the game cannot find at ini-load time is a TEMPORARY reference.
        if (rec.IsDeleted) _deleted.Add(key); else _deleted.Remove(key);
        _exists.Add(key);
        if (rec.EditorID is { Length: > 0 } ed)
        {
            if (_byEditorId.TryGetValue(ed, out var held))
            {
                if (held != key)
                {
                    if (!_editorIdOthers.TryGetValue(ed, out var others)) _editorIdOthers[ed] = others = new List<FormKey>(1);
                    if (!others.Contains(key)) { others.Add(key); EditorIdCollisions++; }
                }
            }
            else _byEditorId[ed] = key;
        }
        switch (rec)
        {
            case ILocationGetter loc:
                _typeOf[key] = "Location";
                _locations[key] = (loc.ParentLocation.FormKeyNullable is { IsNull: false } lp ? lp : null,
                                   loc.Keywords is { } lk ? lk.Select(k => k.FormKey).Where(k => !k.IsNull).ToArray() : Array.Empty<FormKey>(),
                                   loc.EditorID);
                break;
            case IRegionGetter: _typeOf[key] = "Region"; break;
            case IKeywordGetter kw: _typeOf[key] = "Keyword"; if (kw.EditorID is { } ke) _keywordEdid[key] = ke; break;
            case IWorldspaceGetter ws:
                _typeOf[key] = "Worldspace";
                _worldspaces[key] = (ws.Parent?.Worldspace.FormKeyNullable is { IsNull: false } wp ? wp : null,
                                     ws.Location.FormKeyNullable is { IsNull: false } wl ? wl : null);
                break;
            case ICellGetter cell:
                _typeOf[key] = "Cell";
                _cells[key] = (cell.EditorID,
                               cell.Location.FormKeyNullable is { IsNull: false } cl ? cl : null,
                               cell.Regions is { } rg ? rg.Select(r => r.FormKey).Where(k => !k.IsNull).ToArray() : Array.Empty<FormKey>(),
                               cell.EncounterZone.FormKeyNullable is { IsNull: false } ez ? ez : null,
                               cell.Flags.HasFlag(Mutagen.Bethesda.Skyrim.Cell.Flag.IsInteriorCell));
                break;
            case ILeveledItemGetter lvli:
                _typeOf[key] = "LeveledItem";
                _leveledEntries[key] = lvli.Entries is { } ents
                    ? ents.Select(e => e.Data?.Reference.FormKeyNullable).Where(k => k is { IsNull: false }).Select(k => k!.Value).ToArray()
                    : Array.Empty<FormKey>();
                break;
            case IPlaceableObjectGetter and IKeywordedGetter<IKeywordGetter> kwd:
                if (kwd.Keywords is { Count: > 0 } ks) _baseKeywords[key] = ks.Select(k => k.FormKey).Where(k => !k.IsNull).ToArray();
                else _baseKeywords.Remove(key);
                break;
        }
    }

    /// <summary>The worldspace a cell sits in, from the caller's placement extraction (the record does not say).</summary>
    public void NoteCell(FormKey cell, FormKey worldspace) => _cellWorldspace[cell] = worldspace;

    /// <summary>A placed reference's list membership under this plugin (persistent or temporary), from the caller's
    /// placement extraction; the winner's statement stands.</summary>
    public void NotePlaced(FormKey reference, bool persistent) { if (persistent) _temporary.Remove(reference); else _temporary.Add(reference); }

    public bool Exists(FormKey key) => _exists.Contains(key);
    public bool IsDeleted(FormKey key) => _deleted.Contains(key);
    /// <summary>Whether the game holds this form when BOS reads the inis: defined, and not a temporary reference.</summary>
    public bool LoadedAtStart(FormKey key) => _exists.Contains(key) && !_temporary.Contains(key);
    public string? TypeOf(FormKey key) => _typeOf.TryGetValue(key, out var t) ? t : null;
    public FormKey? LocationParent(FormKey loc) => _locations.TryGetValue(loc, out var l) ? l.Parent : null;
    public IReadOnlyList<FormKey> LocationKeywords(FormKey loc) => _locations.TryGetValue(loc, out var l) ? l.Keywords : Array.Empty<FormKey>();
    public string? KeywordEditorId(FormKey kw) => _keywordEdid.TryGetValue(kw, out var e) ? e : null;
    public IReadOnlyList<FormKey> BaseKeywords(FormKey b) => _baseKeywords.TryGetValue(b, out var k) ? k : Array.Empty<FormKey>();
    public FormKey? WorldspaceParent(FormKey ws) => _worldspaces.TryGetValue(ws, out var w) ? w.Parent : null;
    public IReadOnlyList<FormKey> LeveledEntries(FormKey lvli) => _leveledEntries.TryGetValue(lvli, out var e) ? e : Array.Empty<FormKey>();
    public bool TryEditorId(string edid, out FormKey key) => _byEditorId.TryGetValue(edid, out key);

    /// <summary>The cell's facts for the filters; null when the order never showed this cell. The location falls
    /// back to the worldspace's when an exterior cell states none (ASSUMED to be the engine's
    /// TESObjectCELL::GetLocation behaviour; the note says so).</summary>
    public BosCellFacts? Cell(FormKey cell)
    {
        if (!_cells.TryGetValue(cell, out var c)) return null;
        var ws = _cellWorldspace.TryGetValue(cell, out var w) ? w : FormKey.Null;
        var loc = c.Location;
        if (loc is null && !ws.IsNull && _worldspaces.TryGetValue(ws, out var wf)) loc = wf.Location;
        return new BosCellFacts(cell, c.Edid, loc, c.Regions, c.Interior ? FormKey.Null : ws, c.Ez, c.Interior);
    }

    /// <summary>The record a runtime FormID names under this load order: the plugin at its index (0xFE: the light plugin
    /// at its 12-bit light index) and the local id; null when no plugin loads at that index.</summary>
    public FormKey? KeyOfRuntime(uint id)
    {
        if ((id >> 24) == 0xFE)
            return _lightByIndex.TryGetValue((int)((id >> 12) & 0xFFF), out var lk) ? new FormKey(lk, id & 0xFFF) : null;
        return _fullByIndex.TryGetValue((int)(id >> 24), out var fk) ? new FormKey(fk, id & 0xFFFFFF) : null;
    }

    /// <summary>The runtime FormID the game gives a record under this load order (0 when its plugin is not loaded).</summary>
    public uint RuntimeIdOf(FormKey key)
    {
        if (!_plugins.TryGetValue(key.ModKey.FileName.String, out var p)) return 0;
        return p.Light ? 0xFE000000u | ((uint)p.LightIndex << 12) | (key.ID & 0xFFF) : ((uint)p.Index << 24) | key.ID;
    }

    /// <summary>util::GetFormWithID. "~": the plugin must be loaded (found ⇔ loaded; the record need not exist);
    /// bare "0x…": a runtime FormID mapped through the index table (found always, as BOS returns the number);
    /// otherwise an EditorID lookup. A "~" spelling with more or fewer than two parts falls through, as it does in BOS.</summary>
    public BosId Resolve(string token)
    {
        if (token.Contains('~'))
        {
            var parts = token.Split('~');
            if (parts.Length == 2)
            {
                if (!BosGrammar.TryHexPrefix(parts[0], out var raw))
                    return BosId.NotFound(token, "the id before '~' is not a hex number (std::stoul throws in the game)");
                if (!_plugins.TryGetValue(parts[1], out var p))
                    return BosId.NotFound(token, $"plugin '{parts[1]}' is not in the active load order");
                // TESDataHandler::LookupFormID adds, without masking: index << 24 (+ light index << 12) + the raw id.
                // A raw id that carries index bits therefore lands in ANOTHER plugin's id space — the game looks up
                // whatever record has that runtime FormID, which may exist. Mirrored through the runtime table.
                uint runtime = unchecked((p.Light ? 0xFE000000u | ((uint)p.LightIndex << 12) : (uint)p.Index << 24) + raw);
                bool spilled = p.Light ? raw > 0xFFF : raw > 0xFFFFFF;
                FormKey? key = spilled ? KeyOfRuntime(runtime) : new FormKey(p.Key, raw);
                bool exists = key is { } k && _exists.Contains(k);
                string? note = spilled
                    ? $"local id 0x{raw:X} carries index bits — the game adds them onto the plugin's index and looks up runtime FormID {runtime:X8}" + (key is null ? " (no plugin loads there)" : exists ? $" = {key}, a record in another plugin" : $" = {key}, which does not exist")
                    : exists ? null : "the plugin is loaded but defines no such record — BOS accepts the id and finds nothing at runtime";
                return new BosId(token, BosIdKind.PluginLocal, true, key, exists, runtime, note);
            }
        }
        if (BosGrammar.IsOnlyHex(token))
        {
            BosGrammar.TryHexPrefix(token, out var id);
            var key = KeyOfRuntime(id);
            bool exists = key is { } k && _exists.Contains(k);
            bool loaded = key is { } k2 && LoadedAtStart(k2);
            return new BosId(token, BosIdKind.RuntimeHex, true, key, exists, id,
                             key is null ? "no plugin loads at that runtime index"
                             : !exists ? "no record at that runtime FormID — BOS keeps the number and finds nothing at runtime"
                             : !loaded ? "a temporary reference — not in memory when BOS reads the inis, so the game's lookup fails there" : null,
                             loaded);
        }
        if (_byEditorId.TryGetValue(token, out var ek))
        {
            // TESForm::LookupByEditorID reads the EditorID cache, which holds only forms in memory at that moment: a
            // temporary reference's EditorID is not there until its cell loads. When several records carry the name,
            // the first one the game holds at start is the answer (a STAT over the temporary REFR placed with it);
            // two loaded holders are an ambiguity the game settles by its own insert order — the first seen is taken.
            if (!LoadedAtStart(ek))
            {
                if (_editorIdOthers.TryGetValue(token, out var others))
                    foreach (var o in others) if (LoadedAtStart(o)) return new BosId(token, BosIdKind.EditorId, true, o, true, RuntimeIdOf(o), $"'{token}' is also the EditorID of temporary reference {ek}; the loaded record is taken");
                return BosId.NotFound(token, $"'{token}' names {ek}, a temporary reference not in memory when BOS reads the inis");
            }
            string? note = _editorIdOthers.TryGetValue(token, out var more) && more.Any(LoadedAtStart)
                ? $"'{token}' is carried by {more.Count(LoadedAtStart) + 1} loaded records — the first in load order is taken; the game's cache decides by its own insert order"
                : _deleted.Contains(ek) ? $"{ek} is marked Deleted by its winning override — still a loaded form the game finds" : null;
            return new BosId(token, BosIdKind.EditorId, true, ek, true, RuntimeIdOf(ek), note);
        }
        return BosId.NotFound(token);
    }
}

// ---------------------------------------------------------------------------------------------
//  The loaded layer — BOS's tables, as it fills them
// ---------------------------------------------------------------------------------------------

/// <summary>Where a line came from.</summary>
public sealed record BosProvenance(string Ini, int Line, string Section, string? Condition, string Text)
{
    public override string ToString() => $"{Ini}:{Line} [{Section}] {Text}";
}

/// <summary>One swap target: the id as resolved, and (for a leveled-item target) the entries it can resolve to.</summary>
public sealed record BosTarget(BosId Id)
{
    public FormKey? Key => Id.Key;
}

/// <summary>One swap line as BOS stores it (SwapFormData): the set of targets (one, or several for a random pick),
/// the properties riding on the line, its chance, and where it came from. <paramref name="AssignedAtLoad"/>: the
/// "a,b|x,y,z" shape — BOS hands each original one member of the set when the inis load, re-drawn each game start,
/// and the chance field is dropped.</summary>
public sealed record BosSwapEntry(IReadOnlyList<BosTarget> Targets, BosProperties Properties, BosChance Chance, BosProvenance Where, bool AssignedAtLoad)
{
    public bool RandomPick => Targets.Count > 1;
}

/// <summary>One property-override line as BOS stores it (ObjectData).</summary>
public sealed record BosPropertyEntry(BosProperties Properties, BosChance Chance, BosProvenance Where);

public enum BosFilterKind { Form, MissingForm, Text }

/// <summary>One filter token as ConditionFilters stores it: a resolved form (matched by type), a FormID the order
/// could not turn into a record (never matches at runtime), or a bare string (matched against EditorIDs).</summary>
public sealed record BosFilter(string Token, bool Negate, BosFilterKind Kind, FormKey? Key, string? Type);

/// <summary>A conditional section's filters, keyed the way BOS keys its map: "IniName|conditions".</summary>
public sealed record BosFilters(string ConditionId, IReadOnlyList<BosFilter> Not, IReadOnlyList<BosFilter> Match, IReadOnlyList<string> Notes);

/// <summary>The counts the game logs per section, in the shape it logs them — the parse-level comparison's unit.</summary>
public sealed record BosSectionReport(string Ini, string Section, int Conditions, int Keys, IReadOnlyList<string> Lines);

public sealed class BosLayer
{
    public IReadOnlyList<BosIniFile> Inis { get; }
    /// <summary>ref → its [References] swap lines, file order (BOS walks them last first).</summary>
    public Dictionary<FormKey, List<BosSwapEntry>> SwapRefs { get; } = new();
    /// <summary>base → its plain [Forms] swap lines.</summary>
    public Dictionary<FormKey, List<BosSwapEntry>> SwapForms { get; } = new();
    /// <summary>base → conditionId → (filters, lines): the std::map BOS walks in REVERSE key order.</summary>
    public Dictionary<FormKey, SortedDictionary<string, (BosFilters Filters, List<BosSwapEntry> Lines)>> SwapFormsConditional { get; } = new();
    /// <summary>id (a ref's or a base's — BOS does not distinguish) → plain property-override lines.</summary>
    public Dictionary<FormKey, List<BosPropertyEntry>> Properties { get; } = new();
    /// <summary>id → conditionId → (filters, lines); looked up by BASE only at runtime.</summary>
    public Dictionary<FormKey, SortedDictionary<string, (BosFilters Filters, List<BosPropertyEntry> Lines)>> PropertiesConditional { get; } = new();

    /// <summary>The game-log-shaped account of every ini and section, in read order.</summary>
    public List<string> LogLines { get; } = new();
    public List<BosSectionReport> Sections { get; } = new();
    /// <summary>Rule 6: everything the reader saw and could not read the way a human meant it.</summary>
    public List<string> Anomalies { get; } = new();
    public int LinesRead, LinesFailed, UnknownPropertyTokens, EditorIdsUnresolved, TargetsMissing, FiltersAsText, FiltersMissingForm;

    public BosLayer(IReadOnlyList<BosIniFile> inis) => Inis = inis;

    /// <summary>The lines the game writes for the INI phase, timestamps stripped — diff these against
    /// po3_BaseObjectSwapper.log's block between "*INI*" and "*RESULT*".</summary>
    public IEnumerable<string> GameLogLines()
    {
        yield return $"{Inis.Count} matching inis found...";
        foreach (var l in LogLines) yield return l;
    }

    /// <summary>The CONFLICTS block the game writes after RESULT, timestamps stripped (Manager.cpp LoadForms'
    /// log_conflicts, the same at v3.1.1 and v3.5.0): for the plain [Forms], [References] and property tables in that
    /// order, every id with more than one line whose LAST line (the one the reverse walk tries first) is at chance 100;
    /// the winner's base and swap fields, its ini, the count of the others, and each other line's text after its first
    /// '|', last-loaded first. A table with lines but no such id prints "No conflicts found"; an empty table prints
    /// nothing. Ids appear in first-insertion order (BOS's ankerl map iterates that way).</summary>
    public IEnumerable<string> ConflictLogLines()
    {
        var tables = new (string Type, List<(string Text, string Ini, float Chance)>[] Ids)[]
        {
            ("Forms", SwapForms.Values.Select(l => l.Select(e => (e.Where.Text, e.Where.Ini, e.Chance.Value)).ToList()).ToArray()),
            ("References", SwapRefs.Values.Select(l => l.Select(e => (e.Where.Text, e.Where.Ini, e.Chance.Value)).ToList()).ToArray()),
            ("Properties", Properties.Values.Select(l => l.Select(e => (e.Where.Text, e.Where.Ini, e.Chance.Value)).ToList()).ToArray()),
        };
        foreach (var (type, ids) in tables)
        {
            if (ids.Length == 0) continue;
            yield return $"[{type}]";
            bool any = false;
            foreach (var lines in ids)
            {
                if (lines.Count < 2) continue;
                var win = lines[^1];
                if (win.Chance != 100f) continue;
                any = true;
                var f = win.Text.Split('|');
                yield return $"\t{f[0]}";
                yield return $"\t\twinning swap : {(f.Length > 1 ? f[1] : "")} (Data\\{win.Ini})";
                yield return $"\t\t{lines.Count - 1} conflicts";
                for (int i = lines.Count - 2; i >= 0; i--)
                {
                    var t = lines[i].Text;
                    yield return $"\t\t\t{t[(t.IndexOf('|') + 1)..]} (Data\\{lines[i].Ini})";
                }
            }
            if (!any) yield return "\tNo conflicts found";
        }
    }
}

public static class BaseObjectSwaps
{
    /// <summary>Read every discovered ini into the tables, as Manager::LoadForms does, against <paramref name="index"/>.</summary>
    public static BosLayer Load(IReadOnlyList<BosIniFile> inis, BosFormIndex index, Func<string, string>? readFile = null)
    {
        var layer = new BosLayer(inis);
        readFile ??= BosIniReader.ReadText;
        foreach (var ini in inis)
        {
            layer.LogLines.Add($"INI : Data\\{ini.Name}");
            if (ini.ShadowedProviders.Count > 0)
                layer.Anomalies.Add($"{ini.Name}: shipped by {ini.ShadowedProviders.Count + 1} mods — only '{ini.Provider}' is read; {string.Join(", ", ini.ShadowedProviders)} shadowed");
            BosIniDocument doc;
            try { doc = BosIniReader.Read(readFile(ini.Path)); }
            catch (Exception ex) { layer.LogLines.Add("\tcouldn't read INI"); layer.Anomalies.Add($"{ini.Name}: could not be read ({ex.Message})"); continue; }
            foreach (var a in doc.Anomalies) layer.Anomalies.Add($"{ini.Name}: {a}");
            foreach (var sec in doc.Sections) LoadSection(layer, ini.Name, sec, index);
        }
        return layer;
    }

    static void LoadSection(BosLayer layer, string ini, BosSection sec, BosFormIndex index)
    {
        var lines = new List<string>();
        BosFilters? filters = null;
        if (sec.Conditional)
        {
            var conds = sec.Conditions;
            lines.Add($"\treading [{sec.Head}] : {conds.Length} conditions");
            filters = BuildFilters(ini + "|" + sec.Condition, conds, index, lines, layer);
            if (sec.Keys.Count > 0)
                lines.Add(sec.Kind == BosSectionKind.Forms ? $"\t\t\t{sec.Keys.Count} form swaps found" : $"\t\t\t{sec.Keys.Count} ref property overrides found");
        }
        else
        {
            lines.Add($"\treading [{sec.Name}]");
            if (sec.Keys.Count > 0)
                lines.Add(sec.Kind == BosSectionKind.Properties ? $"\t\t\t{sec.Keys.Count} ref property overrides found" : $"\t\t\t{sec.Keys.Count} swaps found");
            if (sec.Kind == BosSectionKind.References && sec.Head != "References")
                layer.Anomalies.Add($"{ini}: section [{sec.Name}] is not a name BOS knows — it is read as [References]");
        }
        foreach (var key in sec.Keys)
        {
            layer.LinesRead++;
            var where = new BosProvenance(ini, key.Line, sec.Name, sec.Condition, key.Text);
            if (sec.Kind == BosSectionKind.Properties) LoadPropertyLine(layer, where, filters, index, lines);
            else LoadSwapLine(layer, where, sec.Kind, filters, index, lines);
        }
        layer.LogLines.AddRange(lines);
        layer.Sections.Add(new BosSectionReport(ini, sec.Name, sec.Conditional ? sec.Conditions.Length : 0, sec.Keys.Count, lines));
    }

    static BosFilters BuildFilters(string conditionId, string[] conds, BosFormIndex index, List<string> log, BosLayer layer)
    {
        var not = new List<BosFilter>(); var match = new List<BosFilter>(); var notes = new List<string>();
        foreach (var raw in conds)
        {
            var c = raw; bool negate = false;
            if (c.Length > 0 && c[0] == '-') { c = c[1..]; negate = true; }
            var id = index.Resolve(c);
            if (id.Kind == BosIdKind.RuntimeHex && !id.Loaded)
                log.Add($"\t\tFilter [{c}] INFO - unable to find form, treating filter as cell formID");
            BosFilter f;
            if (id.Found)
            {
                if (id.Key is { } k && id.Exists)
                {
                    var type = index.TypeOf(k);
                    f = new BosFilter(c, negate, BosFilterKind.Form, k, type);
                    if (type is null) notes.Add($"filter '{c}' resolves to a record of a type BOS never matches (not Location/Region/Keyword/Cell/Worldspace) — it never applies");
                }
                else
                {
                    f = new BosFilter(c, negate, BosFilterKind.MissingForm, id.Key, null);
                    layer.FiltersMissingForm++;
                    notes.Add($"filter '{c}': {id.Note ?? "no record"} — a FormID filter with no form never matches");
                }
            }
            else
            {
                log.Add($"\t\tFilter [{c}] INFO - unable to find form, treating filter as FF keyword or cell editorID");
                f = new BosFilter(c, negate, BosFilterKind.Text, null, null);
                layer.FiltersAsText++;
            }
            (negate ? not : match).Add(f);
        }
        return new BosFilters(conditionId, not, match, notes);
    }

    static void LoadSwapLine(BosLayer layer, BosProvenance where, BosSectionKind kind, BosFilters? filters, BosFormIndex index, List<string> log)
    {
        var text = where.Text;
        var fields = text.Split('|');
        var notes = new List<string>();
        string props = fields.Length > 2 ? fields[2] : "";
        string chanceText = fields.Length > 3 ? fields[3] : "";
        if (fields.Length > 4) notes.Add($"{fields.Length - 4} field(s) after the chance are ignored");

        // SwapFormData::GetForms — the single-base path first, then the ordered-set path. The base is resolved
        // BEFORE the second field is touched, so a line with no '|' whose first token names nothing fails as
        // "BASE formID not found" (a heading or a divider without ';' — seen in the wild); one whose token DOES
        // resolve reads past the end of the line in the game.
        var baseId = index.Resolve(fields[0]);
        if (baseId.Kind == BosIdKind.RuntimeHex && !baseId.Loaded) log.Add($"\t\tFilter [{fields[0]}] INFO - unable to find form, treating filter as cell formID");
        if (fields.Length < 2 && (baseId.Found || fields[0].Contains(',')))
        {
            var setCheck = baseId.Found ? new List<BosId> { baseId } : ResolveOrderedSet(fields[0], index, log, "formID not found", layer);
            if (setCheck.Count > 0)
            {
                layer.LinesFailed++;
                layer.Anomalies.Add($"{where.Ini}:{where.Line}: '{text}' has no '|' and its first token resolves — BOS reads past the end of the line here (undefined behaviour)");
                return;
            }
            log.Add($"\t\t\t\tfail : [{text}] (BASE formID not found)"); layer.LinesFailed++; return;
        }
        if (fields.Length < 2)
        {
            log.Add($"\t\t\t\tfail : [{text}] (BASE formID not found)"); layer.LinesFailed++;
            layer.Anomalies.Add($"{where.Ini}:{where.Line}: '{text}' has no '|' — BOS fails it as a base it cannot find (a heading without ';'?)");
            return;
        }
        if (baseId.Found)
        {
            var swaps = ResolveSet(fields[1], index, log, "SWAP formID not found", layer);
            if (swaps.Count == 0) { log.Add($"\t\t\t\tfail : [{text}] (SWAP formID not found)"); layer.LinesFailed++; return; }
            var properties = BosProperties.Parse(props);
            bool same = swaps.Count == 1 && swaps[0].Id.RuntimeId == baseId.RuntimeId;
            if (same && !IsValidEntry(props)) { log.Add($"\t\t\t\tfail : [{text}] (BASE formID == SWAP formID)"); layer.LinesFailed++; return; }
            var chance = BosChance.Parse(chanceText, notes);
            var entry = new BosSwapEntry(swaps, properties, chance, where, false);
            Account(layer, where, baseId, swaps, properties, notes);
            Store(layer, kind, filters, baseId, entry);
            return;
        }
        var bases = ResolveOrderedSet(fields[0], index, log, "formID not found", layer);
        if (bases.Count == 0) { log.Add($"\t\t\t\tfail : [{text}] (BASE formID not found)"); layer.LinesFailed++; if (!fields[0].Contains(',')) layer.EditorIdsUnresolved++; return; }
        var swapSet = ResolveOrderedSet(fields[1], index, log, "formID not found", layer);
        if (swapSet.Count == 0) { log.Add($"\t\t\t\tfail : [{text}] (SWAP formID set not found)"); layer.LinesFailed++; return; }
        var swapTargets = swapSet.Select(i => new BosTarget(i)).ToList();
        var props2 = BosProperties.Parse(props);
        if (swapSet.Count == 1)
        {
            var chance = BosChance.Parse(chanceText, notes);
            foreach (var b in bases)
            {
                var entry = new BosSwapEntry(swapTargets, props2, chance, where, false);
                Account(layer, where, b, swapTargets, props2, notes);
                Store(layer, kind, filters, b, entry);
            }
        }
        else if (swapSet.Count >= bases.Count)
        {
            // Each original is handed one member at load, chosen by a seeded draw; the chance field is dropped.
            foreach (var b in bases)
            {
                var entry = new BosSwapEntry(swapTargets, props2, BosChance.Always, where, true);
                Account(layer, where, b, swapTargets, props2, notes);
                Store(layer, kind, filters, b, entry);
            }
            layer.Anomalies.Add($"{where.Ini}:{where.Line}: {bases.Count} originals over {swapSet.Count} swaps — assigned one each at load, re-drawn every game start; the chance field is dropped");
        }
        else { log.Add($"\t\t\t\tfail : [{text}] (SWAP formIDSet.size() must be 1 OR equal/greater than BASE formIDSet.size())"); layer.LinesFailed++; }
    }

    static void Account(BosLayer layer, BosProvenance where, BosId baseId, IReadOnlyList<BosTarget> swaps, BosProperties props, List<string> notes)
    {
        if (!baseId.Exists) layer.Anomalies.Add($"{where.Ini}:{where.Line}: original '{baseId.Token}' — {baseId.Note ?? "names no record"}; the line can match nothing");
        foreach (var t in swaps) if (!t.Id.Exists) { layer.TargetsMissing++; layer.Anomalies.Add($"{where.Ini}:{where.Line}: swap '{t.Id.Token}' — {t.Id.Note ?? "names no record"}; picking it swaps nothing"); }
        layer.UnknownPropertyTokens += props.UnknownTokens.Count;
        foreach (var u in props.UnknownTokens) layer.Anomalies.Add($"{where.Ini}:{where.Line}: property '{u}' is not pos/rot/scale/flags — BOS ignores it");
        foreach (var n in props.Notes) layer.Anomalies.Add($"{where.Ini}:{where.Line}: {n}");
        foreach (var n in notes) layer.Anomalies.Add($"{where.Ini}:{where.Line}: {n}");
    }

    static void Store(BosLayer layer, BosSectionKind kind, BosFilters? filters, BosId baseId, BosSwapEntry entry)
    {
        if (baseId.Key is not { } key) { layer.Anomalies.Add($"{entry.Where.Ini}:{entry.Where.Line}: original '{baseId.Token}' maps to no record the order can name — stored under a runtime id nothing will carry"); return; }
        if (filters is not null)
        {
            if (!layer.SwapFormsConditional.TryGetValue(key, out var m)) layer.SwapFormsConditional[key] = m = new SortedDictionary<string, (BosFilters, List<BosSwapEntry>)>(StringComparer.Ordinal);
            if (!m.TryGetValue(filters.ConditionId, out var slot)) m[filters.ConditionId] = slot = (filters, new List<BosSwapEntry>());
            slot.Lines.Add(entry);
        }
        else
        {
            var table = kind == BosSectionKind.Forms ? layer.SwapForms : layer.SwapRefs;
            if (!table.TryGetValue(key, out var list)) table[key] = list = new List<BosSwapEntry>();
            list.Add(entry);
        }
    }

    static void LoadPropertyLine(BosLayer layer, BosProvenance where, BosFilters? filters, BosFormIndex index, List<string> log)
    {
        var text = where.Text;
        var fields = text.Split('|');
        var baseId = index.Resolve(fields[0]);
        if (baseId.Kind == BosIdKind.RuntimeHex && !baseId.Loaded) log.Add($"\t\tFilter [{fields[0]}] INFO - unable to find form, treating filter as cell formID");
        if (!baseId.Found) { log.Add($"\t\t\t\tfail : [{text}] (BASE formID not found)"); layer.LinesFailed++; if (!fields[0].Contains(',')) layer.EditorIdsUnresolved++; return; }
        if (fields.Length < 2) { layer.LinesFailed++; layer.Anomalies.Add($"{where.Ini}:{where.Line}: '{text}' has no '|' and its first token resolves — BOS reads past the end of the line here (undefined behaviour)"); return; }
        var notes = new List<string>();
        var props = BosProperties.Parse(fields[1]);
        var chance = BosChance.Parse(fields.Length > 2 ? fields[2] : "", notes);
        if (fields.Length > 3) notes.Add($"{fields.Length - 3} field(s) after the chance are ignored");
        layer.UnknownPropertyTokens += props.UnknownTokens.Count;
        foreach (var u in props.UnknownTokens) layer.Anomalies.Add($"{where.Ini}:{where.Line}: property '{u}' is not pos/rot/scale/flags — BOS ignores it");
        foreach (var n in props.Notes.Concat(notes)) layer.Anomalies.Add($"{where.Ini}:{where.Line}: {n}");
        if (!props.IsValid) layer.Anomalies.Add($"{where.Ini}:{where.Line}: sets no property — the line never applies");
        if (baseId.Key is not { } key) { layer.Anomalies.Add($"{where.Ini}:{where.Line}: '{baseId.Token}' maps to no record the order can name"); return; }
        var entry = new BosPropertyEntry(props, chance, where);
        if (filters is not null)
        {
            if (!layer.PropertiesConditional.TryGetValue(key, out var m)) layer.PropertiesConditional[key] = m = new SortedDictionary<string, (BosFilters, List<BosPropertyEntry>)>(StringComparer.Ordinal);
            if (!m.TryGetValue(filters.ConditionId, out var slot)) m[filters.ConditionId] = slot = (filters, new List<BosPropertyEntry>());
            slot.Lines.Add(entry);
        }
        else
        {
            if (!layer.Properties.TryGetValue(key, out var list)) layer.Properties[key] = list = new List<BosPropertyEntry>();
            list.Add(entry);
        }
    }

    /// <summary>util::GetSwapFormID: a comma list resolves each member (failures logged), then sorts by runtime id
    /// and drops duplicates; a single token resolves alone. Empty when nothing resolved.</summary>
    static List<BosTarget> ResolveSet(string text, BosFormIndex index, List<string> log, string failWhat, BosLayer layer)
    {
        var result = new List<BosTarget>();
        if (text.Contains(','))
        {
            var seen = new HashSet<uint>();
            var list = new List<BosId>();
            foreach (var t in text.Split(','))
            {
                var id = index.Resolve(t);
                if (id.Kind == BosIdKind.RuntimeHex && !id.Loaded) log.Add($"\t\tFilter [{t}] INFO - unable to find form, treating filter as cell formID");
                if (id.Found) list.Add(id);
                else { log.Add($"\t\t\tfailed to process {t} ({failWhat})"); layer.EditorIdsUnresolved++; }
            }
            // std::ranges::sort then std::ranges::unique on the runtime FormIDs.
            foreach (var id in list.OrderBy(i => i.RuntimeId))
                if (seen.Add(id.RuntimeId)) result.Add(new BosTarget(id));
            return result;
        }
        var single = index.Resolve(text);
        if (single.Kind == BosIdKind.RuntimeHex && !single.Loaded) log.Add($"\t\tFilter [{text}] INFO - unable to find form, treating filter as cell formID");
        if (single.Found) result.Add(new BosTarget(single));
        return result;
    }

    /// <summary>util::GetFormIDOrderedSet: a comma list resolves each member into an ordered set (failures logged);
    /// a single token resolves alone.</summary>
    static List<BosId> ResolveOrderedSet(string text, BosFormIndex index, List<string> log, string failWhat, BosLayer layer)
    {
        var result = new List<BosId>();
        if (text.Contains(','))
        {
            // std::set<FormID>: ordered by runtime FormID, duplicates dropped.
            var seen = new HashSet<uint>();
            foreach (var t in text.Split(','))
            {
                var id = index.Resolve(t);
                if (id.Kind == BosIdKind.RuntimeHex && !id.Loaded) log.Add($"\t\tFilter [{t}] INFO - unable to find form, treating filter as cell formID");
                if (!id.Found) { log.Add($"\t\t\tfailed to process {t} ({failWhat})"); layer.EditorIdsUnresolved++; continue; }
                if (seen.Add(id.RuntimeId)) result.Add(id);
            }
            return result.OrderBy(i => i.RuntimeId).ToList();
        }
        var single = index.Resolve(text);
        if (single.Kind == BosIdKind.RuntimeHex && !single.Loaded) log.Add($"\t\tFilter [{text}] INFO - unable to find form, treating filter as cell formID");
        if (single.Found) result.Add(single);
        return result;
    }

    /// <summary>CLibUtil distribution::is_valid_entry — non-empty and not "NONE" (case-insensitive).</summary>
    public static bool IsValidEntry(string? s) => !string.IsNullOrEmpty(s) && !s.Equals("NONE", StringComparison.OrdinalIgnoreCase);

    // -----------------------------------------------------------------------------------------
    //  Resolution — what one reference gets
    // -----------------------------------------------------------------------------------------

    /// <summary>One possible base for a reference with the probability the reader assigns it. <paramref name="Base"/>
    /// null means "the record base stays" is not an alternative here (every alternative names a base). Entries whose
    /// target is a leveled item carry the item's entries as their own alternatives, flattened.</summary>
    public sealed record BosAlternative(FormKey Base, double Probability, BosSwapEntry? From, bool ViaLeveledItem);

    /// <summary>What BOS does to one reference. <paramref name="Alternatives"/> lists every base the reference can
    /// end up with, the record base included when some path keeps it, with probabilities summing to 1 (the leveled
    /// split is uniform and LABELLED an approximation). <paramref name="EffectiveBase"/> is the deterministic
    /// convenience: the first alternative in BOS's own evaluation order (a set's lowest runtime id) — equal to the
    /// record base when nothing certain swaps it; <paramref name="Random"/> says the game may pick differently.
    /// <paramref name="Properties"/> are the transform/flag overrides that apply (the winning swap line's own, else
    /// the first passing [Transforms] line), with <paramref name="PropertiesRandom"/> when a range or a chance under
    /// 100 is involved.</summary>
    public sealed record BosResolution(FormKey RecordBase, IReadOnlyList<BosAlternative> Alternatives, FormKey EffectiveBase,
                                       bool Swapped, bool Random, bool Leveled, bool PerSession,
                                       BosProperties? Properties, BosProvenance? PropertiesFrom, string? PropertiesSource, bool PropertiesRandom,
                                       IReadOnlyList<string> Notes)
    {
        public static BosResolution Unchanged(FormKey recordBase, string? note = null) =>
            new(recordBase, new[] { new BosAlternative(recordBase, 1.0, null, false) }, recordBase, false, false, false, false,
                null, null, null, false, note is null ? Array.Empty<string>() : new[] { note });

        /// <summary>The swap line the effective base came from, if any.</summary>
        public BosSwapEntry? SwapFrom => Alternatives.FirstOrDefault(a => a.Base == EffectiveBase && a.From is not null)?.From;
        public bool HasTransform => Properties is { HasTransform: true };
        public bool SetsFlags => Properties is { } p && (p.FlagsSet != 0 || p.FlagsUnset != 0);

        /// <summary>The reference's transform after the overrides — ranges at their minimum (<paramref name="max"/>:
        /// maximum), so a caller wanting a bound calls both. Flags: the initially-disabled bit after set/clear.</summary>
        public (Vec3 Position, Vec3 Rotation, float Scale, bool InitiallyDisabled) Apply(Vec3 position, Vec3 rotation, float scale, bool initiallyDisabled, bool max = false)
        {
            if (Properties is not { } p) return (position, rotation, scale, initiallyDisabled);
            var pos = p.Position?.Apply(position, max) ?? position;
            var rot = p.Rotation?.Apply(rotation, max) ?? rotation;
            float sc = p.Scale?.Apply(scale, max) ?? scale;
            bool dis = initiallyDisabled;
            if ((p.FlagsUnset & BosProperties.InitiallyDisabledFlag) != 0) dis = false;
            if ((p.FlagsSet & BosProperties.InitiallyDisabledFlag) != 0) dis = true;
            return (pos, rot, sc, dis);
        }
    }

    /// <summary>Whether a placed arm is one BOS's hooks reach — the TESObjectREFR, Hazard and ArrowProjectile vtables
    /// (Hooks.cpp): PlacedObject, PlacedHazard, PlacedArrow. Never a placed actor (Character has its own vtable), and
    /// not the other projectile arms.</summary>
    public static bool Swappable(string recordType) => recordType is "PlacedObject" or "PlacedHazard" or "PlacedArrow";

    /// <summary>Manager::GetSwapData for one reference, over the layer. <paramref name="cell"/> is the cell the
    /// winning placement puts it in; a null cell context (the order never showed the cell) leaves conditional
    /// sections unjudged, noted.</summary>
    public static BosResolution Resolve(BosLayer layer, BosFormIndex index, FormKey refKey, string recordType, FormKey recordBase, FormKey cell, FormKey? refEncounterZone = null)
    {
        if (!Swappable(recordType)) return BosResolution.Unchanged(recordBase, $"{recordType} is not on a vtable BOS hooks — actors are never swapped");
        var facts = index.Cell(cell);
        var notes = new List<string>();
        if (facts is null) notes.Add("the reference's cell is not in the index — filtered sections could not be judged and were treated as not applying");

        // ---- the swap: References -> conditional Forms -> Forms, each last-first, first pass wins ------------
        var candidates = new List<BosSwapEntry>();
        if (layer.SwapRefs.TryGetValue(refKey, out var byRef)) for (int i = byRef.Count - 1; i >= 0; i--) candidates.Add(byRef[i]);
        if (layer.SwapFormsConditional.TryGetValue(recordBase, out var cond))
            foreach (var (_, slot) in cond.Reverse())
                if (facts is not null && FiltersPass(slot.Filters, facts, recordBase, index))
                    for (int i = slot.Lines.Count - 1; i >= 0; i--) candidates.Add(slot.Lines[i]);
        if (layer.SwapForms.TryGetValue(recordBase, out var byBase)) for (int i = byBase.Count - 1; i >= 0; i--) candidates.Add(byBase[i]);

        var alternatives = new List<BosAlternative>();
        double remaining = 1.0, keep = 0.0;
        bool random = false, leveled = false, perSession = false;
        FormKey? effective = null; BosSwapEntry? effectiveFrom = null;
        // TESObjectREFR::GetEncounterZone — ASSUMED to read the reference's own XEZN and its cell's; a leveled swap
        // with one present is cancelled and the walk ends there (Manager.cpp: swapData.first = nullptr, no retry).
        bool ezPresent = refEncounterZone is { IsNull: false } || facts?.EncounterZone is { IsNull: false };
        foreach (var c in candidates)
        {
            if (remaining <= 1e-12) break;
            // GetSwapBase: the chance is rolled first; then one member is drawn from the whole set, and a member
            // that names no record returns null — which sends BOS on to the NEXT line with that share.
            var members = c.Targets.Where(t => t.Key is { } k && t.Id.Exists).Select(t => t.Key!.Value).ToList();
            if (members.Count == 0) continue;
            double pass = c.Chance.Pass;
            if (pass <= 0) continue;
            if (!c.Chance.Certain) { random = true; if (c.Chance.PerSession) perSession = true; }
            if (c.RandomPick || c.AssignedAtLoad) random = true;
            double perMember = remaining * pass / c.Targets.Count;
            foreach (var m in members)
            {
                if (index.TypeOf(m) == "LeveledItem")
                {
                    leveled = true; random = true;
                    if (ezPresent) { keep += perMember; notes.Add($"swap to leveled item {m} cancelled: the reference or its cell carries an encounter zone (the record base stays)"); continue; }
                    var leaves = Flatten(index, m, 0, new HashSet<FormKey>());
                    if (leaves.Count == 0)
                    {
                        notes.Add($"leveled item {m} resolves to no entry — the reference's base becomes the list itself");
                        alternatives.Add(new BosAlternative(m, perMember, c, true));
                        effective ??= m; effectiveFrom ??= c;
                        continue;
                    }
                    notes.Add($"leveled item {m}: {leaves.Count} entries split evenly — an approximation of the engine's level-gated draw");
                    foreach (var leaf in leaves) alternatives.Add(new BosAlternative(leaf, perMember / leaves.Count, c, true));
                    effective ??= leaves[0]; effectiveFrom ??= c;
                }
                else
                {
                    alternatives.Add(new BosAlternative(m, perMember, c, false));
                    if (effective is null) { effective = m; effectiveFrom = c; }
                }
            }
            remaining -= perMember * members.Count;
        }
        remaining += keep;
        if (remaining > 1e-9) alternatives.Add(new BosAlternative(recordBase, remaining, null, false));
        // Merge equal bases (two lines naming the same swap).
        var merged = alternatives.GroupBy(a => a.Base).Select(g => new BosAlternative(g.Key, g.Sum(a => a.Probability), g.FirstOrDefault(a => a.From is not null)?.From, g.Any(a => a.ViaLeveledItem))).ToList();
        var effectiveBase = effective ?? recordBase;
        bool swapped = effectiveBase != recordBase;
        if (swapped && remaining > 1e-9) random = true;

        // ---- properties: the winning swap line's, else References -> conditional -> Forms by the ORIGINAL base ----
        BosProperties? props = null; BosProvenance? propsFrom = null; string? propsSource = null; bool propsRandom = false;
        if (effectiveFrom is { Properties: { IsValid: true } } ef) { props = ef.Properties; propsFrom = ef.Where; propsSource = "the swap line"; propsRandom = ef.Properties.Random; }
        else
        {
            var pcands = new List<(BosPropertyEntry E, string Source)>();
            if (layer.Properties.TryGetValue(refKey, out var pr)) for (int i = pr.Count - 1; i >= 0; i--) pcands.Add((pr[i], "[Transforms] by reference"));
            if (layer.PropertiesConditional.TryGetValue(recordBase, out var pc))
                foreach (var (_, slot) in pc.Reverse())
                    if (facts is not null && FiltersPass(slot.Filters, facts, recordBase, index))
                        for (int i = slot.Lines.Count - 1; i >= 0; i--) pcands.Add((slot.Lines[i], "filtered [Transforms] by base"));
            if (layer.Properties.TryGetValue(recordBase, out var pb)) for (int i = pb.Count - 1; i >= 0; i--) pcands.Add((pb[i], "[Transforms] by base"));
            foreach (var (e, src) in pcands)
            {
                if (!e.Properties.IsValid) continue;
                if (!e.Chance.Certain) { propsRandom = true; if (e.Chance.PerSession) perSession = true; notes.Add($"a [Transforms] line at {e.Chance.Value:0.#} % ({e.Where}) may or may not apply; the first is taken"); }
                props = e.Properties; propsFrom = e.Where; propsSource = src; propsRandom |= e.Properties.Random;
                break;
            }
        }
        return new BosResolution(recordBase, merged, effectiveBase, swapped, random, leveled, perSession, props, propsFrom, propsSource, propsRandom, notes);
    }

    static List<FormKey> Flatten(BosFormIndex index, FormKey lvli, int depth, HashSet<FormKey> seen)
    {
        var result = new List<FormKey>();
        if (depth > 8 || !seen.Add(lvli)) return result;
        foreach (var e in index.LeveledEntries(lvli))
        {
            if (index.TypeOf(e) == "LeveledItem") result.AddRange(Flatten(index, e, depth + 1, seen));
            else if (index.Exists(e)) result.Add(e);
        }
        return result;
    }

    /// <summary>ConditionalInput::IsValid(filters): no exclusion matches, and some inclusion does when there are any.</summary>
    public static bool FiltersPass(BosFilters f, BosCellFacts cell, FormKey baseKey, BosFormIndex index)
    {
        if (f.Not.Count > 0 && f.Not.Any(x => FilterMatches(x, cell, baseKey, index))) return false;
        if (f.Match.Count > 0 && !f.Match.Any(x => FilterMatches(x, cell, baseKey, index))) return false;
        return true;
    }

    /// <summary>ConditionalInput::IsValid for one filter, by the resolved form's type — or the string fallback.</summary>
    public static bool FilterMatches(BosFilter x, BosCellFacts cell, FormKey baseKey, BosFormIndex index)
    {
        switch (x.Kind)
        {
            case BosFilterKind.MissingForm: return false;
            case BosFilterKind.Text:
                if (cell.EditorId is { } ce && ce.Equals(x.Token, StringComparison.OrdinalIgnoreCase)) return true;
                if (cell.Location is { } loc && index.LocationKeywords(loc).Any(k => index.KeywordEditorId(k)?.Equals(x.Token, StringComparison.OrdinalIgnoreCase) == true)) return true;
                return index.BaseKeywords(baseKey).Any(k => index.KeywordEditorId(k)?.Equals(x.Token, StringComparison.OrdinalIgnoreCase) == true);
            case BosFilterKind.Form:
                var key = x.Key!.Value;
                switch (x.Type)
                {
                    case "Location":
                        var cur = cell.Location;
                        var seen = new HashSet<FormKey>();
                        while (cur is { } l && seen.Add(l)) { if (l == key) return true; cur = index.LocationParent(l); }
                        return false;
                    case "Region": return cell.Regions.Contains(key);
                    case "Keyword":
                        return (cell.Location is { } kl && index.LocationKeywords(kl).Contains(key)) || index.BaseKeywords(baseKey).Contains(key);
                    case "Cell": return cell.Cell == key;
                    case "Worldspace":
                        return !cell.Worldspace.IsNull && (cell.Worldspace == key || index.WorldspaceParent(cell.Worldspace) == key);
                    default: return false;
                }
        }
        return false;
    }
}
