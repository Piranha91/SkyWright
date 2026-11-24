using System.Numerics;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

namespace LeftBehind;

// THE LEFT-BEHIND RULE — was the geometry an occlusion panel was authored against still there in the city arm?
//
// Measure: sample the panel (a PortalBox's mid-quad on a 9 x 9 cell-centred grid; a Box's volume on 5 x 5 x 5), and
// for each sample and each arm list every backer within the largest delta of the sensitivity grid: a present or
// Unknown occluder reference whose world surface is that close (or whose closed solid contains the sample), and the
// terrain when the sample is at most delta above it.
//
// Classify (per thresholds): a sample is backed when its nearest backer is within delta; it is credited to that
// backer. b_B / b_C are the backed shares. Precedence:
//   CANNOT-ASSESS  the panel cannot be measured, a changed reference near it has no readable geometry, or the class
//                  differs between the readings "Unknown present" and "Unknown absent" (in either arm)
//   BACKING-GAINED b_B < tauPair and b_C >= tauPair: nothing behind the panel in the base arm, and the city arm built
//                  up to it. The gain is credited (gainedBy) to the city backers of the samples unbacked in B and
//                  backed in C, with how the city arm produced each. Evidence, not a threshold -- but checked: a gain
//                  credited to nothing the city arm changed is CANNOT-ASSESS, never emitted silently (2026-09-27)
//   UNPAIRED       b_B < tauPair
//   LEFT-BEHIND    lost >= tauLost, lost = share of B's backed samples unbacked in C
//   PARTIAL        tauMinor <= lost < tauLost
//   RE-BACKED      lost < tauMinor, and for >= tauLost of B's backed samples the credit moved to a city-new or
//                  base-swapped reference
//   INTACT         otherwise

public static class LeftBehindRule
{
    /// <summary>The largest delta any classification may ask for; backers are gathered out to this reach.</summary>
    public const float MaxDelta = 128f;
    /// <summary>A changed occluder-type reference with no readable geometry whose origin is this close to the panel's
    /// box makes the panel CANNOT-ASSESS.</summary>
    public const float BlindRadiusU = 1024f;
    /// <summary>A partner whose position differs by more than this, or whose rotation or scale differs at all, moved.</summary>
    public const float MoveThresholdU = 0.5f;
    const string LandKey = "LAND";

    /// <summary>The panel's sample points in the world, and the area or volume one sample stands for.</summary>
    public static (Vector3[] Points, float Weight, string Unit)? Samples(Panel p)
    {
        if (p.Type == "PortalBox")
        {
            var q = StationRule.MidQuad(new PrimitiveGeometry(p.Key.ToString(), PrimitiveKind.PortalBox, p.ExtentFull, p.Position, p.RotationRad, p.Worldspace, ""));
            var u = q[1] - q[0]; var v = q[3] - q[0];
            var pts = new Vector3[81];
            for (int i = 0; i < 9; i++) for (int j = 0; j < 9; j++) pts[i * 9 + j] = q[0] + u * ((i + 0.5f) / 9f) + v * ((j + 0.5f) / 9f);
            return (pts, u.Length() * v.Length() / 81f, "u2");
        }
        if (p.Type == "Box")
        {
            var c = StationRule.Corners(new PrimitiveGeometry(p.Key.ToString(), PrimitiveKind.Box, p.ExtentFull, p.Position, p.RotationRad, p.Worldspace, ""));
            var ax = c[1] - c[0]; var ay = c[2] - c[0]; var az = c[4] - c[0];
            var pts = new Vector3[125];
            int n = 0;
            for (int i = 0; i < 5; i++) for (int j = 0; j < 5; j++) for (int k = 0; k < 5; k++)
                pts[n++] = c[0] + ax * ((i + 0.5f) / 5f) + ay * ((j + 0.5f) / 5f) + az * ((k + 0.5f) / 5f);
            return (pts, ax.Length() * ay.Length() * az.Length() / 125f, "u3");
        }
        return null;
    }

    static Aabb BoxOf(IEnumerable<Vector3> pts) { var b = Aabb.Empty; foreach (var p in pts) b = b.Add(p); return b; }

    static bool SamePose(RefState a, RefState b) =>
        PlacementHistory.Dist(a.Position, b.Position) <= MoveThresholdU
        && MathF.Abs(a.Rotation.X - b.Rotation.X) <= 1e-4f && MathF.Abs(a.Rotation.Y - b.Rotation.Y) <= 1e-4f
        && MathF.Abs(a.Rotation.Z - b.Rotation.Z) <= 1e-4f && MathF.Abs(a.Scale - b.Scale) <= 1e-3f;

