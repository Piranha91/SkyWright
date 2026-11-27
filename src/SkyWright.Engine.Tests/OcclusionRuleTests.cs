using System.Numerics;
using System.Text.Json;
using Xunit;

namespace SkyWright.Engine.Tests;

/// <summary>The occlusion rule in the Engine (<see cref="OcclusionRule"/>, <see cref="OcclusionPrimitiveGeometry"/>):
/// viewprobe's orientation cases 6000-6014 (OcclusionRuleSelfTest), planeassess's rule shapes R1-R9
/// (planeassess.geometry_selftest, its eye and panels with half extents doubled), the rule file's loader and report
/// serialization as captured before the move, and the primitive's corners, quad and axes.</summary>
[Trait("tier", "unit")]
public sealed class OcclusionRuleTests
{
    const string A10 = """
    {
     "identity": "selftest-a10-shape",
     "minimumEyeDistance": 42,
     "maximumAbsNormalZ": 0.001,
     "frozen": false,
     "boxSilhouette": true,
     "evidence": ["selftest"],
     "horizontalPortalBoxNormalDownEyeAbove": true
    }
    """;
    const string A12 = """
    {
     "identity": "selftest-a12-shape",
     "minimumEyeDistance": 42,
     "maximumAbsNormalZ": 0.001,
     "uprightMaximumAbsNormalZ": 0.021,
     "frozen": false,
     "boxSilhouette": true,
     "evidence": ["selftest"],
     "horizontalPortalBoxNormalDownEyeAbove": true
    }
    """;
    // Captured from viewprobe's build before the upright field existed (OcclusionRuleSelfTest.OldConfigurationJson).
    const string A10Json =
        "{\n  \"Identity\": \"selftest-a10-shape\",\n  \"MinimumEyeDistance\": 42,\n  \"MaximumAbsNormalZ\": 0.001,\n  \"Frozen\": false,\n  \"Evidence\": [\n    \"selftest\"\n  ],\n  \"BoxSilhouette\": true,\n  \"HorizontalPortalBoxNormalDownEyeAbove\": true\n}";
    const string A10Scope =
        "Research predictions for rigid opaque shapes and finite PortalBox rectangles; no cell-wide clearance or compatibility attribution. Upright Box primitives beyond the rule's minimum eye distance are predicted by the silhouette of the stored box from the eye together with the box's own volume; a union of per-face shadows is NOT separated from that silhouette by the supporting evidence and remains untested. Horizontal PortalBox rectangles whose authored normal points down (-Z), seen from above their plane, are predicted by the same rectangle shadow; the normal-up form, an eye below the plane and every other tilt remain refused.";
    const string Sealed = "plane orientation outside the supplied rule's evidence scope";
    const string BoxText = "box orientation outside the supplied rule's evidence scope; only upright boxes were measured";
    const string UpText = "horizontal PortalBox with its normal up (+Z): outside the supplied rule's evidence scope; only normal-down (-Z) was opted in";
    const string BelowText = "horizontal PortalBox seen from below its plane: outside the supplied rule's evidence scope; only eye-above was opted in";
    const string NearEyeText = "near-eye behavior unassessed; observations bracket an exemption and do not establish a sharp threshold";

    static OcclusionRule Rule(string json) => OcclusionRule.Load(System.Text.Encoding.UTF8.GetBytes(json)).Rule;

    static OcclusionPrimitive P(OcclusionPrimitiveKind kind, Vector3 rot, Vector3 pos, Vector3 bounds, float scale = 1f) =>
        new(kind, bounds, pos, new Vec3(rot.X, rot.Y, rot.Z), scale);

    static OcclusionPrimitive TiltedPortal(float t, float sign = 1f) =>
        P(OcclusionPrimitiveKind.PortalBox, new Vector3(sign * MathF.Asin(t), 0f, 0f), new Vector3(0f, 1000f, 0f), new Vector3(400f, 2f, 300f));

    static OcclusionPrimitive TiltedBox(float t) =>
        P(OcclusionPrimitiveKind.Box, new Vector3(0f, MathF.Asin(t), 0f), new Vector3(0f, 1000f, 0f), new Vector3(300f, 300f, 300f));

