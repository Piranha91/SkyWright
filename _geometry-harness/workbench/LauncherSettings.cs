using System.Text.Json;

namespace Workbench;

/// <summary>What free mode remembers between sessions: the MO2 instance, the profile, the game folder, the Output Folder
/// and a chosen Work Folder. Kept in <see cref="FileName"/> beside the workbench's exe. <c>Controller</c> is the run engine
/// an older workbench remembered (python or csharp): it is read only to say that the choice is gone, and never written.</summary>
internal sealed record LauncherSettings(string? Instance, string? Profile, string? Game, string? Output, string? WorkFolder,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Controller = null)
{
    internal const string FileName = "workbench-settings.json";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The file beside the running exe (WB_SETTINGS names another, for a test or a demonstration).</summary>
    internal static string DefaultPath => Environment.GetEnvironmentVariable("WB_SETTINGS") is { Length: > 0 } p ? p : Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>The remembered settings, or null when none are saved; a file that cannot be read is said, never ignored.</summary>
    internal static (LauncherSettings? Settings, string? Refusal) Load(string path)
    {
        if (!File.Exists(path)) return (null, null);
        try { return (JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path), Json), null); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { return (null, $"the saved settings at '{path}' could not be read ({ex.Message.TrimEnd('.')}), so nothing was restored."); }
    }

    /// <summary>Whether the settings file's folder can be written, said before anything is opened; null when it can.</summary>
    internal static string? CannotWrite(string path)
    {
        var probe = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, ".write-check-" + Guid.NewGuid().ToString("N"));
        try { File.WriteAllText(probe, ""); File.Delete(probe); return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return $"the workbench's folder '{Path.GetDirectoryName(path)}' is not writable, so the instance, profile and Output Folder will not be remembered for the next session."; }
    }

    /// <summary>Write the settings; returns the one sentence saying why not, or null when they were written.</summary>
    internal static string? Save(string path, LauncherSettings s)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s with { Controller = null }, Json));   // a run engine is never written
            File.Move(tmp, path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return $"the workbench's folder '{Path.GetDirectoryName(path)}' is not writable ({ex.Message.TrimEnd('.')}), so the instance, profile and Output Folder are not remembered for the next session."; }
    }
}
