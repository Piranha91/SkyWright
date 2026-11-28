using System.Globalization;
using System.Text.Json.Nodes;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace HarnessShared;

// ISLANDS IN THE NAVMESH INFO MAP (task 2026-11-16-navmesh-repair, R7; design C:/hc-runs/2026-11-27-navmesh-repair/GATE-A.md
// section 4, Gate B r7/GATE-B.md and its review). An entry of NAVI may hold island data: a box (Min, Max) and a small mesh of
// its own, which is the Creation Kit's simplified outline of the navmesh, not a copy of it (its vertices are the navmesh's in
// 14 of Skyrim.esm's 8,664 islands). Its first Unknown is 32 exactly when it holds island data, and an island's box is, bit for
// bit, the box its navmesh stores: both on every entry of the base game's five files (tests/navi_floor.py). The rules, and
// their one owner:
//   - an edit that leaves the navmesh's geometry untouched (a relink, a door) leaves the island data untouched;
//   - use_version carries the version plugin's OWN entry's flag and island data whole; refused by name when that plugin
//     states no entry and the entry the game takes is an island, or when the version's island box is not its own navmesh's;
//     two plain entries stay as they are, whatever their first Unknowns;
//   - a height-only move (follow the ground, a z-only vertex move) makes the box the navmesh's rebuilt box and moves each
//     island vertex in height by what the navmesh surface under it moved (FollowHeights); an empty mesh stays empty;
//   - a plan-changing edit (a cut, triangles added or removed, a sideways move) on an island with an empty mesh makes the box
//     the rebuilt box; on one with a mesh it is refused by name (the outline's rule is not known).
// The height rule is a design choice, not a measured law: the base game's island vertices stand off the navmesh by up to
// 128 u and more, so no tight relation could be measured; it keeps each vertex as far off the surface as its author left it.

/// <summary>An entry's island data: its box and its own mesh (vertices, triangles as vertex-index triples).</summary>
public sealed record NavIslandData(Vec3 Min, Vec3 Max, IReadOnlyList<Vec3> Vertices, IReadOnlyList<(short A, short B, short C)> Triangles)
{
    public bool EmptyMesh => Vertices.Count == 0 && Triangles.Count == 0;
}

/// <summary>What the island rules read and set on one entry: its first Unknown (the island flag, 32) and its island data
/// (null: not an island).</summary>
public sealed record NavIslandEntry(int Unknown, NavIslandData? Island);

/// <summary>How an edit touches a navmesh's geometry, for the island rules.</summary>
public enum NavIslandEdit { Untouched, HeightOnly, PlanChanging }

public static class NavIsland
{
    /// <summary>The first Unknown of an island entry in the base game (and only of one).</summary>
    public const int IslandFlag = 32;

