using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Workbench;

/// <summary>The four lists of the Results tab.</summary>
internal enum ResultGroup { Addressed, NotAddressed, PatchChange, NotScored }

/// <summary>One value of a results row, with the plugin it comes from when the data names one.</summary>
internal sealed record ResultValue(string Label, string? Plugin, string Text);

/// <summary>Where a row can be shown: a cell and the record to frame in it (a navmesh row names its navmesh), or one sentence why not.</summary>
internal sealed record ResultPlace(string? Cell, string? RefKey, string? OtherKey, bool InPersistentCell, string? WhyNot);

/// <summary>What viewprobe takes to build one setup: --instance, --profile, --game and --order.</summary>
internal sealed record ResultSetup(string Instance, string Profile, string Game, string OrderFile);

/// <summary>One row of the Results tab: a scored finding, a patch change nothing predicted, or a predicted overlap the scorecard does not score.</summary>
internal sealed record ResultRow(
    int Index, ResultGroup Group, string Pair,
    string Lane, string Kind, string RecordType,
    string FormKey, string? Field, string? EditorId, string? BaseEditorId, string? BaseType,
    int? Category, string? Rule,
    string? Attribution,
    string Summary,
    IReadOnlyList<ResultValue> Values,
    ResultPlace Place,
    string DefaultSetup,
    IReadOnlyList<string> JoinedEdits,
    string? Note,
    string? Where = null)
{
    /// <summary>Unique within the evaluation: the pair, the source list (f findings, e edits, o overlaps) and the position in it.</summary>
    internal string Key => $"{Pair}|{(Lane == "edit" ? "e" : Lane == "overlap" ? "o" : "f")}{Index}";
}

/// <summary>A pair's counts as the result file states them; NotScored is null until the overlap list has been read.</summary>
internal sealed record ResultCounts(int Addressed, int NotAddressed, int PatchChange, int Category3, int Category4, int Category5, int? NotScored);

/// <summary>Reading for the results reader: every file is opened for reading only and shared for reading only.</summary>
internal static class ResultFiles
{
    internal static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    internal static byte[] Read(string path) { using var fs = Open(path); return Read(fs); }

    internal static byte[] Read(FileStream fs) { var b = new byte[fs.Length]; fs.ReadExactly(b); return b; }

