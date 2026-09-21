using System.Security.Cryptography;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ViewProbe;

/// <summary>
/// The per-shape visibility-mask evidence: how a mask is derived, how it is identified, and how it
/// is written.
///
/// A mask is a picture of ONE comparison. For each pixel: the shape drawn on its own
/// (<c>ownDepth</c>) against the whole unculled scene from the same camera (<c>plainDepth</c>).
/// Nothing at the far plane is drawn at all; everything else is green where the shape's own depth
/// wins and red where the scene's does. So a mask carries two bits of information per pixel, and a
/// view writes one per assessed shape - of which, in a busy exterior, most are the same picture:
/// every shape the camera cannot see at all gives the same empty mask.
///
/// THE KEY. Within one view <c>plainDepth</c> is fixed - it is drawn once, before the per-shape
/// loop - so <see cref="BuildMask"/> reads exactly one thing that varies per shape: the
/// <c>ownDepth</c> buffer. <see cref="DepthHash"/> is SHA-256 over that buffer's raw bytes, whole
/// and unquantised, and it is taken from the same array in the same iteration. Two shapes whose
/// hash matches therefore have the same <c>ownDepth</c> bytes and so, by construction, the same
/// mask. The implication runs ONE way: two different depth buffers can still produce the same
/// mask, so the cache can miss a repeat - which costs an encode that is paid today anyway - but it
/// cannot merge two different masks without a SHA-256 collision, which is the same assumption the
/// report's own isolatedDepthSha256 already rests on.
///
/// That argument is a claim about this file, so it is checkable here: set
/// HOUSECARL_VERIFY_MASK_CACHE=1 and every cache hit is encoded again and compared, with the count
/// printed and a mismatch refused rather than written. `--mask-evidence-selftest` exercises both.
/// </summary>
public static class MaskEvidence
{
    /// <summary>SHA-256 over a depth buffer's raw bytes - the whole buffer, nothing dropped.</summary>
    public static string DepthHash(float[] depth) =>
        Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(depth.AsSpan())));

    /// <summary>
    /// The mask buffer, RGBA, one pixel per depth sample, in the renderer's row order (the writer
    /// flips it vertically on the way to the file). The ONLY per-shape input is <paramref name="ownDepth"/>.
    /// </summary>
    public static byte[] BuildMask(float[] ownDepth, float[] plainDepth)
    {
        var mask = new byte[ownDepth.Length * 4];
        for (int i = 0; i < ownDepth.Length; i++)
        {
            mask[i * 4 + 3] = 255;
            if (ownDepth[i] == 1) continue;
            mask[i * 4 + (ownDepth[i] <= plainDepth[i] ? 1 : 0)] = 255;
        }
        return mask;
    }

    /// <summary>The bytes of the PNG this evidence writes for a mask buffer.</summary>
    public static byte[] EncodePng(byte[] mask, int width, int height)
    {
        using var img = Image.LoadPixelData<Rgba32>(mask, width, height);
        img.Mutate(x => x.Flip(FlipMode.Vertical));
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    public const string VerifyVariable = "HOUSECARL_VERIFY_MASK_CACHE";

    public static bool VerifyRequested =>
        Environment.GetEnvironmentVariable(VerifyVariable) is "1" or "true" or "TRUE";
}

/// <summary>
/// One view's mask writer. Created where the view's depth buffers are created and thrown away with
/// them: it is scoped to a view BY CONSTRUCTION rather than cleared between views, so a process
/// that renders several shots cannot carry one view's pictures into the next.
/// </summary>
public sealed class MaskEvidenceCache
{
    private readonly Dictionary<string, byte[]> _byKey = new(StringComparer.Ordinal);
    private readonly bool _verify;

    public MaskEvidenceCache(bool verify) => _verify = verify;

    public int Writes { get; private set; }
    public int Encodes { get; private set; }
    public int Hits { get; private set; }
    public int Verified { get; private set; }
    public int Mismatches { get; private set; }
    public bool Verifying => _verify;

