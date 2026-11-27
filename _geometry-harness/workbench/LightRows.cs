using System.Numerics;
using System.Text.Json;

namespace Workbench;

/// <summary>What a lighting row adds to a Conflicts row (environment.lighting, Gate C; run folder C:/hc-runs/2026-11-26-lighting-check):
/// its class and verdict, the scene, the mods in a modder's words, where the view opens and where a gone fixture stood.</summary>
internal sealed record LightDetail(string Class, string ClassTitle, string Verdict, string Scene, string SceneKind, string Space, int? X, int? Y,
                                   Vector3? At, Vector3? FixtureAt, string? Fixture, string? FixtureEdid, string? SubjectEdid, string? SubjectMod,
                                   IReadOnlyList<string> OtherMods, string? Reason)
{
    /// <summary>How many of the 64 checks from a stranded light hit a surface (finding 4), when measured.</summary>
    internal int? Hits { get; init; }
}

/// <summary>Where a lighting row opens the 3D view: the reference to land on in its cell (or the exterior grid), the point to look
/// at, the spot a gone fixture stood (a second marker), or one sentence why the row does not open.</summary>
internal sealed record LightViewTarget(string? RefKey, string? Cell, string? Worldspace, (int X, int Y)? Grid, Vector3? At, Vector3? FixtureAt, string? WhyNot);

/// <summary>Reads the lighting check's output (lightprobe --check, skywright.light-check/1) into Conflicts rows. Which classes are
/// listed as clashes is the registry's data (the check's <c>classes</c>, read from the bundle's own copy of the registry), never
/// this code's: an admitted class's rows are listed; a held class's rows are counted and shown as one line saying what it waits on;
/// the over-limit count is a count ("cannot tell"); not-judged rows are listed with their reason, whatever their class.</summary>
internal static class LightRows
{
    internal const string CheckId = "environment.lighting";
    internal const string SceneClass = "scene-not-judged";

    /// <summary>One class's admission as the registry states it.</summary>
    internal sealed record ClassRule(string Id, string Title, bool Admitted, bool Provisional, bool Count, string? Held)
    {
        /// <summary>Finding 4 and the owner's preliminary ruling (2026-11-26): what the app does with a light that reaches nothing in the
        /// combination (the lights-nothing reading, stranded or not). "count": counted, not listed as a clash, one line, the entries
        /// on the Findings tab to tidy; "list": listed in the lights-nothing class. Either way a stranded light that also reaches
        /// nothing is one entry, with its fixture fact in the sentence. Absent: "list".</summary>
        internal string? WhenItLightsNothing { get; init; }
        internal bool CountOnly => WhenItLightsNothing == "count";
    }

