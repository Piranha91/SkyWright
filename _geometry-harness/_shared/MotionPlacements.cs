using System.Reflection;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HarnessShared;

// WHAT THE PLACEMENT TELLS THE MOTION READER (animated motion follow-ups, items 3 and 4; decisions E3, E4). The NIF
// cannot say which record type places it, whether the reference starts open, or whether two references are linked so
// that their animations may run together. The winning placed records can, and this file reads them from OverlapIndex:
//   Of        - the base record's signature and the reference's open-by-default flag (ONAM), winner decides;
//   Coupling  - a link between two references the files show: a direct XESP / XLKR / XAPR either way, or one hop to a
//               common record (both name the same activate parent, or the same linked-reference target). Null when the
//               files show none; a script can still couple them, and the motion result says so.
//
// STATUS: API frozen at the Gate B red gate; implemented.
public static class MotionPlacements
{
    /// <summary>The motion reader's placement context for a reference, or null when the index has no base for it.</summary>
    public static MotionPlacement? Of(OverlapIndex index, FormKey reference)
    {
        if (!index.RefBase.TryGetValue(reference, out var basis) || !index.BaseInfo.TryGetValue(basis, out var bi)) return null;
        return new MotionPlacement(Signature(bi.Type), index.RefOpenByDefault.Contains(reference));
    }

    static readonly Dictionary<string, string> SigCache = new();

    /// <summary>The base record's signature (MSTT, DOOR, ACTI...) from Mutagen's registration, by the index's class name.</summary>
    public static string Signature(string className)
    {
        lock (SigCache)
        {
            if (SigCache.TryGetValue(className, out var hit)) return hit;
            string sig = className;
            var t = typeof(Mutagen.Bethesda.Skyrim.SkyrimMod).Assembly.GetType("Mutagen.Bethesda.Skyrim." + className);
            try
            {
                var reg = t?.GetProperty("StaticRegistration", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var f = reg?.GetType().GetField("TriggeringRecordType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (f?.GetValue(reg) is RecordType rt) sig = rt.ToString();
                else if (t?.GetField("GrupRecordType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is RecordType g) sig = g.ToString();
            }
            catch { }
            SigCache[className] = sig;
            return sig;
        }
    }

    /// <summary>The link the winning placed records show between two references, in words naming the subrecord and the
    /// record(s) involved; null when there is none in this scope.</summary>
    public static string? Coupling(OverlapIndex index, FormKey a, FormKey b)
    {
        var la = index.RefLinkedRefs.GetValueOrDefault(a)?.Select(l => l.Reference).ToHashSet() ?? new HashSet<FormKey>();
        var lb = index.RefLinkedRefs.GetValueOrDefault(b)?.Select(l => l.Reference).ToHashSet() ?? new HashSet<FormKey>();
        var pa = index.RefActivateParents.GetValueOrDefault(a)?.Select(p => p.Reference).ToHashSet() ?? new HashSet<FormKey>();
        var pb = index.RefActivateParents.GetValueOrDefault(b)?.Select(p => p.Reference).ToHashSet() ?? new HashSet<FormKey>();
        FormKey? Xesp(FormKey k) => index.EnableChanges.TryGetValue(k, out var ec) && ec.Count > 0 ? ec[^1].Parent : null;
        var found = new List<string>();
        if (la.Contains(b)) found.Add($"XLKR: {a} links {b}");
        if (lb.Contains(a)) found.Add($"XLKR: {b} links {a}");
        if (pa.Contains(b)) found.Add($"XAPR: {b} is an activate parent of {a}");
        if (pb.Contains(a)) found.Add($"XAPR: {a} is an activate parent of {b}");
        if (Xesp(a) == b) found.Add($"XESP: {b} is the enable parent of {a}");
        if (Xesp(b) == a) found.Add($"XESP: {a} is the enable parent of {b}");
        foreach (var p in pa.Intersect(pb).OrderBy(k => k.ToString())) found.Add($"XAPR: both name activate parent {p}");
        foreach (var t in la.Intersect(lb).OrderBy(k => k.ToString())) found.Add($"XLKR: both link to {t}");
        return found.Count == 0 ? null : string.Join("; ", found);
    }
}
