using HarnessShared;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

// scopeprobe --self-test: MovedRefFixture (mr/), a copy of MrA.esp whose first REFR cannot be read (broken/), and a plugin
// whose one door leads to a reference no plugin files (door/), written under the fixture folder (PD_FIXTURE_KEEP keeps them
// for a probe run); then the probe's own decisions over them (TargetFootprint.cs, ScopeOutcome.cs), each against an answer
// worked out by hand. Exit 0 when every case passes.
static class ScopeSelfTest
{
    public static int Run() => ProbeSelfTest.Run("scopeprobe", "the scope fixtures through the footprint and outcome decisions", t =>
    {
        var mr = MovedRefFixture.Write(Path.Combine(t.Root, "mr"));
        var mrA = mr.Single(p => Path.GetFileName(p) == "MrA.esp");
        var brokenDir = Path.Combine(t.Root, "broken"); Directory.CreateDirectory(brokenDir);
        var broken = Path.Combine(brokenDir, "MrA.esp");
        File.WriteAllBytes(broken, MovedRefFixture.BreakFirstRefData(File.ReadAllBytes(mrA)));
        var door = ScopeDoorFixture.Write(Path.Combine(t.Root, "door"));
        t.Check("PD2-F0 the fixtures write: MovedRefFixture, the broken MrA copy, the door plugin",
                mr.All(File.Exists) && File.Exists(broken) && File.Exists(door), string.Join(", ", mr.Append(broken).Append(door).Select(Path.GetFileName)));

        FootprintLists WalkOf(params (string Name, string Path)[] targets)
        {
            var byName = targets.Where(x => x.Path != "").ToDictionary(x => x.Name, x => Plugin(x.Name, x.Path), StringComparer.OrdinalIgnoreCase);
            var o = new FootprintLists();
            foreach (var (name, _) in targets)
                TargetFootprint.Walk(name, byName, byName.Count, "self-test", new Dictionary<string, string>(), t.Root, t.Root, 50, o);
            return o;
        }

        // PD2-S1: completeness and the exit code.
        var good = WalkOf(("MrA.esp", mrA));
        t.Check("PD2-S1 a readable target: footprint measured, no walk failure",
                good.Summaries.Count == 1 && good.WalkFailures.Count == 0, string.Join(" | ", good.WalkFailures));
        var absent = WalkOf(("MrA.esp", mrA), ("Absent.esp", ""));
        t.Check("PD2-S1 an absent target is a walk failure naming it",
                absent.WalkFailures.Count == 1 && absent.WalkFailures[0].StartsWith("'Absent.esp' is not in the resolved configuration"), string.Join(" | ", absent.WalkFailures));
        bool c1 = ScopeOutcome.Complete(good.WalkFailures.Count, good.Summaries.Count, 1);
        bool c2 = ScopeOutcome.Complete(absent.WalkFailures.Count, absent.Summaries.Count, 2);
        t.Check("PD2-S1 complete: every target walked -> true, exit 0; one absent of two -> false, exit 1",
                c1 && !c2 && ScopeOutcome.ExitCode(c1) == 0 && ScopeOutcome.ExitCode(c2) == 1, $"{c1} {c2}");
        t.Check("PD2-S1 a failure with every target summarised is still incomplete", !ScopeOutcome.Complete(1, 2, 2), "");

        // PD2-S2: a target holding a record that cannot be read.
        var bad = WalkOf(("MrA.esp", broken));
        t.Check("PD2-S2 the broken MrA: no footprint, one walk failure naming MrA.esp, incomplete",
                bad.Summaries.Count == 0 && bad.WalkFailures.Count == 1 && bad.WalkFailures[0].StartsWith("'MrA.esp' ")
                && !ScopeOutcome.Complete(bad.WalkFailures.Count, bad.Summaries.Count, 1),
                string.Join(" | ", bad.WalkFailures) + $"; unreadable records {bad.UnreadableRecords.Count}");

        // PD2-S3: a refused configuration writes the REFUSED line and exits 2.
        var err = new StringWriter();
        var was = Console.Error;
        int code;
        try { Console.SetError(err); code = ScopeOutcome.Refuse("a plugin's master is absent"); }
        finally { Console.SetError(was); }
        t.Check("PD2-S3 refused: exit 2, stderr starts 'REFUSED: ' with the reason",
                code == 2 && err.ToString().StartsWith("REFUSED: a plugin's master is absent"), $"exit {code}");

        // PD2-S4: a door whose destination no plugin files.
        var d = WalkOf(("SdDoor.esp", door));
        var links = d.PerTargetDoors.SelectMany(x => x.Links).ToList();
        t.Check("PD2-S4 the door plugin's walk finds one door, leading to the unfiled reference",
                links.Count == 1 && links[0].To == ScopeDoorFixture.Nowhere && d.DestinationDoors.Contains(ScopeDoorFixture.Nowhere), $"{links.Count}");
        var (exp, resolved, unresolved) = ScopeOutcome.Doors(d.PerTargetDoors, _ => null);
        t.Check("PD2-S4 nowhere to place it: 0 resolved, 1 unresolved, one expansion row",
                resolved == 0 && unresolved == 1 && exp.Count == 1, $"{resolved} {unresolved} {exp.Count}");
        t.Check("PD2-S4 one unresolved door states the door-destinations extent; none states nothing",
                ScopeOutcome.DoorExtent(1) is not null && ScopeOutcome.DoorExtent(0) is null, "");
    });

    static ConfigPlugin Plugin(string name, string path)
    {
        using var m = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        var masters = m.ModHeader.MasterReferences.Select(x => x.Master.FileName.String).ToList();
        return new ConfigPlugin(name, path, "self-test", new FileInfo(path).Length, "fixture", false, false, masters);
    }
}

/// <summary>SdDoor.esp: a door base, and an interior cell holding one placed door whose teleport destination is a door
/// reference no plugin files.</summary>
static class ScopeDoorFixture
{
    static readonly SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
    public static readonly ModKey Key = new("SdDoor", ModType.Plugin);
    public static readonly FormKey DoorBase = new(Key, 0x800), CellKey = new(Key, 0x801), Placed = new(Key, 0x802), Nowhere = new(Key, 0x8FF);

    public static string Write(string dir)
    {
        Directory.CreateDirectory(dir);
        var mod = new SkyrimMod(Key, Rel);
        mod.Doors.Add(new Door(DoorBase, Rel) { EditorID = "SdDoorBase" });
        var cell = new Cell(CellKey, Rel) { EditorID = "SdDoorCell", Flags = Cell.Flag.IsInteriorCell };
        var r = new PlacedObject(Placed, Rel) { Placement = new Placement { Position = new P3Float(0, 0, 0), Rotation = new P3Float(0, 0, 0) } };
        r.Base.SetTo(DoorBase);
        var td = new TeleportDestination { Position = new P3Float(0, 0, 0), Rotation = new P3Float(0, 0, 0) };
        td.Door.SetTo(Nowhere);
        r.TeleportDestination = td;
        cell.Temporary.Add(r);
        uint id = CellKey.ID;
        var block = new CellBlock { BlockNumber = (int)(id % 10), GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = (int)((id / 10) % 10), GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell); block.SubBlocks.Add(sub); mod.Cells.Records.Add(block);
        var path = Path.Combine(dir, Key.FileName.String);
        mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        return path;
    }
}