    /// <summary>The check's classes from a registry file (the bundle's copy); one sentence when the row or its classes are missing.</summary>
    internal static IReadOnlyList<ClassRule> Classes(string registryPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(registryPath));
        var row = doc.RootElement.GetProperty("checks").EnumerateArray().FirstOrDefault(c => c.GetProperty("id").GetString() == CheckId);
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("classes", out var cls) || cls.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"the bundle's registry has no classes for {CheckId}, so which lighting rows are clashes is not known; open a bundle this SkyWright made.");
        static bool B(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
        return cls.EnumerateArray().Select(c => new ClassRule(c.GetProperty("id").GetString()!, c.GetProperty("title").GetString()!, B(c, "admitted"),
                    B(c, "provisional"), B(c, "count"), c.TryGetProperty("held", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null)
                    { WhenItLightsNothing = c.TryGetProperty("whenItLightsNothing", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null }).ToList();
    }

    /// <summary>The rows a modder reads, and the lines for the check's status: one per held class with rows, and the over-limit count.</summary>
    internal static (List<ConflictRow> Rows, List<string> Lines) Read(string jsonPath, IReadOnlyList<ClassRule> classes, string evidenceStrength) =>
        ReadAll(jsonPath, classes, evidenceStrength) is var (rows, lines, _) ? (rows, lines) : default;

    /// <summary>As <see cref="Read"/>, with the entries counted and not listed (lights that reach nothing, under "count").</summary>
    internal static (List<ConflictRow> Rows, List<string> Lines, List<ConflictRow> Counted) ReadAll(string jsonPath, IReadOnlyList<ClassRule> classes, string evidenceStrength)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var root = doc.RootElement;
        if (!root.TryGetProperty("identity", out var id) || id.GetProperty("schema").GetString() != "skywright.light-check/1")
            throw new InvalidOperationException($"'{Path.GetFileName(jsonPath)}' is not the lighting check's output (skywright.light-check/1); re-run the scan.");
        var rule = classes.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var rows = new List<ConflictRow>();
        var held = new Dictionary<string, int>(StringComparer.Ordinal);
        int overLimit = 0, mergedInto = 0;
        var counted = new List<ConflictRow>();
        const string Stranded = "light-no-fixture", Nothing = "light-lights-nothing";
        // one light, one entry: a stranded light that also reaches nothing is its lights-nothing entry (finding 4), whatever the setting
        bool merge = rule.TryGetValue(Nothing, out var nr) && nr.Admitted;
        bool countOnly = rule.TryGetValue(Nothing, out var nc) && nc.CountOnly;
        static JsonElement Rd(JsonElement e) => e.TryGetProperty("readings", out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
        static bool True(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
        static string? S(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int? I(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        static Vector3? V(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 3
            ? new Vector3(v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle()) : null;
        static (string? Mod, string? Plugin) Mod(JsonElement e) => e.ValueKind == JsonValueKind.Object ? (S(e, "mod"), S(e, "plugin")) : (null, null);
        foreach (var r in root.GetProperty("rows").EnumerateArray())
        {
            string cls = S(r, "class")!, verdict = S(r, "verdict")!;
            bool sceneRow = cls == SceneClass;
            if (!sceneRow && !rule.TryGetValue(cls, out _))
                throw new InvalidOperationException($"the lighting output has a row of class '{cls}', which the bundle's registry does not list; open it with the SkyWright that made it.");
            if (verdict == "cannot tell") { overLimit++; continue; }
            // one light, one entry (finding 4): a stranded light that also reaches nothing is its lights-nothing entry, whatever else
            if (merge && cls == Stranded && verdict == "listed" && True(Rd(r), "alsoLightsNothing")) { mergedInto++; continue; }
            if (verdict == "listed" && !rule[cls].Admitted) { held[cls] = held.GetValueOrDefault(cls) + 1; continue; }
            if (verdict == "listed" && rule[cls].Count) { overLimit++; continue; }
            var subjectMod = r.TryGetProperty("subjectMod", out var sm) ? Mod(sm) : (null, null);
            var others = r.TryGetProperty("otherMods", out var om) && om.ValueKind == JsonValueKind.Array ? om.EnumerateArray().Select(Mod).ToList() : new();
            string? summary = S(r, "summary");
            if (merge && cls == Nothing && verdict == "listed" && S(Rd(r), "strandedSummary") is { } both)
            {
                summary = both;
                if (Rd(r).TryGetProperty("strandedOtherMods", out var so) && so.ValueKind == JsonValueKind.Array)
                    others = others.Concat(so.EnumerateArray().Select(Mod)).DistinctBy(m => m.Plugin).ToList();
            }
            string subject = S(r, "subject") ?? "";
            string sceneId = S(r, "sceneId")!;
            var detail = new LightDetail(cls, sceneRow ? "A scene not judged" : rule[cls].Title, verdict, S(r, "scene") ?? sceneId, S(r, "kind") ?? "interior",
                S(r, "space") ?? sceneId, I(r, "x"), I(r, "y"), V(r, "at"), V(r, "fixtureAt"), S(r, "fixture"), S(r, "fixtureEdid"), S(r, "subjectEdid"),
                subjectMod.Mod, others.Select(o => o.Mod ?? o.Plugin ?? "").ToList(), S(r, "reason")) { Hits = I(Rd(r), "hits") };
            string kind = verdict == "not judged" ? "LIGHT NOT JUDGED" : "LIGHT " + cls.ToUpperInvariant();
            string identity = $"{kind}|{sceneId}|{subject}";
            var row = new ConflictRow(identity, CheckId, kind, subjectMod.Plugin ?? "", others.FirstOrDefault().Plugin ?? "", summary ?? "",
                subject, S(r, "fixture"), null, S(r, "cell"), null, detail.SubjectEdid, null, detail.FixtureEdid, evidenceStrength, null) { Light = detail };
            if (countOnly && cls == Nothing && verdict == "listed") counted.Add(row); else rows.Add(row);
        }
        // the stranded class first and, within it, the lights that reach more surfaces first (finding 4); every other row in the output's order
        int Hits(ConflictRow x) => x.Light?.Hits ?? 0;
        rows = rows.Select((x, i) => (x, i)).OrderBy(p => p.x.Light!.Class == Stranded ? 0 : 1).ThenBy(p => p.x.Light!.Class == Stranded ? -Hits(p.x) : p.i)
                   .Select(p => p.x).ToList();
        var lines = new List<string>();
        if (countOnly && counted.Count > 0)
            lines.Add($"{counted.Count} light(s) that now reach nothing were left out: they reach no surface, so no player can see them"
                      + (mergedInto > 0 ? $" ({mergedInto} of them also left with no fixture)" : "") + "; they are on the Findings tab to tidy");
        else if (mergedInto > 0)
            lines.Add($"{mergedInto} light(s) left with no fixture also light nothing, so each is listed once, under a light that lights nothing");
        foreach (var c in classes.Where(c => held.ContainsKey(c.Id)))
            lines.Add($"held: {c.Title.ToLowerInvariant()} ({held[c.Id]}), not shown as clashes; it waits on: {c.Held}");
        if (overLimit > 0)
            lines.Add($"cannot tell: {overLimit} surface(s) lit by more lights from both sides than the game shows at once; whether this shows in game is not yet known");
        return (rows, lines, counted);
    }

    /// <summary>Where a lighting row opens the view (a strict test reads this): the row's subject in its cell, looking at its
    /// point, with the gone fixture's spot marked; a scene row with no subject opens nowhere and says why.</summary>
    internal static LightViewTarget Target(ConflictRow row)
    {
        if (row.Light is not { } l) throw new ArgumentException("not a lighting row", nameof(row));
        (int, int)? grid = l.X is int x && l.Y is int y ? (x, y) : null;
        string? ws = l.SceneKind == "exterior" ? l.Space : null;
        if (string.IsNullOrEmpty(row.RefKey))
            return new(null, row.Cell, ws, grid, l.At, null, l.Class == SceneClass
                ? "this row stands for a whole scene that was not judged, so there is no one light to open; the reason is in the row"
                : "this row names no light, fixture or surface to open");
        if (row.Cell is null)
            return new(row.RefKey, null, ws, grid, l.At, l.FixtureAt, "the scan did not record which cell this light stands in, so the view cannot open it");
        return new(row.RefKey, row.Cell, ws, grid, l.At, l.FixtureAt, null);
    }
}