    internal static string Sha(ReadOnlySpan<byte> b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    // a UTF-8 byte order mark is hashed with the file and skipped by the parser
    internal static JsonDocument Json(byte[] b) => JsonDocument.Parse(b.AsMemory(b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0));

    internal static string? S(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static JsonElement? O(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    internal static IEnumerable<JsonElement> A(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    internal static string Slash(string p) => p.Replace('\\', '/').TrimEnd('/');

    internal static string Native(string p) => p.Replace('/', Path.DirectorySeparatorChar);
}

/// <summary>What the results folder itself says about every pair: where the runs are, the game, and the overlap lists' pins.</summary>
internal sealed record ResultSources(
    string Folder,
    IReadOnlyDictionary<string, string> PatchedRuns, string? PatchedRunsWhy,
    IReadOnlyDictionary<string, byte[]> Entries,
    string? Game, string? GameWhy,
    IReadOnlyDictionary<string, string> OverlapPins, string? OverlapPinsWhy,
    Func<string, string?>? Locate);

/// <summary>One pair of a finished held-out evaluation: its counts from the result file, and its rows read on demand.</summary>
internal sealed class ResultPair
{
    sealed record Finding(string Id, string Lane, IReadOnlyList<(string Key, string Role)> Subjects, string? Field, bool ActedOn);
    sealed record Edit(string FormKey, string Kind, string RecordType, int Category, string? Rule, IReadOnlyList<string> Joined, string? Cell);
    sealed record SourceRow(string A, string B, string Summary, string? Lost, string? Winner, string? Overridden);
    sealed record ProbeRecord(string FormKey, string? Label, string? Physical, string? PhysicalDetail);
    sealed record ProbeRow(string Kind, string A, string B, string Summary, IReadOnlyList<ProbeRecord> Necessary, IReadOnlyList<ProbeRecord> Alternative);
    sealed record TouchedCell(string? FormKey, string? EditorId, string? Name, string? Grid, string? Worldspace);
    sealed record EditDetail(string? EditorId, string? BaseType, string? BaseEditorId, TouchedCell? Cell,
        string? BeforePlugin, IReadOnlyList<(string Path, string? Text)>? Before, string? PatchPlugin, IReadOnlyList<(string Path, string? Text)>? Patch,
        string? Move, string? Turn, string? Disabled, string? Deltas);

    const string PersistentWhy = "this reference is filed in its worldspace's persistent cell, so the results alone do not say which cell it stands in";
    const string PersistentCellWhy = "this is its worldspace's persistent cell, which is not built as one cell";

    static ResultPlace At(string cell, string? refKey, string? other = null) => new(cell, refKey, other, false, null);
    static ResultPlace Nowhere(string why, string? refKey = null, string? other = null, bool persistent = false) => new(null, refKey, other, persistent, why);
    const string EditListNote = "edit values are read from the patched run's edit list, which the receipt does not pin";
    const string SameValues = "the edit list holds the same value before and after the patch for this edit, so it does not show what the patch changed";
    const int ManyLeaves = 12;

    readonly object _state = new(), _load = new();
    readonly List<string> _notes = new();
    readonly List<(string File, string Sha256)> _checked = new();
    readonly IReadOnlyList<Finding> _findings;
    readonly IReadOnlyList<Edit> _edits;
    readonly ResultCounts _counts;
    readonly string _manifestSha, _arm;
    readonly IReadOnlyDictionary<string, (string Path, string Sha)> _geometry;
    readonly string? _sealedRecorded, _overlapPin, _overlapPinWhy;
    readonly HashSet<string> _subject, _partner;
    IReadOnlyList<ResultRow>? _rows;
    int? _notScored;

    internal string Id { get; }
    internal IReadOnlyList<string> Subject { get; }
    internal IReadOnlyList<string> Partner { get; }
    internal string PatchPlugin { get; }
    /// <summary>The run that made the predictions, or null when it is not on disk.</summary>
    internal string? SealedRun { get; }
    /// <summary>The run scored with the patch in the load order, or null when it is not on disk.</summary>
    internal string? PatchedRun { get; }
    internal ResultSetup? Before { get; }
    internal ResultSetup? After { get; }
    internal ResultCounts Counts { get { lock (_state) return _counts with { NotScored = _notScored }; } }
    /// <summary>What is unavailable for this pair and why, one sentence each.</summary>
    internal IReadOnlyList<string> Notes { get { lock (_state) return _notes.ToList(); } }
    /// <summary>Every file <see cref="LoadRows"/> hashed against its pin.</summary>
    internal IReadOnlyList<(string File, string Sha256)> Checked { get { lock (_state) return _checked.ToList(); } }

    /// <summary>A pair from its result file's bytes, or null when the file is not a pair's (the summary and totals files).</summary>
    internal static ResultPair? Read(string rel, byte[] bytes, ResultSources src)
    {
        using var doc = ResultFiles.Json(bytes);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("findings", out var fs) || fs.ValueKind != JsonValueKind.Array
            || !r.TryGetProperty("edits", out var es) || es.ValueKind != JsonValueKind.Array) return null;
        return new ResultPair(rel, r, src);
    }

    ResultPair(string rel, JsonElement r, ResultSources src)
    {
        Id = ResultFiles.S(r, "pair") ?? Path.GetFileNameWithoutExtension(rel);
        Subject = ResultFiles.A(r, "subject").Select(x => x.GetString()!).ToList();
        Partner = ResultFiles.A(r, "partner").Select(x => x.GetString()!).ToList();
        _subject = new(Subject, StringComparer.OrdinalIgnoreCase); _partner = new(Partner, StringComparer.OrdinalIgnoreCase);
        PatchPlugin = ResultFiles.O(r, "patch") is { } p && ResultFiles.S(p, "plugin") is { } pp ? pp : throw new FormatException("no patch plugin");
        _manifestSha = ResultFiles.S(r, "sealedBundleManifestSha256") ?? "";
        var governing = ResultFiles.O(r, "governing");
        _arm = governing is { } g && ResultFiles.S(g, "armId") is { Length: > 0 } a ? a : "baseline";
        string? setupName = governing is { } g2 ? ResultFiles.S(g2, "setup") : null;

        _findings = r.GetProperty("findings").EnumerateArray().Select(f => new Finding(
            ResultFiles.S(f, "id") ?? "", ResultFiles.S(f, "lane") ?? "",
            ResultFiles.O(f, "subjects") is { } s ? s.EnumerateObject().Select(x => (x.Name, x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString()! : "")).ToList() : new List<(string, string)>(),
            ResultFiles.S(f, "field"), f.GetProperty("actedOn").GetBoolean())).ToList();
        _edits = r.GetProperty("edits").EnumerateArray().Select(e => new Edit(
            ResultFiles.S(e, "formKey") ?? "", ResultFiles.S(e, "kind") ?? "", ResultFiles.S(e, "recordType") ?? "", e.GetProperty("category").GetInt32(),
            ResultFiles.S(e, "rule"), ResultFiles.A(e, "joined").Select(x => x.GetString()!).ToList(), ResultFiles.S(e, "cell"))).ToList();
        _counts = new ResultCounts(_findings.Count(f => f.ActedOn), _findings.Count(f => !f.ActedOn), _edits.Count(e => e.Category is 3 or 4 or 5),
            _edits.Count(e => e.Category == 3), _edits.Count(e => e.Category == 4), _edits.Count(e => e.Category == 5), null);

        var geometry = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        if (ResultFiles.O(r, "geometryMeta") is { } gm)
            foreach (var probe in gm.EnumerateObject())
                if (ResultFiles.O(probe.Value, "source") is { } so && ResultFiles.S(so, "path") is { } path && ResultFiles.S(so, "sha256") is { } sha)
                    geometry[probe.Name] = (ResultFiles.Slash(path), sha);
        _geometry = geometry;

        // the run that made the predictions: the folder above the bundle the result file read its probe rows from
        _sealedRecorded = geometry.Values.Select(v => UpTo(v.Item1, "/bundle/")).FirstOrDefault(x => x is not null);
        if (_sealedRecorded is null) _notes.Add("the result file names no sealed bundle file, so the run that made the predictions cannot be found and values, cells and predicted overlaps are not shown for this pair");
        else
        {
            var at = ResultFiles.Slash(src.Locate?.Invoke(_sealedRecorded) ?? _sealedRecorded);
            if (Directory.Exists(at)) SealedRun = at;
            else _notes.Add($"the run that made the predictions ({at}) is not on disk, so values, cells and predicted overlaps are not shown for this pair");
        }
        if (src.OverlapPins.TryGetValue(_manifestSha, out var pin)) _overlapPin = pin;
        else _overlapPinWhy = src.OverlapPinsWhy ?? "verdicts-pre-patch.json has no entry for this pair's sealed bundle, so nothing pins its overlap list";

        string run = "sc-" + Id;
        if (!src.PatchedRuns.TryGetValue(run, out var recorded))
            _notes.Add((src.PatchedRunsWhy ?? $"scoring/score-driver.log names no folder for {run}") + ", so the values before and after the patch and the two setups are not shown for this pair");
        else
        {
            var at = ResultFiles.Slash(src.Locate?.Invoke(ResultFiles.Slash(recorded)) ?? recorded);
            if (Directory.Exists(at)) { PatchedRun = at; _notes.Add(EditListNote); }
            else _notes.Add($"the patched run ({at}) is not on disk, so the values before and after the patch and the two setups are not shown for this pair");
        }
        if (PatchedRun is not null) (Before, After) = Setups(src, run, setupName);
    }

    static string? UpTo(string path, string marker) { int i = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase); return i > 0 ? path[..i] : null; }

    // The two load orders of the patched run: the one the predictions were scored on, and that one with the patch.
    (ResultSetup? Before, ResultSetup? After) Setups(ResultSources src, string run, string? setupName)
    {
        string? instance = null, entryProfile = null;
        if (src.Entries.TryGetValue(run, out var entry))
            using (var d = ResultFiles.Json(entry))
            {
                instance = ResultFiles.O(d.RootElement, "source") is { } so ? ResultFiles.S(so, "instance") : null;
                entryProfile = ResultFiles.S(d.RootElement, "profile");
            }
        if (instance is null) { _notes.Add($"scoring/entries/{run}.json is not pinned by the receipt or names no MO2 instance, so the two setups cannot be opened"); return (null, null); }
        if (src.Game is null) { _notes.Add($"{src.GameWhy ?? "machine.json names no game folder"}, so the two setups cannot be opened"); return (null, null); }

        var bundle = Path.Combine(PatchedRun!, "bundle", "evidence");
        var dir = Path.Combine(bundle, "candidates");
        if (!Directory.Exists(dir)) { _notes.Add("the patched run carries no candidates folder, so the two setups cannot be opened"); return (null, null); }
        Dictionary<string, string>? pins = null;
        var manifest = Path.Combine(bundle, "manifest.json");
        try { if (File.Exists(manifest)) pins = ManifestPins(ResultFiles.Read(manifest)); }
        catch (Exception x) when (x is JsonException or IOException or InvalidOperationException) { }
        if (pins is null) _notes.Add("the patched run's manifest.json is missing or unreadable, so its two setup files are read unchecked");

        ResultSetup? before = null, after = null;
        foreach (var file in Directory.EnumerateFiles(dir, "candidate-*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var name = "candidates/" + Path.GetFileName(file);
            try
            {
                var bytes = ResultFiles.Read(file);
                if (pins is not null && (!pins.TryGetValue(name, out var want) || !want.Equals(ResultFiles.Sha(bytes), StringComparison.OrdinalIgnoreCase)))
                { _notes.Add($"the patched run's {name} does not match that run's manifest.json, so it is not offered as a setup"); continue; }
                using var d = ResultFiles.Json(bytes);
                if (ResultFiles.O(d.RootElement, "Provenance") is not { } prov || ResultFiles.S(d.RootElement, "BaseProfile") is not { Length: > 0 } profile) continue;
                bool isBefore = setupName is not null && string.Equals(ResultFiles.S(prov, "arm"), setupName, StringComparison.OrdinalIgnoreCase);
                bool isAfter = ResultFiles.S(prov, "role") == "patched" && ResultFiles.A(prov, "keys").Any(k => string.Equals(k.GetString(), PatchPlugin, StringComparison.OrdinalIgnoreCase));
                if (!isBefore && !isAfter) continue;
                if (entryProfile is not null && !string.Equals(entryProfile, profile, StringComparison.OrdinalIgnoreCase))
                    _notes.Add($"the patched run's {name} is made for profile '{profile}', not '{entryProfile}' as scoring/entries/{run}.json says; the setup file's profile is used");
                var setup = new ResultSetup(instance, profile, src.Game, ResultFiles.Slash(file));
                if (isBefore) before ??= setup; else after ??= setup;
            }
            catch (Exception x) when (x is JsonException or IOException or InvalidOperationException) { _notes.Add($"the patched run's {name} is not readable, so it is not offered as a setup"); }
        }
        if (before is null) _notes.Add($"the patched run has no setup file for '{setupName}', so the scene before the patch cannot be opened");
        if (after is null) _notes.Add($"the patched run has no setup file that adds {PatchPlugin}, so the scene after the patch cannot be opened");
        if (!Directory.Exists(instance)) _notes.Add($"the MO2 instance {instance} is not on disk, so neither setup can be built into a scene");
        if (!Directory.Exists(src.Game)) _notes.Add($"the game folder {src.Game} is not on disk, so neither setup can be built into a scene");
        return (before, after);
    }

    // a bundle manifest's pins: evidence[].path -> sha256
    static Dictionary<string, string> ManifestPins(byte[] manifest)
    {
        using var d = ResultFiles.Json(manifest);
        var pins = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in ResultFiles.A(d.RootElement, "evidence"))
            if (ResultFiles.S(e, "path") is { } p && ResultFiles.S(e, "sha256") is { } s) pins[p] = s;
        return pins;
    }

    /// <summary>Every row of the pair, all four groups; read once and kept. The overlap list is 5 to 25 MB, so this can take seconds.</summary>
    internal IReadOnlyList<ResultRow> LoadRows(CancellationToken ct = default)
    {
        lock (_load)
        {
            if (_rows is not null) return _rows;
            var b = new Builder(this, ct);
            IReadOnlyList<ResultRow> rows;
            try { rows = b.Build(); }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException)
            { throw new InvalidOperationException($"a file of pair {Id} could not be read ({x.Message.TrimEnd('.')}), so its rows are not listed."); }
            lock (_state) { _notes.AddRange(b.Notes.Where(n => !_notes.Contains(n)).ToList()); _checked.AddRange(b.Checked); _notScored = b.NotScored; _rows = rows; }
            return rows;
        }
    }

    /// <summary>One read of the pair's rows: the files it opens, what each pin said, and the rows built from them.</summary>
    sealed class Builder
    {
        readonly ResultPair _p;
        readonly CancellationToken _ct;
        internal readonly List<string> Notes = new();
        internal readonly List<(string, string)> Checked = new();
        internal int? NotScored;

        string? _bundle;                                    // the sealed bundle's folder
        Dictionary<string, string>? _pins; string? _pinsWhy; // its manifest's pins, or why they are not used
        BundleConflicts.Names? _names; string? _namesWhy;
        readonly Dictionary<string, string> _cellByEditorId = new(StringComparer.OrdinalIgnoreCase), _editorIdByCell = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _persistentCells = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, (Dictionary<(string, string), List<SourceRow>>? Rows, string? Why)> _recordRows = new(StringComparer.Ordinal);
        readonly Dictionary<string, (List<ProbeRow>? Rows, string? Why)> _probeRows = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string?>? _navCells;
        Dictionary<(string, string), EditDetail>? _editDetails; string? _editsWhy;

        internal Builder(ResultPair p, CancellationToken ct) { _p = p; _ct = ct; }

        void Note(string n) { if (!Notes.Contains(n)) Notes.Add(n); }

        internal IReadOnlyList<ResultRow> Build()
        {
            OpenSealedBundle();
            _ct.ThrowIfCancellationRequested();
            ReadEditList();
            _ct.ThrowIfCancellationRequested();

            // the patch's edits on a finding: every category 1 edit, under each finding its joined[] names
            var joined = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var e in _p._edits.Where(e => e.Category == 1))
                foreach (var id in e.Joined)
                {
                    if (!joined.TryGetValue(id, out var list)) joined[id] = list = new();
                    list.Add(JoinedLine(e));
                }

            var rows = new List<ResultRow>(_p._findings.Count + _p._edits.Count);
            var nth = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < _p._findings.Count; i++)
            {
                var f = _p._findings[i];
                int n = nth.GetValueOrDefault(f.Id); nth[f.Id] = n + 1;
                var mine = joined.TryGetValue(f.Id, out var j) ? (IReadOnlyList<string>)j : Array.Empty<string>();
                rows.Add(f.Lane == "record" ? RecordRow(i, f, n, mine) : ProbeFinding(i, f, mine));
            }
            _ct.ThrowIfCancellationRequested();
            for (int i = 0; i < _p._edits.Count; i++)
                if (_p._edits[i].Category is 3 or 4 or 5) rows.Add(EditRow(i, _p._edits[i]));
            _ct.ThrowIfCancellationRequested();
            Overlaps(rows);
            return rows;
        }

