using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HarnessShared;
using HousecarlCore;

// ViewMeshGaps: the meshes a scene arm's plugins name that its asset view lacks while an installed folder outside the
// view has them (a bare-plugin variant listed without its parent mod). Runs inside the scene preflight, so `evalrun plan`
// and every scene capture refuse such an arm before anything is measured. A mesh no installed folder has is left to the
// missing-assets check.
static class ViewMeshGaps
{
    internal const string Code = "view-lacks-installed-meshes";

    /// <summary>Throws <see cref="SceneCapture.PreflightRefusal"/> when the arm's plugins name meshes the view lacks and an
    /// installed folder outside it would serve if added at the end of the candidate's Mods list; returns otherwise.</summary>
    internal static void Check(SceneCapture.AssetPreflight preflight, string profileDir, string modsDir, string dataDir,
                               string overwriteDir, string gamePath)
    {
        var candidate = CandidateFile.Read(preflight.CandidatePath);
        var plugins = candidate.Plugins;
        var viewMods = preflight.Providers.Select(p => p.Mo2Name).ToList();
        var inView = new HashSet<string>(viewMods, StringComparer.OrdinalIgnoreCase);

        // Each plugin's winning copy, by the precedence the probes load it with; only a mod folder's copy is walked.
        var layer = HarnessOrderCli.PluginLayerMap(profileDir, viewMods, modsDir, dataDir, overwriteDir);
        var walked = new List<(string Plugin, string Folder, HashSet<string> Paths)>();
        foreach (var name in plugins)
        {
            if (!layer.TryGetValue(name, out var path)) continue;
            var folder = ModFolderOf(path, modsDir);
            if (folder is null) continue;
            walked.Add((name, folder, ModelPaths(name, folder, path, dataDir, inView)));
        }
        if (walked.All(w => w.Paths.Count == 0)) return;

        var archives = ArchiveDiscovery.DiscoverFor(plugins, viewMods, profileDir, modsDir, dataDir, overwriteDir, gamePath);
        using var resolver = AssetResolver.Build(overwriteDir, modsDir, dataDir, viewMods, archives.Archives);
        var view = resolver.Capture();
        var misses = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);   // path -> plugins naming it
        foreach (var w in walked)
            foreach (var hit in view.ResolveMany(w.Paths))
                if (!hit.Exists)
                {
                    var key = Key(hit.RelPath);
                    if (!misses.TryGetValue(key, out var naming)) misses[key] = naming = new List<string>();
                    if (!naming.Contains(w.Plugin, StringComparer.OrdinalIgnoreCase)) naming.Add(w.Plugin);
                }
        if (misses.Count == 0) return;

