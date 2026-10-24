namespace LeftBehind;

// THE RUN PIN — the real run reads exactly the instance, game and profile it was told to, or refuses.
//
// A machine's harness config may point at another instance entirely, so nothing here falls back to it: all three are
// required on the command line. The game directory must be the one the MO2 instance itself is set up for (its
// ModOrganizer.ini gamePath), the profile must be the one both order files name as their BaseProfile, and, when
// --require-instance is given, the instance must be that directory.

public static class RunPin
{
    public static string Norm(string p) => Path.GetFullPath(p).Replace('\\', '/').TrimEnd('/');

    /// <summary>The game directory an MO2 instance is configured for, from its ModOrganizer.ini; null when unreadable.</summary>
    public static string? InstanceGamePath(string instance)
    {
        var ini = Path.Combine(instance, "ModOrganizer.ini");
        if (!File.Exists(ini)) return null;
        foreach (var raw in File.ReadLines(ini))
        {
            var line = raw.Trim();
            if (!line.StartsWith("gamePath=", StringComparison.OrdinalIgnoreCase)) continue;
            var v = line["gamePath=".Length..];
            if (v.StartsWith("@ByteArray(", StringComparison.Ordinal) && v.EndsWith(')')) v = v["@ByteArray(".Length..^1];
            return v.Replace("\\\\", "\\");
        }
        return null;
    }

    /// <summary>Null when the pin holds, else the refusal.</summary>
    public static string? Check(string? instance, string? game, string? profile, string? requireInstance,
                                IReadOnlyList<(string File, string BaseProfile)> orders)
    {
        if (instance is null || game is null || profile is null)
            return "the real run needs --instance, --game and --profile on the command line (no fallback to the harness config)";
        if (requireInstance is not null && !string.Equals(Norm(instance), Norm(requireInstance), StringComparison.OrdinalIgnoreCase))
            return $"--instance {Norm(instance)} is not the required instance {Norm(requireInstance)}";
        var cfgGame = InstanceGamePath(instance);
        if (cfgGame is null) return $"cannot read gamePath from {Path.Combine(instance, "ModOrganizer.ini")}";
        if (!string.Equals(Norm(game), Norm(cfgGame), StringComparison.OrdinalIgnoreCase))
            return $"--game {Norm(game)} is not the game directory this instance is configured for ({Norm(cfgGame)})";
        foreach (var (f, bp) in orders)
            if (HarnessShared.HarnessOrderCli.ProfileMismatch(bp, f, profile) is { } mismatch)
                return mismatch;
        if (!Directory.Exists(Path.Combine(instance, "profiles", profile))) return $"profile folder {profile} does not exist in the instance";
        return null;
    }
}
