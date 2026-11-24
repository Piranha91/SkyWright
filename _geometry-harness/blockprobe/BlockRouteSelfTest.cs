using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
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

        // Which of the target's references are judged in which cell (RefAdmission.Winning).
        string Judged(FormKey k, FormKey cell) => RefAdmission.Winning(k, cell, walk.WinRef, walk.WinRefCell) is { } r ? $"at {r.Position.X:0},{r.Position.Y:0}" : "not judged";
        t.Check("PD3-K-1 Rm, moved to I2 by BkMover, is not judged in I1, the cell it left",
                Judged(F.Rm, F.I1) == "not judged", Judged(F.Rm, F.I1));
        t.Check("PD3-K-1b Rm is judged in I2 at its winning place; Dr, Ms and Sx in I1 at theirs",
                Judged(F.Rm, F.I2) == "at 0,400" && Judged(F.Dr, F.I1) == "at -400,0" && Judged(F.Ms, F.I1) == "at 0,0" && Judged(F.Sx, F.I1) == "at 400,0",
                $"{Judged(F.Rm, F.I2)} / {Judged(F.Dr, F.I1)} / {Judged(F.Ms, F.I1)} / {Judged(F.Sx, F.I1)}");

        // The route rule's set-aside choice (RefAdmission.RouteSetAside), on the base types read as step 2 reads them.
        var baseType = new Dictionary<FormKey, string>();
        using (var m0 = SkyrimMod.CreateFromBinaryOverlay(w.Order[0], F.Rel))
            foreach (var rec in m0.EnumerateMajorRecords())
                if (rec is IModeledGetter mo && mo.Model?.File is { } f && !string.IsNullOrWhiteSpace(f.GivenPath))
                    baseType[rec.FormKey] = rec.GetType().Name.Replace("BinaryOverlay", "");
        string Aside(FormKey b) => baseType.TryGetValue(b, out var ty) ? $"{ty} {RefAdmission.RouteSetAside(ty)}" : "(no modeled base)";
        t.Check("PD3-K-2 a Door is set aside as passable by design", Aside(F.DoorBase) == "Door Door", Aside(F.DoorBase));
        t.Check("PD3-K-3 a MoveableStatic is set aside as pushed aside", Aside(F.PushBase) == "MoveableStatic Movable", Aside(F.PushBase));
        t.Check("PD3-K-4 a Static is measured (control)", Aside(F.WallBase) == "Static Measure", Aside(F.WallBase));
    });
}
