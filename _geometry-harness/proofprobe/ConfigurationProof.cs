using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace ProofProbe;

// PROOF for the configuration identity, the candidate resolver and the differential's configuration gate
// (roadmap 4.20, 2026-09-11).
//
// The claim under test is narrow and load-bearing: an emit says what it consumed, and a comparison that cannot
// state what it is comparing does not run. Everything here is a case that would have passed silently before,
// which is the reason each one exists:
//
//   - the fingerprint covers ORDER, not just membership. Two orders with the same winners are two different
//     configurations, because a history-dependent detector (an overlap row is Introduced or Inherited by who
//     came BEFORE) answers differently under each. A fingerprint over winners would have licensed reuse
//     across them, so the cases pin that both order swaps and provider-priority swaps move the hash.
//   - a candidate the engine could not load is REFUSED by name, never approximated. SyntheticOrder learned
//     this on 2026-08-27, when enabling a patch whose master was absent "repaired" nine records.
//   - the parts the identity does NOT model are in Limits, not in silence: a plugin read out of a folder the
//     asset view does not contain, a candidate that re-orders plugins while keeping the profile's mods.
//   - the gate refuses an unlabelled emit and refuses two probes, and it NEVER refuses a named difference —
//     that difference is what a differential is of.
//   - and the case with no honest verdict: identical configurations, different rows.
//
// The fixture is a hand-built MO2 layout with real plugins written by Mutagen: no game data, no archives, no
// installed instance, so this runs on a fresh checkout like every other proof here.

public static class ConfigurationProof
{
    const SkyrimRelease Release = SkyrimRelease.SkyrimSE;

    public static void Run(Action<string, bool, string> check)
    {
        Console.WriteLine();
        Console.WriteLine("---- configuration identity, candidate resolution and the differential gate (4.20) ----");

        HandBuilt(check);
        OnDisk(check);
    }

