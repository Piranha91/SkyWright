namespace SkyWright.Engine;

// LOST EDITS (records.contested-fields, records.cell-attributes): a field one mod changed whose winning value is another
// mod's, where the winner was not made with the loser. The one owner of the rule in SkyWright (record checks Gate A,
// section 4; rulings R5 and R6); recordprobe reads houseCARL's tree and delta answers and asks. Ported from the module's
// evalrecords.lost_edits (which goes at Gate D) with the shipped clauses added:
//   which versions   one plugin's copy of a contested record that does not win is a version. Set aside by type: navmesh
//                    records (the navmesh check's, R6), registration and dialogue records (unit 4's), record types the
//                    field scope leaves out; then by RecordClashRule.LostEdit: outside the selection, a game file on
//                    either side, one mod, a winner made with the loser. A plugin whose masters' headers could not all
//                    be read is not judged.
//   made with what   the loser's own change is read against what it was made with: the highest-priority plugin among its
//                    masters (transitively) that has the record (R5). A plugin that has none of them beneath it defines
//                    the record and authored every field it holds.
//   lost             a field the loser changed (its value differs in game from what it was made with, or it removed a field
//                    what it was made with has) whose winning value differs in game from the loser's. In game: the field
//                    scope's exclusions, numbers at four decimals, record flags without the storage bit, masked integers,
//                    and a reordered list is no change where the game does not read its order and not judged where it does.
// The instrument (--instrument) is the Python capture's rule for its known answer: the selection's plugins as the only
// losers, each against the plugin beneath it (previous_provider), with none of the between-mods clauses and navmesh kept.

/// <summary>One field a version lost: the top-level field, the paths under it, the loser's value, the winner's, and the
/// value the loser was made with ("None" where a part has none; null where the field is one path and has none).</summary>
public sealed record LostField(string Field, IReadOnlyList<string> Paths, string? Lost, string? Winner, string? MadeWith);

/// <summary>What one version gives: its lost fields, how many authored fields were compared, and what was not judged.</summary>
public sealed record VersionResult(IReadOnlyList<LostField> Lost, long FieldsJudged, IReadOnlyDictionary<string, long> NotJudged);

/// <summary>Whether a version is judged, and if not, the reason it is set aside or not judged.</summary>
public sealed record Admission(bool Judge, string? SetAside = null, string? NotJudged = null);

public static class LostEditRule
{
    public const string Registration = "registration and dialogue records: that check's (record checks unit 4)";
    public const string Navmesh = "navmesh: the navmesh check's";
    public const string RuntimeMerged = "a record type the field scope leaves out: the game merges it at run time";
    public const string Incomplete = "a read houseCARL marks incomplete";
    public const string Unreadable = "a delta line SkyWright could not read";
    public const string OrderRead = "a list whose order the game reads, reordered";
    public const string MastersUnread = "a plugin whose masters' headers could not all be read";
    public const string RemovalUnsettled = "a removal whose master's copy could not be settled";
    public const string Derived = "a field the field scope names derived data (a cell's occlusion data and height map), which the editor regenerates: no change a modder acts on (counted per field)";

    /// <summary>Registration and dialogue record types: quests, topics, scenes, form lists, locations, story-manager nodes,
    /// packages (the module's REGISTRATION_TYPES), record checks unit 4's.</summary>
    static readonly HashSet<string> RegistrationTypes = new(StringComparer.Ordinal)
    {
        "Quest", "DialogTopic", "DialogResponses", "Scene", "FormList", "Location", "StoryManagerQuestNode", "StoryManagerBranchNode",
        "StoryManagerEventNode", "Package", "DialogBranch", "DialogView",
    };

    public static bool IsRegistration(string rtype) => RegistrationTypes.Contains(rtype);
    public static bool IsNavmesh(string rtype) => rtype == "NavigationMesh";

    /// <summary>Whether one version (the loser's copy, the winner's on top) is judged. <paramref name="mastersUnread"/>:
    /// a header in either plugin's masters could not be read.</summary>
    public static Admission Admit(string rtype, LinkEnd loser, LinkEnd winner, bool winnerMadeWithLoser, bool mastersUnread, bool instrument, RecordFieldScope scope)
    {
        if (IsRegistration(rtype)) return new(false, SetAside: Registration);
        if (instrument) return loser.Selected ? new(true) : new(false, SetAside: RecordClashRule.NotSelected);
        if (IsNavmesh(rtype)) return new(false, SetAside: Navmesh);
        if (scope.RuntimeMergedRecord(rtype)) return new(false, SetAside: RuntimeMerged);
        var v = RecordClashRule.LostEdit(loser, winner, winnerMadeWithLoser);
        if (!v.Row) return new(false, SetAside: v.Reason);
        if (mastersUnread) return new(false, NotJudged: MastersUnread);
        return new(true);
    }

