using HousecarlCore;
using System.Collections.Concurrent;
using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace SkyWright.Engine;

// ======================================================================
//  PlacementExtract — the SPATIAL PRIMITIVE (geometry-deconfliction wave 1).
//
//  What it is: every placed reference in a plugin, with the world transform the
//  engine will apply to it, plus the navmesh geometry of the cell it sits in.
//  Pure data out; no notion of "conflict", "overlap", or any one job's domain
//  knowledge (CONTRIBUTING: "domain knowledge belongs in skills as data").
//
//  Generality by construction: the transform + scale live on IPlacedGetter, the
//  BASE interface of ALL ten placed arms (PlacedObject, PlacedNpc, PlacedArrow,
//  Barrier, Beam, Cone, Flame, Hazard, Missile, Trap), and cells hand them back
//  as IReadOnlyList<IPlacedGetter>. Nothing here switches on a record type. The
//  ONE per-type-shaped field, the base link (xEdit's NAME), is read by REFLECTION
//  over Mutagen's model: it is declared per arm under a different NAME as well as
//  a different target. MUTAGEN LAGS XEDIT HERE: xEdit calls it Base on every arm;
//  Mutagen 0.54.4 says Base on PlacedObject and PlacedNpc, Hazard on PlacedHazard,
//  Projectile on the seven projectile arms. Base carries the first two only, as it
//  always has; ArmBase carries every arm's link, so a consumer opts in to the trap
//  arms' bases. The lookup is by property name, never by record type.
//
//  API grounded by reflection probe against the pinned Mutagen.Bethesda.Skyrim
//  0.54.4 before a line was written — not from memory.
// ======================================================================

/// <summary>A bare 3-float vector. Deliberately NOT Mutagen's P3Float nor NiflySharp's Vector3: this record is the
/// hand-off point between the two libraries, and owning the type keeps the geometry layer from taking a dependency
/// on the plugin layer (or the reverse).</summary>
public readonly record struct Vec3(float X, float Y, float Z)
{
    public static Vec3 From(P3Float p) => new(p.X, p.Y, p.Z);
    public override string ToString() => $"({X:0.##}, {Y:0.##}, {Z:0.##})";
}

