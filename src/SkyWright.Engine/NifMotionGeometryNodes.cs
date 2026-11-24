using HousecarlCore;

namespace SkyWright.Engine;

// WHICH DRIVEN NODES MOVE GEOMETRY (contactprobe pair-measure gaps, lane 3b, gap b). The 1-DOF blend rule refused a whole model
// when any driven node's poses left one family, including a node whose only children are particle systems (the forge's steam emitter):
// the motion reader already sets particle shapes aside (their geometry is runtime state), so a node that carries nothing else moves no
// measured geometry and its blends bound nothing. The rule is to be checked on the nodes some visible, non-particle shape depends on
// (its node chain, or a bone's chain for a skinned shape).
//
// STATUS: implemented (red at 7bbbe518).

public static partial class NifMotion
{
    /// <summary>The driven nodes whose blends the 1-DOF rule must bound: those on the node chain of a visible, non-particle shape,
    /// or on the chain of a bone of such a shape's skin. Order of <paramref name="driven"/> kept, duplicates dropped.</summary>
    internal static IReadOnlyList<string> BlendCheckNodes(MotionRig rig, IEnumerable<string> driven)
    {
        var all = driven.Distinct().ToList();
        var moving = new HashSet<string>(StringComparer.Ordinal);
        var blocks = rig.Nif.Blocks;
        foreach (var rs in rig.Shapes.Where(s => !s.Hidden && !s.Particle))
        {
            foreach (var n in rs.Chain) if (n.Name?.String is { } nm) moving.Add(nm);
            if (!(rs.Shape.IsSkinned || rs.Shape.HasSkinInstance)) continue;
            // the skin is read after the graph, so its bones come from the skin instance here; one that cannot be read keeps every
            // driven node (the rule as before), never fewer
            int skRef = (rs.Shape.GetType().GetProperty("SkinInstanceRef")?.GetValue(rs.Shape) as NiflySharp.INiRef)?.Index ?? -1;
            if (skRef < 0 || skRef >= blocks.Count || blocks[skRef] is not NiflySharp.Blocks.NiSkinInstance si) return all;
            foreach (var r in si.Bones.References)
            {
                if (r.Index < 0 || r.Index >= blocks.Count || blocks[r.Index] is not NiflySharp.Blocks.NiAVObject bone) return all;
                foreach (var n in rig.Chain(bone)) if (n.Name?.String is { } bn) moving.Add(bn);
            }
        }
        return all.Where(moving.Contains).ToList();
    }
}
