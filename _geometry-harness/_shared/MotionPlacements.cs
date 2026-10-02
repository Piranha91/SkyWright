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
// STATUS: API frozen at the Gate B red gate; Of implemented (item 3), Coupling a stub until item 4.
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
        => throw new NotImplementedException("MotionPlacements.Coupling (Gate B stub)");
}