        // ---- the sealed bundle: its manifest against the result file, then names against the manifest ----

        void OpenSealedBundle()
        {
            if (_p.SealedRun is null) { _pinsWhy = _namesWhy = "the run that made the predictions is not on disk"; return; }
            _bundle = Path.Combine(_p.SealedRun, "bundle", "evidence");
            var (manifest, why) = Pinned(Path.Combine(_bundle, "manifest.json"), "the sealed bundle's manifest.json", _p._manifestSha, "the pair's result file");
            if (manifest is null) { _pinsWhy = _namesWhy = why; Note($"{why}, so the values and cells pinned by it are not shown"); return; }
            try { _pins = ManifestPins(manifest); }
            catch (JsonException) { _pinsWhy = _namesWhy = "the sealed bundle's manifest.json is not readable"; Note($"{_pinsWhy}, so the values and cells pinned by it are not shown"); return; }

            const string rel = "enrichment/names/names.json";
            var path = Path.Combine(_bundle, ResultFiles.Native(rel));
            if (!_pins.TryGetValue(rel, out var want)) _namesWhy = $"the sealed bundle's manifest.json does not list {rel}";
            else if (!File.Exists(path)) _namesWhy = $"the sealed bundle's {rel} is not on disk";
            else
            {
                // held open for reading while the workbench's names reader reads it, so what was hashed is what is read
                using var hold = ResultFiles.Open(path);
                var bytes = ResultFiles.Read(hold);
                var sha = ResultFiles.Sha(bytes);
                if (!sha.Equals(want, StringComparison.OrdinalIgnoreCase)) _namesWhy = $"the sealed bundle's {rel} does not match its manifest.json";
                else
                {
                    try
                    {
                        using (var d = ResultFiles.Json(bytes)) ReadNamesExtras(d.RootElement);
                        _names = BundleConflicts.Names.Load(path);
                        Checked.Add(("sealed bundle: " + rel, sha));
                    }
                    catch (Exception x) when (x is JsonException or KeyNotFoundException or InvalidOperationException) { _names = null; _namesWhy = $"the sealed bundle's {rel} is not readable"; }
                }
            }
            if (_names is null) Note($"{_namesWhy}, so predicted rows have no cell to open");
        }

        // what the workbench's names reader does not give: cells by editor ID, and which cells are persistent cells
        void ReadNamesExtras(JsonElement names)
        {
            if (ResultFiles.O(names, "places") is { } places)
                foreach (var pl in places.EnumerateObject())
                    if (ResultFiles.S(pl.Value, "formKey") is { } fk) { _cellByEditorId.TryAdd(pl.Name, fk); _editorIdByCell.TryAdd(fk, pl.Name); }
            foreach (var m in ResultFiles.A(names, "missing"))
                if (ResultFiles.S(m, "type") == "Cell" && ResultFiles.S(m, "editorId") is { } ed && ResultFiles.S(m, "formKey") is { } fk)
                { _cellByEditorId.TryAdd(ed, fk); _editorIdByCell.TryAdd(fk, ed); }
            if (ResultFiles.O(names, "refs") is { } refs)
                foreach (var r in refs.EnumerateObject())
                    if (ResultFiles.O(r.Value, "cells") is { } cells)
                        foreach (var c in cells.EnumerateObject())
                            if (c.Value.ValueKind == JsonValueKind.String && c.Value.GetString() is { } cell && CellRule.Persistent(cell))
                                _persistentCells.Add(cell[..cell.LastIndexOf('!')]);
        }

        // A pinned file read once: its bytes when it is on disk and hashes to its pin, else why not.
        (byte[]? Bytes, string? Why) Pinned(string full, string shown, string? want, string pinName)
        {
            if (want is null) return (null, $"{pinName} does not list {shown}");
            if (!File.Exists(full)) return (null, $"{shown} is not on disk");
            var bytes = ResultFiles.Read(full);
            var sha = ResultFiles.Sha(bytes);
            if (!sha.Equals(want, StringComparison.OrdinalIgnoreCase)) return (null, $"{shown} does not match {pinName}");
            Checked.Add((shown, sha));
            return (bytes, null);
        }

