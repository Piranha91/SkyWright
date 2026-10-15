using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using F = HarnessShared.BlockFixture;

// blockprobe --self-test: _shared/BlockFixture.cs written (PD_FIXTURE_KEEP keeps it for a probe run), then the probe's own
// per-reference decisions over it, each against an answer worked out by hand from the fixture's table. Exit 0 when every
// case passes. (--moved-ref-selftest is the winning-state walk's, BlockSelfTest.cs.)
static class BlockRouteSelfTest
{
    public static int Run() => ProbeSelfTest.Run("blockprobe", "BlockFixture through the per-reference decisions", t =>
    {
        var w = F.Write(t.Root);
        t.Check("PD3-K-F0 the fixture writes BkMaster.esm, BkMover.esp and meshes\\pd3\\box.nif",
                w.Order.Count == 2 && w.Order.All(File.Exists) && File.Exists(Path.Combine(w.ModsDir, F.MasterKey.Name, "meshes", F.MeshRel)),
                string.Join(", ", w.Order.Select(Path.GetFileName)));

        // Smoke (Gate A risk 3): the box mesh decodes as one complete solid body on a layer an actor collides with.
        var o = NifGeometry.Measure(File.ReadAllBytes(Path.Combine(w.ModsDir, F.MasterKey.Name, "meshes", F.MeshRel)), includeTriangles: true);
        var bodies = o.Model?.CollisionBodies ?? new List<CollisionBodyInfo>();
        string Bodies() => string.Join("; ", bodies.Select(b => $"{b.BodyType} {b.Layer} solid={b.Solid} unread={b.Unread.Count}"));
        t.Check("PD3-K-F1 the box mesh decodes complete, with a solid body on an actor-solid layer",
                o.Model is { HasCollision: true, CollisionComplete: true } && bodies.Any(b => b.Solid && CollisionLayers.IsActorSolid(b.Layer)), Bodies());

        // The winning state of the target's cells, walked as step 2 of a real run walks it.
        var targets = new Dictionary<FormKey, CellPlacements>();
        using (var m0 = SkyrimMod.CreateFromBinaryOverlay(w.Order[0], F.Rel))
            foreach (var cp in PlacementExtract.FromMod(m0)) targets[cp.Cell] = cp;
        var walk = new BlockWalk(targets);
        foreach (var path in w.Order)
            if (BlockWalk.Open(path) is { } m) { using (m as IDisposable) walk.Plugin(m, Path.GetFileName(path), _ => { }); }
        t.Check("PD3-K-F2 the target's cells are I1 and I2, and Rm's winning cell is I2",
                targets.Count == 2 && walk.WinRefCell.TryGetValue(F.Rm, out var rc) && rc == F.I2,
                walk.WinRefCell.TryGetValue(F.Rm, out var rc2) ? rc2.ToString() : "absent");
    });
}
