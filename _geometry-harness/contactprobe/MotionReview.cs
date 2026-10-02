using System.Reflection;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

/// <summary>contactprobe --motion (with --surface / --volume): a pair whose member's stored pose is not its live pose is
/// measured against that member's motion instead of being refused (animated-object swept volume task). Mode
/// <c>swept</c>: the moving member's swept region (<see cref="SweptGeometry"/>) against the other member's static geometry —
/// witness / never / cannot_tell, and for --volume the certified [inner, outer] intersection with a closed partner.
/// Mode <c>pose:NAME</c>: the moving member posed at a named pose (closed, open, state:…, &lt;animation&gt;@start|@end) and
/// measured like a static one. A motion that cannot be read refuses with its reason. Without --motion nothing here runs.</summary>
static class MotionReview
{
    public sealed record Options(string Mode, string? PoseName, MotionOptions Sweep);

    public const string Policy = SweptGeometry.PolicyVersion + "|" + NifMotion.PolicyVersion;

    public static Options? Parse(string? spec, double? delta, int? budget)
    {
        if (spec is null || spec == "off") return null;
        var sweep = new MotionOptions(DeltaTarget: delta ?? 0.5, SampleBudget: budget ?? 4096);
        if (spec == "swept") return new("swept", null, sweep);
        if (spec.StartsWith("pose:", StringComparison.Ordinal) && spec.Length > 5) return new("pose", spec[5..], sweep);
        throw new ArgumentException("--motion takes off|swept|pose:NAME");
    }

    /// <summary>The base record's signature (MSTT, DOOR, ACTI…) from Mutagen's registration, by the index's class name.</summary>
    public static string Signature(string className) => MotionPlacements.Signature(className);

    public sealed class Cache(AssetResolver assets, OverlapIndex index, Options options)
    {
        public OverlapIndex Index => index;
        readonly Dictionary<string, MotionModel> models = new();
        readonly Dictionary<(string, VolumeGeometry), SweptRegion> regions = new();
        public Options Options => options;

        public byte[]? Read(string rel)
        {
            try
            {
                var res = assets.ResolveForPlacement(rel);
                if (res.Sources.Count == 0) return null;
                var (bytes, _) = AssetResolver.ReadPlacementSource(res.Sources[0]);
                return bytes;
            }
            catch (ArgumentException) { return null; }
        }

        public MotionModel Model(string formKey, byte[] bytes)
        {
            if (models.TryGetValue(formKey, out var m)) return m;
            var placement = FormKey.TryFactory(formKey, out var key) ? MotionPlacements.Of(index, key) : null;
            m = NifMotion.Read(bytes, Read, placement);
            models[formKey] = m;
            return m;
        }

        public SweptRegion Region(string formKey, byte[] bytes, VolumeGeometry g)
        {
            if (regions.TryGetValue((formKey, g), out var r)) return r;
            var m = Model(formKey, bytes);
            r = SweptGeometry.Build(bytes, m, null, g, options.Sweep, Read);
            regions[(formKey, g)] = r;
            return r;
        }
    }

    static string Labels(IEnumerable<MotionAnimation> anims) => string.Join("; ", anims.Select(a => $"{a.Id} {a.Cycle} {a.Trigger}"));

