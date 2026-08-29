using System.Diagnostics;
using System.Numerics;
using CharacterViewer.Rendering;
using Mutagen.Bethesda.Plugins;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;
using GlMatrix4 = OpenTK.Mathematics.Matrix4;
using Path = System.IO.Path;
using Image = SixLabors.ImageSharp.Image;

namespace ViewProbe;

// INTERACTIVE FLY-THROUGH — the same scene RenderToPng builds, kept resident and drawn per
// frame into a visible window instead of once into a PNG.
//
// WHAT STAYS FROZEN AT THE ENTRY CAMERA, AND WHY THAT IS SOUND. The overlay's camera-dependent
// work — wire dash sizing, the visible/hidden classification, the adaptive nav colour, the cell
// outline's camera-facing ribbons — is built ONCE, for the camera the run started with. The
// "depth buffer is the arbiter" design is what makes that correct rather than merely cheap:
// every wire segment has a solid depth-tested copy (clipped per pixel, per frame, by the REAL
// depth buffer) and an always-visible dashed copy, so flying somewhere new never makes navmesh
// disappear — at most a dash is sized for where the camera used to be. Cosmetic, not semantic.
//
// PICKING is a CPU raycast against the same world-baked triangles the GPU draws (per-shape AABB
// shortlist, then Möller–Trumbore), NOT a depth-buffer unproject — so unlike --pick it can name
// alpha-blended geometry, and it knows WHICH shape it hit, not just where. It is geometric:
// a hit on the transparent part of a leaf texture still reports the leaf's ref. NPC actors are
// not pickable (their CPU-side geometry lives in the character pipeline's VMs); they already
// carry captions and pucks.

enum PickKind { Ref, Navmesh, Terrain }

/// <summary>One hover/click-able shape: world-baked Y-up triangles plus the identity to report.
/// Verts/Tris are the exact arrays the GlMesh upload used (shared, not copied), so picking
/// tests what is drawn — post-cull, post-filter.</summary>
sealed class PickShape
{
    public PickKind Kind;
    public Vector3[] Verts = Array.Empty<Vector3>();   // Y-up viewer space
    public int[] Tris = Array.Empty<int>();            // 3 indices per triangle
    public int[]? NavTriIds;                           // Kind==Navmesh: local tri -> record tri index
    public SceneRef Ref;                               // Kind==Ref
    public FormKey NavKey;                             // Kind==Navmesh
    public string TerrainTex = "";                     // Kind==Terrain ("" = untextured fallback)
    public Vector3 Min, Max;

    public void ComputeBounds()
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var i in Tris) { mn = Vector3.Min(mn, Verts[i]); mx = Vector3.Max(mx, Verts[i]); }
        Min = mn; Max = mx;
    }
}

/// <summary>Everything the fly-through needs that the scene build cannot derive itself:
/// the window, identity formatters, and the annotation-layer identities. PickShapes is an
/// OUTPUT of the build — TexturedScene fills it while baking the scene.</summary>
sealed class InteractiveOptions
{
    public required GameWindow Window;
    public required Func<FormKey, string> RuntimeId;
    public Func<float, float, string?>? TerrainDescribe;   // Skyrim X,Y -> quadrant/layer description
    public required string CellLabel;
    public required string OutDir;
    public required string SafeName;
    public HashSet<FormKey> RedNav = new();                // conflict-red navmeshes, tagged in reports
    public List<(FormKey Key, int Tri, Vector3 A, Vector3 B, Vector3 C)> NavTris = new();  // Skyrim space
    public List<PickShape> PickShapes = new();             // filled by the scene build
}

static class InteractiveViewer
{
    // The textured path's frustum, exactly.
    const float NearPlane = 4f, FarPlane = 131072f;