    /// <summary>What became of reference <paramref name="key"/> in the city arm, relative to the base arm.</summary>
    public static (string Cause, float MoveU) Cause(FormKey key, LeftBehindArm b, LeftBehindArm c)
    {
        var sb = b.StateOf(key); var sc = c.StateOf(key);
        if (sc is null || sc.Deleted) return (PartnerCause.Deleted, 0);
        if (sc.Unknown) return (PartnerCause.UnknownEnable, 0);
        if (!sc.Present) return (sc.Enable.ViaParent ? PartnerCause.ParentDisabled : PartnerCause.Disabled, 0);
        if (sb is null) return (PartnerCause.Unchanged, 0);    // city-new: not a partner of B, reported for completeness
        if (sb.Base != sc.Base) return (PartnerCause.BaseSwapped, 0);
        if (!SamePose(sb, sc)) return (PartnerCause.Moved, PlacementHistory.Dist(sb.Position, sc.Position));
        if (b.SourceOf(sb.Base) != c.SourceOf(sc.Base)) return (PartnerCause.MeshReplaced, 0);
        return (PartnerCause.Unchanged, 0);
    }

    static bool Changed(FormKey key, LeftBehindArm b, LeftBehindArm c)
    {
        var sb = b.StateOf(key); var sc = c.StateOf(key);
        if (sb is null || sc is null) return sb is not null || sc is not null;
        return sb.Deleted != sc.Deleted || sb.Enable != sc.Enable || sb.Base != sc.Base || !SamePose(sb, sc);
    }

    static string PanelDiff(Panel a, Panel b)
    {
        var d = new List<string>();
        if (a.Type != b.Type) d.Add($"type {a.Type} -> {b.Type}");
        if (Vector3.Distance(a.Position, b.Position) > MoveThresholdU) d.Add($"moved {Vector3.Distance(a.Position, b.Position):0.#} u");
        if (Vector3.Distance(a.RotationRad, b.RotationRad) > 1e-4f) d.Add("rotated");
        if (Vector3.Distance(a.ExtentFull, b.ExtentFull) > 0.01f) d.Add("resized");
        return string.Join(", ", d);
    }

    /// <summary>Measure one panel in both arms. <paramref name="cityPanel"/> is the city arm's version of the panel
    /// (null when the city arm does not place it with a primitive).</summary>
    public static PanelMeasure Measure(Panel basePanel, Panel? cityPanel, LeftBehindArm b, LeftBehindArm c, float maxDelta = MaxDelta)
    {
        var s = Samples(basePanel);
        PanelMeasure Refuse(string why) => new()
        {
            Panel = basePanel, SampleCount = 0, SampleWeight = 0, WeightUnit = basePanel.Type == "Box" ? "u3" : "u2",
            BaseBackers = Array.Empty<List<Backer>>(), CityBackers = Array.Empty<List<Backer>>(), Refusal = why,
        };
        if (s is null) return Refuse($"shape not modelled: {basePanel.Type}");
        var pb = b.StateOf(basePanel.Key);
        if (pb is null || pb.Deleted || !pb.Present)
            return Refuse(pb is { Unknown: true } ? "panel's own enable chain unresolved in the base arm" : "panel not present at load in the base arm");
        var pc = c.StateOf(basePanel.Key);
        if (pc is null || pc.Deleted || cityPanel is null) return Refuse("panel itself changed by the city arm: removed or deleted");
        if (pc.Unknown) return Refuse("panel itself changed by the city arm: its enable chain is unresolved there");
        if (!pc.Present) return Refuse("panel itself changed by the city arm: not present at load");
        var diff = PanelDiff(basePanel, cityPanel);
        if (diff.Length > 0) return Refuse($"panel itself changed by the city arm: {diff}");

        var (pts, weight, unit) = s.Value;
        var box = BoxOf(pts);
        var ws = basePanel.WorldspaceKey;
        List<Backer>[] Gather(LeftBehindArm arm)
        {
            var near = arm.OccludersNear(ws, box, maxDelta).ToList();
            var states = near.ToDictionary(k => k, k => arm.StateOf(k)!);
            var res = new List<Backer>[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                var l = res[i] = new List<Backer>();
                foreach (var k in near)
                    if (arm.DistanceTo(k, pts[i], maxDelta) is { } d) l.Add(new Backer(k, d, states[k].Unknown));
                if (arm.TerrainZ(ws, pts[i].X, pts[i].Y) is { } tz)
                {
                    float d = MathF.Max(0f, pts[i].Z - tz);
                    if (d <= maxDelta) l.Add(new Backer(null, d, false));
                }
            }
            return res;
        }
        var m = new PanelMeasure
        {
            Panel = basePanel, SampleCount = pts.Length, SampleWeight = weight, WeightUnit = unit,
            BaseBackers = Gather(b), CityBackers = Gather(c),
        };
        foreach (var k in b.BlindNear(ws, box, BlindRadiusU).Concat(c.BlindNear(ws, box, BlindRadiusU)).Distinct())
            if (Changed(k, b, c)) m.BlindChanged.Add(k);

        // The terrain field: does the LAND under the panel differ between the arms?
        string terrain = "not compared (unchanged by the city plugin)";
        foreach (var g in LeftBehindArm.GridsUnder(box))
        {
            b.Land.TryGetValue((ws, g.X, g.Y), out var hb); c.Land.TryGetValue((ws, g.X, g.Y), out var hc);
            bool hasB = b.Land.ContainsKey((ws, g.X, g.Y)), hasC = c.Land.ContainsKey((ws, g.X, g.Y));
            if ((hasB && hb is null) || (hasC && hc is null)) { terrain = "cannot assess: a LAND record under the panel could not be decoded"; break; }
            if (!SameLand(hb, hc)) terrain = "compared";
        }
        m.Terrain = terrain;
        return m;
    }

