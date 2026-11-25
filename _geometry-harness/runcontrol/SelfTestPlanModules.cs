using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace RunControl;

/// <summary>The known answers for the modules plan and run need: the registry, the saved settings, the generated
/// states, the live check's population, the pattern rewrite and the Nexus id. Every expected value was computed by
/// the Python modules (known-answers/make_known_answers_plan.py) and this program must reach it exactly.</summary>
public static class SelfTestPlanModules
{
    static Action<string, Func<string?>> Case = (_, _) => { };

    static PyDict D(object? v) => (PyDict)v!;
    static List<object?> L(object? v) => (List<object?>)v!;
    static string S(object? v) => (string)v!;
    static List<string> Strings(object? v) => L(v).Select(S).ToList();
    static byte[] B64(object? v) => Convert.FromBase64String(S(v));
    static string Show(string? s) => s == null ? "null" : PyText.Repr(s.Length > 200 ? s[..200] + "..." : s);

    static string? Same(string what, string? got, string? want)
    {
        if (got == want) return null;
        int at = 0;
        while (got != null && want != null && at < got.Length && at < want.Length && got[at] == want[at]) at++;
        string Around(string? s) => s == null ? "null" : Show(s[Math.Max(0, Math.Min(at, s.Length) - 40)..Math.Min(s.Length, at + 80)]);
        return $"{what}: differs at {at}: got {Around(got)}, Python has {Around(want)}";
    }

    static string? SameList(string what, List<string> got, List<string> want)
    {
        for (int i = 0; i < Math.Max(got.Count, want.Count); i++)
            if (i >= got.Count || i >= want.Count || got[i] != want[i])
                return $"{what}: item {i} of {got.Count} (Python has {want.Count}): got {Show(i < got.Count ? got[i] : null)}, Python has {Show(i < want.Count ? want[i] : null)}";
        return null;
    }

    // The first few rows of a table that do not hold, as one sentence; null when all hold.
    static string? Rows(IEnumerable<PyDict> rows, Func<PyDict, string?> check)
    {
        var bad = new List<string>();
        int n = 0;
        foreach (var row in rows)
        {
            n++;
            string? why;
            try { why = check(row); }
            catch (Exception ex) { why = $"{ex.GetType().Name}: {ex.Message}"; }
            if (why != null) bad.Add($"row {n} ({(row.ContainsKey("name") ? row["name"] : row.Get("pattern"))}): {why}");
        }
        return n == 0 ? "the table is empty" : bad.Count == 0 ? null : $"{bad.Count} of {n} rows differ; " + string.Join(" | ", bad.Take(3));
    }

    // A temporary folder; the answers name it by a placeholder.
    sealed class Temp : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hc-runcontrol-plan-" + Guid.NewGuid().ToString("N")[..12]);
        public Temp() => Directory.CreateDirectory(Root);
        public string Unmask(string s) => s.Replace("<ROOT\\>", Root).Replace("<ROOT/>", PyOrder.Fwd(Root));
        public string Mask(string s) => s.Replace(Root, "<ROOT\\>").Replace(PyOrder.Fwd(Root), "<ROOT/>");
        public object? Mask(object? v) => v switch
        {
            string s => Mask(s),
            PyDict d => new PyDict(d.Select(kv => new KeyValuePair<string, object?>(Mask(kv.Key), Mask(kv.Value)))),
            List<object?> l => l.Select(Mask).ToList(),
            _ => v,
        };
        public string Write(string rel, byte[] data)
        {
            string p = Path.Combine(Root, rel.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, data);
            return p;
        }
        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // A copy of a document with the answers' edits applied: [path, "set", value], [path, "del"] or [path, "append", value].
    static object? Copy(object? v) => v switch
    {
        PyDict d => new PyDict(d.Select(kv => new KeyValuePair<string, object?>(kv.Key, Copy(kv.Value)))),
        List<object?> l => l.Select(Copy).ToList(),
        _ => v,
    };

