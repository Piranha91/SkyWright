using HousecarlCore;
using SkyWright.Engine;
using HousecarlCore.Progress;
using RunControl;

// RUNCONTROL: the evaluation run controller, the one owner of census to bundle.
//
// The pipeline driver and its stages census, prepare, scope, plan, evaluate, records, scenes and bundle, and the
// commands plan, run, resume, status, finalize, bundle, verify-bundle, population and the order sweep,
// over the foundations (the JSON bytes, hashes, orderings and text forms its files are read in, the program identity
// rule, the stage ledger). The names in brackets below are the Python commands each replaced. SkyWright's registry has
// no record check: those only houseCARL's record tools read are the houseCARL integration module's, planned over a
// finished app scan and imported there; so is the live switch check, whose launches a live game driver reports.
// A plan is bound to the controller build that froze it.
// The stages from report-plan on belong to the AI-driven evaluation, which continues a run folder this program took
// through the bundle.
//
// usage: runcontrol run    --entry ENTRY.json --out RUN [--machine MACHINE.json] [--through STAGE]
//                          [--reuse-preparation-from WORK_FOLDER]
//        runcontrol arms   --entry ENTRY.json [--machine MACHINE.json] [--out RUN]
//        runcontrol status --out RUN
//        runcontrol plan   --registry R --candidates DIR --out RUN [--scope scope.json] [evalrun.py plan's other flags]
//        runcontrol --self-test          run every known answer; exit 0 when all pass, 1 otherwise
//        runcontrol identity FOLDER      print the package identity of a build folder as JSON
//        runcontrol canon                read JSON on standard input; print the hash of its two canonical forms
//        runcontrol fingerprint          read a JSON list of configurations on standard input (plugins, providers, overlays,
//                                        population, state, schemaVersion); print the list of their fingerprints as JSON.
//                                        For a caller that must not hold its own copy of the rule.
//        runcontrol finalize --run RUN   derive disposition.json and STATUS.md from the ledger and receipts (evalrun finalize)
//        runcontrol bundle --run RUN [--out DIR] [--reader-cmd CMD] [--reader-env K=V]... [--mod-name PLUGIN=NAME]... [--no-names]
//                          [--registry FILE] [--scope FILE] [--search FILE]   assemble one evidence bundle (evalrun bundle)
//        runcontrol verify-bundle DIR [--out FILE]   check a bundle from its own files only (evalrun verify-bundle)
//        runcontrol run    --run RUN [--budget-minutes N]   run the plan's checks (evalrun run); `run --entry` is the pipeline's
//        runcontrol resume --run RUN [--budget-minutes N] [--rebuild-ledger]   re-check what is kept, redo the rest (evalrun resume)
//        runcontrol status --run RUN     print the ledger as a table (evalrun status); `status --out` is the pipeline's
//        runcontrol population --plugin PATH... [--mod NAME...] [--instance DIR] [--target NAME...] --out FILE   (evalrun population)
//
// EXIT CODES: 0 done, 2 refused (one sentence on standard error), 3 a result that awaits judgement.

if (args.Length >= 1 && args[0] == "ordersweep") return OrderSweep.Run(args[1..]);   // before the lines below: the sweep keeps the console code page its probes inherit, and its run-log records are ordersweep.py's
ConsoleText.WriteUtf8();   // this program's own two streams; the console's code page is left as it is, because the probes inherit it
if (RuleQueries.Handles(args)) return RuleQueries.Run(args);   // pure answers a caller may ask many times a run: no run-log record, whose opening costs seconds
if (EvalCommands.Handles(args)) return EvalCommands.Run(args);   // evalrun's commands: before the process run below, so their run-log records are evalrun.py's
string command = args.Length >= 1 ? args[0] : "";
bool pipeline = command is "run" or "arms" or "status";
// The pipeline commands record under the Python pipeline's run-log name, which the workbench reads. `run` opens its
// record itself (Driver.OpenRun below), once the entry has said where the run log goes.
// `plan` records as evalrun.py's plan does (op evalrun-plan), opened where the plan is written (Plan.Run).
using var __run = command is "run" or "plan" ? null
    : pipeline ? RunLog.BeginProcess("runcontrol-run", args, new RunOptions { Mode = command }) : RunLog.BeginProcess("runcontrol", args);
