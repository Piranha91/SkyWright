using System.Text.Json;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HarnessShared;

// THE MESH REACH (PlacedMeshReach) (fold-review-fix/GATE-A.md section 1): on one arm, the placed references whose base's model a delta mod folder
// provides, and of those the ones whose model that folder's copy wins with and changes (MeshResponsibility's Credited). It is the
// folder rule's own test (overlapprobe MeshRule.CreditedOwner, contactprobe's Owner) applied to every reference of the index, so
// the registry's placedMeshes trigger holds exactly when a probe has something to credit. Which files a folder provides is the
// population's reading of it (PopulationScope.ModFilesByFolder); which archive the arm loads is the arm's asset view
// (ResolvedConfiguration.Archives). Nothing here lists a directory. It counts references of every type: each probe then applies
// its own type and enable gates.
public static class PlacedMeshReach
{
    public const int Schema = 1;

    sealed class Folder
    {
        public long References, Credited, EqualBytes, NoFallback, Unreadable, NotLoaded;
        public readonly SortedDictionary<string, long> OverriddenBy = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<object> Examples = new();
    }

    static bool IsMesh(string rel) => rel.StartsWith("meshes\\", StringComparison.Ordinal) && rel.EndsWith(".nif", StringComparison.Ordinal);
    static bool IsTexture(string rel) => rel.StartsWith("textures\\", StringComparison.Ordinal);
    static bool IsArchive(string rel) => !rel.Contains('\\') && (rel.EndsWith(".bsa", StringComparison.Ordinal) || rel.EndsWith(".ba2", StringComparison.Ordinal));

    /// <summary>The reach as the JSON object scope/mesh-reach.json holds. <paramref name="unreadablePlugins"/> are the plugins pass 1
    /// could not read (their references are missing, so a zero is not a measured zero).</summary>
    public static Dictionary<string, object?> Measure(OverlapIndex idx, PopulationScope population, MeshResponsibility owners,
                                                      ResolvedConfiguration cfg, AssetResolver assets, IReadOnlyList<string> unreadablePlugins,
                                                      string arm)
    {
        var folders = population.Mods.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var per = folders.ToDictionary(f => f, _ => new Folder(), StringComparer.OrdinalIgnoreCase);
        var why = new List<string>();
        foreach (var (key, snaps) in idx.History.Refs.OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal))
        {
            if (snaps.Count == 0 || snaps[^1].Deleted || !idx.RefBase.TryGetValue(key, out var b)) continue;
            if (MeshResponsibility.ModelPathOf(idx, b) is not { } path) continue;
            string norm = PopulationScope.NormalMesh(path);
            var providers = folders.Where(f => population.ModFilesByFolder.TryGetValue(f, out var files) && files.Contains(norm)).ToList();
            if (providers.Count == 0) continue;
            var o = owners.Of(path);
            foreach (var f in providers)
            {
                var c = per[f];
                c.References++;
                bool mine = string.Equals(o.Folder, f, StringComparison.OrdinalIgnoreCase);
                switch (o.Shape)
                {
                    case MeshShape.Credited when mine:
                        c.Credited++;
                        if (c.Examples.Count < 5)
                            c.Examples.Add(new Dictionary<string, object?>
                            {
                                ["reference"] = key.ToString(), ["base"] = b.ToString(), ["model"] = path,
                                ["cell"] = idx.CellName.TryGetValue(snaps[^1].Cell, out var cn) ? cn.Edid ?? snaps[^1].Cell.ToString() : snaps[^1].Cell.ToString(),
                            });
                        break;
                    case MeshShape.EqualBytes when mine: c.EqualBytes++; break;
                    case MeshShape.NoFallback when mine: c.NoFallback++; break;
                    case MeshShape.Unreadable when mine: c.Unreadable++; break;
                    case MeshShape.GameOrMissing: c.NotLoaded++; break;
                    default:
                        // another folder's copy wins over this one: credited to that folder, or a folder this run does not credit
                        string winner = o.Folder ?? "?";
                        c.OverriddenBy[winner] = c.OverriddenBy.GetValueOrDefault(winner) + 1;
                        break;
                }
            }
        }
        if (unreadablePlugins.Count > 0) why.Add($"{unreadablePlugins.Count} plugin(s) could not be read ({string.Join(", ", unreadablePlugins.Take(3))})");
        if (assets.ReadIncomplete) why.Add("an asset root or archive of the view could not be read");
        long unreadable = per.Values.Sum(c => c.Unreadable);
        if (unreadable > 0) why.Add($"{unreadable} placed reference(s) use a mesh whose copies could not be read");
        var loaded = cfg.Archives.Archives;
        var rows = new List<object>();
        foreach (var f in folders)
        {
            var c = per[f];
            var files = population.ModFilesByFolder.TryGetValue(f, out var fs) ? fs : new HashSet<string>();
            var held = files.Where(IsArchive).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var on = loaded.Where(a => string.Equals(a.OwningMod, f, StringComparison.OrdinalIgnoreCase))
                           .Select(a => Path.GetFileName(a.Path)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            rows.Add(new Dictionary<string, object?>
            {
                ["name"] = f, ["meshFiles"] = files.LongCount(IsMesh), ["textureFiles"] = files.LongCount(IsTexture),
                ["archives"] = new Dictionary<string, object?> { ["held"] = held, ["loaded"] = on },
                ["references"] = c.References, ["credited"] = c.Credited, ["equalBytes"] = c.EqualBytes,
                ["overriddenBy"] = c.OverriddenBy.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                ["noFallback"] = c.NoFallback, ["unreadable"] = c.Unreadable, ["notLoaded"] = c.NotLoaded,
                ["examples"] = c.Examples,
            });
        }
        return new Dictionary<string, object?>
        {
            ["schema"] = Schema, ["probe"] = "overlapprobe --mesh-reach", ["arm"] = arm,
            ["configuration"] = cfg.Identity("load-time", "baseline").Short,
            ["population"] = new Dictionary<string, object?> { ["file"] = population.FilePath, ["sha256"] = population.Sha256 },
            ["complete"] = why.Count == 0, ["why"] = why,
            ["folders"] = rows,
            ["references"] = per.Values.Sum(c => c.References), ["credited"] = per.Values.Sum(c => c.Credited),
            ["note"] = "placed references of every type whose base's model a delta folder provides (the population's S2 test on the "
                     + "model the folder rule reads); credited = the folder's copy wins and its bytes differ from the copy below it",
        };
    }

    public static void Write(string path, Dictionary<string, object?> doc)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
