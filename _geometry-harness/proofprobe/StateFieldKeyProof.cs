using System.Numerics;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

// PROOF BY CONSTRUCTION — a state's route field belongs to the state (Q27), including a floor piece that a switch brings
// in from a NEIGHBOURING cell (StateFieldKey, used by stateprobe's FieldFor). Two exterior cells, A at (0,0) and B at
// (1,0): B holds a slab P anchored past A's edge whose box reaches 296 u back into A, enable-parented to switch S, which
// is off at load. Flipping S changes nothing among A's own references, only A's floor. All in memory.
static class StateFieldKeyProof
{
    static readonly ModKey Mod = ModKey.FromNameAndExtension("StateFieldKeyProof.esp");
    static FormKey F(uint id) => new(Mod, id);

    static readonly FormKey S = F(0x801), S2 = F(0x802), S3 = F(0x803);       // switches, all off at load
    static readonly FormKey K = F(0x811), R = F(0x812);                       // A's own: a control, and S3's child
    static readonly FormKey P = F(0x821);                                     // B's slab, S's child
    static readonly FormKey Q = F(0x841);                                     // at (4,0), outside A's 3x3: S2's child
    static readonly FormKey SlabBase = F(0x901);

    static PlacedRef Ref(FormKey k, float x, float y, FormKey? parent) =>
        new(k, "REFR", null, new Vec3(x, y, 0), new Vec3(0, 0, 0), 1f, SlabBase, false, false, false, parent);

    static TriangleSoup Box(float x0, float y0, float z0, float x1, float y1, float z1)
    {
        var v = new[] { new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x0, y1, z0),
                        new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1) };
        var i = new[] { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 1,2,6, 1,6,5, 2,3,7, 2,7,6, 3,0,4, 3,4,7 };
        return new TriangleSoup(v, i);
    }

    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nPROOF — a state's floor includes the neighbour's floor piece its flip brings in (_shared/StateFieldKey.cs)");

        var facts = new Dictionary<FormKey, EnableFacts>
        {
            [S] = new(true, null, false), [S2] = new(true, null, false), [S3] = new(true, null, false),
            [K] = new(false, null, false), [R] = new(false, S3, false), [P] = new(false, S, false), [Q] = new(false, S2, false),
        };
        Func<FormKey, EnableFacts?> raw = k => facts.TryGetValue(k, out var f) ? f : null;
        var baseline = EnablePopulation.Of(raw, EnablePopulation.BaselineStateName);
        EnablePopulation PopOf(StateArm arm) => EnablePopulation.Of(arm.Wrap(raw, k => baseline.Resolution(k)), arm.Name);
        var flipS = new StateArm("flipS", new[] { S });
        var flipS2 = new StateArm("flipS2", new[] { S2 });
        var flipS3 = new StateArm("flipS3", new[] { S3 });

        var refK = Ref(K, 2000, 2000, null);
        var refR = Ref(R, 1000, 1000, S3);
        var refP = Ref(P, 4200, 2000, S);           // anchored in B; its box spans x 3800..4600
        var refQ = Ref(Q, 4 * 4096 + 2000, 2000, S2);
        var ownA = new List<PlacedRef> { refK, refR };
        var byGrid = new Dictionary<(int, int), Dictionary<FormKey, PlacedRef>>
        {
            [(0, 0)] = new() { [K] = refK, [R] = refR },
            [(1, 0)] = new() { [P] = refP },
            [(4, 0)] = new() { [Q] = refQ },
        };
        var (nbA, landA) = FloorFeed.Neighbourhood((0, 0), byGrid, new Dictionary<(int, int), float[,]>());
        // Only P has a slab worth standing on in the read below; K and R are far from it.
        var slab = Box(-400, -400, 0, 400, 400, 100);
        var slabBox = new Aabb(new Vec3(-400, -400, 0), new Vec3(400, 400, 100), false);
        FloorFeed.GeometryOf geometry = r => (slabBox, slab);

        Check("state-field/setup: P is off at load and on under flipS; A's own references are unchanged under flipS",
              !baseline.Enabled(P) && PopOf(flipS).Enabled(P) && ownA.All(r => PopOf(flipS).Enabled(r.FormKey) == baseline.Enabled(r.FormKey))
              && nbA.Any(r => r.FormKey == P) && nbA.All(r => r.FormKey != Q),
              $"P {baseline.Enabled(P)} -> {PopOf(flipS).Enabled(P)}; A's 3x3 holds {string.Join(", ", nbA.Select(r => r.FormKey.ID.ToString("X")))}");

        var popS = PopOf(flipS);
        var keyS = StateFieldKey.For(flipS, ownA, nbA, popS, baseline);
        Check("state-field/neighbour: flipping S keys A's field on the state, because A's FLOOR changes though none of its own references does",
              keyS == flipS.Name, $"key '{keyS}'");

        var usePop = keyS == EnablePopulation.BaselineStateName ? baseline : popS;
        var stateFloor = FloorFeed.Build(nbA, geometry, landA, enabled: usePop.Predicate);
        var baseFloor = FloorFeed.Build(nbA, geometry, landA, enabled: baseline.Predicate);
        var onP = stateFloor.Read(3900, 2000, 100);
        var offP = baseFloor.Read(3900, 2000, 100);
        Check("state-field/neighbour: the state's floor has the slab as one more candidate, and reads its top (100) inside A where the baseline's finds nothing",
              stateFloor.Candidates == baseFloor.Candidates + 1 && onP.Found && Math.Abs(onP.FloorZ - 100) < 0.01f && !offP.Found,
              $"candidates {stateFloor.Candidates} vs {baseFloor.Candidates}; at (3900,2000) state {(onP.Found ? onP.FloorZ.ToString("0.##") : "none")}, baseline {(offP.Found ? offP.FloorZ.ToString("0.##") : "none")}");

        var keyS2 = StateFieldKey.For(flipS2, ownA, nbA, PopOf(flipS2), baseline);
        Check("state-field/control: a switch whose only child stands outside A's 3x3 leaves A on the baseline field",
              keyS2 == EnablePopulation.BaselineStateName, $"key '{keyS2}'");

        var keyS3 = StateFieldKey.For(flipS3, ownA, nbA, PopOf(flipS3), baseline);
        Check("state-field/control: a switch that changes one of A's own references keys A's field on the state",
              keyS3 == flipS3.Name, $"key '{keyS3}'");

        var keyInterior = StateFieldKey.For(flipS, ownA, null, popS, baseline);
        var keyBase = StateFieldKey.For(StateArm.Baseline(), ownA, nbA, baseline, baseline);
        Check("state-field/control: with no floor neighbourhood (an interior) only the cell's own references count; the baseline arm is the baseline",
              keyInterior == EnablePopulation.BaselineStateName && keyBase == EnablePopulation.BaselineStateName,
              $"interior '{keyInterior}', baseline '{keyBase}'");
    }
}
