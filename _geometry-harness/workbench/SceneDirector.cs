using System.Diagnostics;
using System.Windows.Threading;
using ViewProbe;

namespace Workbench;

/// <summary>The load order a scene is built on, as viewprobe takes it: an MO2 instance, profile and game folder, and
/// optionally a candidate order file (<c>--order</c>). <see cref="Label"/> is what the window calls it.</summary>
internal sealed record LoadOrderSpec(string Instance, string Profile, string Game, string? OrderFile, string Label)
{
    /// <summary>Draw the navmesh in scenes built on this order (viewprobe decides it once per run, so it is part of the key).</summary>
    internal bool Navmesh { get; init; } = true;
    /// <summary>Two specs with one key build the same scene for a cell.</summary>
    internal string Key => string.Join("|", Full(Instance), Profile, Full(Game), OrderFile is null ? "" : Full(OrderFile), Navmesh ? "nav" : "no-nav").ToLowerInvariant();
    static string Full(string p) => Path.GetFullPath(p).TrimEnd('\\', '/');

    internal IEnumerable<string> Args()
    {
        yield return "--instance"; yield return Instance;
        yield return "--profile"; yield return Profile;
        yield return "--game"; yield return Game;
        if (OrderFile is not null) { yield return "--order"; yield return OrderFile; }
        if (!Navmesh) { yield return "--nav"; yield return "off"; }
    }

    /// <summary>The spec viewprobe's own arguments name, or null when they leave a part to the environment.</summary>
    internal static LoadOrderSpec? FromArgs(IReadOnlyList<string> args, string label)
    {
        string? V(string n) { for (int i = 0; i + 1 < args.Count; i++) if (args[i] == n) return args[i + 1]; return null; }
        return V("--instance") is { } i && V("--profile") is { } p && V("--game") is { } g
            ? new LoadOrderSpec(i, p, g, V("--order"), label) { Navmesh = V("--nav") != "off" } : null;
    }
}

/// <summary>One cell to show: its load order, the cell as viewprobe names it (a FormKey), the reference to land on, and
/// the words the banner uses. A read-only scene has no move handles and writes nothing.</summary>
internal sealed record SceneRequest(LoadOrderSpec Order, string Cell, string? LandOn, string Why, bool ReadOnly = true)
{
    /// <summary>Called on the UI thread once the scene for this request is shown.</summary>
    internal Action? Shown { get; init; }
    /// <summary>Build the cell again even when it is the scene shown (a layer it was built without was asked for).</summary>
    internal bool Reload { get; init; }
}

/// <summary>The one place a scene is asked for. A request reaches viewprobe in one of three ways: the idle loop in
/// <see cref="Program"/> starts viewprobe on it; a scene on the same load order hands viewprobe the cell and closes its
/// frame; a scene on another load order closes and viewprobe is started again. A request made while a scene is being
/// built abandons that build at its next tick.</summary>
internal static class SceneDirector
{
    static SceneRequest? _pending;
    static bool _abandon;
    static long _lastPump = Stopwatch.GetTimestamp();
    static DispatcherFrame? _idle;

    /// <summary>The load order and request of the viewprobe run in progress; null when viewprobe is not running.</summary>
    internal static LoadOrderSpec? Order { get; private set; }
    internal static SceneRequest? Showing { get; private set; }
    /// <summary>True from the start of a viewprobe run to its return.</summary>
    internal static bool Running { get; private set; }
    /// <summary>True while viewprobe builds a scene: it is running and no scene is shown.</summary>
    internal static bool Building => Running && Shell.Current is null;
    /// <summary>The cell a build in progress is for, or null.</summary>
    internal static string? BuildingCell { get; private set; }

    // ---- what the build's ticks measured (the window's answer time while a scene loads)
    internal static double LongestGapMs { get; private set; }
    internal static int Pumps { get; private set; }
    internal static double LastBuildSeconds { get; private set; }
    internal static double LastBuildLongestGapMs { get; private set; }
    static readonly Stopwatch BuildClock = new();

    /// <summary>The pump interval: the window is answered at most this often while a scene builds.</summary>
    internal const double PumpEveryMs = 30;

    internal static bool HasPending => _pending is not null;
    /// <summary>Requests answered by the scene already shown (its own cell, or one drawn beside it), with no rebuild.</summary>
    internal static int Reused { get; private set; }
    /// <summary>Why the last request on an open scene rebuilt instead of reusing it; null when it reused it.</summary>
    internal static string? LastRebuildWhy { get; private set; }

    /// <summary>Take the request waiting for a new viewprobe run.</summary>
    internal static SceneRequest? Take() { var r = _pending; _pending = null; return r; }