    static bool SameLand(float[,]? a, float[,]? b)
    {
        if (a is null || b is null) return a is null && b is null;
        for (int x = 0; x < 33; x++) for (int y = 0; y < 33; y++) if (a[x, y] != b[x, y]) return false;
        return true;
    }

    /// <summary>Every panel of <paramref name="panelPlugin"/> in the base arm, measured and classified — the one path the
    /// real run and the self-test share.</summary>
    public static List<PanelVerdict> Run(LeftBehindArm b, LeftBehindArm c, string panelPlugin, Thresholds t)
        => MeasureAll(b, c, panelPlugin).Select(m => Classify(m, b, c, t)).ToList();

    public static List<PanelMeasure> MeasureAll(LeftBehindArm b, LeftBehindArm c, string panelPlugin, Action? advance = null)
    {
        var list = new List<PanelMeasure>();
        foreach (var p in b.PanelsOf(panelPlugin)) { list.Add(Measure(p, c.PanelOf(p.Key), b, c)); advance?.Invoke(); }
        return list;
    }

    /// <summary>One reading: per sample, the credited backer key (null = unbacked).</summary>
    static string?[] Credit(List<Backer>[] backers, float delta, bool unknownPresent)
    {
        var r = new string?[backers.Length];
        for (int i = 0; i < backers.Length; i++)
        {
            Backer? best = null;
            foreach (var x in backers[i])
            {
                if (x.Distance > delta || (x.Unknown && !unknownPresent)) continue;
                if (best is null || x.Distance < best.Value.Distance
                    || (x.Distance == best.Value.Distance && string.CompareOrdinal(KeyOf(x), KeyOf(best.Value)) < 0)) best = x;
            }
            r[i] = best is null ? null : KeyOf(best.Value);
        }
        return r;
    }

    static string KeyOf(Backer x) => x.Ref?.ToString() ?? LandKey;

    sealed record Reading(string Class, float BB, float BC, float Lost, int LostCount, string?[] CB, string?[] CC, float ReShare);

    static Reading Evaluate(PanelMeasure m, LeftBehindArm b, LeftBehindArm c, Thresholds t, bool ub, bool uc)
    {
        var cb = Credit(m.BaseBackers, t.DeltaU, ub);
        var cc = Credit(m.CityBackers, t.DeltaU, uc);
        int n = m.SampleCount, backedB = 0, backedC = 0, lost = 0, re = 0;
        for (int i = 0; i < n; i++)
        {
            if (cc[i] is not null) backedC++;
            if (cb[i] is null) continue;
            backedB++;
            if (cc[i] is null) { lost++; continue; }
            if (cc[i] != cb[i] && cc[i] != LandKey && IsNewOrSwapped(cc[i]!, b, c)) re++;
        }
        float bB = n == 0 ? 0 : (float)backedB / n, bC = n == 0 ? 0 : (float)backedC / n;
        float lostShare = backedB == 0 ? 0 : (float)lost / backedB, reShare = backedB == 0 ? 0 : (float)re / backedB;
        string cls = bB < t.TauPair ? (bC >= t.TauPair ? PanelClass.BackingGained : PanelClass.Unpaired)
                   : lostShare >= t.TauLost ? PanelClass.LeftBehind
                   : lostShare >= t.TauMinor ? PanelClass.Partial
                   : reShare >= t.TauLost ? PanelClass.ReBacked
                   : PanelClass.Intact;
        return new Reading(cls, bB, bC, lostShare, lost, cb, cc, reShare);
    }

