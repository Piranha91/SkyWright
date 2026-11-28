using System.Globalization;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace SkyWright.Engine.Tests;

/// <summary>
/// An independent whole-order replay to check the shared world read against, written for the tests and kept apart from
/// the reader's code: every plugin is read whole with <see cref="PlacementExtract.FromMod"/>, every record of every
/// reference is kept as a sighting in load order, and the last sighting wins. A view of the world is a rule saying which
/// sightings are "in the area"; a reference with any sighting in the area is answered from its last sighting.
/// </summary>
public sealed class WorldReadOracle
{
    public sealed record Sighting(int Plugin, string PluginName, FormKey Cell, PlacedRef? Ref, bool Persistent, bool Deleted);

    /// <summary>reference -> every record of it in load order.</summary>
    public readonly Dictionary<FormKey, List<Sighting>> Sightings = new();
    /// <summary>cell -> the references some plugin files under it.</summary>
    public readonly Dictionary<FormKey, HashSet<FormKey>> ByCell = new();

    public static WorldReadOracle Replay(IReadOnlyList<string> orderedPaths)
    {
        var o = new WorldReadOracle();
        for (int p = 0; p < orderedPaths.Count; p++)
        {
            string name = Path.GetFileName(orderedPaths[p]);
            using var mod = SkyrimMod.CreateFromBinaryOverlay(orderedPaths[p], SkyrimRelease.SkyrimSE, HousecarlCore.PluginTextEncoding.ReadFor(orderedPaths[p]));
            foreach (var cp in PlacementExtract.FromMod(mod, includeEmpty: true, includeDeletions: true))
            {
                foreach (var r in cp.Refs) o.Add(r.FormKey, new Sighting(p, name, cp.Cell, r, r.Persistent, r.Deleted));
                foreach (var d in cp.DeletedRefs ?? Array.Empty<DeletedRef>()) o.Add(d.FormKey, new Sighting(p, name, cp.Cell, null, d.Persistent, true));
            }
        }
        return o;
    }

    void Add(FormKey key, Sighting s)
    {
        if (!Sightings.TryGetValue(key, out var list)) Sightings[key] = list = new List<Sighting>(1);
        list.Add(s);
        if (!ByCell.TryGetValue(s.Cell, out var set)) ByCell[s.Cell] = set = new HashSet<FormKey>();
        set.Add(key);
    }

    /// <summary>The expected answer as sorted canonical lines, for the references whose sightings include one in the area.</summary>
    public List<string> Expected(IEnumerable<FormKey> candidates, Func<Sighting, bool> inArea)
    {
        var lines = new List<string>();
        foreach (var key in candidates.Distinct())
        {
            var all = Sightings[key];
            var lastIn = all.LastOrDefault(inArea);
            if (lastIn is null) continue;
            var w = all[^1];
            if (w.Deleted) lines.Add("D " + Line(key, w));
            else if (w.Ref is null) lines.Add("U " + Line(key, w));
            else if (inArea(w)) lines.Add("W " + Line(key, w));
            else lines.Add($"M {key} from p{lastIn.Plugin} {lastIn.PluginName} c{lastIn.Cell} -> {Line(key, w)}");
        }
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    static string Line(FormKey key, Sighting s) => $"{key} p{s.Plugin} {s.PluginName} c{s.Cell} pers{s.Persistent} del{s.Deleted} {Canon(s.Ref)}";

    /// <summary>The reader's answer as the same sorted canonical lines.</summary>
    public static List<string> Lines(WorldAnswer a)
    {
        var lines = new List<string>();
        foreach (var w in a.Winners) lines.Add("W " + Line(w));
        foreach (var w in a.Deleted) lines.Add("D " + Line(w));
        foreach (var w in a.Unplaced) lines.Add("U " + Line(w));
        foreach (var m in a.MovedOut) lines.Add($"M {m.Reference} from p{m.FromPlugin} {m.FromPluginName} c{m.FromCell} -> {Line(m.Winner)}");
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    static string Line(WorldRef w) => $"{w.Reference} p{w.Plugin} {w.PluginName} c{w.Cell} pers{w.Persistent} del{w.Deleted} {Canon(w.Ref)}";

    static string Bits(float f) => BitConverter.SingleToInt32Bits(f).ToString("X8", CultureInfo.InvariantCulture);
    static string V(Vec3 v) => $"{Bits(v.X)},{Bits(v.Y)},{Bits(v.Z)}";
    static string K(FormKey? k) => k is { } x ? x.ToString() : "null";

    /// <summary>Every field of a placed record, floats by their bits, lists with null kept apart from empty.</summary>
    public static string Canon(PlacedRef? r)
    {
        if (r is null) return "-";
        var sb = new StringBuilder();
        sb.Append(r.FormKey).Append('|').Append(r.RecordType).Append('|').Append(r.EditorId ?? "null").Append('|')
          .Append(V(r.Position)).Append('|').Append(V(r.Rotation)).Append('|').Append(Bits(r.Scale)).Append('|')
          .Append(K(r.Base)).Append('|').Append(r.Persistent).Append('|').Append(r.InitiallyDisabled).Append('|').Append(r.Deleted).Append('|')
          .Append(K(r.EnableParent)).Append('|').Append(r.EnableParentOpposite).Append('|');
        sb.Append(r.LinkedReferences is null ? "null" : "[" + string.Join(";", r.LinkedReferences.Select(l => $"{l.Reference}/{K(l.Keyword)}")) + "]").Append('|');
        sb.Append(r.ActivateParents is null ? "null" : "[" + string.Join(";", r.ActivateParents.Select(a => $"{a.Reference}/{Bits(a.Delay)}")) + "]").Append('|');
        sb.Append(r.Primitive is { } pr ? $"{pr.Type}/{V(pr.Bounds)}" : "null").Append('|')
          .Append(K(r.EncounterZone)).Append('|').Append(r.CollisionLayerIndex?.ToString(CultureInfo.InvariantCulture) ?? "null").Append('|')
          .Append(r.OpenByDefault).Append('|').Append(r.RecordFlags.ToString("X8", CultureInfo.InvariantCulture)).Append('|');
        sb.Append(r.Scripts is null ? "null" : "[" + string.Join(";", r.Scripts) + "]").Append('|')
          .Append(K(r.ArmBase)).Append('|').Append(r.ArmBaseLink ?? "null");
        return sb.ToString();
    }

    /// <summary>The exterior grid square a position stands in.</summary>
    public static (int X, int Y) GridOf(Vec3 p) => ((int)MathF.Floor(p.X / 4096f), (int)MathF.Floor(p.Y / 4096f));
}
