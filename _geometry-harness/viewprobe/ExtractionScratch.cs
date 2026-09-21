using System.Diagnostics;
using System.Text.Json;
using HousecarlCore.Progress;

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
    static string? _originalBase;
    static bool _bracketOpen;

    /// <summary>This process's scratch failure, if one happened, whatever became of the exception.
    /// Latched the instant it happens, BEFORE anything is thrown, because a throw can be swallowed
    /// — two of the library's callers sit inside `catch (Exception)` blocks that log and carry on —
    /// so the exit code must not depend on the exception reaching `Main`. Both failures latch here:
    /// a scratch that cannot be claimed and an extraction the guard refused. See
    /// <see cref="ExtractionScratchExit"/>, which owns the latch, the one line and the one code.</summary>
    public static string? Refusal => ExtractionScratchExit.Failure;

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
            try { return ClaimCore(); }
            catch (ExtractionScratchException) { throw; }
            catch (Exception ex)
            {
                // A SCRATCH THAT CANNOT BE CREATED IS A REFUSAL TO RENDER, NOT A CRASH. The data
                // directory is the caller's to choose (HOUSECARL_DATA_DIR), so "it is a file", "it
                // is read-only" and "the disk is full" are things a person does by accident and is
                // owed a sentence about - not a stack trace behind a runtime crash code. The
                // alternative to failing here is extracting into the folder beside the exe, which
                // is the defect this whole file exists to remove.
                throw ExtractionScratchExit.Fail(
                    "viewprobe extraction scratch: this process could not create its own extraction "
                    + $"folder under '{BaseDir()}' ({ex.GetType().Name}: {ex.Message}). Refusing the "
                    + "run rather than extracting where another render may be reading.");
            }
        }
    }

    /// <summary>The claim itself. Separated only so <see cref="Claim"/> can turn anything this
    /// throws into the one documented ending. It takes the gate again — a re-entrant lock the
    /// caller already holds — so the method is correct on its own terms rather than by a comment
    /// about where it may be called from.</summary>
    static string ClaimCore()
    {
        lock (Gate)
        {
            var baseDir = BaseDir();
            Directory.CreateDirectory(baseDir);
            using var me = Process.GetCurrentProcess();
            long startTicks = StartTicks(me);
            try
            {
                var swept = SweepCore(baseDir, delete: true);
                // WHERE A LEAK BECOMES VISIBLE. Not on the console: a start-up line whose text
                // depended on what siblings happen to be running would make two simultaneous
                // renders print different things, and those consoles are compared. The run record
                // is per-run by construction, so it can carry this without that cost - and a
                // recycled process id keeps an orphan for as long as that unrelated process lives,
                // which on a renderer that can die out of memory means gigabytes nobody is told
                // about.
                if (swept.leaks > 0)
                    RunLog.Current?.Note($"extraction scratch: {swept.leaks} folder(s) kept and not "
                        + $"accounted for under '{baseDir}' - run "
                        + $"`viewprobe --extraction-scratch-sweep \"{baseDir}\"` to see why");
            }
            catch (Exception) { /* a sweep that cannot run must never stop a render: the worst case
                is disk left behind, which the next sweep or the CLI entry point reports. */ }

            string name = $"vp-{Environment.ProcessId}-{(uint)(startTicks & 0xFFFFFFFF):x8}";
            string root = Path.Combine(baseDir, name);
            for (int n = 2; Directory.Exists(root) && n < 100; n++)
                root = Path.Combine(baseDir, $"{name}-{n}");
            // BEFORE the directory exists: a root this long cannot deliver a single asset, and the
            // failure it would otherwise produce is the silent one - the library reads nothing from
            // a path past its limit WITHOUT throwing. Checked here rather than guessed at, and only
            // for the case arithmetic settles; see AssetPathBudget.ClaimRefusal.
            if (AssetPathBudget.ClaimRefusal(root) is { } tooLong)
                throw ExtractionScratchExit.Fail(tooLong);
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
        lock (Gate)
        {
            // The value restored is the one captured the FIRST time a bracket opened, never the
            // live slot: two brackets that nested or interleaved would otherwise restore the
            // REDIRECTED value last and leave the process pointing at a folder that is about to be
            // deleted. Nesting is refused outright as well, so neither can happen by construction.
            if (_bracketOpen)
                throw new InvalidOperationException(
                    "viewprobe extraction scratch: a redirect bracket is already open. The bracket " +
                    "exists to wrap ONE constructor; nesting it would restore the redirected value.");
            _originalBase ??= AppContext.BaseDirectory;
            _bracketOpen = true;
        }
        AppContext.SetData(BaseDirectoryKey, root + Path.DirectorySeparatorChar);
        return new Restorer();
    }

    sealed class Restorer : IDisposable
    {
        bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            lock (Gate)
            {
                AppContext.SetData(BaseDirectoryKey, _originalBase);
                _bracketOpen = false;
            }
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
    /// at all when a run never rendered. It is also where the last-chance handler goes in, because
    /// this is the one call every run makes at the top of Main — so no entry point can be added
    /// that has the scratch and not its ending.</summary>
    public static IDisposable ProcessScope()
    {
        ExtractionScratchExit.Install();
        return new Scope();
    }

    sealed class Scope : IDisposable
    {
        public void Dispose() => Release();
    }

    // ----------------------------------------------------------------- guard

    /// <summary>Every extraction destination passes through here. Outside this process's root — or
    /// before it has one — the run is refused rather than allowed to write where another process may
    /// be reading. Throws, and deliberately not from inside the extractor's catch: a guard whose
    /// refusal becomes an error string is a guard that returns false and carries on.
    ///
    /// THE THROW IS NOT THE ONLY THING THAT HAPPENS. Two of the library's callers in this repository
    /// sit inside `catch (Exception)` blocks that log and carry on (TexturedScene's covering-model
    /// resolve and its BuildFromFile), so an exception is not a reliable way to stop a run: whether
    /// the refusal reaches Main is a matter of which call site trips first. The refusal is therefore
    /// LATCHED before it is thrown — and the run ends at the SAME documented code and the SAME one
    /// line either way, swallowed through <see cref="GuardExitCode"/> and uncaught through
    /// <see cref="ExtractionScratchExit"/>.</summary>
    public static void RequireInside(string destPath)
    {
        string full = Path.GetFullPath(destPath);
        string? root = ClaimedRoot;
        if (root is null)
            throw Refuse(
                "viewprobe extraction guard: the rendering library asked to extract '" + full +
                "' but this process has not claimed an extraction scratch. Refusing the run rather " +
                "than writing into a folder another render may be reading.");
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw Refuse(
                "viewprobe extraction guard: the rendering library asked to extract to '" + full +
                "', which is outside this process's extraction scratch '" + rootFull +
                "'. Refusing the run: simultaneous renders must not share an extraction folder.");
    }

    /// <summary>Latched, said once, and thrown as the scratch's own type — all three in
    /// <see cref="ExtractionScratchExit.Fail"/>, so a refusal that is swallowed and a refusal that
    /// reaches Main end the same way. Said here rather than at the end because a caught exception
    /// prints nothing of its own and the reader is owed the reason at the moment it happened.</summary>
    static ExtractionScratchException Refuse(string message) => ExtractionScratchExit.Fail(message);

    /// <summary>The exit code a run must not improve on. Zero when the scratch never failed.
    /// Called on every path out of <c>Main</c> that returns a code — and the uncaught path, which
    /// returns nothing at all, ends at the same code through
    /// <see cref="ExtractionScratchExit.Install"/>.</summary>
    public static int GuardExitCode(int rc)
    {
        if (Refusal is null) return rc;
        return rc != 0 ? rc : ExtractionScratchExit.ExitCode;
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
                              IReadOnlyList<object> errors, int leaks);

    /// <summary>The rule, in one place. A directory under <paramref name="root"/> is removed ONLY
    /// when it carries a parsable marker AND that marker's owner is provably gone. Everything else
    /// is KEPT AND REPORTED — including a directory with no marker at all, which is what a crash
    /// between the mkdir and the marker leaves behind, and which would otherwise leak silently.</summary>
    static SweepReport SweepCore(string root, bool delete)
    {
        var deleted = new List<string>();
        var kept = new List<object>();
        var errors = new List<object>();
        int leaks = 0;
        void Keep(string dir, string reason, bool leak)
        {
            // `leak` is the difference between a sibling that is simply still rendering and disk
            // this tool can no longer account for. Only the second is worth anyone's attention.
            if (leak) leaks++;
            kept.Add(new { dir, reason, leak });
        }
        if (!Directory.Exists(root))
            return new SweepReport("viewprobe", "extraction-scratch-sweep", root, false, deleted, kept, errors, 0);

        foreach (var dir in Directory.GetDirectories(root))
        {
            try
            {
                var marker = Path.Combine(dir, MarkerName);
                if (!File.Exists(marker))
                {
                    Keep(dir, $"no {MarkerName}: not viewprobe's, or a crash before the marker was written", true);
                    continue;
                }
                var m = ReadMarker(marker);
                if (m.Why is not null) { Keep(dir, m.Why, true); continue; }
                var (gone, why) = OwnerIsGone(m.Pid, m.StartTicks);
                if (!gone) { Keep(dir, why, !why.EndsWith("is alive", StringComparison.Ordinal)); continue; }
                if (!delete) { Keep(dir, "would delete: " + why, false); continue; }
                Directory.Delete(dir, recursive: true);
                deleted.Add(dir);
            }
            catch (Exception ex)
            {
                errors.Add(new { dir, error = ex.GetType().Name + ": " + ex.Message });
            }
        }
        return new SweepReport("viewprobe", "extraction-scratch-sweep", root, true, deleted, kept, errors, leaks);
    }

    readonly record struct MarkerRead(int Pid, long StartTicks, string? Why);

    /// <summary>Read a marker, or say why it cannot be used. "A parsable marker" is the contract's
    /// words and it means all of it: the schema this build understands, this tool's name, and both
    /// owner fields. Anything else is a directory we do not understand, and an unrecognised
    /// directory is KEPT — a future schema must not be deletable by an older build.</summary>
    static MarkerRead ReadMarker(string markerPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(markerPath));
            var root = doc.RootElement;
            if (!root.TryGetProperty("schema", out var schema) || schema.GetInt32() != MarkerSchema)
                return new MarkerRead(0, 0, $"{MarkerName} carries schema "
                    + (root.TryGetProperty("schema", out var s2) ? s2.ToString() : "(none)")
                    + $", not {MarkerSchema}: this build does not understand it; kept");
            if (!root.TryGetProperty("tool", out var tool) || tool.GetString() != "viewprobe")
                return new MarkerRead(0, 0, $"{MarkerName} is not viewprobe's "
                    + $"(tool = {(root.TryGetProperty("tool", out var t2) ? t2.ToString() : "(none)")}); kept");
            return new MarkerRead(root.GetProperty("pid").GetInt32(),
                                  root.GetProperty("startTicks").GetInt64(), null);
        }
        catch (Exception ex)
        {
            return new MarkerRead(0, 0, $"{MarkerName} could not be read ({ex.GetType().Name}); kept");
        }
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

    /// <summary>UTC, deliberately. <c>Process.StartTime</c> is LOCAL time: across a daylight-saving
    /// transition the same live process reads an hour different, which would turn "this is my own
    /// folder" into "a recycled id". That only ever costs a kept folder here, never a wrong delete —
    /// but a later rule that treated a different start time as proof of death would be wrong twice a
    /// year, so the value stored is the one that does not move.</summary>
    static long StartTicks(Process p)
    {
        try { return p.StartTime.ToUniversalTime().Ticks; }
        catch (Exception) { return 0; }   // unreadable here means unmatchable there: the folder is kept
    }

    /// <summary>The exit-time delete, and the whole of what <c>--delete-only</c> may do. The SAME
    /// authority as the sweep, in both halves: the marker says the directory is viewprobe's, and the
    /// owner must be this process or be provably gone.
    ///
    /// The second half is not decoration. Without it the command line could delete a live sibling's
    /// folder in the middle of its render — which is the very failure this whole file exists to
    /// prevent, reintroduced through the deleter's own front door.</summary>
    static (bool deleted, string reason) DeleteOwned(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return (true, "already gone");
            var marker = Path.Combine(dir, MarkerName);
            if (!File.Exists(marker))
                return (false, $"refusing to delete '{dir}': it does not carry {MarkerName}, so it is not viewprobe's");
            var m = ReadMarker(marker);
            if (m.Why is not null)
                return (false, $"refusing to delete '{dir}': {m.Why}");
            bool mine;
            using (var me = Process.GetCurrentProcess())
                mine = m.Pid == Environment.ProcessId && m.StartTicks == StartTicks(me);
            if (!mine)
            {
                var (gone, why) = OwnerIsGone(m.Pid, m.StartTicks);
                if (!gone)
                    return (false, $"refusing to delete '{dir}': it is not this process's and its owner is not gone — {why}");
            }
            Directory.Delete(dir, recursive: true);
            return (true, mine ? "deleted: this process's own scratch" : "deleted: the owner is gone");
        }
        catch (Exception ex)
        {
            return (false, $"could not delete '{dir}': {ex.GetType().Name}: {ex.Message}");
        }
    }
}