        // Installed folders the Mods list leaves out, strongest first; a miss goes to the first one that would serve it.
        var servedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in ModlistRows(profileDir).Where(f => !inView.Contains(f)))
        {
            var dir = Path.Combine(modsDir, folder);
            if (!Directory.Exists(dir)) continue;
            var open = misses.Keys.Where(k => !servedBy.ContainsKey(k)).ToList();
            if (open.Count == 0) break;
            foreach (var rel in open)
                if (File.Exists(Path.Combine(dir, rel))) servedBy[rel] = folder;
            var extended = new List<string>(viewMods) { folder };
            var withFolder = ArchiveDiscovery.DiscoverFor(plugins, extended, profileDir, modsDir, dataDir, overwriteDir, gamePath);
            foreach (var a in withFolder.Archives.Where(a => string.Equals(a.OwningMod, folder, StringComparison.OrdinalIgnoreCase)))
            {
                var table = ArchiveTable(a.Path);
                if (table is null) continue;   // an outside archive that will not read serves nothing
                foreach (var rel in open)
                    if (!servedBy.ContainsKey(rel) && table.Contains(rel)) servedBy[rel] = folder;
            }
        }
        if (servedBy.Count == 0) return;

        var namingPlugins = walked.Where(w => servedBy.Keys.Any(k => misses[k].Contains(w.Plugin, StringComparer.OrdinalIgnoreCase)))
                                  .ToList();
        var folders = ModlistRows(profileDir).Where(f => servedBy.Values.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
        int n = servedBy.Count;
        string Named((string Plugin, string Folder, HashSet<string> _) w) =>
            $"'{w.Plugin}' (from '{w.Folder}'{(inView.Contains(w.Folder) ? "" : ", a folder the Mods list leaves out")})";
        string who = namingPlugins.Count == 1
            ? $"plugin {Named(namingPlugins[0])} names"
            : $"plugins {List(namingPlugins.Take(3).Select(Named).ToList(), namingPlugins.Count - 3)} name";
        string what = n == 1 ? "1 mesh that" : $"{n} meshes that";
        string where = view.ReadIncomplete
            ? $"no readable folder in the candidate's Mods provides ({Unread(view)})"
            : "no folder in the candidate's Mods provides";
        string folderList = List(folders.Select(f => $"'{f}'").ToList(), 0);
        string has = folders.Count == 1
            ? $"installed folder {folderList}, which the Mods list leaves out, has {(n == 1 ? "it" : "them")}"
            : $"installed folders {folderList}, which the Mods list leaves out, have {(n == 1 ? "it" : "them")}";
        throw new SceneCapture.PreflightRefusal(Code, $"{who} {what} {where}, and {has}",
            $"add {folderList} at the end of the candidate's Mods list, so the arm's own folders keep priority, then try again");
    }

    /// <summary>The distinct Data-relative model paths a plugin's records name; a plugin that will not read is refused.</summary>
    static HashSet<string> ModelPaths(string plugin, string folder, string path, string dataDir, HashSet<string> inView)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        object? mod = null;
        try
        {
            var overlay = LoadOrderResolver.OpenOverlay(path, dataDir);
            mod = overlay;
            foreach (var rec in overlay.EnumerateMajorRecords())
            {
                if (rec is not IModeledGetter { Model: { } model }) continue;
                string? rel = null;
                try { rel = model.File.DataRelativePath.Path; } catch { /* an unset link names nothing */ }
                if (!string.IsNullOrWhiteSpace(rel)) paths.Add(Key(rel));
            }
        }
        catch (Exception ex)
        {
            string from = inView.Contains(folder) ? $"'{folder}'" : $"'{folder}', a folder the Mods list leaves out";
            throw new SceneCapture.PreflightRefusal("plugin-unreadable",
                $"plugin '{plugin}' (from {from}) could not be read to list the meshes it names: {Because(ex)}",
                "repair or replace that plugin, or drop it from the candidate's Plugins");
        }
        finally { (mod as IDisposable)?.Dispose(); }
        return paths;
    }

    static HashSet<string>? ArchiveTable(string archivePath)
    {
        try
        {
            var reader = Archive.CreateReader(GameRelease.SkyrimSE, archivePath);
            try { return new HashSet<string>(reader.Files.Select(f => Key(f.Path)), StringComparer.OrdinalIgnoreCase); }
            finally { (reader as IDisposable)?.Dispose(); }
        }
        catch { return null; }
    }

    /// <summary>The modlist.txt folder rows, strongest first, enabled or not; separators skipped (SceneCapture's rule).</summary>
    static List<string> ModlistRows(string profileDir)
    {
        var rows = new List<string>();
        foreach (var raw in File.ReadAllLines(Path.Combine(profileDir, "modlist.txt")))
        {
            var line = raw.TrimEnd();
            if (line.Length < 2 || line[0] is not ('+' or '-')) continue;
            var name = line[1..].Trim();
            if (name.Length == 0 || name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase)) continue;
            rows.Add(name);
        }
        return rows;
    }

    static string? ModFolderOf(string path, string modsDir)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(modsDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        var first = full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first.Length > 0 ? first : null;
    }

    static string Unread(AssetResolver.AssetView view)
    {
        var bsa = view.BsaFailures.FirstOrDefault();
        if (bsa is not null)
        {
            int cut = bsa.IndexOf(" (loaded by ", StringComparison.Ordinal);
            return $"archive '{(cut > 0 ? bsa[..cut] : bsa)}' in the view could not be read";
        }
        return "a folder in the view could not be listed";
    }

    /// <summary>'a', 'a and b', 'a, b and c'; with <paramref name="more"/> &gt; 0, 'a, b, c and N more'.</summary>
    static string List(IReadOnlyList<string> items, int more)
    {
        var all = more > 0 ? items.Append($"{more} more").ToList() : items.ToList();
        return all.Count switch
        {
            0 => "",
            1 => all[0],
            _ => string.Join(", ", all.Take(all.Count - 1)) + " and " + all[^1],
        };
    }

    static string Key(string rel) => rel.Replace('/', '\\').TrimStart('\\');

    static string Because(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "access denied",
        IOException => "an I/O error",
        _ => $"it is not a readable plugin ({ex.GetType().Name})",
    };
}
