using HarnessShared;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using SkyWright.Engine;
using ViewProbe;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>Gate B, "disable this object": the write (flags = the winner's with 0x800 added, nothing lost), the patch's
/// contents read back by Mutagen (the right object, only it, in its winner's group, every other field equal as numbers),
/// a move and a disable in one commit, undo staged and written, the refusals each on its own fixture, and what a re-check
/// may say after a disable. The write runs houseCARL's code in process against <see cref="DisableWorld"/>.</summary>
[Trait("tier", "integration")]
public sealed class DisableTests : IClassFixture<DisableWorld>
{
    readonly DisableWorld _w;
    public DisableTests(DisableWorld w) => _w = w;

    string Out => Path.Combine(_w.Root, "out");

    PatchWriter Writer()
    {
        Directory.CreateDirectory(Out);
        return PatchWriter.OpenInProcess(new WriteTarget(_w.Instance, "Default", _w.DataDir, Out),
            Path.Combine(_w.Root, "hcdata-" + Guid.NewGuid().ToString("N")), TestCorpus.Path, _ => { });
    }

    /// <summary>The winner's record of a reference, read by Mutagen from the later plugin when it has one, else the master.</summary>
    IPlacedObjectGetter Winner(FormKey key, out ISkyrimModGetter from)
    {
        foreach (var path in new[] { _w.LaterPath, _w.MasterPath })
        {
            var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            if (mod.EnumerateMajorRecords<IPlacedObjectGetter>().FirstOrDefault(r => r.FormKey == key) is { } r) { from = mod; return r; }
            mod.Dispose();
        }
        throw new InvalidOperationException($"no plugin of the fixture holds {key}");
    }

    static string GroupOf(ISkyrimModGetter mod, FormKey key)
    {
        foreach (var c in mod.EnumerateMajorRecords<ICellGetter>())
        {
            if (c.Persistent.Any(r => r.FormKey == key)) return "persistent";
            if (c.Temporary.Any(r => r.FormKey == key)) return "temporary";
        }
        return "none";
    }

    string WrittenPatch(string name) =>
        Directory.GetFiles(Out, name + ".esp", SearchOption.AllDirectories).Single();

    /// <summary>A staged disable as a commit carries it: the reference at its load-order pose, marked to disable.</summary>
    static PendingEdit DisableOf(FormKey key, Vec3 pos, Vec3 rot, float scale = 1f)
    {
        var p = new RefPlacementEdit.Pose(pos, rot, scale);
        return new PendingEdit(key, p, p, Disable: true);
    }

    static Vec3 V(P3Float p) => new(p.X, p.Y, p.Z);

    // ---- the write and the patch's contents -------------------------------------------------------------------------

    [Fact]
    public async Task A_disable_of_a_persistent_winner_is_written_as_its_flags_with_0x800_added_and_the_persistent_bit_kept()
    {
        var winner = Winner(DisableWorld.Persistent, out _);
        Assert.Equal(0x400, winner.MajorRecordFlagsRaw & 0x400);                       // the fixture's winner is persistent
        await using var w = Writer();
        var ex = await Record.ExceptionAsync(() => w.CommitAsync(new[] { DisableOf(DisableWorld.Persistent, V(winner.Placement!.Position), V(winner.Placement.Rotation), winner.Scale ?? 1f) },
                                                                 "WbDisPersist", dryRun: false));
        Assert.True(ex is null, $"the disable was not written: {ex?.Message}");
        using var patch = SkyrimMod.CreateFromBinaryOverlay(WrittenPatch("WbDisPersist"), SkyrimRelease.SkyrimSE);
        var r = patch.EnumerateMajorRecords<IPlacedObjectGetter>().Single(x => x.FormKey == DisableWorld.Persistent);
        Assert.True((r.MajorRecordFlagsRaw & 0x800) != 0, $"initially disabled (0x800) is not set: flags 0x{r.MajorRecordFlagsRaw:X}");
        Assert.True((r.MajorRecordFlagsRaw & 0x400) != 0, $"the persistent bit (0x400) was lost: flags 0x{r.MajorRecordFlagsRaw:X}");
        Assert.Equal(winner.MajorRecordFlagsRaw | 0x800, r.MajorRecordFlagsRaw);
    }

    [Fact]
    public async Task The_patch_holds_the_disabled_object_only_in_its_winners_group_with_every_other_field_equal()
    {
        // the pair the modder looks at: Persistent and Plain are both drawn; the modder disables Persistent only
        var winner = Winner(DisableWorld.Persistent, out var winnerMod);
        var pairA = Drawn(DisableWorld.Persistent, V(winner.Placement!.Position), V(winner.Placement.Rotation), winner.Scale ?? 1f);
        var pairB = Drawn(DisableWorld.Plain, new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f);
        var s = new EditSession(new[] { pairA, pairB });
        s.Select(DisableWorld.Persistent);
        s.StageDisable(DisableWorld.Persistent);
        await using var w = Writer();
        var ex = await Record.ExceptionAsync(() => w.CommitAsync(s.Net(), "WbDisContents", dryRun: false));
        Assert.True(ex is null, $"the disable was not written: {ex?.Message}");

        using var patch = SkyrimMod.CreateFromBinaryOverlay(WrittenPatch("WbDisContents"), SkyrimRelease.SkyrimSE);
        var placed = patch.EnumerateMajorRecords<IPlacedGetter>().Select(x => x.FormKey).ToList();
        Assert.True(placed.Count == 1 && placed[0] == DisableWorld.Persistent,
            $"the patch holds {string.Join(", ", placed)}; it should hold {DisableWorld.Persistent} only");
        Assert.Equal(GroupOf(winnerMod, DisableWorld.Persistent), GroupOf(patch, DisableWorld.Persistent));
        var r = patch.EnumerateMajorRecords<IPlacedObjectGetter>().Single();
        // every other field equal to the winner's, numbers compared as numbers (houseCARL writes a rotation's -0 as +0)
        var a = (PlacedObject)winner.DeepCopy(); a.MajorRecordFlagsRaw |= 0x800;
        var b = (PlacedObject)r.DeepCopy();
        Assert.Equal(winner.Placement.Rotation.Y, b.Placement!.Rotation.Y);              // -0 == +0 as numbers
        Assert.True(DisableReadBack.Differences(a, b) is { Count: 0 }, "fields differ from the winner's: " + string.Join("; ", DisableReadBack.Differences(a, b)));
    }

    [Fact]
    public async Task One_commit_with_a_move_and_a_disable_writes_both_and_neither_disturbs_the_other()
    {
        var moveFrom = new RefPlacementEdit.Pose(new Vec3(1200, 0, 0), new Vec3(0, 0, 0), 1f);
        var moveTo = moveFrom with { Position = new Vec3(1260, 35, 0) };
        await using var w = Writer();
        var ex = await Record.ExceptionAsync(() => w.CommitAsync(new[]
        {
            new PendingEdit(DisableWorld.MoveTarget, moveFrom, moveTo),
            DisableOf(DisableWorld.Plain, new Vec3(0, 0, 0), new Vec3(0, 0, 0)),
        }, "WbDisMixed", dryRun: false));
        Assert.True(ex is null, $"the commit was not written: {ex?.Message}");
        using var patch = SkyrimMod.CreateFromBinaryOverlay(WrittenPatch("WbDisMixed"), SkyrimRelease.SkyrimSE);
        var moved = patch.EnumerateMajorRecords<IPlacedObjectGetter>().Single(x => x.FormKey == DisableWorld.MoveTarget);
        var off = patch.EnumerateMajorRecords<IPlacedObjectGetter>().Single(x => x.FormKey == DisableWorld.Plain);
        Assert.Equal(1260f, moved.Placement!.Position.X, 3); Assert.Equal(35f, moved.Placement.Position.Y, 3);
        Assert.True((moved.MajorRecordFlagsRaw & 0x800) == 0, "the moved object was disabled too");
        Assert.True((off.MajorRecordFlagsRaw & 0x800) != 0, "the disabled object is not initially disabled");
        Assert.Equal(0f, off.Placement!.Position.X, 3); Assert.Equal(0f, off.Placement.Position.Y, 3);
    }

    [Fact]
    public async Task A_read_back_that_finds_the_persistent_bit_lost_is_an_error()
    {
        // a patch written the wrong way (a plain Set), read back by the app's reader, which is not the writer
        await using var w = Writer();
        await w.CallAsync("housecarl_apply", new Dictionary<string, object?>
        {
            ["ops"] = new[] { new Dictionary<string, object?> { ["formid"] = DisableWorld.Persistent.ToString(), ["field_path"] = "SkyrimMajorRecordFlags", ["value"] = "InitiallyDisabled" } },
            ["patch"] = "WbDisBadSet", ["out_path"] = Out, ["dry_run"] = false,
        });
        var order = HousecarlCore.Mo2LoadOrder.Build(Path.Combine(_w.Instance, "profiles", "Default"), _w.Mods, _w.DataDir).OrderedPaths;
        var ex = Record.Exception(() => DisableReadBack.Check(order, WrittenPatch("WbDisBadSet"), DisableWorld.Persistent));
        Assert.True(ex is InvalidOperationException, $"the read-back did not refuse a patch that lost the persistent bit ({ex?.GetType().Name ?? "no error"})");
        Assert.Contains("0x400", ex!.Message);
    }

    // ---- undo -------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_staged_disable_hides_the_object_counts_as_staged_and_undo_restores_the_state_before_it()
    {
        var ro = Drawn(DisableWorld.Plain, new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f);
        var s = new EditSession(new[] { ro });
        var view = new ViewState(s, () => new[] { ro });
        var before = s.Current(DisableWorld.Plain);
        s.StageDisable(DisableWorld.Plain);
        Assert.True(s.Pending.Count == 1 && s.Pending[0].Disable && s.Pending[0].Ref == DisableWorld.Plain, "the disable is not in the staged list");
        Assert.True(s.IsDisabled(DisableWorld.Plain), "the session does not say the object is disabled");
        Assert.True(ro.Meshes[0].IsHiddenByHost, "the view still draws the disabled object");
        var net = s.Net();
        Assert.True(net.Count == 1 && net[0].Disable, "a commit would not carry the disable");

        var u = s.Undo();
        Assert.True(u is { Disable: true }, "undo did not take back the disable");
        Assert.Empty(s.Pending);
        Assert.False(s.IsDisabled(DisableWorld.Plain));
        Assert.False(ro.Meshes[0].IsHiddenByHost, "after undo the view still hides the object");
        Assert.Equal(before, s.Current(DisableWorld.Plain));
        GC.KeepAlive(view);
    }

    [Fact]
    public void An_object_with_a_staged_move_is_refused_a_disable_and_a_disabled_one_is_refused_a_move()
    {
        var ro = Drawn(DisableWorld.MoveTarget, new Vec3(1200, 0, 0), new Vec3(0, 0, 0), 1f);
        var s = new EditSession(new[] { ro });
        s.Stage(DisableWorld.MoveTarget, s.Current(DisableWorld.MoveTarget) with { Position = new Vec3(1300, 0, 0) });
        var ex = Record.Exception(() => s.StageDisable(DisableWorld.MoveTarget));
        Assert.True(ex is InvalidOperationException && ex.Message.Contains("one fix", StringComparison.Ordinal),
            $"a disable of a moved object was not refused with the one-fix sentence ({ex?.Message ?? "no refusal"})");
        s.Undo();
        s.StageDisable(DisableWorld.MoveTarget);
        var ex2 = Record.Exception(() => s.Stage(DisableWorld.MoveTarget, s.Current(DisableWorld.MoveTarget) with { Position = new Vec3(1300, 0, 0) }));
        Assert.True(ex2 is InvalidOperationException && ex2.Message.Contains("one fix", StringComparison.Ordinal),
            $"a move of a disabled object was not refused with the one-fix sentence ({ex2?.Message ?? "no refusal"})");
    }

    [Fact]
    public async Task Undo_of_a_written_disable_leaves_no_override_of_it_and_says_what_stays_in_the_patch()
    {
        await using var w = Writer();
        var ex = await Record.ExceptionAsync(() => w.CommitAsync(new[]
        {
            DisableOf(DisableWorld.LinkTarget, new Vec3(400, 0, 0), new Vec3(0, 0, 0)),
            DisableOf(DisableWorld.Plain, new Vec3(0, 0, 0), new Vec3(0, 0, 0)),
        }, "WbDisUndo", dryRun: false));
        Assert.True(ex is null, $"the disable was not written: {ex?.Message}");
        var plugin = WrittenPatch("WbDisUndo");
        var said = await w.RevertWrittenAsync(DisableWorld.Plain, Path.GetDirectoryName(plugin)!);
        using var patch = SkyrimMod.CreateFromBinaryOverlay(plugin, SkyrimRelease.SkyrimSE);
        var left = patch.EnumerateMajorRecords<IPlacedGetter>().Select(x => x.FormKey).ToList();
        Assert.True(!left.Contains(DisableWorld.Plain), $"an override of {DisableWorld.Plain} is still in the patch after undo ({said})");
        Assert.Contains(DisableWorld.LinkTarget, left);                                    // the other written disable stays
        Assert.Contains(DisableWorld.Cell.ToString(), said);                              // the carried cell stays: said
        Assert.Contains("nothing else", said);                                            // and what went with it: said
    }

    [Fact]
    public void A_staged_disable_is_saved_with_the_session_and_staged_again_when_it_is_restored()
    {
        var ro = Drawn(DisableWorld.Plain, new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f);
        var s = new EditSession(new[] { ro });
        s.StageDisable(DisableWorld.Plain);
        var data = new SessionData("plan", "baseline", null, null, null,
            s.Pending.Select(e => new SavedEdit(e.Ref.ToString(), e.Before, e.After, e.Disable)).ToList(), Array.Empty<SavedWritten>());
        var path = Path.Combine(_w.Root, "session-" + Guid.NewGuid().ToString("N") + ".json");
        SessionFile.Write(path, data);
        var back = SessionFile.Read(path)!;
        var fresh = new EditSession(new[] { Drawn(DisableWorld.Plain, new Vec3(0, 0, 0), new Vec3(0, 0, 0), 1f) });
        var notes = SessionFile.Restore(fresh, back, (_, _) => null);
        Assert.Empty(notes);
        Assert.True(fresh.IsDisabled(DisableWorld.Plain) && fresh.Pending.Single().Disable, "the restored session lost the staged disable");
    }

    // ---- the refusals, each on its own fixture ---------------------------------------------------------------------

    async Task<(string? Refusal, string? Note)> Judge(FormKey key)
    {
        await using var w = Writer();
        var src = new ServerContextSource(Task.FromResult(w), _ => null);
        var facts = await DisableRule.ReadAsync(src, key);
        return (DisableRule.Refusal(facts), DisableRule.Note(facts));
    }

    public static IEnumerable<object[]> Kinds() => new[]
    {
        new object[] { "already disabled", DisableWorld.AlreadyDisabled.ToString(), "already initially disabled", "" },
        new object[] { "deleted", DisableWorld.Deleted.ToString(), "deleted", "" },
        new object[] { "switched by an enable parent", DisableWorld.Child.ToString(), "enable parent", DisableWorld.Parent.ToString() },
        new object[] { "the enable parent of others", DisableWorld.Parent.ToString(), "enable parent of", DisableWorld.Child.ToString() },
        new object[] { "named by a linked reference", DisableWorld.LinkTarget.ToString(), "linked reference", DisableWorld.Linker.ToString() },
        new object[] { "named by an activate parent", DisableWorld.ActTarget.ToString(), "activate parent", DisableWorld.ActChild.ToString() },
        new object[] { "a door that leads somewhere", DisableWorld.Door.ToString(), "door", DisableWorld.FarDoor.ToString() },
        new object[] { "named by a quest alias", DisableWorld.AliasTarget.ToString(), "quest", DisableWorld.Quest.ToString() },
        new object[] { "a package's location", DisableWorld.PackLocTarget.ToString(), "package", DisableWorld.PackLocation.ToString() },
        new object[] { "a package's target", DisableWorld.PackTargetRef.ToString(), "package", DisableWorld.PackTarget.ToString() },
        new object[] { "a large reference listed only by a copy of its worldspace that does not win", DisableWorld.LargeRef.ToString(), "large reference", DisableWorld.World.ToString() },
        new object[] { "not a placed object", DisableWorld.Actor.ToString(), "actor", "" },
        new object[] { "named by a script property of an object", DisableWorld.ScriptTarget.ToString(), "script", DisableWorld.ScriptHolder.ToString() },
        new object[] { "named by a script property of a quest", DisableWorld.QuestScriptTarget.ToString(), "quest", DisableWorld.Quest.ToString() },
    };

    [Theory, MemberData(nameof(Kinds))]
    public async Task Each_kind_of_dependence_is_refused_before_staging_in_one_sentence_naming_it(string kind, string key, string words, string names)
    {
        var (refusal, _) = await Judge(FormKey.Factory(key));
        Assert.True(refusal is not null, $"{kind}: {key} was not refused");
        Assert.True(refusal!.Contains(words, StringComparison.OrdinalIgnoreCase), $"{kind}: the refusal does not say '{words}': {refusal}");
        if (names.Length > 0) Assert.True(refusal.Contains(names, StringComparison.OrdinalIgnoreCase), $"{kind}: the refusal does not name {names}: {refusal}");
        Assert.True(refusal.TrimEnd().EndsWith('.') && refusal.Count(c => c == '.') <= 2 + refusal.Split(':').Length, $"{kind}: not one sentence: {refusal}");
        Assert.Contains("try", refusal, StringComparison.OrdinalIgnoreCase);              // and what to try
    }

    [Fact]
    public async Task A_plain_object_is_allowed_and_a_navmesh_cutting_one_is_allowed_with_the_cut_said()
    {
        var (plain, plainNote) = await Judge(DisableWorld.Plain);
        Assert.True(plain is null, $"a plain object was refused: {plain}");
        Assert.Null(plainNote);
        var (navcut, note) = await Judge(DisableWorld.Navcut);
        Assert.True(navcut is null, $"a navmesh-cutting object was refused: {navcut}");
        Assert.True(note is not null && note.Contains("navmesh", StringComparison.OrdinalIgnoreCase) && note.Contains("open", StringComparison.OrdinalIgnoreCase),
            $"the sentence does not say the navmesh cut opens: {note ?? "no sentence"}");
    }

    // ---- what a re-check may say after a disable ---------------------------------------------------------------------

    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "disable-recheck", name);

    static string Id(string necessary) =>
        ConflictIo.RowIdentity("OVERLAP", necessary, "000848:Ultimate Markarth.esp", "Ultimate Markarth Expanded.esp", null);

    /// <summary>Step 1's measurement (GATE-B-RED.md): on the lab, a disable of 000848:Ultimate Markarth.esp re-measured
    /// every one of the 36 overlap rows naming it Gone ("a ref is disabled at runtime under this state"), conflictdiff
    /// "repaired 36", and contactprobe created no row. The fixtures are trimmed copies of those emits (each file says what
    /// was kept); one row's carried predicate is left out on purpose, and the contact re-run gains one created row in the
    /// real row's shape, because the real disable stranded nothing.</summary>
    [Fact]
    public void After_a_disable_resolved_is_said_only_where_the_row_was_re_measured_gone_and_what_it_strands_is_new()
    {
        var overlap = DifferentialRows.Credit(ConflictIo.Read(Fixture("overlap-unpatched.json")), ConflictIo.Read(Fixture("overlap-patched.json")));
        var contact = DifferentialRows.Credit(ConflictIo.Read(Fixture("contact-unpatched.json")), ConflictIo.Read(Fixture("contact-patched.json")));
        var said = DisableRecheck.Of(overlap, contact, "000848:Ultimate Markarth.esp", Id("001FBB:JKs Skyrim.esp"));
        Assert.Equal("resolved", said.Row.Word);                                                  // as measured: Holds -> Gone
        var other = said.Others.SingleOrDefault(o => o.Id == Id("001FBC:JKs Skyrim.esp"));
        Assert.True(other is { Word: "resolved" }, $"the other row naming the disabled object, re-measured Gone, does not read resolved: {other?.Sentence ?? "not reported"}");
        var absent = said.Others.SingleOrDefault(o => o.Id == Id("001FC7:JKs Skyrim.esp"));
        Assert.True(absent is not null, "the row naming the disabled object with no carried predicate is not reported");
        Assert.True(absent!.Word != "resolved", $"a row merely absent from the patched run was credited: {absent.Sentence}");
        Assert.DoesNotContain(said.Others, o => o.Id.Contains("001fe3", StringComparison.OrdinalIgnoreCase));   // a row not naming it
        Assert.True(said.New.Any(n => n.Word == "new" && n.Id.Contains("001fbb", StringComparison.OrdinalIgnoreCase)),
            "the fixture the disable left in the air is not shown as new: " + string.Join("; ", said.New.Select(n => n.Id)));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>A drawn reference of the fixture as the scene holds it: one box mesh at the reference's load-order pose.</summary>
    static RefOcclusion Drawn(FormKey key, Vec3 pos, Vec3 rot, float scale)
    {
        var r = new SceneRef("meshes\\test.nif", pos, rot, scale, false, RefKey: key);
        var mesh = new SkyRender.GlMesh();
        var verts = new[] { new System.Numerics.Vector3(-10, -10, -10), new System.Numerics.Vector3(10, -10, -10), new System.Numerics.Vector3(10, 10, -10), new System.Numerics.Vector3(-10, 10, 10) }
            .Select(v => TexturedScene.YUp(new System.Numerics.Vector3(v.X + pos.X, v.Y + pos.Y, v.Z + pos.Z))).ToArray();
        var ps = new PickShape { Kind = PickKind.Ref, Verts = verts, Tris = new[] { 0, 1, 2, 0, 2, 3 }, Ref = r, Mesh = mesh };
        ps.ComputeBounds();
        var ro = new RefOcclusion { Ref = r };
        ro.Meshes.Add(mesh); ro.Shapes.Add(ps);
        return ro;
    }
}