    /// <summary>
    /// Write one shape's mask. <paramref name="key"/> identifies the mask's inputs - the renderer
    /// passes the isolated depth buffer's hash, which it has already computed for the report. A
    /// null key falls back to the mask's own bytes, so a caller that has no key is slower and never
    /// wrong.
    /// </summary>
    public void Write(string? key, byte[] mask, int width, int height, string path)
    {
        Writes++;
        key ??= Convert.ToHexString(SHA256.HashData(mask));
        if (_byKey.TryGetValue(key, out var png))
        {
            Hits++;
            if (_verify)
            {
                Verified++;
                var fresh = MaskEvidence.EncodePng(mask, width, height);
                if (!fresh.AsSpan().SequenceEqual(png))
                {
                    Mismatches++;
                    // The cached bytes are NOT written: a key that does not distinguish two masks
                    // is a defect, and the file on disk is the mask that was asked for either way.
                    Console.Error.WriteLine(
                        $"viewprobe mask evidence: cache key {key[..Math.Min(16, key.Length)]} matched two different masks; "
                        + $"wrote the freshly encoded one to {Path.GetFileName(path)}");
                    png = fresh;
                }
            }
        }
        else
        {
            png = MaskEvidence.EncodePng(mask, width, height);
            Encodes++;
            _byKey[key] = png;
        }
        File.WriteAllBytes(path, png);
    }

    /// <summary>One line, printed only when verification is on: a healthy run's console is unchanged.</summary>
    public string VerifySummary() =>
        $"mask evidence: {Writes} written, {Encodes} encoded, {Hits} from cache, "
        + $"{Verified} re-encoded and checked, {Mismatches} mismatched";
}

/// <summary>
/// The mechanism above, over buffers built here rather than rendered, reachable from the command
/// line so a test can stand one depth value either side of a mask pixel. It REPORTS what it
/// measured; the expectations live in `_geometry-harness/tests/test_mask_evidence.py`. Needs no MO2
/// instance, no game and no load order.
/// </summary>
public static class MaskEvidenceSelfTest
{
    const int W = 16, H = 16, Pixel = 10;

    static float[] Own(float atPixel)
    {
        var d = new float[W * H];
        Array.Fill(d, 1f);
        d[Pixel] = atPixel;
        return d;
    }

