using System.Text.Json;
using CharacterViewer.Rendering;
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
    public sealed record Entry(string Kind, string Asset, string? ResolvedPath, string Reason);

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
        lock (Gate) ThisShot.Clear();
    }

    /// <summary>What this shot found, in the order it was found.</summary>
    public static IReadOnlyList<Entry> ShotEntries { get { lock (Gate) return ThisShot.Values.ToArray(); } }

    // ------------------------------------------------------------------ classification

    /// <summary>Classify the paths a render swallowed. Every decision is made by REDOING the read
    /// that failed, so "found" is the resolver's own answer and "unreadable" is a read that failed
    /// again — not an inference from a count.</summary>
    public static void Classify(GameAssetResolver resolver,
                                IEnumerable<string> missingTextures,
                                IEnumerable<string> meshesWithNoGeometry,
                                IReadOnlyDictionary<string, string>? meshReadErrors = null)
    {

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
                Add(Record("mesh", rel, p0, thrown));
                continue;
            }
            string? path;
            try { path = resolver.ResolveAssetPath(rel); }
            catch (Exception ex)
            {
                Add(Record("mesh", rel, null, $"re-resolving it failed: {ex.GetType().Name}: {ex.Message}"));
                continue;
            }
            if (path is null) continue;                       // absent: not ours
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex)
            {
                Add(Record("mesh", rel, path, $"found and could not be opened: {ex.GetType().Name}: {ex.Message}"));
                continue;
            }
            var verdict = ClassifyMesh(bytes);
            if (verdict.Reason is not null) Add(Record("mesh", rel, path, verdict.Reason));
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
    public readonly record struct MeshVerdict(string? Reason, int DrawableShapes, string FailureKind);

    public static MeshVerdict ClassifyMesh(byte[] bytes)
    {
        // The policy entry point: shapes the file marks hidden are excluded, because the renderer
        // does not draw those either and a disagreement about them would be one this tool invented.
        var measured = NifGeometry.MeasureVisibleRender(bytes);
        if (measured.Model is null)
            return new MeshVerdict($"found and could not be parsed ({measured.FailureKind}): {measured.Error}",
                                   0, measured.FailureKind.ToString());
        int drawable = measured.Model.Shapes.Count(sh => sh.TriangleCount > 0);
        if (drawable == 0) return new MeshVerdict(null, 0, "None");
        return new MeshVerdict(
            $"found, and it parses with {drawable} drawable shape(s), but the renderer produced no "
            + "geometry from it - the read the renderer did and the read done here disagree",
            drawable, "None");
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
                var v = ClassifyMesh(File.ReadAllBytes(path));
                row = new
                {
                    file = path,
                    bytes = new FileInfo(path).Length,
                    verdict = v.Reason is null ? "nothing to draw" :
                              v.DrawableShapes > 0 ? "found and not drawn" : "unreadable",
                    drawableShapes = v.DrawableShapes,
                    failureKind = v.FailureKind,
                    reason = v.Reason,
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
            Console.WriteLine($"      {e.Kind,-8} {e.Asset} — {e.Reason}"
                            + (e.ResolvedPath is null ? "" : $" [{e.ResolvedPath}]"));
    }

    /// <summary>The same facts for the report. Null when there is nothing to report, so a healthy
    /// report gains nothing it did not have.</summary>
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
