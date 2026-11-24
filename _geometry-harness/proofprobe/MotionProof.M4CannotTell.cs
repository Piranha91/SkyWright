using System.Numerics;
using HousecarlCore;
using SkyWright.Engine;
using NiflySharp.Enums;

// Usage-week M4 (2026-10-14): G1's last cannot-tell clusters. Plan: _claude/runs/2026-10-14-g1-cannot-tell/GATE-A.md, reviewed in
// sessions/2026-10-14-g1-cannot-tell-gate-a-review.md (Q1: the EditorMarker rule applies by default, one shared predicate).
// Cases 4000-4008: a placed actor with no model is tested by its humanoid body in box contact (g1-0040's 0A78FB).
// Cases 4012-4026: a shape named EditorMarker is not drawn, so it is never part of a contact (g1-0009's rack triggers).
static partial class MotionProof
{
    static BodyExtent M4Body() => new(ActorBand.PhysicalStep, ActorBand.PhysicalControllerTop(ActorBand.ScaleOneHeight), RouteConnectivity.DefaultCapsuleRadius);
    static Aabb M4Box(float x0, float y0, float z0, float x1, float y1, float z1) => new(new Vec3(x0, y0, z0), new Vec3(x1, y1, z1), false);
    static readonly Vec3 Z3 = new(0, 0, 0);

    // A 20 u opaque cube at the origin (the partner) and a mesh of named shapes, each a 10 u cube, alpha-blended when asked.
    static byte[] M4Mesh(params (string Name, float Cx, bool Alpha)[] shapes) =>
        ChainNif(null, shapes.Select(s => (s.Name, "N1", Cube(s.Cx, 0, 0, 5), s.Alpha)).ToArray(), Array.Empty<(string, FxKeys[], CycleType)>());

