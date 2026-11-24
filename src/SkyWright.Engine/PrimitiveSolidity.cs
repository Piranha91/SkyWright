using System.Numerics;
using System.Text.Json;

using HousecarlCore;

namespace SkyWright.Engine;

// PRIMITIVE SOLIDITY (reach rule `primitive`, Part 3b). Whether a model-less reference's primitive (XPRM) is a body, and what it
// stops, is decided by the reference's BASE (Part 3 station T: a model-less activator box stating L_STATIC was not solid, a
// CollisionMarker box stating L_STATIC was) and its stated collision layer (XTRI; COLL says what a layer collides with, not
// whether a body exists). Nothing here is inferred: every verdict comes from a measurement entry (base or base type, stated
// layer or "none") written from an in-game station. A base/layer pair with no entry keeps the caller's refusal, by name.
//
// STATUS: implemented (red at fb6de1ba).

/// <summary>One measured (base, layer) verdict. <c>Kind</c>: <c>base</c> (Value = the base FormKey, e.g. "000017:Skyrim.esm") or
/// <c>baseType</c> (Value = the base record type, e.g. "AcousticSpace"). <c>Layer</c>: the stated XTRI layer's editor id, or
/// <c>none</c>. <c>Verdict</c>: <c>solid</c> (the box is geometry; <c>Stops</c> names what it was measured to stop) or
/// <c>not_solid</c>.</summary>
public sealed record PrimitiveMeasurement(string Kind, string Value, string Layer, string Verdict, IReadOnlyList<string> Stops, string Evidence);

/// <summary>A reference's primitive as the rule reads it: base FormKey and record type, stated layer editor id (null = none
/// stated), the primitive's shape (XPRM type: Box, Sphere, PortalBox), the box's half extents (XPRM bounds / 2) and the reference placement (radians).</summary>
public sealed record PrimitiveFacts(string? BaseKey, string? BaseType, string? LayerName, string Shape, Vector3 HalfExtents, Vec3 Position, Vec3 Rotation);

public static class PrimitiveSolidity
{
    /// <summary>Read a measurement list (JSON array of PrimitiveMeasurement, camelCase). An unknown kind or verdict, or an entry
    /// without evidence, refuses the whole list with an ArgumentException naming it.</summary>
    public static IReadOnlyList<PrimitiveMeasurement> Read(string json)
    {
        var list = JsonSerializer.Deserialize<List<PrimitiveMeasurement>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new ArgumentException("primitive measurement list: empty document");
        foreach (var m in list)
        {
            if (m.Kind is not ("base" or "baseType")) throw new ArgumentException($"primitive measurement list: unknown kind '{m.Kind}'");
            if (m.Verdict is not ("solid" or "not_solid")) throw new ArgumentException($"primitive measurement list: unknown verdict '{m.Verdict}'");
            if (string.IsNullOrWhiteSpace(m.Value) || string.IsNullOrWhiteSpace(m.Layer)) throw new ArgumentException("primitive measurement list: an entry without value or layer");
            if (string.IsNullOrWhiteSpace(m.Evidence)) throw new ArgumentException($"primitive measurement list: entry {m.Kind} {m.Value} {m.Layer} has no evidence");
            if (m.Verdict == "solid" && (m.Stops is null || m.Stops.Count == 0)) throw new ArgumentException($"primitive measurement list: solid entry {m.Value} {m.Layer} names nothing it stops");
        }
        return list;
    }

    /// <summary>The shapes station Q measured (both per base, and they agreed); any other shape is outside the table.</summary>
    public static readonly IReadOnlySet<string> MeasuredShapes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Box", "PortalBox" };

    /// <summary>The verdict for one reference: a primitive shape other than Box or PortalBox → refused with
    /// <paramref name="refusal"/> plus the shape named; then a <c>base</c> entry for its base and layer wins over a <c>baseType</c> entry; solid
    /// → certified, the exact world box, reason "primitive box (...): stops {stops} (measured: {evidence})" (labels stay empty); not_solid → no_geometry,
    /// reason naming base, layer and evidence; no entry → refused with <paramref name="refusal"/> plus "; no measurement for this
    /// base and layer".</summary>
    public static ReachRegion Decide(PrimitiveFacts facts, IReadOnlyList<PrimitiveMeasurement> table, string refusal)
    {
        string layer = facts.LayerName ?? "none";
        bool LayerIs(PrimitiveMeasurement m) => string.Equals(m.Layer, layer, StringComparison.OrdinalIgnoreCase);
        var hit = table.FirstOrDefault(m => m.Kind == "base" && facts.BaseKey is not null && string.Equals(m.Value, facts.BaseKey, StringComparison.OrdinalIgnoreCase) && LayerIs(m))
               ?? table.FirstOrDefault(m => m.Kind == "baseType" && facts.BaseType is not null && string.Equals(m.Value, facts.BaseType, StringComparison.OrdinalIgnoreCase) && LayerIs(m));
        string who = $"{facts.BaseType ?? "?"} {facts.BaseKey ?? "?"}, layer {layer}";
        if (!MeasuredShapes.Contains(facts.Shape))
            return new("refused", refusal + $"; primitive shape {facts.Shape} was not measured (station Q measured Box and PortalBox)", Aabb.Empty, MeshReach.PolicyVersion, Array.Empty<string>(), Array.Empty<ReachPart>());
        if (hit is null)
            return new("refused", refusal + "; no measurement for this base and layer", Aabb.Empty, MeshReach.PolicyVersion, Array.Empty<string>(), Array.Empty<ReachPart>());
        if (hit.Verdict == "not_solid")
            return new("no_geometry", $"primitive ({who}): not solid (measured: {hit.Evidence})", Aabb.Empty, MeshReach.PolicyVersion, Array.Empty<string>(), Array.Empty<ReachPart>());
        var h = facts.HalfExtents;
        var model = new Aabb(new Vec3(-h.X, -h.Y, -h.Z), new Vec3(h.X, h.Y, h.Z), false);
        // certified: the extent is exact from the record; the label says what the body was measured to stop
        // (Labels stay empty on a certified region by the record's contract; the reason carries the basis)
        return new("certified", $"primitive box ({who}): stops {string.Join(", ", hit.Stops)} (measured: {hit.Evidence})",
                   MeshReach.BoxWorld(model, facts.Position, facts.Rotation, 1f), MeshReach.PolicyVersion, Array.Empty<string>(), Array.Empty<ReachPart>());
    }
}
