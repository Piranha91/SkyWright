using System.Diagnostics;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore.Progress;

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
// usage: driftprobe [--near N] [--list N] [--generic] [--emit FILE]
//                   [--actor EDITORID] [--no-reach] [--bos on|off]
//
//   --actor    print one actor's packages, each anchor resolved against the winning order, and
//              what the reach filter makes of them. The way to check any CHECK 2 verdict.
//   --no-reach report CHECK 2 as it was before the reach filter: raw placement distance.
//   --bos      on (default, since 2026-09-04): the Base Object Swapper layer (*_SWAP.ini, from the
//              profile's VFS; _shared/BosHook.cs) applied to this probe's own placement history as a
//              PSEUDO-PLUGIN after the last real one — an override that moves an alias anchor is a
//              placement step with BOS as the mover, so CHECK 2 measures the anchor where the game
//              puts it; a swapped base replaces the record's for anything typed by base. Actors are
//              never swapped (the reader's scope rule). off: the record bases and placements, as
//              every run before. No rule changes either way.

// Defaults are the live VR instance; --instance / --game / --profile point every path at
// another MO2 layout — the SE conflict-testing lab's pre-flight runs exist so a finding is
// confirmed to reproduce in a minimal test order BEFORE anyone launches the game on it.
// Nothing is compiled in — see _shared/HarnessTarget.cs. Flags win over the environment and
// over harness.local.json; Require() runs AFTER them, so a run that passes --instance never
// trips it, and a run that configures nothing fails with guidance instead of walking a
// directory that isn't there.
using var __run = RunLog.BeginProcess("driftprobe", args);
// Known answers for which version of a moved or deleted reference this probe's walk keeps (_shared/MovedRefFixture.cs).
if (args.Length >= 1 && args[0] == "--moved-ref-selftest") return DriftSelfTest.Run();
// Known answers for CHECK 2 (the alias-split decisions) over a synthetic quest order (_shared/DriftFixture.cs).
if (args.Length >= 1 && args[0] == "--self-test") return DriftQuestSelfTest.Run();

string Instance = HarnessTarget.Instance;
string GamePath = HarnessTarget.Game;
string profileName = HarnessTarget.Profile;
{
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--instance" && i + 1 < args.Length) Instance = args[i + 1];
        else if (args[i] == "--game" && i + 1 < args.Length) GamePath = args[i + 1];
        else if (args[i] == "--profile" && i + 1 < args.Length) profileName = args[i + 1];
    }
}
Instance = HarnessTarget.Require(Instance, "MO2 instance directory", "instance", "--instance");
GamePath = HarnessTarget.Require(GamePath, "game directory", "game", "--game");
profileName = HarnessTarget.Require(profileName, "MO2 profile name", "profile", "--profile");
string profileDir = Path.Combine(Instance, "profiles", profileName);
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");

float near = 600f;      // "together": within this in the authoring view. ~a large room.
int listN = 25;
string? actorDump = null;   // --actor <EditorID substring>: print an actor's packages and stop.
bool reachFilter = true;    // --no-reach: report the pairs as they were reported before 2026-08-31.
bool generic = false;
string? emitPath = null;
bool bosOn = true;
string? orderFile = null;
// driftprobe has no --enable/--disable of its own; the reader takes the empty lists and resolves the
// profile (or a --order candidate), so its emit carries the same configuration identity as every other.
var enablePlugins = new List<string>();
var disablePlugins = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--near" && i + 1 < args.Length) near = float.Parse(args[++i]);
    else if (args[i] == "--bos" && i + 1 < args.Length) bosOn = BosHook.ParseFlag(args[++i]) ?? throw new ArgumentException("--bos takes on|off");
    else if (args[i] == "--list" && i + 1 < args.Length) listN = int.Parse(args[++i]);
    else if (args[i] == "--order" && i + 1 < args.Length) orderFile = args[++i];
    else if (args[i] == "--generic") generic = true;
    else if (args[i] == "--emit" && i + 1 < args.Length) emitPath = args[++i];
    else if (args[i] == "--actor" && i + 1 < args.Length) actorDump = args[++i];
    else if (args[i] == "--no-reach") reachFilter = false;
}

