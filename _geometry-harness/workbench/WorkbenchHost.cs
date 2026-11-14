using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SkyRender;
using ViewProbe;
using GlMatrix4 = OpenTK.Mathematics.Matrix4;
using Window = System.Windows.Window;
using MouseButton = System.Windows.Input.MouseButton;

namespace Workbench;

/// <summary>The workbench window. viewprobe builds the scene exactly as for <c>--interactive</c> and hands it here
/// (<see cref="InteractiveViewer.Host"/>) on its own GL context, which the <see cref="GLWpfControl"/> renders with
/// (<c>GLWpfControlSettings.ContextToUse</c>, GLWpfControl 4.3.3). A plain click selects; a drag on the handle moves or
/// turns the selected reference live; Ctrl+Z undoes; the bar's buttons dry-run or write the staged edits.
///
/// Headless: <c>WB_SCRIPT=file</c> runs the steps in <see cref="Script"/> (the same handlers the mouse uses), so every
/// behaviour is testable without a hand on the mouse; <c>WB_OUT</c> is where captures and dumps go.</summary>
internal sealed class WorkbenchHost
{
    readonly InteractiveOptions _opt;
    readonly GlRenderer _renderer;
    readonly Action<GlMatrix4, GlMatrix4>? _overlay;
    readonly OrbitCamera _camera = new() { NearPlane = ScenePick.NearPlane, FarPlane = 131072f, MaxDistance = 1_000_000f };
    readonly EditSession _session;
    readonly Gizmo _gizmo = new();
    readonly TextBlock _status = new() { Margin = new Thickness(6, 2, 6, 2), TextWrapping = TextWrapping.Wrap };
    readonly string _outDir;
    Window _window = null!;
    Vector3 _eye; float _yaw, _pitch, _fov;
    PatchWriter? _writer;
    int _frame;

    internal static readonly List<string> Log = new();
    internal static void Say(string line) { Console.WriteLine("   [wb] " + line); Log.Add(line); }

    WorkbenchHost(InteractiveOptions opt, GlRenderer renderer, (Vector3 Eye, Vector3 Look, float FovDeg) cam, Action<GlMatrix4, GlMatrix4>? overlay)
    {
        _opt = opt; _renderer = renderer; _overlay = overlay;
        _eye = cam.Eye;
        var d0 = cam.Look - cam.Eye;
        _yaw = (float.RadiansToDegrees(MathF.Atan2(d0.X, d0.Y)) + 360f) % 360f;
        _pitch = float.RadiansToDegrees(MathF.Atan2(-d0.Z, MathF.Max(MathF.Sqrt(d0.X * d0.X + d0.Y * d0.Y), 1e-3f)));
        _fov = cam.FovDeg;
        _camera.FieldOfView = _fov;
        _session = new EditSession(opt.Occlusion?.Refs ?? (IEnumerable<RefOcclusion>)Array.Empty<RefOcclusion>());
        _session.Changed += s => { Say(s); UpdateStatus(); };
        _outDir = Environment.GetEnvironmentVariable("WB_OUT") is { Length: > 0 } o ? o : opt.OutDir;
        Directory.CreateDirectory(_outDir);
        renderer.Overlays.Add(_gizmo);
    }

    /// <summary>The <see cref="InteractiveViewer.Host"/> entry: runs the window until it closes.</summary>
    internal static void Run(InteractiveOptions opt, GlRenderer renderer, (Vector3 Eye, Vector3 Look, float FovDeg) cam, Action<GlMatrix4, GlMatrix4>? overlay)
        => new WorkbenchHost(opt, renderer, cam, overlay).RunWindow();

    // ---- camera: the fly-through's first-person-on-OrbitCamera expression ------------------------------------------
    Vector3 Forward()
    {
        float yr = float.DegreesToRadians(_yaw), pr = float.DegreesToRadians(_pitch);
        return new Vector3(MathF.Sin(yr) * MathF.Cos(pr), MathF.Cos(yr) * MathF.Cos(pr), -MathF.Sin(pr));
    }
    void SyncCamera()
    {
        var look = _eye + Forward() * 512f;
        var eyeY = TexturedScene.YUp(_eye); var lookY = TexturedScene.YUp(look);
        var off = eyeY - lookY;
        float dist = MathF.Max(off.Length(), 1f);
        _camera.Target = new OpenTK.Mathematics.Vector3(lookY.X, lookY.Y, lookY.Z);
        _camera.Distance = dist;
        _camera.Elevation = Math.Clamp(float.RadiansToDegrees(MathF.Asin(off.Y / dist)), -89.5f, 89.5f);
        _camera.Azimuth = float.RadiansToDegrees(MathF.Atan2(off.X, off.Z));
        _gizmo.FitTo(_eye);
    }

