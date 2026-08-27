using System.Diagnostics;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

// SCENARIO CONSISTENCY — does a quest still describe the world it is standing in?
//
// The failure this is built for, in Piranha's words: "SomeQuestMod's quest marker belongs to a
// quest whose description says 'Find Jarl Elisif in the throne room'. SomeBluePalaceMod moves
// Jarl Elisif to a different room and there's no patch, so that marker is now broken."
//
// The graph, established by questprobe against Mutagen 0.54.4 rather than assumed:
//
//   Quest -> Objective -> ObjectiveTarget{ AliasID }  -> QuestAlias
//
// An objective target carries NO ref link of its own, only an alias id, so every objective
// resolves through an alias, and the alias arm says how it resolves:
//
//   UniqueActor       an actor. The compass marker FOLLOWS them.
//   ForcedReference   a specific placed reference. Also follows it — including when it is an actor.
//   FindMatchingRef*  resolved at runtime against the world as it then is.
//   Location / External / CreateReferenceToObject
//
// ⚠️ THE CORRECTION THAT SHAPES THIS FILE. The first cut flagged "the alias's anchor moved" as the
// bug. It is not: an alias is a LINK, so if a mod moves the guard, the alias still points at the
// guard and the marker moves with them. Nothing is broken. Run against the live load order that
// rule produced 285 findings that were almost entirely benign.
//
// What actually breaks is a RELATIONSHIP. A quest is authored with several aliases standing in a
// known arrangement — an actor and the marker that indicates them, two actors who must meet, a
// lever and the gate it opens. Each alias survives being moved. The ARRANGEMENT does not. So the
// question is not "did anything move" but:
//
//   Were these two aliases together when the quest was written, and are they still together now?
//
// "When the quest was written" is not a guess. A plugin can only have seen the positions its own
// masters provide, so the authoring view is the last position contributed by the quest's plugin or
// one of its masters. A winning position from anywhere else is one the quest never saw.
//
// usage: driftprobe [--near N] [--list N] [--generic]

const string Instance = @"C:\Games\Skyrim VR\PiranhaRim VR";
const string GamePath = @"C:\games\steam\steamapps\common\SkyrimVR";
string profileDir = Path.Combine(Instance, "profiles", "PiranhaRimVR");
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");

float near = 600f;      // "together": within this in the authoring view. ~a large room.
int listN = 25;
bool generic = false;
string? emitPath = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--near" && i + 1 < args.Length) near = float.Parse(args[++i]);
    else if (args[i] == "--list" && i + 1 < args.Length) listN = int.Parse(args[++i]);
    else if (args[i] == "--generic") generic = true;
    else if (args[i] == "--emit" && i + 1 < args.Length) emitPath = args[++i];
}

var sw = Stopwatch.StartNew();
var order = Mo2LoadOrder.Build(profileDir, modsDir, dataDir, overwriteDir);
var paths = order.OrderedPaths;
Console.WriteLine($"load order: {paths.Count} plugins ({sw.ElapsedMilliseconds} ms)\n");

// ---------------------------------------------------------------------------------------
// Pass 1 — every plugin once: placement history, quest graphs, master sets.
// ---------------------------------------------------------------------------------------
// Cell travels WITH the position. Two interiors have independent coordinate origins, so a
// distance between them is a meaningless number — the first run of this reported 125,000-unit
// "moves" that were simply a relocation into another cell.
var history = new Dictionary<FormKey, List<Snap>>();
var refBase = new Dictionary<FormKey, FormKey>();
var npcPlaced = new Dictionary<FormKey, List<FormKey>>();   // Npc base -> its placed references
var baseName = new Dictionary<FormKey, (string Type, string? Edid)>();
var cellName = new Dictionary<FormKey, (string? Edid, bool Interior)>();

var pluginKey = new ModKey[paths.Count];
var masterIdx = new HashSet<int>[paths.Count];
var quests = new Dictionary<FormKey, QuestInfo>();
var scenes = new Dictionary<FormKey, (FormKey Quest, int[] Actors)>();
var packageTargets = new Dictionary<FormKey, int[]>();          // package -> alias ids it targets

