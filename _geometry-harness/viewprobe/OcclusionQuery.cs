using OpenTK.Graphics.OpenGL4;

namespace ViewProbe;

/// <summary>
/// --occlusion-query (with --depth-measurement): count a shape's covered and visible pixels on the GPU instead of
/// reading its isolated depth back. The isolated draw is unchanged; its depth is copied into a texture and a
/// full-screen pass evaluates <see cref="HousecarlCore.ShapeVisibility.Compare"/>'s per-pixel rule against the scene
/// depth (copied the same way), with one SamplesPassed query per count. One triangle covers each pixel exactly once and
/// the pass writes nothing, so a passed sample is a pixel the rule accepts.
///
/// Both depths reach the shader through the same copy and the same sampler conversion; whether that conversion gives
/// the same floats as the CPU's ReadPixels is not stated by GL, so <see cref="ConversionCheck"/> measures it on every
/// view and refuses on any difference.
/// </summary>
public sealed class OcclusionQueryPass
{
    public const string Flag = "--occlusion-query";

    const string Vertex = "#version 330 core\n"
        + "void main() { gl_Position = vec4(gl_VertexID == 1 ? 3.0 : -1.0, gl_VertexID == 2 ? 3.0 : -1.0, 0.0, 1.0); }\n";

    // ShapeVisibility.Compare, per pixel: `if (mine == 1) continue; covered++; if (mine <= scene) visible++;`
    const string Predicate = "#version 330 core\n"
        + "uniform sampler2D u_own;\nuniform sampler2D u_scene;\nuniform int u_visible;\nout vec4 o;\n"
        + "void main() {\n"
        + "  ivec2 p = ivec2(gl_FragCoord.xy);\n"
        + "  float mine = texelFetch(u_own, p, 0).r;\n"
        + "  float scene = texelFetch(u_scene, p, 0).r;\n"
        + "  if (mine == 1.0) discard;\n"
        + "  if (u_visible == 1 && !(mine <= scene)) discard;\n"
        + "  o = vec4(0.0);\n"
        + "}\n";

    // The sampled depth written out as a float, so its bits can be compared with ReadPixels's.
    const string ConvertShader = "#version 330 core\n"
        + "uniform sampler2D u_scene;\nout float o;\n"
        + "void main() { o = texelFetch(u_scene, ivec2(gl_FragCoord.xy), 0).r; }\n";

    readonly int w, h;
    readonly int sceneTex, ownTex, sceneFbo, ownFbo, convTex, convFbo, vao, predicate, convert, qRaster, qVisible;
    readonly int uVisible;

