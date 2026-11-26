using System.Reflection;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Xunit;
using SkCell = Mutagen.Bethesda.Skyrim.Cell;

namespace SkyWright.Engine.Tests;

/// <summary>
/// Each placed arm's own base link (xEdit NAME, which Mutagen names Base, Hazard or Projectile by arm), read into
/// PlacedRef.ArmBase. Base itself stays the REFR/ACHR link only. C1750-C1755.
/// </summary>
[Trait("tier", "unit")]
public sealed class PlacedArmBaseTests : IDisposable
{
    static readonly SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    static readonly ModKey Mod = new("HcArmBase", ModType.Master);
    static FormKey K(uint id) => new(Mod, id);

    static readonly FormKey CellKey = K(0x800), HazardBase = K(0x810), ProjectileBase = K(0x811), StaticBase = K(0x812), NpcBase = K(0x813);
    const uint HazRef = 0x900, HazUnset = 0x901, RefrRef = 0x902, RefrNull = 0x903, NpcRef = 0x904, ProjUnset = 0x905;
    static readonly (uint Id, string Arm)[] ProjectileArms =
    {
        (0x910, "PlacedArrow"), (0x911, "PlacedBarrier"), (0x912, "PlacedBeam"), (0x913, "PlacedCone"),
        (0x914, "PlacedFlame"), (0x915, "PlacedMissile"), (0x916, "PlacedTrap"),
    };

    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-armbase-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    static Placement At => new() { Position = new P3Float(0, 0, 0), Rotation = new P3Float(0, 0, 0) };

    static IPlaced Projectile(string arm, FormKey key, FormKey? proj)
    {
        // Each arm declares its own Projectile (no shared interface carries it in 0.54.4), so each is set on its own type.
        var link = new FormLink<IProjectileGetter>(proj ?? FormKey.Null);
        IPlaced p = arm switch
        {
            "PlacedArrow" => new PlacedArrow(key, Rel) { Projectile = link },
            "PlacedBarrier" => new PlacedBarrier(key, Rel) { Projectile = link },
            "PlacedBeam" => new PlacedBeam(key, Rel) { Projectile = link },
            "PlacedCone" => new PlacedCone(key, Rel) { Projectile = link },
            "PlacedFlame" => new PlacedFlame(key, Rel) { Projectile = link },
            "PlacedMissile" => new PlacedMissile(key, Rel) { Projectile = link },
            _ => new PlacedTrap(key, Rel) { Projectile = link },
        };
        p.Placement = At;
        return p;
    }

