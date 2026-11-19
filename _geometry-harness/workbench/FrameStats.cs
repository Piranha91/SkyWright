using System.Diagnostics;
using System.Globalization;
using System.Text;
using OpenTK.Graphics.OpenGL4;

namespace Workbench;

/// <summary>One drawn frame: when it was presented, how long since the previous present, and where the time went.</summary>
internal readonly record struct FrameSample(
    long Frame, double TMs, double IntervalMs, double RenderMs, double SwapMs, double GpuMs, string Loop, bool Resumed,
    string Dirty, double DirtyAgeMs, int Messages, int Gc0, int Gc1, int Gc2, float Yaw, float Pitch, float EyeX, float EyeY, float EyeZ, int W, int H);

/// <summary>Frame intervals summed up: the median, the 1% low (the mean of the slowest 1% of intervals, at least one),
/// the worst, and how many were longer than 25 ms and 50 ms.</summary>
internal readonly record struct IntervalSummary(int Count, double TotalMs, double MedianMs, double Low1Ms, double WorstMs, int Over25, int Over50)
{
    internal double MeanFps => TotalMs > 0 ? Count * 1000.0 / TotalMs : 0;
    internal string Words() => Count == 0 ? "no frames" : string.Create(CultureInfo.InvariantCulture,
        $"{Count} frames over {TotalMs / 1000.0:0.0} s ({MeanFps:0.0} fps mean); interval median {MedianMs:0.00} ms ({1000 / MedianMs:0} fps), 1% low {Low1Ms:0.00} ms ({1000 / Low1Ms:0} fps), worst {WorstMs:0.00} ms; {Over25} over 25 ms, {Over50} over 50 ms");

    internal static IntervalSummary Of(IReadOnlyList<double> intervals)
    {
        if (intervals.Count == 0) return default;
        var s = intervals.OrderBy(x => x).ToArray();
        int slow = Math.Max(1, s.Length / 100);
        double low = 0; for (int i = s.Length - slow; i < s.Length; i++) low += s[i];
        return new IntervalSummary(s.Length, s.Sum(), s[s.Length / 2], low / slow, s[^1], s.Count(x => x > 25), s.Count(x => x > 50));
    }
}

/// <summary>The view's frame times: a rolling window for the on-screen readout, and the per-frame CSV log
/// (<c>WB_FPS_LOG</c>, the script's <c>fpslog</c> step, the state tools' <c>frame_log</c> verb).</summary>
internal sealed class FrameStats
{
    internal const string CsvHeader = "frame,t_ms,interval_ms,render_ms,swap_ms,gpu_ms,loop,resumed,dirty,dirty_age_ms,messages,gc0,gc1,gc2,yaw,pitch,eye_x,eye_y,eye_z,fb_w,fb_h";
    const double WindowMs = 2000;

    readonly Queue<FrameSample> _recent = new();
    StreamWriter? _log;
    string? _logPath;
    readonly List<double> _logIntervals = new();
    int _logResumed;
    double _logStart;

    /// <summary>The last frame's render-call and swap times (what the script's fps step reads).</summary>
    internal double LastRenderMs { get; private set; }
    internal double LastSwapMs { get; private set; }
    internal double LastPresentMs { get; private set; } = -1;
    internal bool Logging => _log is not null;
    internal string? LogPath => _logPath;

    /// <summary>Whether the readout is shown: on in a Debug build, else by <c>WB_FPS=on</c> (or <c>--fps</c>); the window's toggle flips it.</summary>
    internal static bool ShowByDefault =>
#if DEBUG
        Environment.GetEnvironmentVariable("WB_FPS") != "off";
#else
        Environment.GetEnvironmentVariable("WB_FPS") is "on" or "1";
#endif

    /// <summary>The first frame after the view sat with nothing to draw for more than 100 ms: its interval is the pause, not a frame time.</summary>
    internal static bool AfterIdle(FrameSample s) => s.Resumed && s.IntervalMs - s.DirtyAgeMs > 100;

    internal void Add(FrameSample s)
    {
        LastRenderMs = s.RenderMs; LastSwapMs = s.SwapMs; LastPresentMs = s.TMs;
        _recent.Enqueue(s);
        while (_recent.Count > 0 && _recent.Peek().TMs < s.TMs - WindowMs) _recent.Dequeue();
        if (_log is null) return;
        if (AfterIdle(s)) _logResumed++; else if (s.IntervalMs > 0) _logIntervals.Add(s.IntervalMs);
        _log.WriteLine(Csv(s, s.TMs - _logStart));
    }

