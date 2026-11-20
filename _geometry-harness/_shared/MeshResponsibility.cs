using System.Security.Cryptography;
using HousecarlCore;

namespace HarnessShared;

// WHICH MOD FOLDER IS RESPONSIBLE FOR A SHAPE (mesh-responsibility/GATE-A.md 2.1, GATE-B.md). A folder is responsible for
// the shape of a model path in an arm when its copy wins in that arm's asset view (AssetResolver, winner first), the
// folder is one the run may credit, another folder's copy would win without it (the fallback, the first source in the
// same list that is not the folder's), and the two copies' bytes differ. An archive entry is read decompressed
// (AssetResolver.ReadPlacementSource -> Mutagen's BsaFileRecord.GetBytes), so a file stored compressed in one archive and
// loose elsewhere compares equal. The game's own files (the Data layer) are never a folder. Nothing here lists a directory.
//
// The folders a run may credit (GATE-B.md finding 1): with --population, the folders the population names (the delta, the
// set its S2 rule seeds from); otherwise the folders --mesh-folder names; with neither, every folder of the view.

/// <summary>What a model path's winner is, for responsibility.</summary>
public enum MeshShape
{
    /// <summary>The winner is the game's own (Data), or no provider supplies the path.</summary>
    GameOrMissing,
    /// <summary>The winner is a folder this run does not credit.</summary>
    NotCredited,
    /// <summary>A credited folder's copy wins and no other folder's copy exists: a new mesh, not a replacement.</summary>
    NoFallback,
    /// <summary>A credited folder's copy wins and its bytes are the fallback's.</summary>
    EqualBytes,
    /// <summary>A copy could not be read, or archive resolution is incomplete: not judged.</summary>
    Unreadable,
    /// <summary>A credited folder is responsible for the shape.</summary>
    Credited,
}

/// <summary>One model path's answer. <paramref name="Fallback"/> is the mesh that wins without <paramref name="Folder"/>.</summary>
public sealed record MeshOwner(string Path, MeshShape Shape, string? Folder, PlacementSource? Winner, PlacementSource? Fallback,
                               string? WinnerSha256, string? FallbackSha256, string? Reason);

public sealed class MeshResponsibility
{
    readonly AssetResolver _assets;
    readonly IReadOnlySet<string>? _credited;
    readonly Dictionary<string, MeshOwner> _memo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><paramref name="credited"/> null credits every folder of the view.</summary>
    public MeshResponsibility(AssetResolver assets, IEnumerable<string>? credited)
    {
        _assets = assets;
        _credited = credited is null ? null : new HashSet<string>(credited, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The folders this run credits, or null for every folder.</summary>
    public IReadOnlySet<string>? Credited => _credited;

    /// <summary>The MO2 layer a source lives in: the provider for a loose file, the archive's own layer for a BSA entry.</summary>
    public static string FolderOf(PlacementSource s) => s.Kind == AssetKind.Loose ? s.ProviderName : s.OwningMod ?? s.ProviderName;

    public static bool IsGame(string folder) => string.Equals(folder, AssetResolver.DataLayerName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The answer for a Data-relative model path (under meshes\), memoised.</summary>
    public MeshOwner Of(string fullPath)
    {
        if (_memo.TryGetValue(fullPath, out var hit)) return hit;
        return _memo[fullPath] = Decide(fullPath);
    }

    /// <summary>Every path answered so far.</summary>
    public IReadOnlyCollection<MeshOwner> Answered => _memo.Values;

    MeshOwner Decide(string fullPath)
    {
        var res = _assets.ResolveForPlacement(fullPath);
        if (res.Sources.Count == 0) return new(fullPath, MeshShape.GameOrMissing, null, null, null, null, null, "no provider");
        var winner = res.Sources[0];
        string folder = FolderOf(winner);
        if (IsGame(folder)) return new(fullPath, MeshShape.GameOrMissing, null, winner, null, null, null, "the game's own copy wins");
        if (_credited is not null && !_credited.Contains(folder))
            return new(fullPath, MeshShape.NotCredited, folder, winner, null, null, null, "the winning folder is not one this run credits");
        var fallback = res.Sources.Skip(1).FirstOrDefault(s => !string.Equals(FolderOf(s), folder, StringComparison.OrdinalIgnoreCase));
        if (fallback is null) return new(fullPath, MeshShape.NoFallback, folder, winner, null, null, null, "no other folder has this path");
        if (res.ReadIncomplete && (winner.Kind == AssetKind.Bsa || fallback.Kind == AssetKind.Bsa))
            return new(fullPath, MeshShape.Unreadable, folder, winner, fallback, null, null, "an active archive could not be scanned, so precedence is not established");
        var (wb, we) = AssetResolver.ReadPlacementSource(winner);
        var (fb, fe) = AssetResolver.ReadPlacementSource(fallback);
        if (wb is null || fb is null) return new(fullPath, MeshShape.Unreadable, folder, winner, fallback, null, null, we ?? fe);
        string ws = Sha(wb), fs = Sha(fb);
        return new(fullPath, ws == fs ? MeshShape.EqualBytes : MeshShape.Credited, folder, winner, fallback, ws, fs,
                   ws == fs ? "the folder's copy is the fallback's byte for byte" : null);
    }

    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>A model path as a base record stores it, under meshes\ with backslashes (overlapprobe's GeometryOf spelling).</summary>
    public static string FullModelPath(string rel)
        => (rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : System.IO.Path.Combine("meshes", rel)).Replace('/', '\\');
}
