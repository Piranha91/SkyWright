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

        // SS-1: a configuration whose plugin lacks a master. The game will not start on it, so the shared reader refuses the
        // whole configuration before any walk (ruling 12, _shared/OrderMasters.cs): the refusal names MrA.esp and
        // MrMaster.esm, and the run exits 2 with no scope.json (ScopeOutcome.Refuse).
        string? Refusal(params (string Name, string Path)[] loaded)
        {
            var gaps = OrderMasters.Gaps(loaded.Select(x => (x.Name, PluginHeader.TryRead(x.Path)?.Masters)).ToList());
            return gaps.Count == 0 ? null : OrderMasters.Sentence(gaps, g => $"switch '{g.Plugin}' off");
        }
        var orphan = Refusal(("MrA.esp", mrA));
        t.Check("SS-1 MrA.esp without MrMaster.esm: refused in one sentence naming both, starting MISSING MASTER",
                orphan is not null && orphan.StartsWith("MISSING MASTER - 'MrA.esp' masters 'MrMaster.esm'")
                && orphan.Count(c => c == ';') == 1,
                orphan ?? "(not refused)");
        var withMaster = WalkIn(new[] { ("MrMaster.esm", master) }, ("MrA.esp", mrA));
        t.Check("SS-1 control: MrA.esp with MrMaster.esm loaded: not refused; footprint measured, no walk failure",
                Refusal(("MrMaster.esm", master), ("MrA.esp", mrA)) is null
                && withMaster.Summaries.Count == 1 && withMaster.WalkFailures.Count == 0, string.Join(" | ", withMaster.WalkFailures));
        var masterOnly = WalkIn(Array.Empty<(string, string)>(), ("MrMaster.esm", master));
        t.Check("SS-1 control: a master-less target (MrMaster.esm) alone: not refused; footprint measured, no walk failure",
                Refusal(("MrMaster.esm", master)) is null
                && masterOnly.Summaries.Count == 1 && masterOnly.WalkFailures.Count == 0, string.Join(" | ", masterOnly.WalkFailures));

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

        // SR: houseCARL's rulebook, run through THIS exe as a child over an MO2 instance holding MrMaster.esm and MrA.esp,
        // in a working folder with no generated\ in it: the child finds the rulebook from its own build or says it cannot.
        // The test never names the build's rulebook; only SR-2 and SR-3 point WB_CORPUS somewhere broken.
        var inst = Path.Combine(t.Root, "sr-inst");
        var game = Path.Combine(t.Root, "sr-game");
        Directory.CreateDirectory(Path.Combine(inst, "mods", "MrFix"));
        Directory.CreateDirectory(Path.Combine(inst, "profiles", "P"));
        Directory.CreateDirectory(Path.Combine(inst, "overwrite"));
        Directory.CreateDirectory(Path.Combine(game, "Data"));
        File.Copy(master, Path.Combine(inst, "mods", "MrFix", "MrMaster.esm"));
        File.Copy(mrA, Path.Combine(inst, "mods", "MrFix", "MrA.esp"));
        File.WriteAllText(Path.Combine(inst, "profiles", "P", "modlist.txt"), "+MrFix\n");
        File.WriteAllText(Path.Combine(inst, "profiles", "P", "plugins.txt"), "*MrMaster.esm\n*MrA.esp\n");
        File.WriteAllText(Path.Combine(inst, "profiles", "P", "loadorder.txt"), "MrMaster.esm\nMrA.esp\n");

        // SR-1: the expected contributors at E00 are worked out by hand from MovedRefFixture's table (the master's own
        // E00 with its LAND, navmeshes and references; A's E00 copy with navmeshes and references, and A's persistent
        // M5 standing at (1400,1400) in grid 0,0; A carries no LAND there).
        var sr1 = ScopeChild.Run(t.Root, "sr1", inst, game, corpus: null);
        string atE00 = sr1.Doc is { } d1 ? ScopeChild.At(d1, MovedRefFixture.E00.ToString()) : "(no scope.json)";
        bool sr1Complete = sr1.Doc is { } d1c && d1c.RootElement.GetProperty("locationContributors").GetProperty("complete").GetBoolean();
        var sr1Missing = sr1.Doc is { } d1m ? d1m.RootElement.GetProperty("locationContributors").GetProperty("missing").EnumerateArray().Select(x => x.GetString()).ToList() : new();
        t.Check("SR-1 the probe finds its build's rulebook from any working folder: exit 0, contributors complete, E00 as worked by hand",
                sr1.Exit == 0 && sr1Complete && sr1Missing.Count == 0
                && atE00 == "MrMaster.esm:CELL,LAND,NAVM,placed-record | MrA.esp:CELL,NAVM,persistent-placed-record,placed-record",
                $"exit {sr1.Exit}; complete {sr1Complete}; E00 {atE00}; missing {sr1Missing.Count}: {sr1Missing.FirstOrDefault()}");

        // SR-2: a rulebook that is not there. One sentence on stderr, exit 2, nothing on stdout, no scope.json (a stale
        // one from an earlier run at --json is removed, so it cannot pass for this run's), the run log closed as failed.
        // Its exact start also keeps it from reading as an order refusal to runcontrol (the game would not load the order).
        var absentCorpus = Path.GetFullPath(Path.Combine(t.Root, "absent", "corpus.json"));
        var sr2 = ScopeChild.Run(t.Root, "sr2", inst, game, corpus: absentCorpus, stale: true);
        string want2 = $"scopeprobe: houseCARL's rulebook {absentCorpus} is missing (named by WB_CORPUS), so it cannot tell which other "
                     + "plugins change the same places as the mod you are checking; run scripts\\build.ps1 to make it, or set WB_CORPUS to a corpus.json.";
        t.Check("SR-2 a missing rulebook: exit 2, the one sentence, nothing on stdout, no scope.json (stale one removed), run log failed",
                sr2.Exit == 2 && sr2.ErrLines.Count == 1 && sr2.ErrLines[0] == want2 && sr2.Out.Trim().Length == 0
                && !sr2.JsonExists && sr2.RunLogStatus == "Failed",
                $"exit {sr2.Exit}; json {sr2.JsonExists}; log {sr2.RunLogStatus}; stdout {sr2.Out.Trim().Length} chars; stderr: {string.Join(" / ", sr2.ErrLines)}");

        // SR-3: a rulebook that is there and cannot be read.
        var junk = Path.GetFullPath(Path.Combine(t.Root, "junk", "corpus.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(junk)!);
        File.WriteAllText(junk, "not json");
        var sr3 = ScopeChild.Run(t.Root, "sr3", inst, game, corpus: junk);
        string head3 = $"scopeprobe: houseCARL's rulebook {junk} (named by WB_CORPUS) could not be read (";
        string tail3 = "), so it cannot tell which other plugins change the same places as the mod you are checking; "
                     + "run scripts\\build.ps1 -Rulebook to make it again, or set WB_CORPUS to a readable corpus.json.";
        t.Check("SR-3 an unreadable rulebook: exit 2, the one sentence with the reason, nothing on stdout, no scope.json, run log failed",
                sr3.Exit == 2 && sr3.ErrLines.Count == 1 && sr3.ErrLines[0].StartsWith(head3) && sr3.ErrLines[0].EndsWith(tail3)
                && sr3.Out.Trim().Length == 0 && !sr3.JsonExists && sr3.RunLogStatus == "Failed",
                $"exit {sr3.Exit}; json {sr3.JsonExists}; log {sr3.RunLogStatus}; stdout {sr3.Out.Trim().Length} chars; stderr: {string.Join(" / ", sr3.ErrLines)}");
    });

    static ConfigPlugin Plugin(string name, string path)
    {
        using var m = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        var masters = m.ModHeader.MasterReferences.Select(x => x.Master.FileName.String).ToList();
        return new ConfigPlugin(name, path, "self-test", new FileInfo(path).Length, "fixture", false, false, masters);
    }
}

