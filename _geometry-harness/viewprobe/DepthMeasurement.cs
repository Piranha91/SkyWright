namespace ViewProbe;

/// <summary>
/// The depth-measurement mode (--depth-measurement): a --planes-report run that keeps the measurement and drops the
/// appearance work around it.
///
/// What the measurement reads is two depth buffers (the whole scene, and each assessed shape drawn alone). The only
/// texture input to either is the diffuse alpha of a shape drawn with the alpha test: basic.frag discards a fragment
/// whose diffuse alpha is under the threshold, and that changes the depth written behind foliage and cut-outs. Every
/// other texture (normal maps, the terrain base and paint layers, the water noise, the snow coverings) only colours
/// the frame. So the mode loads a static shape's diffuse exactly when that shape has the alpha test, and nothing else;
/// and it writes a shape's evidence mask only when the shape has visible pixels, because the scorer opens no other.
/// </summary>
public sealed class DepthMeasurement
{
    public const string Flag = "--depth-measurement";
    public const string KeepAllMasksFlag = "--keep-all-masks";
    public const string Name = "depth-measurement";

    public bool KeepAllMasks { get; init; }

    /// <summary>--occlusion-query: shapes are counted on the GPU (OcclusionQuery.cs); only shapes with visible pixels,
    /// or every shape under --keep-all-masks, are read back, hashed and compared on the CPU as well.</summary>
    public bool OcclusionQuery { get; init; }

    /// <summary>The per-view conversion check (sampled scene depth against ReadPixels, bit for bit).</summary>
    public int ConversionPixels { get; set; }
    public int ConversionMismatches { get; set; }
    /// <summary>Shapes counted by the query, and of those the ones read back and checked on the CPU.</summary>
    public int QueriedShapes { get; set; }
    public int CpuCheckedShapes { get; set; }

    /// <summary>Whether a shape the query counted is also read back, hashed and compared on the CPU.</summary>
    public static bool ReadsBack(DepthMeasurement? mode, int queriedVisiblePixels) =>
        mode is null || !mode.OcclusionQuery || mode.KeepAllMasks || queriedVisiblePixels > 0;

    /// <summary>Alpha-tested diffuses this shot kept: game path to the resolved file's size in bytes (0 when unresolved).</summary>
    public SortedDictionary<string, long> KeptDiffuse { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a static shape's diffuse is loaded: only when the renderer will alpha-test it.</summary>
    public static bool KeepsDiffuse(bool hasAlphaTest) => hasAlphaTest;

    /// <summary>Whether an assessed shape's evidence mask is built and written.</summary>
    public static bool WritesMask(DepthMeasurement? mode, int visiblePixels) =>
        mode is null || mode.KeepAllMasks || visiblePixels > 0;

    public void NoteKept(string gamePath, string? diskPath)
    {
        if (KeptDiffuse.ContainsKey(gamePath)) return;
        long bytes = 0;
        try { if (diskPath is not null && File.Exists(diskPath)) bytes = new FileInfo(diskPath).Length; }
        catch (IOException) { }
        KeptDiffuse[gamePath] = bytes;
    }

    public object ReportEntry() => OcclusionQuery
        ? new
        {
            name = Name,
            masks = KeepAllMasks ? "all" : "visible-only",
            keptDiffuse = "alpha-tested shapes only",
            alphaTestedDiffuse = new { textures = KeptDiffuse.Count, fileBytes = KeptDiffuse.Values.Sum() },
            occlusionQuery = new
            {
                isolatedDepthHashed = KeepAllMasks ? "all shapes" : "shapes with visible pixels only; isolatedDepthSha256 is null for the others",
                counts = "RasterPixels and VisiblePixels counted on the GPU for every shape; checked against the CPU compare wherever the depth is read back",
                conversionCheck = new { pixels = ConversionPixels, mismatches = ConversionMismatches },
                shapes = new { queried = QueriedShapes, cpuChecked = CpuCheckedShapes },
            },
        }
        : new
        {
            name = Name,
            masks = KeepAllMasks ? "all" : "visible-only",
            keptDiffuse = "alpha-tested shapes only",
            alphaTestedDiffuse = new { textures = KeptDiffuse.Count, fileBytes = KeptDiffuse.Values.Sum() },
        };

    /// <summary>The report's `appearance` block under the mode; it carries the mode's identity (`measurementMode`).</summary>
    public object Appearance() => new
    {
        status = "unassessed",
        reasonCode = "depth_measurement_mode",
        mode = Name,
        measurementMode = ReportEntry(),
        reason = "Depth-measurement mode: the frame is lit without textures except the diffuse of alpha-tested shapes; "
               + "normal maps, terrain textures, terrain paint layers (roads), water noise and snow coverings are not drawn. "
               + "Placed NPCs keep their own textures. The depth buffers the visibility measurement reads are unaffected.",
    };

    /// <summary>Known answers for the two rules. Needs no MO2 instance, no game and no GL context.</summary>
    public static int SelfTest()
    {
        int failed = 0, n = 0;
        void Check(string name, bool got, bool want)
        {
            n++;
            if (got != want) failed++;
            Console.WriteLine($"{(got == want ? "ok  " : "FAIL")} {name} (got {got})");
        }
        Check("an alpha-tested shape keeps its diffuse", KeepsDiffuse(true), true);
        Check("a shape without the alpha test drops its diffuse", KeepsDiffuse(false), false);
        var mode = new DepthMeasurement();
        var all = new DepthMeasurement { KeepAllMasks = true };
        Check("default path: a mask for an invisible shape", WritesMask(null, 0), true);
        Check("default path: a mask for a visible shape", WritesMask(null, 5), true);
        Check("mode: no mask for an invisible shape", WritesMask(mode, 0), false);
        Check("mode: a mask for a visible shape", WritesMask(mode, 1), true);
        Check("mode with --keep-all-masks: a mask for an invisible shape", WritesMask(all, 0), true);
        var query = new DepthMeasurement { OcclusionQuery = true };
        var queryAll = new DepthMeasurement { OcclusionQuery = true, KeepAllMasks = true };
        Check("default path: every shape read back", ReadsBack(null, 0), true);
        Check("mode without the query: every shape read back", ReadsBack(mode, 0), true);
        Check("query: a shape the query finds invisible is not read back", ReadsBack(query, 0), false);
        Check("query: a shape the query finds visible is read back", ReadsBack(query, 1), true);
        Check("query with --keep-all-masks: every shape read back", ReadsBack(queryAll, 0), true);
        Console.WriteLine($"depth-measurement selftest: {n} cases, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}
