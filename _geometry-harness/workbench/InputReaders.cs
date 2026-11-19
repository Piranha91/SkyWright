namespace Workbench;

/// <summary>A path a reader recognised: the reader, the folder or file it settled on, and what it is in a few words.</summary>
internal sealed record InputMatch(IInputReader Reader, string Path, string What);

/// <summary>One kind of thing the window opens. A new kind is a new reader: the Open control, the scene path and the
/// state tools' <c>open</c> verb do not change.</summary>
internal interface IInputReader
{
    /// <summary>The kind in a few words, as the window names it ("evaluation results").</summary>
    string Kind { get; }
    /// <summary>What to choose, for the sentence that refuses anything else ("an evaluation results folder").</summary>
    string Accepts { get; }
    /// <summary>Whether this path is this kind, by the names on disk alone: no file is parsed here.</summary>
    InputMatch? Recognise(string path);
    /// <summary>Open it in the window and say what was opened, in one sentence. Called on the UI thread; long reads go
    /// to a task the reader owns and report through the banner. A refusal is an <see cref="InvalidOperationException"/>.</summary>
    string Open(InputMatch match);
}

/// <summary>The readers, in the order they are asked. The first that recognises a path opens it.</summary>
internal static class InputReaders
{
    static readonly List<IInputReader> _all = new();
    internal static IReadOnlyList<IInputReader> All => _all;

    /// <summary>Add a reader after the ones already listed (a kind is listed once).</summary>
    internal static void Register(IInputReader reader)
    {
        if (_all.All(r => r.Kind != reader.Kind)) _all.Add(reader);
    }

    /// <summary>The sentence that refuses a path no reader recognises.</summary>
    internal static string Refusal(string path, IReadOnlyList<IInputReader> readers) =>
        $"'{path}' is not something the workbench opens: choose {OrList(readers.Select(r => r.Accepts).ToList())}.";

    static string OrList(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "an input a reader is registered for",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " or " + items[^1],
    };

    /// <summary>Which reader takes this path; a path that does not exist, or that none recognises, is refused in one sentence.</summary>
    internal static InputMatch Detect(string? path) => Detect(path, _all);

    internal static InputMatch Detect(string? path, IReadOnlyList<IInputReader> readers)
    {
        var p = (path ?? "").Trim().Trim('"');
        if (p.Length == 0) throw new InvalidOperationException("type or browse to a folder or file to open.");
        if (!System.IO.Path.IsPathFullyQualified(p)) throw new InvalidOperationException($"'{p}' is not a full path; give the folder or file by its full path.");
        if (!Directory.Exists(p) && !File.Exists(p)) throw new InvalidOperationException($"'{p}' does not exist; choose a folder or file that does.");
        p = System.IO.Path.GetFullPath(p);
        foreach (var r in readers)
            if (r.Recognise(p) is { } m) return m;
        throw new InvalidOperationException(Refusal(p, readers));
    }

    /// <summary>Open a path: detect its kind and hand it to that reader.</summary>
    internal static string Open(string? path)
    {
        var m = Detect(path);
        WorkbenchHost.Say($"open: '{m.Path}' is {m.What}");
        return m.Reader.Open(m);
    }
}

/// <summary>An evaluation bundle: the folder holding <c>manifest.json</c>, <c>plan.json</c> and <c>receipts/</c>, or its
/// parent holding <c>evidence/</c>. Opens into the Conflicts and Findings tabs, as <c>--bundle</c> does.</summary>
internal sealed class BundleReader : IInputReader
{
    public string Kind => "evaluation bundle";
    public string Accepts => "an evaluation bundle";

    static bool IsEvidence(string dir) =>
        File.Exists(Path.Combine(dir, "manifest.json")) && File.Exists(Path.Combine(dir, "plan.json")) && Directory.Exists(Path.Combine(dir, "receipts"));

    public InputMatch? Recognise(string path)
    {
        var dir = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
        foreach (var d in new[] { dir, Path.Combine(dir, "evidence"), Path.Combine(dir, "bundle", "evidence") })
            if (Directory.Exists(d) && IsEvidence(d)) return new InputMatch(this, d, "an evaluation bundle");
        return null;
    }

    public string Open(InputMatch match) => Program.OpenBundleInWindow(match.Path);
}