long placements = 0, questRecords = 0;
// Rule 6, applied to the plumbing (2026-08-27): unreadable plugins are counted and named, never
// silently skipped.
var unreadablePlugins = new List<string>();

for (int p = 0; p < paths.Count; p++)
{
    ISkyrimModGetter mod;
    try { mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], SkyrimRelease.SkyrimSE); }
    catch { unreadablePlugins.Add(Path.GetFileName(paths[p])); pluginKey[p] = ModKey.Null; masterIdx[p] = new HashSet<int>(); continue; }

    using (mod as IDisposable)
    {
        pluginKey[p] = mod.ModKey;
        var mset = new HashSet<int>();
        try
        {
            var names = mod.ModHeader.MasterReferences.Select(m => m.Master).ToHashSet();
            for (int q = 0; q < p; q++) if (names.Contains(pluginKey[q])) mset.Add(q);
        }
        catch { /* an unreadable header means we cannot claim what it saw; leave the set empty */ }
        masterIdx[p] = mset;

        foreach (var cp in PlacementExtract.FromMod(mod))
        {
            cellName[cp.Cell] = (cp.CellEditorId, cp.Interior);
            foreach (var r in cp.Refs)
            {
                placements++;
                var snap = new Snap(p, r.Position, cp.Cell, r.InitiallyDisabled, r.Deleted);
                if (!history.TryGetValue(r.FormKey, out var h)) history[r.FormKey] = h = new List<Snap>(1);
                // Record only a plugin that actually CHANGED something. An override that leaves the
                // placement alone is not a step in the story and would swamp the history.
                if (h.Count == 0 || !h[^1].SameAs(snap)) h.Add(snap);

                if (r.Base is { } bk)
                {
                    refBase[r.FormKey] = bk;
                    if (r.RecordType.Contains("PlacedNpc", StringComparison.Ordinal))
                    {
                        if (!npcPlaced.TryGetValue(bk, out var l)) npcPlaced[bk] = l = new List<FormKey>(1);
                        if (!l.Contains(r.FormKey)) l.Add(r.FormKey);
                    }
                }
            }
        }

        foreach (var rec in mod.EnumerateMajorRecords())
        {
            if (rec is IQuestGetter q) { quests[q.FormKey] = QuestInfo.From(q, p); questRecords++; }
            else if (rec is ISceneGetter sc)
                scenes[sc.FormKey] = (sc.Quest.FormKeyNullable ?? FormKey.Null,
                                      sc.Actors.Select(a => (int)a.ID).Distinct().ToArray());
            else if (rec is IPackageGetter pk)
            {
                // A package's data bag is keyed by slot; the arms that matter are the TARGET ones,
                // and of those only the alias arm names a thing this analysis can follow.
                var t = pk.Data.Values.OfType<IPackageDataTargetGetter>()
                          .Select(d => d.Target).OfType<IPackageTargetAliasGetter>()
                          .Select(x => x.Alias).Distinct().ToArray();
                if (t.Length > 0) packageTargets[pk.FormKey] = t;
            }
            else if (rec is INpcGetter npc) baseName[npc.FormKey] = ("Npc", npc.EditorID);
            else if (rec is IModeledGetter && rec is ISkyrimMajorRecordGetter sm)
                baseName[sm.FormKey] = (rec.GetType().Name.Replace("BinaryOverlay", ""), sm.EditorID);
        }
    }
    if ((p + 1) % 150 == 0) Console.WriteLine($"   ... {p + 1}/{paths.Count} plugins, {placements:N0} placements, {sw.Elapsed.TotalSeconds:0}s");
}

Console.WriteLine($"\nindexed {placements:N0} placements over {history.Count:N0} refs; "
                + $"{quests.Count:N0} winning quests of {questRecords:N0} records  ({sw.Elapsed.TotalSeconds:0.0}s)");
Console.WriteLine($"   plugins that could not be read (contribute nothing, counted per rule 6): {unreadablePlugins.Count:N0}"
                + (unreadablePlugins.Count > 0 ? "  — " + string.Join(", ", unreadablePlugins) : "") + "\n");