try
{
    if (args.Length >= 1 && args[0] == "--self-test") return SelfTest.Run();
    if (args.Length == 2 && command == "identity")
    {
        Console.WriteLine(PyJson.Dumps(ToolIdentity.Package(args[1]), indent: 1, ensureAscii: false));
        return Exit.Ok;
    }
    if (args.Length == 1 && command == "canon")
    {
        using var stdin = Console.OpenStandardInput();
        using var buffer = new MemoryStream();
        stdin.CopyTo(buffer);
        object? doc = PyJson.Loads(PyJson.Decode(buffer.ToArray(), bom: true));
        Console.WriteLine($"sha256_json {PyJson.Sha256Json(doc)}");
        Console.WriteLine($"canonical   {Hashing.Sha256Text(PyJson.Canonical(doc))}");
        return Exit.Ok;
    }
    if (command == "plan") return Plan.Run(PlanFlags(args));
    if (!pipeline)
        throw new Refusal(args.Length == 0
            ? "runcontrol needs a command: run, arms, status, plan, --self-test, identity FOLDER or canon."
            : $"runcontrol has no command '{command}' (or it was given the wrong arguments); the commands are run, arms, status, plan, --self-test, identity FOLDER and canon.");

    // `--flag=value` is read as `--flag value`
    args = args.SelectMany(a => a.StartsWith("--") && a.Contains('=') ? new[] { a[..a.IndexOf('=')], a[(a.IndexOf('=') + 1)..] } : [a]).ToArray();
    string? entry = null, outDir = null, machine = null, through = null, reuseFrom = null;
    for (int i = 1; i < args.Length; i++)
    {
        string flag = args[i];
        string Value() => i + 1 < args.Length ? args[++i] : throw new Refusal($"{flag} needs a value after it.");
        // a path argument is made absolute here: children run from the checkout's root, where a relative path would be read
        string Abs() => PyPath.Resolve(Value() is var v && v.StartsWith('~') ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + v[1..] : v);
        switch (flag)
        {
            case "--entry" when command != "status": entry = Abs(); break;
            case "--out": outDir = Abs(); break;
            case "--machine" when command != "status": machine = Abs(); break;
            case "--through" when command == "run": through = Value(); break;
            case "--reuse-preparation-from" when command == "run": reuseFrom = Abs(); break;
            default: throw new Refusal($"runcontrol {command} does not take {flag}; see the usage at the top of Program.cs.");
        }
    }
    if (command != "status" && entry == null) throw new Refusal($"runcontrol {command} needs --entry, the mod's selection entry (JSON).");
    if (command != "arms" && outDir == null) throw new Refusal($"runcontrol {command} needs --out, the run folder.");
    if (through != null && !Driver.Stages.Contains(through))
        throw new Refusal($"--through {through} is not a stage; choose from {string.Join(", ", Driver.Stages)}.");
    var asked = new PipelineArgs(entry, outDir, machine, through, reuseFrom);
    if (command == "arms") return Driver.PrintArms(asked);
    if (command == "status") return Driver.PrintStatus(asked);
    return new Driver
    {
        OpenRun = mode => RunLog.BeginProcess("runcontrol-run", args, new RunOptions { Mode = mode, StageSource = StageSource.Counted }),
    }.Run(asked);
}
catch (Refusal r)
{
    Console.Error.WriteLine($"REFUSED: {r.Message}");
    return Exit.Refused;
}
catch (ProfileUnreadableException locked)
{
    Console.Error.WriteLine($"REFUSED: {locked.Message}");
    return Exit.Refused;
}
catch (KeyNotFoundException missing)
{
    Console.Error.WriteLine($"REFUSED: the entry, the machine file or a run file lacks a key the run needs ({missing.Message}); add it and run again.");
    return Exit.Refused;
}
catch (PyError wrong)
{
    Console.Error.WriteLine($"REFUSED: an input file holds a value of the wrong kind ({wrong.Message}); fix the file and run again.");
    return Exit.Refused;
}
catch (Unreadable u)
{
    Console.Error.WriteLine($"REFUSED: {u.Message}; make the file readable and run again.");
    return Exit.Refused;
}
catch (FormatException f)
{
    Console.Error.WriteLine($"REFUSED: {f.Message}; give valid JSON.");
    return Exit.Refused;
}

// evalrun.py plan's flags, read as its parser reads them; a flag it does not have or a missing value is refused.
static PlanArgs PlanFlags(string[] args)
{
    var a = new PlanArgs();
    for (int i = 1; i < args.Length; i++)
    {
        string flag = args[i];
        string? joined = null;   // the value of `--flag=value`
        if (flag.StartsWith("--", StringComparison.Ordinal) && flag.Contains('='))
        {
            joined = flag[(flag.IndexOf('=') + 1)..];
            flag = flag[..flag.IndexOf('=')];
        }
        string Value()
        {
            if (joined != null) return joined;
            if (i + 1 < args.Length && !PlanArgs.ReadsAsFlag(args[i + 1])) return args[++i];
            throw new Refusal($"plan: {flag} needs a value after it.");
        }
        switch (flag)
        {
            case "--registry": a.Registry = Value(); break;
            case "--scope": a.Scope = Value(); break;
            case "--candidates": a.Candidates = Value(); break;
            case "--alt": a.Alt = PlanArgs.Add(a.Alt, Value()); break;
            case "--supplied": a.Supplied = PlanArgs.Add(a.Supplied, Value()); break;
            case "--states": a.States = Value(); break;
            case "--depth": a.Depth = PlanArgs.DepthOf(Value()); break;
            case "--settings": a.Settings = Value(); break;
            case "--select": a.Select = PlanArgs.Add(a.Select, Value()); break;
            case "--review-arm": a.ReviewArm = Value(); break;
            case "--exclude": a.Exclude = PlanArgs.Add(a.Exclude, Value()); break;
            case "--opt-in": a.OptIn = PlanArgs.Add(a.OptIn, Value()); break;
            case "--budget-minutes": a.BudgetMinutes = PlanArgs.Minutes(Value()); break;
            case "--max-candidates": a.MaxCandidates = PlanArgs.Count(Value()); break;
            case "--out": a.Out = Value(); break;
            case "--instance": a.Instance = Value(); break;
            case "--profile": a.Profile = Value(); break;
            case "--game": a.Game = Value(); break;
            case "--harness": a.Harness = Value(); break;
            case "--probe-args": a.ProbeArgs = Value(); break;
            case "--primitive-solidity": a.PrimitiveSolidity = Value(); break;
            case "--population-from": a.PopulationFrom = Value(); break;
            case "--mesh-reach": a.MeshReach = Value(); break;
            case "--probe-cmd": a.ProbeCmd = PlanArgs.Add(a.ProbeCmd, Value()); break;
            case "--diff-tolerance": a.DiffTolerance = Value(); break;
            case "--no-reverse" when joined == null: a.NoReverse = true; break;
            default: throw new Refusal($"plan does not take {args[i]}; its flags are those of evalrun.py plan.");
        }
    }
    return a.Checked();
}
