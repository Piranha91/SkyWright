using System.Text;
using HousecarlCore;
using HousecarlCore.Progress;
using RunControl;

// RUNCONTROL: the evaluation run controller in C#, replacing evalpipeline.py and evalrun.py stage by stage.
//
// Built so far: the pipeline driver and its first four stages (census, prepare, scope, plan) for both entry kinds,
// and evalrun.py's `plan` command, over the foundations (Python's JSON bytes, hashes, orderings and text forms, the
// program identity rule, the stage ledger). A run folder it writes through scope is one the Python pipeline
// continues, and the reverse; a plan is bound to the controller that froze it. The stages from evaluate on stop the
// run with one line until they are built; the stages from report-plan on stay in the Python pipeline.
//
// usage: runcontrol run    --entry ENTRY.json --out RUN [--machine MACHINE.json] [--through STAGE]
//                          [--reuse-preparation-from WORK_FOLDER]
//        runcontrol arms   --entry ENTRY.json [--machine MACHINE.json] [--out RUN]
//        runcontrol status --out RUN
//        runcontrol plan   --registry R --candidates DIR --out RUN [--scope scope.json] [evalrun.py plan's other flags]
//        runcontrol --self-test          run every known answer; exit 0 when all pass, 1 otherwise
//        runcontrol identity FOLDER      print the package identity of a build folder as JSON
//        runcontrol canon                read JSON on standard input; print the hash of its two canonical forms
//
// EXIT CODES: 0 done, 2 refused (one sentence on standard error), 3 a result that awaits judgement.

Console.OutputEncoding = Encoding.UTF8;
string command = args.Length >= 1 ? args[0] : "";
bool pipeline = command is "run" or "arms" or "status";
// The pipeline commands record under the Python pipeline's run-log name, which the workbench reads. `run` opens its
// record itself (Driver.OpenRun below), once the entry has said where the run log goes.
// `plan` records as evalrun.py's plan does (op evalrun-plan), opened where the plan is written (Plan.Run).
using var __run = command is "run" or "plan" ? null
    : pipeline ? RunLog.BeginProcess("evalpipeline", args, new RunOptions { Mode = command }) : RunLog.BeginProcess("runcontrol", args);
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
    if (command == "plan") return Plan.Run(PlanArgs.Parse(args[1..]));
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
        OpenRun = mode => RunLog.BeginProcess("evalpipeline", args, new RunOptions { Mode = mode, StageSource = StageSource.Counted }),
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