// TRANSITIVE master closure (2026-08-27). "What the quest's author saw" must include everything a
// declared master itself depends on — the author had the whole chain installed. The rule lives in
// _shared/DetectorModel.cs and is owned by proofprobe cases.
var masterClosure = DetectorModel.MasterClosure(masterIdx);

// ---------------------------------------------------------------------------------------
// Resolution helpers
// ---------------------------------------------------------------------------------------
Snap? AsSeenBy(FormKey refKey, int byPlugin)
{
    if (!history.TryGetValue(refKey, out var h)) return null;
    for (int i = h.Count - 1; i >= 0; i--)
        if (h[i].Plugin == byPlugin || masterClosure[byPlugin].Contains(h[i].Plugin)) return h[i];
    return null;
}
Snap? Winning(FormKey refKey) => history.TryGetValue(refKey, out var h) && h.Count > 0 ? h[^1] : null;

// An alias points at a thing that HAS a position only for some arms. UniqueActor names an NPC
// record, not a placement, so it is resolved through the placed-reference index — and only when
// that NPC has exactly one placement, because two placements mean no single answer.
// The player's reference is an engine constant with no placement record anywhere — quests alias it
// constantly, and reading that as "anchored to something that is gone" buries every real finding.
// Matched on the raw id, not a FormKey: a plugin with an unusual master list renders the same
// 0x14 against its OWN ModKey, and Bethesda reserves the low ids so nothing else can claim it.
static bool IsPlayerRef(FormKey k) => k.ID == 0x14;

FormKey? AliasRef(AliasInfo a)
{
    if (a.Forced is { } f) return IsPlayerRef(f) ? null : f;
    if (a.UniqueNpc is { } n && npcPlaced.TryGetValue(n, out var l) && l.Count == 1) return l[0];
    return null;
}
string CellOf(FormKey c) => cellName.TryGetValue(c, out var v) ? (v.Edid ?? c.ToString()) + (v.Interior ? " (int)" : "") : c.ToString();
string NameOf(FormKey refKey)
{
    var bk = refBase.GetValueOrDefault(refKey);
    var bn = baseName.GetValueOrDefault(bk);
    return $"{bn.Type ?? "?"} {bn.Edid ?? bk.ToString()}";
}

// ---------------------------------------------------------------------------------------
// Census
// ---------------------------------------------------------------------------------------
var armCount = new Dictionary<string, int>();
var objArm = new Dictionary<string, int>();
int aliasTotal = 0, objTargets = 0;
foreach (var q in quests.Values)
{
    foreach (var a in q.Aliases) { aliasTotal++; Bump(armCount, a.Arm); }
    foreach (var o in q.Objectives)
        foreach (var id in o.TargetAliasIds)
        {
            objTargets++;
            var a = q.Aliases.FirstOrDefault(x => x.Id == id);
            Bump(objArm, a?.Arm ?? "(alias id not found)");
        }
}
Console.WriteLine("==== CENSUS: how quest aliases resolve ====");
Console.WriteLine($"   {"alias arm",-28} {"aliases",9} {"objective targets",18}");
foreach (var k in armCount.OrderByDescending(k => k.Value))
    Console.WriteLine($"   {k.Key,-28} {k.Value,9:N0} {objArm.GetValueOrDefault(k.Key),18:N0}");
Console.WriteLine($"   {"TOTAL",-28} {aliasTotal,9:N0} {objTargets,18:N0}");

// ---------------------------------------------------------------------------------------
// STATED RELATIONSHIPS — turning "these two were near each other" into "the quest says these
// two belong together".
// ---------------------------------------------------------------------------------------
// Proximity is an inference, and on its own it makes every finding arguable: two aliases 300
// units apart in one room may be a scene that has to play, or may be a coincidence of layout.
// Skyrim states the relationship outright in two places, so it does not have to be guessed:
//
//   SCEN.Actors[].ID   is an ALIAS ID, and SCEN.Quest names the quest. Every actor in a scene
//                      must be able to reach the others for the scene to play.
//   PACK data          a package attached to an alias can TARGET another alias; that is an
//                      instruction to walk to it.
//
// A pair backed by either is a claim about authored intent. A pair backed by neither is kept,
// but reported apart and below, because it may be nothing.
var statedPairs = new Dictionary<FormKey, HashSet<(int, int)>>();   // quest -> unordered alias pairs