/// <summary>One placed reference with everything needed to put its model in the world, and nothing else.</summary>
/// <param name="RecordType">The placed arm's catalog name — "PlacedObject", "PlacedNpc", "PlacedHazard", …</param>
/// <param name="Position">World translation, Skyrim units.</param>
/// <param name="Rotation">Euler XYZ in RADIANS (Mutagen surfaces the raw record value, which the engine reads as radians).</param>
/// <param name="Scale">Uniform scale; 1.0 when the record omits it (the engine's default).</param>
/// <param name="Base">The base record this instances through a property named Base (PlacedObject, PlacedNpc) — null on the
/// trap arms, whose link Mutagen names differently (see <paramref name="ArmBase"/>), and for an unset or null link.</param>
/// <param name="Persistent">Came from the cell's Persistent list rather than Temporary.</param>
/// <param name="InitiallyDisabled">Starts disabled (flag 0x800). A CALLER's filter, not applied here — an enable-parent
/// can switch it on at runtime, so dropping these silently would hide real conflicts. Surfaced, never swallowed.</param>
/// <param name="EnableParent">XESP: the ref whose runtime enabled state this ref follows, or null when there is none.
/// With a parent, the ref's own InitiallyDisabled flag is bookkeeping — the runtime state is the parent's (XOR
/// <paramref name="EnableParentOpposite"/>), so a caller honouring InitiallyDisabled without evaluating the parent
/// drops refs the engine enables. The player ref (ID 0x14) is always enabled at runtime; parent = player with the
/// opposite flag is the standard "permanently disabled" idiom.</param>
/// <param name="EnableParentOpposite">XESP flag SetEnableStateToOppositeOfParent.</param>
/// <param name="LinkedReferences">XLKR: the refs this ref is linked to, each under an optional keyword (patrol chains,
/// a lever and its gate, decoration sets). Null or empty when there are none — the two are equivalent to a caller.
/// Read by reflection like <paramref name="Base"/>: the list is declared per arm, not on the shared placed interface.</param>
/// <param name="ActivateParents">XAPR: the refs activated along with this one, each with its delay. Null or empty when
/// there are none. Lives on the shared placed interface, so every arm carries it.</param>
/// <param name="Primitive">XPRM: the primitive volume the reference carries — a trigger's box, an occlusion plane's
/// portal box. Null when the arm has none or the record omits it. Read by reflection like <paramref name="Base"/>.</param>
/// <param name="EncounterZone">XEZN: the reference's own encounter zone, or null. Read by reflection like
/// <paramref name="Base"/>. What the Base Object Swapper reader asks before honouring a swap to a leveled item.</param>
/// <param name="CollisionLayerIndex">XTRI: the collision layer the reference's primitive collides as, by the layer's
/// INDEX — the number a COLL record carries in its Index field (L_NAVCUT is 49; measured 2026-09-04: the raw value on
/// every navcut box in Skyrim.esm is 0x31, and the record reads Index = 49), not a FormID. A caller names the layer
/// by matching the index against the order's COLL records. Null when the arm has none or the record omits it. Read by
/// reflection like <paramref name="Base"/>.</param>
/// <param name="OpenByDefault">ONAM: the reference starts open (<c>IPlacedObjectGetter.IsOpenByDefault</c>); false for
/// every other placed arm. Read by the motion reader's rest pose (animated motion follow-ups, E3).</param>
/// <param name="ArmBase">The arm's own base link (xEdit NAME), whatever Mutagen names it: Base, Hazard (a HAZD) or Projectile
/// (a PROJ). Equal to <paramref name="Base"/> on PlacedObject and PlacedNpc. Null for an unset or null link. Whether a placed
/// hazard or projectile has a body in game is not measured, so a consumer reads this only where it opts in.</param>
/// <param name="ArmBaseLink">The Mutagen property <paramref name="ArmBase"/> was read from ("Base", "Hazard", "Projectile").</param>
public sealed record PlacedRef(
    FormKey FormKey,
    string RecordType,
    string? EditorId,
    Vec3 Position,
    Vec3 Rotation,
    float Scale,
    FormKey? Base,
    bool Persistent,
    bool InitiallyDisabled,
    bool Deleted,
    FormKey? EnableParent = null,
    bool EnableParentOpposite = false,
    IReadOnlyList<PlacedLink>? LinkedReferences = null,
    IReadOnlyList<PlacedActivateParent>? ActivateParents = null,
    PlacedPrimitiveInfo? Primitive = null,
    FormKey? EncounterZone = null,
    uint? CollisionLayerIndex = null,
    bool OpenByDefault = false,
    uint RecordFlags = 0,
    IReadOnlyList<string>? Scripts = null,
    FormKey? ArmBase = null,
    string? ArmBaseLink = null);

/// <summary>One XPRM: the primitive's kind by the record's own enum name ("Box", "Sphere", "PortalBox", "None") and its
/// bounds as stored. <paramref name="Bounds"/> are FULL extents, centred on the reference's position: the vanilla
/// occlusion planes that also carry the CK-era <c>OcclusionPlane</c> subrecord store exactly half of X and Z there as
/// Width and Height (2026-09-04, every unaltered vanilla row), so the box spans ±Bounds/2 about the ref along each of
/// its local axes.</summary>
public readonly record struct PlacedPrimitiveInfo(string Type, Vec3 Bounds);

/// <summary>One XLKR entry: the linked ref and the keyword the link is filed under (null for a bare link).</summary>
public readonly record struct PlacedLink(FormKey Reference, FormKey? Keyword);

/// <summary>One XAPR entry: a ref activated together with the carrying ref, after <paramref name="Delay"/> seconds.</summary>
public readonly record struct PlacedActivateParent(FormKey Reference, float Delay);

