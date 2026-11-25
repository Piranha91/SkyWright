using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

// PROOF BY CONSTRUCTION — a trap arm's own base link reaches the index only as RefArmBase (C1758). RefBase and BaseChanges,
// which every mesh-by-base consumer reads, stay as they were for the trap arms, so no hazard or projectile mesh becomes a
// floor, a box or a solid shape by this change. A REFR's base reaches both. All in memory.
static class ArmBaseProof
{
    static readonly ModKey Mod = ModKey.FromNameAndExtension("ArmBaseProof.esp");
    static FormKey F(uint id) => new(Mod, id);

    static PlacedRef Ref(FormKey k, string arm, FormKey? @base, FormKey? armBase, string? link) =>
        new(k, arm, null, new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f, @base, false, false, false,
            ArmBase: armBase, ArmBaseLink: link);

    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nPROOF — a trap arm's base reaches the index as RefArmBase only (_shared/OverlapIndex.cs, C1758)");
        FormKey cell = F(0x800), hazRef = F(0x900), arrowRef = F(0x901), refr = F(0x902);
        FormKey haz = F(0x810), proj = F(0x811), stat = F(0x812), haz2 = F(0x813);

        var idx = new OverlapIndex();
        idx.Record(0, cell, FormKey.Null, Ref(hazRef, "PlacedHazard", null, haz, "Hazard"));
        idx.Record(0, cell, FormKey.Null, Ref(arrowRef, "PlacedArrow", null, proj, "Projectile"));
        idx.Record(0, cell, FormKey.Null, Ref(refr, "PlacedObject", stat, stat, "Base"));
        // a later plugin re-bases the hazard: the arm base follows the winner, RefBase and BaseChanges still say nothing
        idx.Record(1, cell, FormKey.Null, Ref(hazRef, "PlacedHazard", null, haz2, "Hazard"));

        Check("arm-base/C1758 a: a trap arm's own link is in RefArmBase, at the last plugin's value",
              idx.RefArmBase.GetValueOrDefault(hazRef) == (haz2, "Hazard") && idx.RefArmBase.GetValueOrDefault(arrowRef) == (proj, "Projectile"),
              $"hazard {idx.RefArmBase.GetValueOrDefault(hazRef)}, arrow {idx.RefArmBase.GetValueOrDefault(arrowRef)}");
        Check("arm-base/C1758 b: no trap arm reaches RefBase or BaseChanges (what the mesh-by-base consumers read)",
              !idx.RefBase.ContainsKey(hazRef) && !idx.RefBase.ContainsKey(arrowRef)
              && !idx.BaseChanges.ContainsKey(hazRef) && !idx.BaseChanges.ContainsKey(arrowRef),
              $"RefBase {string.Join(",", idx.RefBase.Keys)}; BaseChanges {string.Join(",", idx.BaseChanges.Keys)}");
        Check("arm-base/C1758 c: control, a REFR's base reaches both maps",
              idx.RefBase.GetValueOrDefault(refr) == stat && idx.RefArmBase.GetValueOrDefault(refr) == (stat, "Base"),
              $"RefBase {idx.RefBase.GetValueOrDefault(refr)}, RefArmBase {idx.RefArmBase.GetValueOrDefault(refr)}");
    }
}
