using System.Diagnostics;

using HousecarlCore;

namespace SkyWright.Engine;

/// <summary>Cumulative time, work units and outcome counts of <see cref="MeshIntersection.Query"/> calls, by phase
/// (overlap acceleration P0). Observation only: a query given a timer decides exactly what it decides without one.
/// Not thread-safe; one timer per thread, merged with <see cref="Add"/> when the threads have finished.</summary>
public sealed class MeshQueryTimer
{
    /// <summary>Stopwatch ticks: convert, weld and triangles; BVH build; surface traversal; ValidateSolid; containment.</summary>
    public long PrepareTicks, BvhTicks, SurfaceTicks, ValidateTicks, ContainmentTicks;
    /// <summary>Budget units consumed by the surface traversal, solid validation and containment phases.</summary>
    public long SurfaceUnits, ValidateUnits, ContainmentUnits;
    /// <summary>Queries timed, and how many ended in budget exhaustion, cancellation, or a preparation refusal.</summary>
    public long Queries, BudgetExhausted, Cancelled, PrepareRefused;

    public static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

    public void Add(MeshQueryTimer o)
    {
        PrepareTicks += o.PrepareTicks; BvhTicks += o.BvhTicks; SurfaceTicks += o.SurfaceTicks;
        ValidateTicks += o.ValidateTicks; ContainmentTicks += o.ContainmentTicks;
        SurfaceUnits += o.SurfaceUnits; ValidateUnits += o.ValidateUnits; ContainmentUnits += o.ContainmentUnits;
        Queries += o.Queries; BudgetExhausted += o.BudgetExhausted; Cancelled += o.Cancelled; PrepareRefused += o.PrepareRefused;
    }
}

public static partial class MeshIntersection
{
    static long bvhNodesBuilt;

    /// <summary>BVH nodes built in this process, every caller included (the node id counter is an int; this is not).</summary>
    public static long BvhNodesBuilt => Interlocked.Read(ref bvhNodesBuilt);

    static long validateMemoHits, validateMemoMisses, validateRepeats, validateRepeatTicks;

    /// <summary>Prepared-mesh solid validations answered by the memo in this process.</summary>
    public static long ValidateMemoHits => Interlocked.Read(ref validateMemoHits);
    /// <summary>Prepared-mesh solid validations that ran in this process, cut short ones included.</summary>
    public static long ValidateMemoMisses => Interlocked.Read(ref validateMemoMisses);
    /// <summary>Of those, the ones on a mesh prepared again after eviction, and their stopwatch ticks.</summary>
    public static long ValidateRepeats => Interlocked.Read(ref validateRepeats);
    public static long ValidateRepeatTicks => Interlocked.Read(ref validateRepeatTicks);

    enum Phase { None, Surface, Validate, Containment }

    /// <summary>Charges the time and units since the last phase change to that phase.</summary>
    sealed class PhaseClock(MeshQueryTimer timer, WorkBudget budget)
    {
        Phase _phase = Phase.None;
        long _tick = Stopwatch.GetTimestamp(), _units = budget.Used;

        public void Enter(Phase next)
        {
            long now = Stopwatch.GetTimestamp(), dt = now - _tick, du = budget.Used - _units;
            switch (_phase)
            {
                case Phase.Surface: timer.SurfaceTicks += dt; timer.SurfaceUnits += du; break;
                case Phase.Validate: timer.ValidateTicks += dt; timer.ValidateUnits += du; break;
                case Phase.Containment: timer.ContainmentTicks += dt; timer.ContainmentUnits += du; break;
            }
            _tick = now; _units = budget.Used; _phase = next;
        }
    }
}