    // The view's client size in pixels, read each frame.
    int _fbW = 1, _fbH = 1;
    internal int W => _fbW;
    internal int H => _fbH;

    /// <summary>A Skyrim-space point to framebuffer pixels (top-left origin), or null behind the eye.</summary>
    internal Vector2? Project(Vector3 skyrim)
    {
        var y = TexturedScene.YUp(skyrim);
        var clip = new OpenTK.Mathematics.Vector4(y.X, y.Y, y.Z, 1f) * (_camera.GetViewMatrix() * _camera.GetProjectionMatrix((float)W / H));
        if (clip.W <= 1e-4f) return null;
        return new Vector2((clip.X / clip.W * 0.5f + 0.5f) * W, (1f - (clip.Y / clip.W * 0.5f + 0.5f)) * H);
    }

    internal PickHit? PickAt(Vector2 px)
    {
        var (o, d) = _camera.ScreenPointToRay(px.X, px.Y, W, H);
        return ScenePick.Pick(_opt.PickShapes, new Vector3(o.X, o.Y, o.Z), new Vector3(d.X, d.Y, d.Z),
            s => !(s.Mesh is { IsRendering: false }) && s.Kind is PickKind.Ref or PickKind.Terrain);
    }

    // ---- input: a plain click selects; a drag on the handle edits ---------------------------------------------------
    Vector2? _press; Handle _dragHandle; RefPlacementEdit.Pose _dragStart; bool _moved;

    internal void OnPress(Vector2 px)
    {
        _press = px; _moved = false;
        _dragHandle = _session.Selected is not null ? _gizmo.HitTest(px, Project) : Handle.None;
        if (_dragHandle != Handle.None) _dragStart = _session.Current(_session.Selected!.Value);
        _gizmo.Hot = _dragHandle;
    }

    internal void OnMove(Vector2 px)
    {
        if (_press is not { } p0) { _gizmo.Hot = _session.Selected is not null ? _gizmo.HitTest(px, Project) : Handle.None; return; }
        if (Vector2.Distance(p0, px) > 3f) _moved = true;
        if (_dragHandle == Handle.None || !_moved) return;
        _session.Preview(_session.Selected!.Value, DragPose(p0, px));
    }

    internal void OnRelease(Vector2 px)
    {
        if (_press is not { } p0) return;
        _press = null;
        if (_dragHandle != Handle.None && _moved)
        {
            var key = _session.Selected!.Value;
            _session.Stage(key, DragPose(p0, px));
            _gizmo.Centre = ToV(_session.Current(key).Position);
        }
        else if (!_moved) SelectAt(px);
        _dragHandle = Handle.None; _gizmo.Hot = Handle.None;
    }

    RefPlacementEdit.Pose DragPose(Vector2 from, Vector2 to)
    {
        var s = _dragStart;
        if (_dragHandle == Handle.TurnZ)
        {
            float rad = _gizmo.TurnAmount(from, to, Project);
            return s with { Rotation = RefPlacementEdit.Turned(s.Rotation, Vector3.UnitZ, rad) };
        }
        float amount = _gizmo.MoveAmount(_dragHandle, from, to, Project);
        var axis = _dragHandle switch { Handle.MoveX => Vector3.UnitX, Handle.MoveY => Vector3.UnitY, _ => Vector3.UnitZ };
        var np = ToV(s.Position) + axis * amount;
        return s with { Position = new Vec3(np.X, np.Y, np.Z) };
    }

    void SelectAt(Vector2 px)
    {
        var hit = PickAt(px);
        if (hit?.Shape is { Kind: PickKind.Ref } sh && _session.IsDrawn(sh.Ref.RefKey)) Select(sh.Ref.RefKey);
        else Select(null);
    }