    static OcclusionPrimitive Horizontal(bool down, float deviation, Vector3? pos = null)
    {
        foreach (var x in new[] { MathF.PI / 2f, 3f * MathF.PI / 2f })
        {
            var p = P(OcclusionPrimitiveKind.PortalBox, new Vector3(x + MathF.Asin(deviation), 0f, 0f), pos ?? new Vector3(0f, 1000f, -200f),
                      new Vector3(400f, 2f, 300f));
            if ((OcclusionPrimitiveGeometry.Axis(p, Vector3.UnitY).Z < 0f) == down) return p;
        }
        // Neither rotation's normal points the asked way (a broken Axis): hand back the first, so the case that asked
        // fails on its own assertion about the rule's answer rather than here.
        return P(OcclusionPrimitiveKind.PortalBox, new Vector3(MathF.PI / 2f + MathF.Asin(deviation), 0f, 0f), pos ?? new Vector3(0f, 1000f, -200f),
                 new Vector3(400f, 2f, 300f));
    }

    static string? Issue(OcclusionRule r, OcclusionPrimitive p, Vector3? eye = null) => r.Shadow(p, eye ?? Vector3.Zero).Issue;

    // ---------------------------------------------------------------- 6000-6014 (viewprobe's cases, moved)
    [Fact]
    public void Old_rule_refuses_small_tilts_with_the_sealed_texts_and_predicts_upright()
    {
        var r = Rule(A10);
        Assert.Equal(Sealed, Issue(r, TiltedPortal(0.0205f)));
        Assert.Equal(BoxText, Issue(r, TiltedBox(0.0174f)));
        Assert.Null(Issue(r, TiltedPortal(0f)));
        Assert.Null(Issue(r, TiltedBox(0f)));
    }

    [Fact]
    public void Old_rule_serializes_and_scopes_as_before_the_upright_field()
    {
        var r = Rule(A10);
        var json = JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n");
        Assert.Equal(A10Json, json);
        Assert.Equal(A10Scope, OcclusionRule.ScopeOf(r));
    }

    [Fact]
    public void New_rule_admits_tilts_within_its_upright_tolerance()
    {
        var r = Rule(A12);
        foreach (var t in new[] { 0.00373f, 0.00608f, 0.01896f, 0.0205f })
            foreach (var s in new[] { 1f, -1f })
                Assert.Null(Issue(r, TiltedPortal(t, s)));
        Assert.Equal(Sealed, Issue(r, TiltedPortal(0.0215f)));
        Assert.Null(Issue(r, TiltedBox(0.01741f)));
        Assert.Equal(BoxText, Issue(r, TiltedBox(0.0215f)));
    }

    [Fact]
    public void New_rule_keeps_the_horizontal_class_on_its_own_tolerance()
    {
        var r = Rule(A12);
        var above = new Vector3(0f, 800f, 400f);
        var below = new Vector3(0f, 800f, -800f);
        Assert.Null(Issue(r, Horizontal(true, 0f), above));
        Assert.Equal(Sealed, Issue(r, Horizontal(true, 0.00768f), above));
        Assert.Equal(Sealed, Issue(r, Horizontal(true, 0.0245f), above));
        Assert.Equal(UpText, Issue(r, Horizontal(false, 0f), above));
        Assert.Equal(BelowText, Issue(r, Horizontal(true, 0f), below));
    }

    [Fact]
    public void New_rule_serializes_and_scopes_its_upright_field()
    {
        var r = Rule(A12);
        Assert.Contains("\"UprightMaximumAbsNormalZ\": 0.021", JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Contains("within |n.z| <= 0.021 of upright", OcclusionRule.ScopeOf(r));
        Assert.Equal(OcclusionRule.RectangleScope, OcclusionRule.ScopeOf(null));
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("1.5")]
    [InlineData("1e9")]
    public void Load_refuses_an_out_of_range_upright_tolerance_with_the_existing_sentence(string bad)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(A12.Replace("\"uprightMaximumAbsNormalZ\": 0.021", "\"uprightMaximumAbsNormalZ\": " + bad));
        var ex = Assert.Throws<ArgumentException>(() => OcclusionRule.Load(bytes));
        Assert.Equal("Occlusion rule requires an identity, finite camera/orientation limits and evidence references.", ex.Message);
    }