        // ---- record findings ----

        // The lost-edit rows of one record check on the scored setup, for the findings' own (record, field) pairs.
        (Dictionary<(string, string), List<SourceRow>>? Rows, string? Why) RecordRows(string check)
        {
            if (_recordRows.TryGetValue(check, out var have)) return have;
            (Dictionary<(string, string), List<SourceRow>>?, string?) got;
            var rel = $"evidence/imports/{check}__{_p._arm}/03-rows.json";
            if (_pins is null) got = (null, _pinsWhy);
            else
            {
                var (bytes, why) = Pinned(Path.Combine(_bundle!, ResultFiles.Native(rel)), "the sealed bundle's " + rel, _pins.GetValueOrDefault(rel), "the sealed bundle's manifest.json");
                if (bytes is null) { got = (null, why); Note($"{why}, so the record values it holds are not shown"); }
                else
                    try
                    {
                        var wanted = _p._findings.Where(f => f.Lane == "record" && f.Subjects.Count > 0 && f.Field is not null)
                            .Select(f => (f.Subjects[0].Key.ToLowerInvariant(), f.Field!)).ToHashSet();
                        var map = new Dictionary<(string, string), List<SourceRow>>();
                        using var d = ResultFiles.Json(bytes);
                        foreach (var c in ResultFiles.A(d.RootElement, "Conflicts"))
                        {
                            if (ResultFiles.S(c, "Kind") != "LOST-EDIT" || ResultFiles.S(c, "Field") is not { } field) continue;
                            var nec = ResultFiles.A(c, "Necessary").FirstOrDefault();
                            if (ResultFiles.S(nec, "FormKey") is not { } fk) continue;
                            var key = (fk.ToLowerInvariant(), field);
                            if (!wanted.Contains(key)) continue;
                            string a = ResultFiles.S(c, "A") ?? "", b = ResultFiles.S(c, "B") ?? "";
                            // the scoring's own rule: the loser and the winner are on opposite sides of the pair
                            if (!(_p._subject.Contains(a) && _p._partner.Contains(b)) && !(_p._partner.Contains(a) && _p._subject.Contains(b))) continue;
                            var v = ResultFiles.O(nec, "Values");
                            if (!map.TryGetValue(key, out var list)) map[key] = list = new();
                            list.Add(new SourceRow(a, b, ResultFiles.S(c, "Summary") ?? "",
                                v is { } v1 ? ResultFiles.S(v1, "lost") : null, v is { } v2 ? ResultFiles.S(v2, "winner") : null, v is { } v3 ? ResultFiles.S(v3, "overridden") : null));
                        }
                        got = (map, null);
                    }
                    catch (JsonException) { got = (null, $"the sealed bundle's {rel} is not readable"); Note($"{got.Item2}, so the record values it holds are not shown"); }
            }
            return _recordRows[check] = got;
        }

        ResultRow RecordRow(int index, Finding f, int nth, IReadOnlyList<string> joined)
        {
            string fk = f.Subjects.Count > 0 ? f.Subjects[0].Key : "", field = f.Field ?? "";
            string prefix = _p.Id + "-rec-", suffix = "-" + fk + "-" + field;
            string? check = f.Id.StartsWith(prefix, StringComparison.Ordinal) && f.Id.EndsWith(suffix, StringComparison.Ordinal) && f.Id.Length > prefix.Length + suffix.Length
                ? f.Id[prefix.Length..^suffix.Length] : null;

            SourceRow? src = null; string? note = null;
            if (check is null) note = "this finding's id does not say which check it comes from, so its values are not shown";
            else
            {
                var (rows, why) = RecordRows(check);
                if (rows is null) note = $"{why}, so this record's values are not shown";
                else if (rows.TryGetValue((fk.ToLowerInvariant(), field), out var list) && nth < list.Count) src = list[nth];
                else note = $"the sealed bundle's {check} rows hold no row for this record and field, so its values are not shown";
            }

            var named = _names?.Ref(fk);
            string mutagen = src is null ? "" : src.Summary.Split(' ', 2)[0];
            string type = RecordTypes.Code(mutagen);
            if (type.Length == 0 && named?.BaseType is { } baseType) type = baseType == "Npc" ? "ACHR" : "REFR";
            string? editorId = type == "CELL" && _editorIdByCell.TryGetValue(fk, out var ed) ? ed : null;

            var values = src is null ? Array.Empty<ResultValue>() : new[]
            {
                new ResultValue("losing mod's value", src.A, src.Lost ?? "(none)"),
                new ResultValue("winning mod's value", src.B, src.Winner ?? "(none)"),
                new ResultValue("value beneath both", null, src.Overridden ?? "(none)"),
            };
            // the record in words: its type, its editor ID (else its FormID and plugin), and its base object when it has one
            string who = $"{(type.Length > 0 ? RecordWords.Type(type) : "record")} {RecordWords.Name(editorId, named?.BaseEditorId, fk)}"
                       + (editorId is { Length: > 0 } && named?.BaseEditorId is { Length: > 0 } be ? $" ({be})" : "");
            string summary = src is null ? $"{who}: {RecordWords.Field(field)}" : $"{who}: {RecordWords.Field(field)}: {src.A}'s value loses to {src.B}'s";

            ResultPlace place = type switch
            {
                "CELL" => _persistentCells.Contains(fk) ? Nowhere(PersistentCellWhy) : At(fk, null),
                "NAVM" => NavPlace(fk, null),
                "" => Nowhere(named is null ? $"{_namesWhy ?? "the sealed bundle's names do not hold this record"}, so its cell is not known"
                                            : "the record's type is not known without the sealed bundle's rows, so its cell is not known"),
                _ when mutagen.StartsWith("Placed", StringComparison.Ordinal) || mutagen.Length == 0 => RefPlace(fk, null),
                _ => Nowhere(RecordTypes.NoPlace(type)),
            };
            return new ResultRow(index, f.ActedOn ? ResultGroup.Addressed : ResultGroup.NotAddressed, _p.Id, "record", field, type, fk, f.Field, editorId,
                named?.BaseEditorId, named?.BaseType, null, null, null, summary, values, place, "before", joined, note);
        }

        // A placed reference's place from the sealed bundle's names, with its partner's cell when its own is a persistent cell.
        ResultPlace RefPlace(string refKey, string? otherKey)
        {
            if (_names is null) return Nowhere($"{_namesWhy}, so this reference's cell is not known", refKey, otherKey);
            var r = _names.Ref(refKey); var o = otherKey is null ? null : _names.Ref(otherKey);
            if (r is null && o is null) return Nowhere("the sealed bundle's names do not hold this reference, so its cell is not known", refKey, otherKey);
            var cell = CellRule.Drawable(r?.Cell, o?.Cell) ?? CellRule.Drawable(o?.Cell, r?.Cell);
            if (cell is not null) return At(cell, refKey, otherKey);
            if (CellRule.Persistent(r?.Cell)) return Nowhere(PersistentWhy, refKey, otherKey, persistent: true);
            return Nowhere("the sealed bundle's names record no cell for this reference", refKey, otherKey);
        }

        // ---- contact and navmesh findings: the probe's own row, by its position ----

