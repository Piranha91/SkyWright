using System.Text.RegularExpressions;

namespace RunControl;

/// <summary>The names enrichment (bundleenrich.enrich_names): in-game names for the places and objects the bundle's
/// rows name, their bases and models, and each evaluated mod's ordinary name. Places and objects are read with the
/// record reader's `housecarl_records` from the plugin files the plan records, each hashed first; the reads' request
/// and response travel with the names. Nothing is written when it refuses.</summary>
public static class NamesEnrichment
{
    static readonly Regex FormKey = new(@"^[0-9A-Fa-f]{6}:.+\.(?:esm|esp|esl)$", RegexOptions.CultureInvariant);
    static readonly Regex StateKind = new(@"^STATE-[A-Z]+@([0-9A-Fa-f]{6}:[^@]+)@", RegexOptions.CultureInvariant);
    static readonly Regex QuestTrigger = new(@"^quest (\S+) stage \d+ \(QF_\S+?_([0-9A-Fa-f]{8})\.", RegexOptions.CultureInvariant);
    static readonly Regex PluginSuffix = new(@"\.(esp|esm|esl)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // FK.match(str(v or "")).
    static bool IsFormKey(object? v) => FormKey.IsMatch(PyValue.Truthy(v) ? Ev.Str(v) : "");

    // str(v).lower().
    static string Low(object? v) => PyText.Lower(Ev.Str(v));

    // text.lower() of a value Python calls .lower() on directly.
    static string LowText(object? v) => PyText.Lower(v as string ?? throw new PyError($"'{PyValue.TypeName(v)}' object has no attribute 'lower'"));

    // The plugin name of a FormKey, lower-cased.
    static string PluginOf(string fk) => PyText.Lower(fk[(fk.IndexOf(':') + 1)..]);

    // (arm.requested or {}).plugins or [].
    static IEnumerable<object?> ArmPlugins(object? arm) => Ev.Items(Ev.OrEmpty(Ev.G(arm, "requested")).Get("plugins"));

    // The evidence files an enrichment reads rows from: every emit and enrichment whose path ends .json.
    static IEnumerable<object?> RowDocs(string d, PyDict manifest)
    {
        foreach (var e in Ev.Items(manifest.Get("evidence")))
        {
            if (!Ev.Str(Ev.G(e, "path", "")).EndsWith(".json", StringComparison.Ordinal) || Ev.G(e, "kind") is not ("emit" or "enrichment")) continue;
            yield return EnrichCommit.Load(PyPath.Join(d, (string)PyValue.Dict(e)["path"]!));
        }
    }

    // (doc.get(key) if doc is a dict else None) or [].
    static IEnumerable<object?> Rows(object? doc, string key) => doc is PyDict dict ? Ev.Items(dict.Get(key)) : [];

    // The Nexus catalogue cache's name for a mod id, read offline, and the file it came from.
    static (string? name, PyDict? source) CatalogueName(object? nexusId)
    {
        if (!PyValue.Truthy(nexusId)) return (null, null);
        string? Env(string k) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : null;
        string dir = Env("HOUSECARL_NEXUS_CATALOG_DIR")
            ?? (Env("HOUSECARL_DATA_DIR") is string data ? PyPath.Join(data, "nexus-catalog")
                : PyPath.Join(Env("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "houseCARL", "nexus-catalog"));
        string snap = PyPath.Join(dir, "pages", Ev.Str(nexusId), "snapshot.json");
        if (File.Exists(snap) && EnrichCommit.Load(snap) is PyDict doc && PyValue.Truthy(doc.Get("name")))
            return (Ev.Str(doc["name"]), new PyDict { ["path"] = snap, ["sha256"] = Hashing.Sha256File(snap) });
        return (null, null);
    }

    // What the reads have gathered for one FormKey so far.
    sealed class Found
    {
        public object? Type, EditorId, Name, From, Model, Bounds, Scale;
        public PyDict Placements = new();
        public List<object?> CarriedBy = [];
    }

    /// <summary>Runs the enrichment on bundle d. modNames: plugin to declared name. readerEnv: the reader's
    /// environment, or null for a private instance made for this read. Returns the step's summary.</summary>
    public static PyDict Run(string d, string readerCmd, PyDict modNames, PyDict? readerEnv)
    {
        var manifest = EnrichCommit.Guard(d, "names");
        var plan = Ev.OrEmpty(EnrichCommit.Load(PyPath.Join(d, "plan.json")));
        var scope = Ev.OrEmpty(EnrichCommit.Load(PyPath.Join(d, PyValue.Truthy(manifest.Get("scope")) ? Ev.Str(manifest["scope"]) : "scope/scope.json")));
        var targets = Ev.Items(plan.Get("targets")).Where(t => PyValue.Truthy(Ev.G(t, "name"))).Select(t => Ev.G(t, "name")).ToList();
        var arms = Ev.Items(plan.Get("arms")).ToList();

        // the arm under review: the plan's reviewArm when it names one, else the baseline arm, else the first arm
        object? review = plan.Get("reviewArm"), baseArm;
        if (PyValue.Truthy(review))
        {
            baseArm = arms.FirstOrDefault(a => Ev.Eq(Ev.G(a, "armId"), review))
                ?? throw new EnrichRefused($"the plan's reviewArm {Ev.Str(review)} names no arm of the plan, so there is no configuration to read names in");
        }
        else
            baseArm = arms.FirstOrDefault(a => Ev.Eq(Ev.G(a, "kind"), "baseline")) ?? (arms.Count > 0 ? arms[0] : new PyDict());
        var plugins = ArmPlugins(PyValue.Truthy(baseArm) ? baseArm : new PyDict()).ToList();
        var every = new PyDict();
        foreach (var a in arms)
            foreach (var x in ArmPlugins(a))
                if (!every.ContainsKey(Low(Ev.G(x, "name")))) every[Low(Ev.G(x, "name"))] = x;
        // a patch is a plugin some setups have and others lack, other than the evaluated mods
        var armSets = arms.Select(a => ArmPlugins(a).Select(x => Low(Ev.G(x, "name"))).ToHashSet(StringComparer.Ordinal)).ToList();
        var patches = every.Where(kv => !PyValue.In(PyValue.Dict(kv.Value)["name"], targets) && !armSets.All(s => s.Contains(kv.Key)))
            .Select(kv => PyValue.Dict(kv.Value)["name"]).ToList();

        // what to name: every FormKey a row of the bundle names, and every placed cell of the targets
        var fks = new HashSet<string>(StringComparer.Ordinal);
        var placementFks = new HashSet<string>(StringComparer.Ordinal);   // the references of placement rows
        var stateSwitches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var doc in RowDocs(d, manifest))
        {
            foreach (var c in Rows(doc, "Conflicts"))
            {
                // a row's own object and its partner (an overlap's other object, a contact's host)
                foreach (var n in Ev.Items(Ev.G(c, "Necessary")).Concat(Ev.Items(Ev.G(c, "Alternative"))))
                    if (n is PyDict row && IsFormKey(row.Get("FormKey"))) fks.Add((string)row["FormKey"]!);
                if (Ev.Eq(Ev.G(c, "Field"), "Placement"))
                    foreach (var n in Ev.Items(Ev.G(c, "Necessary")))
                        if (n is PyDict row && IsFormKey(row.Get("FormKey"))) placementFks.Add((string)row["FormKey"]!);
                // the switch a world-state clash's Kind names
                var m = StateKind.Match(PyValue.Truthy(Ev.G(c, "Kind")) ? Ev.Str(Ev.G(c, "Kind")) : "");
                if (m.Success) stateSwitches.Add(m.Groups[1].Value);
            }
            foreach (var p in Rows(doc, "Predicates"))
                foreach (string k in new[] { "FormKey", "Other" })
                    if (p is PyDict row && IsFormKey(row.Get(k))) fks.Add((string)row[k]!);
        }
        // the quests those switches are triggered by (switch census): editor id to the fragment's FormID, low 24 bits
        var questEdids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var doc in RowDocs(d, manifest))
            foreach (var sw in Rows(doc, "switches"))
            {
                if (sw is not PyDict row || row.Get("key") is not string key || !stateSwitches.Contains(key)) continue;
                foreach (var by in Ev.Items(Ev.OrEmpty(row.Get("trigger")).Get("by")))
                {
                    var m = QuestTrigger.Match(Ev.Str(by));
                    if (m.Success) questEdids[m.Groups[1].Value] = m.Groups[2].Value[^6..].ToUpperInvariant();
                }
            }
        var cells = new PyDict();
        foreach (var t in Ev.Items(scope.Get("targets")))
            foreach (var c in Ev.Items(Ev.OrEmpty(Ev.OrEmpty(Ev.G(t, "footprint")).Get("placed")).Get("cells")))
                if (PyValue.Truthy(Ev.G(c, "cell")) && IsFormKey(Ev.G(c, "cell"))) cells[(string)Ev.G(c, "cell")!] = Ev.G(c, "name");

        // which files to read: the defining master of each FormKey, the targets and the patches, in load order
        var namesNeeded = fks.Concat(cells.Keys).Select(PluginOf).Concat(targets.Select(LowText)).Concat(patches.Select(LowText)).ToHashSet(StringComparer.Ordinal);
        var order = plugins.Where(x => namesNeeded.Contains(Low(Ev.G(x, "name")))).ToList();
        foreach (var p in patches)
            if (every.ContainsKey(LowText(p)) && !PyValue.In(every[LowText(p)], plugins)) order.Add(every[LowText(p)]);
        foreach (var x in order.Select(PyValue.Dict))   // hash every file before reading any
        {
            string path = PyPath.Str(PyValue.Truthy(x.Get("path")) ? Ev.Str(x["path"]) : "");
            if (!PyValue.Truthy(x.Get("path")) || !File.Exists(path))
                throw new EnrichRefused($"{Ev.Str(x.Get("name"))} is not on disk at the path the plan records ({Ev.Str(x.Get("path"))})");
            if (Hashing.Sha256File(path) != PyText.Lower(PyValue.Truthy(x.Get("sha256")) ? Ev.Str(x["sha256"]) : ""))
                throw new EnrichRefused($"{Ev.Str(x.Get("name"))} at {path} does not hash to the sha256 the plan records, so it is not read");
        }

        string? temp = null;
        if (!PyValue.Truthy(readerEnv))
        {
            temp = Path.Combine(Path.GetTempPath(), "enrich-names-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(temp);
        }
        ReaderClient? opened = null;
        var found = new Dictionary<string, Found>(StringComparer.Ordinal);
        var bases = new PyDict();
        var quests = new PyDict();
        var targetsFk = PyOrder.SortedByCodePoint(fks.Union(cells.Keys));
        try
        {
            var reader = opened = new ReaderClient(readerCmd, PyValue.Truthy(readerEnv) ? readerEnv! : PrivateInstance.Create(temp!));
            // One plugin's version of the wanted records, a hundred a call; a short or redirected answer is refused.
            List<object?> Read(PyDict x, IEnumerable<string> wantedIn, string[] fields)
            {
                var got = new List<object?>();
                var wanted = PyOrder.SortedByCodePoint(wantedIn);
                for (int i = 0; i < wanted.Count; i += 100)
                {
                    var part = wanted.Skip(i).Take(100).Cast<object?>().ToList();
                    var r = reader.Call("tools/call", new PyDict
                    {
                        ["name"] = "housecarl_records",
                        ["arguments"] = new PyDict
                        {
                            ["formids"] = part, ["source"] = x["path"], ["format"] = "json", ["limit"] = (long)part.Count, ["max_chars"] = 4000000L,
                            ["project"] = new PyDict { ["form"] = "fields", ["fields"] = fields.Cast<object?>().ToList() },
                        },
                    });
                    string text = string.Concat(Ev.Items(Ev.OrEmpty(r.Get("result")).Get("content"))
                        .Select(c => Ev.G(c, "text", "") as string ?? throw new PyError("sequence item: expected str instance")));
                    PyDict body;
                    if (PyValue.Strip(text).StartsWith('{'))
                    {
                        try { body = PyValue.Dict(PyJson.Loads(text)); }
                        catch (FormatException ex) { throw new PyValueError(ex.Message); }
                    }
                    else body = new PyDict();
                    string src = PyValue.Truthy(body.Get("source")) ? Ev.Str(body["source"]) : "";
                    if (body.Count == 0 || body.Get("ok") is false)
                        throw new EnrichRefused($"the reader could not read {Ev.Str(x["name"])}: {HeadPoints(PyValue.Truthy(body.Get("error")) ? Ev.Str(body["error"]) : text, 300)}");
                    if (PyText.Lower(src).Contains("active in the load order", StringComparison.Ordinal) && !PyText.Lower(src).Contains("not active", StringComparison.Ordinal))
                        throw new EnrichRefused($"the reader answered {Ev.Str(x["name"])} from its active load order ({src}), not from the "
                            + $"file at {Ev.Str(x["path"])}; start it on an instance in which no plugin of this setup is active");
                    int count = PyValue.Len(Ev.OrList(body.Get("records")));
                    if (PyValue.Truthy(body.Get("truncated")) || PyValue.Truthy(body.Get("spilled")) || count != part.Count)
                        throw new EnrichRefused($"the reader's answer for {Ev.Str(x["name"])} was cut short ({count} of "
                            + $"{part.Count} records), so its names are not used");
                    got.AddRange(Ev.Items(body.Get("records")));
                }
                return got;
            }

            // A later plugin's version wins, field by field, where it has a value (load order). Null: the plugin has no version.
            Dictionary<string, object?>? Keep(object? recRaw, PyDict x)
            {
                var rec = PyValue.Dict(recRaw);
                if (PyValue.Truthy(rec.Get("error")) || !PyValue.Truthy(rec.Get("formid"))) return null;
                var fl = new Dictionary<string, object?>(StringComparer.Ordinal);
                var absent = new HashSet<string>(StringComparer.Ordinal);
                foreach (var f in Ev.Items(rec.Get("fields")))
                {
                    if (Ev.G(f, "path") is not string path) continue;
                    if (Ev.G(f, "value") != null) fl[path] = Ev.G(f, "value");
                    if (Ev.Eq(Ev.G(f, "note"), "(absent)")) absent.Add(path);
                }
                string id = (string)rec["formid"]!;
                found.TryGetValue(id, out var prev);
                prev ??= new Found();
                object? Value(string k) => fl.GetValueOrDefault(k);
                // the model a later version carries wins; a version that reads the field as absent has none
                object? model = fl.ContainsKey("Model.File") ? fl["Model.File"] : absent.Contains("Model.File") ? "" : prev.Model;
                object? bounds = prev.Bounds;
                if (Value("ObjectBounds.First") != null && Value("ObjectBounds.Second") != null)
                {
                    try { bounds = Floats($"{Ev.Str(Value("ObjectBounds.First"))},{Ev.Str(Value("ObjectBounds.Second"))}"); }
                    catch (PyValueError) { bounds = prev.Bounds; }
                }
                object? scale = Value("Scale") is { } s && !Ev.Eq(s, "") ? PyShell.Float(s) : absent.Contains("Scale") ? 1.0 : prev.Scale;
                var placements = new PyDict(prev.Placements);
                if (Value("Placement.Position") != null)
                {
                    try { placements[Ev.Str(x["name"])] = Floats(Ev.Str(Value("Placement.Position"))).Take(3).ToList(); }
                    catch (PyValueError) { }
                }
                bool named = PyValue.Truthy(Value("Name"));
                found[id] = new Found
                {
                    Type = PyValue.Truthy(rec.Get("type")) ? rec["type"] : prev.Type,
                    EditorId = PyValue.Truthy(rec.Get("editorid")) ? rec["editorid"] : prev.EditorId,
                    Name = named ? Value("Name") : prev.Name, From = named ? x["name"] : prev.From,
                    Model = model, Bounds = bounds, Scale = scale, Placements = placements,
                    CarriedBy = [.. prev.CarriedBy, x["name"]],
                };
                return fl;
            }

            foreach (var x in order.Select(PyValue.Dict))
                foreach (var rec in Read(x, targetsFk, ["Name", "Base", "Scale", "Placement.Position"]))
                {
                    var fl = Keep(rec, x);
                    if (fl != null && PyValue.Truthy(fl.GetValueOrDefault("Base")) && IsFormKey(fl["Base"]))
                        bases[(string)PyValue.Dict(rec)["formid"]!] = fl["Base"];
                }
            var baseFks = PyOrder.SortedByCodePoint(bases.Select(kv => (string)kv.Value!).Distinct());
            if (baseFks.Count > 0)
            {
                var from = baseFks.Select(PluginOf).Concat(targets.Select(LowText)).ToHashSet(StringComparer.Ordinal);
                foreach (var x in plugins.Where(p => from.Contains(Low(Ev.G(p, "name")))).Select(PyValue.Dict))
                    foreach (var rec in Read(x, baseFks, ["Name", "Model.File", "ObjectBounds.First", "ObjectBounds.Second"]))
                        Keep(rec, x);
            }
            // a quest's in-game name: the defining plugin is the one whose own file holds that FormID under that
            // editor id; then the last version in the order with a name wins
            foreach (var (edid, low) in questEdids.OrderBy(kv => kv.Key, PyOrder.CodePoint))
            {
                string? owner = null;
                foreach (var x in plugins.Select(PyValue.Dict))
                {
                    var got = Read(x, [$"{low}:{Ev.Str(x["name"])}"], ["Name"]);
                    if (got.Count > 0 && !PyValue.Truthy(Ev.G(got[0], "error")) && Ev.Eq(Ev.G(got[0], "editorid"), edid))
                    {
                        owner = $"{low}:{Ev.Str(x["name"])}";
                        break;
                    }
                }
                if (owner == null) continue;
                var entry = new PyDict { ["name"] = null, ["formKey"] = owner, ["from"] = null };
                foreach (var x in plugins.Select(PyValue.Dict))
                    foreach (var rec in Read(x, [owner], ["Name"]))
                    {
                        if (PyValue.Truthy(Ev.G(rec, "error")) || !PyValue.Truthy(Ev.G(rec, "formid"))) continue;
                        object? name = Ev.Items(Ev.G(rec, "fields")).LastOrDefault(f => Ev.Eq(Ev.G(f, "path"), "Name") && Ev.G(f, "value") != null) is { } f0
                            ? Ev.G(f0, "value") : null;
                        if (PyValue.Truthy(name))
                        {
                            entry["name"] = name;
                            entry["from"] = x["name"];
                        }
                    }
                quests[edid] = entry;
            }
        }
        finally
        {
            opened?.Close();
            if (temp != null)
                try { Directory.Delete(temp, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        var places = new PyDict();
        var objects = new PyDict();
        var missing = new List<object?>();
        foreach (var (fk, editor) in cells)
        {
            found.TryGetValue(fk, out var r);
            object? key = PyValue.Truthy(r?.EditorId) ? r!.EditorId : editor;
            if (PyValue.Truthy(r?.Name)) places[Ev.Str(key)] = new PyDict { ["name"] = r!.Name, ["formKey"] = fk, ["from"] = r.From };
            else if (PyValue.Truthy(key)) missing.Add(new PyDict { ["formKey"] = fk, ["editorId"] = key, ["type"] = PyValue.Truthy(r?.Type) ? r!.Type : "Cell" });
        }
        var baseSet = PyOrder.SortedByCodePoint(bases.Select(kv => (string)kv.Value!).Distinct());
        foreach (string fk in baseSet)
        {
            found.TryGetValue(fk, out var r);
            if (PyValue.Truthy(r?.Name) && PyValue.Truthy(r?.EditorId))
                objects[Ev.Str(r!.EditorId)] = new PyDict { ["name"] = r.Name, ["formKey"] = fk, ["from"] = r.From };
            else missing.Add(new PyDict { ["formKey"] = fk, ["editorId"] = r?.EditorId, ["type"] = r?.Type });
        }
        var mods = new PyDict();
        var nexus = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var t in Ev.Items(scope.Get("targets"))) nexus[PyValue.Hash(Ev.G(t, "name"))] = Ev.OrEmpty(Ev.G(t, "footprint")).Get("nexusId");
        foreach (var m in targets.Concat(patches.Where(p => !PyValue.In(p, targets))))
        {
            string mod = (string)m!;
            if (PyValue.Truthy(modNames.Get(mod)))
            {
                mods[mod] = new PyDict { ["name"] = modNames[mod], ["source"] = "declared" };
                continue;
            }
            var (name, source) = CatalogueName(nexus.GetValueOrDefault(PyValue.Hash(m)));
            mods[mod] = name != null
                ? new PyDict { ["name"] = name, ["source"] = "nexus-catalogue", ["sourceFile"] = source }
                : new PyDict { ["name"] = PluginSuffix.Replace(mod, ""), ["source"] = "plugin-name" };
        }

        const string reqRel = "enrichment/names/01-request.jsonl", respRel = "enrichment/names/02-response.jsonl", outRel = "enrichment/names/names.json";
        Directory.CreateDirectory(PyPath.Join(d, "enrichment", "names"));
        Ev.WriteText(PyPath.Join(d, reqRel), string.Concat(opened!.Requests.Select(m => PyJson.Dumps(m) + "\n")));
        Ev.WriteText(PyPath.Join(d, respRel), string.Concat(opened.Responses.Select(m => PyJson.Dumps(m) + "\n")));
        var derived = order.Select(PyValue.Dict).Select(x => (object?)new PyDict
        {
            ["path"] = Ev.Str(x["path"]), ["sha256"] = Low(x["sha256"]), ["plugin"] = x["name"], ["outside"] = true,
        }).ToList();

        // every object a row names: its base and the plugins read that carry a version of it, in load order; the cell
        // each version is filed in, read from the file's own group tree
        var files = new Dictionary<string, PluginWalk.Plugin>(StringComparer.Ordinal);
        PluginWalk.Plugin FileOf(PyDict x)
        {
            string path = PyPath.Str(Ev.Str(x["path"]));
            if (!files.TryGetValue(path, out var p)) files[path] = p = new PluginWalk.Plugin(path);
            return p;
        }
        var placedFks = targetsFk.Where(fk => found.ContainsKey(fk) && bases.ContainsKey(fk)).ToList();
        var cellsBy = new Dictionary<string, PyDict>(StringComparer.Ordinal);
        foreach (var x in order.Select(PyValue.Dict))
            foreach (var (fk, cell) in PluginWalk.FiledCells(FileOf(x), placedFks))
            {
                if (!cellsBy.ContainsKey(fk)) cellsBy[fk] = new PyDict();
                cellsBy[fk][Ev.Str(x["name"])] = cell;
            }
        // for each placement row's reference: each non-defining carrier's overrides in the cell that version is filed
        // in, as height changes against the defining master's own positions
        var byName = new Dictionary<string, PyDict>(StringComparer.Ordinal);
        foreach (var x in order.Select(PyValue.Dict)) byName[Low(x["name"])] = x;
        var baseCache = new Dictionary<string, Dictionary<string, double[]?>>(StringComparer.Ordinal);
        double[]? BasePositions(string fk)
        {
            string plugin = PluginOf(fk);
            if (!baseCache.TryGetValue(plugin, out var pos))
                baseCache[plugin] = pos = byName.TryGetValue(plugin, out var x) ? PluginWalk.OwnPositions(FileOf(x)) : [];
            return pos.GetValueOrDefault(PyText.Lower(fk));
        }
        var overrides = new Dictionary<(string plugin, string cell), PyDict>();
        foreach (string fk in PyOrder.SortedByCodePoint(placementFks))
            foreach (var (plugin, cellRaw) in cellsBy.GetValueOrDefault(fk) ?? new PyDict())
            {
                string cell = (string)cellRaw!;
                if (PyText.Lower(plugin) == PluginOf(fk) || cell.Length == 0 || cell.EndsWith("!persistent", StringComparison.Ordinal)) continue;
                if (overrides.ContainsKey((plugin, cell))) continue;
                PyDict? got = byName.TryGetValue(PyText.Lower(plugin), out var x) ? PluginWalk.CellOverrides(FileOf(x), cell, BasePositions) : null;
                overrides[(plugin, cell)] = got ?? new PyDict { ["dz"] = new List<object?>(), ["disabled"] = 0L, ["count"] = 0L };
            }
        var refs = new PyDict();
        foreach (string fk in targetsFk.Where(found.ContainsKey))
        {
            var r = found[fk];
            var filed = cellsBy.GetValueOrDefault(fk) ?? new PyDict();
            var row = new PyDict
            {
                ["base"] = bases.Get(fk), ["carriedBy"] = Distinct(r.CarriedBy), ["scale"] = r.Scale, ["cells"] = filed,
            };
            if (placementFks.Contains(fk))
            {
                row["placements"] = r.Placements;
                var over = new PyDict();
                foreach (var (pl, cl) in filed)
                    if (overrides.TryGetValue((pl, (string)cl!), out var o)) over[pl] = new PyDict { ["cell"] = cl }.Update(o);
                row["cellOverrides"] = over;
            }
            refs[fk] = row;
        }
        var baseRows = new PyDict();
        foreach (string fk in baseSet.Where(found.ContainsKey))
            baseRows[fk] = new PyDict { ["type"] = found[fk].Type, ["editorId"] = found[fk].EditorId, ["model"] = found[fk].Model, ["bounds"] = found[fk].Bounds };
        // what could not be read: an object no plugin read carries, a base no plugin read carries
        var unreadObjects = PyOrder.SortedByCodePoint(fks.Where(fk => !found.ContainsKey(fk)));
        var unreadBases = PyOrder.SortedByCodePoint(baseSet.Where(fk => !(found.TryGetValue(fk, out var r) && PyValue.Truthy(r.Type))));
        EnrichCommit.Write(PyPath.Join(d, outRel), new PyDict
        {
            ["schema"] = "names-v1", ["places"] = places, ["objects"] = objects, ["mods"] = mods, ["refs"] = refs, ["bases"] = baseRows,
            ["quests"] = quests,
            ["unread"] = new PyDict { ["objects"] = unreadObjects.Cast<object?>().ToList(), ["bases"] = unreadBases.Cast<object?>().ToList() },
            ["readOrder"] = order.Select(x => PyValue.Dict(x)["name"]).ToList(), ["missing"] = missing, ["derivedFrom"] = derived,
            ["capture"] = new List<object?>
            {
                new PyDict { ["path"] = reqRel, ["sha256"] = Hashing.Sha256File(PyPath.Join(d, reqRel)) },
                new PyDict { ["path"] = respRel, ["sha256"] = Hashing.Sha256File(PyPath.Join(d, respRel)) },
            },
            ["limits"] = "each place or object is read from its defining master, the evaluated mods and "
                + "the patches; a name another mod in the order gives it is not read",
        });
        var outputs = new List<PyDict>
        {
            EnrichCommit.Row(outRel, d, "names", new PyDict { ["checkId"] = null, ["armId"] = null, ["derivedFrom"] = derived }),
            EnrichCommit.Row(reqRel, d, "names", new PyDict { ["role"] = "capture", ["checkId"] = null, ["armId"] = null }),
            EnrichCommit.Row(respRel, d, "names", new PyDict { ["role"] = "capture", ["checkId"] = null, ["armId"] = null }),
        };
        EnrichCommit.Commit(d, manifest, "names", outputs);
        var sources = new PyDict();
        foreach (var (m, v) in mods) sources[m] = PyValue.Dict(v)["source"];
        return new PyDict
        {
            ["kind"] = "names", ["places"] = (long)places.Count, ["objects"] = (long)objects.Count, ["missing"] = (long)missing.Count, ["mods"] = sources,
            ["counts"] = new PyDict
            {
                ["objectsNamed"] = (long)fks.Count, ["objectsUnread"] = (long)unreadObjects.Count,
                ["bases"] = (long)baseSet.Count, ["basesUnread"] = (long)unreadBases.Count,
            },
        };
    }

    // [float(v) for v in text.split(",")].
    static List<object?> Floats(string text) => text.Split(',').Select(v => (object?)PyShell.Float(v)).ToList();

    // list(dict.fromkeys(items)): the first of each, in order.
    static List<object?> Distinct(List<object?> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return items.Where(x => seen.Add(PyValue.Hash(x))).ToList();
    }

    // text[:n], counted in code points as Python counts.
    static string HeadPoints(string s, int n)
    {
        int i = 0;
        for (int points = 0; i < s.Length && points < n; points++) i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
        return s[..i];
    }
}
