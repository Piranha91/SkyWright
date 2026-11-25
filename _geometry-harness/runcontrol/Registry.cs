using System.Numerics;

namespace RunControl;

/// <summary>The check registry: load it, validate it, and derive what applies to a measured scope (evalregistry.py).
/// The registry stays data: nothing here names a check. Applicability is yes, no or unknown, and unknown never becomes no.</summary>
public static class Registry
{
    public const int SchemaVersion = 1;

    /// <summary>The closed vocabularies a list of checks is validated against; a value outside one is a validation
    /// problem. runcontrol's own commands validate only against <see cref="App"/>; another program using this library
    /// for a list of its own passes that list's vocabulary, and nothing it passes reaches runcontrol's registry.</summary>
    public sealed record Vocabulary(
        IReadOnlySet<string> AdapterKinds, IReadOnlySet<string> KindsNeedingReason, IReadOnlySet<string> ResultKinds,
        IReadOnlySet<string> KindsWithoutOutput, IReadOnlySet<string> KindsRequiringOptIn, IReadOnlySet<string> KindsWithChild,
        IReadOnlySet<string> EvidenceStrengths);

    /// <summary>The app registry's vocabulary.</summary>
    public static readonly Vocabulary App = new(
        AdapterKinds: new HashSet<string>(["scopeprobe", "ordersweep-probe", "probe-emit", "probe-json", "probe-target",
            "adviceprobe", "patchprobe", "derived", "manual", "mcp-only", "live-import", "unsupported"]),
        KindsNeedingReason: new HashSet<string>(["manual", "mcp-only", "unsupported"]),
        ResultKinds: new HashSet<string>(["detector", "catalogue", "discovery"]),
        KindsWithoutOutput: new HashSet<string>(["derived", "mcp-only", "manual", "unsupported", "live-import"]),
        KindsRequiringOptIn: new HashSet<string>(["live-import"]),
        KindsWithChild: new HashSet<string>(["scopeprobe", "ordersweep-probe", "probe-emit", "probe-json", "probe-target",
            "adviceprobe", "patchprobe"]),
        EvidenceStrengths: new HashSet<string>(["record-fact", "calibrated-inference", "engine-observation"]));

    static readonly HashSet<string> TriggerKeys = ["always", "anyType", "placed", "exterior", "land", "navmesh", "overlays", "nexusId",
        "masters", "placedMeshes", "anyOf", "allOf"];
    static readonly HashSet<string> Extents = ["script-placement", "alias-fill", "bos-swaps", "asset-replacers", "door-destinations",
        "generated-output"];

    static object?[] Tuple(params object?[] items) => items;

    // ---- loading

    /// <summary>The registry JSON at path; anything but an object is refused.</summary>
    public static PyDict Load(string path)
    {
        object? doc = PyJson.Loads(PyJson.Decode(File.ReadAllBytes(path), bom: false));
        return doc as PyDict ?? throw new Refusal($"{path}: the registry must be a JSON object, got {PyValue.TypeName(doc)}");
    }

    /// <summary>The sha256 of the registry file's bytes, as the plan records it.</summary>
    public static string Sha256(string path) => Hashing.Sha256Bytes(File.ReadAllBytes(path));

    /// <summary>The check with this id, or null when the registry has none.</summary>
    public static PyDict? CheckById(PyDict registry, string checkId)
    {
        if (PyValue.Truthy(registry.Get("checks")))
            foreach (object? c in PyValue.Iter(registry.Get("checks")))
                if (PyValue.Eq(PyValue.Dict(c).Get("id"), checkId)) return (PyDict)c!;
        return null;
    }

    /// <summary>The family rows, in registry order.</summary>
    public static List<object?> Families(PyDict registry) =>
        PyValue.Truthy(registry.Get("families")) ? PyValue.Iter(registry.Get("families")).ToList() : [];

    // ---- validation

    /// <summary>Every problem with this registry, as sentences; an empty list means valid.</summary>
    public static List<string> Validate(PyDict registry) => Validate(registry, App);

