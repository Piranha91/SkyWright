namespace RunControl;

/// <summary>The world states a Deep evaluation judges, generated from one arm's switch census (stategen.py): every
/// switch alone, every quest stage that flips two or more switches, and a not-explored row with its reason for
/// everything else the census names. Nothing the census names is dropped without a row.</summary>
public static class StateGen
{
    public const int SchemaVersion = 1;

    static readonly Dictionary<string, string> Reasons = new()
    {
        ["no-key"] = "The census row names no FormKey for this switch, so no state can name it.",
        ["chain-cycle"] = "The switch's enable-parent chain loops, so its flipped state is undefined.",
        ["chain-unresolved"] = "The switch's enable-parent chain is unresolved, so the opposite of its load-time state is unknown.",
        ["label-unknown"] = "A switch this quest stage flips is named by no switch in this arm's census.",
        ["label-ambiguous"] = "A switch this quest stage flips is named by several switches in this arm's census.",
        ["member-not-explorable"] = "A switch this quest stage flips cannot be explored alone (see its own row).",
        ["disabled-singletons"] = "Initially disabled references that no enable parent names are not switches; nothing flips them as a set.",
        ["player-parent"] = "References whose enable parent is the player change with the player, not with a switch.",
        ["unplaced-parent"] = "References linked to an enable parent that is not placed in this order cannot be flipped.",
        ["unreadable-plugins"] = "Plugins the census could not read: their switches are absent from this list, not zero.",
        ["no-trigger-map"] = "The census ran without the trigger map, so which switches quest stages flip together is unknown.",
        ["combinations"] = "Combinations of switches other than one quest stage's joint flip (stages in sequence, a scene or "
                           + "event flipping several, unrelated switches together) are not enumerated; their extent is unknown, not empty.",
    };

    static object? Or(object? a, object? b) => PyValue.Truthy(a) ? a : b;
    static IEnumerable<object?> Items(object? v) => PyValue.Truthy(v) ? PyValue.Iter(v) : [];

