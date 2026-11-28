using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SkyRender;

namespace ViewProbe;

/// <summary>
/// --resident-key-selftest: known answers for <see cref="ResidentKey"/> against the SkyRender package viewprobe runs on.
/// A 4x4 BC1 texture with its full mip chain (eligible for the compressed route) is written as a loose file, and each
/// policy (decoded, and the compressed default) uploads it through a real <see cref="GlTextureManager"/> with a resident
/// cache; the entry must be found through <see cref="ResidentKey.For"/> holding the very handle the load returned, and the
/// upload must have taken the route the policy names. Needs a GL context; no MO2 instance and no game.
/// </summary>
public static class ResidentKeySelfTest
{
    sealed class NoBsa : IBsaArchiveProvider
    {
        public void EnsureAllArchivesOpened() { }
        public bool TryLocateInBsa(string subpath, out string? containingBsaPath) { containingBsaPath = null; return false; }
        public bool TryLocateInScopedBsa(string subpath, string folderPath, IReadOnlyList<string> modKeyFileNames, out string? containingBsaPath)
        { containingBsaPath = null; return false; }
        public bool TryExtractToDisk(string containingBsaPath, string subpath, string destPath, out string? error)
        { error = "the resident-key self-test reads one loose file"; return false; }
    }

    /// <summary>A BC1 (DXT1) 4x4 DDS with mips 4, 2 and 1: one 8-byte block per level.</summary>
    static byte[] Bc1Chain()
    {
        var b = new byte[128 + 24];
        void U(int at, uint v) => BitConverter.GetBytes(v).CopyTo(b, at);
        U(0, 0x20534444); U(4, 124); U(8, 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000);
        U(12, 4); U(16, 4); U(20, 8); U(28, 3);
        U(76, 32); U(80, 0x4); U(84, 0x31545844);   // pixel format: FourCC DXT1
        U(108, 0x1000 | 0x8 | 0x400000);           // texture, complex, mipmap
        for (int i = 128; i < b.Length; i += 8) { b[i] = 0xFF; b[i + 1] = 0xFF; }   // colour0 white; indices 0
        return b;
    }

    public static int Run()
    {
        var nws = new NativeWindowSettings
        {
            ClientSize = new OpenTK.Mathematics.Vector2i(64, 64), StartVisible = false, StartFocused = false,
            WindowBorder = WindowBorder.Hidden, APIVersion = new Version(3, 3), Profile = ContextProfile.Core,
        };
        using var gw = new GameWindow(GameWindowSettings.Default, nws);
        gw.MakeCurrent();
        Console.WriteLine($"GL: {GL.GetString(StringName.Renderer)} | {GL.GetString(StringName.Version)}");
        string root = Path.Combine(Path.GetTempPath(), "viewprobe-resident-key-" + Environment.ProcessId);
        string data = Path.Combine(root, "Data");
        Directory.CreateDirectory(Path.Combine(data, "textures", "residentkey"));
        File.WriteAllBytes(Path.Combine(data, "textures", "residentkey", "chain.dds"), Bc1Chain());
        const string game = @"textures\residentkey\chain.dds";
        int failed = 0;
        void Check(string name, bool ok, string detail)
        {
            if (!ok) failed++;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name} ({detail})");
        }
        try
        {
            foreach (var (label, max, route) in new[] { ("decoded", (int?)null, "decoded"),
                         ("compressed default", (int?)DdsMipPolicy.DefaultMaximumFinalMipDimension, "compressed-authored") })
            {
                var logger = new ConsoleCvLogger(); var gate = new RenderLogGate();
                var resolver = new GameAssetResolver(new HcDataFolder(data), new NoBsa(), gate, logger);
                var cache = new AssetCache(resolver, logger, gate, new CvSettings()) { DdsMaximumFinalMipDimension = max };
                using var resident = new ResidentTextureCache(logger, 64L * 1024 * 1024);
                using var manager = new GlTextureManager(cache, logger, resident);
                manager.Initialize();
                int handle = manager.LoadTexture(game);
                string? disk = cache.ResolveAssetPath(game);
                string taken = manager.UploadDiagnostics.LastOrDefault()?.Route ?? "none";
                Check($"{label}: the upload took the {route} route", taken == route, $"took {taken}");
                string? key = ResidentKey.For(cache, disk);
                bool found = resident.TryPeek(key, out int held);
                Check($"{label}: the resident entry is found through ResidentKey.For and holds the loaded handle",
                      handle != manager.WhiteTexture && found && held == handle,
                      $"key {key}; loaded {handle}, found {found}, held {held}, {resident.Count} entr{(resident.Count == 1 ? "y" : "ies")}");
            }
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        Console.WriteLine(failed == 0 ? "resident-key self-test: all known answers hold"
            : $"resident-key self-test: {failed} known answer(s) failed: the key ResidentKey.For forms is not the one this SkyRender package uses; fix ResidentKey.cs to match it.");
        return failed == 0 ? 0 : 1;
    }
}