    /// <summary>What the loser was made with on this record: the last of <paramref name="touchers"/> (load order) below it
    /// that is one of its masters; null when none is, and the loser then defines the record.</summary>
    public static string? MadeWithPole(IReadOnlyList<string> touchers, string loser, IReadOnlySet<string> loserMasters)
    {
        int i = touchers.ToList().FindIndex(t => string.Equals(t, loser, StringComparison.OrdinalIgnoreCase));
        for (int j = (i < 0 ? touchers.Count : i) - 1; j >= 0; j--)
            if (loserMasters.Contains(touchers[j])) return touchers[j];
        return null;
    }

    /// <summary>A map that keeps its keys in the order they were first added, as the Python dict it was ported from.</summary>
    sealed class Ordered<T>
    {
        readonly List<string> _keys = new();
        readonly Dictionary<string, T> _map = new(StringComparer.Ordinal);
        public void Set(string k, T v) { if (!_map.ContainsKey(k)) _keys.Add(k); _map[k] = v; }
        public bool TryGet(string k, out T v) => _map.TryGetValue(k, out v);
        public bool Contains(string k) => _map.ContainsKey(k);
        public void Remove(string k) { if (_map.Remove(k)) _keys.Remove(k); }
        public IEnumerable<(string Key, T Value)> Items => _keys.Select(k => (k, _map[k]));
    }

    /// <summary>One authored field: its value, the other side (what it was made with), and how it was read.</summary>
    sealed record Authored(string Field, string? Own, string? Other, string? Prev, string How);

    /// <summary>Whether one of <paramref name="masters"/> holds this field, read off the tree (each provider's lines against
    /// the winner, the winner holding it): true, false, or null when the tree cannot say.</summary>
    static bool? MasterHolds(string field, IReadOnlySet<string> masters, IReadOnlyDictionary<string, (IReadOnlyList<string> Lines, bool Complete)>? providers, IReadOnlyList<string> plugins)
    {
        if (providers is null) return null;
        bool unsure = false;
        foreach (var (name, (lines, complete)) in providers)
        {
            if (!masters.Contains(name)) continue;
            if (!complete) { unsure = true; continue; }
            var here = lines.Where(ln => ln.StartsWith(field, StringComparison.Ordinal) && ln.Length > field.Length && ln[field.Length] is '=' or ':' or ' ').ToList();
            if (here.Count == 0) return true;   // no difference from the winner on this field, and the winner holds it
            var parsed = here.Select(ln => RecordDelta.Parse(ln, plugins)).ToList();
            if (parsed.Any(x => x is null)) unsure = true;
            else if (parsed.Any(x => x!.Kind != DeltaKind.OwnAbsent)) return true;
        }
        return unsure ? null : false;
    }

