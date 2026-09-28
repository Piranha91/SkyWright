using System.Numerics;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

using Mesh = (System.Collections.Generic.List<System.Numerics.Vector3> V,
              System.Collections.Generic.List<(int A, int B, int C, int Flags)> T);

// THE NEEDS-WITNESS JOIN — 3.5.5's named tail, driver half.
//
// The reach pass in Program.cs finds ground only the victim's navmesh makes usable. The in-game
// result said that alone is not a finding: Bethesda ships the shape, and a winner that deletes
// ground under its new walls is doing its job. What turns "coverage lost" into "something is
// broken" is a WITNESS — a records-side fact that an actor is meant to be on that ground:
//
//   W1  a package location slot carried by a placed NPC, anchored in the cell (a placed
//       reference, or the actor's own placement for the editor-location / where-it-stands arms),
//       judged as a disc of its radius;
//   W2  a placed actor (ACHR) whose winning placement is in the cell;
//   W3  a marker or piece of furniture a W1 anchor targets — patrol chains through linked refs,
//       furniture inside a sandbox radius. Partial, weaker, and counted apart from W1.
//
// Player content (doors, chests, activators) is deliberately NOT a witness. Everything in this
// file is I/O — finding the witnesses, resolving anchors against the winning order, measuring
// each one against the same effective-coverage sets the selector uses. The DECISION is
// DetectorModel.NeedsWitness, owned by proofprobe. Lives in its own file because it is a second
// domain (packages and actors) riding a navmesh probe, and the repo's rule against stacking
// domains into one file applies to probes too.

/// <summary>A placed reference as the join sees it: its WINNING placement, who touched the
/// record, and its linked references (winner-takes-all, like the position).</summary>
sealed record WitnessRef(FormKey Key, string Type, string? Edid, FormKey? Base, Vec3 Pos, FormKey Cell,
                         string State, List<int> Touchers, List<FormKey> Links);

/// <summary>One witness, measured and judged. Point is null when the anchor arm has no position
/// this order can read; distances are NaN then. Source says whether the witness sits in the
/// record's own cell or an exterior ring cell.</summary>
sealed record WitnessRow(WitnessKind Kind, FormKey Form, string? Edid, string? Actor, string? Package, int? Slot,
                         string Arm, Vec3? Point, float Radius, float DV, float DW, float HV, float HW,
                         WitnessVerdict Verdict, WitnessBlocker Reason, WitnessAuthorship Authorship, string Why,
                         bool Governing, string State, string Source, bool ElevatedStrand);

/// <summary>What the join found for one clobber record.</summary>
sealed record WitnessReport(List<WitnessRow> Rows, int ActorsInCell, int ActorsOnGround, int AnchorsElsewhere)
{
    /// <summary>A witness on the LOST ground specifically: Stranded, or lost inside the resolution
    /// band. Covered rows stand on ground both arms keep and say the re-cut spared them.</summary>
    public bool AnyOnLostGround => Rows.Any(r => r.Verdict == WitnessVerdict.Stranded || r.Reason == WitnessBlocker.WithinResolutionBand);
    public int Count(WitnessKind k, WitnessVerdict v) => Rows.Count(r => r.Kind == k && r.Verdict == v);
    public IEnumerable<WitnessRow> Stranded => Rows.Where(r => r.Verdict == WitnessVerdict.Stranded);
    public bool AnyWitness => Rows.Any(r => r.Kind != WitnessKind.TargetedRef);
    /// <summary>The blind band the rule states for placements (W2): a placed actor the winner arm
    /// leaves within (0, clearance] of its mesh reads Covered, and nothing below clearance is
    /// measured for a placement. A count beside the verdicts, never a verdict — and an upper bound:
    /// the 3-D distance includes the actor's standing height above its own triangle, so nearly
    /// every Covered placement is in it, which is why the driver splits it by distance.</summary>
    public IEnumerable<WitnessRow> BlindBand(float clearance)
        => Rows.Where(r => r.Kind == WitnessKind.PlacedActor && r.Verdict == WitnessVerdict.Covered && r.DW > 0f && r.DW <= clearance);
}

