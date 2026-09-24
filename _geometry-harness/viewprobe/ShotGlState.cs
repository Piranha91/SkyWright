using OpenTK.Graphics.OpenGL4;

namespace ViewProbe;

/// <summary>F-G5-1 (Gate B8 review Q3): the explicit GL state every draw starts from, on both paths. A retained context
/// keeps whatever the previous shot, the renderer's one-time Initialize or an overlay pass left, so nothing is inherited:
/// every state the G5 RED measured at the draw boundary is set here to the value a fresh context has there. The caller
/// binds its framebuffer and sets its viewport and clear colour first, and calls this before its clear, because the
/// clear itself obeys the scissor test and the colour and depth write masks. The program and vertex array are unbound;
/// the renderer binds its own.</summary>
internal static class ShotGlState
{
    internal static void Establish(int width, int height)
    {
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);       // GlRenderer.Initialize's choice (hair twins repaint their own prepass)
        GL.DepthMask(true);
        GL.DepthRange(0.0, 1.0);
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(CullFaceMode.Back);
        GL.FrontFace(FrontFaceDirection.Ccw);
        GL.Disable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.One, BlendingFactor.Zero);
        GL.BlendEquation(BlendEquationMode.FuncAdd);
        GL.Enable(EnableCap.Multisample);
        GL.Disable(EnableCap.SampleAlphaToCoverage);
        GL.Disable(EnableCap.SampleCoverage);
        GL.Disable(EnableCap.ClipDistance0);
        GL.Disable(EnableCap.ScissorTest);
        GL.Scissor(0, 0, width, height);
        GL.ColorMask(true, true, true, true);
        GL.Disable(EnableCap.StencilTest);
        GL.Disable(EnableCap.PolygonOffsetFill);
        GL.PolygonOffset(0f, 0f);
        GL.Disable(EnableCap.FramebufferSrgb);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.UseProgram(0);
        GL.BindVertexArray(0);
    }
}