/// <summary>One navmesh's geometry, straight out of the plugin — NO NIF involved. Vertices are the record's own values;
/// Triangles index into them.</summary>
/// <param name="Min">The record's own stated bounds. NOT recomputed here — a caller needing a trustworthy AABB should
/// derive it from Vertices, since stale MIN/MAX is a known artifact of hand-edited navmeshes.</param>
/// <param name="EdgeLinks">The record's own NAVM edge-link table, indexed by the triangles' EdgeLink_* fields when those
/// carry the matching flag bit. Null when the geometry was built by an older caller that did not record them; empty when
/// the record genuinely has none.</param>
/// <param name="DoorTriangles">The record's door table: which triangle sits in front of which placed door. A door is how
/// two navmeshes in DIFFERENT cells connect, so this is the only portal the edge-link table cannot express.</param>
/// <param name="Deleted">The record is flagged DELETED although it still carries its geometry. The game does not load a
/// deleted record, so a caller that decides which version of a navmesh stands treats this sighting as taking it out, as it
/// does a <see cref="CellPlacements.DeletedNavmeshes"/> entry. Added 2026-09-28; false from callers that construct the
/// geometry themselves.</param>
public sealed record NavmeshGeometry(
    FormKey FormKey,
    IReadOnlyList<Vec3> Vertices,
    IReadOnlyList<NavTri> Triangles,
    Vec3 Min,
    Vec3 Max,
    IReadOnlyList<NavEdgeLink>? EdgeLinks = null,
    IReadOnlyList<NavDoorTriangle>? DoorTriangles = null,
    bool Deleted = false);

/// <param name="Flags">Raw NavmeshTriangle.Flag bits. 0x08 = Deleted, 0x20 = Overlapping, 0x200 = Water, 0x400 = Door.
/// Kept RAW: which of these counts as "walkable" is a judgement, and judgement belongs in the skill.</param>
/// <param name="EdgeLink01">The neighbour across the A-B edge. Its meaning is SWITCHED by flag bit 0x01: clear, this is a
/// triangle index in the SAME navmesh (-1 = no neighbour, i.e. a mesh boundary); set, it is an index into the record's own
/// <see cref="NavmeshGeometry.EdgeLinks"/> table, which names another navmesh and a triangle in it. Same rule for the other
/// two edges with bits 0x02 and 0x04. Kept RAW for the same reason the flags are: resolving the switch is the caller's
/// judgement, and a caller that only wants intra-mesh adjacency should not be handed a half-resolved graph.</param>
public readonly record struct NavTri(short A, short B, short C, int Flags,
                                     short EdgeLink01 = -1, short EdgeLink12 = -1, short EdgeLink20 = -1);

/// <summary>One entry of a navmesh's NAVM edge-link table: the mesh and triangle a cross-mesh edge leads to.</summary>
/// <param name="Type">xEdit's edge-link Type: 0 Portal, 1 Ledge Up, 2 Ledge Down, 3 Enable/Disable Portal. MUTAGEN LAGS
/// XEDIT HERE: Mutagen 0.54.4 names this field <c>EdgeLink.Unknown</c>; the byte layouts match (xEdit wbDefinitionsTES5.pas,
/// Mutagen NavigationMesh.xml). Kept raw; what each type means for walking is <see cref="NavGraph"/>'s policy.</param>
public readonly record struct NavEdgeLink(FormKey Mesh, short TriangleIndex, int Type = 0);

/// <summary>One entry of a navmesh's door table: the triangle standing in front of a placed door reference.</summary>
public readonly record struct NavDoorTriangle(FormKey Door, short TriangleBeforeDoor);

/// <summary>A placed record flagged deleted with no placement data (see <see cref="CellPlacements.DeletedRefs"/>).</summary>
/// <param name="RecordType">The placed arm's catalog name, as <see cref="PlacedRef.RecordType"/>.</param>
/// <param name="Persistent">Came from the cell's Persistent list rather than Temporary.</param>
public readonly record struct DeletedRef(FormKey FormKey, string RecordType, bool Persistent);

/// <summary>A navmesh record that carries no geometry (see <see cref="CellPlacements.DeletedNavmeshes"/>).</summary>
/// <param name="FlaggedDeleted">The record is flagged DELETED, the usual shape of a later plugin's deletion. False for a
/// record that is not flagged and still has no NVNM.</param>
public readonly record struct DeletedNavmesh(FormKey FormKey, bool FlaggedDeleted);