    /// <summary>Ask for a scene. Returns one sentence saying what happens; a refusal is an <see cref="InvalidOperationException"/>.</summary>
    internal static string Request(SceneRequest req)
    {
        Requested?.Invoke(req);
        if (Shell.Current is { } h)
        {
            if (h.Session.Pending.Count > 0)
                throw new InvalidOperationException($"opening cell {req.Cell} would drop {h.Session.Pending.Count} staged edit(s); write or undo them first.");
            bool same = Order is not null && Order.Key == req.Order.Key;
            bool shown = string.Equals(h.SceneCell, req.Cell, StringComparison.OrdinalIgnoreCase) || string.Equals(h.SceneLabel, req.Cell, StringComparison.OrdinalIgnoreCase);
            // a cell this scene already draws beside its own, on the same load order: land there, no rebuild
            string? partner = req.LandOn is { } lo ? Shell.PartnerFor?.Invoke(lo) : null;
            LastRebuildWhy = shown ? null : !same ? "another load order" : req.Reload ? "a layer it was built without was asked for"
                           : h.ReadOnly != req.ReadOnly ? "the scene's read-only state differs" : h.WhyNotShown(req.Cell, req.LandOn, partner);
            bool beside = !shown && LastRebuildWhy is null;
            if (same && !req.Reload && h.ReadOnly == req.ReadOnly && (shown || beside))
            {
                Showing = req; Reused++;
                if (req.LandOn is { } k && Mutagen.Bethesda.Plugins.FormKey.TryFactory(k, out var key)) h.Land(key);
                req.Shown?.Invoke();
                return (shown ? $"cell {req.Cell} is the scene shown" : $"cell {req.Cell} is already drawn beside cell {h.SceneLabel} on this load order, so the scene is not rebuilt")
                       + (req.LandOn is null ? "" : $"; {Shell.BannerText}");
            }
            Shell.PendingLand = req.LandOn; Shell.SwitchCell = req.Cell; Shell.SwitchClock = Stopwatch.StartNew();
            Shell.CurrentRow = null; Shell.ReadOnlyScene = req.ReadOnly;
            string said;
            if (same)
            {
                InteractiveViewer.CellRequest = req.Cell; InteractiveViewer.CellRequestNav = ViewLayers.NavFor(req.Order); _next = req;
                said = $"loading cell {req.Cell} ({req.Why}); picking another cell stops this load.";
            }
            else
            {
                _pending = req;
                said = $"loading cell {req.Cell} on {req.Order.Label} ({req.Why}): the scene is rebuilt on that load order; picking another cell stops this load.";
            }
            Shell.SetBanner(said);
            Shell.Flush();
            h.StopFrame();
            return said;
        }
        if (Running)
        {
            // a build is running: it is abandoned at its next tick, for this request
            bool same = Order is not null && Order.Key == req.Order.Key;
            if (same) { InteractiveViewer.CellRequest = req.Cell; InteractiveViewer.CellRequestNav = ViewLayers.NavFor(req.Order); _next = req; _pending = null; }
            else { InteractiveViewer.CellRequest = null; InteractiveViewer.CellRequestNav = null; _next = null; _pending = req; }
            _abandon = true; Shell.ReadOnlyScene = req.ReadOnly;
            Shell.PendingLand = req.LandOn; Shell.SwitchCell = req.Cell; Shell.SwitchClock = Stopwatch.StartNew();
            var said = $"stopping the load of cell {BuildingCell} to open cell {req.Cell} ({req.Why}).";
            Shell.SetBanner(said);
            return said;
        }
        _pending = req;
        Shell.PendingLand = req.LandOn; Shell.SwitchCell = req.Cell; Shell.SwitchClock = Stopwatch.StartNew();
        var s = $"loading cell {req.Cell} on {req.Order.Label} ({req.Why}); picking another cell stops this load.";
        Shell.SetBanner(s);
        Wake();
        return s;
    }
    /// <summary>Raised for every request before it is acted on (the load-order panel follows the scene's load order).</summary>
    internal static event Action<SceneRequest>? Requested;
    static SceneRequest? _next;      // the request the running viewprobe continues with (same load order)

    /// <summary>End the idle wait in <see cref="Program"/> (a request or a finished launcher run is ready).</summary>
    internal static void Wake() { if (_idle is { } f) f.Continue = false; }

    /// <summary>Wait, answering the window, until something asks for a scene or the window closes.</summary>
    internal static void WaitIdle()
    {
        if (HasPending || Shell.Closed) return;
        _idle = new DispatcherFrame();
        Dispatcher.PushFrame(_idle);
        _idle = null;
    }

    /// <summary>viewprobe's arguments for a request: a one-cell worklist under the work folder and the request's load order.</summary>
    internal static string[] ArgsFor(SceneRequest req, string work)
    {
        Directory.CreateDirectory(work);
        var wl = new HarnessShared.WorklistFile("workbench: the cell asked for",
            new[] { new HarnessShared.WorklistCell(req.Cell, new[] { new HarnessShared.WorklistShot(
                "placeholder pose: the workbench frames the camera once the cell is built", new[] { 0f, 0f, 0f }) { Nav = ViewLayers.NavFor(req.Order) } }) }.ToList());
        var path = Path.Combine(work, "workbench-request-worklist.json");
        HarnessShared.WorklistIo.Write(path, wl);
        return new[] { "--worklist", path }.Concat(req.Order.Args()).Concat(new[] { "--out", work }).ToArray();
    }

