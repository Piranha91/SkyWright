namespace ViewProbe;

/// <summary>
/// A SCRATCH THAT FAILED ENDS THE RUN ONE WAY: ONE LINE, NO STACK TRACE, ONE EXIT CODE.
///
/// <see cref="ExtractionScratch"/> can fail in two ways, and until this file existed each of them
/// ended the run differently depending on nothing a caller controls:
///
///   * <b>the scratch cannot be claimed</b> — the data directory is a file, the disk is full, the
///     path is refused by the filesystem. That threw out of <c>Claim()</c>, nothing caught it, and
///     the run ended as an unhandled exception: a stack trace, and the runtime's crash code
///     <c>0xE0434352</c>, which no README could document and no caller could branch on.
///   * <b>the guard refused an extraction</b> — the library asked to write outside this process's
///     own folder. Whether that throw reached the top is a matter of WHICH call site tripped first:
///     two of the library's callers in <c>TexturedScene</c> sit inside <c>catch (Exception)</c>
///     blocks that log and carry on. Swallowed, the latch turned it into exit 4; uncaught, it ended
///     as the same crash code behind the same stack trace.
///
/// So one refusal had two endings and one of them was a crash report. A person reading the console
/// cannot tell a refusal from a bug in the renderer, and a script cannot branch on either.
///
/// This file makes the ending single. Every scratch failure goes through <see cref="Fail"/>, which
/// says its one line on stderr AT THE MOMENT IT HAPPENS (a swallowed exception prints nothing of
/// its own, and the reason is owed to the reader then, not at the end) and latches that the run is
/// condemned. From there:
///
///   * swallowed — <c>ExtractionScratch.GuardExitCode</c> reads the latch on the way out of Main and
///     the run cannot end at zero;
///   * uncaught — the handler installed by <see cref="Install"/> recognises the exception as the
///     scratch's own, does not repeat the line, and exits <see cref="ExitCode"/>.
///
/// The handler is deliberately narrow. It acts ONLY when the escaping exception is this file's own
/// (directly or wrapped), never merely because a refusal was latched earlier: a renderer bug that
/// crashes after a refusal is still a bug, and swallowing its stack trace to print a tidier sentence
/// would cost the next reader the thing they actually need.
///
/// Measured on this runtime before the design rested on it (the probe is in the launch record): the
/// <c>UnhandledException</c> handler runs BEFORE the runtime prints its report, and
/// <c>Environment.Exit</c> from inside it sets the process's code and suppresses that report while
/// still running every <c>ProcessExit</c> hook — so the scratch is still deleted, and the run record
/// still terminates, on this path.
/// </summary>
static class ExtractionScratchExit
{
    /// <summary>The documented code for EVERY scratch failure — cannot claim, or guard refused,
    /// swallowed or not. It is not 3: that one means an asset was found and could not be read, which
    /// is a defect in the picture, and these two are a refusal to render at all.</summary>
    public const int ExitCode = 4;

    static readonly object Gate = new();
    static string? _said;

    /// <summary>The scratch failure this process suffered, if any, whatever became of its
    /// exception. Non-null is what makes a refused run impossible to end at zero.</summary>
    public static string? Failure { get { lock (Gate) return _said; } }

    /// <summary>Say it once, latch it, and hand back the exception to throw. Callers write
    /// <c>throw ExtractionScratchExit.Fail(...)</c> so that the saying and the throwing cannot drift
    /// apart — there is no path that throws without having said why.</summary>
    public static ExtractionScratchException Fail(string sentence)
    {
        Say(sentence);
        return new ExtractionScratchException(sentence);
    }

    /// <summary>One line, and only the first one: a run refused twice (the guard trips on asset
    /// after asset) would otherwise fill the console with the same paragraph.</summary>
    static void Say(string sentence)
    {
        lock (Gate)
        {
            if (_said is not null) return;
            _said = sentence;
        }
        Console.Error.WriteLine("   " + sentence + $" Exit {ExitCode}.");
    }

    /// <summary>Install the last-chance handler. Called from
    /// <c>ExtractionScratch.ProcessScope()</c>, which every run takes at the top of Main, so this
    /// covers the whole of a run including the entry points that return before a render.</summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (Mine(e.ExceptionObject as Exception) is not { } ours) return;   // not ours: leave the report alone
            Say(ours.Message);
            Environment.Exit(ExitCode);
        };
    }

    /// <summary>Is this the scratch's own failure, wrapped or not? The library may hand an
    /// exception back inside one of its own, and a render that ran through a task would hand back an
    /// <c>AggregateException</c>, so the chain is walked rather than the top type read.</summary>
    static ExtractionScratchException? Mine(Exception? ex, int depth = 0)
    {
        if (ex is null || depth > 8) return null;
        if (ex is ExtractionScratchException mine) return mine;
        if (ex is AggregateException agg)
            foreach (var inner in agg.InnerExceptions)
                if (Mine(inner, depth + 1) is { } found) return found;
        return Mine(ex.InnerException, depth + 1);
    }

    // ------------------------------------------------------------------ the self-test entry point

    /// <summary><c>viewprobe --extraction-scratch-fail claim|guard|guard-swallowed</c>: the three
    /// ways a scratch failure travels, each reached from the command line so a test can prove they
    /// all end the same way. Needs no MO2 instance, no game and no load order — the guard is a
    /// string comparison against this process's own root, and the claim is a directory create.
    ///
    /// <c>claim</c> with a usable data directory is the GREEN control: it must claim, print the
    /// root and exit 0, so the failing cases below cannot pass for the wrong reason (an entry point
    /// that refused everything would satisfy them all).</summary>
    public static int FailSelfTest(string which)
    {
        switch (which)
        {
            case "claim":
                // Throws when the base directory cannot be created. Uncaught on purpose: this is
                // the path a render takes, where nothing between here and Main catches it.
                Console.WriteLine("claimed " + ExtractionScratch.Claim());
                return 0;

            case "guard":
                ExtractionScratch.Claim();
                // Outside this process's root, and a path nothing creates: the guard is a decision
                // about a destination, taken before anything is written.
                ExtractionScratch.RequireInside(
                    Path.Combine(Path.GetTempPath(), "viewprobe-guard-selftest", "meshes", "x.nif"));
                return 0;

            case "guard-swallowed":
                ExtractionScratch.Claim();
                // EXACTLY what the library's own call sites do to it. The exception dies here; the
                // run must still not end at zero.
                try
                {
                    ExtractionScratch.RequireInside(
                        Path.Combine(Path.GetTempPath(), "viewprobe-guard-selftest", "meshes", "x.nif"));
                }
                catch (Exception) { }
                return ExtractionScratch.GuardExitCode(0);

            default:
                Console.Error.WriteLine("usage: viewprobe --extraction-scratch-fail claim|guard|guard-swallowed");
                return 2;
        }
    }
}

/// <summary>The scratch's own failure, as a type, so the handler in
/// <see cref="ExtractionScratchExit"/> can tell it from every other exception a render can throw
/// without matching on message text.</summary>
sealed class ExtractionScratchException : Exception
{
    public ExtractionScratchException(string message) : base(message) { }
}