    /// <summary>The state document for one arm's census: {schemaVersion, census, generator, states, covered, notExplored}.
    /// generatorSha256 is the identity of the program that generates: the controller's package digest.</summary>
    public static PyDict Generate(PyDict census, string generatorSha256, string? censusPath = null, string? censusSha256 = null)
    {
        var switches = Items(census.Get("switches")).Select(PyValue.Dict).ToList();

        // Each label (editor id, and the FormKey itself) to the switches it names, each switch once.
        var labels = new Dictionary<string, List<PyDict>>(StringComparer.Ordinal);
        void Name(string label, PyDict s)
        {
            if (!labels.TryGetValue(label, out var named)) labels[label] = named = [];
            if (!named.Any(x => PyValue.Hash(x["key"]) == PyValue.Hash(s["key"]))) named.Add(s);
        }
        foreach (var s in switches)
        {
            if (!PyValue.Truthy(s.Get("key"))) continue;
            Name(PyText.Str(Or(s.Get("edid"), s["key"])), s);
            if (PyValue.Truthy(s.Get("edid"))) Name(PyText.Str(s["key"]), s);
        }

        static string? WhyNot(PyDict s) =>
            !PyValue.Truthy(s.Get("key")) ? "no-key"
            : PyValue.Truthy(s.Get("cycle")) ? "chain-cycle"
            : s.Get("baselineResolved") is false ? "chain-unresolved"
            : null;

        var singles = new List<object?>();
        var notExplored = new List<object?>();
        var explorable = new HashSet<string>(StringComparer.Ordinal);
        var byKeyThenEdid = switches.OrderBy(s => PyText.Str(Or(s.Get("key"), "")), PyOrder.CodePoint)
            .ThenBy(s => PyText.Str(Or(s.Get("edid"), "")), PyOrder.CodePoint);
        foreach (var s in byKeyThenEdid)
        {
            string? code = WhyNot(s);
            if (code != null)
            {
                notExplored.Add(new PyDict
                {
                    ["subject"] = Or(s.Get("key"), Or(s.Get("edid"), "(unnamed switch)")), ["kind"] = "single", ["code"] = code,
                    ["reason"] = Reasons[code], ["count"] = 1L,
                });
                continue;
            }
            explorable.Add(PyValue.Hash(s["key"]));
            var trigger = PyValue.Truthy(s.Get("trigger")) ? PyValue.Dict(s.Get("trigger")) : [];
            singles.Add(new PyDict
            {
                ["spec"] = s["key"], ["kind"] = "single", ["label"] = Or(s.Get("edid"), s["key"]),
                ["trigger"] = Or(trigger.Get("status"), "Unknown"),
            });
        }

        var joints = new List<object?>();
        var covered = new List<object?>();
        var jointFlips = Items(census.Get("jointFlips")).Select(PyValue.Dict).ToList();
        foreach (var j in jointFlips.OrderBy(j => PyText.Str(j.Get("stage")), PyOrder.CodePoint))
        {
            string stage = PyText.Str(j.Get("stage"));
            var members = Items(j.Get("switches")).ToList();
            var keys = new List<object?>();
            string? code = null;
            foreach (object? label in members)
            {
                var hits = labels.GetValueOrDefault(PyText.Str(label)) ?? [];
                if (hits.Count != 1) { code = hits.Count == 0 ? "label-unknown" : "label-ambiguous"; break; }
                if (!keys.Any(k => PyValue.Hash(k) == PyValue.Hash(hits[0]["key"]))) keys.Add(hits[0]["key"]);
            }
            if (code == null)
            {
                if (keys.Any(k => !explorable.Contains(PyValue.Hash(k)))) code = "member-not-explorable";
                else if (keys.Count == 1) { covered.Add(new PyDict { ["stage"] = stage, ["by"] = keys[0] }); continue; }
                else { joints.Add(new PyDict { ["spec"] = $"stage:{stage}", ["kind"] = "joint", ["switches"] = keys }); continue; }
            }
            notExplored.Add(new PyDict
            {
                ["subject"] = stage, ["kind"] = "joint", ["code"] = code, ["reason"] = Reasons[code], ["count"] = 1L, ["switches"] = members,
            });
        }

        // What is not a switch or not readable, with the census's own counts.
        var summary = PyValue.Truthy(census.Get("summary")) ? PyValue.Dict(census.Get("summary")) : [];
        var unreadable = Items(census.Get("unreadable")).ToList();
        foreach (var (code, n) in new (string, object?)[]
                 {
                     ("disabled-singletons", summary.Get("disabledSingletons")), ("player-parent", summary.Get("playerParentRefs")),
                     ("unplaced-parent", summary.Get("unplacedParentRefs")), ("unreadable-plugins", (long)unreadable.Count),
                 })
        {
            if (!PyValue.Truthy(n)) continue;
            var row = new PyDict
            {
                ["subject"] = code, ["kind"] = "class", ["code"] = code, ["reason"] = Reasons[code], ["count"] = PyValue.Whole(PyValue.Int(n)),
            };
            if (code == "unreadable-plugins") row["plugins"] = unreadable;
            notExplored.Add(row);
        }
        if (summary.ContainsKey("triggers") && summary["triggers"] == null)
            notExplored.Add(new PyDict
            {
                ["subject"] = "no-trigger-map", ["kind"] = "class", ["code"] = "no-trigger-map", ["reason"] = Reasons["no-trigger-map"], ["count"] = 1L,
            });
        notExplored.Add(new PyDict
        {
            ["subject"] = "combinations", ["kind"] = "class", ["code"] = "combinations", ["reason"] = Reasons["combinations"], ["count"] = null,
        });

        return new PyDict
        {
            ["schemaVersion"] = (long)SchemaVersion,
            ["census"] = new PyDict
            {
                ["path"] = censusPath, ["sha256"] = censusSha256, ["switches"] = (long)switches.Count, ["jointFlips"] = (long)jointFlips.Count,
            },
            ["generator"] = new PyDict { ["name"] = "stategen", ["sha256"] = generatorSha256 },
            ["states"] = singles.Concat(joints).ToList(),
            ["covered"] = covered,
            ["notExplored"] = notExplored,
        };
    }

    /// <summary>The document's text: the same census gives the same bytes.</summary>
    public static string Dumps(PyDict doc) => PyJson.Dumps(doc, indent: 1, ensureAscii: false) + "\n";

    /// <summary>One stateprobe --state spec per line, in the document's order.</summary>
    public static string SpecsText(PyDict doc) =>
        string.Concat(Items(doc.Get("states")).Select(s => (string)PyValue.Dict(s)["spec"]! + "\n"));

    /// <summary>Generate from a census file; the document names the file and the hash of its bytes.</summary>
    public static PyDict GenerateFile(string censusPath, string generatorSha256)
    {
        byte[] data = File.ReadAllBytes(censusPath);
        object? census = PyJson.Loads(PyJson.Decode(data, bom: false));
        return Generate(PyValue.Dict(census), generatorSha256, PyPath.Str(censusPath), Hashing.Sha256Bytes(data));
    }
}
