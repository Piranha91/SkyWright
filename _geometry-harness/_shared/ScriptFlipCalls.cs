using System.Text.RegularExpressions;

namespace HarnessShared;

// THE ENABLE/DISABLE CALLS OF A DECOMPILED SCRIPT — one reader for switchprobe's trigger map and the --targets reader
// (TargetTriggers). Every call is kept with its name; the two families mean different things (Papyrus reference):
//   a FLIP changes the object it is called on: ObjectReference.Enable / Disable and their NoWait forms, and
//     ReferenceAlias.TryToEnable / TryToDisable and their NoWait forms (they flip the alias's reference);
//   a LINK CHAIN call changes the references linked, in a chain, to the object, not the object itself:
//     ObjectReference.EnableLinkChain / DisableLinkChain.
// The object is the token before the dot (an index on it allowed: EnableList[i].Enable()); Self.<prop> gives the property;
// Alias_X.GetReference().Enable() gives Alias_X; a local assigned from a property resolves to it, three deep; a bare call
// is on Self; GetLinkedRef().Enable() is on GetLinkedRef. A function the decompiler refused prints its raw bytecode as
// comment lines; the Raw rule reads "; CallMethod <name> <object>" from them.
public sealed class ScriptFlipCalls
{
    public readonly List<(string Function, string Object, string Call, string Text)> Calls = new();
    public int FailedFunctions;
    /// <summary>A bare flip on Self (no object before the call).</summary>
    public bool SelfCall;

    /// <summary>Calls that flip the object they are called on (lower case).</summary>
    public static readonly IReadOnlySet<string> Flip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "enable", "disable", "enablenowait", "disablenowait", "trytoenable", "trytodisable", "trytoenablenowait", "trytodisablenowait" };
    /// <summary>Calls that flip the object's linked chain, not the object (lower case).</summary>
    public static readonly IReadOnlySet<string> LinkChain = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "enablelinkchain", "disablelinkchain" };
    public static bool Enables(string call) => call is "enable" or "enablenowait" or "trytoenable" or "trytoenablenowait";
    public static bool Disables(string call) => call is "disable" or "disablenowait" or "trytodisable" or "trytodisablenowait";

    const string Names = "TryToEnableNoWait|TryToDisableNoWait|TryToEnable|TryToDisable|EnableLinkChain|DisableLinkChain|EnableNoWait|DisableNoWait|Enable|Disable";
    static readonly Regex Header = new(@"^\s*(?:[\w\[\]:]+\s+)?(?:Function|Event)\s+([\w:]+)\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex End = new(@"^\s*End(?:Function|Event)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Call = new(@"(?<![\w.])(?:Self\.)?(?<obj>[A-Za-z_]\w*)(?:\[[^\]]*\])?(?:\.(?:GetReference|GetRef)\(\))?\.(?<call>" + Names + @")\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex LinkedCall = new(@"(?<![\w.])(?:Self\.)?GetLinkedRef\([^)]*\)\.(?<call>" + Names + @")\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex SelfCallRx = new(@"(?<![\w.])(?:Self\.)?(?<call>" + Names + @")\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // A local that aliases a property (or an element of a list property): `objectreference x = Prop[i]`, `x = Prop`.
    static readonly Regex Alias = new(@"^\s*(?:[\w\[\]:]+\s+)?(?<local>[A-Za-z_]\w*)\s*=\s*(?<src>[A-Za-z_]\w*)(?:\[[^\]]*\])?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Raw = new(@"^\s*;\s+CallMethod\s+(?<call>" + Names + @")\s+(?<obj>\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static ScriptFlipCalls Parse(string source, int failed = 0)
    {
        var sc = new ScriptFlipCalls { FailedFunctions = failed };
        string fn = "(top)";
        // Per function: local -> every property it is assigned from; chains resolved at lookup, three deep.
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
            if (!any && SelfCallRx.Match(code) is { Success: true } sm)
            {
                var call = sm.Groups["call"].Value.ToLowerInvariant();
                sc.Calls.Add((fn, "Self", call, "Self." + sm.Groups["call"].Value + "()"));
                if (Flip.Contains(call)) sc.SelfCall = true;
            }
        }
        return sc;
    }

    /// <summary>Every call made on one bound property (case-insensitive), or on Self when the property is "Self".</summary>
    public IEnumerable<(string Function, string Call, string Text)> CallsOn(string property)
    {
        foreach (var (fn, obj, call, text) in Calls)
            if (string.Equals(obj, property, StringComparison.OrdinalIgnoreCase)) yield return (fn, call, text);
    }

    /// <summary>The calls on one bound property that flip it (<see cref="Flip"/>); a link-chain call does not.</summary>
    public IEnumerable<(string Function, string Text)> FlipsOn(string property)
    {
        foreach (var (fn, call, text) in CallsOn(property))
            if (Flip.Contains(call)) yield return (fn, text);
    }
}
