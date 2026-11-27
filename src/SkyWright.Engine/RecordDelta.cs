using System.Text.RegularExpressions;

namespace SkyWright.Engine;

// THE ONE READER OF houseCARL's DELTA LINES in SkyWright (record checks Gate A, ruling R2). houseCARL's record tool
// (housecarl_records, forms tree and delta) owns what differs and writes one line per difference; this class reads a line
// into its field, the two values, the other plugin and the kind of difference. A line it cannot read is null, and the
// caller counts it as not judged, never as no difference. Ported from the module's evalrecords.parse_delta (which goes at
// Gate D); the line shapes are FieldsDiff's in the pin:
//   "<f>=<own> (<P> <other>)"                 a value on both sides
//   "<f>=<own> (<P> has no <...>)"            the other side has no such field (also "(<P> has <f> ABSENT)")
//   "<f>: ABSENT here (<P> has <other>)"      this side has no such field ("<f> only in <P>: <other>" too)
//   "<f>: same <n> item(s), ORDER DIFFERS from <P>"   the same items in another order
//   "<f>: <own> vs <P> <n> item(s)"           a list compared by its content, not itemised
// A plugin is matched without case, as the game and Windows match it, and returned in the order's spelling.

/// <summary>What a houseCARL delta line says about one field: a value on both sides, a field the other side lacks
/// (Absent), a field this side lacks (OwnAbsent), a list in another order (Order), or a list compared by its content (List).</summary>
public enum DeltaKind { Value, Absent, OwnAbsent, Order, List }

/// <summary>One delta line read: the field path, this side's value, the other plugin (in the order's spelling), its value,
/// and what kind of difference it is.</summary>
public sealed record DeltaLine(string Field, string? Own, string Other, string? OtherValue, DeltaKind Kind);

public static class RecordDelta
{
    static readonly Regex OwnAbsentLine = new(@"^([A-Za-z0-9_.\[\]]+): ABSENT here \((.+?) has (.*)\)$", RegexOptions.CultureInvariant);
    static readonly Regex OnlyInLine = new(@"^([A-Za-z0-9_.\[\]]+) only in (.+?): (.*)$", RegexOptions.CultureInvariant);
    static readonly Regex OrderLine = new(@"^([A-Za-z0-9_.\[\]]+): same \d+ item\(s\), ORDER DIFFERS from (.+)$", RegexOptions.CultureInvariant);
    static readonly Regex ListLine = new(@"^([A-Za-z0-9_.\[\]]+): (.*) vs (.+?) (\S+) item\(s\)", RegexOptions.CultureInvariant);

    /// <summary>The order's spelling of a plugin name, matched without case; null when the order does not load it.</summary>
    public static string? Spelling(string? name, IReadOnlyList<string> plugins) =>
        name is null ? null : plugins.FirstOrDefault(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A field path's top-level field: "Placement" for "Placement.Position", "Items" for "Items[2].Count".</summary>
    public static string Top(string field) => field.Split('.')[0].Split('[')[0];

    /// <summary>One line read, or null when it is of no shape this reader knows.</summary>
    public static DeltaLine? Parse(string line, IReadOnlyList<string> plugins)
    {
        if (OwnAbsentLine.Match(line) is { Success: true } m)
            return new DeltaLine(m.Groups[1].Value, null, Spelling(m.Groups[2].Value, plugins) ?? m.Groups[2].Value, m.Groups[3].Value, DeltaKind.OwnAbsent);
        if (OnlyInLine.Match(line) is { Success: true } o && Spelling(o.Groups[2].Value, plugins) is { } op)
            return new DeltaLine(o.Groups[1].Value, null, op, o.Groups[3].Value, DeltaKind.OwnAbsent);
        if (OrderLine.Match(line) is { Success: true } r && Spelling(r.Groups[2].Value, plugins) is { } rp)
            return new DeltaLine(r.Groups[1].Value, null, rp, null, DeltaKind.Order);
        if (ListLine.Match(line) is { Success: true } l)
        {
            string named = l.Groups[3].Value;
            string other = plugins.FirstOrDefault(p => named.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ?? named;
            return new DeltaLine(l.Groups[1].Value, l.Groups[2].Value, other, l.Groups[4].Value, DeltaKind.List);
        }
        int eq = line.IndexOf('=');
        if (eq < 0) return null;
        string field = line[..eq], rest = line[(eq + 1)..];
        if (!rest.EndsWith(')')) return null;
        // the longest name first, so a plugin whose name begins another's is not taken for it
        foreach (var p in plugins.OrderByDescending(p => p.Length))
            foreach (var (marker, kind) in new[] { ($" ({p} has no ", DeltaKind.Absent), ($" ({p} has {field} ABSENT", DeltaKind.Absent), ($" ({p} ", DeltaKind.Value) })
            {
                // the last occurrence, without case, at its place in the original text
                int i = rest.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (i < 0) continue;
                string own = rest[..i];
                string? other = kind == DeltaKind.Absent ? null : rest[(i + marker.Length)..^1];
                return new DeltaLine(field, own, p, other, kind);
            }
        return null;
    }
}