/// <summary>Every placed reference and navmesh in ONE cell, with the cell's own spatial identity.</summary>
/// <param name="Worldspace">FormKey.Null for an interior cell — interiors have their own private coordinate space and
/// can never collide with anything outside themselves.</param>
/// <param name="Grid">Exterior cell grid coordinates (X,Y); null for interiors.</param>
/// <param name="Land">The cell's LAND heightmap decoded to world heights (<see cref="LandHeight.Decode"/>, [x, y] over
/// the 33×33 vertices); null for an interior or a cell record without one. The floor under most exterior navmesh, which
/// no NIF carries (<see cref="NavmeshFloor"/>, 2026-09-05); 4 KB per exterior cell.</param>
/// <param name="DeletedRefs">Placed records in this cell that are flagged DELETED and carry no placement data, so they
/// are not in <paramref name="Refs"/> (nothing spatial can be said about them). A later plugin's deletion of a reference
/// usually looks like this, and the game does not load a deleted reference: a caller that decides which plugin's
/// version of a reference wins must treat such an entry as that reference's sighting. Added 2026-09-27; null from
/// callers that construct a CellPlacements themselves, and empty when the cell has none.</param>
/// <param name="DeletedNavmeshes">Navmesh records in this cell that carry no geometry (no NVNM), so they are not in
/// <paramref name="Navmeshes"/>: nearly always a later plugin's deletion, flagged as such (<see cref="DeletedNavmesh.FlaggedDeleted"/>).
/// Such a record is the navmesh's winning version when it is the last sighting, and it has nothing to walk on, so a caller
/// that decides which version of a navmesh stands must treat an entry as a sighting that takes the navmesh out. Added
/// 2026-09-28; null from callers that construct a CellPlacements themselves, and empty when the cell has none.</param>
/// <param name="LandUnreadableReason">Why an exterior cell's LAND gave no heights although the record says it has them:
/// null when the heightmap decoded (or the cell has no LAND, is an interior, or its LAND declares no heights, see
/// <paramref name="LandDeclaresNoHeights"/>). Set when reading the LAND or its heightmap throws, or when the LAND's DATA
/// flag says it has heights (VertexNormalsHeightMap) and it carries no VHGT; <paramref name="Land"/> is then null. Such a
/// LAND is not a trustworthy floor, and keeping an earlier plugin's heights in its place would be a silent wrong answer, so
/// a caller refuses or counts on this. It catches a heightmap Mutagen FAILS to read; one it misreads without an exception
/// (a VHGT cut short in the middle of a plugin reads the following bytes, measured 2026-09-27) is not seen here. Added
/// 2026-09-27; distinct from CellBoundaryIndex's per-cell LandUnreadable counter.</param>
/// <param name="LandDeclaresNoHeights">The cell's LAND is present, its DATA flag does NOT say it has heights
/// (VertexNormalsHeightMap clear, or no DATA), and it carries no VHGT: the record declares that this square has no terrain.
/// <paramref name="Land"/> and <paramref name="LandUnreadableReason"/> are then null, and a caller treats the grid as
/// having no terrain at all, NOT as keeping an earlier plugin's heights. Engine evidence (manual lab playtest, 2026-09-27):
/// a later plugin's override of a LAND that had heights, with that flag cleared and VHGT and VNML dropped, draws no ground
/// over the square (a hole; the earlier heights are not kept and the square is not flat; placed objects float), and it has
/// no terrain collision: the player fell through the hole, through the low-detail terrain seen beneath it, into the water
/// below the land surface. The matched control (the same override records with the heights kept) showed normal ground on
/// that square, so the hole comes from the removed heightmap, not from the override itself. A LAND whose flag is set
/// but whose VHGT is missing or unreadable is not this: it is <paramref name="LandUnreadableReason"/>. Added 2026-09-27.</param>
public sealed record CellPlacements(
    FormKey Cell,
    string? CellEditorId,
    bool Interior,
    FormKey Worldspace,
    (int X, int Y)? Grid,
    IReadOnlyList<PlacedRef> Refs,
    IReadOnlyList<NavmeshGeometry> Navmeshes,
    float[,]? Land = null,
    IReadOnlyList<DeletedRef>? DeletedRefs = null,
    string? LandUnreadableReason = null,
    bool LandDeclaresNoHeights = false,
    IReadOnlyList<DeletedNavmesh>? DeletedNavmeshes = null)
{
    /// <summary>The partition key two references must SHARE before they can possibly touch: the same worldspace, or the
    /// same interior cell. Exterior refs in ADJACENT cells can overlap, so the grid is deliberately NOT part of this.</summary>
    public string SpaceKey => Interior ? $"int:{Cell}" : $"wrld:{Worldspace}";
}

public static class PlacementExtract
{
    // concrete getter type -> its Base property, or null when the arm has none. Mutagen hands back a small closed set
    // of concrete overlay types, so this saturates immediately.
    static readonly ConcurrentDictionary<Type, PropertyInfo?> BaseProp = new();