    internal static void Run(
        InteractiveOptions opt,
        GlRenderer renderer,
        (Vector3 Eye, Vector3 Look, float FovDeg) cam,
        Action<GlMatrix4, GlMatrix4>? overlayDraw)
    {
        var gw = opt.Window;

        // Navmesh pick shapes: the annotated triangle list, grouped per record, converted to
        // the same Y-up space the ref shapes already live in.
        foreach (var g in opt.NavTris.GroupBy(t => t.Key))
        {
            var verts = new List<Vector3>(); var tris = new List<int>(); var ids = new List<int>();
            foreach (var (_, ti, a, b, c) in g)
            {
                int at = verts.Count;
                verts.Add(TexturedScene.YUp(a)); verts.Add(TexturedScene.YUp(b)); verts.Add(TexturedScene.YUp(c));
                tris.Add(at); tris.Add(at + 1); tris.Add(at + 2);
                ids.Add(ti);
            }
            var ps = new PickShape { Kind = PickKind.Navmesh, NavKey = g.Key, Verts = verts.ToArray(), Tris = tris.ToArray(), NavTriIds = ids.ToArray() };
            ps.ComputeBounds();
            opt.PickShapes.Add(ps);
        }

        // ---- camera state, in the game's own conventions -----------------------------------
        // (yaw 0 = north/+Y, clockwise; positive pitch looks down) — so P prints a line that
        // pastes straight back into --eye/--yaw/--pitch, and a getpos pastes in mentally.
        var eye = cam.Eye;
        var d0 = cam.Look - cam.Eye;
        float yaw = (float.RadiansToDegrees(MathF.Atan2(d0.X, d0.Y)) + 360f) % 360f;
        float pitch = float.RadiansToDegrees(MathF.Atan2(-d0.Z, MathF.Max(MathF.Sqrt(d0.X * d0.X + d0.Y * d0.Y), 1e-3f)));
        float fov = cam.FovDeg;
        float speed = 600f;                     // u/s; scroll adjusts, shift x5, ctrl x0.2

        var camera = new OrbitCamera
        {
            FieldOfView = fov,
            NearPlane = NearPlane,
            FarPlane = FarPlane,
            MaxDistance = 1_000_000f,
        };
        Vector3 Forward()
        {
            float yr = float.DegreesToRadians(yaw), pr = float.DegreesToRadians(pitch);
            return new Vector3(MathF.Sin(yr) * MathF.Cos(pr), MathF.Cos(yr) * MathF.Cos(pr), -MathF.Sin(pr));
        }
        void SyncCamera()
        {
            // The exact first-person-on-OrbitCamera expression the static path uses, including
            // the off-vertical clamp (LookAt with UnitY degenerates straight up/down).
            var look = eye + Forward() * 512f;
            var eyeY = TexturedScene.YUp(eye); var lookY = TexturedScene.YUp(look);
            var off = eyeY - lookY;
            float dist = MathF.Max(off.Length(), 1f);
            camera.Target = new OpenTK.Mathematics.Vector3(lookY.X, lookY.Y, lookY.Z);
            camera.Distance = dist;
            camera.Elevation = Math.Clamp(float.RadiansToDegrees(MathF.Asin(off.Y / dist)), -89.5f, 89.5f);
            camera.Azimuth = float.RadiansToDegrees(MathF.Atan2(off.X, off.Z));
        }

        string Pose() => $"eye {eye.X:0.##},{eye.Y:0.##},{eye.Z:0.##} yaw {yaw:0.#}° pitch {pitch:0.#}° fov {fov:0.#}°";
        string PoseArgs() => $"--eye {eye.X:0.##},{eye.Y:0.##},{eye.Z:0.##} --yaw {yaw:0.#} --pitch {pitch:0.#} --fov {fov:0.#}";

        // ---- hover picking -----------------------------------------------------------------
        (PickShape Shape, int LocalTri, float T, Vector3 HitY)? hover = null;

        (PickShape Shape, int LocalTri, float T, Vector3 HitY)? PickAt(float mx, float my)
        {
            int w = gw.ClientSize.X, h = gw.ClientSize.Y;
            if (w <= 0 || h <= 0) return null;
            var (o, dir) = camera.ScreenPointToRay(mx, my, w, h);
            var ro = new Vector3(o.X, o.Y, o.Z);
            var rd = new Vector3(dir.X, dir.Y, dir.Z);

            // AABB shortlist first, sorted by entry distance so triangle testing can stop as
            // soon as the best hit is nearer than the next box.
            var candidates = new List<(PickShape S, float T0)>();
            foreach (var s in opt.PickShapes)
                if (RayAabb(ro, rd, s.Min, s.Max, out var t0)) candidates.Add((s, t0));
            candidates.Sort((a, b) => a.T0.CompareTo(b.T0));

            (PickShape Shape, int LocalTri, float T, Vector3 HitY)? best = null;
            foreach (var (s, t0) in candidates)
            {
                if (best is { } b0 && t0 > b0.T) break;
                var v = s.Verts; var tr = s.Tris;
                for (int i = 0; i + 2 < tr.Length; i += 3)
                    if (RayTri(ro, rd, v[tr[i]], v[tr[i + 1]], v[tr[i + 2]], out var t)
                        && (best is not { } bb || t < bb.T))
                        best = (s, i / 3, t, ro + rd * t);
            }
            return best;
        }

        string HoverLine((PickShape Shape, int LocalTri, float T, Vector3 HitY)? hit)
        {
            if (hit is not { } hv) return "hover: nothing under the cursor (hold RMB to look, WASD to fly)";
            var ws = new Vector3(hv.HitY.X, -hv.HitY.Z, hv.HitY.Y);   // Y-up back to Skyrim
            var s = hv.Shape;
            string tail = $" — hit {ws.X:0},{ws.Y:0},{ws.Z:0}, {hv.T:0} u";
            switch (s.Kind)
            {
                case PickKind.Ref:
                    var r = s.Ref;
                    string ed = r.BaseEditorId is { Length: > 0 } e ? e : "(no EditorID)";
                    return $"REFR {r.RefKey} = FormID {opt.RuntimeId(r.RefKey)} — {ed} — {r.MeshRelPath}{tail}";
                case PickKind.Navmesh:
                    int ti = s.NavTriIds![hv.LocalTri];
                    string red = opt.RedNav.Contains(s.NavKey) ? " [RED — the finding]" : "";
                    return $"NAVM {s.NavKey} = FormID {opt.RuntimeId(s.NavKey)} tri {ti}{red}{tail}";
                default:
                    string desc = opt.TerrainDescribe?.Invoke(ws.X, ws.Y)
                               ?? (s.TerrainTex.Length > 0 ? s.TerrainTex : "(untextured fallback)");
                    return $"terrain — {desc}{tail}";
            }
        }

        void PrintDetail((PickShape Shape, int LocalTri, float T, Vector3 HitY) hv)
        {
            var ws = new Vector3(hv.HitY.X, -hv.HitY.Z, hv.HitY.Y);
            var s = hv.Shape;
            switch (s.Kind)
            {
                case PickKind.Ref:
                    var r = s.Ref;
                    Console.WriteLine($"   picked REFR {r.RefKey} = FormID {opt.RuntimeId(r.RefKey)}"
                                    + $"{(r.Highlight ? "  [highlighted]" : "")}{(r.Marker ? "  [editor marker]" : "")}");
                    Console.WriteLine($"      base {r.BaseKey} = FormID {opt.RuntimeId(r.BaseKey)}"
                                    + $"  {r.BaseEditorId ?? "(no EditorID)"}");
                    Console.WriteLine($"      mesh {r.MeshRelPath}");
                    Console.WriteLine($"      placed at {r.Position.X:0},{r.Position.Y:0},{r.Position.Z:0} scale {r.Scale:0.00}"
                                    + $"  — hit at {ws.X:0},{ws.Y:0},{ws.Z:0}, {hv.T:0} u from the eye");
                    break;
                case PickKind.Navmesh:
                    int ti = s.NavTriIds![hv.LocalTri];
                    Console.WriteLine($"   picked NAVM {s.NavKey} = FormID {opt.RuntimeId(s.NavKey)} tri {ti}"
                                    + $"{(opt.RedNav.Contains(s.NavKey) ? "  [RED — the finding]" : "")}"
                                    + $"  — hit at {ws.X:0},{ws.Y:0},{ws.Z:0}  (--red \"{s.NavKey}:{ti}\")");
                    break;
                default:
                    Console.WriteLine($"   picked terrain at {ws.X:0},{ws.Y:0},{ws.Z:0} — "
                                    + (opt.TerrainDescribe?.Invoke(ws.X, ws.Y)
                                       ?? (s.TerrainTex.Length > 0 ? s.TerrainTex : "(untextured fallback)")));
                    break;
            }
        }

        // ---- HUD: a caption-style bar at the top of the frame ------------------------------
        // Same visual language as the burned-in PNG caption (dark bar, bold white text), drawn
        // as a textured quad. Rebuilt only when its text or the window width changes.
        int hudProg = HudShader();
        int hudVao = GL.GenVertexArray();                      // attribute-less draw still needs a VAO
        int hudTex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, hudTex);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        Font? hudFont = null;
        foreach (var famName in new[] { "Segoe UI", "Arial", "Verdana" })
            if (SystemFonts.TryGet(famName, out var fam)) { hudFont = fam.CreateFont(15f, FontStyle.Bold); break; }
        if (hudFont is null)
            foreach (var fam in SystemFonts.Families) { hudFont = fam.CreateFont(15f, FontStyle.Bold); break; }
        string hudLine1 = "hover: (free the cursor and point at an object; RMB-drag to look)";
        string hudLine2 = "";
        string hudBuilt = "";
        int hudBuiltW = 0, hudBarH = 10 + 22 * 2;
        void RebuildHud(int width)
        {
            if (hudFont is null || width <= 0) return;
            string key = hudLine1 + "\n" + hudLine2;
            if (key == hudBuilt && width == hudBuiltW) return;
            hudBuilt = key; hudBuiltW = width;
            using var img = new Image<Rgba32>(width, hudBarH);
            var f = hudFont;
            img.Mutate(x =>
            {
                x.Fill(SixLabors.ImageSharp.Color.FromRgba(8, 10, 14, 200), new SixLabors.ImageSharp.RectangleF(0, 0, width, hudBarH));
                x.DrawText(hudLine1, f, SixLabors.ImageSharp.Color.White, new SixLabors.ImageSharp.PointF(12f, 7f));
                x.DrawText(hudLine2, f, SixLabors.ImageSharp.Color.FromRgb(200, 205, 215), new SixLabors.ImageSharp.PointF(12f, 7f + 22f));
            });
            var px = new byte[width * hudBarH * 4];
            img.CopyPixelDataTo(px);
            GL.BindTexture(TextureTarget.Texture2D, hudTex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, hudBarH, 0,
                          PixelFormat.Rgba, PixelType.UnsignedByte, px);
        }
        void DrawHud(int fbW, int fbH)
        {
            if (hudFont is null || fbW <= 0 || fbH <= 0) return;
            RebuildHud(fbW);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.UseProgram(hudProg);
            GL.Uniform1(GL.GetUniformLocation(hudProg, "u_bottom"), 1f - 2f * hudBarH / fbH);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, hudTex);
            GL.Uniform1(GL.GetUniformLocation(hudProg, "u_tex"), 0);
            GL.BindVertexArray(hudVao);
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            GL.BindVertexArray(0);
            GL.Disable(EnableCap.Blend);
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.CullFace);
        }

        // ---- screenshot: the frame as rendered, minus the HUD, plus the standard caption ----
        bool wantShot = false;
        void CaptureShot(int fbW, int fbH)
        {
            // The window's back buffer may be multisampled; resolve by blit before reading.
            int fbo = GL.GenFramebuffer(), rb = GL.GenRenderbuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, rb);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Rgba8, fbW, fbH);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, rb);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            GL.ReadBuffer(ReadBufferMode.Back);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);
            GL.BlitFramebuffer(0, 0, fbW, fbH, 0, 0, fbW, fbH, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            var px = new byte[fbW * fbH * 4];
            GL.ReadPixels(0, 0, fbW, fbH, PixelFormat.Rgba, PixelType.UnsignedByte, px);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteFramebuffer(fbo); GL.DeleteRenderbuffer(rb);

            string pose = Pose();
            string outPath = Path.Combine(opt.OutDir, $"{opt.SafeName}-fly-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            TexturedScene.SaveFrame(px, fbW, fbH,
                new[] { $"{opt.CellLabel} — fly capture — {pose}" },
                Array.Empty<(string, Vector3)>(),
                $"pose: {pose} | cell {opt.CellLabel}",
                outPath);
            Console.WriteLine($"   wrote {outPath}");
            Console.WriteLine($"   replay: {PoseArgs()}");
        }

        void PrintHelp()
        {
            Console.WriteLine("   fly controls: WASD move (W/S along the view), E/Space up, Q/C down, "
                            + "hold RMB + mouse to look, scroll = speed (shift x5, ctrl x0.2)");
            Console.WriteLine("   hover = identify under cursor (HUD), LMB click = full detail to console, "
                            + "P = print --eye/--yaw/--pitch, F12 = save PNG, H/F1 = this help, Esc = quit");
        }

        // ---- window setup and the loop -----------------------------------------------------
        gw.Title = $"viewprobe — {opt.CellLabel}";
        gw.VSync = VSyncMode.On;
        gw.CursorState = CursorState.Normal;
        gw.IsVisible = true;
        gw.Focus();
        SyncCamera();
        Console.WriteLine($"   interactive: {opt.PickShapes.Count:N0} pickable shape(s) "
                        + $"({opt.PickShapes.Count(p => p.Kind == PickKind.Ref):N0} ref, "
                        + $"{opt.PickShapes.Count(p => p.Kind == PickKind.Navmesh):N0} navmesh, "
                        + $"{opt.PickShapes.Count(p => p.Kind == PickKind.Terrain):N0} terrain)");
        PrintHelp();

        // Headless smoke knobs: VP_FLY_FRAMES=N renders N frames then saves a capture and
        // closes (the loop is testable without a hand on the mouse); VP_FLY_PICK=x,y runs one
        // programmatic pick at those client coordinates before closing.
        int autoFrames = int.TryParse(Environment.GetEnvironmentVariable("VP_FLY_FRAMES"), out var af) ? af : 0;
        (float X, float Y)? autoPick = null;
        if (Environment.GetEnvironmentVariable("VP_FLY_PICK") is { } aps)
        {
            var p = aps.Split(',');
            if (p.Length == 2 && float.TryParse(p[0], out var ax) && float.TryParse(p[1], out var ay))
                autoPick = (ax, ay);
        }
        int frameCount = 0;

        var titleClock = Stopwatch.StartNew();
        var pickClock = Stopwatch.StartNew();
        OpenTK.Mathematics.Vector2 lastPickPos = new(-1000f, -1000f);

        gw.UpdateFrame += e =>
        {
            var kb = gw.KeyboardState;
            var ms = gw.MouseState;
            float dt = (float)e.Time;

            bool looking = ms.IsButtonDown(MouseButton.Right);
            gw.CursorState = looking ? CursorState.Grabbed : CursorState.Normal;
            if (looking)
            {
                yaw = (yaw + ms.Delta.X * 0.15f + 360f) % 360f;
                pitch = Math.Clamp(pitch + ms.Delta.Y * 0.15f, -89f, 89f);
            }

            float mult = kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift) ? 5f
                       : kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl) ? 0.2f : 1f;
            float step = speed * mult * dt;
            var fwd = Forward();
            float yr = float.DegreesToRadians(yaw);
            var right = new Vector3(MathF.Cos(yr), -MathF.Sin(yr), 0f);
            if (kb.IsKeyDown(Keys.W)) eye += fwd * step;
            if (kb.IsKeyDown(Keys.S)) eye -= fwd * step;
            if (kb.IsKeyDown(Keys.A)) eye -= right * step;
            if (kb.IsKeyDown(Keys.D)) eye += right * step;
            if (kb.IsKeyDown(Keys.E) || kb.IsKeyDown(Keys.Space)) eye.Z += step;
            if (kb.IsKeyDown(Keys.Q) || kb.IsKeyDown(Keys.C)) eye.Z -= step;
            if (MathF.Abs(ms.ScrollDelta.Y) > 0f)
                speed = Math.Clamp(speed * MathF.Pow(1.25f, ms.ScrollDelta.Y), 20f, 20000f);

            if (kb.IsKeyPressed(Keys.P))
            {
                Console.WriteLine($"   pose: {Pose()}");
                Console.WriteLine($"   args: {PoseArgs()}");
            }
            if (kb.IsKeyPressed(Keys.F12)) wantShot = true;
            if (kb.IsKeyPressed(Keys.H) || kb.IsKeyPressed(Keys.F1)) PrintHelp();
            if (kb.IsKeyPressed(Keys.Escape)) gw.Close();

            SyncCamera();

            if (!looking)
            {
                var mp = gw.MousePosition;
                bool moved = (mp - lastPickPos).LengthSquared > 4f;
                if (moved && pickClock.ElapsedMilliseconds > 60)
                {
                    pickClock.Restart();
                    lastPickPos = mp;
                    hover = PickAt(mp.X, mp.Y);
                    hudLine1 = HoverLine(hover);
                }
                if (ms.IsButtonPressed(MouseButton.Left) && hover is { } hv) PrintDetail(hv);
            }
            else
                hudLine1 = "looking (release RMB to hover-pick)";

            if (titleClock.ElapsedMilliseconds > 150)
            {
                titleClock.Restart();
                hudLine2 = $"{Pose()} — speed {speed * mult:0} u/s — P pose, F12 capture, H help";
            }
        };

        gw.RenderFrame += e =>
        {
            int w = gw.FramebufferSize.X, h = gw.FramebufferSize.Y;
            if (w <= 0 || h <= 0) return;                        // minimized
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            renderer.Render(camera, w, h);
            var view = camera.GetViewMatrix();
            var proj = camera.GetProjectionMatrix((float)w / h);
            overlayDraw?.Invoke(view, proj);
            if (wantShot) { wantShot = false; CaptureShot(w, h); if (autoFrames > 0) gw.Close(); }
            DrawHud(w, h);
            gw.SwapBuffers();

            // Smoke path: run the pick, then request a capture — which the NEXT frame takes at
            // the pre-HUD point above, exactly like a hand-pressed F12 — and close after it.
            if (autoFrames > 0 && ++frameCount == autoFrames)
            {
                if (autoPick is { } ap)
                {
                    var hit = PickAt(ap.X, ap.Y);
                    Console.WriteLine($"   [auto] pick at {ap.X:0},{ap.Y:0}: {HoverLine(hit)}");
                    if (hit is { } hv) PrintDetail(hv);
                }
                wantShot = true;
            }
        };

        gw.Run();

        GL.DeleteProgram(hudProg);
        GL.DeleteVertexArray(hudVao);
        GL.DeleteTexture(hudTex);
        Console.WriteLine($"   interactive session ended at {Pose()}");
        Console.WriteLine($"   replay: {PoseArgs()}");
    }

    // Ray-AABB slab test; tEnter is the entry distance (0 when the origin is inside).
    static bool RayAabb(in Vector3 o, in Vector3 d, in Vector3 mn, in Vector3 mx, out float tEnter)
    {
        tEnter = 0f;
        float t0 = 0f, t1 = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            float oi = i == 0 ? o.X : i == 1 ? o.Y : o.Z;
            float di = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
            float lo = i == 0 ? mn.X : i == 1 ? mn.Y : mn.Z;
            float hi = i == 0 ? mx.X : i == 1 ? mx.Y : mx.Z;
            if (MathF.Abs(di) < 1e-12f)
            {
                if (oi < lo || oi > hi) return false;
                continue;
            }
            float inv = 1f / di;
            float ta = (lo - oi) * inv, tb = (hi - oi) * inv;
            if (ta > tb) (ta, tb) = (tb, ta);
            t0 = MathF.Max(t0, ta); t1 = MathF.Min(t1, tb);
            if (t0 > t1) return false;
        }
        tEnter = t0;
        return true;
    }

    // Möller–Trumbore, double-sided (the scene draws plenty of double-sided geometry).
    static bool RayTri(in Vector3 o, in Vector3 d, in Vector3 a, in Vector3 b, in Vector3 c, out float t)
    {
        t = 0f;
        var e1 = b - a; var e2 = c - a;
        var p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f) return false;
        float inv = 1f / det;
        var s = o - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) return false;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, q) * inv;
        if (v < 0f || u + v > 1f) return false;
        t = Vector3.Dot(e2, q) * inv;
        return t > NearPlane;                   // nothing nearer than the near plane is a hit
    }

    static int HudShader()
    {
        const string vs = @"#version 330 core
out vec2 uv;
uniform float u_bottom;   // NDC y of the bar's bottom edge
void main(){
    float x = (gl_VertexID & 1) == 1 ? 1.0 : -1.0;
    bool top = gl_VertexID >= 2;
    float y = top ? 1.0 : u_bottom;
    uv = vec2((x + 1.0) * 0.5, top ? 0.0 : 1.0);
    gl_Position = vec4(x, y, 0.0, 1.0);
}";
        const string fs = @"#version 330 core
in vec2 uv;
uniform sampler2D u_tex;
out vec4 frag;
void main(){ frag = texture(u_tex, uv); }";
        int v = GL.CreateShader(ShaderType.VertexShader); GL.ShaderSource(v, vs); GL.CompileShader(v); Check(v, "vertex");
        int f = GL.CreateShader(ShaderType.FragmentShader); GL.ShaderSource(f, fs); GL.CompileShader(f); Check(f, "fragment");
        int p = GL.CreateProgram(); GL.AttachShader(p, v); GL.AttachShader(p, f); GL.LinkProgram(p);
        GL.GetProgram(p, GetProgramParameterName.LinkStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException("hud shader link failed: " + GL.GetProgramInfoLog(p));
        GL.DeleteShader(v); GL.DeleteShader(f);
        return p;

        static void Check(int s, string what)
        {
            GL.GetShader(s, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException($"hud {what} shader: {GL.GetShaderInfoLog(s)}");
        }
    }
}
