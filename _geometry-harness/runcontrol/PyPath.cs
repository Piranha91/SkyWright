using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RunControl;

/// <summary>pathlib and ntpath spellings on Windows: str(Path), suffix, normpath, resolve, relative_to, rglob.</summary>
public static class PyPath
{
    // ntpath.splitroot: (drive, root, rest).
    static (string drive, string root, string rest) SplitRoot(string p)
    {
        string n = p.Replace('/', '\\');
        if (n.StartsWith('\\'))
        {
            if (n.Length > 1 && n[1] == '\\')
            {
                int start = n.Length >= 8 && n[..8].ToUpperInvariant() == @"\\?\UNC\" ? 8 : 2;
                int index = n.IndexOf('\\', start);
                if (index < 0) return (p, "", "");
                int index2 = n.IndexOf('\\', index + 1);
                if (index2 < 0) return (p, "", "");
                return (p[..index2], p.Substring(index2, 1), p[(index2 + 1)..]);
            }
            return ("", p[..1], p[1..]);
        }
        if (n.Length > 1 && n[1] == ':')
            return n.Length > 2 && n[2] == '\\' ? (p[..2], p.Substring(2, 1), p[3..]) : (p[..2], "", p[2..]);
        return ("", "", p);
    }

    // pathlib's parse: drive, root and the parts after them, with "." and empty parts dropped.
    static (string drive, string root, List<string> tail) Parse(string path)
    {
        var (drv, root, rel) = SplitRoot(path.Replace('/', '\\'));
        if (root.Length == 0 && drv.StartsWith('\\') && !drv.EndsWith('\\'))
        {
            string[] dp = drv.Split('\\');
            if ((dp.Length == 4 && dp[2] is not ("" or "?" or ".")) || dp.Length == 6) root = "\\";
        }
        return (drv, root, rel.Split('\\').Where(x => x.Length > 0 && x != ".").ToList());
    }

    /// <summary>str(Path(p)): backslashes, no "." parts, no trailing separator.</summary>
    public static string Str(string path)
    {
        var (drv, root, tail) = Parse(path);
        if (drv.Length > 0 || root.Length > 0) return drv + root + string.Join('\\', tail);
        if (tail.Count > 0 && SplitRoot(tail[0]).drive.Length > 0) return ".\\" + string.Join('\\', tail);
        return tail.Count > 0 ? string.Join('\\', tail) : ".";
    }

    /// <summary>str(Path(a) / b).</summary>
    public static string Join(string a, params string[] more)
    {
        string p = a;
        foreach (string b in more)
        {
            var (drv, root, _) = SplitRoot(b);
            p = drv.Length > 0 || root.Length > 0 ? b : p + "\\" + b;
        }
        return Str(p);
    }

    /// <summary>Path(p).as_posix().</summary>
    public static string AsPosix(string path) => Str(path).Replace('\\', '/');

    /// <summary>Path(p).name: the last part, or empty for a bare drive or root.</summary>
    public static string Name(string path)
    {
        var tail = Parse(path).tail;
        return tail.Count > 0 ? tail[^1] : "";
    }

    /// <summary>Path(p).suffix: from the last dot of the name, unless the dot leads or ends it.</summary>
    public static string Suffix(string path)
    {
        string name = Name(path);
        int i = name.LastIndexOf('.');
        return i > 0 && i < name.Length - 1 ? name[i..] : "";
    }

    /// <summary>ntpath.normpath: separators unified, "." and ".." folded.</summary>
    public static string NormPath(string path)
    {
        var (drive, root, rest) = SplitRoot(path.Replace('/', '\\'));
        var comps = rest.Split('\\').ToList();
        int i = 0;
        while (i < comps.Count)
        {
            if (comps[i].Length == 0 || comps[i] == ".") comps.RemoveAt(i);
            else if (comps[i] == "..")
            {
                if (i > 0 && comps[i - 1] != "..") { comps.RemoveRange(i - 1, 2); i--; }
                else if (i == 0 && root.Length > 0) comps.RemoveAt(i);
                else i++;
            }
            else i++;
        }
        string prefix = drive + root;
        if (prefix.Length == 0 && comps.Count == 0) comps.Add(".");
        return prefix + string.Join('\\', comps);
    }

