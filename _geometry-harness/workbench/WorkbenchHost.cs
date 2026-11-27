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
using SkyWright.Engine;
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
    readonly ResultMarks _resultMarks = new();

    /// <summary>Mark the drawn references named, each in its group's shape (the Results tab's filter); returns how many
    /// of them this scene draws. An empty list clears the marks.</summary>
    internal int MarkResults(IEnumerable<(FormKey Key, MarkStyle Style)> refs)
    {
        _resultMarks.Set(refs.Where(r => _session.IsDrawn(r.Key)).Select(r => { var ro = _session.RefOf(r.Key); return (ro.Min, ro.Max, r.Style); }));
        Invalidate("marks");
        UpdateKey();
        return _resultMarks.Marked;
    }
    internal int ResultsMarked => _resultMarks.Marked;
    readonly TextBlock _status = new() { Margin = new Thickness(6, 2, 6, 2), TextWrapping = TextWrapping.Wrap };
    readonly string _outDir;
    Window _window = null!;
    Vector3 _eye; float _yaw, _pitch, _fov;
    PatchWriter? _writer;
    int _frame;

    internal static readonly List<string> Log = new();
    /// <summary>Why a read-only scene takes no edits.</summary>
    internal const string ReadOnlyWhy = "this scene is read-only (it was opened from the Results or World tab): nothing is moved or written here.";
    /// <summary>This scene takes no edits: no handles, no staging, no writes.</summary>
    internal bool ReadOnly { get; } = Shell.ReadOnlyScene;
    bool IWorkbenchScene.ReadOnly => ReadOnly;
    /// <summary>The decided rows this scene draws, compared when it arrives so the window's list needs no client; a failure is said.</summary>
    static async Task CompareDecidedAsync()
    {
        try { await DecisionCompare.SceneOpenedAsync(new LiveSurface(), Shell.Standings); }
        catch (Exception ex) { var m = "the stored decisions could not be compared: " + ex.Message; Say(m); Shell.SetBanner(m); }
    }

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
        // the live scene's cells (LiveCells.cs): the loaded square is built here, before anything reads the drawn references
        _live = new LiveCells(opt);
        if (_live.Available && opt.Grid is { } home)
        {
            // the scene's own cell before the window shows; the rest of the square is built, nearest first, once it draws
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var square = LiveCells.Square(home, opt.GridsToLoad).Where(opt.Loaded.Contains).ToList();
            if (square.Contains(home))
            {
                try { opt.PrefetchCell?.Invoke(home, false, true, CancellationToken.None); }   // read in parallel first; the build then uploads
                catch (Exception ex) { Say($"the scene's cell could not be read ahead ({ex.Message}); it is built without"); }
                _live.Add(home, false);
                _prefetched[(home, false)] = true;
            }
            foreach (var w in LiveCells.Plan(home, opt.GridsToLoad, _live.FarWidth, _live.Kinds, opt.Reach).Add) _queue.Enqueue(w);
            _queueClock.Restart(); _queueBuilt = 0;
            StartPrefetch();
            Say(FormattableString.Invariant($"loaded cells: {_live.Held.Count} built in {sw.Elapsed.TotalSeconds:0.0} s before the window shows; {_queue.Count} more follow: the {opt.GridsToLoad}x{opt.GridsToLoad} square ({opt.GridsToLoadSource}) and the large references out to {_live.FarWidth}x{_live.FarWidth} ({opt.LargeGridSource})"));
        }
        _session = new EditSession(opt.Occlusion?.Refs ?? (IEnumerable<RefOcclusion>)Array.Empty<RefOcclusion>());
        // an edit of the object shown as another version: it is drawn as the scene has it again first, so the edit starts from what is drawn
        _session.Replacing += () => { if (_versionKey is not null) LoadOrderPanel.ForgetObjectSwitches(); };
        _session.Changed += s => { Say(s); UpdateMarks(); UpdateStatus(); if (_window is not null) Invalidate("edit"); if (!ReadOnly) SaveSession(); };
        _viewState = new ViewState(_session, () => DrawnRefs(), _gizmo);
        _viewState.Changed += s => { Say(s); UpdateStatus(); if (_window is not null) Invalidate("view"); };
        _tints.State = _viewState;
        _outDir = Environment.GetEnvironmentVariable("WB_OUT") is { Length: > 0 } o ? o : opt.OutDir;
        Directory.CreateDirectory(_outDir);
        renderer.Overlays.Add(_resultMarks);
        renderer.Overlays.Add(_marks);
        var ui = Dispatcher.CurrentDispatcher;          // the crossing is measured off the window's thread
        _marks.Changed = () => ui.BeginInvoke(() => { UpdateKey(); Invalidate("marks"); });
        if (!ReadOnly) renderer.Overlays.Add(_gizmo);     // a read-only scene has no move handles
        renderer.Overlays.Insert(0, _tints);
        BuildLayers(renderer);
        _input = new ViewInput(_session, _gizmo, Project, RayAt, PickAt, () => _gizmo.FitTo(_eye))
        {
            Holding = () => _viewState.ShowingOriginal,
            Locked = () => ReadOnly ? ReadOnlyWhy
                : _session.Selected is { } sk && _session.IsDisabled(sk) ? $"{sk} is to be disabled, and an object takes one fix, a move or a disable; undo the disable first to move it." : null,
            Refused = s => { Say(s); Shell.SetBanner(s); },
        };
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
        if (_window is not null) Invalidate("camera");
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
        return ScenePick.Pick(_opt.PickShapes, new Vector3(o.X, o.Y, o.Z), new Vector3(d.X, d.Y, d.Z), Pickable);
    }

    /// <summary>What a click can hit: a drawn reference or the terrain, not one the view has hidden.</summary>
    internal static bool Pickable(PickShape s) => Pickable(s, ViewLayers.IsOn(Layer.Terrain));
    internal static bool Pickable(PickShape s, bool terrainShown) => !(s.Mesh is { IsRendering: false } or { IsHiddenByHost: true })
        && (s.Kind is PickKind.Ref || s.Kind is PickKind.Terrain && terrainShown);

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
        // the open conflict row's other reference, else the other reference of the Results row this scene was opened for
        string? other = _session.Selected is { } s0 && Shell.CurrentRow is { } r && string.Equals(r.Row.RefKey, s0.ToString(), StringComparison.OrdinalIgnoreCase)
            ? r.Row.OtherKey : _session.Selected is { } s1 && Shell.CurrentRow is null ? Shell.PartnerFor?.Invoke(s1.ToString()) : null;
        if (_session.Selected is { } s && other is { } o && FormKey.TryFactory(o, out var ok))
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
        if (_marks.Selected is not null) { _marks.StandIn = new(); _marks.StandInTris = new(); _marks.StandInFor = null; }
        _marks.Set(_marks.Selected, _marks.Partner);
        UpdateKey();
        LoadOrderPanel.Selection(_session.Selected?.ToString(), other);
    }

    // ---- the view's key: what each colour and box in the view is (walkthrough problem 5) ----------------------------------
    readonly WrapPanel _key = new() { Margin = new Thickness(6, 2, 6, 0) };

    static string NameOf(RefOcclusion ro) => (ro.Ref.BaseEditorId is { Length: > 0 } b ? b + " " : "") + ro.Ref.RefKey;

    /// <summary>The key under the view: a swatch and words for each mark drawn now, nothing when the marks are off.</summary>
    void UpdateKey()
    {
        _key.Children.Clear();
        UIElement Item(System.Windows.Media.Color c, bool box, string words, string? tip = null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 2), ToolTip = tip };
            var brush = new System.Windows.Media.SolidColorBrush(c);
            sp.Children.Add(new Border { Width = 14, Height = 14, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center,
                                         Background = box ? null : brush, BorderBrush = brush, BorderThickness = new Thickness(box ? 2 : 0) });
            sp.Children.Add(new TextBlock { Text = words, VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }
        static System.Windows.Media.Color C(OpenTK.Mathematics.Vector3 v) => System.Windows.Media.Color.FromRgb((byte)(v.X * 255), (byte)(v.Y * 255), (byte)(v.Z * 255));
        if (_marks.Enabled && _marks.StandInFor is { } standIn) _key.Children.Add(Item(C(ConflictMarks.SelectedColour), true, "stand-in for " + standIn, "an object with no model has nothing of its own to draw; this cyan shape stands in for it"));
        if (_marks.Enabled && _marks.Selected is { } sel) _key.Children.Add(Item(C(ConflictMarks.SelectedColour), false, $"{NameOf(sel)}, selected", "tinted cyan; Outlines shows its outline through anything in front"));
        if (_marks.Enabled && _marks.Partner is { } par) _key.Children.Add(Item(C(ConflictMarks.PartnerColour), false, $"{NameOf(par)}, the other object of the clash", "tinted magenta; Outlines shows its outline through anything in front"));
        if (_marks.Enabled && _marks.Selected is not null && _marks.Partner is not null)
            _key.Children.Add(Item(C(ConflictMarks.OverlapColour), false, _marks.CrossingSegments > 0 ? "where they pass into each other" : _marks.Overlap ?? "measuring where they cross",
                                   "a band along where the two objects' own surfaces meet; Outlines shows the line where they cross through anything in front"));
        if (_marks.Enabled && _marks.Selected is not null && _marks.Partner is not null && _marks.Buried is { } buried)
            _key.Children.Add(Item(C(ConflictMarks.BuriedColour), false, buried.Words, buried.Tip));
        var words = ResultRules.Groups.ToDictionary(g => g.Value.Mark, g => g.Value.Words);
        foreach (var style in _resultMarks.Enabled ? _resultMarks.StylesShown.OrderBy(x => x) : Enumerable.Empty<MarkStyle>())
        {
            var (colour, shape, colourName) = ResultMarks.Styles[style];
            _key.Children.Add(Item(C(colour), true, $"{shape}: a listed row, {words[style]}", $"the Results tab marks each listed row of the open pair that this scene draws, {colourName}"));
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
            PartnerNote ?? "";
        string written = _session.Selected is { } dk && _session.IsDisabled(dk)
                           ? "\nthe view hides " + dk + ": it is " + (_session.WrittenDisabled.Contains(dk) ? $"written initially disabled to {_session.Written[dk].Patch}, not enabled" : "staged to be disabled") + " (hold Space to see it)"
                       : _session.Selected is { } wk && _session.Written.TryGetValue(wk, out var wv) ? "\n" + EditSession.WrittenLabel(wk, wv.Patch)
                       : _session.Selected is { } sk2 && _session.Pending.Any(e => e.Ref == sk2) ? "\nthe view shows " + sk2 + " at its staged place" + (_session.IsNudged(sk2) ? " (a nudge)" : "") + " (hold Space to see where the load order places it)"
                       : _session.Selected is { } bk ? "\nthe view shows " + bk + " where the load order places it" : "";
        string recheck = Rechecking ? "re-checking" : LastRecheck is { } lr ? $"last re-check: {lr.Row.Word}, {lr.New.Count} new" : RecheckRefusal is { } rr ? "re-check refused: " + rr : "";
        if (ReadOnly)
        {
            _status.Text = (Shell.SceneCaption is { } cap ? cap + "  |  " : "") + $"{sel}  |  read-only scene on {SceneDirector.Order?.Label ?? "its load order"}" + (marks.Length > 0 ? "\n" + marks : "");
            return;
        }
        _status.Text = $"{sel}  |  {_session.Pending.Count} staged edit(s)  |  writes go to {target}" + (recheck.Length > 0 ? "  |  " + recheck : "") + written + (marks.Length > 0 ? "\n" + marks : "");
    }

    // ---- the window ---------------------------------------------------------------------------------------------------
    NativeGlHost _view = null!;
    bool _vsync = Environment.GetEnvironmentVariable("WB_VSYNC") != "off";
    // The frame loop. Default "demand": one frame per desktop composition (FramePacer: the child window's swap interval
    // does not block, and WPF's own frame tick ran at about 30 ms with the view hosted), and only while something
    // changed: input, the camera, a held fly key, the edit session, the script, the view's size or a repaint. "cap60":
    // the same pacing, drawing at every composition whether or not anything changed. "free": as fast as it can
    // (measurement). "wpf-tick": once per WPF frame tick.
    readonly string _loop = Environment.GetEnvironmentVariable("WB_LOOP") is { Length: > 0 } lp ? lp : "demand";
    bool _dirty = true, _parked;
    DispatcherTimer? _sizeWatch;

    /// <summary>Something on screen changed: draw the next frame (wakes a parked demand loop). <paramref name="why"/>
    /// names the first change since the last frame in the frame log.</summary>
    internal void Invalidate(string why = "other")
    {
        _dirty = true;
        if (_dirtyWhy is null) { _dirtyWhy = why; _dirtyAt = Clock.Elapsed.TotalMilliseconds; }
        if (!_alive) return;
        if (_parked) { _parked = false; _resumed = true; }
        _pacer?.Request();
    }
    string? _dirtyWhy; double _dirtyAt; bool _resumed, _drewOnce; int _messages;
    FramePacer? _pacer;

    DispatcherFrame? _frameLoop;
    static bool _launchLogStarted;
    bool _alive;

    void RunWindow()
    {
        var undo = new Button { Content = "Undo (Ctrl+Z)", Margin = new Thickness(4) };
        var dry = new Button { Content = "Dry run", Margin = new Thickness(4) };
        var write = new Button { Content = "Write patch", Margin = new Thickness(4) };
        undo.Click += (_, _) => { _session.Undo(); AfterStage(); };
        dry.Click += async (_, _) => await CommitAsync(dryRun: true, "Workbench");
        write.Click += async (_, _) => await CommitAsync(dryRun: false, "Workbench");
        // an edit scene's bar has a fixed height, so the 3D view keeps its size whatever the edits list holds; a read-only
        // scene's bar is as tall as its rows, so no empty band sits between the status line and the view tools
        var bar = new DockPanel { LastChildFill = true };
        if (!ReadOnly) bar.Height = 270;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        if (!ReadOnly) { buttons.Children.Add(undo); buttons.Children.Add(dry); buttons.Children.Add(write); }
        DockPanel.SetDock(buttons, Dock.Right);
        var tools = ToolsRow();
        DockPanel.SetDock(tools, Dock.Bottom);
        // the edits list and, beside it, the selected reference's context (item 5)
        var pendingPanel = new Grid();
        pendingPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pendingPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var edits = PendingPanel(); var context = ContextPanel();
        Grid.SetColumn(context, 1);
        pendingPanel.Children.Add(edits); pendingPanel.Children.Add(context);
        DockPanel.SetDock(pendingPanel, Dock.Bottom);
        _layerBar = new LayerBar(() => _layers, (l, on) => { try { Shell.SetBanner(SetLayer(l, on)); } catch (InvalidOperationException ex) { Shell.SetBanner(ex.Message); _layerBar?.Refresh(); } },
                                 (o, on) => { try { Shell.SetBanner(SetBorderOption(o, on)); } catch (InvalidOperationException ex) { Shell.SetBanner(ex.Message); _layerBar?.Refresh(); } });
        // the layer toggles, with the frame-rate switch at the row's right end and its readout under the row when shown
        var topRow = new DockPanel();
        var fps = FpsToggle();
        fps.Padding = new Thickness(6, 1, 6, 1); fps.Margin = new Thickness(2, 2, 6, 2); fps.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(fps, Dock.Right);
        var grids = GridsControl();
        DockPanel.SetDock(grids, Dock.Right);
        topRow.Children.Add(fps); topRow.Children.Add(grids); topRow.Children.Add(_layerBar.Element);
        DockPanel.SetDock(topRow, Dock.Top);
        bar.Children.Add(topRow);
        _fpsText.Width = double.NaN; _fpsText.Margin = new Thickness(8, 0, 8, 2);
        DockPanel.SetDock(_fpsText, Dock.Top);
        bar.Children.Add(_fpsText);
        DockPanel.SetDock(_key, Dock.Top);
        bar.Children.Add(_key);
        bar.Children.Add(tools);
        if (!ReadOnly) bar.Children.Add(pendingPanel);
        bar.Children.Add(buttons); bar.Children.Add(_status);

        // The view: a Win32 child window drawn with the scene's own context (NativeGlHost says why not GLWpfControl).
        _view = NewView(_opt.Window);
        _view.Message += OnViewMessage;
        bool first = Shell.Window is null;
        _window = Shell.EnsureWindow($"SkyWright — {_opt.CellLabel}");
        _window.Title = $"SkyWright — {_opt.CellLabel}";
        if (first && Environment.GetEnvironmentVariable("WB_WINDOW") is { Length: > 0 } ws && ws.Split('x') is [var ww, var wh])
        {
            double w = double.Parse(ww, CultureInfo.InvariantCulture), h = double.Parse(wh, CultureInfo.InvariantCulture);
            if (w < Shell.MinWidth || h < Shell.MinHeight)
                Say(FormattableString.Invariant($"window {w}x{h} asked for is below the workbench's minimum {Shell.MinWidth}x{Shell.MinHeight}; it opens at the minimum so the 3D view keeps room."));
            _window.Width = Math.Max(w, Shell.MinWidth); _window.Height = Math.Max(h, Shell.MinHeight);
        }
        Shell.Current = this;
        SceneDirector.SceneShown();
        if (!ReadOnly)
        {
            try { RestoreSession(); } catch (InvalidOperationException ex) { Say(ex.Message); Shell.SetBanner(ex.Message); }
            _ = CompareDecidedAsync();
        }
        Shell.Mount(_view, bar);
        Shell.FillTree(BuildTree());
        if (Shell.SwitchClock is { } sw)
        {
            Say(FormattableString.Invariant($"cell switch to {Shell.SwitchCell}: scene rebuilt in {sw.Elapsed.TotalSeconds:0.0} s (request to window back)"));
            Shell.SetBanner(FormattableString.Invariant($"cell {Shell.SwitchCell} loaded in {sw.Elapsed.TotalSeconds:0.0} s"));
            Shell.SwitchClock = null;
        }

        EventHandler tick = (_, _) => { TickStamps?.Add(Clock.Elapsed.TotalMilliseconds); if (_loop == "wpf-tick" && _alive) Frame(); };
        System.Windows.Media.CompositionTarget.Rendering += tick;
        _alive = true;
        void Start()
        {
            _window.Activate(); _window.Topmost = true; _window.Topmost = false; _window.Focus();
            if (_view.MakeCurrent())
                Say($"GL: {GL.GetString(StringName.Vendor)} | {GL.GetString(StringName.Renderer)} | {GL.GetString(StringName.Version)}; "
                    + $"swap interval {(_vsync ? 1 : 0)} set: {_view.SetSwapInterval(_vsync ? 1 : 0)}");
            if (_loop == "free") _window.Dispatcher.BeginInvoke(FreeFrame, DispatcherPriority.Background);
            else if (_loop != "wpf-tick") { _pacer = new FramePacer(_window.Dispatcher, PacedFrame); _pacer.Request(); }
            _sizeWatch ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
                (_, _) => { if (_view.ClientSize() != (_fbW, _fbH)) Invalidate("resize"); }, _window.Dispatcher);
            ShowFps(_showFps);
            if (!Frames.Logging && !_launchLogStarted && Environment.GetEnvironmentVariable("WB_FPS_LOG") is { Length: > 0 } fl)
            { _launchLogStarted = true; Say(Frames.StartLog(fl, Now)); }
            if (Shell.PendingLand is { } land) { Shell.PendingLand = null; _landOnFirstFrame = land; }
            else if (ViewLayers.TakePose(SceneDirector.Showing?.Cell) is { } kept) SetPose(kept.Eye, kept.Yaw, kept.Pitch);   // the same cell again, for a layer
            else if (SceneDirector.Showing is not null) FrameScene();   // a cell asked for with no reference to land on
            ScheduleCell();   // the rest of the loaded square, once the window draws
            ApplyLod();       // distant LOD streams around the camera from the first frames
        }
        SyncCamera();
        UpdateStatus();
        _script = Script.Attach(this);
        _frameLoop = new DispatcherFrame();
        if (first) { _window.Loaded += (_, _) => Start(); _window.Show(); }
        else _window.Dispatcher.BeginInvoke(Start, DispatcherPriority.Loaded);
        if (!Shell.Closed) Dispatcher.PushFrame(_frameLoop);
        _alive = false;
        try { _preloadProcess?.Kill(); } catch (InvalidOperationException) { }   // a preload for a scene that is gone
        // the scene's asset owners are disposed when this loop returns: the prefetch stops reading through them first
        _prefetch?.Cancel();
        try { _prefetchTask.Wait(TimeSpan.FromSeconds(30)); } catch (AggregateException) { }
        _sizeWatch?.Stop();
        _fpsTimer?.Stop();
        _pacer?.Dispose();
        if (_pacer?.Fallback is { } fb) Say(fb);
        if (Shell.Closed && Frames.Logging) Say(Frames.StopLog());
        if (Shell.Closed && Environment.GetEnvironmentVariable("WB_DUMP") is { Length: > 0 } dump) Shell.Dump(this, dump);
        System.Windows.Media.CompositionTarget.Rendering -= tick;
        Shell.Current = null;
        SceneDirector.SceneClosed();
        Shell.Unmount();
        _view.Dispose();
        // Hand the context back to its own (hidden) window: viewprobe's teardown clears it through GLFW, which fails
        // while it is current on the destroyed view.
        _opt.Window.MakeCurrent();
        // the LOD's meshes belong to the scene's renderer, which is disposed when this loop returns
        _lod?.Dispose(); _lod = null;
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

    /// <summary>The paced loop's frame: drawn when something changed (or every composition under cap60); after it,
    /// another is asked for only while something still wants frames, else the loop parks until <see cref="Invalidate"/>.</summary>
    void PacedFrame()
    {
        if (!_alive) return;
        if (_loop == "demand" && !WantsFrame()) { _parked = true; return; }
        Frame();
        if (_loop == "cap60" || WantsFrame()) _pacer?.Request(); else _parked = true;
    }
    bool WantsFrame() => _dirty || _held.Count > 0 || (_script?.WantsFrames ?? false) || _view.ClientSize() != (_fbW, _fbH);

    /// <summary>The "free" loop: a frame as soon as the dispatcher is free, always (a measurement of the draw alone).</summary>
    void FreeFrame()
    {
        if (!_alive) return;
        Frame();
        if (_alive) _window.Dispatcher.BeginInvoke(FreeFrame, DispatcherPriority.Background);
    }

    internal bool Vsync => _vsync;

    void OnViewMessage(int msg, int x, int y, IntPtr wParam)
    {
        _messages++;
        Invalidate(MessageName(msg));
        var p = new Vector2(x, y);
        switch (msg)
        {
            case 0x201 when (GetKeyState(0x12) & 0x8000) != 0:              // Alt+click: hide (Alt+Shift: one part); view only
            {
                bool part = (GetKeyState(0x10) & 0x8000) != 0;
                if (PickAt(p) is { Shape: { Kind: PickKind.Ref } sh })
                    try { Shell.SetBanner(part ? _viewState.HidePart(sh) : _viewState.Hide(sh.Ref.RefKey)); } catch (InvalidOperationException ex) { Shell.SetBanner(ex.Message); }
                break;
            }
            case 0x201: OnPress(p); break;                                   // WM_LBUTTONDOWN
            case 0x202: OnRelease(p); break;                                 // WM_LBUTTONUP
            case 0x204: _look = p; break;                                    // WM_RBUTTONDOWN
            case 0x205: _look = null; break;                                 // WM_RBUTTONUP
            case 0x200:                                                      // WM_MOUSEMOVE
                if (_look is { } l && (wParam.ToInt64() & 0x2) != 0)
                { _yaw = (_yaw + (p.X - l.X) * 0.15f + 360f) % 360f; _pitch = Math.Clamp(_pitch + (p.Y - l.Y) * 0.15f, -89f, 89f); _look = p; SyncCamera(); }
                else OnMove(p);
                break;
            case 0x20A:                                                      // WM_MOUSEWHEEL: the point is in screen pixels
            {
                var pt = new POINT { X = x, Y = y };
                ScreenToClient(_view.Handle, ref pt);
                WheelZoom(new Vector2(pt.X, pt.Y), (short)((wParam.ToInt64() >> 16) & 0xFFFF) / 120f);
                break;
            }
            case 0x0008: _viewState.OnFocusLost(); _held.Clear(); break;          // WM_KILLFOCUS: a held key's release will not come
            case 0x101:                                                      // WM_KEYUP
                _held.Remove((int)wParam.ToInt64());
                _viewState.OnKey((int)wParam.ToInt64(), down: false, repeat: false);
                break;
            case 0x100:                                                      // WM_KEYDOWN
            {
                int vk = (int)wParam.ToInt64();
                if (_viewState.OnKey(vk, down: true, repeat: (y & 0x4000) != 0)) break;
                bool ctrl = (GetKeyState(0x11) & 0x8000) != 0, shift = (GetKeyState(0x10) & 0x8000) != 0;
                if (ctrl && vk == 'Z') { if (!ReadOnly) { _session.Undo(); AfterStage(); } break; }
                // a fly key is held, not stepped: the eye moves by the clock in each frame while it is down (Fly)
                if (FlyKeys.Contains((char)vk) && _held.Add(vk) && _held.Count == 1) _flyAt = Now;
                break;
            }
        }
    }
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hwnd, ref POINT p);

    /// <summary>The wheel moves the eye toward (up) or away from (down) the point under the mouse: a fifth of the way to
    /// what is under it per notch, or a fixed step when nothing is (Shift: five times as far).</summary>
    internal void WheelZoom(Vector2 px, float notches)
    {
        if (notches == 0) return;
        var (_, dir) = RayAt(px);
        float toHit = PickAt(px) is { } hit ? hit.T : 0f;
        bool shift = (GetKeyState(0x10) & 0x8000) != 0;
        float step = WheelStep(toHit, notches * (shift ? 5f : 1f));
        _eye += Vector3.Normalize(dir) * step;
        SyncCamera();
    }

    /// <summary>How far one wheel turn moves the eye along the mouse's ray: in, a fifth of the distance to what is under
    /// the mouse (never through it, at least 8 units); out, a quarter of it; 256 units a notch when nothing is under it.
    /// Shift's fivefold notches are passed in, so the cap holds for them too.</summary>
    internal static float WheelStep(float toHit, float notches)
    {
        if (toHit <= 0f) return 256f * notches;
        float frac = notches > 0 ? 0.2f : 0.25f;
        float step = MathF.Max(toHit * frac, 8f) * notches;
        return notches > 0 ? MathF.Min(step, toHit * 0.9f) : step;
    }

    static string MessageName(int msg) => msg switch
    {
        0x200 => "mousemove", 0x201 or 0x202 => "lbutton", 0x204 or 0x205 => "rbutton", 0x100 => "keydown", 0x101 => "keyup",
        0x000F => "paint", 0x0005 => "size", 0x0008 => "focus", 0x20A => "wheel", _ => "message",
    };

    Vector2? _look;

    // ---- fly: W A S D along the view, E Q up and down, by the clock while held (Shift: five times as fast) ----------
    const string FlyKeys = "WASDQE";
    internal const float FlySpeed = 1800f;                           // units a second; the old key-repeat steps made about this
    readonly HashSet<int> _held = new();
    double _flyAt;

    /// <summary>Move the eye for the time since the last fly move, for each fly key held down; at most a tenth of a second at once.</summary>
    void Fly()
    {
        if (_held.Count == 0) return;
        double now = Now;
        float dt = (float)Math.Min(0.1, (now - _flyAt) / 1000.0); _flyAt = now;
        bool shift = (GetKeyState(0x10) & 0x8000) != 0;
        _eye += FlyMove(_held, Forward(), (shift ? 5f : 1f) * FlySpeed * dt);
        SyncCamera();
    }

    /// <summary>The eye's move for the keys held: along the view, sideways level with the ground, and up or down.</summary>
    internal static Vector3 FlyMove(IReadOnlySet<int> held, Vector3 forward, float distance)
    {
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
        var m = Vector3.Zero;
        if (held.Contains('W')) m += forward;
        if (held.Contains('S')) m -= forward;
        if (held.Contains('D')) m += right;
        if (held.Contains('A')) m -= right;
        if (held.Contains('E')) m += Vector3.UnitZ;
        if (held.Contains('Q')) m -= Vector3.UnitZ;
        return m.LengthSquared() < 1e-6f ? Vector3.Zero : Vector3.Normalize(m) * distance;
    }

    Script? _script;
    internal Stopwatch Clock => ProcessClock;
    internal Action? AfterFrame;
    internal List<double>? TickStamps;
    /// <summary>The view's frame times: the readout and the frame log, one for the process so a log runs across cell switches.</summary>
    internal static readonly FrameStats Frames = new();
    internal static double Now => ProcessClock.Elapsed.TotalMilliseconds;
    static readonly Stopwatch ProcessClock = Stopwatch.StartNew();
    static bool _showFps = FrameStats.ShowByDefault;
    readonly TextBlock _fpsText = new() { Margin = new Thickness(8, 6, 8, 2), Width = 480, TextWrapping = TextWrapping.Wrap, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                                          ToolTip = "frame rate of the 3D view over the last two seconds: frames a second, the median frame time, the mean of the slowest 1%, the worst, and what paces the frames: the desktop compositor, or the view's own clock while the desktop composes slower than the display" };
    DispatcherTimer? _fpsTimer;

    Button? _fpsButton;
    Button FpsToggle()
    {
        // a plain button that names what it will do: the theme draws a checked toggle's text unreadably
        _fpsButton = new Button { Margin = new Thickness(4), ToolTip = "show or hide the 3D view's frame rate, under this row" };
        _fpsButton.Click += (_, _) => ShowFps(!_showFps);
        _fpsButton.Content = _showFps ? "Hide frame rate" : "Show frame rate";
        return _fpsButton;
    }

    /// <summary>Show or hide the readout; it is refreshed four times a second and never asks for a frame.</summary>
    internal void ShowFps(bool on)
    {
        _showFps = on;
        if (_fpsButton is not null) _fpsButton.Content = on ? "Hide frame rate" : "Show frame rate";
        _fpsText.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) { _fpsTimer?.Stop(); return; }
        _fpsTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal,
            (_, _) => _fpsText.Text = Frames.Readout(Now, _loop, _vsync, PacingWords), _window.Dispatcher);
        _fpsTimer.Start();
    }
    internal string FpsReadout => Frames.Readout(Now, _loop, _vsync, PacingWords);
    string? PacingWords => _pacer is { } p ? FrameStats.Pacing(p.PostedBy, p.ComposeMs) : null;
    readonly GpuTimer _gpu = new();

    internal bool Paused;
    (string Path, TaskCompletionSource<string> Done)? _captureAsk;

    /// <summary>Save the whole window, with this scene's next frame in the view's place, as a PNG.</summary>
    internal Task<string> CaptureWindowAsync(string path)
    {
        var done = new TaskCompletionSource<string>();
        _captureAsk = (path, done);
        Invalidate("capture");
        return done.Task;
    }

    void Frame()
    {
        if (Paused || !_view.MakeCurrent()) return;
        _dirty = false;
        var t0 = Clock.Elapsed.TotalMilliseconds;
        string dirtyWhy = _dirtyWhy ?? "none"; double dirtyAge = _dirtyWhy is null ? 0 : t0 - _dirtyAt; int messages = _messages;
        bool resumed = _resumed || !_drewOnce;
        _dirtyWhy = null; _messages = 0; _resumed = false; _drewOnce = true;
        (_fbW, _fbH) = _view.ClientSize();
        Fly();
        if (_landOnFirstFrame is { } land)
        {
            _landOnFirstFrame = null;
            // a reference this scene does not draw has nothing to land on: say so, and show the cell instead of the placeholder pose
            if (!Land(FormKey.Factory(land)) && !_session.IsDrawn(FormKey.Factory(land)) && SceneDirector.Showing is not null) FrameScene();
        }
        else FollowCamera();   // after the first frame's landing, so the placeholder pose never moves the square
        if (_lod is not null && ViewLayers.LodOn && _lod.Update(_eye)) Invalidate("lod");   // more blocks to stream: draw on
        if (_landOnFirstFrame is null && _queue.Count > 0 && (_opt.PrefetchCell is null || _prefetched.ContainsKey(_queue.Peek())))   // (cell, kind)
        {
            BuildNextCell();   // one loaded cell per frame, once the worker has read it; its AfterCellsChanged asks for the next frame
        }
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.Viewport(0, 0, W, H);
#if SKYRENDER_SKY
        _renderer.SkyTime = (float)Clock.Elapsed.TotalSeconds;   // the clouds drift with each frame drawn
#endif
        _gpu.Begin();
        _renderer.Render(_camera, W, H);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _overlay?.Invoke(_camera.GetViewMatrix(), _camera.GetProjectionMatrix((float)W / H));
        double gpuMs = _gpu.End();
        _frame++;
        double renderMs = Clock.Elapsed.TotalMilliseconds - t0;
        _script?.Tick();                    // a capture step reads the back buffer before the swap
        if (_captureAsk is { } ask)
        {
            _captureAsk = null;
            try
            {
                var viewPng = Capture(Path.GetFileNameWithoutExtension(ask.Path) + "-view", caption: false);
                var (cw, ch) = WindowCapture.SaveComposite(Shell.Window!, Shell.ViewSlot, viewPng, ask.Path);
                ask.Done.SetResult($"window captured with its scene ({cw}x{ch}) to {ask.Path}");
            }
            catch (Exception ex) { ask.Done.SetException(new InvalidOperationException("the window could not be captured: " + ex.Message)); }
        }
        var t1 = Clock.Elapsed.TotalMilliseconds;
        _view.Swap();
        var t2 = Clock.Elapsed.TotalMilliseconds;
        double prev = Frames.LastPresentMs;
        Frames.Add(new FrameSample(_frame, t2, prev < 0 ? 0 : t2 - prev, renderMs, t2 - t1, gpuMs,
            _loop + (_script?.WantsFrames == true ? "+script" : ""), resumed || prev < 0, dirtyWhy, dirtyAge, _pacer is { } pc ? t0 - pc.PostedAt : double.NaN, messages,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), _yaw, _pitch, _eye.X, _eye.Y, _eye.Z, W, H,
            _pacer?.PostedBy ?? "", _pacer?.ComposeMs ?? double.NaN));
        AfterFrame?.Invoke();
    }

    // ---- the shell's side of a scene: the Records tree, landing on a reference, the state dump ----------------------
    string? _landOnFirstFrame;

    /// <summary>The cell this scene was built for, as the bundle names cells (a FormKey string).</summary>
    internal string? SceneCell => _opt.CellKey?.ToString();

    /// <summary>Whether this scene already shows <paramref name="cell"/> well enough to land in it without a rebuild
    /// (walkthrough problem 9): an exterior scene draws the cells around its own, so a cell is shown when it is the scene's
    /// cell or holds a reference the scene walked, and the object to land on and the clash's other object are drawn (or,
    /// having no model, have a stand-in place). The caller checks the load order and the read-only state are the same.</summary>
    internal bool Shows(string cell, string? landOn, string? partner) => WhyNotShown(cell, landOn, partner) is null;

    /// <summary>Why <see cref="Shows"/> says no, in a few words; null when it says yes.</summary>
    internal string? WhyNotShown(string cell, string? landOn, string? partner)
    {
        bool own = string.Equals(SceneCell, cell, StringComparison.OrdinalIgnoreCase);
        if (!own && _opt.Interior) return "an interior scene shows its own cell only";
        if (!own && (_opt.RefCells is not { } rc || !rc.Values.Any(c => string.Equals(c.ToString(), cell, StringComparison.OrdinalIgnoreCase))))
            return $"cell {cell} is not among the cells this scene drew";
        bool Here(string? k) => k is null || FormKey.TryFactory(k, out var fk) && (_session.IsDrawn(fk) || _opt.UndrawnAt?.ContainsKey(fk) == true
                                                                                   || _opt.PickShapes.Any(s => s.Kind == PickKind.Plane && s.Plane?.Key == fk));
        if (!Here(landOn)) return $"{landOn} is not drawn in this scene";
        if (!Here(partner)) return $"the clash's other object {partner} is not drawn in this scene";
        return null;
    }
    internal string SceneLabel => _opt.CellLabel;

    /// <summary>A three-quarter view of the scene's own cell, for a scene opened with no reference to land on: the camera
    /// looks at the middle of the cell's drawn references from outside their span (the outer tenth on each axis is left
    /// out, so one far object does not set the frame). An exterior cell that draws no reference of its own is framed on
    /// its square of terrain.</summary>
    internal void FrameScene()
    {
        var boxed = DrawnAll().Where(r => r.Min.X <= r.Max.X).ToList();
        var own = boxed.Where(r => CellOf(r.Ref.RefKey) is not { } c || string.Equals(c, SceneCell, StringComparison.OrdinalIgnoreCase)).ToList();
        if (own.Count == 0 && FrameTerrain()) return;
        if (own.Count == 0) own = boxed;
        if (own.Count == 0) return;
        static (float Lo, float Hi) Span(IEnumerable<float> v) { var s = v.OrderBy(x => x).ToList(); return (s[s.Count / 10], s[Math.Min(s.Count - 1, s.Count * 9 / 10)]); }
        var (x0, x1) = Span(own.Select(r => (r.Min.X + r.Max.X) * 0.5f));
        var (y0, y1) = Span(own.Select(r => (r.Min.Y + r.Max.Y) * 0.5f));
        var (z0, z1) = Span(own.Select(r => (r.Min.Z + r.Max.Z) * 0.5f));
        var centre = new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f);
        float radius = MathF.Max(MathF.Max(x1 - x0, y1 - y0) * 0.5f, 400f);
        if (_opt.Interior)
        {
            // inside the rooms, not outside their shell: at the middle of the cell's references, eye height above them, looking down the long side
            bool alongX = x1 - x0 >= y1 - y0;
            _yaw = alongX ? 90f : 0f; _pitch = 12f;
            _eye = new Vector3(centre.X, centre.Y, centre.Z + 120f);
        }
        else { _yaw = 20f; _pitch = 40f; _eye = centre - Forward() * (radius * 2.2f); }
        SyncCamera();
        Shell.LastLanding = FormattableString.Invariant($"cell {SceneCell}: {(_opt.Interior ? "a view from inside" : "an overview")} of {own.Count} drawn reference(s), eye {_eye.X:0},{_eye.Y:0},{_eye.Z:0}");
        Say("framing: " + Shell.LastLanding);
    }

    /// <summary>An overview of the scene's own cell square, at the height of the terrain drawn in it; false for an
    /// interior, or when no terrain is drawn in the square.</summary>
    bool FrameTerrain()
    {
        if (_opt.Interior || _opt.Grid is not { } g) return false;
        const float Cell = 4096f;
        float x0 = g.X * Cell, y0 = g.Y * Cell;
        float lo = float.MaxValue, hi = float.MinValue;
        // terrain vertices are Y-up (TexturedScene.YUp): Skyrim X is X, Skyrim Y is -Z, height is Y
        foreach (var s in _opt.PickShapes)
        {
            if (s.Kind != PickKind.Terrain || s.Max.X < x0 || s.Min.X > x0 + Cell || -s.Min.Z < y0 || -s.Max.Z > y0 + Cell) continue;
            foreach (var v in s.Verts)
                if (v.X >= x0 && v.X <= x0 + Cell && -v.Z >= y0 && -v.Z <= y0 + Cell) { lo = MathF.Min(lo, v.Y); hi = MathF.Max(hi, v.Y); }
        }
        if (lo > hi) return false;
        var centre = new Vector3(x0 + Cell * 0.5f, y0 + Cell * 0.5f, (lo + hi) * 0.5f);
        _yaw = 20f; _pitch = 40f; _eye = centre - Forward() * (Cell * 0.5f * 2.2f);
        SyncCamera();
        Shell.LastLanding = FormattableString.Invariant($"cell {SceneCell}: an overview of its terrain (no reference of its own is drawn), heights {lo:0} to {hi:0}, eye {_eye.X:0},{_eye.Y:0},{_eye.Z:0}");
        Say("framing: " + Shell.LastLanding);
        return true;
    }

    internal IReadOnlyList<TreeNode> BuildTree()
    {
        var cells = _opt.RefCells ?? new Dictionary<FormKey, FormKey>();
        var drawn = RecordsTree.Entries(DrawnAll().Select(r => (r.Ref.RefKey, r.Ref.BaseEditorId)), _opt.RefOutcomes, cells);
        var conflictRefs = Shell.Ranked.SelectMany(r => new[] { r.Row.RefKey, r.Row.OtherKey }).Where(k => k is not null)
                                 .Select(k => k!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ReadOnly) conflictRefs.UnionWith(ResultsView.ConflictRefs());   // a scene opened from Earlier results: its pair's predicted clashes
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
        if (!_session.IsDrawn(key) && LandOnStandIn(key)) return true;
        if (!_session.IsDrawn(key))
        {
            // why, in the walk's own word when it read the reference: no model (a light, a marker), disabled, deleted; else it is not in this cell's scene
            string why = _opt.RefOutcomes is { } ro && ro.TryGetValue(key, out var w) ? (w.Outcome == "drawn" ? "its model could not be read" : NotDrawnWords(w.Outcome))
                       : "the scene's walk did not meet it in this cell or the cells around it";
            if (LandOnRecordPlace(key, why)) return true;
            Select(null);                                   // the last object landed on is not this one: nothing stays selected in its place
            Shell.SetBanner($"{key} is not drawn in this scene ({why}); nothing to land on.");
            Shell.LastLanding = $"{key}: not drawn ({why})";
            return false;
        }
        Select(key);
        var (kept, clear, crossing, coveredBy) = FrameOn(key);
        Shell.LastLanding = kept;
        Shell.SetBanner(clear ? $"landed on {key}" : crossing ? $"landed on {key}; it is inside or behind {coveredBy} from every side tried" : $"landed near {key}; the view's centre misses its shapes");
        Say("landing: " + Shell.LastLanding);
        return clear || crossing;
    }

    /// <summary>An object with no model (an occlusion plane's marker, a light, a sound or editor marker) has nothing of its
    /// own to land on (walkthrough problem 8): the view lands on its stand-in instead, outlined in cyan: an occlusion
    /// plane as the plane its record sizes, anything else as the diamond or marker the Markers layer draws at its place.
    /// Only this object's stand-in is drawn; the layers stay as they are. False when the scene has no place for it either.</summary>
    bool LandOnStandIn(FormKey key)
    {
        string outcome = _opt.RefOutcomes is { } ro && ro.TryGetValue(key, out var w) ? w.Outcome : "";
        Vector3 min, max; string what;
        var lines = new List<(Vector3 A, Vector3 B)>(); var tris = new List<float>();
        float? faceYaw = null; bool shaded = false;
        if (_opt.PickShapes.FirstOrDefault(sh => sh.Kind == PickKind.Plane && sh.Plane?.Key == key) is { } plane)
        {
            // the plane's own triangles (Y-up), back to Skyrim space for the camera
            var corners = plane.Verts.Select(v => new Vector3(v.X, -v.Z, v.Y)).ToList();
            min = corners.Aggregate(Vector3.Min); max = corners.Aggregate(Vector3.Max);
            var b = plane.Plane!.Bounds;
            what = FormattableString.Invariant($"its occlusion plane, {b.X:0} by {b.Z:0} units as the record sizes it");
            for (int t = 0; t + 2 < plane.Tris.Length; t += 3)
            {
                Vector3 a = plane.Verts[plane.Tris[t]], b2 = plane.Verts[plane.Tris[t + 1]], c2 = plane.Verts[plane.Tris[t + 2]];
                ConflictMarks.AddTriangle(tris, a, b2, c2);
                ConflictMarks.AddTriangle(tris, a, c2, b2);      // a plane has one face; both sides are tinted
            }
            // its outline: the edges only one triangle has, so the panel reads as one, without the diagonal between its triangles
            var edges = new Dictionary<(int, int), int>();
            for (int t = 0; t + 2 < plane.Tris.Length; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int i = plane.Tris[t + e], j = plane.Tris[t + (e + 1) % 3];
                    var k = (Math.Min(i, j), Math.Max(i, j));
                    edges[k] = edges.GetValueOrDefault(k) + 1;
                }
            foreach (var ((i, j), n) in edges) if (n == 1) lines.Add((plane.Verts[i], plane.Verts[j]));
            // look at the plane square on: the heading along its face's normal, in Skyrim space
            if (corners.Count >= 3)
            {
                var n = Vector3.Cross(corners[1] - corners[0], corners[2] - corners[0]);
                if (n.X * n.X + n.Y * n.Y > 1e-6f) faceYaw = (MathF.Atan2(n.X, n.Y) * 180f / MathF.PI + 360f) % 360f;
            }
        }
        else if (_opt.UndrawnAt is { } at && at.TryGetValue(key, out var place))
        {
            var p = new Vector3(place.Position.X, place.Position.Y, place.Position.Z);
            bool marker = outcome == "marker";
            min = p - new Vector3(48f); max = p + new Vector3(48f, 48f, marker ? 96f : 48f);
            what = marker ? "a solid diamond over the marker the Markers layer draws at its place (a stem with an arrow the way it faces)" : "a solid diamond at its place";
            // a marker's stem and arrow, under a solid diamond so the small mark is found among the others
            if (marker) foreach (var (a, b2) in MarkerGizmos.MarkerLines(p, place.Rotation)) lines.Add((TexturedScene.YUp(a), TexturedScene.YUp(b2)));
            MarkerGizmos.DiamondTriangles(tris, p + new Vector3(0, 0, marker ? 48f : 0f), marker ? 32f : 40f);
            shaded = true;
        }
        else return false;
        Select(null);
        _marks.StandIn = lines; _marks.StandInTris = tris; _marks.StandInShaded = shaded;
        _marks.StandInFor = $"{key}, which has no model: {what}";
        var c = (min + max) * 0.5f;
        float r = MathF.Max((max - min).Length() * 0.5f, 48f);
        if (faceYaw is { } fy) _yaw = fy;
        _pitch = faceYaw is null ? 30f : 12f; _eye = c - Forward() * MathF.Max(r * (faceYaw is null ? 3f : 1.4f), 300f);
        // above the ground and the stand-in's top, then aimed back at its middle: a plane often reaches under the land or water
        float floor = MathF.Max(max.Z, _opt.TerrainHeightAt?.Invoke(_eye.X, _eye.Y) ?? float.MinValue) + 150f;
        if (_eye.Z < floor)
        {
            _eye = new Vector3(_eye.X, _eye.Y, floor);
            var flat = MathF.Sqrt((c.X - _eye.X) * (c.X - _eye.X) + (c.Y - _eye.Y) * (c.Y - _eye.Y));
            _pitch = MathF.Atan2(_eye.Z - c.Z, MathF.Max(flat, 1f)) * 180f / MathF.PI;
        }
        SyncCamera();
        string why = outcome.Length > 0 ? NotDrawnWords(outcome) : "it is not drawn";
        Shell.SetBanner($"{key} has no model of its own ({why}); the view shows a stand-in in cyan: {what}.");
        Shell.LastLanding = $"{key}: a stand-in, {what}";
        UpdateKey(); Invalidate("stand-in");
        return true;
    }

    /// <summary>A reference the scene does not draw (disabled, deleted, its model unreadable) still has the position its
    /// record gives: the view looks at that place and marks it with a solid cyan diamond, so Zoom to always takes the camera
    /// there. False when the load order's record index is not read yet or the record has no position.</summary>
    bool LandOnRecordPlace(FormKey key, string why)
    {
        if (SceneDirector.Order is not { } order) return false;
        var index = LoadOrderIndexes.For(order);
        if (!index.IsCompletedSuccessfully)
        {
            Shell.SetBanner($"{key} is not drawn in this scene ({why}), and the load order is still being read, so where its record places it is not known yet; try Zoom to again in a moment.");
            Shell.LastLanding = $"{key}: not drawn ({why}); its place not known yet";
            return true;
        }
        Located? at;
        // one record body: short enough for the window's thread (a click waits on it as on a cell's tree)
        try { at = index.Result.Locate(key.ToString()); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Shell.SetBanner($"{key} is not drawn in this scene ({why}), and its record could not be read: {ex.Message}");
            return true;
        }
        if (at?.Position is not { Length: >= 3 } pos) return false;
        // a place is only this scene's when the record is in this scene's worldspace (an interior: in this very cell)
        bool here = _opt.Interior ? string.Equals(at.Cell, SceneCell, StringComparison.OrdinalIgnoreCase)
                                  : string.Equals(at.Worldspace, HousecarlCore.FormIdToken.Of(_opt.Worldspace), StringComparison.OrdinalIgnoreCase);
        if (!here)
        {
            Select(null);
            Shell.SetBanner($"{key} is not in this scene ({why}): its record places it in {(at.Cell is { } c ? "cell " + c : "worldspace " + at.Worldspace)}, so open that cell to see it.");
            Shell.LastLanding = $"{key}: in another cell ({at.Cell ?? at.Worldspace})";
            return true;
        }
        var p = new Vector3(pos[0], pos[1], pos[2]);
        Select(null);
        var tris = new List<float>();
        MarkerGizmos.DiamondTriangles(tris, p + new Vector3(0, 0, 40f), 32f);
        _marks.StandIn = new(); _marks.StandInTris = tris; _marks.StandInShaded = true;
        _marks.StandInFor = $"{key}, which is not drawn ({why}): a solid diamond where its record places it";
        _pitch = 30f; _eye = p - Forward() * 400f;
        float floor = MathF.Max(p.Z, _opt.TerrainHeightAt?.Invoke(_eye.X, _eye.Y) ?? float.MinValue) + 100f;
        if (_eye.Z < floor) _eye = new Vector3(_eye.X, _eye.Y, floor);
        var flat = MathF.Sqrt((p.X - _eye.X) * (p.X - _eye.X) + (p.Y - _eye.Y) * (p.Y - _eye.Y));
        _pitch = MathF.Atan2(_eye.Z - p.Z, MathF.Max(flat, 1f)) * 180f / MathF.PI;
        SyncCamera();
        Shell.SetBanner($"{key} is not drawn in this scene ({why}); the view looks at where its record places it, marked with a cyan diamond.");
        Shell.LastLanding = $"{key}: not drawn ({why}); looked at its record's place";
        UpdateKey(); Invalidate("stand-in");
        return true;
    }

    // ---- one object as the load order without some plugins has it (the load-order panel with an object selected) ------
    FormKey? _versionKey;
    /// <summary>The object the view shows as another version than the scene's, or null.</summary>
    internal FormKey? VersionShown => _versionKey;

    /// <summary>Draw <paramref name="key"/> as <paramref name="v"/> has it, the rest of the cell untouched: moved, turned and
    /// scaled to that version's placement, or hidden when that load order does not have it. View only: nothing is staged.
    /// A different base object cannot be swapped in without building the cell again, which is said.</summary>
    /// <summary>Refuse, in one sentence, an object that cannot be drawn as another plugin's version.</summary>
    internal void CheckVersionable(FormKey key)
    {
        if (!_session.IsDrawn(key)) throw new InvalidOperationException($"{key} is not drawn in this scene, so it cannot be compared plugin by plugin; select a drawn object.");
        if (_session.Edited().Contains(key)) throw new InvalidOperationException($"{key} has an edit of yours staged or written; undo it first to compare plugins on it.");
        if (_session.RefOf(key).Ref.BosSwapped) throw new InvalidOperationException($"{key} is drawn with the Base Object Swapper's base and placement, so the view cannot show a plugin's version of it; untick with Ctrl+click to build the cell again instead.");
    }

    internal string ShowVersion(FormKey key, VersionWithout v, string without)
    {
        CheckVersionable(key);
        if (_versionKey is { } old && old != key) ClearVersion();
        var ro = _session.RefOf(key);
        _versionKey = key;                                    // set first, so ClearVersion can always undo what follows
        string name = ro.Ref.BaseEditorId ?? key.ToString();
        string said;
        bool gone = v.Plugin is null || v.Placed is { Deleted: true } or { InitiallyDisabled: true };
        if (gone)
        {
            _session.Preview(key, _session.Current(key));
            _viewState.SetVersionHidden(ro.Meshes, true);
            said = v.Plugin is null ? $"without {without}, no plugin has {name}: it is not there (hidden in the view)"
                 : $"without {without}, {name} is {(v.Placed!.Deleted ? "deleted" : "initially disabled")} as {v.Plugin} has it (hidden in the view)";
        }
        else
        {
            _viewState.SetVersionHidden(ro.Meshes, false);
            if (v.Placed is not { } p) throw new InvalidOperationException($"{key} in {v.Plugin} is not a placed object, so it has no placement to show.");
            var pose = new RefPlacementEdit.Pose(p.Position, p.Rotation, p.Scale);
            _session.Preview(key, pose);
            var now = RefPlacementEdit.BakedPose(ro.Ref);
            bool same = pose == now;
            said = $"without {without}, {name} is as {v.Plugin} has it" + (same ? ": the same placement as with every plugin" : FormattableString.Invariant(
                $": at {p.Position.X:0.#}, {p.Position.Y:0.#}, {p.Position.Z:0.#}, scale {p.Scale:0.##} (with every plugin: {now.Position.X:0.#}, {now.Position.Y:0.#}, {now.Position.Z:0.#}, scale {now.Scale:0.##})"));
            if (p.Base is { } b && !ro.Ref.BaseKey.IsNull && b != ro.Ref.BaseKey)
                said += $"; its base object there is {b}, not the drawn {ro.Ref.BaseKey}, and the view cannot swap the model without building the cell again (Ctrl+click the plugin to rebuild it)";
        }
        UpdateMarks(); Invalidate("version");
        return said + ". The rest of the cell is unchanged.";
    }

    /// <summary>Draw the object shown as another version as the scene has it again.</summary>
    internal void ClearVersion()
    {
        if (_versionKey is not { } k) return;
        _versionKey = null;
        if (_session.IsDrawn(k))
        {
            _viewState.SetVersionHidden(_session.RefOf(k).Meshes, false);
            _session.Preview(k, _session.Current(k));
        }
        UpdateMarks(); Invalidate("version");
    }

    /// <summary>The walk's one-word outcome for a reference it did not draw, in a few plain words.</summary>
    internal static string NotDrawnWords(string outcome) => outcome switch
    {
        "no-model" => "its base object has no model, as a light or a marker has none",
        "marker" => "it is a marker, which the game does not draw",
        "disabled" => "it is disabled in this load order",
        "deleted" => "it is deleted in this load order",
        "unresolved-mesh" => "its model could not be read",
        _ => outcome,
    };

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

    /// <summary>The live scene's cells for a state readout: the square, its centre, and each held cell named by the walk.</summary>
    internal object? LiveState() => _live.Available ? new
    {
        grids = _live.Grids, maxGrids = _live.MaxGrids, source = _opt.GridsToLoadSource, centre = _live.Centre is { } lg ? new[] { lg.X, lg.Y } : null,
        sceneCell = _opt.Grid is { } sg ? new[] { sg.X, sg.Y } : null, follow = _follow, preload = _preloadOn,
        preloading = _preloading is { } pl ? new[] { pl.X, pl.Y } : null, prepared = _opt.Reach.Count,
        held = _live.Held.Count, far = _live.FarOn ? _live.FarWidth : 0, farHeld = _live.FarHeld, farSource = _opt.LargeGridSource,
        lod = new { on = ViewLayers.LodOn && _lod is not null, why = _lodWhy, source = _lod?.Source, residentBlocks = _lod?.Blocks.Resident, pendingBlocks = _lod?.Blocks.Pending,
                    kinds = ViewLayers.LodKinds.ToDictionary(k => k.Name, k => ViewLayers.IsOn(k.Kind)) },
        cells = _opt.CellGrids.Where(kv => _live.Holds(kv.Value)).Select(kv => new { cell = kv.Key.ToString(), x = kv.Value.X, y = kv.Value.Y }).ToList(),
    } : null;

    internal object SceneState()
    {
        var centre = PickAt(new Vector2(W * 0.5f, H * 0.5f));
        return new
        {
            cell = SceneCell, cellLabel = _opt.CellLabel, worldspace = _opt.Worldspace.ToString(), interior = _opt.Interior,
            worklistCell = _opt.WorklistCell, drawnRefs = DrawnAll().Count(),
            walkedRefs = _opt.RefOutcomes?.Count,
            walkOutcomes = _opt.RefOutcomes?.GroupBy(x => x.Value.Outcome).ToDictionary(g => g.Key, g => g.Count()),
            layers = _layers.State(),
            live = LiveState(),
            marks = new { enabled = _marks.Enabled, selected = _marks.Selected?.Ref.RefKey.ToString(), partner = _marks.Partner?.Ref.RefKey.ToString(), partnerNote = PartnerNote, legend = ConflictMarks.Legend,
                             buried = _marks.Buried is { } bu ? new { words = bu.Words, detail = bu.Tip, triangles = _marks.BuriedTriangles, seconds = _marks.BuriedSeconds } : null },
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
    internal IntPtr ViewHandle => _view.Handle;
    internal string? SceneStateCentre()
    {
        var c = PickAt(new Vector2(W * 0.5f, H * 0.5f));
        return c?.Shape is { Kind: PickKind.Ref } sh ? sh.Ref.RefKey.ToString() : c is null ? null : c.Shape.Kind.ToString();
    }

    // ---- the loaded cells (LiveCells.cs): the square around the scene's cell, added and dropped without a rebuild -------
    readonly LiveCells _live;
    internal LiveCells Live => _live;
    ComboBox? _gridsBox;

    /// <summary>Load a square <paramref name="grids"/> cells wide around the scene's cell: build the cells that come in, free the
    /// cells that go out, and leave every other cell as it is drawn. A cell holding the selected reference or an edit is kept.
    /// One sentence back.</summary>
    internal string SetGrids(int grids)
    {
        if (_live.Refuse(grids) is { } why) throw new InvalidOperationException("Loaded cells: " + why);
        _live.SetGrids(grids);
        return Replan($"Loaded cells {grids}x{grids}");
    }

    /// <summary>Show or hide the far grid: the large references beyond the loaded cells, out to uLargeRefLODGridSize.</summary>
    internal string SetFar(bool on)
    {
        if (!_live.Available) throw new InvalidOperationException("Far grid: this scene cannot load cells around its own.");
        _live.SetFar(on);
        return Replan(on ? $"Far grid {_live.FarWidth}x{_live.FarWidth} shown ({_opt.LargeGridSource})" : "Far grid hidden");
    }

    /// <summary>Bring the held cells to the squares the choices ask for: free what goes out (a cell holding the selection or
    /// an edit is kept as it is), queue what comes in. One sentence back, starting with <paramref name="what"/>.</summary>
    string Replan(string what)
    {
        var (add, drop) = LiveCells.Plan(_live.Centre!.Value, _live.Grids, _live.FarWidth, _live.Kinds, _opt.Reach);
        var kept = drop.Where(c => _live.All.First(l => l.Grid == c).Refs.Any(r => _session.Holds(r.Ref.RefKey))).ToList();
        drop = drop.Except(kept).ToList();
        add = add.Where(a => !kept.Contains(a.Cell)).ToList();
        if (_window is not null) _view.MakeCurrent();
        foreach (var c in drop)
            if (_live.Drop(c, _renderer) is { } lc) _session.Remove(lc.Refs);
        // the cells that come in are built one at a time, nearest first, with the view drawing between them
        _queue.Clear(); foreach (var a in add) _queue.Enqueue(a);
        _queueClock.Restart(); _queueBuilt = 0;
        StartPrefetch();
        AfterCellsChanged();
        var said = $"{what}: " + (add.Count > 0 ? $"building {add.Count} cell(s), nearest first" : "nothing to build")
                 + $", {drop.Count} freed, {_live.Held.Count} held"
                 + (kept.Count > 0 ? $"; {kept.Count} cell(s) outside the square kept because they hold the selection or an edit" : "");
        Say(said);
        ScheduleCell();
        return said;
    }

    // ---- the cells still to build: one at the start of each frame, so the view keeps drawing while they come in ---------
    // (a dispatcher job below Render priority starves while anything asks for frames continuously, such as flying)
    readonly Queue<((int X, int Y) Cell, bool LargeOnly)> _queue = new();
    readonly System.Diagnostics.Stopwatch _queueClock = new();
    int _queueBuilt;
    /// <summary>Loaded cells are still being built.</summary>
    internal bool CellsBuilding => _queue.Count > 0 || _preloading is not null;   // (a script step waits for a preload too)

    CancellationTokenSource? _prefetch;
    Task _prefetchTask = Task.CompletedTask;
    // cells whose meshes and textures the worker has read: the window builds a queued cell only once it is here
    readonly System.Collections.Concurrent.ConcurrentDictionary<((int X, int Y) Cell, bool LargeOnly), bool> _prefetched = new();
    /// <summary>Read the queued cells' meshes and decode their textures on a worker, in the queue's order, so each cell's
    /// build on the window's thread is mostly upload. A new queue cancels the old prefetch.</summary>
    void StartPrefetch()
    {
        _prefetch?.Cancel();
        if (_opt.PrefetchCell is not { } prefetch || _queue.Count == 0) return;
        var cts = _prefetch = new CancellationTokenSource();
        var cells = _queue.ToList();
        var before = _prefetchTask;
        _prefetchTask = Task.Run(() =>
        {
            before.Wait();   // one prefetch at a time: the cancelled one finishes its current mesh first
            foreach (var w in cells)
            {
                if (cts.IsCancellationRequested) return;
                try { prefetch(w.Cell, w.LargeOnly, false, cts.Token); }
                catch (Exception ex) { Console.WriteLine($"   [wb] prefetch of cell {w.Cell.X},{w.Cell.Y} stopped: {ex.Message} (the cell is built on the window's thread instead)"); }
                _prefetched[w] = true;
                _window?.Dispatcher.BeginInvoke(() => ScheduleCell());   // the window builds it on its next frame
            }
        });
    }

    /// <summary>Ask for a frame, which builds the next queued cell first.</summary>
    void ScheduleCell() { if (_queue.Count > 0 && _window is not null && _alive) Invalidate("cells"); }

    /// <summary>Build one queued cell (the frame calls it with the context current), and ask for the next frame if more remain.</summary>
    void BuildNextCell()
    {
        if (_queue.Count == 0) return;
        var (c, largeOnly) = _queue.Dequeue();
        var cellSw = System.Diagnostics.Stopwatch.StartNew();
        if (_live.Add(c, largeOnly) is { } lc) { _session.Add(lc.Refs); _queueBuilt++; }
        Console.WriteLine(FormattableString.Invariant($"   [wb] cell {c.X},{c.Y}{(largeOnly ? " (large references)" : "")} built on the window's thread in {cellSw.Elapsed.TotalMilliseconds:0} ms"));
        bool done = _queue.Count == 0;
        AfterCellsChanged(tree: done);
        if (done)
        {
            var said = FormattableString.Invariant($"loaded cells: {_queueBuilt} cell(s) built in {_queueClock.Elapsed.TotalSeconds:0.0} s; {_live.Held.Count - _live.FarHeld} whole ({_live.Grids}x{_live.Grids}), {_live.FarHeld} with their large references only")
                + (_live.FarOn ? FormattableString.Invariant($" (far grid {_live.FarWidth}x{_live.FarWidth})") : "");
            Say(said); Shell.SetBanner(said);
            UpdateMarks();
        }
        else
        {
            Shell.SetBanner($"building loaded cells: {_queueBuilt} built, {_queue.Count} to go");
        }
    }

    /// <summary>The cell of the prepared square a request names (by FormKey), or null when it is not one this scene can
    /// recentre on: the square around it must lie inside what the walk prepared, at the current width.</summary>
    internal (int X, int Y)? RecentreGrid(string cell)
    {
        if (!_live.Available || !FormKey.TryFactory(cell, out var key) || !_opt.CellGrids.TryGetValue(key, out var g)) return null;
        return LiveCells.MaxSquare(g, _opt.Reach) >= _live.Grids ? g : null;
    }

    // ---- distant LOD beyond the loaded cells (viewprobe's LiveLod.cs): streamed around the camera, hidden under every whole
    // cell, each kind switched at draw time; nothing is rebuilt when the cells or the camera move ----------------------------
    LiveLod? _lod;
    string? _lodWhy;
    System.Windows.Controls.Primitives.ToggleButton? _lodButton;
    readonly Dictionary<ViewLayers.LodKind, MenuItem> _lodItems = new();

    /// <summary>Apply the LOD choices: make the live LOD the first time it is shown, hide it under the whole cells held, switch
    /// its kinds, and set the far plane it needs.</summary>
    void ApplyLod()
    {
        if (ViewLayers.LodOn && _lod is null && _lodWhy is null && _live.Available && _opt.Live is { } session)
        {
            _lod = LiveLod.Create(session, out var why);
            if (_lod is null) { _lodWhy = why; Say("distant LOD: " + why); }
            else Say("distant LOD: " + _lod.Source);
        }
        bool on = ViewLayers.LodOn && _lod is not null;
        _camera.FarPlane = on ? 409600f : 131072f;   // viewprobe's LOD reach (DistantLodLayer.FarPlane)
        if (_lod is not null)
        {
            _lod.SetLoaded(_live.All.Where(c => !c.LargeOnly && c.HasLand).Select(c => c.Grid).ToList());
            _lod.Show(on && ViewLayers.IsOn(ViewLayers.LodKind.Terrain), on && ViewLayers.IsOn(ViewLayers.LodKind.Objects),
                      on && ViewLayers.IsOn(ViewLayers.LodKind.Trees), on && ViewLayers.IsOn(ViewLayers.LodKind.Water));
        }
        RefreshLodControl();
        if (_window is not null) Invalidate("lod");
    }

    /// <summary>Show or hide distant LOD. One sentence back.</summary>
    internal string SetLod(bool on)
    {
        ViewLayers.LodOn = on;
        ApplyLod();
        var said = !on ? "Distant LOD hidden (what it streamed stays, so showing it again is immediate)"
                 : _lod is null ? $"Distant LOD cannot be shown: {_lodWhy ?? "this scene has no cells around it"}"
                 : $"Distant LOD shown beyond the loaded cells ({_lod.Source})";
        Say(said);
        return said;
    }

    /// <summary>Show or hide one kind of LOD. One sentence back.</summary>
    internal string SetLodKind(ViewLayers.LodKind kind, bool on)
    {
        ViewLayers.Choose(kind, on);
        ApplyLod();
        var said = $"{ViewLayers.LodKinds.First(k => k.Kind == kind).Label} {(on ? "shown" : "hidden")}" + (ViewLayers.LodOn ? "" : "; distant LOD is hidden, so it shows when LOD is shown");
        Say(said);
        return said;
    }

    UIElement LodControl()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _lodButton = new System.Windows.Controls.Primitives.ToggleButton { Content = "LOD", Margin = new Thickness(4, 2, 0, 2), Padding = new Thickness(6, 1, 6, 1), Focusable = false };
        ToolTipService.SetShowOnDisabled(_lodButton, true);
        _lodButton.Click += (_, _) => Shell.SetBanner(SetLod(_lodButton.IsChecked == true));
        var menu = new ContextMenu();
        foreach (var k in ViewLayers.LodKinds)
        {
            var item = new MenuItem { Header = k.Label, IsCheckable = true, StaysOpenOnClick = true };
            var kind = k.Kind;
            item.Click += (_, _) => Shell.SetBanner(SetLodKind(kind, item.IsChecked));
            _lodItems[kind] = item; menu.Items.Add(item);
        }
        var arrow = new Button { Content = "\u25BE", Margin = new Thickness(0, 2, 2, 2), Padding = new Thickness(3, 1, 3, 1), Focusable = false, ContextMenu = menu,
            ToolTip = "Which kinds of distant LOD draw: terrain, objects, trees, water." };
        menu.PlacementTarget = arrow; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        arrow.Click += (_, _) => menu.IsOpen = true;
        row.Children.Add(_lodButton); row.Children.Add(arrow);
        RefreshLodControl();
        return row;
    }

    void RefreshLodControl()
    {
        if (_lodButton is null) return;
        _lodButton.IsChecked = ViewLayers.LodOn && _lod is not null;
        _lodButton.IsEnabled = _lodWhy is null && _live.Available;
        _lodButton.ToolTip = _lodWhy is { } w ? $"Distant LOD: {w}."
            : "Distant LOD beyond the loaded cells, as the game draws it: terrain, object, tree and water LOD blocks streamed around the camera, "
              + "hidden under every loaded cell" + (_lod is null ? "." : $"; {_lod.Source}.");
        foreach (var (k, item) in _lodItems) item.IsChecked = ViewLayers.IsOn(k);
    }

    // ---- the loaded square follows the camera (the Follow camera toggle) --------------------------------------------------
    bool _follow = true;
    bool _beyondSaid;
    /// <summary>The eye has left the loaded square's centre cell (by more than 512 units): the square moves to the eye's cell,
    /// building the cells that come in and freeing the ones that go out. Past what the scene prepared, it says so once.</summary>
    void FollowCamera()
    {
        if (!_follow || !_live.Available || _live.Centre is not { } c) return;
        if (LiveCells.Follow(c, _eye.X, _eye.Y, 512f) is not { } to) { _beyondSaid = false; return; }
        if (LiveCells.MaxSquare(to, _opt.Reach) < _live.Grids)
        {
            MaybePreload(to);
            if (!_beyondSaid)
            {
                _beyondSaid = true;
                var why = _preloading is { } p
                    ? $"the camera is in cell {to.X},{to.Y}, past the cells this scene prepared; the cells around {p.X},{p.Y} are being prepared in the background, and the loaded cells follow once they are"
                    : $"the camera is in cell {to.X},{to.Y}, past the cells this scene prepared, so the loaded cells stay around {c.X},{c.Y}; "
                      + (_preloadOn ? "open a cell there to load around it" : "switch Preload cells on, or open a cell there, to load around it");
                Say(why); Shell.SetBanner(why);
            }
            return;
        }
        _live.Centre = to;
        Shell.SetBanner(Replan($"the camera moved to cell {to.X},{to.Y}: loaded cells follow"));
        MaybePreload(to);
    }

    // ---- preloading (viewprobe's LiveArea.cs): when the camera nears the edge of what the scene prepared, another viewprobe
    // process prepares the area around it in the background, and its cells join the scene when it is done ------------------
    bool _preloadOn = true;
    (int X, int Y)? _preloading;
    (int X, int Y)? _preloadFailedAt;
    System.Diagnostics.Process? _preloadProcess;
    /// <summary>Cells of room the prepared square keeps beyond the loaded and far squares before a preload starts.</summary>
    internal const int PreloadMargin = 2;

    /// <summary>Start preparing the area around <paramref name="at"/> when the prepared square leaves less than
    /// <see cref="PreloadMargin"/> cells beyond the loaded square there, and none is being prepared.</summary>
    void MaybePreload((int X, int Y) at)
    {
        if (!_preloadOn || _preloading is not null || _opt.MergeArea is null || SceneDirector.Order is not { } order || _window is null) return;
        if (_preloadFailedAt == at) return;   // not again for the same cell: the camera moving on, or the toggle, tries again
        // room for the loaded square and the margin, as much as one prepared area can give (the far grid takes what is left)
        if (LiveCells.MaxSquare(at, _opt.Reach) >= Math.Min(_live.Grids + 2 * PreloadMargin, 2 * SceneDirector.LiveReach + 1)) return;
        _preloading = at;
        var dir = Path.Combine(_outDir, "preload");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, FormattableString.Invariant($"area_{at.X}_{at.Y}.bin"));
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "viewprobe.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
        foreach (var a in new[] { FormattableString.Invariant($"grid:{at.X},{at.Y}@{_opt.Worldspace}"), "--ring", SceneDirector.LiveReach.ToString(CultureInfo.InvariantCulture) }
                     .Concat(order.Args()).Concat(new[] { "--prepare-out", file, "--no-nav", "--no-weather", "--size", "64", "--out", dir,
                     "--eye", FormattableString.Invariant($"{at.X * 4096 + 2048},{at.Y * 4096 + 2048},20000"), "--look", FormattableString.Invariant($"{at.X * 4096 + 2048},{at.Y * 4096 + 2058},0") }))
            psi.ArgumentList.Add(a);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var ui = _window.Dispatcher;
        Say($"preloading the cells around {at.X},{at.Y} in the background (another viewprobe process)");
        Task.Run(() =>
        {
            string? failed = null;
            try
            {
                using var p = System.Diagnostics.Process.Start(psi)!;
                _preloadProcess = p;
                var err = p.StandardError.ReadToEndAsync();
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                // the run's own words, kept beside the area: what it prepared, or why it did not
                var log = Path.ChangeExtension(file, ".log");
                File.WriteAllText(log, output + err.Result);
                string Last(string text) => text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
                if (p.ExitCode != 0 || !File.Exists(file))
                    failed = $"viewprobe stopped with exit code {p.ExitCode} without writing the area"
                           + (Last(err.Result.Length > 0 ? err.Result : output) is { Length: > 0 } e ? $": {e}" : "") + $"; its output is in {log}";
            }
            catch (Exception ex) { failed = ex.Message; }
            _preloadProcess = null;
            ui.BeginInvoke(() => PreloadDone(at, file, failed, clock.Elapsed.TotalSeconds));
        });
    }

    void PreloadDone((int X, int Y) at, string file, string? failed, double seconds)
    {
        _preloading = null;
        if (!_alive) return;
        _preloadFailedAt = failed is null ? null : at;
        if (failed is not null)
        {
            var why = $"the cells around {at.X},{at.Y} could not be preloaded ({failed}); open a cell there to load around it";
            Say(why); Shell.SetBanner(why);
            return;
        }
        try
        {
            int joined = _opt.MergeArea!(file);
            // keep the prepared cells near the camera and every cell drawn; the rest of the old area's memory goes
            var centre = _live.Centre ?? at;
            int keepOut = SceneDirector.LiveReach + PreloadMargin;
            static int Ring((int X, int Y) a, (int X, int Y) b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
            // near the loaded square's centre or the new area's: the camera is between the two
            int freed = _opt.ForgetCells?.Invoke(c => _live.Holds(c) || Ring(c, centre) <= keepOut || Ring(c, at) <= keepOut) ?? 0;
            var said = FormattableString.Invariant($"preloaded {joined} cell(s) around {at.X},{at.Y} in {seconds:0.0} s in the background; {freed} far cell(s) released");
            Say(said);
            // the squares can be wider now (the far grid is cut to what is prepared): plan them again, building only what comes in
            Shell.SetBanner(Replan(said));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            var why = $"the preloaded cells around {at.X},{at.Y} could not be read ({ex.Message}); open a cell there to load around it";
            Say(why); Shell.SetBanner(why);
        }
        finally { try { File.Delete(file); } catch (IOException) { } }
        _beyondSaid = false;
        FillGrids();
        Invalidate("preload");   // the next frame's follow check moves the square if the camera is waiting past the old edge
    }

    /// <summary>Preload cells around the camera in the background, or not (each preloaded area holds a few hundred MB).</summary>
    internal string SetPreload(bool on)
    {
        _preloadOn = on; _preloadFailedAt = null;
        var said = on ? "Preload cells on: the cells around the camera are prepared in the background as it nears the edge of the prepared area"
                      : "Preload cells off: the scene keeps the cells it prepared when it opened";
        Say(said);
        if (on && _live.Centre is { } c) MaybePreload(c);
        return said;
    }

    /// <summary>Let the loaded square follow the camera, or hold it where it is. One sentence back.</summary>
    internal string SetFollow(bool on)
    {
        _follow = on; _beyondSaid = false;
        var said = on ? "Follow camera on: the loaded cells move with the camera, as the game's do with the player"
                      : $"Follow camera off: the loaded cells stay around cell {_live.Centre?.X},{_live.Centre?.Y}";
        Say(said);
        if (on) Invalidate("follow");
        return said;
    }

    /// <summary>Make <paramref name="cell"/> the scene's cell without a rebuild: the loaded square moves to it, building the
    /// cells that come in and freeing the cells that go out. The navmesh and the occlusion planes stay those of the cell
    /// the scene was built for (they are built once, for it). One sentence back.</summary>
    internal string Recentre(string cell, string? label, (int X, int Y) grid)
    {
        var was = _opt.CellLabel;
        _opt.Grid = grid; _live.Centre = grid;
        _opt.CellKey = FormKey.Factory(cell);
        _opt.CellLabel = label ?? cell;
        var said = SetGrids(_live.Grids);
        if (_window is not null) _window.Title = $"SkyWright — {_opt.CellLabel}";
        return $"cell {_opt.CellLabel} is now the scene's cell (it was {was}): " + said;
    }

    /// <summary>What reads the drawn cells is read again: the layers (and their hidden state on the new meshes), the cell
    /// borders, the markers, the tree and the key.</summary>
    void AfterCellsChanged(bool tree = true)
    {
        if (_borders is not null) _renderer.Overlays.Remove(_borders);
        if (_markerGizmos is not null) _renderer.Overlays.Remove(_markerGizmos);
        _borders = null;
        BuildLayers(_renderer);
        _layerBar?.Refresh();
        FillGrids();
        ApplyLod();
        if (_window is not null) { if (tree) Shell.FillTree(BuildTree()); UpdateKey(); UpdateStatus(); Invalidate("cells"); }
    }

    /// <summary>The Loaded cells choice beside the layer toggles: the square's width, up to what the scene prepared.</summary>
    UIElement GridsControl()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 2, 0) };
        if (!_live.Available) return row;
        row.Children.Add(new TextBlock { Text = "Loaded cells:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), Foreground = Theme.Brush(Theme.Dim) });
        _gridsBox = new ComboBox { Margin = new Thickness(0, 2, 2, 2), Padding = new Thickness(6, 1, 6, 1), Focusable = false,
            ToolTip = $"How many cells around this one are loaded, as the game's uGridsToLoad: a square this many cells wide. It starts at {_opt.GridsToLoadSource}. "
                    + "A change builds only the cells that come in and frees the cells that go out." };
        FillGrids();
        _gridsBox.SelectionChanged += (_, _) =>
        {
            if (_fillingGrids || _gridsBox.SelectedItem is not ComboBoxItem { Tag: int g } || g == _live.Grids) return;
            try { Shell.SetBanner(SetGrids(g)); }
            catch (InvalidOperationException ex) { Shell.SetBanner(ex.Message); FillGrids(); }
        };
        row.Children.Add(_gridsBox);
        // the far grid: the large references beyond the loaded cells, as the game draws them out to uLargeRefLODGridSize
        var far = new System.Windows.Controls.Primitives.ToggleButton { Content = $"Far grid {Math.Min(_live.Far, _live.MaxGrids)}×{Math.Min(_live.Far, _live.MaxGrids)}", IsChecked = _live.FarOn, Margin = new Thickness(4, 2, 2, 2),
            Padding = new Thickness(6, 1, 6, 1), Focusable = false,
            ToolTip = $"The large references beyond the loaded cells (buildings, cliffs and other objects the worldspace lists as large), drawn out to a "
                    + $"square {_live.Far} cells wide as the game does: {_opt.LargeGridSource}. Off draws the loaded cells only." };
        far.Click += (_, _) =>
        {
            try { Shell.SetBanner(SetFar(far.IsChecked == true)); }
            catch (InvalidOperationException ex) { Shell.SetBanner(ex.Message); far.IsChecked = _live.FarOn; }
        };
        row.Children.Add(far);
        var follow = new System.Windows.Controls.Primitives.ToggleButton { Content = "Follow camera", IsChecked = _follow, Margin = new Thickness(4, 2, 2, 2),
            Padding = new Thickness(6, 1, 6, 1), Focusable = false,
            ToolTip = "The loaded cells and the far grid move with the camera, as the game's move with the player: crossing into a neighbouring "
                    + "cell builds the cells that come in and frees the ones that go out. Off holds them where they are." };
        follow.Click += (_, _) => Shell.SetBanner(SetFollow(follow.IsChecked == true));
        row.Children.Add(follow);
        var preload = new System.Windows.Controls.Primitives.ToggleButton { Content = "Preload cells", IsChecked = _preloadOn, Margin = new Thickness(4, 2, 2, 2),
            Padding = new Thickness(6, 1, 6, 1), Focusable = false,
            ToolTip = "When the camera nears the edge of the area this scene prepared, prepare the area around it in the background (another viewprobe "
                    + "process, about 10 to 15 s of CPU), so the loaded cells keep following. Each prepared area holds a few hundred MB; switch it off to keep memory down." };
        preload.Click += (_, _) => Shell.SetBanner(SetPreload(preload.IsChecked == true));
        row.Children.Add(preload);
        row.Children.Add(LodControl());
        return row;
    }

    bool _fillingGrids;
    /// <summary>The choice's widths, up to what the prepared cells allow around the current centre, with the current one chosen.</summary>
    void FillGrids()
    {
        if (_gridsBox is null) return;
        _fillingGrids = true;
        _gridsBox.Items.Clear();
        for (int g = 1; g <= _live.MaxGrids; g += 2)
        {
            var item = new ComboBoxItem { Content = $"{g}\u00d7{g}", Tag = g };
            _gridsBox.Items.Add(item);
            if (g == _live.Grids) _gridsBox.SelectedItem = item;
        }
        _fillingGrids = false;
    }

    // ---- the view's layers (ViewLayers.cs): what this scene can draw, set to the window's choice ---------------------
    SceneLayers _layers = null!;
    LayerBar? _layerBar;
    CellBorders? _borders;
    MarkerGizmos? _markerGizmos;
    internal SceneLayers Layers => _layers;
    internal CellBorders? Borders => _borders;
    internal MarkerGizmos? MarkerPlaces => _markerGizmos;

    /// <summary>Read what this scene was built with, add the layers' own overlays, and set each layer to the window's choice.</summary>
    void BuildLayers(GlRenderer renderer)
    {
        string navEntry = _opt.NavMode;                  // the mode the scene was built to show its navmesh in
        if (!_opt.Interior && _opt.TerrainHeightAt is { } height && _opt.LandCells.Count > 0)
        {
            _borders = new CellBorders(_opt.LandCells, _opt.Grid, height);
            renderer.Overlays.Insert(1, _borders);
        }
        // a plane is a marker too, and the planes layer draws it as what it is
        var planes = _opt.PickShapes.Where(s => s.Kind == PickKind.Plane && s.Plane is not null).Select(s => s.Plane!.Key).ToHashSet();
        var places = (_opt.UndrawnAt ?? new Dictionary<FormKey, (Vec3, Vec3)>()).Where(kv => !planes.Contains(kv.Key))
            .Where(kv => !_live.Available || _live.HoldsWhole(LiveSlicer.CellOf(kv.Value.Item1.X, kv.Value.Item1.Y)))   // markers of the loaded cells
            .Select(kv => (kv.Key, kv.Value.Position, kv.Value.Rotation, _opt.RefOutcomes is { } ro && ro.TryGetValue(kv.Key, out var o) && o.Outcome == "marker")).ToList();
        _markerGizmos = new MarkerGizmos(places);
        renderer.Overlays.Add(_markerGizmos);
        var facts = new LayerFacts(_opt.Interior, NavBuilt: navEntry != "off", NavLeftOut: SceneDirector.Order is { Navmesh: false },
            CanAskAgain: SceneDirector.Showing is not null && SceneDirector.Order is not null, _opt.PlaneCount, _borders is null ? 0 : _opt.LandCells.Count,
            places.Count, _opt.LayerMeshes, DrawnAll().ToList(), _opt.BaseTypes);
        _layers = SceneLayers.From(facts, new LayerSwitches(
            Navmesh: on => InteractiveViewer.ApplyNavMode(_opt, on ? navEntry : "off"),
            Planes: on => _opt.ShowPlanes = on,
            Borders: on => { _bordersOn = on; ApplyBorders(); },
            Markers: on => _markerGizmos.Enabled = on,
            Marks: on => { _marks.Enabled = on; UpdateKey(); },
            Boxes: on => { _resultMarks.Enabled = on; UpdateKey(); },
            Outlines: on => _marks.Outlines = on,
            Hide: (meshes, hidden) => _viewState.SetLayerHidden(meshes, hidden)));
        _layers.ApplyAll();
    }

    bool _bordersOn;

    /// <summary>The cell borders as the layer and its two options say: the outline, drawn through what is in front or
    /// hidden behind it, and the fence standing on it.</summary>
    void ApplyBorders()
    {
        // the outline is the borders' own (it follows the scene's cell when the view recentres); viewprobe's draws only
        // when the scene has no borders overlay
        _opt.ShowCellOutline = _bordersOn && _borders is null;
        _opt.CellOutlineThrough = ViewLayers.IsOn(BorderOption.Through);
        if (_borders is not null)
        {
            _borders.Enabled = _bordersOn && ViewLayers.IsOn(BorderOption.Fence);
            _borders.Outline = _bordersOn; _borders.Through = ViewLayers.IsOn(BorderOption.Through);
        }
    }

    /// <summary>Switch one of the cell borders' options for the window (the arrow's menu, the script's borders step, the
    /// state tool's layer verb); one sentence back. The choice is kept in a scene without cell borders too.</summary>
    internal string SetBorderOption(BorderOption o, bool on)
    {
        ViewLayers.Choose(o, on);
        ApplyBorders();
        var said = $"Cell borders: {ViewLayers.Of(o).Label} {(on ? "on" : "off")}"
                 + (_layers.Has(Layer.Borders) ? (_bordersOn ? "" : "; the cell borders are hidden, so it shows when they are shown") : $"; {_layers.Why(Layer.Borders)}");
        Say(said); UpdateStatus(); _layerBar?.Refresh(); Invalidate("layer");
        return said;
    }

    /// <summary>Switch a layer for the window (the toolbar, the script's layer step, the state tool's layer verb). One
    /// sentence back; a layer this scene cannot draw is refused with the reason, and pressing Navmesh in a cell opened
    /// without it asks for the cell again with it (the camera stays where it is).</summary>
    internal string SetLayer(Layer l, bool on)
    {
        if (l == Layer.Navmesh && on && _layers.NavLoadable) return ReloadWithNavmesh();
        var said = _layers.Set(l, on);
        Say(said); UpdateStatus(); _layerBar?.Refresh(); Invalidate("layer");
        return said;
    }

    string ReloadWithNavmesh()
    {
        var show = SceneDirector.Showing ?? throw new InvalidOperationException("this scene was not opened from a tab that can ask for it again, so its navmesh cannot be loaded here.");
        ViewLayers.WantsNavmesh = true;
        ViewLayers.Choose(Layer.Navmesh, true);
        ViewLayers.KeepPose(show.Cell, _eye, _yaw, _pitch);
        try
        {
            var said = SceneDirector.Request(show with { Reload = true, LandOn = null, Why = "its navmesh was asked for" });
            _layerBar?.Refresh(loading: said);
            return said;
        }
        catch (InvalidOperationException) { ViewLayers.WantsNavmesh = false; ViewLayers.TakePose(null); throw; }
    }

    // ---- what the script and the bar call ---------------------------------------------------------------------------
    internal EditSession Session => _session;
    internal string? CellOf(FormKey key) => _opt.RefCells is { } rc && rc.TryGetValue(key, out var c) ? c.ToString() : null;

    // The MCP state tools reach the scene through these: the same members the bar and the script call.
    EditSession IWorkbenchScene.Session => _session;
    ViewState? IWorkbenchScene.View => _viewState;
    IReadOnlyList<PickShape> IWorkbenchScene.Shapes => _opt.PickShapes;
    // item 5: the server's answers, the scene's navmesh triangles and the bundle's own declared-dependencies rows
    ServerContextSource? _context;
    IContextSource? IWorkbenchScene.ContextSource => _context ??= new ServerContextSource(WriterAsync(),
        k => _session.IsDrawn(k) ? RefPlacementEdit.BakedPose(_session.RefOf(k).Ref) : null);
    IEnumerable<(FormKey Key, int Tri, Vector3 A, Vector3 B, Vector3 C)> IWorkbenchScene.NavTris => _opt.NavTris;
    // ---- item 8: the trial lane, writing each candidate as its own patch under the Work Folder and re-checking it
    TrialLane? _trials;
    string? _trialsWork;
    TrialLane? IWorkbenchScene.Trials
    {
        get
        {
            var work = new LiveSurface().WorkFolder;
            if (_trials is not null && _trialsWork == work) return _trials;
            if (Shell.Bundle is null || _session.Selected is not { } key) return _trials;
            var writer = WriterAsync();                                   // started here, on the UI thread; the lane awaits it
            _trialsWork = work;
            return _trials = new TrialLane(work,
                async (edit, patch, outPath) => await (await writer).CommitAsync(new[] { edit }, patch, dryRun: false, outPath: outPath),
                (patch, folder, dir, contact, ct) => RecheckTrialAsync(writer, patch, folder, dir, contact, ct),
                pose => Settle(_trials?.Key ?? _session.Selected ?? key, pose), pose => ContactsAt(_trials?.Key ?? _session.Selected ?? key, pose));
        }
    }

    (Aabb Subject, Aabb Other)? IWorkbenchScene.PairBoxes(FormKey key)
    {
        var row = Shell.CurrentRow?.Row;
        var otherKey = row is null ? null : string.Equals(row.RefKey, key.ToString(), StringComparison.OrdinalIgnoreCase) ? row.OtherKey : row.RefKey;
        if (otherKey is null || !FormKey.TryFactory(otherKey, out var ok)) return null;
        var g = CaptureViews.FromPickShapes(_opt.PickShapes.Where(s => s.Kind == PickKind.Ref && (s.Ref.RefKey == key || s.Ref.RefKey == ok)));
        return g.Refs.TryGetValue(key.ToString(), out var a) && g.Refs.TryGetValue(ok.ToString(), out var b) && a.I.Count > 0 && b.I.Count > 0 ? (a.Box, b.Box) : null;
    }

    /// <summary>A drawn shape as the drop and the contact read take it: its key, name, world vertices (viewer Y-up) and bounds.</summary>
    internal sealed record GeoShape(bool Terrain, string Key, string Name, Vector3[] Verts, int[] Tris, Vector3 Min, Vector3 Max);

    /// <summary>The geometry near <paramref name="pose"/> with the reference placed at it: its baked vertices carried there by
    /// <paramref name="m"/>, and every other shape within <paramref name="margin"/> of the pose.</summary>
    static CaptureViews.Geometry Compose(string key, IEnumerable<GeoShape> others, IEnumerable<(string Name, Vector3[] Baked, int[] Tris)> subject,
                                         Matrix4x4 m, Vector3 at, float margin)
    {
        var lo = at - new Vector3(margin); var hi = at + new Vector3(margin);
        var g = new CaptureViews.Geometry();
        foreach (var (name, baked, tris) in subject) g.Add(key, name, baked.Select(v => Vector3.Transform(v, m)).ToArray(), tris);
        foreach (var s in others)
            if (s.Max.X >= lo.X && s.Min.X <= hi.X && s.Max.Y >= lo.Y && s.Min.Y <= hi.Y && s.Max.Z >= lo.Z && s.Min.Z <= hi.Z)
                g.Add(s.Terrain ? CaptureViews.TerrainKey : s.Key, s.Terrain ? CaptureViews.TerrainKey : s.Name, s.Verts, s.Tris);
        return g;
    }

    IEnumerable<GeoShape> OthersOf(FormKey key, bool copy) => _opt.PickShapes
        .Where(s => (s.Kind == PickKind.Ref && s.Ref.RefKey != key) || s.Kind == PickKind.Terrain)
        .Select(s => new GeoShape(s.Kind == PickKind.Terrain, s.Kind == PickKind.Terrain ? CaptureViews.TerrainKey : s.Ref.RefKey.ToString(),
                                  s.Kind == PickKind.Terrain ? CaptureViews.TerrainKey : s.Ref.BaseEditorId ?? s.Ref.RefKey.ToString(),
                                  copy ? s.Verts.ToArray() : s.Verts, copy ? s.Tris.ToArray() : s.Tris, s.Min, s.Max));

    IEnumerable<(string Name, Vector3[] Baked, int[] Tris)> SubjectOf(FormKey key, bool copy) => _opt.PickShapes
        .Where(s => s.Kind == PickKind.Ref && s.Ref.RefKey == key)
        .Select(s => (s.Ref.BaseEditorId ?? key.ToString(), copy ? RefPlacementEdit.BakedVerts(s).ToArray() : RefPlacementEdit.BakedVerts(s), copy ? s.Tris.ToArray() : s.Tris));

    /// <summary>The live geometry near <paramref name="pose"/> with <paramref name="key"/> placed at it (its baked vertices carried there).</summary>
    CaptureViews.Geometry GeometryWith(FormKey key, RefPlacementEdit.Pose pose, float margin = 600f)
        => Compose(key.ToString(), OthersOf(key, false), SubjectOf(key, false),
                   RefPlacementEdit.DeltaYUp(RefPlacementEdit.BakedPose(_session.RefOf(key).Ref), pose), TexturedScene.YUp(ToV(pose.Position)), margin);

    /// <summary>A candidate raised 20 units and dropped onto what is below it; a refused drop keeps it at its own height and says why.</summary>
    (RefPlacementEdit.Pose Pose, string Drop) Settle(FormKey key, RefPlacementEdit.Pose target)
        => TrialLane.SettleBy(target, p => CaptureViews.Drop(GeometryWith(key, p), key.ToString()));

    /// <summary>Gate C: the same settle, over a copy of the scene's shapes near the object taken now on the UI thread, so the
    /// nudge settles its candidates in the background while the window stays usable (and an edit made meanwhile changes nothing).</summary>
    Func<RefPlacementEdit.Pose, (RefPlacementEdit.Pose Pose, string Drop)> SettleSnapshot(FormKey key, RefPlacementEdit.Pose home)
    {
        const float margin = 600f;
        var at = TexturedScene.YUp(ToV(home.Position));
        var reach = new Vector3(margin + NudgeRule.Cap + 40f);
        var others = OthersOf(key, false).Where(s => s.Max.X >= at.X - reach.X && s.Min.X <= at.X + reach.X && s.Max.Y >= at.Y - reach.Y && s.Min.Y <= at.Y + reach.Y
                                                     && s.Max.Z >= at.Z - reach.Z && s.Min.Z <= at.Z + reach.Z)
                                         .Select(s => s with { Verts = s.Verts.ToArray(), Tris = s.Tris.ToArray() }).ToList();
        var subject = SubjectOf(key, true).ToList();
        var baked = RefPlacementEdit.BakedPose(_session.RefOf(key).Ref);
        string k = key.ToString();
        return target => TrialLane.SettleBy(target, p => CaptureViews.Drop(Compose(k, others, subject, RefPlacementEdit.DeltaYUp(baked, p), TexturedScene.YUp(ToV(p.Position)), margin), k));
    }

    /// <summary>What the contact owner says the reference touches or crosses at a candidate pose: a fact beside the drop sentence.</summary>
    IReadOnlyList<string> ContactsAt(FormKey key, RefPlacementEdit.Pose pose)
    {
        var g = GeometryWith(key, pose);
        var c = CaptureViews.Contacts(g, key.ToString());
        if (c.Refusal is { } why) return new[] { "contact not assessed: " + why };
        return ContextRead.ContactWords(c, g);
    }

    /// <summary>One trial's re-check: the open row's probe on the trial patch, unpatched and patched (<see cref="Recheck.RunAll"/>),
    /// with contactprobe too for a nudge's proof (<paramref name="contact"/>), then the other rows of the moved reference and its
    /// carried predicates still holding.</summary>
    async Task<TrialRecheck> RecheckTrialAsync(Task<PatchWriter> writer, string patch, string outPath, string work, bool contact = false, CancellationToken ct = default)
    {
        var w = await writer;
        var open = Shell.CurrentRow ?? throw new InvalidOperationException("no conflict row is open, so the trial cannot be re-checked.");
        var b = Shell.Bundle ?? throw new InvalidOperationException("no bundle is open, so the re-check has no detector command to run.");
        var t = w.Target;
        var plugin = PluginName(patch);
        var mod = Recheck.PatchFolder(outPath, plugin);
        var engine = Shell.Launcher.Engine;
        var harness = engine.HarnessDir();
        var conflictdiff = Environment.GetEnvironmentVariable("WB_CONFLICTDIFF") is { Length: > 0 } cd ? cd
            : HarnessShared.CheckoutLayout.ProgramExe(harness, "conflictdiff");
        var buildDirs = (Environment.GetEnvironmentVariable("WB_PROBE_DIRS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        var game = Path.GetDirectoryName(Path.GetFullPath(t.DataDir.TrimEnd('\\', '/')))!;
        string population = "not read";
        var key = _trials?.Key?.ToString() ?? open.Row.RefKey;
        var r = await Task.Run(() => Recheck.RunAll(b.Dir, b.Arm, open.Row, plugin, mod, t.Instance, t.Profile, game, work, engine, conflictdiff,
            buildDirs, harness, s => { if (population == "not read" && s.Contains("population", StringComparison.OrdinalIgnoreCase)) population = s.Split('\n')[0].Trim();
                                       _window?.Dispatcher.BeginInvoke(() => Say("trial re-check: " + s)); },
            moved: contact ? new[] { key } : null, ct: ct,
            routeAt: contact && _trials?.Proving is { } pe ? new[] { (key, pe.Before.Position, pe.After.Position) } : null), ct);
        var un = Path.Combine(work, "unpatched.json"); var pa = Path.Combine(work, "patched.json");
        return new TrialRecheck(r, ClearRule.OthersOfRef(un, pa, r.Row.Id, key), ClearRule.StillHolding(pa, key), population, r.ContactNew,
                                contact ? NudgeProofRule.HeldPairs(un, key) : null, r.Route);
    }

    // ---- Gate C: nudge one object -----------------------------------------------------------------------------------
    NudgeLane? _nudge;
    CancellationTokenSource? _nudgeStop;
    string? _nudgeSaid;
    FormKey? _nudgeKey;
    Task<string>? IWorkbenchScene.NudgeRunning => NudgeRunning;
    internal Task<string>? NudgeRunning { get; private set; }

    object? IWorkbenchScene.NudgeState => new
    {
        running = NudgeRunning is { IsCompleted: false }, @ref = _nudgeKey?.ToString(), phase = _nudge?.Phase, judged = _nudge?.Judged, clear = _nudge?.ClearCount,
        proofs = _nudge?.Proofs.Select(p => new { n = p.N, word = p.Word, sentence = p.Sentence, seconds = p.Seconds }).ToList(),
        said = _nudgeSaid, cap = NudgeRule.Cap, maxProofs = NudgeLane.MaxProofs,
        notChecked = (Shell.Bundle is { } nb && Recheck.RouteArmMissing(nb.Dir, nb.Arm) is { } rm ? new[] { $"walking routes (route cut): {rm}, so a nudge says walking routes were not checked" } : Array.Empty<string>())
                     .Concat(new[] { "rotation: not searched", "scripts that find the object at run time, packages that pick furniture by type, and LOD built from it: not read" }).ToArray(),
        routeArm = "blockprobe --near on the moved object and every object whose walking route its move can reach, before and after the move; a move that cuts one is rejected",
    };

    string IWorkbenchScene.AbortNudge() => AbortNudge();
    internal string AbortNudge()
    {
        if (NudgeRunning is not { IsCompleted: false }) return "no nudge is running.";
        _nudgeStop?.Cancel();
        return $"stopping the nudge of {_nudgeKey}: the running probe is stopped and nothing is staged.";
    }

    Task<string> IWorkbenchScene.NudgeAsync(FormKey key) => NudgeAsync(key);

    /// <summary>The Nudge button's and the <c>nudge</c> verb's one path: every refusal first, in one sentence each, then the lane in
    /// the background. Returns the sentence that it started.</summary>
    internal async Task<string> NudgeAsync(FormKey key)
    {
        if (ReadOnly) throw new InvalidOperationException(ReadOnlyWhy);
        if (NudgeRunning is { IsCompleted: false }) throw new InvalidOperationException($"a nudge of {_nudgeKey} is already running; wait for it or abort it.");
        if (_trials?.Running is { IsCompleted: false }) throw new InvalidOperationException("a find_clear is running; wait for it before nudging.");
        if (!_session.IsDrawn(key)) throw new InvalidOperationException($"{key} is not a drawn reference in this scene; select one the window shows.");
        var open = Shell.CurrentRow ?? throw new InvalidOperationException("a nudge clears the open conflict, and none is open; open one from the Conflicts tab.");
        if (open.Row.CheckId != "geometry.object-overlap")
            throw new InvalidOperationException($"a nudge clears an object overlap, and the open conflict is a {open.Row.CheckId} row; open an overlap, or move the object by hand.");
        var otherKey = string.Equals(open.Row.RefKey, key.ToString(), StringComparison.OrdinalIgnoreCase) ? open.Row.OtherKey
                     : string.Equals(open.Row.OtherKey, key.ToString(), StringComparison.OrdinalIgnoreCase) ? open.Row.RefKey : null;
        if (otherKey is null || !FormKey.TryFactory(otherKey, out var ok))
            throw new InvalidOperationException($"{key} is not one of the open conflict's two objects; select one of them to nudge.");
        if (NudgeRule.OneFix(_session, key) is { } oneFix) throw new InvalidOperationException(oneFix);
        var b = Shell.Bundle ?? throw new InvalidOperationException("no bundle is open, so a nudge has no probe command to prove a position with.");
        if (Recheck.ContactArm(b.Dir, b.Arm, new[] { key.ToString() }).Why is not null)
            throw new InvalidOperationException("this scan did not include placement contact, so a nudge cannot prove that nothing is left in the air; scan again with the placement-contact check ticked, or move the object by hand.");
        if (((IWorkbenchScene)this).Trials is not { } trials || trials.WorkFolder is null)
            throw new InvalidOperationException("with no Work Folder, a nudge's trial patches have nowhere to go; choose one outside the instance and the game folder (workbench_act verb set_work_folder).");
        var harness = Shell.Launcher.Engine.HarnessDir();
        var judgeExe = HarnessShared.CheckoutLayout.ProgramExe(harness, "overlapprobe");
        if (!File.Exists(judgeExe)) throw new InvalidOperationException($"overlapprobe is not built at {judgeExe}, so the nudge's judge cannot run; build SkyWright (scripts\\build.ps1).");
        var src = ((IWorkbenchScene)this).ContextSource ?? throw new InvalidOperationException("this scene has no write target to read what depends on the object from; open it from the launcher's profile.");
        if (await src.MismatchAsync(key) is { } why) throw new InvalidOperationException(why);
        var (refusal, note) = NudgeRule.Refusal(await DisableRule.ReadAsync(src, key), NudgeKinds.Load(NudgeKinds.DefaultPath()));
        if (refusal is not null) throw new InvalidOperationException(refusal);
        var boxes = ((IWorkbenchScene)this).PairBoxes(key) ?? throw new InvalidOperationException($"{key} or {ok} is not drawn in this scene, so no candidate can be made.");
        var home = RefPlacementEdit.BakedPose(_session.RefOf(key).Ref);
        var name = _session.RefOf(key).Ref.BaseEditorId;
        var w = await WriterAsync();
        var t = w.Target;
        var game = Path.GetDirectoryName(Path.GetFullPath(t.DataDir.TrimEnd('\\', '/')))!;
        var dir = Path.Combine(trials.WorkFolder, "nudge-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        var candidate = Recheck.FromBundle(b.Dir, open.Row.CheckId, b.Arm).Candidate;
        var stop = new CancellationTokenSource();
        var settle = SettleSnapshot(key, home);
        var lane = new NudgeLane(settle,
            (req, ct) => Task.Run(() => RunJudge(judgeExe, req, dir, candidate, t, game, ct), ct),
            (edit, n, ct) => trials.ProveAsync(edit, n, contact: true, ct),
            (pose, staged) => _window!.Dispatcher.Invoke(() =>
            {
                // the object may have been edited while the nudge ran: then nothing is staged
                if (NudgeRule.OneFix(_session, key) is { } changed) return "nothing is staged: " + changed;
                _session.Stage(key, pose, staged);
                AfterStage();
                Say($"staged: nudge {name ?? key.ToString()} {staged}" + (note is null ? "" : " " + note));
                return $"staged: nudge {name ?? key.ToString()} {staged}";
            }),
            s => _window?.Dispatcher.BeginInvoke(() => { Say(s); Shell.SetBanner(s); }),
            NameOf,
            s => _window?.Dispatcher.BeginInvoke(() => Say(s)));
        _nudge = lane; _nudgeStop = stop; _nudgeKey = key; _nudgeSaid = null;
        async Task<string> Go()
        {
            string said;
            try { said = await lane.RunAsync(key, name, home, boxes.Subject, boxes.Other, stop.Token, ok); }
            catch (OperationCanceledException) { said = $"the nudge of {key} was stopped; nothing is staged."; }
            catch (Exception ex) { said = "the nudge could not finish: " + (ex is AggregateException ae ? ae.GetBaseException() : ex).Message; }
            _nudgeSaid = said;
            _window?.Dispatcher.BeginInvoke(() => { Say(said); Shell.SetBanner(said); UpdateStatus(); NudgeChanged?.Invoke(); });
            return said;
        }
        NudgeRunning = Go();
        NudgeChanged?.Invoke();
        return FormattableString.Invariant($"nudging {name ?? key.ToString()} ({key}) away from {ok}: the search and the judge take about {NudgeLane.JudgeSeconds} s, then each proof by the probes {NudgeLane.ProofSecondsLow}-{NudgeLane.ProofSecondsHigh} s, at most {NudgeLane.MaxProofs}; the window stays usable, and Abort stops it. Nothing is written: the position the probes confirm is staged.")
               + (note is null ? "" : " " + note);
    }

    /// <summary>An object in the modder's words: its base's editor ID and its plugin as the scene spells it.</summary>
    string NameOf(string k) => FormKey.TryFactory(k, out var fk) ? _session.IsDrawn(fk) is var drawn && drawn
        ? $"{_session.RefOf(fk).Ref.BaseEditorId ?? k} ({_session.RefOf(fk).Ref.RefKey.ModKey.FileName})"   // the plugin's name as the scene spells it
        : $"{k} ({fk.ModKey.FileName})" : k;

    /// <summary>Raised when a nudge starts or ends (the button's label).</summary>
    internal event Action? NudgeChanged;

    /// <summary>The judge program: overlapprobe --nudge on the bundle arm's order rewritten for the scene's profile.</summary>
    static NudgeJudgeAnswer RunJudge(string exe, NudgeRequest req, string dir, string candidate, WriteTarget t, string game, CancellationToken ct)
    {
        var order = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(candidate))!.AsObject();
        order["BaseProfile"] = t.Profile;
        var orderPath = Path.Combine(dir, "order.json");
        File.WriteAllText(orderPath, order.ToJsonString());
        var reqPath = Path.Combine(dir, "request.json"); var ansPath = Path.Combine(dir, "answer.json");
        static float[] V(Vec3 v) => new[] { v.X, v.Y, v.Z };
        File.WriteAllText(reqPath, JsonSerializer.Serialize(new
        {
            schema = "nudge-request/1", subject = req.Subject.ToString(), other = req.Other.ToString(), home = V(req.Home.Position), reach = NudgeRule.Cap + 30f,
            candidates = req.Candidates.Select(c => new { n = c.N, position = V(c.Pose.Position) }).ToList(),
        }));
        var (x, text) = Recheck.Run(exe, new[] { "--order", orderPath, "--instance", t.Instance, "--profile", t.Profile, "--game", game, "--nudge", reqPath, "--nudge-out", ansPath },
                                    Path.Combine(dir, "judge.log"), ct);
        if (File.Exists(ansPath)) return NudgeJudgeAnswer.Read(ansPath);
        throw new InvalidOperationException($"the nudge's judge (overlapprobe --nudge) exited {x} without an answer: {text.Trim().Split('\n').LastOrDefault()?.Trim()}; see {Path.Combine(dir, "judge.log")}.");
    }

    IReadOnlyList<string>? IWorkbenchScene.DependencyRows(string key)
    {
        const string check = "geometry.declared-dependencies";
        if (Shell.Bundle?.Checks.FirstOrDefault(c => c.CheckId == check) is not { State: "ran" }) return null;
        return Shell.Findings.Where(f => f.Row.CheckId == check && string.Equals(f.Row.RefKey, key, StringComparison.OrdinalIgnoreCase))
                             .Select(f => f.Row.Summary).ToList();
    }
    PickHit? IWorkbenchScene.PickCentre() => PickAt(new Vector2(W / 2f, H / 2f));
    float[]? IWorkbenchScene.Camera { get => new[] { _eye.X, _eye.Y, _eye.Z, _yaw, _pitch }; set { if (value is { Length: 5 } c) SetPose(new Vector3(c[0], c[1], c[2]), c[3], c[4]); } }
    string? IWorkbenchScene.PatchFolderOf(string patch) => PatchFolderOf(patch);
    Task<string> IWorkbenchScene.RevertWrittenAsync(FormKey key) => RevertWrittenAsync(key);
    Task<(IReadOnlyList<FormKey> Refs, string? Refusal, string Note)> IWorkbenchScene.RefsOfModAsync(string modFolder, string? plugin) => RefsOfModAsync(modFolder, plugin);
    Task<(RowState Row, IReadOnlyList<ObjectState> Objects, string ServerBuild)> IWorkbenchScene.DecisionInputsAsync(RankedRow row) => DecisionInputsAsync(row);
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

    readonly ViewState _viewState;

    // ---- the review controls: view-only (originals, ghost, hide, see-through, isolate, restore), precise moves
    // (typed offset, axis lock, snap, drop), decisions, and the pending-edits list with a revert per reference.
    // Each control calls the same method its state-tool verb calls.
    readonly Button _restore = new() { Content = "Restore visibility", Margin = new Thickness(4), Width = 300, Background = System.Windows.Media.Brushes.IndianRed,
                                       Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.Bold, Visibility = Visibility.Hidden };
    readonly StackPanel _pendingRows = new();
    readonly TextBox _reason = new() { Width = 180, Margin = new Thickness(4), ToolTip = "a few words saying why" };

    static Button B(string text, string tip, Action click) { var b = new Button { Content = text, Margin = new Thickness(4), ToolTip = tip }; b.Click += (_, _) => click(); return b; }

    void Do(Func<string> act) { try { var s = act(); Say(s); Shell.SetBanner(s); } catch (InvalidOperationException ex) { Shell.SetBanner(ex.Message); } UpdateStatus(); Invalidate(); }

    UIElement ToolsRow()
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        var original = new System.Windows.Controls.Primitives.ToggleButton { Content = "Original (hold Space)", Margin = new Thickness(4), ToolTip = "draw every edited object where the load order puts it" };
        original.Checked += (_, _) => Do(() => _viewState.ShowOriginal(true));
        original.Unchecked += (_, _) => Do(() => _viewState.ShowOriginal(false));
        _viewState.Changed += _ => { if (original.IsChecked != _viewState.ShowingOriginal) original.IsChecked = _viewState.ShowingOriginal; };
        var ghost = new System.Windows.Controls.Primitives.ToggleButton { Content = "Ghost", Margin = new Thickness(4), ToolTip = "a see-through copy of each edited object at its load-order place" };
        ghost.Checked += (_, _) => Do(() => { _viewState.Ghost = true; return "ghost shown"; });
        ghost.Unchecked += (_, _) => Do(() => { _viewState.Ghost = false; return "ghost hidden"; });
        if (!ReadOnly) { row.Children.Add(original); row.Children.Add(ghost); }   // both are about edits, which a read-only scene has none of
        row.Children.Add(B("Hide in front", "hide whatever stands between the camera and the selected object (Alt+click hides one object, Alt+Shift+click one part)",
            () => Do(() => _session.Selected is { } k ? _viewState.HideInFront(k, () => PickAt(new Vector2(W / 2f, H / 2f))) : "select an object first")));
        row.Children.Add(B("See-through", "draw the selected object as a flat see-through tint", () => Do(() => _session.Selected is { } k ? _viewState.SeeThrough(k) : "select an object first")));
        var mod = new TextBox { Width = 140, Margin = new Thickness(4), ToolTip = "a mod folder, as the launcher lists it" };
        row.Children.Add(mod);
        row.Children.Add(B("Mod see-through", "draw every object the named mod folder's plugins win as a see-through tint", async () =>
        {
            try
            {
                var (refs, why, note) = await RefsOfModAsync(mod.Text.Trim(), null);
                Do(() => (why ?? (refs.Count == 0 ? $"no object drawn here is won by {mod.Text.Trim()}" : _viewState.SeeThrough(refs, mod.Text.Trim()))) + (note.Length > 0 ? "; " + note : ""));
            }
            catch (InvalidOperationException ex) { Shell.SetBanner(ex.Message); }
        }));
        row.Children.Add(B("Isolate", "hide everything but the selected object and the open conflict's other object", () => Do(() =>
        {
            if (_session.Selected is not { } a) return "select an object first";
            FormKey? b = Shell.CurrentRow?.Row.OtherKey is { } o && FormKey.TryFactory(o, out var ok) && ok != a ? ok : null;
            return _viewState.Isolate(a, b);
        })));
        _restore.Click += (_, _) => Do(() => _viewState.Restore());
        _viewState.Changed += _ => { _restore.Content = _viewState.Label.Length > 0 ? _viewState.Label : "Restore visibility"; _restore.Visibility = _viewState.Active ? Visibility.Visible : Visibility.Hidden; };
        row.Children.Add(_restore);

        if (ReadOnly)
        {
            row.Children.Add(new TextBlock { Text = "  Read-only scene: view tools only.", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush(Theme.Dim) });
            return row;
        }
        var offset = new TextBox { Width = 90, Margin = new Thickness(4), ToolTip = "a typed move, such as +120 x, -30 z or 15°" };
        row.Children.Add(offset);
        row.Children.Add(B("Move", "stage the typed move on the selected object", () => Do(() =>
        {
            if (_session.Selected is not { } k) return "select an object first";
            var (d, turn, why) = MoveRules.ParseOffset(offset.Text);
            if (why is not null) return why;
            var c = _session.Current(k);
            var after = c with { Position = new Vec3(c.Position.X + d.X, c.Position.Y + d.Y, c.Position.Z + d.Z),
                                 Rotation = turn == 0 ? c.Rotation : RefPlacementEdit.Turned(c.Rotation, Vector3.UnitZ, float.DegreesToRadians(MoveRules.SnapTurn(turn, _input.AngleStep))) };
            _session.Stage(k, MoveRules.Snap(c, after, _input.SnapStep, 0f));
            AfterStage();
            return $"staged: {EditSession.Sentence(_session.Pending[^1], _session.RefOf(k).Ref.BaseEditorId)}";
        })));
        row.Children.Add(B("Disable", "stage the selected object to be written initially disabled in the patch plugin (never deleted); refused when another record depends on it. " + DisableRule.Limits, async () =>
        {
            if (_session.Selected is not { } k) { Shell.SetBanner("select an object first"); return; }
            try { var s = await DisableAction.StageAsync(this, k); Say(s); Shell.SetBanner(s); }
            catch (InvalidOperationException ex) { Say("disable refused: " + ex.Message); Shell.SetBanner(ex.Message); }
            UpdateStatus(); Invalidate();
        }));
        var nudge = new Button { Content = "Nudge", Margin = new Thickness(4),
            ToolTip = $"find the smallest move, within {NudgeRule.Cap:0} units, that clears the open conflict for the selected object; the judge chooses, the probes prove it (overlap, what rests on it, and walking routes when the scan included the route-cut check), and the position is staged, never written. Press again to abort." };
        nudge.Click += async (_, _) =>
        {
            if (NudgeRunning is { IsCompleted: false }) { var a = AbortNudge(); Say(a); Shell.SetBanner(a); return; }
            if (_session.Selected is not { } k) { Shell.SetBanner("select an object first"); return; }
            try { var s = await NudgeAsync(k); Say(s); Shell.SetBanner(s); }
            catch (InvalidOperationException ex) { Say("nudge refused: " + ex.Message); Shell.SetBanner(ex.Message); }
            UpdateStatus(); Invalidate();
        };
        NudgeChanged += () => nudge.Content = NudgeRunning is { IsCompleted: false } ? "Abort nudge" : "Nudge";
        row.Children.Add(nudge);
        var snap = new ComboBox { Margin = new Thickness(4), ToolTip = "snap moves to a grid and turns to a step", ItemsSource = new[] { "no snap", "grid 1", "grid 8", "grid 32", "grid 128", "turn 5°", "turn 15°", "turn 45°" }, SelectedIndex = 0 };
        snap.SelectionChanged += (_, _) =>
        {
            var t = (string)snap.SelectedItem;
            if (t == "no snap") { _input.SnapStep = 0; _input.AngleStep = 0; }
            else if (t.StartsWith("grid")) _input.SnapStep = float.Parse(t[5..], CultureInfo.InvariantCulture);
            else _input.AngleStep = float.Parse(t[5..].TrimEnd('°'), CultureInfo.InvariantCulture);
            Shell.SetBanner($"snap: grid {_input.SnapStep} units, turn {_input.AngleStep}°");
        };
        row.Children.Add(snap);
        row.Children.Add(B("Drop to surface", "lower the selected object straight down onto the drawn surface or terrain below it; it lowers only", () => Do(() =>
        {
            if (_session.Selected is not { } k) return "select an object first";
            var ans = CaptureViews.Drop(CaptureViews.FromPickShapes(_opt.PickShapes), k.ToString());
            if (ans.Refusal is { } why) return why;
            _session.StageDelta(k, new Vector3(0, 0, -ans.Distance!.Value), Vector3.UnitZ, 0f);
            AfterStage();
            return FormattableString.Invariant($"lowered {k} {ans.Distance:0.##} units onto {(ans.Onto == CaptureViews.TerrainKey ? "the terrain" : $"{ans.Onto} ({ans.OntoName})")}");
        })));

        row.Children.Add(new TextBlock { Text = "  Decide:", VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(_reason);
        foreach (var v in Decisions.Verdicts)
            row.Children.Add(B(char.ToUpperInvariant(v[0]) + v[1..], $"record '{v}' for the open conflict with the reason typed, then open the next undecided one", async () =>
            {
                if (Shell.CurrentRow is not { } r) { Shell.SetBanner("open a conflict first"); return; }
                var res = await StateTools.DecideOn(new LiveSurface(), Shell.Standings, r.Rank, null, v, _reason.Text);
                var text = string.Join(" ", res.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text));
                Shell.SetBanner(res.IsError == true ? text : (System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("said").GetString() ?? ""));
                if (res.IsError != true) { _reason.Text = ""; Shell.FillConflicts(); }
            }));
        return row;
    }

    // ---- item 5: the selected reference's context, read off the UI thread when it is selected
    readonly StackPanel _contextRows = new();
    string? _contextShown;

    UIElement ContextPanel()
    {
        _session.Changed += _ => FillContext();
        FillContext();
        return new Expander { Header = "Context: what follows a move and what stays", IsExpanded = true,
                              Content = new ScrollViewer { Height = 90, Content = _contextRows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
    }

    void Line(string text, System.Windows.Media.Brush? colour = null) =>
        _contextRows.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 0, 6, 0), Foreground = colour ?? System.Windows.Media.Brushes.Black });

    void FillContext()
    {
        var scene = (IWorkbenchScene)this;
        _contextRows.Children.Clear();
        if (_session.Selected is not { } k) { Line("Select a reference to see its doors, what names it, what rests on it and the navmesh under it."); _viewState.HighlightNav(Array.Empty<(Vector3, Vector3, Vector3)>()); return; }
        var key = k.ToString();
        if (ContextCache.Read(scene, key) is not { } read) { Line("This scene has no write target to read the context from; open it from the launcher's profile."); return; }
        if (!read.IsCompleted)
        {
            Line($"{key}: reading its doors, what names it, what rests on it and the navmesh under it", System.Windows.Media.Brushes.DimGray);
            if (_contextShown != key) { _contextShown = key; read.ContinueWith(_ => _window?.Dispatcher.BeginInvoke(FillContext), TaskScheduler.Default); }
            return;
        }
        if (read.IsFaulted) { Line($"{key}: " + read.Exception!.GetBaseException().Message, System.Windows.Media.Brushes.Firebrick); return; }
        var a = read.Result;
        var ticks = new List<(CheckBox Box, FormKey Key)>();
        void Tickable(string k2, string text)
        {
            if (FormKey.TryFactory(k2, out var fk) && _session.IsDrawn(fk))
            {
                var cb = new CheckBox { Content = text, Margin = new Thickness(6, 0, 6, 0), IsChecked = ContextCache.MovedWith(scene, key).Contains(k2) };
                ticks.Add((cb, fk)); _contextRows.Children.Add(cb);
            }
            else Line(text + "   (not drawn here)");
        }
        foreach (var d in a.Doors)
            Tickable(d.Key, $"door {d.Key}: {d.Label}" + (d.FarDoor is { } far ? $"; leads to {far} in cell {d.FarCell ?? "?"}" : ""));
        foreach (var n in a.Names) Tickable(n.Key, $"{n.Key} names it ({n.Field})");
        bool nudged = _session.IsNudged(k);
        foreach (var r in a.Resting) Tickable(r.Key, nudged ? $"{r.Key} rests on it by the drawn shapes" : r.Sentence);
        if (nudged && a.Resting.Count > 0) Line(ContextRead.NudgedResting(a.Resting.Select(r => r.Key)), System.Windows.Media.Brushes.DimGray);
        foreach (var (name, text) in a.Sections) if (name != "navmesh") Line($"{name}: {text}", System.Windows.Media.Brushes.DimGray);
        Line(string.Join("; ", a.Navmesh.Select(n => $"{n.Navmesh}: {n.Triangles} triangle(s)")) is { Length: > 0 } nv ? $"navmesh under it (green): {nv}" : "no navmesh under it", System.Windows.Media.Brushes.DarkGreen);
        Line(a.Sections.TryGetValue("navmesh", out var rule) ? rule : ContextRead.NavRule, System.Windows.Media.Brushes.DimGray);
        Line(a.Statement, System.Windows.Media.Brushes.DimGray);
        if (ticks.Count > 0)
            _contextRows.Children.Add(B("Move ticked with it", "stage the selected reference's move for each ticked reference", () =>
                Do(() => MoveWithRule.Apply(scene, k, ticks.Where(t => t.Box.IsChecked == true).Select(t => t.Key).ToList()))));
        Line("Before a commit: " + ContextRead.StaysBehind(a, ContextCache.MovedWith(scene, key), nudged));
        if (a.Box is { } box)
            _viewState.HighlightNav(ContextRead.TrianglesUnder(_opt.NavTris, box).Select(t => (t.A, t.B, t.C)));
        Invalidate();
    }

    UIElement PendingPanel()
    {
        void Fill()
        {
            _pendingRows.Children.Clear();
            foreach (var k in _session.Edited())
            {
                var line = new DockPanel { Margin = new Thickness(6, 1, 6, 1) };
                bool staged = _session.Pending.Any(e => e.Ref == k);
                string text = staged
                    ? EditSession.Sentence(new PendingEdit(k, _session.Pending.First(e => e.Ref == k).Before, _session.Current(k), _session.IsDisabled(k), _session.Pending.Last(e => e.Ref == k).Nudge), _session.RefOf(k).Ref.BaseEditorId)
                    : _session.WrittenDisabled.Contains(k) ? $"written to {_session.Written[k].Patch}: {_session.RefOf(k).Ref.BaseEditorId ?? k.ToString()}, initially disabled (the plugin is not enabled)"
                    : _session.WrittenNudged.Contains(k) ? NudgeRule.WrittenLine(_session.RefOf(k).Ref.BaseEditorId ?? k.ToString(), _session.Written[k].Patch, _session.NudgeRechecked(k))
                    : $"written to {_session.Written[k].Patch}: {_session.RefOf(k).Ref.BaseEditorId ?? k.ToString()}";
                var revert = new Button { Content = "Revert", Margin = new Thickness(4, 0, 0, 0), ToolTip = staged ? "drop this object's staged edits" : "remove this object's override from its patch" };
                var key = k;
                revert.Click += async (_, _) =>
                {
                    try { Shell.SetBanner(_session.Revert(key) is { } r ? $"reverted {key}" : await RevertWrittenAsync(key)); AfterStage(); }
                    catch (InvalidOperationException ex) { Shell.SetBanner(ex.Message); }
                };
                DockPanel.SetDock(revert, Dock.Right);
                line.Children.Add(revert);
                line.Children.Add(new TextBlock { Text = (staged ? "staged: " : "") + text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
                _pendingRows.Children.Add(line);
            }
        }
        _session.Changed += _ => Fill();
        Fill();
        return new Expander { Header = "Edits", IsExpanded = true, Content = new ScrollViewer { Height = 90, Content = _pendingRows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
    }
    readonly ViewTints _tints = new();
    internal ViewState View => _viewState;

    /// <summary>The folder of a patch this session wrote, by plugin name, under the write target's patch root.</summary>
    internal string? PatchFolderOf(string patch)
    {
        var t = _writer?.Target ?? TargetFromEnv();
        if (t is null) return null;
        try { return Recheck.PatchFolder(t.PatchRoot, PluginName(patch)); } catch (InvalidOperationException) { return null; }
    }

    /// <summary>Undo a written edit: its override is removed from the patch, and the view draws the load order's place.</summary>
    internal async Task<string> RevertWrittenAsync(FormKey key)
    {
        if (!_session.Written.TryGetValue(key, out var w)) throw new InvalidOperationException($"{key} has no written edit to undo.");
        var folder = PatchFolderOf(w.Patch) ?? throw new InvalidOperationException($"the folder holding {w.Patch} was not found under the write target, so its override cannot be removed.");
        var said = await (await WriterAsync()).RevertWrittenAsync(key, folder);
        _session.ForgetWritten(key);
        return said;
    }

    /// <summary>The bundle arm's candidate order: its mod folders (the view) and plugins.</summary>
    (IReadOnlyList<string> Mods, IReadOnlyList<string> Plugins) ArmOrder()
    {
        var b = Shell.Bundle ?? throw new InvalidOperationException("no bundle is open, so the arm's mod folders are not known.");
        var cand = BundleConflicts.CandidateFile(b.Dir, b.Arm);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cand));
        static List<string> L(System.Text.Json.JsonElement r, string n) => r.TryGetProperty(n, out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>();
        return (L(doc.RootElement, "Mods"), L(doc.RootElement, "Plugins"));
    }

    /// <summary>The drawn references whose winning record a plugin of this mod folder (in the arm's view, by the probes'
    /// layer map) or this plugin provides. The winners are read in one call, each checked against the scene's pose.</summary>
    internal async Task<(IReadOnlyList<FormKey> Refs, string? Refusal, string Note)> RefsOfModAsync(string? modFolder, string? plugin)
    {
        var w = await WriterAsync();
        IReadOnlyList<string> plugins;
        if (modFolder is not null)
        {
            var (mods, order) = ArmOrder();
            var t = w.Target;
            var (ps, why) = ModTint.PluginsOf(modFolder, t.ProfileDir, mods, t.ModsDir, t.DataDir, Path.Combine(Mo2Instance.LayoutRoot(t.Instance), "overwrite"), order);
            if (why is not null) return (Array.Empty<FormKey>(), why, "");
            plugins = ps;
        }
        else plugins = new[] { plugin! };
        // a Base Object Swapper reference is drawn with the swap's transform, not its record's: its winner is read
        // without the placement check, and it is left out and counted; one drawn elsewhere is counted too
        var all = DrawnAll().Select(r => (Key: r.Ref.RefKey, Bos: r.Ref.BosSwapped, Pose: r.Ref.BosSwapped ? (RefPlacementEdit.Pose?)null : RefPlacementEdit.BakedPose(r.Ref))).ToList();
        var (winners, mismatched) = await w.WinnersAsync(all.Select(a => (a.Key, a.Pose)).ToList());
        var (refs, note) = ModTint.Pick(all.Select(a => (a.Key, a.Bos)), winners, mismatched, plugins);
        return (refs, null, note);
    }

    /// <summary>The detector row and the objects a decision on it is keyed on, read now through the bound server; each
    /// drawn reference's placement is checked against the scene first.</summary>
    internal async Task<(RowState Row, IReadOnlyList<ObjectState> Objects, string ServerBuild)> DecisionInputsAsync(RankedRow row)
    {
        var w = await WriterAsync();
        var keys = new[] { row.Row.RefKey, row.Row.OtherKey }.Where(k => k is not null && FormKey.TryFactory(k, out _)).Select(k => FormKey.Factory(k!)).ToList();
        var notDrawn = keys.Where(k => !_session.IsDrawn(k)).ToList();
        if (notDrawn.Count > 0)
            throw new InvalidOperationException($"{string.Join(" and ", notDrawn)} is not drawn in this scene, so its objects are not compared until its cell is open.");
        var objects = await w.ReadObjectsAsync(keys.Select(k => (k, RefPlacementEdit.BakedPose(_session.RefOf(k).Ref))).ToList());
        return (((IWorkbenchSurface)new LiveSurface()).RowStateOf(row), objects, w.ReaderBuild);
    }

    /// <summary>The session file follows every change; a failed write is said, never silent.</summary>
    void SaveSession()
    {
        try { SessionFile.Save(new LiveSurface()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Say($"the session file could not be written ({ex.Message})"); }
    }

    /// <summary>Restore this bundle's saved session, once, when its scene opens: written edits are redrawn from the patch file
    /// itself (Mutagen reads the plugin), staged edits re-staged where their reference has not moved, the selection and camera.</summary>
    void RestoreSession()
    {
        IWorkbenchSurface live = new LiveSurface();
        if (live.PlanId is not { } plan || live.Arm is not { } arm) return;
        var (path, _) = SessionFile.PathFor(live.WorkFolder, plan, arm);
        if (path is null || SessionFile.Read(path) is not { } d) return;
        var notes = SessionFile.Restore(_session, d, (k, sw) => ReadPatchPose(k, sw));
        if (SessionFile.CameraFor(d, SceneCell) is { Length: 5 } c) SetPose(new Vector3(c[0], c[1], c[2]), c[3], c[4]);
        AfterStage();
        var said = $"restored the session from {path}: {_session.Pending.Count} staged edit(s), {_session.Written.Count} written" + (notes.Count > 0 ? "; " + string.Join(" ", notes) : "");
        Say(said); Shell.SetBanner(said);
    }

    static RefPlacementEdit.Pose? ReadPatchPose(FormKey key, SavedWritten w)
    {
        var plugin = Path.Combine(w.PatchFolder, PluginName(w.Patch));
        if (!File.Exists(plugin)) return null;
        using var mod = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(plugin, Mutagen.Bethesda.Skyrim.SkyrimRelease.SkyrimSE);
        var r = mod.EnumerateMajorRecords<Mutagen.Bethesda.Skyrim.IPlacedObjectGetter>().FirstOrDefault(x => x.FormKey == key);
        if (r?.Placement is not { } p) return null;
        if (w.Disable && (r.MajorRecordFlagsRaw & 0x800) == 0) return null;            // the patch no longer disables it
        return new RefPlacementEdit.Pose(new Vec3(p.Position.X, p.Position.Y, p.Position.Z), new Vec3(p.Rotation.X, p.Rotation.Y, p.Rotation.Z), r.Scale ?? 1f);
    }

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

    /// <summary>The houseCARL writer for the write target the scene has now, from its one owner (<see cref="HousecarlWriters"/>):
    /// houseCARL's code in this process, no program started; asked for on every use, never kept past a profile change.</summary>
    internal Task<PatchWriter> WriterAsync()
    {
        try
        {
            var target = TargetFromEnv()
                ?? throw new InvalidOperationException("no write target: set WB_INSTANCE, WB_PROFILE and WB_GAME_DATA to the MO2 instance, profile and game Data folder the scene was built from.");
            var fresh = _writer?.Target != target;
            _writer = HousecarlWriters.For(target, Path.Combine(_outDir, "housecarl-data"), Program.Corpus(), Say);
            // the status line belongs to the window: it is updated there
            if (fresh) { if (_window?.Dispatcher is { } d && !d.CheckAccess()) _ = d.BeginInvoke(UpdateStatus); else UpdateStatus(); }
            return Task.FromResult(_writer);
        }
        catch (Exception ex) { return Task.FromException<PatchWriter>(ex); }
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
            // every object this patch disables, the open row's own object and its other first
            var disabledHere = _session.WrittenDisabled.Where(k => string.Equals(_session.Written[k].Patch, plugin, StringComparison.OrdinalIgnoreCase))
                .Select(k => k.ToString())
                .OrderBy(k => string.Equals(k, open.Row.RefKey, StringComparison.OrdinalIgnoreCase) || string.Equals(k, open.Row.OtherKey, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(k => k, StringComparer.Ordinal).ToList();
            IReadOnlyList<string>? disabled = disabledHere.Count > 0 ? disabledHere : null;
            // every object this patch moved by a nudge (Gate C): contactprobe judges what the move left in the air, and the banner
            // says walking routes were not checked
            var nudgedHere = _session.NudgedIn(plugin);
            IReadOnlyList<string>? nudged = nudgedHere.Count > 0 && disabled is null ? nudgedHere : null;
            // The view shows the edited reference where this patch puts it (read back from the patch), never silently
            // where the load order still has it (stage-4 review M-1); the open row's own object, when it is the one this
            // patch disables, has no new place to show.
            if (FormKey.TryFactory(open.Row.RefKey, out var rk) && !disabledHere.Contains(rk.ToString(), StringComparer.OrdinalIgnoreCase) && _session.IsDrawn(rk)
                && !(_session.Written.TryGetValue(rk, out var wr) && string.Equals(wr.Patch, plugin, StringComparison.OrdinalIgnoreCase)))
            {
                var wtr = await WriterAsync();
                var (pp, pr) = await wtr.ReadPlacementAsync(open.Row.RefKey, source: plugin);
                _session.ShowWritten(rk, new RefPlacementEdit.Pose(pp, pr, _session.Current(rk).Scale), plugin);
                Reframe(rk);
            }
            var engine = Shell.Launcher.Engine;
            var harness = engine.HarnessDir();
            var conflictdiff = Environment.GetEnvironmentVariable("WB_CONFLICTDIFF") is { Length: > 0 } cd ? cd
                : HarnessShared.CheckoutLayout.ProgramExe(harness, "conflictdiff");
            var buildDirs = (Environment.GetEnvironmentVariable("WB_PROBE_DIRS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
            var game = Path.GetDirectoryName(Path.GetFullPath(t.DataDir.TrimEnd('\\', '/')))!;
            var work = Path.Combine(_outDir, "recheck-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss"));
            Rechecking = true; RecheckRefusal = null;
            Shell.SetBanner($"re-checking conflict {open.Rank} ({open.Row.Kind}) with {plugin}: the detector runs on the patch's cell, unpatched and patched{(disabled is null ? nudged is null ? "" : ", and placement contact for what the nudge may leave in the air" : ", and placement contact for what rested on the disabled object")}; the window stays usable.");
            UpdateStatus();
            var row = open.Row; var arm = b.Arm; var dir = b.Dir;
            var routeAt = nudged is null ? null : Recheck.RouteTargets(_session, nudged);
            var r = await Task.Run(() => Recheck.RunAll(dir, arm, row, plugin, mod, t.Instance, t.Profile, game, work, engine, conflictdiff,
                buildDirs, harness, s => _window?.Dispatcher.BeginInvoke(() => Say("re-check: " + s)), disabled, nudged, routeAt: routeAt));
            LastRecheck = r;
            Shell.SetBanner(Recheck.Banner(open.Rank, r, NameOf));
            // the Edits line of each nudged object says this re-check's short words (review E2)
            foreach (var nk in nudged ?? Array.Empty<string>())
                if (FormKey.TryFactory(nk, out var nfk)) _session.NoteNudgeRechecked(nfk, Recheck.NudgeOutcome(r));
            Say("re-check: " + r.Verdict);
            if (r.Route is { } ra) Say($"re-check: route cut: {ra.Word}: {ra.Sentence}");
            Say("re-check: row " + r.Row.Id + ": " + r.Row.Sentence);
            foreach (var n in r.New.Concat(r.ContactNew ?? Array.Empty<RowVerdict>())) Say("re-check: new row " + n.Id + ": " + n.Sentence);
            foreach (var o in r.Others ?? Array.Empty<RowVerdict>()) Say("re-check: other row naming a disabled object " + o.Id + ": " + o.Sentence);
            if (r.ContactNote is { } cn) Say("re-check: " + cn);
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
    internal string Capture(string name, bool caption = true)
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
        // a view capture carries its cell and pose in a band on top; a window capture shows the view as the window does, without it
        TexturedScene.SaveFrame(px, w, h, caption ? new[] { $"{_opt.CellLabel} — SkyWright — {name} — {pose}" } : Array.Empty<string>(),
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
