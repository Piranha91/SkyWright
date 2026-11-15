using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
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
internal sealed class WorkbenchHost : IWorkbenchScene
{
    readonly InteractiveOptions _opt;
    readonly GlRenderer _renderer;
    readonly Action<GlMatrix4, GlMatrix4>? _overlay;
    readonly OrbitCamera _camera = new() { NearPlane = ScenePick.NearPlane, FarPlane = 131072f, MaxDistance = 1_000_000f };
    readonly EditSession _session;
    readonly Gizmo _gizmo = new();
    readonly ConflictMarks _marks = new();
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
        _session.Changed += s => { Say(s); UpdateMarks(); UpdateStatus(); if (_window is not null) Invalidate(); };
        _outDir = Environment.GetEnvironmentVariable("WB_OUT") is { Length: > 0 } o ? o : opt.OutDir;
        Directory.CreateDirectory(_outDir);
        renderer.Overlays.Add(_marks);
        renderer.Overlays.Add(_gizmo);
        _input = new ViewInput(_session, _gizmo, Project, RayAt, PickAt, () => _gizmo.FitTo(_eye));
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
        if (_window is not null) Invalidate();
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

    /// <summary>The eye ray through a framebuffer pixel, in Skyrim space.</summary>
    internal (Vector3 O, Vector3 D) RayAt(Vector2 px)
    {
        var (o, d) = _camera.ScreenPointToRay(px.X, px.Y, W, H);
        return (new Vector3(o.X, -o.Z, o.Y), new Vector3(d.X, -d.Z, d.Y));
    }

    internal PickHit? PickAt(Vector2 px)
    {
        var (o, d) = _camera.ScreenPointToRay(px.X, px.Y, W, H);
        return ScenePick.Pick(_opt.PickShapes, new Vector3(o.X, o.Y, o.Z), new Vector3(d.X, d.Y, d.Z),
            s => !(s.Mesh is { IsRendering: false }) && s.Kind is PickKind.Ref or PickKind.Terrain);
    }

    // ---- input: a plain click selects; a drag on the handle edits (ViewInput) ------------------------------------
    ViewInput _input = null!;
    internal void OnPress(Vector2 px) => _input.OnPress(px);
    internal void OnMove(Vector2 px) => _input.OnMove(px);
    internal void OnRelease(Vector2 px) => _input.OnRelease(px);
    void Select(FormKey? key) => _input.Select(key);

