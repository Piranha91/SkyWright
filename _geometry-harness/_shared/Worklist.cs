using System.IO;
using System;
using System.Linq;
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

/// <summary>
/// A synthetic placed object for THIS shot: a base record stood where the file says, at the yaw and
/// scale the file says. It is what a runtime PlaceAtMe + SetPosition/SetAngle/SetScale produced during
/// a test, put back into the records-side scene so the picture the measurement was taken against and
/// the measurement itself share one frame. The ref is invented for the render (a high ID in the base's
/// own plugin) and is never written anywhere.
/// </summary>
/// <param name="Form">FormKey of the BASE record (STAT, MSTT, …), "XXXXXX:Plugin.esp". It must carry a model.</param>
/// <param name="Pos">World position [X, Y, Z] — the engine's own numbers, so z is NOT taken from the navmesh.</param>
/// <param name="Yaw">Heading in degrees, `getangle z` convention (0 = north, clockwise).</param>
/// <param name="Scale">Uniform ref scale; 1 is the record default. Collision scales with it in the engine.</param>
public sealed record WorklistProp(
    string Form,
    IReadOnlyList<float> Pos,
    float Yaw = 0f,
    float Scale = 1f,
    string? Label = null);

/// <summary>
/// One of several paths drawn over one shot, each in its own colour. Two kinds, told apart by the width
/// of a point, and the difference is the whole point of having two:
/// <list type="bullet">
/// <item><c>[X, Y]</c> — a PLANNED route: z from the winning navmesh, every waypoint clearance-checked,
/// every leg sampled onto the mesh, refused off-mesh — exactly the rule <see cref="WorklistShot.Route"/> follows.</item>
/// <item><c>[X, Y, Z]</c> — a MEASURED path: positions the engine reported (an actor's own trajectory),
/// drawn through exactly those points. Never resolved against the mesh and never refused, because the
/// mesh is what the measurement is being compared with and cannot be allowed to edit it. Samples the
/// mesh does not cover are counted and said, not hidden.</item>
/// </list>
/// A route must be all one kind.
/// </summary>
/// <param name="Color">A --route-color name (cyan, magenta, yellow, green, blue, pink) or an R,G,B triple; absent inherits --route-color.</param>
/// <param name="Discs">Translucent disc at each point; absent means the run's --no-route-discs setting for a planned route and OFF for a measured one.</param>
/// <param name="Arrows">auto | end | units-between-arrowheads, as --route-arrows; absent inherits the run's.</param>
/// <param name="Caption">The caption line for this route, in its colour. Absent: an automatic line (label, count, length, provenance). Empty string: no line — for a shot carrying many paths whose meaning one summary line already states.</param>
public sealed record WorklistRoute(
    IReadOnlyList<IReadOnlyList<float>> Points,
    string? Color = null,
    string? Label = null,
    bool? Discs = null,
    string? Arrows = null,
    string? Caption = null);