    // -------------------------------------------------------------------------------------------
    //  Part 1 — the identity's own arithmetic and the gate. Hand-built records, no disk.
    // -------------------------------------------------------------------------------------------
    static void HandBuilt(Action<string, bool, string> check)
    {
        // --- row identity is case-insensitive (2026-09-11): a filename's letter case differs between providers on the
        //     lab and split one navownprobe row into GONE + NEW, turning a no-op --enable into DIFFERENTIAL-RECLASSIFIED ---
        check("row identity: plugin-name and FormKey letter case do not split a row",
              ConflictIo.RowIdentity("CLOBBERED", "000ABC:ccBGSSSE025-AdvDSGS.esm", null, "ccBGSSSE025-AdvDSGS.esm")
              == ConflictIo.RowIdentity("CLOBBERED", "000abc:ccbgssse025-advdsgs.esm", null, "ccbgssse025-advdsgs.esm"),
              "one key for both spellings");
        check("row identity: an unordered pair is one key whatever the case or the side",
              ConflictIo.RowIdentity("OVERLAP", "000AAA:A.esp", "000BBB:b.ESP", "x") == ConflictIo.RowIdentity("OVERLAP", "000bbb:B.esp", "000aaa:a.ESP", "y"),
              "pair keys equal");

        static ConfigPlugin P(string name, string sha, string provider = "ModA", bool master = false)
            => new(name, $@"C:\x\{name}", sha, 100, provider, master, false, Array.Empty<string>());
        static ConfigProvider V(string name) => new(name, "mod");

        var plugins = new[] { P("Base.esm", "aaa", master: true), P("Mid.esp", "bbb"), P("Top.esp", "ccc") };
        var providers = new[] { new ConfigProvider("overwrite", "overwrite"), V("ModA"), V("ModB"), new ConfigProvider("Data", "data") };
        var overlays = new[] { new ConfigOverlay("bos-ini", @"C:\x\A_SWAP.ini", "111") };
        var id = ConfigurationIdentity.Compute("profile:P", plugins, providers, overlays, "load-time", "baseline");

        // --- round trip through the emit format ---
        var tmp = Path.Combine(Path.GetTempPath(), $"hc-configproof-{Guid.NewGuid():N}.json");
        ConflictIo.Write(tmp, new ConflictFile("navownprobe", "inst", "t", Array.Empty<Conflict>(),
                                               Population: "load-time", State: "baseline", Configuration: id));
        var back = ConflictIo.Read(tmp).Configuration;
        try { File.Delete(tmp); } catch { }
        check("config/io: the identity round-trips through the emit (fingerprint, plugins, providers, overlays, limits, unknown)",
              back is not null && back.Fingerprint == id.Fingerprint && back.Plugins.Count == 3 && back.Providers.Count == 4
              && back.Overlays.Count == 1 && back.Plugins[2].Name == "Top.esp" && back.Plugins[0].MasterFlag
              && back.Unknown.Count == ConfigurationIdentity.DefaultUnknown.Count && back.SchemaVersion == ConfigurationIdentity.CurrentSchema,
              back is null ? "null after read" : $"{back.Fingerprint[..12]} {back.Plugins.Count}p/{back.Providers.Count}v/{back.Overlays.Count}o");

        // --- the fingerprint is order-sensitive, on both axes ---
        var reordered = ConfigurationIdentity.Compute("profile:P", new[] { plugins[1], plugins[0], plugins[2] }, providers, overlays, "load-time", "baseline");
        check("config/fingerprint: same plugins and the SAME WINNER, two orders -> different fingerprints",
              reordered.Fingerprint != id.Fingerprint && reordered.Plugins[^1].Name == id.Plugins[^1].Name,
              $"{id.Fingerprint[..12]} vs {reordered.Fingerprint[..12]}, winner {id.Plugins[^1].Name} both");

        var provSwap = ConfigurationIdentity.Compute("profile:P", plugins, new[] { providers[0], providers[2], providers[1], providers[3] }, overlays, "load-time", "baseline");
        check("config/fingerprint: identical plugins, asset priority reversed -> different fingerprints",
              provSwap.Fingerprint != id.Fingerprint, $"{id.Fingerprint[..12]} vs {provSwap.Fingerprint[..12]}");

        var popSwap = ConfigurationIdentity.Compute("profile:P", plugins, providers, overlays, "flags", "baseline");
        check("config/fingerprint: same configuration, different population -> different fingerprints",
              popSwap.Fingerprint != id.Fingerprint, $"{id.Fingerprint[..12]} vs {popSwap.Fingerprint[..12]}");

        // --- the delta says what changed, in words a reader can act on ---
        var moved = ConfigurationIdentity.Compute("profile:P", new[] { plugins[0], plugins[2], plugins[1] }, providers, overlays, "load-time", "baseline");
        var dMoved = id.DeltaTo(moved);
        check("config/delta: a plugin moved -> one 'plugin order changed' line naming the flipped pair",
              dMoved.Count == 1 && dMoved[0].StartsWith("plugin order changed") && dMoved[0].Contains("Mid.esp"),
              string.Join(" | ", dMoved));

        var rehashed = ConfigurationIdentity.Compute("profile:P", new[] { plugins[0], plugins[1], P("Top.esp", "zzz") }, providers, overlays, "load-time", "baseline");
        var dHash = id.DeltaTo(rehashed);
        check("config/delta: one plugin's content changed -> 'plugin content changed' naming it, nothing else",
              dHash.Count == 1 && dHash[0] == "plugin content changed: Top.esp", string.Join(" | ", dHash));

        var dProv = id.DeltaTo(provSwap);
        check("config/delta: the provider list reordered -> 'asset providers changed: priority reordered'",
              dProv.Count == 1 && dProv[0].Contains("asset providers changed") && dProv[0].Contains("priority reordered"),
              string.Join(" | ", dProv));

        var overlaid = ConfigurationIdentity.Compute("profile:P", plugins, providers,
                                                    new[] { new ConfigOverlay("bos-ini", @"C:\x\A_SWAP.ini", "222") }, "load-time", "baseline");
        var dOver = id.DeltaTo(overlaid);
        check("config/delta: a runtime overlay's content changed -> 'overlays changed'",
              dOver.Count == 1 && dOver[0].StartsWith("overlays changed"), string.Join(" | ", dOver));

        var dropped = ConfigurationIdentity.Compute("profile:P", new[] { plugins[0], plugins[1] }, providers, overlays, "load-time", "baseline");
        var dDrop = id.DeltaTo(dropped);
        check("config/delta: a plugin removed -> named, with the position it held",
              dDrop.Any(l => l.StartsWith("plugin removed: Top.esp")), string.Join(" | ", dDrop));

        check("config/delta: an identity against itself is empty", id.DeltaTo(id).Count == 0, $"{id.DeltaTo(id).Count} lines");

        // --- the gate ---
        static ConflictFile F(string probe, ConfigurationIdentity? c) => new(probe, "inst", "t", Array.Empty<Conflict>(), Configuration: c);

        var gNullBase = ConfigurationGate.Check(F("navownprobe", null), F("navownprobe", id));
        check("gate: the BASE emit names no configuration -> REFUSED, saying which side and why",
              gNullBase.Refused && gNullBase.Lines.Any(l => l.Contains("base emit does not say"))
              && gNullBase.Lines.Any(l => l.Contains("UNKNOWN, not")),
              string.Join(" ", gNullBase.Lines).Substring(0, Math.Min(90, string.Join(" ", gNullBase.Lines).Length)));

        var gNullChanged = ConfigurationGate.Check(F("navownprobe", id), F("navownprobe", null));
        check("gate: the CHANGED emit names no configuration -> REFUSED", gNullChanged.Refused
              && gNullChanged.Lines.Any(l => l.Contains("changed emit does not say")), $"refused={gNullChanged.Refused}");

        var gBoth = ConfigurationGate.Check(F("navownprobe", null), F("navownprobe", null));
        check("gate: NEITHER emit names one -> REFUSED (an unlabelled pair is not a matching pair)",
              gBoth.Refused && gBoth.Lines.Any(l => l.Contains("neither emit says")), $"refused={gBoth.Refused}");

        var gProbe = ConfigurationGate.Check(F("navownprobe", id), F("overlapprobe", id));
        check("gate: two DIFFERENT probes -> REFUSED, naming both",
              gProbe.Refused && gProbe.Lines[0].Contains("navownprobe") && gProbe.Lines[0].Contains("overlapprobe"), gProbe.Lines[0][..60]);

        var gDiff = ConfigurationGate.Check(F("navownprobe", id), F("navownprobe", moved));

        var overlap = F("overlapprobe", id) with { MeasurementPolicy = "render-vertices-v1" };
        foreach (var missing in new[] { (Base: F("overlapprobe", id), Changed: overlap),
                                        (Base: overlap, Changed: F("overlapprobe", moved)),
                                        (Base: F("overlapprobe", id), Changed: F("overlapprobe", moved)) })
        {
            var g = ConfigurationGate.Check(missing.Base, missing.Changed);
            check("gate: unknown overlap measurement policy refuses repair credit",
                  g.Refused && g.Lines.Any(l => l.Contains("measurement policies cannot establish repair credit")),
                  string.Join(" ", g.Lines));
        }
        var changedPolicy = ConfigurationGate.Check(overlap, overlap with { MeasurementPolicy = "render-vertices-v2", Configuration = moved });
        check("gate: changed overlap policy refuses even when plugin configuration changed",
              changedPolicy.Refused && changedPolicy.Lines.Any(l => l.Contains("render-vertices-v2")), string.Join(" ", changedPolicy.Lines));
        var samePolicy = ConfigurationGate.Check(overlap, overlap with { Configuration = moved });
        check("gate: matching overlap policy permits a named plugin configuration difference",
              !samePolicy.Refused && samePolicy.Delta.Count == 1, string.Join(" ", samePolicy.Lines));
        var historicalBounds = overlap with { MeasurementPolicy = HousecarlCore.NifOverlapGeometry.MeasurementPolicy };
        var actualShape = overlap with { MeasurementPolicy = HousecarlCore.NifOverlapGeometry.SurfaceMeasurementIdentity(1e-5), Configuration = moved };
        var shapeGate = ConfigurationGate.Check(historicalBounds, actualShape);
        check("gate: historical bounds and actual-shape results cannot receive cross-method repair credit",
              shapeGate.Refused, string.Join(" ", shapeGate.Lines));
        check("gate: actual-shape identity includes the reader and independent mesh predicate",
              actualShape.MeasurementPolicy!.Contains(HousecarlCore.NifOverlapGeometry.ReaderVersion, StringComparison.Ordinal)
              && actualShape.MeasurementPolicy.Contains(HousecarlCore.MeshIntersection.PolicyVersion, StringComparison.Ordinal),
              actualShape.MeasurementPolicy!);
        var changedTolerance = actualShape with { MeasurementPolicy = HousecarlCore.NifOverlapGeometry.SurfaceMeasurementIdentity(.1) };
        check("gate: changing contact tolerance cannot manufacture repair credit",
              ConfigurationGate.Check(actualShape, changedTolerance).Refused
              && !ConfigurationGate.Check(actualShape, actualShape).Refused,
              $"base={actualShape.MeasurementPolicy}; changed={changedTolerance.MeasurementPolicy}");
        check("gate: a NAMED configuration difference is labelled, never refused — the delta is what the differential is of",
              !gDiff.Refused && gDiff.Delta.Count == 1 && gDiff.Lines.Any(l => l.Contains("configuration delta"))
              && gDiff.Lines.Any(l => l.Contains(id.Fingerprint[..12])) && gDiff.Lines.Any(l => l.Contains(moved.Fingerprint[..12])),
              $"refused={gDiff.Refused} delta={gDiff.Delta.Count} lines={gDiff.Lines.Count}");

        var gSame = ConfigurationGate.Check(F("navownprobe", id), F("navownprobe", id));
        check("gate: identical configurations + identical rows -> not refused, empty delta, not nondeterministic",
              !gSame.Refused && gSame.Delta.Count == 0 && !ConfigurationGate.Nondeterministic(gSame, anyRowDiffers: false),
              $"delta={gSame.Delta.Count}");

        check("gate: identical configurations that produced DIFFERENT rows -> nondeterministic (DIFFERENTIAL-UNJUDGED)",
              ConfigurationGate.Nondeterministic(gSame, anyRowDiffers: true), "empty delta + differing rows");

        check("gate: a real configuration difference with differing rows is NOT nondeterminism",
              !ConfigurationGate.Nondeterministic(gDiff, anyRowDiffers: true), $"delta={gDiff.Delta.Count}");

        check("gate: a refused pair is never reported as nondeterminism",
              !ConfigurationGate.Nondeterministic(gNullBase, anyRowDiffers: true), $"refused={gNullBase.Refused}");
    }