    [Fact]
    public void Load_names_the_file_bytes_by_their_upper_case_sha256()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(A10);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), OcclusionRule.Load(bytes).Sha256);
    }

    // ---------------------------------------------------------------- R1-R9 (planeassess's rule shapes)
    static readonly Vector3 PaEye = new(0f, 0f, 120.5f);
    static OcclusionPrimitive Pa(Vector3 pos, Vector3 half, Vector3 rot = default, OcclusionPrimitiveKind kind = OcclusionPrimitiveKind.PortalBox)
        => P(kind, rot, pos, half * 2f);

    [Fact]
    public void R1_to_R9_planeassess_rule_shapes()
    {
        var r = Rule(A10);
        Assert.Null(Issue(r, Pa(new(0, 1000, 120), new(300, 1, 200)), PaEye));                                          // R1
        Assert.Equal(Sealed, Issue(r, Pa(new(0, 1000, 120), new(300, 1, 200), new(MathF.PI / 6f, 0, 0)), PaEye));      // R2
        Assert.Null(Issue(r, Horizontal(true, 0f, new Vector3(0, 1000, 0)), PaEye));                                     // R3
        Assert.Equal(UpText, Issue(r, Horizontal(false, 0f, new Vector3(0, 1000, 0)), PaEye));                           // R4
        Assert.Equal(BelowText, Issue(r, Horizontal(true, 0f, new Vector3(0, 1000, 500)), PaEye));                       // R5
        Assert.Null(Issue(r, Pa(new(0, 1000, 120), new(300, 300, 300), kind: OcclusionPrimitiveKind.Box), PaEye));      // R6
        Assert.Equal(BoxText, Issue(r, Pa(new(0, 1000, 120), new(300, 300, 300), new(0, MathF.PI / 18f, 0), OcclusionPrimitiveKind.Box), PaEye)); // R7
        Assert.Equal("Box face behavior unassessed",
            Issue(r with { BoxSilhouette = false }, Pa(new(0, 1000, 120), new(300, 300, 300), kind: OcclusionPrimitiveKind.Box), PaEye)); // R8
        Assert.Equal(NearEyeText, Issue(r, Pa(new(0, 30, 120), new(300, 1, 200)), PaEye));                              // R9
    }

    [Fact]
    public void Invalid_bounds_and_an_eye_inside_a_box_are_refused_by_name()
    {
        var r = Rule(A10);
        Assert.Equal("invalid plane bounds or scale", Issue(r, P(OcclusionPrimitiveKind.PortalBox, default, new(0, 1000, 0), new(0, 2, 300))));
        Assert.Equal("invalid plane bounds or scale", Issue(r, P(OcclusionPrimitiveKind.Box, default, new(0, 1000, 0), new(300, 300, 300), scale: -1f)));
        Assert.Equal("no box view shadow: a degenerate box, or an eye inside or on it, is unassessed",
            Issue(r, P(OcclusionPrimitiveKind.Box, default, Vector3.Zero, new(300, 300, 300))));
    }

    [Fact]
    public void A_predicted_rectangle_has_the_shadow_of_its_mid_quad()
    {
        var r = Rule(A10);
        var p = Pa(new(0, 1000, 120), new(300, 1, 200));
        var (shadow, issue) = r.Shadow(p, PaEye);
        Assert.Null(issue);
        var direct = ConvexShadow.FromRectangle(OcclusionPrimitiveGeometry.MidQuad(p), PaEye)!;
        var behind = new SphereBound(new Vector3(0, 2000, 120), 10f);
        Assert.Equal(direct.Clearances(behind), shadow!.Clearances(behind));
        Assert.Equal(ShadowRelation.Inside, shadow.Classify(behind));
    }

    // ---------------------------------------------------------------- the primitive's geometry
    [Fact]
    public void Corners_are_half_the_full_bounds_times_the_scale_indexed_by_axis_bits()
    {
        var c = OcclusionPrimitiveGeometry.BoxCorners(P(OcclusionPrimitiveKind.Box, default, new(10, 20, 30), new(2, 4, 6), 2f));
        Assert.Equal(8, c.Length);
        Assert.Equal(new Vector3(8, 16, 24), c[0]);
        Assert.Equal(new Vector3(12, 24, 36), c[7]);
        Assert.Equal(new Vector3(12, 16, 24), c[1]);
    }

    [Fact]
    public void The_mid_quad_lies_in_local_y_zero_ring_ordered()
    {
        var q = OcclusionPrimitiveGeometry.MidQuad(P(OcclusionPrimitiveKind.PortalBox, default, Vector3.Zero, new(400, 2, 300)));
        Assert.Equal(new[] { new Vector3(-200, 0, -150), new Vector3(200, 0, -150), new Vector3(200, 0, 150), new Vector3(-200, 0, 150) }, q);
    }

    [Fact]
    public void Local_plus_y_under_a_yaw_z_is_sin_cos_zero()
    {
        var a = OcclusionPrimitiveGeometry.Axis(P(OcclusionPrimitiveKind.PortalBox, new(0, 0, 0.8f), Vector3.Zero, new(400, 2, 300)), Vector3.UnitY);
        Assert.True(MathF.Abs(a.X - MathF.Sin(0.8f)) < 1e-6f && MathF.Abs(a.Y - MathF.Cos(0.8f)) < 1e-6f && MathF.Abs(a.Z) < 1e-6f, a.ToString());
    }
}
