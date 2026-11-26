using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using HousecarlCore.Progress;
using Mutagen.Bethesda.Plugins;
using SkyWright.Engine;

// NEARPROBE — what stands near these placed references, for an LLM: SkyWright's own query (SkyWright.Engine.NearReferences.Of) over a load order, as JSON.
//
// The houseCARL integration module's answer to "what is near this reference" (Piranha, 2026-11-24: "an LLM asks
// SkyWright that question"; houseCARL's near= is gone). It reads the load order the way lookprobe does
// (_shared/HarnessOrderCli.cs), asks the engine, and prints the engine's own fields: per record, the hits (the seed, the
// plugin whose version reached it, the gap between the two extents, the rule); the engine's note; the plugins it could
// not read. It adds no distance rule: the radius is the engine's to validate and to judge complete
// (NearAnswer.WithinIsComplete), and nearprobe refuses when the engine says the answer would be short.
//
// usage: nearprobe --instance DIR --profile NAME --game DIR [--order FILE]
//                  --of XXXXXX:Plugin.esp [--of ...] | --of @<absolute path to a housecarl_records to_file= result>
//                  --within UNITS [--types SIGNATURE-OR-NAME ...]
//
// Exit 0: answered, every plugin read. 2: refused, one sentence on stderr, nothing on stdout. 3: answered, but a plugin
// could not be read: the JSON (complete: false, the plugins named) on stdout and one sentence on stderr.

return NearProbe.NearProbeMain.Run(args, Console.Out, Console.Error);

namespace NearProbe
{
    /// <summary>One question: the seeds, the radius, and the record types asked for (their names, and the getter types
    /// houseCARL's resolver gave for them).</summary>
    public sealed record NearRequest(IReadOnlyList<FormKey> Seeds, double Within, IReadOnlyList<string>? TypesAsked,
                                     IReadOnlyList<Type>? Types);

    /// <summary>What nearprobe answers: the JSON for stdout (empty on a refusal), the exit code, and the one sentence for
    /// stderr (null when complete).</summary>
    public sealed record NearOutcome(string Json, int Exit, string? Sentence);

    public static class NearProbeMain
    {
        public const int Answered = 0, Refused = 2, Incomplete = 3;

        const string Options = "--instance, --profile, --game, --order, --of, --within and --types";

        /// <summary>The program: the command line, the load order, the question, the answer.</summary>
        public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            using var __run = RunLog.BeginProcess("nearprobe", args);
            int Refuse(string why) { stderr.WriteLine(why); return Refused; }

            string? instance = null, profile = null, game = null, order = null, within = null;
            var of = new List<string>();
            var types = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                bool HasValue() => i + 1 < args.Length;
                if (a == "--instance" && HasValue()) instance = args[++i];
                else if (a == "--profile" && HasValue()) profile = args[++i];
                else if (a == "--game" && HasValue()) game = args[++i];
                else if (a == "--order" && HasValue()) order = args[++i];
                else if (a == "--of" && HasValue()) of.Add(args[++i]);
                else if (a == "--within" && HasValue()) within = args[++i];
                else if (a == "--types" && HasValue()) types.Add(args[++i]);
                else if (a is "--instance" or "--profile" or "--game" or "--order" or "--of" or "--within" or "--types")
                    return Refuse($"{a} needs a value after it: pass {a} and then its value.");
                else if (a.StartsWith("--", StringComparison.Ordinal))
                    return Refuse($"nearprobe has no option {a}; its options are {Options}.");
                else
                    return Refuse($"nearprobe does not take the argument '{a}'; every value follows its option ({Options}).");
            }
            if (of.Count == 0)
                return Refuse("nearprobe needs the references to look near: pass --of XXXXXX:Plugin.esp once per reference, or --of @<absolute path> to a housecarl_records to_file= result.");
            if (within is null)
                return Refuse("nearprobe needs --within, the distance in game units between the two extents (0 for touching).");
            if (!double.TryParse(within, NumberStyles.Float, CultureInfo.InvariantCulture, out var distance))
                return Refuse($"--within takes a distance in game units, such as 0 or 256; '{within}' is not one.");