static (int, int) Pair(int a, int b) => a <= b ? (a, b) : (b, a);

void AddStated(FormKey quest, int a, int b)
{
    if (a == b) return;
    if (!statedPairs.TryGetValue(quest, out var set)) statedPairs[quest] = set = new HashSet<(int, int)>();
    set.Add(Pair(a, b));
}

foreach (var sc in scenes.Values)
{
    if (sc.Quest.IsNull || sc.Actors.Length < 2) continue;
    for (int i = 0; i < sc.Actors.Length; i++)
        for (int j = i + 1; j < sc.Actors.Length; j++)
            AddStated(sc.Quest, sc.Actors[i], sc.Actors[j]);
}

foreach (var q in quests.Values)
    foreach (var a in q.Aliases)
        foreach (var pk in a.Packages)
            if (packageTargets.TryGetValue(pk, out var targets))
                foreach (var t in targets)
                    AddStated(q.Key, a.Id, t);

int statedTotal = statedPairs.Values.Sum(v => v.Count);
Console.WriteLine();
Console.WriteLine($"stated relationships: {scenes.Count:N0} winning scenes, {packageTargets.Count:N0} packages with an alias target");
Console.WriteLine($"   -> {statedTotal:N0} alias pairs the data says belong together, across {statedPairs.Count:N0} quests");

// A published patch is evidence the conflict is already known and handled. If any plugin masters
// BOTH the quest's plugin and the plugin that moved things, someone has reconciled the two.
bool PatchExists(int questPlugin, int moverPlugin)
{
    if (questPlugin == moverPlugin) return true;
    for (int p = 0; p < masterIdx.Length; p++)
        if (p != questPlugin && p != moverPlugin
            && masterIdx[p].Contains(questPlugin) && masterIdx[p].Contains(moverPlugin)) return true;
    return false;
}

// ---------------------------------------------------------------------------------------
// CHECK 1 — a hard break: the alias points at a reference that is gone.
// ---------------------------------------------------------------------------------------
// No inference and no threshold. If the winning state deletes the reference an alias is forced
// onto, the alias cannot fill, and everything gated on it stops.
var broken = new List<string>();
foreach (var q in quests.Values)
    foreach (var a in q.Aliases)
    {
        if (AliasRef(a) is not { } rk) continue;
        var w = Winning(rk);
        bool objTarget = q.Objectives.Any(o => o.TargetAliasIds.Contains(a.Id));
        if (w is null)
            broken.Add($"   MISSING   {q.Label} [{pluginKey[q.Plugin].FileName}] alias '{a.Name}' -> {rk} (no placement anywhere){(objTarget ? "   OBJECTIVE" : "")}");
        else if (w.Value.Deleted)
            broken.Add($"   DELETED   {q.Label} [{pluginKey[q.Plugin].FileName}] alias '{a.Name}' -> {NameOf(rk)} deleted by {pluginKey[w.Value.Plugin].FileName}{(objTarget ? "   OBJECTIVE" : "")}");
    }

Console.WriteLine($"\n==== CHECK 1: alias anchored to a reference that is gone ====");
Console.WriteLine($"   findings: {broken.Count:N0}");
foreach (var b in broken.Take(listN)) Console.WriteLine(b);

// ---------------------------------------------------------------------------------------
// CHECK 2 — the flagship: a relationship that came apart.
// ---------------------------------------------------------------------------------------
// Two aliases of the SAME quest that were together when it was authored, and are not together
// now. Neither alias is broken on its own — each still points at its thing. What is broken is
// the arrangement the quest assumed, which is exactly the Elisif-and-her-marker case and needs
// no guess about which of the two is "the marker".
// Rule 6: the aliases this check CANNOT judge, counted by reason instead of silently skipped.
// Non-positional arms (FindMatchingRef*, Location, flags-only, the player ref) are out of scope by
// design; the other three reasons are genuine blind spots and belong in the output.
// (The counters accumulate only meaningfully on the first FindSplits call — the sensitivity sweep
// below re-runs the check at other thresholds after they are printed.)
long skipNonPositional = 0, skipMultiPlacement = 0, skipNoAuthoringView = 0, skipNoWinning = 0;

