using System.Numerics;
using NiflySharp;
using NiflySharp.Blocks;

using HousecarlCore;

namespace SkyWright.Engine;

/// <summary>What the files say about one weapon rack's starting item (rest rule part 2): the activator reference whose default
/// (keyword-less) linked reference is the item, its script and the script's rack properties, the trigger reference its
/// <c>WRackTrigger</c> link names, and the item's own facts. Enabled flags are the judged state's.</summary>
public sealed record RackFacts(string ActivatorScript, int RackType, bool CoaPatch, bool ActivatorEnabled, bool TriggerPresent,
                               bool TriggerEnabled, bool ItemEnabled, bool SameCell, string ItemKind, IReadOnlySet<string> ItemKeywords,
                               bool ItemHasCrossbowRoot = false, bool ItemIsForswornStaff = false, bool TriggerHasBogusPivot = false);

/// <summary>A world pose: position and rotation (radians, the record's Euler convention).</summary>
public sealed record RackPose(Vec3 Position, Vec3 Rotation);

/// <summary>The rack rule's reading for one item: <c>mounted</c> (moved to <c>Node</c> of the trigger's model at <c>Pose</c>, keyframed),
/// <c>not-mounted</c> (the script leaves it: a type the rack rejects), or <c>refused</c> (the chain is incomplete; <c>Reason</c> names
/// what is missing).</summary>
public sealed record RackMountReading(string Status, string? Reason, string? Node, RackPose? Pose, string? Trigger = null);

/// <summary>Rest rule part 2: a weapon rack activator (USSEP's <c>WeaponRackActivateSCRIPT</c>) mounts its default linked reference at
/// cell attach onto the <c>&lt;Type&gt;Pivot01</c> node of the trigger reference (runs/2026-10-13-day-ci-contact-drop/O2-O3.md).</summary>
public static class RackMount
{
    public const string ActivatorScript = "WeaponRackActivateSCRIPT";
    public const string MountedLabel = "rest: mounted by a weapon rack at load (WeaponRackActivateSCRIPT); keyframed after mounting";
    public const string RefusedReason = "a weapon rack's starting item: mounting not read";

    static readonly (string Keyword, string Node)[] Pivots =
    {
        ("WeaponTypeSword", "SwordPivot01"), ("WeaponTypeWarAxe", "WarAxePivot01"), ("WeaponTypeMace", "MacePivot01"),
        ("WeaponTypeGreatSword", "GreatSwordPivot01"), ("WeaponTypeBattleAxe", "BattleAxePivot01"), ("WeaponTypeWarhammer", "WarhammerPivot01"),
    };

    /// <summary>Decide the reading from the facts; <paramref name="pivotWorld"/> gives a trigger model node's world pose, or null when
    /// the model has no such node. Follows USSEP's ActivatorSetup / CheckRackType / PlaceItem order.</summary>
    public static RackMountReading Decide(RackFacts facts, Func<string, RackPose?> pivotWorld)
    {
        RackMountReading No() => new("not-mounted", null, null, null);
        RackMountReading Refused(string why) => new("refused", $"{RefusedReason} ({why})", null, null);
        if (facts.ActivatorScript != ActivatorScript) return No();
        if (!facts.ActivatorEnabled) return Refused("the rack activator is disabled; whether its script runs at attach is not read");
        if (!facts.TriggerPresent || !facts.TriggerEnabled) return No();             // CheckConfiguration fails: the script goes inactive
        if (!facts.ItemEnabled || !facts.SameCell) return No();                      // the starting item is skipped
        bool Has(string k) => facts.ItemKeywords.Contains(k);
        bool weapon = facts.ItemKind == "weapon", shield = facts.ItemKind == "shield";
        // CheckRackType: a rejected type gets a message only, it is not moved
        if (facts.RackType == 3 || facts.CoaPatch)
        {
            if (!weapon || Has("WeaponTypeDagger") || Has("WeaponTypeBow")) return No();
            if (facts.ItemIsForswornStaff && facts.TriggerHasBogusPivot) return No();
        }
        else if (facts.RackType is 1 or 5)
        {
            if (!weapon) return No();
            if (facts.ItemIsForswornStaff && facts.TriggerHasBogusPivot) return No();
        }
        else if (facts.RackType == 2) { if (!shield) return No(); }
        else if (facts.RackType == 4) { if (!Has("WeaponTypeDagger")) return No(); }
        else return Refused($"rack type {facts.RackType} is not one the script knows");
        // PlaceItem: the first keyword in the script's order names the pivot
        string? node = Pivots.FirstOrDefault(p => Has(p.Keyword)).Node;
        if (node is null)
        {
            if (Has("WeaponTypeBow"))
            {
                if (facts.ItemHasCrossbowRoot && pivotWorld("CrossbowPivot01") is not null)
                    return Refused("a crossbow: the choice between CrossbowPivot01 and CrossbowPivot02 is not read");
                node = "BowPivot01";
            }
            else if (Has("WeaponTypeStaff")) return Refused("a staff: the script's named staff pivots are not read");
            else if (Has("WeaponTypeDagger")) node = "DaggerPivot01";
            else if (shield) return Refused("a shield: the pivot by shield material is not read");
            else return No();                                                         // rejected for the trigger, not moved
        }
        return pivotWorld(node) is { } pose ? new("mounted", null, node, pose)
                                            : Refused($"no {node} in the trigger's model");
    }