    /// <summary>Every problem with a list of checks of the registry's shape, against the vocabulary given (see
    /// <see cref="Vocabulary"/>); an empty list means valid.</summary>
    public static List<string> Validate(PyDict registry, Vocabulary vocabulary)
    {
        var (AdapterKinds, KindsNeedingReason, ResultKinds, KindsWithoutOutput, KindsRequiringOptIn, KindsWithChild, EvidenceStrengths) =
            (vocabulary.AdapterKinds, vocabulary.KindsNeedingReason, vocabulary.ResultKinds, vocabulary.KindsWithoutOutput,
             vocabulary.KindsRequiringOptIn, vocabulary.KindsWithChild, vocabulary.EvidenceStrengths);
        var problems = new List<string>();

        if (!PyValue.Eq(registry.Get("schemaVersion"), (long)SchemaVersion))
            problems.Add($"schemaVersion must be {SchemaVersion}; got {PyText.Repr(registry.Get("schemaVersion"))}");
        if (!PyValue.IsIntOrBool(registry.Get("registryVersion")))
            problems.Add("registryVersion must be an integer (the plan records it and finalize compares it)");

        var familyRows = registry.Get("families") as List<object?>;
        if (familyRows == null || familyRows.Count == 0)
        {
            problems.Add("families must be a non-empty list");
            familyRows = [];
        }
        var familyIds = new List<string>();
        for (int index = 0; index < familyRows.Count; index++)
        {
            if (familyRows[index] is not PyDict family) { problems.Add($"families[{index}] must be an object"); continue; }
            if (family.Get("id") is not string fid || fid.Length == 0) { problems.Add($"families[{index}] has no id"); continue; }
            if (familyIds.Contains(fid)) problems.Add($"duplicate family id: {fid}");
            familyIds.Add(fid);
            if (!PyValue.Truthy(family.Get("packetRow"))) problems.Add($"family {fid} does not name the packet row it accounts for");
        }

        var checks = registry.Get("checks") as List<object?>;
        if (checks == null || checks.Count == 0)
        {
            problems.Add("checks must be a non-empty list");
            checks = [];
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < checks.Count; index++)
        {
            if (checks[index] is not PyDict check) { problems.Add($"checks[{index}] must be an object"); continue; }
            if (check.Get("id") is not string cid || cid.Length == 0) { problems.Add($"checks[{index}] has no id"); continue; }
            if (!ids.Add(cid)) problems.Add($"duplicate check id: {cid}");
        }

        foreach (var check in checks.OfType<PyDict>())
        {
            object? rawId = PyValue.Truthy(check.Get("id")) ? check.Get("id") : "<unnamed>";
            object? family = check.Get("family");
            if (!(family is string fs && familyIds.Contains(fs))) problems.Add($"{PyText.Str(rawId)}: unknown family {PyText.Repr(family)}");
            if (rawId is not string cid) throw new PyError($"'{PyValue.TypeName(rawId)}' object has no attribute 'split'");
            if (!PyValue.Eq(cid.Split('.', 2)[0], family))
                problems.Add($"{cid}: the id must be prefixed with its family ({PyText.Repr(family)})");
            if (!PyValue.Truthy(check.Get("title"))) problems.Add($"{cid}: no title");
            object? required = check.Get("required");
            if (required is not bool) problems.Add($"{cid}: required must be true or false");

            if (check.Get("adapter") is not PyDict adapter)
            {
                problems.Add($"{cid}: no adapter");
                adapter = [];
            }
            object? kind = adapter.Get("kind");
            string kindText = PyText.Str(kind);
            if (!PyValue.InSet(kind, AdapterKinds)) problems.Add($"{cid}: unknown adapter kind {PyText.Repr(kind)}");
            if (PyValue.InSet(kind, KindsNeedingReason) && !PyValue.Truthy(check.Get("unsupportedReason")))
                problems.Add($"{cid}: kind {kindText} needs an unsupportedReason saying what is missing");
            bool withChild = PyValue.InSet(kind, KindsWithChild);
            if (withChild && !PyValue.Truthy(adapter.Get("exe"))) problems.Add($"{cid}: adapter kind {kindText} names no exe");
            if (adapter.Get("args") is not (null or List<object?>)) problems.Add($"{cid}: adapter args must be a list of argv tokens");

            // Whether the child takes --instance/--profile/--game is never a default.
            if (withChild && adapter.Get("commonFlags") is not bool)
                problems.Add($"{cid}: adapter.commonFlags must be true or false — a child either takes "
                             + "--instance/--profile/--game or finds them through the environment");
            if (!withChild && adapter.ContainsKey("commonFlags"))
                problems.Add($"{cid}: kind {kindText} runs no child, so adapter.commonFlags means nothing");

            foreach (string field in new[] { "population", "state" })
                if (adapter.ContainsKey(field) && !(adapter[field] is string text && text.Length > 0))
                    problems.Add($"{cid}: adapter.{field}, when present, must be the non-empty string "
                                 + "the probe's emit actually carries");

            if (adapter.ContainsKey("primitiveSolidityFlag"))
            {
                if (!PyValue.Eq(kind, "probe-emit") || !PyValue.Eq(adapter.Get("exe"), "stateprobe"))
                    problems.Add($"{cid}: adapter.primitiveSolidityFlag is passed only to stateprobe's probe-emit check, not by kind {kindText}");
                else if (!PyValue.Eq(adapter["primitiveSolidityFlag"], "--primitive-solidity"))
                    problems.Add($"{cid}: adapter.primitiveSolidityFlag must be \"--primitive-solidity\", the flag stateprobe takes");
            }
            if (adapter.ContainsKey("populationFlag"))
            {
                if (!PyValue.In(kind, Tuple("ordersweep-probe", "probe-emit")))
                    problems.Add($"{cid}: adapter.populationFlag is passed only by the ordersweep and probe-emit adapters, not by kind {kindText}");
                else if (!PyValue.Eq(adapter["populationFlag"], "--population"))
                    problems.Add($"{cid}: adapter.populationFlag must be \"--population\", the flag the scoped probes take");
            }

            object? output = adapter.Get("output");
            object? result = adapter.Get("result");
            // The files a text probe writes besides its printed output, named with {out}, {run} and {arm}.
            if (adapter.ContainsKey("files"))
            {
                var files = adapter["files"] as List<object?>;
                if (!PyValue.Eq(output, "text") || !PyValue.Eq(kind, "probe-target"))
                    problems.Add($"{cid}: adapter.files is read only for a probe-target adapter whose output is text");
                else if (files == null || files.Count == 0 || !files.All(f => f is string s && PyValue.Strip(s).Length > 0))
                    problems.Add($"{cid}: adapter.files must be a non-empty list of file paths");
                else if (!files.Cast<string>().All(f => (f.Contains("{out}") || f.Contains("{run}")) && f.Contains("{arm}")))
                    problems.Add($"{cid}: each adapter.files path names the arm ({{arm}}) under the run ({{out}} or {{run}}), "
                                 + "so two arms never write one file");
            }
            if (PyValue.InSet(kind, KindsWithoutOutput) && output != null)
                problems.Add($"{cid}: kind {kindText} runs no child, so adapter.output must be null");
            else if (PyValue.In(output, Tuple("json", "text")) && !PyValue.Eq(kind, "scopeprobe"))
            {
                if (result is not PyDict res)
                    problems.Add($"{cid}: a {output} adapter needs adapter.result "
                                 + "{kind, findings, judged, unjudgeable} saying how its output is read");
                else
                    ValidateResult(cid, (string)output!, res, problems, ResultKinds);
            }

            object? applic = check.Get("applicability");
            var applicDict = applic as PyDict;
            object? trigger = applicDict?.Get("trigger");
            if (PyValue.Truthy(required) && !PyValue.Truthy(trigger))
                problems.Add($"{cid}: a required check must carry an applicability trigger");
            if (trigger != null) problems.AddRange(ValidateTrigger(trigger, "trigger").Select(p => $"{cid}: {p}"));
            if (applicDict != null && trigger != null && !PyValue.Truthy(applicDict.Get("note")))
                problems.Add($"{cid}: the trigger carries no note saying why it is the right trigger");
            object? depends = applicDict?.Get("dependsOnExtents");
            if (depends != null)
            {
                if (depends is not List<object?> extents) problems.Add($"{cid}: applicability.dependsOnExtents must be a list of extent keys");
                else
                    foreach (object? extent in extents)
                        if (!PyValue.InSet(extent, Extents)) problems.Add($"{cid}: unknown unmeasured extent {PyText.Repr(extent)}");
            }

            if (check.Get("inputs") is not PyDict inputs) problems.Add($"{cid}: no inputs block ({{arms, states}})");
            else
            {
                if (!PyValue.In(inputs.Get("arms"), Tuple("all", "baseline"))) problems.Add($"{cid}: inputs.arms must be 'all' or 'baseline'");
                if (!PyValue.In(inputs.Get("states"), Tuple("baseline", "selected", "all")))
                    problems.Add($"{cid}: inputs.states must be 'baseline', 'selected' or 'all'");
            }

            if (check.Get("evidence") is not PyDict evidence) problems.Add($"{cid}: no evidence block");
            else
            {
                if (!PyValue.InSet(evidence.Get("strength"), EvidenceStrengths))
                    problems.Add($"{cid}: unknown evidence strength {PyText.Repr(evidence.Get("strength"))}");
                if (!evidence.ContainsKey("claim")) problems.Add($"{cid}: evidence names no claim row (use null and say why in limits)");
                if (evidence.Get("claim") == null && !PyValue.Truthy(evidence.Get("limits")))
                    problems.Add($"{cid}: no claim row and no limits — a check with no ledger row must say so");
            }

            if (check.Get("manualDecisionAllowed") is not bool) problems.Add($"{cid}: manualDecisionAllowed must be true or false");

            object? optIn = check.ContainsKey("optIn") ? check["optIn"] : false;
            if (optIn is not bool) problems.Add($"{cid}: optIn, when present, must be true or false");
            else if ((bool)optIn && PyValue.Truthy(required))
                problems.Add($"{cid}: an opt-in check cannot be required by default (required must be false; "
                             + "opting in makes it required for that run)");
            if (PyValue.InSet(kind, KindsRequiringOptIn) && optIn is not true)
                problems.Add($"{cid}: adapter kind {kindText} needs optIn: true (the controller never launches the game)");

            object? prereqs = check.Get("prerequisites");
            if (prereqs == null) problems.Add($"{cid}: prerequisites must be a list (use [] for none)");
            else if (prereqs is not List<object?> list) problems.Add($"{cid}: prerequisites must be a list");
            else
                foreach (object? prereq in list)
                    if (!PyValue.InSet(prereq, ids)) problems.Add($"{cid}: prerequisite names an unknown check id: {PyText.Repr(prereq)}");
        }

        problems.AddRange(Cycles(checks, ids));

        // A check on every arm cannot rest on a prerequisite that only runs on the baseline.
        var armsOf = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var c in checks.OfType<PyDict>())
            if (c.Get("id") is string id) armsOf[id] = (PyValue.Truthy(c.Get("inputs")) ? PyValue.Dict(c.Get("inputs")) : []).Get("arms");
        foreach (var check in checks.OfType<PyDict>())
        {
            PyValue.Hash(check.Get("id"));
            if (check.Get("id") is not string id || !PyValue.Eq(armsOf.GetValueOrDefault(id), "all")) continue;
            foreach (object? prereq in PyValue.Truthy(check.Get("prerequisites")) ? PyValue.Iter(check.Get("prerequisites")) : [])
            {
                PyValue.Hash(prereq);
                if (prereq is string p && PyValue.Eq(armsOf.GetValueOrDefault(p), "baseline"))
                    problems.Add($"{id} runs on every arm but its prerequisite {p} runs on the "
                                 + "baseline only, so on a candidate arm it would be blocked for want of an input");
            }
        }

