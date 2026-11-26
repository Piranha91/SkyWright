using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using SkyWright.Engine;

// NEARPROBE — what stands near these placed references, for an LLM: SkyWright's own query (SkyWright.Engine.NearReferences.Of) over a load order, as JSON.
//
// The houseCARL integration module's answer to "what is near this reference" (Piranha, 2026-11-24: "an LLM asks
// SkyWright that question"; houseCARL's near= is gone). It reads the load order the way lookprobe does
// (_shared/HarnessOrderCli.cs), asks the engine, and prints the engine's own fields: per record, the hits (the seed, the
// plugin whose version reached it, the gap between the two extents, the rule); the engine's note; the plugins it could
// not read. It adds no distance rule: the radius is the engine's to validate, and the one comparison here is the asked
// radius against the radius the engine says its answer is complete to (NearAnswer.WithinIsComplete).
//
// usage: nearprobe --instance DIR --profile NAME --game DIR [--order FILE]
//                  --of XXXXXX:Plugin.esp [--of ...] | --of @<absolute path to a housecarl_records to_file= result>
//                  --within UNITS [--types SIGNATURE-OR-NAME ...]
//
// Exit 0: answered, every plugin read. 2: refused, one sentence on stderr, nothing on stdout. 3: answered, but a plugin
// could not be read: the JSON (complete: false, the plugins named) on stdout and one sentence on stderr.
//
// GATE B STUB: prints an empty object and exits 0, so the module's tests are red on their own assertions.

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

        /// <summary>The program: the command line, the load order, the question, the answer.</summary>
        public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
        {
            stdout.WriteLine("{}");   // GATE B STUB
            return Answered;
        }

        /// <summary>The question asked of one captured load order, answered as nearprobe prints it. <paramref name="source"/>
        /// describes the load order (instance, profile, configuration, plugin count, limits, warnings);
        /// <paramref name="notIndexed"/> names the plugins the index could not read (plugin → why).</summary>
        public static NearOutcome Ask(LoadOrderResolver.IndexView view, NearRequest request,
                                      IReadOnlyDictionary<string, object?> source, IReadOnlyDictionary<string, string> notIndexed)
            => new("{}", Answered, null);   // GATE B STUB

        /// <summary>Record type names to getter types through houseCARL's own resolver, or one sentence.
        /// <paramref name="resolver"/> names the resolver's type (houseCARL's internal TypeLookup).</summary>
        public static (IReadOnlyList<Type>? Types, string? Refusal) ResolveTypes(IReadOnlyList<string> names,
                                                                                  string resolver = TypeLookupName)
            => (Array.Empty<Type>(), null);   // GATE B STUB

        /// <summary>houseCARL's record type resolver: internal to housecarl-mcp (its friends are houseCARL's generator and
        /// tests), so it is reached by name; the owner of "REFR" → IPlacedObjectGetter, called rather than copied.</summary>
        public const string TypeLookupName = "HousecarlMcp.TypeLookup, housecarl-mcp";
    }
}
