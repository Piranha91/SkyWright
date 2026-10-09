using HousecarlCore;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace ViewProbe;

/// <summary>
/// --occlusion-query-selftest: known answers for <see cref="OcclusionQueryPass"/> on this machine's GPU and driver.
/// 1. Conversion: a 4096x4096 depth renderbuffer is filled with every depth value a full-range write reaches, copied the
///    way a view's scene depth is, sampled, and compared bit for bit with ReadPixels. How many distinct 24-bit values
///    the fill reached is printed, not assumed. The comparison's own red (one flipped bit) must be caught.
/// 2. Counts: synthetic scene and shape depths with ties, uncovered pixels, one-step-hidden pixels, strictly visible
///    pixels and sky, counted by the query and by <see cref="ShapeVisibility.Compare"/>; they must agree, for two shapes
///    in a row (a stale copy of the first would count the second wrong).
/// 3. Bindings: the pass puts back texture units 0 and 1, the active unit, program, vertex array and both framebuffers.
/// </summary>
public static class OcclusionQuerySelfTest
{
    const string Vertex = "#version 330 core\n"
        + "void main() { gl_Position = vec4(gl_VertexID == 1 ? 3.0 : -1.0, gl_VertexID == 2 ? 3.0 : -1.0, 0.0, 1.0); }\n";

