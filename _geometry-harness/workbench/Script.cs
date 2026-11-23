using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Mutagen.Bethesda.Plugins;
using ViewProbe;

namespace Workbench;

/// <summary>The headless driver (<c>WB_SCRIPT=file</c>): one step per line, run between frames through the same
/// handlers the mouse and the bar use, so a test exercises the real input path. Steps:
/// <code>
/// wait N                         render N frames
/// pose X,Y,Z YAW PITCH           put the camera there
/// clickworld X,Y,Z               click where a Skyrim point is drawn
/// click X,Y                      press and release at framebuffer pixel X,Y (a plain click selects)
/// drag X0,Y0 X1,Y1 [N]           press, move in N steps (default 8), release (a drag on the handle edits)
/// dragworld AXIS|ring D          drag the selected handle part by D units (D degrees for ring), from its own screen place
/// select FORMKEY | none          select directly (what an MCP client will do)
/// stage FORMKEY DX,DY,DZ DEG     stage a move and a turn about world Z, composed (what an MCP client will do)
/// undo                           undo the last staged edit
/// pickworld X,Y,Z                project a Skyrim point to the screen, pick there, print what is hit
/// capture NAME                   save the frame as NAME.png
/// capturewindow NAME             save the whole window (tabs, banner, status bar, view) as NAME.png
/// dump FORMKEY NAME              save the reference's drawn vertices as NAME.json
/// tilted [N]                     list drawn references tilted about X and Y, nearest first
/// fps N                          time N frames; print mean fps, p50/p95/max frame ms, foreground, refresh rate
/// fpslog start PATH | stop       write one CSV row per frame to PATH (FrameStats); stop prints the summary line
/// fpsshow on|off                 the frame-rate readout beside the bar's buttons
/// hold SECONDS                   wait without asking for frames (the view draws only what changes)
/// yawsweep SECONDS [DEG/S] [HZ]  hold the right button and move the mouse sideways (default 45 degrees a second, 250 messages a second), as posted messages to the view
/// fly SECONDS [KEY]              hold W (or A, S, D, Q, E) down for SECONDS with the keyboard's own repeat delay and rate, as posted messages to the view
/// commit dry|real PATCHNAME      dry-run or write the staged edits (a real write re-checks the open conflict row)
/// recheck PATCHNAME              re-check the open conflict row against that written patch
/// repairpreview N|FORMKEY        plan the repair of the navmesh Findings row ranked N, or naming that navmesh (RepairDesk); nothing written
/// repairwrite N|FORMKEY PATCH    write that row's previewed repair to that new patch, read it back and re-check it
/// expect recheck WORD            pass or fail on the last re-check's word for the open row (resolved, new, ...)
/// expect created FORMKEY         pass or fail: the last re-check lists a new row naming that reference
/// readback FORMKEY PLUGIN        read that plugin's Placement of the reference through the server
/// conflict N                     click the Conflicts tab's row ranked N (as listed now)
/// treeref FORMKEY                click that reference in the Records tree
/// showall on|off                 the Conflicts tab's "all rows on this arm" toggle
/// state NAME                     dump the shell's structured state (bundle, ranked rows, tree, scene, camera) as NAME.json
/// msgclickworld X,Y,Z            post WM_LBUTTONDOWN/UP to the view's window at the point's device pixel (the real mouse path)
/// msgclickref FORMKEY           the same, at the reference's box centre (where a landing points the camera)
/// msgdragworld AXIS D            the same as dragworld, by posted WM_LBUTTONDOWN/MOUSEMOVE/LBUTTONUP messages
/// expect selected FORMKEY|none   pass or fail on the selection (a failure makes the exit code 4)
/// expect staged N                pass or fail on the number of staged edits
/// expect moved FORMKEY DX,DY,DZ T   pass or fail on the selected reference's staged move from its built position, within T units
/// expect marked                 pass or fail: cyan pixels in the selected reference's projected box (and magenta in the partner's)
/// expect partner FORMKEY|none    pass or fail on the marked partner
/// marks on|off                   the toolbar's Marks layer (the same as layer marks on|off)
/// layer NAME on|off              a layer of the view (ViewLayers): navmesh, borders, markers, planes, terrain, water, grass, trees, clutter, statics, marks
/// borders through|fence on|off   an option of the cell borders, as their arrow's menu: the outline drawn through objects; the fence
/// expect landed [FORMKEY]        pass or fail: that reference (default: the selected one) selected, the screen-centre ray crossing its shapes
/// expect inview FORMKEY          pass or fail: the reference's box centre is drawn inside the view and the screen-centre ray crosses it
/// waitfile PATH [MAXSEC]         wait (drawing nothing) until PATH exists, or MAXSEC seconds (default 900): an MCP client drives the window meanwhile
/// idlecpu SECONDS                the process's CPU over SECONDS with nothing happening, by cycle counts, as a share of one busy core
/// cell CELL [FORMKEY]            ask for a cell (a FormKey or EditorID) on this scene's load order, read-only, as the World tab does; land on FORMKEY
/// cellon PROFILE CELL [FORMKEY]  the same on another profile of this instance: the scene is rebuilt on that load order
/// expect cell CELL               pass or fail: the scene shown is that cell (its FormKey, or its label)
/// quit                           close the window
/// </code>
/// Every result line is printed with the prefix <c>[wb]</c>.</summary>
internal sealed class Script
{
    WorkbenchHost _h;
    readonly Queue<string> _steps;
    static Script? _shared;
    bool _quiet;                       // an idle measurement: the script does not ask for frames while it runs