        (List<ProbeRow>? Rows, string? Why) ProbeRows(string probe)
        {
            if (_probeRows.TryGetValue(probe, out var have)) return have;
            (List<ProbeRow>?, string?) got;
            if (!_p._geometry.TryGetValue(probe, out var source)) got = (null, $"the result file names no {probe} file");
            else if (_p.SealedRun is null || _p._sealedRecorded is null || !source.Path.StartsWith(_p._sealedRecorded, StringComparison.OrdinalIgnoreCase)) got = (null, "the run that made the predictions is not on disk");
            else
            {
                var rel = source.Path[_p._sealedRecorded.Length..].TrimStart('/');
                var (bytes, why) = Pinned(Path.Combine(_p.SealedRun, ResultFiles.Native(rel)), "the sealed run's " + rel, source.Sha, "the pair's result file");
                if (bytes is null) { got = (null, why); Note($"{why}, so what that check says is not shown"); }
                else
                    try
                    {
                        using var d = ResultFiles.Json(bytes);
                        static List<ProbeRecord> Recs(JsonElement c, string k) => ResultFiles.A(c, k).Select(n => new ProbeRecord(
                            ResultFiles.S(n, "FormKey") ?? "", ResultFiles.S(n, "Label"), ResultFiles.S(n, "Physical"), ResultFiles.S(n, "PhysicalDetail"))).ToList();
                        got = (ResultFiles.A(d.RootElement, "Conflicts").Select(c => new ProbeRow(ResultFiles.S(c, "Kind") ?? "", ResultFiles.S(c, "A") ?? "", ResultFiles.S(c, "B") ?? "",
                            ResultFiles.S(c, "Summary") ?? "", Recs(c, "Necessary"), Recs(c, "Alternative"))).ToList(), null);
                    }
                    catch (JsonException) { got = (null, $"the sealed run's {rel} is not readable"); Note($"{got.Item2}, so what that check says is not shown"); }
            }
            return _probeRows[probe] = got;
        }

        // Each navmesh's cell, from the labels of the navmesh check's rows.
        Dictionary<string, string?> NavCells()
        {
            if (_navCells is not null) return _navCells;
            _navCells = new(StringComparer.OrdinalIgnoreCase);
            if (ProbeRows("navownprobe").Rows is { } rows)
                foreach (var n in rows.SelectMany(r => r.Necessary.Concat(r.Alternative)))
                    if (n.Label is not null && LabelCell(n.Label, out _) is { } cell) _navCells.TryAdd(n.FormKey, cell);
            return _navCells;
        }

