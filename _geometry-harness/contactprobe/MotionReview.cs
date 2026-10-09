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
        readonly Dictionary<string, MemberModel> models = new();
        readonly Dictionary<(string, VolumeGeometry), SweptRegion> regions = new();
        public Options Options => options;
        /// <summary>The shared reader's sweep (the --motion-delta target and the --motion-budget evaluations).</summary>
        public MemberSweep Sweep => new(options.Sweep.DeltaTarget, options.Sweep.SampleBudget);

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

        /// <summary>The member read once under the shared rules (MemberMotion: lane M), with its placing base's record type.</summary>
        public MemberModel Model(string formKey, byte[] bytes)
        {
            if (models.TryGetValue(formKey, out var m)) return m;
            var placement = FormKey.TryFactory(formKey, out var key) ? MotionPlacements.Of(index, key) : null;
            m = MemberMotion.Read(bytes, Read, placement);
            models[formKey] = m;
            return m;
        }

        readonly Dictionary<string, (MotionModel Model, PathTubeResult Tube)> paths = new();
        /// <summary>Lane 3b, gap d: the model read with the path rule (path-driven nodes left to the tube) and its model-space tube.</summary>
        public (MotionModel Model, PathTubeResult Tube) Path(string formKey, byte[] bytes)
        {
            if (paths.TryGetValue(formKey, out var p)) return p;
            var placement = FormKey.TryFactory(formKey, out var key) ? MotionPlacements.Of(index, key) : null;
            var m = NifMotion.Read(bytes, Read, placement, new MotionReadOptions(PathHull: true));
            p = (m, NifMotion.PathTube(bytes, m, Read));
            paths[formKey] = p;
            return p;
        }

        public SweptRegion Region(string formKey, byte[] bytes, VolumeGeometry g)
        {
            if (regions.TryGetValue((formKey, g), out var r)) return r;
            // a clip-only member's region is exactly SweptGeometry.Build's (MemberMotion.Region keeps it)
            r = MemberMotion.Region(bytes, Model(formKey, bytes), g, Sweep, Read);
            regions[(formKey, g)] = r;
            return r;
        }
    }

    static string Labels(IEnumerable<MotionAnimation> anims) => string.Join("; ", anims.Select(a => $"{a.Id} {a.Cycle} {a.Trigger}"));

    /// <summary>Lane M: a moving member whose region holds more than clips (blends, a blender, a reference pose, a pinned hinge, a
    /// closed book), decided against the static member by the shared loop (MemberMotion.Decide) with the swept contact's per-pose
    /// test. The record names the witness's origin and what it means for a player (P1, P2), the evaluations spent and, for a
    /// cannot_tell, the width left and whether the budget ran out.</summary>
    static object Decided(Cache cache, VolumeGeometry g, string gname, string member, bool movA, string mForm, byte[] mBytes, Snap mPose, MemberModel mModel,
                          string oForm, byte[] oBytes, Snap oPose, (TriangleSoup? Soup, string[]? Tags, string? Refusal) other, bool volume, AlphaVisibility? alpha)
    {
        string policy = Policy + "|" + MemberMotion.PolicyVersion;
        object Refuse(string why) => new { geometry = gname, status = "refused", reason = $"{member}: live pose: {why}", motion = new { mode = "swept", policy, member, refusal = why } };
        var stored = NifMotion.Pose(mBytes, mModel.Motion, "stored", 0f, g, cache.Read);
        if (stored.Status != "posed" && (stored.Reason ?? "").EndsWith("no_solid_collision", StringComparison.Ordinal))
            return new { geometry = gname, status = "refused", reason = $"{member}: no_solid_collision" };
        if (stored.Status != "posed") return Refuse(stored.Reason ?? "the stored pose could not be read");
        // lane 3b: a pair whose world placement collapses a triangle is measured in the static member's frame (PairFrame)
        var so = PairFrame.Origin(stored.Soup!, mPose.Pos, mPose.Rot, mPose.Scale, other.Soup!, oPose.Pos, oPose.Rot, oPose.Scale);
        var partner = SpatialOverlap.TransformSoup(other.Soup!, PairFrame.Shift(oPose.Pos, so), oPose.Rot, oPose.Scale);
        bool partnerSolid = MeshVolume.Solid(partner).Status == "solid";
        var capture = new MemberCapture();
        var (pred, pwhy) = MemberPredicates.Contact(partner, other.Tags, partnerSolid, g, capture);
        if (pred is null)
            return new { geometry = gname, status = "refused", reason = pwhy!.StartsWith("partner: ", StringComparison.Ordinal) ? $"{(movA ? "B" : "A")}: {pwhy[9..]}" : $"{member}: {pwhy}",
                         motion = new { mode = "swept", policy, member, refusal = pwhy } };
        var at = PairFrame.Shift(mPose.Pos, so);
        var d = MemberMotion.Decide(mBytes, mModel, g, at, mPose.Rot, mPose.Scale, cache.Sweep, pred, cache.Read);
        if (d.Decision == "refused") return Refuse(d.Reason ?? "refused");
        // the swept volume and the visible part of an alpha crossing are measured over the member's region, as for a clip-only member
        SweptRegion? placed = null;
        SweptRegion? Placed()
        {
            if (placed is not null) return placed;
            var region = cache.Region(mForm, mBytes, g);
            return placed = region.Status == "built" ? SweptGeometry.Place(region, at, mPose.Rot, mPose.Scale) : null;
        }
        SweptVolume? v = volume && partnerSolid && Placed() is { Solid: true } pr ? SweptGeometry.Volume(pr, partner) : null;
        object? visibleMotion = null;
        if (alpha is not null && !volume && g == VolumeGeometry.Render && d.Decision == "witness"
            && !(capture.CrossingByTags?.GetValueOrDefault(SurfaceMaterial.Pair(SurfaceMaterial.Opaque, SurfaceMaterial.Opaque)) > 0)
            && AlphaVisibility.HasAlphaCrossing(capture.CrossingByTags) && Placed() is { } pv)
        {
            var vd = SweptGeometry.ContactVisible(pv, alpha.MovingSource(mForm, mBytes)(), partner, other.Tags, alpha.Source(oForm, oBytes, other.Soup!)(),
                                                  partnerSolid, alpha.Options);
            visibleMotion = alpha.MotionBlock(vd, mForm, oForm);
        }
        // P2: a swing witness's meaning from the partner's solid status (its collision layer against the body's is not read)
        string? meaning = d.Origin == MemberMotion.OriginSwing ? MemberMotion.SwingMeaning(MemberMotion.HasSolidCollision(oBytes)) + " (from the partner's solid collision; the layer filter is not read)" : null;
        return ContactNotes.Attach(new
        {
            geometry = gname, status = "motion",
            motion = new
            {
                mode = "swept", policy, member, animations = mModel.Motion.Animations.Select(a => a.Id).ToArray(),
                mechanisms = mModel.Mechanisms.Select(x => x.Kind).ToArray(), label = string.Join("; ", mModel.Labels),
                decision = d.Decision, reason = d.Reason, origin = d.Origin, witnessLabel = d.Label, meaning, note = d.Note,
                delta = d.Width, converged = d.Converged, evaluations = d.Evaluations, budget = cache.Sweep.Budget,
                time = d.Time, witnessAnimation = d.AnimationId, animationB = d.AnimationB, timeB = d.TimeB, weights = d.Weights, angles = d.Angles,
                crossingLower = d.Decision == "witness" ? capture.CrossingLower : null, volumeLower = d.Decision == "witness" ? capture.VolumeLower : null,
                crossingByTags = d.Decision == "witness" ? capture.CrossingByTags : null, partnerSolid,
                volume = v is null ? null : new { v.Status, v.Reason, intersection = v.WithPartner, v.Converged },
            },
        }, ContactNotes.Options, ("pairFrame", ContactNotes.Frame(so)), ("visibleMotion", visibleMotion));
    }

    /// <summary>The motion record for one pair and geometry, or null when neither member moves (the caller keeps its own
    /// result). <paramref name="staticOf"/> gives a member's static model-space geometry (surface or solid) or a refusal.</summary>
    public static object? Pair(Cache cache, VolumeGeometry g, string formA, byte[] bytesA, Snap poseA, string formB, byte[] bytesB, Snap poseB,
                               Func<byte[], (TriangleSoup? Soup, string[]? Tags, string? Refusal)> staticOf, bool volume,
                               AlphaVisibility? alpha = null)
    {
        string gname = g.ToString().ToLowerInvariant();
        var ma = cache.Model(formA, bytesA); var mb = cache.Model(formB, bytesB);
        bool movA = ma.Status == "moving", movB = mb.Status == "moving";
        bool refA = ma.Status == "refused", refB = mb.Status == "refused";
        if (!movA && !movB && !refA && !refB) return null;
        object Refuse(string member, string why) => new { geometry = gname, status = "refused", reason = $"{member}: live pose: {why}",
                                                          motion = new { mode = cache.Options.Mode, policy = Policy, member, refusal = why } };
        // lane 3b, gap d: a member refused only for a NiPathInterpolator is measured as a tube around its path (swept mode)
        static bool PathRefusal(MemberModel m) => m.Status == "refused" && (m.Reason ?? "").Contains("NiPathInterpolator", StringComparison.Ordinal);
        bool pathA = PathRefusal(ma), pathB = PathRefusal(mb);
        if ((pathA || pathB) && cache.Options.Mode == "swept" && !(pathA && pathB))
        {
            bool otherMoves = pathA ? movB : movA, otherRefused = pathA ? refB : refA;
            string pm = pathA ? "A" : "B", po = pathA ? "B" : "A";
            if (otherRefused) return Refuse(po, (pathA ? mb.Reason : ma.Reason) ?? "refused");
            var (pBytes, pPose, pForm) = pathA ? (bytesA, poseA, formA) : (bytesB, poseB, formB);
            var (sBytes, sPose, sForm) = pathA ? (bytesB, poseB, formB) : (bytesA, poseA, formA);
            var (pModel, tube) = cache.Path(pForm, pBytes);
            if (tube.Status != "built") return Refuse(pm, tube.Status == "none" ? "path rule found no path-driven node" : tube.Reason ?? "path tube refused");
            var outside = NifMotion.OutsidePathSurface(pBytes, pModel, tube, g, cache.Read);
            if (outside.Status == "refused" && outside.Reason == "no_solid_collision") return new { geometry = gname, status = "refused", reason = $"{pm}: no_solid_collision" };
            if (outside.Status == "refused") return Refuse(pm, outside.Reason ?? "outside the path subtree refused");
            var placedTube = NifMotion.PlaceTube(tube, pPose.Pos, pPose.Rot, pPose.Scale);
            SweptDecision td; object? rest = null; Vec3 restFrame = new(0, 0, 0);
            string decision; string? why;
            if (otherMoves)
            {
                // the partner moves too (lane 3b, real data: the wind-swayed banners): the tube against every sample of its swept region,
                // and the path model's static rest against the partner's motion
                var otherRegion = cache.Region(sForm, sBytes, g);
                if (otherRegion.Status != "built") return Refuse(po, otherRegion.Reason ?? "swept region refused");
                var placedOther = SweptGeometry.Place(otherRegion, sPose.Pos, sPose.Rot, sPose.Scale);
                td = SweptGeometry.ContactTubeSwept(placedTube, placedOther);
                if (td.Decision == "refused") return Refuse(pm, td.Reason ?? "path tube contact refused");
                SweptDecision? rd = null;
                if (outside.Status == "measured")
                {
                    var restW = SpatialOverlap.TransformSoup(outside.Soup!, pPose.Pos, pPose.Rot, pPose.Scale);
                    rd = SweptGeometry.Contact(placedOther, restW, outside.Tags, MeshVolume.Solid(restW).Status == "solid", cache.Options.Sweep);
                    rest = rd;
                }
                decision = rd is { Decision: "refused" or "cannot_tell" } ? "cannot_tell" : rd is { Decision: "witness" } ? "witness" : td.Decision;
                why = rd is { Decision: "refused" or "cannot_tell" } ? $"outside the path subtree against the moving partner: {rd.Decision} {rd.Reason}"
                    : decision == "witness" ? "a sampled pose of the partner crosses a shape outside the path subtree" : td.Reason;
            }
            else
            {
                var st = staticOf(sBytes);
                if (st.Soup is null) return new { geometry = gname, status = "refused", reason = $"{po}: {st.Refusal}" };
                var partnerW = SpatialOverlap.TransformSoup(st.Soup, sPose.Pos, sPose.Rot, sPose.Scale);
                td = SweptGeometry.ContactTube(placedTube, partnerW, st.Tags);
                if (td.Decision == "refused") return Refuse(pm, td.Reason ?? "path tube contact refused");
                SurfaceContactResult? sr = null;
                if (outside.Status == "measured")
                {
                    (sr, restFrame) = pathA
                        ? MeshSurface.MeasurePlacedFramed(outside.Soup!, outside.Tags, pPose.Pos, pPose.Rot, pPose.Scale, st.Soup, st.Tags, sPose.Pos, sPose.Rot, sPose.Scale, g)
                        : MeshSurface.MeasurePlacedFramed(st.Soup, st.Tags, sPose.Pos, sPose.Rot, sPose.Scale, outside.Soup!, outside.Tags, pPose.Pos, pPose.Rot, pPose.Scale, g);
                    rest = sr;
                }
                var restCross = sr is null ? null : MeshSurface.OpaqueCrossing(sr);
                bool restBuried = sr is not null && ((sr.AInB?.Inside?.Lower ?? 0) > 0 || (sr.BInA?.Inside?.Lower ?? 0) > 0);
                decision = sr is not null && sr.Status != "measured" ? "cannot_tell"
                         : sr is not null && (restCross is { Lower: > 0 } || restBuried) ? "witness"
                         : td.Decision;
                why = sr is not null && sr.Status != "measured" ? $"outside the path subtree: {sr.Status} {sr.Reason}"
                    : decision == "witness" ? "a shape outside the path subtree crosses the partner at its stored pose" : td.Reason;
            }
            return ContactNotes.Attach(new
            {
                geometry = gname, status = "motion",
                motion = new
                {
                    mode = "path-tube", policy = Policy, member = pm, decision, reason = why, tubeDecision = td.Decision, tubeReach = td.Decision == "cannot_tell" ? td.Width : (double?)null,
                    tubePieces = placedTube.Pieces.Count, pathNodes = tube.PathNodes, labels = tube.Labels,
                    label = "the path-driven subtree anywhere on its curve, any rotation: never or cannot_tell, never a witness",
                    partnerMoves = otherMoves, outside = outside.Status, outsideResult = rest,
                },
            }, ContactNotes.Options, ("pairFrame", ContactNotes.Frame(restFrame)));
        }
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
            // lane 3b: a pair whose world placement collapses a triangle is measured in B's frame (PairFrame)
            var fo = PairFrame.Origin(ra.Samples[0].Soup, poseA.Pos, poseA.Rot, poseA.Scale, rb.Samples[0].Soup, poseB.Pos, poseB.Rot, poseB.Scale);
            var qa = PairFrame.Shift(poseA.Pos, fo); var qb = PairFrame.Shift(poseB.Pos, fo);
            var pa = SweptGeometry.Place(ra, qa, poseA.Rot, poseA.Scale); var pb = SweptGeometry.Place(rb, qb, poseB.Rot, poseB.Scale);
            var ha = SweptGeometry.HeldSoups(bytesA, ma.Motion, g, qa, poseA.Rot, poseA.Scale, cache.Read);
            var hb = SweptGeometry.HeldSoups(bytesB, mb.Motion, g, qb, poseB.Rot, poseB.Scale, cache.Read);
            string? coupling = FormKey.TryFactory(formA, out var ka) && FormKey.TryFactory(formB, out var kb) ? MotionPlacements.Coupling(cache.Index, ka, kb) : null;
            var two = SweptGeometry.ContactBoth(pa, ha, pb, hb, coupling, cache.Options.Sweep);
            if (two.Decision == "refused")
                return new { geometry = gname, status = "refused", reason = two.Reason, motion = new { mode = "swept", policy = Policy, member = "both", refusal = two.Reason } };
            return ContactNotes.Attach(new
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
            }, ContactNotes.Options, ("pairFrame", ContactNotes.Frame(fo)));
        }
        if (refA || refB) return refA ? Refuse("A", ma.Reason ?? "refused") : Refuse("B", mb.Reason ?? "refused");
        string member = movA ? "A" : "B";
        var (mForm, mBytes, mPose, mModel) = movA ? (formA, bytesA, poseA, ma) : (formB, bytesB, poseB, mb);
        var (oBytes, oPose, oForm) = movA ? (bytesB, poseB, formB) : (bytesA, poseA, formA);
        var other = staticOf(oBytes);
        if (other.Soup is null) return new { geometry = gname, status = "refused", reason = $"{(movA ? "B" : "A")}: {other.Refusal}" };
        if (cache.Options.Mode == "pose")
        {
            var p = NifMotion.Pose(mBytes, mModel.Motion, cache.Options.PoseName!, g, cache.Read);
            if (p.Status != "posed") return Refuse(member, p.Reason ?? "pose refused");
            // lane 3b: a pair whose world placement collapses a triangle is measured in the static member's frame (PairFrame)
            var po = PairFrame.Origin(p.Soup!, mPose.Pos, mPose.Rot, mPose.Scale, other.Soup, oPose.Pos, oPose.Rot, oPose.Scale);
            var posePartner = SpatialOverlap.TransformSoup(other.Soup, PairFrame.Shift(oPose.Pos, po), oPose.Rot, oPose.Scale);
            var posed = SpatialOverlap.TransformSoup(p.Soup!, PairFrame.Shift(mPose.Pos, po), mPose.Rot, mPose.Scale);
            // found defect (follow-ups E3): "rest" names no pose; the rest pose is the one flagged Rest, as NifMotion.Pose resolves it
            var pose = cache.Options.PoseName == "rest" ? mModel.Motion.Poses.FirstOrDefault(x => x.Rest) : mModel.Motion.Poses.FirstOrDefault(x => x.Name == cache.Options.PoseName);
            var motion = new { mode = "pose", policy = Policy, member, pose = cache.Options.PoseName, animation = p.AnimationId, time = p.Time, label = pose?.Label };
            if (volume)
            {
                var r = MeshVolume.Measure(posed, posePartner, g);
                return ContactNotes.Attach(new { geometry = gname, status = r.Status, reason = r.Reason, intersection = r.Intersection, aInB = r.AInB, bInA = r.BInA, converged = r.Converged, policy = r.PolicyVersion, motion },
                                           ContactNotes.Options, ("pairFrame", ContactNotes.Frame(po)));
            }
            var s = movA ? MeshSurface.Measure(posed, p.Tags, posePartner, other.Tags, g) : MeshSurface.Measure(posePartner, other.Tags, posed, p.Tags, g);
            // --alpha-visibility: the visible part of an alpha crossing at the pose (the moving member's texture coordinates are its
            // static soup's; a posed soup with another triangle count refuses)
            object? poseVisibility = null;
            if (alpha is not null && g == VolumeGeometry.Render && AlphaVisibility.HasAlphaCrossing(s.CrossingLengthByTags))
            {
                var mAlpha = alpha.MovingSource(mForm, mBytes)(); var oAlpha = alpha.Source(oForm, oBytes, other.Soup)();
                SurfaceVisibilityResult pv = mAlpha?.Texcoords.Triangles is { } tc && tc.Count != posed.TriangleCount
                    ? new("refused", $"{member}: the posed soup has {posed.TriangleCount} triangles, the texture coordinates {tc.Count}", s, null, null, null, false, Array.Empty<string>())
                    : movA ? SurfaceVisibility.Measure(posed, p.Tags, mAlpha, posePartner, other.Tags, oAlpha, alpha.Options)
                           : SurfaceVisibility.Measure(posePartner, other.Tags, oAlpha, posed, p.Tags, mAlpha, alpha.Options);
                poseVisibility = alpha.Block(pv, formA, formB);
            }
            if (!PairFrame.IsWorld(po))
            {
                double[]? Back(double[]? v) => v is null ? null : [v[0] + po.X, v[1] + po.Y, v[2] + po.Z];
                s = s with { EnvelopeMin = Back(s.EnvelopeMin), EnvelopeMax = Back(s.EnvelopeMax) };
            }
            return ContactNotes.Attach(new { geometry = gname, result = s, motion }, ContactNotes.Options, ("pairFrame", ContactNotes.Frame(po)), ("visibility", poseVisibility));
        }
        // lane M: a member with a mechanism beyond clips (blend, blender, reference pose, hinge, book) is decided by the shared loop
        if (!MemberMotion.IsClipOnly(mBytes, mModel, cache.Read))
            return Decided(cache, g, gname, member, movA, mForm, mBytes, mPose, mModel, oForm, oBytes, oPose, other, volume, alpha);
        var region = cache.Region(mForm, mBytes, g);
        if (region.Status != "built" && (region.Reason ?? "").EndsWith("no_solid_collision", StringComparison.Ordinal))
            return new { geometry = gname, status = "refused", reason = $"{member}: no_solid_collision" };   // not a pose question
        if (region.Status != "built") return Refuse(member, region.Reason ?? "swept region refused");
        // lane 3b: a pair whose world placement collapses a triangle is measured in the static member's frame (PairFrame)
        var so = PairFrame.Origin(region.Samples[0].Soup, mPose.Pos, mPose.Rot, mPose.Scale, other.Soup, oPose.Pos, oPose.Rot, oPose.Scale);
        var partner = SpatialOverlap.TransformSoup(other.Soup, PairFrame.Shift(oPose.Pos, so), oPose.Rot, oPose.Scale);
        var placed = SweptGeometry.Place(region, PairFrame.Shift(mPose.Pos, so), mPose.Rot, mPose.Scale);
        bool partnerSolid = MeshVolume.Solid(partner).Status == "solid";
        var d = SweptGeometry.Contact(placed, partner, other.Tags, partnerSolid, cache.Options.Sweep);
        if (d.Decision == "refused")
            return new { geometry = gname, status = "refused", reason = d.Reason!.StartsWith("partner: ", StringComparison.Ordinal) ? $"{(movA ? "B" : "A")}: {d.Reason[9..]}" : $"{member}: {d.Reason}",
                         motion = new { mode = "swept", policy = Policy, member, refusal = d.Reason } };
        SweptVolume? v = volume && partnerSolid && placed.Solid ? SweptGeometry.Volume(placed, partner) : null;
        // --alpha-visibility: a witness whose crossing is not opaque x opaque is re-read for its visible part at every unclear sample
        object? visibleMotion = null;
        if (alpha is not null && !volume && g == VolumeGeometry.Render && d.Decision == "witness"
            && !(d.WitnessCrossingByTags?.GetValueOrDefault(SurfaceMaterial.Pair(SurfaceMaterial.Opaque, SurfaceMaterial.Opaque)) > 0)
            && AlphaVisibility.HasAlphaCrossing(d.WitnessCrossingByTags))
        {
            var vd = SweptGeometry.ContactVisible(placed, alpha.MovingSource(mForm, mBytes)(), partner, other.Tags, alpha.Source(oForm, oBytes, other.Soup)(),
                                                  partnerSolid, alpha.Options);
            visibleMotion = alpha.MotionBlock(vd, mForm, oForm);
        }
        return ContactNotes.Attach(new
        {
            geometry = gname, status = "motion",
            motion = new
            {
                mode = "swept", policy = Policy, member, animations = region.Animations.Select(a => a.Id).ToArray(), label = Labels(region.Animations),
                decision = d.Decision, reason = d.Reason, delta = d.Decision == "cannot_tell" ? d.Width : placed.MaxDelta, converged = d.Converged,
                time = d.Time, witnessAnimation = d.AnimationId, crossingLower = d.WitnessCrossingLower, volumeLower = d.WitnessVolumeLower,
                crossingByTags = d.WitnessCrossingByTags,
                samples = placed.Samples.Count, partnerSolid,
                volume = v is null ? null : new { v.Status, v.Reason, intersection = v.WithPartner, v.Converged },
            },
        }, ContactNotes.Options, ("pairFrame", ContactNotes.Frame(so)), ("visibleMotion", visibleMotion));
    }
}
