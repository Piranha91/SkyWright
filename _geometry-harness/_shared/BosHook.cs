using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
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
//
// Two ways in (the second added 2026-09-04, for the route rule and driftprobe):
//   - a probe on a shared index (overlap, contact, dependency) calls Apply(index, layer, bosPlugin)
//     after ingest;
//   - a probe that walks the order ITSELF feeds a BosFeed during that walk — one call per plugin,
//     per record and per cell, the same three notes the indexes make — loads the layer once after,
//     and then either asks Effective(ref) per reference for the base and transform the game loads
//     (blockprobe, navprobe, corridorprobe: the transform is applied to the geometry directly) or
//     calls Apply(history, …) to step its own placement history (driftprobe). Every path goes through
//     one resolve step, so a sabotage of it reddens every hooked probe's case at once.

public sealed class BosApplyReport
{
    public const string PseudoPluginName = "BOS (*_SWAP.ini)";
    /// <summary>Set by <see cref="BosFeed.Effective"/>: the probe applies the transform to its geometry directly rather
    /// than recording a history step, and the summary says so ("applied", not "recorded").</summary>
    public bool Direct;
    public int Considered, NotSwappable, NoBase, NoCell, Swapped, Random, PerSession, Leveled, WithProperties, TransformSteps, SilentTransforms, FlagChanges;
    /// <summary>Direct only: references a flags(0x800) override disables at init — not an obstacle, skipped and counted.</summary>
    public int Disabled;
    public readonly Dictionary<string, int> SwapsByIni = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, int> PropertiesByIni = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, int> NewBaseTypes = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Notes = new();

    public IEnumerable<string> Summary()
    {
        string transforms = Direct
            ? $"{TransformSteps:N0} move or re-flag the reference and {SilentTransforms:N0} only rotate or scale it — all applied to the geometry; {Disabled:N0} disabled by a flags(0x800) override, not an obstacle"
            : $"{TransformSteps:N0} placement steps recorded, {SilentTransforms:N0} rotation/scale-only not recorded";
        yield return $"BOS: {Swapped:N0} of {Considered:N0} winning refs get a different base ({Random:N0} random — the first alternative is drawn; {PerSession:N0} per-session; {Leveled:N0} via a leveled item), "
                   + $"{WithProperties:N0} carry a property override ({transforms}, {FlagChanges:N0} flag changes); "
                   + $"{NotSwappable:N0} refs on arms BOS never swaps (actors), {NoCell:N0} without a cell context, {NoBase:N0} without a base";
        foreach (var kv in SwapsByIni.OrderByDescending(k => k.Value).Take(15))
            yield return $"   {kv.Value,7:N0} swaps from {kv.Key}";
        if (SwapsByIni.Count > 15) yield return $"   ... and {SwapsByIni.Count - 15} more ini(s)";
        foreach (var kv in PropertiesByIni.OrderByDescending(k => k.Value).Take(8))
            yield return $"   {kv.Value,7:N0} property overrides from {kv.Key}";
    }
}

/// <summary>What a reference IS under BOS, for a probe that places geometry itself: the base to look the mesh up by, the
/// transform to place it at, the initially-disabled flag after any override, and the resolution behind them (null when
/// BOS is off or left the reference alone — then every other field is the record's own).</summary>
public readonly record struct BosEffective(FormKey Base, Vec3 Position, Vec3 Rotation, float Scale, bool InitiallyDisabled,
                                           BaseObjectSwaps.BosResolution? Resolution)
{
    public bool Swapped => Resolution is { Swapped: true };
    public bool Random => Resolution is { Random: true };
    /// <summary>BOS changed something the geometry reads: the base, the transform, or the flag.</summary>
    public bool Touched => Resolution is { } r && (r.Swapped || r.Properties is { IsValid: true });
}

/// <summary>The "feed the index during the walk" step for a probe that walks the load order itself and holds no shared
/// index. Off (<see cref="On"/> false) every call is a no-op and <see cref="Effective"/> returns the record's own base and
/// transform, so a probe's `--bos off` arm is the code path it always ran, byte for byte.</summary>
public sealed class BosFeed
{
    public BosFormIndex? Index { get; }
    public BosLayer? Layer { get; private set; }
    readonly List<(ModKey Key, bool Light)> _order = new();

