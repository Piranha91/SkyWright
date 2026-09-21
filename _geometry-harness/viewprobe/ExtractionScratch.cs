using System.Diagnostics;
using System.Text.Json;

namespace ViewProbe;

/// <summary>
/// WHERE THE RENDERING LIBRARY EXTRACTS BSA ASSETS, AND WHY IT IS NOT BESIDE THE EXE.
///
/// The library extracts an archived asset to disk before it can read it, and the destination it
/// picks is <c>Path.Combine(AppContext.BaseDirectory, "CharacterViewerCache")</c>, captured once in
/// <c>GameAssetResolver</c>'s constructor. Beside the exe, in other words — the SAME folder for every
/// process started from that build. Two renders started together therefore write the same files at
/// the same moment: one truncates a file while the other reads it, and because every failure on that
/// path is swallowed (a mesh that will not parse becomes "no geometry", a texture that will not
/// decode becomes a white fallback) the run still ends at exit 0 with a picture that is quietly
/// wrong. That is the defect this class exists to remove, and `_geometry-harness/tests/` holds the
/// tests that fail without it.
///
/// The fix is a per-process extraction root, deleted when the process ends:
///
///   * <see cref="Redirect"/> is a set / construct / restore bracket around the ONE line that
///     captures the directory. Nothing else in viewprobe reads <c>AppContext.BaseDirectory</c> inside
///     it, and the library re-reads the slot on every access, so the window is as narrow as the
///     construction itself.
///   * <see cref="RequireInside"/> is the guard. Every extraction destination passes through it; one
///     outside this process's own root REFUSES THE RUN rather than writing. If the library ever stops
///     taking the directory from <c>AppContext.BaseDirectory</c>, that is a loud failure on the first
///     extraction instead of a silent return to the shared folder.
///   * The root is deleted when the process ends. A process that is killed leaves its root behind,
///     so the next process sweeps orphans whose owner is provably dead — see <see cref="Sweep"/>,
///     whose whole contract is what it may NEVER delete.
///
/// Nothing is held between runs: the root exists for one process and no other process may use it.
/// The library's own <c>ClearExtractedFiles()</c> is deliberately NOT called — deleting the root is
/// strictly stronger, and the library documents that method as unsafe to call while a render is in
/// progress, which at process exit cannot be established.
/// </summary>
static class ExtractionScratch
{
    /// <summary>The file that makes a directory viewprobe's. A directory without it is untouchable,
    /// whatever it is called.</summary>
    public const string MarkerName = "viewprobe-extract.json";
    const int MarkerSchema = 1;
    const string BaseDirectoryKey = "APP_CONTEXT_BASE_DIRECTORY";

    static readonly object Gate = new();
    static string? _root;
    static bool _exitHooked;

    /// <summary>This process's extraction root once claimed, else null. Null after release.</summary>
    public static string? ClaimedRoot { get { lock (Gate) return _root; } }

    // ----------------------------------------------------------------- where