    /// <summary>The motion record for one pair and geometry, or null when neither member moves (the caller keeps its own
    /// result). <paramref name="staticOf"/> gives a member's static model-space geometry (surface or solid) or a refusal.</summary>
    public static object? Pair(Cache cache, VolumeGeometry g, string formA, byte[] bytesA, Snap poseA, string formB, byte[] bytesB, Snap poseB,
                               Func<byte[], (TriangleSoup? Soup, string[]? Tags, string? Refusal)> staticOf, bool volume)
    {
        string gname = g.ToString().ToLowerInvariant();
        var ma = cache.Model(formA, bytesA); var mb = cache.Model(formB, bytesB);
        bool movA = ma.Status == "moving", movB = mb.Status == "moving";
        bool refA = ma.Status == "refused", refB = mb.Status == "refused";
        if (!movA && !movB && !refA && !refB) return null;
        object Refuse(string member, string why) => new { geometry = gname, status = "refused", reason = $"{member}: live pose: {why}",
                                                          motion = new { mode = cache.Options.Mode, policy = Policy, member, refusal = why } };
        if (!movA && !movB)
            return refA ? Refuse("A", ma.Reason ?? "refused") : Refuse("B", mb.Reason ?? "refused");
        if (movA && movB)
        {
            // E4 (follow-ups item 4): two moving members on independent clocks unless the files link them
            if (cache.Options.Mode == "pose")
                return Refuse("A", "both members move: --motion pose:NAME poses one moving member; use --motion swept for a pair of moving members");
            var ra = cache.Region(formA, bytesA, g); var rb = cache.Region(formB, bytesB, g);
            if (ra.Status != "built") return Refuse("A", ra.Reason ?? "swept region refused");
            if (rb.Status != "built") return Refuse("B", rb.Reason ?? "swept region refused");
            var pa = SweptGeometry.Place(ra, poseA.Pos, poseA.Rot, poseA.Scale); var pb = SweptGeometry.Place(rb, poseB.Pos, poseB.Rot, poseB.Scale);
            var ha = SweptGeometry.HeldSoups(bytesA, ma, g, poseA.Pos, poseA.Rot, poseA.Scale, cache.Read);
            var hb = SweptGeometry.HeldSoups(bytesB, mb, g, poseB.Pos, poseB.Rot, poseB.Scale, cache.Read);
            string? coupling = FormKey.TryFactory(formA, out var ka) && FormKey.TryFactory(formB, out var kb) ? MotionPlacements.Coupling(cache.Index, ka, kb) : null;
            var two = SweptGeometry.ContactBoth(pa, ha, pb, hb, coupling, cache.Options.Sweep);
            if (two.Decision == "refused")
                return new { geometry = gname, status = "refused", reason = two.Reason, motion = new { mode = "swept", policy = Policy, member = "both", refusal = two.Reason } };
            return new
            {
                geometry = gname, status = "motion",
                motion = new
                {
                    mode = "swept", policy = Policy, member = "both", decision = two.Decision, reason = two.Reason, delta = two.Width, converged = two.Converged,
                    animationA = two.AnimationA, timeA = two.TimeA, poseA = two.PoseA, animationB = two.AnimationB, timeB = two.TimeB, poseB = two.PoseB,
                    coupling = two.Coupling, label = two.Coupling is null ? SweptGeometry.IndependentClocks : "the files link the two references: " + two.Coupling,
                    labelA = Labels(pa.Animations), labelB = Labels(pb.Animations), pairsExamined = two.PairsExamined,
                    volume = (object?)null, volumeReason = volume ? "not measured: two moving members" : null,
                },
            };
        }
        if (refA || refB) return refA ? Refuse("A", ma.Reason ?? "refused") : Refuse("B", mb.Reason ?? "refused");
        string member = movA ? "A" : "B";
        var (mForm, mBytes, mPose, mModel) = movA ? (formA, bytesA, poseA, ma) : (formB, bytesB, poseB, mb);
        var (oBytes, oPose) = movA ? (bytesB, poseB) : (bytesA, poseA);
        var other = staticOf(oBytes);
        if (other.Soup is null) return new { geometry = gname, status = "refused", reason = $"{(movA ? "B" : "A")}: {other.Refusal}" };
        var partner = SpatialOverlap.TransformSoup(other.Soup, oPose.Pos, oPose.Rot, oPose.Scale);
        if (cache.Options.Mode == "pose")
        {
            var p = NifMotion.Pose(mBytes, mModel, cache.Options.PoseName!, g, cache.Read);
            if (p.Status != "posed") return Refuse(member, p.Reason ?? "pose refused");
            var posed = SpatialOverlap.TransformSoup(p.Soup!, mPose.Pos, mPose.Rot, mPose.Scale);
            // found defect (follow-ups E3): "rest" names no pose; the rest pose is the one flagged Rest, as NifMotion.Pose resolves it
            var pose = cache.Options.PoseName == "rest" ? mModel.Poses.FirstOrDefault(x => x.Rest) : mModel.Poses.FirstOrDefault(x => x.Name == cache.Options.PoseName);
            var motion = new { mode = "pose", policy = Policy, member, pose = cache.Options.PoseName, animation = p.AnimationId, time = p.Time, label = pose?.Label };
            if (volume)
            {
                var r = MeshVolume.Measure(posed, partner, g);
                return new { geometry = gname, status = r.Status, reason = r.Reason, intersection = r.Intersection, aInB = r.AInB, bInA = r.BInA, converged = r.Converged, policy = r.PolicyVersion, motion };
            }
            var s = movA ? MeshSurface.Measure(posed, p.Tags, partner, other.Tags, g) : MeshSurface.Measure(partner, other.Tags, posed, p.Tags, g);
            return new { geometry = gname, result = s, motion };
        }
        var region = cache.Region(mForm, mBytes, g);
        if (region.Status != "built" && (region.Reason ?? "").EndsWith("no_solid_collision", StringComparison.Ordinal))
            return new { geometry = gname, status = "refused", reason = $"{member}: no_solid_collision" };   // not a pose question
        if (region.Status != "built") return Refuse(member, region.Reason ?? "swept region refused");
        var placed = SweptGeometry.Place(region, mPose.Pos, mPose.Rot, mPose.Scale);
        bool partnerSolid = MeshVolume.Solid(partner).Status == "solid";
        var d = SweptGeometry.Contact(placed, partner, other.Tags, partnerSolid, cache.Options.Sweep);
        if (d.Decision == "refused")
            return new { geometry = gname, status = "refused", reason = d.Reason!.StartsWith("partner: ", StringComparison.Ordinal) ? $"{(movA ? "B" : "A")}: {d.Reason[9..]}" : $"{member}: {d.Reason}",
                         motion = new { mode = "swept", policy = Policy, member, refusal = d.Reason } };
        SweptVolume? v = volume && partnerSolid && placed.Solid ? SweptGeometry.Volume(placed, partner) : null;
        return new
        {
            geometry = gname, status = "motion",
            motion = new
            {
                mode = "swept", policy = Policy, member, animations = region.Animations.Select(a => a.Id).ToArray(), label = Labels(region.Animations),
                decision = d.Decision, reason = d.Reason, delta = d.Decision == "cannot_tell" ? d.Width : placed.MaxDelta, converged = d.Converged,
                time = d.Time, witnessAnimation = d.AnimationId, crossingLower = d.WitnessCrossingLower, volumeLower = d.WitnessVolumeLower,
                samples = placed.Samples.Count, partnerSolid,
                volume = v is null ? null : new { v.Status, v.Reason, intersection = v.WithPartner, v.Converged },
            },
        };
    }
}