    /// <summary>The script still has work, so a demand loop keeps drawing (its steps run between frames).</summary>
    internal bool WantsFrames => !_quiet && (_steps.Count > 0 || _wait > 0 || _busy);
    int _wait = 5;                     // settle a few frames before the first step
    bool _busy;

    Script(WorkbenchHost h, IEnumerable<string> steps) { _h = h; _steps = new(steps); }

    /// <summary>The Findings row ranked N, or the first navmesh row naming that navmesh (XXXXXX:Plugin); shown selected in
    /// the Findings tab, as a click leaves it.</summary>
    static RankedFinding Finding(string key)
    {
        var f = (key.Contains(':') ? Shell.Findings.FirstOrDefault(x => string.Equals(x.Row.RefKey, key, StringComparison.OrdinalIgnoreCase) && NavRepairLane.Kinds.Contains(x.Row.Kind))
                                   : Shell.Findings.FirstOrDefault(x => x.Rank == int.Parse(key, CultureInfo.InvariantCulture)))
            ?? throw new InvalidOperationException($"no Findings row {key} is listed.");
        Shell.ShowTab("Findings"); Shell.SelectFinding(f);
        return f;
    }

    /// <summary>The run's one script, attached to the scene now shown: a cell switch hands the remaining steps to the
    /// next scene, which settles a few frames before the next step.</summary>
    internal static Script? Attach(WorkbenchHost h)
    {
        if (_shared is null) return _shared = Load(h);
        _shared._h = h; _shared._wait = 5; _shared._busy = false;
        return _shared;
    }

    static Script? Load(WorkbenchHost h)
    {
        var path = Environment.GetEnvironmentVariable("WB_SCRIPT");
        if (path is not { Length: > 0 }) return null;
        var lines = File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        WorkbenchHost.Say($"script {path}: {lines.Count} step(s)");
        return new Script(h, lines);
    }

    static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
    /// <summary>A step's words. A FormKey's plugin name may hold spaces ("00083C:unofficial skyrim special edition patch.esp"):
    /// the words after one that holds a colon join it until a plugin extension ends the name.</summary>
    internal static string[] Tokens(string line)
    {
        var o = new List<string>();
        foreach (var w in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (o.Count > 0 && LooksLikeFormKey(o[^1]) && !EndsLikePlugin(o[^1])) o[^1] += " " + w;
            else o.Add(w);
        }
        return o.ToArray();
    }
    /// <summary>Six hex digits and a colon: a FormKey's start (a drive-letter path's colon is not one).</summary>
    static bool LooksLikeFormKey(string w) => w.Length > 7 && w[6] == ':' && w.Take(6).All(char.IsAsciiHexDigit);
    static bool EndsLikePlugin(string w) => w.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || w.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || w.EndsWith(".esl", StringComparison.OrdinalIgnoreCase);

    static Vector2 P2(string s) { var p = s.Split(','); return new(F(p[0]), F(p[1])); }
    static Vector3 P3(string s) { var p = s.Split(','); return new(F(p[0]), F(p[1]), F(p[2])); }