            var (seeds, seedRefusal) = Seeds(of);
            if (seedRefusal is not null) return Refuse(seedRefusal);
            IReadOnlyList<Type>? getters = null;
            if (types.Count > 0)
            {
                var (resolved, typeRefusal) = ResolveTypes(types);
                if (typeRefusal is not null) return Refuse(typeRefusal);
                getters = resolved;
            }

            // the load order, read the way the module's other probes read it
            instance = HarnessTarget.Require(instance!, "MO2 instance directory", "instance", "--instance");
            game = HarnessTarget.Require(game!, "game directory", "game", "--game");
            profile = HarnessTarget.Require(profile!, "MO2 profile name", "profile", "--profile");
            string profileDir = Path.Combine(instance, "profiles", profile), modsDir = Path.Combine(instance, "mods");
            string overwrite = Path.Combine(instance, "overwrite"), dataDir = Path.Combine(game, "Data");
            var cfg = HarnessOrderCli.Resolve(profile, profileDir, modsDir, dataDir, overwrite, game, order, null, null);
            if (cfg.Refused) return Refuse(cfg.RefusedReason!);
            if (cfg.OrderedPaths.Count == 0)
                return Refuse($"the load order of profile '{profile}' lists no active plugin, so nothing near a reference can be read; check that profile's plugins.txt.");