        var withChecks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in checks.OfType<PyDict>())
        {
            PyValue.Hash(c.Get("family"));
            if (c.Get("family") is string f) withChecks.Add(f);
        }
        foreach (string fid in familyIds)
            if (!withChecks.Contains(fid)) problems.Add($"family {fid} carries no check — every family must be accounted for");

        return problems;
    }

    // How a json or text output is read: the result kind and the three count specs.
    static void ValidateResult(string cid, string output, PyDict result, List<string> problems, IReadOnlySet<string> ResultKinds)
    {
        if (!PyValue.InSet(result.Get("kind"), ResultKinds)) problems.Add($"{cid}: unknown result kind {PyText.Repr(result.Get("kind"))}");
        foreach (string field in new[] { "findings", "judged", "unjudgeable" })
        {
            if (!result.ContainsKey(field))
            {
                problems.Add($"{cid}: adapter.result has no {field} spec (use null and "
                             + "say in result.note that the probe prints no such count)");
                continue;
            }
            object? spec = result[field];
            if (spec == null)
            {
                if (!PyValue.Truthy(result.Get("note")))
                    problems.Add($"{cid}: adapter.result.{field} is null with no note "
                                 + "saying why the probe prints no such count");
            }
            else if (spec is List<object?> parts)
            {
                if (output != "json" || parts.Count == 0 || !parts.All(x => x is string s && s.Length > 0 && !s.StartsWith("regex:", StringComparison.Ordinal)))
                    problems.Add($"{cid}: adapter.result.{field} as a list must hold dotted paths "
                                 + "into a json output, summed");
            }
            else if (spec is not string text || text.Length == 0)
                problems.Add($"{cid}: adapter.result.{field} must be a dotted path, a list of "
                             + "dotted paths, regex:<pattern>, or null");
            else if (text.StartsWith("regex:", StringComparison.Ordinal))
            {
                if (output != "text")
                    problems.Add($"{cid}: adapter.result.{field} is a regex but the "
                                 + $"output is {output}, not text");
                else if (!text.Contains("(?P<n>"))
                    problems.Add($"{cid}: adapter.result.{field} regex has no named "
                                 + "group `n` to read the count out of");
                else
                {
                    // The reason is the regex engine's own text, so it differs from Python's after "compile: ".
                    try { PyRegex.Compile(text["regex:".Length..]); }
                    catch (Exception err) when (err is ArgumentException or NotSupportedException)
                    {
                        problems.Add($"{cid}: adapter.result.{field} regex does not "
                                     + $"compile: {err.Message}");
                    }
                }
            }
            else if (output != "json")
                problems.Add($"{cid}: adapter.result.{field} is a dotted path but the "
                             + $"output is {output}; a text output needs regex:<pattern>");
        }
    }

    // Problems with one trigger expression, recursively.
    static List<string> ValidateTrigger(object? trigger, string path)
    {
        if (trigger is not PyDict t) return [$"{path} must be an object"];
        if (t.Count != 1)
            return [$"{path} must carry exactly one operator; got {PyText.Repr(PyOrder.SortedByCodePoint(t.Keys).Cast<object?>().ToList())}"];
        string key = t.Keys[0];
        object? value = t[key];
        if (!TriggerKeys.Contains(key)) return [$"{path}: unknown trigger operator {PyText.Repr(key)}"];
        var problems = new List<string>();
        if (key is "anyOf" or "allOf")
        {
            if (value is not List<object?> subs || subs.Count == 0) return [$"{path}.{key} must be a non-empty list of triggers"];
            for (int i = 0; i < subs.Count; i++) problems.AddRange(ValidateTrigger(subs[i], $"{path}.{key}[{i}]"));
        }
        else if (key == "anyType")
        {
            if (value is not List<object?> types || types.Count == 0 || !types.All(x => x is string))
                problems.Add($"{path}.anyType must be a non-empty list of record type names");
        }
        else if (value is not true)
            problems.Add($"{path}.{key} must be true (the vocabulary has no negation)");
        return problems;
    }

    // Prerequisite cycles, named by the ids that form them.
    static List<string> Cycles(List<object?> checks, HashSet<string> ids)
    {
        var graph = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var check in checks.OfType<PyDict>())
            if (check.Get("id") is string id)
                graph[id] = (PyValue.Truthy(check.Get("prerequisites")) ? PyValue.Iter(check.Get("prerequisites")) : [])
                    .Where(p => PyValue.InSet(p, ids)).Cast<string>().ToList();
        var problems = new HashSet<string>(StringComparer.Ordinal);
        var state = new Dictionary<string, string>(StringComparer.Ordinal);

        void Walk(string node, List<string> stack)
        {
            string? seen = state.GetValueOrDefault(node);
            if (seen == "done") return;
            if (seen == "open")
            {
                problems.Add("prerequisite cycle: " + string.Join(" -> ", stack.Skip(stack.IndexOf(node)).Append(node)));
                return;
            }
            state[node] = "open";
            foreach (string next in graph.GetValueOrDefault(node) ?? []) Walk(next, [.. stack, node]);
            state[node] = "done";
        }

        foreach (string node in PyOrder.SortedByCodePoint(graph.Keys)) Walk(node, []);
        return PyOrder.SortedByCodePoint(problems);
    }

    // ---- applicability

    /// <summary>Per check id: {applicability: yes | no | unknown, evidence: one sentence}. A null scope is "not measured"; so is a
    /// null mesh reach (scope/mesh-reach.json, overlapprobe --mesh-reach), which only the placedMeshes trigger reads.</summary>
    public static PyDict Applicability(PyDict registry, PyDict? scope, PyDict? meshReach = null)
    {
        var result = new PyDict();
        foreach (object? row in PyValue.Truthy(registry.Get("checks")) ? PyValue.Iter(registry.Get("checks")) : [])
        {
            var check = PyValue.Dict(row);
            string cid = PyText.Str(check.Get("id"));
            var applic = check.Get("applicability") as PyDict ?? [];
            object? trigger = applic.Get("trigger");

            // A declared dependence on an unmeasured extent wins over the trigger.
            var hit = UnmeasuredHit(scope, PyValue.Truthy(applic.Get("dependsOnExtents")) ? PyValue.Iter(applic.Get("dependsOnExtents")) : []);
            if (hit is { } h)
            {
                result[cid] = new PyDict { ["applicability"] = "unknown", ["evidence"] = $"the scope lists {h.extent} under unmeasured: \"{h.why}\"." };
                continue;
            }
            if (trigger == null)
            {
                result[cid] = new PyDict
                {
                    ["applicability"] = "unknown",
                    ["evidence"] = "the registry gives this check no trigger, so its applicability is not derived.",
                };
                continue;
            }
            var (verdict, evidence) = Evaluate(trigger, scope, meshReach);
            result[cid] = new PyDict { ["applicability"] = verdict, ["evidence"] = evidence };
        }
        return result;
    }

    // The first declared extent the scope lists under unmeasured, with its sentence.
    static (string extent, string why)? UnmeasuredHit(PyDict? scope, IEnumerable<object?> depends)
    {
        var wanted = depends.ToList();
        if (wanted.Count == 0) return null;
        var listed = new Dictionary<string, string>(StringComparer.Ordinal);
        object? unmeasured = scope?.Get("unmeasured");
        foreach (object? entry in PyValue.Truthy(unmeasured) ? PyValue.Iter(unmeasured) : [])
            if (entry is PyDict e && PyValue.Truthy(e.Get("extent")))
                listed[PyValue.Hash(e["extent"])] = PyText.Str(PyValue.Truthy(e.Get("why")) ? e["why"] : "");
        foreach (object? extent in wanted)
            if (listed.TryGetValue(PyValue.Hash(extent), out string? why)) return (PyText.Str(extent), why);
        return null;
    }

    static (string verdict, string evidence) Evaluate(object? trigger, PyDict? scope, PyDict? reach)
    {
        var t = PyValue.Dict(trigger);
        if (t.Count != 1) throw new PyError(t.Count > 1 ? "too many values to unpack (expected 1)" : "not enough values to unpack (expected 1, got 0)");
        string key = t.Keys[0];
        object? value = t[key];

        if (key == "always") return ("yes", "this check applies to every evaluation.");
        if (key == "anyOf") return Combine(value, scope, reach, anyMode: true);
        if (key == "allOf") return Combine(value, scope, reach, anyMode: false);
        if (key == "placedMeshes") return PlacedMeshes(reach);

        if (scope == null) return ("unknown", "no scope.json was measured, so this trigger could not be read.");
        if (scope.Get("complete") is not true)
        {
            object? failures = scope.Get("walkFailures");
            string detail = PyValue.Truthy(failures) ? $" ({PyValue.Len(failures)} target(s) not walked)" : "";
            return ("unknown", $"the scope is incomplete{detail}, so this trigger could not be read.");
        }

        var totals = new Totals(scope);
        switch (key)
        {
            case "anyType":
                var wanted = PyValue.Iter(value).Select(PyText.Str).ToList();
                var present = wanted.Where(x => totals.Types.TryGetValue(x, out object? n) && PyValue.AboveZero(n)).ToList();
                if (present.Count > 0)
                    return ("yes", "the target carries " + string.Join(", ", PyOrder.SortedByCodePoint(present).Select(x => $"{x} x{PyText.Str(totals.Types[x])}")) + ".");
                return ("no", "none of " + string.Join(", ", PyOrder.SortedByCodePoint(wanted))
                              + $" is present among the {totals.Types.Count} record types measured.");
            case "placed":
                return totals.Placed > 0 ? ("yes", $"{totals.Placed} placed record(s) filed under cells.")
                    : ("no", "placed: 0 records filed under any cell.");
            case "exterior":
                return totals.Exterior > 0
                    ? ("yes", $"placed.exterior is {totals.Exterior}: the target files records in exterior cells "
                              + $"({totals.ExteriorCells} grid cell(s) listed).")
                    : ("no", "placed.exterior is 0: the target files nothing in an exterior cell.");
            case "land":
                return totals.Land > 0 ? ("yes", $"{totals.Land} LAND record(s).") : ("no", "land: 0 LAND records.");
            case "navmesh":
                return totals.Navmesh > 0 ? ("yes", $"{totals.Navmesh} NAVM record(s).") : ("no", "navmesh: 0 NAVM records.");
            case "overlays":
                return totals.Overlays > 0 ? ("yes", $"the configuration lists {totals.Overlays} overlay file(s).")
                    : ("no", "the configuration lists no overlay (BOS ini) files.");
            case "nexusId":
                return totals.NexusIds.Count > 0
                    ? ("yes", "the provider's meta.ini names Nexus mod id(s) " + string.Join(", ", totals.NexusIds.Select(PyText.Str)) + ".")
                    : ("no", "no target resolves to a mod folder with a Nexus id in its meta.ini.");
            case "masters":
                return totals.Masters > 0 ? ("yes", $"the target(s) declare {totals.Masters} master(s).")
                    : ("no", "the target(s) declare no masters.");
        }
        return ("unknown", $"unknown trigger operator {PyText.Repr(key)}.");
    }

    // anyOf is yes on the first yes; allOf is no on the first no; an unknown arm is never resolved by its neighbours.
    static (string verdict, string evidence) Combine(object? subs, PyDict? scope, PyDict? reach, bool anyMode)
    {
        var results = PyValue.Iter(subs).Select(sub => Evaluate(sub, scope, reach)).ToList();
        var (win, lose) = anyMode ? ("yes", "no") : ("no", "yes");
        foreach (var r in results)
            if (r.verdict == win) return (win, r.evidence);
        foreach (var r in results)
            if (r.verdict == "unknown") return r;
        return (lose, string.Join("; ", results.Select(r => r.evidence)));
    }

    /// <summary>The placedMeshes trigger: does a selected folder provide a mesh that wins for a placed reference and differs from
    /// the one it replaces. Read from the mesh reach only; a found yes stands even when the reach is incomplete, a no needs it complete.</summary>
    static (string verdict, string evidence) PlacedMeshes(PyDict? reach)
    {
        const string what = "whether a selected folder's mesh is used by a placed object";
        if (reach == null) return ("unknown", $"no mesh reach was measured, so {what} is not known.");
        var folders = PyValue.Truthy(reach.Get("folders")) ? PyValue.Iter(reach.Get("folders")).Select(PyValue.Dict).ToList() : [];
        BigInteger credited = PyValue.Truthy(reach.Get("credited")) ? PyValue.Int(reach["credited"]) : 0;
        string names = string.Join(", ", folders.Select(f => PyText.Str(f.Get("name"))));
        if (credited > 0)
            return ("yes", $"{credited} placed reference(s) use a mesh {names} provide(s) that wins and differs from the one it replaces.");
        if (reach.Get("complete") is not true)
        {
            var why = PyValue.Truthy(reach.Get("why")) ? string.Join("; ", PyValue.Iter(reach.Get("why")).Select(PyText.Str)) : "no reason given";
            return ("unknown", $"the mesh reach is incomplete ({why}), so {what} is not known.");
        }
        if (folders.Count == 0) return ("no", "the arms differ by no mod folder, so no mesh is credited to one.");
        BigInteger Sum(string key) => folders.Aggregate(BigInteger.Zero, (n, f) => n + (PyValue.Truthy(f.Get(key)) ? PyValue.Int(f[key]) : 0));
        BigInteger refs = Sum("references"), equal = Sum("equalBytes"), fresh = Sum("noFallback"), notLoaded = Sum("notLoaded");
        var stronger = new SortedDictionary<string, BigInteger>(StringComparer.Ordinal);
        foreach (var f in folders)
            foreach (var (k, v) in PyValue.Truthy(f.Get("overriddenBy")) ? PyValue.Dict(f["overriddenBy"]) : [])
                stronger[k] = stronger.GetValueOrDefault(k) + PyValue.Int(v);
        BigInteger over = stronger.Values.Aggregate(BigInteger.Zero, (a, b) => a + b);
        var parts = new List<string>();
        if (equal > 0) parts.Add($"{equal} equal to the copy they replace");
        if (over > 0) parts.Add($"{over} overridden by {string.Join(", ", stronger.Keys)}");
        if (fresh > 0) parts.Add($"{fresh} new with nothing below");
        if (notLoaded > 0) parts.Add($"{notLoaded} in an archive the game does not load");
        return ("no", $"no placed reference uses a mesh {names} provide(s) that wins and differs: "
                      + (refs == 0 ? "no placed reference uses any of its meshes." : $"{refs} use one of its meshes ({string.Join(", ", parts)})."));
    }

    // The scope counters the triggers read, summed over every target.
    sealed class Totals
    {
        public readonly Dictionary<string, object> Types = new(StringComparer.Ordinal);
        public readonly List<object?> NexusIds = [];
        public BigInteger Placed, Exterior, Land, Navmesh;
        public int ExteriorCells, Masters, Overlays;

        public Totals(PyDict scope)
        {
            foreach (object? target in OrEmpty(scope.Get("targets")))
            {
                if ((target as PyDict)?.Get("footprint") is not PyDict footprint) continue;
                object? recordTypes = footprint.Get("recordTypes");
                if (PyValue.Truthy(recordTypes))
                    foreach (var (name, counts) in recordTypes as PyDict ?? throw new PyError($"'{PyValue.TypeName(recordTypes)}' object has no attribute 'items'"))
                    {
                        object total = counts is PyDict c
                            ? PyValue.Add(c.ContainsKey("new") ? c["new"] : 0L, c.ContainsKey("override") ? c["override"] : 0L)
                            : PyValue.IsIntOrBool(counts) ? counts! : 0L;
                        Types[name] = PyValue.Add(Types.GetValueOrDefault(name, 0L), total);
                    }
                var placed = PyValue.Truthy(footprint.Get("placed")) ? PyValue.Dict(footprint.Get("placed")) : [];
                Placed += Count(placed, "total");
                Exterior += Count(placed, "exterior");
                Land += Count(placed, "land");
                Navmesh += Count(placed, "navm");
                ExteriorCells += Length(placed.Get("exteriorCells"));
                Masters += Length(footprint.Get("masters"));
                if (PyValue.Truthy(footprint.Get("nexusId"))) NexusIds.Add(footprint["nexusId"]);
            }
            var configuration = PyValue.Truthy(scope.Get("configuration")) ? PyValue.Dict(scope.Get("configuration")) : [];
            Overlays = Length(configuration.Get("overlays"));
        }

        static IEnumerable<object?> OrEmpty(object? v) => PyValue.Truthy(v) ? PyValue.Iter(v) : [];
        static BigInteger Count(PyDict block, string key) => PyValue.Truthy(block.Get(key)) ? PyValue.Int(block[key]) : 0;
        static int Length(object? v) => PyValue.Truthy(v) ? PyValue.Len(v) : 0;
    }
}
