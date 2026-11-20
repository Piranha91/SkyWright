namespace RunControl;

/// <summary>Where the controller's lines go: standard output and standard error, or a test's own writers.</summary>
public static class Report
{
    public static TextWriter Out { get; set; } = Console.Out;
    public static TextWriter Err { get; set; } = Console.Error;

    public static void Line(string text) => Out.WriteLine(text);
    public static void Error(string text) => Err.WriteLine(text);
}