    /// <summary>Every cell in <paramref name="mod"/> that HAS placed content or navmeshes, with its references resolved
    /// to world transforms. Walks the groups EXPLICITLY (interior blocks, then worldspace blocks) rather than using
    /// EnumerateMajorRecords&lt;ICellGetter&gt;, because the worldspace a cell belongs to is STRUCTURAL — it exists only
    /// in the containment chain, and a flat enumeration loses it. Cells with nothing in them are skipped rather than
    /// emitted empty, unless <paramref name="includeEmpty"/>. A cell whose only content is positionless deletions
    /// (<see cref="CellPlacements.DeletedRefs"/>, <see cref="CellPlacements.DeletedNavmeshes"/>) is skipped too unless
    /// <paramref name="includeDeletions"/>, so a caller that does not ask for them sees exactly the cells it saw before they
    /// existed.</summary>
    public static IEnumerable<CellPlacements> FromMod(ISkyrimModGetter mod, bool includeEmpty = false, bool includeDeletions = false)
    {
        foreach (var block in mod.Cells.Records)
            foreach (var sub in block.SubBlocks)
                foreach (var cell in sub.Cells)
                {
                    var cp = FromCell(cell, FormKey.Null);
                    if (includeEmpty || cp.Refs.Count > 0 || cp.Navmeshes.Count > 0 || (includeDeletions && HasDeletions(cp))) yield return cp;
                }

        foreach (var wrld in mod.Worldspaces.Records)
        {
            if (wrld.TopCell is { } top)
            {
                var cp = FromCell(top, wrld.FormKey);
                if (includeEmpty || cp.Refs.Count > 0 || cp.Navmeshes.Count > 0 || (includeDeletions && HasDeletions(cp))) yield return cp;
            }
            foreach (var block in wrld.SubCells)
                foreach (var sub in block.Items)
                    foreach (var cell in sub.Items)
                    {
                        var cp = FromCell(cell, wrld.FormKey);
                        if (includeEmpty || cp.Refs.Count > 0 || cp.Navmeshes.Count > 0 || (includeDeletions && HasDeletions(cp))) yield return cp;
                    }
        }
    }

    static bool HasDeletions(CellPlacements cp) => cp.DeletedRefs!.Count > 0 || cp.DeletedNavmeshes!.Count > 0;

    /// <summary>One cell's placed content. <paramref name="worldspace"/> is the containing worldspace's key, or
    /// <see cref="FormKey.Null"/> for an interior — the caller supplies it because the cell record does not carry it.</summary>
    public static CellPlacements FromCell(ICellGetter cell, FormKey worldspace)
    {
        bool interior = cell.Flags.HasFlag(Cell.Flag.IsInteriorCell);
        (int X, int Y)? grid = (!interior && cell.Grid is { } g) ? (g.Point.X, g.Point.Y) : null;

        var refs = new List<PlacedRef>(cell.Persistent.Count + cell.Temporary.Count);
        var deleted = new List<DeletedRef>();
        foreach (var p in cell.Persistent)
        {
            if (ToRef(p, persistent: true) is { } r) refs.Add(r);
            else if (p.IsDeleted) deleted.Add(new DeletedRef(p.FormKey, RecordNaming.StripGetterInterface(InterfaceNameOf(p)), true));
        }
        foreach (var p in cell.Temporary)
        {
            if (ToRef(p, persistent: false) is { } r) refs.Add(r);
            else if (p.IsDeleted) deleted.Add(new DeletedRef(p.FormKey, RecordNaming.StripGetterInterface(InterfaceNameOf(p)), false));
        }

        var navs = new List<NavmeshGeometry>(cell.NavigationMeshes.Count);
        var deletedNavs = new List<DeletedNavmesh>();
        foreach (var nav in cell.NavigationMeshes)
        {
            if (ToNav(nav) is { } n) navs.Add(n);
            else deletedNavs.Add(new DeletedNavmesh(nav.FormKey, nav.IsDeleted));
        }

        // The terrain, decoded once here so every consumer reads one height. A LAND that says it has heights and gives
        // none is named (LandUnreadableReason), never read as "no LAND": a caller that took it as absent would keep an
        // earlier plugin's heights. A LAND that does not say so and has no VHGT declares no terrain (LandDeclaresNoHeights).
        float[,]? land = null;
        string? landWhy = null;
        bool landDeclaresNone = false;
        if (!interior)
        {
            ILandscapeGetter? ls = null;
            try { ls = cell.Landscape; } catch (Exception ex) { landWhy = "LAND unreadable: " + ex.GetType().Name; }
            if (ls is not null)
            {
                var lc = LandHeight.Classify(ls);   // the one rule (viewprobe's terrain walk calls it too)
                switch (lc.Kind)
                {
                    case LandHeight.LandKind.Heights: land = LandHeight.Decode(lc.Offset, lc.Gradients!); break;
                    case LandHeight.LandKind.DeclaresNone: landDeclaresNone = true; break;
                    default: landWhy = lc.Reason; break;
                }
            }
        }

        return new CellPlacements(cell.FormKey, cell.EditorID, interior,
                                  interior ? FormKey.Null : worldspace, grid, refs, navs, land, deleted, landWhy, landDeclaresNone, deletedNavs);
    }

