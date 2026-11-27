using System.Numerics;

namespace SkyWright.Engine;

// The lighting check's data (2026-11-26, Gate B): what one arrangement of a load order shows in one scene, read by lightprobe's
// --check mode and judged by LightCheck. Plugin names are file names (case-insensitive); reference keys are FormKey strings.

/// <summary>Which side of the evaluated order a plugin is on: the dependencies (base content), the selection, or the rest.</summary>
public enum LightSide { Base, Selection, Rest }

/// <summary>One emitting part of a placed reference, already placed in the world (a box: corner and three edges).</summary>
public sealed record LightCheckEmitter(string Part, string Kind, double? Value, Vector3 Origin, Vector3 EdgeX, Vector3 EdgeY, Vector3 EdgeZ);

/// <summary>One placed reference in one scene of one arrangement. <paramref name="On"/>: enabled at load, resolved and not deleted.
/// <paramref name="HasModel"/>: its base names a model (a light with a model hosts itself).</summary>
public sealed record LightCheckRef(string Key, string Type, string? Edid, bool HasModel, Vector3 Pos, float Scale, bool On)
{
    /// <summary>Its base's record bounds (OBND extents); half their diagonal times the scale is how far an unreadable model could reach.</summary>
    public Vector3? Obnd { get; init; }
    /// <summary>Deleted in this arrangement (On is then false).</summary>
    public bool Deleted { get; init; }
    /// <summary>Its emitting parts (null when it has none or they were not read).</summary>
    public IReadOnlyList<LightCheckEmitter>? Emitters { get; init; }
    /// <summary>Why its emitting parts could not be read (null when read).</summary>
    public string? EmitterStatus { get; init; }
    /// <summary>The plugin whose record places it where it stands in this arrangement.</summary>
    public string? Responsible { get; init; }
    /// <summary>Every plugin of this arrangement carrying a record of it, in load order (the defining plugin first).</summary>
    public IReadOnlyList<string> Touchers { get; init; } = Array.Empty<string>();
    /// <summary>The plugins carrying a record of its enable parent, in load order (empty when it has none).</summary>
    public IReadOnlyList<string> ParentTouchers { get; init; } = Array.Empty<string>();
    /// <summary>A subject of this scene (false for an exterior neighbour cell's reference, read only as a surface or a context light).</summary>
    public bool Subject { get; init; } = true;
}

/// <summary>One placed light in one scene of one arrangement, with lightprobe's readings when it is enabled and geometry was read.</summary>
public sealed record LightCheckLight(string Key, string? Edid, bool Enabled, Vector3 Pos, float Radius, bool Shadow)
{
    public FacingResult? Facing { get; init; }
    public double Lit { get; init; }
    public double Leak { get; init; }
    /// <summary>References whose model could not be read and whose bounds the light's radius reaches.</summary>
    public IReadOnlyList<string> Incomplete { get; init; } = Array.Empty<string>();
    /// <summary>Back-face first hits per surface owner (reference key).</summary>
    public IReadOnlyDictionary<string, int> BackByRef { get; init; } = new Dictionary<string, int>();
    /// <summary>Lit (unblocked) area per surface owner (reference key).</summary>
    public IReadOnlyDictionary<string, double> LitByRef { get; init; } = new Dictionary<string, double>();
    public bool Subject { get; init; } = true;
}

/// <summary>One lit shape in one scene of one arrangement and the lights that reach it (lightprobe's count by placed box).</summary>
public sealed record LightCheckShape(string Ref, int Index, string? Name, int Count, int Shadow, int Strict, IReadOnlyList<string> Lights)
{
    public bool Subject { get; init; } = true;
}

/// <summary>One arrangement's reading of one scene. An arrangement that does not have the scene at all is an empty reading.</summary>
public sealed class LightCheckReading
{
    public LightCheckReading(IEnumerable<string> plugins) => Plugins = new HashSet<string>(plugins, StringComparer.OrdinalIgnoreCase);
    /// <summary>Every plugin this arrangement loads.</summary>
    public HashSet<string> Plugins { get; }
    public Dictionary<string, LightCheckRef> Refs { get; } = new();
    public Dictionary<string, LightCheckLight> Lights { get; } = new();
    public Dictionary<(string Ref, int Index), LightCheckShape> Shapes { get; } = new();
    /// <summary>Light Limit Fix is installed in this arrangement's files (its count decides the over-limit reading).</summary>
    public bool LlfInstalled { get; set; }
    /// <summary>Surfaces this reading could not include (an unreadable terrain cell): every light reading here is then not judged.</summary>
    public List<string> SurfaceGaps { get; } = new();
    public LightCheckReading Add(LightCheckRef r) { Refs[r.Key] = r; return this; }
    public LightCheckReading Add(LightCheckLight l) { Lights[l.Key] = l; return this; }
    public LightCheckReading Add(LightCheckShape s) { Shapes[(s.Ref, s.Index)] = s; return this; }
}