    void Select(FormKey? key)
    {
        _session.Select(key);
        _gizmo.Centre = key is { } k ? ToV(_session.Current(k).Position) : null;
        _gizmo.FitTo(_eye);
    }

    static Vector3 ToV(Vec3 v) => new(v.X, v.Y, v.Z);

    void UpdateStatus()
    {
        string sel = _session.Selected is { } k ? $"selected {k} {EditSession.Describe(_session.Current(k))}" : "nothing selected (click a reference)";
        string target = _writer?.Target.ToString() ?? (TargetFromEnv()?.ToString() ?? "no write target set");
        _status.Text = $"{sel}  |  {_session.Pending.Count} staged edit(s)  |  writes go to {target}";
    }

    // ---- the window ---------------------------------------------------------------------------------------------------
    NativeGlHost _view = null!;
    bool _vsync = Environment.GetEnvironmentVariable("WB_VSYNC") != "off";
    // The frame loop. Default "cap60": the view draws itself at up to 60 frames a second, paced by a clock, because on
    // this machine WPF's own frame tick ran at 31 ms with the view hosted and the child window's swap interval did not
    // block (stage 2 receipts). "free": as fast as it can (measurement). "wpf-tick": once per WPF frame tick.
    readonly string _loop = Environment.GetEnvironmentVariable("WB_LOOP") is { Length: > 0 } lp ? lp : "cap60";
    bool _freeLoop => _loop != "wpf-tick";
    double _nextFrame;

    void RunWindow()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var undo = new Button { Content = "Undo (Ctrl+Z)", Margin = new Thickness(4) };
        var dry = new Button { Content = "Dry run", Margin = new Thickness(4) };
        var write = new Button { Content = "Write patch", Margin = new Thickness(4) };
        undo.Click += (_, _) => { _session.Undo(); AfterStage(); };
        dry.Click += async (_, _) => await CommitAsync(dryRun: true, "Workbench");
        write.Click += async (_, _) => await CommitAsync(dryRun: false, "Workbench");
        var bar = new DockPanel { LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(undo); buttons.Children.Add(dry); buttons.Children.Add(write);
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons); bar.Children.Add(_status);