            using var resolver = LoadOrderResolver.Build(cfg.OrderedPaths);
            var source = new Dictionary<string, object?>
            {
                ["instance"] = instance, ["profile"] = profile, ["configuration"] = cfg.Source, ["plugins"] = cfg.OrderedPaths.Count,
                ["limits"] = cfg.Limits, ["warnings"] = cfg.Warnings,
            };
            var outcome = Ask(resolver.Capture(), new NearRequest(seeds!, distance, types.Count > 0 ? types : null, getters), source,
                              resolver.ExcludedPlugins);
            if (outcome.Json.Length > 0) stdout.WriteLine(outcome.Json);
            if (outcome.Sentence is not null) stderr.WriteLine(outcome.Sentence);
            return outcome.Exit;
        }

        /// <summary>The seeds named with --of: plugin-form FormIDs one by one, or one houseCARL to_file= result read with
        /// houseCARL's own reader; or one sentence.</summary>
        static (List<FormKey>? Seeds, string? Refusal) Seeds(IReadOnlyList<string> of)
        {
            var tokens = new List<string>();
            if (of.Any(o => o.TrimStart().StartsWith('@')))
            {
                if (of.Count > 1)
                    return (null, "--of @<file> stands for the whole list of references, so it cannot be mixed with references named one by one; pass the file alone, or every reference with its own --of.");
                var path = of[0].Trim()[1..].Trim().Trim('"', '\'');
                if (!Path.IsPathFullyQualified(path))
                    return (null, $"the seeds file '{path}' is not an absolute path; give --of @ and the file's absolute path, as housecarl_records to_file= wrote it.");
                string content;
                try { content = File.ReadAllText(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { return (null, $"the seeds file '{path}' could not be read ({ex.GetType().Name}); check the path, or write the file again with housecarl_records to_file=."); }
                if (!ResultArtifact.LooksLikeArtifact(content))
                    return (null, $"'{path}' is not a housecarl_records to_file= result, so nearprobe does not read it; pass each reference with its own --of, or write the list with housecarl_records and to_file=.");
                var (manifest, read, err) = ResultArtifact.ReadIdentity(path, content);
                if (err is not null) return (null, err);
                if (!string.Equals(manifest!.Identity, "formid", StringComparison.OrdinalIgnoreCase))
                    return (null, $"the seeds file '{path}' lists '{manifest.Identity}' identities from {manifest.Tool}, not formids, so it names no reference; write the references with housecarl_records to_file=.");
                tokens.AddRange(read!);
            }
            else tokens.AddRange(of);

            var seeds = new List<FormKey>();
            foreach (var raw in tokens)
            {
                var t = raw.Trim();
                if (RuntimeFormId.HybridNote(t) is { } hybrid) return (null, hybrid);
                if (RuntimeFormId.TryParse(t, out _))
                    return (null, $"'{t}' is a runtime FormID (the console's eight digits), which names a load-order slot rather than a record; write it as XXXXXX:Plugin.esp.");
                if (!FormKey.TryFactory(t, out var fk))
                    return (null, $"'{t}' is not a reference FormID; write it as XXXXXX:Plugin.esp.");
                seeds.Add(fk);
            }
            return (seeds, null);
        }

        /// <summary>The question asked of one captured load order, answered as nearprobe prints it. <paramref name="source"/>
        /// describes the load order (instance, profile, configuration, plugin count, limits, warnings);
        /// <paramref name="notIndexed"/> names the plugins the index could not read (plugin → why).</summary>
        public static NearOutcome Ask(LoadOrderResolver.IndexView view, NearRequest request,
                                      IReadOnlyDictionary<string, object?> source, IReadOnlyDictionary<string, string> notIndexed)
        {
            var answer = NearReferences.Of(view, request.Seeds, request.Within, request.Types);
            if (answer.Refusal is { } why) return new("", Refused, why);
            if (!answer.WithinIsComplete)
            {
                var named = request.Seeds.Count <= 3 ? string.Join(", ", request.Seeds.Select(FormIdToken.Of)) : $"{request.Seeds.Count} references";
                return new("", Refused,
                    $"--within {request.Within.ToString(CultureInfo.InvariantCulture)} reaches past the cells read around {named} " +
                    $"(the {NearReferences.GridReach} grid cells on each side), so records farther out would be missed; the answer is complete " +
                    $"only below {answer.CompleteWithin.ToString("0.######", CultureInfo.InvariantCulture)} units, so ask with --within " +
                    $"{Offer(answer.CompleteWithin)} or less.");
            }

            bool complete = answer.Unread.Count == 0 && notIndexed.Count == 0;
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                w.WriteString("tool", "nearprobe");
                w.WriteNumber("schema", 1);
                w.WriteStartObject("query");
                w.WriteStartArray("seeds");
                foreach (var s in request.Seeds) w.WriteStringValue(FormIdToken.Of(s));
                w.WriteEndArray();
                w.WriteNumber("within", request.Within);
                Strings(w, "types", request.TypesAsked);
                Strings(w, "typesResolved", request.Types?.Select(t => t.FullName ?? t.Name).ToList());
                w.WriteEndObject();
                w.WritePropertyName("source");
                JsonSerializer.Serialize(w, source);
                w.WriteBoolean("complete", complete);
                if (double.IsPositiveInfinity(answer.CompleteWithin)) w.WriteNull("completeWithin");
                else w.WriteNumber("completeWithin", answer.CompleteWithin);
                w.WriteStartArray("records");
                foreach (var k in answer.Keys)
                {
                    w.WriteStartObject();
                    w.WriteString("formid", FormIdToken.Of(k));
                    w.WriteStartArray("hits");
                    foreach (var h in answer.Hits[k])
                    {
                        w.WriteStartObject();
                        w.WriteString("seed", FormIdToken.Of(h.Seed));
                        w.WriteString("provider", h.Provider);
                        w.WriteNumber("gap", h.Gap);
                        w.WriteString("rule", h.Rule);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteString("note", answer.Note);
                Strings(w, "unread", answer.Unread);
                if (answer.UnreadSentence is { } us) w.WriteString("unreadSentence", us); else w.WriteNull("unreadSentence");
                w.WriteStartArray("notIndexed");
                foreach (var (plugin, reason) in notIndexed.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                {
                    w.WriteStartObject();
                    w.WriteString("plugin", plugin);
                    w.WriteString("why", reason);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            var json = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            if (complete) return new(json, Answered, null);

            var parts = new List<string>();
            if (notIndexed.Count > 0)
                parts.Add($"{notIndexed.Count} plugin(s) of this load order could not be read when it was indexed (" +
                          string.Join("; ", notIndexed.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => $"{kv.Key}: {OneLine(kv.Value)}")) +
                          "), so what they place near these references is not listed");
            if (answer.UnreadSentence is { } unread) parts.Add(unread);
            return new(json, Incomplete, string.Join("; ", parts) + "; close any program holding them, or fix or disable them, and ask again.");
        }

        /// <summary>The radius a refusal offers for an answer complete to <paramref name="completeWithin"/>: rounded DOWN to
        /// three decimals and kept clear of the bound, so asking with the printed number is answered (the engine judges
        /// completeness strictly below the bound, with its own 1e-9 tolerance).</summary>
        public static string Offer(double completeWithin) =>
            (Math.Floor((completeWithin - 1e-6) * 1000) / 1000).ToString("0.###", CultureInfo.InvariantCulture);

        static string OneLine(string s) => string.Join(" ", s.Split('\n', '\r').Select(x => x.Trim()).Where(x => x.Length > 0)).Replace(". ", ", ").TrimEnd('.');

        static void Strings(Utf8JsonWriter w, string name, IReadOnlyList<string>? values)
        {
            if (values is null) { w.WriteNull(name); return; }
            w.WriteStartArray(name);
            foreach (var v in values) w.WriteStringValue(v);
            w.WriteEndArray();
        }

        /// <summary>Record type names to getter types through houseCARL's own resolver, or one sentence.
        /// <paramref name="resolver"/> names the resolver's type (houseCARL's internal TypeLookup).</summary>
        public static (IReadOnlyList<Type>? Types, string? Refusal) ResolveTypes(IReadOnlyList<string> names,
                                                                                  string resolver = TypeLookupName)
        {
            string Pin(string what) =>
                $"houseCARL's record type resolver ({resolver}) {what} in the houseCARL this build carries, so --types cannot be read; build the module again, or check that the pin (external/houseCARL) still has it.";
            Type? type;
            try { type = Type.GetType(resolver, throwOnError: false); }
            catch (Exception ex) when (ex is IOException or BadImageFormatException) { return (null, Pin($"would not load ({ex.GetType().Name})")); }
            var resolveSet = type?.GetMethod("ResolveSet", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                                             new[] { typeof(IReadOnlyList<string>) }, null);
            if (type is null || resolveSet is null) return (null, Pin("is not"));

            // the rulebook the resolver reads: the one beside the module's houseCARL server, or WB_CORPUS
            var corpus = Environment.GetEnvironmentVariable("WB_CORPUS") is { Length: > 0 } env ? env.Trim() : ModuleCorpus();
            if (corpus is null || !File.Exists(corpus))
                return (null, $"houseCARL's rulebook {corpus ?? "(corpus.json beside the module's houseCARL server)"} is missing, so --types cannot be read; run scripts\\build.ps1, or set WB_CORPUS to a corpus.json.");
            HousecarlRulebook.Use(corpus);
            try
            {
                var lookup = Activator.CreateInstance(type, nonPublic: true);
                var got = (IReadOnlyList<Type>?)resolveSet.Invoke(lookup, new object?[] { names });
                return got is { Count: > 0 } ? (got, null) : (null, Pin("answered nothing"));
            }
            catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException bad) { return (null, bad.Message); }
            catch (Exception ex) when (ex is TargetInvocationException or MissingMethodException or IOException or TypeLoadException)
            { return (null, Pin($"would not run ({(ex.InnerException ?? ex).GetType().Name})")); }
        }

        /// <summary>houseCARL's record type resolver: internal to housecarl-mcp (its friends are houseCARL's generator and
        /// tests), so it is reached by name; the owner of "REFR" → IPlacedObjectGetter, called rather than copied.</summary>
        public const string TypeLookupName = "HousecarlMcp.TypeLookup, housecarl-mcp";

        /// <summary>corpus.json beside the module's houseCARL server, where the module's layout marker says its tools are.</summary>
        static string? ModuleCorpus()
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
            var marker = Path.Combine(root, "housecarl-integration-layout.json");
            if (!File.Exists(marker)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(marker));
            var tool = doc.RootElement.GetProperty("tool").GetString()!.Replace("{name}", "housecarl-mcp");
            return Path.Combine(Path.GetDirectoryName(Path.Combine(root, tool.Replace('/', Path.DirectorySeparatorChar)))!, "corpus.json");
        }
    }
}
