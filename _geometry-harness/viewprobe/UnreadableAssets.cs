using System.Text.Json;
using SkyRender;
using SkyRender.Character;
using HousecarlCore;
using HousecarlCore.Progress;

namespace ViewProbe;

/// <summary>
/// AN ASSET THAT WAS FOUND AND COULD NOT BE READ IS NOT A PICTURE THAT CAME OUT RIGHT.
///
/// The rendering library collapses two very different things into one answer. A mesh it cannot parse
/// and a mesh that is not installed both end as "no resolvable mesh"; a texture whose file will not
/// decode and a texture nobody shipped both end as a white fallback. Both are counted, neither is
/// named, and the run exits 0 either way — so a render that quietly dropped a building looked exactly
/// like a render of a load order that is missing a decal.
///
/// Those two cases are not the same, and only one of them is a defect in the picture:
///
///   * <b>absent</b> — the resolver could not find the asset anywhere. The load order simply does not
///     contain it. Every campaign render has some of these and they are not this tool's business.
///   * <b>present, valid, nothing to draw</b> — the file was found and parsed, and it has no render
///     geometry. Collision-only meshes are exactly this, and a healthy campaign exterior contains
///     them. Failing a run over these would fail every healthy render, which is why the rule below
///     turns on whether the READ succeeded and never on whether the result was empty.
///   * <b>present and unreadable</b> — the file was found and the read failed: the parser refused it,
///     the decoder threw, the extraction failed, or it vanished between being resolved and being
///     opened. THIS is the one that must never be rendered around in silence.
///
/// The third class fails the run (exit 3) unless the caller deliberately opts out. The opt-out is
/// honoured as a flag and as an environment variable — the variable so that an existing caller can
/// pass it through without its command line changing — and whenever it is what suppressed the exit
/// code, the census block, the report block and the run record all say so and name which of the two
/// did it. An ambient switch that silently restored the old behaviour would reopen the door this
/// closes.
/// </summary>
static class UnreadableAssets
{
    public const string OptOutFlag = "--allow-unreadable-assets";
    public const string OptOutEnv = "VP_ALLOW_UNREADABLE_ASSETS";

    /// <summary>One asset that was found and could not be read.</summary>
    public sealed record Entry(string Kind, string Asset, string? ResolvedPath, string Reason,
                               IReadOnlyList<Placement>? At = null, float? ModelRadius = null);

    /// <summary>One placed reference that uses a mesh the renderer drew nothing from: where the
    /// picture has a hole. Printed under the census entry so a planner can tell a hole beside the
    /// subject from one across the cell.</summary>
    public sealed record Placement(string FormKey, Vec3 Position, float Scale);

    static readonly object Gate = new();
    static readonly List<Entry> Found = new();
    static bool _flagOptOut;

    /// <summary>Set by the argument parser. The environment is read live so a test can set it
    /// per-process without the flag.</summary>
    public static void OptOutByFlag() { lock (Gate) _flagOptOut = true; }

    static bool EnvOptOut => Environment.GetEnvironmentVariable(OptOutEnv) == "1";

    /// <summary>Which of the two suppressed the exit code, or null when neither did. Named, never
    /// merely "suppressed": an ambient variable set once in a profile is exactly the thing a reader
    /// needs told apart from a flag someone typed on this command line.</summary>
    public static string? OptOutSource
    {
        get
        {
            bool flag;
            lock (Gate) flag = _flagOptOut;
            if (flag && EnvOptOut) return OptOutFlag + " (and " + OptOutEnv + "=1 in the environment)";
            if (flag) return OptOutFlag;
            if (EnvOptOut) return OptOutEnv + "=1 in the environment";
            return null;
        }
    }

    public static IReadOnlyList<Entry> Entries { get { lock (Gate) return Found.ToArray(); } }
    public static int Count { get { lock (Gate) return Found.Count; } }