        // The view: a Win32 child window drawn with the scene's own context (NativeGlHost says why not GLWpfControl).
        var gw = _opt.Window;
        _view = NewView(gw);
        _view.Message += OnViewMessage;
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar); root.Children.Add(_view);
        _window = new Window { Title = $"workbench — {_opt.CellLabel}", Width = 1440, Height = 960, Content = root };
        if (Environment.GetEnvironmentVariable("WB_WINDOW") is { Length: > 0 } ws && ws.Split('x') is [var ww, var wh])
        { _window.Width = double.Parse(ww, CultureInfo.InvariantCulture); _window.Height = double.Parse(wh, CultureInfo.InvariantCulture); }

        System.Windows.Media.CompositionTarget.Rendering += (_, _) =>
        {
            TickStamps?.Add(Clock.Elapsed.TotalMilliseconds);
            if (!_freeLoop) Frame();
        };
        _window.Closed += async (_, _) => { if (_writer is not null) await _writer.DisposeAsync(); };
        _window.Loaded += (_, _) =>
        {
            _window.Activate(); _window.Topmost = true; _window.Topmost = false; _window.Focus();
            if (_view.MakeCurrent())
            {
                Say($"GL: {GL.GetString(StringName.Vendor)} | {GL.GetString(StringName.Renderer)} | {GL.GetString(StringName.Version)}; "
                    + $"swap interval {(_vsync ? 1 : 0)} set: {_view.SetSwapInterval(_vsync ? 1 : 0)}");
            }
            if (_freeLoop) _window.Dispatcher.BeginInvoke(FreeFrame, System.Windows.Threading.DispatcherPriority.Background);
        };
        SyncCamera();
        UpdateStatus();
        _script = Script.Load(this);
        app.Run(_window);
        // Hand the context back to its own (hidden) window: viewprobe's teardown clears it through GLFW, which fails
        // while it is current on the destroyed view.
        _opt.Window.MakeCurrent();
        Say("window closed");
    }

    static unsafe NativeGlHost NewView(OpenTK.Windowing.Desktop.NativeWindow gw) =>
        new((IntPtr)GLFW.GetWin32Window(gw.WindowPtr), GLFW.GetWGLContext(gw.WindowPtr));

    void FreeFrame()
    {
        if (_loop == "cap60")
        {
            double now = Clock.Elapsed.TotalMilliseconds;
            if (now < _nextFrame - 1.5) { System.Threading.Thread.Sleep(1); }
            else { _nextFrame = Math.Max(_nextFrame + 1000.0 / 60.0, now - 5); Frame(); }
        }
        else Frame();
        if (_window.IsLoaded) _window.Dispatcher.BeginInvoke(FreeFrame, System.Windows.Threading.DispatcherPriority.Background);
    }

    internal bool Vsync => _vsync;

    void OnViewMessage(int msg, int x, int y, IntPtr wParam)
    {
        var p = new Vector2(x, y);
        switch (msg)
        {
            case 0x201: OnPress(p); break;                                   // WM_LBUTTONDOWN
            case 0x202: OnRelease(p); break;                                 // WM_LBUTTONUP
            case 0x204: _look = p; break;                                    // WM_RBUTTONDOWN
            case 0x205: _look = null; break;                                 // WM_RBUTTONUP
            case 0x200:                                                      // WM_MOUSEMOVE
                if (_look is { } l && (wParam.ToInt64() & 0x2) != 0)
                { _yaw = (_yaw + (p.X - l.X) * 0.15f + 360f) % 360f; _pitch = Math.Clamp(_pitch + (p.Y - l.Y) * 0.15f, -89f, 89f); _look = p; SyncCamera(); }
                else OnMove(p);
                break;
            case 0x100:                                                      // WM_KEYDOWN
            {
                int vk = (int)wParam.ToInt64();
                bool ctrl = (GetKeyState(0x11) & 0x8000) != 0, shift = (GetKeyState(0x10) & 0x8000) != 0;
                if (ctrl && vk == 'Z') { _session.Undo(); AfterStage(); break; }
                float step = shift ? 300f : 60f;
                var right = Vector3.Normalize(Vector3.Cross(Forward(), Vector3.UnitZ));
                if (vk == 'W') _eye += Forward() * step;
                if (vk == 'S') _eye -= Forward() * step;
                if (vk == 'A') _eye -= right * step;
                if (vk == 'D') _eye += right * step;
                if (vk == 'E') _eye += Vector3.UnitZ * step;
                if (vk == 'Q') _eye -= Vector3.UnitZ * step;
                SyncCamera();
                break;
            }
        }
    }
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);

    Vector2? _look;

    Script? _script;
    internal readonly Stopwatch Clock = Stopwatch.StartNew();
    internal Action? AfterFrame;
    internal List<double>? TickStamps;
    internal readonly List<double> RenderCallMs = new();
    internal readonly List<double> SwapMs = new();

    internal bool Paused;

    void Frame()
    {
        if (Paused || !_view.MakeCurrent()) return;
        var t0 = Clock.Elapsed.TotalMilliseconds;
        (_fbW, _fbH) = _view.ClientSize();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.Viewport(0, 0, W, H);
        _renderer.Render(_camera, W, H);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _overlay?.Invoke(_camera.GetViewMatrix(), _camera.GetProjectionMatrix((float)W / H));
        _frame++;
        RenderCallMs.Add(Clock.Elapsed.TotalMilliseconds - t0);
        _script?.Tick();                    // a capture step reads the back buffer before the swap
        var t1 = Clock.Elapsed.TotalMilliseconds;
        _view.Swap();
        SwapMs.Add(Clock.Elapsed.TotalMilliseconds - t1);
        AfterFrame?.Invoke();
    }

    // ---- what the script and the bar call ---------------------------------------------------------------------------
    internal EditSession Session => _session;
    internal Gizmo Handles => _gizmo;
    internal Window MainWindow => _window;
    internal string OutDir => _outDir;
    internal void SetPose(Vector3 eye, float yaw, float pitch) { _eye = eye; _yaw = yaw; _pitch = pitch; SyncCamera(); }
    internal void SelectKey(FormKey? key) => Select(key);
    internal void AfterStage() { if (_session.Selected is { } k) _gizmo.Centre = ToV(_session.Current(k).Position); }

    internal static WriteTarget? TargetFromEnv()
    {
        var inst = Environment.GetEnvironmentVariable("WB_INSTANCE");
        var prof = Environment.GetEnvironmentVariable("WB_PROFILE");
        var data = Environment.GetEnvironmentVariable("WB_GAME_DATA");
        return inst is { Length: > 0 } && prof is { Length: > 0 } && data is { Length: > 0 } ? new WriteTarget(inst, prof, data) : null;
    }

    internal async Task<PatchWriter> WriterAsync()
    {
        if (_writer is not null) return _writer;
        var target = TargetFromEnv()
            ?? throw new InvalidOperationException("no write target: set WB_INSTANCE, WB_PROFILE and WB_GAME_DATA to the MO2 instance, profile and game Data folder the scene was built from.");
        var exe = Environment.GetEnvironmentVariable("WB_SERVER") is { Length: > 0 } s ? s : Program.DefaultServerExe();
        var allowed = new[] { Program.LabInstance };
        var dataDir = Path.Combine(_outDir, "housecarl-data");
        var corpus = Environment.GetEnvironmentVariable("WB_CORPUS") is { Length: > 0 } cp ? cp : Program.DefaultCorpus();
        _writer = await PatchWriter.StartAsync(exe, target, allowed, dataDir, corpus, Say);
        UpdateStatus();
        return _writer;
    }

    internal async Task<string?> CommitAsync(bool dryRun, string patchName)
    {
        try
        {
            var w = await WriterAsync();
            var text = await w.CommitAsync(_session.Net(), patchName, dryRun);
            Say((dryRun ? "dry run" : "write") + " answer:\n" + text);
            return text;
        }
        catch (Exception ex) { Say("refused: " + ex.Message); return null; }
    }

    /// <summary>Read the frame back (resolving the control's multisampled framebuffer) and save a captioned PNG.</summary>
    internal string Capture(string name)
    {
        int w = W, h = H, src = 0;            // the view's back buffer, before the swap
        int fbo = GL.GenFramebuffer(), rb = GL.GenRenderbuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, rb);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Rgba8, w, h);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, rb);
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, src);
        GL.ReadBuffer(ReadBufferMode.Back);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);
        GL.BlitFramebuffer(0, 0, w, h, 0, 0, w, h, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        var px = new byte[w * h * 4];
        GL.ReadPixels(0, 0, w, h, PixelFormat.Rgba, PixelType.UnsignedByte, px);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, src);
        GL.DeleteFramebuffer(fbo); GL.DeleteRenderbuffer(rb);
        var path = Path.Combine(_outDir, name + ".png");
        string pose = FormattableString.Invariant($"eye {_eye.X:0.##},{_eye.Y:0.##},{_eye.Z:0.##} yaw {_yaw:0.#} pitch {_pitch:0.#} fov {_fov:0.#}");
        TexturedScene.SaveFrame(px, w, h, new[] { $"{_opt.CellLabel} — workbench — {name} — {pose}" },
            Array.Empty<(string, Vector3)>(), $"pose: {pose} | cell {_opt.CellLabel}", path);
        Say($"captured {path}");
        return path;
    }

    /// <summary>The reference's drawn pick triangles' vertices, Skyrim space, for comparing a preview with a rebuild.</summary>
    internal string DumpRef(FormKey key, string name)
    {
        var ro = _session.RefOf(key);
        var verts = ro.Shapes.SelectMany(s => s.Verts).Select(v => new[] { v.X, -v.Z, v.Y }).ToList();
        var path = Path.Combine(_outDir, name + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            refKey = key.ToString(),
            shapes = ro.Shapes.Count,
            pose = EditSession.Describe(_session.Current(key)),
            vertices = verts,
        }));
        Say($"dumped {verts.Count} vertices of {key} to {path}");
        return path;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    internal bool IsForeground => GetForegroundWindow() == new WindowInteropHelper(_window).Handle;

    internal static unsafe int RefreshRate()
    {
        var m = GLFW.GetPrimaryMonitor();
        var mode = GLFW.GetVideoMode(m);
        return mode == null ? -1 : mode->RefreshRate;
    }
}