var sw = Stopwatch.StartNew();
// WHAT THIS RUN EVALUATES, decided in one place (_shared/HarnessOrderCli.cs): the real profile, a --enable/--disable
// what-if, or a --order candidate file - and, in the same step, the ASSET VIEW that configuration implies, so a run
// whose plugins changed no longer reads the profile's meshes and BSAs regardless. The identity it produces is written
// into the emit, so a reader (and conflictdiff) can see exactly what was consumed instead of inferring it.
var cfg = HarnessOrderCli.Resolve(profileName, profileDir, modsDir, dataDir, overwriteDir, GamePath,
                                  orderFile, enablePlugins, disablePlugins);
if (cfg.Refused) { Console.Error.WriteLine(cfg.RefusedReason); return 2; }
var paths = cfg.OrderedPaths;
Console.WriteLine($"load order: {paths.Count} plugins ({sw.ElapsedMilliseconds} ms)\n");
HarnessOrderCli.Print(cfg);

// ---------------------------------------------------------------------------------------
// Pass 1 — every plugin once: placement history, quest graphs, master sets.
// ---------------------------------------------------------------------------------------
// Cell travels WITH the position. Two interiors have independent coordinate origins, so a
// distance between them is a meaningless number — the first run of this reported 125,000-unit
// "moves" that were simply a relocation into another cell. The history itself — snaps, the
// changed-only rule, the authoring view — lives in _shared/PlacementHistory.cs since 2026-09-01,
// shared with depprobe's placement-dependency pass.
var history = new PlacementHistory();
var aliasTargets = new AliasTargets(history);      // CHECK 1's question and its part of the walk (AliasTargets.cs)
var refBase = new Dictionary<FormKey, FormKey>();
var npcPlaced = new Dictionary<FormKey, List<FormKey>>();   // Npc base -> its placed references
var baseName = new Dictionary<FormKey, (string Type, string? Edid)>();
var cellName = new Dictionary<FormKey, (string? Edid, bool Interior)>();

var pluginKey = new ModKey[paths.Count];
// --bos on (default): the Base Object Swapper layer is a PSEUDO-PLUGIN after the last real one, every real plugin its
// master (BOS applies on top of the winner) — index paths.Count in the closure and the mover tables, named
// BosApplyReport.PseudoPluginName wherever a plugin is named. Its index fills during the walk below (BosFeed).
int bosPlugin = bosOn ? paths.Count : -1;
var masterIdx = new HashSet<int>[paths.Count + (bosOn ? 1 : 0)];
var bos = new BosFeed(bosOn);
var refArm = new Dictionary<FormKey, string>();          // ref -> its placed arm (BOS never swaps an actor)
var refEz = new Dictionary<FormKey, FormKey>();          // ref -> its own XEZN, where it has one (the leveled-swap rule)
string PluginName(int p) => p == bosPlugin ? BosApplyReport.PseudoPluginName : pluginKey[p].FileName.String;
var quests = new Dictionary<FormKey, QuestInfo>();
var scenes = new Dictionary<FormKey, (FormKey Quest, int[] Actors)>();
var packageTargets = new Dictionary<FormKey, int[]>();          // package -> alias ids it targets

// WHERE AN ACTOR LIVES (2026-08-31). CHECK 2 measures the distance between two AUTHORED
// placements, and the field test showed that only predicts runtime position for actors whose
// packages keep them at their editor location — which IS the placement. Freir sits 1,632u from
// hers seconds after cell load, in both arms of the A/B, so every pair involving her is noise.
// These two indexes carry the records needed to tell one case from the other; the rule that reads
// them is DetectorModel.ClassifyReach, owned by proofprobe cases.
var packFacts = new Dictionary<FormKey, PackFacts>();
var npcPackages = new Dictionary<FormKey, FormKey[]>();        // Npc -> its packages, PRIORITY order
var npcTemplated = new HashSet<FormKey>();                     // inherits packages from a template

// WHY a split happened (3.5.3). Classifying intent needs "what ELSE does the moving plugin
// touch": its narrative records and whose quest they belong to, its placement edits per cell, and
// a per-type census for the report. Collected for every plugin in the same pass — the movers are
// only known after CHECK 2 runs. The RULE that reads these is DetectorModel.ClassifyIntent,
// owned by proofprobe cases; everything here is I/O.
var narrativeQuests = new Dictionary<int, HashSet<FormKey>>(); // plugin -> quests its QUST/SCEN/DIAL records belong to
var narrativeCount = new int[paths.Count + (bosOn ? 1 : 0)];   // DIAL/INFO/SCEN/QUST records in the plugin (the pseudo-plugin's slot stays 0)
var recordCensus = new Dictionary<int, Dictionary<string, int>>();
var cellEdits = new Dictionary<int, Dictionary<FormKey, int>>(); // plugin -> cell -> placement edits

