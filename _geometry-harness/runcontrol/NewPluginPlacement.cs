namespace RunControl;

/// <summary>Where the plugins of a newly enabled mod folder land in the load order. This is Mod Organizer 2's
/// behaviour, stated here only: no probe or core reader owns it yet (it belongs in the engine library when one
/// exists). MO2 appends a newly enabled plugin to the end of the load order; the game loads every master-flagged
/// plugin before any other, so a master-flagged one goes to the end of the masters.</summary>
public static class NewPluginPlacement
{
    /// <summary>The plugin files of the switched-on folders that the profile does not already load: weakest folder
    /// first, in name order within a folder, each plugin once.</summary>
    public static List<string> Added(string modsDir, IEnumerable<string> switchedOnStrongestFirst, IEnumerable<string> active, PluginNames names)
    {
        var have = active.Select(PyText.Lower).ToHashSet(StringComparer.Ordinal);
        var added = new List<string>();
        foreach (string mod in switchedOnStrongestFirst.Reverse())
        {
            string folder = PyPath.Join(modsDir, mod);
            var files = Directory.Exists(folder)
                ? PyOrder.SortedByLowerKey(Directory.GetFiles(folder).Where(PluginFiles.IsPlugin).Select(PyPath.Name))
                : [];
            foreach (string f in Py.Strs(names.Many(files)))
                if (have.Add(PyText.Lower(f))) added.Add(f);
        }
        return added;
    }

    /// <summary>The load order with the added plugins placed: the master-flagged ones after the last master the
    /// order already loads, the others at the end. `loaded` is the order as resolved, added plugins included.</summary>
    public static List<string> Place(IReadOnlyList<string> loaded, IReadOnlyCollection<string> added, Func<string, bool> isMaster)
    {
        var fresh = loaded.Where(added.Contains).ToList();
        var kept = loaded.Where(p => !added.Contains(p)).ToList();
        int last = kept.FindLastIndex(p => isMaster(p));
        return kept.Take(last + 1).Concat(fresh.Where(isMaster)).Concat(kept.Skip(last + 1)).Concat(fresh.Where(p => !isMaster(p))).ToList();
    }
}
