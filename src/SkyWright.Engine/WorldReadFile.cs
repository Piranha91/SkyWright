using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SkyWright.Engine;

// ======================================================================
//  WorldReadFile — ONE PLUGIN'S PREPARED FILE for the shared world read.
//
//  Layout (little-endian):
//    header   magic "SWWORLD1", schema (int32), Engine module version id (16 bytes), the plugin's content sha256 (32 bytes),
//             total file length (int64), section count (int32), then per section: kind (int32), offset (int64),
//             length (int64), sha256 of its bytes (32 bytes)
//    sections Placements (every cell's placed records, one blob per cell), Meta (plugin name, key table), Cells,
//             Directory, Bases; a later schema adds kinds (navmesh, land, more base facts) to the table
//    end      "SWWEND!!"
//  A file whose header, length, end marker or a loaded section's hash does not match is not read: it is rebuilt from the
//  plugin. A cell blob is checked against its own hash when it is read. Every FormKey is stored against the file's own
//  key table (the plugin's masters and itself as Mutagen resolved them), so the file does not depend on the load order.
// ======================================================================

/// <summary>One placed record as a prepared file holds it.</summary>
readonly record struct WorldEntry(FormKey Key, PlacedRef? Ref, bool Persistent, bool Deleted);

/// <summary>One cell as one plugin's prepared file holds it.</summary>
sealed record WorldFileCell(FormKey Cell, string? EditorId, bool Interior, FormKey? Worldspace, (int X, int Y)? Grid, bool IsPersistentCell,
                            long Offset, int Length, ulong Check);

/// <summary>One base record's facts as one plugin's prepared file holds them.</summary>
readonly record struct WorldFileBase(string TypeName, string? EditorId, string? Model, string? FemaleModel, Vec3? Obnd, bool IsMarker, bool LightCanBeCarried);

/// <summary>A prepared file's tables, loaded; the cell blobs stay on disk until a read asks for them.</summary>
sealed class WorldFile
{
    public required string FilePath { get; init; }
    public required string PluginName { get; init; }
    public required ModKey[] Keys { get; init; }
    public required Dictionary<ModKey, ushort> KeyIndex { get; init; }
    public required WorldFileCell[] Cells { get; init; }
    public required ulong[] DirKeys { get; init; }
    public required int[] DirCell { get; init; }
    public required byte[] DirFlags { get; init; }
    public required ulong[] BaseKeys { get; init; }
    public required WorldFileBase[] Bases { get; init; }

    public const byte FlagPersistent = 1, FlagDeleted = 2, FlagPlacement = 4;

    /// <summary>The directory row of a reference, or -1 when this plugin holds no record of it.</summary>
    public int Find(FormKey key) => Search(DirKeys, key);
    public int FindBase(FormKey key) => Search(BaseKeys, key);

    int Search(ulong[] keys, FormKey key)
    {
        if (!KeyIndex.TryGetValue(key.ModKey, out var li)) return -1;
        int i = Array.BinarySearch(keys, ((ulong)li << 32) | key.ID);
        return i < 0 ? -1 : i;
    }

    /// <summary>One cell's placed records, checked against the hash its table row states.</summary>
    public List<WorldEntry> ReadCell(int cellIndex)
    {
        var c = Cells[cellIndex];
        var buf = new byte[c.Length];
        using (var h = File.OpenHandle(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            int got = 0;
            while (got < buf.Length)
            {
                int n = RandomAccess.Read(h, buf.AsSpan(got), c.Offset + got);
                if (n <= 0) throw new WorldFileUnreadable("cell blob cut short");
                got += n;
            }
        }
        if (WorldReadFile.Check(buf) != c.Check) throw new WorldFileUnreadable("cell blob hash differs");
        return WorldReadFile.DecodeCell(buf, Keys);
    }
}

/// <summary>A prepared file that cannot be read as it stands; the reader rebuilds it.</summary>
sealed class WorldFileUnreadable(string why) : Exception(why);

static class WorldReadFile
{
    static readonly byte[] Magic = Encoding.ASCII.GetBytes("SWWORLD1"), EndMarker = Encoding.ASCII.GetBytes("SWWEND!!");
    const int KindPlacements = 1, KindMeta = 2, KindCells = 3, KindDirectory = 4, KindBases = 5;
    const int FixedHeader = 8 + 4 + 16 + 32 + 8 + 4, SectionRow = 4 + 8 + 8 + 32;
    static readonly int[] Kinds = { KindPlacements, KindMeta, KindCells, KindDirectory, KindBases };