long placements = 0, questRecords = 0;
// Rule 6, applied to the plumbing (2026-08-27): unreadable plugins are counted and named, never
// silently skipped.
var unreadablePlugins = new List<string>();
__run.Feature("plugins", paths.Count, "plugins").Stage("scan", "reading plugins", paths.Count, "plugins");
// The per-plugin walk (DriftIndex.cs) fills the tables above.
var index = new DriftIndex(pluginKey, masterIdx, bos, aliasTargets, refBase, npcPlaced, baseName, cellName, refArm, refEz, quests, scenes,
                           packageTargets, packFacts, npcPackages, npcTemplated, narrativeQuests, narrativeCount, recordCensus, cellEdits);


for (int p = 0; p < paths.Count; p++)
{
    __run.Advance();
    ISkyrimModGetter mod;
    try { mod = SkyrimMod.CreateFromBinaryOverlay(paths[p], SkyrimRelease.SkyrimSE); }
    catch { unreadablePlugins.Add(Path.GetFileName(paths[p])); pluginKey[p] = ModKey.Null; masterIdx[p] = new HashSet<int>(); bos.Plugin(null); continue; }

    using (mod as IDisposable)
    {
        var (addedPlacements, addedQuestRecords) = index.Plugin(p, mod);
        placements += addedPlacements; questRecords += addedQuestRecords;
    }
    if ((p + 1) % 150 == 0) Console.WriteLine($"   ... {p + 1}/{paths.Count} plugins, {placements:N0} placements, {sw.Elapsed.TotalSeconds:0}s");
}

Console.WriteLine($"\nindexed {placements:N0} placements over {history.Count:N0} refs; "
                + $"{quests.Count:N0} winning quests of {questRecords:N0} records  ({sw.Elapsed.TotalSeconds:0.0}s)");
Console.WriteLine($"   plugins that could not be read (contribute nothing, counted per rule 6): {unreadablePlugins.Count:N0}"
                + (unreadablePlugins.Count > 0 ? "  — " + string.Join(", ", unreadablePlugins) : ""));

// The Base Object Swapper layer applied to THIS probe's history (_shared/BosHook.cs, the history overload): the effective
// base replaces refBase (recordBase keeps the record's) and an override that moves a reference is a placement step under
// the pseudo-plugin — otherwise CHECK 2 measures an anchor the game moved as if it stood still. Actors are never swapped
// (the reader's scope rule), so only object anchors — markers, doors, levers, furniture — can gain a step. --bos off: the
// record bases and placements, as every run before 2026-09-04. No rule changes; the summary says how much the input moved.
var recordBase = new Dictionary<FormKey, FormKey>();
var bosSwaps = new Dictionary<FormKey, BaseObjectSwaps.BosResolution>();
if (bosOn)
{
    masterIdx[bosPlugin] = BosHook.PseudoMasters(paths.Count);
    var bosLayer = bos.Load(BosHook.LooseRoots(overwriteDir, modsDir, dataDir, cfg.EnabledMods));
    var bosRep = BosHook.Apply(history, refBase, refArm, refEz, bos.Index!, bosLayer, bosPlugin, recordBase, bosSwaps);
    Console.WriteLine("   " + bos.LayerLine());
    foreach (var l in bosRep.Summary()) Console.WriteLine("   " + l);
}
else Console.WriteLine("   " + bos.LayerLine());
Console.WriteLine();

// TRANSITIVE master closure (2026-08-27). "What the quest's author saw" must include everything a
// declared master itself depends on — the author had the whole chain installed. The rule lives in
// _shared/DetectorModel.cs and is owned by proofprobe cases.
var masterClosure = DetectorModel.MasterClosure(masterIdx);

// Where an actor can stand, for the reach filter (AliasSplits.cs, ActorReach).
var reach = new ActorReach(packFacts, history.Winning, refBase, npcPackages, npcTemplated);

// WHERE A PACKAGE PUTS AN ACTOR, resolved against the winning order. Everything here is
// I/O — looking anchors up and measuring to them; the DECISION on what the numbers mean is
// DetectorModel.ClassifyReach, which proofprobe owns.
DetectorModel.PackageAnchor? AnchorOf(FormKey packageKey, Snap at) => reach.AnchorOf(packageKey, at);

