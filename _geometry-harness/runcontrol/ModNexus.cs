using HousecarlCore;

namespace RunControl;

/// <summary>A mod folder's Nexus id, read by the owner of MO2's meta.ini rules (Mo2ModMeta in housecarl-core).</summary>
public static class ModNexus
{
    /// <summary>The Nexus mod id in the folder's meta.ini, or null when the file is unreadable or names none.</summary>
    public static int? ModNexusId(string modFolder) =>
        Mo2ModMeta.Read(Path.Combine(modFolder, "meta.ini")) is { ModId: > 0 } meta ? meta.ModId : null;
}
