using System.Numerics;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using SkyRender;
using ViewProbe;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>Items 2, 3 and 4 against the houseCARL server this checkout builds, on the synthetic tilted-reference world:
/// the one placement check before any server answer is used, the objects a decision is keyed on, undoing a written
/// edit by removal, and view-only controls leaving a written patch alone.</summary>
[Trait("tier", "integration")]
public class ReviewSpeedServerTests
{
    static string ServerExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, HarnessShared.CheckoutLayout.Marker))) dir = dir.Parent;
        Assert.NotNull(dir);
        var exe = TestBuild.Server;
        Assert.True(File.Exists(exe), $"build src/housecarl-mcp in Release first: {exe}");
        return exe;
    }

    static Task<PatchWriter> Start(TiltedRefWorld w) => PatchWriter.StartAsync(ServerExe(),
        new WriteTarget(w.Instance, "Default", w.DataDir, Path.Combine(w.Instance, "mods")),
        Path.Combine(w.Root, "hcdata-" + Guid.NewGuid().ToString("N")), TestCorpus.Path, _ => { });

    static readonly RefPlacementEdit.Pose Drawn = new(TiltedRefWorld.Pos, TiltedRefWorld.Rot, 1f);

    /// <summary>The world's master rewritten with the reference at another position (a mod update).</summary>
    static void MoveInMaster(TiltedRefWorld w, Vec3 pos)
    {
        var path = Path.Combine(w.Instance, "mods", "WorldMod", TiltedRefWorld.Master.FileName.String);
        var m = SkyrimMod.CreateFromBinary(path, SkyrimRelease.SkyrimSE);
        foreach (var c in m.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(s => s.Cells))
            foreach (var r in c.Temporary.OfType<PlacedObject>())
                if (r.FormKey == TiltedRefWorld.Ref) r.Placement = new Placement { Position = new P3Float(pos.X, pos.Y, pos.Z), Rotation = new P3Float(TiltedRefWorld.Rot.X, TiltedRefWorld.Rot.Y, TiltedRefWorld.Rot.Z) };
        var tmp = Path.Combine(w.Root, "rewrite-" + Guid.NewGuid().ToString("N"), Path.GetFileName(path));
        Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
        m.BeginWrite.ToPath(tmp).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        File.Move(tmp, path, overwrite: true);
    }

    // S4a: one placement check, used before any server answer about a drawn reference
    [Fact]
    public async Task The_placement_check_says_one_sentence_when_the_scene_drew_the_reference_elsewhere()
    {
        using var w = new TiltedRefWorld();
        await using var p = await Start(w);
        Assert.Null(await p.PlacementMismatchAsync(TiltedRefWorld.Ref, Drawn));
        var why = await p.PlacementMismatchAsync(TiltedRefWorld.Ref, Drawn with { Position = new Vec3(1, 2, 3) });
        Assert.NotNull(why);
        Assert.Contains("not the same configuration", why);
        Assert.DoesNotContain("\n", why);
        var turned = await p.PlacementMismatchAsync(TiltedRefWorld.Ref, Drawn with { Rotation = new Vec3(TiltedRefWorld.Rot.X, TiltedRefWorld.Rot.Y, TiltedRefWorld.Rot.Z + 0.2f) });
        Assert.NotNull(turned);                                      // a rotation-only difference is a different placement
        Assert.Contains("not the same configuration", turned);
    }

    // S4b: a bundle whose arm places the reference elsewhere than the profile: no decision key is read
    [Fact]
    public async Task Decision_objects_are_refused_for_a_reference_the_bundles_arm_placed_elsewhere()
    {
        using var w = new TiltedRefWorld();
        await using var p = await Start(w);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            p.ReadObjectsAsync(new[] { (TiltedRefWorld.Ref, Drawn with { Position = new Vec3(9, 9, 9) }) }));
        Assert.Contains("not the same configuration", ex.Message);
    }

    // S2a: two server processes give the same key; one changed field changes it
    [Fact]
    public async Task Two_server_processes_read_the_same_objects_and_a_changed_position_is_seen()
    {
        using var w = new TiltedRefWorld();
        IReadOnlyList<ObjectState> first, second;
        await using (var p = await Start(w)) first = await p.ReadObjectsAsync(new[] { (TiltedRefWorld.Ref, Drawn) });
        await using (var p = await Start(w)) second = await p.ReadObjectsAsync(new[] { (TiltedRefWorld.Ref, Drawn) });
        var a = Assert.Single(first); var b = Assert.Single(second);
        Assert.Equal(TiltedRefWorld.Ref.ToString(), a.FormKey, ignoreCase: true);
        Assert.Contains(a.Fields, f => f.Path == "Placement.Position");
        Assert.Equal(a.Fields, b.Fields);
        Assert.Equal(a.BaseFields, b.BaseFields);
        Assert.Equal("000801:HcWbWorld.esm", a.BaseKey, ignoreCase: true);

        var moved = new Vec3(TiltedRefWorld.Pos.X + 30, TiltedRefWorld.Pos.Y, TiltedRefWorld.Pos.Z);
        MoveInMaster(w, moved);
        await using var q = await Start(w);
        var third = Assert.Single(await q.ReadObjectsAsync(new[] { (TiltedRefWorld.Ref, Drawn with { Position = moved }) }));
        var m = Decisions.Compare(new Decision(w.Instance, "Default", "geometry.object-overlap", "k", "intentional", "r", DateTimeOffset.UtcNow,
                                               new RowState("Holds", "s", "b"), first, "same-server"), new RowState("Holds", "s", "b"), new[] { third }, "same-server");
        Assert.Equal(Standing.Reconsider, m.Standing);
        Assert.Contains("Placement.Position", m.Sentence);
    }

    static RefOcclusion WorldRef()
    {
        var r = new SceneRef("meshes\\test.nif", TiltedRefWorld.Pos, TiltedRefWorld.Rot, 1f, false, RefKey: TiltedRefWorld.Ref, BaseEditorId: "HcWbStatic");
        var ro = new RefOcclusion { Ref = r };
        var verts = new[] { new Vector3(-10, -10, 0), new Vector3(10, -10, 0), new Vector3(0, 10, 20) }
            .Select(v => TexturedScene.YUp(Vector3.Transform(v, RefPlacementEdit.World(Drawn)))).ToArray();
        var mesh = new GlMesh();
        var ps = new PickShape { Kind = PickKind.Ref, Verts = verts, Tris = new[] { 0, 1, 2 }, Ref = r, Mesh = mesh };
        ps.ComputeBounds();
        ro.Meshes.Add(mesh); ro.Shapes.Add(ps);
        return ro;
    }

    // S4c: a written edit is undone by removing its override, never by writing the old pose back
    [Fact]
    public async Task Reverting_a_written_edit_removes_its_override_from_the_patch_and_keeps_the_file()
    {
        using var w = new TiltedRefWorld();
        await using var p = await Start(w);
        var after = Drawn with { Position = new Vec3(700f, -300f, 90f) };
        await p.CommitAsync(new[] { new PendingEdit(TiltedRefWorld.Ref, Drawn, after) }, "WbRevert", dryRun: false);
        var file = Assert.Single(Directory.GetFiles(Path.Combine(w.Instance, "mods"), "WbRevert.esp", SearchOption.AllDirectories));
        var said = await p.RevertWrittenAsync(TiltedRefWorld.Ref, Path.GetDirectoryName(file)!);
        Assert.True(File.Exists(file), "the patch file was deleted");
        using (var patch = SkyrimMod.CreateFromBinaryOverlay(file, SkyrimRelease.SkyrimSE))
            Assert.DoesNotContain(patch.EnumerateMajorRecords<IPlacedObjectGetter>(), r => r.FormKey == TiltedRefWorld.Ref);
        Assert.Contains(TiltedRefWorld.Ref.ToString(), said);
        Assert.Contains("no placed reference", said);              // the last override: the patch is now empty of references
    }

    // S3a: view-only controls never change a written patch
    [Fact]
    public async Task Hiding_isolating_and_see_through_change_only_the_view_and_the_patch_is_the_staged_edit()
    {
        using var w = new TiltedRefWorld();
        var ro = WorldRef();
        var s = new EditSession(new[] { ro });
        var v = new ViewState(s, () => new[] { ro });
        var after = Drawn with { Position = new Vec3(640f, -256f, 64f) };
        s.Stage(TiltedRefWorld.Ref, after);
        v.Hide(TiltedRefWorld.Ref);
        v.SeeThrough(TiltedRefWorld.Ref);
        v.Isolate(TiltedRefWorld.Ref, null);
        Assert.True(v.Active, "no view-only change took effect");
        await using var p = await Start(w);
        var net = s.Net();
        Assert.Equal(new[] { new PendingEdit(TiltedRefWorld.Ref, Drawn, after) }, net);
        await p.CommitAsync(net, "WbViewOnly", dryRun: false);
        var (pos, _) = await p.ReadPlacementAsync(TiltedRefWorld.Ref.ToString(), source: "WbViewOnly.esp");
        Assert.Equal(after.Position.X, pos.X, 3); Assert.Equal(after.Position.Y, pos.Y, 3); Assert.Equal(after.Position.Z, pos.Z, 3);
        Assert.True(v.Active, "the write cleared the view state");
    }

    // B1d, revised by the Gate C review (C8): a reference drawn elsewhere is left out and counted, not a refusal of all
    [Fact]
    public async Task Winners_are_read_in_one_call_and_a_reference_drawn_elsewhere_is_left_out()
    {
        using var w = new TiltedRefWorld();
        await using var p = await Start(w);
        var (winners, mismatched) = await p.WinnersAsync(new[] { (TiltedRefWorld.Ref, (RefPlacementEdit.Pose?)Drawn) });
        Assert.True(winners.TryGetValue(TiltedRefWorld.Ref, out var win), "no winner read");
        Assert.Equal(TiltedRefWorld.Master.FileName.String, win, ignoreCase: true);
        Assert.Empty(mismatched);
        var (w2, m2) = await p.WinnersAsync(new[] { (TiltedRefWorld.Ref, (RefPlacementEdit.Pose?)(Drawn with { Position = new Vec3(9, 9, 9) })) });
        Assert.Equal(new[] { TiltedRefWorld.Ref }, m2);
        Assert.False(w2.ContainsKey(TiltedRefWorld.Ref), "a reference drawn elsewhere was given a winner to use");
    }
    // B4: the base's fields are the ones geometry reads, and the mesh is keyed by its content hash
    [Fact]
    public async Task Decision_objects_keep_the_bases_geometry_fields_and_the_meshes_content_hash()
    {
        using var w = new TiltedRefWorld(withMesh: true);
        ObjectState o;
        await using (var p = await Start(w)) o = Assert.Single(await p.ReadObjectsAsync(new[] { (TiltedRefWorld.Ref, Drawn) }));
        Assert.Contains(o.BaseFields, f => f.Path.StartsWith("Model", StringComparison.Ordinal));
        Assert.DoesNotContain(o.BaseFields, f => f.Path.StartsWith("EditorID", StringComparison.Ordinal));
        Assert.Equal(@"meshes\hcwb\box.nif", o.Model, ignoreCase: true);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(w.MeshFile))).ToLowerInvariant();
        Assert.Equal(sha, o.ModelSha256);

        File.WriteAllText(w.MeshFile, "nif bytes, version two");
        ObjectState o2;
        await using (var q = await Start(w)) o2 = Assert.Single(await q.ReadObjectsAsync(new[] { (TiltedRefWorld.Ref, Drawn) }));
        var row = new RowState("Holds", "s", "b");
        var m = Decisions.Compare(new Decision(w.Instance, "Default", "geometry.object-overlap", "k", "intentional", "r", DateTimeOffset.UtcNow, row, new[] { o }, "srv"),
                                  row, new[] { o2 }, "srv");
        Assert.Equal(Standing.Reconsider, m.Standing);
        Assert.Contains("mesh", m.Sentence);
    }
}