    /// <param name="track">Receives every GL object made here (kind, handle) for the shot's teardown.</param>
    public OcclusionQueryPass(int width, int height, Action<string, int> track)
    {
        w = width; h = height;
        Drain();
        using var keep = new BindingGuard();
        sceneTex = DepthTexture(); track("texture", sceneTex);
        ownTex = DepthTexture(); track("texture", ownTex);
        sceneFbo = DepthFbo(sceneTex); track("framebuffer", sceneFbo);
        ownFbo = DepthFbo(ownTex); track("framebuffer", ownFbo);
        convTex = GL.GenTexture(); track("texture", convTex);
        GL.BindTexture(TextureTarget.Texture2D, convTex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R32f, w, h, 0, PixelFormat.Red, PixelType.Float, IntPtr.Zero);
        Nearest();
        convFbo = GL.GenFramebuffer(); track("framebuffer", convFbo);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, convFbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, convTex, 0);
        Complete("conversion check");
        vao = GL.GenVertexArray(); track("vertex-array", vao);
        predicate = Link(Predicate, "predicate"); track("program", predicate);
        convert = Link(ConvertShader, "conversion");  track("program", convert);
        GL.UseProgram(predicate);
        GL.Uniform1(GL.GetUniformLocation(predicate, "u_own"), 0);
        GL.Uniform1(GL.GetUniformLocation(predicate, "u_scene"), 1);
        uVisible = GL.GetUniformLocation(predicate, "u_visible");
        GL.UseProgram(convert);
        GL.Uniform1(GL.GetUniformLocation(convert, "u_scene"), 1);
        qRaster = GL.GenQuery(); track("query", qRaster);
        qVisible = GL.GenQuery(); track("query", qVisible);
        Check("creating the occlusion-query objects");
    }

    /// <summary>Copy the scene depth from <paramref name="sourceFbo"/> (call right after the scene-depth draw).</summary>
    public void CopySceneDepth(int sourceFbo)
    {
        Drain();
        using var keep = new BindingGuard();
        Blit(sourceFbo, sceneFbo, "the scene depth");
    }

    /// <summary>Bits of the sampled scene depth against the CPU's ReadPixels floats: (pixels compared, mismatches).</summary>
    public (int Pixels, int Mismatches) ConversionCheck(float[] plainDepth)
    {
        var sampled = new float[w * h];
        Drain();
        using (var keep = new BindingGuard())
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, convFbo);
            PassState();
            GL.ColorMask(true, true, true, true);
            GL.ActiveTexture(TextureUnit.Texture1);
            GL.BindTexture(TextureTarget.Texture2D, sceneTex);
            GL.UseProgram(convert);
            GL.BindVertexArray(vao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GL.ReadPixels(0, 0, w, h, PixelFormat.Red, PixelType.Float, sampled);
            Check("the conversion check");
        }
        return (sampled.Length, Mismatches(sampled, plainDepth));
    }

    /// <summary>Count bitwise differences between two float buffers (public for the self-test's own red).</summary>
    public static int Mismatches(float[] a, float[] b)
    {
        if (a.Length != b.Length) return Math.Max(a.Length, b.Length);
        int n = 0;
        for (int i = 0; i < a.Length; i++)
            if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i])) n++;
        return n;
    }

    /// <summary>The covered and visible pixel counts of the isolated depth now in <paramref name="sourceFbo"/>.
    /// Leaves that framebuffer's depth untouched, so it can still be read back.</summary>
    public (int Raster, int Visible) Count(int sourceFbo)
    {
        Drain();
        using var keep = new BindingGuard();
        Blit(sourceFbo, ownFbo, "the shape's depth");
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, sourceFbo);
        if (GL.GetInteger(GetPName.SampleBuffers) != 0)
            throw new InvalidOperationException("the occlusion query's framebuffer is multisampled, so its count would be samples rather than pixels; refusing this view.");
        PassState();
        GL.ColorMask(false, false, false, false);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, ownTex);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, sceneTex);
        GL.UseProgram(predicate);
        GL.BindVertexArray(vao);
        GL.Uniform1(uVisible, 0);
        GL.BeginQuery(QueryTarget.SamplesPassed, qRaster);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        GL.EndQuery(QueryTarget.SamplesPassed);
        GL.Uniform1(uVisible, 1);
        GL.BeginQuery(QueryTarget.SamplesPassed, qVisible);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        GL.EndQuery(QueryTarget.SamplesPassed);
        // Both issued before either is read: one wait, not two.
        GL.GetQueryObject(qRaster, GetQueryObjectParam.QueryResult, out long raster);
        GL.GetQueryObject(qVisible, GetQueryObjectParam.QueryResult, out long visible);
        Check("the occlusion query");
        return (checked((int)raster), checked((int)visible));
    }

    void PassState()
    {
        GL.Viewport(0, 0, w, h);
        GL.Disable(EnableCap.DepthTest);
        GL.DepthMask(false);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.ScissorTest);
        GL.Disable(EnableCap.StencilTest);
    }

    void Blit(int from, int to, string what)
    {
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, from);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, to);
        GL.BlitFramebuffer(0, 0, w, h, 0, 0, w, h, ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
        Check("copying " + what);
    }

    int DepthTexture()
    {
        int t = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, t);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent24, w, h, 0,
            PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
        Nearest();
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.None);
        return t;
    }

    static void Nearest()
    {
        // Without these the texture has no complete mip chain under the default minifying filter, and sampling it is undefined.
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
    }

    int DepthFbo(int tex)
    {
        int f = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, f);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, tex, 0);
        GL.DrawBuffer(DrawBufferMode.None);
        GL.ReadBuffer(ReadBufferMode.None);
        Complete("depth copy");
        return f;
    }

    static void Complete(string what)
    {
        var s = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (s != FramebufferErrorCode.FramebufferComplete)
            throw new InvalidOperationException($"the occlusion query's {what} framebuffer is incomplete ({s}); refusing this view.");
    }

    static int Link(string fragment, string what)
    {
        int v = GL.CreateShader(ShaderType.VertexShader); GL.ShaderSource(v, Vertex); GL.CompileShader(v); Compiled(v, what + " vertex");
        int f = GL.CreateShader(ShaderType.FragmentShader); GL.ShaderSource(f, fragment); GL.CompileShader(f); Compiled(f, what + " fragment");
        int p = GL.CreateProgram();
        GL.AttachShader(p, v); GL.AttachShader(p, f); GL.LinkProgram(p);
        GL.GetProgram(p, GetProgramParameterName.LinkStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException($"the occlusion query's {what} shader did not link: {GL.GetProgramInfoLog(p)}");
        GL.DetachShader(p, v); GL.DetachShader(p, f); GL.DeleteShader(v); GL.DeleteShader(f);
        return p;

        static void Compiled(int s, string what)
        {
            GL.GetShader(s, ShaderParameter.CompileStatus, out int okc);
            if (okc == 0) throw new InvalidOperationException($"the occlusion query's {what} shader did not compile: {GL.GetShaderInfoLog(s)}");
        }
    }

    // An error an earlier caller left unread belongs to that caller; clear it so Check reports only this pass's own.
    static void Drain() { for (int i = 0; i < 16 && GL.GetError() != ErrorCode.NoError; i++) { } }

    static void Check(string what)
    {
        var e = GL.GetError();
        if (e != ErrorCode.NoError)
            throw new InvalidOperationException($"GL reported {e} while {what}; refusing this view.");
    }

    /// <summary>Takes a snapshot of every binding and state the pass changes and puts it back on Dispose, so the pass
    /// leaves the context as it found it: the textures on units 0 and 1 and on the active unit, the active unit,
    /// program, vertex array, read and draw framebuffers, viewport, depth test and mask, cull, blend, scissor, stencil
    /// and colour mask. Read and draw buffer selections are per framebuffer and only set on this class's own.</summary>
    sealed class BindingGuard : IDisposable
    {
        readonly int active, tex0, tex1, texActive, program, vertexArray, readFbo, drawFbo;
        readonly int[] viewport = new int[4];
        readonly bool depthTest, depthMask, cull, blend, scissor, stencil;
        readonly bool[] colorMask = new bool[4];

        public BindingGuard()
        {
            active = GL.GetInteger(GetPName.ActiveTexture);
            texActive = GL.GetInteger(GetPName.TextureBinding2D);   // the constructor binds on whatever unit is active
            GL.ActiveTexture(TextureUnit.Texture0); tex0 = GL.GetInteger(GetPName.TextureBinding2D);
            GL.ActiveTexture(TextureUnit.Texture1); tex1 = GL.GetInteger(GetPName.TextureBinding2D);
            GL.ActiveTexture((TextureUnit)active);
            program = GL.GetInteger(GetPName.CurrentProgram);
            vertexArray = GL.GetInteger(GetPName.VertexArrayBinding);
            readFbo = GL.GetInteger(GetPName.ReadFramebufferBinding);
            drawFbo = GL.GetInteger(GetPName.DrawFramebufferBinding);
            GL.GetInteger(GetPName.Viewport, viewport);
            depthTest = GL.IsEnabled(EnableCap.DepthTest);
            depthMask = GL.GetBoolean(GetPName.DepthWritemask);
            cull = GL.IsEnabled(EnableCap.CullFace);
            blend = GL.IsEnabled(EnableCap.Blend);
            scissor = GL.IsEnabled(EnableCap.ScissorTest);
            stencil = GL.IsEnabled(EnableCap.StencilTest);
            GL.GetBoolean(GetPName.ColorWritemask, colorMask);
        }

        public void Dispose()
        {
            GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, tex0);
            GL.ActiveTexture(TextureUnit.Texture1); GL.BindTexture(TextureTarget.Texture2D, tex1);
            GL.ActiveTexture((TextureUnit)active); GL.BindTexture(TextureTarget.Texture2D, active == (int)TextureUnit.Texture0 ? tex0
                : active == (int)TextureUnit.Texture1 ? tex1 : texActive);
            GL.UseProgram(program);
            GL.BindVertexArray(vertexArray);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readFbo);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, drawFbo);
            GL.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
            Set(EnableCap.DepthTest, depthTest);
            GL.DepthMask(depthMask);
            Set(EnableCap.CullFace, cull);
            Set(EnableCap.Blend, blend);
            Set(EnableCap.ScissorTest, scissor);
            Set(EnableCap.StencilTest, stencil);
            GL.ColorMask(colorMask[0], colorMask[1], colorMask[2], colorMask[3]);
        }

        static void Set(EnableCap cap, bool on) { if (on) GL.Enable(cap); else GL.Disable(cap); }
    }
}
