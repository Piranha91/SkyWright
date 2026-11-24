using System.Buffers.Binary;
using System.Text;

using HousecarlCore;

namespace SkyWright.Engine;

// HAVOK BINARY PACKFILE READER (animated-object swept volume, tier 2). Reads the header, the section headers, and the
// data section's local, global and virtual fixups of a binary packfile, so objects are found by their class names and
// pointers by their fixups — object boundaries are never guessed. Accepts only what Skyrim SE's files are and what the
// field offsets were established for (decision record D5): magic 0x57E0E057 / 0x10C0C010, file version 8, layout
// (pointer size 8, little endian, reuse-padding 0, empty-base 1), contents version "hk_2010.2.0-r1". Anything else is a
// HavokFormatException naming what differs. Field offsets are taken from an independent layout reference
// (HKX2-Enhanced-Library) and pinned by known-answer tests; no code from it is used.
//
// STATUS: API frozen at the Gate B red gate; implemented.

public sealed class HavokFormatException(string message) : Exception(message);

/// <summary>One object of the data section: its offset (relative to the data section) and its class name.</summary>
public readonly record struct HavokObject(int Offset, string ClassName);

public sealed class HavokPackfile
{
    public const string SupportedVersion = "hk_2010.2.0-r1";

    public string ContentsVersion { get; private init; } = "";
    public int FileVersion { get; private init; }
    public int PointerSize { get; private init; }
    public IReadOnlyList<HavokObject> Objects { get; private init; } = Array.Empty<HavokObject>();

    byte[] _b = Array.Empty<byte>();
    int _data, _dataLen;
    readonly Dictionary<int, int> _local = new();
    readonly Dictionary<int, int> _global = new();
    readonly Dictionary<int, HavokObject> _byOffset = new();

    HavokPackfile() { }