// An alias resolves to a placed reference; only an ACTOR carries packages. Anything else — a
// marker, a container, a door — stays exactly where it was placed, so its placement predicts its
// position perfectly and its reach is zero.
(ReachVerdict Verdict, uint Radius, string Why) ReachOf(FormKey refKey, Snap at, bool byCoverage = false) => reach.ReachOf(refKey, at, byCoverage);

// --actor: the records behind one actor's reach, printed as they were read. This exists because
// the filter below is a claim about package data, and a claim about package data should be
// checkable against the package data without reading this file.
if (actorDump is not null)
{
    var hits = baseName.Where(kv => kv.Value.Type == "Npc"
                                 && (kv.Value.Edid?.Contains(actorDump, StringComparison.OrdinalIgnoreCase) ?? false))
                       .OrderBy(kv => kv.Value.Edid).ToList();
    Console.WriteLine($"==== --actor '{actorDump}': {hits.Count} NPC(s) ====");
    Console.WriteLine();
    // Where an anchor actually sits in the WINNING order — the fact that decides whether a
    // reference-anchored actor is bound to the same spot the placement is.
    string AnchorLabel(FormKey k)
    {
        if (Winning(k) is not { } w) return $"{k}(no placement)";
        return $"{k}@{CellOf(w.Cell)}({w.Pos.X:0},{w.Pos.Y:0},{w.Pos.Z:0})";
    }

    foreach (var (key, nm) in hits)
    {
        var pkgs = npcPackages.GetValueOrDefault(key, Array.Empty<FormKey>());
        var placements2 = npcPlaced.GetValueOrDefault(key, new List<FormKey>());
        foreach (var pr in placements2)
            if (Winning(pr) is { } wp)
                Console.WriteLine($"   placement {pr} @ {CellOf(wp.Cell)} ({wp.Pos.X:0},{wp.Pos.Y:0},{wp.Pos.Z:0})"
                                + $"  [winner {PluginName(wp.Plugin)}]");
        Console.WriteLine($"{nm.Edid}  {key}   packages: {pkgs.Length}"
                        + (npcTemplated.Contains(key) ? "   TEMPLATED (inherits packages)" : "")
                        + $"   placements: {placements2.Count}");
        for (int i = 0; i < pkgs.Length; i++)
        {
            if (!packFacts.TryGetValue(pkgs[i], out var f))
            { Console.WriteLine($"   [{i}] {pkgs[i]}  (package record not in the order)"); continue; }
            string tmpl = packFacts.TryGetValue(f.Template, out var tf) ? (tf.Edid ?? f.Template.ToString())
                        : f.Template.IsNull ? "(none)" : f.Template.ToString();
            string sched = f.Scheduled ? $"{f.ScheduleHour:00}:00 for {f.ScheduleMinutes / 60.0:0.#}h" : "any time";
            string locs = f.Locations.Count == 0 ? "(no location slot)"
                        : string.Join(", ", f.Locations.Select(l => $"{l.Arm} r={l.Radius}"
                              + (l.Link.IsNull ? "" : $"->{AnchorLabel(l.Link)}")));
            Console.WriteLine($"   [{i}] {f.Edid,-38} tmpl={tmpl,-22} {sched,-16} cond={f.Conditions}  {locs}");
        }
        foreach (var pr in placements2)
        {
            if (Winning(pr) is not { } at) continue;
            var byPriority = DetectorModel.ClassifyReach(pkgs.Select(k => AnchorOf(k, at)).ToArray(), npcTemplated.Contains(key));
            var byCoverage = DetectorModel.ClassifyReach(pkgs.Select(k => AnchorOf(k, at)).ToArray(), npcTemplated.Contains(key), byCoverage: true);
            Console.WriteLine($"   => {byPriority.Verdict}"
                            + (byPriority.Verdict == ReachVerdict.PlacementBound ? $", reach {byPriority.Radius}u" : "")
                            + $"   ({byPriority.Why})");
            Console.WriteLine($"      read as most-of-the-day instead: {byCoverage.Verdict}"
                            + (byCoverage.Verdict == ReachVerdict.PlacementBound ? $", reach {byCoverage.Radius}u" : "")
                            + $"   ({byCoverage.Why})");
        }
        Console.WriteLine();
    }
    return 0;
}

// ---------------------------------------------------------------------------------------
// Resolution helpers
// ---------------------------------------------------------------------------------------
Snap? AsSeenBy(FormKey refKey, int byPlugin) => history.AsSeenBy(refKey, byPlugin, masterClosure);
Snap? Winning(FormKey refKey) => history.Winning(refKey);