/// <summary>Which side each plugin is on and the mod (as Mod Organizer names it) that provides it.</summary>
public sealed class LightCheckSides
{
    readonly Dictionary<string, LightSide> _side;
    readonly Dictionary<string, string> _mod;
    public LightCheckSides(IReadOnlyDictionary<string, LightSide> side, IReadOnlyDictionary<string, string> mod)
    {
        _side = new(side, StringComparer.OrdinalIgnoreCase);
        _mod = new(mod, StringComparer.OrdinalIgnoreCase);
    }
    public IReadOnlyDictionary<string, LightSide> Side => _side;
    public LightSide Of(string? plugin) => plugin is not null && _side.TryGetValue(plugin, out var s) ? s : LightSide.Base;
    /// <summary>The mod that provides the plugin, or the plugin's own name when no mod folder does (the game's Data folder).</summary>
    public string ModOf(string plugin) => _mod.TryGetValue(plugin, out var m) && m.Length > 0 ? m : plugin;
    public IEnumerable<string> PluginsOn(LightSide s) => _side.Where(kv => kv.Value == s).Select(kv => kv.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
}

/// <summary>A mod named in a row: the mod as Mod Organizer shows it, and the plugin whose record is meant.</summary>
public sealed record LightCheckMod(string Mod, string Plugin);

/// <summary>One row: a subject (a light, a fixture or a shape) in one scene, in one class. Verdict "listed" (a clash only the
/// combination makes), "cannot tell" (the over-limit count) or "not judged" (with <see cref="Reason"/>).</summary>
public sealed record LightCheckRow(string Class, string Verdict, string Scene, string Subject)
{
    public string? SubjectEdid { get; init; }
    public string? Fixture { get; init; }
    public string? FixtureEdid { get; init; }
    public int? ShapeIndex { get; init; }
    public string? ShapeName { get; init; }
    public LightCheckMod? SubjectMod { get; init; }
    public IReadOnlyList<LightCheckMod> OtherMods { get; init; } = Array.Empty<LightCheckMod>();
    /// <summary>The sentence a modder reads.</summary>
    public string Summary { get; init; } = "";
    /// <summary>For "not judged": what went wrong and what to try, one sentence. Otherwise the reading's own label.</summary>
    public string? Reason { get; init; }
    /// <summary>Where the view opens (the light, the fixture or the shape's reference).</summary>
    public Vector3 At { get; init; }
    /// <summary>Where the fixture stood in the subject's own arrangement, when the combination no longer has it there (removed,
    /// disabled or moved): the view marks that spot, since the scene shows the combination. Null otherwise.</summary>
    public Vector3? FixtureAt { get; init; }
    public bool? Shadow { get; init; }
    public IReadOnlyDictionary<string, object?> Readings { get; init; } = new Dictionary<string, object?>();
}

/// <summary>Per class: how many subjects it was evaluated on, and what each read.</summary>
public sealed class LightCheckCount
{
    public int Subjects { get; set; }
    public int Listed { get; set; }
    public int NotThePair { get; set; }
    public int NoClash { get; set; }
    public int NotJudged { get; set; }
    /// <summary>Of <see cref="Listed"/>: the lights that also read "lights nothing" (finding 4: the app may list them once, in that class,
    /// as the registry's class entry says). A fact of the reading; counted here so the class's subjects still add up.</summary>
    public int AlsoLightsNothing { get; set; }
    public void Add(LightCheckCount o) { Subjects += o.Subjects; Listed += o.Listed; NotThePair += o.NotThePair; NoClash += o.NoClash; NotJudged += o.NotJudged; AlsoLightsNothing += o.AlsoLightsNothing; }
}

public sealed record LightCheckSceneResult(string Scene, IReadOnlyList<LightCheckRow> Rows, IReadOnlyDictionary<string, LightCheckCount> Counts);

/// <summary>The check's parameters (the deployed fixture rule's H and emit threshold).</summary>
public sealed record LightCheckOptions(double HostRadius = LightCheck.HostRadius, double EmitThreshold = LightReach.EmitThreshold);
