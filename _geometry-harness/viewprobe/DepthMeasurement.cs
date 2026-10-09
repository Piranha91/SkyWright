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

    public object ReportEntry() => new
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
        Console.WriteLine($"depth-measurement selftest: {n} cases, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}