    /// <summary>The fixture: a HAZD and a PROJ with models, a STAT, an NPC_, and one interior cell holding every arm.</summary>
    static SkyrimMod Build()
    {
        var mod = new SkyrimMod(Mod, Rel);
        mod.Hazards.Add(new Hazard(HazardBase, Rel) { EditorID = "HcArmHazard", Model = new Model { File = @"effects\hcarmhazard.nif" } });
        mod.Projectiles.Add(new Projectile(ProjectileBase, Rel) { EditorID = "HcArmProjectile", Model = new Model { File = @"weapons\hcarmarrow.nif" } });
        mod.Statics.Add(new Static(StaticBase, Rel) { EditorID = "HcArmStatic", Model = new Model { File = @"architecture\hcarmstatic.nif" } });
        mod.Npcs.Add(new Npc(NpcBase, Rel) { EditorID = "HcArmNpc" });

        var cell = new SkCell(CellKey, Rel) { Flags = SkCell.Flag.IsInteriorCell, EditorID = "HcArmCell" };
        var h = new PlacedHazard(K(HazRef), Rel) { Placement = At }; h.Hazard.SetTo(HazardBase); cell.Temporary.Add(h);
        cell.Temporary.Add(new PlacedHazard(K(HazUnset), Rel) { Placement = At });
        foreach (var (id, arm) in ProjectileArms) cell.Temporary.Add(Projectile(arm, K(id), ProjectileBase));
        cell.Temporary.Add(Projectile("PlacedArrow", K(ProjUnset), null));
        cell.Temporary.Add(new PlacedObject(K(RefrRef), Rel) { Placement = At, Base = new FormLinkNullable<IPlaceableObjectGetter>(StaticBase) });
        cell.Temporary.Add(new PlacedObject(K(RefrNull), Rel) { Placement = At, Base = new FormLinkNullable<IPlaceableObjectGetter>(FormKey.Null) });
        var n = new PlacedNpc(K(NpcRef), Rel) { Placement = At }; n.Base.SetTo(NpcBase); cell.Temporary.Add(n);

        var block = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell); block.SubBlocks.Add(sub); mod.Cells.Records.Add(block);
        return mod;
    }

    /// <summary>The refs by FormID, read from the in-memory mod ("mutable") or from the written file ("overlay"), which is
    /// what production reads.</summary>
    Dictionary<uint, PlacedRef> Refs(string path)
    {
        var mod = Build();
        if (path == "mutable") return PlacementExtract.FromMod(mod).SelectMany(c => c.Refs).ToDictionary(r => r.FormKey.ID);
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, Mod.FileName.String);
        mod.BeginWrite.ToPath(file).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        using var ov = SkyrimMod.CreateFromBinaryOverlay(file, Rel);
        return PlacementExtract.FromMod(ov).SelectMany(c => c.Refs).ToDictionary(r => r.FormKey.ID);
    }

    // C1750. Rejects: the hazard's base left unread (null), or read into Base, which every mesh consumer reads.
    [Theory, InlineData("mutable"), InlineData("overlay")]
    public void C1750_placed_hazard_arm_base_is_its_hazard(string path)
    {
        var r = Refs(path)[HazRef];
        Assert.Equal("PlacedHazard", r.RecordType);
        Assert.Equal(HazardBase, r.ArmBase);
        Assert.Equal("Hazard", r.ArmBaseLink);
        Assert.Null(r.Base);
    }

    // C1751. Rejects: any of the seven projectile arms left unread, or its PROJ leaking into Base.
    [Theory, InlineData("mutable"), InlineData("overlay")]
    public void C1751_each_projectile_arm_base_is_its_projectile(string path)
    {
        var refs = Refs(path);
        var wrong = ProjectileArms.Where(a => refs[a.Id] is var r
                                              && !(r.RecordType == a.Arm && r.ArmBase == ProjectileBase && r.ArmBaseLink == "Projectile" && r.Base is null))
                                  .Select(a => $"{a.Arm}: arm {refs[a.Id].RecordType} armBase {refs[a.Id].ArmBase} link {refs[a.Id].ArmBaseLink} base {refs[a.Id].Base}")
                                  .ToList();
        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    // C1752. Rejects: an unset non-nullable link read as FormKey.Null (a "base" every lookup misses), and an explicitly
    // null REFR base kept as FormKey.Null (decision 4: a null key reads as no base on every arm).
    [Theory, InlineData("mutable"), InlineData("overlay")]
    public void C1752_unset_or_null_links_read_as_no_base(string path)
    {
        var refs = Refs(path);
        Assert.Null(refs[HazUnset].ArmBase);
        Assert.Null(refs[ProjUnset].ArmBase);
        Assert.Null(refs[RefrNull].Base);
        Assert.Null(refs[RefrNull].ArmBase);
    }

    // C1753. Control: REFR and ACHR read their bases as before, and ArmBase agrees with Base on them.
    [Theory, InlineData("mutable"), InlineData("overlay")]
    public void C1753_refr_and_achr_unchanged(string path)
    {
        var refs = Refs(path);
        Assert.Equal(StaticBase, refs[RefrRef].Base);
        Assert.Equal(StaticBase, refs[RefrRef].ArmBase);
        Assert.Equal("Base", refs[RefrRef].ArmBaseLink);
        Assert.Equal(NpcBase, refs[NpcRef].Base);
        Assert.Equal(NpcBase, refs[NpcRef].ArmBase);
    }

    // C1754. Rejects: an arm (a new one, or a rename on a Mutagen bump) whose base link no name in the lookup reaches, or one
    // where two names resolve. Every concrete placed type in the pinned assembly, mutable and overlay alike.
    [Fact]
    public void C1754_every_placed_type_resolves_exactly_one_base_link()
    {
        var types = typeof(IPlacedGetter).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IPlacedGetter).IsAssignableFrom(t)).ToList();
        Assert.True(types.Count >= 20, $"expected the ten arms' mutable and overlay types, found {types.Count}");
        var bad = new List<string>();
        foreach (var t in types)
        {
            var hits = PlacementExtract.ArmBaseLinkNames
                .Where(n => t.GetProperty(n, BindingFlags.Public | BindingFlags.Instance) is { } p && typeof(IFormLinkGetter).IsAssignableFrom(p.PropertyType))
                .ToList();
            if (hits.Count != 1) bad.Add($"{t.Name}: [{string.Join(",", hits)}]");
            else if (PlacementExtract.ArmBaseLinkOf(t) != hits[0]) bad.Add($"{t.Name}: reader says {PlacementExtract.ArmBaseLinkOf(t)}, type has {hits[0]}");
        }
        Assert.True(bad.Count == 0, string.Join("; ", bad));
    }

    // C1755. The premise the stateprobe census rests on: HAZD and PROJ are modeled records, so the base index names their
    // type and editor id. Green on the base by design; red would mean the census cannot name them.
    [Fact]
    public void C1755_hazard_and_projectile_are_modeled()
    {
        Assert.True(typeof(IModeledGetter).IsAssignableFrom(typeof(IHazardGetter)), "IHazardGetter is not IModeledGetter");
        Assert.True(typeof(IModeledGetter).IsAssignableFrom(typeof(IProjectileGetter)), "IProjectileGetter is not IModeledGetter");
    }
}
