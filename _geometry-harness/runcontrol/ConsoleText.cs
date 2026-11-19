using System.Text;

namespace RunControl;

/// <summary>This program's own standard output and standard error.</summary>
public static class ConsoleText
{
    /// <summary>Writes both streams as UTF-8 through this program's own writers. The console's code page is not
    /// set: a probe inherits the console, and its code page decides the bytes of the probe's log.</summary>
    public static void WriteUtf8()
    {
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
    }
}
