using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HarnessShared;

// THE BOS HOOK — where the Base Object Swapper reader meets the shared indexes.
//
// Every geometry detector resolves a reference through its RECORD base: OverlapIndex.RefBase feeds
// the overlap and contact passes, DependencyIndex.RefBase the records pass, and the meshes, bounds,
// collision and world boxes all follow from it. Under BOS the game loads a different object, sometimes
// at a different position and scale, so all of that is computed against a thing that is not there.
// This applies the reader's answer to the indexes AFTER the load order is ingested and BEFORE any
// pass reads them, as one more step in each reference's story:
//
//   - the effective base replaces RefBase (the record base is kept in RecordBase), and BaseChanges
//     gains an entry from the BOS pseudo-plugin, so a previous snap is still rebuilt with the base
//     it had then;
//   - a transform override that moves the reference (or flips its initially-disabled flag) is a
//     PLACEMENT STEP of a new kind, recorded in the history under the BOS pseudo-plugin index the
//     caller reserves after the last real plugin — the mover discipline then sees BOS as a mover,
//     and the arm without it is the arm without BOS. A rotation- or scale-only override is not a
//     step, on the history's standing rule, and is counted (SilentTransforms) like the plugin
//     overrides of the same shape;
//   - nothing is rolled: a Random resolution takes the reader's documented First and is tallied.
//
// The pseudo-plugin needs a master set in the caller's closure: BOS applies on top of the winner,
// so it "sees" every plugin — give it the full index set. Its name is not a plugin's; callers label
// it (BosHook.PseudoPluginName).

public sealed class BosApplyReport
{
    public const string PseudoPluginName = "BOS (*_SWAP.ini)";
    public int Considered, NotSwappable, NoBase, NoCell, Swapped, Random, PerSession, Leveled, WithProperties, TransformSteps, SilentTransforms, FlagChanges;
    public readonly Dictionary<string, int> SwapsByIni = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, int> PropertiesByIni = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, int> NewBaseTypes = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Notes = new();

    public IEnumerable<string> Summary()
    {
        yield return $"BOS: {Swapped:N0} of {Considered:N0} winning refs get a different base ({Random:N0} random — the first alternative is drawn; {PerSession:N0} per-session; {Leveled:N0} via a leveled item), "
                   + $"{WithProperties:N0} carry a property override ({TransformSteps:N0} placement steps recorded, {SilentTransforms:N0} rotation/scale-only not recorded, {FlagChanges:N0} flag changes); "
                   + $"{NotSwappable:N0} refs on arms BOS never swaps (actors), {NoCell:N0} without a cell context, {NoBase:N0} without a base";
        foreach (var kv in SwapsByIni.OrderByDescending(k => k.Value).Take(15))
            yield return $"   {kv.Value,7:N0} swaps from {kv.Key}";
        if (SwapsByIni.Count > 15) yield return $"   ... and {SwapsByIni.Count - 15} more ini(s)";
        foreach (var kv in PropertiesByIni.OrderByDescending(k => k.Value).Take(8))
            yield return $"   {kv.Value,7:N0} property overrides from {kv.Key}";
    }
}

public static class BosHook
{
    /// <summary>Read the discovered inis against the index the caller filled during ingest (OverlapIndex.Bos /
    /// DependencyIndex.Bos), after <paramref name="index"/>.SetLoadOrder.</summary>
    public static BosLayer Load(IReadOnlyList<(string Name, string Dir)> looseRoots, BosFormIndex index)
        => BaseObjectSwaps.Load(BosDiscovery.Discover(looseRoots), index);

    /// <summary>Whether a plugin is a light master the way the game decides it: the header flag, or the .esl extension.</summary>
    public static bool IsLight(ISkyrimModGetter mod) => mod.IsSmallMaster || mod.ModKey.Type == ModType.Light;

    /// <summary>The VFS roots in precedence order for a probe that builds no AssetResolver (the records-only passes):
    /// overwrite, the enabled mods highest-priority-first, the game Data folder — AssetResolver.LooseRoots' rule.</summary>
    public static IReadOnlyList<(string Name, string Dir)> LooseRoots(string overwriteDir, string modsDir, string dataDir, IReadOnlyList<string> enabledMods)
    {
        var roots = new List<(string, string)>(enabledMods.Count + 2);
        if (overwriteDir.Length > 0) roots.Add(("overwrite", overwriteDir));
        foreach (var m in enabledMods) roots.Add((m, Path.Combine(modsDir, m)));
        if (dataDir.Length > 0) roots.Add(("Data", dataDir));
        return roots;
    }

