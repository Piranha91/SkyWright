using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Workbench;

/// <summary>The open results row, laid out for reading: what the object is, what the result means in a sentence, what
/// clashes, and the values side by side in a small table. The plain-text account (ResultsView's DetailText) stays for
/// the state tools and the Copy button.</summary>
internal static class ResultDetail
{
    /// <summary>What a result means, in one sentence a modder reads.</summary>
    internal static string Meaning(ResultRow r) => r.Group switch
    {
        ResultGroup.Addressed => "The patch edits this record. That alone does not show the clash is gone.",
        ResultGroup.NotAddressed => "The patch leaves this clash as it is.",
        ResultGroup.PatchChange => "The patch changes this, but no check predicted a clash here" + (r.Category switch
        {
            3 => ": it sets a new value.",
            4 => ": it forwards a value from another mod.",
            5 => "; the change is not measured.",
            _ => ".",
        }),
        _ => "The checks predicted these objects overlap. Overlaps are not counted as addressed or not addressed.",
    };

    /// <summary>A value as read: "x 21,273.9  y −7,263.2  z −3,435.6" for a position, each part of a list on its own line.</summary>
    internal static string Value(string text)
    {
        var parts = text.Split("; ", StringSplitOptions.RemoveEmptyEntries).Select(Part).ToList();
        return string.Join(Environment.NewLine, parts);

        static string Part(string p)
        {
            var m = Regex.Match(p, @"^(?<k>[A-Za-z][\w .]*)=(?<v>.*)$");
            string key = m.Success ? m.Groups["k"].Value : "", v = m.Success ? m.Groups["v"].Value : p;
            var nums = v.Split(',');
            string shown = nums.Length == 3 && nums.All(n => double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                ? string.Join(Environment.NewLine, new[] { "x", "y", "z" }.Zip(nums, (a, n) => $"{a} {double.Parse(n, NumberStyles.Float, CultureInfo.InvariantCulture).ToString("#,0.0", CultureInfo.InvariantCulture)}"))
                : v;
            return key.Length > 0 && !string.Equals(key, "Position", StringComparison.OrdinalIgnoreCase) && !string.Equals(key, "Rotation", StringComparison.OrdinalIgnoreCase)
                ? $"{key}: {shown}" : key.Length > 0 && nums.Length == 3 && string.Equals(key, "Rotation", StringComparison.OrdinalIgnoreCase) ? "turn " + shown : shown;
        }
    }

    /// <summary>A value's row label, in the order a modder reads them: what was there first, then each mod's, then the patch's.</summary>
    static (int Order, string Label) Role(string label) => label switch
    {
        "value beneath both" => (0, "Original"),
        "losing mod's value" => (1, "Overridden"),
        "winning mod's value" => (2, "Wins in game"),
        "before the patch" => (0, "Before the patch"),
        "the patch sets" => (1, "The patch sets"),
        _ => (3, char.ToUpperInvariant(label[0]) + label[1..]),
    };

    /// <summary>The pane for one row.</summary>
    internal static UIElement Build(ResultRow r, Action copy)
    {
        var dim = Theme.Brush(Theme.Dim);
        var sp = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };

        // what it is
        sp.Children.Add(new TextBlock { Text = RecordWords.Name(r.EditorId, r.BaseEditorId, r.FormKey), FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var what = new List<string> { RecordWords.Type(r.RecordType) + (r.BaseType is { Length: > 0 } b ? $" ({RecordWords.BaseType(b)})" : ""), RecordWords.Form(r.FormKey) };
        if (r.Where is { Length: > 0 } w) what.Add(w); else if (r.Place.Cell is { } c && r.RecordType != "CELL") what.Add("cell " + RecordWords.Form(c));
        sp.Children.Add(new TextBlock { Text = string.Join("  ·  ", what), Foreground = dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 10) });

        // the result and what it means
        var result = new StackPanel { Orientation = Orientation.Horizontal };
        var sq = ResultTableRow.Square(r.Group); sq.Margin = new Thickness(0, 0, 8, 0);
        result.Children.Add(sq);
        var words = r.Group == ResultGroup.PatchChange ? "Patch change" : ResultRules.Groups[r.Group].Words;
        result.Children.Add(new TextBlock { Text = char.ToUpperInvariant(words[0]) + words[1..], FontWeight = FontWeights.SemiBold });
        sp.Children.Add(result);
        sp.Children.Add(new TextBlock { Text = Meaning(r), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(19, 2, 0, 10) });

        // what clashes, in a sentence
        var values = OverlapWords.Values(r);
        string clash = r.Lane == "record" && values.FirstOrDefault(v => v.Label == "losing mod's value")?.Plugin is { } loser
                                          && values.FirstOrDefault(v => v.Label == "winning mod's value")?.Plugin is { } winner
            ? $"Both mods set its {RecordWords.Field(r.Field ?? r.Kind)}. {winner} wins; {loser}'s is overridden."
            : r.Lane == "edit" ? $"What the patch does: {RecordWords.Change(r.Kind)}."
            : values.FirstOrDefault(v => v.Label == "what the check says")?.Text ?? r.Summary;
        sp.Children.Add(new TextBlock { Text = clash, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });

        // the values side by side
        var rows = values.Where(v => v.Label != "what the check says").Select(v => (Role: Role(v.Label), v)).OrderBy(x => x.Role.Order).ToList();
        if (rows.Count > 0)
        {
            // two columns that both wrap, so a long plugin name never squeezes the values out: the role with its mod under it, then the value
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            int i = 0;
            foreach (var (role, v) in rows)
            {
                grid.RowDefinitions.Add(new RowDefinition());
                var who = new StackPanel { Margin = new Thickness(0, 4, 12, 4) };
                who.Children.Add(new TextBlock { Text = role.Label, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                if (v.Plugin is { Length: > 0 } plugin) who.Children.Add(new TextBlock { Text = plugin, Foreground = dim, TextWrapping = TextWrapping.Wrap });
                var value = new TextBlock { Text = Value(v.Text), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
                Grid.SetRow(who, i); Grid.SetRow(value, i); Grid.SetColumn(value, 1);
                grid.Children.Add(who); grid.Children.Add(value);
                i++;
            }
            sp.Children.Add(new Border { BorderBrush = Theme.Brush(Theme.Line), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 4, 0, 0), Child = grid });
        }

        if (r.JoinedEdits.Count > 0)
        {
            sp.Children.Add(new TextBlock { Text = "The patch's edits here", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
            foreach (var j in r.JoinedEdits) sp.Children.Add(new TextBlock { Text = j, TextWrapping = TextWrapping.Wrap, Foreground = dim });
        }
        if (r.Note is { } n && r.Lane != "overlap") sp.Children.Add(new TextBlock { Text = char.ToUpperInvariant(n[0]) + n[1..] + (n.EndsWith('.') ? "" : "."), TextWrapping = TextWrapping.Wrap, Foreground = dim, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 6, 0, 0) });

        var copyButton = new Button { Content = "Copy details", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0), ToolTip = "copy this row's full account as text" };
        copyButton.Click += (_, _) => copy();
        sp.Children.Add(copyButton);
        return sp;
    }
}