/// <summary>This probe's own exe run as a child for the SR cases: target MrA.esp over the instance the self-test wrote,
/// working folder <c>root</c>, WB_CORPUS removed or set to <c>corpus</c>, its own run log folder.</summary>
static class ScopeChild
{
    public sealed record Result(int Exit, string Out, List<string> ErrLines, bool JsonExists, System.Text.Json.JsonDocument? Doc, string? RunLogStatus);

    public static Result Run(string root, string name, string inst, string game, string? corpus, bool stale = false)
    {
        var json = Path.Combine(root, name, "scope.json");
        var runlog = Path.Combine(root, name, "runlog");
        Directory.CreateDirectory(Path.GetDirectoryName(json)!);
        if (stale) File.WriteAllText(json, "{\"stale\": \"from an earlier run\"}");
        var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
        {
            WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in new[] { "--instance", inst, "--profile", "P", "--game", game, "--target", "MrA.esp", "--json", json })
            psi.ArgumentList.Add(a);
        psi.Environment.Remove("WB_CORPUS");
        if (corpus is not null) psi.Environment["WB_CORPUS"] = corpus;
        psi.Environment["HOUSECARL_RUNLOG_DIR"] = runlog;
        using var p = System.Diagnostics.Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var errLines = err.Result.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        System.Text.Json.JsonDocument? doc = null;
        if (File.Exists(json)) { try { doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json)); } catch (System.Text.Json.JsonException) { } }
        return new Result(p.ExitCode, stdout, errLines, File.Exists(json), doc, RunLogStatus(runlog));
    }

    /// <summary>The terminal status the run log recorded for the child's scopeprobe run, or null when it recorded none.</summary>
    static string? RunLogStatus(string runlog)
    {
        var history = Path.Combine(runlog, "history.jsonl");
        if (!File.Exists(history)) return null;
        string? status = null;
        foreach (var line in File.ReadAllLines(history))
        {
            try
            {
                using var d = System.Text.Json.JsonDocument.Parse(line);
                if (d.RootElement.TryGetProperty("op", out var op) && op.GetString() == "scopeprobe"
                    && d.RootElement.TryGetProperty("status", out var s)) status = s.GetString();
            }
            catch (System.Text.Json.JsonException) { }
        }
        return status;
    }

    /// <summary>One location's contributors as "plugin:kind,kind | plugin:kind", in the document's order.</summary>
    public static string At(System.Text.Json.JsonDocument doc, string cell)
    {
        foreach (var loc in doc.RootElement.GetProperty("locationContributors").GetProperty("locations").EnumerateArray())
            if (loc.GetProperty("cell").GetString() == cell)
                return string.Join(" | ", loc.GetProperty("plugins").EnumerateArray().Select(p =>
                    p.GetProperty("name").GetString() + ":" + string.Join(",", p.GetProperty("recordKinds").EnumerateArray().Select(k => k.GetString()))));
        return $"(no location {cell})";
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
