using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Workbench;

/// <summary>One posted window message of a motion step, at its time from the step's start.</summary>
internal readonly record struct TimedMessage(double AtMs, int Msg, IntPtr WParam, IntPtr LParam);

/// <summary>A script motion step: <c>yawsweep SECONDS [DEG_PER_S] [HZ]</c> holds the right button and moves the mouse
/// steadily sideways; <c>fly SECONDS [KEY]</c> holds a key down with the system's own key-repeat delay and rate. Both
/// post real window messages to the 3D view, so they take the path the hand's input takes.</summary>
internal sealed record MotionStep(string Kind, double Seconds, double DegPerSecond, double Hz, char Key)
{
    internal const float DegPerPixel = 0.15f;                 // the view's right-drag look, degrees a pixel

    /// <summary>The step from its words, or why it cannot run.</summary>
    internal static MotionStep Parse(string[] a)
    {
        static double D(string s, string what) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v
            : throw new InvalidOperationException($"{what} '{s}' is not a positive number.");
        if (a.Length < 2) throw new InvalidOperationException($"{a[0]} needs a number of seconds, for example '{a[0]} 5'.");
        double secs = D(a[1], "seconds");
        switch (a[0])
        {
            case "yawsweep":
                return new MotionStep("yawsweep", secs, a.Length > 2 ? D(a[2], "degrees a second") : 45, a.Length > 3 ? D(a[3], "mouse messages a second") : 250, '\0');
            case "fly":
            {
                char k = a.Length > 2 ? char.ToUpperInvariant(a[2][0]) : 'W';
                if (a.Length > 2 && (a[2].Length != 1 || "WASDQE".IndexOf(k) < 0)) throw new InvalidOperationException($"fly key '{a[2]}' is not one of W, A, S, D, Q or E.");
                return new MotionStep("fly", secs, 0, 0, k);
            }
            default: throw new InvalidOperationException($"'{a[0]}' is not a motion step; use yawsweep or fly.");
        }
    }

    /// <summary>The messages to post, in time order. <paramref name="repeatDelayMs"/> and <paramref name="repeatHz"/>
    /// are the keyboard's typematic delay and rate (the fly step's held key).</summary>
    internal IReadOnlyList<TimedMessage> Messages(int x0, int y0, double repeatDelayMs, double repeatHz)
    {
        var o = new List<TimedMessage>();
        double total = Seconds * 1000;
        if (Kind == "yawsweep")
        {
            const int MK_RBUTTON = 0x2;
            o.Add(new(0, 0x204, (IntPtr)MK_RBUTTON, Point(x0, y0)));
            double pxPerMs = DegPerSecond / DegPerPixel / 1000.0, step = 1000.0 / Hz;
            int n = (int)Math.Floor(total / step);
            for (int i = 1; i <= n; i++)
            {
                double t = i * step;
                int x = x0 + (int)Math.Round(t * pxPerMs);
                o.Add(new(t, 0x200, (IntPtr)MK_RBUTTON, Point(x, y0)));
            }
            o.Add(new(total, 0x205, IntPtr.Zero, Point(x0 + (int)Math.Round(total * pxPerMs), y0)));
        }
        else
        {
            o.Add(new(0, 0x100, (IntPtr)Key, KeyParam(false, false)));
            for (double t = repeatDelayMs; t < total; t += 1000.0 / repeatHz) o.Add(new(t, 0x100, (IntPtr)Key, KeyParam(true, false)));
            o.Add(new(total, 0x101, (IntPtr)Key, KeyParam(true, true)));
        }
        return o;
    }

    static IntPtr Point(int x, int y) => (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
    /// <summary>WM_KEYDOWN/UP lParam: repeat count 1, bit 30 the key's previous state, bit 31 set on a release.</summary>
    static IntPtr KeyParam(bool wasDown, bool up) => (IntPtr)(1L | (wasDown ? 1L << 30 : 0) | (up ? 1L << 31 : 0));

    /// <summary>The keyboard's typematic delay (ms) and rate (repeats a second), from the system's settings.</summary>
    internal static (double DelayMs, double Hz) Typematic() =>
        ((System.Windows.SystemParameters.KeyboardDelay + 1) * 250.0, 2.5 + System.Windows.SystemParameters.KeyboardSpeed * (27.5 / 31.0));

    /// <summary>Post the messages to <paramref name="hwnd"/> on their schedule from a background thread (a spin on the
    /// clock, so the process's timer resolution is left as it is); <paramref name="done"/> runs on that thread at the end.</summary>
    internal static void Post(IntPtr hwnd, IReadOnlyList<TimedMessage> messages, Action<string> done)
    {
        var th = new Thread(() =>
        {
            var sw = Stopwatch.StartNew(); int failed = 0; double late = 0;
            foreach (var m in messages)
            {
                while (sw.Elapsed.TotalMilliseconds < m.AtMs) Thread.Yield();
                late = Math.Max(late, sw.Elapsed.TotalMilliseconds - m.AtMs);
                if (!PostMessage(hwnd, m.Msg, m.WParam, m.LParam)) failed++;
            }
            done(FormattableString.Invariant($"{messages.Count} messages posted over {sw.Elapsed.TotalSeconds:0.00} s, latest {late:0.0} ms behind its time") + (failed > 0 ? $", {failed} could not be posted" : ""));
        }) { IsBackground = true, Name = "motion step" };
        th.Start();
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
