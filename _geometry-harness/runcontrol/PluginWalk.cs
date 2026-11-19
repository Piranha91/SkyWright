using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace RunControl;

/// <summary>bundleenrich.py's raw walk of a plugin file's group tree (filed_cells, _records, _position,
/// cell_overrides): which cell a placed reference is filed in, and the height changes of the references a plugin
/// overrides in a cell. A copy of what the record library knows, kept because names.json carries its output; no
/// owner is called yet.</summary>
public static class PluginWalk
{
    static readonly HashSet<uint> Placed = new(new[] { "REFR", "ACHR", "PGRE", "PHZD", "PMIS", "PARW", "PBAR", "PBEA", "PCON", "PFLA" }.Select(Sig));

    static uint Sig(string s) => BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(s));
    static readonly uint Tes4 = Sig("TES4"), Grup = Sig("GRUP"), Mast = Sig("MAST"), Data = Sig("DATA");
    static readonly Encoding Cp1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

    static uint U32(byte[] d, long at) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(checked((int)at), 4));
    static ushort U16(byte[] d, long at) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(checked((int)at), 2));

    /// <summary>One plugin file read whole: its masters then its own name, and where its groups start. Names is
    /// empty for a file that is not a plugin.</summary>
    public sealed class Plugin
    {
        public byte[] Bytes { get; }
        public List<string> Names { get; } = [];
        public long Start { get; }
        public bool IsPlugin { get; }

        public Plugin(string path)
        {
            Bytes = File.ReadAllBytes(path);
            IsPlugin = Bytes.Length >= 4 && U32(Bytes, 0) == Tes4;
            if (!IsPlugin) return;
            long hsize = U32(Bytes, 4), q = 24;
            while (q < 24 + hsize)
            {
                int ssz = U16(Bytes, q + 4);
                if (U32(Bytes, q) == Mast)
                {
                    int end = (int)Math.Min(q + 6 + ssz, Bytes.Length), from = (int)q + 6;
                    while (end > from && Bytes[end - 1] == 0) end--;
                    Names.Add(Cp1252.GetString(Bytes, from, end - from));
                }
                q += 6 + ssz;
            }
            Names.Add(PyPath.Name(path));
            Start = 24 + hsize;
        }

        /// <summary>"LLLLLL:Plugin" for a raw FormID by this file's master list, or null past its end.</summary>
        public string? FormKey(uint raw) => raw >> 24 < Names.Count ? $"{raw & 0xFFFFFF:X6}:{Names[(int)(raw >> 24)]}" : null;
    }

    /// <summary>One placed record met in the walk: its header fields, where its body lies, and the groups around it
    /// (type, label), outermost first.</summary>
    public readonly record struct PlacedRecord(uint FormId, uint Flags, long Body, long Size, IReadOnlyList<(int type, uint label)> Stack);

    // Every placed record of the file in file order; groups are never compressed, so record headers suffice.
    static IEnumerable<PlacedRecord> Walk(Plugin p)
    {
        if (!p.IsPlugin) yield break;
        var d = p.Bytes;
        var stack = new List<(int type, uint label)>();
        var ends = new List<long>();
        long pos = p.Start, end = d.Length;
        while (true)
        {
            while (pos >= end && ends.Count > 0)
            {
                // a group ends where its own size says, wherever its children stopped
                pos = ends[^1];
                ends.RemoveAt(ends.Count - 1);
                stack.RemoveAt(stack.Count - 1);
                end = ends.Count > 0 ? ends[^1] : d.Length;
            }
            if (pos >= end) yield break;
            uint sig = U32(d, pos);
            long size = U32(d, pos + 4);
            if (sig == Grup)
            {
                stack.Add(((int)U32(d, pos + 12), U32(d, pos + 8)));
                ends.Add(pos + size);
                end = pos + size;
                pos += 24;
                continue;
            }
            if (Placed.Contains(sig))
                yield return new PlacedRecord(U32(d, pos + 12), U32(d, pos + 8), pos + 24, size, stack.ToArray());
            pos += 24 + size;
        }
    }

    // The innermost cell-children group's label, or null outside one.
    static uint? CellOf(IReadOnlyList<(int type, uint label)> stack)
    {
        for (int i = stack.Count - 1; i >= 0; i--)
            if (stack[i].type == 6) return stack[i].label;
        return null;
    }

    /// <summary>{FormKey: cell FormKey} for the placed references of fks this file carries; a reference filed in a
    /// worldspace's persistent cell maps to that cell with "!persistent" after it. In file order.</summary>
    public static PyDict FiledCells(Plugin p, IEnumerable<string> fks)
    {
        var found = new PyDict();
        if (!p.IsPlugin) return found;
        var low = p.Names.Select(PyText.Lower).ToList();
        var want = new Dictionary<uint, string>();
        foreach (string fk in fks)
        {
            int colon = fk.IndexOf(':');
            int index = low.IndexOf(PyText.Lower(fk[(colon + 1)..]));
            if (index >= 0) want[((uint)index << 24) | Convert.ToUInt32(fk[..colon], 16)] = fk;
        }
        foreach (var r in Walk(p))
        {
            if (!want.TryGetValue(r.FormId, out string? fk)) continue;
            var s = r.Stack;
            bool persistent = s.Count >= 2 && s[^2].type == 6 && s.Any(g => g.type == 1) && !s.Any(g => g.type is 4 or 5);
            if (CellOf(s) is uint cell && p.FormKey(cell) is string cellKey) found[fk] = cellKey + (persistent ? "!persistent" : "");
        }
        return found;
    }

    // A placed record's body, inflated when the record is compressed; empty when it does not inflate.
    static byte[] Body(Plugin p, PlacedRecord r)
    {
        var raw = p.Bytes.AsSpan(checked((int)r.Body), checked((int)r.Size));
        if ((r.Flags & 0x40000) == 0) return raw.ToArray();
        try
        {
            using var z = new ZLibStream(new MemoryStream(raw[4..].ToArray()), CompressionMode.Decompress);
            using var plain = new MemoryStream();
            z.CopyTo(plain);
            return plain.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException) { return []; }
    }

    /// <summary>A placed record's DATA position, each number rounded to three places, or null.</summary>
    public static double[]? Position(byte[] body)
    {
        int q = 0;
        while (q + 6 <= body.Length)
        {
            int ssz = U16(body, q + 4);
            if (U32(body, q) == Data && ssz >= 12)
                return [.. Enumerable.Range(0, 3).Select(i => PyText.Round(BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(q + 6 + 4 * i, 4)), 3))];
            q += 6 + ssz;
        }
        return null;
    }

    /// <summary>{own FormKey lower-cased: position} for every placed record the file itself defines.</summary>
    public static Dictionary<string, double[]?> OwnPositions(Plugin p)
    {
        var pos = new Dictionary<string, double[]?>(StringComparer.Ordinal);
        foreach (var r in Walk(p))
            if (r.FormId >> 24 == p.Names.Count - 1)
                pos[PyText.Lower($"{r.FormId & 0xFFFFFF:X6}:{p.Names[^1]}")] = Position(Body(p, r));
        return pos;
    }

    /// <summary>{dz, disabled, count} for the references this file overrides inside one cell: each one's height
    /// change against basePositions(FormKey), and how many it turns off. Null when it overrides none there.</summary>
    public static PyDict? CellOverrides(Plugin p, string cell, Func<string, double[]?> basePositions)
    {
        PyDict? e = null;
        int own = p.Names.Count - 1;
        foreach (var r in Walk(p))
        {
            uint index = r.FormId >> 24;
            if (index >= own || index >= p.Names.Count) continue;   // the file's own new references are not overrides
            if (CellOf(r.Stack) is not uint c || c >> 24 >= p.Names.Count || p.FormKey(c) != cell) continue;
            e ??= new PyDict { ["dz"] = new List<object?>(), ["disabled"] = 0L, ["count"] = 0L };
            e["count"] = (long)e["count"]! + 1;
            if ((r.Flags & 0x800) != 0 || (r.Flags & 0x20) != 0)
            {
                e["disabled"] = (long)e["disabled"]! + 1;
                continue;
            }
            var pos = Position(Body(p, r));
            var basePos = basePositions(p.FormKey(r.FormId)!);
            if (pos != null && basePos != null) ((List<object?>)e["dz"]!).Add(PyText.Round(pos[2] - basePos[2], 1));
        }
        return e;
    }
}
