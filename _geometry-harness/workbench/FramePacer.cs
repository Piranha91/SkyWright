using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Workbench;

/// <summary>Paces the 3D view's frames to the desktop compositor. The view is a child window whose SwapBuffers does not
/// wait for the vertical blank under the compositor, so the wait happens here instead: a background thread waits for
/// each composition (<c>DwmFlush</c>) while a frame is wanted, then posts one frame to the UI thread at
/// <see cref="DispatcherPriority.Render"/>, above input, so a stream of mouse messages cannot hold frames back. Nothing
/// sleeps on the UI thread, and nothing is posted while no frame is wanted. One frame is in flight at a time.</summary>
internal sealed class FramePacer : IDisposable
{
    readonly Dispatcher _ui;
    readonly Action _frame;
    readonly AutoResetEvent _wake = new(false);
    readonly Thread _thread;
    volatile bool _want, _inFlight, _stop;

    /// <summary>When the last frame was posted to the UI thread, on <see cref="WorkbenchHost.Now"/>'s clock.</summary>
    internal double PostedAt { get; private set; } = double.NaN;

    /// <summary>Why the compositor wait is not used, when it is not (the pacer then waits on its own clock).</summary>
    internal string? Fallback { get; private set; }

    internal FramePacer(Dispatcher ui, Action frame)
    {
        _ui = ui; _frame = frame;
        _thread = new Thread(Loop) { IsBackground = true, Name = "frame pacer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Ask for a frame at the next composition; many asks before it is drawn make one frame.</summary>
    internal void Request() { _want = true; _wake.Set(); }

    void Loop()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double next = 0;
        while (!_stop)
        {
            if (!_want || _inFlight) { _wake.WaitOne(50); continue; }
            if (Fallback is null && DwmFlush() != 0) Fallback = "the desktop compositor's frame wait (DwmFlush) failed, so frames are paced by a 60 Hz clock";
            if (Fallback is not null)
            {
                while (clock.Elapsed.TotalMilliseconds < next) Thread.Sleep(1);
                next = Math.Max(next + 1000.0 / 60, clock.Elapsed.TotalMilliseconds - 5);
            }
            if (_stop || !_want) continue;
            _want = false; _inFlight = true;
            PostedAt = WorkbenchHost.Now;
            try { _ui.BeginInvoke(DispatcherPriority.Render, Draw); }
            catch (InvalidOperationException) { return; }       // the dispatcher has shut down
        }
    }

    void Draw()
    {
        try { _frame(); }
        finally { _inFlight = false; _wake.Set(); }
    }

    public void Dispose() { _stop = true; _wake.Set(); _thread.Join(500); }

    [DllImport("dwmapi.dll")] static extern int DwmFlush();
}
