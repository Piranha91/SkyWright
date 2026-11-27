namespace RunControl;

// Step 3 of the SkyWright / houseCARL boundary: the app's check registry holds nothing only an LLM fills. These cases
// read the registry in this checkout (data/check-registry.json) and the real scopes of plan-modules.json; worked by hand.
public static partial class SelfTest
{
    /// <summary>The rows an AI-side evaluation fills and the kinds only they use (known-answers/boundary.json, as Gate B
    /// settled them; one kind is a program of the houseCARL integration module, which SkyWright's code does not name);
    /// none may be in the app's registry.</summary>
    static readonly PyDict BoundaryLists = LoadAnswers("boundary.json");
    static readonly string[] AiSideKinds = L(BoundaryLists["aiSideKinds"]).Select(S).ToArray();
    static readonly string[] AiSideRows = L(BoundaryLists["aiSideRows"]).Select(S).ToArray();

    static PyDict LoadAnswers(string name)
    {
        using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return D(PyJson.Loads(reader.ReadToEnd()));
    }

    static void BoundaryCases(Action<string, string> skip)
    {
        string? root = HarnessShared.CheckoutLayout.Locate(null, out _)?.Checkout;
        string registryPath = root == null ? "" : HarnessShared.CheckoutLayout.Registry(root);
        const string t1 = "boundary: the app's registry holds no row only an LLM fills and no skill key";
        const string t2 = "boundary: on the real registry, what keeps an app scan of each recorded real scope from completing is no AI-side row";
        if (!File.Exists(registryPath))
        {
            skip(t1, "this build is not inside a SkyWright checkout");
            skip(t2, "this build is not inside a SkyWright checkout");
        }
        else
        {
            var registry = Registry.Load(registryPath);
            // verify-bundle refuses a check or credited receipt of a foreign kind in a plan of the registry this SkyWright
            // carries: its version constant must be the registry's own, or that rule would test a version no plan records
            Case("boundary: verify-bundle's current registry version is the app registry's own", () =>
                Ev.Eq(registry.Get("registryVersion"), (long)VerifyBundle.CurrentRegistryVersion) ? null
                    : $"data/check-registry.json records {Ev.Str(registry.Get("registryVersion"))}, verify-bundle's constant is {VerifyBundle.CurrentRegistryVersion}");
            Case(t1, () =>
            {
                var rows = PyValue.Iter(registry.Get("checks")).Select(D)
                    .Where(c => AiSideRows.Contains(S(c["id"])) || AiSideKinds.Contains(Ev.Str(Ev.G(c["adapter"], "kind")))).Select(c => S(c["id"])).ToList();
                var families = PyValue.Iter(registry.Get("families")).Select(f => Ev.Str(D(f)["id"])).ToList();
                var why = new List<string>();
                if (rows.Count > 0) why.Add("AI-side rows: " + string.Join(", ", rows));
                if (registry.ContainsKey("skill")) why.Add($"a skill key ({Ev.Str(registry["skill"])})");
                // 2026-11-27 (record checks unit 2): the records family is back, its two lost-edit checks run by recordprobe in C#
                // (no model, no houseCARL program); what stays AI-side is the rows list in known-answers/boundary.json
                return why.Count == 0 ? null : string.Join("; ", why);
            });

            // A required row that applies (yes or unknown) blocks completion for ever when the app never runs it: its
            // kind is unsupported or manual (no decision here), or its applicability is unknown. Which of those are AI-side?
            var scopes = L(LoadAnswers("plan-modules.json")["registry"] is PyDict r ? r["applicability"] : null).Select(D)
                .Where(x => S(x["name"]).StartsWith("real scope ", StringComparison.Ordinal) && x["scope"] != null).ToList();
            Case(t2, () =>
            {
                if (scopes.Count < 2) return $"{scopes.Count} real scope(s) in the answers";
                var bad = new List<string>();
                foreach (var row in scopes)
                {
                    var scope = D(PyJson.Loads(S(row["scope"])));
                    var applic = Registry.Applicability(registry, scope);
                    var blockers = new List<string>();
                    foreach (var c in PyValue.Iter(registry.Get("checks")).Select(D))
                    {
                        string id = S(c["id"]), kind = Ev.Str(Ev.G(c["adapter"], "kind"));
                        string a = Ev.Str(Ev.G(applic[id], "applicability"));
                        bool required = c["required"] is true;
                        if (!required || a == "no") continue;
                        if (a == "unknown" || kind is "unsupported" or "manual" || AiSideKinds.Contains(kind)) blockers.Add($"{id} ({(a == "unknown" ? "unknown" : kind)})");
                    }
                    Console.WriteLine($"INFO {S(row["name"])}: an app scan cannot complete while these required rows apply: {string.Join(", ", blockers)}");
                    var ai = blockers.Where(b => AiSideRows.Any(x => b.StartsWith(x + " ", StringComparison.Ordinal))).ToList();
                    if (ai.Count > 0) bad.Add($"{S(row["name"])}: {string.Join(", ", ai)}");
                }
                return bad.Count == 0 ? null : string.Join("; ", bad.Take(3));
            });
        }

        // the validator refuses each AI-side kind by name, on an otherwise valid registry (the recorded one's first row)
        Case("boundary: the registry validator refuses each AI-side adapter kind by name", () =>
        {
            var recorded = D(PyJson.Loads(System.Text.Encoding.UTF8.GetString(B64(D(LoadAnswers("plan-modules.json")["registry"])["bytes"]))));
            var why = new List<string>();
            foreach (string kind in AiSideKinds)
            {
                var reg = D(Py.Copy(recorded));
                var first = D(L(reg["checks"])[0]);
                D(first["adapter"])["kind"] = kind;
                var said = Registry.Validate(reg);
                if (!said.Any(p => p.Contains($"unknown adapter kind '{kind}'", StringComparison.Ordinal))) why.Add($"{kind}: {(said.Count == 0 ? "accepted" : said[0])}");
            }
            return why.Count == 0 ? null : string.Join("; ", why);
        });

        // another program's kinds on which a decision never completes only add to runcontrol's: no list a caller passes
        // lets a decision on an unsupported check complete, and runcontrol's own bundle command takes no such list, nor
        // a folder to link
        Case("boundary: a caller's never-completing kinds and linked folders cannot loosen runcontrol's own rules", () =>
        {
            using var t = new Temp();
            var plan = D(PyJson.Loads("{\"checks\": [{\"checkId\": \"u.check\", \"manualDecisionAllowed\": true, \"adapter\": {\"kind\": \"unsupported\"}}, "
                + "{\"checkId\": \"o.check\", \"manualDecisionAllowed\": true, \"adapter\": {\"kind\": \"other\"}}]}"));
            void Write(string rel, string text)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(t.At(rel))!);
                File.WriteAllBytes(t.At(rel), PyJson.Utf8(text));
            }
            Write("decisions/evidence/note.txt", "seen");
            foreach (string id in new[] { "u.check", "o.check" })
                Write($"decisions/{id}.json", ("{\"checkId\": \"" + id + "\", \"armId\": \"baseline\", \"decidedBy\": \"Reviewer\", "
                    + "\"decision\": \"completed-no-finding\", \"evidence\": [\"decisions/evidence/note.txt\"]}"));
            string? Completes(IReadOnlyCollection<string>? never) => string.Join(" ", Decisions.Load(t.Root, plan, never).Select(D)
                .Select(r => $"{S(r["checkId"])}={r["satisfiesCompletion"]}"));
            var why = Same("runcontrol's own", Completes(null), "o.check=True u.check=False")
                ?? Same("an empty list", Completes([]), "o.check=True u.check=False")
                ?? Same("a caller's list adds its kind", Completes(["other"]), "o.check=False u.check=False");
            if (why != null) return why;
            foreach (string flag in new[] { "--linked-folder", "--never-completing" })
            {
                var before = Console.Error;
                var sink = new StringWriter();
                Console.SetError(sink);
                int code;
                try { code = EvalCommands.Run(["bundle", "--run", t.Root, flag, "imports"]); }
                catch (Refusal r) { sink.Write(r.Message); code = Exit.Refused; }
                finally { Console.SetError(before); }
                string err = sink.ToString();
                if (code == Exit.Ok || !err.Contains($"bundle does not take {flag}.", StringComparison.Ordinal))
                    return $"runcontrol bundle with {flag}: exit {code}, {err.Trim()}";
            }
            return new BundleArgs() is { LinkedFolders.Count: 0, NeverCompletingKinds.Count: 0 } ? null : "a bundle's arguments start with a caller's list";
        });

        // a caller's catalogue-like result kinds are its own: runcontrol's validator and executor start with none, and
        // the app registry refuses the AI-side result kind by name
        Case("boundary: a caller's catalogue-like result kinds never reach runcontrol's own validator or registry", () =>
        {
            var recorded = D(PyJson.Loads(System.Text.Encoding.UTF8.GetString(B64(D(LoadAnswers("plan-modules.json")["registry"])["bytes"]))));
            var row = L(recorded["checks"]).Select(D).FirstOrDefault(c => Ev.OrEmpty(c.Get("adapter")).Get("result") is PyDict);
            if (row == null) return "the recorded registry has no row with a result to give the kind";
            D(D(row["adapter"])["result"])["kind"] = "discovery";
            var said = Registry.Validate(recorded);
            if (!said.Any(p => p.Contains("unknown result kind 'discovery'", StringComparison.Ordinal)))
                return $"the app registry took the result kind discovery: {(said.Count == 0 ? "accepted" : said[0])}";
            return new Validator(".", new PyDict()).MoreCatalogueKinds.Count == 0 ? null : "runcontrol's validator starts with a caller's kinds";
        });
    }
}
