using System.Numerics;
using HarnessShared;
using Mutagen.Bethesda.Plugins;
using SkyWright.Engine;

// overlapprobe --self-test, Gate C: the nudge judge's rule (NudgeJudge.JudgeAll) on synthetic placed boxes, no plugins.
// A candidate is rejected when it crosses the other object of the row, or crosses any object it did not cross at its
// load-order place (another mod's, the same mod's or the base game's); a crossing already there at home is not held
// against it. Boxes are closed render boxes, half extent 10 u, unrotated:
//   S subject (NjMod.esp) home (0,0,0); O the row's other (NjOther.esp) (15,0,0), crossed at home;
//   H (NjOther.esp) (0,15,0), crossed at home; X (NjMod.esp, the subject's own mod) (-40,0,0); B (Skyrim.esm) (0,-40,0).
//   Candidates: 1 (5,0,0) crosses O; 2 (-25,0,0) crosses X only; 3 (0,-25,0) crosses B only; 4 (-8,22,0) crosses H only;
//   5 (0,60,0) crosses nothing.
static class NudgeSelfTest
{
    static readonly string[] Plugins = { "Skyrim.esm", "NjMod.esp", "NjOther.esp" };
    static readonly ModKey Mod = ModKey.FromNameAndExtension("NjMod.esp"), Other = ModKey.FromNameAndExtension("NjOther.esp"), Sky = ModKey.FromNameAndExtension("Skyrim.esm");
    internal static readonly FormKey S = new(Mod, 0x801), O = new(Other, 0x801), H = new(Other, 0x802), X = new(Mod, 0x802), B = new(Sky, 0x801);
    static readonly FormKey Cell = new(Sky, 0x900);

    static TriangleSoup Box(float h)
    {
        var v = new Vector3[8];
        for (int i = 0; i < 8; i++) v[i] = new Vector3((i & 1) == 0 ? -h : h, (i & 2) == 0 ? -h : h, (i & 4) == 0 ? -h : h);
        int[] f = { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5 };
        return new TriangleSoup(v, f);
    }

    internal static SurfaceOverlapCandidate Placed(FormKey key, int plugin, float x, float y, float z)
    {
        var model = Box(10f);
        var snap = new Snap(plugin, new Vec3(x, y, z), Cell, false, false, FormKey.Null, new Vec3(0, 0, 0), 1f);
        var world = SpatialOverlap.TransformSoup(model, snap.Pos, snap.Rot, snap.Scale);
        return new SurfaceOverlapCandidate(key, snap, new FormKey(Sky, 0x800), SpatialOverlap.SoupBounds(world), world, 'm', model);
    }

    public static void Run(ProbeSelfTest t)
    {
        var s = Placed(S, 1, 0, 0, 0);
        var near = new[] { Placed(O, 2, 15, 0, 0), Placed(H, 2, 0, 15, 0), Placed(X, 1, -40, 0, 0), Placed(B, 0, 0, -40, 0) };
        var poses = new (int, Vec3)[] { (1, new Vec3(5, 0, 0)), (2, new Vec3(-25, 0, 0)), (3, new Vec3(0, -25, 0)), (4, new Vec3(-8, 22, 0)), (5, new Vec3(0, 60, 0)) };
        var (home, c) = NudgeJudge.JudgeAll(s, O, near, poses, p => BaseGamePlugins.IsCoreMaster(Plugins[p]), p => Plugins[p]);
        NudgeVerdict V(int n) => c.Single(x => x.N == n);
        string Said(NudgeVerdict v) => $"clear {v.Clear}, other {v.OtherHit}, new [{string.Join(", ", v.NewCrossings.Select(h => $"{h.Key} {h.Who}"))}], inherited [{string.Join(", ", v.Inherited)}]";
        t.Check("NJ-1 at home the subject crosses the row's other object and H", home.OtherHit && home.Inherited.Contains(H), Said(home));
        t.Check("NJ-2 a candidate that still crosses the other object is rejected", !V(1).Clear && V(1).OtherHit, Said(V(1)));
        t.Check("NJ-3 a candidate with a new crossing with an object of the subject's own mod is rejected",
                !V(2).Clear && V(2).NewCrossings.Any(h => h.Key == X && h.Who == "same mod"), Said(V(2)));
        t.Check("NJ-4 a candidate with a new crossing with a base-game object is rejected",
                !V(3).Clear && V(3).NewCrossings.Any(h => h.Key == B && h.Who == "base game"), Said(V(3)));
        t.Check("NJ-5 a crossing already there at home is not held against a candidate",
                V(4).Clear && V(4).Inherited.Contains(H) && V(4).NewCrossings.Count == 0, Said(V(4)));
        t.Check("NJ-6 a candidate that crosses nothing is clear", V(5).Clear && V(5).NewCrossings.Count == 0 && !V(5).OtherHit, Said(V(5)));
    }
}
