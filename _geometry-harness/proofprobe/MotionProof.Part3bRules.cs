using System.Numerics;
using HousecarlCore;

/// <summary>Known answers for the reach-region Part 3b rules (refusals to verdicts): clip names through the character's list
/// (CN), measured primitive solidity (PS), pose transfer between models on identical graph files (PT), blend envelopes (BE),
/// path hulls (PR), moving emitters, settled gravity and the projectile range (PM), and a measured absent model (AM). Expected
/// values are analytic or closed form, stated beside each case; each case names the wrong answer it rejects. The rule cases are
/// red until implemented (the APIs are stubs that throw). The canary is wrong by construction.</summary>
static partial class MotionProof
{
    static bool Near3b(double a, double b, double tol = 1e-3) => Math.Abs(a - b) <= tol;
    static bool BoxContains3b(Aabb box, Vector3 p, float tol = 1e-3f) => !box.IsEmpty
        && p.X >= box.Min.X - tol && p.X <= box.Max.X + tol && p.Y >= box.Min.Y - tol && p.Y <= box.Max.Y + tol && p.Z >= box.Min.Z - tol && p.Z <= box.Max.Z + tol;
    static string B3b(Aabb b) => b.ToString();

    public static void RunPart3bRules(Action<string, bool, string> check)
    {
        // ================= CN: clip names through the character's animation list =================
        const string pd = "meshes\\W\\X";
        var list = new[] { "meshes\\W\\X\\Animations\\Rel.hkx", "meshes\\W\\X\\Animations\\Rld.hkx" };
        // CN1: the behaviour names another character's folder; the list holds one entry of that file name. Rejects the literal path.
        Case(check, "part3b K-CN1: AnimationsDwarven\\Rel.hkx resolves to the character's Animations\\Rel.hkx, labelled", () =>
        {
            var r = HavokBehaviour.ResolveClipName(pd, "AnimationsDwarven\\Rel.hkx", list);
            return (string.Equals(r.Path, list[0], StringComparison.OrdinalIgnoreCase) && r.Label is not null && r.Refusal is null, $"{r}");
        });
        // CN2: the literal path is in the list: unchanged, no label. Rejects relabelling everything.
        Case(check, "part3b K-CN2: a literal path the list holds stands, unlabelled", () =>
        {
            var r = HavokBehaviour.ResolveClipName(pd, "Animations\\Rel.hkx", list);
            return (string.Equals(r.Path, list[0], StringComparison.OrdinalIgnoreCase) && r.Label is null && r.Refusal is null, $"{r}");
        });
        // CN3: file names compare case-insensitively (the shared behaviour writes .HKX). Rejects an ordinal comparison.
        Case(check, "part3b K-CN3: AnimationsDwarven\\RLD.HKX matches Animations\\Rld.hkx", () =>
        {
            var r = HavokBehaviour.ResolveClipName(pd, "AnimationsDwarven\\RLD.HKX", list);
            return (string.Equals(r.Path, list[1], StringComparison.OrdinalIgnoreCase) && r.Refusal is null, $"{r}");
        });
        // CN4: no entry of that name: refused by name. Rejects falling back to the literal path silently.
        Case(check, "part3b K-CN4: a clip name the character does not list refuses, naming it", () =>
        {
            var r = HavokBehaviour.ResolveClipName(pd, "Other\\Missing.hkx", list);
            return (r.Refusal is { } why && why.Contains("Missing.hkx") && why.Contains("not in the character's animation list"), $"{r}");
        });
        // CN5: two entries of that name in different folders: refused (which one plays is not in the files). Rejects taking the first.
        Case(check, "part3b K-CN5: two list entries of one file name refuse, naming the count", () =>
        {
            var two = new[] { "meshes\\W\\X\\A\\Rel.hkx", "meshes\\W\\X\\B\\Rel.hkx" };
            var r = HavokBehaviour.ResolveClipName(pd, "Z\\Rel.hkx", two);
            return (r.Refusal is { } why && why.Contains('2'), $"{r}");
        });
        // CN6: an empty list (a character that lists nothing): the literal path, as before the rule.
        Case(check, "part3b K-CN6: an empty character list keeps the literal path", () =>
        {
            var r = HavokBehaviour.ResolveClipName(pd, "AnimationsDwarven\\Rel.hkx", Array.Empty<string>());
            return (string.Equals(r.Path, "meshes\\W\\X\\AnimationsDwarven\\Rel.hkx", StringComparison.OrdinalIgnoreCase) && r.Label is null && r.Refusal is null, $"{r}");
        });

        // ================= PS: measured primitive solidity =================
        const string tableJson = """
            [{"kind":"base","value":"000021:Skyrim.esm","layer":"none","verdict":"solid","stops":["player"],"evidence":"Q1"},
             {"kind":"baseType","value":"AcousticSpace","layer":"none","verdict":"not_solid","stops":[],"evidence":"Q2"},
             {"kind":"baseType","value":"Static","layer":"none","verdict":"solid","stops":["player","items"],"evidence":"Q3"},
             {"kind":"base","value":"000021:Skyrim.esm","layer":"L_NAVCUT","verdict":"not_solid","stops":[],"evidence":"Q4"}]
            """;
        // PS1: a measured solid base: certified, the exact rotated world box (90 deg about z swaps the x and y half extents),
        // labelled with what it stops; the base entry wins over the Static baseType entry (no "items"). Rejects COLL-only and a box
        // left unrotated.
        Case(check, "part3b K-PS1: CollisionMarker, no layer -> certified box (80,190,295)..(120,210,305), stops player (Q1)", () =>
        {
            var t = PrimitiveSolidity.Read(tableJson);
            var r = PrimitiveSolidity.Decide(new PrimitiveFacts("000021:Skyrim.esm", "Static", null, new Vector3(10, 20, 5), new Vec3(100, 200, 300), new Vec3(0, 0, MathF.PI / 2)), t, "REFUSAL");
            bool box = Near3b(r.Box.Min.X, 80, 1e-2) && Near3b(r.Box.Max.X, 120, 1e-2) && Near3b(r.Box.Min.Y, 190, 1e-2) && Near3b(r.Box.Max.Y, 210, 1e-2) && Near3b(r.Box.Min.Z, 295, 1e-2) && Near3b(r.Box.Max.Z, 305, 1e-2);
            string lab = string.Join(" | ", r.Labels);
            return (r.Status == "certified" && box && lab.Contains("player") && lab.Contains("Q1") && !lab.Contains("items"), $"{r.Status} {B3b(r.Box)} [{lab}]");
        });
        // PS2: an acoustic space measured not solid: no_geometry even though COLL L_ACOUSTIC_SPACE lists the character controller.
        // Rejects "COLL decides".
        Case(check, "part3b K-PS2: AcousticSpace, no layer -> no_geometry (Q2)", () =>
        {
            var r = PrimitiveSolidity.Decide(new PrimitiveFacts("0FF495:Skyrim.esm", "AcousticSpace", null, new Vector3(500, 500, 400), new Vec3(0, 0, 0), new Vec3(0, 0, 0)), PrimitiveSolidity.Read(tableJson), "REFUSAL");
            return (r.Status == "no_geometry" && (r.Reason ?? "").Contains("Q2"), $"{r.Status} {r.Reason}");
        });
        // PS3: a base and type with no entry: refused, the caller's refusal kept and the gap named. Rejects a default verdict.
        Case(check, "part3b K-PS3: an unmeasured base refuses with the caller's text and 'no measurement'", () =>
        {
            var r = PrimitiveSolidity.Decide(new PrimitiveFacts("000017:Skyrim.esm", "Furniture", null, new Vector3(1, 1, 1), new Vec3(0, 0, 0), new Vec3(0, 0, 0)), PrimitiveSolidity.Read(tableJson), "REFUSAL");
            return (r.Status == "refused" && (r.Reason ?? "").StartsWith("REFUSAL") && (r.Reason ?? "").Contains("no measurement"), $"{r.Status} {r.Reason}");
        });
        // PS4: a measured base with an unmeasured layer: refused. Rejects matching on the base alone.
        Case(check, "part3b K-PS4: a measured base stating an unmeasured layer refuses", () =>
        {
            var r = PrimitiveSolidity.Decide(new PrimitiveFacts("000021:Skyrim.esm", "Static", "L_TRANSPARENT_SMALL", new Vector3(1, 1, 1), new Vec3(0, 0, 0), new Vec3(0, 0, 0)), PrimitiveSolidity.Read(tableJson), "REFUSAL");
            return (r.Status == "refused", $"{r.Status} {r.Reason}");
        });
        // PS5: the stated layer selects its own entry. Rejects ignoring the layer.
        Case(check, "part3b K-PS5: CollisionMarker stating L_NAVCUT -> no_geometry (Q4)", () =>
        {
            var r = PrimitiveSolidity.Decide(new PrimitiveFacts("000021:Skyrim.esm", "Static", "L_NAVCUT", new Vector3(1, 1, 1), new Vec3(0, 0, 0), new Vec3(0, 0, 0)), PrimitiveSolidity.Read(tableJson), "REFUSAL");
            return (r.Status == "no_geometry" && (r.Reason ?? "").Contains("Q4"), $"{r.Status} {r.Reason}");
        });
        // PS6: the list refuses an unknown verdict and an entry with no evidence, naming them.
        Case(check, "part3b K-PS6: a list with verdict 'maybe' or empty evidence is refused by name", () =>
        {
            string a = "", b = "";
            try { PrimitiveSolidity.Read("""[{"kind":"base","value":"x","layer":"none","verdict":"maybe","stops":[],"evidence":"e"}]"""); a = "read"; }
            catch (ArgumentException e) { a = e.Message; }
            try { PrimitiveSolidity.Read("""[{"kind":"base","value":"x","layer":"none","verdict":"solid","stops":["player"],"evidence":""}]"""); b = "read"; }
            catch (ArgumentException e) { b = e.Message; }
            return (a.Contains("maybe") && b.Contains("evidence"), $"{a} / {b}");
        });

        // ================= PT: pose transfer =================
        var files = new Dictionary<string, string> { ["meshes\\a\\P.hkx"] = "AA11", ["meshes\\a\\Beh.hkx"] = "BB22" };
        var locs = new Dictionary<string, NodeLocal>
        {
            ["Bone1"] = new(new Vector3(1, 2, 3), Quaternion.Identity, 1),
            ["Bone2"] = new(new Vector3(0, 5, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f), 1),
        };
        var src = new TransferSource("meshes\\w\\Conf.nif", files, locs, "rest: stored pose (confirmed by look)");
        Dictionary<string, NodeLocal> Locs(string node, NodeLocal v) { var d = new Dictionary<string, NodeLocal>(locs) { [node] = v }; return d; }
        // PT1: identical up to path case/slash and sha case: transfers. Rejects an ordinal path comparison.
        Case(check, "part3b K-PT1: identical graph files (case, slash) and equal locals -> transfers", () =>
        {
            var f2 = new Dictionary<string, string> { ["MESHES/A/p.hkx"] = "aa11", ["meshes\\A\\beh.HKX"] = "bb22" };
            var r = PoseTransfer.Decide(f2, locs, new[] { src });
            return (r.From == src, r.Reason);
        });
        // PT2: one graph file differs: no transfer, naming it. Rejects transfer by graph name alone.
        Case(check, "part3b K-PT2: a differing graph file sha -> no transfer, names the file", () =>
        {
            var f2 = new Dictionary<string, string>(files) { ["meshes\\a\\Beh.hkx"] = "CC33" };
            var r = PoseTransfer.Decide(f2, locs, new[] { src });
            return (r.From is null && r.Reason.Contains("Beh.hkx", StringComparison.OrdinalIgnoreCase), r.Reason);
        });
        // PT3: one driven node's stored translation moved 0.01 u: no transfer, naming it. Rejects ignoring the locals.
        Case(check, "part3b K-PT3: Bone2 moved 0.01 u -> no transfer, names Bone2", () =>
        {
            var r = PoseTransfer.Decide(files, Locs("Bone2", new(new Vector3(0, 5.01f, 0), locs["Bone2"].R, 1)), new[] { src });
            return (r.From is null && r.Reason.Contains("Bone2"), r.Reason);
        });
        // PT4: an extra graph file on the candidate: no transfer. Rejects a subset comparison.
        Case(check, "part3b K-PT4: an extra graph file -> no transfer", () =>
        {
            var f2 = new Dictionary<string, string>(files) { ["meshes\\a\\Extra.hkx"] = "DD44" };
            return (PoseTransfer.Decide(f2, locs, new[] { src }).From is null, "");
        });
        // PT5: rotation 1e-3 rad off: no transfer; 1e-5 rad off: transfers. Rejects a loose rotation tolerance and a zero one.
        Case(check, "part3b K-PT5: rotation 1e-3 rad off -> none; 1e-5 rad off -> transfers", () =>
        {
            var q = locs["Bone2"].R;
            var far = PoseTransfer.Decide(files, Locs("Bone2", new(locs["Bone2"].T, Quaternion.Concatenate(q, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1e-3f)), 1)), new[] { src });
            var near = PoseTransfer.Decide(files, Locs("Bone2", new(locs["Bone2"].T, Quaternion.Concatenate(q, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1e-5f)), 1)), new[] { src });
            return (far.From is null && near.From == src, $"{far.Reason} / {near.Reason}");
        });
        // PT6: a driven node missing on the candidate: no transfer.
        Case(check, "part3b K-PT6: a driven node missing -> no transfer", () =>
        {
            var d = new Dictionary<string, NodeLocal>(locs); d.Remove("Bone2");
            return (PoseTransfer.Decide(files, d, new[] { src }).From is null, "");
        });

        // ================= BE: blend envelopes =================
        // BE1: rotations about z at 0, 30, 90 deg with a fixed translation: a rotation family spanning pi/2.
        Case(check, "part3b K-BE1: poses about z at 0/30/90 deg -> rotation family, interval width pi/2, extremes reproduced", () =>
        {
            var t0 = new Vector3(1, 0, 0);
            var poses = new[] { 0.0, 30.0, 90.0 }.Select(dg => (Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(dg * Math.PI / 180)), t0)).ToList();
            var f = BlendEnvelope.Family(poses, out var why);
            if (f is null) return (false, why ?? "null");
            var qMin = Quaternion.CreateFromAxisAngle(f.Axis, f.Min) * f.Q0; var qMax = Quaternion.CreateFromAxisAngle(f.Axis, f.Max) * f.Q0;
            bool ends = new[] { qMin, qMax }.All(q => poses.Any(p => HavokClip.AngleBetween(Quaternion.Normalize(q), p.Item1) < 1e-3f));
            return (f.Kind == "rotation" && Near3b(f.Max - f.Min, Math.PI / 2, 1e-3) && ends, $"{f}");
        });
        // BE2: rotations about two axes: not a family.
        Case(check, "part3b K-BE2: rotations about x and about z -> no family", () =>
        {
            var poses = new[] { (Quaternion.Identity, Vector3.Zero), (Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f), Vector3.Zero), (Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f), Vector3.Zero) };
            return (BlendEnvelope.Family(poses, out var why) is null && why is not null, why ?? "");
        });
        // BE3: two links each rotating about z over [0, pi/2], offset 10, point 5: x = 10 cos a + 5 cos(a+b), y = 10 sin a + 5 sin(a+b)
        // spans x [-5, 15], y [0, 15] (a = 90, b = 0 gives y = 15; a = b = 90 gives x = -5). The box must contain it and lie within
        // delta + 1e-3 of it. Rejects the union of the two end poses only (y would stop at 10).
        Case(check, "part3b K-BE3: two-link product box = x[-5,15] y[0,15] z[0,0] within delta", () =>
        {
            float d = 0.5f;
            var chain = new[] { new BlendFamily("rotation", Quaternion.Identity, Vector3.Zero, Vector3.UnitZ, 0, MathF.PI / 2),
                                new BlendFamily("rotation", Quaternion.Identity, new Vector3(10, 0, 0), Vector3.UnitZ, 0, MathF.PI / 2) };
            var b = BlendEnvelope.ProductBox(chain, new Vector3(5, 0, 0), d);
            bool contains = b.Min.X <= -5 + 1e-3 && b.Max.X >= 15 - 1e-3 && b.Min.Y <= 1e-3 && b.Max.Y >= 15 - 1e-3 && b.Min.Z <= 1e-3 && b.Max.Z >= -1e-3;
            bool tight = b.Min.X >= -5 - d - 1e-3 && b.Max.X <= 15 + d + 1e-3 && b.Min.Y >= -d - 1e-3 && b.Max.Y <= 15 + d + 1e-3;
            return (contains && tight, B3b(b));
        });
        // BE4: chord bound, parent within pi/2 of identity, child fixed at offset 10, point 5: reference (15,0,0), half-width
        // 2 sin(pi/4) x 15 = 21.2132 on every axis; dense random rotations within pi/2 must lie inside. Rejects dropping the
        // parent's term (half-width 0).
        Case(check, "part3b K-BE4: chord box = (15,0,0) +- 21.2132, containing sampled rotations within pi/2", () =>
        {
            var chain = new[] { new ChordLink(Quaternion.Identity, Vector3.Zero, MathF.PI / 2, 0), new ChordLink(Quaternion.Identity, new Vector3(10, 0, 0), 0, 0) };
            var b = BlendEnvelope.ChordBox(chain, new Vector3(5, 0, 0));
            double hw = 2 * Math.Sin(Math.PI / 4) * 15;
            bool exact = Near3b(b.Min.X, 15 - hw, 1e-2) && Near3b(b.Max.X, 15 + hw, 1e-2) && Near3b(b.Max.Y, hw, 1e-2) && Near3b(b.Min.Z, -hw, 1e-2);
            var rng = new Random(7); bool all = true;
            for (int i = 0; i < 2000 && all; i++)
            {
                var ax = Vector3.Normalize(new Vector3((float)rng.NextDouble() - .5f, (float)rng.NextDouble() - .5f, (float)rng.NextDouble() - .5f));
                var q = Quaternion.CreateFromAxisAngle(ax, (float)(rng.NextDouble() * Math.PI / 2));
                all = BoxContains3b(b, Vector3.Transform(new Vector3(15, 0, 0), q));
            }
            return (exact && all, B3b(b));
        });
        // BE5: an angle bound >= pi is the whole sphere: half-width 2 x 15 = 30.
        Case(check, "part3b K-BE5: chord angle 4 rad -> the sphere, half-width 30", () =>
        {
            var b = BlendEnvelope.ChordBox(new[] { new ChordLink(Quaternion.Identity, Vector3.Zero, 4f, 0) }, new Vector3(15, 0, 0));
            return (Near3b(b.Max.X, 45, 1e-2) && Near3b(b.Min.X, -15, 1e-2) && Near3b(b.Max.Y, 30, 1e-2), B3b(b));
        });

        // ================= PR: path hulls =================
        // PR1: quadratic keys (0,0,0)@0 -> (10,0,0)@2, tangents F0 = (0,30,0), B1 = (0,-30,0). Per segment the curve bulges to y 7.5
        // (Bezier controls at y 10); read per second (x 2) to y 15 (controls at y 20). The hull must contain dense samples of both
        // readings and stay within y 20. Rejects the key points only (y 0).
        Case(check, "part3b K-PR1: quadratic bulge: hull contains both tangent readings (y to 15), y max <= 20", () =>
        {
            var keys = new[] { new PathKey(0, Vector3.Zero, new Vector3(0, 30, 0), Vector3.Zero, 0, 0, 0), new PathKey(2, new Vector3(10, 0, 0), Vector3.Zero, new Vector3(0, -30, 0), 0, 0, 0) };
            var b = PathReach.PathHull(keys, "QUADRATIC_KEY", out var why);
            bool all = true;
            foreach (float k in new[] { 1f, 2f })
                for (int i = 0; i <= 100; i++)
                {
                    float t = i / 100f, h00 = 2 * t * t * t - 3 * t * t + 1, h10 = t * t * t - 2 * t * t + t, h01 = -2 * t * t * t + 3 * t * t, h11 = t * t * t - t * t;
                    var p = h00 * keys[0].Value + h10 * k * keys[0].Forward + h01 * keys[1].Value + h11 * k * keys[1].Backward;
                    all &= BoxContains3b(b, p);
                }
            return (why is null && all && b.Max.Y <= 20 + 1e-3, $"{B3b(b)} {why}");
        });
        // PR2: linear keys: exactly the key points' box.
        Case(check, "part3b K-PR2: linear keys -> the key points' box", () =>
        {
            var keys = new[] { new PathKey(0, new Vector3(1, 2, 3), default, default, 0, 0, 0), new PathKey(1, new Vector3(-4, 5, 0), default, default, 0, 0, 0) };
            var b = PathReach.PathHull(keys, "LINEAR_KEY", out var why);
            return (why is null && Near3b(b.Min.X, -4) && Near3b(b.Max.X, 1) && Near3b(b.Min.Y, 2) && Near3b(b.Max.Y, 5) && Near3b(b.Min.Z, 0) && Near3b(b.Max.Z, 3), B3b(b));
        });
        // PR3: an unknown key type refuses by name.
        Case(check, "part3b K-PR3: key type XYZ_ROTATION_KEY refuses by name", () =>
        {
            var b = PathReach.PathHull(new[] { new PathKey(0, Vector3.Zero, default, default, 0, 0, 0) }, "XYZ_ROTATION_KEY", out var why);
            return (b.IsEmpty && (why ?? "").Contains("XYZ_ROTATION_KEY"), why ?? "");
        });
        // PR4: percent values inside [0,1] keep the node on the path; 1.2 refuses, naming it.
        Case(check, "part3b K-PR4: percent [0,0.5,1] ok; [0,1.2] refused naming 1.2", () =>
        {
            var ok = PathReach.PercentRefusal(new[] { 0f, 0.5f, 1f }); var bad = PathReach.PercentRefusal(new[] { 0f, 1.2f });
            return (ok is null && (bad ?? "").Contains("1.2"), $"{ok} / {bad}");
        });

        // ================= PM: moving emitters, settled gravity, projectile range =================
        // PM1: a translating emitter: origins x [0,100] grown by offsets +-10.
        Case(check, "part3b K-PM1: translating emitter -> x[-10,110] y[-10,10] z[-10,10]", () =>
        {
            var o = new Aabb(new Vec3(0, 0, 0), new Vec3(100, 0, 0), false); var off = new Aabb(new Vec3(-10, -10, -10), new Vec3(10, 10, 10), false);
            var b = ParticleMotionReach.MovingEmitterBox(o, off, false, Quaternion.Identity, 0);
            return (Near3b(b.Min.X, -10) && Near3b(b.Max.X, 110) && Near3b(b.Min.Y, -10) && Near3b(b.Max.Z, 10), B3b(b));
        });
        // PM2: a turning emitter with a one-sided offset box x [0,50] (+-1 on y, z): the sphere of the farthest corner,
        // sqrt(2502) = 50.02. Rejects the offsets left in the stored frame (y would stay +-1).
        Case(check, "part3b K-PM2: turning emitter -> origins grown by 50.02 on every axis", () =>
        {
            var o = new Aabb(new Vec3(0, 0, 0), new Vec3(100, 0, 0), false); var off = new Aabb(new Vec3(0, -1, -1), new Vec3(50, 1, 1), false);
            var b = ParticleMotionReach.MovingEmitterBox(o, off, true, Quaternion.Identity, 0);
            double r = Math.Sqrt(2502);
            return (Near3b(b.Min.X, -r, 1e-2) && Near3b(b.Max.X, 100 + r, 1e-2) && Near3b(b.Min.Y, -r, 1e-2) && Near3b(b.Max.Z, r, 1e-2), B3b(b));
        });
        // PM3: a still frame turned 90 deg about z carries offsets x [0,50] onto y [0,50].
        Case(check, "part3b K-PM3: non-turning emitter frame 90 deg about z: offsets onto y [0,50]", () =>
        {
            var o = new Aabb(new Vec3(0, 0, 0), new Vec3(100, 0, 0), false); var off = new Aabb(new Vec3(0, -1, -1), new Vec3(50, 1, 1), false);
            var b = ParticleMotionReach.MovingEmitterBox(o, off, false, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), 0);
            return (Near3b(b.Min.Y, 0, 1e-2) && Near3b(b.Max.Y, 50, 1e-2) && Near3b(b.Min.X, -1, 1e-2) && Near3b(b.Max.X, 101, 1e-2), B3b(b));
        });
        // PM4: inherited travel 5 grows every axis by 5.
        Case(check, "part3b K-PM4: inherited travel 5 grows every axis by 5", () =>
        {
            var o = new Aabb(new Vec3(0, 0, 0), new Vec3(0, 0, 0), false); var off = new Aabb(new Vec3(-1, -1, -1), new Vec3(1, 1, 1), false);
            var b = ParticleMotionReach.MovingEmitterBox(o, off, false, Quaternion.Identity, 5);
            return (Near3b(b.Min.X, -6) && Near3b(b.Max.Z, 6), B3b(b));
        });
        // SA1: unsettled: v2's symmetric bound v T + r + g T^2/2 (no drag).
        double v = 312, T = 6.666667, r = 1, g = 180;
        Case(check, "part3b K-SA1: unsettled gravity -> symmetric v T + r + g T^2/2", () =>
        {
            var (lo, hi) = ParticleMotionReach.SettledAxis(v, T, r, 0, 0, g, null);
            double e = v * T + r + g * T * T / 2;
            return (Near3b(lo, -e, 1e-2) && Near3b(hi, e, 1e-2), $"{lo} {hi}");
        });
        // SA2: settled downward: the climb is the apex v^2/2g + r = 271.4 (v/g = 1.73 s <= T); the fall v T + r + g T^2/2.
        // Rejects the symmetric bound (today) and a one-sided v T climb.
        Case(check, "part3b K-SA2: settled toward -axis -> max = v^2/2g + r, min = -(v T + r + g T^2/2)", () =>
        {
            var (lo, hi) = ParticleMotionReach.SettledAxis(v, T, r, 0, 0, g, true);
            return (Near3b(hi, v * v / (2 * g) + r, 1e-2) && Near3b(lo, -(v * T + r + g * T * T / 2), 1e-2), $"{lo} {hi}");
        });
        // SA3: settled toward +axis: the mirror. Rejects a sign hard-wired to world down.
        Case(check, "part3b K-SA3: settled toward +axis mirrors SA2", () =>
        {
            var (lo, hi) = ParticleMotionReach.SettledAxis(v, T, r, 0, 0, g, false);
            return (Near3b(lo, -(v * v / (2 * g) + r), 1e-2) && Near3b(hi, v * T + r + g * T * T / 2, 1e-2), $"{lo} {hi}");
        });
        // SA4: a short life (v/g = 10 s > T = 2): the climb is v T - g T^2/2 + r = 180 + r.
        Case(check, "part3b K-SA4: life shorter than the time to apex -> v T - g T^2/2 + r", () =>
        {
            var (_, hi) = ParticleMotionReach.SettledAxis(100, 2, 1, 0, 0, 10, true);
            return (Near3b(hi, 181, 1e-6), $"{hi}");
        });
        // SA5: drag on the settled axis: the climb stays the drag-free apex (drag only shortens it); the fall uses v2's drag terms.
        Case(check, "part3b K-SA5: drag 0.5/s on the settled axis -> max apex, min -(v phi1 + r + g phi2)", () =>
        {
            double k = 0.5, phi1 = (1 - Math.Exp(-k * T)) / k, phi2 = T / k - (1 - Math.Exp(-k * T)) / (k * k);
            var (lo, hi) = ParticleMotionReach.SettledAxis(v, T, r, 0, k, g, true);
            return (Near3b(hi, v * v / (2 * g) + r, 1e-2) && Near3b(lo, -(v * phi1 + r + g * phi2), 1e-2), $"{lo} {hi}");
        });
        // RG1-3: projectile range min((v/g) sqrt(v^2 + 2 g h), v T).
        Case(check, "part3b K-RG1: range from the plane (h 0) = v^2/g = 540.8", () => (Near3b(ParticleMotionReach.ProjectileRange(v, g, 0, T), v * v / g, 1e-2), ""));
        Case(check, "part3b K-RG2: range from h 30 = (v/g) sqrt(v^2 + 2 g h) = 570.0", () =>
        { double e = v / g * Math.Sqrt(v * v + 2 * g * 30); return (Near3b(ParticleMotionReach.ProjectileRange(v, g, 30, T), e, 1e-2), $"{e}"); });
        Case(check, "part3b K-RG3: life-limited range = v T", () => (Near3b(ParticleMotionReach.ProjectileRange(10, 1, 0, 1), 10, 1e-6), ""));

        // ================= AM: absent model =================
        var absent = new[] { new AbsenceMeasurement("meshes\\A\\b.nif", "found only in an inactive mod", "frames M1") };
        Case(check, "part3b K-AM1: a measured absent model -> no_geometry naming the provider search and the frames", () =>
        {
            var x = MeshReach.OfAbsentModel("MESHES/a/B.NIF", absent, "REFUSAL");
            return (x.Status == "no_geometry" && (x.Reason ?? "").Contains("inactive mod") && (x.Reason ?? "").Contains("frames M1"), $"{x.Status} {x.Reason}");
        });
        Case(check, "part3b K-AM2: an absent model with no measurement keeps the refusal", () =>
        {
            var x = MeshReach.OfAbsentModel("meshes\\C\\d.nif", absent, "REFUSAL");
            return (x.Status == "refused" && x.Reason == "REFUSAL", $"{x.Status} {x.Reason}");
        });

        // ================= canary =================
        Case(check, "part3b CANARY (must FAIL): the two-link product box stops at y 10", () => (false, "y reaches 15 at a = 90, b = 0"));
    }
}
