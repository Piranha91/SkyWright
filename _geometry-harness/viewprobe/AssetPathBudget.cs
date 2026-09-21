using System.Text.Json;

namespace ViewProbe;

/// <summary>
/// A PATH TOO LONG FOR THE LIBRARY'S MESH READER IS NAMED AS THE CAUSE, NOT LEFT AS A MYSTERY.
///
/// The rendering library reads a mesh with nifly's native loader, and a native Win32 open is still
/// bound by MAX_PATH. <c>BuildFromFile</c> returns an EMPTY list when that load reports failure —
/// it does not throw — so a file that is on disk, whole, and readable by every managed API in this
/// process produces no geometry and nothing says why. houseCARL's own parse reads the same bytes
/// happily, which is exactly the disagreement the unreadable-asset rule reports:
/// <i>found, parses with N drawable shape(s), but the renderer produced no geometry</i>. True, and
/// useless to act on, because the file is not the problem: its path is.
///
/// The length is the caller's, not ours. The library extracts to
/// <c>&lt;this process's scratch&gt;\CharacterViewerCache\&lt;source token&gt;\&lt;the asset's own
/// path&gt;</c>, and the scratch lives under <c>HOUSECARL_DATA_DIR</c> when that is set — so a data
/// directory buried a few folders deep can push ordinary assets past the limit while the default
/// location has room to spare.
///
/// <b>Measured, not taken from the constant's reputation.</b> One real mesh was copied to paths of
/// every length in a range and handed to the library's own <c>NifMeshBuilder.BuildFromFile</c>: it
/// returns geometry at <see cref="Limit"/> - 1 characters and an empty list from <see cref="Limit"/>
/// upwards, with no exception and the file present at every length. A second sweep moved the same
/// total length between the directory and the file name and flipped at the same total, so this is
/// the whole path's length and not one component's. The probe and its output are in the launch
/// record.
///
/// Two things use this: the entry that reports an unreadable asset adds the reason when the path is
/// at or past the limit, and the scratch refuses at claim time when its own prefix leaves an asset
/// NO characters at all — which is arithmetic, not a guess, and the only case where a refusal
/// cannot be wrong.
/// </summary>
static class AssetPathBudget
{
    /// <summary>The first full path length the library's mesh reader will not read. A path of
    /// <c>Limit - 1</c> characters is fine; <c>Limit</c> reads nothing and says nothing. This is
    /// Windows' MAX_PATH as the native loader still sees it — managed IO in this process is long-path
    /// clean, which is why the file is there and readable by everything else.</summary>
    public const int Limit = 260;

    /// <summary>The folder the library appends to the directory it extracts under, and the length of
    /// the per-source token it puts inside that (a SHA256 prefix, four bytes as hex). Both are the
    /// library's, read from its source rather than guessed from a sample of paths.</summary>
    public const string LibraryCacheFolder = "CharacterViewerCache";
    public const int LibrarySourceTokenLength = 8;

    /// <summary>How many characters an asset's OWN path may have, once a scratch root has spent its
    /// share. Zero or less means this root cannot deliver any asset at all.</summary>
    public static int RemainingFor(string extractionRoot)
    {
        int prefix = extractionRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length
                   + 1 + LibraryCacheFolder.Length + 1 + LibrarySourceTokenLength + 1;
        return (Limit - 1) - prefix;
    }

    /// <summary>What to add to an unreadable asset's reason when its path is the reason. Null when
    /// the path is short enough to be innocent — the common case, where adding anything would be
    /// noise around the real fault.
    ///
    /// The wording differs by kind because the evidence does. A MESH is named outright: the library's
    /// mesh reader was measured stopping at this length, and a mesh at or past it was not read.
    /// Anything else gets the same fact and is told to rule the path out first, because what was
    /// measured is the mesh reader and a claim about another reader would be one this tool invented.
    /// An <c>archive</c> entry is skipped entirely: its recorded path is the BSA the asset was found
    /// in, not the destination that was too long, and length-checking that would be a sentence about
    /// the wrong string.</summary>
    public static string? Clause(string kind, string? resolvedPath)
    {
        if (resolvedPath is null || kind == "archive") return null;
        int n = resolvedPath.Length;
        if (n < Limit) return null;
        return kind == "mesh"
            ? $" — AND THE PATH IS WHY: at {n} characters it is at or past the {Limit} the rendering "
              + "library's mesh reader stops at, so the file was never opened. Shorten the extraction "
              + "path (the README's path budget) rather than looking for a fault in the asset."
            : $" — its path is {n} characters, at or past the {Limit} the rendering library's mesh "
              + "reader stops at, so rule the path length out first: shorten the extraction path (the "
              + "README's path budget) and read the entry again.";
    }

    /// <summary>The refusal at claim time, or null to carry on. ONLY the provable case: a root whose
    /// own prefix leaves an asset zero characters cannot deliver anything, whatever the load order
    /// holds. A "this looks tight" threshold is deliberately NOT here — the tool does not know how
    /// long the longest path in a load order is, and a guess that refused a run whose assets are all
    /// short-named would cost more than it saved.</summary>
    public static string? ClaimRefusal(string extractionRoot)
    {
        int remaining = RemainingFor(extractionRoot);
        if (remaining > 0) return null;
        return $"viewprobe extraction scratch: '{extractionRoot}' is too long to extract into. The "
             + $"rendering library extracts to <root>\\{LibraryCacheFolder}\\<source token>\\<the "
             + $"asset's own path> and stops reading at {Limit} characters, so this root leaves "
             + $"{remaining} characters for the asset — nothing in this run could be read. Set "
             + "HOUSECARL_DATA_DIR to a shorter directory.";
    }

    /// <summary><c>viewprobe --asset-path-budget &lt;path&gt;…</c>: the two decisions above over
    /// paths given on the command line, one JSON object each. Needs no MO2 instance, no game and no
    /// load order — both are decisions about a string — which is how a test puts the real budget in
    /// front of a path one character either side of the limit.</summary>
    public static int Report(string[] paths)
    {
        foreach (var p in paths)
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                path = p,
                length = p.Length,
                limit = Limit,
                readable = p.Length < Limit,
                remainingForAssetIfUsedAsRoot = RemainingFor(p),
                claimRefusal = ClaimRefusal(p),
                clause = new
                {
                    mesh = Clause("mesh", p),
                    texture = Clause("texture", p),
                    archive = Clause("archive", p),
                },
            }));
        return 0;
    }
}
