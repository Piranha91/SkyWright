using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HarnessShared;

// AUTHOR-STATED LOAD ORDERS — read the patch page before inferring an order
// (Q39, a roadmap 4.20 follow-up; the per-file ordering evidence roadmap 4.21's catalogue will consume. 2026-09-11.)
//
// _shared/OrderRules.cs says an advice rule without provenance is a rumour and refuses it. That leaves an obvious
// hole: the provenance most patches actually HAVE is a sentence on the mod's own Nexus page, and until now nothing
// read it. A person read it, retyped it, and the retyping was the provenance. This file is the reader.
//
// WHAT IT IS AND IS NOT. It turns a page's description into ORDER RULES, and it would rather emit nothing than emit
// a rule the page does not state. Every shape it recognises is enumerated below; anything else becomes a NOTE that
// quotes the sentence and says why it was not turned into a rule. The one thing it must never do is the thing that
// would make it useless: launder a sentence it half-understood into a constraint the search then obeys.
//
//   S1  an ORDER LIST — a "Load order:" header followed by item lines, each of which names a mod. n items yield
//       n-1 adjacent rules. A list with an item that does not resolve is refused WHOLE, never as a partial list:
//       a list is a statement about a sequence, and two thirds of a sequence is a different statement.
//   S2  a PAIR SENTENCE — one sentence naming exactly two known mods with a direction between them. One rule.
//       'below' means later in the order; 'above' means earlier.
//   S3  the GENERIC PATCH-AFTER-MASTERS statement — "patches should be loaded after any mods which they are
//       patching". This is the trap. A naive "load after" matcher turns it into a rule about nothing, and it is
//       what a large share of patch pages actually say. It is reported as a note (the engine's master rule already
//       enforces it) and is explicitly NOT "no load order stated" — the page did say something, just nothing new.
//
// REFUSALS, each a note carrying the sentence: a conditional anywhere in the statement; three or more known names
// in a sentence that is not a list; two statements that disagree (both noted, no rule for the pair they disagree
// about); a name that resolves to nothing, or to two mods, or to no plugin, or to two plugins; and the word
// 'overwrite', which is ASSET priority and not load order at all.
//
// PURE. No file system, no network, no clock. It is handed the page's text and a WORLD — the known names, what the
// instance has installed, the page's own plugin's master list, and the dates the staleness check compares. That is
// what lets proofprobe pin every shape above on hand-written fixture text with the answer worked out by hand, and
// what lets adviceprobe replay an archived response with no network at all.
//
// The definitions implemented here are D1 (what counts as a stated order), D2 (a mention -> a mod -> one plugin
// filename) and D3 (staleness) from the Q39 run plan. Where the plan left a choice, the choice is marked CHOICE
// in a comment and repeated in the probe's report, because a reader who disagrees with a choice needs to find it.

// ---------------------------------------------------------------------------------------------------------
//  The world the extractor is handed
// ---------------------------------------------------------------------------------------------------------

/// <summary>A name the extractor is allowed to match a mention against: the page's own mod, or one of the page's
/// Nexus requirements. Nothing else is a known name — an unlisted mod named in the text resolves to nothing and the
/// statement that named it is refused, which is the point.</summary>
public sealed record AdviceKnownName(int ModId, string Name, bool IsPageMod);

/// <summary>What the INSTANCE has for one mod: the enabled folder(s) carrying its Nexus id, the plugin filenames
/// those folders put in the order, and the installed version and file date the staleness check needs.
/// <paramref name="Folders"/> with more than one entry is an ambiguity the extractor refuses on, not a list to pick
/// from. <paramref name="InstalledFileDate"/> null means UNKNOWN — never "old".
///
/// <paramref name="Plugins"/> is the LOADED BASELINE only: loadorder.txt minus the plugins plugins.txt leaves
/// unticked, which is exactly the set orderprobe builds its own baseline from. An unticked plugin is not in the
/// order the game loads, so it can neither resolve a mention nor make one ambiguous — a mod whose plugins are all
/// unticked contributes none. This matters on real mods: one College overhaul ships ten plugins in its folder root
/// (the main .esp plus nine compatibility patches), most of them unticked in any given profile, and counting the
/// folder's files rather than the order's would make almost every mention of it "ambiguous".</summary>
public sealed record AdviceWorldMod(
    int ModId,
    string Name,
    IReadOnlyList<string> Folders,
    IReadOnlyList<string> Plugins,
    string? InstalledVersion,
    string? InstalledFileDate,
    string? InstalledDateSource);

/// <summary>Everything the pure layer is told about the page and the machine. The probe builds it; the proof cases
/// write it by hand. <paramref name="PagePluginMasters"/> is the page's own plugin's declared master list and may be
/// empty — empty means "not known", so it breaks no tie rather than breaking every tie.
///
/// DATES. Every date compared here — <paramref name="PageUpdatedDate"/>, <paramref name="NewestMainDate"/> and each
/// mod's installed file date — is yyyy-MM-dd in UTC, and the caller converts. The two sources disagree about their
/// zone (a page's updatedAt is an ISO UTC timestamp, a file's date is epoch seconds), and rendering one of them in
/// local time moves it by a day often enough to matter: one real fixture's file uploaded 2025-05-02 UTC is
/// 2025-05-01 locally here. <paramref name="ReadDate"/> is the exception and is the machine's LOCAL date — it
/// records when a person's tool looked at the page, not an instant to compare against anything.</summary>
public sealed record AdviceWorld(
    int PageModId,
    string PageModName,
    string PageUrl,
    string ReadDate,
    IReadOnlyList<AdviceKnownName> Known,
    IReadOnlyDictionary<int, AdviceWorldMod> Installed,
    IReadOnlyList<string> PagePluginMasters,
    string? PageUpdatedDate = null,
    string? NewestMainVersion = null,
    string? NewestMainDate = null);

// ---------------------------------------------------------------------------------------------------------
//  What the extractor produces
// ---------------------------------------------------------------------------------------------------------

public enum AdviceShape
{
    /// <summary>S1 — a header and item lines.</summary>
    OrderList,
    /// <summary>S2 — one sentence naming exactly two mods with a direction.</summary>
    PairSentence,
    /// <summary>S3 — "a patch loads after what it patches", naming nothing.</summary>
    GenericPatchAfterMasters,
}