    /// <summary>One placed record read on its own, outside a cell walk: its world transform, or null when it carries no
    /// placement data. <paramref name="persistent"/> is the caller's knowledge of which list held it.</summary>
    public static PlacedRef? RefOf(IPlacedGetter placed, bool persistent = false) => ToRef(placed, persistent);

    /// <summary>One placed record -> its world transform. Null when the record carries NO placement data at all
    /// (a DELETED reference typically strips it) — such a record has no position, so there is nothing spatial to say
    /// about it, and inventing (0,0,0) would put phantom clutter at the worldspace origin.</summary>
    static PlacedRef? ToRef(IPlacedGetter placed, bool persistent)
    {
        if (placed.Placement is not { } pl) return null;

        var flags = placed is ISkyrimMajorRecordGetter smr ? smr.SkyrimMajorRecordFlags : default;

        // XESP and XAPR live on IPlacedGetter itself (all ten arms), so no reflection is needed for them.
        FormKey? enableParent = null;
        bool enableOpposite = false;
        if (placed.EnableParent is { } ep && !ep.Reference.FormKey.IsNull)
        {
            enableParent = ep.Reference.FormKey;
            enableOpposite = ep.Flags.HasFlag(EnableParent.Flag.SetEnableStateToOppositeOfParent);
        }
        List<PlacedActivateParent>? activateParents = null;
        if (placed.ActivateParents is { } aps)
            foreach (var ap in aps.Parents)
                if (!ap.Reference.FormKey.IsNull)
                    (activateParents ??= new List<PlacedActivateParent>(aps.Parents.Count)).Add(new PlacedActivateParent(ap.Reference.FormKey, ap.Delay));

        return new PlacedRef(
            placed.FormKey,
            RecordNaming.StripGetterInterface(InterfaceNameOf(placed)),
            placed.EditorID,
            Vec3.From(pl.Position),
            Vec3.From(pl.Rotation),
            placed.Scale ?? 1.0f,
            ReadBase(placed),
            persistent,
            flags.HasFlag(SkyrimMajorRecord.SkyrimMajorRecordFlag.InitiallyDisabled),
            placed.IsDeleted,
            enableParent,
            enableOpposite,
            ReadLinkedReferences(placed),
            activateParents,
            ReadPrimitive(placed),
            ReadEncounterZone(placed),
            ReadCollisionLayerIndex(placed),
            placed is IPlacedObjectGetter po && po.IsOpenByDefault,
            unchecked((uint)placed.MajorRecordFlagsRaw),
            placed is IPlacedObjectGetter pv && pv.VirtualMachineAdapter is { } vm && vm.Scripts.Count > 0 ? vm.Scripts.Select(x => x.Name).ToList() : null,
            ReadArmBase(placed, out var armLink),
            armLink);
    }

    // concrete getter type -> its CollisionLayer property. Declared on the PlacedObject arm, read like Base.
    static readonly ConcurrentDictionary<Type, PropertyInfo?> CollisionLayerProp = new();

    /// <summary>XTRI by reflection over the arm's CollisionLayer: Mutagen 0.54.4 declares it as a raw <c>uint?</c>, and
    /// the value is the layer's INDEX (a COLL record's Index field), which is what the caller matches. Null when the
    /// arm has none or the record omits it.</summary>
    static uint? ReadCollisionLayerIndex(IPlacedGetter placed)
    {
        var prop = CollisionLayerProp.GetOrAdd(placed.GetType(),
            static t => t.GetProperty("CollisionLayer", BindingFlags.Public | BindingFlags.Instance));
        if (prop is null) return null;
        return prop.GetValue(placed) is uint raw ? raw : null;
    }

