using System.Text.Json;
using Mutagen.Bethesda.Plugins;

/// <summary>
/// Whether a declared scene cell can exist in the evaluated setup. Only the owning plugin of a FormKey, or a
/// plugin that lists it as a master, can declare that FormKey; when the setup neither loads nor masters the
/// owner, the cell does not exist there and nothing is placed in it in game. This is the same rule, from the same
/// plugin list and masters, that overlap_attribution.py applies to objects (owning-plugin-not-loaded).
///
/// An imported scope's cell with an absent owner bounds nothing here and is recorded, not refused: comparing a
/// setup without the candidate against the candidate's own scene is the point of importing it. A cell typed with
/// --scene-cell is the operator's own bound, so an absent owner there is refused.
/// </summary>
static class DeclaredCellPresence
{
    internal const string Present = "present";
    internal const string Absent = "absent";
    internal const string Refuse = "refuse";
    internal const string TypedOrigin = "--scene-cell";
    internal const string Basis = "owning-plugin-not-loaded";

    internal sealed record Row(FormKey Cell, string OwningPlugin, string DeclaredBy, string Outcome);

    /// <summary>Every plugin the setup loads plus every master any of them lists, ignoring case.</summary>
    internal static HashSet<string> Declarable(IEnumerable<(string Name, IReadOnlyList<string> Masters)> plugins)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, masters) in plugins)
        {
            set.Add(name);
            foreach (var m in masters) set.Add(m);
        }
        return set;
    }

    internal static Row Classify(FormKey cell, string declaredBy, IReadOnlySet<string> declarable)
    {
        string owner = cell.ModKey.FileName.String;
        string outcome = declarable.Contains(owner) ? Present : declaredBy == TypedOrigin ? Refuse : Absent;
        return new Row(cell, owner, declaredBy, outcome);
    }

    internal static object AbsentRow(Row r) => new { cell = r.Cell.ToString(), owningPlugin = r.OwningPlugin, declaredBy = r.DeclaredBy, basis = Basis };

    internal static string SkipLine(Row r) =>
        $"{r.DeclaredBy} declares cell {r.Cell}, owned by {r.OwningPlugin}, which this setup neither loads nor masters: the cell does not exist here and bounds nothing";

    internal static string RefuseLine(Row r) =>
        $"{r.DeclaredBy} declares cell {r.Cell}, owned by {r.OwningPlugin}, which this setup neither loads nor masters, so the cell does not exist here; load {r.OwningPlugin} or leave the cell out";

    /// <summary>--self-check-declared-cells FIXTURE: {plugins:[{Name,Masters}], declared:[{cell,declaredBy}]}; prints the outcome per cell.</summary>
    internal static int SelfCheck(string fixturePath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(fixturePath));
            var plugins = doc.RootElement.GetProperty("plugins").EnumerateArray()
                .Select(p => (p.GetProperty("Name").GetString()!,
                              (IReadOnlyList<string>)p.GetProperty("Masters").EnumerateArray().Select(m => m.GetString()!).ToList()))
                .ToList();
            var declarable = Declarable(plugins);
            var rows = doc.RootElement.GetProperty("declared").EnumerateArray()
                .Select(d => Classify(FormKey.Factory(d.GetProperty("cell").GetString()!), d.GetProperty("declaredBy").GetString()!, declarable))
                .DistinctBy(r => r.Cell)
                .OrderBy(r => r.Cell.ToString(), StringComparer.Ordinal)
                .ToList();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                cells = rows.Select(r => new { cell = r.Cell.ToString(), owningPlugin = r.OwningPlugin, declaredBy = r.DeclaredBy, outcome = r.Outcome }).ToArray(),
                absentDeclaredCells = rows.Where(r => r.Outcome == Absent).Select(AbsentRow).ToArray(),
                presentCount = rows.Count(r => r.Outcome == Present),
                absentCount = rows.Count(r => r.Outcome == Absent),
                refuseCount = rows.Count(r => r.Outcome == Refuse),
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not run --self-check-declared-cells on '{fixturePath}': {ex.Message}");
            return 2;
        }
    }
}
