using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

// PROOF — the floor filing and the LAND read against moved and deleted references (_shared/MovedRefFixture.cs).
//
// A reference's state is its LAST sighting in load order: a later plugin that moves it leaves no copy at the old grid,
// a deletion (with or without placement data) takes it out, and an unreadable LAND is named rather than read as "no
// LAND" (which would let an earlier plugin's heights stand). Every expectation is a literal worked out by hand from the
// fixture's table. The fixture is read through PlacementExtract in load order, as the probes read a real order.
//
// Only the adapter below (FloorOf) knows how FloorFeed is called; the expectations do not depend on it.
// MR_CANARY=1 plants one wrong expectation (C1's position, a passing control), which must fail. MR_FIXTURE_KEEP=<dir> keeps the plugins.
static class MovedRefProof
{
    const SkyrimRelease Rel = MovedRefFixture.Rel;

    sealed record FloorView(
        Dictionary<FormKey, Dictionary<(int, int), Dictionary<FormKey, PlacedRef>>> Refs,
        Dictionary<FormKey, Dictionary<(int, int), float[,]>> Land,
        HashSet<(FormKey Ws, int X, int Y)> UnreadableLand);

    /// <summary>The floor filing, driven the way a probe that files every exterior cell drives it: a FloorIndex over every
    /// cell of every plugin (LastSighting.Cells), in load order.</summary>
    static FloorView FloorOf(IReadOnlyList<string> paths)
    {
        var index = new FloorFeed.FloorIndex();
        foreach (var path in paths)
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(path, Rel);
            foreach (var cp in LastSighting.Cells(mod)) index.Observe(cp, Path.GetFileName(path));
        }
        return new FloorView(index.RefsByWorld, index.LandByWorld, index.UnreadableLand.ToHashSet());
    }

    static CellPlacements? CellOf(string path, FormKey cell)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(path, Rel);
        return PlacementExtract.FromMod(mod, includeEmpty: true, includeDeletions: true).FirstOrDefault(c => c.Cell == cell);
    }

    /// <summary>The LAND reason, read by name: the member is added by the fix, and this file compiles without it.</summary>
    static (bool Has, string? Reason) ReasonOf(CellPlacements cp)
    {
        var prop = typeof(CellPlacements).GetProperty("LandUnreadableReason");
        return prop is null ? (false, null) : (true, prop.GetValue(cp) as string);
    }

    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nPROOF — moved and deleted references: FloorFeed and the core LAND read (_shared/MovedRefFixture.cs)");
        bool canary = Environment.GetEnvironmentVariable("MR_CANARY") == "1";
        string? keep = Environment.GetEnvironmentVariable("MR_FIXTURE_KEEP") is { Length: > 0 } k ? Path.GetFullPath(k) : null;
        var root = keep ?? Path.Combine(Path.GetTempPath(), "mr-proof-" + Environment.ProcessId);
        if (keep is not null && Directory.Exists(keep) && Directory.EnumerateFileSystemEntries(keep).Any())
        { Check("moved-ref/fixture: MR_FIXTURE_KEEP folder is empty", false, keep); return; }
        try { Body(Check, MovedRefFixture.Write(root), canary); }
        catch (Exception ex) { Check("moved-ref/fixture", false, $"threw {ex.GetType().Name}: {ex.Message}"); }
        finally { if (keep is null) { try { Directory.Delete(root, true); } catch { } } else Console.WriteLine($"   fixture kept in {keep}"); }
    }

    static void Body(Action<string, bool, string> Check, IReadOnlyList<string> paths, bool canary)
    {
        string master = paths[0], a = paths[1], lp = paths[4];
        var F = MovedRefFixture.Refs;
        string Name(FormKey key) => F.FirstOrDefault(kv => kv.Value == key).Key ?? key.ToString();

        // ---- preconditions: the fixture is what its table says ----
        foreach (var (n, ok, d) in MovedRefFixture.Preconditions(paths)) Check("moved-ref/precondition: " + n, ok, d);
        {
            using var am = SkyrimMod.CreateFromBinaryOverlay(a, Rel);
            using var lm = SkyrimMod.CreateFromBinaryOverlay(lp, Rel);
            string where = "no throw";
            bool threw = false, noVhgt = false;
            foreach (var ws in lm.Worldspaces.Records)
                foreach (var blk in ws.SubCells) foreach (var sub in blk.Items) foreach (var c in sub.Items)
                    if (c.FormKey == MovedRefFixture.E00)
                    {
                        try { _ = c.Landscape!.VertexHeightMap!.HeightMap[32, 32]; } catch (Exception ex) { threw = true; where = ex.GetType().Name; }
                    }
            foreach (var ws in am.Worldspaces.Records)
                foreach (var blk in ws.SubCells) foreach (var sub in blk.Items) foreach (var c in sub.Items)
                    if (c.FormKey == MovedRefFixture.Em10) noVhgt = c.Landscape is not null && c.Landscape.VertexHeightMap is null;
            Check("moved-ref/precondition: L's E00 heightmap throws when read (L1)", threw, where);
            Check("moved-ref/precondition: A's Em10 LAND is present with no VHGT (L4)", noVhgt, "");
            var b0 = CellOf(a, MovedRefFixture.E10)?.Refs.FirstOrDefault(r => r.FormKey == MovedRefFixture.B0);
            Check("moved-ref/precondition: A's B0 is read with no base", b0 is not null && b0.Base is null, b0 is null ? "absent" : $"base {b0.Base}");
        }

        // ---- the core LAND reason (L cases), both directions ----
        {
            var l1 = CellOf(lp, MovedRefFixture.E00)!;
            var (has1, r1) = ReasonOf(l1);
            Check("moved-ref/L1  a present LAND whose heightmap cannot be read: named, and no heights",
                  has1 && r1 is not null && l1.Land is null, has1 ? $"reason '{r1}', land {(l1.Land is null ? "null" : "decoded")}" : "no LandUnreadableReason member");
            var lr = CellOf(master, MovedRefFixture.E00)!;
            var (hasR, rR) = ReasonOf(lr);
            Check("moved-ref/L-readable  a readable LAND: no reason, heights decoded",
                  hasR && rR is null && lr.Land is not null, hasR ? $"reason '{rR}', land {(lr.Land is null ? "null" : "decoded")}" : "no LandUnreadableReason member");
            var la = CellOf(a, MovedRefFixture.E10)!;
            var (hasA, rA) = ReasonOf(la);
            Check("moved-ref/L-absent  a cell override with no LAND: no reason, no heights",
                  hasA && rA is null && la.Land is null, hasA ? $"reason '{rA}', land {(la.Land is null ? "null" : "decoded")}" : "no LandUnreadableReason member");
            var l4 = CellOf(a, MovedRefFixture.Em10)!;
            var (has4, r4) = ReasonOf(l4);
            Check("moved-ref/L4  a present LAND with no VHGT: named (no trustworthy floor), no heights",
                  has4 && r4 is not null && l4.Land is null, has4 ? $"reason '{r4}'" : "no LandUnreadableReason member");
            var li = CellOf(a, MovedRefFixture.I2)!;
            var (hasI, rI) = ReasonOf(li);
            Check("moved-ref/L-interior  an interior: no reason", hasI && rI is null, hasI ? $"reason '{rI}'" : "no LandUnreadableReason member");
        }

        // ---- FloorFeed ----
        var fv = FloorOf(paths);
        var w = fv.Refs.GetValueOrDefault(MovedRefFixture.W) ?? new();
        var wLand = fv.Land.GetValueOrDefault(MovedRefFixture.W) ?? new();

        // Each reference at most once over every bucket of the worldspace.
        var seen = new Dictionary<FormKey, List<(int, int)>>();
        foreach (var (g, d) in w) foreach (var key in d.Keys) { if (!seen.TryGetValue(key, out var l)) seen[key] = l = new(); l.Add(g); }
        var dup = seen.Where(kv => kv.Value.Count > 1).Select(kv => $"{Name(kv.Key)} in {string.Join(" ", kv.Value.OrderBy(x => x))}").OrderBy(x => x).ToList();
        Check("moved-ref/F-once  no reference is filed in two grid buckets", dup.Count == 0, dup.Count == 0 ? "" : string.Join("; ", dup));

        var (nb, nbLand) = FloorFeed.Neighbourhood((0, 0), w, wLand);
        var got = new Dictionary<FormKey, PlacedRef>();
        var twice = new List<string>();
        foreach (var r in nb) { if (!got.TryAdd(r.FormKey, r)) twice.Add(Name(r.FormKey)); }
        var expect = MovedRefFixture.ExpectedFloor00.ToDictionary(kv => kv.Key, kv => kv.Value);
        var missing = expect.Keys.Where(key => !got.ContainsKey(key)).Select(Name).OrderBy(x => x).ToList();
        var extra = got.Keys.Where(key => !expect.ContainsKey(key)).Select(key => $"{Name(key)} at {got[key].Position.X:0},{got[key].Position.Y:0}").OrderBy(x => x).ToList();
        var wrong = expect.Where(kv => got.TryGetValue(kv.Key, out var r) && (MathF.Abs(r.Position.X - kv.Value.X) > 0.01f || MathF.Abs(r.Position.Y - kv.Value.Y) > 0.01f))
                          .Select(kv => $"{Name(kv.Key)} at {got[kv.Key].Position.X:0},{got[kv.Key].Position.Y:0}").OrderBy(x => x).ToList();
        Check("moved-ref/F-nb00  the (0,0) neighbourhood is exactly the hand-derived set, each once, at its last position",
              missing.Count == 0 && extra.Count == 0 && wrong.Count == 0 && twice.Count == 0,
              $"missing [{string.Join(", ", missing)}] extra [{string.Join(", ", extra)}] wrong [{string.Join(", ", wrong)}] twice [{string.Join(", ", twice)}]");

        // Named cases, so a failure says which kind of sighting was mishandled.
        bool In00(FormKey key) => got.ContainsKey(key);
        string At(FormKey key) => got.TryGetValue(key, out var r) ? $"at {r.Position.X:0},{r.Position.Y:0}" : "absent";
        Check("moved-ref/F-M1  moved to a far grid: not under (0,0)", !In00(MovedRefFixture.M1), At(MovedRefFixture.M1));
        Check("moved-ref/F-M2  moved to the next grid: once, at the new position", In00(MovedRefFixture.M2) && !twice.Contains("M2") && At(MovedRefFixture.M2) == "at 4200,100", At(MovedRefFixture.M2));
        Check("moved-ref/F-M3  moved to an interior: gone", !In00(MovedRefFixture.M3), At(MovedRefFixture.M3));
        Check("moved-ref/F-M4  moved to another worldspace at the same XY: gone from W", !In00(MovedRefFixture.M4), At(MovedRefFixture.M4));
        Check("moved-ref/F-M5  made persistent inside the band: at the new position", At(MovedRefFixture.M5) == "at 1400,1400", At(MovedRefFixture.M5));
        Check("moved-ref/F-M7  filed in a far CELL but standing in grid (0,0): present by position", At(MovedRefFixture.M7) == "at 700,700", At(MovedRefFixture.M7));
        Check("moved-ref/F-D1  deleted with no placement data: gone", !In00(MovedRefFixture.D1), At(MovedRefFixture.D1));
        Check("moved-ref/F-D2  deleted in a cell copy holding only the deletion: gone", !In00(MovedRefFixture.D2), At(MovedRefFixture.D2));
        Check("moved-ref/F-D3  deleted WITH placement data at another grid: gone from both grids", !In00(MovedRefFixture.D3), At(MovedRefFixture.D3));
        Check("moved-ref/F-D4  deleted, then placed far away: not under (0,0)", !In00(MovedRefFixture.D4), At(MovedRefFixture.D4));
        Check("moved-ref/F-B0  base cleared at another grid: not a floor candidate", !In00(MovedRefFixture.B0), At(MovedRefFixture.B0));
        Check("moved-ref/F-U2  deleted with no data after its introduction: gone", !In00(MovedRefFixture.U2), At(MovedRefFixture.U2));
        Check("moved-ref/F-C1  control: moved within E00, at the new position" + (canary ? " [CANARY: expectation planted wrong]" : ""),
              At(MovedRefFixture.C1) == (canary ? "at 999,999" : "at 1200,1100"), At(MovedRefFixture.C1));

        // The second worldspace holds what moved there, and only that.
        var w2 = fv.Refs.GetValueOrDefault(MovedRefFixture.W2) ?? new();
        var w2Keys = w2.Values.SelectMany(d => d.Keys).Select(Name).OrderBy(x => x).ToList();
        Check("moved-ref/F-W2  the other worldspace holds M4 and P2 (control)", w2Keys.SequenceEqual(new[] { "M4", "P2" }), string.Join(", ", w2Keys));

        // LAND by grid.
        string LandAt((int, int) g) => wLand.TryGetValue(g, out var h) ? $"heights {h[16, 16]:0}" : "no heights";
        bool Unreadable(int x, int y) => fv.UnreadableLand.Contains((MovedRefFixture.W, x, y));
        Check("moved-ref/F-L1  an unreadable later LAND: no heights for the grid, and the grid is named unreadable",
              !wLand.ContainsKey((0, 0)) && Unreadable(0, 0), $"{LandAt((0, 0))}; named {Unreadable(0, 0)}");
        Check("moved-ref/F-L2  a later cell copy with no LAND: the master's heights stand",
              wLand.TryGetValue((1, 0), out var h10) && h10[16, 16] == MovedRefFixture.MasterHeight && !Unreadable(1, 0), $"{LandAt((1, 0))}; named {Unreadable(1, 0)}");
        Check("moved-ref/F-L3  a later readable LAND: its heights",
              wLand.TryGetValue((5, 0), out var h50) && h50[16, 16] == MovedRefFixture.BHeight50 && !Unreadable(5, 0), $"{LandAt((5, 0))}; named {Unreadable(5, 0)}");
        Check("moved-ref/F-L4  a later LAND with no VHGT: no heights, named",
              !wLand.ContainsKey((-1, 0)) && Unreadable(-1, 0), $"{LandAt((-1, 0))}; named {Unreadable(-1, 0)}");
        Check("moved-ref/F-L5  a later cell copy carrying ONLY a readable LAND: its heights",
              wLand.TryGetValue((2, 0), out var h20) && h20[16, 16] == MovedRefFixture.BHeight50 && !Unreadable(2, 0), $"{LandAt((2, 0))}; named {Unreadable(2, 0)}");
        _ = nbLand;
    }
}
