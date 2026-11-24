using System.Text.Json;
using NiflySharp;
using NiflySharp.Blocks;

using HousecarlCore;

namespace SkyWright.Engine;

/// <summary>One load-settle exemption, as data: a property kind, the value that matches, and the lab evidence behind it.
/// Kinds the reader interprets: <c>refRecordFlag</c> (a REFR record-flag mask, hex), <c>bsxFlags</c> (the model's BSX bits and
/// behaviour graph), <c>enabledAfterLoad</c> (a reference the judged state enables that is disabled at load). A new exemption is
/// a new entry, not a new rule.</summary>
public sealed record SettleExemption(string Kind, string Value, string Evidence);

/// <summary>What the reader knows about one reference: its winning REFR record flags, whether it is enabled at load (false:
/// disabled at load and enabled by the judged state), and its model's BSX bits and behaviour graph.</summary>
public sealed record SettleFacts(uint RefRecordFlags, bool EnabledAtLoad, uint? BsxFlags = null, bool? HasBehaviorGraph = null);

/// <summary>The rule's answer: <c>settles</c> (the engine settles it when the cell loads: dropped), <c>rests</c> (an
/// exemption holds it at its placed pose until disturbed), <c>timing-unknown</c> (enabled by the judged state; whether
/// before or after the cell loaded is not in the data: placed and dropped poses both held).</summary>
public sealed record SettleDecision(string Status, SettleExemption? Exemption);

/// <summary>The drop rule as one rule: an object drops only if the engine settles it when the cell loads; otherwise it
/// rests at its placed pose until disturbed. Which objects are not settled is a data-driven exemption list.</summary>
public static class LoadSettle
{
    public const string RestsLabel = "rests where placed (measured: not settled at load; falls when disturbed)";
    public const string TimingUnknownLabel = "enable timing unknown: enabled by the judged state, possibly after the cell loaded; placed and dropped poses both held";
    public const string SettlesLabel = "drop: settles at cell load (measured on a crossbow, a one-handed sword and an ingredient)";

    /// <summary>The exemption list from its JSON: an array of {kind, value, evidence}. An unknown kind fails loud.</summary>
    public static IReadOnlyList<SettleExemption> Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<SettleExemption>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var x = new SettleExemption(e.GetProperty("kind").GetString()!, e.GetProperty("value").GetString()!, e.GetProperty("evidence").GetString()!);
            if (x.Kind is not ("refRecordFlag" or "bsxFlags" or "enabledAfterLoad"))
                throw new ArgumentException($"load-settle exemption: unknown kind '{x.Kind}'");
            list.Add(x);
        }
        return list;
    }

    /// <summary>The decision for one reference under the list. Property exemptions (record flag, BSX bits) come
    /// first: they hold the object whenever it was enabled. The timing exemption applies to a reference disabled at load and
    /// enabled by the judged state; the data cannot say whether that happened before the cell loaded, so the answer is
    /// timing-unknown, never a plain hold. No matching entry: settles.</summary>
    public static SettleDecision Decide(SettleFacts facts, IReadOnlyList<SettleExemption> exemptions)
    {
        foreach (var e in exemptions)
        {
            if (e.Kind == "refRecordFlag" && (facts.RefRecordFlags & ParseMask(e.Value)) != 0) return new("rests", e);
            if (e.Kind == "bsxFlags" && facts.BsxFlags is { } v)
            {
                var (set, clear, graph) = ParseBsx(e.Value);
                if ((v & set) == set && (v & clear) == 0 && (graph is null || facts.HasBehaviorGraph == graph)) return new("rests", e);
            }
        }
        foreach (var e in exemptions)
            if (e.Kind == "enabledAfterLoad" && !facts.EnabledAtLoad) return new("timing-unknown", e);
        return new("settles", null);
    }

    /// <summary>A bsxFlags value: "set=MASK;clear=MASK;graph=present|absent" (hex or decimal masks; any part may be omitted;
    /// graph names whether the model must carry a BSBehaviorGraphExtraData block).</summary>
    static (uint Set, uint Clear, bool? Graph) ParseBsx(string v)
    {
        uint set = 0, clear = 0; bool? graph = null;
        foreach (var part in v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) throw new ArgumentException($"load-settle exemption: bsxFlags value '{v}' is not set=MASK;clear=MASK");
            if (kv[0] == "set") set = ParseMask(kv[1]);
            else if (kv[0] == "clear") clear = ParseMask(kv[1]);
            else if (kv[0] == "graph" && kv[1] is "present" or "absent") graph = kv[1] == "present";
            else throw new ArgumentException($"load-settle exemption: bsxFlags part '{part}' is not set=, clear= or graph=present|absent");
        }
        return (set, clear, graph);
    }

    /// <summary>The model's BSXFlags integer (the first BSXFlags block, null when none) and whether it carries a behaviour
    /// graph (a BSBehaviorGraphExtraData block).</summary>
    public static (uint? Bsx, bool Graph) NifFacts(byte[] nif)
    {
        var f = new NifFile();
        using (var ms = new MemoryStream(nif, writable: false))
            if (f.Load(ms) != 0) throw new InvalidDataException("the NIF could not be read");
        return (f.Blocks.OfType<BSXFlags>().Select(b => (uint?)b.IntegerData).FirstOrDefault(), f.Blocks.OfType<BSBehaviorGraphExtraData>().Any());
    }

    static uint ParseMask(string v)
        => v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToUInt32(v[2..], 16) : uint.Parse(v);
}