    public BosFeed(bool on) { Index = on ? new BosFormIndex() : null; }
    public bool On => Index is not null;

    /// <summary>Once per plugin, in load order, before its records; null for a plugin that would not open (it keeps its
    /// slot in the runtime-id table, as it does in the game's).</summary>
    public void Plugin(ISkyrimModGetter? mod)
    {
        if (Index is null) return;
        _order.Add(mod is null ? (ModKey.Null, false) : (mod.ModKey, BosHook.IsLight(mod)));
    }

    /// <summary>Every major record the walk sees (the index keeps only what the filters and ids read).</summary>
    public void Record(IMajorRecordGetter rec) => Index?.Note(rec);

    /// <summary>Every cell the walk extracts, with its references — a reference's list membership (persistent or
    /// temporary) is what an EditorID lookup at ini-load time depends on. A caller that iterates the refs itself passes
    /// <paramref name="withRefs"/> false and calls <see cref="Placed"/> per reference.</summary>
    public void Cell(CellPlacements cp, bool withRefs = true)
    {
        if (Index is null) return;
        Index.NoteCell(cp.Cell, cp.Worldspace);
        if (withRefs) foreach (var r in cp.Refs) Index.NotePlaced(r.FormKey, r.Persistent);
    }

    public void Placed(PlacedRef r) => Index?.NotePlaced(r.FormKey, r.Persistent);

    /// <summary>After the walk: the runtime-id table from the plugins fed, then the layer over the inis discovered on
    /// <paramref name="looseRoots"/> (AssetResolver.LooseRoots, or <see cref="BosHook.LooseRoots"/> for a probe that
    /// builds no resolver).</summary>
    public BosLayer Load(IReadOnlyList<(string Name, string Dir)> looseRoots)
    {
        if (Index is null) throw new InvalidOperationException("BOS is off — nothing to load");
        Index.SetLoadOrder(_order);
        return Layer = BosHook.Load(looseRoots, Index);
    }

    /// <summary>One line for a probe's summary: the layer as read, or the off notice.</summary>
    public string LayerLine() => Layer is null
        ? "BOS: off — record bases (--bos on to apply the *_SWAP.ini layer)"
        : $"BOS on: {Layer.Inis.Count} *_SWAP.ini read ({Layer.LinesRead:N0} lines, {Layer.LinesFailed:N0} the game would refuse, {Layer.Anomalies.Count:N0} anomalies — bosprobe lists them); "
        + "every ref is judged by the base and transform the game loads";

