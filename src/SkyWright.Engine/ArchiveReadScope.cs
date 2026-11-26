using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace SkyWright.Engine;

/// <summary>Archive reads for ONE pass: each archive's table is read once, on its first use in the pass, instead of on every
/// entry read as <see cref="AssetResolver.TryReadArchiveEntry"/> does (which costs a full table parse, megabytes of garbage, per
/// call). The caller creates a scope for one pass and disposes it in a <c>finally</c>; nothing outlives it, so no archive is held
/// at rest. The static <see cref="AssetResolver"/> reads are unchanged. The bytes per entry are the static path's: the first
/// matching entry in table order, null when absent, and an archive that cannot be read throws. An archive whose size or
/// write time changed since this scope read its table refuses rather than read stale offsets.</summary>
public sealed class ArchiveReadScope : IDisposable
{
    sealed record Table(FileStamp Stamp, Dictionary<string, Func<byte[]>> Entries);

    readonly Dictionary<string, Table> _tables = new(StringComparer.OrdinalIgnoreCase);
    bool _disposed;

    /// <summary>Archives whose table this scope has read.</summary>
    public int TablesRead { get; private set; }

    /// <summary>As <see cref="AssetResolver.TryReadArchiveEntry"/>, through this scope's table of the archive.</summary>
    public byte[]? TryRead(string archivePath, string entryPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_tables.TryGetValue(archivePath, out var t))
        {
            var stamp = FileStamp.Of(archivePath);
            var entries = new Dictionary<string, Func<byte[]>>(StringComparer.OrdinalIgnoreCase);
            var reader = Archive.CreateReader(GameRelease.SkyrimSE, archivePath);
            foreach (var file in reader.Files)
                entries.TryAdd(AssetResolver.Normalize(file.Path), file.GetBytes);   // first in table order wins, as the static scan returns it
            _tables[archivePath] = t = new Table(stamp, entries);
            TablesRead++;
        }
        else if (!FileStamp.Of(archivePath).Equals(t.Stamp))
            throw new IOException($"archive {archivePath} changed on disk during the pass; rerun");
        return t.Entries.TryGetValue(AssetResolver.Normalize(entryPath), out var read) ? read() : null;
    }

    /// <summary>As <see cref="AssetResolver.ReadPlacementSource"/>, reading an archive source through this scope.</summary>
    public (byte[]? Bytes, string? Error) ReadPlacementSource(PlacementSource s)
    {
        if (s.Kind == AssetKind.Loose) return AssetResolver.ReadPlacementSource(s);
        try
        {
            var b = s.ArchivePath is null ? null : TryRead(s.ArchivePath, s.EntryPath);
            return b is null ? (null, $"entry '{s.EntryPath}' not found inside '{Path.GetFileName(s.ArchivePath ?? "?")}'") : (b, null);
        }
        catch (ObjectDisposedException) { throw; }
        catch (Exception ex) { return (null, $"could not read archive '{Path.GetFileName(s.ArchivePath ?? "?")}': {ex.Message}"); }
    }

    /// <summary>Drop every table. A later read throws <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        _tables.Clear();
        _disposed = true;
    }
}