/// <summary>One statement found in the text, as found: its shape, the text a reader can grep the page for, (for
/// a list) the item lines in order, and the SYMPTOM the page states for the other order, if it states one.
/// The symptom rides on the statement rather than in a side table because it is part of what the page said.</summary>
public sealed record AdviceStatement(AdviceShape Shape, string Text, IReadOnlyList<string> Items, string? Symptom = null);

/// <summary>How one mention became a plugin filename, step by step. A rule that cannot show this chain is a rule
/// nobody can check.</summary>
public sealed record AdviceResolution(string Mention, int ModId, string ModName, string? Plugin, string How);

/// <summary>One emitted rule: always <c>advice</c>, always with the page as its source.</summary>
public sealed record AdviceRuleOut(
    string Before,
    string After,
    string Reason,
    string Source,
    string Checked,
    string VersionScope,
    string? Note,
    IReadOnlyList<string> Chain);

/// <summary>The extractor's whole answer. <paramref name="NothingStated"/> is the explicit positive result — the
/// page was read and states no order — and is non-null only when no statement and no note was produced.</summary>
public sealed record AdviceReport(
    int ModId,
    string ModName,
    string Url,
    string ReadDate,
    string? PageUpdatedDate,
    string? NewestMainVersion,
    string? NewestMainDate,
    IReadOnlyList<AdviceStatement> Statements,
    IReadOnlyList<AdviceRuleOut> Rules,
    IReadOnlyList<AdviceResolution> Resolutions,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> StalenessLines,
    IReadOnlyList<int> NamedModIds,
    string? NothingStated,
    IReadOnlyList<string> Choices);

// ---------------------------------------------------------------------------------------------------------

public static class OrderAdvice
{
    const RegexOptions IC = RegexOptions.IgnoreCase;

    /// <summary>The header spellings S1 recognises, lower-cased, with an optional trailing ':' or '-' already
    /// stripped by the caller. CHOICE: the run plan wrote the set as "load order, load the mods in this order, load
    /// in this order, recommended (load) order, install/load order"; the parenthetical and the slash are expanded
    /// here into literal alternatives rather than into a fuzzy matcher, so a reader can see the whole set.</summary>
    static readonly string[] Headers =
    {
        "load order",
        "load the mods in this order",
        "load in this order",
        "load them in this order",
        "recommended order",
        "recommended load order",
        "install order",
        "install/load order",
        "load/install order",
    };

    /// <summary>Self-references that mean "the mod whose page this is".</summary>
    static readonly string[] SelfRefs = { "this mod", "this patch", "the patch", "my patch", "this file" };

    /// <summary>Self-references for the SUPERSESSION check only. "this one" is added here and nowhere else: it is a
    /// perfectly clear back-reference in "…instead of this one", and a hopeless one as an item on a list.</summary>
    static readonly string[] SupersessionSelfRefs =
    { "this one", "this mod", "this patch", "this file", "my patch" };

    /// <summary>Edition suffixes dropped from a KNOWN name (never from the mention — dropping them from both would
    /// make "X SE" and "X SSE" the same mention, which is a guess).</summary>
    static readonly string[] EditionSuffixes =
    { " - special edition", " - sse", " - se", " (sse)", " (se)", " sse", " se", " le" };

    /// <summary>Conditionals. A statement carrying one of these is about SOME orders, not this one, so it is noted
    /// with its own sentence and never turned into a rule.</summary>
    static readonly string[] Conditionals =
    { "if you use", "if you have", "if you run", "if using", "when using", "for users of", "in case", "unless" };

    /// <summary>Tokens allowed to sit between the first mod name and the direction word in an S2 sentence. Anything
    /// else means the sentence is not the shape S2 describes, and a sentence this layer did not understand is a note,
    /// not a rule.</summary>
    static readonly HashSet<string> S2Filler = new(StringComparer.Ordinal)
    {
        "must", "should", "shall", "need", "needs", "has", "have", "to", "be", "been", "is", "are", "will", "can",
        "always", "load", "loads", "loaded", "loading", "go", "goes", "going", "sit", "sits", "place", "placed",
        "places", "put", "puts", "come", "comes", "install", "installs", "installed", "and", "then", "it", "this",
        "in", "the", "your", "order",
    };

    /// <summary>Tokens allowed between the direction word and the second mod name.</summary>
    static readonly HashSet<string> S2Tail = new(StringComparer.Ordinal) { "the", "your", "both", "all", "of" };

    /// <summary>The generic "a patch loads after what it patches" shapes (S3). Matched on a normalised sentence.</summary>
    static readonly Regex[] GenericPatch =
    {
        new(@"after\s+(any\s+|all\s+(of\s+)?the\s+|the\s+)?(mods?|plugins?|files?)\s+(which|that)\s+(they|it)\s+(are\s+)?patch(ing|es)?\b", IC),
        new(@"after\s+(any\s+|all\s+(of\s+)?the\s+|the\s+)?(mods?|plugins?|files?)\s+(they|it)\s+patch(es)?\b", IC),
        new(@"after\s+(its|their|the|the\s+required)\s+(masters?|requirements?|required\s+mods?)\b", IC),
        new(@"after\s+(what|whatever)\s+(they|it)\s+patch(es)?\b", IC),
    };

    /// <summary>Words that say a sentence is about ASSET priority, which is the mod manager's business and not the
    /// load order at all.</summary>
    static readonly Regex Overwrite = new(@"\boverwrit(e|es|ed|ing)\b", IC);

    /// <summary>The two halves of "this sentence is about ordering": a placement verb and a direction. Both together
    /// are what makes an unresolvable sentence worth a note rather than silence.</summary>
    static readonly Regex Direction = new(@"\b(after|before|below|above)\b", IC);
    static readonly Regex OrderVerb = new(@"\b(load|loads|loaded|loading|place|placed|places|put|puts|install|installs|installed)\b", IC);

    /// <summary>An S1 header phrase written INLINE rather than on its own line — "Load order: A, B, C". Not a list
    /// (the header is not alone on its line), but unmistakably about ordering.</summary>
    static bool HasOrderingPhrase(string normalised) =>
        Headers.Any(h => normalised.Contains(h, StringComparison.Ordinal))
        || normalised.Contains("in this order", StringComparison.Ordinal);

    /// <summary>Symptom markers — what the page says goes wrong in the other order. Carried verbatim into the rule's
    /// note, because "the skull is invisible" is what makes an advice rule checkable by a human.</summary>
    static readonly string[] SymptomMarkers =
    { "other way", "otherwise", "reversed", "wrong order", "the other order" };