    /// <summary>Apply the layer to the overlap/contact index. <paramref name="bosPlugin"/> is the pseudo-plugin index
    /// (the count of real plugins) the caller has also given a full master set in its closure.</summary>
    public static BosApplyReport Apply(OverlapIndex idx, BosLayer layer, int bosPlugin)
    {
        var rep = new BosApplyReport();
        if (idx.Bos is null) { rep.Notes.Add("the index carries no BOS form index — nothing applied"); return rep; }
        idx.BosPlugin = bosPlugin;
        var keys = idx.History.Refs.Select(kv => kv.Key).ToList();
        foreach (var key in keys)
        {
            var win = idx.History.Winning(key);
            if (win is not { } w || w.Deleted) continue;
            rep.Considered++;
            if (!idx.RefArm.TryGetValue(key, out var arm) || !BaseObjectSwaps.Swappable(arm)) { rep.NotSwappable++; continue; }
            if (!idx.RefBase.TryGetValue(key, out var recordBase)) { rep.NoBase++; continue; }
            if (!idx.RefCell.TryGetValue(key, out var where)) { rep.NoCell++; continue; }
            idx.RefEncounterZone.TryGetValue(key, out var ez);
            var res = BaseObjectSwaps.Resolve(layer, idx.Bos, key, arm, recordBase, where.Cell, ez == default ? null : ez);
            if (!res.Swapped && res.Properties is null && !res.Random) continue;
            idx.BosSwaps[key] = res;
            if (res.Random) rep.Random++;
            if (res.PerSession) rep.PerSession++;
            if (res.Leveled) rep.Leveled++;
            if (res.Swapped)
            {
                rep.Swapped++;
                idx.RecordBase[key] = recordBase;
                idx.RefBase[key] = res.EffectiveBase;
                if (!idx.BaseChanges.TryGetValue(key, out var changes)) idx.BaseChanges[key] = changes = new List<(int, FormKey)>(1);
                changes.Add((bosPlugin, res.EffectiveBase));
                if (res.SwapFrom is { } from) rep.SwapsByIni[from.Where.Ini] = rep.SwapsByIni.GetValueOrDefault(from.Where.Ini) + 1;
                if (idx.BaseInfo.TryGetValue(res.EffectiveBase, out var bi)) rep.NewBaseTypes[bi.Type] = rep.NewBaseTypes.GetValueOrDefault(bi.Type) + 1;
            }
            if (res.Properties is { IsValid: true })
            {
                rep.WithProperties++;
                if (res.PropertiesFrom is { } pf) rep.PropertiesByIni[pf.Ini] = rep.PropertiesByIni.GetValueOrDefault(pf.Ini) + 1;
                var (pos, rot, scale, disabled) = res.Apply(w.Pos, w.Rot, w.Scale, w.Disabled);
                if (disabled != w.Disabled) rep.FlagChanges++;
                var snap = new Snap(bosPlugin, pos, w.Cell, disabled, w.Deleted, w.Worldspace, rot, scale);
                if (!w.SameAs(snap)) { idx.History.Record(key, snap); rep.TransformSteps++; }
                else if (MathF.Abs(w.Rot.X - rot.X) > 1e-4f || MathF.Abs(w.Rot.Y - rot.Y) > 1e-4f || MathF.Abs(w.Rot.Z - rot.Z) > 1e-4f || MathF.Abs(w.Scale - scale) > 1e-3f)
                    rep.SilentTransforms++;
            }
        }
        return rep;
    }

    /// <summary>Apply the layer to the dependency index (the records pass): the same base substitution and history step.</summary>
    public static BosApplyReport Apply(DependencyIndex idx, BosLayer layer, int bosPlugin)
    {
        var rep = new BosApplyReport();
        if (idx.Bos is null) { rep.Notes.Add("the index carries no BOS form index — nothing applied"); return rep; }
        idx.BosPlugin = bosPlugin;
        var keys = idx.History.Refs.Select(kv => kv.Key).ToList();
        foreach (var key in keys)
        {
            var win = idx.History.Winning(key);
            if (win is not { } w || w.Deleted) continue;
            rep.Considered++;
            if (!idx.RefInfo.TryGetValue(key, out var info) || !BaseObjectSwaps.Swappable(info.Type)) { rep.NotSwappable++; continue; }
            if (!idx.RefBase.TryGetValue(key, out var recordBase)) { rep.NoBase++; continue; }
            if (!idx.RefCell.TryGetValue(key, out var where)) { rep.NoCell++; continue; }
            var res = BaseObjectSwaps.Resolve(layer, idx.Bos, key, info.Type, recordBase, where.Cell, null);
            if (!res.Swapped && res.Properties is null && !res.Random) continue;
            idx.BosSwaps[key] = res;
            if (res.Random) rep.Random++;
            if (res.PerSession) rep.PerSession++;
            if (res.Leveled) rep.Leveled++;
            if (res.Swapped)
            {
                rep.Swapped++;
                idx.RecordBase[key] = recordBase;
                idx.RefBase[key] = res.EffectiveBase;
                if (res.SwapFrom is { } from) rep.SwapsByIni[from.Where.Ini] = rep.SwapsByIni.GetValueOrDefault(from.Where.Ini) + 1;
            }
            if (res.Properties is { IsValid: true })
            {
                rep.WithProperties++;
                if (res.PropertiesFrom is { } pf) rep.PropertiesByIni[pf.Ini] = rep.PropertiesByIni.GetValueOrDefault(pf.Ini) + 1;
                var (pos, rot, scale, disabled) = res.Apply(w.Pos, w.Rot, w.Scale, w.Disabled);
                if (disabled != w.Disabled) rep.FlagChanges++;
                var snap = new Snap(bosPlugin, pos, w.Cell, disabled, w.Deleted, w.Worldspace, rot, scale);
                if (!w.SameAs(snap)) { idx.History.Record(key, snap); rep.TransformSteps++; }
                else if (MathF.Abs(w.Rot.X - rot.X) > 1e-4f || MathF.Abs(w.Rot.Y - rot.Y) > 1e-4f || MathF.Abs(w.Rot.Z - rot.Z) > 1e-4f || MathF.Abs(w.Scale - scale) > 1e-3f)
                    rep.SilentTransforms++;
            }
        }
        return rep;
    }

    /// <summary>The closure entry for the pseudo-plugin: every real plugin index (BOS applies on top of the winner).</summary>
    public static HashSet<int> PseudoMasters(int pluginCount) => new(Enumerable.Range(0, pluginCount));

    /// <summary>Parse the shared `--bos on|off` flag value; null when the text is neither.</summary>
    public static bool? ParseFlag(string value) => value.ToLowerInvariant() switch { "on" => true, "off" => false, _ => null };
}