    internal void Tick()
    {
        if (_busy) return;
        if (_wait > 0) { _wait--; return; }
        if (_steps.Count == 0) return;
        var line = _steps.Dequeue();
        WorkbenchHost.Say("> " + line);
        var a = Tokens(line);
        try { Run(a); }
        catch (Exception ex) { WorkbenchHost.Say($"step refused: {ex.Message}"); }
    }

    void Run(string[] a)
    {
        switch (a[0])
        {
            case "wait": _wait = int.Parse(a[1]); break;
            case "pose": _h.SetPose(P3(a[1]), F(a[2]), F(a[3])); _wait = 2; break;
            case "clickworld":
            {
                var sp = _h.Project(P3(a[1]));
                if (sp is not { } c) { WorkbenchHost.Say($"clickworld {a[1]}: behind the eye"); break; }
                WorkbenchHost.Say(FormattableString.Invariant($"clickworld {a[1]} -> screen {c.X:0},{c.Y:0}"));
                _h.OnPress(c); _h.OnRelease(c); _wait = 2; break;
            }
            case "click": { var p = P2(a[1]); _h.OnPress(p); _h.OnRelease(p); _wait = 2; break; }
            case "drag":
            {
                var p0 = P2(a[1]); var p1 = P2(a[2]); int n = a.Length > 3 ? int.Parse(a[3]) : 8;
                _h.OnPress(p0);
                for (int i = 1; i <= n; i++) _h.OnMove(Vector2.Lerp(p0, p1, i / (float)n));
                _h.OnRelease(p1); _wait = 2; break;
            }
            case "dragworld":
            {
                var g = _h.Handles;
                if (g.Centre is not { } c) { WorkbenchHost.Say("dragworld: nothing selected"); break; }
                float d = F(a[2]);
                Vector3 A, B;
                if (a[1] == "ring")
                {
                    float r = g.Size * 0.8f, t = float.DegreesToRadians(d);
                    A = c + new Vector3(r, 0, 0); B = c + new Vector3(MathF.Cos(t) * r, MathF.Sin(t) * r, 0);
                    // a turn is read from the angle about the centre, so go there by the ring's own arc
                    var p0 = _h.Project(A)!.Value; _h.OnPress(p0);
                    for (int i = 1; i <= 16; i++) { float ti = t * i / 16; _h.OnMove(_h.Project(c + new Vector3(MathF.Cos(ti) * r, MathF.Sin(ti) * r, 0))!.Value); }
                    _h.OnRelease(_h.Project(B)!.Value); _wait = 2; break;
                }
                var axis = a[1] switch { "x" => Vector3.UnitX, "y" => Vector3.UnitY, _ => Vector3.UnitZ };
                A = c + axis * g.Size * 0.9f; B = A + axis * d;
                var q0 = _h.Project(A)!.Value; var q1 = _h.Project(B)!.Value;
                _h.OnPress(q0);
                for (int i = 1; i <= 8; i++) _h.OnMove(Vector2.Lerp(q0, q1, i / 8f));
                _h.OnRelease(q1); _wait = 2; break;
            }
            case "select": _h.SelectKey(a[1] == "none" ? null : FormKey.Factory(a[1])); _wait = 2; break;
            case "stage":
                _h.Session.StageDelta(FormKey.Factory(a[1]), P3(a[2]), Vector3.UnitZ, float.DegreesToRadians(F(a[3])));
                _h.AfterStage(); _wait = 2; break;
            case "undo": _h.Session.Undo(); _h.AfterStage(); _wait = 2; break;
            case "pickworld":
            {
                var w = P3(a[1]);
                var s = _h.Project(w);
                if (s is not { } sp) { WorkbenchHost.Say($"pickworld {a[1]}: behind the eye"); break; }
                var hit = _h.PickAt(sp);
                string what = hit?.Shape is { Kind: PickKind.Ref } sh ? $"REFR {sh.Ref.RefKey}" : hit is null ? "nothing" : hit.Shape.Kind.ToString();
                WorkbenchHost.Say(FormattableString.Invariant($"pickworld {a[1]} -> screen {sp.X:0},{sp.Y:0} -> {what}"));
                break;
            }
            case "capture": _h.Capture(a[1]); break;
            case "capturewindow":
            {
                var viewPng = _h.Capture(a[1] + "-view");
                var (w, hh) = WindowCapture.SaveComposite(Shell.Window!, Shell.ViewSlot, viewPng, Path.Combine(_h.OutDir, a[1] + ".png"));
                WorkbenchHost.Say($"window captured as a composite ({w}x{hh}: WPF's render of the window, the scene frame {Path.GetFileName(viewPng)} in the view's place) to {Path.Combine(_h.OutDir, a[1] + ".png")}"); break;
            }
            case "dump": _h.DumpRef(FormKey.Factory(a[1]), a[2]); break;
            case "fps": Fps(int.Parse(a[1])); break;
            case "fpslog":
                if (a.Length > 2 && a[1] == "start") WorkbenchHost.Say(WorkbenchHost.Frames.StartLog(a[2], WorkbenchHost.Now));
                else if (a.Length > 1 && a[1] == "stop") WorkbenchHost.Say(WorkbenchHost.Frames.StopLog());
                else WorkbenchHost.Say("fpslog needs 'start PATH' or 'stop'.");
                break;
            case "fpsshow": _h.ShowFps(a.Length > 1 && a[1] == "on"); break;
            case "hold": Hold(F(a[1])); break;
            case "yawsweep" or "fly": Motion(MotionStep.Parse(a)); break;
            case "tilted":
            {
                // Drawn references with X and Y rotation both non-zero, nearest the eye first: candidates for a
                // composition test on real data.
                int n = a.Length > 1 ? int.Parse(a[1]) : 5;
                foreach (var ro in _h.DrawnRefs().Where(r => MathF.Abs(r.Ref.Rotation.X) > 0.02f && MathF.Abs(r.Ref.Rotation.Y) > 0.02f)
                                                .OrderBy(r => _h.EyeDistance(r.Ref.Position)).Take(n))
                    WorkbenchHost.Say(FormattableString.Invariant(
                        $"tilted {ro.Ref.RefKey} {ro.Ref.BaseEditorId} pos {ro.Ref.Position.X:0.##},{ro.Ref.Position.Y:0.##},{ro.Ref.Position.Z:0.##} rot {ro.Ref.Rotation.X:0.#####},{ro.Ref.Rotation.Y:0.#####},{ro.Ref.Rotation.Z:0.#####} scale {ro.Ref.Scale:0.###} bos {ro.Ref.BosSwapped} shapes {ro.Shapes.Count}"));
                break;
            }
            case "ticks": Ticks(int.Parse(a[1])); break;
            case "commit": Async(() => _h.CommitAsync(a[1] == "dry", a[2])); break;
            case "recheck": Async(() => _h.RecheckAsync(a[1])); break;
            case "repairpreview": Async(async () => (await Shell.Repair.PreviewAsync(Finding(a[1]))).Sentence); break;
            case "repairwrite": Async(async () => (await Shell.Repair.WriteAsync(Finding(a[1]), a[2])).Sentence); break;
            case "expectwritten":
                // M-1: the reference is drawn where the patch puts it (read back through the server), and the status bar says so.
                Async(async () =>
                {
                    var key = FormKey.Factory(a[1]); var plugin = WorkbenchHost.PluginName(a[2]);
                    var w = await _h.WriterAsync();
                    var (pos, _) = await w.ReadPlacementAsync(a[1], source: plugin);
                    var now = _h.Session.Current(key).Position;
                    float d = Vector3.Distance(new Vector3(now.X, now.Y, now.Z), new Vector3(pos.X, pos.Y, pos.Z));
                    bool label = _h.StatusText.Contains($"written to {plugin}, not enabled", StringComparison.Ordinal);
                    bool ok = d <= 0.01f && label;
                    WorkbenchHost.Say(FormattableString.Invariant($"EXPECT {(ok ? "PASS" : "FAIL")}: written {a[1]} {plugin} (got drawn {now.X:0.###},{now.Y:0.###},{now.Z:0.###}; patch {pos.X:0.###},{pos.Y:0.###},{pos.Z:0.###}; {d:0.###} u apart; label in status bar {label})"));
                    if (!ok) Shell.ExpectFailures++;
                    return (string?)null;
                });
                break;
            case "readback":
                Async(async () =>
                {
                    var w = await _h.WriterAsync();
                    var (pos, rot) = await w.ReadPlacementAsync(a[1], source: a[2]);
                    WorkbenchHost.Say(FormattableString.Invariant($"readback {a[1]} from {a[2]}: position {pos.X:R},{pos.Y:R},{pos.Z:R} rotation {rot.X:R},{rot.Y:R},{rot.Z:R}"));
                    return (string?)null;
                });
                break;
            case "conflict":
            {
                int n = int.Parse(a[1]);
                var row = Shell.Visible.FirstOrDefault(r => r.Rank == n);
                if (row is null) { WorkbenchHost.Say($"conflict {n}: no row ranked {n} is listed"); break; }
                Shell.OpenRow(row); _wait = 3; break;
            }
            case "treeref":
            {
                var node = RecordsTree.Walk(Shell.Tree).FirstOrDefault(t => t.Kind == "ref" && string.Equals(t.Key, a[1], StringComparison.OrdinalIgnoreCase));
                if (node is null) { WorkbenchHost.Say($"treeref {a[1]}: the Records tree has no such reference"); break; }
                Shell.CurrentRow = null; Shell.OpenRef(node.Key, Shell.CellOfRef(node.Key)); _wait = 3; break;
            }
            case "showall": Shell.ShowAll = a[1] == "on"; Shell.FillConflicts(); break;
            case "state": Shell.Dump(_h, Path.Combine(_h.OutDir, a[1] + ".json")); break;
            case "msgclickworld":
            {
                var sp = _h.Project(P3(a[1]));
                if (sp is not { } c) { WorkbenchHost.Say($"msgclickworld {a[1]}: behind the eye"); break; }
                WorkbenchHost.Say(FormattableString.Invariant($"msgclickworld {a[1]} -> device pixel {c.X:0},{c.Y:0}"));
                _h.PostMouse(0x201, c); _h.PostMouse(0x202, c); _wait = 3; break;
            }
            case "msgclickref":
            {
                var ro = _h.Session.RefOf(FormKey.Factory(a[1]));
                var c3 = ro.Min.X <= ro.Max.X ? (ro.Min + ro.Max) * 0.5f : new Vector3(ro.Ref.Position.X, ro.Ref.Position.Y, ro.Ref.Position.Z);
                var sp = _h.Project(c3);
                if (sp is not { } c) { WorkbenchHost.Say($"msgclickref {a[1]}: behind the eye"); break; }
                WorkbenchHost.Say(FormattableString.Invariant($"msgclickref {a[1]} -> device pixel {c.X:0},{c.Y:0} (framebuffer {_h.W}x{_h.H})"));
                _h.PostMouse(0x201, c); _h.PostMouse(0x202, c); _wait = 3; break;
            }
            case "msgdragworld":
            {
                var g = _h.Handles;
                if (g.Centre is not { } c) { WorkbenchHost.Say("msgdragworld: nothing selected"); break; }
                var axis = a[1] switch { "x" => Vector3.UnitX, "y" => Vector3.UnitY, _ => Vector3.UnitZ };
                var A = c + axis * g.Size * 0.9f; var B = A + axis * F(a[2]);
                var q0 = _h.Project(A)!.Value; var q1 = _h.Project(B)!.Value;
                WorkbenchHost.Say(FormattableString.Invariant($"msgdragworld {a[1]} {a[2]}: device pixels {q0.X:0},{q0.Y:0} -> {q1.X:0},{q1.Y:0}"));
                _h.PostMouse(0x201, q0);
                for (int i = 1; i <= 8; i++) _h.PostMouse(0x200, Vector2.Lerp(q0, q1, i / 8f));
                _h.PostMouse(0x202, q1); _wait = 3; break;
            }
            case "expect": Expect(a); break;
            case "marks": _h.SetLayer(Layer.Marks, a[1] == "on"); _wait = 2; break;
            case "layer":
            {
                var (step, why) = ViewLayers.ParseStep(a);
                if (step is { } st) _h.SetLayer(st.Layer, st.On); else WorkbenchHost.Say("step refused: " + why);
                _wait = 3; break;
            }
            case "borders":
            {
                var (step, why) = ViewLayers.ParseBorderStep(a);
                if (step is { } st) _h.SetBorderOption(st.Option, st.On); else WorkbenchHost.Say("step refused: " + why);
                _wait = 3; break;
            }
            case "idlecpu": IdleCpu(F(a[1])); break;
            case "waitfile": WaitFile(a[1], a.Length > 2 ? F(a[2]) : 900f); break;
            case "cell":
            {
                var order = SceneDirector.Order ?? throw new InvalidOperationException("this scene's load order was not named in full (instance, profile, game), so another cell cannot be asked for.");
                WorkbenchHost.Say(SceneDirector.Request(new SceneRequest(order, a[1], a.Length > 2 ? a[2] : null, "the script's cell step")));
                _wait = 3; break;
            }
            case "cellon":
            {
                var order = SceneDirector.Order ?? throw new InvalidOperationException("this scene's load order was not named in full (instance, profile, game), so another profile cannot be asked for.");
                WorkbenchHost.Say(SceneDirector.Request(new SceneRequest(order with { Profile = a[1], OrderFile = null, Label = $"profile '{a[1]}'" }, a[2], a.Length > 3 ? a[3] : null, "the script's cellon step")));
                _wait = 3; break;
            }
            case "quit": _h.MainWindow.Close(); break;
            default: WorkbenchHost.Say($"unknown step '{a[0]}'"); break;
        }
    }

