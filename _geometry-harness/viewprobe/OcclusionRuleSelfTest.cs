using System.Numerics;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

namespace ViewProbe;

/// <summary>Known answers for the occlusion rule's orientation scope (ShapeCullPolicy over the Engine's
/// OcclusionRule and PortalBoxOrientation), run through the rule loader and Shadow itself on planes built here.
/// Cases 6000-6014. Needs no MO2 instance, no game and no GL context.</summary>
static class OcclusionRuleSelfTest
{
    // A10's fields, short evidence: what every rule file without the upright field looks like.
    const string OldRule = """
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
    const string NewRule = """
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

    // The A10-shaped rule's report fields, captured from the build before the upright field existed.
    const string OldConfigurationJson =
        "{\r\n  \"Identity\": \"selftest-a10-shape\",\r\n  \"MinimumEyeDistance\": 42,\r\n  \"MaximumAbsNormalZ\": 0.001,\r\n  \"Frozen\": false,\r\n  \"Evidence\": [\r\n    \"selftest\"\r\n  ],\r\n  \"BoxSilhouette\": true,\r\n  \"HorizontalPortalBoxNormalDownEyeAbove\": true\r\n}";
    const string OldScope =
        "Research predictions for rigid opaque shapes and finite PortalBox rectangles; no cell-wide clearance or compatibility attribution. Upright Box primitives beyond the rule's minimum eye distance are predicted by the silhouette of the stored box from the eye together with the box's own volume; a union of per-face shadows is NOT separated from that silhouette by the supporting evidence and remains untested. Horizontal PortalBox rectangles whose authored normal points down (-Z), seen from above their plane, are predicted by the same rectangle shadow; the normal-up form, an eye below the plane and every other tilt remain refused.";

    const string Sealed = "plane orientation outside the supplied rule's evidence scope";
    const string BoxText = "box orientation outside the supplied rule's evidence scope; only upright boxes were measured";
    const string UpText = "horizontal PortalBox with its normal up (+Z): outside the supplied rule's evidence scope; only normal-down (-Z) was opted in";
    const string BelowText = "horizontal PortalBox seen from below its plane: outside the supplied rule's evidence scope; only eye-above was opted in";

    static readonly Vector3 Eye = Vector3.Zero;

    static PlaneRender Plane(PlaneKind kind, Vector3 rotRad, Vector3 pos, Vector3 bounds) =>
        new(FormKey.Factory("000800:SelfTest.esp"), kind, bounds, pos, new Vec3(rotRad.X, rotRad.Y, rotRad.Z), 1f,
            PlaneProvenance.Added, "SelfTest.esp", null, new[] { "SelfTest.esp" }, "FE000800");

    /// <summary>An upright-ish PortalBox 1,000 u ahead whose quad normal has |z| = <paramref name="t"/>, leaning by
    /// <paramref name="sign"/>.</summary>
    static PlaneRender TiltedPortal(float t, float sign = 1f) =>
        Plane(PlaneKind.PortalBox, new Vector3(sign * MathF.Asin(t), 0f, 0f), new Vector3(0f, 1000f, 0f), new Vector3(400f, 2f, 300f));

    /// <summary>A Box 1,000 u ahead rolled about its local Y so its local X axis has |z| = <paramref name="t"/>.</summary>
    static PlaneRender TiltedBox(float t) =>
        Plane(PlaneKind.Box, new Vector3(0f, MathF.Asin(t), 0f), new Vector3(0f, 1000f, 0f), new Vector3(300f, 300f, 300f));

