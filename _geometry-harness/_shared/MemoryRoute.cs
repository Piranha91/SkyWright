namespace HarnessShared;

/// <summary>What the user asked of the memory route (--memory-route): auto decides; fast and lean are the user's answer to a
/// contention warning; ask stops at the warning so a session can put the question to its user.</summary>
public enum RouteRequest { Auto, Fast, Lean, Ask }
public enum RouteName { Fast, Lean }
/// <summary>The memory case read before the host loop: free (fast fits), contended (the RAM exists, other programs hold it),
/// absent (a limit or the machine is below the fast route's need), nothing fits (even the lean route does not).</summary>
public enum MemoryCase { Free, Contended, Absent, NothingFits }

/// <summary>The machine and process readings the route is decided from. Bytes throughout.</summary>
public sealed record MemoryReadings(long TotalPhysical, long? AvailablePhysical, long OwnPrivate, long? ProcessLimit, string LimitSource);

/// <summary>Another program and the memory it holds, for the contention warning.</summary>
public sealed record ProcessUse(string Name, int Pid, long Bytes);

/// <summary>Live data at the decision and each route's shape.</summary>
public sealed record RouteShape(long Live, int FastThreads, long FastBudget, int LeanThreads, long LeanBudget);

public sealed record RouteDecision(MemoryCase Case, RouteName? Route, int ExitCode, bool UnattendedDefault, long NeedFast, long NeedLean,
    int Threads, long Budget, long? HeapLimit, string Line, string? Warning, string? Refusal);

/// <summary>STUB (red stage): today's behaviour - always fast at the default threads, no estimate, no warning, no lean route.
/// The memory rule's known answers (contactprobe --self-test CR-1..CR-12) are written against this and fail on it.</summary>
public static class MemoryRoute
{
    public const double Margin = 0.25;
    public static long FastExtra(int threads, long budget) => 0L;
    public static long FastNeed(RouteShape s) => s.Live + s.FastBudget + FastExtra(s.FastThreads, s.FastBudget);
    public static long LeanNeed(RouteShape s) => s.Live + s.LeanBudget;

    public static RouteDecision Decide(MemoryReadings r, RouteShape s, RouteRequest request, IReadOnlyList<ProcessUse> others, bool explicitShape = false)
        => new(MemoryCase.Free, RouteName.Fast, 0, false, FastNeed(s), LeanNeed(s), s.FastThreads, s.FastBudget, null, "memory route: fast", null, null);
}