    static bool IsNewOrSwapped(string key, LeftBehindArm b, LeftBehindArm c)
    {
        var fk = FormKey.Factory(key);
        var sb = b.StateOf(fk); var sc = c.StateOf(fk);
        return sb is null || (sc is not null && sb.Base != sc.Base);
    }

    /// <summary>Distinct Unknown references among the backers within delta, in either arm.</summary>
    static int UnknownBackers(PanelMeasure m, float delta)
        => m.BaseBackers.Concat(m.CityBackers).SelectMany(l => l).Where(x => x.Unknown && x.Distance <= delta)
            .Select(x => x.Ref).Distinct().Count();

    /// <summary>Classify a measured panel under one set of thresholds.</summary>
    public static PanelVerdict Classify(PanelMeasure m, LeftBehindArm b, LeftBehindArm c, Thresholds t)
    {
        var none = Array.Empty<Partner>();
        if (m.Refusal is not null)
            return new PanelVerdict(m.Panel, PanelClass.CannotAssess, m.Refusal, 0, 0, 0, 0, m.WeightUnit, none, m.Terrain, 0);
        int unknown = UnknownBackers(m, t.DeltaU);
        if (m.BlindChanged.Count > 0)
            return new PanelVerdict(m.Panel, PanelClass.CannotAssess,
                $"a changed reference near the panel has no readable geometry: {string.Join(", ", m.BlindChanged.Select(k => k.ToString()).OrderBy(x => x, StringComparer.Ordinal))}",
                0, 0, 0, 0, m.WeightUnit, none, m.Terrain, unknown);

        var readings = new[] { (true, true), (true, false), (false, true), (false, false) }
            .Select(r => Evaluate(m, b, c, t, r.Item1, r.Item2)).ToList();
        var r0 = readings[0];
        var partners = Partners(m, b, c, r0);
        if (readings.Select(r => r.Class).Distinct().Count() > 1)
        {
            var unk = m.BaseBackers.Concat(m.CityBackers).SelectMany(l => l).Where(x => x.Unknown && x.Distance <= t.DeltaU)
                .Select(x => x.Ref!.Value.ToString()).Distinct().OrderBy(x => x, StringComparer.Ordinal);
            return new PanelVerdict(m.Panel, PanelClass.CannotAssess,
                $"backing depends on an unresolved enable chain: {string.Join(", ", unk)} (classes {string.Join("/", readings.Select(r => r.Class))})",
                r0.BB, r0.BC, r0.Lost, r0.LostCount * m.SampleWeight, m.WeightUnit, partners, m.Terrain, unknown);
        }

        IReadOnlyList<Gain>? gained = null;
        if (r0.Class == PanelClass.BackingGained)
        {
            gained = Gains(m, b, c, r0);
            if (gained.Count == 0 || gained.All(g => g.How == GainHow.Unchanged))
                return new PanelVerdict(m.Panel, PanelClass.CannotAssess,
                    "backing gained in the city arm but credited to nothing the city arm changed: "
                    + (gained.Count == 0 ? "no credited backer" : string.Join(", ", gained.Select(g => $"{g.Ref} {g.How} {g.ShareOfGain:0.00}"))),
                    r0.BB, r0.BC, r0.Lost, r0.LostCount * m.SampleWeight, m.WeightUnit, partners, m.Terrain, unknown, gained);
        }
        string? reason = r0.Class switch
        {
            PanelClass.BackingGained => $"backed share {r0.BB:0.00} in the base arm (below tauPair {t.TauPair:0.00}) rose to {r0.BC:0.00} in the city arm; "
                                        + "gained from " + string.Join(", ", gained!.Take(3).Select(g => $"{g.Ref} ({g.How}, {g.ShareOfGain:0.00})")),
            PanelClass.Unpaired => $"backed share in the base arm {r0.BB:0.00} is below tauPair {t.TauPair:0.00}: nothing to pair the panel with by this rule",
            PanelClass.ReBacked => $"{r0.ReShare:0.00} of the base arm's backing is now credited to a reference the city arm added or base-swapped"
                                   + (partners.FirstOrDefault(p => p.ReplacedBy is not null) is { } rp ? $" (e.g. {rp.Ref} -> {rp.ReplacedBy})" : ""),
            PanelClass.LeftBehind or PanelClass.Partial => DominantLoss(m, r0, partners),
            _ => null,
        };
        if (unknown > 0) reason = (reason is null ? "" : reason + "; ") + $"{unknown} backer(s) with an unresolved enable chain, class unchanged under both readings";
        return new PanelVerdict(m.Panel, r0.Class, reason, r0.BB, r0.BC, r0.Lost, r0.LostCount * m.SampleWeight, m.WeightUnit,
                                partners, m.Terrain, unknown, gained);
    }