    static Vector3 ToV(Vec3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The marks follow the selection; the partner is the open conflict row's other reference while its own
    /// reference is the one selected.</summary>
    void UpdateMarks()
    {
        _marks.Selected = _session.Selected is { } k ? _session.RefOf(k) : null;
        _marks.Partner = null; PartnerNote = null;
        if (_session.Selected is { } s && Shell.CurrentRow is { } r && string.Equals(r.Row.RefKey, s.ToString(), StringComparison.OrdinalIgnoreCase)
            && r.Row.OtherKey is { } o && FormKey.TryFactory(o, out var ok))
        {
            if (_session.IsDrawn(ok)) _marks.Partner = _session.RefOf(ok);
            else
            {
                // Said, never silently unmarked: the probe measured the row with this reference, the viewer did not draw it.
                string why = _opt.RefOutcomes is { } ro && ro.TryGetValue(ok, out var w) ? (w.Outcome == "drawn" ? "unresolved-mesh" : w.Outcome)
                           : "not walked in this scene";
                PartnerNote = $"the conflict's other reference {o} is not drawn in this scene ({why}), so it cannot be marked";
            }
        }
    }

    /// <summary>Why the open row's other reference is not marked, when it is not; null otherwise.</summary>
    internal string? PartnerNote { get; private set; }

    internal ConflictMarks Marks => _marks;

    void UpdateStatus()
    {
        string sel = _session.Selected is { } k ? $"selected {k} {EditSession.Describe(_session.Current(k))}" : "nothing selected (click a reference)";
        string target = _writer?.Target.ToString() ?? (TargetFromEnv()?.ToString() ?? "no write target set");
        string marks = !_marks.Enabled ? "marks off" : _marks.Selected is null ? "" :
            $"cyan: selected {_marks.Selected.Ref.RefKey}" + (_marks.Partner is { } p ? $"; magenta: the conflict's other reference {p.Ref.RefKey}" : "") + " (drawn through anything in front)"
            + (PartnerNote is { } pn ? "; " + pn : "");
        string written = _session.Selected is { } wk && _session.Written.TryGetValue(wk, out var wv) ? "\n" + EditSession.WrittenLabel(wk, wv.Patch)
                       : _session.Selected is { } bk ? "\nthe view shows " + bk + " where the load order places it" : "";
        string recheck = Rechecking ? "re-checking..." : LastRecheck is { } lr ? $"last re-check: {lr.Row.Word}, {lr.New.Count} new" : RecheckRefusal is { } rr ? "re-check refused: " + rr : "";
        _status.Text = $"{sel}  |  {_session.Pending.Count} staged edit(s)  |  writes go to {target}" + (recheck.Length > 0 ? "  |  " + recheck : "") + written + (marks.Length > 0 ? "\n" + marks : "");
    }

    // ---- the window ---------------------------------------------------------------------------------------------------
    NativeGlHost _view = null!;
    bool _vsync = Environment.GetEnvironmentVariable("WB_VSYNC") != "off";
    // The frame loop. Default "demand": up to 60 frames a second, paced by a clock (on this machine WPF's own frame tick
    // ran at 31 ms with the view hosted and the child window's swap interval did not block, stage 2 receipts), and only
    // while something changed: input, the camera, the edit session, the script, the view's size or a repaint. "cap60":
    // the same pacing, drawing every frame whether or not anything changed (stage 2's loop). "free": as fast as it can
    // (measurement). "wpf-tick": once per WPF frame tick.
    readonly string _loop = Environment.GetEnvironmentVariable("WB_LOOP") is { Length: > 0 } lp ? lp : "demand";
    bool _dirty = true, _parked;
    DispatcherTimer? _sizeWatch;

    /// <summary>Something on screen changed: draw the next frame (wakes a parked demand loop).</summary>
    internal void Invalidate()
    {
        _dirty = true;
        if (_parked && _alive) { _parked = false; _window.Dispatcher.BeginInvoke(FreeFrame, DispatcherPriority.Background); }
    }
    bool _freeLoop => _loop != "wpf-tick";
    double _nextFrame;

    DispatcherFrame? _frameLoop;
    bool _alive;

    void RunWindow()
    {
        var undo = new Button { Content = "Undo (Ctrl+Z)", Margin = new Thickness(4) };
        var marksBtn = new Button { Content = "Marks", Margin = new Thickness(4), ToolTip = ConflictMarks.Legend };
        marksBtn.Click += (_, _) => { _marks.Enabled = !_marks.Enabled; UpdateStatus(); Invalidate(); };
        var dry = new Button { Content = "Dry run", Margin = new Thickness(4) };
        var write = new Button { Content = "Write patch", Margin = new Thickness(4) };
        undo.Click += (_, _) => { _session.Undo(); AfterStage(); };
        dry.Click += async (_, _) => await CommitAsync(dryRun: true, "Workbench");
        write.Click += async (_, _) => await CommitAsync(dryRun: false, "Workbench");
        var bar = new DockPanel { LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(marksBtn); buttons.Children.Add(undo); buttons.Children.Add(dry); buttons.Children.Add(write);
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons); bar.Children.Add(_status);

        // The view: a Win32 child window drawn with the scene's own context (NativeGlHost says why not GLWpfControl).
        _view = NewView(_opt.Window);
        _view.Message += OnViewMessage;
        bool first = Shell.Window is null;
        _window = Shell.EnsureWindow($"workbench — {_opt.CellLabel}");
        _window.Title = $"workbench — {_opt.CellLabel}";
        if (first && Environment.GetEnvironmentVariable("WB_WINDOW") is { Length: > 0 } ws && ws.Split('x') is [var ww, var wh])
        {
            double w = double.Parse(ww, CultureInfo.InvariantCulture), h = double.Parse(wh, CultureInfo.InvariantCulture);
            if (w < Shell.MinWidth || h < Shell.MinHeight)
                Say(FormattableString.Invariant($"window {w}x{h} asked for is below the workbench's minimum {Shell.MinWidth}x{Shell.MinHeight}; it opens at the minimum so the 3D view keeps room."));
            _window.Width = Math.Max(w, Shell.MinWidth); _window.Height = Math.Max(h, Shell.MinHeight);
        }
        Shell.Current = this;
        Shell.Mount(_view, bar);
        Shell.FillTree(BuildTree());
        if (Shell.SwitchClock is { } sw)
        {
            Say(FormattableString.Invariant($"cell switch to {Shell.SwitchCell}: scene rebuilt in {sw.Elapsed.TotalSeconds:0.0} s (request to window back)"));
            Shell.SetBanner(FormattableString.Invariant($"cell {Shell.SwitchCell} loaded in {sw.Elapsed.TotalSeconds:0.0} s"));
            Shell.SwitchClock = null;
        }

        EventHandler tick = (_, _) => { TickStamps?.Add(Clock.Elapsed.TotalMilliseconds); if (!_freeLoop && _alive) Frame(); };
        System.Windows.Media.CompositionTarget.Rendering += tick;
        _alive = true;
        void Start()
        {
            _window.Activate(); _window.Topmost = true; _window.Topmost = false; _window.Focus();
            if (_view.MakeCurrent())
                Say($"GL: {GL.GetString(StringName.Vendor)} | {GL.GetString(StringName.Renderer)} | {GL.GetString(StringName.Version)}; "
                    + $"swap interval {(_vsync ? 1 : 0)} set: {_view.SetSwapInterval(_vsync ? 1 : 0)}");
            if (_freeLoop) _window.Dispatcher.BeginInvoke(FreeFrame, DispatcherPriority.Background);
            if (Shell.PendingLand is { } land) { Shell.PendingLand = null; _landOnFirstFrame = land; }
        }
        SyncCamera();
        UpdateStatus();
        _script = Script.Attach(this);
        _frameLoop = new DispatcherFrame();
        if (first) { _window.Loaded += (_, _) => Start(); _window.Show(); }
        else _window.Dispatcher.BeginInvoke(Start, DispatcherPriority.Loaded);
        if (!Shell.Closed) Dispatcher.PushFrame(_frameLoop);
        _alive = false;
        _sizeWatch?.Stop();
        if (Shell.Closed && Environment.GetEnvironmentVariable("WB_DUMP") is { Length: > 0 } dump) Shell.Dump(this, dump);
        System.Windows.Media.CompositionTarget.Rendering -= tick;
        if (_writer is not null) _writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Shell.Current = null;
        Shell.Unmount();
        _view.Dispose();
        // Hand the context back to its own (hidden) window: viewprobe's teardown clears it through GLFW, which fails
        // while it is current on the destroyed view.
        _opt.Window.MakeCurrent();
        Say(Shell.Closed ? "window closed" : "scene closed for a cell switch");
    }

    /// <summary>End this scene's frame loop (the window closed, or a cell switch was asked for).</summary>
    internal void StopFrame() { if (_frameLoop is { } f) f.Continue = false; }

    /// <summary>Ask viewprobe for the worklist's cell <paramref name="index"/>: this scene closes, viewprobe rebuilds, and
    /// the shell calls the host again with the new scene.</summary>
    internal void RequestCell(int index)
    {
        _opt.CellSwitch = (index, 0);
        Shell.Flush();
        StopFrame();
    }

    static unsafe NativeGlHost NewView(OpenTK.Windowing.Desktop.NativeWindow gw) =>
        new((IntPtr)GLFW.GetWin32Window(gw.WindowPtr), GLFW.GetWGLContext(gw.WindowPtr));

    void FreeFrame()
    {
        if (!_alive) return;
        if (_loop == "demand" && !_dirty && !(_script?.WantsFrames ?? false) && _view.ClientSize() == (_fbW, _fbH))
        {
            // Nothing to draw: park until Invalidate, with a slow timer that only watches for a resize.
            _parked = true;
            _sizeWatch ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
                (_, _) => { if (_parked && _view.ClientSize() != (_fbW, _fbH)) Invalidate(); }, _window.Dispatcher);
            return;
        }
        if (_loop is "cap60" or "demand")
        {
            double now = Clock.Elapsed.TotalMilliseconds;
            if (now < _nextFrame - 1.5) { System.Threading.Thread.Sleep(1); }
            else { _nextFrame = Math.Max(_nextFrame + 1000.0 / 60.0, now - 5); Frame(); }
        }
        else Frame();
        if (_alive) _window.Dispatcher.BeginInvoke(FreeFrame, DispatcherPriority.Background);
    }

