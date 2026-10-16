using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using F = HarnessShared.PopulationFixture;

// overlapprobe --self-test, P25 part: _shared/PopulationFixture.cs written, the scope (_shared/PopulationScope.cs) derived on
// the arm WITHOUT the delta (PpBase, PpA, PpB, PpMover) from a population naming PpDelta.esp and the PpMeshMod folder, and
// every seed rule checked against the fixture's table. The rows themselves come from real probe runs on the written order
// (tests/test_population_scope.py).
static class PopulationSelfTest
{
    public static void Run(ProbeSelfTest t)
    {
        var w = F.Write(t.Root);
        var pop = F.WritePopulation(w, Path.Combine(t.Root, "pp", "population.json"));
        var idx = new OverlapIndex();
        int p = 0;
        foreach (var path in w.Order.Where(x => Path.GetFileName(x) != "PpDelta.esp"))
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            idx.Ingest(p++, mod);
        }
        var scope = PopulationScope.Load(pop);
        scope.Bind(idx);
        string Has(params FormKey[] keys) => string.Join(", ", keys.Where(k => !scope.Seeds.Contains(k)));
        var fileRefs = new[] { F.D0, F.D3, F.B4, F.Dc1, F.PA, F.Mk, F.DE, F.Ds2 };

        t.Check("C400 S1 comes from the delta FILE: in an arm without PpDelta.esp its 8 references (new, overrides of PpBase refs) are seeds",
                scope.FileRefs.SetEquals(fileRefs) && fileRefs.All(scope.Seeds.Contains), $"file refs {scope.FileRefs.Count}; missing seeds: {Has(fileRefs)}");
        t.Check("C401 a delta cell is a population cell (IPop, IOther, E00, E50) and IOut is not; a1 in IPop is in scope, a2 in IOut is not",
                scope.FileCells.SetEquals(new[] { F.IPop, F.IOther, F.E00, F.E50 }) && scope.KeyInScope(F.A1, F.IPop) && !scope.KeyInScope(F.A2, F.IOut),
                string.Join(", ", scope.FileCells));
        t.Check("C402 the huge partner hA (filed in E10) is not itself in scope; its delta partner dE (E00) is",
                !scope.KeyInScope(F.HA, F.E10) && scope.KeyInScope(F.DE, F.E00), "");
        t.Check("C403 the persistent pA the delta moves is a seed by key; pA2 is not; the worldspace's persistent cell is never a population cell",
                scope.Seeds.Contains(F.PA) && !scope.Seeds.Contains(F.PA2) && !scope.FileCells.Contains(F.T) && !scope.KeyInScope(F.PA2, F.T), "");
        t.Check("C404 S3: Kc, whose enable parent mk the delta overrides, is a seed in the arm without the delta",
                scope.Seeds.Contains(F.Kc), string.Join(", ", scope.SeedCounts.Select(k => $"{k.Key} {k.Value}")));
        t.Check("C405 S4: x7, whose base PpStatX the delta overrides, is a seed", scope.Seeds.Contains(F.X7), "");
        t.Check("C406 S2: m8, whose model PpMeshMod ships, is a seed in an arm whose view lacks PpMeshMod",
                scope.Seeds.Contains(F.M8) && scope.ModFiles.Contains("meshes\\pp\\mshb.nif"), string.Join(", ", scope.ModFiles));
        var expected = fileRefs.Concat(new[] { F.Kc, F.X7, F.M8 }).ToHashSet();
        t.Check("C406 nothing else is a seed: exactly the 8 file references, Kc, x7 and m8",
                scope.Seeds.SetEquals(expected), string.Join(", ", scope.Seeds.Except(expected)));
        Snap S(FormKey k) => idx.History.Winning(k)!.Value;
        t.Check("C407 the contact region holds c2 (E50, the ring of the delta's ds2) in an arm where ds2 does not exist; not c3 (E600)",
                scope.PlaceInScope(S(F.C2)) && !scope.PlaceInScope(S(F.C3)) && !scope.PlaceInScope(S(F.Hs3)), "");
        t.Check("C407 the contact region holds every placement in a seed's interior cell (hs1 in IOther), not one in IOut",
                scope.PlaceInScope(S(F.Hs1)) && !scope.PlaceInScope(S(F.A2)), "");
        t.Check("C400 the identity is the enable population plus the first 12 of the file's sha256",
                scope.Identity("load-time") == "load-time+population:" + scope.Sha256[..12] && scope.Sha256.Length == 64, scope.Identity("load-time"));

        // Refusals: one plain sentence each, never a silently narrower or wider run.
        string? Refused(Action a) { try { a(); return null; } catch (PopulationRefused ex) { return ex.Message; } }
        var bad = Path.Combine(t.Root, "pp", "population-badsha.json");
        File.WriteAllText(bad, File.ReadAllText(pop).Replace(FileSha(w.Order.Last()), new string('0', 64)));
        var r1 = Refused(() => PopulationScope.Load(bad));
        t.Check("C400 refused: a delta plugin whose bytes are not the ones the population names", r1?.Contains("hashes to") == true, r1 ?? "(not refused)");
        var swapDir = Path.Combine(t.Root, "pp", "swapmod"); Directory.CreateDirectory(swapDir);
        File.WriteAllText(Path.Combine(swapDir, "PpThing_SWAP.ini"), "[Forms]\n");
        var swapPop = Path.Combine(t.Root, "pp", "population-swap.json");
        File.WriteAllText(swapPop, "{\"schema\": 1, \"plugins\": [], \"mods\": [{\"name\": \"swapmod\", \"path\": \"" + swapDir.Replace("\\", "\\\\") + "\"}]}");
        var r2 = Refused(() => PopulationScope.Load(swapPop));
        t.Check("C400 refused: a delta mod folder shipping a Base Object Swapper ini", r2?.Contains("Base Object Swapper") == true, r2 ?? "(not refused)");
        var collPath = Path.Combine(t.Root, "pp", "PpColl.esp");
        var cm = new SkyrimMod(new ModKey("PpColl", ModType.Plugin), SkyrimRelease.SkyrimSE);
        cm.CollisionLayers.Add(new CollisionLayer(new FormKey(cm.ModKey, 0x801), SkyrimRelease.SkyrimSE) { EditorID = "PpLayer", Index = 60 });
        cm.BeginWrite.ToPath(collPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var collPop = Path.Combine(t.Root, "pp", "population-coll.json");
        File.WriteAllText(collPop, "{\"schema\": 1, \"plugins\": [{\"name\": \"PpColl.esp\", \"path\": \"" + collPath.Replace("\\", "\\\\") + "\", \"sha256\": \"" + FileSha(collPath) + "\"}], \"mods\": []}");
        var r3 = Refused(() => PopulationScope.Load(collPop));
        t.Check("C400 refused: a delta plugin carrying a collision-layer record", r3?.Contains("collision-layer") == true, r3 ?? "(not refused)");
        var emptyPop = Path.Combine(t.Root, "pp", "population-empty.json");
        File.WriteAllText(emptyPop, "{\"schema\": 1, \"plugins\": [], \"mods\": []}");
        var r4 = Refused(() => PopulationScope.Load(emptyPop));
        t.Check("C400 refused: a population naming nothing", r4?.Contains("names no plugin") == true, r4 ?? "(not refused)");
    }

    static string FileSha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s)).ToLowerInvariant();
    }
}