    void Expect(string[] a)
    {
        bool ok; string got;
        var s = _h.Session;
        switch (a[1])
        {
            case "selected":
                got = s.Selected?.ToString() ?? "none"; ok = string.Equals(got, a[2], StringComparison.OrdinalIgnoreCase); break;
            case "staged":
                got = s.Pending.Count.ToString(CultureInfo.InvariantCulture); ok = got == a[2]; break;
            case "moved":
            {
                var k = FormKey.Factory(a[2]); var want = P3(a[3]); float tol = F(a[4]);
                var now = s.Current(k).Position; var built = RefPlacementEdit.BakedPose(s.RefOf(k).Ref).Position;
                var d = new Vector3(now.X - built.X, now.Y - built.Y, now.Z - built.Z);
                got = FormattableString.Invariant($"{d.X:0.###},{d.Y:0.###},{d.Z:0.###}");
                ok = Vector3.Distance(d, want) <= tol; break;
            }
            case "landed":
            {
                // Landed: the reference is selected and the screen-centre ray crosses its own shapes (the camera is framed on
                // it). What the ray meets first is reported, not required: an overlap row's reference is often inside another.
                var st = _h.SceneStateCentre();
                var want = a.Length > 2 ? a[2] : s.Selected?.ToString();
                bool crosses = want is not null && FormKey.TryFactory(want, out var wk) && s.IsDrawn(wk) && _h.PickOwn(new Vector2(_h.W * 0.5f, _h.H * 0.5f), wk) is not null;
                got = $"selected {s.Selected?.ToString() ?? "none"}, centre ray crosses it {crosses}, first thing it meets {st ?? "nothing"}";
                if (want is not null && FormKey.TryFactory(want, out var wk2) && s.IsDrawn(wk2))
                    got += $" [own shapes {s.RefOf(wk2).Shapes.Count}, scene pick shapes of that key {_h.ScenePickShapesOf(wk2)}]";
                ok = want is not null && string.Equals(s.Selected?.ToString(), want, StringComparison.OrdinalIgnoreCase) && crosses;
                break;
            }
            case "marked":
            {
                // Mark-coloured pixels inside the selected (cyan) and, when a partner is marked, the partner's (magenta)
                // projected box, read from this frame's back buffer before the swap.
                var m = _h.Marks;
                if (m.Selected is null) { got = "nothing selected"; ok = false; break; }
                var sc = _h.CountMarked(m.Selected, (r, g, b) => r < 80 && g > 200 && b > 200);
                got = $"selected {m.Selected.Ref.RefKey}: {sc.Marked} cyan of {sc.Box} pixels in its projected box";
                ok = sc.Marked > 0;
                if (m.Partner is { } p)
                {
                    var pc = _h.CountMarked(p, (r, g, b) => r > 200 && g < 80 && b > 200);
                    got += $"; partner {p.Ref.RefKey}: {pc.Marked} magenta of {pc.Box}";
                    ok &= pc.Marked > 0;
                }
                else got += "; no partner marked";
                break;
            }
            case "recheck":
                got = _h.LastRecheck is { } lr ? lr.Row.Word : _h.RecheckRefusal is { } rr ? "refused: " + rr : "none";
                ok = string.Equals(_h.LastRecheck?.Row.Word, string.Join(' ', a.Skip(2)), StringComparison.Ordinal); break;
            case "created":
                got = _h.LastRecheck is { } lc ? (lc.New.Count == 0 ? "no new rows" : string.Join("; ", lc.New.Select(n => n.Id))) : "no re-check";
                ok = _h.LastRecheck?.New.Any(n => n.Id.Contains(a[2], StringComparison.OrdinalIgnoreCase)) == true; break;
            case "inview":
            {
                var (inView, account) = _h.InView(FormKey.Factory(a[2]));
                ok = inView; got = account; break;
            }
            case "cell":
                got = $"{_h.SceneCell} ({_h.SceneLabel})";
                ok = string.Equals(_h.SceneCell, a[2], StringComparison.OrdinalIgnoreCase) || string.Equals(_h.SceneLabel, a[2], StringComparison.OrdinalIgnoreCase);
                break;
            case "partner":
                got = _h.Marks.Partner?.Ref.RefKey.ToString() ?? "none"; ok = string.Equals(got, a[2], StringComparison.OrdinalIgnoreCase);
                if (_h.PartnerNote is { } pn) got += "; " + pn;
                break;
            default: got = "unknown expectation"; ok = false; break;
        }
        if (!ok) Shell.ExpectFailures++;
        WorkbenchHost.Say($"EXPECT {(ok ? "PASS" : "FAIL")}: {string.Join(' ', a.Skip(1))} (got {got})");
    }