    // concrete getter type -> its EncounterZone property. Declared on the PlacedObject arm, read like Base.
    static readonly ConcurrentDictionary<Type, PropertyInfo?> EncounterZoneProp = new();

    /// <summary>XEZN by reflection over the arm's EncounterZone link. Null when the arm has none or the link is unset.</summary>
    static FormKey? ReadEncounterZone(IPlacedGetter placed)
    {
        var prop = EncounterZoneProp.GetOrAdd(placed.GetType(),
            static t => t.GetProperty("EncounterZone", BindingFlags.Public | BindingFlags.Instance));
        if (prop is null || prop.GetValue(placed) is not IFormLinkGetter link) return null;
        var k = link.FormKeyNullable;
        return k is { IsNull: false } ? k : null;
    }

    // concrete getter type -> its Primitive property. Declared on the PlacedObject arm and not on IPlacedGetter, so
    // it is read the same way Base and LinkedReferences are.
    static readonly ConcurrentDictionary<Type, PropertyInfo?> PrimitiveProp = new();

    /// <summary>XPRM by reflection over the arm's Primitive. Null when the arm has none or the record omits it.</summary>
    static PlacedPrimitiveInfo? ReadPrimitive(IPlacedGetter placed)
    {
        var prop = PrimitiveProp.GetOrAdd(placed.GetType(),
            static t => t.GetProperty("Primitive", BindingFlags.Public | BindingFlags.Instance));
        if (prop is null || prop.GetValue(placed) is not IPlacedPrimitiveGetter pp) return null;
        return new PlacedPrimitiveInfo(pp.Type.ToString(), Vec3.From(pp.Bounds));
    }

    static NavmeshGeometry? ToNav(INavigationMeshGetter nav)
    {
        if (nav.Data is not { } d) return null;
        var verts = new List<Vec3>(d.Vertices.Count);
        foreach (var v in d.Vertices) verts.Add(Vec3.From(v));
        var tris = new List<NavTri>(d.Triangles.Count);
        foreach (var t in d.Triangles)
            tris.Add(new NavTri(t.Vertices.X, t.Vertices.Y, t.Vertices.Z, (int)t.Flags,
                                t.EdgeLink_0_1, t.EdgeLink_1_2, t.EdgeLink_2_0));
        // The CONNECTIVITY tables, carried alongside the shapes. The geometry alone cannot answer
        // "can an actor walk from here to there": two triangles that share an edge on paper are
        // neighbours only if the record SAYS they are, and two in different meshes are joined by
        // the edge-link table or by a door and by nothing else. Extracting the shapes and dropping
        // the links produced a structure that looked complete and could not be walked.
        var links = new List<NavEdgeLink>(d.EdgeLinks.Count);
        // el.Unknown is xEdit's edge-link Type (Mutagen lags xEdit on the name; see NavEdgeLink.Type).
        foreach (var el in d.EdgeLinks) links.Add(new NavEdgeLink(el.Mesh.FormKey, el.TriangleIndex, el.Unknown));
        var doors = new List<NavDoorTriangle>(d.DoorTriangles.Count);
        foreach (var dt in d.DoorTriangles) doors.Add(new NavDoorTriangle(dt.Door.FormKey, dt.TriangleBeforeDoor));
        return new NavmeshGeometry(nav.FormKey, verts, tris, Vec3.From(d.Min), Vec3.From(d.Max), links, doors, nav.IsDeleted);
    }

    // concrete getter type -> its LinkedReferences property. Same shape as BaseProp, same reason: the XLKR list is
    // declared on each arm's own interface (IPlacedObjectGetter, IPlacedNpcGetter, ...) and not on IPlacedGetter, and
    // hand-wiring the arms is what CONTRIBUTING forbids.
    static readonly ConcurrentDictionary<Type, PropertyInfo?> LinksProp = new();

