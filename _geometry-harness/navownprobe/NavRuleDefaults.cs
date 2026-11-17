using HarnessShared;

// The border and ground rules' thresholds, each set from the vanilla floor (the base-game order alone) and named for the
// quantile it is: _claude/runs/2026-11-16-navmesh-repair/GATE-B.md has the measurement. PROVISIONAL until that measurement
// is written here: a value below is a placeholder only while it says so.
static class NavRuleDefaults
{
    /// <summary>The edge-match tolerance (plan, height). PROVISIONAL.</summary>
    public static readonly EdgeTolerance Edge = new(16f, 128f);
    /// <summary>How close to the author's ground a vertex lies to count as on it, and how far off the winning ground it must
    /// then be. PROVISIONAL.</summary>
    public const float GroundBand = 16f;
    /// <summary>How far the ground must have moved under a vertex. PROVISIONAL.</summary>
    public const float GroundMove = 32f;
    /// <summary>Counted vertices a navmesh needs to be a row.</summary>
    public const int GroundMinVertices = 3;
}