    // u_mode 0: every value i = y*width+x, as i/16777215. 1: the synthetic scene. 2, 3: two synthetic shapes.
    const string Fill = "#version 330 core\n"
        + "uniform int u_mode;\nuniform int u_width;\nout vec4 o;\n"
        + "float d(uint i) { return float(i) / 16777215.0; }\n"
        + "void main() {\n"
        + "  uint x = uint(gl_FragCoord.x), y = uint(gl_FragCoord.y);\n"
        + "  uint base = 4000000u + y * 7919u + x * 13u;\n"   // a 24-bit value well inside the range
        + "  float scene = (y % 50u == 0u) ? 1.0 : d(base);\n" // sky rows
        + "  float own;\n"
        + "  if (u_mode == 0) { gl_FragDepth = d(y * uint(u_width) + x); o = vec4(0.0); return; }\n"
        + "  if (u_mode == 1) { gl_FragDepth = scene; o = vec4(0.0); return; }\n"
        + "  if (x < 300u) own = scene;\n"                                         // ties (the shape is the scene's front)
        + "  else if (x < 600u) own = (u_mode == 2 || y % 3u == 0u) ? 1.0 : d(base - 50000u);\n"   // uncovered / visible
        + "  else if (x < 900u) own = d(base + 1u);\n"                              // hidden by about one step
        + "  else own = (y % 2u == 0u) ? d(base - 1000u) : d(base + 1000000u);\n"  // visible / hidden
        + "  if (u_mode == 3 && x >= 1100u) own = 1.0;\n"
        + "  gl_FragDepth = own; o = vec4(0.0);\n"
        + "}\n";

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
        int failed = 0, n = 0;
        void Check(string name, bool ok, string detail)
        {
            n++;
            if (!ok) failed++;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name} ({detail})");
        }
        var owned = new List<(string Kind, int Handle)>();
        int fill = Program(Fill);
        int vao = GL.GenVertexArray();

        // 1. Conversion over the full range.
        {
            const int S = 4096;
            var (fbo, color, depth) = Target(S);
            var pass = new OcclusionQueryPass(S, S, (k, h) => owned.Add((k, h)));
            Draw(fbo, S, fill, vao, 0);
            var f = new float[S * S];
            var u = new uint[S * S];
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.ReadPixels(0, 0, S, S, PixelFormat.DepthComponent, PixelType.Float, f);
            GL.ReadPixels(0, 0, S, S, PixelFormat.DepthComponent, PixelType.UnsignedInt, u);
            pass.CopySceneDepth(fbo);
            var (pixels, mismatches) = pass.ConversionCheck(f);
            Check("sampled depth equals ReadPixels bit for bit over a full-range fill", mismatches == 0, $"{mismatches} of {pixels} differ");
            var flipped = (float[])f.Clone();
            flipped[S * S / 2] = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(flipped[S * S / 2]) ^ 1);
            var (_, redMismatches) = pass.ConversionCheck(flipped);
            Check("RED the conversion check catches one flipped bit", redMismatches == 1, $"{redMismatches} caught");
            // The integer each pixel holds, from the UnsignedInt read (normalised 32-bit): round(u * (2^24-1) / (2^32-1)).
            var d = new int[S * S];
            for (int i = 0; i < d.Length; i++) d[i] = (int)Math.Round(u[i] * 16777215.0 / 4294967295.0);
            var keys = (int[])d.Clone();
            var vals = (float[])f.Clone();
            Array.Sort(keys, vals);
            int distinct = 0, orderBreaks = 0;
            for (int i = 0; i < keys.Length; i++)
            {
                if (i == 0 || keys[i] != keys[i - 1]) distinct++;
                if (i > 0)
                {
                    bool sameKey = keys[i] == keys[i - 1];
                    bool ok = sameKey ? vals[i] == vals[i - 1] : vals[i] > vals[i - 1];
                    if (!ok) orderBreaks++;
                }
            }
            Console.WriteLine($"     coverage: {distinct} distinct 24-bit depth values of 16777216 reached by the fill "
                            + $"({16777216 - distinct} untested)");
            // Name the values the fill missed (keys is sorted), so a gap is a fact about named values, not a count.
            var untested = new List<int>();
            for (int i = 0, next = 0; i <= keys.Length && untested.Count < 32; i++)
            {
                int upTo = i < keys.Length ? keys[i] : 16777216;
                for (; next < upTo && untested.Count < 32; next++) untested.Add(next);
                if (i < keys.Length) next = Math.Max(next, keys[i] + 1);
            }
            if (untested.Count > 0)
                Console.WriteLine($"     untested values{(untested.Count == 32 ? " (first 32)" : "")}: "
                                + string.Join(", ", untested.Select(v => $"0x{v:X6}")));
            Check("ReadPixels floats are strictly increasing in the stored integer (one float per value)", orderBreaks == 0, $"{orderBreaks} breaks");
            int top = Array.IndexOf(keys, 16777215), bottom = Array.IndexOf(keys, 0);
            Check("the largest depth value reads as exactly 1.0", top >= 0 && vals[top] == 1f, top >= 0 ? $"{vals[top]:R}" : "not reached");
            Check("the smallest depth value reads as exactly 0.0", bottom >= 0 && vals[bottom] == 0f, bottom >= 0 ? $"{vals[bottom]:R}" : "not reached");
            GL.DeleteFramebuffer(fbo); GL.DeleteTexture(color); GL.DeleteRenderbuffer(depth);
        }

        // 2 and 3. Counts against the CPU compare, and the bindings the pass must put back.
        {
            const int W = 1200;
            var (fbo, color, depth) = Target(W);
            Draw(fbo, W, fill, vao, 1);
            var scene = new float[W * W];
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.ReadPixels(0, 0, W, W, PixelFormat.DepthComponent, PixelType.Float, scene);
            // Sentinels around the construction, the scene copy and the conversion check, with unit 3 active.
            int s0 = GL.GenTexture(), s1 = GL.GenTexture(), s3 = GL.GenTexture(), sp0 = Program(Fill), sv = GL.GenVertexArray();
            GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, s0);
            GL.ActiveTexture(TextureUnit.Texture1); GL.BindTexture(TextureTarget.Texture2D, s1);
            GL.ActiveTexture(TextureUnit.Texture3); GL.BindTexture(TextureTarget.Texture2D, s3);
            GL.UseProgram(sp0); GL.BindVertexArray(sv);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);
            var setupBefore = Bindings();
            var pass = new OcclusionQueryPass(W, W, (k, h) => owned.Add((k, h)));
            pass.CopySceneDepth(fbo);
            var (sp, sm) = pass.ConversionCheck(scene);
            var setupAfter = Bindings();
            Check("construction, scene copy and conversion check put back every binding they change", setupBefore == setupAfter,
                  $"before {setupBefore} after {setupAfter}");
            GL.ActiveTexture(TextureUnit.Texture0); GL.UseProgram(0); GL.BindVertexArray(0);
            GL.DeleteTexture(s0); GL.DeleteTexture(s1); GL.DeleteTexture(s3); GL.DeleteProgram(sp0); GL.DeleteVertexArray(sv);
            Check("synthetic scene: conversion check", sm == 0, $"{sm} of {sp} differ");
            foreach (var mode in new[] { 2, 3 })
            {
                Draw(fbo, W, fill, vao, mode);
                // Sentinel bindings the pass must put back.
                int t0 = GL.GenTexture(), t1 = GL.GenTexture(), t3 = GL.GenTexture(), sentinelProgram = Program(Fill), sentinelVao = GL.GenVertexArray();
                GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, t0);
                GL.ActiveTexture(TextureUnit.Texture1); GL.BindTexture(TextureTarget.Texture2D, t1);
                GL.ActiveTexture(TextureUnit.Texture3); GL.BindTexture(TextureTarget.Texture2D, t3);
                GL.UseProgram(sentinelProgram); GL.BindVertexArray(sentinelVao);
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);   // read and draw differ, so a swap would show
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);
                var before = Bindings();
                var (raster, visible) = pass.Count(fbo);
                var after = Bindings();
                Check($"shape {mode - 1}: the pass puts back every binding it changes", before == after, $"before {before} after {after}");
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.UseProgram(0); GL.BindVertexArray(0);
                GL.DeleteTexture(t0); GL.DeleteTexture(t1); GL.DeleteTexture(t3); GL.DeleteProgram(sentinelProgram); GL.DeleteVertexArray(sentinelVao);
                var own = new float[W * W];
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                GL.ReadPixels(0, 0, W, W, PixelFormat.DepthComponent, PixelType.Float, own);
                var cpu = ShapeVisibility.Compare(own, scene);
                int ties = 0, uncovered = 0, hidden = 0, strict = 0;
                for (int i = 0; i < own.Length; i++)
                {
                    if (own[i] == 1) uncovered++;
                    else if (own[i] == scene[i]) ties++;
                    else if (own[i] < scene[i]) strict++;
                    else hidden++;
                }
                Console.WriteLine($"     shape {mode - 1}: ties {ties}, strictly visible {strict}, hidden {hidden}, uncovered {uncovered}");
                Check($"shape {mode - 1}: the synthetic depth exercises ties, strict visibility, hidden and uncovered pixels",
                      ties > 0 && strict > 0 && hidden > 0 && uncovered > 0, "");
                Check($"shape {mode - 1}: query counts equal the CPU compare",
                      cpu.Issue is null && cpu.RasterPixels == raster && cpu.VisiblePixels == visible,
                      $"query {raster}/{visible}, CPU {cpu.RasterPixels}/{cpu.VisiblePixels}");
            }
            GL.DeleteFramebuffer(fbo); GL.DeleteTexture(color); GL.DeleteRenderbuffer(depth);
        }
        foreach (var (kind, h) in owned.AsEnumerable().Reverse())
        {
            if (kind == "texture") GL.DeleteTexture(h);
            else if (kind == "framebuffer") GL.DeleteFramebuffer(h);
            else if (kind == "program") GL.DeleteProgram(h);
            else if (kind == "vertex-array") GL.DeleteVertexArray(h);
            else if (kind == "query") GL.DeleteQuery(h);
        }
        GL.DeleteProgram(fill); GL.DeleteVertexArray(vao);
        Console.WriteLine($"occlusion-query selftest: {n} cases, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static string Bindings()
    {
        int active = GL.GetInteger(GetPName.ActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture0); int a = GL.GetInteger(GetPName.TextureBinding2D);
        GL.ActiveTexture(TextureUnit.Texture1); int b = GL.GetInteger(GetPName.TextureBinding2D);
        GL.ActiveTexture((TextureUnit)active); int c = GL.GetInteger(GetPName.TextureBinding2D);
        return $"unit={active - (int)TextureUnit.Texture0} tex0={a} tex1={b} texActive={c} program={GL.GetInteger(GetPName.CurrentProgram)} "
             + $"vao={GL.GetInteger(GetPName.VertexArrayBinding)} read={GL.GetInteger(GetPName.ReadFramebufferBinding)} "
             + $"draw={GL.GetInteger(GetPName.DrawFramebufferBinding)} depthTest={GL.IsEnabled(EnableCap.DepthTest)} "
             + $"depthMask={GL.GetBoolean(GetPName.DepthWritemask)} cull={GL.IsEnabled(EnableCap.CullFace)}";
    }

    /// <summary>A target shaped like a view's single-sample framebuffer: RGBA8 colour texture, DepthComponent24 renderbuffer.</summary>
    static (int Fbo, int Color, int Depth) Target(int size)
    {
        int fbo = GL.GenFramebuffer(), color = GL.GenTexture(), depth = GL.GenRenderbuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.BindTexture(TextureTarget.Texture2D, color);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, size, size, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, color, 0);
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, size, size);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, depth);
        if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
            throw new InvalidOperationException("the self-test's framebuffer is incomplete");
        return (fbo, color, depth);
    }

    static void Draw(int fbo, int size, int program, int vao, int mode)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.Viewport(0, 0, size, size);
        ShotGlState.Establish(size, size);
        GL.ClearDepth(1.0);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        GL.DepthFunc(DepthFunction.Always);   // depth writes need the test on; Always keeps every written value
        GL.Disable(EnableCap.CullFace);
        GL.UseProgram(program);
        GL.Uniform1(GL.GetUniformLocation(program, "u_mode"), mode);
        GL.Uniform1(GL.GetUniformLocation(program, "u_width"), size);
        GL.BindVertexArray(vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        GL.Finish();
        ShotGlState.Establish(size, size);
        var e = GL.GetError();
        if (e != ErrorCode.NoError) throw new InvalidOperationException($"GL reported {e} filling the self-test's depth");
    }

    static int Program(string fragment)
    {
        int v = GL.CreateShader(ShaderType.VertexShader); GL.ShaderSource(v, Vertex); GL.CompileShader(v);
        int f = GL.CreateShader(ShaderType.FragmentShader); GL.ShaderSource(f, fragment); GL.CompileShader(f);
        GL.GetShader(f, ShaderParameter.CompileStatus, out int okf);
        if (okf == 0) throw new InvalidOperationException("self-test fill shader: " + GL.GetShaderInfoLog(f));
        int p = GL.CreateProgram();
        GL.AttachShader(p, v); GL.AttachShader(p, f); GL.LinkProgram(p);
        GL.GetProgram(p, GetProgramParameterName.LinkStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException("self-test fill program: " + GL.GetProgramInfoLog(p));
        GL.DeleteShader(v); GL.DeleteShader(f);
        return p;
    }
}
