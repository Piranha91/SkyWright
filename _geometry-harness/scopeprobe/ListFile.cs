using System.Text;

// scopeprobe's list files (--target-file, --also-scan-file): one entry a line, UTF-8, blank lines skipped, so a caller
// passes a list that grows with the load order without a command line that grows with it. This is the one reader.
static class ListFile
{
    /// <summary>The entries of the list file <paramref name="path"/> given to <paramref name="flag"/>, or the one sentence
    /// saying why it cannot be read.</summary>
    public static (List<string>? Entries, string? Problem) Read(string path, string flag)
    {
        string one = flag == "--target-file" ? "--target" : "--also-scan";
        string Why(string what) => $"scopeprobe: the list file {path} given to {flag} {what}; check that path, or pass the entries one by one with {one}.";
        if (Directory.Exists(path)) return (null, Why("is a folder, not a file"));
        if (!File.Exists(path)) return (null, Why("does not exist"));
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(File.ReadAllBytes(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, Why($"could not be read ({ex.Message.TrimEnd('.')})")); }
        catch (DecoderFallbackException) { return (null, Why("is not UTF-8 text")); }
        if (text.StartsWith('﻿')) text = text[1..];
        var entries = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        return (entries, null);
    }
}