static class NeedsWitnessJoin
{
    /// <summary>Everything the join needs from the load order, gathered in one extra pass over
    /// every plugin: packages and NPC records from anywhere (an actor placed in another cell can
    /// anchor a package here), placed references only for the cells the pool names.</summary>
    public sealed class Census
    {
        public readonly Dictionary<FormKey, WitnessRef> Refs = new();
        public readonly Dictionary<FormKey, List<FormKey>> RefsByCell = new();
        public readonly Dictionary<FormKey, PackFacts> Packs = new();
        public readonly Dictionary<FormKey, FormKey[]> NpcPackages = new();
        public readonly Dictionary<FormKey, string?> NpcEdid = new();
        public readonly HashSet<FormKey> NpcTemplated = new();
        public readonly HashSet<FormKey> LeveledNpcs = new();
        public readonly HashSet<FormKey> NpcPlacedAnywhere = new();
        /// <summary>every placed reference in the order -> its winning cell, so an anchor OUTSIDE the
        /// pool's cells can be told apart from one that is not placed anywhere.</summary>
        public readonly Dictionary<FormKey, FormKey> RefCellAnywhere = new();
        /// <summary>anchor REFR -> every reference-arm slot of a package some placed NPC carries.</summary>
        public readonly Dictionary<FormKey, List<(FormKey Npc, FormKey Pack, int Slot, uint Radius)>> AnchoredHere = new();
        public int UnreadablePlugins;
        public int RefsSeen;
        /// <summary>The enable facts of every placement in the order — a witness's enable parent is usually in another
        /// cell, so the chain can only be resolved once the whole order has been walked (Q27).</summary>
        public readonly EnableFactsIndex EnableFacts = new();
        /// <summary>Witnesses whose enable chain says they are not in the world when the cell loads.</summary>
        public int RefsAbsentAtLoad;
        /// <summary>Witnesses whose chain could not be read to the end — the answer is EnableState's fallback, not a
        /// measurement, and a row resting on one says so.</summary>
        public int RefsChainUnresolved;
    }