    static PyDict Apply(PyDict doc, object? ops)
    {
        var copy = D(Copy(doc));
        foreach (var op in L(ops).Select(L))
        {
            var path = L(op[0]);
            object? at = copy;
            foreach (object? step in path.Take(path.Count - 1)) at = step is string k ? D(at)[k] : L(at)[(int)(long)step!];
            object? last = path[^1];
            switch (S(op[1]))
            {
                case "set":
                    if (last is string key) D(at)[key] = Copy(op[2]);
                    else L(at)[(int)(long)last!] = Copy(op[2]);
                    break;
                case "del": D(at).Pop(S(last)); break;
                default: L(D(at)[S(last)]).Add(Copy(op[2])); break;
            }
        }
        return copy;
    }

    public static void Run(Action<string, Func<string?>> @case, Action<string, string> skip)
    {
        Case = @case;
        PyDict ka;
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("plan-modules.json")!)
        using (var r = new StreamReader(s, Encoding.UTF8))
            ka = D(PyJson.Loads(r.ReadToEnd()));

        ValueCases(D(ka["values"]));
        RegistryCases(D(ka["registry"]));
        PatternCases(L(ka["patterns"]).Select(D).ToList());
        StateCases(D(ka["states"]));
        PopulationCases(L(ka["populations"]).Select(D).ToList());
        MetaCases(L(ka["metaIni"]).Select(D).ToList());
        LiveCases(skip);
    }

    // ---- Python's value rules

    static void ValueCases(PyDict ka)
    {
        var rows = L(ka["values"]).Select(D).ToList();
        var values = rows.Select(r => PyJson.Loads(S(r["v"]))).ToList();
        static string? Tried(Func<string?> f)
        {
            try { return f(); }
            catch (PyError) { return null; }
        }
        Case($"values: truth, type, len, int, iteration and hashing of {rows.Count} values", () => Rows(rows, row =>
        {
            object? v = PyJson.Loads(S(row["v"]));
            return Same("truth", PyValue.Truthy(v).ToString(), ((bool)row["truthy"]!).ToString())
                   ?? Same("type", PyValue.TypeName(v), S(row["type"]))
                   ?? Same("len", Tried(() => PyValue.Len(v).ToString()), (row["len"] as long?)?.ToString())
                   ?? Same("int", Tried(() => PyValue.Int(v).ToString()), row["int"] as string)
                   ?? Same("iter", Tried(() => PyJson.Canonical(PyValue.Iter(v).ToList())), row["iter"] as string)
                   ?? Same("hashable", (Tried(() => PyValue.Hash(v)) != null).ToString(), ((bool)row["hashable"]!).ToString());
        }));
        Case("values: equality, and one hash for equal values", () => Rows(rows, row =>
        {
            object? v = PyJson.Loads(S(row["v"]));
            string eq = string.Concat(values.Select(w => PyValue.Eq(v, w) ? "1" : "0"));
            if (eq != S(row["eq"])) return $"== gives {eq}, Python gives {row["eq"]}";
            if (!(bool)row["hashable"]!) return null;
            string hash = string.Concat(values.Select(w => Tried(() => PyValue.Hash(w)) == PyValue.Hash(v) ? "1" : "0"));
            return hash == eq ? null : $"hashes are shared as {hash}, equality is {eq}";
        }));
        Case("values: int() of text, strip, isdigit and +", () =>
            Rows(L(ka["intText"]).Select(D), r => Same("int " + Show(S(r["text"])), PyValue.IntText(S(r["text"]))?.ToString(), r["int"] as string))
            ?? Rows(L(ka["strip"]).Select(D), r => Same("strip " + Show(S(r["text"])), PyValue.Strip(S(r["text"])), S(r["out"])))
            ?? Rows(L(ka["isdigit"]).Select(D), r => Same("isdigit " + Show(S(r["text"])), PyValue.IsDigit(S(r["text"])).ToString(), ((bool)r["out"]!).ToString()))
            ?? Rows(L(ka["add"]).Select(D), r => Same($"{r["a"]} + {r["b"]}",
                Tried(() => PyJson.Canonical(PyValue.Add(PyJson.Loads(S(r["a"])), PyJson.Loads(S(r["b"]))))), r["sum"] as string)));
    }

    // ---- the registry

    static string Refused(Action act)
    {
        try { act(); }
        catch (Refusal r) { return r.Message; }
        return "nothing was refused";
    }

    static void RegistryCases(PyDict ka)
    {
        using var t = new Temp();
        string path = t.Write("check-registry.json", B64(ka["bytes"]));
        var registry = Registry.Load(path);
        // the registry as recorded before the AI-side rows left it: the base of the variants whose subject is one of
        // those rows, until that row's rule moves to the houseCARL integration module (step 3 of the boundary)
        var withAiRows = Registry.Load(t.Write("check-registry-with-ai-rows.json", B64(ka["oldBytes"])));
        PyDict BaseOf(PyDict row) => row.Get("base") as string == "with-ai-rows" ? withAiRows : registry;

        Case("registry: sha256 of the file's bytes", () => Same("sha256", Registry.Sha256(path), S(ka["sha256"])));
        Case("registry: the real registry validates", () => SameList("problems", Registry.Validate(registry), Strings(ka["problems"])));
        Case("registry: families", () => Same("families", PyJson.Canonical(Registry.Families(registry)), S(ka["families"])));
        Case("registry: check by id", () =>
            Registry.CheckById(registry, "geometry.route-cut")?.Get("family") as string == "geometry" && Registry.CheckById(registry, "no.such") == null
                ? null : "a check was not found by its id, or one was invented");
        Case("registry: anything but an object is refused", () => Rows(L(ka["notObjects"]).Select(D), row =>
        {
            string p = t.Write("not-object.json", Encoding.UTF8.GetBytes(S(row["text"])));
            return Same(S(row["text"]), Refused(() => Registry.Load(p)).Replace(p, "<FILE>"), S(row["message"]));
        }));

        var variants = L(ka["variants"]).Select(D).ToList();
        Case($"registry: validate names every problem of {variants.Count(v => v.ContainsKey("problems"))} broken registries", () =>
            Rows(variants.Where(v => v.ContainsKey("problems")), row =>
                SameList("problems", Registry.Validate(Apply(BaseOf(row), row["ops"])), Strings(row["problems"]))));
        Case($"registry: validate stops where Python stops, on {variants.Count(v => v.ContainsKey("raises"))} malformed registries", () =>
            Rows(variants.Where(v => v.ContainsKey("raises")), row =>
            {
                try { Registry.Validate(Apply(BaseOf(row), row["ops"])); }
                catch (PyError e) { return Same("error", e.Message, S(row["raises"])); }
                return "nothing was raised";
            }));
        Case("registry: a pattern that does not compile is one problem", () =>
        {
            var bad = D(ka["badRegex"]);
            var said = Registry.Validate(Apply(registry, bad["ops"]));
            if (said.Count != (long)bad["count"]!) return $"{said.Count} problems, Python has {bad["count"]}";
            return said[0].StartsWith(S(bad["prefix"]), StringComparison.Ordinal) && said[0].Length > S(bad["prefix"]).Length
                ? null : $"got {Show(said[0])}, Python starts {Show(S(bad["prefix"]))}";
        });

        var scopes = L(ka["applicability"]).Select(D).ToList();
        Case($"registry: applicability of every check on {scopes.Count} scopes", () => Rows(scopes, row =>
        {
            var scope = row["scope"] == null ? null : D(PyJson.Loads(S(row["scope"])));
            return Same("applicability", PyJson.Canonical(Registry.Applicability(Apply(registry, row["ops"]), scope)), S(row["expected"]));
        }));
        Case("registry: applicability covers yes, no and unknown, and the real scopes", () =>
        {
            string all = string.Concat(scopes.Select(r => S(r["expected"])));
            foreach (string verdict in new[] { "yes", "no", "unknown" })
                if (!all.Contains($"\"applicability\":\"{verdict}\"")) return $"no scope gives {verdict}";
            return scopes.Count(r => S(r["name"]).StartsWith("real scope ")) >= 2 ? null : "the real scope documents are missing from the answers";
        });
    }

    // ---- patterns

    // Where .NET cannot be made to agree with Python's re. Each row is checked, so the list stays true:
    // "refused" means the rewrite refuses the pattern; any other text is what .NET finds instead (null for no match).
    static readonly Dictionary<(string pattern, string? text), string?> PatternDifferences = new()
    {
        // (?i): Python pairs sigma with final sigma, s with long s, and i with dotless and dotted i; .NET's invariant rule does not.
        [(@"(?i)stra\u00dfe|\u03c3\u03c2", "\u03a3\u03a3")] = null,
        [(@"(?i)stra\u00dfe|\u03c3\u03c2", "\u03c3\u03c3")] = null,
        [(@"(?i)k|\u017f|i", "S")] = null,
        [(@"(?i)k|\u017f|i", "\u0130")] = null,
        [(@"(?i)k|\u017f|i", "\u0131")] = null,
        // A character outside the basic plane is two units to .NET: "." and a set match half of it, and counts differ.
        [(".", "\U0001F600")] = "\ud83d",
        [(".", "\n\U0001F600")] = "\ud83d",
        [("^.$", "\U0001F600")] = null,
        [("[^a]{2}", "\U0001F600")] = "\U0001F600",
        // Refused by the rewrite: .NET has no spelling that means the same.
        [(@"(a)(?P<n>b)(c)\3", null)] = "refused",
        [(@"(?a)\w+", null)] = "refused",
        [(@"\N{BULLET}", null)] = "refused",
        [(@"\U0001F600", null)] = "refused",
        [(@"[\W]+", null)] = "refused",
        [(@"[\S]+", null)] = "refused",
        // A possessive quantifier is an error to .NET.
        [("a*+a", null)] = "does not compile",
    };

    static int CodePoints(string s, int units) => PyValue.Len(s[..units]);

    static string? FoundText(Match m) => m.Success ? m.Value : null;

    static void PatternCases(List<PyDict> rows)
    {
        // What one row gives here: "refused", or the match as text the answers can be compared with.
        static string Describe(PyDict row, out string? found)
        {
            found = null;
            Regex rx;
            try { rx = PyRegex.Compile(S(row["pattern"]), (bool)row["multiline"]!); }
            catch (NotSupportedException) { return "refused"; }
            catch (ArgumentException) { return "does not compile"; }
            if (!row.ContainsKey("text")) return "compiles";
            string text = S(row["text"]);
            var m = rx.Match(text);
            found = FoundText(m);
            if (!m.Success) return "null";
            var named = new PyDict();
            foreach (string name in rx.GetGroupNames().Where(n => !int.TryParse(n, out _)))
                named[name] = m.Groups[name].Success ? m.Groups[name].Value : null;
            var got = new PyDict
            {
                ["start"] = (long)CodePoints(text, m.Index), ["end"] = (long)CodePoints(text, m.Index + m.Length), ["text"] = m.Value, ["named"] = named,
            };
            if (named.Count == 0) got["groups"] = m.Groups.Values.Skip(1).Select(g => g.Success ? (object?)g.Value : null).ToList();
            if (named.Get("n") is string n) got["count"] = PyValue.IntText(n.Replace(",", "")) is { } count ? PyValue.Whole(count) : null;
            return PyJson.Canonical(got);
        }

        static string Expected(PyDict row)
        {
            if (!(bool)row["compiles"]!) return "does not compile";
            if (row["match"] == null) return "null";
            var want = D(Copy(row["match"]));
            if (D(want["named"]).Count > 0) want.Pop("groups");
            return PyJson.Canonical(want);
        }

        var agree = rows.Where(r => !PatternDifferences.ContainsKey((S(r["pattern"]), null)) && !PatternDifferences.ContainsKey((S(r["pattern"]), r.Get("text") as string))).ToList();
        Case($"patterns: {agree.Count} searches agree with Python's re through the rewrite", () =>
            Rows(agree, row => Same($"on {Show(row.Get("text") as string)}", Describe(row, out _) is "refused" && !(bool)row["compiles"]! ? "does not compile" : Describe(row, out _), Expected(row))));
        Case("patterns: the registry's two specs read their counts", () =>
        {
            int read = rows.Count(r => S(r["pattern"]).Contains("(?P<n>[\\d,]+)") && r.Get("match") is PyDict m && m.Get("count") != null
                                       && Describe(r, out _) == Expected(r));
            return read >= 4 ? null : $"only {read} searches with the registry's specs read a count";
        });
        Case($"patterns: the {PatternDifferences.Count} listed differences from Python's re are still the differences", () =>
        {
            var bad = new List<string>();
            foreach (var ((pattern, text), dotnet) in PatternDifferences)
            {
                var hit = rows.Where(r => S(r["pattern"]) == pattern && (text == null || r.Get("text") as string == text)).ToList();
                if (hit.Count == 0) { bad.Add($"{Show(pattern)} on {Show(text)} is not in the answers"); continue; }
                foreach (var row in hit)
                {
                    string got = Describe(row, out string? found);
                    string seen = got is "refused" or "does not compile" or "compiles" ? got : found ?? "null";
                    if (got == Expected(row)) bad.Add($"{Show(pattern)} on {Show(text)} now agrees with Python");
                    else if (seen != (dotnet ?? "null")) bad.Add($"{Show(pattern)} on {Show(row.Get("text") as string)}: .NET gives {Show(seen)}, the list says {Show(dotnet)}");
                }
            }
            return bad.Count == 0 ? null : string.Join(" | ", bad.Take(4));
        });
        Case("patterns: the rewrite itself", () =>
        {
            (string python, string dotnet)[] pairs =
            [
                (@"(?P<n>[\d,]+) x", @"(?<n>[\d,]+) x"), (@"(?P<a>.)(?P=a)", @"(?<a>.)\k<a>"), (@"\(?P<n>\)", @"\(?P<n>\)"), (@"[(?P<n>]", @"[(?P<n>]"),
                (@"a\Z", @"a\z"), (@"x{,3}", @"x{0,3}"), (@"x{,}", @"x{,}"), (@"[a-[b]]", @"[a-\[b]]"), (@"[]a]", @"[\]a]"), (@"(?u)a", "a"), (@"(?iu:a)", "(?i:a)"),
                (@"\w\s", @"[\p{L}\p{N}_][\s\x1c-\x1f]"), (@"[\w\s]", @"[\p{L}\p{N}_\s\x1c-\x1f]"), (@"\\Z\\w", @"\\Z\\w"),
            ];
            foreach (var (python, dotnet) in pairs)
                if (PyRegex.ToDotNet(python) != dotnet) return $"{python} became {PyRegex.ToDotNet(python)}, not {dotnet}";
            return null;
        });
    }

    // ---- the generated states

    static void StateCases(PyDict ka)
    {
        string generator = S(ka["generatorSha256"]);
        var cases = L(ka["cases"]).Select(D).ToList();
        Case($"states: the document and the spec list of {cases.Count} censuses, byte for byte", () => Rows(cases, row =>
        {
            using var t = new Temp();
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(t.Write(S(row["path"]).Replace("sub/../", ""), B64(row["census"])))!, "sub"));
            var doc = StateGen.GenerateFile(t.Root + "\\" + S(row["path"]).Replace('/', '\\'), generator);
            return Same("document", StateGen.Dumps(D(t.Mask(doc))), S(row["document"])) ?? Same("specs", StateGen.SpecsText(doc), S(row["specs"]));
        }));
        Case("states: the generator's identity is the one the caller passes", () =>
        {
            var census = D(PyJson.Loads(Encoding.UTF8.GetString(B64(cases[1]["census"]))));
            string a = StateGen.Dumps(StateGen.Generate(census, new string('a', 64))), b = StateGen.Dumps(StateGen.Generate(census, new string('b', 64)));
            return a.Replace(new string('a', 64), new string('b', 64)) == b && a != b && a.Contains("\"sha256\": \"" + new string('a', 64) + "\"")
                ? null : "the generator field is not the only thing the identity changes";
        });
        Case("states: the cases reach every not-explored reason", () =>
        {
            string all = string.Concat(cases.Select(r => S(r["document"])));
            foreach (string code in new[] { "no-key", "chain-cycle", "chain-unresolved", "label-unknown", "label-ambiguous", "member-not-explorable",
                         "disabled-singletons", "player-parent", "unplaced-parent", "unreadable-plugins", "no-trigger-map", "combinations", "\"kind\": \"joint\",\n   \"switches\"", "\"covered\": [\n  {" })
                if (!all.Contains(code)) return $"no census reaches {code}";
            return null;
        });
    }

    // ---- the live check's population

    static void PopulationCases(List<PyDict> cases)
    {
        Case($"switch scope: the population and its sentence on {cases.Count} census and scope pairs", () => Rows(cases, row =>
        {
            var result = SwitchFlipScope.Population(D(PyJson.Loads(S(row["census"]))), D(PyJson.Loads(S(row["scope"]))));
            return Same("population", PyJson.Canonical(result), S(row["population"])) ?? Same("sentence", SwitchFlipScope.Sentence(result), S(row["sentence"]));
        }));
        Case("switch scope: the cases reach every cause and both ways of touching", () =>
        {
            string all = string.Concat(cases.Select(r => S(r["population"])));
            foreach (string code in new[] { "no-trigger-map", "unreadable-plugins", "scope-incomplete", "no-targets", "cells-unknown", "\"by\":\"plugin\"", "\"by\":\"cell\"",
                         "\"status\":\"not-applicable\"", "\"status\":\"open\"" })
                if (!all.Contains(code)) return $"no case reaches {code}";
            return null;
        });
    }

    // ---- a mod folder's Nexus id

    // Where the owner (Mo2ModMeta in housecarl-core) reads a meta.ini differently from modnames.meta_ini_mod_id.
    // The owner wins; each row is the owner's answer, and is checked, so the list stays true.
    static readonly Dictionary<string, long?> MetaDifferences = new()
    {
        // The owner reads the first modid line whatever section it is under; Python reads [General] only.
        ["no General section"] = 77,
        ["modid under another section"] = 42,
        ["modid before General"] = 11,
        ["a byte-order mark"] = 31,
        ["a section with trailing text"] = 47,
        // The owner reads the value as Qt wrote it: spaces round the "=", quotes, @ByteArray(), a sign.
        ["spaces around the equals sign"] = 35,
        ["a quoted value"] = 38,
        ["a ByteArray value"] = 46,
        ["a plus sign"] = 40,
        // The owner takes ASCII digits that fit 32 bits; Python takes any digits of any size.
        ["digits that are not ASCII"] = null,
        ["a value too large for 32 bits"] = null,
    };

    static void MetaCases(List<PyDict> cases)
    {
        long? Owner(PyDict row)
        {
            using var t = new Temp();
            if (row["bytes"] != null) t.Write("meta.ini", B64(row["bytes"]));
            return ModNexus.ModNexusId(t.Root);
        }
        static string Id(long? id) => id?.ToString() ?? "none";

        Case($"meta.ini: the owner reads the Nexus id as Python does on {cases.Count - MetaDifferences.Count} files", () =>
            Rows(cases.Where(c => !MetaDifferences.ContainsKey(S(c["name"]))), row =>
                Owner(row) == (long?)row["python"] ? null : $"the owner reads {Id(Owner(row))}, Python reads {Id((long?)row["python"])}"));
        Case($"meta.ini: the {MetaDifferences.Count} listed differences are still the differences, and the owner wins", () =>
            Rows(cases.Where(c => MetaDifferences.ContainsKey(S(c["name"]))), row =>
            {
                long? owner = Owner(row), listed = MetaDifferences[S(row["name"])];
                if (owner == (long?)row["python"]) return "the owner now agrees with Python";
                return owner == listed ? null : $"the owner reads {Id(owner)}, the list says {Id(listed)}";
            }));
    }

    // ---- against this checkout

    static void LiveCases(Action<string, string> skip)
    {
        // (the live comparison with Python's evalregistry went with that module in step 3: the registry's rules are this
        // program's own, and its recorded answers above are the known answers; the settings record went with the settings
        // reader in step 2, to the integration module, which owns the evaluation settings)
        const string name = "checkout: the registry in this checkout validates, and its patterns compile";
        string? root = HarnessShared.CheckoutLayout.Locate(null, out _)?.Checkout;
        string registryPath = root == null ? "" : HarnessShared.CheckoutLayout.Registry(root);
        if (!File.Exists(registryPath))
        {
            skip(name, "this build is not inside a SkyWright checkout");
            return;
        }
        var registry = Registry.Load(registryPath);
        Case(name, () => Registry.Validate(registry) is { Count: > 0 } said ? string.Join("; ", said.Take(3)) : null);
    }
}
