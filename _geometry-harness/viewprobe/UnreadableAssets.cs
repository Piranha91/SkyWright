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
    /// caller can print what THIS render found without printing what an earlier shot found.</summary>
    public static Entry Record(string kind, string asset, string? resolved, string reason)
    {
        var e = new Entry(kind, asset, resolved, reason);
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

    /// <summary>What THIS render found, as opposed to what the process has accumulated. A worklist
    /// runs many shots through one process, and a census line that reprinted an earlier shot's
    /// assets would describe a picture nobody is looking at.</summary>
    static readonly System.Threading.ThreadLocal<List<Entry>> ThisRender = new(() => new List<Entry>());

    // ------------------------------------------------------------------ classification

    /// <summary>Classify the paths a render swallowed. Every decision is made by REDOING the read
    /// that failed, so "found" is the resolver's own answer and "unreadable" is a read that failed
    /// again — not an inference from a count.</summary>
    public static void Classify(GameAssetResolver resolver,
                                IEnumerable<string> missingTextures,
                                IEnumerable<string> meshesWithNoGeometry,
                                IReadOnlyDictionary<string, string>? meshReadErrors = null)
    {
        ThisRender.Value!.Clear();
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
            var measured = NifGeometry.Measure(bytes);
            if (measured.Model is null)
            {
                Add(Record("mesh", rel, path,
                           $"found and could not be parsed ({measured.FailureKind}): {measured.Error}"));
                continue;
            }
            // PARSED. Zero shapes here is a mesh with nothing to draw - a collision-only mesh is
            // exactly that, and a healthy campaign exterior contains them. Not an unreadable asset.
        }
    }

    static void Add(Entry e) => ThisRender.Value!.Add(e);

    // ------------------------------------------------------------------ what a run says

    /// <summary>The census block, printed where the renderer prints its own counts and only when
    /// there is something to say. A healthy run's console is unchanged, to the character.</summary>
    public static void PrintCensus()
    {
        var entries = (IReadOnlyList<Entry>)ThisRender.Value!.ToArray();
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
        var entries = Entries;
        if (entries.Count == 0) return null;
        var opt = OptOutSource;
        return new
        {
            count = entries.Count,
            status = opt is null ? "the run failed on these" : "the run was allowed to succeed",
            exitCode = opt is null ? FailExitCode : 0,
            suppressedBy = opt,
            note = "assets found and unreadable across this PROCESS (a worklist's earlier shots "
                 + "included); absent assets, and assets that parsed with nothing to draw, are not "
                 + "listed here",
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
        RunLog.Current?.Note(opt is null
            ? $"unreadable assets: {Count} found and could not be read; exit {FailExitCode}"
            : $"unreadable assets: {Count} found and could not be read; exit {FailExitCode} suppressed by {opt}");
        return opt is null ? FailExitCode : 0;
    }
}
