using HousecarlCore;

namespace SkyWright.Engine;

public static partial class MeshIntersection
{
    /// <summary>One triangle soup prepared once (overlap acceleration P2): the welded double vertices, triangles and BVH
    /// that <see cref="Query(TriangleSoup, TriangleSoup, double, long, CancellationToken, MeshQueryTimer?)"/> builds on every
    /// call, or the preparation's refusal. Immutable except for its solid-validation memo, which keeps a COMPLETED
    /// validation's result with the budget units it consumed, so a later query charges exactly those units. Thread-safe.</summary>
    public sealed class PreparedMesh
    {
        // The prepared state, typed as MeshIntersection's private Core (its mesh types are private to the query).
        internal readonly object State;

        PreparedMesh(TriangleSoup source, object state, long bytes) { Source = source; State = state; EstimatedBytes = bytes; }

        /// <summary>The soup this was prepared from.</summary>
        public TriangleSoup Source { get; }
        /// <summary>Bytes this preparation holds, estimated from its arrays (for a byte-bounded cache).</summary>
        public long EstimatedBytes { get; }
        /// <summary>Whether preparation refused the soup (a query then answers that refusal, labelled by side).</summary>
        public bool Refused => ((Core)State).ErrorSuffix is not null;
        /// <summary>Completed validations held (for counting memo use).</summary>
        public int MemoisedValidations { get { var c = (Core)State; lock (c) return c.Validations.Count; } }
        /// <summary>Marks this preparation as a repeat of one a cache evicted, before it is shared (for counting repeat validation).</summary>
        public void MarkRepeat() => ((Core)State).Repeat = true;

        /// <summary>Prepare <paramref name="soup"/> with the soup query's own preparation. It consumes no budget units there
        /// either, and nothing cancels it here, so the result never depends on the clock.</summary>
        public static PreparedMesh Create(TriangleSoup soup, MeshQueryTimer? timer = null)
        {
            ArgumentNullException.ThrowIfNull(soup);
            var budget = new WorkBudget(long.MaxValue, CancellationToken.None);
            if (!TryPrepare(soup, "x", budget, out var mesh, out var error, timer))
                return new(soup, new Core(null, error!.Substring(1)), 256);
            long v = mesh!.Vertices.Length, t = mesh.Triangles.Length;
            return new(soup, new Core(mesh, null), 4096 + v * 28 + t * 232);
        }
    }

    sealed class Core(Mesh? mesh, string? errorSuffix)
    {
        public readonly Mesh? Mesh = mesh;
        public readonly string? ErrorSuffix = errorSuffix;
        /// <summary>Completed validations by tolerance: the result and the units it consumed.</summary>
        public readonly Dictionary<double, (SolidValidation Result, long Units)> Validations = new();
        public bool Repeat;
    }

    /// <summary>The query on two prepared meshes. Decides exactly what the soup query decides on their soups, with the same
    /// budget accounting: preparation consumes no units, and a memoised validation charges the units it consumed, exhausting
    /// the budget where the original would have. The difference is the clock: a memo hit and a prepared mesh read no
    /// cancellation, so a pair the soup query cut short by its time limit in those phases can complete here.</summary>
    public static MeshIntersectionResult Query(PreparedMesh a, PreparedMesh b, double tolerance = 1e-5, long maxTrianglePairs = 2_000_000,
                                               CancellationToken cancellationToken = default, MeshQueryTimer? timer = null)
    {
        if (a is null || b is null)
            return UnknownResult("null_mesh", 0);
        if (!double.IsFinite(tolerance) || tolerance < 0)
            return UnknownResult("invalid_tolerance", 0);
        if (maxTrianglePairs <= 0)
            return UnknownResult("invalid_triangle_pair_budget", 0);
        var budget = new WorkBudget(maxTrianglePairs, cancellationToken);
        if (timer is not null) timer.Queries++;
        var ca = (Core)a.State; var cb = (Core)b.State;
        if (ca.ErrorSuffix is { } ea) { if (timer is not null) timer.PrepareRefused++; return UnknownResult("a" + ea, 0); }
        if (cb.ErrorSuffix is { } eb) { if (timer is not null) timer.PrepareRefused++; return UnknownResult("b" + eb, 0); }
        return Evaluate(ca.Mesh!, cb.Mesh!, ca, cb, tolerance, budget, timer);
    }

    /// <summary>A solid validation through a prepared mesh's memo. A hit charges the units the completed validation consumed;
    /// a miss validates and keeps the result only when it completed (a budget or cancellation exception escapes, nothing kept).</summary>
    static SolidValidation ValidateMemo(Mesh mesh, Core? memo, double tolerance, WorkBudget budget)
    {
        if (memo is null) return ValidateSolid(mesh, tolerance, budget);
        // Held across the validation: a second thread waits for the answer instead of repeating the work.
        lock (memo)
        {
            if (memo.Validations.TryGetValue(tolerance, out var hit))
            {
                Interlocked.Increment(ref validateMemoHits);
                budget.Charge(hit.Units);
                return hit.Result;
            }
            Interlocked.Increment(ref validateMemoMisses);
            long before = budget.Used, started = memo.Repeat ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try
            {
                var result = ValidateSolid(mesh, tolerance, budget);
                memo.Validations[tolerance] = (result, budget.Used - before);
                return result;
            }
            finally
            {
                if (memo.Repeat)
                {
                    Interlocked.Increment(ref validateRepeats);
                    Interlocked.Add(ref validateRepeatTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
                }
            }
        }
    }
}