    static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    static string V(Vec3 v) => $"{F(v.X)},{F(v.Y)},{F(v.Z)}";
    static string Show(Vec3 v) => $"({F(v.X)}, {F(v.Y)}, {F(v.Z)})";
    static bool Bits(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    static bool Bits(Vec3 a, Vec3 b) => Bits(a.X, b.X) && Bits(a.Y, b.Y) && Bits(a.Z, b.Z);

    /// <summary>An entry's island state, read from Mutagen's getter.</summary>
    public static NavIslandEntry Of(INavigationMapInfoGetter e) => new(e.Unknown, e.Island is { } i
        ? new NavIslandData(new Vec3(i.Min.X, i.Min.Y, i.Min.Z), new Vec3(i.Max.X, i.Max.Y, i.Max.Z),
                            i.Vertices.Select(v => new Vec3(v.X, v.Y, v.Z)).ToList(), i.Triangles.Select(t => (t.X, t.Y, t.Z)).ToList())
        : null);

    /// <summary>Two island datas the same, every float bit for bit and every vertex and triangle in order (null equals null).</summary>
    public static bool Same(NavIslandData? a, NavIslandData? b) => a is null || b is null ? a is null && b is null
        : Bits(a.Min, b.Min) && Bits(a.Max, b.Max) && a.Vertices.Count == b.Vertices.Count && a.Vertices.Zip(b.Vertices).All(z => Bits(z.First, z.Second))
          && a.Triangles.SequenceEqual(b.Triangles);

    public static bool Same(NavIslandEntry a, NavIslandEntry b) => a.Unknown == b.Unknown && Same(a.Island, b.Island);

    /// <summary>The island data an edit of kind <paramref name="kind"/> leaves on <paramref name="key"/>'s entry, from the
    /// navmesh before and after it; or the one-sentence refusal. A navmesh that is no island (<paramref name="before"/> null)
    /// stays none.</summary>
    public static (NavIslandData? After, string? Refused) ForEdit(FormKey key, NavIslandData? before, NavmeshGeometry navBefore,
                                                                  NavmeshGeometry navAfter, NavIslandEdit kind)
    {
        if (before is null) return (null, null);
        switch (kind)
        {
            case NavIslandEdit.Untouched:
                return (before, null);
            case NavIslandEdit.HeightOnly:
                return (before with { Min = navAfter.Min, Max = navAfter.Max, Vertices = FollowHeights(before.Vertices, navBefore, navAfter) }, null);
            default:
                if (before.EmptyMesh) return (before with { Min = navAfter.Min, Max = navAfter.Max }, null);
                return (null, $"{key} is an island in the navmesh info map with its own outline mesh of {before.Vertices.Count} vertices, which this edit "
                            + "would leave wrong and whose rule the Creation Kit does not publish; move vertices in height only, or carry a version that "
                            + "comes with its own island data (use_version).");
        }
    }

    /// <summary>THE HEIGHT RULE. Each island vertex (x, y, z) moves in height by what the navmesh surface under it moved between
    /// <paramref name="before"/> and <paramref name="after"/> (the same triangles, heights changed): at (x, y) on the triangle
    /// of <paramref name="before"/> that holds it in plan (of several, the one whose plane there is nearest z), else at the
    /// nearest point of any triangle edge in plan (of ties, the nearest in height). x and y stay. tests/test_navmesh_repair_rules.py
    /// states the same rule apart from this code (heights_by_rule) and holds a written patch to it.</summary>
    public static IReadOnlyList<Vec3> FollowHeights(IReadOnlyList<Vec3> island, NavmeshGeometry before, NavmeshGeometry after)
    {
        var B = before.Vertices; var A = after.Vertices;
        var outp = new List<Vec3>(island.Count);
        foreach (var p in island)
        {
            double x = p.X, y = p.Y, z = p.Z;
            (int I, double W)[]? best = null; double bestK = double.MaxValue;
            foreach (var t in before.Triangles)
            {
                Vec3 a = B[t.A], b = B[t.B], c = B[t.C];
                double den = ((double)b.Y - c.Y) * ((double)a.X - c.X) + ((double)c.X - b.X) * ((double)a.Y - c.Y);
                if (den == 0) continue;
                double l1 = (((double)b.Y - c.Y) * (x - c.X) + ((double)c.X - b.X) * (y - c.Y)) / den;
                double l2 = (((double)c.Y - a.Y) * (x - c.X) + ((double)a.X - c.X) * (y - c.Y)) / den;
                double l3 = 1 - l1 - l2;
                if (l1 < -1e-9 || l2 < -1e-9 || l3 < -1e-9) continue;
                double k = Math.Abs(l1 * a.Z + l2 * b.Z + l3 * c.Z - z);
                if (k < bestK) { bestK = k; best = new[] { ((int)t.A, l1), ((int)t.B, l2), ((int)t.C, l3) }; }
            }
            if (best is null)
            {
                (double D, double H) bk = (double.MaxValue, double.MaxValue);
                foreach (var t in before.Triangles)
                    foreach (var (i, j) in new[] { ((int)t.A, (int)t.B), ((int)t.B, (int)t.C), ((int)t.C, (int)t.A) })
                    {
                        Vec3 P = B[i], Q = B[j];
                        double ex = (double)Q.X - P.X, ey = (double)Q.Y - P.Y, L = ex * ex + ey * ey;
                        double s = L == 0 ? 0 : Math.Clamp(((x - P.X) * ex + (y - P.Y) * ey) / L, 0, 1);
                        double d = Math.Sqrt(Math.Pow(P.X + ex * s - x, 2) + Math.Pow(P.Y + ey * s - y, 2));
                        double h = Math.Abs(P.Z + ((double)Q.Z - P.Z) * s - z);
                        if (d < bk.D || (d == bk.D && h < bk.H)) { bk = (d, h); best = new[] { (i, 1 - s), (j, s) }; }
                    }
            }
            double dz = best is null ? 0 : best.Sum(w => w.W * ((double)A[w.I].Z - B[w.I].Z));
            outp.Add(new Vec3(p.X, p.Y, (float)(z + dz)));
        }
        return outp;
    }

    /// <summary>The entry use_version leaves for <paramref name="key"/>: the version plugin's own entry's flag and island data
    /// (<paramref name="version"/>, null when that plugin states none). Two plain entries stay as the game takes them, whatever
    /// their first Unknowns (review F1: R7 changes no use_version row that plans today). Refused by name when the plugin states
    /// no entry and the entry the game takes (<paramref name="taken"/>) is an island.</summary>
    public static (NavIslandEntry After, string? Refused) ForVersion(FormKey key, string versionPlugin, NavIslandEntry taken, NavIslandEntry? version)
    {
        if (version is null)
            return taken.Island is null ? (taken, null)
                : (taken, $"{versionPlugin} states no navmesh info map entry for {key}, so there is no island data for its version to carry; carry a version whose plugin states the entry, or keep the winner's.");
        if (taken.Island is null && version.Island is null) return (taken, null);
        return (version, null);
    }

    /// <summary>Why a version's own island data cannot be carried (review F2): its box is not, bit for bit, the box its own navmesh
    /// stores (<paramref name="navMin"/>, <paramref name="navMax"/>), as every island of the base game's is. One sentence, or null.</summary>
    public static string? VersionBoxRefusal(FormKey key, string versionPlugin, NavIslandData island, Vec3 navMin, Vec3 navMax)
        => Bits(island.Min, navMin) && Bits(island.Max, navMax) ? null
            : $"{versionPlugin}'s navmesh info map entry gives {key}'s island the box {Show(island.Min)}-{Show(island.Max)}, which is not the box "
              + $"{Show(navMin)}-{Show(navMax)} its own navmesh stores, so carrying it would leave the coarse map wrong; have {versionPlugin}'s navmesh saved "
              + "again in the Creation Kit, which rebuilds the box, or keep the winner's version.";

    /// <summary>The write ops that make entry <paramref name="index"/> of NAVI <paramref name="navi"/>'s winner go from
    /// <paramref name="before"/> to <paramref name="after"/>, in place: the island data set whole (or removed), then the flag set
    /// when it differs. None when nothing changes.</summary>
    public static List<RepairOp> Ops(string navi, int index, NavIslandEntry before, NavIslandEntry after)
    {
        var ops = new List<RepairOp>();
        if (!Same(before.Island, after.Island))
            ops.Add(after.Island is null ? new RepairOp(navi, $"MapInfos[{index}].Island", "Remove")
                                         : new RepairOp(navi, $"MapInfos[{index}].Island", "Set", ComposeNode: Compose(after.Island)));
        if (before.Unknown != after.Unknown)
            ops.Add(new RepairOp(navi, $"MapInfos[{index}].Unknown", "Set", Value: after.Unknown.ToString(CultureInfo.InvariantCulture)));
        return ops;
    }

    /// <summary>Island data as a housecarl_apply compose (an IslandData with its box and its mesh, in order). Every float in its
    /// round-trip text, so the written bits are the planned ones.</summary>
    public static JsonObject Compose(NavIslandData d)
    {
        var sets = new JsonArray();
        foreach (var v in d.Vertices) sets.Add(new JsonObject { ["path"] = "Vertices", ["verb"] = "Add", ["value"] = V(v) });
        foreach (var t in d.Triangles) sets.Add(new JsonObject { ["path"] = "Triangles", ["verb"] = "Add", ["value"] = $"{t.A},{t.B},{t.C}" });
        return new JsonObject
        {
            ["type"] = "IslandData",
            ["fields"] = new JsonObject { ["Min"] = V(d.Min), ["Max"] = V(d.Max) },
            ["sets"] = sets,
        };
    }

    /// <summary>Island data as Mutagen's (the read-back's expected entry).</summary>
    public static IslandData ToMutagen(NavIslandData d)
    {
        var i = new IslandData { Min = new P3Float(d.Min.X, d.Min.Y, d.Min.Z), Max = new P3Float(d.Max.X, d.Max.Y, d.Max.Z) };
        foreach (var v in d.Vertices) i.Vertices.Add(new P3Float(v.X, v.Y, v.Z));
        foreach (var (a, b, c) in d.Triangles) i.Triangles.Add(new P3Int16(a, b, c));
        return i;
    }

    /// <summary>An entry's flag and island data as a plan carries it (floats in round-trip text).</summary>
    public static JsonObject Json(NavIslandEntry e) => new()
    {
        ["unknown"] = e.Unknown,
        ["data"] = e.Island is not { } d ? null : new JsonObject
        {
            ["min"] = V(d.Min), ["max"] = V(d.Max),
            ["vertices"] = new JsonArray(d.Vertices.Select(v => (JsonNode)V(v)).ToArray()),
            ["triangles"] = new JsonArray(d.Triangles.Select(t => (JsonNode)$"{t.A},{t.B},{t.C}").ToArray()),
        },
    };

    public static NavIslandEntry FromJson(JsonNode j)
    {
        static Vec3 P(string s) { var p = s.Split(','); return new Vec3(float.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture), float.Parse(p[2], CultureInfo.InvariantCulture)); }
        var d = j["data"];
        return new NavIslandEntry((int)j["unknown"]!, d is null ? null : new NavIslandData(P((string)d["min"]!), P((string)d["max"]!),
            d["vertices"]!.AsArray().Select(x => P((string)x!)).ToList(),
            d["triangles"]!.AsArray().Select(x => { var p = ((string)x!).Split(','); return (short.Parse(p[0], CultureInfo.InvariantCulture), short.Parse(p[1], CultureInfo.InvariantCulture), short.Parse(p[2], CultureInfo.InvariantCulture)); }).ToList()));
    }

