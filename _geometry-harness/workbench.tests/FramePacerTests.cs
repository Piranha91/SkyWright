using System.Diagnostics;
using System.Windows.Threading;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The pacer's compositor-or-clock choice on known series of composition intervals, and the pacer itself fed a
/// slow and a healthy stand-in compositor through its test hook.</summary>
[Trait("tier", "unit")]
public class FramePacerTests
{
    const double Period = 1000.0 / 60;

    static PaceChoice Fed(IEnumerable<double> intervals, PaceChoice? c = null)
    {
        c ??= new PaceChoice(Period);
        foreach (var iv in intervals) c.Composed(iv);
        return c;
    }

    [Fact]
    public void Compositions_at_the_display_rate_keep_the_compositor_even_with_one_missed_refresh()
    {
        var jitter = Enumerable.Range(0, 300).Select(i => Period + (i % 3 - 1) * 0.4);
        var c = Fed(jitter);
        Assert.False(c.OnClock);
        Fed(new[] { 2 * Period }, c);                          // one late composition: one missed refresh, not a slow desktop
        Fed(Enumerable.Repeat(Period, 10), c);
        Assert.False(c.OnClock);
        Assert.Equal(Period, c.LastIntervalMs, 6);
    }

    [Fact]
    public void Compositions_three_timer_ticks_apart_switch_to_the_clock_on_the_second()
    {
        var c = Fed(Enumerable.Repeat(Period, 50));
        Fed(new[] { 46.8 }, c);
        Assert.False(c.OnClock);
        Fed(new[] { 46.8 }, c);
        Assert.True(c.OnClock);
        Fed(Enumerable.Repeat(46.8, 200), c);
        Assert.True(c.OnClock);
        Assert.Equal(46.8, c.LastIntervalMs, 6);
    }

    [Fact]
    public void A_compositor_back_at_the_display_rate_takes_over_after_thirty_on_time_compositions()
    {
        var c = Fed(Enumerable.Repeat(46.8, 20));
        Assert.True(c.OnClock);
        Fed(Enumerable.Repeat(Period, PaceChoice.OnTimeToCompositor - 1), c);
        Assert.True(c.OnClock);
        Fed(new[] { Period }, c);
        Assert.False(c.OnClock);
    }

    [Fact]
    public void A_desktop_at_the_boundary_does_not_flap_between_the_two()
    {
        // one slow composition in every two, and intervals either side of a period and a half: at most one switch
        foreach (var start in new[] { false, true })
            foreach (var (fast, slow) in new[] { (Period, 46.8), (Period * 1.45, Period * 1.55), (Period * 1.49, Period * 1.51) })
            {
                var c = new PaceChoice(Period);
                if (start) Fed(new[] { 46.8, 46.8 }, c);
                Assert.Equal(start, c.OnClock);
                int switches = 0; bool was = c.OnClock;
                for (int i = 0; i < 400; i++)
                {
                    c.Composed(i % 2 == 0 ? fast : slow);
                    if (c.OnClock != was) { switches++; was = c.OnClock; }
                }
                Assert.Equal(0, switches);
            }
    }

    [Fact]
    public void A_failed_wait_or_a_pause_is_handled_the_way_the_counts_say()
    {
        var c = new PaceChoice(Period);
        c.Failed();
        Assert.True(c.OnClock);
        Fed(Enumerable.Repeat(Period, PaceChoice.OnTimeToCompositor), c);
        Assert.False(c.OnClock);
        Fed(new[] { 46.8 }, c);
        c.Paused();                                            // a slow composition before a pause is not counted after it
        Fed(new[] { 46.8 }, c);
        Assert.False(c.OnClock);
    }

