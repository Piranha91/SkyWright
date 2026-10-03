using System.Numerics;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

// G1 build list, Stage B (plan g1/PLAN-8.md §5): forced enable of singletons (EnableArms.Force, `--force-enable`), the
// primitive box and the reachable band in the navmesh distance (floorgapprobe reach). Each case names the wrong answer it
// rejects. Red on the base (the stubs force nothing, return no box and ignore the band).
static class StageBProof
{
    static bool Near(double? a, double b, double tol = 1e-3) => a is { } v && Math.Abs(v - b) <= tol;

    static void Case(Action<string, bool, string> check, string name, Func<(bool Ok, string Detail)> body)
    {
        (bool, string) r;
        try { r = body(); } catch (Exception ex) { r = (false, ex.GetType().Name + ": " + ex.Message); }
        check(name, r.Item1, r.Item2);
    }

    public static void Run(Action<string, bool, string> check)
    {
        RunForce(check);
        RunNavmesh(check);
    }

    // ---------------------------------------------------------------- forced enable
    static void FileInterior(SkyrimMod mod, Cell cell)
    {
        uint id = cell.FormKey.ID;
        int blockN = (int)(id % 10), subN = (int)((id / 10) % 10);
        var block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        block.SubBlocks.Add(sub); mod.Cells.Records.Add(block); sub.Cells.Add(cell);
    }
    static PlacedObject Ref(FormKey key, FormKey baseKey, bool disabled, FormKey? parent = null)
    {
        var r = new PlacedObject(key, SkyrimRelease.SkyrimSE) { Placement = new Placement { Position = new P3Float(0, 0, 0), Rotation = new P3Float(0, 0, 0) } };
        r.Base.SetTo(baseKey);
        if (disabled) r.SkyrimMajorRecordFlags |= SkyrimMajorRecord.SkyrimMajorRecordFlag.InitiallyDisabled;
        if (parent is { } pk) { r.EnableParent = new EnableParent(); r.EnableParent.Reference.SetTo(pk); }
        return r;
    }