// An alias points at a thing that HAS a position only for some arms. UniqueActor names an NPC
// record, not a placement, so it is resolved through the placed-reference index — and only when
// that NPC has exactly one placement, because two placements mean no single answer.
// The player's reference is an engine constant with no placement record anywhere — quests alias it
// constantly, and reading that as "anchored to something that is gone" buries every real finding.
// Matched on the raw id, not a FormKey: a plugin with an unusual master list renders the same
// 0x14 against its OWN ModKey, and Bethesda reserves the low ids so nothing else can claim it.
FormKey? AliasRef(AliasInfo a) => AliasStated.AliasRef(a, npcPlaced);
string CellOf(FormKey c) => cellName.TryGetValue(c, out var v) ? (v.Edid ?? c.ToString()) + (v.Interior ? " (int)" : "") : c.ToString();
string NameOf(FormKey refKey)
{
    var bk = refBase.GetValueOrDefault(refKey);
    var bn = baseName.GetValueOrDefault(bk);
    var name = $"{bn.Type ?? "?"} {bn.Edid ?? bk.ToString()}";
    // A swapped base: the effective one is named, the record's kept beside it — the same row the swap layer's driver prints.
    if (recordBase.TryGetValue(refKey, out var rb))
    {
        var rn = baseName.GetValueOrDefault(rb);
        var res = bosSwaps.GetValueOrDefault(refKey);
        name += $" (BOS: record {rn.Type ?? "?"} {rn.Edid ?? rb.ToString()}{(res is { Random: true } ? $"; random, the first of {res.Alternatives.Count} alternatives" : "")})";
    }
    return name;
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
if (bosOn)
{
    // How much of the swap layer lands on what CHECK 2 measures: the references some alias is anchored to.
    var anchored = new HashSet<FormKey>();
    foreach (var q in quests.Values) foreach (var a in q.Aliases) if (AliasRef(a) is { } rk) anchored.Add(rk);
    int stepped = anchored.Count(k => history.Winning(k) is { } w && w.Plugin == bosPlugin);
    int swapped = anchored.Count(recordBase.ContainsKey);
    Console.WriteLine($"\n   BOS on the alias anchors: {anchored.Count:N0} references some alias is anchored to (ForcedReference, or a UniqueActor's one placement); "
                    + $"{stepped:N0} have a *_SWAP.ini override as their winning placement, {swapped:N0} carry a swapped base");
}

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
var statedPairs = AliasStated.Of(scenes, quests, packageTargets);   // quest -> unordered alias pairs (AliasSplits.cs)

int statedTotal = statedPairs.Values.Sum(v => v.Count);
Console.WriteLine();
Console.WriteLine($"stated relationships: {scenes.Count:N0} winning scenes, {packageTargets.Count:N0} packages with an alias target");
Console.WriteLine($"   -> {statedTotal:N0} alias pairs the data says belong together, across {statedPairs.Count:N0} quests");

// A published patch is evidence the conflict is already known and handled. If any plugin masters
// BOTH the quest's plugin and the plugin that moved things, someone has reconciled the two.
bool PatchExists(int questPlugin, int moverPlugin)
{
    if (questPlugin == moverPlugin) return true;
    // The BOS pseudo-plugin masters every plugin and patches nothing — it is never the reconciling third party, and a
    // move it made is one no plugin can have reconciled (a *_SWAP.ini is outside the records).
    for (int p = 0; p < masterIdx.Length; p++)
        if (p != questPlugin && p != moverPlugin && p != bosPlugin
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
        var brk = aliasTargets.Break(rk);
        bool objTarget = q.Objectives.Any(o => o.TargetAliasIds.Contains(a.Id));
        if (brk is { Kind: "MISSING" })
            broken.Add($"   MISSING   {q.Label} [{PluginName(q.Plugin)}] alias '{a.Name}' -> {rk} (no placement anywhere){(objTarget ? "   OBJECTIVE" : "")}");
        else if (brk is { Kind: "DELETED" } d)
            broken.Add($"   DELETED   {q.Label} [{PluginName(q.Plugin)}] alias '{a.Name}' -> {NameOf(rk)} deleted by {PluginName(d.Plugin)}{(objTarget ? "   OBJECTIVE" : "")}");
    }

aliasTargets.Dump(PluginName);
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
// design; the other three reasons are genuine blind spots and belong in the output. The reach
// filter's drops are counted by reason, and where the two readings of "governing" (the
// HIGHEST-PRIORITY package or the one SCHEDULED for most of the day) disagree, that is counted too.
// CHECK 2 is a function of the "together" threshold, so the sweep below can re-ask the question
// at other settings over the same indexed data. The review showed the shortlist is threshold-
// sensitive and non-monotonic (no-patch count 7 / 4 / 8 at 300 / 600 / 1200), which no single
// number reveals. Each call returns its own counts; the console and the emit print this call's.
var splits = new AliasSplits(quests, npcPlaced, statedPairs, reachFilter, reach, AliasRef, AsSeenBy, Winning, PatchExists, PluginName, NameOf, CellOf);
var (split, splitCoverage) = splits.Find(near);

Console.WriteLine();
Console.WriteLine($"==== CHECK 2: a quest's own aliases came apart (were within {near:0}u, same cell) ====");
Console.WriteLine($"   ---- aliases the check could not judge (counted, not cleared) ----");
Console.WriteLine($"   non-positional arm (runtime-resolved, location, flags-only, player) : {splitCoverage.SkipNonPositional:N0}");
Console.WriteLine($"   UniqueActor with several placements (no single answer)              : {splitCoverage.SkipMultiPlacement:N0}");
Console.WriteLine($"   no authoring-view position (quest plugin + closure contributed none): {splitCoverage.SkipNoAuthoringView:N0}");
Console.WriteLine($"   no winning placement at all                                         : {splitCoverage.SkipNoWinning:N0}");
if (reachFilter)
{
    Console.WriteLine($"   ---- pairs the REACH filter removed (2026-08-31; --no-reach to see them) ----");
    Console.WriteLine($"   the placement does not predict where the actor stands:");
    foreach (var kv in splitCoverage.ReachDropped.OrderByDescending(k => k.Value))
        Console.WriteLine($"      {kv.Key,-28} : {kv.Value:N0}");
    if (splitCoverage.ReachDropped.Count == 0) Console.WriteLine("      (none)");
    Console.WriteLine($"   both actors bound, but their reachable areas still OVERLAP             : {splitCoverage.ReachOverlap:N0}");
    Console.WriteLine($"   pairs where the two readings of \"governing\" DISAGREE                    : {splitCoverage.ReachReadingDisagrees:N0}"
                    + "   <- highest-priority vs most-of-the-day; --actor <name> shows both");
}
else Console.WriteLine("   ---- REACH filter OFF (--no-reach): pairs below are raw placement distance ----");
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
// INTENT — why did the moving plugin separate each STATED pair? (roadmap 3.5.3)
// ---------------------------------------------------------------------------------------
// Detection without explanation moves the work rather than doing it: the user is left to find out
// whether the move was deliberate. The rule table from RECOMMENDATIONS-2026-08-30 is frozen as
// DetectorModel.ClassifyIntent (proofprobe): classify by what ELSE the moving plugin touches. The
// RECORDS decide; the author's cached page description (meta.ini nexusDescription — source 2) is
// corroboration a human reads, quoted in excerpt, and never outvotes them. A missing description
// is counted (rule 6), never read as evidence. A wild (accidental) edit is a CANDIDATE flag that
// asks for corroboration — never asserted from records alone.
{
    var stated = split.Where(s => s.Stated).ToList();
    Console.WriteLine();
    Console.WriteLine($"==== INTENT: why the moving plugin separated each STATED pair ({stated.Count}) ====");
    var corroboration = new Dictionary<string, int>();
    foreach (var s in stated)
    {
        Console.WriteLine();
        Console.WriteLine($"   {s.Quest}  [{s.QuestPlugin}]  '{s.Alias1}' + '{s.Alias2}'{(s.Patched ? "   (patched)" : "")}");
        if (s.MoverIdxList.Count == 0)
        { Console.WriteLine("      no mover to classify (the winning positions come from the pair's own masters)"); continue; }
        foreach (var m in s.MoverIdxList)
        {
            if (m == bosPlugin)
            {
                // The mover is the swap layer: an ini line, not a plugin with records to classify. The line is named; the
                // intent is the ini author's, and the corroboration tally counts the row apart.
                Console.WriteLine($"      mover {BosApplyReport.PseudoPluginName}");
                Console.WriteLine("      -> (not classified: a *_SWAP.ini transform moved the anchor — the line below is the whole of the evidence)");
                foreach (var mk in s.MovedRefs)
                    if (bosSwaps.TryGetValue(mk, out var bres)) Console.WriteLine($"         evidence [ini]: {mk}  {BosHook.Describe(bres)}");
                Bump(corroboration, "SwapLayer");
                continue;
            }
            var cells = cellEdits.GetValueOrDefault(m);
            var affected = s.AffectedCells.ToHashSet();
            int inAffected = affected.Sum(c => cells?.GetValueOrDefault(c) ?? 0);
            int placedTotal = cells?.Values.Sum() ?? 0;
            var otherCells = cells is null ? new List<FormKey>() : cells.Keys.Where(c => !affected.Contains(c)).ToList();
            bool otherExtOnly = otherCells.Count > 0
                             && otherCells.All(c => cellName.TryGetValue(c, out var oc) && !oc.Interior);
            bool interior = cellName.TryGetValue(s.AffectedCells[0], out var thenCell) && thenCell.Interior;
            var facts = new DetectorModel.IntentFacts(
                narrativeQuests.GetValueOrDefault(m)?.Contains(s.QuestKey) ?? false,
                narrativeCount[m], placedTotal, inAffected, s.MovedRefs.Count, interior, otherExtOnly);
            var (verdict, wildCandidate, why) = DetectorModel.ClassifyIntent(facts);

            var census = recordCensus.GetValueOrDefault(m);
            string censusLine = census is null ? "(no records read)"
                : $"{census.Values.Sum():N0} records: "
                + string.Join(", ", census.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Value} {kv.Key}"))
                + (census.Count > 6 ? ", …" : "");
            Console.WriteLine($"      mover {PluginName(m)}");
            Console.WriteLine($"      -> {verdict}   ({why})");
            Console.WriteLine($"         evidence [records]: {censusLine}");
            Console.WriteLine($"         evidence [records]: narrative (DIAL/INFO/SCEN/QUST): {narrativeCount[m]}; "
                            + $"placement edits: {placedTotal} total, {inAffected} in the pair's cell(s); "
                            + $"other cells: {otherCells.Count}{(otherCells.Count > 0 ? (otherExtOnly ? ", all exterior" : ", some interior") : "")}");
            if (wildCandidate)
                Console.WriteLine("         possible WILD EDIT — the plugin touches nothing in the pair's cell(s) beyond the moved\n"
                                + "         ref(s). Candidate only: corroborate (position sanity, stated scope) before asserting.");

            // Source 2 — the author's own statement of purpose, cached offline by MO2 beside the
            // plugin's winning file. Only the finding's movers are ever read (a description is
            // median ~4.5 KB and one mod in four has none). Both sides go through GetFullPath:
            // GetDirectoryName normalizes separators, and a harness config written with forward
            // slashes made every mover read as "no MO2 mod folder" until the roots matched.
            var moverDir = Path.GetDirectoryName(Path.GetFullPath(paths[m]));
            string? metaPath = moverDir is not null
                && moverDir.StartsWith(Path.GetFullPath(modsDir), StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(moverDir, "meta.ini") : null;
            bool metaFound = metaPath is not null && File.Exists(metaPath);
            var desc = metaFound ? Mo2ModMeta.ReadDescription(metaPath!) : null;
            var corr = DetectorModel.CorroborationOf(metaFound, desc);
            Bump(corroboration, corr.ToString());
            Console.WriteLine(corr switch
            {
                IntentCorroboration.DescriptionOnFile =>
                    $"         evidence [meta.ini]: nexusDescription on file ({desc!.Length:N0} chars): \"{Excerpt(desc, 160)}\"",
                IntentCorroboration.NoDescription =>
                    "         evidence [meta.ini]: no nexusDescription — corroboration unavailable (counted; not evidence of anything)",
                _ => "         evidence [meta.ini]: no MO2 mod folder (vanilla/base or overwrite) — corroboration unavailable (counted)",
            });
        }
    }
    if (stated.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("   ---- corroboration availability (rule 6: unjudgeable is counted, never 'no intent') ----");
        foreach (var kv in corroboration.OrderByDescending(k => k.Value))
            Console.WriteLine($"   {kv.Key,-20} : {kv.Value}");
    }
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
    var perSetting = settings.Select(v => splits.Find(v).Split
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
    foreach (var (_, h) in history.Refs)
    {
        if (h.Count < 2) continue;
        changed++;
        var first = h[0]; var last = h[^1];
        switch (PlacementHistory.Classify(first, last))
        {
            case Movement.Relocated: relocated++; break;
            case Movement.MovedFar: movedFar++; break;
            default: continue;
        }
        if (last.Plugin != first.Plugin)
            Bump(moverPairs, $"{PluginName(first.Plugin)}  -> moved by ->  {PluginName(last.Plugin)}");
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
    foreach (var (s, necessary) in AliasSplits.Emitted(split))
    {
        var alternative = new List<ConflictRecord> { new(s.QuestKey.ToString(), "quest", s.Quest) };

        foreach (var mover in s.MoverList.DefaultIfEmpty(""))
            conflicts.Add(new Conflict(
                "SPLIT", s.QuestPlugin, mover,
                $"quest '{s.Quest}' aliases '{s.Alias1}' and '{s.Alias2}' were {s.WasApart:N0}u apart in {s.CellThen}, "
              + (s.DifferentCell ? $"now in different cells ({s.CellNow1} vs {s.CellNow2})" : $"now {s.NowApart:N0}u apart"),
                necessary, alternative));
    }

    // What this run judged and what it could not, in the shape every other emitting probe writes (Part B of the
    // 2026-09-12 handoff). Without it an empty Conflicts list is indistinguishable from a run that judged nothing,
    // and a consumer has to guess which it was — the reading the evaluation controller refuses as coverage-missing.
    // Every number here is one the console already prints; nothing new is computed.
    List<CoverageLine> coverage =
    [
        new("plugins", paths.Count - unreadablePlugins.Count,
            new Dictionary<string, long> { ["UnreadablePlugin"] = unreadablePlugins.Count }),
        new("winning quests with stated alias pairs", statedPairs.Count, null, null,
            $"{statedTotal:N0} stated alias pair(s), from {scenes.Count:N0} winning scene(s) and "
          + $"{packageTargets.Count:N0} package(s) with an alias target"),
        // The split test's two lines are the reported threshold's call alone (AliasSplits.cs), never the sweep's.
        .. splitCoverage.Lines(reachFilter, split),
    ];

    ConflictIo.Write(emitPath, new ConflictFile("driftprobe", Instance,
        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"), conflicts, coverage,
        // driftprobe's population is placement history and package reach: no enable filter is applied and none belongs
        // here (an actor dormant at load is still an actor the order places). Labelled so a reader is never left to guess.
        Population: "n/a (placement history; no enable-state filter)", State: "n/a",
        Configuration: cfg.Identity("n/a (placement history; no enable-state filter)", "n/a")));
    Console.WriteLine($"\nemitted {conflicts.Count:N0} conflicts ({conflicts.Count(c => c.B.Length == 0)} with no second party to name) -> {emitPath}");
}

Console.WriteLine($"\nelapsed {sw.Elapsed.TotalSeconds:0.0}s");
return 0;

static void Bump(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;

// For QUOTING a description in a report line: strip BBCode/HTML markup and literal \n escapes,
// collapse whitespace, cut at max. Display only — the classification never parses the text.
static string Excerpt(string s, int max)
{
    s = System.Text.RegularExpressions.Regex.Replace(s, @"\[/?[a-zA-Z][^\]]*\]|<[^>]+>", " ");
    s = s.Replace("\\r", " ").Replace("\\n", " ").Replace("\\t", " ");
    s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
    return s.Length <= max ? s : s[..max] + "…";
}

record Split(string Quest, string QuestPlugin, string? Alias1, string? Alias2, string Name1, string Name2,
             string CellThen, string CellNow1, string CellNow2, float WasApart, float NowApart,
             bool DifferentCell, string Movers, bool IsObjectiveTarget, bool Stated, bool Patched,
             string? ObjectiveText,
             // For --emit: the exact records a fix would have to touch, and who to look a patch up
             // against. MovedRefs is the SHORT list — a repair only has to reconcile the placement
             // that actually changed, not both ends of the pair.
             IReadOnlyList<FormKey> MovedRefs, IReadOnlyList<FormKey> AllRefs,
             FormKey QuestKey, IReadOnlyList<string> MoverList,
             // For the intent join (3.5.3): the movers as plugin INDICES, and the cells the pair
             // occupied/occupies — authoring cell FIRST (its interior flag feeds scope mismatch).
             IReadOnlyList<int> MoverIdxList, IReadOnlyList<FormKey> AffectedCells);

// PackFacts — the package location-slot extraction — moved to _shared/PackFacts.cs (2026-09-02),
// shared with navownprobe's needs-witness join. AnchorOf above stays here: it is bound to this
// probe's placement history and aggregates per package, which the join does not want.

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