    [Fact]
    public void The_readout_names_the_clock_and_the_composition_interval()
    {
        Assert.Equal("own clock, desktop composes every 46.6 ms", FrameStats.Pacing("clock", 46.58));
        Assert.Equal("compositor, desktop composes every 16.7 ms", FrameStats.Pacing("compositor", 16.66));
        Assert.Equal("compositor", FrameStats.Pacing("compositor", double.NaN));
        Assert.EndsWith("own clock, desktop composes every 46.6 ms", new FrameStats().Readout(0, "demand", true, FrameStats.Pacing("clock", 46.6)));
        Assert.EndsWith(",pacer,compose_ms", FrameStats.CsvHeader);
    }

    // functional checks through the test hook: a stand-in compositor, not the desktop's real slow state
    [Theory]
    [InlineData(46.8, "clock")]
    [InlineData(Period, "compositor")]
    public void The_pacer_keeps_the_display_rate_whatever_the_stand_in_compositor_does(double composeMs, string by)
    {
        var (median, last) = Run(composeMs, 1500);
        Assert.Equal(by, last);
        Assert.InRange(median, Period - 2.5, Period + 3);      // compositor-only pacing gives the stand-in's 46.8 ms here
    }

    /// <summary>Runs a pacer that always wants a frame for <paramref name="ms"/>, against a stand-in compositor that composes
    /// every <paramref name="composeMs"/>; returns the median interval between drawn frames after the first 300 ms, and what
    /// timed the last one.</summary>
    static (double Median, string By) Run(double composeMs, int ms)
    {
        var (iv, by) = Intervals(t => (Math.Floor(t / composeMs) + 1) * composeMs, ms);
        return (iv[iv.Length / 2], by);
    }

    [Fact]
    public void A_composition_that_comes_three_periods_late_holds_a_frame_back_a_period_and_a_quarter_at_most()
    {
        // a healthy stand-in compositor, except that every twentieth composition comes two refreshes late
        static double Next(double t)
        {
            double next = (Math.Floor(t / Period) + 1) * Period;
            return Math.Round(next / Period) % 20 == 0 ? next + 2 * Period : next;
        }
        var (iv, by) = Intervals(Next, 1500);
        Assert.Equal("compositor", by);
        Assert.InRange(iv[iv.Length / 2], Period - 2.5, Period + 3);
        Assert.True(iv[^1] < 2.4 * Period, $"worst interval {iv[^1]:F1} ms; waiting for the late composition gives about {3 * Period:F0}");
    }

    /// <summary>Runs a pacer that always wants a frame for <paramref name="ms"/>, against a stand-in compositor whose next
    /// composition after time t (ms) is <paramref name="nextComposition"/>(t); returns the sorted intervals between drawn
    /// frames after the first 300 ms, and what timed the last one.</summary>
    static (double[] Sorted, string By) Intervals(Func<double, double> nextComposition, int ms)
    {
        Dispatcher? d = null; using var ready = new ManualResetEventSlim();
        var t = new Thread(() => { d = Dispatcher.CurrentDispatcher; ready.Set(); Dispatcher.Run(); }) { IsBackground = true, Name = "fake UI thread" };
        t.SetApartmentState(ApartmentState.STA); t.Start(); ready.Wait();
        var clock = Stopwatch.StartNew();
        var drawn = new List<double>();
        FramePacer? pacer = null;
        bool Compose()
        {
            double next = nextComposition(clock.Elapsed.TotalMilliseconds);
            while (clock.Elapsed.TotalMilliseconds < next) Thread.Sleep(0);
            return true;
        }
        pacer = new FramePacer(d!, () => { lock (drawn) drawn.Add(clock.Elapsed.TotalMilliseconds); pacer!.Request(); }, Compose, Period);
        pacer.Request();
        Thread.Sleep(ms);
        pacer.Dispose();
        string by = pacer.PostedBy;
        d!.InvokeShutdown();
        double[] times; lock (drawn) times = drawn.Where(x => x > 300).ToArray();
        var iv = times.Zip(times.Skip(1), (a, b) => b - a).OrderBy(x => x).ToArray();
        Assert.True(iv.Length > 10, $"only {iv.Length} frame intervals were drawn");
        return (iv, by);
    }
}