    /// <summary>XLKR by reflection over the arm's LinkedReferences list. Null when the arm has no such list or it is empty.</summary>
    static IReadOnlyList<PlacedLink>? ReadLinkedReferences(IPlacedGetter placed)
    {
        var prop = LinksProp.GetOrAdd(placed.GetType(),
            static t => t.GetProperty("LinkedReferences", BindingFlags.Public | BindingFlags.Instance));
        if (prop is null || prop.GetValue(placed) is not IEnumerable<ILinkedReferencesGetter> links) return null;
        List<PlacedLink>? result = null;
        foreach (var l in links)
        {
            if (l.Reference.FormKey.IsNull) continue;
            var kw = l.KeywordOrReference.FormKeyNullable;
            (result ??= new List<PlacedLink>()).Add(new PlacedLink(l.Reference.FormKey, kw is { IsNull: false } k ? k : null));
        }
        return result;
    }

    /// <summary>The record's link through a property named Base, by REFLECTION — the field is declared per-arm with a different
    /// link target, so there is no shared interface to read it from, and hand-wiring the arms is exactly what CONTRIBUTING
    /// forbids. Null on the trap arms (their link has another name: <see cref="ReadArmBase"/>) and for an unset or null link.</summary>
    static FormKey? ReadBase(IPlacedGetter placed)
    {
        var prop = BaseProp.GetOrAdd(placed.GetType(),
            static t => t.GetProperty("Base", BindingFlags.Public | BindingFlags.Instance));
        if (prop is null) return null;
        return prop.GetValue(placed) is IFormLinkGetter link ? NonNull(link.FormKeyNullable) : null;
    }

    // A null key is no base: Mutagen's non-nullable links (Hazard, Projectile) read FormKey.Null when unset.
    static FormKey? NonNull(FormKey? k) => k is { IsNull: false } ? k : null;

    /// <summary>The names Mutagen gives a placed arm's base link (xEdit NAME), tried in order. A placed type that matches none,
    /// or more than one, fails the C1754 guard rather than reading as "no base".</summary>
    public static readonly IReadOnlyList<string> ArmBaseLinkNames = new[] { "Base", "Hazard", "Projectile" };

    // concrete getter type -> its base-link property (the first of ArmBaseLinkNames it carries as a form link), or null.
    static readonly ConcurrentDictionary<Type, PropertyInfo?> ArmBaseProp = new();

    static PropertyInfo? ArmBasePropOf(Type t) => ArmBaseProp.GetOrAdd(t, static t =>
    {
        foreach (var n in ArmBaseLinkNames)
            if (t.GetProperty(n, BindingFlags.Public | BindingFlags.Instance) is { } p && typeof(IFormLinkGetter).IsAssignableFrom(p.PropertyType))
                return p;
        return null;
    });

    /// <summary>The property a placed type's base link is read from, or null when it carries none of
    /// <see cref="ArmBaseLinkNames"/>.</summary>
    public static string? ArmBaseLinkOf(Type placedType) => ArmBasePropOf(placedType)?.Name;

    /// <summary>A placed record's base, whatever its arm names the link; null when it has none or the link is unset.</summary>
    public static FormKey? BaseOf(IPlacedGetter placed) => ReadArmBase(placed, out _);

    /// <summary>The arm's own base link, whatever Mutagen names it. Null when the type has none or the link is unset or null.</summary>
    static FormKey? ReadArmBase(IPlacedGetter placed, out string? linkName)
    {
        var prop = ArmBasePropOf(placed.GetType());
        linkName = prop?.Name;
        if (prop is null) return null;
        return prop.GetValue(placed) is IFormLinkGetter link ? NonNull(link.FormKeyNullable) : null;
    }

    /// <summary>The GETTER-interface name of a concrete overlay type ("PlacedObjectBinaryOverlay" -> "IPlacedObjectGetter"),
    /// so <see cref="RecordNaming.StripGetterInterface"/> yields the catalog name the rest of houseCARL uses. Picked from
    /// the interface list rather than the class name, because the concrete type is "…BinaryOverlay" on a read path and
    /// plain on a write path, and only the interface is stable across both.</summary>
    static string InterfaceNameOf(IPlacedGetter placed)
    {
        foreach (var i in placed.GetType().GetInterfaces())
        {
            var n = i.Name;
            if (n.StartsWith("IPlaced", StringComparison.Ordinal)
                && n.EndsWith("Getter", StringComparison.Ordinal)
                && n != "IPlacedGetter" && n != "IPlacedSimpleGetter" && n != "IPlacedThingGetter")
                return n;
        }
        return placed.GetType().Name;
    }
}