    static void RunForce(Action<string, bool, string> check)
    {
        var release = SkyrimRelease.SkyrimSE;
        var key = new ModKey("StageBForce", ModType.Master);
        FormKey K(uint id) => new(key, id);
        var stat = K(0x801); var cellK = K(0x820);
        var single = K(0xA01); var other = K(0xA02); var sw = K(0xA03); var child = K(0xA04); var enabled = K(0xA05);
        var mod = new SkyrimMod(key, release);
        mod.Statics.Add(new Static(stat, release) { EditorID = "SbStat", Model = new Model { File = "meshes\\sb\\stat.nif" } });
        var cell = new Cell(cellK, release) { EditorID = "SbCell", Flags = Cell.Flag.IsInteriorCell };
        cell.Temporary.Add(Ref(single, stat, disabled: true));      // the singleton to force
        cell.Temporary.Add(Ref(other, stat, disabled: true));       // a second singleton, never listed
        cell.Temporary.Add(Ref(sw, stat, disabled: true));          // a switch: the parent of `child`
        cell.Temporary.Add(Ref(child, stat, disabled: false, parent: sw));
        cell.Temporary.Add(Ref(enabled, stat, disabled: false));
        FileInterior(mod, cell);
        EnableArms Fresh() { var idx = new OverlapIndex(); idx.Ingest(0, mod); return new EnableArms(idx); }

        // S1: the listed singleton reads enabled (winner arm and raw facts), nothing refused. Rejects the singleton
        // silently absent (the behaviour before --force-enable).
        Case(check, "stageB S1: --force-enable on an initially-disabled singleton reads it enabled, labelled forced", () =>
        {
            var a = Fresh();
            bool before = a.Winner(single).Enabled;
            var refused = a.Force(new[] { single });
            var w = a.Winner(single);
            return (!before && refused.Count == 0 && w.Enabled && w.Resolved && a.IsForced(single) && a.WinnerRawFacts(single) is { InitiallyDisabled: false },
                    $"before {before}; refused {refused.Count}; after {w}; forced {a.IsForced(single)}");
        });
        // S2: a switch, an enable-parented child and an already-enabled reference are refused by name, and a list holding
        // one of them forces nothing. Rejects hand-enabling what the state machinery owns.
        Case(check, "stageB S2: a switch / an enable-parented ref / an enabled ref are refused by name; a refused list forces nothing", () =>
        {
            var a = Fresh();
            var r1 = a.Force(new[] { sw }); var r2 = a.Force(new[] { child }); var r3 = a.Force(new[] { enabled });
            var r4 = a.Force(new[] { single, enabled });
            bool named = r1.Count == 1 && r1[0].Reason.Contains("switch") && r2.Count == 1 && r2[0].Reason.Contains("enable parent")
                         && r3.Count == 1 && r3[0].Reason.Contains("not initially disabled") && r4.Count == 1;
            return (named && !a.IsForced(single) && !a.Winner(single).Enabled,
                    $"{string.Join(" | ", r1.Concat(r2).Concat(r3).Concat(r4).Select(x => x.Reason))}; single forced {a.IsForced(single)}");
        });
        // S3: an unlisted singleton stays absent. Rejects enabling every singleton.
        Case(check, "stageB S3: an unlisted singleton stays disabled when another is forced", () =>
        {
            var a = Fresh();
            a.Force(new[] { single });
            return (a.Winner(single).Enabled && !a.Winner(other).Enabled, $"single {a.Winner(single)} other {a.Winner(other)}");
        });
        // S4: the arm without a mover reads the forced reference enabled too (a pair cannot be credited as introduced by
        // the mover merely because the singleton is absent in that arm). Rejects forcing the winner arm only.
        Case(check, "stageB S4: the arm without a mover also reads the forced singleton enabled", () =>
        {
            var a = Fresh();
            var closure = new List<HashSet<int>> { new(), new() };
            bool before = a.Without(single, 1, closure).Enabled;
            a.Force(new[] { single });
            var after = a.Without(single, 1, closure);
            return (!before && after.Enabled, $"before {before} after {after}");
        });
        // S5: the output names every forced reference; with nothing forced the text is byte-identical. Rejects a silent
        // force and a default-output change.
        Case(check, "stageB S5: StampForced names the forced references and the label; unchanged text when nothing is forced", () =>
        {
            var a = Fresh();
            const string doc = "{\n  \"x\": 1\n}";
            bool same = a.StampForced(doc) == doc;
            a.Force(new[] { single });
            var s = a.StampForced(doc);
            return (same && s.Contains("forcedEnabled") && s.Contains(single.ToString()) && s.Contains(EnableArms.ForcedLabel) && s.Contains("\"x\": 1"),
                    $"same {same}; {s.Replace('\n', ' ')}");
        });
    }

