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

        // The configuration holds the targets and MrMaster.esm, the master MrA.esp and the broken copy name: a walk
        // checks a target's masters against the configuration, so a case about something else loads them.
        var master = mr.Single(p => Path.GetFileName(p) == "MrMaster.esm");
        FootprintLists WalkIn(IEnumerable<(string Name, string Path)> others, params (string Name, string Path)[] targets)
        {
            var byName = targets.Concat(others).Where(x => x.Path != "").ToDictionary(x => x.Name, x => Plugin(x.Name, x.Path), StringComparer.OrdinalIgnoreCase);
            var o = new FootprintLists();
            foreach (var (name, _) in targets)
                TargetFootprint.Walk(name, byName, byName.Count, "self-test", new Dictionary<string, string>(), t.Root, t.Root, 50, o);
            return o;
        }
        FootprintLists WalkOf(params (string Name, string Path)[] targets) => WalkIn(new[] { ("MrMaster.esm", master) }, targets);

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
                bad.Summaries.Count == 0 && bad.WalkFailures.Count == 1 && bad.WalkFailures[0].StartsWith("'MrA.esp' could not be walked for its placements")
                && !ScopeOutcome.Complete(bad.WalkFailures.Count, bad.Summaries.Count, 1),
                string.Join(" | ", bad.WalkFailures) + $"; unreadable records {bad.UnreadableRecords.Count}");

        // SS-1: a target whose master the configuration does not load. The game will not start on it, and records that
        // override the master's cannot be told from new ones, so the footprint is not measured: one walk failure naming
        // the target and the missing master, no footprint, the scope incomplete (exit 1).
        var orphan = WalkIn(Array.Empty<(string, string)>(), ("MrA.esp", mrA));
        t.Check("SS-1 MrA.esp without MrMaster.esm: one walk failure naming MrA.esp and MrMaster.esm, no footprint, incomplete",
                orphan.Summaries.Count == 0 && orphan.WalkFailures.Count == 1 && orphan.WalkFailures[0].StartsWith("'MrA.esp' ")
                && orphan.WalkFailures[0].Contains("'MrMaster.esm'")
                && !ScopeOutcome.Complete(orphan.WalkFailures.Count, orphan.Summaries.Count, 1)
                && ScopeOutcome.ExitCode(ScopeOutcome.Complete(orphan.WalkFailures.Count, orphan.Summaries.Count, 1)) == 1,
                $"summaries {orphan.Summaries.Count}; " + string.Join(" | ", orphan.WalkFailures));
        var withMaster = WalkIn(new[] { ("MrMaster.esm", master) }, ("MrA.esp", mrA));
        t.Check("SS-1 control: MrA.esp with MrMaster.esm loaded (not a target): footprint measured, no walk failure",
                withMaster.Summaries.Count == 1 && withMaster.WalkFailures.Count == 0, string.Join(" | ", withMaster.WalkFailures));
        var masterOnly = WalkIn(Array.Empty<(string, string)>(), ("MrMaster.esm", master));
        t.Check("SS-1 control: a master-less target (MrMaster.esm) alone: footprint measured, no walk failure",
                masterOnly.Summaries.Count == 1 && masterOnly.WalkFailures.Count == 0, string.Join(" | ", masterOnly.WalkFailures));

        // SS-4: a target holding a record whose body cannot be read: SdBad.esp's one STAT, marked compressed, with a body
        // that does not inflate. The open succeeds (the overlay frames groups and headers only); the read of that record
        // fails inside the record walk, so the unreadable-record catch names it (FrameCheck locates it by its bytes).
        var badBody = ScopeBadRecordFixture.Write(Path.Combine(t.Root, "badbody"));
        var bb = WalkOf(("SdBad.esp", badBody));
        string Rows(FootprintLists o) => string.Join(" | ", o.UnreadableRecords.Select(x => System.Text.Json.JsonSerializer.Serialize(x)));
        bool namesStat = bb.UnreadableRecords.Count == 1 && Rows(bb).Contains("\"signature\":\"STAT\"")
                         && Rows(bb).Contains(ScopeBadRecordFixture.Bad.ToString()) && Rows(bb).Contains("\"located\":\"frame\"");
        t.Check("SS-4 the unreadable STAT: one walk failure 'has a record that cannot be read' naming it, no footprint, incomplete",
                bb.Summaries.Count == 0 && bb.WalkFailures.Count == 1
                && bb.WalkFailures[0].StartsWith("'SdBad.esp' has a record that cannot be read (STAT ")
                && !ScopeOutcome.Complete(bb.WalkFailures.Count, bb.Summaries.Count, 1),
                string.Join(" | ", bb.WalkFailures));
        t.Check("SS-4 the unreadable record is listed once: STAT, its FormKey, located by the file's frames",
                namesStat, Rows(bb));

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

/// <summary>SdBad.esp: one STAT record whose header says compressed and whose body is not a zlib stream, so reading the
/// record fails while the file still opens.</summary>
static class ScopeBadRecordFixture
{
    public static readonly ModKey Key = new("SdBad", ModType.Plugin);
    public static readonly FormKey Bad = new(Key, 0x800);

    public static string Write(string dir)
    {
        Directory.CreateDirectory(dir);
        var mod = new SkyrimMod(Key, SkyrimRelease.SkyrimSE);
        mod.Statics.Add(new Static(Bad, SkyrimRelease.SkyrimSE) { EditorID = "SdBadStatic", Model = new Model { File = "Meshes/sd/bad.nif" } });
        var path = Path.Combine(dir, Key.FileName.String);
        mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var b = File.ReadAllBytes(path);
        // The STAT group's header is 24 bytes: "GRUP", size, label "STAT", ...; its first record follows.
        int g = -1;
        for (int i = 0; i + 12 <= b.Length; i++)
            if (b[i] == 'G' && b[i + 1] == 'R' && b[i + 2] == 'U' && b[i + 3] == 'P' && System.Text.Encoding.ASCII.GetString(b, i + 8, 4) == "STAT") { g = i; break; }
        if (g < 0) throw new InvalidDataException("SdBad.esp: no STAT group was written");
        int r = g + 24;
        if (System.Text.Encoding.ASCII.GetString(b, r, 4) != "STAT") throw new InvalidDataException("SdBad.esp: the STAT group does not start with a STAT record");
        int size = BitConverter.ToInt32(b, r + 4);
        if (size < 8) throw new InvalidDataException($"SdBad.esp: the STAT body is {size} bytes, too short to stand in for a compressed one");
        BitConverter.GetBytes(BitConverter.ToUInt32(b, r + 8) | 0x00040000u).CopyTo(b, r + 8);   // the Compressed flag
        BitConverter.GetBytes(256u).CopyTo(b, r + 24);                                          // claimed inflated size
        for (int i = r + 28; i < r + 24 + size; i++) b[i] = 0xFF;                               // not a zlib stream
        File.WriteAllBytes(path, b);
        return path;
    }
}
