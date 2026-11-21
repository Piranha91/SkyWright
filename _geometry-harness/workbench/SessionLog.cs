using System.Diagnostics;
using System.Text;

namespace Workbench;

/// <summary>The app's log file. The workbench is a windowed program with no console of its own, so everything written to
/// standard output and error (its own lines and viewprobe's) also goes to this file; a parent that captures the output
/// (the tests, runcontrol) still receives it. One file per session, replaced at the next start.</summary>
internal static class SessionLog
{
    /// <summary>The log file this session writes, or null when none could be opened.</summary>
    internal static string? FilePath { get; private set; }

    internal static void Start()
    {
        var dir = Environment.GetEnvironmentVariable("WB_LOG_DIR") is { Length: > 0 } d ? d : Path.Combine(Path.GetTempPath(), "workbench");
        StreamWriter? file = null;
        // a second workbench open at the same time keeps its own file
        foreach (var name in new[] { "workbench.log", $"workbench-{Environment.ProcessId}.log" })
        {
            try
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, name);
                file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };
                FilePath = path;
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (file is null) return;
        var shared = TextWriter.Synchronized(file);
        Console.SetOut(new Tee(Console.Out, shared));
        Console.SetError(new Tee(Console.Error, shared));
        shared.WriteLine($"workbench log, started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    }

    /// <summary>Open the log in the system's text viewer.</summary>
    internal static string Open()
    {
        if (FilePath is null || !File.Exists(FilePath)) throw new InvalidOperationException("this session has no log file; the temp folder could not be written.");
        Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true });
        return $"the log is open ({FilePath})";
    }

    /// <summary>Writes each line to the original stream and the file; a stream that fails is dropped, never thrown at the caller.</summary>
    sealed class Tee(TextWriter first, TextWriter second) : TextWriter
    {
        public override Encoding Encoding => first.Encoding;
        public override void Write(char value) { Both(w => w.Write(value)); }
        public override void Write(string? value) { Both(w => w.Write(value)); }
        public override void WriteLine(string? value) { Both(w => w.WriteLine(value)); }
        public override void Flush() { Both(w => w.Flush()); }
        void Both(Action<TextWriter> act)
        {
            try { act(first); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            try { act(second); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }
}
