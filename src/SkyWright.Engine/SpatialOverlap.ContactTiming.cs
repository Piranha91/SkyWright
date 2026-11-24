using HousecarlCore;

namespace SkyWright.Engine;

/// <summary>Opt-in step timers for <see cref="SpatialOverlap.Contact(TriangleSoup, TriangleGrid, float, float, CancellationToken, ContactStepTimer)"/>:
/// <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> ticks and query counts per step, summed over calls. Null at the call site:
/// no clock is read. Changes no result.</summary>
public sealed class ContactStepTimer
{
    /// <summary>Calls; calls that returned before step 1 (an empty soup).</summary>
    public long Calls, EmptyCalls;
    /// <summary>Step 1 split: nearest host triangle per dependent triangle, and the band search for facing candidates.</summary>
    public long NearestTicks, BandTicks, NearestQueries, BandQueries, BandCandidates;
    /// <summary>Step 2 (candidate filter and normal), step 3 (penetration per touching vertex), step 4 (enclosed depth per vertex).</summary>
    public long CandidateTicks, PenetrationTicks, PenetrationQueries, EnclosedTicks, EnclosedQueries;
    /// <summary>Whole-call ticks (entry to return), so the steps reconcile against it.</summary>
    public long CallTicks;
    /// <summary>Σ dependent triangles and Σ host triangles over the calls.</summary>
    public long DependentTriangles, HostTriangles;

    public static double Seconds(long ticks) => ticks / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Add another thread's timer into this one. A timer itself belongs to one thread at a time.</summary>
    public void Add(ContactStepTimer o)
    {
        Calls += o.Calls; EmptyCalls += o.EmptyCalls;
        NearestTicks += o.NearestTicks; BandTicks += o.BandTicks; NearestQueries += o.NearestQueries; BandQueries += o.BandQueries; BandCandidates += o.BandCandidates;
        CandidateTicks += o.CandidateTicks; PenetrationTicks += o.PenetrationTicks; PenetrationQueries += o.PenetrationQueries; EnclosedTicks += o.EnclosedTicks; EnclosedQueries += o.EnclosedQueries;
        CallTicks += o.CallTicks; DependentTriangles += o.DependentTriangles; HostTriangles += o.HostTriangles;
    }
}