    static int Differing(byte[] a, byte[] b)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i += 4)
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2] || a[i + 3] != b[i + 3]) n++;
        return n;
    }

    static byte[] DirectSave(byte[] mask, string path)
    {
        // Exactly what the writer did before there was a cache: encode straight to the file.
        using (var img = Image.LoadPixelData<Rgba32>(mask, W, H))
        {
            img.Mutate(x => x.Flip(FlipMode.Vertical));
            img.SaveAsPng(path);
        }
        return File.ReadAllBytes(path);
    }

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        string P(string name) => Path.Combine(dir, name + ".png");
        byte[] Read(string name) => File.ReadAllBytes(P(name));
        bool Same(string a, string b) => Read(a).AsSpan().SequenceEqual(Read(b));

        var plain = new float[W * H];
        Array.Fill(plain, 0.5f);
        float[] a = Own(0.4f), a2 = Own(0.4f), b = Own(0.6f), c = Own(0.3f);
        byte[] mA = MaskEvidence.BuildMask(a, plain), mA2 = MaskEvidence.BuildMask(a2, plain),
               mB = MaskEvidence.BuildMask(b, plain), mC = MaskEvidence.BuildMask(c, plain);
        string kA = MaskEvidence.DepthHash(a), kA2 = MaskEvidence.DepthHash(a2),
               kB = MaskEvidence.DepthHash(b), kC = MaskEvidence.DepthHash(c);

        var direct = DirectSave(mA, P("direct-a"));
        var directB = DirectSave(mB, P("direct-b"));

        // 1. the same buffer twice: one encode, and the repeat lands byte-identical
        var c1 = new MaskEvidenceCache(verify: false);
        c1.Write(kA, mA, W, H, P("i1"));
        c1.Write(kA2, mA2, W, H, P("i2"));
        var identical = new
        {
            writes = c1.Writes, encodes = c1.Encodes, hits = c1.Hits,
            filesByteEqual = Same("i1", "i2"),
            equalToDirectSave = Read("i1").AsSpan().SequenceEqual(direct),
        };

        // 2. ONE depth value, ONE mask pixel: the key must not match and both must be encoded
        var c2 = new MaskEvidenceCache(verify: false);
        c2.Write(kA, mA, W, H, P("f1"));
        c2.Write(kB, mB, W, H, P("f2"));
        var flips = new
        {
            writes = c2.Writes, encodes = c2.Encodes, hits = c2.Hits,
            depthValuesDiffering = Enumerable.Range(0, a.Length).Count(i => a[i] != b[i]),
            maskPixelsDiffering = Differing(mA, mB),
            keysEqual = kA == kB,
            filesByteEqual = Same("f1", "f2"),
            equalToDirectSave = Read("f1").AsSpan().SequenceEqual(direct)
                             && Read("f2").AsSpan().SequenceEqual(directB),
        };

        // 3. different depth, same mask: the key MAY miss the repeat - that costs an encode, never
        //    a wrong file
        var c3 = new MaskEvidenceCache(verify: false);
        c3.Write(kA, mA, W, H, P("m1"));
        c3.Write(kC, mC, W, H, P("m2"));
        var missed = new
        {
            writes = c3.Writes, encodes = c3.Encodes, keysEqual = kA == kC,
            maskPixelsDiffering = Differing(mA, mC), filesByteEqual = Same("m1", "m2"),
        };

        // 4. a second view: a new cache, empty by construction
        var c4 = new MaskEvidenceCache(verify: false);
        c4.Write(kA, mA, W, H, P("v2"));
        var second = new
        {
            encodesInFirstView = c1.Encodes,
            writesInSecondView = c4.Writes, encodesInSecondView = c4.Encodes,
            sharedAnyEntry = c4.Encodes < c4.Writes,
            filesByteEqual = Same("i1", "v2"),
        };

        // 5. verification on, healthy cache
        var c5 = new MaskEvidenceCache(verify: true);
        c5.Write(kA, mA, W, H, P("h1"));
        c5.Write(kA2, mA2, W, H, P("h2"));
        c5.Write(kA2, mA2, W, H, P("h3"));
        var healthy = new
        {
            writes = c5.Writes, encodes = c5.Encodes, hits = c5.Hits,
            verified = c5.Verified, mismatches = c5.Mismatches,
            summary = c5.VerifySummary(),
        };

        // 6. the check has to be able to go RED. A key that does NOT distinguish two masks is
        //    exactly the failure the design argues cannot happen; here it is forced, and
        //    verification must catch it - and the run WITHOUT verification must write the wrong
        //    bytes, which is what makes the switch worth having.
        var c6 = new MaskEvidenceCache(verify: true);
        c6.Write("a-key-that-lies", mA, W, H, P("l1"));
        c6.Write("a-key-that-lies", mB, W, H, P("l2"));
        var c7 = new MaskEvidenceCache(verify: false);
        c7.Write("a-key-that-lies", mA, W, H, P("n1"));
        c7.Write("a-key-that-lies", mB, W, H, P("n2"));
        var lying = new
        {
            hits = c6.Hits, verified = c6.Verified, mismatches = c6.Mismatches,
            fileMatchesItsOwnMask = Read("l2").AsSpan().SequenceEqual(directB),
            withoutVerifyFileIsWrong = !Read("n2").AsSpan().SequenceEqual(directB),
        };

        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            width = W, height = H, pixel = Pixel,
            verifyVariable = MaskEvidence.VerifyVariable,
            identicalBuffers = identical,
            oneDepthValueFlipsOnePixel = flips,
            differentDepthSameMask = missed,
            secondView = second,
            verifyOnHealthyCache = healthy,
            verifyCatchesALyingKey = lying,
        }));
        return 0;
    }
}