    static int I32(byte[] b, int o) => o >= 0 && o + 4 <= b.Length ? BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o)) : throw new HavokFormatException($"read past the end of the file at {o}");

    /// <summary>Parse a packfile. Throws <see cref="HavokFormatException"/> naming the unsupported or malformed part.</summary>
    public static HavokPackfile Read(byte[] bytes)
    {
        if (bytes is null || bytes.Length < 64) throw new HavokFormatException("not a Havok binary packfile: shorter than the 64-byte header (magic missing)");
        if ((uint)I32(bytes, 0) != 0x57E0E057u || (uint)I32(bytes, 4) != 0x10C0C010u)
            throw new HavokFormatException($"not a Havok binary packfile: magic {(uint)I32(bytes, 0):X8}/{(uint)I32(bytes, 4):X8}");
        int fileVersion = I32(bytes, 12);
        if (fileVersion != 8) throw new HavokFormatException($"unsupported packfile file version {fileVersion} (8 is supported)");
        byte ptr = bytes[16], le = bytes[17], reuse = bytes[18], emptyBase = bytes[19];
        if (ptr != 8 || le != 1 || reuse != 0 || emptyBase != 1)
            throw new HavokFormatException($"unsupported packfile layout (pointer {ptr}, little-endian {le}, reuse-padding {reuse}, empty-base {emptyBase}); (8,1,0,1) is supported");
        int numSections = I32(bytes, 20), contents = I32(bytes, 24), cnSection = I32(bytes, 32);
        int z = Array.IndexOf(bytes, (byte)0, 40, 16);
        string version = Encoding.ASCII.GetString(bytes, 40, (z < 0 ? 56 : z) - 40);
        if (version != SupportedVersion) throw new HavokFormatException($"unsupported Havok version '{version}' ({SupportedVersion} is supported)");
        if (numSections < 1 || numSections > 16 || contents < 0 || contents >= numSections || cnSection < 0 || cnSection >= numSections)
            throw new HavokFormatException($"malformed section table ({numSections} sections, contents {contents}, class names {cnSection})");
        var secs = new (int Start, int Local, int Global, int Virt, int Exports, int Imports, int End)[numSections];
        for (int s = 0; s < numSections; s++)
        {
            int o = 64 + 48 * s;
            secs[s] = (I32(bytes, o + 20), I32(bytes, o + 24), I32(bytes, o + 28), I32(bytes, o + 32), I32(bytes, o + 36), I32(bytes, o + 40), I32(bytes, o + 44));
        }
        // class names: (u32 signature, 0x09, name\0) records in the class-name section's data
        var names = new Dictionary<int, string>();
        var cn = secs[cnSection];
        for (int o = cn.Start; o + 5 < cn.Start + cn.Local && bytes[o + 4] == 0x09;)
        {
            int e = Array.IndexOf(bytes, (byte)0, o + 5);
            if (e < 0) throw new HavokFormatException("class-name record without a terminator");
            names[o + 5 - cn.Start] = Encoding.ASCII.GetString(bytes, o + 5, e - o - 5);
            o = e + 1;
        }
        var d = secs[contents];
        if (d.Start < 0 || d.Start + d.Exports > bytes.Length + 12) throw new HavokFormatException("data section outside the file");
        var local = new Dictionary<int, int>(); var global = new Dictionary<int, int>(); var byOffset = new Dictionary<int, HavokObject>();
        for (int o = d.Start + d.Local; o + 8 <= d.Start + d.Global; o += 8)
        {
            int src = I32(bytes, o); if (src == -1) continue;
            local[src] = I32(bytes, o + 4);
        }
        for (int o = d.Start + d.Global; o + 12 <= d.Start + d.Virt; o += 12)
        {
            int src = I32(bytes, o); if (src == -1) continue;
            if (I32(bytes, o + 4) != contents) throw new HavokFormatException("global fixup into another section is not supported");
            global[src] = I32(bytes, o + 8);
        }
        var objects = new List<HavokObject>();
        int vEnd = Math.Min(d.Start + d.Exports, bytes.Length);
        for (int o = d.Start + d.Virt; o + 12 <= vEnd; o += 12)
        {
            int src = I32(bytes, o); if (src == -1) continue;
            int nameOff = I32(bytes, o + 8);
            objects.Add(new HavokObject(src, names.TryGetValue(nameOff, out var n) ? n : throw new HavokFormatException($"virtual fixup names an unknown class at {nameOff}")));
        }
        objects.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        foreach (var ob in objects) byOffset[ob.Offset] = ob;
        return new HavokPackfile
        {
            ContentsVersion = version, FileVersion = fileVersion, PointerSize = ptr, Objects = objects,
            _b = bytes, _data = d.Start, _dataLen = d.Local,
        }.With(local, global, byOffset);
    }

    HavokPackfile With(Dictionary<int, int> local, Dictionary<int, int> global, Dictionary<int, HavokObject> byOffset)
    {
        foreach (var kv in local) _local[kv.Key] = kv.Value;
        foreach (var kv in global) _global[kv.Key] = kv.Value;
        foreach (var kv in byOffset) _byOffset[kv.Key] = kv.Value;
        return this;
    }

    int Abs(int rel, int size)
    {
        if (rel < 0 || rel + size > _dataLen) throw new HavokFormatException($"field at data offset {rel} lies outside the data");
        return _data + rel;
    }

    /// <summary>The objects of one class, in data order.</summary>
    public IEnumerable<HavokObject> OfClass(string className) => Objects.Where(o => o.ClassName == className);

    /// <summary>The object a pointer field at (object + fieldOffset) points to through a global fixup, or null.</summary>
    public HavokObject? Pointer(HavokObject obj, int fieldOffset) =>
        _global.TryGetValue(obj.Offset + fieldOffset, out int dst) && _byOffset.TryGetValue(dst, out var o) ? o : null;

    string? CString(int rel)
    {
        int a = Abs(rel, 1);
        int e = Array.IndexOf(_b, (byte)0, a, _data + _dataLen - a);
        if (e < 0) throw new HavokFormatException($"unterminated string at data offset {rel}");
        return Encoding.ASCII.GetString(_b, a, e - a);
    }

    /// <summary>The C string a string-pointer field points to through a local fixup, or null when the pointer is null.</summary>
    public string? String(HavokObject obj, int fieldOffset) =>
        _local.TryGetValue(obj.Offset + fieldOffset, out int dst) ? CString(dst) : null;

    int ArrayCount(HavokObject obj, int fieldOffset) => BinaryPrimitives.ReadInt32LittleEndian(_b.AsSpan(Abs(obj.Offset + fieldOffset + 8, 4)));

    /// <summary>The raw bytes of an hkArray field's elements (size × elementSize), empty for a null array.</summary>
    public ReadOnlyMemory<byte> ArrayBytes(HavokObject obj, int fieldOffset, int elementSize)
    {
        int n = ArrayCount(obj, fieldOffset);
        if (n == 0 || !_local.TryGetValue(obj.Offset + fieldOffset, out int dst)) return ReadOnlyMemory<byte>.Empty;
        if (n < 0) throw new HavokFormatException($"negative array size at data offset {obj.Offset + fieldOffset}");
        return new ReadOnlyMemory<byte>(_b, Abs(dst, n * elementSize), n * elementSize);
    }

    /// <summary>The string elements of an hkArray of string pointers.</summary>
    public IReadOnlyList<string?> StringArray(HavokObject obj, int fieldOffset)
    {
        int n = ArrayCount(obj, fieldOffset);
        if (n == 0 || !_local.TryGetValue(obj.Offset + fieldOffset, out int dst)) return Array.Empty<string?>();
        var r = new List<string?>(n);
        for (int i = 0; i < n; i++) r.Add(_local.TryGetValue(dst + 8 * i, out int s) ? CString(s) : null);
        return r;
    }

    /// <summary>The string pointer at offset 0 of each element of an hkArray of structs with the given stride.</summary>
    public IReadOnlyList<string?> StructStrings(HavokObject obj, int fieldOffset, int stride, int expectedCount)
    {
        int n = ArrayCount(obj, fieldOffset);
        if (n != expectedCount) throw new HavokFormatException($"array at data offset {obj.Offset + fieldOffset} has {n} elements, {expectedCount} expected");
        if (n == 0 || !_local.TryGetValue(obj.Offset + fieldOffset, out int dst)) return Array.Empty<string?>();
        var r = new List<string?>(n);
        for (int i = 0; i < n; i++) r.Add(_local.TryGetValue(dst + stride * i, out int s) ? CString(s) : null);
        return r;
    }

    /// <summary>The objects an hkArray of class pointers points to.</summary>
    public IReadOnlyList<HavokObject?> PointerArray(HavokObject obj, int fieldOffset)
    {
        int n = ArrayCount(obj, fieldOffset);
        if (n == 0 || !_local.TryGetValue(obj.Offset + fieldOffset, out int dst)) return Array.Empty<HavokObject?>();
        var r = new List<HavokObject?>(n);
        for (int i = 0; i < n; i++) r.Add(_global.TryGetValue(dst + 8 * i, out int o) && _byOffset.TryGetValue(o, out var ob) ? ob : null);
        return r;
    }

    public int Int32(HavokObject obj, int fieldOffset) => BinaryPrimitives.ReadInt32LittleEndian(_b.AsSpan(Abs(obj.Offset + fieldOffset, 4)));
    public short Int16(HavokObject obj, int fieldOffset) => BinaryPrimitives.ReadInt16LittleEndian(_b.AsSpan(Abs(obj.Offset + fieldOffset, 2)));
    public sbyte SByte(HavokObject obj, int fieldOffset) => (sbyte)_b[Abs(obj.Offset + fieldOffset, 1)];
    public float Single(HavokObject obj, int fieldOffset) => BinaryPrimitives.ReadSingleLittleEndian(_b.AsSpan(Abs(obj.Offset + fieldOffset, 4)));
}
