using System.Text;
using HousecarlCore.Progress;
using RunControl;

// RUNCONTROL: the evaluation run controller in C#, replacing evalpipeline.py and evalrun.py stage by stage.
//
// This is the foundation only: Python's JSON bytes, hashes, orderings and text forms (PyJson, PyText, PyOrder,
// PyPath, Hashing), the program identity rule (ToolIdentity) and the stage ledger (StageLedger). No stage runs yet.
//
// usage: runcontrol --self-test          run every known answer; exit 0 when all pass, 1 otherwise
//        runcontrol identity FOLDER      print the package identity of a build folder as JSON
//        runcontrol canon                read JSON on standard input; print the hash of its two canonical forms
//
// EXIT CODES: 0 done, 2 refused (one sentence on standard error), 3 a result that awaits judgement.

Console.OutputEncoding = Encoding.UTF8;
using var __run = RunLog.BeginProcess("runcontrol", args);
try
{
    if (args.Length >= 1 && args[0] == "--self-test") return SelfTest.Run();
    if (args.Length == 2 && args[0] == "identity")
    {
        Console.WriteLine(PyJson.Dumps(ToolIdentity.Package(args[1]), indent: 1, ensureAscii: false));
        return Exit.Ok;
    }
    if (args.Length == 1 && args[0] == "canon")
    {
        using var stdin = Console.OpenStandardInput();
        using var buffer = new MemoryStream();
        stdin.CopyTo(buffer);
        object? doc = PyJson.Loads(PyJson.Decode(buffer.ToArray(), bom: true));
        Console.WriteLine($"sha256_json {PyJson.Sha256Json(doc)}");
        Console.WriteLine($"canonical   {Hashing.Sha256Text(PyJson.Canonical(doc))}");
        return Exit.Ok;
    }
    throw new Refusal(args.Length == 0
        ? "runcontrol needs a command; the ones built so far are --self-test, identity FOLDER and canon."
        : $"runcontrol has no command '{args[0]}' yet (or it was given the wrong arguments); the ones built so far are --self-test, identity FOLDER and canon.");
}
catch (Refusal r)
{
    Console.Error.WriteLine($"REFUSED: {r.Message}");
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