    /// <summary>One version judged: <paramref name="authoredLines"/> are the loser's lines against what it was made with,
    /// <paramref name="vsWinnerLines"/> its lines against the winner. <paramref name="defines"/>: nothing is beneath it, so
    /// every field it holds is its own. <paramref name="masters"/>: the loser's masters (transitively) and
    /// <paramref name="providers"/> the record's tree, which settle whether a field it lacks was removed by it.
    /// <paramref name="complete"/>: false when houseCARL marked a read incomplete; the version is then not judged.
    /// <paramref name="angles"/>: rotations compared modulo a full turn (false only for the instrument, as the Python capture).</summary>
    public static VersionResult Judge(string rtype, IReadOnlyList<string> authoredLines, IReadOnlyList<string> vsWinnerLines, IReadOnlyList<string> plugins,
        RecordFieldScope scope, bool complete = true, bool defines = false, IReadOnlySet<string>? masters = null,
        IReadOnlyDictionary<string, (IReadOnlyList<string> Lines, bool Complete)>? providers = null, bool mastersUnread = false, bool angles = true)
    {
        bool Same(string field, string? x, string? y) => RecordFieldScope.SameValue(x, y) || (angles && scope.AngleEqual(field, x, y));
        var unjudged = new Dictionary<string, long>(StringComparer.Ordinal);
        void Count(string why) => unjudged[why] = unjudged.GetValueOrDefault(why) + 1;
        if (!complete) { Count(Incomplete); return new VersionResult(Array.Empty<LostField>(), 0, unjudged); }
        var authored = new Ordered<Authored>();
        var against = new Ordered<DeltaLine>();
        var lacking = new List<DeltaLine>();
        IReadOnlySet<string>? lower = masters?.Select(m => m.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        if (defines)
        {
            authoredLines = Array.Empty<string>();
            foreach (var line in vsWinnerLines)
                if (RecordDelta.Parse(line, plugins) is { } p && p.Kind is not (DeltaKind.OwnAbsent or DeltaKind.Order))
                    authored.Set(p.Field, new Authored(p.Field, p.Own, null, null, "defined"));
        }
        foreach (var line in authoredLines)
        {
            var p = RecordDelta.Parse(line, plugins);
            if (p is null) { Count(Unreadable); continue; }
            if (p.Kind == DeltaKind.Order)
            {
                if (RecordFieldScope.OrderIsRead(rtype, p.Field) && scope.Excluded(rtype, p.Field) is null) Count(OrderRead);
                continue;
            }
            if (p.Kind == DeltaKind.OwnAbsent) { lacking.Add(p); continue; }
            authored.Set(p.Field, new Authored(p.Field, p.Own, p.Other, p.OtherValue, p.Kind.ToString()));
        }
        foreach (var line in vsWinnerLines)
        {
            var p = RecordDelta.Parse(line, plugins);
            if (p is null) { Count(Unreadable); continue; }
            if (p.Kind == DeltaKind.Order) continue;
            against.Set(p.Field, p);
        }
        foreach (var p in lacking)
        {
            // no line against the winner: the winner lacks it too, so whatever this was, it stands
            if (lower is null || !against.Contains(p.Field) || scope.Excluded(rtype, p.Field) is not null) continue;
            bool? held = lower.Contains(p.Other.ToLowerInvariant()) ? true : MasterHolds(p.Field, lower, providers, plugins);
            if (held is not true && mastersUnread) Count(MastersUnread);
            else if (held is null) Count(RemovalUnsettled);
            else if (held is true) authored.Set(p.Field, new Authored(p.Field, "(absent)", p.Other, p.OtherValue, "removed"));
        }
        if (scope.FlagsField is { } ff && authored.Contains(ff))
            foreach (var view in scope.FlagsViews) authored.Remove(view);   // the same header bits under a per-type name: judged once
        var rows = new Ordered<List<(string Field, string? Own, string? Winner, string? Prev)>>();
        long judged = 0;
        foreach (var (field, a) in authored.Items)
        {
            if (scope.Excluded(rtype, field) is not null) continue;
            judged++;
            if (!against.TryGet(field, out var w)) continue;   // the winner keeps this plugin's value
            string? own = a.Own, prev = a.Prev;
            if (a.How == "Value" && Same(field, own, prev)) continue;   // no change from what it was made with
            if (w.Kind == DeltaKind.Value && (Same(field, w.Own, w.OtherValue) || scope.MaskedEqual(rtype, field, w.Own, w.OtherValue))) continue;
            if (a.How == "Value" && scope.MaskedEqual(rtype, field, own, prev)) continue;
            string top = RecordDelta.Top(field);
            if (!rows.TryGet(top, out var parts)) rows.Set(top, parts = new());
            parts.Add((field, own, w.OtherValue, prev));
        }
        var lost = new List<LostField>();
        foreach (var (top, parts) in rows.Items)
        {
            // a field's parts joined "Leaf=value; Leaf=value" (a placement reads "Position=x,y,z; Rotation=x,y,z"), as the
            // module's record rows join them; a part with no value reads "None"
            string? Joined(Func<(string Field, string? Own, string? Winner, string? Prev), string?> pick) =>
                parts.Count == 1 && parts[0].Field == top ? pick(parts[0])
                : string.Join("; ", parts.Select(p => $"{(p.Field.StartsWith(top + ".", StringComparison.Ordinal) ? p.Field[(top.Length + 1)..] : p.Field)}={pick(p) ?? "None"}"));
            lost.Add(new LostField(top, parts.Select(p => p.Field).ToList(), Joined(p => p.Own), Joined(p => p.Winner), Joined(p => p.Prev)));
        }
        return new VersionResult(lost, judged, unjudged);
    }

    /// <summary>Whether the winner changed a top-level field itself, against what it was made with: yes when it defines the
    /// record or a line of its own changes is on the field (in game), no when none is, null when its read is incomplete or a
    /// line on the field could not be read.</summary>
    public static bool? WinnerChanged(string top, IReadOnlyList<string> winnerLines, bool complete, bool defines, string rtype, IReadOnlyList<string> plugins, RecordFieldScope scope)
    {
        if (defines) return true;
        if (!complete) return null;
        bool unsure = false;
        foreach (var line in winnerLines)
        {
            var p = RecordDelta.Parse(line, plugins);
            if (p is null) { if (line.StartsWith(top, StringComparison.Ordinal)) unsure = true; continue; }
            if (RecordDelta.Top(p.Field) != top || p.Kind == DeltaKind.Order) continue;
            if (p.Kind == DeltaKind.Value && (RecordFieldScope.SameValue(p.Own, p.OtherValue) || scope.AngleEqual(p.Field, p.Own, p.OtherValue) || scope.MaskedEqual(rtype, p.Field, p.Own, p.OtherValue))) continue;
            return true;
        }
        return unsure ? null : false;
    }
}
