namespace RunControl;

/// <summary>The names step of `bundle` and what its command line means (evalrun.py's default_reader_cmd, mod_names,
/// reader_env, names_step and write_names_status).</summary>
public static class BundleSteps
{
    /// <summary>The environment variable naming the record reader when --reader-cmd names none.</summary>
    public const string ReaderVariable = "HOUSECARL_RECORD_READER_CMD";

    /// <summary>The houseCARL server built in this checkout's pinned houseCARL (external/houseCARL).</summary>
    public static string CheckoutReader() =>
        PyPath.Join(HarnessShared.CheckoutLayout.Housecarl(HarnessShared.CheckoutLayout.FindRoot()
                                                           ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."))),
            "src", "housecarl-mcp", "bin", "Release", "net9.0", "housecarl-mcp.exe");

    /// <summary>The record reader the names enrichment starts: --reader-cmd, else the environment variable, else this
    /// checkout's built server. (command, where it came from), or (null, one sentence why there is none).</summary>
    public static (string? command, string source) DefaultReaderCmd(string? given)
    {
        if (!string.IsNullOrEmpty(given)) return (given, "--reader-cmd");
        string env = PyValue.Strip(Environment.GetEnvironmentVariable(ReaderVariable) ?? "");
        if (env.Length > 0) return (env, ReaderVariable);
        string exe = CheckoutReader();
        if (File.Exists(exe)) return ($"\"{exe}\"", "checkout");
        return (null, $"no record reader was given (--reader-cmd or {ReaderVariable}) and this checkout's houseCARL server is not built at {exe}.");
    }

    /// <summary>--mod-name PLUGIN=NAME pairs, each side stripped.</summary>
    public static PyDict ModNames(IEnumerable<string> pairs)
    {
        var names = new PyDict();
        foreach (string x in pairs.Where(x => x.Contains('=')))
            names[PyValue.Strip(x[..x.IndexOf('=')])] = PyValue.Strip(x[(x.IndexOf('=') + 1)..]);
        return names;
    }

    /// <summary>--reader-env KEY=VALUE pairs as given.</summary>
    public static PyDict ReaderEnv(IEnumerable<string> pairs)
    {
        var env = new PyDict();
        foreach (string x in pairs.Where(x => x.Contains('='))) env[x[..x.IndexOf('=')]] = x[(x.IndexOf('=') + 1)..];
        return env;
    }

    /// <summary>The seconds a step took, read when it ends; a test replaces the clock.</summary>
    public static Func<Func<double>> Timer = () =>
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        return () => watch.Elapsed.TotalSeconds;
    };

    /// <summary>Runs the names enrichment on bundle d and says what happened in one sentence: enriched, partial or
    /// refused. A refusal writes nothing of the names and never throws.</summary>
    public static PyDict NamesStep(string d, string? cmd, string source, PyDict mods, PyDict env)
    {
        var elapsed = Timer();
        PyDict Done(string status, string sentence, object? result = null, object? counts = null) => new()
        {
            ["status"] = status, ["sentence"] = string.Join(' ', PyText.Split(sentence)), ["reader"] = cmd != null ? source : null,
            ["counts"] = counts, ["seconds"] = PyText.Round(elapsed(), 1), ["result"] = result,
        };
        if (cmd == null) return Done("refused", "The names enrichment was not run because " + source);
        PyDict made;
        try { made = NamesEnrichment.Run(d, cmd, mods, env.Count > 0 ? env : null); }
        catch (EnrichRefused ex) { return Done("refused", $"The names enrichment was refused: {ex.Message.TrimEnd('.')}."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PyValueError)
        {
            // Python's OSError and ValueError: the reader could not start, or wrote something that is not a message
            string text = ex.Message.TrimEnd('.');
            return Done("refused", $"The names enrichment could not start or read with its record reader ({cmd}): "
                + $"{(text.Length > 0 ? text : ex.GetType().Name)}.");
        }
        var c = Ev.OrEmpty(made.Get("counts"));
        if (PyValue.Truthy(c.Get("objectsUnread")) || PyValue.Truthy(c.Get("basesUnread")))
            return Done("partial", $"The names enrichment could not read {Ev.Str(c.Get("objectsUnread"))} of the "
                + $"{Ev.Str(c.Get("objectsNamed"))} objects the rows name and {Ev.Str(c.Get("basesUnread"))} of their "
                + $"{Ev.Str(c.Get("bases"))} base records (listed under unread in enrichment/names/names.json), so "
                + "the report counts their clashes as cannot-tell.", made, c);
        return Done("enriched", $"The names enrichment read all {Ev.Str(c.Get("objectsNamed"))} objects the rows name and their "
            + $"{Ev.Str(c.Get("bases"))} base records.", made, c);
    }

    /// <summary>Records the names step's statement in the manifest (enrichmentStatus.names), without its result.</summary>
    public static PyDict WriteNamesStatus(string d, PyDict st)
    {
        string file = PyPath.Join(d, "manifest.json");
        var m = Ev.OrEmpty(PyJson.LoadEvalFile(file));
        if (!m.ContainsKey("enrichmentStatus")) m["enrichmentStatus"] = new PyDict();
        PyValue.Dict(m["enrichmentStatus"])["names"] = new PyDict(st.Where(kv => kv.Key != "result"));
        PyJson.WriteEvalFile(file, m);
        return st;
    }
}
