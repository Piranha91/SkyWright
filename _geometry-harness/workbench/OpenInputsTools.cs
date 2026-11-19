using ModelContextProtocol.Protocol;

namespace Workbench;

/// <summary>The state tools' side of the Results and World tabs: the <c>results</c> and <c>world</c> verbs of
/// <c>workbench_act</c> and the parts of the same names of <c>workbench_state</c>. Each calls what the tab's own controls call.</summary>
internal static class OpenInputsTools
{
    static CallToolResult Answer(object o) => new() { Content = [new TextContentBlock { Text = System.Text.Json.JsonSerializer.Serialize(o, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) }] };

    /// <summary>results: choose the pair, groups, categories, setup and text, and with <paramref name="row"/> open that row.
    /// world: search the cells, or open a cell or the cell of a record.</summary>
    internal static CallToolResult Act(string verb, string? pair, string[]? groups, int[]? categories, string? setup, string? search, string? row, string? cell, string? refKey)
    {
        try
        {
            if (verb == "results")
            {
                var said = pair is null && groups is null && categories is null && setup is null && search is null ? null : ResultsView.Choose(pair, groups, categories, setup, search);
                if (row is not null) said = ResultsView.OpenRow(ResultsView.RowOf(row));
                if (said is null) return StateTools.Refuse("results needs pair, groups, categories, setup, search, or row (a row's key).");
                return Answer(new { said, results = ResultsView.State(20, 0) });
            }
            string w;
            if (cell is not null) w = WorldView.OpenCell(cell, refKey);
            else if (refKey is not null) w = WorldView.PickKey(refKey);
            else if (search is not null) w = WorldView.Search(search);
            else return StateTools.Refuse("world needs cell (a cell's FormKey), ref (a FormKey of any record in a cell) or search.");
            return Answer(new { said = w, world = WorldView.State(null, 20, 0) });
        }
        catch (InvalidOperationException ex) { return StateTools.Refuse(ex.Message); }
    }

    /// <summary>The map verb: set the World tab map's worldspace, centre, zoom and Textured checkbox.</summary>
    internal static CallToolResult Map(string? worldspace, double[]? centre, double? pixelsPerCell, bool? textured)
    {
        try
        {
            if (worldspace is null && centre is null && pixelsPerCell is null && textured is null)
                return StateTools.Refuse("map needs worldspace, map_centre, pixels_per_cell or textured.");
            var said = WorldView.SetMapView(worldspace, centre, pixelsPerCell, textured);
            return Answer(new { said, world = WorldView.State(null, 0, 0) });
        }
        catch (InvalidOperationException ex) { return StateTools.Refuse(ex.Message); }
    }
}
