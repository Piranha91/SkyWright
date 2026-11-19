using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Order;

namespace Workbench;

/// <summary>A game's Data folder as a load order viewprobe, the load-order reader and the houseCARL server already
/// take: an instance-shaped folder under the Work Folder (a profile holding <c>plugins.txt</c>, <c>loadorder.txt</c>,
/// an empty <c>modlist.txt</c> and <c>Skyrim.ini</c>; empty <c>mods</c> and <c>overwrite</c>), with the game folder as
/// the game. Every plugin and archive then resolves from Data, which is the whole world when Mod Organizer 2 started
/// the workbench (its virtual folder), when a tool deployed the mods into Data, and for a plain install.</summary>
internal static class DataFolderOrder
{
    internal const string StagedProfile = "Data";

    /// <summary>True when Mod Organizer 2's virtual file system is loaded in this process: MO2 started the workbench.</summary>
    internal static bool UnderMo2 => GetModuleHandle("usvfs_x64.dll") != IntPtr.Zero;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

    /// <summary>The game folder a path names: the folder holding <c>Data/Skyrim.esm</c>, given itself, its Data folder,
    /// or a file directly in either; null when the path is none of those.</summary>
    internal static string? GameOf(string path)
    {
        var dir = File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path))! : Path.GetFullPath(path);
        dir = dir.TrimEnd('\\', '/');
        if (File.Exists(Path.Combine(dir, "Data", "Skyrim.esm"))) return dir;
        if (File.Exists(Path.Combine(dir, "Skyrim.esm")) && string.Equals(Path.GetFileName(dir), "Data", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(dir);
        return null;
    }

    /// <summary>Which game the folder holds, by its executable: VR, the GOG edition, else Special Edition.</summary>
    internal static GameRelease ReleaseOf(string game) =>
        File.Exists(Path.Combine(game, "SkyrimVR.exe")) ? GameRelease.SkyrimVR
        : File.Exists(Path.Combine(game, "Galaxy64.dll")) ? GameRelease.SkyrimSEGog
        : GameRelease.SkyrimSE;

    /// <summary>What a staged order was made from, said in the window.</summary>
    internal sealed record Staged(LoadOrderSpec Order, string Root, int Plugins, IReadOnlyList<string> Notes);

    /// <summary>Stage <paramref name="game"/>'s Data folder under <paramref name="work"/>. The plugin lists are the two
    /// files beside each other in <paramref name="listsDir"/> when both are there (Mod Organizer 2's profile, which is
    /// also what its virtual file system shows at the game's own list location); else the order the game itself would
    /// load, from Mutagen's listing (the base game, Creation Club and <c>plugins.txt</c>).</summary>
    internal static Staged Stage(string game, string work, string? listsDir = null, string? iniDir = null)
    {
        game = Path.GetFullPath(game).TrimEnd('\\', '/');
        var data = Path.Combine(game, "Data");
        if (!File.Exists(Path.Combine(data, "Skyrim.esm")))
            throw new InvalidOperationException($"'{game}' is not a Skyrim game folder: its Data folder holds no Skyrim.esm; choose the game folder or its Data folder.");
        var release = ReleaseOf(game);
        var notes = new List<string>();
        string tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(game.ToLowerInvariant())))[..12].ToLowerInvariant();
        var root = Path.Combine(Path.GetFullPath(work), "staged", tag);
        var prof = Path.Combine(root, "profiles", StagedProfile);
        Directory.CreateDirectory(prof);
        Directory.CreateDirectory(Path.Combine(root, "mods"));
        Directory.CreateDirectory(Path.Combine(root, "overwrite"));

        listsDir ??= Path.GetDirectoryName(PluginListings.GetListingsPath(release));
        string? po = listsDir is null ? null : Path.Combine(listsDir, "plugins.txt"), lo = listsDir is null ? null : Path.Combine(listsDir, "loadorder.txt");
        int count;
        if (po is not null && lo is not null && File.Exists(po) && File.Exists(lo))
        {
            // both lists are there: a mod manager's own pair, read by the same rule houseCARL reads an MO2 profile with
            File.Copy(po, Path.Combine(prof, "plugins.txt"), overwrite: true);
            File.Copy(lo, Path.Combine(prof, "loadorder.txt"), overwrite: true);
            count = File.ReadAllLines(lo).Count(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'));
            notes.Add($"the load order is the plugins.txt and loadorder.txt in '{listsDir}'" + (UnderMo2 ? " (Mod Organizer 2's profile, seen through its virtual file system)" : ""));
        }
        else
        {
            // one list or none: the order the game itself loads (base game, Creation Club, then plugins.txt), from Mutagen
            List<ILoadOrderListingGetter> listing;
            try { listing = LoadOrder.GetLoadOrderListings(release, data, throwOnMissingMods: false).ToList(); }
            catch (Exception ex) when (ex is IOException or ArgumentException)
            { throw new InvalidOperationException($"the load order of '{game}' could not be read ({ex.Message.TrimEnd('.')}); start the game's launcher once so it writes plugins.txt, or open an MO2 profile instead."); }
            var present = listing.Where(l => File.Exists(Path.Combine(data, l.FileName))).ToList();
            File.WriteAllLines(Path.Combine(prof, "loadorder.txt"), present.Select(l => l.FileName));
            File.WriteAllLines(Path.Combine(prof, "plugins.txt"), present.Select(l => (l.Enabled ? "*" : "") + l.FileName));
            count = present.Count(l => l.Enabled);
            notes.Add($"the load order is the one the game loads: the base game and Creation Club plugins, then the game's own plugins.txt ({count} plugin(s) on)");
            if (listing.Count > present.Count) notes.Add($"{listing.Count - present.Count} listed plugin(s) are not in the Data folder and are left out");
        }
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# staged by the workbench: a Data folder has no mod folders\n");

        // the archive list: the INI the game reads (My Games), else the game folder's own, which the archive reader falls back to
        iniDir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", MyGamesName(release));
        var ini = new[] { Path.Combine(iniDir, "Skyrim.ini"), Path.Combine(iniDir, "SkyrimVR.ini") }.FirstOrDefault(File.Exists);
        var staged = Path.Combine(prof, "Skyrim.ini");
        if (ini is not null) File.Copy(ini, staged, overwrite: true);
        else
        {
            if (File.Exists(staged)) File.Delete(staged);
            notes.Add($"no Skyrim.ini was found in '{iniDir}', so the base archives are read from the game folder's own Skyrim.ini");
        }
        var order = new LoadOrderSpec(root, StagedProfile, game, null, $"the Data folder of '{game}'");
        return new Staged(order, root, count, notes);
    }

    static string MyGamesName(GameRelease r) => r switch
    {
        GameRelease.SkyrimVR => "Skyrim VR",
        GameRelease.SkyrimSEGog => "Skyrim Special Edition GOG",
        _ => "Skyrim Special Edition",
    };

    /// <summary>The game folder to open when Mod Organizer 2 started the workbench with no arguments: the working
    /// folder when it is the game's, else the game the MO2 that started this process manages (its ModOrganizer.ini),
    /// else the installed game Mutagen finds. Null when MO2 did not start the workbench, or none of those names one.</summary>
    internal static string? StartGame()
    {
        if (!UnderMo2) return null;
        if (GameOf(Environment.CurrentDirectory) is { } here) return here;
        foreach (var exe in ParentExes())
        {
            if (!string.Equals(Path.GetFileName(exe), "ModOrganizer.exe", StringComparison.OrdinalIgnoreCase)) continue;
            var inst = Path.GetDirectoryName(exe)!;
            if (Mo2Instance.TryResolve(inst, out var paths) && paths is not null && GameOf(paths.GamePath) is { } g) return g;
        }
        foreach (var r in new[] { GameRelease.SkyrimSE, GameRelease.SkyrimSEGog, GameRelease.SkyrimVR })
            if (Mutagen.Bethesda.Installs.GameLocations.TryGetGameFolder(r, out var dir) && GameOf(dir.Path) is { } found) return found;
        return null;
    }

    /// <summary>The executables of this process's parents, nearest first (at most six), where they can be read.</summary>
    internal static IReadOnlyList<string> ParentExes()
    {
        var list = new List<string>();
        try
        {
            var byPid = new Dictionary<int, int>();
            IntPtr snap = CreateToolhelp32Snapshot(2, 0);
            var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snap, ref pe)) do { byPid[(int)pe.th32ProcessID] = (int)pe.th32ParentProcessID; } while (Process32Next(snap, ref pe));
            CloseHandle(snap);
            int pid = Environment.ProcessId;
            for (int depth = 0; depth < 6 && byPid.TryGetValue(pid, out var parent) && parent != 0; depth++)
            {
                pid = parent;
                try { if (Process.GetProcessById(pid).MainModule?.FileName is { } f) list.Add(f); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
        return list;
    }

    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 pe);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 pe);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
}