// CHECK 2 as a function of the "together" threshold, so the sweep below can re-ask the question
// at other settings over the same indexed data. The review showed the shortlist is threshold-
// sensitive and non-monotonic (no-patch count 7 / 4 / 8 at 300 / 600 / 1200), which no single
// number reveals.
List<Split> FindSplits(float nearU)
{
var split = new List<Split>();
foreach (var q in quests.Values)
{
    var resolved = new List<(AliasInfo A, FormKey Ref, Snap Then, Snap Now)>();
    foreach (var a in q.Aliases)
    {
        if (AliasRef(a) is not { } rk)
        {
            if (a.UniqueNpc is { } n0 && npcPlaced.TryGetValue(n0, out var l0) && l0.Count > 1) skipMultiPlacement++;
            else skipNonPositional++;
            continue;
        }
        if (AsSeenBy(rk, q.Plugin) is not { } then) { skipNoAuthoringView++; continue; }
        if (Winning(rk) is not { } now) { skipNoWinning++; continue; }
        resolved.Add((a, rk, then, now));
    }

    for (int i = 0; i < resolved.Count; i++)
        for (int j = i + 1; j < resolved.Count; j++)
        {
            var (a1, r1, t1, n1) = resolved[i];
            var (a2, r2, t2, n2) = resolved[j];
            if (r1 == r2) continue;
            bool togetherThen = t1.Cell == t2.Cell;
            float was = togetherThen ? Dist(t1.Pos, t2.Pos) : float.PositiveInfinity;
            bool apartNow = n1.Cell != n2.Cell;
            float isNow = apartNow ? float.PositiveInfinity : Dist(n1.Pos, n2.Pos);
            // The split decision is DetectorModel.CameApart — proven by construction in proofprobe:
            // together = same cell within nearU; split = different cells now, or drawn apart past
            // max(nearU, 3× the authored distance). A pair that moved TOGETHER is fine.
            if (!DetectorModel.CameApart(togetherThen, was, !apartNow, isNow, nearU)) continue;

            bool objTarget = q.Objectives.Any(o => o.TargetAliasIds.Contains(a1.Id) || o.TargetAliasIds.Contains(a2.Id));
            bool stated = statedPairs.TryGetValue(q.Key, out var sp) && sp.Contains(Pair(a1.Id, a2.Id));
            int moverPlugin = n1.Plugin != t1.Plugin ? n1.Plugin : n2.Plugin;
            bool patched = PatchExists(q.Plugin, moverPlugin);
            string movers = string.Join("+", new[] { n1, n2 }
                .Where(s => s.Plugin != t1.Plugin && s.Plugin != t2.Plugin)
                .Select(s => pluginKey[s.Plugin].FileName).Distinct());
            var moverList = new[] { n1, n2 }
                .Where(s => s.Plugin != t1.Plugin && s.Plugin != t2.Plugin)
                .Select(s => pluginKey[s.Plugin].FileName.String).Distinct().ToArray();
            split.Add(new Split(q.Label, pluginKey[q.Plugin].FileName, a1.Name, a2.Name,
                                NameOf(r1), NameOf(r2), CellOf(t1.Cell), CellOf(n1.Cell), CellOf(n2.Cell),
                                was, isNow, apartNow, movers, objTarget, stated, patched,
                                q.Objectives.Where(o => o.TargetAliasIds.Contains(a1.Id) || o.TargetAliasIds.Contains(a2.Id))
                                            .Select(o => o.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)),
                                new[] { (Key: r1, Moved: n1.Plugin != t1.Plugin), (Key: r2, Moved: n2.Plugin != t2.Plugin) }
                                    .Where(x => x.Moved).Select(x => x.Key).ToArray(),
                                new[] { r1, r2 }, q.Key, moverList));
        }
}
return split;
}

var split = FindSplits(near);