    /// <summary>Remember one, for the exit code and the report. Returns the entry either way, so a
    /// caller can print what THIS render found without printing what an earlier shot found.
    ///
    /// ONE PLACE FOR THE PATH-LENGTH CAUSE. Every entry is built here — the census line, the report
    /// block and the run record all read what this returns — so a path too long for the library's
    /// reader is named once, in the reason, for whichever kind of asset hit it. See
    /// <see cref="AssetPathBudget"/> for what was measured and why an archive entry is left
    /// alone.</summary>
    public static Entry Record(string kind, string asset, string? resolved, string reason)
    {
        var e = new Entry(kind, asset, resolved, reason + AssetPathBudget.Clause(kind, resolved));
        lock (Gate)
        {
            if (!Found.Any(f => f.Kind == kind && string.Equals(f.Asset, asset, StringComparison.OrdinalIgnoreCase)))
                Found.Add(e);
        }
        return e;
    }

    /// <summary>An extraction that failed: the asset IS in an archive — it was located there — and
    /// could not be written out. Recorded where it happens because the library turns it into the same
    /// "not found" everything else collapses into.</summary>
    public static void ExtractionFailed(string subpath, string archive, string? error)
        => Record("archive", subpath, archive,
                  "found in an archive and the extraction failed: " + (error ?? "(no detail)"));

    /// <summary>What THIS SHOT found, as opposed to what the process has accumulated. A worklist
    /// runs many shots through one process: a census line, or a report block, that carried an
    /// earlier shot's assets would describe a picture nobody is looking at. Keyed, so the two
    /// callers inside one shot - the scene and the NPC pipeline - both add to it and a second call
    /// from the same one cannot double-count.</summary>
    static readonly Dictionary<(string, string), Entry> ThisShot = new();

    /// <summary>Start a shot. Called once per shot, before anything is built for it.</summary>
    public static void BeginShot()
    {
        lock (Gate) { ThisShot.Clear(); ThisShotByRule.Clear(); }
    }

    /// <summary>This shot's meshes that the rendering library skips by its own rule (not failures;
    /// see <see cref="RendererRule"/>). Kept apart from <see cref="ThisShot"/> so nothing about them
    /// can reach the exit code.</summary>
    static readonly Dictionary<string, Entry> ThisShotByRule = new(StringComparer.OrdinalIgnoreCase);

    static void AddByRule(Entry e)
    {
        lock (Gate) ThisShotByRule[e.Asset] = e;
    }

    public static IReadOnlyList<Entry> ShotNotDrawnByRule { get { lock (Gate) return ThisShotByRule.Values.ToArray(); } }

    /// <summary>What this shot found, in the order it was found.</summary>
    public static IReadOnlyList<Entry> ShotEntries { get { lock (Gate) return ThisShot.Values.ToArray(); } }

    // ------------------------------------------------------------------ classification

