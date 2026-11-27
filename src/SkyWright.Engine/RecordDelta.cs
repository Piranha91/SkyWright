namespace SkyWright.Engine;

// STUB (record checks unit 2, the red point): reads only "<field>=<own> (<plugin> <other>)". The real parser replaces it.

/// <summary>What a houseCARL delta line says about one field: a value on both sides, a field the other side lacks
/// (Absent), a field this side lacks (OwnAbsent), a list in another order (Order), or a list compared by its content (List).</summary>
public enum DeltaKind { Value, Absent, OwnAbsent, Order, List }

/// <summary>One delta line read: the field path, this side's value, the other plugin (in the order's spelling), its value,
/// and what kind of difference it is.</summary>
public sealed record DeltaLine(string Field, string? Own, string Other, string? OtherValue, DeltaKind Kind);

public static class RecordDelta
{
    public static string? Spelling(string? name, IReadOnlyList<string> plugins) =>
        name is null ? null : plugins.FirstOrDefault(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));

    public static string Top(string field) => field.Split('.')[0].Split('[')[0];

    public static DeltaLine? Parse(string line, IReadOnlyList<string> plugins)
    {
        int eq = line.IndexOf('=');
        if (eq < 0 || !line.EndsWith(')')) return null;
        string field = line[..eq], rest = line[(eq + 1)..];
        foreach (var p in plugins)
        {
            int i = rest.LastIndexOf(" (" + p + " ", StringComparison.OrdinalIgnoreCase);
            if (i >= 0) return new DeltaLine(field, rest[..i], p, rest[(i + p.Length + 3)..^1], DeltaKind.Value);
        }
        return null;
    }
}