    /// <summary>The parent every process's root is created under. Resolved the way the run store
    /// resolves its own (<c>HousecarlCore.Progress.RunPaths</c>): an explicit data directory first,
    /// then the platform's per-user state directory. Never beside the exe — the installed binaries
    /// are the one place this must not be.</summary>
    public static string BaseDir()
    {
        var dataDir = Environment.GetEnvironmentVariable("HOUSECARL_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(dataDir)) return Path.Combine(dataDir.Trim(), "viewprobe-extract");
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(local)) return Path.Combine(local, "houseCARL", "viewprobe-extract");
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(xdg)) return Path.Combine(xdg, "housecarl", "viewprobe-extract");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "state", "housecarl", "viewprobe-extract");
    }

    // ----------------------------------------------------------------- claim

    /// <summary>Claim this process's own root, once. Sweeps orphans first — silently: a start-up that
    /// printed a line per kept folder would change the console of every render and would race a
    /// sibling process between its mkdir and its marker. <c>--extraction-scratch-sweep</c> is the
    /// entry point that reports.</summary>
    public static string Claim()
    {
        lock (Gate)
        {
            if (_root is not null) return _root;
            var baseDir = BaseDir();
            Directory.CreateDirectory(baseDir);
            using var me = Process.GetCurrentProcess();
            long startTicks = StartTicks(me);
            try { SweepCore(baseDir, delete: true); } catch (Exception) { /* a sweep that cannot run
                must never stop a render: the worst case is disk left behind, which the next sweep
                or the CLI entry point reports. */ }

            string name = $"vp-{Environment.ProcessId}-{(uint)(startTicks & 0xFFFFFFFF):x8}";
            string root = Path.Combine(baseDir, name);
            for (int n = 2; Directory.Exists(root) && n < 100; n++)
                root = Path.Combine(baseDir, $"{name}-{n}");
            Directory.CreateDirectory(root);
            // The marker goes in immediately: the window between the mkdir and this line is the
            // only window in which a crash leaves a directory nothing can ever claim, and the sweep
            // reports such a directory as kept rather than deleting it.
            File.WriteAllText(Path.Combine(root, MarkerName), JsonSerializer.Serialize(new
            {
                schema = MarkerSchema,
                tool = "viewprobe",
                pid = Environment.ProcessId,
                startTicks,
            }));
            _root = root;
            if (!_exitHooked)
            {
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Release();
                _exitHooked = true;
            }
            return root;
        }
    }

    /// <summary>Set the directory the library's constructor is about to read, and put it back.
    /// Measured on this runtime before the design rested on it: the redirect takes, a constructor
    /// inside the bracket captures the redirected value, the restore is clean, and a lazily loaded
    /// assembly still resolves inside the window.</summary>
    public static IDisposable Redirect()
    {
        string root = Claim();
        string previous = AppContext.BaseDirectory;
        AppContext.SetData(BaseDirectoryKey, root + Path.DirectorySeparatorChar);
        return new Restorer(previous);
    }

    sealed class Restorer(string previous) : IDisposable
    {
        bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            AppContext.SetData(BaseDirectoryKey, previous);
        }
    }

    /// <summary>Delete this process's root. Safe to call twice; called from a <c>using</c> in
    /// <c>Program.cs</c> and again from <c>ProcessExit</c> for the paths that do not return.</summary>
    public static void Release()
    {
        string? root;
        lock (Gate)
        {
            root = _root;
            // Cleared FIRST, so an extraction that somehow arrives after this point is refused by
            // the guard rather than re-creating files under a directory being deleted.
            _root = null;
        }
        if (root is null) return;
        var (deleted, reason) = DeleteOwned(root);
        if (!deleted)
            Console.Error.WriteLine($"   viewprobe: left its extraction scratch behind at '{root}' — {reason}");
    }

    /// <summary>A <c>using</c> for <c>Program.cs</c>: releases whatever was claimed, and does nothing
    /// at all when a run never rendered.</summary>
    public static IDisposable ProcessScope() => new Scope();

    sealed class Scope : IDisposable
    {
        public void Dispose() => Release();
    }

    // ----------------------------------------------------------------- guard

    /// <summary>Every extraction destination passes through here. Outside this process's root — or
    /// before it has one — the run is refused rather than allowed to write where another process may
    /// be reading. Throws, and deliberately not from inside the extractor's catch: a guard whose
    /// refusal becomes an error string is a guard that returns false and carries on.</summary>
    public static void RequireInside(string destPath)
    {
        string full = Path.GetFullPath(destPath);
        string? root = ClaimedRoot;
        if (root is null)
            throw new InvalidOperationException(
                "viewprobe extraction guard: the rendering library asked to extract '" + full +
                "' but this process has not claimed an extraction scratch. Refusing the run rather " +
                "than writing into a folder another render may be reading.");
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "viewprobe extraction guard: the rendering library asked to extract to '" + full +
                "', which is outside this process's extraction scratch '" + rootFull +
                "'. Refusing the run: simultaneous renders must not share an extraction folder.");
    }

    // ---------------------------------------------------------------- sweep

    /// <summary><c>viewprobe --extraction-scratch-sweep &lt;root&gt; [--delete-only &lt;dir&gt;]</c>.
    /// The deleter, on the command line, reporting one JSON object on stdout. The ownership rule
    /// below is its ONLY authority: a directory's name means nothing.</summary>
    public static int Sweep(string[] args)
    {
        string root = args[1];
        string? deleteOnly = null;
        for (int i = 2; i + 1 < args.Length; i++)
            if (args[i] == "--delete-only") deleteOnly = args[i + 1];

        if (deleteOnly is not null)
        {
            var (deleted, reason) = DeleteOwned(deleteOnly);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                tool = "viewprobe",
                action = "extraction-scratch-delete",
                root,
                target = deleteOnly,
                deleted = deleted ? new[] { deleteOnly } : Array.Empty<string>(),
                kept = deleted ? Array.Empty<object>() : new object[] { new { dir = deleteOnly, reason } },
                refused = deleted ? null : reason,
            }));
            return deleted ? 0 : 3;
        }

        var report = SweepCore(root, delete: true);
        Console.WriteLine(JsonSerializer.Serialize(report));
        return 0;
    }

    sealed record SweepReport(string tool, string action, string root, bool rootExists,
                              IReadOnlyList<string> deleted, IReadOnlyList<object> kept,
                              IReadOnlyList<object> errors);

    /// <summary>The rule, in one place. A directory under <paramref name="root"/> is removed ONLY
    /// when it carries a parsable marker AND that marker's owner is provably gone. Everything else
    /// is KEPT AND REPORTED — including a directory with no marker at all, which is what a crash
    /// between the mkdir and the marker leaves behind, and which would otherwise leak silently.</summary>
    static SweepReport SweepCore(string root, bool delete)
    {
        var deleted = new List<string>();
        var kept = new List<object>();
        var errors = new List<object>();
        if (!Directory.Exists(root))
            return new SweepReport("viewprobe", "extraction-scratch-sweep", root, false, deleted, kept, errors);

        foreach (var dir in Directory.GetDirectories(root))
        {
            try
            {
                var marker = Path.Combine(dir, MarkerName);
                if (!File.Exists(marker))
                {
                    kept.Add(new { dir, reason = $"no {MarkerName}: not viewprobe's, or a crash before the marker was written" });
                    continue;
                }
                int pid;
                long ticks;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(marker));
                    pid = doc.RootElement.GetProperty("pid").GetInt32();
                    ticks = doc.RootElement.GetProperty("startTicks").GetInt64();
                }
                catch (Exception ex)
                {
                    kept.Add(new { dir, reason = $"{MarkerName} could not be read ({ex.GetType().Name}); kept" });
                    continue;
                }
                var (gone, why) = OwnerIsGone(pid, ticks);
                if (!gone) { kept.Add(new { dir, reason = why }); continue; }
                if (!delete) { kept.Add(new { dir, reason = "would delete: " + why }); continue; }
                Directory.Delete(dir, recursive: true);
                deleted.Add(dir);
            }
            catch (Exception ex)
            {
                errors.Add(new { dir, error = ex.GetType().Name + ": " + ex.Message });
            }
        }
        return new SweepReport("viewprobe", "extraction-scratch-sweep", root, true, deleted, kept, errors);
    }

    /// <summary>Is the owner provably gone? "I could not tell" is NOT "it is dead", and a process id
    /// is reused: the marker's start time is what tells this process from a later one that inherited
    /// its id.</summary>
    static (bool gone, string why) OwnerIsGone(int pid, long startTicks)
    {
        if (pid <= 0) return (false, $"owner id {pid} is not a usable process id; liveness unknown, kept");
        Process owner;
        try { owner = Process.GetProcessById(pid); }
        catch (ArgumentException) { return (true, $"owner {pid} is not running"); }
        catch (Exception ex) { return (false, $"could not tell whether owner {pid} is alive ({ex.GetType().Name}); kept"); }
        using (owner)
        {
            try
            {
                long live = StartTicks(owner);
                if (live == startTicks) return (false, $"owner {pid} is alive");
                return (false, $"process id {pid} is alive but started at a different time — a recycled id; kept");
            }
            catch (Exception ex)
            {
                return (false, $"could not read the start time of owner {pid} ({ex.GetType().Name}); kept");
            }
        }
    }

    static long StartTicks(Process p)
    {
        try { return p.StartTime.Ticks; }
        catch (Exception) { return 0; }   // unreadable here means unmatchable there: the folder is kept
    }

    /// <summary>The exit-time delete. Same authority as the sweep: the marker, and nothing else.
    /// A directory without one is refused with the reason, never removed.</summary>
    static (bool deleted, string reason) DeleteOwned(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return (true, "already gone");
            var marker = Path.Combine(dir, MarkerName);
            if (!File.Exists(marker))
                return (false, $"refusing to delete '{dir}': it does not carry {MarkerName}, so it is not viewprobe's");
            try { using var _ = JsonDocument.Parse(File.ReadAllText(marker)); }
            catch (Exception ex)
            {
                return (false, $"refusing to delete '{dir}': its {MarkerName} could not be read ({ex.GetType().Name})");
            }
            Directory.Delete(dir, recursive: true);
            return (true, "deleted");
        }
        catch (Exception ex)
        {
            return (false, $"could not delete '{dir}': {ex.GetType().Name}: {ex.Message}");
        }
    }
}
