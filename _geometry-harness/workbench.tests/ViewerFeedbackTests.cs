using System.Numerics;
using HousecarlCore;
using SkyWright.Engine;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>Piranha's 2026-11-23 viewer feedback: the row boxes and the outlines are their own layers and start off, the
/// no-model diamond is solid, the yellow is a band along where two objects meet, and the wheel moves the eye.</summary>
public class ViewerFeedbackTests : IDisposable
{
    public ViewerFeedbackTests() => ViewLayers.Reset();
    public void Dispose() => ViewLayers.Reset();

    [Fact]
    public void Row_boxes_and_outlines_are_their_own_layers_and_start_off()
    {
        Assert.False(ViewLayers.IsOn(Layer.Boxes));
        Assert.False(ViewLayers.IsOn(Layer.Outlines));
        Assert.Equal("boxes", ViewLayers.Of(Layer.Boxes).Name);
        Assert.Equal("outlines", ViewLayers.Of(Layer.Outlines).Name);
        Assert.DoesNotContain("boxes", ViewLayers.Of(Layer.Marks).Tip);
    }

    [Fact]
    public void A_no_model_diamond_is_eight_outward_faces()
    {
        var tris = new List<float>();
        var at = new Vector3(100, -50, 20);
        MarkerGizmos.DiamondTriangles(tris, at, 10f);
        Assert.Equal(8 * 3 * 6, tris.Count);
        var centre = new Vector3(at.X, at.Z, -at.Y);   // the view's Y-up space
        for (int t = 0; t < 8; t++)
        {
            Vector3 V(int i) => new(tris[(t * 3 + i) * 6], tris[(t * 3 + i) * 6 + 1], tris[(t * 3 + i) * 6 + 2]);
            Vector3 a = V(0), b = V(1), c = V(2);
            var n = Vector3.Cross(b - a, c - a);
            Assert.True(Vector3.Dot(n, (a + b + c) / 3f - centre) > 0, $"face {t} points inward");
        }
    }

    [Fact]
    public void The_band_keeps_only_the_part_of_a_triangle_near_its_crossing()
    {
        // a 1000-unit triangle crossed along a short segment near one corner: the band is a small part of it
        var soup = new TriangleSoup(new[] { new Vector3(0, 0, 0), new Vector3(1000, 0, 0), new Vector3(0, 1000, 0) }, new[] { 0, 1, 2 });
        var piece = new CrossingPiece("crossing", new[] { 0 }, new[] { 0 }, 10, 10, 0, 40, 10, 0, "t", true);
        var band = new List<float>();
        ConflictMarks.Band(band, soup, new[] { (0, piece) }, 8f);
        Assert.NotEmpty(band);
        float area = 0;
        for (int i = 0; i < band.Count; i += 18)
        {
            Vector3 a = new(band[i], band[i + 1], band[i + 2]), b = new(band[i + 6], band[i + 7], band[i + 8]), c = new(band[i + 12], band[i + 13], band[i + 14]);
            area += Vector3.Cross(b - a, c - a).Length() / 2;
            var mid = (a + b + c) / 3f;
            Assert.True(ConflictMarks.DistanceToSegment(mid, new Vector3(10, 10, 0), new Vector3(40, 10, 0)) <= 8f + 1e-3f, $"a band piece's middle {mid} is outside the band");
        }
        Assert.True(area < 0.01f * 500_000f, $"the band covers {area:0} of the triangle's 500000 square units");
        Assert.InRange(ConflictMarks.BandWidth(soup, soup), 3f, 16f);
    }

    [Fact]
    public void The_wheel_moves_toward_what_is_under_the_mouse_and_never_through_it()
    {
        Assert.Equal(256f, WorkbenchHost.WheelStep(0f, 1f));
        Assert.Equal(-512f, WorkbenchHost.WheelStep(0f, -2f));
        Assert.Equal(200f, WorkbenchHost.WheelStep(1000f, 1f));
        Assert.Equal(-250f, WorkbenchHost.WheelStep(1000f, -1f));
        Assert.True(WorkbenchHost.WheelStep(20f, 10f) <= 18f, "ten notches in passed through what is under the mouse");
        Assert.Equal(8f, WorkbenchHost.WheelStep(10f, 1f));
    }
}