        static readonly Regex LabelKey = new(@"^([0-9A-Fa-f]{6}:.+?\.es[pml])(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // A probe label starts with its cell: a FormKey, or an editor ID the names resolve.
        string? LabelCell(string label, out string named)
        {
            var m = LabelKey.Match(label);
            if (m.Success) { named = m.Groups[1].Value; return named; }
            int cut = label.IndexOf(" (", StringComparison.Ordinal);
            named = (cut < 0 ? label : label[..cut]).Trim();
            return _cellByEditorId.TryGetValue(named, out var cell) ? cell : null;
        }

        ResultPlace NavPlace(string navmesh, string? other)
        {
            if (NavCells().TryGetValue(navmesh, out var cell) && cell is not null) return At(cell, navmesh, other);
            var (rows, why) = ProbeRows("navownprobe");
            if (rows is null) return Nowhere($"{why}, so this navmesh's cell is not known", null, other);
            var label = rows.SelectMany(r => r.Necessary.Concat(r.Alternative)).FirstOrDefault(n => n.Label is not null && string.Equals(n.FormKey, navmesh, StringComparison.OrdinalIgnoreCase))?.Label;
            if (label is null) return Nowhere("no row of the navmesh check names this navmesh's cell, so there is no cell to open", null, other);
            LabelCell(label, out var named);
            return Nowhere(_names is null ? $"{_namesWhy}, so the cell '{named}' of this navmesh cannot be opened"
                : $"the navmesh check names its cell by editor ID '{named}', which the sealed bundle's names do not resolve to a cell", null, other);
        }

        ResultRow ProbeFinding(int index, Finding f, IReadOnlyList<string> joined)
        {
            var group = f.ActedOn ? ResultGroup.Addressed : ResultGroup.NotAddressed;
            bool navmesh = f.Lane == "navmesh";
            // <pair>-<probe>-<1-based position in the probe's file>
            string rest = f.Id.StartsWith(_p.Id + "-", StringComparison.Ordinal) ? f.Id[(_p.Id.Length + 1)..] : f.Id;
            int dash = rest.LastIndexOf('-');
            string probe = dash > 0 ? rest[..dash] : "";
            int position = dash > 0 && int.TryParse(rest[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

            ProbeRow? row = null; string? note = null;
            List<ProbeRow>? rows = null; string? why = "this finding's id does not name its check";
            if (probe.Length > 0) (rows, why) = ProbeRows(probe);
            if (rows is null) note = $"{why}, so what the check says about this finding is not shown";
            else if (position < 1 || position > rows.Count) note = $"the {probe} file has no row {position}, so what the check says about this finding is not shown";
            else
            {
                row = rows[position - 1];
                var named = row.Necessary.Concat(row.Alternative).Select(x => x.FormKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!named.SetEquals(f.Subjects.Select(s => s.Key)))
                { row = null; note = $"row {position} of the {probe} file does not name this finding's records, so what the check says is not shown"; }
            }

            // the record the row is about and its partner: the probe's first of each, else the finding's own roles
            string key = row?.Necessary.FirstOrDefault()?.FormKey ?? f.Subjects.Where(s => s.Role != "host").Select(s => s.Key).FirstOrDefault() ?? f.Subjects.Select(s => s.Key).FirstOrDefault() ?? "";
            string? other = row is not null ? row.Alternative.FirstOrDefault()?.FormKey : f.Subjects.Where(s => s.Role == "host").Select(s => s.Key).FirstOrDefault();

            var values = new List<ResultValue>();
            if (row is not null)
            {
                values.Add(new("what the check says", null, row.Summary));
                bool many = row.Necessary.Count > 1;
                foreach (var rec in row.Necessary)
                {
                    string Of(string text) => many ? $"{rec.FormKey}: {text}" : text;
                    if (rec.Label is { Length: > 0 }) values.Add(new("label", null, Of(rec.Label)));
                    if (rec.Physical is { Length: > 0 }) values.Add(new("physical state", null, Of(rec.Physical)));
                    if (rec.PhysicalDetail is { Length: > 0 }) values.Add(new("physical detail", null, Of(rec.PhysicalDetail)));
                }
            }

            var names = navmesh ? null : _names?.Ref(key);
            ResultPlace place;
            if (!navmesh) place = RefPlace(key, other);
            else
            {
                // the first navmesh of the row whose cell is known; a row can name navmeshes in many cells
                var keys = row is not null ? row.Necessary.Select(x => x.FormKey).ToList() : f.Subjects.Select(s => s.Key).ToList();
                var placed = keys.Select(k => NavPlace(k, other)).FirstOrDefault(pl => pl.Cell is not null);
                place = placed ?? NavPlace(key, other);
                if (placed is not null && keys.Count > 1) note ??= $"this row names {keys.Count} navmeshes; the cell offered is the one of {placed.RefKey}";
            }
            string type = navmesh ? "NAVM" : names?.BaseType == "Npc" ? "ACHR" : "REFR";
            string summary = row is not null ? Regex.Replace(row.Summary, @"\s+", " ").Trim()
                : $"{(navmesh ? "navmesh" : "placement")} finding on {RecordWords.Type(type)} {RecordWords.Form(key)}{(other is null ? "" : $" and {RecordWords.Form(other)}")}";
            return new ResultRow(index, group, _p.Id, f.Lane, row?.Kind ?? "", type, key, null, null, names?.BaseEditorId, names?.BaseType,
                null, null, null, summary, values, place, "before", joined, note);
        }

        // ---- the patch's edits ----

        // The patched run's edit list, streamed: one entry per (record, kind), holding only what a row shows.
        void ReadEditList()
        {
            if (_p.PatchedRun is null) { _editsWhy = "the patched run is not on disk"; return; }
            var rel = $"precedent-nocards/{Path.GetFileNameWithoutExtension(_p.PatchPlugin)}/edits.jsonl";
            var path = Path.Combine(_p.PatchedRun, ResultFiles.Native(rel));
            if (!File.Exists(path)) { _editsWhy = $"the patched run's {rel} is not on disk"; Note($"{_editsWhy}, so the values before and after the patch are not shown"); return; }
            var map = new Dictionary<(string, string), EditDetail>();
            int line = 0;
            try
            {
                using var reader = new StreamReader(ResultFiles.Open(path), Encoding.UTF8);
                while (reader.ReadLine() is { } text)
                {
                    line++;
                    if (text.Length == 0) continue;
                    if ((line & 255) == 0) _ct.ThrowIfCancellationRequested();
                    using var d = JsonDocument.Parse(text);
                    var e = d.RootElement;
                    if (ResultFiles.S(e, "formKey") is not { } fk || ResultFiles.S(e, "kind") is not { } kind) continue;
                    map[(fk.ToLowerInvariant(), kind)] = Detail(e);
                }
                _editDetails = map;
            }
            catch (JsonException) { _editsWhy = $"line {line} of the patched run's {rel} is not readable"; Note($"{_editsWhy}, so the values before and after the patch are not shown"); }
        }

        static EditDetail Detail(JsonElement e)
        {
            var touched = ResultFiles.O(e, "touched");
            var tb = touched is { } t1 ? ResultFiles.O(t1, "base") : null;
            var tc = touched is { } t2 ? ResultFiles.O(t2, "cell") : null;
            var values = ResultFiles.O(e, "values");
            (string?, IReadOnlyList<(string, string?)>?) Side(string k)
            {
                if (values is not { } v || ResultFiles.O(v, k) is not { } side) return (null, null);
                var leaves = ResultFiles.O(side, "values") is { } lv
                    ? lv.EnumerateObject().Select(x => (x.Name, x.Value.ValueKind switch { JsonValueKind.Null => null, JsonValueKind.String => x.Value.GetString(), _ => x.Value.GetRawText() })).ToList() : null;
                return (ResultFiles.S(side, "plugin"), leaves);
            }
            var (beforePlugin, before) = Side("unpatchedWinner");
            var (patchPlugin, patch) = Side("patch");
            static string N(JsonElement m, string k) => m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble().ToString("0.##", CultureInfo.InvariantCulture) : "?";
            string? move = ResultFiles.O(e, "move") is { } m ? $"{N(m, "distance")} units (x {N(m, "dx")}, y {N(m, "dy")}, z {N(m, "dz")})" : null;
            var deg = ResultFiles.A(e, "rotateDeg").Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble().ToString("0.##", CultureInfo.InvariantCulture)).ToList();
            string? turn = deg.Count == 3 ? $"x {deg[0]}°, y {deg[1]}°, z {deg[2]}°" : null;
            string? disabled = ResultFiles.O(e, "disable") is { } dis && ResultFiles.A(dis, "mechanism").Select(x => x.GetString()).Where(x => x is { Length: > 0 }).ToList() is { Count: > 0 } how ? string.Join(", ", how) : null;
            string? deltas = ResultFiles.O(e, "patchDiff") is { } pd && ResultFiles.A(pd, "deltas").Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText()).ToList() is { Count: > 0 } dl ? string.Join("; ", dl) : null;
            return new EditDetail(ResultFiles.S(e, "editorId"),
                tb is { } b1 ? ResultFiles.S(b1, "type") : null, tb is { } b2 ? ResultFiles.S(b2, "editorId") : null,
                tc is { } c ? new TouchedCell(ResultFiles.S(c, "formKey"), ResultFiles.S(c, "editorId"), ResultFiles.S(c, "name"), ResultFiles.S(c, "grid"), ResultFiles.S(c, "worldspace")) : null,
                beforePlugin, before, patchPlugin, patch, move, turn, disabled, deltas);
        }

        // the scorecard calls a primitive's bounds BOUNDS; the edit list files the same edit under the field
        EditDetail? DetailOf(Edit e)
        {
            if (_editDetails is null) return null;
            var fk = e.FormKey.ToLowerInvariant();
            return _editDetails.TryGetValue((fk, e.Kind), out var d) ? d : e.Kind == "BOUNDS" && _editDetails.TryGetValue((fk, "FIELD:Primitive"), out d) ? d : null;
        }

        // The two sides of an edit as text: every value of a short edit, only the values that differ of a long one.
        static (string? Before, string? Patch, string? Note) Sides(EditDetail d)
        {
            static string V(string? t) => t ?? "(none)";
            static string Text(IReadOnlyList<(string Path, string? Text)> leaves) =>
                leaves.Count == 1 ? V(leaves[0].Text) : string.Join("; ", leaves.Select(l => $"{l.Path}={V(l.Text)}"));
            var before = d.Before; var patch = d.Patch;
            if (patch is null or { Count: 0 }) return (before is { Count: > 0 } ? Text(before) : null, null, null);
            if (before is null) return (null, Text(patch), null);
            if (patch.Count <= ManyLeaves && before.Count <= ManyLeaves)
                return (before.Count > 0 ? Text(before) : "(none)", Text(patch), before.SequenceEqual(patch) ? SameValues : null);
            var was = before.GroupBy(l => l.Path).ToDictionary(g => g.Key, g => g.First().Text);
            var now = patch.GroupBy(l => l.Path).ToDictionary(g => g.Key, g => g.First().Text);
            var differ = patch.Select(l => l.Path).Concat(before.Select(l => l.Path)).Distinct().Where(k => !(was.TryGetValue(k, out var x) && now.TryGetValue(k, out var y) && x == y)).ToList();
            int all = was.Keys.Union(now.Keys).Count();
            if (differ.Count == 0) return (null, null, $"the edit list holds {all} values of this field and none differs before and after the patch, so none is listed and it does not show what the patch changed");
            return (string.Join("; ", differ.Select(k => $"{k}={(was.TryGetValue(k, out var x) ? V(x) : "(not there)")}")),
                    string.Join("; ", differ.Select(k => $"{k}={(now.TryGetValue(k, out var y) ? V(y) : "(not there)")}")),
                    $"{differ.Count} of this field's {all} values differ before and after the patch; the others are the same and are not listed");
        }

        string JoinedLine(Edit e)
        {
            var head = $"{e.Kind} {e.RecordType} {e.FormKey}";
            if (DetailOf(e) is not { } d) return head;
            var (before, patch, _) = Sides(d);
            return before is null && patch is null ? head : $"{head}: before the patch {before ?? "(none)"}; the patch sets {patch ?? "(none)"}";
        }

        static string Verb(string kind) => kind switch
        {
            "MOVE" => "moves", "ROTATE" => "turns", "SCALE" => "rescales", "DISABLE" => "disables", "ENABLE" => "enables", "ADD" => "adds",
            "BASE" => "swaps the base object of", "LOCATION-REF" => "sets the location reference of", "OWNER" => "changes the owner of",
            "BOUNDS" => "changes the bounds of", "RECORD-FLAGS" => "changes the record flags of", "ENABLE-PARENT" => "changes the enable parent of",
            _ when kind.StartsWith("FIELD:", StringComparison.Ordinal) => $"changes the {RecordWords.Field(kind[6..])} of",
            _ => $"makes a {kind} change to",
        };

        ResultRow EditRow(int index, Edit e)
        {
            var d = DetailOf(e);
            string? note = d is not null ? null : _editDetails is null ? $"{_editsWhy}, so the values before and after the patch are not shown"
                : "the patched run's edit list has no entry for this edit, so its values are not shown";
            var values = new List<ResultValue>();
            string? before = null, patch = null;
            if (d is not null)
            {
                (before, patch, note) = Sides(d);
                if (before is not null) values.Add(new("before the patch", d.BeforePlugin, before));
                if (patch is not null) values.Add(new("the patch sets", d.PatchPlugin, patch));
                if (d.Move is not null) values.Add(new("moved by", null, d.Move));
                if (d.Turn is not null) values.Add(new("turned by", null, d.Turn));
                if (d.Disabled is not null) values.Add(new("how it is disabled", null, d.Disabled));
                if (patch is null && note is null)
                    note = e.Kind == "ADD" ? "the edit list does not hold an added record's position or other values; they are only in the patch plugin"
                        : "the edit list holds no values for this field" + (d.Deltas is not null ? ", only a description of how the patch's record differs" : "");
                // no value of the edit itself shows a change: the edit list's description of the whole record is what there is
                if ((patch is null || note == SameValues) && e.Kind != "ADD" && d.Deltas is not null) values.Add(new("how the patch's record differs", d.PatchPlugin, d.Deltas));
            }

            // the cell the edit list read for the record, else the one the result file states
            var tc = d?.Cell;
            string? cell = tc?.FormKey ?? e.Cell;
            // a persistent cell: a worldspace and no grid of its own, or a cell the sealed bundle's names mark persistent
            bool persistent = cell is not null && ((tc is { Worldspace: not null, Grid: null }) || _persistentCells.Contains(cell));
            ResultPlace place = cell is null ? Nowhere(RecordTypes.NoPlace(e.RecordType))
                : e.RecordType == "CELL" ? (persistent ? Nowhere(PersistentCellWhy) : At(cell, null))
                : persistent ? Nowhere(PersistentWhy, e.FormKey, persistent: true)
                : At(cell, e.FormKey);

            string? cellName = tc is null ? null : persistent ? (tc.Worldspace is null ? "a persistent cell" : $"{tc.Worldspace}'s persistent cell")
                : tc.EditorId ?? tc.Name ?? (tc.Worldspace is not null ? $"{tc.Worldspace}{(tc.Grid is null ? "" : " cell " + tc.Grid)}" : tc.FormKey);
            string where = cellName is { Length: > 0 } && e.RecordType != "CELL" ? $" in {cellName}" : "";
            string change = before is { Length: <= 80 } && patch is { Length: <= 80 } && before != patch ? $": {before} → {patch}" : "";
            // the record in words: its type, its editor ID (else its FormID and plugin), and its base object when it has one
            string name = RecordWords.Name(d?.EditorId, d?.BaseEditorId, e.FormKey);
            string baseOf = d?.EditorId is { Length: > 0 } && d.BaseEditorId is { Length: > 0 } be ? $" ({be})" : "";
            string summary = $"the patch {Verb(e.Kind)} {RecordWords.Type(e.RecordType)} {name}{baseOf}{where}{change}";
            return new ResultRow(index, ResultGroup.PatchChange, _p.Id, "edit", e.Kind, e.RecordType, e.FormKey,
                e.Kind.StartsWith("FIELD:", StringComparison.Ordinal) ? e.Kind[6..] : null, d?.EditorId, d?.BaseEditorId, d?.BaseType,
                e.Category, e.Rule, null, summary, values, place, e.Kind == "DISABLE" ? "before" : "after", Array.Empty<string>(), note,
                e.RecordType == "CELL" ? null : cellName);
        }

        // ---- predicted overlaps the scorecard does not score ----

        void Overlaps(List<ResultRow> rows)
        {
            const string rel = "verdict-plan/presentation.json";
            if (_p.SealedRun is null) return;   // already said at open
            if (_p._overlapPin is null) { Note($"{_p._overlapPinWhy}, so the predicted overlaps are not listed"); return; }
            var (bytes, why) = Pinned(Path.Combine(_p.SealedRun, ResultFiles.Native(rel)), "the sealed run's " + rel, _p._overlapPin, "verdicts-pre-patch.json");
            if (bytes is null) { Note($"{why}, so the predicted overlaps are not listed"); return; }
            try
            {
                using var d = ResultFiles.Json(bytes);
                if (ResultFiles.O(d.RootElement, "reader") is not { } reader || ResultFiles.O(reader, "prediction") is not { } prediction
                    || !prediction.TryGetProperty("record", out var record) || record.ValueKind != JsonValueKind.Array)
                { Note($"the sealed run's {rel} holds no list of predicted rows, so the predicted overlaps are not listed"); return; }
                int index = -1, count = 0;
                foreach (var e in record.EnumerateArray())
                {
                    index++;
                    if (ResultFiles.S(e, "checkId") != "geometry.object-overlap" || ResultFiles.S(e, "admission") != "admitted") continue;
                    if ((++count & 255) == 0) _ct.ThrowIfCancellationRequested();
                    string fk = ResultFiles.S(e, "formKey") ?? "";
                    string? other = ResultFiles.A(e, "other").Select(x => x.GetString()).FirstOrDefault();
                    string says = ResultFiles.S(e, "summary") ?? "", attribution = ResultFiles.S(e, "attribution") ?? "";
                    var values = new List<ResultValue> { new("what the check says", null, says) };
                    if (ResultFiles.S(e, "physical") is { Length: > 0 } physical) values.Add(new("physical state", null, physical));
                    values.Add(new("attribution", ResultFiles.S(e, "attributionMod"), attribution.Length > 0 ? attribution : "(none)"));
                    var named = _names?.Ref(fk);
                    rows.Add(new ResultRow(index, ResultGroup.NotScored, _p.Id, "overlap", ResultFiles.S(e, "kind") ?? "OVERLAP", named?.BaseType == "Npc" ? "ACHR" : "REFR",
                        fk, null, null, named?.BaseEditorId, named?.BaseType, null, null, attribution.Length > 0 ? attribution : null,
                        says.Length > 0 ? Regex.Replace(says, @"\s+", " ").Trim() : $"predicted overlap of {fk} and {other}", values, RefPlace(fk, other), "before", Array.Empty<string>(),
                        attribution == "needs-both" ? null : OverlapWords.Attribution(attribution, ResultFiles.S(e, "attributionMod"))));
                }
                NotScored = count;
            }
            catch (JsonException) { Note($"the sealed run's {rel} is not readable, so the predicted overlaps are not listed"); }
        }
    }
}