    /// <summary>The base and transform the game loads for one winning reference standing in <paramref name="cell"/>,
    /// tallied into <paramref name="rep"/> (Direct). Off, or for a reference BOS never reaches, the record's own. The
    /// transform is the override applied to the record's placement — ranges at their minimum, the reader's First; a
    /// flags(0x800) override reads back as InitiallyDisabled and is counted, so the caller skips it exactly as it skips
    /// the record's own flag. A Random resolution returns the first alternative and is counted, never a silent pick.</summary>
    public BosEffective Effective(PlacedRef r, FormKey cell, BosApplyReport rep)
    {
        var plain = new BosEffective(r.Base ?? FormKey.Null, r.Position, r.Rotation, r.Scale, r.InitiallyDisabled, null);
        if (Layer is null || Index is null) return plain;
        rep.Direct = true;
        rep.Considered++;
        if (!BaseObjectSwaps.Swappable(r.RecordType)) { rep.NotSwappable++; return plain; }
        if (r.Base is not { } recordBase) { rep.NoBase++; return plain; }
        var res = BaseObjectSwaps.Resolve(Layer, Index, r.FormKey, r.RecordType, recordBase, cell, r.EncounterZone);
        if (!res.Swapped && res.Properties is null && !res.Random) return plain;
        if (res.Random) rep.Random++;
        if (res.PerSession) rep.PerSession++;
        if (res.Leveled) rep.Leveled++;
        var bk = recordBase;
        if (res.Swapped)
        {
            rep.Swapped++;
            bk = res.EffectiveBase;
            if (res.SwapFrom is { } from) rep.SwapsByIni[from.Where.Ini] = rep.SwapsByIni.GetValueOrDefault(from.Where.Ini) + 1;
        }
        var (pos, rot, scale, disabled) = (r.Position, r.Rotation, r.Scale, r.InitiallyDisabled);
        if (res.Properties is { IsValid: true })
        {
            rep.WithProperties++;
            if (res.PropertiesFrom is { } pf) rep.PropertiesByIni[pf.Ini] = rep.PropertiesByIni.GetValueOrDefault(pf.Ini) + 1;
            (pos, rot, scale, disabled) = res.Apply(r.Position, r.Rotation, r.Scale, r.InitiallyDisabled);
            bool flag = disabled != r.InitiallyDisabled;
            if (flag) { rep.FlagChanges++; if (disabled) rep.Disabled++; }
            bool moved = MathF.Abs(pos.X - r.Position.X) >= 0.01f || MathF.Abs(pos.Y - r.Position.Y) >= 0.01f || MathF.Abs(pos.Z - r.Position.Z) >= 0.01f;
            if (moved || flag) rep.TransformSteps++;
            else if (BosHook.RotOrScaleDiffers(r.Rotation, r.Scale, rot, scale)) rep.SilentTransforms++;
        }
        return new BosEffective(bk, pos, rot, scale, disabled, res);
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
            if (!idx.RefCell.TryGetValue(key, out _)) { rep.NoCell++; continue; }
            idx.RefEncounterZone.TryGetValue(key, out var ez);
            var res = Step(idx.History, key, w, arm, recordBase, ez == default ? null : ez, idx.Bos, layer, bosPlugin, rep);
            if (res is null) continue;
            idx.BosSwaps[key] = res;
            if (res.Swapped)
            {
                idx.RecordBase[key] = recordBase;
                idx.RefBase[key] = res.EffectiveBase;
                if (!idx.BaseChanges.TryGetValue(key, out var changes)) idx.BaseChanges[key] = changes = new List<(int, FormKey)>(1);
                changes.Add((bosPlugin, res.EffectiveBase));
                if (idx.BaseInfo.TryGetValue(res.EffectiveBase, out var bi)) rep.NewBaseTypes[bi.Type] = rep.NewBaseTypes.GetValueOrDefault(bi.Type) + 1;
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
            if (!idx.RefCell.TryGetValue(key, out _)) { rep.NoCell++; continue; }
            var res = Step(idx.History, key, w, info.Type, recordBase, null, idx.Bos, layer, bosPlugin, rep);
            if (res is null) continue;
            idx.BosSwaps[key] = res;
            if (res.Swapped)
            {
                idx.RecordBase[key] = recordBase;
                idx.RefBase[key] = res.EffectiveBase;
            }
        }
        return rep;
    }

    /// <summary>Apply the layer to a probe's OWN placement history and base map (driftprobe): the same substitution and
    /// step as the index overloads. <paramref name="refArm"/> gives each reference's placed arm and
    /// <paramref name="refEncounterZone"/> its own XEZN where it has one; <paramref name="recordBase"/> receives the record
    /// base of every swapped reference and <paramref name="swaps"/> every resolution that swapped, moved or may swap.</summary>
    public static BosApplyReport Apply(PlacementHistory history, IDictionary<FormKey, FormKey> refBase,
                                       IReadOnlyDictionary<FormKey, string> refArm, IReadOnlyDictionary<FormKey, FormKey> refEncounterZone,
                                       BosFormIndex bos, BosLayer layer, int bosPlugin,
                                       IDictionary<FormKey, FormKey> recordBase, IDictionary<FormKey, BaseObjectSwaps.BosResolution> swaps)
    {
        var rep = new BosApplyReport();
        var keys = history.Refs.Select(kv => kv.Key).ToList();
        foreach (var key in keys)
        {
            var win = history.Winning(key);
            if (win is not { } w || w.Deleted) continue;
            rep.Considered++;
            if (!refArm.TryGetValue(key, out var arm) || !BaseObjectSwaps.Swappable(arm)) { rep.NotSwappable++; continue; }
            if (!refBase.TryGetValue(key, out var rb)) { rep.NoBase++; continue; }
            var res = Step(history, key, w, arm, rb, refEncounterZone.TryGetValue(key, out var ez) ? ez : null, bos, layer, bosPlugin, rep);
            if (res is null) continue;
            swaps[key] = res;
            if (res.Swapped) { recordBase[key] = rb; refBase[key] = res.EffectiveBase; }
        }
        return rep;
    }