Console.WriteLine();
Console.WriteLine($"==== CHECK 2: a quest's own aliases came apart (were within {near:0}u, same cell) ====");
Console.WriteLine($"   ---- aliases the check could not judge (counted, not cleared) ----");
Console.WriteLine($"   non-positional arm (runtime-resolved, location, flags-only, player) : {skipNonPositional:N0}");
Console.WriteLine($"   UniqueActor with several placements (no single answer)              : {skipMultiPlacement:N0}");
Console.WriteLine($"   no authoring-view position (quest plugin + closure contributed none): {skipNoAuthoringView:N0}");
Console.WriteLine($"   no winning placement at all                                         : {skipNoWinning:N0}");
Console.WriteLine($"   pairs split                        : {split.Count:N0}");
Console.WriteLine($"   ...the quest STATES belong together: {split.Count(s => s.Stated):N0}   <- a scene or a package says so");
Console.WriteLine($"   ...of those, NO patch reconciles   : {split.Count(s => s.Stated && !s.Patched):N0}   <- the shortlist");
Console.WriteLine($"   ...into DIFFERENT CELLS            : {split.Count(s => s.DifferentCell):N0}");
Console.WriteLine($"   ...where one is an objective target: {split.Count(s => s.IsObjectiveTarget):N0}");

foreach (var s in split.OrderByDescending(s => s.Stated && !s.Patched).ThenByDescending(s => s.Stated)
                       .ThenByDescending(s => s.IsObjectiveTarget).ThenByDescending(s => s.DifferentCell)
                       .ThenByDescending(s => s.NowApart).Take(listN))
{
    Console.WriteLine();
    Console.WriteLine($"   {(s.Stated ? (s.Patched ? "STATED/patched" : "STATED        ") : "proximity only"),-15} "
                    + $"{(s.IsObjectiveTarget ? "OBJECTIVE" : "         ")}  {s.Quest}  [{s.QuestPlugin}]");
    Console.WriteLine($"      '{s.Alias1}' {s.Name1}");
    Console.WriteLine($"      '{s.Alias2}' {s.Name2}");
    Console.WriteLine($"      were {s.WasApart:N0}u apart in {s.CellThen}");
    Console.WriteLine(s.DifferentCell
        ? $"      now in DIFFERENT CELLS: {s.CellNow1}  vs  {s.CellNow2}"
        : $"      now {s.NowApart:N0}u apart");
    if (!string.IsNullOrWhiteSpace(s.Movers)) Console.WriteLine($"      moved by {s.Movers}");
    if (!string.IsNullOrWhiteSpace(s.ObjectiveText)) Console.WriteLine($"      objective: \"{s.ObjectiveText}\"");
}

if (split.Count > 0)
{
    Console.WriteLine($"\n   ---- by the plugin that moved things ----");
    foreach (var g in split.Where(s => !string.IsNullOrWhiteSpace(s.Movers))
                           .GroupBy(s => s.Movers).OrderByDescending(g => g.Count()).Take(listN))
        Console.WriteLine($"   {g.Count(),5}  {g.Key}");
    Console.WriteLine($"\n   ---- by the quest's plugin ----");
    foreach (var g in split.GroupBy(s => s.QuestPlugin).OrderByDescending(g => g.Count()).Take(listN))
        Console.WriteLine($"   {g.Count(),5}  {g.Key}");
}

// ---------------------------------------------------------------------------------------
// Sensitivity — the same question at half and double the threshold, every run.
// ---------------------------------------------------------------------------------------
// "4 with no patch" is a point estimate at an arbitrary knob, and it moves non-monotonically
// (a smaller `near` also shrinks the moved-apart tolerance, so pairs ENTER the split set as the
// threshold falls). A finding that appears at every setting is load-bearing; one that appears at
// exactly one setting is an artifact of the knob and says so here instead of in a headline.
{
    var settings = new[] { near / 2f, near, near * 2f };
    var perSetting = settings.Select(v => FindSplits(v)
            .Where(s => s.Stated && !s.Patched)
            .ToDictionary(s => (s.Quest, s.Alias1, s.Alias2), s => s)).ToArray();

    Console.WriteLine();
    Console.WriteLine($"==== SENSITIVITY: the stated-and-unpatched shortlist vs the 'together' threshold ====");
    for (int i = 0; i < settings.Length; i++)
        Console.WriteLine($"   near = {settings[i],6:0}u  ->  shortlist {perSetting[i].Count,3}");
    var stable = perSetting.Skip(1).Aggregate(new HashSet<(string, string?, string?)>(perSetting[0].Keys),
                                              (acc, d) => { acc.IntersectWith(d.Keys); return acc; });
    Console.WriteLine($"   STABLE CORE (present at every setting): {stable.Count}");
    foreach (var k in stable.OrderBy(k => k.Item1))
        Console.WriteLine($"      {k.Item1}  ·  '{k.Item2}' + '{k.Item3}'");
    var fringe = perSetting.SelectMany(d => d.Keys).Distinct().Count() - stable.Count;
    Console.WriteLine($"   threshold-dependent fringe: {fringe} more appear at only some settings — leads, not findings.");
}

