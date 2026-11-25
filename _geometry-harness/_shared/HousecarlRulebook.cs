using HousecarlCore;

namespace HarnessShared;

/// <summary>houseCARL's rulebook (corpus.json) as this process's houseCARL code reads it. houseCARL keeps ONE rulebook
/// per process in a static (<see cref="CorpusRulebook.CorpusPath"/>), and its default is a path relative to the working
/// folder, so every program that runs houseCARL code reading it sets it here first: the one owner of that setting. Where
/// the rulebook is comes from the build layout (<see cref="CheckoutLayout"/>, <c>Piece.Corpus</c>, <c>WB_CORPUS</c>).</summary>
public static class HousecarlRulebook
{
    static readonly object Gate = new();
    static string? _path;

    /// <summary>Make <paramref name="corpusPath"/> the rulebook houseCARL's code reads in this process. A second, different
    /// one is refused: houseCARL caches what it read from the first.</summary>
    public static void Use(string corpusPath)
    {
        var full = Path.GetFullPath(corpusPath);
        lock (Gate)
        {
            if (_path is not null && !string.Equals(_path, full, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"houseCARL's rulebook in this program is '{_path}', and a read or write asked for '{full}'; start the program again to use another rulebook.");
            CorpusRulebook.CorpusPath = _path = full;
        }
    }

    /// <summary>For a program that reads records through houseCARL: its rulebook (WB_CORPUS, else its build's), set and
    /// loaded now, so a missing or unreadable one is known before any work. Null when it loaded; else one sentence for
    /// the user, starting with <paramref name="program"/>, saying what is lost (<paramref name="lost"/>) and what to try.</summary>
    public static string? Require(string program, string lost)
    {
        string path, source;
        try { (path, source) = CheckoutLayout.ResolveWithSource(CheckoutLayout.Piece.Corpus, "rulebook corpus.json"); }
        catch (InvalidOperationException ex) { return $"{program}: {ex.Message}"; }
        path = Path.GetFullPath(path);
        source = source == "build" ? "the one this SkyWright build carries" : $"named by {source}";
        if (!File.Exists(path))
            return $"{program}: houseCARL's rulebook {path} is missing ({source}), so {lost}; run scripts\\build.ps1 to make it, or set WB_CORPUS to a corpus.json.";
        Use(path);
        if (ModeledFieldIndex.Unavailable is null) return null;
        // houseCARL's own reason, read again only on this failure path (the index keeps its sentence, not the cause alone)
        string why;
        try { CorpusRulebook.LoadCorpus(path); why = "houseCARL could not build its field index from it"; }
        catch (Exception ex) { why = string.Join(" ", ex.Message.Split('\n', '\r').Select(s => s.Trim()).Where(s => s.Length > 0)).TrimEnd('.'); }
        return $"{program}: houseCARL's rulebook {path} ({source}) could not be read ({why}), so {lost}; run scripts\\build.ps1 -Rulebook to make it again, or set WB_CORPUS to a readable corpus.json.";
    }
}