    internal bool Vsync => _vsync;

    void OnViewMessage(int msg, int x, int y, IntPtr wParam)
    {
        Invalidate();
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
        _dirty = false;
        var t0 = Clock.Elapsed.TotalMilliseconds;
        (_fbW, _fbH) = _view.ClientSize();
        if (_landOnFirstFrame is { } land) { _landOnFirstFrame = null; Land(FormKey.Factory(land)); }
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

    // ---- the shell's side of a scene: the Records tree, landing on a reference, the state dump ----------------------
    string? _landOnFirstFrame;

    /// <summary>The cell this scene was built for, as the bundle names cells (a FormKey string).</summary>
    internal string? SceneCell => _opt.CellKey?.ToString();

    internal IReadOnlyList<TreeNode> BuildTree()
    {
        var cells = _opt.RefCells ?? new Dictionary<FormKey, FormKey>();
        var drawn = RecordsTree.Entries(DrawnAll().Select(r => (r.Ref.RefKey, r.Ref.BaseEditorId)), _opt.RefOutcomes, cells);
        var conflictRefs = Shell.Ranked.SelectMany(r => new[] { r.Row.RefKey, r.Row.OtherKey }).Where(k => k is not null)
                                 .Select(k => k!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loadedCells = drawn.Where(d => d.Cell is not null).Select(d => d.Cell!.Value.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (SceneCell is { } sc) loadedCells.Add(sc);
        var unloaded = Shell.Ranked.Where(r => r.Row.Cell is { } c && !loadedCells.Contains(c))
            .GroupBy(r => r.Row.Cell!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => r.Row.RefKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        string ws = _opt.Interior ? $"interior {_opt.CellLabel}" : $"worldspace {_opt.Worldspace}";
        return RecordsTree.Build(ws, drawn, c => c == _opt.CellKey ? $"{c} {_opt.CellLabel} (this scene's cell)" : $"{c} (loaded ring)",
                                 conflictRefs, unloaded);
    }

    /// <summary>Every drawn reference of the scene, one entry per reference.</summary>
    internal IEnumerable<RefOcclusion> DrawnAll() =>
        (_opt.Occlusion?.Refs ?? new List<RefOcclusion>()).Where(r => r.Meshes.Count > 0 && !r.Ref.RefKey.IsNull)
            .GroupBy(r => r.Ref.RefKey).Select(g => g.First());

    /// <summary>Select <paramref name="key"/> and frame the camera on it: the eye looks at the reference's box centre
    /// from a distance that fits the box, trying a few headings and pitches until a pick at the screen centre names the
    /// reference (other geometry can stand in front). Says which pose it kept and whether the centre pick named it.</summary>
    internal bool Land(FormKey key)
    {
        if (!_session.IsDrawn(key)) { Shell.SetBanner($"{key} is not drawn in this scene; nothing to land on."); Shell.LastLanding = $"{key}: not drawn"; return false; }
        Select(key);
        var (kept, clear, crossing, coveredBy) = FrameOn(key);
        Shell.LastLanding = kept;
        Shell.SetBanner(clear ? $"landed on {key}" : crossing ? $"landed on {key}; it is inside or behind {coveredBy} from every side tried" : $"landed near {key}; the view's centre misses its shapes");
        Say("landing: " + Shell.LastLanding);
        return clear || crossing;
    }

    /// <summary>After a write or a re-check moved a reference on screen, point the camera at where it is drawn now
    /// (the selection and the banner stay as they are).</summary>
    internal void Reframe(FormKey key)
    {
        if (!_session.IsDrawn(key)) return;
        var (kept, _, _, _) = FrameOn(key);
        Say("re-framed on the moved reference: " + kept);
    }

    /// <summary>Frame the camera on <paramref name="key"/> as drawn now (its pose, not the scene build's). Returns the
    /// one-line account and whether the centre ray meets it first, or at least crosses it.</summary>
    (string Account, bool Clear, bool Crossing, string CoveredBy) FrameOn(FormKey key)
    {
        var ro = _session.RefOf(key);
        var p = ToV(_session.Current(key).Position);
        // RefPlacementEdit.Apply moves the box with the reference, so its centre is where the reference is drawn now.
        Vector3 c = ro.Min.X <= ro.Max.X ? (ro.Min + ro.Max) * 0.5f : p;
        float r = ro.Min.X <= ro.Max.X ? MathF.Max((ro.Max - ro.Min).Length() * 0.5f, 16f) : 64f;
        float dist = MathF.Max(r * 3f, 250f);
        // Prefer a pose where the reference is the first thing the centre ray meets; else the first pose where the
        // centre ray at least crosses it (an overlap row's reference is often inside the other one, so covered).
        (float Yaw, float Pitch)? clear = null, crossing = null; string coveredBy = "nothing";
        int tried = 0;
        foreach (var pitch in new[] { 30f, 55f, 80f })
        {
            foreach (var dy in new[] { 0f, 90f, 180f, 270f, 45f, 135f, 225f, 315f })
            {
                float yaw = (_yaw + dy) % 360f;
                _yaw = yaw; _pitch = pitch; _eye = c - Forward() * dist; SyncCamera();
                tried++;
                var centre = new Vector2(W * 0.5f, H * 0.5f);
                var first = CentreName(PickAt(centre));
                if (first == key.ToString()) { clear = (yaw, pitch); break; }
                if (crossing is null && PickOwn(centre, key) is not null) { crossing = (yaw, pitch); coveredBy = first; }
            }
            if (clear is not null) break;
        }
        var keep = clear ?? crossing;
        if (keep is { } k) { _yaw = k.Yaw; _pitch = k.Pitch; _eye = c - Forward() * dist; SyncCamera(); }
        string how = clear is not null ? $"it is the first thing the screen-centre ray meets (pose {tried} of 24 tried)"
                   : crossing is not null ? $"the screen-centre ray crosses it, but {coveredBy} is in front from all 24 poses tried"
                   : "the screen-centre ray misses its shapes from every pose tried (its box centre is outside them)";
        var account = FormattableString.Invariant(
            $"{key}: box centre {c.X:0.#},{c.Y:0.#},{c.Z:0.#}, radius {r:0.#} u; eye {dist:0} u away at yaw {_yaw:0} pitch {_pitch:0}; ") + how;
        return (account, clear is not null, crossing is not null, coveredBy);
    }

    /// <summary>Whether <paramref name="key"/> is in view: its box centre projects inside the view, and the screen-centre
    /// ray crosses its own shapes. Says what it measured.</summary>
    internal (bool InView, string Account) InView(FormKey key)
    {
        var ro = _session.RefOf(key);
        var c = ro.Min.X <= ro.Max.X ? (ro.Min + ro.Max) * 0.5f : ToV(_session.Current(key).Position);
        var sp = Project(c);
        bool inside = sp is { } s && s.X >= 0 && s.X < W && s.Y >= 0 && s.Y < H;
        bool crosses = PickOwn(new Vector2(W * 0.5f, H * 0.5f), key) is not null;
        string at = sp is { } q ? FormattableString.Invariant($"box centre {c.X:0.#},{c.Y:0.#},{c.Z:0.#} drawn at pixel {q.X:0},{q.Y:0} of {W}x{H}") : "box centre behind the eye";
        return (inside && crosses, $"{at}; screen-centre ray crosses it {crosses}");
    }

    static string CentreName(PickHit? h) => h?.Shape is { Kind: PickKind.Ref } sh ? sh.Ref.RefKey.ToString() : h is null ? "nothing" : h.Shape.Kind.ToString();

    /// <summary>The ray through a pixel against <paramref name="key"/>'s own shapes only (what is in front does not count).</summary>
    internal int ScenePickShapesOf(FormKey key) => _opt.PickShapes.Count(s => s.Kind == PickKind.Ref && s.Ref.RefKey == key);

    internal PickHit? PickOwn(Vector2 px, FormKey key)
    {
        var (o, d) = _camera.ScreenPointToRay(px.X, px.Y, W, H);
        return ScenePick.Pick(_session.RefOf(key).Shapes, new Vector3(o.X, o.Y, o.Z), new Vector3(d.X, d.Y, d.Z), _ => true);
    }

    internal object SceneState()
    {
        var centre = PickAt(new Vector2(W * 0.5f, H * 0.5f));
        return new
        {
            cell = SceneCell, cellLabel = _opt.CellLabel, worldspace = _opt.Worldspace.ToString(), interior = _opt.Interior,
            worklistCell = _opt.WorklistCell, drawnRefs = DrawnAll().Count(),
            walkedRefs = _opt.RefOutcomes?.Count,
            walkOutcomes = _opt.RefOutcomes?.GroupBy(x => x.Value.Outcome).ToDictionary(g => g.Key, g => g.Count()),
            marks = new { enabled = _marks.Enabled, selected = _marks.Selected?.Ref.RefKey.ToString(), partner = _marks.Partner?.Ref.RefKey.ToString(), partnerNote = PartnerNote, legend = ConflictMarks.Legend },
            drawnRefKeys = DrawnAll().Select(r => r.Ref.RefKey.ToString()).OrderBy(k => k, StringComparer.Ordinal),
            selected = _session.Selected?.ToString(), staged = _session.Pending.Count,
            camera = new { eye = new[] { _eye.X, _eye.Y, _eye.Z }, yaw = _yaw, pitch = _pitch, fov = _fov, framebuffer = new[] { W, H } },
            centreRayCrossesSelected = _session.Selected is { } sk && PickOwn(new Vector2(W * 0.5f, H * 0.5f), sk) is not null,
            pickAtCentre = centre?.Shape is { Kind: PickKind.Ref } sh ? sh.Ref.RefKey.ToString() : centre is null ? null : centre.Shape.Kind.ToString(),
        };
    }

    /// <summary>Post a mouse message to the 3D view's own window at a framebuffer (device) pixel, so the Win32 path
    /// (WndProc, lParam decoding) carries it exactly as it carries the real mouse.</summary>
    internal void PostMouse(int msg, Vector2 px)
    {
        int x = (int)MathF.Round(px.X), y = (int)MathF.Round(px.Y);
        var lp = (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
        var wp = (IntPtr)(msg == 0x202 ? 0 : 0x1);                       // MK_LBUTTON held while down and moving
        if (!PostMessage(_view.Handle, msg, wp, lp)) Say($"PostMessage {msg:X} failed (Win32 error {Marshal.GetLastWin32Error()})");
    }
    [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    internal int FrameCount => _frame;
    internal string? SceneStateCentre()
    {
        var c = PickAt(new Vector2(W * 0.5f, H * 0.5f));
        return c?.Shape is { Kind: PickKind.Ref } sh ? sh.Ref.RefKey.ToString() : c is null ? null : c.Shape.Kind.ToString();
    }

    // ---- what the script and the bar call ---------------------------------------------------------------------------
    internal EditSession Session => _session;
    internal string? CellOf(FormKey key) => _opt.RefCells is { } rc && rc.TryGetValue(key, out var c) ? c.ToString() : null;

    // The MCP state tools reach the scene through these: the same members the bar and the script call.
    EditSession IWorkbenchScene.Session => _session;
    string? IWorkbenchScene.Cell => SceneCell;
    string IWorkbenchScene.Status => _status.Text;
    string? IWorkbenchScene.CellOf(FormKey key) => CellOf(key);
    void IWorkbenchScene.Select(FormKey? key) => SelectKey(key);
    void IWorkbenchScene.AfterStage() => AfterStage();
    Task<string?> IWorkbenchScene.CommitAsync(bool dryRun, string patch) => CommitAsync(dryRun, patch, awaitRecheck: false);
    string? IWorkbenchScene.CommitRefusal => CommitRefusal;
    Task<string?> IWorkbenchScene.StartRecheck(string patch) => StartRecheck(patch);
    Task? IWorkbenchScene.RecheckRunning => RecheckRunning;
    RecheckResult? IWorkbenchScene.LastRecheck => LastRecheck;
    string? IWorkbenchScene.RecheckRefusal => RecheckRefusal;
    bool IWorkbenchScene.Rechecking => Rechecking;
    internal Gizmo Handles => _gizmo;
    internal Window MainWindow => _window;
    internal string OutDir => _outDir;
    internal void SetPose(Vector3 eye, float yaw, float pitch) { _eye = eye; _yaw = yaw; _pitch = pitch; SyncCamera(); }
    internal void SelectKey(FormKey? key) => Select(key);
    internal IEnumerable<RefOcclusion> DrawnRefs() => (_opt.Occlusion?.Refs ?? new List<RefOcclusion>()).Where(r => r.Meshes.Count > 0 && _session.IsDrawn(r.Ref.RefKey));
    internal float EyeDistance(Vec3 p) => Vector3.Distance(_eye, ToV(p));
    internal void AfterStage() { if (_session.Selected is { } k) _gizmo.Centre = ToV(_session.Current(k).Position); }

    /// <summary>A manual session's target (the launcher's instance, profile, game and Output Folder); null in the bundle path.</summary>
    internal static WriteTarget? ManualTarget { get; set; }

    internal static WriteTarget? TargetFromEnv()
    {
        var inst = Environment.GetEnvironmentVariable("WB_INSTANCE");
        var prof = Environment.GetEnvironmentVariable("WB_PROFILE");
        var data = Environment.GetEnvironmentVariable("WB_GAME_DATA");
        var output = Environment.GetEnvironmentVariable("WB_OUTPUT") is { Length: > 0 } o ? o : null;
        return ManualTarget ?? (inst is { Length: > 0 } && prof is { Length: > 0 } && data is { Length: > 0 } ? new WriteTarget(inst, prof, data, output) : null);
    }

    internal async Task<PatchWriter> WriterAsync()
    {
        if (_writer is not null) return _writer;
        var target = TargetFromEnv()
            ?? throw new InvalidOperationException("no write target: set WB_INSTANCE, WB_PROFILE and WB_GAME_DATA to the MO2 instance, profile and game Data folder the scene was built from.");
        var exe = Environment.GetEnvironmentVariable("WB_SERVER") is { Length: > 0 } s ? s : Program.DefaultServerExe();
        var dataDir = Path.Combine(_outDir, "housecarl-data");
        var corpus = Environment.GetEnvironmentVariable("WB_CORPUS") is { Length: > 0 } cp ? cp : Program.DefaultCorpus();
        _writer = await PatchWriter.StartAsync(exe, target, dataDir, corpus, Say);
        UpdateStatus();
        return _writer;
    }

    /// <summary>Dry-run or write the staged edits (the bar's buttons, the script's <c>commit</c>, the MCP <c>commit</c> verb).
    /// A real write re-checks the open conflict row; <paramref name="awaitRecheck"/> false starts the re-check and returns
    /// once the patch is written (<see cref="RecheckRunning"/> holds it). Returns the server's text, or null with
    /// <see cref="CommitRefusal"/> saying why.</summary>
    internal async Task<string?> CommitAsync(bool dryRun, string patchName, bool awaitRecheck = true)
    {
        CommitRefusal = null;
        try
        {
            var w = await WriterAsync();
            // The re-check finds the patch by its plugin name, so a real write goes to a name no mod folder holds yet
            // (the server would otherwise write a second, renamed patch).
            var plugin = PluginName(patchName);
            if (!dryRun && Directory.Exists(w.Target.PatchRoot)
                && Directory.EnumerateDirectories(w.Target.PatchRoot).FirstOrDefault(d => File.Exists(Path.Combine(d, plugin))) is { } holder)
                throw new InvalidOperationException($"the mod folder '{Path.GetFileName(holder)}' already holds {plugin}; write to a new patch name.");
            var net = _session.Net();
            var cells = _opt.RefCells ?? new Dictionary<FormKey, FormKey>();
            var parents = PatchWriter.ParentRecordsSentence(net.Select(e => (e.Ref, cells.TryGetValue(e.Ref, out var c) ? c : (FormKey?)null)),
                                                            _opt.Interior ? null : _opt.Worldspace);
            var text = parents + "\n" + await w.CommitAsync(net, patchName, dryRun);
            Say((dryRun ? "dry run" : "write") + " answer:\n" + text);
            // A written edit stays drawn where the patch puts it, labelled (stage-4 review M-1), and the camera follows it.
            if (!dryRun && text.Contains("wrote ", StringComparison.Ordinal))
            {
                _session.MarkWritten(PluginName(patchName));
                if ((net.FirstOrDefault(e => e.Ref == _session.Selected) ?? net.FirstOrDefault()) is { } moved) Reframe(moved.Ref);
            }
            // After a write, the detector re-runs on the patch's cell when a conflict row is open (stage 4).
            if (!dryRun && Shell.Bundle is not null && Shell.CurrentRow is not null)
            {
                var t = RecheckAsync(patchName);
                RecheckRunning = t;
                if (awaitRecheck) await t;
            }
            return text;
        }
        catch (Exception ex) { CommitRefusal = ex is AggregateException ae ? ae.GetBaseException().Message : ex.Message; Say("refused: " + CommitRefusal); return null; }
    }

    /// <summary>Why the last commit was refused, in one sentence; null when it was not.</summary>
    internal string? CommitRefusal { get; private set; }

    /// <summary>The last re-check started, running or done (null before the first).</summary>
    internal Task<string?>? RecheckRunning { get; private set; }

    /// <summary>Start a re-check without waiting for it (the MCP <c>recheck</c> verb); <see cref="RecheckRunning"/> holds it.</summary>
    internal Task<string?> StartRecheck(string patchName) => RecheckRunning = RecheckAsync(patchName);

    /// <summary>The last re-check: its result, or the sentence that refused it; and whether one is running.</summary>
    internal static string PluginName(string patchName) => patchName.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ? patchName : patchName + ".esp";

    /// <summary>What the status bar says about the selected reference's drawn placement (written or load order).</summary>
    internal string StatusText => _status.Text;

    internal RecheckResult? LastRecheck { get; private set; }
    internal string? RecheckRefusal { get; private set; }
    internal bool Rechecking { get; private set; }

    /// <summary>Re-run the open row's detector on the written patch's cell, unpatched and patched, and show what conflictdiff
    /// and the frozen rule say (<see cref="Recheck"/>). Runs off the UI thread; the banner says "re-checking" meanwhile.</summary>
    internal async Task<string?> RecheckAsync(string patchName)
    {
        try
        {
            var open = Shell.CurrentRow ?? throw new InvalidOperationException("no conflict row is open, so there is nothing to re-check; open one from the Conflicts tab.");
            var b = Shell.Bundle ?? throw new InvalidOperationException("no bundle is open, so the re-check has no detector command to run.");
            var t = _writer?.Target ?? TargetFromEnv() ?? throw new InvalidOperationException("no write target is set, so the patch cannot be found.");
            var plugin = patchName.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ? patchName : patchName + ".esp";
            var mod = Recheck.PatchFolder(t.PatchRoot, plugin);
            // The view shows the edited reference where this patch puts it (read back from the patch), never silently
            // where the load order still has it (stage-4 review M-1).
            if (FormKey.TryFactory(open.Row.RefKey, out var rk) && _session.IsDrawn(rk)
                && !(_session.Written.TryGetValue(rk, out var wr) && string.Equals(wr.Patch, plugin, StringComparison.OrdinalIgnoreCase)))
            {
                var wtr = await WriterAsync();
                var (pp, pr) = await wtr.ReadPlacementAsync(open.Row.RefKey, source: plugin);
                _session.ShowWritten(rk, new RefPlacementEdit.Pose(pp, pr, _session.Current(rk).Scale), plugin);
                Reframe(rk);
            }
            var evalrun = Program.FromCheckoutPublic("WB_EVALRUN", "evaluation controller evalrun.py", "_geometry-harness", "evalrun.py");
            var harness = Path.GetDirectoryName(Path.GetFullPath(evalrun))!;
            var conflictdiff = Environment.GetEnvironmentVariable("WB_CONFLICTDIFF") is { Length: > 0 } cd ? cd
                : Path.Combine(harness, "conflictdiff", "bin", "Release", "net9.0", "conflictdiff.exe");
            var buildDirs = (Environment.GetEnvironmentVariable("WB_PROBE_DIRS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
            var game = Path.GetDirectoryName(Path.GetFullPath(t.DataDir.TrimEnd('\\', '/')))!;
            var work = Path.Combine(_outDir, "recheck-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss"));
            Rechecking = true; RecheckRefusal = null;
            Shell.SetBanner($"re-checking conflict {open.Rank} ({open.Row.Kind}) with {plugin}: the detector runs on the patch's cell, unpatched and patched; the window stays usable.");
            UpdateStatus();
            var row = open.Row; var arm = b.Arm; var dir = b.Dir;
            var r = await Task.Run(() => Recheck.RunAll(dir, arm, row, plugin, mod, t.Instance, t.Profile, game, work, evalrun, conflictdiff,
                buildDirs, harness, s => _window?.Dispatcher.BeginInvoke(() => Say("re-check: " + s))));
            LastRecheck = r;
            string news = r.New.Count == 0 ? "no new conflict involving the moved reference" : $"{r.New.Count} new conflict(s) involving the moved reference: " + string.Join("; ", r.New.Select(n => n.Id));
            Shell.SetBanner(FormattableString.Invariant($"re-check of conflict {open.Rank}: {r.Row.Word}. {news}. ({r.Verdict}; {r.WallSeconds:0.0} s)"));
            Say("re-check: row " + r.Row.Id + ": " + r.Row.Sentence);
            foreach (var n in r.New) Say("re-check: new row " + n.Id + ": " + n.Sentence);
            Say("re-check: " + r.BuildSentence);
            Say(FormattableString.Invariant($"re-check: wall time {r.WallSeconds:0.0} s; conflictdiff's lines in {Path.Combine(work, "conflictdiff.txt")}"));
            return r.Row.Word;
        }
        catch (Exception ex)
        {
            RecheckRefusal = ex is AggregateException ae ? ae.GetBaseException().Message : ex.Message;
            Shell.SetBanner("re-check refused: " + RecheckRefusal);
            return null;
        }
        finally { Rechecking = false; UpdateStatus(); }
    }

    /// <summary>Count the back buffer's pixels inside <paramref name="ro"/>'s projected box that a test says are its mark
    /// colour (call between the draw and the swap). Returns (inside, of which mark-coloured).</summary>
    internal (int Box, int Marked) CountMarked(RefOcclusion ro, Func<byte, byte, byte, bool> isMark)
    {
        var px = ReadBack();
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var cx in new[] { ro.Min.X, ro.Max.X }) foreach (var cy in new[] { ro.Min.Y, ro.Max.Y }) foreach (var cz in new[] { ro.Min.Z, ro.Max.Z })
            if (Project(new Vector3(cx, cy, cz)) is { } p) { x0 = MathF.Min(x0, p.X); y0 = MathF.Min(y0, p.Y); x1 = MathF.Max(x1, p.X); y1 = MathF.Max(y1, p.Y); }
        int ix0 = Math.Max(0, (int)x0), iy0 = Math.Max(0, (int)y0), ix1 = Math.Min(W - 1, (int)x1), iy1 = Math.Min(H - 1, (int)y1);
        int box = 0, marked = 0;
        for (int y = iy0; y <= iy1; y++)
            for (int x = ix0; x <= ix1; x++)
            {
                int i = ((H - 1 - y) * W + x) * 4;                    // GL rows run bottom-up
                box++;
                if (isMark(px[i], px[i + 1], px[i + 2])) marked++;
            }
        return (box, marked);
    }

    byte[] ReadBack()
    {
        int w = W, h = H;
        int fbo = GL.GenFramebuffer(), rb = GL.GenRenderbuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, rb);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Rgba8, w, h);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, rb);
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        GL.ReadBuffer(ReadBufferMode.Back);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);
        GL.BlitFramebuffer(0, 0, w, h, 0, 0, w, h, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        var px = new byte[w * h * 4];
        GL.ReadPixels(0, 0, w, h, PixelFormat.Rgba, PixelType.UnsignedByte, px);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.DeleteFramebuffer(fbo); GL.DeleteRenderbuffer(rb);
        return px;
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
