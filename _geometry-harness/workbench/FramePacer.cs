using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Workbench;

// The compositor-or-clock strategy is SkyRender.Wpf's FramePacer (SkyRender's view host benchmark, 2026-11-19), adapted.
/// <summary>Paces the 3D view's frames. The view is a child window whose SwapBuffers does not wait for the vertical blank
/// under the compositor, so the wait happens here instead: a background thread waits for the next beat while a frame is
/// wanted, then posts one frame to the UI thread at <see cref="DispatcherPriority.Render"/>, above input, so a stream of
/// mouse messages cannot hold frames back. Nothing sleeps on the UI thread, and nothing is posted while no frame is
/// wanted. One frame is in flight at a time.
/// <para>The beat is each desktop composition (<c>DwmFlush</c>) while the compositor keeps the display's rate. A desktop
/// can compose much more slowly than the display refreshes (every few system timer ticks rather than every
/// refresh), and a view that waits for it then runs at that rate. So a second thread watches the compositions
/// while frames are wanted, and when two in a row come more than a period and a half apart the pacer keeps time with
/// its own high-resolution clock at the display's period, until the compositor keeps the rate again (<see cref="PaceChoice"/>).</para></summary>
internal sealed class FramePacer : IDisposable
{
    readonly Dispatcher _ui;
    readonly Action _frame;
    readonly Func<bool> _compose;
    readonly PaceChoice _choice;
    readonly AutoResetEvent _wake = new(false), _composed = new(false), _watchWake = new(false);
    readonly Thread _thread, _watch;
    volatile bool _want, _inFlight, _stop;
    long _watchUntil;                                   // the compositor is watched until this Stopwatch timestamp
    IntPtr _timer;

    /// <summary>When the last frame was posted to the UI thread, on <see cref="WorkbenchHost.Now"/>'s clock.</summary>
    internal double PostedAt { get; private set; } = double.NaN;

    /// <summary>What timed the last posted frame: "compositor" or "clock".</summary>
    internal string PostedBy { get; private set; } = "compositor";

    /// <summary>The last time between two desktop compositions the watch saw, in ms (NaN before the first).</summary>
    internal double ComposeMs => _choice.LastIntervalMs;

    /// <summary>The display's refresh period the clock keeps, in ms.</summary>
    internal double PeriodMs => _choice.PeriodMs;

    /// <summary>Why the compositor wait is not used, when it is not (the pacer then waits on its own clock).</summary>
    internal string? Fallback { get; private set; }

    /// <param name="compose">Test hook: waits for one composition and says whether it could; the default is DwmFlush.</param>
    /// <param name="periodMs">Test hook: the display's refresh period; the default asks the compositor.</param>
    internal FramePacer(Dispatcher ui, Action frame, Func<bool>? compose = null, double? periodMs = null)
    {
        _ui = ui; _frame = frame;
        _compose = compose ?? (() => DwmFlush() == 0);
        _choice = new PaceChoice(periodMs ?? RefreshPeriodMs() ?? 1000.0 / 60);
        _timer = CreateWaitableTimerEx(IntPtr.Zero, null, 0x2 /* CREATE_WAITABLE_TIMER_HIGH_RESOLUTION */, 0x1F0003);
        _thread = new Thread(Loop) { IsBackground = true, Name = "frame pacer", Priority = ThreadPriority.AboveNormal };
        _watch = new Thread(Watch) { IsBackground = true, Name = "compositor watch", Priority = ThreadPriority.AboveNormal };
        _thread.Start(); _watch.Start();
    }

    /// <summary>Ask for a frame at the next beat; many asks before it is drawn make one frame.</summary>
    internal void Request() { _want = true; _wake.Set(); }

    void Loop()
    {
        var clock = Stopwatch.StartNew();
        double next = 0;
        bool idle = true;
        while (!_stop)
        {
            if (!_want || _inFlight) { if (!_want && !_inFlight) idle = true; _wake.WaitOne(50); continue; }
            // watched while frames are wanted and for half a second after, so a view parked between mouse moves keeps its count
            Volatile.Write(ref _watchUntil, Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2);
            _watchWake.Set();
            bool onClock = _choice.OnClock;
            if (onClock) ClockWait(clock, ref next);
            else
            {
                if (idle) _composed.Reset();                                 // a composition before the pause is not this frame's
                _composed.WaitOne((int)Math.Ceiling(_choice.PeriodMs * 3)); // late rather than never
            }
            idle = false;
            if (_stop || !_want) continue;
            _want = false; _inFlight = true;
            PostedAt = WorkbenchHost.Now; PostedBy = onClock ? "clock" : "compositor";
            try { _ui.BeginInvoke(DispatcherPriority.Render, Draw); }
            catch (InvalidOperationException) { return; }       // the dispatcher has shut down
        }
    }

