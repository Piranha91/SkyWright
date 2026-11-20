using System.Text.RegularExpressions;

namespace RunControl;

/// <summary>A bad settings file or value; the message is the one sentence the user reads.</summary>
public sealed class SettingsError(string sentence) : Refusal(sentence);

/// <summary>The saved evaluation settings as a plan reads them (evalsettings.py: load, value, record, sentences).
/// Each option's value comes from a flag, else an environment variable where one exists, else the file, else the
/// built-in default. The settings window and the commands that write the file stay in Python.</summary>
public sealed class EvalSettings
{
    public const int SchemaVersion = 1;
    public const string FileName = "houseCARL.evaluation.json";

    // One option: its key, value type, allowed values (enum), built-in default, and the few extras validation reads.
    sealed record Option(string Key, string Type, object? Default = null, string[]? Allowed = null, string? Env = null,
        bool AllowOff = false, string? DefaultText = null);

    static readonly string[] OffAskAlways = ["off", "ask", "always"];

    // The option table, in the order the plan records it.
    static readonly Option[] Options =
    [
        new("evaluation.depth", "enum", "deep", ["standard", "deep"]),
        new("evaluation.liveSwitchCheck", "enum", "off", OffAskAlways),
        new("evaluation.reverseComparison", "bool", true),
        new("evaluation.budgetMinutes", "number", DefaultText: "none"),
        new("evaluation.nexusLookups", "bool", true),
        new("liveSwitchCheck.instance", "path", DefaultText: "not set"),
        new("liveSwitchCheck.shortcut", "path", DefaultText: "not set"),
        new("liveSwitchCheck.gameExe", "path", DefaultText: "not set"),
        new("liveSwitchCheck.crashDir", "path", DefaultText: "~/Documents/My Games/Skyrim Special Edition/SKSE"),
        new("liveSwitchCheck.devbenchPort", "int", DefaultText: "8920"),
        new("liveSwitchCheck.minFreeGb", "number", DefaultText: "16"),
        new("liveSwitchCheck.launchMinutes", "number", DefaultText: "25"),
        new("report.mode", "enum", "concise", ["concise", "exhaustive"]),
        new("report.pdf", "enum", "when-browser-found", ["when-browser-found", "off"]),
        new("report.maxPages", "int", DefaultText: "the skill's value"),
        new("report.rowsPerGroup", "int", DefaultText: "the skill's value"),
        new("report.figuresPerFinding", "int", DefaultText: "the skill's value"),
        new("video.make", "enum", "off", OffAskAlways),
        new("video.maxSeconds", "number", DefaultText: "the skill's value"),
        new("video.footage", "enum", "offline-renders", ["offline-renders", "offline-and-game"]),
        new("video.narration.engine", "enum", null, ["kokoro", "qwen3", "voxcpm2"], DefaultText: "kokoro"),
        new("video.narration.voice", "string", DefaultText: "bm_george"),
        new("video.narration.device", "string", DefaultText: "cpu"),
        new("video.narration.modelDir", "path", DefaultText: "not set"),
        new("video.narration.modelRevision", "string", DefaultText: "not set"),
        new("video.ffmpeg", "path", Env: "HOUSECARL_FFMPEG", DefaultText: "discovered (PATH or a bundled copy)"),
        new("models.recordChecks", "model", "session"),
        new("models.imageReview", "model", "session"),
        new("models.flipPlan", "model", "session"),
        new("models.rewriteExplanations", "model", "off", AllowOff: true),
        new("models.checkExplanations", "model", "session"),
    ];
    static readonly Dictionary<string, Option> ByKey = Options.ToDictionary(o => o.Key, StringComparer.Ordinal);

    // ---- the accepted model IDs

