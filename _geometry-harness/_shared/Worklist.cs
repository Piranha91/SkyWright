using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HarnessShared;

// A SHOT LIST: the handoff between "here is what I found" and "here is what you look at".
//
// One file drives two consumers that used to be maintained by hand, separately, and therefore
// drifted: viewprobe's fly-through (PageUp/PageDown steps the shots, Ctrl+PageUp/Down steps the
// cells) and the in-game console bat files (--emit-bats). Both read THESE coordinates, so the
// picture a render shows and the place a tester ends up standing cannot disagree — which they did,
// by hand, in the 2026-08-28 field run.
//
// Conventions are the game's throughout, so a `getpos`/`getangle` pair pastes straight in:
// +Y is north, yaw 0 = north and increases clockwise, POSITIVE PITCH LOOKS DOWN.

/// <summary>One camera pose, plus what it is for.</summary>
/// <param name="Label">
/// Shown in the fly-through HUD and in the bat manifest — the sentence that says why this shot is
/// worth a look. Load-bearing for a human, never matched on.
/// </param>
/// <param name="Eye">Camera EYE position, Skyrim coordinates, [X, Y, Z]. Not the floor — see FloorZ.</param>
/// <param name="Look">
/// Optional aim point. When present it wins over Yaw/Pitch, exactly as viewprobe's --look wins over
/// --yaw/--pitch, so "look at that door" can be authored without doing the trigonometry.
/// </param>
/// <param name="FloorZ">
/// Standable ground z under Eye, when it is known — from --nav-at, which is the only source that
/// answers "standable" rather than "some surface is here". The bat generator gives this to
/// player.setpos; giving it the EYE z instead buries the tester's feet in the floor. Absent means
/// "derive it as Eye.Z - eyeHeight", which is an assumption, not a measurement.
/// </param>
/// <param name="Nav">Per-shot navmesh display override (off|filled|wire|both); absent inherits the run's --nav.</param>
/// <param name="CullAbove">Per-shot dollhouse slice override; absent inherits the run's --cull-above.</param>
/// <param name="Route">
/// The path to WALK from this camera, as waypoints in order: <c>[[X, Y], [X, Y], ...]</c>, at least
/// two. X and Y ONLY — z is resolved from the winning navmesh, waypoint by waypoint, because a route
/// must sit on walkable ground rather than on whatever surface a pick happens to return, and because
/// letting the mesh supply z makes the arrows follow a sloping walkway for free. viewprobe draws it
/// as straight segments with arrowheads; --route X,Y is the same thing from the command line.
/// </param>
public sealed record WorklistShot(
    string Label,
    IReadOnlyList<float> Eye,
    float Yaw = 0f,
    float Pitch = 0f,
    float Fov = 65f,
    IReadOnlyList<float>? Look = null,
    float? FloorZ = null,
    string? Note = null,
    string? Nav = null,
    float? CullAbove = null,
    IReadOnlyList<IReadOnlyList<float>>? Route = null);

/// <summary>The shots that share one loaded cell — i.e. one load screen's worth of work.</summary>
/// <param name="Cell">CellEditorID or FormID, the same string viewprobe takes as its first argument.</param>
/// <param name="Travel">
/// Explicit console travel command (`coc SomeInterior`, `cow Tamriel -7,-21`). Normally absent:
/// the generator derives it from the cell's resolved identity, which is more trustworthy than a
/// remembered grid reference. Present only to override a derivation that is wrong or awkward.
/// </param>
public sealed record WorklistCell(
    string Cell,
    IReadOnlyList<WorklistShot> Shots,
    string? Travel = null,
    string? Note = null);

/// <param name="EyeHeight">
/// Eye height above the floor, used for shots that carry no measured FloorZ. Matches viewprobe's
/// --eye-height, whose default is the same 120.
/// </param>
public sealed record WorklistFile(
    string Label,
    IReadOnlyList<WorklistCell> Cells,
    string? Probe = null,
    string? Instance = null,
    string? GeneratedUtc = null,
    float EyeHeight = 120f);

