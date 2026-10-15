using HarnessShared;
using F = HarnessShared.OverlapClassFixture;

// overlapprobe --self-test, slice 4: _shared/OverlapClassFixture.cs written beside the base-game fixture, and the
// decisions the probe's top level hands to its classes checked on it. Expectations are worked out by hand from the
// fixture's table.
static class OverlapClassSelfTest
{
    public static void Run(ProbeSelfTest t)
    {
        var w = F.Write(t.Root);
        t.Check("PD4-O-F0 the fixture writes the eight plugins, the box, the _SWAP.ini and the base emit",
                w.Order.Select(Path.GetFileName).SequenceEqual(F.Order) && w.Order.All(File.Exists)
                && File.Exists(Path.Combine(w.ModsDir, F.Skyrim.Name, "meshes", F.MeshRel)) && File.Exists(Path.Combine(w.ModsDir, F.OcA.Name, F.SwapIni))
                && ConflictIo.Read(w.BaseEmit).Conflicts.Count == 10,
                string.Join(", ", w.Order.Select(Path.GetFileName)));
    }
}
