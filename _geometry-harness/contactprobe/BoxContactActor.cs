using System.Numerics;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

/// <summary>contactprobe --box-contact, usage-week M4 (2026-10-14, g1-0040): a placed actor whose base has no model path is tested
/// by its humanoid body (<see cref="AnchorBodyBox"/>, the body --anchor uses: route constants at scale one, race height not applied)
/// instead of being refused. Any other model-less member keeps its refusal.</summary>
static class BoxContactActor
{
    public static readonly BodyExtent Body =
        new(ActorBand.PhysicalStep, ActorBand.PhysicalControllerTop(ActorBand.ScaleOneHeight), RouteConnectivity.DefaultCapsuleRadius);

    /// <summary>The per-box results for <paramref name="formKey"/> when the body applies (a model-less PlacedNpc), else null.</summary>
    public static List<object>? Boxes(string formKey, Snap? pose, OverlapIndex index, IEnumerable<(string? Label, Aabb Box, double[] Min, double[] Max)> boxes)
    {
        if (pose is not { } p || !FormKey.TryFactory(formKey, out var key)) return null;
        bool hasModel = index.RefBase.TryGetValue(key, out var basis) && index.BaseMesh.ContainsKey(basis);
        if (!AnchorBodyBox.Applies(index.RefArm.GetValueOrDefault(key), hasModel)) return null;
        var at = new Vector3(p.Pos.X, p.Pos.Y, p.Pos.Z);
        return boxes.Select(x =>
        {
            bool m = AnchorBodyBox.Meets(at, Body, x.Box);
            return (object)new { x.Label, min = x.Min, max = x.Max, opaqueMeets = m, anyMeets = m, reason = (string?)null };
        }).ToList();
    }

    /// <summary>The note a measured actor-body row carries.</summary>
    public static object Note => new
    {
        geometry = AnchorBodyBox.Label,
        bottom = Body.Bottom, top = Body.Top, radius = Body.Radius,
        note = "the winning base has no model path: the actor's humanoid body (a capsule above its placement) stands in for it",
    };
}