    /// <summary>The per-reference core every Apply shares, after the caller's own checks (arm, base, cell): resolve, tally,
    /// and record a moving or re-flagging override as a history step under the pseudo-plugin. Null when BOS leaves the
    /// reference alone — nothing to keep.</summary>
    static BaseObjectSwaps.BosResolution? Step(PlacementHistory history, FormKey key, Snap w, string arm, FormKey recordBase, FormKey? ez,
                                                BosFormIndex bos, BosLayer layer, int bosPlugin, BosApplyReport rep)
    {
        var res = BaseObjectSwaps.Resolve(layer, bos, key, arm, recordBase, w.Cell, ez);
        if (!res.Swapped && res.Properties is null && !res.Random) return null;
        if (res.Random) rep.Random++;
        if (res.PerSession) rep.PerSession++;
        if (res.Leveled) rep.Leveled++;
        if (res.Swapped)
        {
            rep.Swapped++;
            if (res.SwapFrom is { } from) rep.SwapsByIni[from.Where.Ini] = rep.SwapsByIni.GetValueOrDefault(from.Where.Ini) + 1;
        }
        if (res.Properties is { IsValid: true })
        {
            rep.WithProperties++;
            if (res.PropertiesFrom is { } pf) rep.PropertiesByIni[pf.Ini] = rep.PropertiesByIni.GetValueOrDefault(pf.Ini) + 1;
            var (pos, rot, scale, disabled) = res.Apply(w.Pos, w.Rot, w.Scale, w.Disabled);
            if (disabled != w.Disabled) rep.FlagChanges++;
            var snap = new Snap(bosPlugin, pos, w.Cell, disabled, w.Deleted, w.Worldspace, rot, scale);
            if (!w.SameAs(snap)) { history.Record(key, snap); rep.TransformSteps++; }
            else if (RotOrScaleDiffers(w.Rot, w.Scale, rot, scale)) rep.SilentTransforms++;
        }
        return res;
    }

    /// <summary>The history's "silent" override test: a rotation or scale change with the position and flags unchanged.</summary>
    internal static bool RotOrScaleDiffers(Vec3 rot0, float scale0, Vec3 rot1, float scale1)
        => MathF.Abs(rot0.X - rot1.X) > 1e-4f || MathF.Abs(rot0.Y - rot1.Y) > 1e-4f || MathF.Abs(rot0.Z - rot1.Z) > 1e-4f || MathF.Abs(scale0 - scale1) > 1e-3f;

    /// <summary>One line naming what BOS did to a reference, in FormKeys: record → effective (ini:line), the random mark,
    /// and the properties an override set and where from. A probe with names of its own substitutes them.</summary>
    public static string Describe(BaseObjectSwaps.BosResolution res)
    {
        var sb = new System.Text.StringBuilder("BOS ");
        sb.Append(res.Swapped ? $"{res.RecordBase} -> {res.EffectiveBase}" : $"{res.RecordBase} kept");
        if (res.SwapFrom is { } sf) sb.Append($" ({sf.Where.Ini}:{sf.Where.Line})");
        if (res.Random) sb.Append($" [random: {res.Alternatives.Count} alternatives, the first drawn{(res.PerSession ? ", re-rolled per session" : "")}]");
        if (res.Properties is { IsValid: true } p)
            sb.Append($" [{(p.Position is not null ? "pos " : "")}{(p.Rotation is not null ? "rot " : "")}{(p.Scale is not null ? "scale " : "")}{(p.FlagsSet != 0 || p.FlagsUnset != 0 ? "flags " : "")}"
                    + $"from {res.PropertiesFrom?.Ini}:{res.PropertiesFrom?.Line}{(res.PropertiesRandom ? ", a range — minimum drawn" : "")}]");
        return sb.ToString();
    }

    /// <summary>The closure entry for the pseudo-plugin: every real plugin index (BOS applies on top of the winner).</summary>
    public static HashSet<int> PseudoMasters(int pluginCount) => new(Enumerable.Range(0, pluginCount));

    /// <summary>Parse the shared `--bos on|off` flag value; null when the text is neither.</summary>
    public static bool? ParseFlag(string value) => value.ToLowerInvariant() switch { "on" => true, "off" => false, _ => null };
}