    /// <summary>A viewprobe run starts on this load order (null: one the command line or the launcher named in full).</summary>
    internal static void Began(LoadOrderSpec? order, SceneRequest? first)
    {
        Running = true; Order = order; Showing = first; _next = null; _abandon = false;
        BuildingCell = first?.Cell; Shell.ReadOnlyScene = first?.ReadOnly ?? false;
        BuildStarted();
    }

    internal static void Ended() { Running = false; Order = null; Showing = null; BuildingCell = null; _abandon = false; Loading.Clear("scene"); }

    static void BuildStarted() { BuildClock.Restart(); LongestGapMs = 0; Pumps = 0; _lastPump = Stopwatch.GetTimestamp(); ShowProgress(); }

    /// <summary>The loading strip's line for the build: its cell, and the stage the build's run record is in, with that
    /// stage's share done when the stage knows its total.</summary>
    static void ShowProgress()
    {
        var stage = HousecarlCore.Progress.RunLog.Current?.Record.Stages.LastOrDefault();
        var cell = BuildingCell ?? Shell.SwitchCell;
        // the build's stages in a modder's words; a stage this list does not know keeps the build's own title
        var words = stage?.Id switch
        {
            RenderStages.FindCell => "finding the cell",
            RenderStages.RecordWalk => "reading the plugins",
            RenderStages.SceneBuild => "building the meshes and textures",
            RenderStages.Report or RenderStages.Encode => "finishing",
            _ => stage?.Title,
        };
        var what = (cell is null ? "Loading the scene" : $"Loading cell {cell}") + (words is { Length: > 0 } t ? ": " + t : "");
        double? f = stage is { Total: > 0 } st ? (double)st.Processed / st.Total.Value : null;
        if (stage is { Total: > 0 } s2) what += $" ({s2.Processed:N0} of {s2.Total:N0} {s2.Unit})";
        Loading.Set("scene", what, f);
    }

    /// <summary>A scene arrived (the host calls this before its frame loop): the request it answers is the one shown.</summary>
    internal static void SceneShown()
    {
        if (_next is { } n) { Showing = n; _next = null; }
        LastBuildSeconds = BuildClock.Elapsed.TotalSeconds; LastBuildLongestGapMs = LongestGapMs;
        BuildClock.Stop();
        BuildingCell = null;
        Loading.Clear("scene");
        WorkbenchHost.Say(FormattableString.Invariant(
            $"scene build: {LastBuildSeconds:0.0} s; the window was answered {Pumps} time(s) meanwhile, longest wait {LastBuildLongestGapMs:0} ms"));
        Showing?.Shown?.Invoke();
    }

    /// <summary>A scene closed for another cell of the same run: the next build's clock starts.</summary>
    internal static void SceneClosed()
    {
        if (!Running || Shell.Closed) return;
        BuildingCell = _next?.Cell ?? Shell.SwitchCell;
        BuildStarted();
    }

    /// <summary>viewprobe's tick while it builds (<see cref="RenderStages.Tick"/>): answer the window every
    /// <see cref="PumpEveryMs"/>, and abandon the build when another request, or the window's close, asks.</summary>
    internal static void Tick()
    {
        if (!Running || Shell.Current is not null || Shell.Window is null) return;
        long now = Stopwatch.GetTimestamp();
        double since = (now - _lastPump) * 1000.0 / Stopwatch.Frequency;
        if (since < PumpEveryMs) return;
        if (since > LongestGapMs) LongestGapMs = since;
        Pumps++;
        ShowProgress();
        Shell.Flush();
        _lastPump = Stopwatch.GetTimestamp();
        if (_abandon || Shell.Closed)
        {
            _abandon = false;
            if (Shell.Closed) { InteractiveViewer.CellRequest = null; InteractiveViewer.CellRequestNav = null; _next = null; }
            if (_next is { } n) { BuildingCell = n.Cell; BuildStarted(); }
            throw new BuildAbandoned();
        }
    }

    /// <summary>The <c>scene</c> part of the state: what is shown or loading, on which load order, and the last build's times.</summary>
    internal static object State() => new
    {
        running = Running, building = Building, buildingCell = BuildingCell,
        loadOrder = Order?.Label, orderFile = Order?.OrderFile, profile = Order?.Profile,
        showing = Showing is null ? null : new { cell = Showing.Cell, landOn = Showing.LandOn, why = Showing.Why, readOnly = Showing.ReadOnly },
        pending = _pending?.Cell ?? _next?.Cell,
        reused = Reused, lastRebuildWhy = LastRebuildWhy, lastBuildSeconds = LastBuildSeconds, lastBuildLongestWaitMs = LastBuildLongestGapMs, lastBuildPumps = Pumps,
        pumpEveryMs = PumpEveryMs,
    };
}