/// <summary>An NPC stood in THIS shot — the worklist form of <c>--npc FORMKEY[@X,Y,Z[,HEADING]][=label]</c>, same rules.</summary>
/// <param name="Form">The placed ACHR or the NPC_ base, "XXXXXX:Plugin.esp".</param>
/// <param name="Pos">Optional [X, Y, Z]; absent uses the ref's own winning placement, which must exist.</param>
/// <param name="Heading">Optional degrees, `getangle z` convention.</param>
public sealed record WorklistNpc(
    string Form,
    IReadOnlyList<float>? Pos = null,
    float? Heading = null,
    string? Label = null);

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
/// <param name="Props">Synthetic placed objects drawn in this shot only — see <see cref="WorklistProp"/>.</param>
/// <param name="Routes">Further paths, planned or measured, each in its own colour — see <see cref="WorklistRoute"/>. Drawn alongside <paramref name="Route"/>, which stays the one-route shorthand.</param>
/// <param name="Npcs">Actors stood in this shot only, on top of the command line's --npc — see <see cref="WorklistNpc"/>.</param>
/// <param name="Feet">
/// Where the PLAYER stands for this shot: [X, Y, Z] at the FLOOR — the point the planner's placement
/// test passed, which shotplan.py writes for every shot it emits. A runner takes its spot from here
/// rather than typing a coordinate at the storey's height: a multi-storey interior's floor can be a
/// ring round a void, and a typed spot dropped a player 765 u onto the storey below (2026-09-03).
/// Absent means "the eye's X,Y at FloorZ"; present says so explicitly. The render ignores it.
/// </param>
/// <param name="Doors">
/// FormKeys ("XXXXXX:Plugin.esp") of the Door refs near this shot's subjects, for a runner to close
/// (ObjectReference.SetOpen false) before driving the camera. The records say a door is closed; the
/// game can leave its leaf standing open across the only clear bearing (2026-09-03). Written by
/// shotplan.py from --refs-at. The render ignores it.
/// </param>
/// <param name="Planes">Per-shot occlusion-plane layer override (viewprobe's --planes); absent inherits the run's.</param>
/// <param name="Occlusion">Per-shot occlusion preview override (off|on|diff, viewprobe's --occlusion); absent inherits the run's.</param>
/// <param name="Aspect">
/// Per-shot CAPTURE CANVAS SHAPE, width / height (viewprobe's --aspect); absent inherits the run's,
/// which is square unless the command line said otherwise. Fov is the VERTICAL angle, so this
/// widens the horizontal extent and leaves the vertical alone — 65 at 1.7778 is 65 deg tall by
/// 97.11 wide. A shot planned for a non-square frame and rendered without this gets viewprobe's
/// square, which holds the vertical angle and crops about a third off the horizontal, so the
/// picture stops matching the framing that qualified the pose and nothing errors (Piranha,
/// 2026-09-15). Absent is NOT 1.0 recorded: it is "this shot never said", so it INHERITS THE RUN'S
/// setting — square by default, or whatever --aspect asked for — which is what keeps every worklist
/// written before this field rendering exactly as it always did.
/// </param>
public sealed record WorklistShot(
    string Label,
    IReadOnlyList<float> Eye,
    float Yaw = 0f,
    float Pitch = 0f,
    float Fov = 65f,
    float? Aspect = null,
    IReadOnlyList<float>? Look = null,
    float? FloorZ = null,
    string? Note = null,
    string? Nav = null,
    float? CullAbove = null,
    IReadOnlyList<IReadOnlyList<float>>? Route = null,
    IReadOnlyList<WorklistProp>? Props = null,
    IReadOnlyList<WorklistRoute>? Routes = null,
    IReadOnlyList<WorklistNpc>? Npcs = null,
    IReadOnlyList<float>? Feet = null,
    IReadOnlyList<string>? Doors = null,
    bool? Planes = null,
    string? Occlusion = null,
    string? State = null,
    string? ViewpointKind = null,
    string? ViewpointEvidence = null,
    bool Control = false);

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
        var text = File.ReadAllText(path);
        WorklistFile? f;
        try { f = JsonSerializer.Deserialize<WorklistFile>(text, Opts); }
        catch (JsonException ex) { throw new InvalidDataException($"worklist '{path}': not valid JSON — {ex.Message}"); }
        if (f is null) throw new InvalidDataException($"empty or unreadable worklist: {path}");
        // A .json handed to a probe is not necessarily a worklist — the harness emits conflict files
        // too, and since a worklist DROPPED ON THE EXE is picked out by nothing more than its
        // extension, the wrong file is the likeliest mistake anyone makes here. "no cells" is a
        // baffling thing to be told about a file that is visibly full of data, so name what arrived.
        if (f.Cells is null || f.Cells.Count == 0)
            if (Describe(text) is { } what)
                throw new InvalidDataException($"'{path}' is not a worklist — it looks like {what}.");
        Validate(f, path);
        return f;
    }

    /// <summary>Name a recognised NON-worklist shape, or null when there is nothing useful to say.</summary>
    static string? Describe(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text,
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return $"a JSON {root.ValueKind.ToString().ToLowerInvariant()}, where a worklist is an object with a \"cells\" array";
            // Case-insensitively, for the same reason Read is: these files are hand-authored as
            // often as they are generated, and the generators do not agree on casing either —
            // ConflictIo writes PascalCase, the worklists in the wild are camelCase.
            bool Has(string name) => root.EnumerateObject()
                .Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (Has("conflicts"))
                return "a detector CONFLICT file — pass it as --conflicts to paint those findings red on a cell, rather than as a shot list";
            return "JSON with no \"cells\" array";
        }
        catch { return null; }   // unparseable: Validate's own message is the honest one
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
                // An aspect of 0, a negative, or a NaN would reach the projection and the canvas
                // arithmetic and produce a degenerate frame rather than an error. Refuse it HERE,
                // before a load walk is spent on it, like every other camera number.
                if (s.Aspect is { } asp && !(asp > 0.01f && asp < 100f && float.IsFinite(asp)))
                    Bad($"{sw}: aspect {asp} is out of range (0.01..100) — it is width / height, so 16:9 is 1.7778.");
                if (s.Pitch is < -89f or > 89f) Bad($"{sw}: pitch {s.Pitch} is out of range (-89..89).");
                if (s.Nav is not null && s.Nav is not ("off" or "filled" or "wire" or "both"))
                    Bad($"{sw}: nav '{s.Nav}' is not off|filled|wire|both.");
                if (s.Occlusion is not null && s.Occlusion is not ("off" or "on" or "diff"))
                    Bad($"{sw}: occlusion '{s.Occlusion}' is not off|on|diff.");
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
                if (s.Props is not null)
                    for (int pi = 0; pi < s.Props.Count; pi++)
                    {
                        var pr = s.Props[pi];
                        if (pr is null || string.IsNullOrWhiteSpace(pr.Form)) Bad($"{sw}: prop[{pi}] has no form — the BASE record's FormKey.");
                        if (pr!.Pos is null || pr.Pos.Count != 3) Bad($"{sw}: prop[{pi}] '{pr.Form}': pos must be [X, Y, Z] — the engine's own numbers, z included.");
                        if (pr.Scale is <= 0f or > 100f) Bad($"{sw}: prop[{pi}] '{pr.Form}': scale {pr.Scale} is out of range (0..100).");
                    }
                if (s.Routes is not null)
                    for (int qi = 0; qi < s.Routes.Count; qi++)
                    {
                        var rt = s.Routes[qi];
                        string rl = rt?.Label ?? $"route {qi + 1}";
                        if (rt is null || rt.Points is null || rt.Points.Count < 2)
                            Bad($"{sw}: routes[{qi}] '{rl}' has {rt?.Points?.Count ?? 0} point(s) — a path needs at least 2.");
                        // One width per route: a 2-wide point is a plan the mesh completes, a 3-wide one is a
                        // measurement the mesh must not touch, and a route cannot be half of each.
                        int width = rt!.Points[0]?.Count ?? 0;
                        if (width is not (2 or 3)) Bad($"{sw}: routes[{qi}] '{rl}' point[0] must be [X, Y] (planned) or [X, Y, Z] (measured).");
                        for (int pi = 0; pi < rt.Points.Count; pi++)
                            if (rt.Points[pi] is null || rt.Points[pi].Count != width)
                                Bad($"{sw}: routes[{qi}] '{rl}' point[{pi}] has {rt.Points[pi]?.Count ?? 0} number(s); point[0] has {width}, and a route is all planned or all measured.");
                        if (rt.Arrows is not null && rt.Arrows is not ("auto" or "end") && !(float.TryParse(rt.Arrows, out var au) && au > 0f))
                            Bad($"{sw}: routes[{qi}] '{rl}': arrows '{rt.Arrows}' is not auto|end|<units>.");
                    }
                if (s.Npcs is not null)
                    for (int ni = 0; ni < s.Npcs.Count; ni++)
                    {
                        var np = s.Npcs[ni];
                        if (np is null || string.IsNullOrWhiteSpace(np.Form)) Bad($"{sw}: npcs[{ni}] has no form.");
                        if (np!.Pos is not null && np.Pos.Count != 3) Bad($"{sw}: npcs[{ni}] '{np.Form}': pos must be [X, Y, Z].");
                    }
                if (s.Feet is not null && s.Feet.Count != 3) Bad($"{sw}: feet must be [X, Y, Z] at the floor.");
                if (s.Doors is not null)
                    for (int di = 0; di < s.Doors.Count; di++)
                        if (string.IsNullOrWhiteSpace(s.Doors[di]) || !s.Doors[di].Contains(':'))
                            Bad($"{sw}: doors[{di}] must be a FormKey \"XXXXXX:Plugin.esp\".");
            }
        }
    }

    /// <summary>
    /// Concatenate several worklists into ONE run, in the order given — what a multi-file drop on
    /// the exe means. EyeHeight has to AGREE across them: it is the file-wide fallback that places
    /// every shot carrying no measured floorZ, so quietly adopting the first file's value would
    /// move the other file's cameras off the ground they were authored against. One file in, that
    /// same file back out, untouched.
    /// </summary>
    public static WorklistFile Merge(IReadOnlyList<(string Path, WorklistFile File)> loaded)
    {
        if (loaded.Count == 0) throw new InvalidDataException("no worklists to merge.");
        if (loaded.Count == 1) return loaded[0].File;

        var first = loaded[0];
        foreach (var (p, wl) in loaded.Skip(1))
            if (wl.EyeHeight != first.File.EyeHeight)
                throw new InvalidDataException(
                    $"cannot merge worklists with different eyeHeight: '{Path.GetFileName(first.Path)}' says "
                  + $"{first.File.EyeHeight}, '{Path.GetFileName(p)}' says {wl.EyeHeight}. That number places every "
                  + "camera without a measured floorZ, so one of the two sets of shots would come out wrong. "
                  + "Open them one at a time.");

        return new WorklistFile(
            Label: string.Join(" + ", loaded.Select(l => l.File.Label)),
            Cells: loaded.SelectMany(l => l.File.Cells).ToList(),
            Probe: first.File.Probe,
            Instance: first.File.Instance,
            GeneratedUtc: first.File.GeneratedUtc,
            EyeHeight: first.File.EyeHeight);
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