    // -------------------------------------------------------------------------------------------
    //  Part 2 — candidate resolution over a real MO2 layout, and the asset view that follows it.
    // -------------------------------------------------------------------------------------------
    static void OnDisk(Action<string, bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hc-configproof-{Guid.NewGuid():N}");
        try
        {
            var (instance, game, profile) = BuildLayout(root);
            string profileDir = Path.Combine(instance, "profiles", profile);
            string modsDir = Path.Combine(instance, "mods");
            string overwriteDir = Path.Combine(instance, "overwrite");
            string dataDir = Path.Combine(game, "Data");

            ResolvedConfiguration R(string? order, IEnumerable<string>? enable = null)
                => HarnessOrderCli.Resolve(profile, profileDir, modsDir, dataDir, overwriteDir, game,
                                           order, enable?.ToList(), null);

            string Candidate(string name, IReadOnlyList<string> plugins, IReadOnlyList<string>? mods = null)
            {
                var path = Path.Combine(root, name + ".json");
                new CandidateFile(ConfigurationIdentity.CurrentSchema, name, profile, plugins, mods,
                                  Array.Empty<string>(), null).Write(path);
                return path;
            }

            // --- the profile itself ---
            var prof = R(null);
            check("candidate/profile: the real profile resolves to its five active plugins, winner last",
                  !prof.Refused && prof.Plugins.Count == 5 && prof.Plugins[0].Name == "Base.esm" && prof.Plugins[^1].Name == "Child.esp"
                  && prof.Source == $"profile:{profile}",
                  $"{prof.Source} {string.Join(" < ", prof.Plugins.Select(p => p.Name))}");
            check("candidate/profile: the providers are overwrite, the enabled mods in priority, then Data",
                  prof.Providers.Select(p => p.Name).SequenceEqual(new[] { "overwrite", "ModB", "ModA", "ModC", "Data" })
                  && prof.Providers[0].Kind == "overwrite" && prof.Providers[^1].Kind == "data",
                  string.Join(" > ", prof.Providers.Select(p => p.Name)));
            check("candidate/profile: nothing is out of view, so Limits is empty", prof.Limits.Count == 0, $"{prof.Limits.Count} limits");

            // --- THE PRECEDENCE THE 2026-09-11 LAB FINDING WAS ABOUT ---
            // A plugin the game's Data folder provides AND a switched-off mod folder also provides must resolve to
            // DATA, on every arm. It did not until 2026-09-11: SyntheticOrder ranked the disabled folders above Data,
            // so the baseline read one copy and every --enable arm read the other, and on a profile with Creation Club
            // plugins in both places that put their different bytes inside what was reported as one differential.
            var synArm = R(null, enable: new[] { "Off.esp" });
            var candArm = R(Candidate("prec", new[] { "Base.esm", "Shared.esp", "Both.esp", "Parent.esp", "Child.esp" }, new[] { "ModB", "ModA", "ModC" }));
            ConfigPlugin Of(ResolvedConfiguration c, string n) => c.Plugins.First(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase));
            var sharedArms = new[] { Of(prof, "Shared.esp"), Of(synArm, "Shared.esp"), Of(candArm, "Shared.esp") };
            check("precedence: a plugin in BOTH the game Data folder and a switched-off mod resolves to DATA on the baseline, a --enable arm and a --order candidate",
                  sharedArms.All(p => p.Provider == "Data"),
                  string.Join(" / ", sharedArms.Select(p => p.Provider)));
            check("precedence: and the three arms therefore read the SAME BYTES of it (this is the defect's whole content)",
                  sharedArms.Select(p => p.Sha256).Distinct().Count() == 1 && sharedArms[0].Sha256 != "unreadable",
                  $"{sharedArms[0].Sha256[..12]} x3");
            var bothArms = new[] { Of(prof, "Both.esp"), Of(synArm, "Both.esp"), Of(candArm, "Both.esp") };
            check("precedence: an ENABLED mod still outranks a switched-off folder for the same filename, on all three arms",
                  bothArms.All(p => p.Provider == "ModA") && bothArms.Select(p => p.Sha256).Distinct().Count() == 1,
                  string.Join(" / ", bothArms.Select(p => p.Provider)));
            var synDelta = prof.Identity("load-time", "baseline").DeltaTo(synArm.Identity("load-time", "baseline"));
            check("precedence: the baseline and a real --enable arm differ ONLY by the plugin that was switched on — no plugin's CONTENT moves",
                  synArm.Plugins.Count == prof.Plugins.Count + 1 && synDelta.Count == 1 && synDelta[0].StartsWith("plugin added: Off.esp")
                  && !synDelta.Any(l => l.Contains("content changed")),
                  $"{prof.Plugins.Count} -> {synArm.Plugins.Count}: {string.Join(" | ", synDelta)}");

            // --- a candidate that spells the profile's own order round-trips to the same fingerprint ---
            var same = R(Candidate("same", new[] { "Base.esm", "Shared.esp", "Both.esp", "Parent.esp", "Child.esp" }, new[] { "ModB", "ModA", "ModC" }));
            check("candidate/round-trip: a candidate spelling the profile's own order and mods has the profile's fingerprint",
                  !same.Refused && same.Identity("load-time", "baseline").Fingerprint == prof.Identity("load-time", "baseline").Fingerprint
                  && same.Source.StartsWith("candidate:"),
                  $"{same.Source} {same.Identity("load-time", "baseline").Fingerprint[..12]} == {prof.Identity("load-time", "baseline").Fingerprint[..12]}");

            // --- Mods omitted: the asset view stays the profile's, and the identity SAYS so ---
            var noMods = R(Candidate("nomods", new[] { "Base.esm", "Shared.esp", "Both.esp", "Parent.esp", "Child.esp" }));
            check("candidate/limits: a candidate with no Mods keeps the profile's asset priority and records that limit",
                  !noMods.Refused && noMods.Limits.Any(l => l.Contains("keeps the base profile's asset priority"))
                  && noMods.EnabledMods.SequenceEqual(prof.EnabledMods),
                  noMods.Limits.Count > 0 ? noMods.Limits[0][..60] : "(no limits)");

            // --- a plugin that lives in a switched-off folder: allowed, and named as out of view ---
            var offFolder = R(Candidate("off", new[] { "Base.esm", "Parent.esp", "Child.esp", "Extra.esp" }, new[] { "ModB", "ModA", "ModC" }));
            check("candidate/limits: a plugin resolved from a DISABLED folder loads its records and none of its assets — said in Limits, naming the folder and the plugin",
                  !offFolder.Refused && offFolder.Limits.Any(l => l.Contains("'ModOff'") && l.Contains("Extra.esp") && l.Contains("NOT in this")),
                  offFolder.Limits.FirstOrDefault(l => l.Contains("ModOff"))?[..70] ?? "(missing)");
            check("candidate/limits: a plugin the profile's loadorder.txt never lists is ALLOWED under --order (the position is given) and recorded",
                  !offFolder.Refused && offFolder.Limits.Any(l => l.Contains("Extra.esp") && l.Contains("loadorder.txt does not list")),
                  offFolder.Limits.FirstOrDefault(l => l.Contains("does not list"))?[..70] ?? "(missing)");

            // --- the four refusals, each naming the plugin ---
            var unknown = R(Candidate("unknown", new[] { "Base.esm", "Parent.esp", "Nope.esp" }));
            check("candidate/refuse: a name nothing provides -> REFUSED, naming it",
                  unknown.Refused && unknown.RefusedReason!.Contains("'Nope.esp'") && unknown.RefusedReason.Contains("no mod folder"),
                  unknown.RefusedReason?.Split('\n')[^1][..Math.Min(80, unknown.RefusedReason.Split('\n')[^1].Length)] ?? "");

            var beforeMaster = R(Candidate("beforemaster", new[] { "Base.esm", "Child.esp", "Parent.esp" }));
            check("candidate/refuse: a plugin ahead of its own master -> REFUSED, naming both and their positions",
                  beforeMaster.Refused && beforeMaster.RefusedReason!.Contains("'Child.esp'") && beforeMaster.RefusedReason.Contains("'Parent.esp'")
                  && beforeMaster.RefusedReason.Contains("ahead of its master"),
                  beforeMaster.RefusedReason?.Split('\n')[^1][..Math.Min(80, beforeMaster.RefusedReason.Split('\n')[^1].Length)] ?? "");

            var missingMaster = R(Candidate("missingmaster", new[] { "Base.esm", "Child.esp" }));
            check("candidate/refuse: a master absent from the loaded set -> REFUSED, naming the dependent and the master",
                  missingMaster.Refused && missingMaster.RefusedReason!.Contains("'Child.esp'") && missingMaster.RefusedReason.Contains("'Parent.esp'")
                  && missingMaster.RefusedReason.Contains("refuses to start"),
                  missingMaster.RefusedReason?.Split('\n')[^1][..Math.Min(80, missingMaster.RefusedReason.Split('\n')[^1].Length)] ?? "");

            var espFirst = R(Candidate("espfirst", new[] { "Parent.esp", "Base.esm" }));
            check("candidate/refuse: a non-master plugin ahead of a master-flagged one -> REFUSED, naming both",
                  espFirst.Refused && espFirst.RefusedReason!.Contains("'Parent.esp'") && espFirst.RefusedReason.Contains("'Base.esm'")
                  && espFirst.RefusedReason.Contains("master-flagged"),
                  espFirst.RefusedReason?.Split('\n')[^1][..Math.Min(80, espFirst.RefusedReason.Split('\n')[^1].Length)] ?? "");

            var both = R(Candidate("both", new[] { "Base.esm", "Parent.esp", "Child.esp" }), enable: new[] { "Extra.esp" });
            check("candidate/refuse: --order together with --enable -> REFUSED (two descriptions of one order)",
                  both.Refused && both.RefusedReason!.Contains("two descriptions of ONE order"), both.RefusedReason?[..60] ?? "");

            var dupe = R(Candidate("dupe", new[] { "Base.esm", "Parent.esp", "Parent.esp" }));
            check("candidate/refuse: the same plugin listed twice -> REFUSED, naming it",
                  dupe.Refused && dupe.RefusedReason!.Contains("'Parent.esp'") && dupe.RefusedReason.Contains("more than once"),
                  dupe.RefusedReason?.Split('\n')[^1][..Math.Min(70, dupe.RefusedReason.Split('\n')[^1].Length)] ?? "");

            check("candidate/refuse: a refused configuration has NO identity — asking for one throws rather than answering",
                  Throws(() => unknown.Identity("load-time", "baseline")), "InvalidOperationException");

            // --- THE SEAM: the asset view follows the candidate's mod priority, the plugins byte-equal ---
            var aFirst = R(Candidate("afirst", new[] { "Base.esm", "Parent.esp", "Child.esp" }, new[] { "ModA", "ModB", "ModC" }));
            var bFirst = R(Candidate("bfirst", new[] { "Base.esm", "Parent.esp", "Child.esp" }, new[] { "ModB", "ModA", "ModC" }));
            bool bytesEqual = aFirst.Plugins.Select(p => p.Sha256).SequenceEqual(bFirst.Plugins.Select(p => p.Sha256))
                           && aFirst.Plugins.Select(p => p.Name).SequenceEqual(bFirst.Plugins.Select(p => p.Name));
            var fpA = aFirst.Identity("load-time", "baseline");
            var fpB = bFirst.Identity("load-time", "baseline");
            check("seam/identity: two candidates with byte-equal plugins and reversed mod priority -> different fingerprints",
                  bytesEqual && fpA.Fingerprint != fpB.Fingerprint, $"plugins equal={bytesEqual}; {fpA.Fingerprint[..12]} vs {fpB.Fingerprint[..12]}");
            check("seam/identity: and the delta between them is the provider change, nothing about plugins",
                  fpA.DeltaTo(fpB).Count == 1 && fpA.DeltaTo(fpB)[0].Contains("asset providers changed"),
                  string.Join(" | ", fpA.DeltaTo(fpB)));

            using (var assetsA = AssetResolver.Build(overwriteDir, modsDir, dataDir, aFirst.EnabledMods, aFirst.Archives.Archives))
            using (var assetsB = AssetResolver.Build(overwriteDir, modsDir, dataDir, bFirst.EnabledMods, bFirst.Archives.Archives))
            {
                var hitA = assetsA.Resolve(@"meshes\x.nif");
                var hitB = assetsB.Resolve(@"meshes\x.nif");
                check("seam/consumer: the ASSET RESOLVER built from each configuration serves a different copy of the same path",
                      hitA.Exists && hitB.Exists && hitA.Winner!.Source == "ModA" && hitB.Winner!.Source == "ModB",
                      $"ModA-first -> {hitA.Winner?.Source}; ModB-first -> {hitB.Winner?.Source}");
            }

            // --- a --enable arm gets an identity too, and it differs from the profile's ---
            check("candidate/synthetic: a --enable arm carries its own identity, and it is not the profile's",
                  !synArm.Refused && synArm.Source.StartsWith("synthetic:")
                  && synArm.Identity("load-time", "baseline").Fingerprint != prof.Identity("load-time", "baseline").Fingerprint,
                  $"{synArm.Source} {synArm.Identity("load-time", "baseline").Fingerprint[..12]} vs {prof.Identity("load-time", "baseline").Fingerprint[..12]}");

            // The check that stood here until 2026-10-24 asserted that a --enable of a plugin loadorder.txt does not list is
            // NOT refused and measures the profile without it. That is the silent different-order defect; case 1300 below
            // replaces it.

            // --- cases 350-355: an order whose plugin lacks a master is refused from every source (ruling 12) ---
            string Prof(string name, string[] modlist, string[] loadorder, string[] plugins)
            {
                var d = Path.Combine(instance, "profiles", name);
                Directory.CreateDirectory(d);
                const string head = "# This file was automatically generated by Mod Organizer.";
                File.WriteAllLines(Path.Combine(d, "modlist.txt"), modlist.Prepend(head));
                File.WriteAllLines(Path.Combine(d, "loadorder.txt"), loadorder.Prepend(head));
                File.WriteAllLines(Path.Combine(d, "plugins.txt"), plugins.Prepend(head));
                return name;
            }
            ResolvedConfiguration RP(string prof, string[]? enable = null, string[]? disable = null)
                => HarnessOrderCli.Resolve(prof, Path.Combine(instance, "profiles", prof), modsDir, dataDir, overwriteDir, game,
                                           null, enable?.ToList(), disable?.ToList());
            bool OneSentence(string? r, string start, string remedy)
                => r is not null && r.StartsWith(start) && r.Contains(remedy) && r.Contains("refuses to start on a missing master")
                   && r.EndsWith(", then run again.") && r.Count(c => c == ';') == 1 && !r.Contains('\n');
            string Show(ResolvedConfiguration c) => c.Refused ? c.RefusedReason! : $"not refused ({c.Plugins.Count} plugins)";

            var mmMods = new[] { "+ModB", "+ModA", "+ModC", "-ModOff" };
            var mmOrder = new[] { "Base.esm", "Parent.esp", "Child.esp" };

            var c350 = RP(Prof("MM-off", mmMods, mmOrder, new[] { "*Base.esm", "Parent.esp", "*Child.esp" }));
            check("350 profile: Child.esp ticked, its master Parent.esp unticked -> REFUSED in one sentence: switch Parent.esp on or Child.esp off",
                  c350.Refused && OneSentence(c350.RefusedReason, "MISSING MASTER - 'Child.esp' masters 'Parent.esp', which this order does not load",
                                              "switch 'Parent.esp' on in MO2, or switch 'Child.esp' off"), Show(c350));

            // Gone.esp masters Nowhere.esm, which no layer of the install holds.
            var nowhereDir = Path.Combine(root, "nowhere"); Directory.CreateDirectory(nowhereDir);
            Directory.CreateDirectory(Path.Combine(modsDir, "ModG"));
            var nowhereKey = new ModKey("Nowhere", ModType.Master);
            var nowhere = new SkyrimMod(nowhereKey, Release);
            nowhere.Statics.Add(new Static(new FormKey(nowhereKey, 0x800), Release) { EditorID = "NowhereStatic" });
            var nowherePath = Path.Combine(nowhereDir, nowhereKey.FileName.String);
            nowhere.BeginWrite.ToPath(nowherePath).WithNoLoadOrder().Write();
            using (var nowhereOverlay = SkyrimMod.CreateFromBinaryOverlay(nowherePath, Release))
            {
                var gone = new SkyrimMod(new ModKey("Gone", ModType.Plugin), Release);
                gone.Statics.Add(new Static(new FormKey(nowhereKey, 0x800), Release) { EditorID = "NowhereOverride" });
                gone.BeginWrite.ToPath(Path.Combine(modsDir, "ModG", "Gone.esp")).WithLoadOrder(nowhereOverlay).Write();
            }
            var c351 = RP(Prof("MM-gone", new[] { "+ModG" }, new[] { "Base.esm", "Gone.esp" }, new[] { "*Base.esm", "*Gone.esp" }));
            check("351 profile: Gone.esp masters Nowhere.esm, installed nowhere -> REFUSED: install Nowhere.esm or switch Gone.esp off",
                  c351.Refused && OneSentence(c351.RefusedReason, "MISSING MASTER - 'Gone.esp' masters 'Nowhere.esm'",
                                              "install 'Nowhere.esm', or switch 'Gone.esp' off"), Show(c351));

            var c352 = RP(profile, disable: new[] { "Parent.esp" });
            check("352 --disable Parent.esp orphans Child.esp -> REFUSED: also --disable Child.esp or keep Parent.esp",
                  c352.Refused && OneSentence(c352.RefusedReason, "MISSING MASTER - 'Child.esp' masters 'Parent.esp'",
                                              "also --disable 'Child.esp' or keep 'Parent.esp'"), Show(c352));

            var c353 = RP(Prof("MM-enable", mmMods, mmOrder, new[] { "*Base.esm", "Parent.esp", "Child.esp" }), enable: new[] { "Child.esp" });
            check("353 --enable Child.esp with Parent.esp off -> REFUSED (not measured without Child): also --enable Parent.esp",
                  c353.Refused && OneSentence(c353.RefusedReason, "MISSING MASTER - 'Child.esp' masters 'Parent.esp'",
                                              "also --enable 'Parent.esp'"), Show(c353));

            var c354 = R(Candidate("mm354", new[] { "Base.esm", "Child.esp" }));
            check("354 --order candidate [Base.esm, Child.esp] -> REFUSED with the same sentence: add Parent.esp ahead of Child.esp",
                  c354.Refused && c354.RefusedReason!.Split('\n').Count(l => l.Contains("MISSING MASTER")) == 1
                  && OneSentence(c354.RefusedReason.Split('\n').Single(l => l.Contains("MISSING MASTER")).TrimStart(' ', '-'),
                                 "MISSING MASTER - 'Child.esp' masters 'Parent.esp'", "add 'Parent.esp' to the candidate ahead of 'Child.esp'"),
                  Show(c354));

            var c355a = RP(Prof("MM-good", mmMods, mmOrder, new[] { "*Base.esm", "*Parent.esp", "*Child.esp" }));
            var c355b = RP("MM-good", disable: new[] { "Child.esp" });
            var c355c = R(Candidate("mm355", mmOrder));
            check("355 control: every master present (profile, a --disable that orphans nothing, a candidate) -> none refused",
                  !c355a.Refused && !c355b.Refused && !c355c.Refused && c355a.Plugins.Count == 3 && c355b.Plugins.Count == 2,
                  $"{Show(c355a)} | {Show(c355b)} | {Show(c355c)}");

            // --- cases 1300-1313: a requested order the reader cannot build as asked is refused, not answered with another ---
            bool OneLine(string? r, string start, string remedy)
                => r is not null && r.StartsWith(start) && r.Contains(remedy) && r.EndsWith(", then run again.")
                   && r.Count(c => c == ';') == 1 && !r.Contains('\n');

            // 1300 replaces the check above that asserted the defect: the arm is refused, never measured without Extra.esp.
            var c1300 = R(null, enable: new[] { "Extra.esp" });
            check("1300 --enable Extra.esp, installed in a switched-off folder but not in loadorder.txt -> REFUSED in one sentence: switch it on once in MO2 or give it a position with --order",
                  c1300.Refused && OneLine(c1300.RefusedReason, "CANNOT ENABLE - 'Extra.esp' has no load-order position: loadorder.txt does not list it",
                                           "switch 'Extra.esp' on once in MO2 so loadorder.txt lists it, or give it a position in an --order candidate"),
                  Show(c1300));

            var c1301 = R(null, enable: new[] { "Absent.esp" });
            check("1301 --enable Absent.esp, which no layer of the install holds and loadorder.txt does not list -> REFUSED: install it",
                  c1301.Refused && OneLine(c1301.RefusedReason, "CANNOT ENABLE - 'Absent.esp' has no load-order position: loadorder.txt does not list it",
                                           "install the mod that provides 'Absent.esp'"),
                  Show(c1301));

            var c1302 = RP(Prof("EN-lost", mmMods, new[] { "Base.esm", "Parent.esp", "Child.esp", "Lost.esp" },
                                new[] { "*Base.esm", "*Parent.esp", "*Child.esp", "Lost.esp" }), enable: new[] { "Lost.esp" });
            check("1302 --enable Lost.esp, listed in loadorder.txt but provided by no layer -> REFUSED (not measured without it): install it",
                  c1302.Refused && OneLine(c1302.RefusedReason, "CANNOT ENABLE - 'Lost.esp' is listed in loadorder.txt, but no mod folder, the overwrite layer or the game Data folder provides it",
                                           "install the mod that provides 'Lost.esp'"),
                  Show(c1302));

            var c1303 = R(null, enable: new[] { "Off.esp", "Extra.esp", "Absent.esp" });
            check("1303 --enable of one listed and two unlisted plugins -> REFUSED in ONE sentence naming the first and counting the other",
                  c1303.Refused && OneLine(c1303.RefusedReason, "CANNOT ENABLE - 'Extra.esp' has no load-order position",
                                           "(and 1 more --enable request(s) cannot be built, first 'Absent.esp')"),
                  Show(c1303));

            var c1304 = R(null, enable: new[] { "Off.esp" });
            check("1304 control: --enable Off.esp, listed and installed -> not refused, and Off.esp is loaded",
                  !c1304.Refused && c1304.Plugins.Any(p => p.Name == "Off.esp"), Show(c1304));

            // F2: two profiles that differ in one mod's priority. P ranks ModB above ModA; Q ranks ModA above ModB, so
            // meshes\x.nif is ModA's under Q and ModB's under P. A candidate built on Q with no Mods list takes its asset
            // priority from its base profile, so resolving it under --profile P would serve the other copy.
            Prof("Q", new[] { "+ModA", "+ModB", "+ModC", "-ModOff" },
                 new[] { "Base.esm", "Shared.esp", "Both.esp", "Off.esp", "Parent.esp", "Child.esp" },
                 new[] { "*Base.esm", "*Shared.esp", "*Both.esp", "Off.esp", "*Parent.esp", "*Child.esp" });
            string CandidateOn(string name, string? baseProfile, IReadOnlyList<string> plugins)
            {
                var path = Path.Combine(root, name + ".json");
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["SchemaVersion"] = ConfigurationIdentity.CurrentSchema, ["Name"] = name,
                    ["BaseProfile"] = baseProfile, ["Plugins"] = plugins, ["Changes"] = Array.Empty<string>(),
                }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value)));
                return path;
            }
            var qOrder = new[] { "Base.esm", "Shared.esp", "Both.esp", "Parent.esp", "Child.esp" };
            var c1310 = R(CandidateOn("onq", "Q", qOrder));
            check("1310 --order candidate built on profile Q, run under --profile P (the two rank ModA and ModB the other way) -> REFUSED in one sentence, never resolved through P",
                  c1310.Refused && OneLine(c1310.RefusedReason, "PROFILE MISMATCH - candidate BaseProfile 'Q' does not match --profile 'P' ('onq.json')",
                                           "run with --profile 'Q', or rebuild the candidate on profile 'P'"),
                  c1310.Refused ? c1310.RefusedReason! : $"not refused: asset view {string.Join(" > ", c1310.EnabledMods)} (profile P's; Q says ModA > ModB > ModC)");

            var c1311 = R(CandidateOn("nobase", null, qOrder));
            check("1311 --order candidate that names no BaseProfile -> REFUSED: nothing says which profile its names belong to",
                  c1311.Refused && OneLine(c1311.RefusedReason, "PROFILE MISMATCH - candidate 'nobase.json' names no BaseProfile",
                                           "add \"BaseProfile\": \"P\" if it was built on profile 'P', or rebuild it there"),
                  Show(c1311));

            var c1312 = R(CandidateOn("onp-case", "p", qOrder));
            check("1312 control: a candidate whose BaseProfile differs from --profile only in letter case -> not refused (MO2 profile names are case-insensitive on Windows)",
                  !c1312.Refused, Show(c1312));

            var c1313 = R(CandidateOn("onq-nope", "Q", new[] { "Base.esm", "Parent.esp", "Nope.esp" }));
            check("1313 a mismatched candidate that also names a plugin nothing provides -> the one PROFILE MISMATCH sentence, no second refusal",
                  c1313.Refused && OneLine(c1313.RefusedReason, "PROFILE MISMATCH - candidate BaseProfile 'Q' does not match --profile 'P'",
                                           "run with --profile 'Q'") && !c1313.RefusedReason!.Contains("Nope.esp"),
                  Show(c1313));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    static bool Throws(Action a) { try { a(); return false; } catch (InvalidOperationException) { return true; } catch { return false; } }

    /// <summary>A minimal MO2 layout: a master in the game's Data folder, a Parent/Child pair across two enabled mod
    /// folders, one plugin in a DISABLED folder, and the same loose mesh path in two enabled folders with different
    /// bytes — the one thing that makes the asset half of a configuration observable.</summary>
    static (string Instance, string Game, string Profile) BuildLayout(string root)
    {
        string instance = Path.Combine(root, "instance"), game = Path.Combine(root, "game");
        string modsDir = Path.Combine(instance, "mods"), profile = "P";
        string profileDir = Path.Combine(instance, "profiles", profile);
        string dataDir = Path.Combine(game, "Data");
        foreach (var d in new[] { profileDir, dataDir, Path.Combine(instance, "overwrite"),
                                  Path.Combine(modsDir, "ModA"), Path.Combine(modsDir, "ModB"),
                                  Path.Combine(modsDir, "ModC"), Path.Combine(modsDir, "ModOff") })
            Directory.CreateDirectory(d);

        // Base.esm — master-flagged by extension, no masters of its own.
        var baseKey = new ModKey("Base", ModType.Master);
        var baseMod = new SkyrimMod(baseKey, Release);
        baseMod.Statics.Add(new Static(new FormKey(baseKey, 0x800), Release) { EditorID = "BaseStatic" });
        baseMod.BeginWrite.ToPath(Path.Combine(dataDir, baseKey.FileName.String)).WithNoLoadOrder().Write();

        // Parent.esp — a plain plugin with a record of its own.
        var parentKey = new ModKey("Parent", ModType.Plugin);
        var parent = new SkyrimMod(parentKey, Release);
        parent.Statics.Add(new Static(new FormKey(parentKey, 0x800), Release) { EditorID = "ParentStatic" });
        var parentPath = Path.Combine(modsDir, "ModA", parentKey.FileName.String);
        parent.BeginWrite.ToPath(parentPath).WithNoLoadOrder().Write();

        // Child.esp — overrides Parent's record, so its TES4 header masters Parent.esp and nothing else.
        using (var parentOverlay = SkyrimMod.CreateFromBinaryOverlay(parentPath, Release))
        {
            var child = new SkyrimMod(new ModKey("Child", ModType.Plugin), Release);
            child.Statics.Add(new Static(new FormKey(parentKey, 0x800), Release) { EditorID = "ParentStaticOverride" });
            child.BeginWrite.ToPath(Path.Combine(modsDir, "ModC", "Child.esp")).WithLoadOrder(parentOverlay).Write();
        }

        // Extra.esp — in a switched-off folder, and absent from loadorder.txt.
        var extraKey = new ModKey("Extra", ModType.Plugin);
        var extra = new SkyrimMod(extraKey, Release);
        extra.Statics.Add(new Static(new FormKey(extraKey, 0x800), Release) { EditorID = "ExtraStatic" });
        extra.BeginWrite.ToPath(Path.Combine(modsDir, "ModOff", extraKey.FileName.String)).WithNoLoadOrder().Write();

        // Two plugins that exist TWICE under different names of the same file, with different bytes — the precedence
        // the 2026-09-11 lab finding was about. Shared.esp is in the game's Data folder AND in the switched-off
        // ModOff: Data must win, on every arm, because MO2 puts nothing from a switched-off folder into the game's
        // view. Both.esp is in the ENABLED ModA and in ModOff: the enabled mod must win, as it always did.
        // Off.esp — in an ENABLED folder and listed in loadorder.txt, but unchecked in plugins.txt: the plugin a
        // --enable arm actually switches on. (A plugin loadorder.txt never lists has no position, and SyntheticOrder
        // refuses to invent one — so it is no use as the subject of a what-if.)
        Written("Off", Path.Combine(modsDir, "ModA", "Off.esp"), "installed but unchecked");

        Written("Shared", Path.Combine(dataDir, "Shared.esp"), "the copy the game loads");
        Written("Shared", Path.Combine(modsDir, "ModOff", "Shared.esp"), "a switched-off folder's copy, longer on purpose so the bytes differ");
        Written("Both", Path.Combine(modsDir, "ModA", "Both.esp"), "an enabled mod's copy");
        Written("Both", Path.Combine(modsDir, "ModOff", "Both.esp"), "a switched-off folder's copy, longer on purpose so the bytes differ");

        // The same loose path from two enabled folders, different bytes: whichever mod outranks the other wins it.
        Directory.CreateDirectory(Path.Combine(modsDir, "ModA", "meshes"));
        Directory.CreateDirectory(Path.Combine(modsDir, "ModB", "meshes"));
        File.WriteAllText(Path.Combine(modsDir, "ModA", "meshes", "x.nif"), "this copy is ModA's");
        File.WriteAllText(Path.Combine(modsDir, "ModB", "meshes", "x.nif"), "and this one is ModB's, a different length");

        // modlist.txt: TOP IS HIGHEST priority, '+' enabled, '-' installed but off.
        File.WriteAllLines(Path.Combine(profileDir, "modlist.txt"),
            new[] { "# This file was automatically generated by Mod Organizer.", "+ModB", "+ModA", "+ModC", "-ModOff" });
        File.WriteAllLines(Path.Combine(profileDir, "loadorder.txt"),
            new[] { "# This file was automatically generated by Mod Organizer.", "Base.esm", "Shared.esp", "Both.esp", "Off.esp", "Parent.esp", "Child.esp" });
        File.WriteAllLines(Path.Combine(profileDir, "plugins.txt"),
            new[] { "# This file was automatically generated by Mod Organizer.", "*Base.esm", "*Shared.esp", "*Both.esp", "Off.esp", "*Parent.esp", "*Child.esp" });
        return (instance, game, profile);
    }

    /// <summary>One standalone plugin written to an exact path, its EditorID carrying <paramref name="note"/> so two
    /// copies of the same filename differ in content as well as in length.</summary>
    static void Written(string name, string path, string note)
    {
        var key = new ModKey(name, ModType.Plugin);
        var mod = new SkyrimMod(key, Release);
        mod.Statics.Add(new Static(new FormKey(key, 0x800), Release) { EditorID = $"{name}Static: {note}" });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        mod.BeginWrite.ToPath(path).WithNoLoadOrder().Write();
    }
}