    /// <summary>The generic-statement note, spelled once so the probe, the report and the proof case read the same
    /// sentence and a reader can grep for one string.</summary>
    public const string GenericNote =
        "the page states only that the patch loads after the mods it patches, which the engine's master rule already " +
        "enforces — no rule was emitted, and this is NOT the same as the page stating no order";

    // -----------------------------------------------------------------------------------------------------
    //  Markup -> text
    // -----------------------------------------------------------------------------------------------------

    /// <summary>A Nexus description (BBCode interleaved with HTML) as plain text. The steps are the ones
    /// housecarl-mcp's NexusTools.Render.StripMarkup uses — embeds dropped whole, [url=…]label[/url] to its label,
    /// [*] to its own line, every remaining BBCode/HTML tag stripped, entities decoded, whitespace normalised —
    /// copied here because the harness cannot reference that project (it is an exe and Render is internal).
    ///
    /// The ONE deliberate difference: there is NO length cap. That renderer caps a description at 6,000 characters
    /// because it is writing into a chat response; a statement can sit anywhere on a page, and the College hub's
    /// page is longer than that cap, so a capped read here would silently answer "no order stated" for the exact
    /// page the negative control is built on.</summary>
    public static string Clean(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var s = raw;

        // MO2 caches the description with literal \n escapes; a page read live has real newlines. Both arrive here.
        s = s.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\t", " ");

        s = Regex.Replace(s, @"\[(img|youtube|video|media|embed)\b[^\]]*\].*?\[/\1\]", " ", IC | RegexOptions.Singleline);
        s = Regex.Replace(s, @"\[url\b[^\]]*\](.*?)\[/url\]", "$1", IC | RegexOptions.Singleline);
        s = Regex.Replace(s, @"\[\*\]", "\n• ", IC);
        s = Regex.Replace(s, @"\[/\*\]", "", IC);
        s = Regex.Replace(s, @"\[/?[a-z][a-z0-9]*(=[^\]]*)?\]", "", IC);

        s = Regex.Replace(s, @"<\s*br\s*/?>", "\n", IC);
        s = Regex.Replace(s, @"<\s*/?\s*(p|div|li|ul|ol|h[1-6]|tr|table)\b[^>]*>", "\n", IC);
        s = Regex.Replace(s, @"<[^>]+>", "", RegexOptions.Singleline);

        s = s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
             .Replace("&#39;", "'").Replace("&apos;", "'").Replace("&nbsp;", " ")
             .Replace("&amp;", "&");

        s = s.Replace("﻿", "").Replace("​", "").Replace("‌", "").Replace("‍", "").Replace(' ', ' ');

        s = s.Replace("\r\n", "\n").Replace('\r', '\n');
        s = Regex.Replace(s, @"[ \t]+", " ");
        s = Regex.Replace(s, @" *\n *", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    // -----------------------------------------------------------------------------------------------------
    //  D2 — a mention becomes a mod
    // -----------------------------------------------------------------------------------------------------

    /// <summary>Case-folded, apostrophes and quotes unified, whitespace collapsed. Dashes are NOT normalised: the
    /// definition does not ask for it, and a name that only matches once an en dash becomes a hyphen is a finding
    /// about the definition, not something to paper over here.</summary>
    static string Norm(string s)
    {
        if (s is null) return "";
        var t = s.Replace('’', '\'').Replace('‘', '\'').Replace('“', '"').Replace('”', '"');
        t = Regex.Replace(t, @"\s+", " ").Trim();
        return t.ToLowerInvariant();
    }

    static string DropEdition(string normalisedKnown)
    {
        foreach (var suf in EditionSuffixes)
            if (normalisedKnown.EndsWith(suf, StringComparison.Ordinal) && normalisedKnown.Length > suf.Length)
                return normalisedKnown[..^suf.Length].Trim();
        return normalisedKnown;
    }

    /// <summary>A trailing "(…)" removed, and a trailing " patch" removed, and both. CHOICE: D2 spells the arm as
    /// "the name equals the mention with a trailing ' patch' / ' (this mod)' parenthetical removed"; applying both
    /// reductions together is the only way "X patch (this mod)" — which is exactly how one of the three real pages
    /// writes its own name — reaches "X".</summary>
    static IEnumerable<string> Reductions(string m)
    {
        yield return m;
        var noParen = StripTrailingParenthetical(m);
        if (noParen != m) yield return noParen;
        foreach (var b in new[] { m, noParen }.Distinct(StringComparer.Ordinal))
            if (b.EndsWith(" patch", StringComparison.Ordinal))
                yield return b[..^" patch".Length].Trim();
    }

    static string StripTrailingParenthetical(string m)
    {
        var t = m.TrimEnd();
        if (!t.EndsWith(")", StringComparison.Ordinal)) return m;
        int open = t.LastIndexOf('(');
        return open <= 0 ? m : t[..open].Trim();
    }

    static string? TrailingParentheticalBody(string m)
    {
        var t = m.TrimEnd();
        if (!t.EndsWith(")", StringComparison.Ordinal)) return null;
        int open = t.LastIndexOf('(');
        return open < 0 ? null : t[(open + 1)..^1].Trim();
    }

    /// <summary>Does <paramref name="mention"/> name <paramref name="known"/>? The three arms of D2 and nothing else:
    /// equality; equality with the KNOWN name's edition suffix dropped; and the known name equalling a reduction of
    /// the mention. No substring, no edit distance — a resolver that fuzzes is a resolver that guesses.</summary>
    static bool NameMatches(string mention, string known)
    {
        var m = Norm(mention);
        var k = Norm(known);
        if (m.Length == 0 || k.Length == 0) return false;
        if (m == k) return true;
        if (m == DropEdition(k)) return true;
        foreach (var r in Reductions(m)) if (r == k) return true;
        return false;
    }

    sealed record Hit(int ModId, string ModName, string How);

    /// <summary>Resolve one whole mention (an S1 item line, say) to a mod. Null when nothing matches; a
    /// <paramref name="ambiguity"/> message when two known names match, which is a refusal and not a coin toss.</summary>
    static Hit? ResolveMention(string mention, AdviceWorld world, out string? ambiguity)
    {
        ambiguity = null;
        var m = Norm(mention);
        var mNoParen = Norm(StripTrailingParenthetical(mention));

        // 1. the mention IS a self-reference.
        if (SelfRefs.Contains(m) || SelfRefs.Contains(mNoParen))
            return new Hit(world.PageModId, world.PageModName, $"'{mention.Trim()}' is a self-reference to the page's own mod");

        // 2. a known name. Longest first, so a longer name is preferred over a shorter one it contains.
        var matches = world.Known
            .Where(k => NameMatches(mention, k.Name))
            .OrderByDescending(k => k.Name.Length)
            .ToList();
        if (matches.Count == 1)
            return new Hit(matches[0].ModId, matches[0].Name,
                           $"'{mention.Trim()}' matches the {(matches[0].IsPageMod ? "page's own mod" : "requirement")} '{matches[0].Name}' (mod {matches[0].ModId})");
        if (matches.Count > 1)
        {
            // Two DIFFERENT mods with names that both match is genuinely ambiguous. The same mod listed twice is not.
            var ids = matches.Select(x => x.ModId).Distinct().ToList();
            if (ids.Count > 1)
            {
                ambiguity = $"'{mention.Trim()}' matches more than one known mod name ({string.Join(", ", matches.Select(x => $"'{x.Name}' (mod {x.ModId})"))}) — refused rather than picked between";
                return null;
            }
            return new Hit(matches[0].ModId, matches[0].Name, $"'{mention.Trim()}' matches '{matches[0].Name}' (mod {matches[0].ModId})");
        }

        // 3. CHOICE: "<full name> (this mod)" where the name itself matched nothing. The author wrote the strongest
        //    self-reference a page can carry, in brackets, on the item line. Resolved to the page's mod, and the
        //    chain SAYS the name before the bracket matched nothing so a reader can see which half did the work.
        var body = TrailingParentheticalBody(mention);
        if (body is not null && SelfRefs.Contains(Norm(body)))
            return new Hit(world.PageModId, world.PageModName,
                           $"'{mention.Trim()}' resolved by its '({body})' self-reference; the text before the bracket " +
                           $"('{StripTrailingParenthetical(mention).Trim()}') matched no known name");
        return null;
    }

    // -----------------------------------------------------------------------------------------------------
    //  D2 — a mod becomes one plugin filename
    // -----------------------------------------------------------------------------------------------------

    sealed record PluginPick(string? Plugin, string How);

    static PluginPick PickPlugin(int modId, string modName, AdviceWorld world)
    {
        if (!world.Installed.TryGetValue(modId, out var m) || m.Folders.Count == 0)
            return new PluginPick(null, $"'{modName}' (mod {modId}) is not installed in this profile, so no rule can name a plugin for it");
        if (m.Folders.Count > 1)
            return new PluginPick(null, $"'{modName}' (mod {modId}) is ambiguous: {m.Folders.Count} enabled mod folders carry that Nexus id ({string.Join(", ", m.Folders.Select(f => $"'{f}'"))})");
        if (m.Plugins.Count == 0)
            return new PluginPick(null, $"'{modName}' (mod {modId}, folder '{m.Folders[0]}') is installed but contributes no plugin to the order");
        if (m.Plugins.Count == 1)
            return new PluginPick(m.Plugins[0], $"'{modName}' (mod {modId}) puts exactly one plugin in the order: {m.Plugins[0]}");

        var named = m.Plugins.Where(p => world.PagePluginMasters.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        if (named.Count == 1)
            return new PluginPick(named[0],
                $"'{modName}' (mod {modId}) puts {m.Plugins.Count} plugins in the order ({string.Join(", ", m.Plugins)}); " +
                $"the page's own plugin declares exactly one of them as a master, so the master list chose {named[0]}");
        return new PluginPick(null,
            $"'{modName}' (mod {modId}) is ambiguous: it puts {m.Plugins.Count} plugins in the order ({string.Join(", ", m.Plugins)}) " +
            (world.PagePluginMasters.Count == 0
                ? "and the page's own plugin's master list is not known, so nothing chooses between them"
                : $"and the page's own plugin's master list names {named.Count} of them, not exactly one"));
    }

    // -----------------------------------------------------------------------------------------------------
    //  Sentences and lines
    // -----------------------------------------------------------------------------------------------------

    static List<string> Sentences(string text)
    {
        var outv = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var l = line.Trim();
            if (l.Length == 0) continue;
            foreach (var part in Regex.Split(l, @"(?<=[.!?])\s+"))
            {
                var p = part.Trim();
                if (p.Length > 0) outv.Add(p);
            }
        }
        return outv;
    }

    static readonly Regex Marker = new(@"^\s*(•|●|▪|·|[-–—*>]|\(?\d+[.)])\s+", RegexOptions.None);

    static (bool Marked, string Body) StripMarker(string line)
    {
        var m = Marker.Match(line);
        return m.Success ? (true, line[m.Length..].Trim()) : (false, line.Trim());
    }

    static bool IsHeader(string line)
    {
        var (_, body) = StripMarker(line);
        var t = Norm(body).TrimEnd('.', ':', '-', '–', '—').Trim();
        return Headers.Contains(t, StringComparer.Ordinal);
    }

    static string? Conditional(string text)
    {
        var n = Norm(text);
        foreach (var c in Conditionals) if (n.Contains(c, StringComparison.Ordinal)) return c;
        return null;
    }

    // -----------------------------------------------------------------------------------------------------
    //  Finding known names INSIDE a sentence (S2 / the three-name refusal)
    // -----------------------------------------------------------------------------------------------------

    sealed record Span(int Start, int Length, int ModId, string ModName, string Text)
    {
        public int End => Start + Length;
    }

    /// <summary>Every known mod named in one sentence, in text order, longest name first so a longer name is never
    /// split into a shorter one it contains. Spans do not overlap: a name found inside an already-claimed span is
    /// the same mention, not a second one.</summary>
    static List<Span> NamesIn(string sentence, AdviceWorld world)
    {
        var norm = Norm(sentence);
        var found = new List<Span>();

        void Claim(int start, int len, int modId, string modName)
        {
            foreach (var s in found) if (start < s.End && s.Start < start + len) return;   // overlaps a longer name
            found.Add(new Span(start, len, modId, modName, norm.Substring(start, len)));
        }

        var forms = new List<(string Form, int ModId, string ModName)>();
        foreach (var k in world.Known)
        {
            var n = Norm(k.Name);
            forms.Add((n, k.ModId, k.Name));
            var d = DropEdition(n);
            if (d != n) forms.Add((d, k.ModId, k.Name));
        }
        foreach (var sr in SelfRefs) forms.Add((sr, world.PageModId, world.PageModName));

        foreach (var (form, modId, modName) in forms.OrderByDescending(f => f.Form.Length).ThenBy(f => f.Form, StringComparer.Ordinal))
        {
            if (form.Length == 0) continue;
            int at = 0;
            while (true)
            {
                int i = norm.IndexOf(form, at, StringComparison.Ordinal);
                if (i < 0) break;
                bool leftOk = i == 0 || !char.IsLetterOrDigit(norm[i - 1]);
                bool rightOk = i + form.Length >= norm.Length || !char.IsLetterOrDigit(norm[i + form.Length]);
                if (leftOk && rightOk) Claim(i, form.Length, modId, modName);
                at = i + 1;
            }
        }
        return found.OrderBy(s => s.Start).ToList();
    }

    static IEnumerable<string> Tokens(string s) =>
        Regex.Split(s, @"[^a-z0-9']+").Where(t => t.Length > 0);

    // -----------------------------------------------------------------------------------------------------
    //  The extractor
    // -----------------------------------------------------------------------------------------------------

    /// <summary>Read a page's description and say what order, if any, it states. <paramref name="rawDescription"/>
    /// is the page's markup as the API returned it; the world is everything else. Pure: same inputs, same answer.</summary>
    public static AdviceReport Extract(string rawDescription, AdviceWorld world)
    {
        var text = Clean(rawDescription);
        var lines = text.Split('\n');

        var statements = new List<AdviceStatement>();
        var notes = new List<string>();
        var resolutions = new List<AdviceResolution>();
        var choices = new List<string>
        {
            "S1 header set: the run plan's parenthetical and slash were expanded into literal alternatives (" +
            string.Join(", ", Headers.Select(h => $"'{h}'")) + "), each with an optional trailing ':' or '-'.",
            "S1 item termination: the first item fixes the list's STYLE. In a marked list (bullet or number) every " +
            "further marked line is an item and an item that does not resolve refuses the whole list; the list ends " +
            "at the first unmarked line. In a BARE list an unresolved line ends the list only when it reads as a " +
            "heading or as prose — it ends with ':', or it ends in '.', '!' or '?', or it runs to more than eight " +
            "words. Any other unresolved bare line is a broken ITEM and refuses the whole list, because D1 never " +
            "emits a partial list and 'the first unresolved line ends it' would do exactly that.",
            "A mention's reductions (trailing parenthetical, trailing ' patch') are applied together, so " +
            "'X patch (this mod)' reaches 'X'.",
            "S2 shape: a direction word (after/before/below/above) must sit BETWEEN the two name spans, with only " +
            "connective tokens on either side of it. A sentence that names two mods and carries a direction word but " +
            "does not fit is a NOTE, never a rule.",
            "D2's 'plugin files that appear in loadorder.txt' is read as the LOADED BASELINE — loadorder.txt minus " +
            "the plugins plugins.txt leaves unticked. An unticked plugin resolves no mention and creates no ambiguity.",
            "D3 compares dates in UTC on both sides (the page's updatedAt is an ISO UTC timestamp, a file's date is " +
            "epoch seconds); the staleness lines say UTC. The 'checked' date is the machine's LOCAL date, because it " +
            "records when the page was looked at rather than an instant being compared.",
            "A sentence carrying ordering language (a placement verb AND a direction word) that resolves fewer than " +
            "two known mods is a NOTE quoting it, not silence — 'Load ICOW after MLM' is a stated order this tool " +
            "cannot follow, and answering 'no order stated' there would be a wrong answer rather than a refusal.",
            "The three-or-more-names refusal is GATED on ordering language (a direction word, or an S1 header phrase " +
            "written inline). A sentence naming three mods with no ordering language — 'this patch makes X compatible " +
            "with Y' — produces nothing, so the notes stay readable.",
            "A mention inside PROSE is a word-bounded occurrence of a known name or of its edition-suffix-dropped " +
            "form, longest first; an acronym or abbreviation never resolves. An S1 item line resolves by equality " +
            "over the whole line instead, so 'Alpha Mod Redux' is not 'Alpha Mod' there.",
            "A supersession notice ('download X instead of this one') is a NOTE and never a rule, and it never " +
            "changes the rules. The page's stated order is still the page's stated order; whether to use the mod at " +
            "all is a different question, and answering the first while hiding the second would be a wrong answer.",
        };

        // ---- S1 -----------------------------------------------------------------------------------------
        var consumed = new bool[lines.Length];
        var listPairs = new List<(AdviceStatement St, List<Hit> Items)>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (!IsHeader(lines[i])) continue;
            var header = lines[i].Trim();
            var items = new List<Hit>();
            var itemText = new List<string>();
            bool? marked = null;
            string? refusal = null;
            int j = i + 1, last = i;
            while (j < lines.Length)
            {
                if (lines[j].Trim().Length == 0) { j++; continue; }
                var (isMarked, body) = StripMarker(lines[j]);
                if (marked is null) marked = isMarked;
                else if (marked != isMarked) break;                         // the list's style changed: it ended
                var hit = ResolveMention(body, world, out var amb);
                if (hit is null)
                {
                    // A BARE line that fails to resolve ends the list only when it reads as a heading or as prose:
                    // it ends with ':', it ends in sentence punctuation, or it runs longer than eight words. Anything
                    // shorter and unpunctuated was meant as an item, and an item that names nothing known refuses the
                    // WHOLE list — D1 never emits a partial list, and "the first unresolved line ends it" would.
                    if (marked == false && EndsTheList(body)) break;
                    refusal = $"the load-order list under '{header}' names '{body}', which matches no known mod " +
                              (amb is null
                                  ? "(the page's own mod and its Nexus requirements are the only known names) — "
                                  : $"unambiguously: {amb} — ") +
                              $"the whole list is refused rather than emitted in part. Items: {string.Join(" → ", itemText.Append(body))}";
                    break;
                }
                items.Add(hit); itemText.Add(body); consumed[j] = true; last = j;
                j++;
            }
            consumed[i] = true;
            if (refusal is not null) { notes.Add(refusal); for (int k = i; k <= last; k++) consumed[k] = true; continue; }
            if (items.Count < 2)
            {
                notes.Add($"'{header}' looks like a load-order header but only {items.Count} item(s) followed it that " +
                          "resolve to a known mod; a list needs at least two, so no rule was emitted from it.");
                continue;
            }
            // The symptom: one of the two sentences after the last item line.
            var st = new AdviceStatement(AdviceShape.OrderList, header + " " + string.Join(" → ", itemText), itemText,
                                         SymptomAfter(lines, last));
            var cond = Conditional(st.Text);
            if (cond is not null)
            {
                notes.Add($"the load-order list \"{st.Text}\" carries the conditional '{cond}', so it states an order for " +
                          "SOME setups and not for this one — noted, never turned into a rule.");
                statements.Add(st);
                continue;
            }
            statements.Add(st);
            listPairs.Add((st, items));
        }

        // ---- S2 / S3 / the refusals, sentence by sentence -------------------------------------------------
        var pairStatements = new List<(AdviceStatement St, Hit Before, Hit After)>();
        int lineNo = -1;
        foreach (var rawLine in lines)
        {
            lineNo++;
            if (consumed[lineNo]) continue;
            foreach (var sentence in Regex.Split(rawLine.Trim(), @"(?<=[.!?])\s+"))
            {
                var s = sentence.Trim();
                if (s.Length == 0) continue;
                var named = NamesIn(s, world);
                var distinct = named.Select(n => n.ModId).Distinct().Count();

                // Supersession. Not an order and never a rule, but a page whose first line says to use a different
                // patch is a page whose stated order comes with a warning attached, and handing over the order in
                // silence is the wrong answer rather than a refusal.
                var ns = Norm(s);
                if (ns.Contains("instead", StringComparison.Ordinal)
                    && SupersessionSelfRefs.Any(x => ns.Contains(x, StringComparison.Ordinal)))
                {
                    notes.Add($"the author recommends something else instead of this mod: \"{s}\" — the rules below are " +
                              "still the page's stated order for THIS mod; whether to use it at all is the reader's call");
                    continue;
                }

                if (Overwrite.IsMatch(s) && distinct >= 2)
                {
                    notes.Add($"\"{s}\" is about ASSET overwrite priority, which the mod manager decides and the load " +
                              "order does not — noted, never turned into an order rule.");
                    continue;
                }

                // S3 — the generic statement. "with no other mod named" is read as: no mod OTHER than the page's own,
                // so "this patch loads after its masters" is still the generic statement and not a pair sentence.
                int others = named.Where(n => n.ModId != world.PageModId).Select(n => n.ModId).Distinct().Count();
                if (others == 0 && GenericPatch.Any(rx => rx.IsMatch(Norm(s))) && Regex.IsMatch(s, @"\bpatch(es)?\b", IC))
                {
                    statements.Add(new AdviceStatement(AdviceShape.GenericPatchAfterMasters, s, Array.Empty<string>()));
                    notes.Add($"\"{s}\" — {GenericNote}.");
                    continue;
                }
                // A sentence that is plainly ABOUT ordering but names fewer than two mods this tool can resolve is
                // the most dangerous silence available: the page did state an order, and the answer "no order
                // stated" would be wrong. It is noted with its own words. (A real page says "Load ICOW after MLM" —
                // an acronym and an abbreviation, neither of which is a known name, and neither of which this tool
                // will guess at.)
                bool orderingLanguage = OrderVerb.IsMatch(s) && Direction.IsMatch(s);
                if (distinct < 2)
                {
                    if (orderingLanguage)
                        notes.Add($"an ordering sentence names something this tool cannot resolve: \"{s}\" — the page's " +
                                  "own mod and its Nexus requirements are the only known names; no rule");
                    continue;
                }

                if (distinct >= 3)
                {
                    // Gated on ordering language: a sentence can name three mods while saying nothing about their
                    // order ("this patch makes X compatible with Y"), and noting every one of those would bury the
                    // refusals that matter under prose.
                    if (Direction.IsMatch(s) || HasOrderingPhrase(Norm(s)))
                        notes.Add($"\"{s}\" names {distinct} known mods in one sentence and is not a load-order list; " +
                                  "a three-way order cannot be read out of a sentence without guessing which pair each " +
                                  "clause is about — noted, no rule.");
                    continue;
                }

                var cond2 = Conditional(s);
                if (cond2 is not null)
                {
                    notes.Add($"\"{s}\" carries the conditional '{cond2}', so it states an order for SOME setups and " +
                              "not for this one — noted, never turned into a rule.");
                    continue;
                }

                var parsed = ParsePair(s, named);
                if (parsed is null)
                {
                    if (Direction.IsMatch(s))
                        notes.Add($"\"{s}\" names two known mods and carries a direction word, but not in the shape S2 " +
                                  "describes (a direction between the two names with only connective words around it) — " +
                                  "noted rather than guessed at.");
                    continue;
                }
                var (beforeSpan, afterSpan) = parsed.Value;
                var hb = ResolveMention(beforeSpan.Text, world, out _) ?? new Hit(beforeSpan.ModId, beforeSpan.ModName, $"'{beforeSpan.Text}' names '{beforeSpan.ModName}' (mod {beforeSpan.ModId})");
                var ha = ResolveMention(afterSpan.Text, world, out _) ?? new Hit(afterSpan.ModId, afterSpan.ModName, $"'{afterSpan.Text}' names '{afterSpan.ModName}' (mod {afterSpan.ModId})");
                var st2 = new AdviceStatement(AdviceShape.PairSentence, s, new[] { beforeSpan.Text, afterSpan.Text },
                                              SymptomAfterSentence(text, s));
                statements.Add(st2);
                pairStatements.Add((st2, hb, ha));
            }
        }

        // ---- candidate rules, by mod id, before the disagreement pass -------------------------------------
        var candidates = new List<(AdviceStatement St, Hit Before, Hit After)>();
        foreach (var (st, items) in listPairs)
            for (int k = 0; k + 1 < items.Count; k++)
                candidates.Add((st, items[k], items[k + 1]));
        candidates.AddRange(pairStatements);

        // ---- disagreement ------------------------------------------------------------------------------
        var byPair = candidates.GroupBy(c => PairKey(c.Before.ModId, c.After.ModId)).ToList();
        var conflicted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in byPair)
        {
            var dirs = g.Select(c => c.Before.ModId < c.After.ModId).Distinct().ToList();
            if (dirs.Count <= 1) continue;
            conflicted.Add(g.Key);
            foreach (var st in g.Select(c => c.St).Distinct())
                notes.Add($"two statements on this page disagree about the order of '{g.First().Before.ModName}' and " +
                          $"'{g.First().After.ModName}'. One of them is \"{st.Text}\". No rule is emitted for that pair; " +
                          "both statements are reported so a reader can decide.");
        }
        candidates = candidates.Where(c => !conflicted.Contains(PairKey(c.Before.ModId, c.After.ModId))).ToList();

        // ---- resolution to plugins, staleness, and the rules ----------------------------------------------
        var namedMods = candidates.SelectMany(c => new[] { c.Before, c.After })
                                  .GroupBy(h => h.ModId).Select(g => g.First())
                                  .OrderBy(h => h.ModId).ToList();
        var picks = new Dictionary<int, PluginPick>();
        foreach (var h in namedMods)
        {
            var p = PickPlugin(h.ModId, h.ModName, world);
            picks[h.ModId] = p;
            resolutions.Add(new AdviceResolution(h.How, h.ModId, h.ModName, p.Plugin, p.How));
            if (p.Plugin is null) notes.Add(p.How + " — the statement that names it produced no rule.");
        }

        var staleness = new Dictionary<int, (string Report, string? Note)>();
        var stalenessLines = new List<string>();
        foreach (var h in namedMods)
        {
            if (picks[h.ModId].Plugin is null) continue;
            var line = Staleness(h, world, out var noteLine);
            staleness[h.ModId] = (line, noteLine);
            stalenessLines.Add(line);
        }

        var rules = new List<AdviceRuleOut>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (st, hb, ha) in candidates)
        {
            var pb = picks[hb.ModId]; var pa = picks[ha.ModId];
            if (pb.Plugin is null || pa.Plugin is null) continue;
            if (pb.Plugin.Equals(pa.Plugin, StringComparison.OrdinalIgnoreCase))
            {
                notes.Add($"\"{st.Text}\" orders '{hb.ModName}' against '{ha.ModName}', but both resolve to the same " +
                          $"plugin ({pb.Plugin}) — no rule can be made from that.");
                continue;
            }
            if (!seen.Add($"{pb.Plugin}|{pa.Plugin}")) continue;

            var noteParts = new List<string>();
            if (st.Symptom is { Length: > 0 } sym) noteParts.Add($"symptom stated: \"{sym}\"");
            foreach (var id in new[] { hb.ModId, ha.ModId })
                if (staleness.TryGetValue(id, out var sv) && sv.Note is not null) noteParts.Add(sv.Note);

            rules.Add(new AdviceRuleOut(
                pb.Plugin, pa.Plugin,
                st.Shape == AdviceShape.OrderList ? st.Text : st.Text,
                world.PageUrl, world.ReadDate, VersionScope(world, st),
                noteParts.Count == 0 ? null : string.Join("; ", noteParts),
                new[] { hb.How, pb.How, ha.How, pa.How }));
        }

        string? nothing = null;
        if (statements.Count == 0 && notes.Count == 0)
            nothing = $"no load order stated on {world.PageUrl} (read {world.ReadDate})";

        return new AdviceReport(
            world.PageModId, world.PageModName, world.PageUrl, world.ReadDate,
            world.PageUpdatedDate, world.NewestMainVersion, world.NewestMainDate,
            statements, rules, resolutions, notes, stalenessLines,
            namedMods.Select(h => h.ModId).Distinct().OrderBy(x => x).ToList(),
            nothing, choices);
    }