    // ---------------------------------------------------------------- navmesh: primitive box, reachable band
    static void RunNavmesh(Action<string, bool, string> check)
    {
        var big = new NavTriangle(new Vector3(-1000, -1000, 0), new Vector3(1000, -1000, 0), new Vector3(0, 1000, 0), 0, "Nav", 0);
        // P1: a Box primitive (full extents 100 x 200 x 50) placed at (0,0,100) over a flat triangle at z 0: its bottom is
        // at 75, so the distance is 75. Rejects the origin (100) and half-extents read as full (87.5).
        Case(check, "stageB P1: primitive box 100x200x50 at z 100 over a floor — 75 from its bottom (not 100 from the origin)", () =>
        {
            var soup = NavmeshReach.PrimitiveBoxSoup("Box", new Vector3(100, 200, 50));
            if (soup is null) return (false, "no box soup");
            var w = SpatialOverlap.TransformSoup(soup, new Vec3(0, 0, 100), new Vec3(0, 0, 0), 1f);
            var d = NavmeshReach.FromSoup(w, new[] { big });
            return (d.Status == "measured" && Near(d.Distance, 75), $"{d}");
        });
        // P2: full extents (100, 20, 20) at (0,0,10), yaw 90, scale 2, against a floor strip at x 300..400: the box spans
        // x ±20 after the yaw and scale, so 280. Rejects a dropped rotation (200) and a missing scale (290).
        Case(check, "stageB P2: primitive box yaw 90 scale 2 — 280 to a strip at x 300 (not 200 unrotated, not 290 unscaled)", () =>
        {
            var soup = NavmeshReach.PrimitiveBoxSoup("Box", new Vector3(100, 20, 20));
            if (soup is null) return (false, "no box soup");
            var strip = new NavTriangle(new Vector3(300, -500, 0), new Vector3(400, -500, 0), new Vector3(300, 500, 0), 0, "Nav", 1);
            var w = SpatialOverlap.TransformSoup(soup, new Vec3(0, 0, 10), new Vec3(0, 0, MathF.PI / 2f), 2f);
            var d = NavmeshReach.FromSoup(w, new[] { strip });
            return (d.Status == "measured" && Near(d.Distance, 280, 1e-2), $"{d}");
        });
        // P3: the band 0..128 over a flat triangle: a point 100 above reads 0, 140 above reads 12, 20 below reads 20.
        // Rejects the band ignored (100 / 140) and a band applied downward (0 for the point below).
        Case(check, "stageB P3: reachable band 0..128 — 0 at +100, 12 at +140, 20 at -20", () =>
        {
            var a = NavmeshReach.FromPointBand(new Vector3(0, 0, 100), new[] { big }, 0, 128);
            var b = NavmeshReach.FromPointBand(new Vector3(0, 0, 140), new[] { big }, 0, 128);
            var c = NavmeshReach.FromPointBand(new Vector3(0, 0, -20), new[] { big }, 0, 128);
            return (Near(a.Distance, 0) && Near(b.Distance, 12) && Near(c.Distance, 20), $"{a.Distance} {b.Distance} {c.Distance}");
        });
        // P4: a box above the band (bottom 150) reads 22 to the band's top; the same box with its bottom at 100 reads 0 (a
        // vertex inside). Rejects the soup band distance taken from the triangle itself (150 / 100).
        Case(check, "stageB P4: soup band — box bottom 150 reads 22, box bottom 100 reads 0", () =>
        {
            var soup = NavmeshReach.PrimitiveBoxSoup("Box", new Vector3(20, 20, 20));
            if (soup is null) return (false, "no box soup");
            var hi = NavmeshReach.FromSoupBand(SpatialOverlap.TransformSoup(soup, new Vec3(0, 0, 160), new Vec3(0, 0, 0), 1f), new[] { big }, 0, 128);
            var lo = NavmeshReach.FromSoupBand(SpatialOverlap.TransformSoup(soup, new Vec3(0, 0, 110), new Vec3(0, 0, 0), 1f), new[] { big }, 0, 128);
            return (Near(hi.Distance, 22) && Near(lo.Distance, 0), $"{hi.Distance} {lo.Distance}");
        });
        // P5: a box whose vertices are all outside the band but which passes through it (a tall thin box straddling the
        // band from below the floor to above its top) reads 0. Rejects a vertex-only inside test.
        Case(check, "stageB P5: a box straddling the whole band with no vertex inside reads 0", () =>
        {
            var soup = NavmeshReach.PrimitiveBoxSoup("Box", new Vector3(10, 10, 400));
            if (soup is null) return (false, "no box soup");
            var d = NavmeshReach.FromSoupBand(SpatialOverlap.TransformSoup(soup, new Vec3(0, 0, 50), new Vec3(0, 0, 0), 1f), new[] { big }, 0, 128);
            return (Near(d.Distance, 0), $"{d}");
        });
        // P6: a sphere primitive is not a box: no soup (the caller keeps the labelled position route). Rejects a sphere's
        // radius read as a box.
        Case(check, "stageB P6: a Sphere primitive gives no box soup", () =>
        {
            var s = NavmeshReach.PrimitiveBoxSoup("Sphere", new Vector3(50, 0, 0));
            var b = NavmeshReach.PrimitiveBoxSoup("Box", new Vector3(10, 10, 10));
            return (s is null && b is { TriangleCount: 12 }, $"sphere {(s is null ? "null" : "soup")} box {b?.TriangleCount}");
        });
    }
}
