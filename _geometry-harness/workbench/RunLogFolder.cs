using System.Diagnostics;

namespace Workbench;

/// <summary>Where every program the app starts keeps its run records (houseCARL's run log, <c>HOUSECARL_RUNLOG_DIR</c>): a
/// folder beside what that start writes, never the user's shared store. A program started without it reads the whole
/// shared history at start-up and appends to it (%LOCALAPPDATA%\houseCARL\runs\history.jsonl, 316.5 MB on 2026-11-27, and
/// nothing compacts it). The one owner: a ProcessStartInfo the app uses is made here (RunLogFolderTests holds the app to
/// that).</summary>
internal static class RunLogFolder
{
    internal const string Variable = "HOUSECARL_RUNLOG_DIR";

    /// <summary>The run log kept in <paramref name="folder"/>: its <c>runlog</c> subfolder.</summary>
    internal static string In(string folder) => Path.Combine(Path.GetFullPath(folder), "runlog");

    /// <summary>The run log beside <paramref name="file"/>, in that file's folder.</summary>
    internal static string Beside(string file) => In(Path.GetDirectoryName(Path.GetFullPath(file))!);

    /// <summary>A program started with its run records in <paramref name="runlogDir"/>; the caller sets the rest.</summary>
    internal static ProcessStartInfo Start(string program, string runlogDir)
    {
        var psi = new ProcessStartInfo(program) { UseShellExecute = false };
        psi.Environment[Variable] = runlogDir;   // set, not left to a default
        return psi;
    }
}
