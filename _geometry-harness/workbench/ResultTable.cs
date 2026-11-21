using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Workbench;

/// <summary>Record words a modder reads: a record type in words, and a record by its editor ID or its FormID and plugin,
/// never the four-letter signature or a raw FormKey.</summary>
internal static class RecordWords
{
    internal static string Type(string recordType) => recordType switch
    {
        "REFR" => "placed object",
        "ACHR" => "placed NPC",
        "CELL" => "cell",
        "WRLD" => "worldspace",
        "NAVM" => "navmesh",
        "NAVI" => "navmesh info",
        "LAND" => "landscape",
        "STAT" => "static",
        "NPC_" => "NPC",
        "LIGH" => "light",
        "DOOR" => "door",
        "CONT" => "container",
        "FURN" => "furniture",
        "ACTI" => "activator",
        "TREE" => "tree",
        "FLOR" => "plant",
        "MSTT" => "movable static",
        "QUST" => "quest",
        "LCTN" => "location",
        _ => recordType,
    };

    /// <summary>"000891 in Skyrim.esm" for "000891:Skyrim.esm".</summary>
    internal static string Form(string formKey)
    {
        int at = formKey.IndexOf(':');
        return at < 0 ? formKey : $"{formKey[..at]} in {formKey[(at + 1)..]}";
    }

    /// <summary>The plugin a FormKey's record comes from.</summary>
    internal static string Plugin(string formKey) { int at = formKey.IndexOf(':'); return at < 0 ? "" : formKey[(at + 1)..]; }

    /// <summary>A record as named: its editor ID, else its base object's, else its FormID and plugin.</summary>
    internal static string Name(string? editorId, string? baseEditorId, string formKey) =>
        editorId is { Length: > 0 } ? editorId : baseEditorId is { Length: > 0 } ? baseEditorId : Form(formKey);

    /// <summary>A record in a sentence: its type in words, its name, and where its FormID comes from, as
    /// "placed object WRWallStr01Stockades01 (06E9DD in Skyrim.esm)".</summary>
    internal static string Named(string recordType, string? editorId, string? baseEditorId, string formKey)
    {
        var name = Name(editorId, baseEditorId, formKey);
        return $"{Type(recordType)} {name}" + (name == Form(formKey) ? "" : $" ({Form(formKey)})");
    }

    /// <summary>A record field in words: the ones a placed object's rows name most, else the field's own name.</summary>
    internal static string Field(string field) => field switch
    {
        "SkyrimMajorRecordFlags" or "MajorRecordFlagsRaw" => "record flags",
        "Placement" => "position",
        "EnableParent" => "enable parent",
        "LocationReference" => "location reference",
        "Base" => "base object",
        "Scale" => "scale",
        "Ownership" => "owner",
        "Primitive" => "bounds",
        _ => field,
    };

    /// <summary>A change kind in words, as done to the record ("moved", "changed Grid").</summary>
    internal static string Change(string kind) => kind switch
    {
        "MOVE" => "moved", "ROTATE" => "turned", "SCALE" => "rescaled", "DISABLE" => "disabled", "ENABLE" => "enabled", "ADD" => "added",
        "BASE" => "base object swapped", "LOCATION-REF" => "location reference set", "OWNER" => "owner changed",
        "BOUNDS" => "bounds changed", "RECORD-FLAGS" => "record flags changed", "ENABLE-PARENT" => "enable parent changed",
        "OVERLAP" => "overlap",
        _ when kind.StartsWith("FIELD:", StringComparison.Ordinal) => $"changed {Field(kind[6..])}",
        "" => "",
        _ => kind.Replace('-', ' ').ToLowerInvariant(),
    };
}

/// <summary>One results row as the table shows it: a column per thing a modder sorts or reads by.</summary>
internal sealed record ResultTableRow(ResultRow Row, string Line)
{
    public string Mark => ResultRules.Groups[Row.Group].Shape;
    public string Result => Row.Group == ResultGroup.PatchChange ? Row.Category switch { 3 => "new value", 4 => "forwards a value", 5 => "not measured", _ => "patch change" }
                                                                 : ResultRules.Groups[Row.Group].Words;
    public string Pair => Row.Pair;
    public string What => Row.Lane == "record" ? $"{RecordWords.Field(Row.Field ?? Row.Kind)} lost" : RecordWords.Change(Row.Kind);
    public string Object => RecordWords.Name(Row.EditorId, Row.BaseEditorId, Row.FormKey);
    public string Type => RecordWords.Type(Row.RecordType) + (Row.BaseType is { Length: > 0 } b && Row.RecordType is "REFR" or "ACHR" ? $" ({b.ToLowerInvariant()})" : "");
    public string Plugin => RecordWords.Plugin(Row.FormKey);
    public string Where => Row.Where ?? (Row.Place.Cell is { } c ? RecordWords.Form(c) : "");
    /// <summary>Before → after, when both are short enough to read in a cell; else empty (the values pane has them).</summary>
    public string Change
    {
        get
        {
            var before = Row.Values.FirstOrDefault(v => v.Label == "before the patch")?.Text;
            var after = Row.Values.FirstOrDefault(v => v.Label == "the patch sets")?.Text;
            return before is { Length: <= 60 } && after is { Length: <= 60 } && before != after ? $"{before} → {after}" : "";
        }
    }
    public string View => Row.Place.Cell is not null || ResultRules.PlacedOnOpen(Row) ? "" : "no view";
    /// <summary>The row's whole line, for the tooltip.</summary>
    public string Tip => Line;
    public override string ToString() => Line;

    /// <summary>The table's columns, in order: header, the property shown, and the width it starts at.</summary>
    internal static readonly (string Header, string Path, double Width, string Tip)[] Columns =
    {
        ("", nameof(Mark), 26, "the row's mark in the 3D view"),
        ("Result", nameof(Result), 100, "addressed, not addressed, a patch change nothing predicted (by kind), or an overlap the scorecard does not score"),
        ("Pair", nameof(Pair), 90, "the pair of mods the row belongs to"),
        ("Change", nameof(What), 130, "what was done to the record"),
        ("Object", nameof(Object), 190, "the record by its editor ID, else its FormID and plugin"),
        ("Type", nameof(Type), 130, "the kind of record"),
        ("Plugin", nameof(Plugin), 150, "the plugin the record comes from"),
        ("Where", nameof(Where), 170, "the cell it is in"),
        ("Before → after", nameof(Change), 200, "the value before the patch and the one the patch sets, when short"),
        ("View", nameof(View), 70, "'no view' when the row has no place to show; its tooltip says why"),
    };

    /// <summary>A grid view with the table's columns.</summary>
    internal static GridView Grid()
    {
        var g = new GridView { AllowsColumnReorder = true };
        foreach (var (h, path, w, tip) in Columns)
            g.Columns.Add(new GridViewColumn { Header = new TextBlock { Text = h, ToolTip = tip }, DisplayMemberBinding = new Binding(path), Width = w });
        return g;
    }
}
