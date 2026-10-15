using HarnessShared;
using F = HarnessShared.BaseGameFixture;

// overlapprobe --self-test: _shared/BaseGameFixture.cs written (PD_FIXTURE_KEEP keeps it for a probe run). The rows the
// by-name population split decides come from probe runs on the written order; this checks the fixture is what its table says.
static class OverlapSelfTest
{
    public static int Run() => ProbeSelfTest.Run("overlapprobe", "BaseGameFixture (base-game plugin names)", t =>
    {
        var w = F.Write(t.Root);
        t.Check("CR-F0 the fixture writes Skyrim.esm, Dawnguard.esm, HearthFires.esm, CrMod.esp, CrA.esp, CrB.esp and the render box",
                w.Overlap.Count == 6 && w.Overlap.All(File.Exists) && File.Exists(Path.Combine(w.OverlapMods, F.SkyrimKey.Name, "meshes", F.MeshRel)),
                string.Join(", ", w.Overlap.Select(Path.GetFileName)));

        // The names the population split and the candidate filter read (_shared/BaseGamePlugins.cs).
        var seven = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm", "SkyrimVR.esm", "_ResourcePack.esl" };
        var spelled = seven.SelectMany(n => new[] { n, n.ToLowerInvariant(), n.ToUpperInvariant() }).ToList();
        t.Check("CR-N1 the seven base-game names are base game in any case; a Creation Club plugin and USSEP are not",
                spelled.All(BaseGamePlugins.IsBaseGame) && BaseGamePlugins.Names.Count == 7
                && !BaseGamePlugins.IsBaseGame("ccbgssse001-fish.esm") && !BaseGamePlugins.IsBaseGame(BaseGamePlugins.Ussep)
                && BaseGamePlugins.IsUssep("unofficial skyrim special edition patch.esp"),
                string.Join(", ", spelled.Where(n => !BaseGamePlugins.IsBaseGame(n))));
        t.Check("CR-N2 the core masters are Skyrim.esm and Update.esm in any case, and nothing else",
                BaseGamePlugins.IsCoreMaster("skyrim.esm") && BaseGamePlugins.IsCoreMaster("UPDATE.ESM") && BaseGamePlugins.IsCoreMaster("Skyrim.esm")
                && !BaseGamePlugins.IsCoreMaster("Dawnguard.esm") && !BaseGamePlugins.IsCoreMaster("Skyrim.esp"), "");

        // F5: the patch the console's "[patched, residual]" mark needs; the marks themselves come from probe runs.
        var patch = F.WritePatch(t.Root);
        using var po = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(patch, F.Rel);
        var masters = po.ModHeader.MasterReferences.Select(r => r.Master.FileName.String).ToList();
        t.Check("FF-F0 the fixture writes CrPatch.esp mastering Skyrim.esm, CrA.esp and CrB.esp, and nothing else",
                masters.SequenceEqual(new[] { "Skyrim.esm", "CrA.esp", "CrB.esp" }), string.Join(", ", masters));

        // Slice 4: the class split, the provenance-unknown count and the physical predicate (_shared/OverlapClassFixture.cs).
        OverlapClassSelfTest.Run(t);
    });
}
