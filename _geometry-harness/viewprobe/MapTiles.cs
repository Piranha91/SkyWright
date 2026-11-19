using System.Diagnostics;
using System.Text.Json;
using OpenTK.Graphics.OpenGL4;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SkyRender;
using Path = System.IO.Path;
using File = System.IO.File;
using Directory = System.IO.Directory;
using Image = SixLabors.ImageSharp.Image;

namespace ViewProbe;

/// <summary><c>--map-tiles FILE.json</c>: after the scene is built (the cell, its ring, and everything the walk adds from
/// further out), draw it from straight above with no perspective over each listed world rectangle and write each as an
/// RGBA PNG, transparent where nothing is drawn. Nothing else is rendered. The file is
/// <c>{"floor": Z, "ceiling": Z, "tiles": [{"west": X, "south": Y, "east": X, "north": Y, "unitsPerPixel": U, "out": PATH}]}</c>
/// in Skyrim units (+X east, +Y north); a PNG's top row is the rectangle's north edge.</summary>
internal static class MapTiles
{
    internal sealed record Tile(float West, float South, float East, float North, float UnitsPerPixel, string Out);
    internal sealed record Request(IReadOnlyList<Tile> Tiles, float Floor, float Ceiling);

    /// <summary>The tiles asked for on the command line; null on every other run.</summary>
    internal static Request? Requested { get; set; }

    /// <summary>Why this build cannot draw map tiles, or null when it can.</summary>
    internal static string? CannotDraw =>
#if SKYRENDER_MAP_TILES
        null;
#else
        $"this viewprobe is built against SkyRender {typeof(GlRenderer).Assembly.GetName().Version}, which has no top-down map camera; build it against SkyRender 4.0 or later (-p:SkyRenderVersion=...).";
#endif

    /// <summary>Read a tile request file; a malformed one is refused in one sentence.</summary>
    internal static Request Read(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        float F(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetSingle(out var f)
            ? f : throw new InvalidDataException($"the map tile file '{path}' has no number '{name}'.");
        var tiles = new List<Tile>();
        if (!root.TryGetProperty("tiles", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"the map tile file '{path}' has no 'tiles' list.");
        foreach (var t in list.EnumerateArray())
        {
            var tile = new Tile(F(t, "west"), F(t, "south"), F(t, "east"), F(t, "north"), F(t, "unitsPerPixel"),
                t.TryGetProperty("out", out var o) && o.GetString() is { Length: > 0 } s ? s : throw new InvalidDataException($"a tile in '{path}' has no 'out' path."));
            if (!(tile.East > tile.West) || !(tile.North > tile.South) || !(tile.UnitsPerPixel > 0))
                throw new InvalidDataException($"a tile in '{path}' has an empty rectangle or a scale that is not positive.");
            tiles.Add(tile);
        }
        float floor = root.TryGetProperty("floor", out _) ? F(root, "floor") : -40000f;
        float ceiling = root.TryGetProperty("ceiling", out _) ? F(root, "ceiling") : 60000f;
        if (!(ceiling > floor)) throw new InvalidDataException($"the map tile file '{path}' has a ceiling that is not above its floor.");
        return new Request(tiles, floor, ceiling);
    }

    /// <summary>Draw every tile of <paramref name="req"/> with the built scene's renderer; the GL context is current. Each PNG
    /// is written beside itself first and moved into place, so a reader never sees half a file.</summary>
    internal static void Render(GlRenderer renderer, Request req)
    {
#if SKYRENDER_MAP_TILES
        // the same light for every tile: no weather, no time of day, no shadows, no point lights, no fog
        renderer.ApplyEnvironment(SceneEnvironment.TopDownMap);
        renderer.EnableShadows = false;
        renderer.PointLights.Clear();
        renderer.EnableToneMapping = false;
        renderer.TransparentBackground = true;
        int fbo = 0, tex = 0, depth = 0, fw = 0, fh = 0;
        try
        {
            foreach (var t in req.Tiles)
            {
                var clock = Stopwatch.StartNew();
                var cam = new TopDownCamera(t.West, t.South, t.East, t.North, req.Floor, req.Ceiling);
                var (w, h) = cam.ImageSize(t.UnitsPerPixel);
                if (w != fw || h != fh)
                {
                    Free(ref fbo, ref tex, ref depth);
                    (fbo, tex, depth) = Target(w, h);
                    (fw, fh) = (w, h);
                }
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                GL.Viewport(0, 0, w, h);
                ShotGlState.Establish(w, h);
                GL.ClearColor(0f, 0f, 0f, 0f);
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                renderer.Render(cam, w, h);
                GL.Finish();
                var px = new byte[w * h * 4];
                GL.ReadPixels(0, 0, w, h, PixelFormat.Rgba, PixelType.UnsignedByte, px);
                double drawMs = clock.Elapsed.TotalMilliseconds;
                Save(px, w, h, t.Out);
                Console.WriteLine(FormattableString.Invariant(
                    $"   map tile: {t.Out}  X {t.West:0}..{t.East:0}, Y {t.South:0}..{t.North:0}, {w}x{h} px; drawn in {drawMs:0} ms, written in {clock.Elapsed.TotalMilliseconds - drawMs:0} ms"));
            }
        }
        finally
        {
            Free(ref fbo, ref tex, ref depth);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            renderer.TransparentBackground = false;
        }
#else
        throw new InvalidOperationException(CannotDraw);
#endif
    }

    static (int Fbo, int Tex, int Depth) Target(int w, int h)
    {
        int fbo = GL.GenFramebuffer(), tex = GL.GenTexture(), depth = GL.GenRenderbuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.BindTexture(TextureTarget.Texture2D, tex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, w, h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, w, h);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, depth);
        var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != FramebufferErrorCode.FramebufferComplete)
            throw new InvalidOperationException($"the map tile's {w}x{h} framebuffer is incomplete ({status}); try a smaller scale.");
        return (fbo, tex, depth);
    }

    static void Free(ref int fbo, ref int tex, ref int depth)
    {
        if (fbo != 0) GL.DeleteFramebuffer(fbo);
        if (tex != 0) GL.DeleteTexture(tex);
        if (depth != 0) GL.DeleteRenderbuffer(depth);
        fbo = tex = depth = 0;
    }

    /// <summary>Save GL's bottom-up, premultiplied RGBA rows as a top-down PNG with straight alpha.</summary>
    internal static void Save(byte[] px, int w, int h, string outPath)
    {
        var rows = new byte[px.Length];
        for (int y = 0; y < h; y++)
        {
            int src = (h - 1 - y) * w * 4, dst = y * w * 4;
            for (int x = 0; x < w * 4; x += 4)
            {
                byte a = px[src + x + 3];
                rows[dst + x + 3] = a;
                for (int c = 0; c < 3; c++)
                    rows[dst + x + c] = a == 0 ? (byte)0 : a == 255 ? px[src + x + c] : (byte)Math.Min(255, (px[src + x + c] * 255 + a / 2) / a);
            }
        }
        var full = Path.GetFullPath(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var part = full + ".part";
        using (var img = Image.LoadPixelData<Rgba32>(rows, w, h)) img.SaveAsPng(part);
        File.Move(part, full, overwrite: true);
    }
}