public static class WorklistIo
{
    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,          // hand-authored files should not fail on a capital
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static void Write(string path, WorklistFile file)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(file, Opts));
    }

    /// <summary>Read and VALIDATE. A worklist that parses but is nonsense wastes a load walk, so it fails here instead.</summary>
    public static WorklistFile Read(string path)
    {
        var f = JsonSerializer.Deserialize<WorklistFile>(File.ReadAllText(path), Opts)
                ?? throw new InvalidDataException($"empty or unreadable worklist: {path}");
        Validate(f, path);
        return f;
    }

    public static void Validate(WorklistFile f, string path)
    {
        void Bad(string why) => throw new InvalidDataException($"worklist '{path}': {why}");

        if (f.Cells is null || f.Cells.Count == 0) Bad("no cells — there is nothing to look at.");
        if (f.EyeHeight is <= 0f or > 512f) Bad($"eyeHeight {f.EyeHeight} is out of range (0..512).");

        for (int ci = 0; ci < f.Cells!.Count; ci++)
        {
            var c = f.Cells[ci];
            string where = $"cell[{ci}] '{c.Cell}'";
            if (string.IsNullOrWhiteSpace(c.Cell)) Bad($"{where}: cell name is empty.");
            if (c.Shots is null || c.Shots.Count == 0) Bad($"{where}: no shots.");

            for (int si = 0; si < c.Shots!.Count; si++)
            {
                var s = c.Shots[si];
                string sw = $"{where} shot[{si}] '{s.Label}'";
                if (string.IsNullOrWhiteSpace(s.Label)) Bad($"{sw}: label is empty — the HUD and the bat manifest both show it.");
                if (s.Eye is null || s.Eye.Count != 3) Bad($"{sw}: eye must be [X, Y, Z].");
                if (s.Look is not null && s.Look.Count != 3) Bad($"{sw}: look must be [X, Y, Z].");
                if (s.Fov is <= 0f or >= 180f) Bad($"{sw}: fov {s.Fov} is out of range (0..180).");
                if (s.Pitch is < -89f or > 89f) Bad($"{sw}: pitch {s.Pitch} is out of range (-89..89).");
                if (s.Nav is not null && s.Nav is not ("off" or "filled" or "wire" or "both"))
                    Bad($"{sw}: nav '{s.Nav}' is not off|filled|wire|both.");
                if (s.Route is not null)
                {
                    // One waypoint is a position, not a path — and a three-number waypoint is an
                    // author supplying a z the navmesh is supposed to supply. Both fail HERE, before
                    // a load walk is spent on them.
                    if (s.Route.Count < 2) Bad($"{sw}: route has {s.Route.Count} waypoint(s) — a path needs at least 2.");
                    for (int ri = 0; ri < s.Route.Count; ri++)
                        if (s.Route[ri] is null || s.Route[ri].Count != 2)
                            Bad($"{sw}: route waypoint[{ri}] must be [X, Y] — z comes from the winning navmesh, not from the file.");
                }
            }
        }
    }

    /// <summary>
    /// Floor z for player.setpos. Measured FloorZ when the author had one, otherwise eye minus eye
    /// height — and the caller is expected to SAY which it used, because only one of them is evidence.
    /// </summary>
    public static (float Z, bool Measured) FloorZ(WorklistFile f, WorklistShot s)
        => s.FloorZ is { } fz ? (fz, true) : (s.Eye[2] - f.EyeHeight, false);

    /// <summary>Yaw/pitch for a shot, resolving a Look aim point the way viewprobe's --look does.</summary>
    public static (float Yaw, float Pitch) Aim(WorklistShot s)
    {
        if (s.Look is not { Count: 3 }) return (Norm(s.Yaw), s.Pitch);
        float dx = s.Look[0] - s.Eye[0], dy = s.Look[1] - s.Eye[1], dz = s.Look[2] - s.Eye[2];
        float flat = MathF.Sqrt(dx * dx + dy * dy);
        // atan2(x, y) not (y, x): yaw is a COMPASS heading off +Y, clockwise. Pitch is negated
        // because positive pitch looks DOWN, and a look point above the eye has positive dz.
        return (Norm(float.RadiansToDegrees(MathF.Atan2(dx, dy))),
                float.RadiansToDegrees(-MathF.Atan2(dz, flat)));
    }

    static float Norm(float deg) => (deg % 360f + 360f) % 360f;
}