// ---------------------------------------------------------------------------------------
// The generic pass — the same question with quests taken out of it.
// ---------------------------------------------------------------------------------------
if (generic)
{
    // Context only. There is no "did the dependent see it" question to ask here, because a generic
    // ref has no single dependent — that question is per-plugin, and CHECK 2 asks it properly. An
    // earlier version tried to ask it of the definer and could only ever answer no: a plugin that
    // overrides a Skyrim.esm reference necessarily masters Skyrim.esm.
    long changed = 0, relocated = 0, movedFar = 0;
    var moverPairs = new Dictionary<string, int>();
    foreach (var (_, h) in history)
    {
        if (h.Count < 2) continue;
        changed++;
        var first = h[0]; var last = h[^1];
        if (first.Cell != last.Cell) relocated++;
        else if (Dist(first.Pos, last.Pos) <= 16f) continue;
        else movedFar++;
        if (last.Plugin != first.Plugin)
            Bump(moverPairs, $"{pluginKey[first.Plugin].FileName}  -> moved by ->  {pluginKey[last.Plugin].FileName}");
    }
    Console.WriteLine();
    Console.WriteLine($"==== GENERIC placement drift (scale context for the checks above) ====");
    Console.WriteLine($"   refs whose placement any plugin altered : {changed:N0} of {history.Count:N0}");
    Console.WriteLine($"   ...relocated to another cell            : {relocated:N0}");
    Console.WriteLine($"   ...moved more than 16u within its cell  : {movedFar:N0}");
    Console.WriteLine($"   A quest-aware check sits on TOP of this rather than surveying it: the raw volume is");
    Console.WriteLine($"   the reason an unfiltered sweep drowns and a relationship test does not.");
    foreach (var g in moverPairs.OrderByDescending(g => g.Value).Take(listN))
        Console.WriteLine($"   {g.Value,6}  {g.Key}");
}

// ---------------------------------------------------------------------------------------
// --emit — hand the exact broken records to the patch verifier.
// ---------------------------------------------------------------------------------------
// Only the STATED splits are emitted. A proximity-only pair is a guess about what the author meant;
// a stated one is the quest's own scenes and packages saying these two aliases belong together, and
// that is the population the shortlist was built from.
if (emitPath is not null)
{
    var conflicts = new List<Conflict>();
    foreach (var s in split.Where(s => s.Stated))
    {
        // A repair has to reconcile the PLACEMENT that changed. Re-pointing the quest's alias is the
        // other legitimate fix and is reported as an alternative rather than scored, because a patch
        // that overrides the quest may well be doing something unrelated to this pair.
        var necessary = s.MovedRefs.Select(k => new ConflictRecord(k.ToString(), "moved-ref", s.CellThen)).ToList();
        if (necessary.Count == 0) continue;                       // nothing identifiable moved; not judgeable
        var alternative = new List<ConflictRecord> { new(s.QuestKey.ToString(), "quest", s.Quest) };

        foreach (var mover in s.MoverList.DefaultIfEmpty(""))
            conflicts.Add(new Conflict(
                "SPLIT", s.QuestPlugin, mover,
                $"quest '{s.Quest}' aliases '{s.Alias1}' and '{s.Alias2}' were {s.WasApart:N0}u apart in {s.CellThen}, "
              + (s.DifferentCell ? $"now in different cells ({s.CellNow1} vs {s.CellNow2})" : $"now {s.NowApart:N0}u apart"),
                necessary, alternative));
    }

    ConflictIo.Write(emitPath, new ConflictFile("driftprobe", Instance,
        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), conflicts));
    Console.WriteLine($"\nemitted {conflicts.Count:N0} conflicts ({conflicts.Count(c => c.B.Length == 0)} with no second party to name) -> {emitPath}");
}

