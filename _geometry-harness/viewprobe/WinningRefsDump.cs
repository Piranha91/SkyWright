using System.Security.Cryptography;
using System.Text.Json;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

namespace ViewProbe;

// --winning-refs-dump FILE: the winning state the real load-order walk produced for one target cell, written as one JSON
// line, and nothing drawn. It exists so the walk can be checked, reference by reference, against an independent reader of
// the same plugins, over many cells, without paying for a render.
//
// It FILTERS NOTHING and decides nothing: every entry of the walk's winning-reference table is written as the walk holds
// it (position, the cell the walk says it came from, persistent and deleted flags, whether it is a persistent-merge
// candidate), with the target's identity as the prepass resolved it and the exact plugin files walked. Judging it is the
// caller's job. With --dump-terrain the line also carries the winning heightmaps of the loaded grid squares, which the
// terrain walk produces later in the same run.
internal static class WinningRefsDump
{
    static readonly Dictionary<string, string> ShaCache = new(StringComparer.OrdinalIgnoreCase);

    static string Sha256Of(string path)
    {
        if (ShaCache.TryGetValue(path, out var s)) return s;
        using var fs = File.OpenRead(path);
        return ShaCache[path] = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>The cells a multi-target dump walks: one cell (FormKey, FormID or EditorID) per line; blank lines and
    /// lines starting with '#' are skipped.</summary>
    internal static List<string> ReadCells(string path) =>
        File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();

    internal static void Write(string outPath, string cellTarget, FormKey cellKey, bool interior, FormKey worldspace,
                               (int X, int Y)? grid, bool ringRefs, IReadOnlyList<string> orderedPaths,
                               IReadOnlyDictionary<FormKey, PlacedRef> winRef,
                               IReadOnlyDictionary<FormKey, FormKey> refCellOf,
                               IReadOnlyDictionary<FormKey, (FormKey Ws, PlacedRef Ref)> persistentWin,
                               IEnumerable<FormKey>? takenOut,
                               IReadOnlyDictionary<(int X, int Y), float[,]>? landHeights,
                               IReadOnlyDictionary<(int X, int Y), int>? landLayerCounts)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("target", cellTarget);
            w.WriteString("cell", cellKey.ToString());
            w.WriteBoolean("interior", interior);
            w.WriteString("worldspace", worldspace.IsNull ? null : worldspace.ToString());
            if (grid is { } g) { w.WriteStartArray("grid"); w.WriteNumberValue(g.X); w.WriteNumberValue(g.Y); w.WriteEndArray(); }
            else w.WriteNull("grid");
            w.WriteBoolean("ring", ringRefs);
            w.WriteStartArray("plugins");
            foreach (var p in orderedPaths)
            {
                w.WriteStartObject();
                w.WriteString("path", Path.GetFullPath(p));
                w.WriteString("sha256", Sha256Of(p));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("refs");
            foreach (var (k, r) in winRef.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("formKey", k.ToString());
                w.WriteStartArray("pos");
                w.WriteNumberValue(r.Position.X); w.WriteNumberValue(r.Position.Y); w.WriteNumberValue(r.Position.Z);
                w.WriteEndArray();
                w.WriteString("cell", refCellOf.TryGetValue(k, out var c) ? c.ToString() : null);
                w.WriteBoolean("persistent", r.Persistent);
                w.WriteBoolean("deleted", r.Deleted);
                w.WriteBoolean("mergeCandidate", persistentWin.ContainsKey(k));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (takenOut is null) w.WriteNull("takenOut");
            else
            {
                w.WriteStartArray("takenOut");
                foreach (var k in takenOut.Select(k => k.ToString()).OrderBy(s => s, StringComparer.Ordinal)) w.WriteStringValue(k);
                w.WriteEndArray();
            }
            if (landHeights is not null)
            {
                w.WriteStartArray("terrain");
                foreach (var ((gx, gy), h) in landHeights.OrderBy(x => x.Key.Y).ThenBy(x => x.Key.X))
                {
                    w.WriteStartObject();
                    w.WriteStartArray("grid"); w.WriteNumberValue(gx); w.WriteNumberValue(gy); w.WriteEndArray();
                    var bytes = new byte[h.Length * 4];
                    Buffer.BlockCopy(h, 0, bytes, 0, bytes.Length);
                    w.WriteString("heightsSha256", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
                    w.WriteNumber("h00", h[0, 0]);
                    w.WriteNumber("h1616", h[16, 16]);
                    if (landLayerCounts is not null && landLayerCounts.TryGetValue((gx, gy), out var n)) w.WriteNumber("layers", n);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        using var f = new FileStream(outPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        f.Write(ms.ToArray());
        f.WriteByte((byte)'\n');
    }
}