    /// <summary>The full model IDs that have an agent in a plugin's agents folder, in file order (evalsettings._model_agents).</summary>
    public static List<string> ModelIds(string agentsFolder)
    {
        var ids = new List<string>();
        if (!Directory.Exists(agentsFolder)) return ids;
        var files = Directory.GetFiles(agentsFolder).Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
        foreach (string file in PyOrder.SortedPaths(files))
        {
            var lines = PyText.SplitLines(PyJson.Decode(File.ReadAllBytes(file), bom: false));
            if (lines.Count == 0 || PyValue.Strip(lines[0]) != "---") continue;
            var head = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in lines.Skip(1))
            {
                if (PyValue.Strip(line) == "---") break;
                int colon = line.IndexOf(':');
                if (colon >= 0) head[PyValue.Strip(line[..colon])] = PyValue.Strip(line[(colon + 1)..]);
            }
            if (head.GetValueOrDefault("model") is { Length: > 0 } model && head.GetValueOrDefault("name") is { Length: > 0 } && !ids.Contains(model))
                ids.Add(model);
        }
        return ids;
    }

    /// <summary>The agents folder of the plugin this build sits beside: the nearest "plugin/agents" above the program.</summary>
    public static string? DefaultAgentsFolder()
    {
        for (string? at = AppContext.BaseDirectory; at != null; at = Path.GetDirectoryName(at.TrimEnd('\\', '/')))
        {
            string folder = Path.Combine(at, "plugin", "agents");
            if (Directory.Exists(folder)) return folder;
        }
        return null;
    }

    // ---- where the file is

    /// <summary>The settings file: the given path, else HOUSECARL_EVAL_SETTINGS, else HOUSECARL_DATA_DIR, else %LOCALAPPDATA%/houseCARL.</summary>
    public static string ResolvePath(string? explicitPath = null, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        if (!string.IsNullOrEmpty(explicitPath)) return PyPath.Str(explicitPath);
        string? named = env("HOUSECARL_EVAL_SETTINGS");
        if (named != null && PyValue.Strip(named).Length > 0) return PyPath.Str(PyValue.Strip(named));
        string? data = env("HOUSECARL_DATA_DIR");
        if (data != null && PyValue.Strip(data).Length > 0) return PyPath.Join(PyValue.Strip(data), FileName);
        if (OperatingSystem.IsWindows() && !string.IsNullOrEmpty(env("LOCALAPPDATA")))
            return PyPath.Join(env("LOCALAPPDATA")!, "houseCARL", FileName);
        if (!string.IsNullOrEmpty(env("XDG_STATE_HOME"))) return PyPath.Join(env("XDG_STATE_HOME")!, "housecarl", FileName);
        return PyPath.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state", "housecarl", FileName);
    }

    // ---- validation

    readonly IReadOnlyList<string> _modelIds;

    // The values a models.* option accepts: session, off where allowed, and each ID with a shipped agent.
    List<string> ModelChoices(Option o) => ["session", .. o.AllowOff ? new[] { "off" } : [], .. _modelIds];

    string ChoicesText(Option o)
    {
        var c = ModelChoices(o);
        return c.Count > 1 ? string.Join(", ", c.Take(c.Count - 1)) + " or " + c[^1] : c[0];
    }

    void CheckValue(string key, object? value, string where)
    {
        if (!ByKey.TryGetValue(key, out var o))
            throw new SettingsError($"{where} sets `{key}`, which is not an evaluation setting this houseCARL knows: "
                                    + "remove it, or reopen the window (`evalsettings.py window`).");
        if (value == null) return;
        bool ok = true;
        switch (o.Type)
        {
            case "enum":
                if (!(value is string s && o.Allowed!.Contains(s)))
                    throw new SettingsError($"{where} sets `{key}` to `{PyText.Str(value)}`, which is not one of "
                                            + $"{string.Join(", ", o.Allowed!)}: fix that value or reopen the window.");
                break;
            case "bool": ok = value is bool; break;
            case "int": ok = PyValue.IsInt(value) && PyValue.AboveZero(value); break;
            case "number": ok = PyValue.IsNumber(value) && PyValue.AboveZero(value); break;
            case "path" or "string": ok = value is string text && PyValue.Strip(text) != ""; break;
            case "model":
                ok = value is string;
                if (value is string model && !ModelChoices(o).Contains(model))
                {
                    string why = model == "off" ? "but this step cannot be turned off"
                        : !model.StartsWith("claude-", StringComparison.Ordinal) ? "a short name the host can resolve to an older model"
                        : "which has no houseCARL agent to run it";
                    throw new SettingsError($"{where} sets `{key}` to `{model}`, {why}: run `evalsettings.py set {key}=VALUE` "
                                            + $"with VALUE one of {ChoicesText(o)}.");
                }
                break;
        }
        if (ok) return;
        string want = o.Type switch
        {
            "bool" => "true or false",
            "int" => "a whole number above zero",
            "number" => "a number above zero",
            "path" => "a path",
            "string" => "a non-empty text",
            _ => ChoicesText(o),
        };
        throw new SettingsError($"{where} sets `{key}` to `{PyJson.Dumps(value)}`, which is not {want}: fix that value or "
                                + "reopen the window.");
    }

    // Rules across options, on the effective values, so a flag that combines badly with the file is caught too.
    static void CheckCombination(PyDict values, string where)
    {
        object? depth = values.Get("evaluation.depth"), live = values.Get("evaluation.liveSwitchCheck");
        if (PyValue.Eq(depth, "standard") && live is "ask" or "always")
            throw new SettingsError($"{where} sets the depth to Standard, which judges the load-time state only, so the live "
                                    + $"switch check cannot be set to {live}: choose Deep, or set the live switch check to off.");
        if (!PyValue.Eq(live, "always")) return;
        var missing = new[] { "liveSwitchCheck.instance", "liveSwitchCheck.shortcut", "liveSwitchCheck.gameExe" }
            .Where(k => !PyValue.Truthy(values.Get(k))).Select(k => k.Split('.')[1]).ToList();
        if (missing.Count > 0)
            throw new SettingsError($"{where} opts every evaluation into the live switch check, but no scratch "
                                    + $"{string.Join(", ", missing)} is set: set them in the window, or switch the check to off or ask.");
    }

    // The document's chosen values {key: value}, nulls dropped.
    PyDict ValidateDoc(object? doc, string where)
    {
        if (doc is not PyDict d)
            throw new SettingsError($"{where} is not a JSON object: fix it, or run `evalsettings.py window` to replace it.");
        object? sv = d.Get("schemaVersion");
        if (!PyValue.IsInt(sv))
            throw new SettingsError($"{where} has no whole-number schemaVersion: fix it, or run `evalsettings.py window` "
                                    + "to replace it.");
        var version = PyValue.Int(sv);
        if (version > SchemaVersion)
            throw new SettingsError($"{where} was written by a newer houseCARL (schema {version}): update houseCARL, or pass "
                                    + "--settings with a file this version can read.");
        if (version < SchemaVersion)
            throw new SettingsError($"{where} carries schema {version}, which no houseCARL wrote: fix it, or run "
                                    + "`evalsettings.py window` to replace it.");
        var chosen = new PyDict();
        foreach (var (section, body) in d)
        {
            if (section is "schemaVersion" or "saved") continue;
            if (body is not PyDict block)
            {
                CheckValue(section, null, where);
                throw new SettingsError($"{where} sets `{section}` to a value where a section belongs: fix it or reopen the window.");
            }
            foreach (var (key, value) in Leaves(block, section + "."))
            {
                CheckValue(key, value, where);
                if (value != null) chosen[key] = value;
            }
        }
        return chosen;
    }

    // Every option-level value under a section: a dict is walked into unless its dotted key is itself an option.
    static IEnumerable<(string key, object? value)> Leaves(PyDict block, string prefix)
    {
        foreach (var (k, v) in block)
        {
            string key = prefix + k;
            if (v is PyDict inner && !ByKey.ContainsKey(key))
                foreach (var leaf in Leaves(inner, key + ".")) yield return leaf;
            else yield return (key, v);
        }
    }

    // (document, bytes), or (null, null) when the file is absent; a file that is not JSON is refused with its line.
    static (object? doc, byte[]? raw) ReadJson(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return (null, null);
        byte[] raw = File.ReadAllBytes(path);
        string? line = null;
        try
        {
            string text = PyJson.Decode(raw, bom: true);
            try { return (PyJson.Loads(text), raw); }
            catch (FormatException ex)
            {
                // PyJson names the character where the parse stopped, the place Python's json reports.
                var at = Regex.Match(ex.Message, @"at character (\d+)$");
                if (at.Success) line = $" (line {text.Take(int.Parse(at.Groups[1].Value)).Count(c => c == '\n') + 1})";
            }
        }
        catch (FormatException) { }
        throw new SettingsError($"The evaluation settings file `{path}` is not valid JSON" + line
                                + ": fix it, or run `evalsettings.py window` to replace it.");
    }

    // ---- the resolved settings

    readonly PyDict? _doc;
    readonly string? _sha256;
    readonly PyDict _options = [];
    readonly List<object?> _newOptions = [];

    /// <summary>The settings file's path, as Python's str(Path) spells it.</summary>
    public string FilePath { get; }

    EvalSettings(string path, PyDict? flags, Func<string, string?> env, IReadOnlyList<string> modelIds)
    {
        FilePath = path;
        _modelIds = modelIds;
        string where = $"The evaluation settings file `{path}`";
        var (doc, raw) = ReadJson(path);
        var chosen = new PyDict();
        if (doc != null)
        {
            // The file is judged alone first, so a bad file is refused whatever the flags say.
            chosen = ValidateDoc(doc, where);
            var alone = new PyDict();
            foreach (var o in Options) alone[o.Key] = chosen.ContainsKey(o.Key) ? chosen[o.Key] : o.Default;
            CheckCombination(alone, where);
            _doc = (PyDict)doc;
            _sha256 = Hashing.Sha256Bytes(raw!);
        }
        foreach (var o in Options)
        {
            string k = o.Key;
            if (flags != null && flags.Get(k) != null) _options[k] = Row(flags[k], "flag");
            else if (o.Env != null && !string.IsNullOrEmpty(env(o.Env))) _options[k] = Row(env(o.Env), "environment");
            else if (chosen.ContainsKey(k)) _options[k] = Row(chosen[k], "file");
            else _options[k] = Row(o.Default, "built-in default");
        }
        object? saved = _doc?.Get("saved");
        if ((PyValue.Truthy(saved) ? PyValue.Dict(saved) : []).Get("options") is List<object?> known)
            _newOptions.AddRange(Options.Where(o => !PyValue.In(o.Key, known)).Select(o => o.Key));
        CheckCombination(Values(), _doc != null ? where : "This evaluation's flags");
    }

    static PyDict Row(object? value, string source) => new() { ["value"] = value, ["source"] = source };

    /// <summary>The resolved settings for an evaluation command; a bad file or combination is a SettingsError.
    /// flags holds the command's own flags by option key. env and modelIds are for tests; by default the process
    /// environment and the agents of the plugin this build sits beside.</summary>
    public static EvalSettings Load(string? explicitPath = null, PyDict? flags = null, Func<string, string?>? env = null,
        IReadOnlyList<string>? modelIds = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        modelIds ??= DefaultAgentsFolder() is { } folder ? ModelIds(folder) : [];
        return new EvalSettings(ResolvePath(explicitPath, env), flags, env, modelIds);
    }

    /// <summary>One option's effective value.</summary>
    public object? Value(string key) => ((PyDict)_options[key]!)["value"];

    PyDict Values() => new(_options.Select(kv => new KeyValuePair<string, object?>(kv.Key, ((PyDict)kv.Value!)["value"])));

    /// <summary>What a plan freezes: the file's identity and every option's value and source.</summary>
    public PyDict Record() => new()
    {
        ["path"] = FilePath,
        ["sha256"] = _sha256,
        ["schemaVersion"] = _doc?.Get("schemaVersion"),
        ["source"] = _doc != null ? "file" : "built-in defaults",
        ["options"] = _options,
        ["newOptions"] = _newOptions,
    };

    /// <summary>What to tell the user: no saved file, and each option newer than the saved file.</summary>
    public List<string> Sentences()
    {
        var lines = new List<string>();
        if (_doc == null)
            lines.Add("No saved evaluation settings were found, so this evaluation uses the built-in defaults (Deep depth, live "
                      + "switch check off, no video); run `evalsettings.py window` to choose.");
        foreach (string k in _newOptions.Cast<string>())
        {
            var o = ByKey[k];
            string shown = o.Default != null ? PyJson.Dumps(o.Default) : o.DefaultText ?? "the tool default";
            lines.Add($"This houseCARL adds the option `{k}`, which your saved settings do not set, so its default "
                      + $"(`{shown}`) applies.");
        }
        return lines;
    }
}