    internal static string Csv(FrameSample s, double t) => string.Create(CultureInfo.InvariantCulture,
        $"{s.Frame},{t:0.000},{s.IntervalMs:0.000},{s.RenderMs:0.000},{s.SwapMs:0.000},{(double.IsNaN(s.GpuMs) ? "" : s.GpuMs.ToString("0.000", CultureInfo.InvariantCulture))},{s.Loop},{(s.Resumed ? 1 : 0)},{s.Dirty},{s.DirtyAgeMs:0.000},{s.Messages},{s.Gc0},{s.Gc1},{s.Gc2},{s.Yaw:0.###},{s.Pitch:0.###},{s.EyeX:0.#},{s.EyeY:0.#},{s.EyeZ:0.#},{s.W},{s.H}");

    /// <summary>The readout's words over the last two seconds, or "idle" when no frame was drawn in the last half second.</summary>
    internal string Readout(double nowMs, string loop, bool vsync)
    {
        string tail = $"  |  {loop}  |  vsync {(vsync ? "on" : "off")}";
        if (_recent.Count == 0 || nowMs - LastPresentMs > 500) return "idle (nothing to draw)" + tail;
        var iv = _recent.Where(r => !AfterIdle(r) && r.IntervalMs > 0 && r.TMs >= nowMs - WindowMs).Select(r => r.IntervalMs).ToList();
        if (iv.Count < 2) return "drawing..." + tail;
        var sum = IntervalSummary.Of(iv);
        return string.Create(CultureInfo.InvariantCulture, $"{1000 / sum.MedianMs:0} fps  {sum.MedianMs:0.0} ms  1% low {sum.Low1Ms:0.0} ms  worst {sum.WorstMs:0.0} ms") + tail;
    }

    /// <summary>Start writing one CSV row per frame to <paramref name="path"/>; a log already running is stopped first.</summary>
    internal string StartLog(string path, double nowMs)
    {
        string? before = _log is not null ? StopLog() : null;
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _log = new StreamWriter(full, append: false, new UTF8Encoding(false), 1 << 16);
        _log.WriteLine(CsvHeader);
        _logPath = full; _logIntervals.Clear(); _logResumed = 0; _logStart = nowMs;
        return (before is null ? "" : before + "; ") + $"frame log started: {full}";
    }

    /// <summary>Stop the log and say what it held; the rows are on disk when this returns.</summary>
    internal string StopLog()
    {
        if (_log is null) return "no frame log is running.";
        _log.Flush(); _log.Dispose(); _log = null;
        var sum = IntervalSummary.Of(_logIntervals);
        var line = $"frame log {_logPath}: " + sum.Words() + (_logResumed > 0 ? $"; {_logResumed} frame(s) after an idle pause not counted" : "");
        File.AppendAllText(Path.ChangeExtension(_logPath!, ".summary.txt"), line + Environment.NewLine);
        return line;
    }
}

/// <summary>GPU time of each frame's draw, from two GL timestamp queries read back a few frames later so the read
/// never waits on the GPU. NaN while a result is not in, or where the driver has no timer queries.</summary>
internal sealed class GpuTimer
{
    const int Depth = 4;
    readonly int[] _q = new int[Depth * 2];
    int _slot;
    bool _ok, _tried;
    readonly bool[] _issued = new bool[Depth];

    internal void Begin()
    {
        if (!_tried)
        {
            _tried = true;
            GL.GetQuery(QueryTarget.Timestamp, GetQueryParam.QueryCounterBits, out int bits);
            _ok = bits > 0 && GL.GetError() == ErrorCode.NoError;
            if (_ok) GL.GenQueries(_q.Length, _q);
        }
        if (_ok) GL.QueryCounter(_q[_slot * 2], QueryCounterTarget.Timestamp);
    }

    /// <summary>Mark the draw's end and return the GPU time of the oldest frame still in flight, if it has arrived.</summary>
    internal double End()
    {
        if (!_ok) return double.NaN;
        GL.QueryCounter(_q[_slot * 2 + 1], QueryCounterTarget.Timestamp);
        _issued[_slot] = true;
        _slot = (_slot + 1) % Depth;
        if (!_issued[_slot]) return double.NaN;                     // the oldest slot is the one about to be reused
        GL.GetQueryObject(_q[_slot * 2 + 1], GetQueryObjectParam.QueryResultAvailable, out int ready);
        if (ready == 0) { _issued[_slot] = false; return double.NaN; }
        GL.GetQueryObject(_q[_slot * 2], GetQueryObjectParam.QueryResult, out long a);
        GL.GetQueryObject(_q[_slot * 2 + 1], GetQueryObjectParam.QueryResult, out long b);
        _issued[_slot] = false;
        return (b - a) / 1e6;
    }
}