    /// <summary>A horizontal PortalBox 1,000 u ahead, its authored normal <paramref name="down"/> or up, tipped
    /// <paramref name="deviation"/> (sine) off horizontal.</summary>
    static PlaneRender Horizontal(bool down, float deviation)
    {
        foreach (var x in new[] { MathF.PI / 2f, 3f * MathF.PI / 2f })
        {
            var p = Plane(PlaneKind.PortalBox, new Vector3(x + MathF.Asin(deviation), 0f, 0f), new Vector3(0f, 1000f, -200f),
                          new Vector3(400f, 2f, 300f));
            if ((p.Normal.Z < 0f) == down) return p;
        }
        throw new InvalidOperationException("no rotation puts the normal " + (down ? "down" : "up"));
    }

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        string oldPath = Path.Combine(dir, "rule-old.json"), newPath = Path.Combine(dir, "rule-new.json");
        File.WriteAllText(oldPath, OldRule);
        File.WriteAllText(newPath, NewRule);
        int fails = 0, ran = 0;
        void C(string name, bool ok, string detail = "")
        {
            ran++;
            if (!ok) fails++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -- " + detail : "")}");
        }
        string? Issue(PlaneRender p, Vector3? eye = null) => ShapeCullPolicy.Shadow(p, eye ?? Eye).Issue;
        string Show(string? s) => s ?? "assessed";

        ShapeCullPolicy.Load(oldPath);
        var oldJson = System.Text.Json.JsonSerializer.Serialize(ShapeCullPolicy.Configuration, ReportSink.Options());
        var oldScope = ShapeCullPolicy.Scope;
        Console.WriteLine("old ruleConfiguration: " + System.Text.Json.JsonSerializer.Serialize(oldJson));
        Console.WriteLine("old scope: " + System.Text.Json.JsonSerializer.Serialize(oldScope));
        C("6000 old rule: PortalBox at |n.z| 0.0205 refused with the sealed text",
          Issue(TiltedPortal(0.0205f)) == Sealed, Show(Issue(TiltedPortal(0.0205f))));
        C("6000 old rule: Box at |axis.z| 0.0174 refused with the box text",
          Issue(TiltedBox(0.0174f)) == BoxText, Show(Issue(TiltedBox(0.0174f))));
        C("6000 old rule: upright PortalBox and Box assessed",
          Issue(TiltedPortal(0f)) is null && Issue(TiltedBox(0f)) is null, $"{Show(Issue(TiltedPortal(0f)))}; {Show(Issue(TiltedBox(0f)))}");
        C("6010 old rule: ruleConfiguration bytes as before the upright field",
          oldJson.Replace("\r\n", "\n") == OldConfigurationJson.Replace("\r\n", "\n"));
        C("6014 old rule: scope text as before the upright field", oldScope == OldScope);

        ShapeCullPolicy.Load(newPath);
        foreach (var t in new[] { 0.00373f, 0.00608f, 0.01896f, 0.0205f })
            foreach (var s in new[] { 1f, -1f })
                C($"6002 new rule: PortalBox at |n.z| {t} leaning {(s > 0 ? "+" : "-")} assessed",
                  Issue(TiltedPortal(t, s)) is null, Show(Issue(TiltedPortal(t, s))));
        C("6004 new rule: PortalBox at |n.z| 0.0215 refused with the sealed text",
          Issue(TiltedPortal(0.0215f)) == Sealed, Show(Issue(TiltedPortal(0.0215f))));
        C("6006 new rule: Box at |axis.z| 0.01741 assessed", Issue(TiltedBox(0.01741f)) is null, Show(Issue(TiltedBox(0.01741f))));
        C("6006 new rule: Box at |axis.z| 0.0215 refused with the box text",
          Issue(TiltedBox(0.0215f)) == BoxText, Show(Issue(TiltedBox(0.0215f))));
        var above = new Vector3(0f, 800f, 400f);
        var below = new Vector3(0f, 800f, -800f);
        C("6008 new rule: exactly horizontal normal-down panel seen from above assessed",
          Issue(Horizontal(true, 0f), above) is null, Show(Issue(Horizontal(true, 0f), above)));
        C("6008 new rule: normal-down panel 0.00768 off horizontal still refused (horizontal tolerance 0.001)",
          Issue(Horizontal(true, 0.00768f), above) == Sealed, Show(Issue(Horizontal(true, 0.00768f), above)));
        C("6008 new rule: near-horizontal panel (deviation 0.0245) refused",
          Issue(Horizontal(true, 0.0245f), above) == Sealed, Show(Issue(Horizontal(true, 0.0245f), above)));
        C("6008 new rule: normal-up and eye-below texts unchanged",
          Issue(Horizontal(false, 0f), above) == UpText && Issue(Horizontal(true, 0f), below) == BelowText,
          $"{Show(Issue(Horizontal(false, 0f), above))}; {Show(Issue(Horizontal(true, 0f), below))}");
        var newJson = System.Text.Json.JsonSerializer.Serialize(ShapeCullPolicy.Configuration, ReportSink.Options());
        C("6010 new rule: ruleConfiguration carries the upright field",
          newJson.Contains("\"UprightMaximumAbsNormalZ\": 0.021"), newJson.Replace("\r", "").Replace("\n", " "));
        C("6014 new rule: scope names the upright tolerance", ShapeCullPolicy.Scope.Contains("within |n.z| <= 0.021 of upright"),
          ShapeCullPolicy.Scope);

        foreach (var bad in new[] { "-0.1", "1.5", "1e9" })
        {
            var badPath = Path.Combine(dir, "rule-bad.json");
            File.WriteAllText(badPath, NewRule.Replace("\"uprightMaximumAbsNormalZ\": 0.021", "\"uprightMaximumAbsNormalZ\": " + bad));
            string? why = null;
            try { ShapeCullPolicy.Load(badPath); }
            catch (Exception ex) { why = ex.Message; }
            C($"6012 Load refuses uprightMaximumAbsNormalZ {bad} with the existing sentence",
              why == "Occlusion rule requires an identity, finite camera/orientation limits and evidence references.", why ?? "loaded");
        }
        Console.WriteLine($"occlusion-rule selftest: {ran - fails} of {ran} passed");
        return fails == 0 ? 0 : 1;
    }
}
