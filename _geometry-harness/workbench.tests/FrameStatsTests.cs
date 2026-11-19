using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>The frame statistics' maths and the motion steps' parsing and message schedules, on known answers.</summary>
[Trait("tier", "unit")]
public class FrameStatsTests
{
    [Fact]
    public void A_steady_series_with_two_hitches_gives_the_expected_median_low_and_counts()
    {
        // 198 intervals of 16.7 ms, one of 30 and one of 70: median 16.7, the slowest 1% (one interval) is 70
        var iv = Enumerable.Repeat(16.7, 198).Append(30).Append(70).ToList();
        var s = IntervalSummary.Of(iv);
        Assert.Equal(200, s.Count);
        Assert.Equal(16.7, s.MedianMs, 6);
        Assert.Equal((70 + 30) / 2.0, s.Low1Ms, 6);        // 200 intervals: the slowest 2 make the 1%
        Assert.Equal(70, s.WorstMs);
        Assert.Equal(2, s.Over25);
        Assert.Equal(1, s.Over50);
        Assert.Equal(16.7 * 198 + 100, s.TotalMs, 6);
    }

    [Fact]
    public void Fewer_than_a_hundred_intervals_take_the_single_worst_as_the_1_percent_low()
    {
        var s = IntervalSummary.Of(new[] { 10.0, 12, 11, 40, 13 });
        Assert.Equal(12, s.MedianMs);
        Assert.Equal(40, s.Low1Ms);
        Assert.Equal(1, s.Over25);
        Assert.Equal(0, s.Over50);
    }

    [Fact]
    public void Alternating_frames_of_16_and_31_ms_are_jitter_the_median_alone_hides()
    {
        var iv = Enumerable.Range(0, 300).Select(i => i % 2 == 0 ? 16.0 : 31.0).ToList();
        var s = IntervalSummary.Of(iv);
        Assert.Equal(31, s.MedianMs);                         // the upper median of an even count
        Assert.Equal(150, s.Over25);
        Assert.Equal(31, s.Low1Ms);
    }

    [Fact]
    public void No_intervals_say_no_frames()
    {
        var s = IntervalSummary.Of(Array.Empty<double>());
        Assert.Equal(0, s.Count);
        Assert.Equal("no frames", s.Words());
    }

    [Fact]
    public void The_log_writes_a_row_a_frame_and_leaves_frames_after_a_pause_out_of_the_summary()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-framestats-" + Guid.NewGuid().ToString("N"));
        var f = new FrameStats();
        var path = Path.Combine(dir, "log.csv");
        f.StartLog(path, 1000);
        double t = 1000;
        for (int i = 0; i < 10; i++) { t += 20; f.Add(Sample(i, t, 20, resumed: false)); }
        t += 900; f.Add(Sample(10, t, 900, resumed: true));     // the first frame after the loop parked for 900 ms
        t += 30; f.Add(Sample(11, t, 30, resumed: true) with { DirtyAgeMs = 25 });   // parked 5 ms between two changes: a frame time, counted
        var said = f.StopLog();
        var rows = File.ReadAllLines(path);
        Assert.Equal(FrameStats.CsvHeader, rows[0]);
        Assert.Equal(13, rows.Length);
        Assert.Contains("11 frames", said);
        Assert.Contains("worst 30.00 ms", said);
        Assert.Contains("1 frame(s) after an idle pause not counted", said);
        Assert.True(File.Exists(Path.Combine(dir, "log.summary.txt")));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void The_readout_says_idle_when_nothing_was_drawn_for_half_a_second()
    {
        var f = new FrameStats();
        for (int i = 0; i < 30; i++) f.Add(Sample(i, 1000 + i * 10, 10, false));
        Assert.StartsWith("100 fps  10.0 ms", f.Readout(1300, "demand", true));
        Assert.StartsWith("idle", f.Readout(2000, "demand", true));
        Assert.Contains("vsync on", f.Readout(2000, "demand", true));
    }

    static FrameSample Sample(long n, double t, double iv, bool resumed) =>
        new(n, t, iv, 2, 1, double.NaN, "demand", resumed, "camera", 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 100, 100);

    [Fact]
    public void Motion_steps_parse_their_words_and_refuse_bad_ones_in_a_sentence()
    {
        var y = MotionStep.Parse(new[] { "yawsweep", "5" });
        Assert.Equal(("yawsweep", 5.0, 45.0, 250.0), (y.Kind, y.Seconds, y.DegPerSecond, y.Hz));
        var y2 = MotionStep.Parse(new[] { "yawsweep", "2.5", "90", "500" });
        Assert.Equal((2.5, 90.0, 500.0), (y2.Seconds, y2.DegPerSecond, y2.Hz));
        var f = MotionStep.Parse(new[] { "fly", "3", "d" });
        Assert.Equal(("fly", 3.0, 'D'), (f.Kind, f.Seconds, f.Key));
        Assert.Equal('W', MotionStep.Parse(new[] { "fly", "1" }).Key);
        Assert.Contains("needs a number of seconds", Assert.Throws<InvalidOperationException>(() => MotionStep.Parse(new[] { "fly" })).Message);
        Assert.Contains("not a positive number", Assert.Throws<InvalidOperationException>(() => MotionStep.Parse(new[] { "yawsweep", "-1" })).Message);
        Assert.Contains("not one of W, A, S, D", Assert.Throws<InvalidOperationException>(() => MotionStep.Parse(new[] { "fly", "2", "X" })).Message);
    }

    [Fact]
    public void A_yaw_sweep_holds_the_right_button_and_moves_by_its_rate()
    {
        var m = MotionStep.Parse(new[] { "yawsweep", "1", "45", "100" }).Messages(500, 300, 250, 30);
        Assert.Equal(0x204, m[0].Msg);
        Assert.Equal(0x205, m[^1].Msg);
        var moves = m.Where(x => x.Msg == 0x200).ToList();
        Assert.Equal(100, moves.Count);
        Assert.All(moves, x => Assert.Equal(0x2, (int)x.WParam));
        // 45 degrees a second at 0.15 degrees a pixel is 300 pixels a second
        int lastX = (short)((long)moves[^1].LParam & 0xFFFF);
        Assert.Equal(800, lastX);
        Assert.Equal(1000, m[^1].AtMs);
    }

    [Fact]
    public void A_fly_step_repeats_the_key_at_the_keyboard_rate_after_its_delay()
    {
        var m = MotionStep.Parse(new[] { "fly", "1" }).Messages(0, 0, 250, 30);
        var downs = m.Where(x => x.Msg == 0x100).ToList();
        Assert.Equal(0, downs[0].AtMs);
        Assert.Equal(0, (long)downs[0].LParam >> 30 & 1);   // the first press is not a repeat
        Assert.Equal(250, downs[1].AtMs);
        Assert.Equal(1, (long)downs[1].LParam >> 30 & 1);
        Assert.Equal(1 + 23, downs.Count);                    // 250 ms then every 33.3 ms until 1000 ms: 23 repeats
        Assert.Equal(0x101, m[^1].Msg);
        Assert.Equal('W', (int)m[^1].WParam);
    }
}
