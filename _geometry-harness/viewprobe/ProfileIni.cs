namespace ViewProbe;

/// <summary>A setting from the profile's game INI files, read the way the game reads them: Skyrim.ini, then
/// SkyrimPrefs.ini, a key the later file sets winning. The profile folder is the one the grass reads
/// (<see cref="GrassLayer.ProfileDir"/>).</summary>
internal static class ProfileIni
{
    /// <summary>The value of <paramref name="key"/> in <paramref name="section"/>, with the file it came from, or null when no
    /// file sets it (or the profile folder is unknown).</summary>
    internal static (string Value, string File)? Read(string section, string key)
    {
        if (GrassLayer.ProfileDir is not { } dir || !Directory.Exists(dir)) return null;
        (string, string)? found = null;
        foreach (var name in new[] { "Skyrim.ini", "SkyrimPrefs.ini" })
        {
            var file = Directory.EnumerateFiles(dir, "*.ini").FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
            if (file is null) continue;
            bool inSection = false;
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line.StartsWith('[')) { inSection = string.Equals(line, $"[{section}]", StringComparison.OrdinalIgnoreCase); continue; }
                int eq = line.IndexOf('=');
                if (inSection && eq > 0 && string.Equals(line[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase))
                    found = (line[(eq + 1)..].Trim(), name);
            }
        }
        return found;
    }

    /// <summary>A whole-number setting, or <paramref name="gameDefault"/> when no file sets it (or sets something that is not
    /// a whole number); <paramref name="source"/> says which, in words.</summary>
    internal static int Int(string section, string key, int gameDefault, out string source)
    {
        if (Read(section, key) is { } v && int.TryParse(v.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n))
        { source = $"{key}={n} from {v.File}"; return n; }
        source = $"{key} not set in the profile's INI files: the game's default {gameDefault}";
        return gameDefault;
    }
}
