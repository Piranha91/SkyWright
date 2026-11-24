using System.Globalization;
using System.Text.Json;

namespace Workbench;

/// <summary>Where the houseCARL server reads and writes: an explicit game Data folder, MO2 mods folder and profile
/// folder (the server's explicit-paths mode), so the profile is never whatever MO2 last had selected; and the Output
/// Folder a new patch's houseCARL folder goes in (null: the instance's mods folder, the server's default lane).</summary>
internal sealed record WriteTarget(string Instance, string Profile, string DataDir, string? Output = null)
{
    /// <summary>The folder new patches' houseCARL folders are made in.</summary>
    internal string PatchRoot => Output ?? ModsDir;
    internal string Game => Path.GetDirectoryName(Path.GetFullPath(DataDir.TrimEnd('\\', '/')))!;
    internal string ModsDir => Path.Combine(HousecarlCore.Mo2Instance.LayoutRoot(Instance), "mods");
    internal string ProfileDir => Path.Combine(HousecarlCore.Mo2Instance.LayoutRoot(Instance), "profiles", Profile);
    public override string ToString() => $"instance '{Instance}', profile '{Profile}'" + (Output is null ? "" : $", output '{Output}'");
}

/// <summary>Writes the staged edits as placement overrides through houseCARL's own write path (<c>housecarl_apply</c>;
/// <c>housecarl_records</c> to check and read back), its tool code compiled in (<see cref="HousecarlLibrary"/>): no
/// houseCARL program is started. Opened by <see cref="OpenInProcess"/>; the app's callers get one from
/// <see cref="HousecarlWriters"/>.</summary>
internal sealed class PatchWriter : IAsyncDisposable
{
    readonly HousecarlLibrary _library;
    internal WriteTarget Target { get; }
    internal string ServerExe { get; }
    internal string ServerSha256 { get; }
    internal Action<string> Log { get; }

    PatchWriter(HousecarlLibrary library, WriteTarget target, string exe, string sha, Action<string> log)
    { _library = library; Target = target; ServerExe = exe; ServerSha256 = sha; Log = log; }

    /// <summary>The refusals before anything opens: the Output Folder rule, the rulebook, the profile.</summary>
    static void Preflight(WriteTarget target, string corpusPath)
    {
        if (target.Output is null)
            throw new InvalidOperationException($"writes to {target} are refused: no Output Folder is chosen; choose one in the launcher or set WB_OUTPUT (the instance's mods folder itself is allowed).");
        if (OutputFolderRule.Check(target.PatchRoot, target.Instance, target.Profile, target.Game) is { } why)
            throw new InvalidOperationException($"writes to {target} are refused: {why}");
        if (!File.Exists(corpusPath))
            throw new InvalidOperationException($"the houseCARL rulebook is not at '{corpusPath}'; run scripts\\build.ps1 -Rulebook, or set WB_CORPUS.");
        if (!Directory.Exists(target.ProfileDir))
            throw new InvalidOperationException($"the profile folder '{target.ProfileDir}' does not exist; name a profile of that instance.");
    }

    /// <summary>houseCARL's code in this process against <paramref name="target"/>, with no program started, after the
    /// refusals of <see cref="Preflight"/>. <paramref name="dataDir"/> holds its store and spilled results.</summary>
    internal static PatchWriter OpenInProcess(WriteTarget target, string dataDir, string corpusPath, Action<string> log)
    {
        Preflight(target, corpusPath);
        var lib = HousecarlLibrary.Open(target, dataDir, corpusPath);
        log($"houseCARL in process: {HousecarlLibrary.AssemblyPath} ({HousecarlLibrary.Identity}); writes go to {target}");
        return new PatchWriter(lib, target, HousecarlLibrary.AssemblyPath, HousecarlLibrary.Identity, log);
    }

    /// <summary>One houseCARL tool call: the tool's own answer, or its refusal as one sentence keeping houseCARL's reason
    /// (<see cref="HousecarlAnswer"/>, the app's one reading of a refusal).</summary>
    internal async Task<string> CallAsync(string tool, Dictionary<string, object?> args, CancellationToken ct = default) =>
        HousecarlAnswer.Checked(tool, await _library.CallAsync(tool, args, ct));

    static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    static string V(HousecarlCore.Vec3 v) => $"{F(v.X)},{F(v.Y)},{F(v.Z)}";