Console.WriteLine($"\nelapsed {sw.Elapsed.TotalSeconds:0.0}s");
return 0;

static void Bump(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;
static float Dist(Vec3 a, Vec3 b)
{
    float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
    return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
}

readonly record struct Snap(int Plugin, Vec3 Pos, FormKey Cell, bool Disabled, bool Deleted)
{
    public bool SameAs(Snap o) => Cell == o.Cell && Disabled == o.Disabled && Deleted == o.Deleted
                               && MathF.Abs(Pos.X - o.Pos.X) < 0.01f
                               && MathF.Abs(Pos.Y - o.Pos.Y) < 0.01f
                               && MathF.Abs(Pos.Z - o.Pos.Z) < 0.01f;
}

record Split(string Quest, string QuestPlugin, string? Alias1, string? Alias2, string Name1, string Name2,
             string CellThen, string CellNow1, string CellNow2, float WasApart, float NowApart,
             bool DifferentCell, string Movers, bool IsObjectiveTarget, bool Stated, bool Patched,
             string? ObjectiveText,
             // For --emit: the exact records a fix would have to touch, and who to look a patch up
             // against. MovedRefs is the SHORT list — a repair only has to reconcile the placement
             // that actually changed, not both ends of the pair.
             IReadOnlyList<FormKey> MovedRefs, IReadOnlyList<FormKey> AllRefs,
             FormKey QuestKey, IReadOnlyList<string> MoverList);

sealed record AliasInfo(int Id, string? Name, string Arm, FormKey? Forced, FormKey? UniqueNpc, FormKey[] Packages);
sealed record ObjectiveInfo(string? Text, int[] TargetAliasIds);

sealed record QuestInfo(FormKey Key, string? Edid, string? Name, int Plugin,
                        IReadOnlyList<AliasInfo> Aliases, IReadOnlyList<ObjectiveInfo> Objectives)
{
    public string Label => Name ?? Edid ?? Key.ToString();

    /// <summary>Flatten a quest to the parts that decide fragility. The overlay is disposed with its plugin,
    /// so nothing may be held by reference past this call.</summary>
    public static QuestInfo From(IQuestGetter q, int plugin)
    {
        var aliases = new List<AliasInfo>();
        foreach (var a in q.Aliases)
        {
            // The arms are separate nullable properties on QuestAlias (questprobe, 2026-08-27), so the
            // resolution kind reads off directly rather than from a discriminator enum.
            string arm;
            FormKey? forced = null, npc = null;
            if (!a.ForcedReference.IsNull) { arm = "ForcedReference"; forced = a.ForcedReference.FormKey; }
            else if (!a.UniqueActor.IsNull) { arm = "UniqueActor"; npc = a.UniqueActor.FormKey; }
            else if (a.FindMatchingRefNearAlias is not null) arm = "FindMatchingRefNearAlias";
            else if (a.FindMatchingRefFromEvent is not null) arm = "FindMatchingRefFromEvent";
            else if (a.External is not null) arm = "External";
            else if (a.Location is not null) arm = "LocationAlias";
            else if (!a.SpecificLocation.IsNull) arm = "SpecificLocation";
            else if (a.CreateReferenceToObject is not null) arm = "CreateReferenceToObject";
            else arm = "(none / conditions only)";
            aliases.Add(new AliasInfo((int)a.ID, a.Name, arm, forced, npc,
                                      a.PackageData.Select(x => x.FormKey).ToArray()));
        }

        var objectives = q.Objectives
            .Select(o => new ObjectiveInfo(o.DisplayText?.String, o.Targets.Select(t => t.AliasID).ToArray()))
            .ToList();

        return new QuestInfo(q.FormKey, q.EditorID, q.Name?.String, plugin, aliases, objectives);
    }
}
