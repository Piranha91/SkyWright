using System.Reflection;
using ViewProbe;

namespace Workbench;

/// <summary>Entry point: viewprobe's own command line builds the scene (cell, instance, profile, camera — anything
/// <c>viewprobe --interactive</c> takes), then hands it to the workbench window instead of the fly-through.</summary>
internal static class Program
{
    /// <summary>The only MO2 instance the workbench may write to while it is in development (the lab, a scratch
    /// instance). A synthetic test instance is allowed by the test that builds it, never from the command line.</summary>
    internal const string LabInstance = @"X:\Skyrim Conflict Lab";

    /// <summary>The houseCARL server built in this checkout, beside this exe's checkout: src/housecarl-mcp/bin/Release.</summary>
    internal static string DefaultServerExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "housecarl.sln"))) dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("this workbench was not built inside a houseCARL checkout; set WB_SERVER to the housecarl-mcp.exe it should use.");
        return Path.Combine(dir.FullName, "src", "housecarl-mcp", "bin", "Release", "net9.0", "housecarl-mcp.exe");
    }

    /// <summary>The rulebook the server reads: this checkout's generated/corpus.json (the generator writes it).</summary>
    internal static string DefaultCorpus()
    {
        var exe = DefaultServerExe();
        return Path.Combine(Path.GetDirectoryName(exe)!, "..", "..", "..", "..", "..", "generated", "corpus.json");
    }

    [STAThread]
    static int Main(string[] args)
    {
        InteractiveViewer.Host = WorkbenchHost.Run;
        var a = args.Contains("--interactive") ? args : args.Append("--interactive").ToArray();
        var entry = typeof(InteractiveViewer).Assembly.EntryPoint
            ?? throw new InvalidOperationException("viewprobe has no entry point");
        var r = entry.Invoke(null, new object[] { a });
        return r is int code ? code : 0;
    }
}