    /// <summary>ntpath.normcase: lower case, backslashes.</summary>
    public static string NormCase(string path) => PyText.Lower(path.Replace('/', '\\'));

    /// <summary>Path(p).resolve(): absolute, links and junctions followed, on-disk case, for the part that exists.</summary>
    public static string Resolve(string path)
    {
        string full = Path.GetFullPath(Str(path));
        string rest = "";
        while (true)
        {
            string? final = NativeFile.FinalPath(full);
            if (final != null) return rest.Length == 0 ? final : Str(final + "\\" + rest);
            string? parent = Path.GetDirectoryName(full);
            if (parent == null) return Str(full + (rest.Length == 0 ? "" : "\\" + rest));
            rest = rest.Length == 0 ? Path.GetFileName(full) : Path.GetFileName(full) + "\\" + rest;
            full = parent;
        }
    }

    /// <summary>The parts of child below parent, joined with "/", or null when child is not at or under parent
    /// (PureWindowsPath.relative_to: case ignored; the child's own case kept; "." for the same path).</summary>
    public static string? RelativeTo(string child, string parent)
    {
        var c = Parse(child);
        var p = Parse(parent);
        if (PyText.Lower(c.drive + c.root) != PyText.Lower(p.drive + p.root) || p.tail.Count > c.tail.Count) return null;
        for (int i = 0; i < p.tail.Count; i++)
            if (PyText.Lower(c.tail[i]) != PyText.Lower(p.tail[i])) return null;
        return p.tail.Count == c.tail.Count ? "." : string.Join('/', c.tail.Skip(p.tail.Count));
    }

    /// <summary>evalrun.rel_to: a path relative to the run folder with forward slashes, else the path as given.</summary>
    public static string RelTo(string run, string p) => RelativeTo(Resolve(p), Resolve(run)) ?? AsPosix(p);

    /// <summary>Path(folder).rglob("*"): every file and folder below it, junctions followed, unsorted.</summary>
    public static List<string> RGlob(string folder)
    {
        var found = new List<string>();
        var todo = new Stack<string>();
        todo.Push(Str(folder));
        while (todo.Count > 0)
        {
            string dir = todo.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (string e in entries)
            {
                found.Add(e);
                if (Directory.Exists(e)) todo.Push(e);
            }
        }
        return found;
    }
}

/// <summary>What a file handle tells: the final path, and the volume and file index a hard link shares.</summary>
static class NativeFile
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct ByHandleFileInformation
    {
        public uint Attributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLinkW(string newName, string existingName, IntPtr security);

    // Opens a file or folder for its attributes only, following links.
    static SafeFileHandle Open(string path) =>
        CreateFileW(path, 0x80 /* FILE_READ_ATTRIBUTES */, 7 /* share all */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
            0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS */, IntPtr.Zero);

    /// <summary>The path Windows itself reports for an existing file or folder, or null when it cannot be opened.</summary>
    public static string? FinalPath(string path)
    {
        using var h = Open(path);
        if (h.IsInvalid) return null;
        var sb = new StringBuilder(1024);
        uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
        if (n > sb.Capacity) { sb = new StringBuilder((int)n + 1); n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0); }
        if (n == 0) return null;
        string s = sb.ToString();
        if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + s[8..];
        return s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
    }

    /// <summary>(volume, file index, size) of an existing file, or null when it cannot be opened.</summary>
    public static (uint volume, ulong index, long size)? Identity(string path)
    {
        using var h = Open(path);
        if (h.IsInvalid || !GetFileInformationByHandle(h, out var i)) return null;
        return (i.VolumeSerialNumber, ((ulong)i.FileIndexHigh << 32) | i.FileIndexLow, ((long)i.FileSizeHigh << 32) | i.FileSizeLow);
    }

    /// <summary>How many names an existing file has, or 0 when it cannot be opened.</summary>
    public static uint LinkCount(string path)
    {
        using var h = Open(path);
        return h.IsInvalid || !GetFileInformationByHandle(h, out var i) ? 0 : i.NumberOfLinks;
    }

    /// <summary>Makes newName a second name of an existing file.</summary>
    public static void HardLink(string newName, string existingName)
    {
        if (!CreateHardLinkW(newName, existingName, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"could not hard-link {newName} to {existingName}");
    }
}