    /// <summary>CPU over a quiet window, by QueryProcessCycleTime (GetProcessTimes undercounts on this machine), then a
    /// one-second busy spin on a fresh thread read by QueryThreadCycleTime as the cycles of one fully busy core.</summary>
    void IdleCpu(float seconds)
    {
        _busy = true; _quiet = true;
        var proc = System.Diagnostics.Process.GetCurrentProcess().Handle;
        QueryProcessCycleTime(proc, out ulong c0);
        var t0 = _h.Clock.Elapsed.TotalSeconds;
        int frames0 = _h.FrameCount;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            QueryProcessCycleTime(proc, out ulong c1);
            double el = _h.Clock.Elapsed.TotalSeconds - t0;
            int frames = _h.FrameCount - frames0;
            ulong busy = 0;
            var th = new System.Threading.Thread(() =>
            {
                var me = GetCurrentThread();
                QueryThreadCycleTime(me, out ulong b0);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < 1.0) { }
                QueryThreadCycleTime(me, out ulong b1);
                busy = (ulong)((b1 - b0) / sw.Elapsed.TotalSeconds);
            });
            th.Start(); th.Join();
            double cores = (c1 - c0) / el / busy;
            WorkbenchHost.Say(string.Create(CultureInfo.InvariantCulture,
                $"idlecpu: {el:0.0} s quiet, {frames} frames drawn; process cycles {c1 - c0:N0}; one busy core {busy:N0} cycles/s; "
                + $"= {cores * 100:0.0} % of one core; loop {Environment.GetEnvironmentVariable("WB_LOOP") ?? "demand"}"));
            _busy = false; _quiet = false;
            _h.Invalidate();
        };
        timer.Start();
    }

    /// <summary>Wait without asking for frames: what the view draws meanwhile is what the loop draws on its own.</summary>
    void Hold(float seconds)
    {
        _busy = true; _quiet = true;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => { timer.Stop(); _busy = false; _quiet = false; WorkbenchHost.Say(FormattableString.Invariant($"held {seconds:0.#} s")); _h.Invalidate("script"); };
        timer.Start();
    }

    /// <summary>A motion step's messages, posted to the view's own window; the script asks for no frames meanwhile, so
    /// the frames drawn are the ones the input causes.</summary>
    void Motion(MotionStep m)
    {
        _busy = true; _quiet = true;
        var (delay, hz) = MotionStep.Typematic();
        var msgs = m.Messages(_h.W / 2, _h.H / 2, delay, hz);
        WorkbenchHost.Say(FormattableString.Invariant($"{m.Kind} {m.Seconds:0.#} s: {msgs.Count} messages") + (m.Kind == "fly" ? FormattableString.Invariant($", key {m.Key}, repeat delay {delay:0} ms, rate {hz:0.#} a second") : FormattableString.Invariant($", {m.DegPerSecond:0.#} degrees a second at {m.Hz:0} messages a second")));
        var d = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        MotionStep.Post(_h.ViewHandle, msgs, said => d.BeginInvoke(() => { WorkbenchHost.Say(m.Kind + ": " + said); _busy = false; _quiet = false; _h.Invalidate("script"); }));
    }

    /// <summary>Hold the script (no frames asked for) until a file exists: the window stays live for an MCP client.</summary>
    void WaitFile(string path, float maxSeconds)
    {
        _busy = true; _quiet = true;
        var t0 = _h.Clock.Elapsed.TotalSeconds;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            double el = _h.Clock.Elapsed.TotalSeconds - t0;
            if (!File.Exists(path) && el < maxSeconds) return;
            timer.Stop();
            WorkbenchHost.Say(string.Create(CultureInfo.InvariantCulture, $"waitfile {path}: {(File.Exists(path) ? "found" : "NOT found")} after {el:0.0} s"));
            _busy = false; _quiet = false; _h.Invalidate();
        };
        timer.Start();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool QueryProcessCycleTime(IntPtr process, out ulong cycles);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();

    void Async(Func<Task<string?>> f)
    {
        _busy = true;
        f().ContinueWith(t =>
        {
            if (t.Exception is { } ex) WorkbenchHost.Say("step refused: " + ex.GetBaseException().Message);
            _busy = false;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void Fps(int n)
    {
        _busy = true;
        var stamps = new List<double>(n + 1);
        var gpu = new List<double>(n);
        bool fg = _h.IsForeground;
        _h.TickStamps = new List<double>();
        var calls = new List<double>(n + 1); var swaps = new List<double>(n + 1);
        _h.AfterFrame = () =>
        {
            calls.Add(WorkbenchHost.Frames.LastRenderMs); swaps.Add(WorkbenchHost.Frames.LastSwapMs);
            var t0 = _h.Clock.Elapsed.TotalMilliseconds;
            OpenTK.Graphics.OpenGL4.GL.Finish();
            gpu.Add(_h.Clock.Elapsed.TotalMilliseconds - t0);
            stamps.Add(_h.Clock.Elapsed.TotalMilliseconds);
            if (stamps.Count <= n) return;
            _h.AfterFrame = null;
            var ticks = _h.TickStamps!; _h.TickStamps = null;
            var tick = new List<double>(); for (int i = 1; i < ticks.Count; i++) tick.Add(ticks[i] - ticks[i - 1]);
            var per = new List<double>();
            for (int i = 1; i < stamps.Count; i++) per.Add(stamps[i] - stamps[i - 1]);
            per.Sort();
            double total = stamps[^1] - stamps[0];
            WorkbenchHost.Say(string.Create(CultureInfo.InvariantCulture,
                $"fps: {per.Count} frames in {total:0.0} ms = {per.Count * 1000.0 / total:0.0} fps mean; frame ms p50 {per[per.Count / 2]:0.00} p95 {per[(int)(per.Count * 0.95)]:0.00} max {per[^1]:0.00}; "
                + $"foreground at start {fg}, at end {_h.IsForeground}; primary monitor refresh {WorkbenchHost.RefreshRate()} Hz; framebuffer {_h.W}x{_h.H}; "
                + $"GL.Finish wait after the swap p50 {Median(gpu):0.00} ms; draw p50 {Median(calls):0.00} ms; SwapBuffers p50 {Median(swaps):0.00} ms; vsync {(_h.Vsync ? "on" : "off")}; loop {Environment.GetEnvironmentVariable("WB_LOOP") ?? "cap60"}; "
                + $"WPF CompositionTarget.Rendering ticks {ticks.Count} in the window, interval p50 {(tick.Count > 0 ? Median(tick) : double.NaN):0.00} ms"));
            _busy = false;
        };
    }

    /// <summary>WPF's frame tick with the GL control NOT rendering: whether the window's pace is WPF's own or the draw's.</summary>
    void Ticks(int n)
    {
        _busy = true;
        _h.Paused = true;
        var stamps = new List<double>();
        EventHandler? h = null;
        h = (_, _) =>
        {
            stamps.Add(_h.Clock.Elapsed.TotalMilliseconds);
            if (stamps.Count <= n) return;
            System.Windows.Media.CompositionTarget.Rendering -= h;
            var per = new List<double>(); for (int i = 1; i < stamps.Count; i++) per.Add(stamps[i] - stamps[i - 1]);
            WorkbenchHost.Say(string.Create(CultureInfo.InvariantCulture,
                $"ticks with GL rendering stopped: {per.Count} intervals, p50 {Median(per):0.00} ms; WPF render tier {System.Windows.Media.RenderCapability.Tier >> 16} (2 = hardware); "
                + $"process render mode {System.Windows.Media.RenderOptions.ProcessRenderMode}"));
            _h.Paused = false;
            _busy = false;
        };
        System.Windows.Media.CompositionTarget.Rendering += h;
    }

    static double Median(List<double> v) { var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
}
