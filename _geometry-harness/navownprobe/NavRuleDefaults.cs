using HarnessShared;

// The border and ground rules' thresholds, each fitted on one part of the base game's own records and checked on a part not
// used to fit it (the vanilla floor; _claude/runs/2026-11-16-navmesh-repair/GATE-B.md has the measurement and the rule).
static class NavRuleDefaults
{
    /// <summary>The edge-match tolerance (plan, height): each the 0.999 quantile of the gaps of ordinary links between
    /// navmeshes the first master defines, rounded up to a whole unit.</summary>
    public static readonly EdgeTolerance Edge = new(8f, 63f);
    /// <summary>How close to the author's ground a vertex lies to count as on it, and how far off the winning ground it must
    /// then be: the 0.9 quantile of vertex heights above the author's ground within 64 u of it, over one fixed half of the
    /// base game's navmeshes, rounded up.</summary>
    public const float GroundBand = 38f;
    /// <summary>How far the ground must have moved under a vertex: the band, since a smaller move cannot be told from the
    /// on-ground scatter.</summary>
    public const float GroundMove = 38f;
    /// <summary>Counted vertices a navmesh needs to be a row.</summary>
    public const int GroundMinVertices = 3;
}