    /// <summary>The world pose of a named node of the trigger's model (the root's own transform left out, as the rig reads models),
    /// placed by the trigger reference's position, rotation and scale; null when the model has no such node. The item's origin and
    /// axes go to that node (<c>MoveToNode</c>). The rotation is returned as record Euler angles (<see cref="SpatialOverlap.RefrRotation"/>)
    /// and checked by recomposition: a pose the convention cannot express throws, never silently drifts.</summary>
    public static RackPose? NodePose(byte[] triggerNif, string node, Vec3 position, Vec3 rotation, float scale)
    {
        var f = new NifFile();
        using (var ms = new MemoryStream(triggerNif, writable: false))
            if (f.Load(ms) != 0) throw new InvalidDataException("the trigger's NIF could not be read");
        var n = f.Blocks.OfType<NiAVObject>().FirstOrDefault(b => string.Equals(b.Name?.String, node, StringComparison.OrdinalIgnoreCase));
        if (n is null) return null;
        var chain = new List<NiAVObject>();
        for (var cur = n; cur is not null; cur = f.GetParentNode(cur))
        {
            if (chain.Count >= 256) throw new InvalidOperationException("a node chain is cyclic or exceeds 256 nodes");
            chain.Add(cur);
        }
        var r = Matrix4x4.Identity; var t = Vector3.Zero; float sc = 1f;
        for (int i = chain.Count - 2; i >= 0; i--)          // root's own transform left out
        {
            var c = chain[i]; var cr = MotionRig.Rows(c.Rotation);
            t = Vector3.Transform(c.Translation * sc, r) + t; r = cr * r; sc *= c.Scale;
        }
        var refR = SpatialOverlap.RefrRotation(rotation);
        var world = r * refR;
        var pos = Vector3.Transform(t * scale, refR) + new Vector3(position.X, position.Y, position.Z);
        var rot = FromRefr(world);
        var back = SpatialOverlap.RefrRotation(rot);
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++)
            if (MathF.Abs(Get(back, i, j) - Get(world, i, j)) > 1e-4f) throw new InvalidOperationException($"node '{node}': its world rotation is not a record Euler rotation");
        return new RackPose(new Vec3(pos.X, pos.Y, pos.Z), rot);
    }

    static float Get(Matrix4x4 m, int i, int j) => (i, j) switch
    {
        (0, 0) => m.M11, (0, 1) => m.M12, (0, 2) => m.M13, (1, 0) => m.M21, (1, 1) => m.M22, (1, 2) => m.M23,
        (2, 0) => m.M31, (2, 1) => m.M32, _ => m.M33,
    };

    /// <summary>The record angles whose <see cref="SpatialOverlap.RefrRotation"/> is <paramref name="m"/>: RefrRotation(x, y, z) =
    /// Rz(a)·Ry(b)·Rx(c) (System.Numerics, row vectors) with a = −z, b = −y, c = −x, so M31 = sin b, M32 = −cos b sin c, M33 = cos b cos c,
    /// M21 = −sin a cos b, M11 = cos a cos b.</summary>
    static Vec3 FromRefr(Matrix4x4 m)
    {
        float b = MathF.Asin(Math.Clamp(m.M31, -1f, 1f));
        float c = MathF.Atan2(-m.M32, m.M33);
        float a = MathF.Atan2(-m.M21, m.M11);
        return new Vec3(-c, -b, -a);
    }
}