    /// <summary>The read-back of one edited navmesh's entry, as the game takes it with the patch last (<paramref name="taken"/>,
    /// from <paramref name="takenFrom"/>): its flag agreeing with the presence of island data, its island box bit for bit the box
    /// of the navmesh the game takes (<paramref name="navMin"/>, <paramref name="navMax"/>), and its flag and island data equal to
    /// <paramref name="expected"/>, vertex for vertex and triangle for triangle. One sentence per difference.</summary>
    public static List<string> Check(FormKey key, NavIslandEntry taken, string takenFrom, Vec3 navMin, Vec3 navMax, NavIslandEntry expected)
    {
        var d = new List<string>();
        bool flag = (taken.Unknown & IslandFlag) != 0;
        if (flag && taken.Island is null) d.Add($"{key}'s navmesh info map entry ({takenFrom}) has the island flag but no island data.");
        if (!flag && taken.Island is not null) d.Add($"{key}'s navmesh info map entry ({takenFrom}) holds island data without the island flag.");
        if (taken.Unknown != expected.Unknown) d.Add($"{key}'s navmesh info map entry ({takenFrom}) has the flag value {taken.Unknown}, where {expected.Unknown} was planned.");
        if ((taken.Island is null) != (expected.Island is null))
            d.Add($"{key}'s navmesh info map entry ({takenFrom}) {(taken.Island is null ? "has no island data, where the plan has some" : "has island data, where the plan has none")}.");
        if (taken.Island is { } t)
        {
            if (!Bits(t.Min, navMin) || !Bits(t.Max, navMax))
                d.Add($"{key}'s island box {Show(t.Min)}-{Show(t.Max)} is not, bit for bit, the box {Show(navMin)}-{Show(navMax)} of the navmesh the game takes.");
            if (expected.Island is { } e)
            {
                if (!Bits(t.Min, e.Min) || !Bits(t.Max, e.Max)) d.Add($"{key}'s island box {Show(t.Min)}-{Show(t.Max)} is not the planned {Show(e.Min)}-{Show(e.Max)}.");
                if (t.Vertices.Count != e.Vertices.Count) d.Add($"{key}'s island mesh has {t.Vertices.Count} vertices, where {e.Vertices.Count} were planned.");
                else if (Enumerable.Range(0, t.Vertices.Count).FirstOrDefault(i => !Bits(t.Vertices[i], e.Vertices[i]), -1) is var vi && vi >= 0)
                    d.Add($"{key}'s island vertex {vi} is {Show(t.Vertices[vi])}, where {Show(e.Vertices[vi])} was planned.");
                if (t.Triangles.Count != e.Triangles.Count) d.Add($"{key}'s island mesh has {t.Triangles.Count} triangles, where {e.Triangles.Count} were planned.");
                else if (Enumerable.Range(0, t.Triangles.Count).FirstOrDefault(i => t.Triangles[i] != e.Triangles[i], -1) is var ti && ti >= 0)
                    d.Add($"{key}'s island triangle {ti} is {t.Triangles[ti]}, where {e.Triangles[ti]} was planned.");
            }
        }
        return d;
    }
}
