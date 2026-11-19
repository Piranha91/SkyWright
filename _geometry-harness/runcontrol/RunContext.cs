namespace RunControl;

/// <summary>A selection entry resolved against its instance: the entry, the machine file, the run folder and where
/// the plugins are read. An in-place entry (the workbench's manual mode) reads a chosen profile where it is; a lab
/// entry reads a source profile and prepares a profile in the lab.</summary>
public sealed class RunContext
{
    public string EntryPath { get; }
    public string? MachinePath { get; }
    public PluginNames Names { get; } = new();
    public Entry Entry { get; }
    public PyDict Machine { get; }
    public string Out { get; }
    public PyDict? InPlace { get; }
    public string Instance { get; } = "";
    public string Game { get; } = "";
    public string? Template { get; }
    public string SourceInstance { get; } = "";
    public string SourceProfile { get; } = "";
    public string SourceGame { get; } = "";
    public string Id { get; }
    public string Profile { get; } = "";

    /// <summary>Test seam: what Resolve returns, in place of reading an instance.</summary>
    public Func<PyDict>? ResolveWith { get; init; }

    public RunContext(string entryPath, string? machinePath, string outDir)
        : this(Py.D(PyJson.ReadPipelineFile(entryPath)), machinePath != null ? Py.D(PyJson.ReadPipelineFile(machinePath)) : new PyDict(), outDir)
    {
        EntryPath = PyPath.Str(entryPath);
        MachinePath = machinePath == null ? null : PyPath.Str(machinePath);
    }

    /// <summary>A context over documents already read; `id` stands in when the entry is not a whole one (tests).</summary>
    public RunContext(PyDict entry, PyDict machine, string outDir, string? id = null)
    {
        EntryPath = "";
        var doc = Names.Normalise(entry);
        Entry = new Entry(doc);
        Machine = machine;
        Out = PyPath.Str(outDir);
        if (id != null) { Id = id; return; }
        InPlace = Py.Truthy(doc.Get("inPlace")) ? Py.D(doc["inPlace"]) : null;
        if (InPlace != null)
        {
            Instance = PyPath.Str(Py.S(InPlace["instance"]));
            Game = PyPath.Str(Py.S(InPlace["game"]));
            SourceInstance = Instance;
            SourceProfile = Profile = Py.S(InPlace["profile"]);
            SourceGame = Game;
            Id = PyText.Str(doc["id"]);
            return;
        }
        var lab = new PyDict(Py.DictOrEmpty(machine.Get("lab"))).Update(Py.DictOrEmpty(doc.Get("lab")));
        Instance = PyPath.Str(Py.S(lab["instance"]));
        Game = PyPath.Str(Py.S(lab["game"]));
        Template = lab.Get("templateProfile") as string;
        var src = Py.D(doc["source"]);
        SourceInstance = PyPath.Str(Py.S(src["instance"]));
        SourceProfile = Py.S(src["profile"]);
        SourceGame = Py.Truthy(src.Get("game")) ? PyPath.Str(Py.S(src["game"])) : Game;
        Id = PyText.Str(doc["id"]);
        Profile = Py.Truthy(doc.Get("profile")) ? Py.S(doc["profile"]) : $"EP-{Id}";
    }

    /// <summary>Headers, baseline and every arm's ordered plugins, providers and mods.</summary>
    public PyDict Resolve() => ResolveWith != null ? ResolveWith() : InPlace != null ? InPlaceResolve.Resolve(this) : LabResolve.Resolve(this);
}