    /// <summary>The ops one commit writes: Placement.Position and Placement.Rotation per edited reference.</summary>
    internal static List<Dictionary<string, object?>> Ops(IEnumerable<PendingEdit> edits) =>
        edits.SelectMany(e => new[]
        {
            new Dictionary<string, object?> { ["formid"] = e.Ref.ToString(), ["field_path"] = "Placement.Position", ["value"] = V(e.After.Position) },
            new Dictionary<string, object?> { ["formid"] = e.Ref.ToString(), ["field_path"] = "Placement.Rotation", ["value"] = V(e.After.Rotation) },
        }).ToList();

    /// <summary>The winner's Placement as the server reads it: (position, rotation), or a refusal.</summary>
    internal async Task<(HousecarlCore.Vec3 Pos, HousecarlCore.Vec3 Rot)> ReadPlacementAsync(string formKey, string? source = null, CancellationToken ct = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["formids"] = new[] { formKey },
            ["project"] = new Dictionary<string, object?> { ["form"] = "fields", ["fields"] = new[] { "Placement" }, ["depth"] = 2 },
            ["format"] = "json",
        };
        if (source is not null) args["source"] = source;
        var text = await CallAsync("housecarl_records", args, ct);
        return ParsePlacement(text, formKey);
    }

    /// <summary>Pull Placement.Position and Placement.Rotation out of a records json answer.</summary>
    internal static (HousecarlCore.Vec3 Pos, HousecarlCore.Vec3 Rot) ParsePlacement(string json, string formKey)
    {
        HousecarlCore.Vec3? pos = null, rot = null;
        using var doc = JsonDocument.Parse(json);
        foreach (var el in Walk(doc.RootElement))
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("path", out var p) || !el.TryGetProperty("value", out var v)) continue;
            var path = p.GetString();
            if (path == "Placement.Position") pos = Parse(v);
            else if (path == "Placement.Rotation") rot = Parse(v);
        }
        if (pos is null || rot is null)
            throw new InvalidOperationException($"the server's answer for {formKey} carried no Placement.Position and Placement.Rotation; read it with housecarl_records to see why.");
        return (pos.Value, rot.Value);

        static HousecarlCore.Vec3 Parse(JsonElement v)
        {
            var s = v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();
            var parts = s.Trim('[', ']', '(', ')', ' ').Split(',');
            if (parts.Length != 3) throw new InvalidOperationException($"a placement value '{s}' is not three numbers.");
            float P(string x) => float.Parse(x.Trim(), CultureInfo.InvariantCulture);
            return new HousecarlCore.Vec3(P(parts[0]), P(parts[1]), P(parts[2]));
        }
        static IEnumerable<JsonElement> Walk(JsonElement e)
        {
            yield return e;
            if (e.ValueKind == JsonValueKind.Object) foreach (var c in e.EnumerateObject()) foreach (var x in Walk(c.Value)) yield return x;
            else if (e.ValueKind == JsonValueKind.Array) foreach (var c in e.EnumerateArray()) foreach (var x in Walk(c)) yield return x;
        }
    }

    /// <summary>Write (or dry-run) the net edits into one new patch, after checking each edit's <c>Before</c> is the
    /// winner the server reads. Returns the tool's own text.</summary>
    internal async Task<string> CommitAsync(IReadOnlyList<PendingEdit> net, string patchName, bool dryRun, string? into = null,
                                            float tolerance = 0.01f, CancellationToken ct = default, string? outPath = null)
    {
        if (net.Count == 0) throw new InvalidOperationException("there are no staged edits to write; move or rotate a reference first.");
        foreach (var e in net)
        {
            var (pos, rot) = await ReadPlacementAsync(e.Ref.ToString(), ct: ct);
            if (Mismatch(e.Ref, pos, rot, e.Before, tolerance) is { } why) throw new InvalidOperationException(why);
        }
        var args = new Dictionary<string, object?> { ["ops"] = Ops(net), ["dry_run"] = dryRun };
        if (into is not null) args["into"] = into;
        else
        {
            args["patch"] = patchName;
            // a trial patch goes to its own folder (item 8); otherwise manual mode writes under the Output Folder
            if ((outPath ?? Target.Output) is { } o) args["out_path"] = o;
        }
        Log($"{(dryRun ? "dry run" : "WRITE")} of {net.Count} placement edit(s) to {Target}, {(into is not null ? $"into '{into}'" : $"new patch '{patchName}'")}");
        return await CallAsync("housecarl_apply", args, ct);
    }

    /// <summary>One sentence naming the parent records a placement override carries with it (stage-2 review L-b): the
    /// write path copies each edited reference's CELL, and for an exterior its WRLD, as they win when the patch is written,
    /// so a patch loaded last masks any later edit to those records.</summary>
    internal static string ParentRecordsSentence(IEnumerable<(Mutagen.Bethesda.Plugins.FormKey Ref, Mutagen.Bethesda.Plugins.FormKey? Cell)> edits,
                                                 Mutagen.Bethesda.Plugins.FormKey? worldspace)
    {
        var cells = edits.Select(e => e.Cell is { } c ? c.ToString() : $"(the cell of {e.Ref}, not recorded by the scene)").Distinct().ToList();
        string parents = (cells.Count == 1 ? "cell " : "cells ") + string.Join(", ", cells)
                         + (worldspace is { IsNull: false } w ? $" and worldspace {w}" : "");
        return $"The patch also carries {parents} as they win now, so loaded last it masks any later edit to those records.";
    }

    static bool Near(HousecarlCore.Vec3 a, HousecarlCore.Vec3 b, float t) =>
        MathF.Abs(a.X - b.X) <= t && MathF.Abs(a.Y - b.Y) <= t && MathF.Abs(a.Z - b.Z) <= t;
    static bool NearAngles(HousecarlCore.Vec3 a, HousecarlCore.Vec3 b, float t)
    {
        static float D(float x, float y) { var d = MathF.Abs(x - y) % (2 * MathF.PI); return MathF.Min(d, 2 * MathF.PI - d); }
        return D(a.X, b.X) <= t && D(a.Y, b.Y) <= t && D(a.Z, b.Z) <= t;
    }

    /// <summary>The one placement check: null when the server's winning placement <paramref name="pos"/>/<paramref name="rot"/>
    /// is the pose the scene drew, else the one sentence every server answer about that reference is refused with.</summary>
    internal string? Mismatch(Mutagen.Bethesda.Plugins.FormKey key, HousecarlCore.Vec3 pos, HousecarlCore.Vec3 rot, ViewProbe.RefPlacementEdit.Pose drawn,
                              float tolerance = 0.01f)
        => Near(pos, drawn.Position, tolerance) && NearAngles(rot, drawn.Rotation, 1e-4f) ? null
         : $"{key}'s winning placement on the server ({V(pos)} / {V(rot)}) is not the one the scene drew "
           + $"({V(drawn.Position)} / {V(drawn.Rotation)}); the scene and {Target} are not the same configuration.";

    /// <summary>Null when the server's winning placement of <paramref name="key"/> is the one the scene drew, else the one
    /// sentence a commit refuses with. Every server answer about a drawn reference goes through it first.</summary>
    internal async Task<string?> PlacementMismatchAsync(Mutagen.Bethesda.Plugins.FormKey key, ViewProbe.RefPlacementEdit.Pose drawn, CancellationToken ct = default)
    {
        var (pos, rot) = await ReadPlacementAsync(key.ToString(), ct: ct);
        return Mismatch(key, pos, rot, drawn);
    }

    /// <summary>One records read of many references, json: each record's winner and its fields, by FormKey.</summary>
    async Task<Dictionary<Mutagen.Bethesda.Plugins.FormKey, (string Winner, List<FieldValue> Fields)>> ReadRecordsAsync(
        IEnumerable<Mutagen.Bethesda.Plugins.FormKey> keys, object project, CancellationToken ct)
    {
        var list = keys.Distinct().ToList();
        var result = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, (string, List<FieldValue>)>();
        // in chunks, so each answer stays under the server's render ceiling; a cut answer is refused below, never read short
        foreach (var chunk in list.Chunk(60))
        {
        var text = await CallAsync("housecarl_records", new Dictionary<string, object?>
        {
            ["formids"] = chunk.Select(k => k.ToString()).ToArray(), ["project"] = project, ["format"] = "json", ["limit"] = chunk.Length + 10,
            ["max_chars"] = 1_000_000,
        }, ct);
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("records", out var recs))
            throw new InvalidOperationException("the server's records answer carried no records; read it with housecarl_records to see why.");
        if (doc.RootElement.TryGetProperty("truncated", out var cut) && cut.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException($"the server cut its records answer for {chunk.Length} references short; read them with housecarl_records to see why.");
        foreach (var r in recs.EnumerateArray())
        {
            var fid = r.TryGetProperty("formid", out var f) ? f.GetString() : null;
            if (fid is null || !Mutagen.Bethesda.Plugins.FormKey.TryFactory(fid, out var k)) continue;
            if (r.TryGetProperty("error", out var err))
                throw new InvalidOperationException($"the server could not read {fid}: {err.GetString()}");
            var fields = new List<FieldValue>();
            if (r.TryGetProperty("fields", out var fs))
                foreach (var x in fs.EnumerateArray())
                    if (x.TryGetProperty("path", out var path) && x.TryGetProperty("value", out var value))
                        fields.Add(new FieldValue(path.GetString()!, value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()));
            result[k] = (r.TryGetProperty("winner", out var w) ? w.GetString() ?? "" : "", fields);
        }
        }
        foreach (var k in list)
            if (!result.ContainsKey(k)) throw new InvalidOperationException($"the server's answer had no row for {k}; read it with housecarl_records to see why.");
        return result;
    }

    static (HousecarlCore.Vec3 Pos, HousecarlCore.Vec3 Rot)? PlacementOf(List<FieldValue> fields)
    {
        string? p = fields.FirstOrDefault(f => f.Path == "Placement.Position")?.Value, r = fields.FirstOrDefault(f => f.Path == "Placement.Rotation")?.Value;
        if (p is null || r is null) return null;
        static HousecarlCore.Vec3 Parse(string s)
        {
            var parts = s.Trim('[', ']', '(', ')', ' ').Split(',');
            float P(string x) => float.Parse(x.Trim(), CultureInfo.InvariantCulture);
            return new HousecarlCore.Vec3(P(parts[0]), P(parts[1]), P(parts[2]));
        }
        return (Parse(p), Parse(r));
    }

    void CheckAll(IReadOnlyList<(Mutagen.Bethesda.Plugins.FormKey Ref, ViewProbe.RefPlacementEdit.Pose Drawn)> refs,
                  Dictionary<Mutagen.Bethesda.Plugins.FormKey, (string Winner, List<FieldValue> Fields)> read)
    {
        foreach (var (k, drawn) in refs)
        {
            if (PlacementOf(read[k].Fields) is not { } pr)
                throw new InvalidOperationException($"the server's answer for {k} carried no Placement.Position and Placement.Rotation; read it with housecarl_records to see why.");
            if (Mismatch(k, pr.Pos, pr.Rot, drawn) is { } why) throw new InvalidOperationException(why);
        }
    }

    /// <summary>The objects a decision is keyed on, read from the server for references the scene drew at these poses:
    /// each reference's winning record (every field), its base's geometry fields (model, bounds), and the base model's
    /// winning copy with its content hash (<c>housecarl_asset_status hash=true</c>). Each placement is checked first.</summary>
    internal async Task<IReadOnlyList<ObjectState>> ReadObjectsAsync(IReadOnlyList<(Mutagen.Bethesda.Plugins.FormKey Ref, ViewProbe.RefPlacementEdit.Pose Drawn)> refs,
                                                                     CancellationToken ct = default)
    {
        var read = await ReadRecordsAsync(refs.Select(r => r.Ref), new Dictionary<string, object?> { ["form"] = "everything", ["depth"] = 2 }, ct);
        CheckAll(refs, read);
        var bases = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, Mutagen.Bethesda.Plugins.FormKey>();
        foreach (var (k, (_, fields)) in read)
            if (fields.FirstOrDefault(f => f.Path == "Base")?.Value is { } b && Mutagen.Bethesda.Plugins.FormKey.TryFactory(b.Split(' ')[0], out var bk)) bases[k] = bk;
        var baseRead = await ReadRecordsAsync(bases.Values, new Dictionary<string, object?> { ["form"] = "fields", ["fields"] = Decisions.BaseGeometryFields, ["depth"] = 2 }, ct);
        var models = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, string>();
        foreach (var (bk, (_, fields)) in baseRead)
            if (fields.FirstOrDefault(f => f.Path == "Model.File")?.Value is { Length: > 0 } m)
                models[bk] = m.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) || m.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase) ? m : "meshes\\" + m;
        var hashes = new Dictionary<string, (string? Winner, string? Sha)>(StringComparer.OrdinalIgnoreCase);
        if (models.Count > 0)
        {
            var text = await CallAsync("housecarl_asset_status", new Dictionary<string, object?>
            {
                ["asset_paths"] = models.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), ["hash"] = true, ["format"] = "json",
            }, ct);
            using var doc = JsonDocument.Parse(text);
            foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
            {
                var path = r.GetProperty("path").GetString()!;
                string? win = r.TryGetProperty("winner", out var w) && w.ValueKind == JsonValueKind.Object && w.TryGetProperty("name", out var src) ? src.GetString() : null;
                string? sha = r.TryGetProperty("winner_sha256", out var sh) && sh.ValueKind == JsonValueKind.String ? sh.GetString() : null;
                hashes[path] = (win, sha);
            }
        }
        return refs.Select(r =>
        {
            var (winner, fields) = read[r.Ref];
            bases.TryGetValue(r.Ref, out var bk);
            var bf = bases.ContainsKey(r.Ref) && baseRead.TryGetValue(bk, out var b) ? b.Fields : new List<FieldValue>();
            string? model = bases.ContainsKey(r.Ref) && models.TryGetValue(bk, out var m) ? m : null;
            var h = model is not null && hashes.TryGetValue(model.Replace('/', '\\'), out var hh) ? hh : model is not null && hashes.TryGetValue(model, out var h2) ? h2 : (null, null);
            return new ObjectState(r.Ref.ToString(), winner, fields, bases.ContainsKey(r.Ref) ? bk.ToString() : null, bf, model, h.Winner, h.Sha);
        }).ToList();
    }

    /// <summary>The winning plugin of each reference, read in one call, each checked against the pose the scene drew.</summary>
    internal async Task<(IReadOnlyDictionary<Mutagen.Bethesda.Plugins.FormKey, string> Winners, IReadOnlyList<Mutagen.Bethesda.Plugins.FormKey> Mismatched)> WinnersAsync(
        IReadOnlyList<(Mutagen.Bethesda.Plugins.FormKey Ref, ViewProbe.RefPlacementEdit.Pose? Drawn)> refs, CancellationToken ct = default)
    {
        var read = await ReadRecordsAsync(refs.Select(r => r.Ref), new Dictionary<string, object?> { ["form"] = "fields", ["fields"] = new[] { "Placement" }, ["depth"] = 2 }, ct);
        // the one placement check per reference; a reference drawn elsewhere is left out and listed, not a refusal of all
        var mismatched = new List<Mutagen.Bethesda.Plugins.FormKey>();
        foreach (var (k, drawn) in refs)
        {
            if (drawn is not { } pose) continue;
            if (PlacementOf(read[k].Fields) is not { } pr || Mismatch(k, pr.Pos, pr.Rot, pose) is not null) mismatched.Add(k);
        }
        return (read.Where(kv => !mismatched.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value.Winner), mismatched);
    }

    /// <summary>Undo a written edit: remove the reference's override from the patch in <paramref name="patchFolder"/>
    /// (<c>housecarl_remove into=</c>), never by writing the old pose back, which would leave an override that masks
    /// later edits. The patch file is kept even when it holds no placed reference afterwards.</summary>
    internal async Task<string> RevertWrittenAsync(Mutagen.Bethesda.Plugins.FormKey key, string patchFolder, CancellationToken ct = default)
    {
        var text = await CallAsync("housecarl_remove", new Dictionary<string, object?> { ["formids"] = new[] { key.ToString() }, ["into"] = patchFolder }, ct);
        var plugin = Directory.EnumerateFiles(patchFolder, "*.esp").FirstOrDefault();
        int left = -1;
        if (plugin is not null)
            using (var mod = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(plugin, Mutagen.Bethesda.Skyrim.SkyrimRelease.SkyrimSE))
                left = mod.EnumerateMajorRecords<Mutagen.Bethesda.Skyrim.IPlacedGetter>().Count();
        var first = text.Split('\n')[0].Trim();
        return $"removed {key}'s override from {Path.GetFileName(plugin) ?? patchFolder} ({first})"
               + (left == 0 ? "; the patch now holds no placed reference, and the file is kept (delete it yourself if you no longer want it)." : left > 0 ? $"; {left} placed reference(s) remain in it." : ".");
    }

    /// <summary>Closes the service, waiting for its calls; never on a window's thread (<see cref="HousecarlLibrary.Dispose"/>).</summary>
    public ValueTask DisposeAsync() { _library.Dispose(); return ValueTask.CompletedTask; }
}
