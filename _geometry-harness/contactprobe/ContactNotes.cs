using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>Lane 3b labels on contactprobe rows, following <see cref="WindNotes"/>: an entry gains <c>pairFrame</c> (the origin a pair
/// was measured shifted by, when world placement collapsed a triangle) and <c>particlesA</c> / <c>particlesB</c> (particle systems set
/// aside from a member's render surface) only when there is something to say. An entry without either is returned as it was, not
/// re-serialised, so such rows are unchanged byte for byte.</summary>
static class ContactNotes
{
    /// <summary>The options contactprobe's review files are written with (camelCase, named floating-point literals).</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static object Attach(object entry, JsonSerializerOptions options, params (string Key, object? Value)[] notes)
    {
        if (notes.All(n => n.Value is null)) return entry;
        var node = entry as JsonObject ?? JsonSerializer.SerializeToNode(entry, entry.GetType(), options)!.AsObject();
        foreach (var (k, v) in notes)
            if (v is not null) node[k] = JsonSerializer.SerializeToNode(v, v.GetType(), options);
        return node;
    }

    public static double[]? Frame(SkyWright.Engine.Vec3 origin) => SkyWright.Engine.PairFrame.IsWorld(origin) ? null : new double[] { origin.X, origin.Y, origin.Z };
}