    /// <summary>Classify the paths a render swallowed. Every decision is made by REDOING the read
    /// that failed, so "found" is the resolver's own answer and "unreadable" is a read that failed
    /// again — not an inference from a count.</summary>
    public static void Classify(GameAssetResolver resolver,
                                IEnumerable<string> missingTextures,
                                IEnumerable<string> meshesWithNoGeometry,
                                IReadOnlyDictionary<string, string>? meshReadErrors = null,
                                IReadOnlyDictionary<string, List<Placement>>? placements = null)
    {
        void AddMesh(Entry e, float? radius)
        {
            List<Placement>? at = null;
            placements?.TryGetValue(e.Asset, out at);
            Add(e with { At = at?.ToArray(), ModelRadius = radius });
        }


        foreach (var tex in missingTextures ?? Array.Empty<string>())
        {
            string? path;
            try { path = resolver.ResolveAssetPath(tex); }
            catch (Exception ex)
            {
                Add(Record("texture", tex, null, $"re-resolving it failed: {ex.GetType().Name}: {ex.Message}"));
                continue;
            }
            if (path is null) continue;                       // absent: not ours
            Add(Record("texture", tex, path,
                       File.Exists(path)
                           ? "found on disk and the decoder would not read it"
                           : "the resolver names a file that is not there"));
        }

        foreach (var rel in meshesWithNoGeometry ?? Array.Empty<string>())
        {
            if (meshReadErrors is not null && meshReadErrors.TryGetValue(rel, out var thrown)
                && thrown.StartsWith("renderer geometry read failed", StringComparison.Ordinal))
            {
                // The renderer's own read threw. A thrown read is a failed read whatever a second
                // opinion says, so this does not need - and must not wait for - a re-parse.
                string? p0 = null;
                try { p0 = resolver.ResolveAssetPath(rel); } catch (Exception) { }
                AddMesh(Record("mesh", rel, p0, thrown), null);
                continue;
            }
            string? path;
            try { path = resolver.ResolveAssetPath(rel); }
            catch (Exception ex)
            {
                AddMesh(Record("mesh", rel, null, $"re-resolving it failed: {ex.GetType().Name}: {ex.Message}"), null);
                continue;
            }
            if (path is null) continue;                       // absent: not ours
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex)
            {
                AddMesh(Record("mesh", rel, path, $"found and could not be opened: {ex.GetType().Name}: {ex.Message}"), null);
                continue;
            }
            var verdict = ClassifyMesh(bytes, path);
            if (verdict.Reason is not null) AddMesh(Record("mesh", rel, path, verdict.Reason), verdict.ModelRadius);
            else if (verdict.NotDrawnByRule is not null) AddByRule(new Entry("mesh", rel, path, verdict.NotDrawnByRule));
            else if (Environment.GetEnvironmentVariable("VP_CV_VERBOSE") == "1")
                Console.Error.WriteLine($"   [unreadable] dismissed '{rel}': the parse agrees there is "
                                      + $"nothing to draw ({verdict.DrawableShapes} drawable shape(s))");
        }
    }

    static void Add(Entry e)
    {
        lock (Gate) ThisShot[(e.Kind, e.Asset.ToLowerInvariant())] = e;
    }

    /// <summary>How many of this shot's white-fallback textures were found and unreadable, so the
    /// census line above them does not say "could NOT find" about a file it just found.</summary>
    public static int ShotTextureCount => ShotEntries.Count(e => e.Kind == "texture");

    /// <summary>What an independent parse says about a mesh the renderer produced nothing from.
    /// <see cref="MeshVerdict.Reason"/> is null when the asset is FINE - and it is fine in exactly
    /// one case: the parse succeeded AND agrees there was nothing to draw.
    ///
    /// The last clause is the whole point. Dismissing a mesh because the parse merely succeeded
    /// would dismiss the original defect's own mesh mode: a file torn by another process reads as
    /// no geometry WITHOUT throwing, and by the time it is re-read it is whole again. It would also
    /// dismiss any other transient failure inside the library. So the two answers have to AGREE:
    /// a mesh that parses with drawable shapes and that the renderer drew nothing from is an asset
    /// that was found and not drawn, whatever the reason.</summary>
    /// <param name="NotDrawnByRule">Set, and <see cref="Reason"/> null, when the mesh has drawable
    /// shapes but EVERY shape is one the rendering library declines to draw by its own documented
    /// rule (see <see cref="RendererRule"/>). The picture is missing it by design, not by a failed
    /// read, so it is named on the console and in the run record and never fails the run. The
    /// planes report is left as it was: its references already say "renderer produced no
    /// geometry".</param>
    /// <param name="ModelRadius">Distance from the model origin to the farthest corner of the parsed
    /// render box, when the parse succeeded: with a placement's scale, a sphere that holds the mesh
    /// however it is rotated.</param>
    public readonly record struct MeshVerdict(string? Reason, int DrawableShapes, string FailureKind,
                                              string? NotDrawnByRule = null, float? ModelRadius = null);

    public static MeshVerdict ClassifyMesh(byte[] bytes, string? path = null)
    {
        // The policy entry point: shapes the file marks hidden are excluded, because the renderer
        // does not draw those either and a disagreement about them would be one this tool invented.
        var measured = NifGeometry.MeasureVisibleRender(bytes);
        if (measured.Model is null)
            return new MeshVerdict($"found and could not be parsed ({measured.FailureKind}): {measured.Error}",
                                   0, measured.FailureKind.ToString());
        int drawable = measured.Model.Shapes.Count(sh => sh.TriangleCount > 0);
        float? radius = null;
        if (!measured.Model.Bounds.IsEmpty)
        {
            var (lo, hi) = (measured.Model.Bounds.Min, measured.Model.Bounds.Max);
            float mx = MathF.Max(MathF.Abs(lo.X), MathF.Abs(hi.X)), my = MathF.Max(MathF.Abs(lo.Y), MathF.Abs(hi.Y)),
                  mz = MathF.Max(MathF.Abs(lo.Z), MathF.Abs(hi.Z));
            radius = MathF.Sqrt(mx * mx + my * my + mz * mz);
        }
        if (drawable == 0) return new MeshVerdict(null, 0, "None");
        const string disagree = "found, and it parses with {0} drawable shape(s), but the renderer produced no "
                              + "geometry from it - the read the renderer did and the read done here disagree";
        // THE SAME QUESTION THE RENDERER ASKED, with the renderer's own reader. Only an answer that
        // every shape is one the library skips by rule turns the disagreement into "not drawn by
        // design"; a file that cannot be re-read this way, or any shape the rule would draw, leaves
        // the disagreement standing - so a torn read of a textured mesh still fails the run.
        var rule = path is null ? null : RendererRule(path);
        if (rule is { AnyDrawn: false, Shapes.Count: > 0 })
            return new MeshVerdict(null, drawable, "None",
                "the rendering library skips every shape of it by rule: "
                + string.Join("; ", rule.Value.Shapes.Select(s => $"'{s.Name}' ({s.Shader}) {s.Skip}")));
        return new MeshVerdict(string.Format(disagree, drawable)
                               + (rule is { Error: { } err } ? $" (the renderer's own rule could not be re-read: {err})" : ""),
                               drawable, "None", ModelRadius: radius);
    }

    /// <summary>One shape as the rendering library's shape builder sees it: <see cref="Skip"/> is
    /// null when the library would build it, or the rule that makes it skip the shape.</summary>
    public sealed record RuleShape(string Name, string Shader, string? Skip);

    public readonly record struct RuleResult(IReadOnlyList<RuleShape> Shapes, bool AnyDrawn, string? Error);

    /// <summary>MIRRORS the pre-geometry checks of the rendering library's per-shape build
    /// (<c>NifMeshBuilder.BuildShape</c> in the pinned CharacterViewer.Rendering), with the same
    /// <c>nifly</c> calls in the same order: a shape is skipped when its AppCulled bit is set, when
    /// it has no shader property, when it binds no diffuse and no normal texture and carries no alpha
    /// property (the library's "likely physics/collision mesh" rule), or when it has no vertices.
    /// Skyrim's placed water (<c>BSWaterShaderProperty</c>, no texture set) and its editor-marker
    /// shapes meet the third rule. This is a statement about the library, not about the game: the
    /// game draws a rain barrel's water, and the entry says what the picture lacks.</summary>
    public static RuleResult? RendererRule(string path)
    {
        try
        {
            using var nif = new nifly.NifFile();
            int rc = nif.Load(path);
            if (rc != 0) return new RuleResult(Array.Empty<RuleShape>(), false, $"nifly Load returned {rc}");
            var header = nif.GetHeader();
            using var shapes = nif.GetShapes();
            var list = new List<RuleShape>();
            for (int si = 0; si < shapes.Count; si++)
            {
                var shape = shapes[si];
                string name = shape.name?.get() ?? "?";
                string shader = "no shader";
                string? skip = null;
                var sref = shape.ShaderPropertyRef();
                if (sref is not null && !sref.IsEmpty())
                    shader = header.GetBlockById(sref.index)?.GetType().Name ?? "unknown shader";
                if ((shape.flags & 1u) != 0) skip = "hidden (AppCulled flag)";
                else if (sref is null || sref.IsEmpty()) skip = "no shader property";
                else if (string.IsNullOrWhiteSpace(nif.GetTexturePathByIndex(shape, 0))
                         && string.IsNullOrWhiteSpace(nif.GetTexturePathByIndex(shape, 1))
                         && !shape.HasAlphaProperty())
                    skip = "no diffuse or normal texture and no alpha property";
                else
                {
                    using var verts = nif.GetVertsForShape(shape);
                    if (verts is null || verts.Count == 0) skip = "no vertices";
                }
                list.Add(new RuleShape(name, shader, skip));
            }
            return new RuleResult(list, list.Any(s => s.Skip is null), null);
        }
        catch (Exception ex)
        {
            return new RuleResult(Array.Empty<RuleShape>(), false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary><c>viewprobe --unreadable-asset-smoke &lt;file&gt;...</c>: the mesh decision, over
    /// real files, printing one JSON line per file. The test that uses it owns the oracle - it
    /// knows which of its files is garbage, which is collision-only and which has shapes to
    /// draw - and reads the verdict off this output rather than from a count.</summary>
    public static int MeshSmoke(string[] paths)
    {
        int bad = 0;
        foreach (var path in paths)
        {
            object row;
            try
            {
                var v = ClassifyMesh(File.ReadAllBytes(path), path);
                row = new
                {
                    file = path,
                    bytes = new FileInfo(path).Length,
                    verdict = v.NotDrawnByRule is not null ? "not drawn by the renderer's rule" :
                              v.Reason is null ? "nothing to draw" :
                              v.DrawableShapes > 0 ? "found and not drawn" : "unreadable",
                    drawableShapes = v.DrawableShapes,
                    failureKind = v.FailureKind,
                    reason = v.Reason ?? v.NotDrawnByRule,
                };
            }
            catch (Exception ex)
            {
                bad++;
                row = new { file = path, error = ex.GetType().Name + ": " + ex.Message };
            }
            Console.WriteLine(JsonSerializer.Serialize(row));
        }
        return bad == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ what a run says

    /// <summary>The census block, printed where the renderer prints its own counts and only when
    /// there is something to say. A healthy run's console is unchanged, to the character.</summary>
    public static void PrintCensus()
    {
        var entries = ShotEntries;
        if (entries.Count == 0) return;
        var opt = OptOutSource;
        Console.WriteLine($"   assets FOUND but UNREADABLE: {entries.Count:N0}"
                        + " — the picture is missing what they would have drawn"
                        + (opt is null ? $"; this run fails with exit {FailExitCode}"
                                       : $"; exit {FailExitCode} SUPPRESSED by {opt}"));
        foreach (var e in entries)
        {
            Console.WriteLine($"      {e.Kind,-8} {e.Asset} — {e.Reason}"
                            + (e.ResolvedPath is null ? "" : $" [{e.ResolvedPath}]"));
            // WHERE the hole is, one line per placed reference, in world units; `r` bounds the
            // mesh at that placement's scale, or `?` when no parse measured it. A caller that
            // cannot place the hole has to treat it as anywhere.
            foreach (var a in e.At ?? Array.Empty<Placement>())
                Console.WriteLine(FormattableString.Invariant(
                    $"         at {a.FormKey}  {a.Position.X:0.0},{a.Position.Y:0.0},{a.Position.Z:0.0}  r ")
                    + (e.ModelRadius is { } mr ? FormattableString.Invariant($"{mr * a.Scale:0.0}") : "?"));
        }
    }

    /// <summary>The PNG tEXt value (keyword <c>viewprobe-incomplete</c>) for a frame missing assets
    /// that were found and could not be read, or null when this shot found none. It names every
    /// asset and whether the run failed on them or an opt-out let it succeed.</summary>
    public static string? FrameIncompleteText()
    {
        var entries = ShotEntries;
        if (entries.Count == 0) return null;
        var opt = OptOutSource;
        return $"INCOMPLETE: {entries.Count} asset(s) found and unreadable are not drawn: "
             + string.Join("; ", entries.Select(e => $"{e.Kind} {e.Asset}"))
             + (opt is null ? $". The run failed with exit {FailExitCode}." : $". Exit {FailExitCode} suppressed by {opt}.");
    }

    /// <summary>The meshes the picture lacks by the rendering library's rule, printed only when
    /// there are some, so a console without them is unchanged to the character. Printed whether
    /// or not the unreadable census fires: the two are separate facts about the picture.</summary>
    public static void PrintNotDrawnByRule()
    {
        var entries = ShotNotDrawnByRule;
        if (entries.Count == 0) return;
        Console.WriteLine($"   meshes NOT DRAWN by the renderer's rule: {entries.Count:N0}"
                        + " — found and read; the picture lacks them by design, not by a failed read; the run does not fail");
        foreach (var e in entries)
            Console.WriteLine($"      {e.Kind,-8} {e.Asset} — {e.Reason}");
        RunLog.Current?.Note($"meshes not drawn by the renderer's rule: {entries.Count} ("
                             + string.Join(", ", entries.Select(e => e.Asset)) + ")");
    }

    /// <summary>The same facts for the report, or null when this shot found nothing.
    ///
    /// NULL IS NOT ABSENT, AND THAT IS ON PURPOSE. The report writer emits the key either way, so a
    /// healthy report carries <c>"unreadableAssets": null</c> — one line it did not have before this
    /// signal existed. That line is worth its width because it is the only way a reader can tell
    /// "this build looked and found none" from "this build never looked": the key is MISSING in a
    /// report written by a build from before the check, and null in a healthy one from a build that
    /// has it. A driver reads the three states as: key absent → not checked; null → checked, clean;
    /// object → assets were found and could not be read, and <c>exitCode</c> there says whether an
    /// opt-out let the run succeed anyway.</summary>
    public static object? ReportBlock()
    {
        var entries = ShotEntries;
        if (entries.Count == 0) return null;
        var opt = OptOutSource;
        return new
        {
            count = entries.Count,
            status = opt is null ? "the run failed on these" : "the run was allowed to succeed",
            exitCode = opt is null ? FailExitCode : 0,
            suppressedBy = opt,
            note = "assets found and unreadable in THIS shot; absent assets, and assets that "
                 + "parsed with nothing to draw, are not listed here",
            processTotal = Count,
            assets = entries.Select(e => new { kind = e.Kind, asset = e.Asset, resolved = e.ResolvedPath, reason = e.Reason }).ToArray(),
        };
    }

    public const int FailExitCode = 3;

    /// <summary>THE ONE PLACE THE DEFAULT LIVES. A shot must never return this itself — the worklist
    /// loop stops at the first shot returning non-zero, and a degraded shot must not stop the ones
    /// after it — so the condition is accumulated across the process and spent here, once, on the way
    /// out of Main.</summary>
    public static int FinalExitCode(int rc)
    {
        if (rc != 0) return rc;                     // a real failure outranks this one
        if (Count == 0) return 0;
        var opt = OptOutSource;
        int code = opt is null ? FailExitCode : 0;
        var said = opt is null
            ? $"unreadable assets: {Count} found and could not be read; exit {FailExitCode}"
            : $"unreadable assets: {Count} found and could not be read; exit {FailExitCode} suppressed by {opt}";
        // THE OUTCOME, not only a note. The process-level record takes its outcome from the exit
        // code at process exit, so an opted-out run would be stored as a clean Ok - a degraded
        // render filed as a good one, which is the door this whole signal closes. Partial exists
        // for exactly this, and terminating the record here is what makes it stick.
        RunLog.Current?.Note(said);
        RunLog.Current?.Complete(RunOutcome.Partial, code, said);
        return code;
    }
}
