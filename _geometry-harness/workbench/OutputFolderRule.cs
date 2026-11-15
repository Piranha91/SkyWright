using HousecarlCore;

namespace Workbench;

/// <summary>The folders of one MO2 instance and its game that the workbench's folder rules compare against, read through
/// housecarl-core (<see cref="Mo2Instance.LayoutRoot"/> honours the ini's base_directory). Paths compare as resolved full
/// paths, case-insensitively; a junction is not followed.</summary>
internal sealed record InstanceLayout(string Instance, string Root, string Mods, string Profiles, string Overwrite, string Game, string Data)
{
    internal static InstanceLayout Of(string instance, string game)
    {
        var inst = Full(instance);
        var root = Full(Mo2Instance.LayoutRoot(inst));
        var g = Full(game);
        return new InstanceLayout(inst, root, Path.Combine(root, "mods"), Path.Combine(root, "profiles"), Path.Combine(root, "overwrite"), g, Path.Combine(g, "Data"));
    }

    internal static string Full(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    internal static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    internal static bool AtOrUnder(string path, string dir) => Same(path, dir)
        || path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal bool InsideInstance(string p) => AtOrUnder(p, Instance) || AtOrUnder(p, Root);
}

/// <summary>Where the workbench may write patches: the instance's mods
/// folder itself (each patch then goes to a new "houseCARL - name" folder there), or any folder outside the instance and
/// the game folder. <see cref="Check"/> returns null when the folder may take output, else one sentence saying why not.</summary>
internal static class OutputFolderRule
{
    /// <summary>The rule's one owner is housecarl-core's <see cref="OutputFolderPolicy"/>, which the server's out_path lane
    /// also calls, so the workbench and the server cannot disagree.</summary>
    internal static string? Check(string output, string instance, string profile, string game) =>
        OutputFolderPolicy.Refusal(output, Mo2OutputLayout.Of(instance, profile, game));
}

/// <summary>Where a manual run keeps its own files (entry, candidates, plan, receipts, bundle, logs, re-checks): never
/// inside the instance or the game folder. With the Output Folder outside the instance the default is
/// its "workbench-runs" subfolder; with the Output Folder at the instance's mods folder there is no default.</summary>
internal static class WorkFolderRule
{
    internal static string? Check(string work, string instance, string profile, string game)
    {
        if (string.IsNullOrWhiteSpace(work) || !Path.IsPathFullyQualified(work.Trim()))
            return $"'{work}' is not a full path; choose the Work Folder by its full path.";
        var w = InstanceLayout.Full(work.Trim());
        if (File.Exists(w)) return $"'{w}' is a file, not a folder; choose a folder as the Work Folder.";
        var l = InstanceLayout.Of(instance, game);
        if (l.InsideInstance(w))
            return $"'{w}' is inside the MO2 instance, where MO2 would list or load the run's files; choose a Work Folder outside the instance.";
        if (InstanceLayout.AtOrUnder(w, l.Game))
            return $"'{w}' is inside the game folder; choose a Work Folder outside the game folder.";
        return null;
    }

    internal static string Default(string output, string instance)
    {
        var o = InstanceLayout.Full(output);
        var inst = InstanceLayout.Full(instance);
        var root = InstanceLayout.Full(Mo2Instance.LayoutRoot(inst));
        if (InstanceLayout.AtOrUnder(o, inst) || InstanceLayout.AtOrUnder(o, root))
            throw new InvalidOperationException("the Output Folder is inside the MO2 instance, so there is no default Work Folder; choose a Work Folder outside the instance and the game folder before running.");
        return Path.Combine(o, "workbench-runs");
    }
}