/// <summary>Record type words: the four-letter code for a Mutagen type name, and why a record of a type has no place to show.</summary>
internal static class RecordTypes
{
    internal static string Code(string mutagenName) => mutagenName switch
    {
        "PlacedObject" => "REFR", "PlacedNpc" => "ACHR", "NavigationMesh" => "NAVM", "Cell" => "CELL",
        "Worldspace" => "WRLD", "Landscape" => "LAND", "Location" => "LCTN", "NavigationMeshInfoMap" => "NAVI",
        _ => mutagenName,
    };

    internal static string NoPlace(string type) => type switch
    {
        "NAVI" => "a NAVI record belongs to no cell, so there is nothing to show",
        "WRLD" => "a worldspace record has no place of its own, so there is nothing to show",
        "LCTN" => "a location record is not placed in any cell, so there is nothing to show",
        "LAND" => "the results do not record which cell this landscape record belongs to, so there is no cell to open",
        "NAVM" => "the results do not record which cell this navmesh belongs to, so there is no cell to open",
        _ => $"the results record no cell for this {(type.Length > 0 ? type + " " : "")}record, so there is no cell to open",
    };
}

/// <summary>A finished held-out evaluation, opened read-only: a folder holding SCORING-RECEIPT.txt and scoring/result/&lt;pair&gt;.json.</summary>
internal sealed class EvaluationResults
{
    const string Receipt = "SCORING-RECEIPT.txt", ResultDir = "scoring/result/", EntryDir = "scoring/entries/", DriverLog = "scoring/score-driver.log";
    const string NotOpened = "so the evaluation results are not opened";

