using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

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

    /// <summary>A base object's type in words: "moveable static" for MoveableStatic.</summary>
    internal static string BaseType(string baseType) =>
        System.Text.RegularExpressions.Regex.Replace(baseType, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();

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

/// <summary>One results row as the table shows it: a column per thing a modder sorts or reads by. The pair's mods are
/// named, not the pair's id; the result is a colored square (its words in the tooltip).</summary>
internal sealed record ResultTableRow(ResultRow Row, string Line, ResultPair? Pair)
{
    /// <summary>A result's color: green addressed, red not addressed, amber a patch change nothing predicted, grey an overlap not scored.</summary>
    internal static Brush BrushOf(ResultGroup g) => Theme.Brush(g switch
    {
        ResultGroup.Addressed => "#4CAF50",
        ResultGroup.NotAddressed => "#E5534B",
        ResultGroup.PatchChange => "#E0A030",
        _ => "#8A8A8A",
    });

    /// <summary>A small square of a result's color, for the table and the filters.</summary>
    internal static Border Square(ResultGroup g) => new() { Width = 11, Height = 11, CornerRadius = new CornerRadius(2), Background = BrushOf(g), VerticalAlignment = VerticalAlignment.Center };

    public string FormKey => Row.FormKey;
    public string Type => RecordWords.Type(Row.RecordType) + (Row.BaseType is { Length: > 0 } b && Row.RecordType is "REFR" or "ACHR" ? $" ({RecordWords.BaseType(b)})" : "");
    public Brush ResultBrush => BrushOf(Row.Group);
    public string Result => Row.Group == ResultGroup.PatchChange
        ? "patch change nothing predicted: " + (Row.Category switch { 3 => "a new value", 4 => "forwards a value", 5 => "not measured", _ => "category " + Row.Category })
        : ResultRules.Groups[Row.Group].Words;
    /// <summary>The mod (or mods) the pair checks, and the one it is checked against, by plugin name.</summary>
    /// <summary>An overlap names the plugins of its own two objects (the selected one's, then the other's), which need not be
    /// the pair's: a pair's mod can clash with a third plugin's object.</summary>
    OverlapWords.Parts? Objects => Row.Lane == "overlap" ? OverlapWords.Parse(Row.Summary) : null;
    public string Mod => Objects is { } o ? o.PluginA : Pair is null ? Row.Pair : string.Join(", ", Pair.Subject);
    public string Against => Objects is { } o ? o.PluginB : Pair is null ? "" : string.Join(", ", Pair.Partner);
    public string What => Row.Lane == "record" ? $"{RecordWords.Field(Row.Field ?? Row.Kind)} lost" : RecordWords.Change(Row.Kind);
    public string Object => RecordWords.Name(Row.EditorId, Row.BaseEditorId, Row.FormKey);
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

    /// <summary>The table's columns, in order: header, the property shown (null: the result square), the width it starts at, and the header's tooltip.</summary>
    internal static readonly (string Header, string? Path, double Width, string Tip)[] Columns =
    {
        ("FormKey", nameof(FormKey), 150, "the record's FormID and the plugin it comes from"),
        ("Type", nameof(Type), 175, "the kind of record"),
        ("", null, 30, "green: addressed · red: not addressed · amber: a patch change nothing predicted · grey: an overlap the scorecard does not score"),
        ("Mod", nameof(Mod), 190, "the mod the pair checks; for an overlap, the plugin of the row's own object (cyan in the view)"),
        ("Against", nameof(Against), 170, "the mod it is checked against; for an overlap, the plugin of the object it passes into (magenta in the view)"),
        ("Change", nameof(What), 130, "what was done to the record"),
        ("Object", nameof(Object), 190, "the record by its editor ID, else its FormID and plugin"),
        ("Where", nameof(Where), 170, "the cell it is in"),
        ("Before → after", nameof(Change), 200, "the value before the patch and the one the patch sets, when short"),
        ("View", nameof(View), 70, "'no view' when the row has no place to show; its tooltip says why"),
    };

    /// <summary>A grid view with the table's columns; the result column draws its square.</summary>
    internal static GridView Grid()
    {
        var g = new GridView { AllowsColumnReorder = true };
        foreach (var (h, path, w, tip) in Columns)
        {
            var col = new GridViewColumn { Header = new TextBlock { Text = h, ToolTip = tip, MinWidth = 14, Background = Brushes.Transparent }, Width = w };
            if (path is not null) col.DisplayMemberBinding = new Binding(path);
            else
            {
                var sq = new FrameworkElementFactory(typeof(Border));
                sq.SetValue(FrameworkElement.WidthProperty, 11.0); sq.SetValue(FrameworkElement.HeightProperty, 11.0);
                sq.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
                sq.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                sq.SetBinding(Border.BackgroundProperty, new Binding(nameof(ResultBrush)));
                sq.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(Result)));
                col.CellTemplate = new DataTemplate { VisualTree = sq };
            }
            g.Columns.Add(col);
        }
        return g;
    }
}