    /// <summary>Where the prepared file of a plugin with this content and name lives in the cache folder. The name is
    /// part of the key because a plugin's own records take their FormKeys from its file name.</summary>
    internal static string PathFor(string cacheFolder, string contentSha, string pluginName)
    {
        var tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pluginName.ToLowerInvariant())))[..16].ToLowerInvariant();
        return Path.Combine(cacheFolder, $"{contentSha}-{tag}.swr");
    }

    /// <summary>The sha256 of a file's content, lower-case hex.</summary>
    internal static string ContentSha(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    internal static ulong Check(ReadOnlySpan<byte> data) => BitConverter.ToUInt64(SHA256.HashData(data), 0);

    /// <summary>Prepare <paramref name="pluginPath"/> into <paramref name="outPath"/>, stating the given content hash,
    /// schema and Engine version in its header: written to a temporary file in the same folder, flushed, then renamed.
    /// Returns the path the file was left at.</summary>
    internal static string WriteFor(string pluginPath, string contentSha, string outPath, int schema, Guid engine)
    {
        var tmp = outPath + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            {
                Write(fs, pluginPath, contentSha, schema, engine);
                fs.Flush(flushToDisk: true);
            }
            try { File.Move(tmp, outPath, overwrite: true); return outPath; }
            catch (IOException) { return tmp; }   // another process holds the old file open: this reader uses its own complete copy
            catch (UnauthorizedAccessException) { return tmp; }
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    // ---------------------------------------------------------------- preparing

    sealed class KeyTable
    {
        public readonly List<ModKey> Keys = new();
        readonly Dictionary<ModKey, ushort> _ix = new();
        public ushort Of(ModKey k)
        {
            if (_ix.TryGetValue(k, out var i)) return i;
            if (Keys.Count >= 0xFFFE) throw new InvalidOperationException("more than 65,534 masters");
            i = (ushort)Keys.Count; Keys.Add(k); _ix[k] = i; return i;
        }
        public ulong Packed(FormKey k) => ((ulong)Of(k.ModKey) << 32) | k.ID;
    }

    sealed class CellBuild
    {
        public required FormKey Cell; public string? EditorId; public bool Interior; public FormKey? Worldspace; public (int, int)? Grid; public bool IsPersistent;
        public readonly List<WorldEntry> Entries = new();
    }

    static void Write(FileStream fs, string pluginPath, string contentSha, int schema, Guid engine)
    {
        var keys = new KeyTable();
        var cells = new List<CellBuild>();
        var bases = new Dictionary<FormKey, WorldFileBase>();
        string name = Path.GetFileName(pluginPath);
        ModKey self;

        // The cell walk and the base walk each open the plugin on their own, so they run side by side.
        using (var mod = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(pluginPath)))
        {
            self = mod.ModKey;
            var baseWalk = Task.Run(() => WalkBases(pluginPath, bases));
            WalkCells(mod, cells);
            baseWalk.GetAwaiter().GetResult();
        }
        keys.Of(self);

        var hdr = new byte[FixedHeader + Kinds.Length * SectionRow];
        fs.Write(hdr);
        var rows = new List<(int Kind, long Off, long Len, byte[] Sha)>();
        var cellRows = new List<(long Off, int Len, ulong Check)>(cells.Count);

        // Placements: one blob per cell.
        {
            long start = fs.Position;
            using var inc = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var ms = new MemoryStream();
            foreach (var c in cells)
            {
                ms.SetLength(0);
                EncodeCell(new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true), c.Entries, keys);
                var blob = ms.GetBuffer().AsSpan(0, (int)ms.Length);
                cellRows.Add((fs.Position, blob.Length, Check(blob)));
                fs.Write(blob); inc.AppendData(blob);
            }
            rows.Add((KindPlacements, start, fs.Position - start, inc.GetHashAndReset()));
        }

        // Every key the plugin's records name is in the key table now except the bases' and cells' own; add them first.
        foreach (var k in bases.Keys) keys.Of(k.ModKey);
        foreach (var c in cells) { keys.Of(c.Cell.ModKey); if (c.Worldspace is { } w) keys.Of(w.ModKey); }

        void Section(int kind, Action<BinaryWriter> body)
        {
            var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) body(bw);
            var bytes = ms.GetBuffer().AsSpan(0, (int)ms.Length);
            long off = fs.Position;
            fs.Write(bytes);
            rows.Add((kind, off, bytes.Length, SHA256.HashData(bytes)));
        }

        // Cells and Directory first so that the key table is complete when Meta is written last of the three.
        var cellsBytes = new MemoryStream();
        using (var bw = new BinaryWriter(cellsBytes, Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(cells.Count);
            for (int i = 0; i < cells.Count; i++)
            {
                var c = cells[i];
                WriteKey(bw, c.Cell, keys); WriteStr(bw, c.EditorId);
                bw.Write((byte)((c.Interior ? 1 : 0) | (c.Worldspace is not null ? 2 : 0) | (c.Grid is not null ? 4 : 0) | (c.IsPersistent ? 8 : 0)));
                if (c.Worldspace is { } w) WriteKey(bw, w, keys);
                if (c.Grid is { } g) { bw.Write(g.Item1); bw.Write(g.Item2); }
                bw.Write(cellRows[i].Off); bw.Write(cellRows[i].Len); bw.Write(cellRows[i].Check);
            }
        }

        // Directory: every placed record, sorted by packed key; a duplicate keeps the last one walked.
        var dir = new Dictionary<ulong, (int Cell, byte Flags)>();
        for (int i = 0; i < cells.Count; i++)
            foreach (var e in cells[i].Entries)
                dir[keys.Packed(e.Key)] = (i, (byte)((e.Persistent ? WorldFile.FlagPersistent : 0) | (e.Deleted ? WorldFile.FlagDeleted : 0) | (e.Ref is not null ? WorldFile.FlagPlacement : 0)));
        var dirKeys = dir.Keys.ToArray(); Array.Sort(dirKeys);

        var baseList = bases.Select(kv => (Packed: keys.Packed(kv.Key), kv.Value)).OrderBy(x => x.Packed).ToArray();

        Section(KindCells, bw => bw.Write(cellsBytes.GetBuffer().AsSpan(0, (int)cellsBytes.Length)));
        Section(KindDirectory, bw =>
        {
            bw.Write(dirKeys.Length);
            bw.Write(MemoryMarshal.AsBytes(dirKeys.AsSpan()));
            var cellIx = new int[dirKeys.Length]; var flags = new byte[dirKeys.Length];
            for (int i = 0; i < dirKeys.Length; i++) { var d = dir[dirKeys[i]]; cellIx[i] = d.Cell; flags[i] = d.Flags; }
            bw.Write(MemoryMarshal.AsBytes(cellIx.AsSpan()));
            bw.Write(flags);
        });
        Section(KindBases, bw =>
        {
            bw.Write(baseList.Length);
            foreach (var (packed, _) in baseList) bw.Write(packed);
            foreach (var (_, b) in baseList)
            {
                bw.Write(b.TypeName); WriteStr(bw, b.EditorId); WriteStr(bw, b.Model); WriteStr(bw, b.FemaleModel);
                bw.Write((byte)((b.Obnd is not null ? 1 : 0) | (b.IsMarker ? 2 : 0) | (b.LightCanBeCarried ? 4 : 0)));
                if (b.Obnd is { } o) { bw.Write(o.X); bw.Write(o.Y); bw.Write(o.Z); }
            }
        });
        Section(KindMeta, bw =>
        {
            bw.Write(name);
            bw.Write(keys.Keys.Count);
            foreach (var k in keys.Keys) bw.Write(k.FileName.String);
        });

        fs.Write(EndMarker);
        long total = fs.Position;

        // The header last: a file cut short anywhere fails its length or its end marker.
        using (var hw = new BinaryWriter(new MemoryStream(hdr), Encoding.UTF8))
        {
            hw.Write(Magic); hw.Write(schema); hw.Write(engine.ToByteArray()); hw.Write(Convert.FromHexString(contentSha));
            hw.Write(total); hw.Write(rows.Count);
            foreach (var kind in Kinds)
            {
                var r = rows.Single(x => x.Kind == kind);
                hw.Write(r.Kind); hw.Write(r.Off); hw.Write(r.Len); hw.Write(r.Sha);
            }
        }
        fs.Position = 0; fs.Write(hdr); fs.Position = total;
    }

    /// <summary>Every cell's placed records, walked by group as <see cref="PlacementExtract.FromMod"/> walks them:
    /// interior blocks, then each worldspace's persistent cell and its exterior blocks. A record with no placement data
    /// is kept (a deletion, nearly always): it is still the reference's record in this plugin.</summary>
    static void WalkCells(ISkyrimModGetter mod, List<CellBuild> cells)
    {
        var byKey = new Dictionary<FormKey, CellBuild>();
        void Add(ICellGetter cell, FormKey? ws, bool persistentCell)
        {
            if (!byKey.TryGetValue(cell.FormKey, out var cb))
            {
                cb = new CellBuild
                {
                    Cell = cell.FormKey, EditorId = cell.EditorID, Interior = ws is null, Worldspace = ws, IsPersistent = persistentCell,
                    Grid = ws is not null && !persistentCell && cell.Grid is { } g ? (g.Point.X, g.Point.Y) : null,
                };
                byKey[cell.FormKey] = cb; cells.Add(cb);
            }
            foreach (var p in cell.Persistent) cb.Entries.Add(new WorldEntry(p.FormKey, PlacementExtract.RefOf(p, persistent: true), true, p.IsDeleted));
            foreach (var p in cell.Temporary) cb.Entries.Add(new WorldEntry(p.FormKey, PlacementExtract.RefOf(p, persistent: false), false, p.IsDeleted));
        }
        foreach (var block in mod.Cells.Records)
            foreach (var sub in block.SubBlocks)
                foreach (var cell in sub.Cells) Add(cell, null, false);
        foreach (var wrld in mod.Worldspaces.Records)
        {
            if (wrld.TopCell is { } top) Add(top, wrld.FormKey, true);
            foreach (var block in wrld.SubCells)
                foreach (var sub in block.Items)
                    foreach (var cell in sub.Items) Add(cell, wrld.FormKey, false);
        }
    }

    /// <summary>The facts of every modeled base record the plugin holds, as the whole-order pass reads them
    /// (OverlapIndex.Ingest): armour by its world models, any other modeled record by its Model.</summary>
    static void WalkBases(string pluginPath, Dictionary<FormKey, WorldFileBase> bases)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(pluginPath));
        static Vec3 Extents(Noggog.P3Int16 first, Noggog.P3Int16 second)
            => new(MathF.Abs(second.X - first.X), MathF.Abs(second.Y - first.Y), MathF.Abs(second.Z - first.Z));
        foreach (var rec in mod.EnumerateMajorRecords())
        {
            if (rec is IPlacedGetter) continue;
            bool marker = (rec.MajorRecordFlagsRaw & EnableState.IsMarkerFlag) != 0;
            string type = rec.GetType().Name.Replace("BinaryOverlay", "");
            if (rec is IArmorGetter ar)
                bases[ar.FormKey] = new WorldFileBase(type, ar.EditorID, BaseModel.Of(ar), BaseModel.FemaleOf(ar), ar.ObjectBounds is { } ab ? Extents(ab.First, ab.Second) : null, marker, false);
            else if (rec is IModeledGetter && rec is ISkyrimMajorRecordGetter sm)
                bases[sm.FormKey] = new WorldFileBase(type, sm.EditorID, BaseModel.Of(rec), null,
                    rec is IObjectBoundedGetter ob && ob.ObjectBounds is { } bnd ? Extents(bnd.First, bnd.Second) : null, marker,
                    rec is ILightGetter lg && lg.Flags.HasFlag(Light.Flag.CanBeCarried));
        }
    }

    // ---------------------------------------------------------------- encoding

    const ushort NullKey = 0xFFFF;
    static void WriteKey(BinaryWriter w, FormKey k, KeyTable t) { w.Write(t.Of(k.ModKey)); w.Write(k.ID); }
    static void WriteKey(BinaryWriter w, FormKey? k, KeyTable t) { if (k is { } x) WriteKey(w, x, t); else { w.Write(NullKey); w.Write(0u); } }
    static void WriteStr(BinaryWriter w, string? s) { w.Write(s is not null); if (s is not null) w.Write(s); }
    static void WriteVec(BinaryWriter w, Vec3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }

    static FormKey ReadKey(BinaryReader r, ModKey[] keys) { var i = r.ReadUInt16(); var id = r.ReadUInt32(); return new FormKey(keys[i], id); }
    static FormKey? ReadNKey(BinaryReader r, ModKey[] keys) { var i = r.ReadUInt16(); var id = r.ReadUInt32(); return i == NullKey ? null : new FormKey(keys[i], id); }
    static string? ReadStr(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;
    static Vec3 ReadVec(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    static void EncodeCell(BinaryWriter w, List<WorldEntry> entries, KeyTable t)
    {
        w.Write(entries.Count);
        foreach (var e in entries)
        {
            w.Write((byte)((e.Persistent ? 1 : 0) | (e.Deleted ? 2 : 0) | (e.Ref is not null ? 4 : 0)));
            WriteKey(w, e.Key, t);
            if (e.Ref is not { } r) continue;
            w.Write(r.RecordType); WriteStr(w, r.EditorId);
            WriteVec(w, r.Position); WriteVec(w, r.Rotation); w.Write(r.Scale);
            WriteKey(w, r.Base, t);
            w.Write((byte)((r.Persistent ? 1 : 0) | (r.InitiallyDisabled ? 2 : 0) | (r.Deleted ? 4 : 0) | (r.EnableParentOpposite ? 8 : 0) | (r.OpenByDefault ? 16 : 0)));
            WriteKey(w, r.EnableParent, t);
            w.Write(r.LinkedReferences?.Count ?? -1);
            foreach (var l in r.LinkedReferences ?? Array.Empty<PlacedLink>()) { WriteKey(w, l.Reference, t); WriteKey(w, l.Keyword, t); }
            w.Write(r.ActivateParents?.Count ?? -1);
            foreach (var a in r.ActivateParents ?? Array.Empty<PlacedActivateParent>()) { WriteKey(w, a.Reference, t); w.Write(a.Delay); }
            w.Write(r.Primitive is not null);
            if (r.Primitive is { } pr) { w.Write(pr.Type); WriteVec(w, pr.Bounds); }
            WriteKey(w, r.EncounterZone, t);
            w.Write(r.CollisionLayerIndex is not null); if (r.CollisionLayerIndex is { } cl) w.Write(cl);
            w.Write(r.RecordFlags);
            w.Write(r.Scripts?.Count ?? -1);
            foreach (var s in r.Scripts ?? Array.Empty<string>()) w.Write(s);
            WriteKey(w, r.ArmBase, t); WriteStr(w, r.ArmBaseLink);
        }
    }

    internal static List<WorldEntry> DecodeCell(byte[] blob, ModKey[] keys)
    {
        var rd = new BinaryReader(new MemoryStream(blob, writable: false), Encoding.UTF8);
        int n = rd.ReadInt32();
        var list = new List<WorldEntry>(n);
        for (int i = 0; i < n; i++)
        {
            byte f = rd.ReadByte();
            var key = ReadKey(rd, keys);
            if ((f & 4) == 0) { list.Add(new WorldEntry(key, null, (f & 1) != 0, (f & 2) != 0)); continue; }
            string type = rd.ReadString(); var edid = ReadStr(rd);
            var pos = ReadVec(rd); var rot = ReadVec(rd); float scale = rd.ReadSingle();
            var baseKey = ReadNKey(rd, keys);
            byte b = rd.ReadByte();
            var ep = ReadNKey(rd, keys);
            int nl = rd.ReadInt32(); List<PlacedLink>? links = nl < 0 ? null : new List<PlacedLink>(nl);
            for (int k = 0; k < nl; k++) links!.Add(new PlacedLink(ReadKey(rd, keys), ReadNKey(rd, keys)));
            int na = rd.ReadInt32(); List<PlacedActivateParent>? aps = na < 0 ? null : new List<PlacedActivateParent>(na);
            for (int k = 0; k < na; k++) aps!.Add(new PlacedActivateParent(ReadKey(rd, keys), rd.ReadSingle()));
            PlacedPrimitiveInfo? prim = rd.ReadBoolean() ? new PlacedPrimitiveInfo(rd.ReadString(), ReadVec(rd)) : null;
            var ez = ReadNKey(rd, keys);
            uint? cl = rd.ReadBoolean() ? rd.ReadUInt32() : null;
            uint rf = rd.ReadUInt32();
            int ns = rd.ReadInt32(); List<string>? scripts = ns < 0 ? null : new List<string>(ns);
            for (int k = 0; k < ns; k++) scripts!.Add(rd.ReadString());
            var armBase = ReadNKey(rd, keys); var armLink = ReadStr(rd);
            var r = new PlacedRef(key, type, edid, pos, rot, scale, baseKey, (b & 1) != 0, (b & 2) != 0, (b & 4) != 0, ep, (b & 8) != 0,
                                  links, aps, prim, ez, cl, (b & 16) != 0, rf, scripts, armBase, armLink);
            list.Add(new WorldEntry(key, r, (f & 1) != 0, (f & 2) != 0));
        }
        return list;
    }

    // ---------------------------------------------------------------- loading

    /// <summary>Load a prepared file's tables, or throw <see cref="WorldFileUnreadable"/> when it is absent, cut short,
    /// made by another schema or Engine build, for other content, or does not match its own hashes.</summary>
    internal static WorldFile Load(string path, string contentSha, string pluginName, int schema, Guid engine)
    {
        if (!File.Exists(path)) throw new WorldFileUnreadable("absent");
        using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        long length = RandomAccess.GetLength(h);
        int hdrLen = FixedHeader + Kinds.Length * SectionRow;
        if (length < hdrLen + EndMarker.Length) throw new WorldFileUnreadable("shorter than its header");
        var hdr = ReadAt(h, 0, hdrLen);
        var hr = new BinaryReader(new MemoryStream(hdr), Encoding.UTF8);
        if (!hr.ReadBytes(8).AsSpan().SequenceEqual(Magic)) throw new WorldFileUnreadable("not a prepared file");
        if (hr.ReadInt32() != schema) throw new WorldFileUnreadable("another schema");
        if (new Guid(hr.ReadBytes(16)) != engine) throw new WorldFileUnreadable("another Engine build");
        if (!hr.ReadBytes(32).AsSpan().SequenceEqual(Convert.FromHexString(contentSha))) throw new WorldFileUnreadable("other content");
        if (hr.ReadInt64() != length) throw new WorldFileUnreadable("length differs");
        if (!ReadAt(h, length - EndMarker.Length, EndMarker.Length).AsSpan().SequenceEqual(EndMarker)) throw new WorldFileUnreadable("no end marker");
        int count = hr.ReadInt32();
        if (count != Kinds.Length) throw new WorldFileUnreadable("section count differs");
        var sections = new Dictionary<int, (long Off, long Len, byte[] Sha)>();
        for (int i = 0; i < count; i++)
        {
            int kind = hr.ReadInt32(); long off = hr.ReadInt64(), len = hr.ReadInt64(); var sha = hr.ReadBytes(32);
            if (off < hdrLen || len < 0 || off + len > length - EndMarker.Length) throw new WorldFileUnreadable("section out of range");
            sections[kind] = (off, len, sha);
        }
        byte[] Sec(int kind)
        {
            if (!sections.TryGetValue(kind, out var s)) throw new WorldFileUnreadable("section missing");
            var bytes = ReadAt(h, s.Off, checked((int)s.Len));
            if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(s.Sha)) throw new WorldFileUnreadable("section hash differs");
            return bytes;
        }

        var meta = new BinaryReader(new MemoryStream(Sec(KindMeta)), Encoding.UTF8);
        var storedName = meta.ReadString();
        if (!string.Equals(storedName, pluginName, StringComparison.OrdinalIgnoreCase)) throw new WorldFileUnreadable("another plugin name");
        var keys = new ModKey[meta.ReadInt32()];
        for (int i = 0; i < keys.Length; i++) keys[i] = ModKey.FromFileName(meta.ReadString());
        var keyIndex = new Dictionary<ModKey, ushort>();
        for (int i = 0; i < keys.Length; i++) keyIndex[keys[i]] = (ushort)i;

        var cr = new BinaryReader(new MemoryStream(Sec(KindCells)), Encoding.UTF8);
        var cells = new WorldFileCell[cr.ReadInt32()];
        for (int i = 0; i < cells.Length; i++)
        {
            var key = ReadKey(cr, keys); var edid = ReadStr(cr); byte f = cr.ReadByte();
            FormKey? ws = (f & 2) != 0 ? ReadKey(cr, keys) : null;
            (int, int)? grid = (f & 4) != 0 ? (cr.ReadInt32(), cr.ReadInt32()) : null;
            cells[i] = new WorldFileCell(key, edid, (f & 1) != 0, ws, grid, (f & 8) != 0, cr.ReadInt64(), cr.ReadInt32(), cr.ReadUInt64());
        }

        var dirBytes = Sec(KindDirectory);
        int dn = BitConverter.ToInt32(dirBytes, 0);
        var dirKeys = MemoryMarshal.Cast<byte, ulong>(dirBytes.AsSpan(4, dn * 8)).ToArray();
        var dirCell = MemoryMarshal.Cast<byte, int>(dirBytes.AsSpan(4 + dn * 8, dn * 4)).ToArray();
        var dirFlags = dirBytes.AsSpan(4 + dn * 12, dn).ToArray();

        var br = new BinaryReader(new MemoryStream(Sec(KindBases)), Encoding.UTF8);
        int bn = br.ReadInt32();
        var baseKeys = new ulong[bn];
        for (int i = 0; i < bn; i++) baseKeys[i] = br.ReadUInt64();
        var bases = new WorldFileBase[bn];
        for (int i = 0; i < bn; i++)
        {
            string type = br.ReadString(); var edid = ReadStr(br); var model = ReadStr(br); var female = ReadStr(br); byte f = br.ReadByte();
            Vec3? obnd = (f & 1) != 0 ? ReadVec(br) : null;
            bases[i] = new WorldFileBase(type, edid, model, female, obnd, (f & 2) != 0, (f & 4) != 0);
        }

        return new WorldFile
        {
            FilePath = path, PluginName = pluginName, Keys = keys, KeyIndex = keyIndex, Cells = cells,
            DirKeys = dirKeys, DirCell = dirCell, DirFlags = dirFlags, BaseKeys = baseKeys, Bases = bases,
        };
    }

    static byte[] ReadAt(Microsoft.Win32.SafeHandles.SafeFileHandle h, long off, int len)
    {
        var buf = new byte[len];
        int got = 0;
        while (got < len)
        {
            int n = RandomAccess.Read(h, buf.AsSpan(got), off + got);
            if (n <= 0) throw new WorldFileUnreadable("cut short");
            got += n;
        }
        return buf;
    }
}