    static string DominantLoss(PanelMeasure m, Reading r, IReadOnlyList<Partner> partners)
    {
        var lostBy = new Dictionary<string, int>();
        for (int i = 0; i < m.SampleCount; i++)
            if (r.CB[i] is { } k && r.CC[i] is null) lostBy[k] = lostBy.GetValueOrDefault(k) + 1;
        if (lostBy.Count == 0) return "lost backing";
        var top = lostBy.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).First();
        var p = partners.FirstOrDefault(x => x.Ref == top.Key);
        return $"lost {r.Lost:0.00} of the base arm's backing; most from {top.Key} ({p?.Cause ?? "?"}, {top.Value} samples)";
    }

    /// <summary>How the city arm produced backer <paramref name="key"/> (a FormKey string, or LAND) for a sample the base
    /// arm left unbacked.</summary>
    public static string HowGained(string key, PanelMeasure m, LeftBehindArm b, LeftBehindArm c)
    {
        if (key == LandKey) return m.Terrain == "compared" ? GainHow.Terrain : GainHow.Unchanged;
        var fk = FormKey.Factory(key);
        var sb = b.StateOf(fk); var sc = c.StateOf(fk);
        if (sb is null) return GainHow.New;
        if (sc is null) return GainHow.Unchanged;
        if (sb.Unknown || sc.Unknown) return GainHow.UnknownEnable;
        if (!sb.Present && sc.Present) return sb.Enable.ViaParent ? GainHow.ParentEnabled : GainHow.Enabled;
        if (sb.Base != sc.Base) return GainHow.BaseSwapped;
        if (!SamePose(sb, sc)) return GainHow.Moved;
        if (b.SourceOf(sb.Base) != c.SourceOf(sc.Base)) return GainHow.MeshReplaced;
        return GainHow.Unchanged;
    }

    /// <summary>The gained samples (unbacked in the base arm, backed in the city arm, reading 0), grouped by city backer,
    /// largest share first.</summary>
    static IReadOnlyList<Gain> Gains(PanelMeasure m, LeftBehindArm b, LeftBehindArm c, Reading r)
    {
        var idx = Enumerable.Range(0, m.SampleCount).Where(i => r.CB[i] is null && r.CC[i] is not null).ToList();
        if (idx.Count == 0) return Array.Empty<Gain>();
        var list = new List<Gain>();
        foreach (var g in idx.GroupBy(i => r.CC[i]!).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            float share = (float)g.Count() / idx.Count;
            string how = HowGained(g.Key, m, b, c);
            if (g.Key == LandKey) { list.Add(new Gain(LandKey, null, null, share, how, "terrain")); continue; }
            var sc = c.StateOf(FormKey.Factory(g.Key))!;
            string? edid = c.Index.BaseInfo.TryGetValue(sc.Base, out var bi) ? bi.Edid : null;
            list.Add(new Gain(g.Key, sc.Base.ToString(), edid, share, how, c.GeometryLabel(sc.Base)));
        }
        return list;
    }

    static IReadOnlyList<Partner> Partners(PanelMeasure m, LeftBehindArm b, LeftBehindArm c, Reading r)
    {
        int backed = r.CB.Count(x => x is not null);
        if (backed == 0) return Array.Empty<Partner>();
        var list = new List<Partner>();
        foreach (var g in Enumerable.Range(0, m.SampleCount).Where(i => r.CB[i] is not null).GroupBy(i => r.CB[i]!)
                     .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            float share = (float)g.Count() / backed;
            var repl = g.Where(i => r.CC[i] is not null && r.CC[i] != g.Key).GroupBy(i => r.CC[i]!)
                        .OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal).FirstOrDefault()?.Key;
            if (g.Key == LandKey)
            {
                list.Add(new Partner(LandKey, null, null, share, m.Terrain == "compared" ? PartnerCause.Terrain : PartnerCause.Unchanged,
                                     0, repl, "terrain"));
                continue;
            }
            var fk = FormKey.Factory(g.Key);
            var sb = b.StateOf(fk)!;
            var (cause, moveU) = Cause(fk, b, c);
            string? edid = b.Index.BaseInfo.TryGetValue(sb.Base, out var bi) ? bi.Edid : null;
            list.Add(new Partner(g.Key, sb.Base.ToString(), edid, share, cause, moveU, repl, b.GeometryLabel(sb.Base)));
        }
        return list;
    }
}
