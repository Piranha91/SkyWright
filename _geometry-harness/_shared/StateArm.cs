using System.IO;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

namespace HarnessShared;

// THE STATE ARM — the dependency-free half (roadmap 4.11): a named set of switches flipped from their
// baseline, and the wrap that makes EnableState resolve the flipped world. Parse (by editor id, FormKey
// or quest stage) and Changes (what the flip changes) read the census and live in WorldState.cs as
// the other half of the partial class; this file compiles against housecarl-core alone, so viewprobe
// takes the arm without the index stack.

/// <summary>One state of the world: a named set of switches flipped from their baseline. Applied by wrapping the
/// caller's enable facts so EnableState resolves the flipped world.</summary>
public sealed partial class StateArm
{
    public string Name { get; }
    public IReadOnlyList<FormKey> Flips { get; }
    public bool IsBaseline => Flips.Count == 0;
    public const string BaselineName = "baseline";

    public StateArm(string name, IReadOnlyList<FormKey> flips) { Name = name; Flips = flips; }

    public static StateArm Baseline() => new(BaselineName, Array.Empty<FormKey>());

    /// <summary>Wrap the caller's enable facts: a flipped switch reads as a parentless reference whose flag is the
    /// opposite of its baseline state — Enable()/Disable() on the marker itself. Everything else is the caller's.</summary>
    public Func<FormKey, EnableFacts?> Wrap(Func<FormKey, EnableFacts?> facts, Func<FormKey, EnableResolution> baseline)
    {
        if (Flips.Count == 0) return facts;
        var flipped = new HashSet<FormKey>(Flips);
        return k =>
        {
            if (!flipped.Contains(k)) return facts(k);
            var b = baseline(k);
            return new EnableFacts(InitiallyDisabled: b.Enabled, Parent: null, Opposite: false);
        };
    }
}

/// <summary>The part of switchprobe's JSON another probe reads: the joint flips per quest stage (Part B's trigger map)
/// and each switch's trigger status. Stage keys are <c>&lt;quest editor id&gt;:&lt;stage&gt;</c>; values the switches
/// (editor id or FormKey text) the stage enables or disables.</summary>
public sealed class SwitchMap
{
    public Dictionary<string, List<string>> Stages { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>switch FormKey text -> (Triggered | BoundUnread | Unknown, the kind) from switchprobe's trigger map.</summary>
    public Dictionary<string, (string Status, string Kind)> Triggers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public (string Status, string Kind) TriggerOf(FormKey k) => Triggers.TryGetValue(k.ToString(), out var t) ? t : ("(no map)", "");

    public IReadOnlyList<string>? StageFlips(string questAndStage)
        => Stages.TryGetValue(questAndStage, out var l) && l.Count > 0 ? l : null;

    public static SwitchMap Read(string path)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var map = new SwitchMap();
        if (doc.RootElement.TryGetProperty("switches", out var sws) && sws.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var e in sws.EnumerateArray())
                if (e.TryGetProperty("trigger", out var tr) && tr.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty("key", out var kk))
                    map.Triggers[kk.GetString() ?? ""] = (tr.GetProperty("status").GetString() ?? "?", tr.TryGetProperty("kind", out var kd) ? kd.GetString() ?? "" : "");
        if (doc.RootElement.TryGetProperty("jointFlips", out var jf) && jf.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var e in jf.EnumerateArray())
            {
                string key = e.GetProperty("stage").GetString() ?? "";
                var l = new List<string>();
                foreach (var t in e.GetProperty("switches").EnumerateArray()) if (t.GetString() is { } s) l.Add(s);
                map.Stages[key] = l;
            }
        return map;
    }
}
