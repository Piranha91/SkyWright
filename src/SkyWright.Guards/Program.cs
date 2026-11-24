using SkyWright.Guards;

// SkyWright.Guards <guard> [options]: one guard per run, exit 0 when it passes.
//   harness-suites [--full] [--self-check]   the Python suites and the probes' self-tests (HarnessSuitesGuard)
var guards = new Dictionary<string, Func<string[], int>>(StringComparer.Ordinal)
{
    ["harness-suites"] = HarnessSuitesGuard.RunGuard,
};
if (args.Length == 0 || !guards.TryGetValue(args[0], out var run))
{
    Console.Error.WriteLine($"name a guard to run: {string.Join(", ", guards.Keys)} (for example: SkyWright.Guards harness-suites).");
    return 2;
}
return run(args[1..]);