    static string PairKey(int a, int b) => a < b ? $"{a}|{b}" : $"{b}|{a}";

    /// <summary>Does this unresolved BARE line end the list, or is it a broken item? The three signals a mod page
    /// actually uses to move from a list back to prose: a heading ("Requirements:"), a finished sentence ("If I've
    /// missed anything, please let me know!"), or a line too long to be a mod name. Everything else was meant as an
    /// item, and a list with an item that names nothing known is refused whole (D1).
    ///
    /// Eight words is the cut. It is a judgement, and it is deliberately generous: the longest mod name in the three
    /// real pages this was built against is six words, and a sentence of eight words or fewer that carries no final
    /// punctuation is rare enough that refusing the list — which is the SAFE answer — is the right cost.</summary>
    static bool EndsTheList(string body)
    {
        var t = body.Trim();
        if (t.Length == 0) return true;
        if (t.EndsWith(":", StringComparison.Ordinal)) return true;
        if (t[^1] is '.' or '!' or '?') return true;
        return t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 8;
    }

    /// <summary>The symptom, if one of the TWO sentences after the list's last item names one.</summary>
    static string? SymptomAfter(string[] lines, int lastItemLine)
    {
        var rest = new StringBuilder();
        for (int i = lastItemLine + 1; i < lines.Length; i++) rest.Append(lines[i]).Append('\n');
        return FirstSymptom(Sentences(rest.ToString()).Take(2));
    }

