using System.Numerics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using ViewProbe;

namespace Workbench;

/// <summary>What a press, move and release in the 3D view do: a press that moves no more than
/// <see cref="ClickSlopPx"/> pixels is a click and only selects; a press on the handle that moves further drags it and
/// stages one edit at release. The window's mouse messages and the headless script both call these three methods; the
/// camera is reached only through the three functions given, so a test can drive it with a synthetic camera.</summary>
internal sealed class ViewInput
{
    /// <summary>Jitter a click may have, in framebuffer (device) pixels, and still be a click.</summary>
    internal const float ClickSlopPx = 3f;

    readonly EditSession _session;
    readonly Gizmo _gizmo;
    readonly Func<Vector3, Vector2?> _project;
    readonly Func<Vector2, (Vector3 O, Vector3 D)> _ray;
    readonly Func<Vector2, PickHit?> _pick;
    readonly Action _afterSelect;

    Vector2? _press; Handle _dragHandle; RefPlacementEdit.Pose _dragStart; bool _moved;

    /// <summary>Snapping applied to a drag's result: the grid in units and the turn step in degrees (0 = off).</summary>
    internal float SnapStep, AngleStep;
    /// <summary>True while the view shows originals: a drag is refused then, and says so.</summary>
    internal Func<bool> Holding = () => false;
    internal Action<string>? Refused;

    internal ViewInput(EditSession session, Gizmo gizmo, Func<Vector3, Vector2?> project, Func<Vector2, (Vector3 O, Vector3 D)> ray,
                       Func<Vector2, PickHit?> pick, Action afterSelect)
    { _session = session; _gizmo = gizmo; _project = project; _ray = ray; _pick = pick; _afterSelect = afterSelect; }

    internal void OnPress(Vector2 px)
    {
        _press = px; _moved = false;
        _dragHandle = _session.Selected is not null ? _gizmo.HitTest(px, _project) : Handle.None;
        if (_dragHandle != Handle.None && Holding()) { Refused?.Invoke("release Space (or the Original button) to edit; the view is showing originals"); _dragHandle = Handle.None; }
        if (_dragHandle != Handle.None) _dragStart = _session.Current(_session.Selected!.Value);
        _gizmo.Hot = _dragHandle;
    }

    internal void OnMove(Vector2 px)
    {
        if (_press is not { } p0) { _gizmo.Hot = _session.Selected is not null ? _gizmo.HitTest(px, _project) : Handle.None; return; }
        if (Vector2.Distance(p0, px) > ClickSlopPx) _moved = true;
        if (_dragHandle == Handle.None || !_moved) return;
        _session.Preview(_session.Selected!.Value, DragPose(p0, px));
    }

    internal void OnRelease(Vector2 px)
    {
        if (_press is not { } p0) return;
        _press = null;
        if (Vector2.Distance(p0, px) > ClickSlopPx) _moved = true;
        if (_dragHandle != Handle.None && _moved)
        {
            var key = _session.Selected!.Value;
            _session.Stage(key, DragPose(p0, px));
            _gizmo.Centre = ToV(_session.Current(key).Position);
        }
        else if (!_moved && _dragHandle == Handle.None) SelectAt(px);   // a click on the handle itself keeps the selection
        _dragHandle = Handle.None; _gizmo.Hot = Handle.None;
    }

    RefPlacementEdit.Pose DragPose(Vector2 from, Vector2 to)
    {
        var s = _dragStart;
        if (_dragHandle is Handle.TurnZ or Handle.TurnX or Handle.TurnY)
        {
            var turnAxis = Gizmo.TurnAxis(_dragHandle);
            float rad = float.DegreesToRadians(MoveRules.SnapTurn(float.RadiansToDegrees(_gizmo.TurnAmountAbout(turnAxis, from, to, _ray)), AngleStep));
            return s with { Rotation = RefPlacementEdit.Turned(s.Rotation, turnAxis, rad) };
        }
        float amount = _gizmo.MoveAmount(_dragHandle, from, to, _ray);
        var axis = _dragHandle switch { Handle.MoveX => Vector3.UnitX, Handle.MoveY => Vector3.UnitY, _ => Vector3.UnitZ };
        var np = ToV(s.Position) + axis * amount;
        return MoveRules.Snap(s, s with { Position = new Vec3(np.X, np.Y, np.Z) }, SnapStep, 0f);
    }

    void SelectAt(Vector2 px)
    {
        var hit = _pick(px);
        if (hit?.Shape is { Kind: PickKind.Ref } sh && _session.IsDrawn(sh.Ref.RefKey)) Select(sh.Ref.RefKey);
        else Select(null);
    }

    internal void Select(FormKey? key)
    {
        _session.Select(key);
        _gizmo.Centre = key is { } k ? ToV(_session.Current(k).Position) : null;
        _afterSelect();
    }

    static Vector3 ToV(Vec3 v) => new(v.X, v.Y, v.Z);
}