    internal required string Folder { get; init; }
    internal required IReadOnlyList<ResultPair> Pairs { get; init; }
    /// <summary>Every file the receipt lists that was hashed and matched.</summary>
    internal required IReadOnlyList<(string File, string Sha256)> Checked { get; init; }
    internal required IReadOnlyList<string> Notes { get; init; }

    /// <summary>The pairs' counts added up; NotScored is null until every pair's rows have been loaded.</summary>
    internal ResultCounts Totals
    {
        get
        {
            var c = Pairs.Select(p => p.Counts).ToList();
            return new ResultCounts(c.Sum(x => x.Addressed), c.Sum(x => x.NotAddressed), c.Sum(x => x.PatchChange), c.Sum(x => x.Category3), c.Sum(x => x.Category4), c.Sum(x => x.Category5),
                c.All(x => x.NotScored is not null) ? c.Sum(x => x.NotScored!.Value) : null);
        }
    }

    static string? FolderOf(string path)
    {
        try
        {
            if (File.Exists(path) && Path.GetFileName(path).Equals(Receipt, StringComparison.OrdinalIgnoreCase)) path = Path.GetDirectoryName(Path.GetFullPath(path))!;
            return Directory.Exists(path) && File.Exists(Path.Combine(path, Receipt)) && Directory.Exists(Path.Combine(path, "scoring", "result")) ? Path.GetFullPath(path) : null;
        }
        catch (Exception x) when (x is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>By names only: a folder with SCORING-RECEIPT.txt and scoring/result/, or that receipt file.</summary>
    internal static bool LooksLike(string path) => FolderOf(path) is not null;

    static readonly Regex ReceiptLine = new(@"^([0-9a-fA-F]{64})\s+(\S.*?)\s*$", RegexOptions.CultureInvariant);
    static readonly Regex DriverLine = new(@"^\S+ \S+ ([^\s:]+): -> (.+?)\s*$", RegexOptions.CultureInvariant);

    /// <summary>Open the results: check every file against the receipt, then read the pairs. Refuses in one sentence.</summary>
    internal static EvaluationResults Open(string path, Func<string, string?>? locate = null)
    {
        var folder = FolderOf(path) ?? throw new InvalidOperationException($"'{path}' is not an evaluation results folder: it needs {Receipt} and scoring/result/.");
        string Full(string rel) => Path.Combine(folder, ResultFiles.Native(rel));

        var listed = new List<(string Rel, string Sha)>();
        foreach (var line in Encoding.UTF8.GetString(ResultFiles.Read(Full(Receipt))).Split('\n'))
            if (ReceiptLine.Match(line.TrimEnd('\r')) is { Success: true } m) listed.Add((m.Groups[2].Value.Replace('\\', '/'), m.Groups[1].Value.ToLowerInvariant()));
        if (listed.Count == 0) throw new InvalidOperationException($"{Receipt} lists no files, {NotOpened}.");
        var byRel = new HashSet<string>(listed.Select(l => l.Rel), StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(Full("scoring/result"), "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            if (!byRel.Contains(ResultDir + Path.GetFileName(f)))
                throw new InvalidOperationException($"{ResultDir}{Path.GetFileName(f)} is not listed in {Receipt}, {NotOpened}.");

        // every listed file is hashed; a result file or a file read here that is missing or changed refuses
        var notes = new List<string>();
        var done = new List<(string, string)>();
        var bytesOf = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var root = folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var (rel, want) in listed)
        {
            bool read = rel.StartsWith(ResultDir, StringComparison.OrdinalIgnoreCase) || rel.StartsWith(EntryDir, StringComparison.OrdinalIgnoreCase) || rel.Equals(DriverLog, StringComparison.OrdinalIgnoreCase);
            var full = Path.GetFullPath(Full(rel));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { notes.Add($"{Receipt} lists {rel}, which is outside the results folder; it is not read"); continue; }
            if (!File.Exists(full))
            {
                if (read) throw new InvalidOperationException($"{Receipt} lists {rel}, which is missing, {NotOpened}.");
                notes.Add($"{Receipt} lists {rel}, which is missing; the workbench does not read it"); continue;
            }
            var bytes = ResultFiles.Read(full);
            var sha = ResultFiles.Sha(bytes);
            if (sha != want)
            {
                if (read) throw new InvalidOperationException($"{rel} does not match {Receipt}, {NotOpened}.");
                notes.Add($"{rel} does not match {Receipt}; the workbench does not read it"); continue;
            }
            done.Add((rel, sha));
            if (read) bytesOf[rel] = bytes;
        }

        // where each patched run is: the driver's own log, "<run>: -> <folder>"
        var runs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? runsWhy = null;
        if (bytesOf.TryGetValue(DriverLog, out var log))
        {
            foreach (var line in Encoding.UTF8.GetString(log).Split('\n'))
                if (DriverLine.Match(line.TrimEnd('\r')) is { Success: true } m) runs[m.Groups[1].Value] = m.Groups[2].Value;
        }
        else runsWhy = $"{Receipt} does not list {DriverLog}, so where the patched runs are is not read";
        if (runsWhy is not null) notes.Add(runsWhy);

        var entries = bytesOf.Where(kv => kv.Key.StartsWith(EntryDir, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kv => Path.GetFileNameWithoutExtension(kv.Key), kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        // two files the receipt does not pin: the game folder, and the pins of the predicted-overlap lists
        string? game = null, gameWhy = null;
        try
        {
            if (!File.Exists(Full("machine.json"))) gameWhy = "machine.json is not in the results folder, so the game folder is not known";
            else
            {
                using var d = ResultFiles.Json(ResultFiles.Read(Full("machine.json")));
                game = ResultFiles.O(d.RootElement, "lab") is { } lab ? ResultFiles.S(lab, "game") : null;
                if (game is null) gameWhy = "machine.json names no game folder";
            }
        }
        catch (JsonException) { gameWhy = "machine.json is not readable, so the game folder is not known"; }
        var overlapPins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? overlapWhy = null;
        try
        {
            if (!File.Exists(Full("verdicts-pre-patch.json"))) overlapWhy = "verdicts-pre-patch.json is not in the results folder, so nothing pins the overlap lists";
            else
            {
                using var d = ResultFiles.Json(ResultFiles.Read(Full("verdicts-pre-patch.json")));
                foreach (var p in ResultFiles.A(d.RootElement, "pairs"))
                    if (ResultFiles.S(p, "bundleManifestSha256") is { } manifest && ResultFiles.S(p, "presentationSha256") is { } presentation) overlapPins[manifest] = presentation;
            }
        }
        catch (JsonException) { overlapWhy = "verdicts-pre-patch.json is not readable, so nothing pins the overlap lists"; }
        if (gameWhy is not null) notes.Add(gameWhy);
        if (overlapWhy is not null) notes.Add(overlapWhy);
        notes.Add($"machine.json (the game folder) and verdicts-pre-patch.json (the pins of the predicted-overlap lists) are read as they are: {Receipt} does not list them");

        var src = new ResultSources(folder, runs, runsWhy, entries, game, gameWhy, overlapPins, overlapWhy, locate);
        var pairs = new List<ResultPair>();
        foreach (var (rel, _) in listed.Where(l => l.Rel.StartsWith(ResultDir, StringComparison.OrdinalIgnoreCase) && l.Rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            if (!bytesOf.TryGetValue(rel, out var bytes)) continue;
            try { if (ResultPair.Read(rel, bytes, src) is { } pair) pairs.Add(pair); }
            catch (Exception x) when (x is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            { throw new InvalidOperationException($"{rel} is not a result file the workbench can read ({x.Message.Split('\n')[0].TrimEnd('.')}), {NotOpened}."); }
        }
        if (pairs.Count == 0) throw new InvalidOperationException($"{ResultDir} holds no pair's result file, {NotOpened}.");
        return new EvaluationResults { Folder = folder, Pairs = pairs, Checked = done, Notes = notes };
    }
}