    static string? SymptomAfterSentence(string text, string sentence)
    {
        var all = Sentences(text);
        int at = all.FindIndex(s => string.Equals(s, sentence, StringComparison.Ordinal));
        return at < 0 ? null : FirstSymptom(all.Skip(at + 1).Take(2));
    }

    static string? FirstSymptom(IEnumerable<string> sentences)
    {
        foreach (var s in sentences)
        {
            var n = Norm(s);
            if (SymptomMarkers.Any(m => n.Contains(m, StringComparison.Ordinal))) return s;
        }
        return null;
    }

    /// <summary>S2's shape test: the two names, a direction between them, only connective words either side of it.
    /// Returns (the one that loads EARLIER, the one that loads LATER), or null when the sentence is some other shape.</summary>
    static (Span Before, Span After)? ParsePair(string sentence, List<Span> named)
    {
        if (named.Count < 2) return null;
        var a = named[0];
        var b = named.First(n => n.ModId != a.ModId);
        if (b.Start < a.End) return null;
        var norm = Norm(sentence);
        var between = norm[a.End..b.Start];

        var m = Regex.Match(between, @"\b(after|before|below|above)\b", IC);
        if (!m.Success) return null;
        var pre = between[..m.Index];
        var post = between[(m.Index + m.Length)..];
        if (Tokens(pre).Any(t => !S2Filler.Contains(t))) return null;
        if (Tokens(post).Any(t => !S2Tail.Contains(t))) return null;

        // 'below' = later in the order; 'above' = earlier. 'A after B' => B loads first.
        return m.Value.ToLowerInvariant() switch
        {
            "after" or "below" => (b, a),
            _ => (a, b),
        };
    }