    /// <summary>Waits on each composition while frames are wanted, signals it, and feeds its interval to the choice.</summary>
    void Watch()
    {
        var clock = Stopwatch.StartNew();
        double last = -1;
        while (!_stop)
        {
            if (Stopwatch.GetTimestamp() > Volatile.Read(ref _watchUntil)) { last = -1; _choice.Paused(); _watchWake.WaitOne(200); continue; }
            if (!_compose())
            {
                Fallback ??= "the desktop compositor's frame wait (DwmFlush) failed, so frames are paced by a 60 Hz clock";
                _choice.Failed(); last = -1;
                Thread.Sleep(100);
                continue;
            }
            double now = clock.Elapsed.TotalMilliseconds;
            if (last >= 0) _choice.Composed(now - last);
            last = now;
            _composed.Set();
        }
    }

    /// <summary>Waits for the clock's next beat on a high-resolution waitable timer; the process's timer resolution is left alone.</summary>
    void ClockWait(Stopwatch clock, ref double next)
    {
        double now = clock.Elapsed.TotalMilliseconds, period = _choice.PeriodMs;
        if (next < now - period) next = now;                    // resumed after a pause, or just switched: a new beat
        double wait = next - now;
        if (wait > 0.05)
        {
            long due = -(long)(wait * 10_000);                  // relative, in 100 ns units
            if (_timer != IntPtr.Zero && SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) WaitForSingleObject(_timer, 200);
            else Thread.Sleep((int)Math.Ceiling(wait));
        }
        next += period;
    }

    void Draw()
    {
        try { _frame(); }
        finally { _inFlight = false; _wake.Set(); }
    }

    public void Dispose()
    {
        _stop = true; _wake.Set(); _watchWake.Set();
        _thread.Join(500); _watch.Join(500);
        if (_timer != IntPtr.Zero) CloseHandle(_timer);
        _timer = IntPtr.Zero;
    }

    /// <summary>The display's refresh period from the compositor's timing counters, or null when it does not answer.</summary>
    static double? RefreshPeriodMs()
    {
        var b = new byte[292];                                  // DWM_TIMING_INFO, packed to 1; qpcRefreshPeriod at offset 12
        BitConverter.TryWriteBytes(b, (uint)b.Length);
        if (DwmGetCompositionTimingInfo(IntPtr.Zero, b) != 0) return null;
        double ms = BitConverter.ToUInt64(b, 12) * 1000.0 / Stopwatch.Frequency;
        return ms is > 2 and < 100 ? ms : null;
    }

    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    [DllImport("dwmapi.dll")] static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, byte[] timingInfo);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr routine, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}

/// <summary>Whether the pacer follows the compositor or its own clock, decided from the time between compositions: two in a
/// row more than a period and a half apart switch to the clock, thirty in a row within it (half a second at 60 Hz) switch
/// back. The gap between the two counts is what keeps a desktop near the boundary from flapping.</summary>
internal sealed class PaceChoice
{
    internal const int SlowToClock = 2, OnTimeToCompositor = 30;
    volatile bool _onClock;
    int _onTime, _slow;
    double _last = double.NaN;

    internal PaceChoice(double periodMs) => PeriodMs = periodMs;
    internal double PeriodMs { get; }
    internal bool OnClock => _onClock;
    internal double LastIntervalMs => Volatile.Read(ref _last);

    /// <summary>One composition came <paramref name="intervalMs"/> after the previous one.</summary>
    internal void Composed(double intervalMs)
    {
        Volatile.Write(ref _last, intervalMs);
        if (intervalMs < PeriodMs * 1.5) { _onTime++; _slow = 0; } else { _slow++; _onTime = 0; }
        if (!_onClock && _slow >= SlowToClock) _onClock = true;
        else if (_onClock && _onTime >= OnTimeToCompositor) _onClock = false;
    }

    /// <summary>The compositor could not be waited on: keep the clock until it composes on time again.</summary>
    internal void Failed() { _onTime = 0; _slow = SlowToClock; _onClock = true; }

    /// <summary>The watch paused with nothing to draw: the counts start again, the choice stays.</summary>
    internal void Paused() { _onTime = 0; _slow = 0; }
}