    public static Census Build(IReadOnlyList<string> paths, HashSet<FormKey> wantedCells, bool flagsOnly = false)
    {
        var c = new Census();
        // Two last-sighting sets (_shared/LastSighting.cs), the census's only membership decisions: every reference that is
        // not deleted at its last sighting (anywhere), and those whose last sighting also stands in a wanted cell (held).
        // A deletion, with or without placement data, takes a reference out of both.
        var held = new LastSightingSet();
        var anywhere = new LastSightingSet();
        for (int p = 0; p < paths.Count; p++)
        {
            ISkyrimModGetter mod;
            try { mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], SkyrimRelease.SkyrimSE); }
            catch { c.UnreadablePlugins++; continue; }
            using (mod as IDisposable)
            {
                // The last-sighting rule (_shared/LastSighting.cs): every cell of the plugin, deletion-only ones included;
                // a witness is held iff its last sighting stands in a wanted cell, and a later plugin that moves it out or
                // deletes it takes it out of the census, of the whole-order cell table and of the placed-NPC set.
                foreach (var cp in LastSighting.Cells(mod))
                {
                    bool wanted = wantedCells.Contains(cp.Cell);
                    held.Observe(cp, (_, r) => wanted && !r.Deleted);
                    anywhere.Observe(cp, (_, r) => !r.Deleted);
                    if (cp.DeletedRefs is { Count: > 0 } gone)
                        foreach (var d in gone) { c.Refs.Remove(d.FormKey); c.EnableFacts.NoteDeleted(d.FormKey); }
                    foreach (var r in cp.Refs)
                    {
                        c.EnableFacts.Note(r);
                        if (!held.Refs.ContainsKey(r.FormKey)) { c.Refs.Remove(r.FormKey); continue; }
                        c.RefsSeen++;
                        // A PROVISIONAL state: the chain cannot be resolved until every plugin has been read (an override
                        // three plugins later can enable or disable the parent), so the winning record's own shape is
                        // recorded here and Resolve() below settles it. Before Q27 this string WAS the answer, and
                        // "enable-parent" counted as live whatever the parent's own state was.
                        string state = r.Deleted ? "deleted"
                            : r.EnableParent is { } ep && ep.ID == 0x14 && r.EnableParentOpposite ? "permanently disabled"
                            : r.EnableParent is not null ? "enable-parent"
                            : r.InitiallyDisabled ? "initially disabled"
                            : "enabled";
                        var prev = c.Refs.GetValueOrDefault(r.FormKey);
                        var touchers = prev?.Touchers ?? new List<int>();
                        if (touchers.Count == 0 || touchers[^1] != p) touchers.Add(p);
                        c.Refs[r.FormKey] = new WitnessRef(r.FormKey, r.RecordType, r.EditorId, r.Base, r.Position, cp.Cell,
                                                           state, touchers, prev?.Links ?? new List<FormKey>());
                    }
                }
                foreach (var rec in mod.EnumerateMajorRecords())
                {
                    if (rec is IPackageGetter pk) c.Packs[pk.FormKey] = PackFacts.From(pk);
                    else if (rec is INpcGetter npc)
                    {
                        c.NpcPackages[npc.FormKey] = npc.Packages.Select(x => x.FormKey).ToArray();
                        c.NpcEdid[npc.FormKey] = npc.EditorID;
                        if (!npc.Template.IsNull && npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.AIPackages))
                            c.NpcTemplated.Add(npc.FormKey);
                        else c.NpcTemplated.Remove(npc.FormKey);
                    }
                    else if (rec is ILeveledNpcGetter lv) c.LeveledNpcs.Add(lv.FormKey);
                    // Linked references, winner-takes-all, only for refs already known to sit in a
                    // wanted cell (the placement walk above ran first for this plugin).
                    else if (rec is IPlacedObjectGetter po && c.Refs.TryGetValue(po.FormKey, out var wr))
                    { wr.Links.Clear(); wr.Links.AddRange(po.LinkedReferences.Select(l => l.Reference.FormKey).Where(k => !k.IsNull)); }
                    else if (rec is IPlacedNpcGetter pn && c.Refs.TryGetValue(pn.FormKey, out var wn))
                    { wn.Links.Clear(); wn.Links.AddRange(pn.LinkedReferences.Select(l => l.Reference.FormKey).Where(k => !k.IsNull)); }
                }
            }
        }
        // The whole-order cell table, and the NPC bases placed somewhere: both from the last sightings that are not deletions.
        foreach (var (k, cell) in anywhere.CellOf) c.RefCellAnywhere[k] = cell;
        foreach (var r in anywhere.Refs.Values)
            if (r.RecordType.Contains("PlacedNpc", StringComparison.Ordinal) && r.Base is { } bk) c.NpcPlacedAnywhere.Add(bk);
        foreach (var (k, r) in c.Refs)
        {
            if (!c.RefsByCell.TryGetValue(r.Cell, out var l)) c.RefsByCell[r.Cell] = l = new List<FormKey>();
            l.Add(k);
        }
        // Which reference-arm anchors are carried by a placed NPC — indexed once so a record's cell
        // can ask "who is sent here" without walking every NPC per record.
        foreach (var bk in c.NpcPlacedAnywhere)
        {
            if (!c.NpcPackages.TryGetValue(bk, out var pkgs) || c.NpcTemplated.Contains(bk)) continue;
            foreach (var pkKey in pkgs)
            {
                if (!c.Packs.TryGetValue(pkKey, out var f)) continue;
                for (int s = 0; s < f.Locations.Count; s++)
                {
                    var (arm, radius, link) = f.Locations[s];
                    if (arm != PackFacts.RefArm || link.IsNull || !c.Refs.ContainsKey(link)) continue;
                    if (!c.AnchoredHere.TryGetValue(link, out var l)) c.AnchoredHere[link] = l = new();
                    l.Add((bk, pkKey, s, radius));
                }
            }
        }
        // THE POPULATION (Q27): now that every plugin has spoken, resolve each wanted reference's chain and replace the
        // provisional state with what the game will do at load. A witness that is not in the world cannot abandon any
        // ground, and one whose chain could not be read is counted rather than assumed present.
        var pop = flagsOnly ? EnablePopulation.Flags(c.EnableFacts) : EnablePopulation.LoadTime(c.EnableFacts);
        if (!flagsOnly)
            foreach (var key in c.Refs.Keys.ToList())
            {
                var wr = c.Refs[key];
                if (wr.State == "deleted") continue;
                var res = pop.Resolution(key);
                if (!res.Resolved) c.RefsChainUnresolved++;
                if (!res.Enabled) c.RefsAbsentAtLoad++;
                string state = !res.Enabled ? (res.Resolved ? "absent at load" : "absent at load (chain unresolved)")
                             : res.Resolved ? (res.ViaParent ? "enabled via parent" : "enabled")
                             : "enabled (chain unresolved)";
                c.Refs[key] = wr with { State = state };
            }
        return c;
    }

    /// <summary>HOUSECARL_WINSTATE_DUMP: the census's witnesses, its whole-order cell table and the NPC bases it calls
    /// placed, for an independent reader.</summary>
    public static void Dump(Census c, HashSet<FormKey> wantedCells)
    {
        if (!WinStateDump.Asked) return;
        WinStateDump.Write("navownprobe-census", new
        {
            wantedCells = wantedCells.Select(k => k.ToString()).OrderBy(k => k, StringComparer.Ordinal).ToList(),
            refs = c.Refs.Values.OrderBy(r => r.Key.ToString(), StringComparer.Ordinal)
                    .Select(r => new { formKey = r.Key.ToString(), cell = r.Cell.ToString(), x = r.Pos.X, y = r.Pos.Y, z = r.Pos.Z, state = r.State }).ToList(),
            refCellAnywhere = c.RefCellAnywhere.OrderBy(k => k.Key.ToString(), StringComparer.Ordinal)
                    .Select(k => new { formKey = k.Key.ToString(), cell = k.Value.ToString() }).ToList(),
            npcPlacedAnywhere = c.NpcPlacedAnywhere.Select(k => k.ToString()).OrderBy(k => k, StringComparer.Ordinal).ToList(),
        });
    }

    /// <summary>The join for one record. <paramref name="cells"/> is the record's cell first, then
    /// (exteriors) its ±1 ring; <paramref name="L"/>/<paramref name="W"/> are the loser's and
    /// winner's versions of the record and <paramref name="others"/> every other winning mesh
    /// nearby — the same effective-coverage sets the selector used.</summary>
    public static WitnessReport Judge(Census c, FormKey cell, IReadOnlyList<FormKey> cells, int winner,
        HashSet<int>[] masterClosure, Mesh L, Mesh W, IReadOnlyList<Mesh> others, float clearance,
        Func<FormKey, string?> baseTypeOf)
    {
        var rows = new List<WitnessRow>();
        var cellSet = cells.ToHashSet();
        Vector3 P(Vec3 v) => new(v.X, v.Y, v.Z);
        // "The winner's side": the winner itself, or anything that masters it (downstream — it saw
        // the winner's mesh). Upstream masters did not, which is why every mod mastering Skyrim.esm
        // does not make vanilla's placements Deliberate.
        bool WinnerSide(List<int> touchers) => touchers.Any(p => p == winner || masterClosure[p].Contains(winner));
        // A witness is THIS record's business only when it stands on this record's ground in one
        // arm or the other. By construction a Stranded witness is within clearance of the loser's
        // version (the arms differ in one record; ground the rest of the cell supplies is common to
        // both), so this is exact, not a heuristic — witnesses elsewhere in the cell are context.
        bool OnGround(Vector3 p) => DetectorModel.DistanceToCoverage(p, L.V, L.T) <= clearance
                                 || DetectorModel.DistanceToCoverage(p, W.V, W.T) <= clearance;
        float Eff(Vector3 p, Mesh own, Func<Vector3, List<Vector3>, List<(int, int, int, int)>, float> measure)
        {
            float d = measure(p, own.V, own.T);
            foreach (var o in others) { if (d <= 0f) break; d = MathF.Min(d, measure(p, o.V, o.T)); }
            return d;
        }
        string Source(FormKey inCell) => inCell == cell ? "cell" : "ring";

        WitnessRow Make(WitnessKind kind, WitnessRef rec, string? actor, string? package, int? slot, string arm,
                        Vector3? point, float radius, WitnessBlocker blocker, bool governing, List<int> touchers)
        {
            float dV = float.NaN, dW = float.NaN, hV = float.NaN, hW = float.NaN;
            if (point is { } p)
            {
                dV = Eff(p, L, DetectorModel.DistanceToCoverage); dW = Eff(p, W, DetectorModel.DistanceToCoverage);
                hV = Eff(p, L, DetectorModel.DistanceToCoverageXY); hW = Eff(p, W, DetectorModel.DistanceToCoverageXY);
            }
            bool touched = WinnerSide(touchers);
            var v = DetectorModel.NeedsWitness(new(kind, radius, dV, dW, blocker, touched), clearance);
            // The horizontal reading beside the frozen 3-D one: an anchor hung above its floor reads
            // Uncovered in 3-D; if it would strand horizontally, count it, do not clear it.
            bool elevated = v.Verdict == WitnessVerdict.Uncovered
                && DetectorModel.NeedsWitness(new(kind, radius, hV, hW, blocker, touched), clearance).Verdict == WitnessVerdict.Stranded;
            return new WitnessRow(kind, rec.Key, rec.Edid, actor, package, slot, arm,
                                  point is { } q ? new Vec3(q.X, q.Y, q.Z) : null, radius, dV, dW, hV, hW,
                                  v.Verdict, v.Reason, v.Authorship, v.Why, governing, rec.State, Source(rec.Cell), elevated);
        }

        // A live witness is one the chain says is in the world at load. "enable-parent" is the pre-Q27 vocabulary and is
        // kept only for a --flags run, where no chain was resolved; an unresolved chain counts as live and is reported
        // apart, because refusing it would hide the witness rather than qualify it.
        static bool Live(WitnessRef r) => r.State is "enabled" or "enabled via parent" or "enabled (chain unresolved)" or "enable-parent";
        var refsHere = cells.Where(c.RefsByCell.ContainsKey).SelectMany(k => c.RefsByCell[k]).Select(k => c.Refs[k]).ToList();
        var actors = refsHere.Where(r => r.Type.Contains("PlacedNpc", StringComparison.Ordinal) && Live(r)).ToList();
        int actorsInCell = actors.Count(a => a.Cell == cell);
        var slotsDone = new HashSet<(FormKey, FormKey, int)>();
        var anchorsForW3 = new List<(WitnessRef Anchor, float Radius)>();
        int onGround = 0, anchorsElsewhere = 0;

        foreach (var a in actors)
        {
            var ap = P(a.Pos);
            if (!OnGround(ap)) continue;
            onGround++;
            var bk = a.Base ?? FormKey.Null;
            string? actorName = c.NpcEdid.GetValueOrDefault(bk) ?? a.Edid ?? bk.ToString();
            // W2 — the placement itself is a fact whatever the base is.
            rows.Add(Make(WitnessKind.PlacedActor, a, actorName, null, null, "placement", ap, 0, WitnessBlocker.None, false, a.Touchers));
            // W1 — every location slot of every package the actor carries, the first positional
            // package flagged as governing (the highest-priority reading, confirmed in game).
            if (c.LeveledNpcs.Contains(bk))
            { rows.Add(Make(WitnessKind.PackageAnchor, a, actorName, null, null, "leveled-base", ap, 0, WitnessBlocker.LeveledBase, false, a.Touchers)); continue; }
            if (!c.NpcPackages.TryGetValue(bk, out var pkgs))
            { rows.Add(Make(WitnessKind.PackageAnchor, a, actorName, null, null, "npc-base-not-in-order", ap, 0, WitnessBlocker.PackageMissing, false, a.Touchers)); continue; }
            if (c.NpcTemplated.Contains(bk))
            { rows.Add(Make(WitnessKind.PackageAnchor, a, actorName, null, null, "templated", ap, 0, WitnessBlocker.Templated, false, a.Touchers)); continue; }
            int governing = -1;
            for (int i = 0; i < pkgs.Length; i++)
            {
                if (!c.Packs.TryGetValue(pkgs[i], out var f))
                { rows.Add(Make(WitnessKind.PackageAnchor, a, actorName, pkgs[i].ToString(), null, "package-missing", ap, 0, WitnessBlocker.PackageMissing, false, a.Touchers)); continue; }
                if (f.Locations.Count > 0 && governing < 0) governing = i;
                for (int s = 0; s < f.Locations.Count; s++)
                {
                    var (arm, radius, link) = f.Locations[s];
                    slotsDone.Add((bk, pkgs[i], s));
                    string pkName = f.Edid ?? pkgs[i].ToString();
                    if (arm == PackFacts.EditorArm || arm == PackFacts.SelfArm)
                    {
                        rows.Add(Make(WitnessKind.PackageAnchor, a, actorName, pkName, s, arm, ap, radius, WitnessBlocker.None, i == governing, a.Touchers));
                        anchorsForW3.Add((a, radius));
                    }
                    else if (arm == PackFacts.RefArm && !link.IsNull && c.Refs.TryGetValue(link, out var anchor) && cellSet.Contains(anchor.Cell))
                    {
                        var xp = P(anchor.Pos);
                        rows.Add(Make(WitnessKind.PackageAnchor, anchor, actorName, pkName, s, arm, xp, radius, WitnessBlocker.None, i == governing, anchor.Touchers));
                        if (OnGround(xp)) anchorsForW3.Add((anchor, radius));
                    }
                    else if (arm == PackFacts.RefArm && !link.IsNull && c.RefCellAnywhere.ContainsKey(link))
                        anchorsElsewhere++;      // placed, readable, and in another cell: not this record's witness
                    else
                        rows.Add(Make(WitnessKind.PackageAnchor, a, actorName, pkName, s,
                                      arm == PackFacts.RefArm ? "reference (not placed)" : arm,
                                      null, radius, WitnessBlocker.UnreadableAnchor, i == governing, a.Touchers));
                }
            }
        }

        // W1 by the other route: an NPC placed ANYWHERE whose package anchors to a reference here.
        foreach (var anchor in refsHere)
        {
            if (!c.AnchoredHere.TryGetValue(anchor.Key, out var carried) || !Live(anchor)) continue;
            var xp = P(anchor.Pos);
            if (!OnGround(xp)) continue;
            foreach (var (npc, pack, slot, radius) in carried)
            {
                if (slotsDone.Contains((npc, pack, slot))) continue;
                slotsDone.Add((npc, pack, slot));
                var pkgs = c.NpcPackages[npc];
                int governing = Array.FindIndex(pkgs, k => c.Packs.TryGetValue(k, out var g) && g.Locations.Count > 0);
                bool isGoverning = governing >= 0 && pkgs[governing] == pack;
                rows.Add(Make(WitnessKind.PackageAnchor, anchor, c.NpcEdid.GetValueOrDefault(npc) ?? npc.ToString(),
                              c.Packs[pack].Edid ?? pack.ToString(), slot, PackFacts.RefArm + " (actor elsewhere)",
                              xp, radius, WitnessBlocker.None, isGoverning, anchor.Touchers));
                anchorsForW3.Add((anchor, radius));
            }
        }

        // W3 — partial by design: the linked-ref chain from each W1 anchor (patrol markers), and
        // furniture inside a sandbox radius. Never merged into W1.
        var w3Seen = new HashSet<FormKey>();
        foreach (var (anchor, radius) in anchorsForW3.DistinctBy(x => (x.Anchor.Key, x.Radius)))
        {
            var queue = new Queue<(FormKey Key, int Depth)>();
            var visited = new HashSet<FormKey> { anchor.Key };
            queue.Enqueue((anchor.Key, 0));
            while (queue.Count > 0)
            {
                var (k, depth) = queue.Dequeue();
                if (depth >= 8 || !c.Refs.TryGetValue(k, out var cur)) continue;
                foreach (var next in cur.Links)
                {
                    if (!visited.Add(next) || !c.Refs.TryGetValue(next, out var tr) || !cellSet.Contains(tr.Cell) || !Live(tr)) continue;
                    queue.Enqueue((next, depth + 1));
                    var tp = P(tr.Pos);
                    if (!w3Seen.Add(next) || !OnGround(tp)) continue;
                    rows.Add(Make(WitnessKind.TargetedRef, tr, null, null, null, "linked-ref chain", tp, 0, WitnessBlocker.None, false, tr.Touchers));
                }
            }
            if (radius == 0) continue;
            foreach (var r in refsHere)
            {
                if (!Live(r) || r.Base is not { } b || baseTypeOf(b) != "Furniture") continue;
                float dx = r.Pos.X - anchor.Pos.X, dy = r.Pos.Y - anchor.Pos.Y;
                if (MathF.Sqrt(dx * dx + dy * dy) > radius) continue;
                var fp = P(r.Pos);
                if (!w3Seen.Add(r.Key) || !OnGround(fp)) continue;
                rows.Add(Make(WitnessKind.TargetedRef, r, null, null, null, "furniture in sandbox radius", fp, 0, WitnessBlocker.None, false, r.Touchers));
            }
        }

        return new WitnessReport(rows, actorsInCell, onGround, anchorsElsewhere);
    }
}