    static string VersionScope(AdviceWorld world, AdviceStatement st)
    {
        var sb = new StringBuilder();
        sb.Append(world.PageModName);
        sb.Append(world.NewestMainVersion is { Length: > 0 } v ? $" newest MAIN file v{v}" : " newest MAIN file version unknown");
        if (world.NewestMainDate is { Length: > 0 } d) sb.Append($" (uploaded {d} UTC)");
        var versions = Regex.Matches(st.Text, @"\bv(?:ersion\s+)?(\d[\w.]*)", IC)
                            .Select(x => x.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (versions.Count > 0) sb.Append($"; the statement itself names version(s) {string.Join(", ", versions)}");
        return sb.ToString();
    }

    /// <summary>D3. Never removes or weakens a rule — it annotates it. An unknown installed date is UNKNOWN and says
    /// so; it is never quietly read as "not newer".</summary>
    static string Staleness(Hit h, AdviceWorld world, out string? note)
    {
        note = null;
        var stated = world.PageUpdatedDate;
        if (!world.Installed.TryGetValue(h.ModId, out var m))
        {
            note = $"staleness not checked for {h.ModName}: it is not installed";
            return note;
        }
        if (m.InstalledFileDate is null or "")
        {
            note = $"staleness not checked for {h.ModName}: installed version and date unknown" +
                   (m.InstalledVersion is { Length: > 0 } iv ? $" (meta.ini says version {iv}, which matched no file on the page)" : "");
            return note;
        }
        if (stated is null or "")
        {
            note = $"staleness not checked for {h.ModName}: the page's own last-update date is unknown";
            return note;
        }
        // Both are yyyy-MM-dd in UTC (see AdviceWorld), so an ordinal compare IS a chronological one and the two
        // sides are in the same zone. Rendering either in local time would move it by a day on a real fixture.
        bool newer = string.CompareOrdinal(m.InstalledFileDate, stated) > 0;
        if (newer)
        {
            note = $"statement may be stale: {h.ModName} installed {m.InstalledVersion ?? "(version unknown)"} " +
                   $"(uploaded {m.InstalledFileDate} UTC) is newer than the page's last update ({stated} UTC)";
            return note;
        }
        return $"checked: {h.ModName} installed {m.InstalledFileDate} UTC, not newer than the statement ({stated} UTC)";
    }

    // -----------------------------------------------------------------------------------------------------
    //  The rules fragment
    // -----------------------------------------------------------------------------------------------------

    /// <summary>The report as an OrderRules schema-1 fragment: <c>schemaVersion</c>, an empty <c>fixed</c>, the rules,
    /// and <c>$comment</c> carrying the notes. System.Text.Json ignores the unmapped <c>$comment</c>, so the fragment
    /// round-trips through <see cref="OrderRules.Parse"/> unchanged — the notes travel WITH the rules instead of
    /// living only in a report nobody passes to the search.
    ///
    /// A report with no rules still writes a file, and the file still says why: an empty rules array with no
    /// explanation is indistinguishable from a run that failed silently (Q3).</summary>
    public static string ToRulesJson(AdviceReport r)
    {
        var comments = new List<string>
        {
            $"generated by adviceprobe from {r.Url} (read {r.ReadDate}) — every rule below is the page's own words, " +
            "and every sentence that was NOT turned into a rule is listed here with the reason.",
        };
        if (r.NothingStated is not null) comments.Add(r.NothingStated);
        foreach (var s in r.Statements)
            comments.Add($"statement ({Shape(s.Shape)}): {s.Text}" +
                         (s.Symptom is { Length: > 0 } ? $"   [symptom stated: \"{s.Symptom}\"]" : ""));
        foreach (var n in r.Notes) comments.Add("note: " + n);
        foreach (var s in r.StalenessLines) comments.Add("staleness: " + s);
        if (r.Rules.Count == 0)
            comments.Add("no rules: nothing on this page states an order between two plugins in this order. " +
                         "This file is a positive result, not an empty one — see the notes above for what was found.");

        var doc = new Dictionary<string, object?>
        {
            ["$comment"] = comments,
            ["schemaVersion"] = OrderRules.CurrentSchema,
            ["fixed"] = Array.Empty<string>(),
            ["rules"] = r.Rules.Select(x => new Dictionary<string, object?>
            {
                ["before"] = x.Before,
                ["after"] = x.After,
                ["hardness"] = "advice",
                ["reason"] = x.Reason,
                ["provenance"] = new Dictionary<string, object?>
                {
                    ["source"] = x.Source,
                    ["checked"] = x.Checked,
                    ["versionScope"] = x.VersionScope,
                    ["note"] = x.Note,
                },
                ["$comment"] = x.Chain,
            }).ToList(),
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string Shape(AdviceShape s) => s switch
    {
        AdviceShape.OrderList => "S1, an order list",
        AdviceShape.PairSentence => "S2, a pair sentence",
        _ => "S3, the generic patch-after-masters statement",
    };

    /// <summary>The report in words — what a reader sees on the console and what the run record keeps.</summary>
    public static string Describe(AdviceReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{r.ModName} (mod {r.ModId}) — {r.Url}");
        sb.AppendLine($"read {r.ReadDate} (local); page last updated {(r.PageUpdatedDate is { Length: > 0 } ? r.PageUpdatedDate + " UTC" : "(unknown)")}; " +
                      $"newest MAIN file {(r.NewestMainVersion is { Length: > 0 } ? "v" + r.NewestMainVersion : "(version unknown)")}" +
                      $"{(r.NewestMainDate is { Length: > 0 } ? $" uploaded {r.NewestMainDate} UTC" : "")}");
        sb.AppendLine();
        if (r.NothingStated is not null) sb.AppendLine("  " + r.NothingStated);
        sb.AppendLine($"  statements found: {r.Statements.Count}");
        foreach (var s in r.Statements)
        {
            sb.AppendLine($"     [{Shape(s.Shape)}] {s.Text}");
            if (s.Symptom is { Length: > 0 }) sb.AppendLine($"        symptom stated: \"{s.Symptom}\"");
        }
        sb.AppendLine($"  rules emitted: {r.Rules.Count}");
        foreach (var x in r.Rules)
        {
            sb.AppendLine($"     advice: '{x.Before}' before '{x.After}'");
            sb.AppendLine($"        reason: {x.Reason}");
            sb.AppendLine($"        provenance: {x.Source} (checked {x.Checked}; scope: {x.VersionScope})" +
                          (x.Note is null ? "" : $" — {x.Note}"));
            foreach (var c in x.Chain) sb.AppendLine($"        chain: {c}");
        }
        sb.AppendLine($"  notes / refusals: {r.Notes.Count}");
        foreach (var n in r.Notes) sb.AppendLine($"     {n}");
        sb.AppendLine($"  staleness: {r.StalenessLines.Count} line(s)");
        foreach (var s in r.StalenessLines) sb.AppendLine($"     {s}");
        return sb.ToString();
    }
}