    static (double Crossing, string Status, string Detail) M4Cross(byte[] nif)
    {
        var s = MeshSurface.ModelSurface(nif, VolumeGeometry.Render);
        if (s.Status != "measured") return (double.NaN, s.Status, s.Reason ?? "");
        var r = MeshSurface.MeasurePlaced(s.Soup!, s.Tags, Z3, Z3, 1f, Cube(0, 0, 0, 10), null, Z3, Z3, 1f, VolumeGeometry.Render);
        double c = r.CrossingLength?.Lower ?? double.NaN;
        return (c, r.Status, $"{r.Status} {r.Reason} crossing {c:0.###} by tags {string.Join(", ", (r.CrossingLengthByTags ?? new Dictionary<string, double>()).Select(kv => $"{kv.Key}={kv.Value:0.###}"))}");
    }

    public static void RunM4CannotTell(Action<string, bool, string> check)
    {
        var body = M4Body();
        float top = body.Top, r = body.Radius;

        // ================= g1-0040: box contact against a model-less actor's body =================
        // 4000: a box over the body's middle meets it. Rejects today's refusal and a body that never meets (the stub).
        Case(check, "m4 4000: a box over a model-less actor's body middle meets it", () =>
        {
            bool m = AnchorBodyBox.Meets(Vector3.Zero, body, M4Box(-5, -5, top / 2 - 5, 5, 5, top / 2 + 5));
            return (m, $"meets {m}; body {body}");
        });
        // 4002: a box 1 u above the body top does not meet it. Rejects a vertical line or an unbounded body above the actor.
        Case(check, "m4 4002: a box 1 u above the body top - clear", () =>
        {
            bool m = AnchorBodyBox.Meets(Vector3.Zero, body, M4Box(-5, -5, top + 1, 5, 5, top + 20));
            return (!m, $"meets {m}; top {top}");
        });
        // 4004: the capsule's radius decides sideways: a box 1 u beyond the radius at mid height is clear, 1 u inside it meets.
        // Rejects a point or line test and a box-shaped body (the corner case below).
        Case(check, "m4 4004: radius decides - 1 u outside clear, 1 u inside meets, a box at the bottom corner diagonal clear", () =>
        {
            float mid = (body.Bottom + body.Top) / 2;   // the body spans Bottom..Top above the anchor (corrected at Gate C: was top / 2, below the axis)
            bool outside = AnchorBodyBox.Meets(Vector3.Zero, body, M4Box(r + 1, -5, mid - 5, r + 20, 5, mid + 5));
            bool inside = AnchorBodyBox.Meets(Vector3.Zero, body, M4Box(r - 1, -5, mid - 5, r + 20, 5, mid + 5));
            // beside the axis at the capsule's lower end, diagonally 0.8 r out on each of x and y (distance 1.13 r): clear for a
            // capsule, met by a box-shaped body
            float d = 0.8f * r, zb = body.Bottom + r;
            bool corner = AnchorBodyBox.Meets(Vector3.Zero, body, M4Box(d, d, zb - 2, d + 10, d + 10, zb + 2));
            return (!outside && inside && !corner, $"outside {outside} inside {inside} corner {corner}");
        });
        // 4006: only a placed actor without a model gets the body. Rejects a body for markers and activators.
        Case(check, "m4 4006: the body applies to a model-less PlacedNpc only", () =>
        {
            bool npc = AnchorBodyBox.Applies("PlacedNpc", hasModelPath: false), obj = AnchorBodyBox.Applies("PlacedObject", hasModelPath: false);
            return (npc && !obj, $"PlacedNpc {npc} PlacedObject {obj}");
        });
        // 4008: a member with a model keeps its render surface. Rejects the body branch leaking into modelled members.
        Case(check, "m4 4008: a PlacedNpc with a model path keeps its render surface (no body)", () =>
        {
            bool b = AnchorBodyBox.Applies("PlacedNpc", hasModelPath: true);
            return (!b, $"applies {b}");
        });

        // ================= g1-0009: EditorMarker shapes are not drawn =================
        // 4012: a mesh whose only shape is an alpha-blended EditorMarker crosses the partner's face: no crossing, and the pair is
        // measured, not refused. Rejects today's crossing (alpha_blend x opaque) and a refusal for an empty member.
        Case(check, "m4 4012: EditorMarker-only mesh crossing a cube - measured, no crossing", () =>
        {
            var (c, st, det) = M4Cross(M4Mesh(("EditorMarker", 10, true)));
            return (st == "measured" && c == 0, det);
        });
        // 4014: the same shape under another name keeps its crossing. Rejects a broad "untextured or alpha = not drawn" rule.
        Case(check, "m4 4014: the same alpha shape named TriggerVol keeps its crossing", () =>
        {
            var (c, st, det) = M4Cross(M4Mesh(("TriggerVol", 10, true)));
            return (st == "measured" && c > 0, det);
        });
        // 4016: a drawn opaque shape plus an EditorMarker shape, both crossing: the crossing is the drawn shape's alone. Rejects the
        // marker zeroing the whole member and the marker's crossing being counted.
        Case(check, "m4 4016: Frame + EditorMarker - crossing equals the Frame-only mesh's", () =>
        {
            var both = M4Cross(M4Mesh(("Frame", -10, false), ("EditorMarker", 10, true)));
            var frame = M4Cross(M4Mesh(("Frame", -10, false)));
            return (both.Status == "measured" && frame.Crossing > 0 && Math.Abs(both.Crossing - frame.Crossing) < 1e-6,
                    $"both: {both.Detail} | frame only: {frame.Detail}");
        });
        // 4018: the name compares ignoring case, as the light reader's rule does. Rejects a case-sensitive copy of the predicate.
        Case(check, "m4 4018: a shape named 'editormarker' is not drawn either", () =>
        {
            var (c, st, det) = M4Cross(M4Mesh(("editormarker", 10, true)));
            return (st == "measured" && c == 0, det);
        });
        // 4020: the surface keeps every triangle, index-aligned with the stored render soup (the alpha reader's texcoords depend
        // on it); the marker's triangles carry the not_drawn tag. Rejects dropping triangles (alignment) and an untagged marker.
        Case(check, "m4 4020: marker triangles kept in the soup, tagged not_drawn; the Frame's stay opaque", () =>
        {
            var s = MeshSurface.ModelSurface(M4Mesh(("Frame", -10, false), ("EditorMarker", 10, true)), VolumeGeometry.Render);
            var tags = s.Tags ?? Array.Empty<string>();
            int nd = tags.Count(t => t == SurfaceMaterial.NotDrawn), op = tags.Count(t => t == SurfaceMaterial.Opaque);
            var tc = SurfaceTexcoords.Read(M4Mesh(("Frame", -10, false), ("EditorMarker", 10, true)), s.Soup!);
            bool aligned = !(tc.Reason ?? "").Contains("alignment");
            return (s.Status == "measured" && s.Soup?.TriangleCount == 24 && nd == 12 && op == 12 && aligned,
                    $"{s.Status} tris {s.Soup?.TriangleCount} not_drawn {nd} opaque {op} texcoords {tc.Status} {tc.Reason}");
        });
        // 4022: box contact ignores not-drawn triangles: a box straddling only the marker's face at x = 15 meets nothing. Rejects anyMeets
        // on a marker (the first version sat inside the marker cube touching no face, and passed on the base: corrected at Gate B).
        Case(check, "m4 4022: a box touching only the EditorMarker shape - neither opaque nor any", () =>
        {
            var s = MeshSurface.ModelSurface(M4Mesh(("Frame", -10, false), ("EditorMarker", 10, true)), VolumeGeometry.Render);
            var b = MeshSurfaceBox.BoxContact(s.Soup!, s.Tags, M4Box(13, -2, -2, 17, 2, 2));
            return (!b.OpaqueMeets && !b.AnyMeets && b.Reason is null, $"{b}");
        });
        // 4024: an actor standing inside a marker-only mesh is clear. Rejects the base's "meets: the body lies inside a closed component".
        Case(check, "m4 4024: an anchor body inside an EditorMarker-only mesh - clear", () =>
        {
            var nif = ChainNif(null, new[] { ("EditorMarker", "N1", Box(-50, -50, 0, 50, 50, 200), true) }, Array.Empty<(string, FxKeys[], CycleType)>());
            var s = MeshSurface.ModelSurface(nif, VolumeGeometry.Render);
            if (s.Status != "measured") return (false, $"{s.Status} {s.Reason}");
            var a = MeshAnchor.TestPlaced(s.Soup!, s.Tags, Z3, Z3, 1f, Vector3.Zero, body);
            return (a.Decision == "clear", $"{a.Decision} {a.Reason}");
        });
        // 4026: one predicate - the light reader's rule and the shared one agree on every name in the set, and the shared one says an
        // EditorMarker is not drawn. Rejects two diverging copies.
        Case(check, "m4 4026: ShapeDrawing.Drawn and LightReach.ShapeDrawn agree; EditorMarker not drawn", () =>
        {
            string[] names = { "EditorMarker", "editormarker", "EDITORMARKER", "EditorMarker01", "Frame", "TriggerVol", "" };
            var bad = names.Where(n => ShapeDrawing.Drawn(n, false, false, false) != LightReach.ShapeDrawn(n, false, false, false)).ToList();
            bool marker = !ShapeDrawing.Drawn("EditorMarker", false, false, false) && ShapeDrawing.IsEditorMarker("EditorMarker");
            return (bad.Count == 0 && marker, $"disagree on [{string.Join(", ", bad)}]; marker not drawn {marker}");
        });
    }
}
