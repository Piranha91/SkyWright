using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

// THE PER-REFERENCE DECISIONS of step 4 (Program.cs): whether a target cell's reference is judged in that cell at all, and
// whether the route rule measures it or sets it aside. Held in their own class so --self-test runs exactly the decisions a
// real run makes; the geometry around them stays in Program.cs.
static class RefAdmission
{
    /// <summary>The winning placement of a reference the target holds in <paramref name="cellKey"/>, or null when it is not
    /// judged there: absent from the winning state, its winning cell another (a later plugin moved it), or deleted.</summary>
    public static PlacedRef? Winning(FormKey key, FormKey cellKey, Dictionary<FormKey, PlacedRef> winRef, Dictionary<FormKey, FormKey> winRefCell)
    {
        if (!winRef.TryGetValue(key, out var r)) return null;
        if (winRefCell.TryGetValue(key, out var wc) && wc != cellKey) return null;
        if (r.Deleted) return null;
        return r;
    }

    public enum SetAside { Door, Movable, Measure }

    /// <summary>The route rule's choice by the base record's type: a Door is passable by design (the navmesh runs through the
    /// doorway), a MoveableStatic is pushed aside (Part C's urn); both are set aside and counted. Everything else is measured.</summary>
    public static SetAside RouteSetAside(string baseType)
    {
        if (baseType == "Door") return SetAside.Door;
        if (baseType == "MoveableStatic") return SetAside.Movable;
        return SetAside.Measure;
    }
}
