using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HarnessShared;

// THE BOUNDED CANDIDATE SEARCH (roadmap 4.20, 2026-09-11).
//
// "Explore constrained alternatives deterministically, prioritizing changes that affect a relevant winner and move
// the fewest plugins from the user's order. Bound and log search effort; no factorial enumeration as the default
// strategy." That sentence is the whole design, and each clause is a mechanism here:
//
//   constrained      every candidate is re-checked against OrderRuleSet.Check and dropped if a hard rule forbids
//                    it — with the rule named, so a move that could not be made is reported rather than missing.
//   deterministic    no dictionary iteration order, no clock, no randomness. Groups sort by their smallest member,
//                    moves by the order they touch, candidates by (phase, relevance, contention, displacement,
//                    name key). The same inputs give byte-identical output, which is what makes a re-run
//                    comparable at all.
//   relevant first   the "affects a relevant winner" clause, expressed against the only evidence this layer has:
//                    the advice rules and the contention edges the caller supplies. A candidate that SATISFIES an
//                    advice rule the user's current order violates comes first; then the most CONTESTED pairs,
//                    because that is where a reorder changes what the game actually loads. The second term is not
//                    decoration. A real order yields thousands of legal single moves, nearly all of them between
//                    plugins that share a handful of records; drop the contention term and the emitted set falls
//                    back to displacement and then to the NAME KEY, so the alphabet picks which few the reader
//                    sees. The pairs worth showing are the ones that contest heavily, by orders of magnitude.
//                    Between relevance and total contention sits CELL contention (2026-09-12): the caller may
//                    supply, per contested record, the cell or worldspace it is filed in (PlaceEvidence, built by
//                    _shared/PlaceIndex.cs), and a pair that contests records IN A CELL outranks one that contests
//                    only placeless records — items, spells, keywords, quests — however many, because a reorder of
//                    two plugins that meet in a cell changes what the game loads THERE, and each candidate names
//                    the places its pair meets in. Two placed records do NOT count toward that term: a WRLD record
//                    and a persistent cell's own CELL record, which any plugin filing anything in the worldspace
//                    carries as containers (on a real order Tamriel's are shared by tens of thousands of pairs).
//                    Without that evidence the ranking is exactly what it was.
//   fewest moved     the distance metric is the number of plugins whose INDEX CHANGED, not the number of moves.
//                    Moving one plugin ten slots disturbs ten others and is the bigger change to the user's order.
//                    It breaks ties under contention, not the other way round: a change that touches nothing is
//                    cheap and useless.
//   bounded          one budget (candidates emitted) and one enumeration cap (orders constructed). Both outcomes
//                    are reported, and an enumeration that was cut short reports its unexplored count as UNKNOWN.
//                    Never 0 — "we stopped looking" and "there was nothing left" are different answers.
//
// WHAT THIS LAYER DOES NOT DECIDE. Whether a candidate PASSES is a detector's question, not the search's: this file
// never opens a plugin, never reads a record and has no opinion about what is broken. The search hands the consumer
// a list of legal orders and says what it did not look at. Hence the sentence every bounded result carries: no
// working order found within the budget is not proof that none exists.
//
// ONE PLUGIN, ONE GLOBAL POSITION. A candidate is a single flat list. Two interaction groups that want the same
// plugin moved in opposite directions produce TWO CANDIDATES, never one order with a per-group opinion — and every
// emitted candidate is checked to be a permutation of the baseline before it leaves this file.

/// <summary>One reason two plugins interact, supplied by the caller. <paramref name="Kind"/> is "record" (both
/// override the same FormKey), "asset" (both provide the same relative file path) or "explicit" (a rule names them).
/// <paramref name="Count"/> is how much — contested records, contested paths — so a report can say how contested a
/// pair is. Mod CATEGORIES are deliberately not an input: a quest mod can also rebuild a location.</summary>
public sealed record ContentionEdge(string A, string B, string Kind, int Count);

/// <summary>A place a contested record can be filed at: a cell (<paramref name="Kind"/> "interior", "exterior" or
/// "persistent") or a worldspace ("worldspace"). <paramref name="Id"/> is the FormKey spelled XXXXXX:Plugin.esp — the
/// identity; <paramref name="Name"/> is display only (the first non-empty EditorID in load order, else the Id).
/// An exterior or persistent cell names its worldspace; an exterior cell carries its grid, when its XCLC gave one.
/// Declared here, Mutagen-free, so the search can consume the evidence without opening a plugin itself.</summary>
public sealed record PlaceDescriptor(string Id, string Kind, string Name, string? WorldspaceId, string? WorldspaceName, int? X, int? Y)
{
    public string Grid => X is { } x && Y is { } y ? $"({x},{y})" : "(?,?)";
}

/// <summary>One pair's contested records by place: how many have a place, how many have none, and the count at each
/// place (keyed by <see cref="PlaceDescriptor.Id"/>). Compact on purpose — a count per (pair, place), never a record
/// list per pair: a 1,700-plugin order carries over a million pair-record incidences.</summary>
public sealed class PlacePairCounts
{
    public int Place;
    /// <summary>The ranking's term (D-1, 2026-09-12): of the records with a place, those filed in an interior or exterior
    /// cell (that cell's own CELL record included) or PLACED inside a persistent cell — every WRLD record and every
    /// persistent cell's own CELL record excluded, because any plugin filing anything in a worldspace carries those two
    /// as containers, not edits.</summary>
    public int Cell;
    public int NoPlace;
    public readonly Dictionary<string, int> ByPlace = new(StringComparer.Ordinal);
    /// <summary>D-2: the persistent cells whose count in <see cref="ByPlace"/> includes the cell's own CELL record —
    /// at most one such record per pair per cell. The places table subtracts it; the blocks (P3) keep it.</summary>
    public HashSet<string>? ContainerAt;
}

/// <summary>The place evidence a caller hands <see cref="OrderSearch.Run"/>: per pair of plugins, its place / no-place
/// counts and its per-place counts (P2), plus the whole-order totals the manifest reports. Null evidence — a caller
/// that supplies none — reproduces the ranking without it exactly: every place contention 0, no heading, an empty
/// table.</summary>
public sealed class PlaceEvidence
{
    readonly Dictionary<string, PlacePairCounts> _pairs = new(StringComparer.Ordinal);
    readonly Dictionary<string, PlaceDescriptor> _places = new(StringComparer.Ordinal);

    public int RecordsWithPlace, RecordsInterior, RecordsExterior, RecordsPersistent, RecordsWorldspace, RecordsWithoutPlace;
    /// <summary>WRLD records and persistent cells' own CELL records over the whole contested set — the worldspace-wide
    /// containers a pair shares by filing anything in the worldspace (D-1).</summary>
    public int RecordsWorldwideContainers;
    public int PlaceDisputed;

    /// <summary>One contested record, counted once for the whole-order totals by the kind of its place.
    /// <paramref name="container"/>: the record IS a worldspace-wide container (a WRLD, or a persistent cell's own CELL).</summary>
    public void CountRecord(PlaceDescriptor? place, bool container)
    {
        if (place is null) { RecordsWithoutPlace++; return; }
        RecordsWithPlace++;
        if (container) RecordsWorldwideContainers++;
        switch (place.Kind)
        {
            case "interior": RecordsInterior++; break;
            case "exterior": RecordsExterior++; break;
            case "persistent": RecordsPersistent++; break;
            case "worldspace": RecordsWorldspace++; break;
        }
    }

    /// <summary>One contested record for one pair of its carriers: with its place, or with none. A placed record that
    /// is not a worldspace-wide container also counts toward the pair's CELL contention, the ranking's term.</summary>
    public void Add(string a, string b, PlaceDescriptor? place, bool container)
    {
        var k = OrderSearch.PairKey(a, b);
        if (!_pairs.TryGetValue(k, out var pp)) _pairs[k] = pp = new PlacePairCounts();
        if (place is null) { pp.NoPlace++; return; }
        pp.Place++;
        if (!container) pp.Cell++;
        else if (place.Kind == "persistent") (pp.ContainerAt ??= new HashSet<string>(StringComparer.Ordinal)).Add(place.Id);
        pp.ByPlace[place.Id] = pp.ByPlace.TryGetValue(place.Id, out var n) ? n + 1 : 1;
        _places.TryAdd(place.Id, place);
    }

    public PlacePairCounts? Of(string a, string b) => _pairs.TryGetValue(OrderSearch.PairKey(a, b), out var pp) ? pp : null;
    public PlaceDescriptor? Describe(string id) => _places.TryGetValue(id, out var d) ? d : null;
    public int PairsWithEvidence => _pairs.Count;
}

/// <summary>One block of a pair's places (P3): an interior cell, a persistent cell, a worldspace place, or exterior
/// cells of one worldspace joined by edge adjacency. <paramref name="Name"/> is the heaviest cell's name (the block's
/// name), <paramref name="Spelling"/> the one string the console, the manifest and the proof all carry.</summary>
public sealed record PlaceBlock(string Name, string Spelling, int Count, int Cells);

/// <summary>One row of the places table (P4): a place any movable pair contests, how many such pairs meet there, the
/// sum of their counts there (pair-record incidences — a record two movable pairs contest counts twice), and the
/// union of those pairs' plugins (up to 8 named, then "and N more").</summary>
public sealed record ManifestPlace(string Id, string Name, string Kind, string? Worldspace, string? Grid, int Pairs,
                                   long Contention, IReadOnlyList<string> Plugins);

/// <summary>A connected component of the contention graph. <paramref name="Key"/> is the smallest member name, which
/// is what the group sorts by — stable across runs and independent of the order the edges arrived in.</summary>
public sealed record InteractionGroup(string Key, IReadOnlyList<string> Members, IReadOnlyList<ContentionEdge> Edges)
{
    public int Contention => Edges.Sum(e => e.Count);
}

/// <summary>One move inside a candidate: <paramref name="Moved"/> went to just before / just after
/// <paramref name="Reference"/>, from baseline index <paramref name="From"/> to <paramref name="To"/>.</summary>
public sealed record CandidateMove(string Moved, string Placement, string Reference, int From, int To, string Group)
{
    /// <summary>Both indices are labelled because in a COMPOSED candidate the second move shifts the first one's
    /// plugin again: <c>To</c> is where it ends up in the finished order, so a plugin can start and end at the same
    /// index while everything around it moved. The candidate's Displaced count is the measure of the change.</summary>
    public string Describe => $"moved '{Moved}' to just {Placement} '{Reference}' (baseline index {From}, final index {To})";
    public string Key => $"{Moved}|{Placement}|{Reference}";
}

public sealed record OrderCandidate(
    string Name,
    IReadOnlyList<string> Plugins,
    IReadOnlyList<CandidateMove> Moves,
    int Displaced,
    int Phase,
    bool Relevant,
    int Contention,
    IReadOnlyList<OrderRule> SatisfiesAdvice,
    IReadOnlyList<OrderRule> ViolatesAdvice,
    LegalityResult Legality,
    IReadOnlyList<string> Groups,
    // Place-based grouping (2026-09-12). PlaceContention + NoPlaceContention is the pair's RECORD contention; asset
    // and explicit edges have no place and sit only in Contention. A composed candidate sums its two pairs' counts
    // and lists the first pair's blocks followed by the second's. Trailing and defaulted so every existing
    // construction of a candidate still means what it meant: no places.
    int PlaceContention = 0,
    int NoPlaceContention = 0,
    IReadOnlyList<PlaceBlock>? PlaceBlocks = null,
    // D-1: the ranking's term — the pair's placed records less the worldspace-wide containers (WRLD records and
    // persistent cells' own CELL records). Never more than PlaceContention.
    int CellContention = 0)
{
    public string SortKey => string.Join(";", Moves.Select(m => m.Key));
    public IReadOnlyList<string> Changes => Moves.Count == 0
        ? new[] { "no change — this is the user's own order" }
        : Moves.Select(m => m.Describe).ToArray();
    public IReadOnlyList<PlaceBlock> Blocks => PlaceBlocks ?? Array.Empty<PlaceBlock>();
    /// <summary>The P3 strings a candidate names: its first 12 blocks and one line counting the rest.</summary>
    public IReadOnlyList<string> Places => OrderSearch.PlaceListing(Blocks);
}

public enum SearchResult
{
    /// <summary>The mandatory rules contradict each other. No candidate is generated; the cycle is the answer.</summary>
    ConstraintsUnsatisfiable,
    /// <summary>Candidates were emitted and the budget was not reached.</summary>
    CandidatesEmitted,
    /// <summary>The budget was reached. Legal candidates exist that were not emitted.</summary>
    BudgetExhausted,
}

/// <summary>One rule's share of the order-fixed prune: how many pairs that rule already ordered, how many edges they
/// carried and what they weighed. The rule is named exactly as a violation would name it, so a reader can grep for one
/// string across a manifest and a report.</summary>
public sealed record ManifestPruneRule(string Rule, int Pairs, int Edges, long Contention);

/// <summary>What the search SPENT, and what it deliberately did not look at. Every number here answers a question a
/// bounded search owes its reader: which pairs were dropped before anything was built and by whose rule, which were too
/// light to matter at the threshold in force, how far down the weight the enumeration actually got, and how many pairs
/// inside a group were never built because the two contest nothing. Without them a reader cannot tell a search that
/// ranked by contention from one that ran out of cap after an alphabetical walk — which is exactly how eight candidates
/// on a 1,696-plugin order came to be chosen by the cap rather than by the weight.</summary>
/// <param name="MinContentionSource">"default" or "--min-contention" — a threshold that was never asked for and one the
/// caller set are different facts about the run.</param>
/// <param name="ReachedContention">The contention of the LIGHTEST pair whose two moves were fully constructed and
/// checked; null when no pair was enumerated at all. Read with the cap: when enumeration was cut short this is the
/// weight the walk got down to, and pairs below it are unexamined, not clean.</param>
/// <remarks>Every COUNT comes before the two lists on purpose: ordersweep's report prints this section truncated, and
/// the numbers a reader judges the run by must survive the truncation. The lists are the detail, and the manifest they
/// live in is named in the same report.</remarks>
public sealed record ManifestSearch(
    int MinContention,
    string MinContentionSource,
    int EdgesSupplied,
    int PairsSupplied,
    int OrderFixedPairs,
    int OrderFixedEdges,
    long OrderFixedContention,
    int BelowThresholdPairs,
    int BelowThresholdEdges,
    long BelowThresholdContention,
    int KeptByRulePairs,
    int WeightedPairs,
    int PairsEnumerated,
    long NoContentionPairsNotBuilt,
    int? ReachedContention,
    int EnumerationCap,
    int OrdersConstructed,
    // Place-based grouping (2026-09-12): the whole-order totals the caller's evidence carried (contested records by
    // the kind of their place, and without one; disputed filings), and what the search made of them. Before the two
    // lists, like every other count here, so they survive ordersweep's truncated print.
    int RecordsWithPlace,
    int RecordsInterior,
    int RecordsExterior,
    int RecordsPersistent,
    int RecordsWorldspace,
    int RecordsWithoutPlace,
    int PlaceDisputed,
    int WeightedPairsWithPlace,
    int WeightedPairsWithoutPlace,
    int GeneratedWithoutPlace,
    // D-1: the containers (WRLD records + persistent cells' own CELL records) over the contested set, and the cell
    // versions of the two search counts — the ranking is on cell contention, so these are the ones a reader judges by.
    int RecordsWorldwideContainers,
    int WeightedPairsWithCell,
    int GeneratedWithoutCell,
    // Whether the caller supplied place evidence at all. When false, every place and cell count above is UNMEASURED
    // — not zero, not all — and a reader who never sees the console must be able to tell that from this section.
    bool PlaceEvidenceSupplied,
    IReadOnlyList<ManifestPruneRule> OrderFixedByRule,
    IReadOnlyList<string> HeaviestOrderFixed);

/// <param name="GeneratedNotEmitted">Legal candidates this search BUILT and the budget kept back. Exact — it is the
/// length of a list the search is holding.</param>
/// <param name="Search">What the search spent and what it pruned. The same record the manifest carries, on purpose:
/// one shape means the console line and the manifest section can never drift apart.</param>
/// <param name="EnumerationCutShort">Whether the search stopped BUILDING candidates early: the enumeration cap was
/// hit, or the composition phase was never run because the budget was already full. When true,
/// <see cref="Unexplored"/> is "unknown" — the two questions "how many did we hold back" and "how many did we never
/// look at" have different answers, and collapsing them is how a bounded search comes to sound exhaustive.</param>
public sealed record SearchReport(
    SearchResult Result,
    IReadOnlyList<InteractionGroup> Groups,
    IReadOnlyList<OrderCandidate> Candidates,
    int Emitted,
    int GeneratedNotEmitted,
    bool EnumerationCutShort,
    IReadOnlyList<string> RejectedMoves,
    IReadOnlyList<string> Notes,
    string Outcome,
    ManifestSearch Search,
    IReadOnlyList<ManifestPlace> Places,
    bool PlaceEvidenceSupplied)
{
    /// <summary>The heading the console prints once before the emitted candidates whose pair contests no CELL (D-1).
    /// Never dropped for having none — listed after it, and counted (GeneratedWithoutCell).</summary>
    public const string NoCellHeading = "candidates whose pair contests no cell (its shared records are items, spells, keywords, quests — or only a worldspace's own record and its persistent cell):";

    /// <summary>The sentence a search run without place evidence carries in its notes, and the console prints in place
    /// of the counts: said once so the two cannot drift, and so a manifest whose place and cell counts read 0 and
    /// "all" is never mistaken for a measurement by a reader who never saw the console.</summary>
    public const string NoPlaceEvidence =
        "no place evidence was supplied to the search, so its place and cell counts (RecordsWithPlace through " +
        "GeneratedWithoutCell) are UNMEASURED — not zero, not all — and no candidate was ranked by cell";

    /// <summary>How many legal candidates the search never built. "unknown" whenever enumeration was cut short —
    /// never 0, because "we stopped looking" is not "there was nothing left".</summary>
    public string Unexplored => EnumerationCutShort ? "unknown" : "0";
}

public static class OrderSearch
{
    /// <summary>The sentence a bounded search owes its reader, spelled once so no caller paraphrases it into a
    /// stronger claim. The roadmap's wording: "No working order found is not proof that no working order exists."</summary>
    public const string NotProof =
        "No working order found within the budget is not proof that none exists: this search looked only at single " +
        "and paired moves inside the interaction groups, and whether an order PASSES is a detector's question, not " +
        "this search's.";

    /// <summary>Connected components over the caller's contention edges. Deterministic: members sorted by name, groups
    /// sorted by their smallest member. A plugin with no edge is in no group and is never moved.</summary>
    public static IReadOnlyList<InteractionGroup> BuildGroups(IEnumerable<ContentionEdge> edges, IReadOnlyList<string> order)
    {
        var present = new HashSet<string>(order, StringComparer.OrdinalIgnoreCase);
        var kept = edges.Where(e => present.Contains(e.A) && present.Contains(e.B)
                                 && !e.A.Equals(e.B, StringComparison.OrdinalIgnoreCase)).ToList();

        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Find(string x)
        {
            if (!parent.TryGetValue(x, out var p)) { parent[x] = x; return x; }
            if (!p.Equals(x, StringComparison.OrdinalIgnoreCase)) { var r = Find(p); parent[x] = r; return r; }
            return x;
        }
        void Union(string a, string b)
        {
            var ra = Find(a); var rb = Find(b);
            if (ra.Equals(rb, StringComparison.OrdinalIgnoreCase)) return;
            // union by NAME, not by rank: the representative must not depend on the order edges arrived in
            if (string.Compare(ra, rb, StringComparison.OrdinalIgnoreCase) <= 0) parent[rb] = ra; else parent[ra] = rb;
        }
        foreach (var e in kept.OrderBy(e => e.A, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.B, StringComparer.OrdinalIgnoreCase))
        { Find(e.A); Find(e.B); Union(e.A, e.B); }

        var byRoot = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in parent.Keys.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            var r = Find(n);
            if (!byRoot.TryGetValue(r, out var l)) byRoot[r] = l = new List<string>();
            l.Add(n);
        }

        var groups = new List<InteractionGroup>();
        foreach (var (root, members) in byRoot)
        {
            var ms = members.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            if (ms.Count < 2) continue;                       // a component of one cannot be reordered against itself
            var memberSet = new HashSet<string>(ms, StringComparer.OrdinalIgnoreCase);
            var es = kept.Where(e => memberSet.Contains(e.A))
                         .OrderBy(e => e.A, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(e => e.B, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(e => e.Kind, StringComparer.Ordinal).ToList();
            groups.Add(new InteractionGroup(ms[0], ms, es));
        }
        return groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A pair of plugins and everything the caller said about it. The search works in PAIRS, not edges: the
    /// rules fix a pair's order, a threshold judges a pair's weight, and one move reverses a pair.</summary>
    sealed class Pair
    {
        public string Earlier = "";                  // earlier in the user's own order
        public string Later = "";
        public int Edges;
        public int Contention;
        public bool Named;                           // a rule names this pair (an "explicit" edge)
        public List<ContentionEdge> Items = new();
        public int PlaceContention;                  // contested records filed in a cell or a worldspace (P2)
        public int CellContention;                   // ...less the worldspace-wide containers (D-1) — the ranking's term
        public int NoPlaceContention;                // contested records with no place
        public PlacePairCounts? Places;              // the per-place counts, when the caller supplied evidence
        public IReadOnlyList<PlaceBlock>? Blocks;    // the P3 partition, built once when a candidate needs it
    }

    /// <summary>The sentence a pair with no contention is owed. Said once so the console line, the manifest note and the
    /// proof case cannot drift into implying those pairs were examined and found clean.</summary>
    public const string NoContentionNotBuilt =
        "not built by design — a reorder of two plugins that contest nothing changes no winner between them";

    /// <summary>The search. <paramref name="budget"/> caps the candidates EMITTED (the baseline is not counted against
    /// it); <paramref name="enumerationCap"/> caps the orders CONSTRUCTED, so a large group cannot turn the pair phase
    /// into a factorial walk. Hitting the second one makes the unexplored count unknown, and says so.
    /// <paramref name="minContention"/> drops pairs weighing less than it before anything is built — 0, the library
    /// default, means no threshold at all; a pair a rule NAMES is exempt, because a rule is the caller's statement that
    /// the pair matters whatever the files weigh. <paramref name="minContentionSource"/> is only reported, so a reader
    /// can tell a threshold nobody asked for from one the caller set. <paramref name="places"/> is the place evidence
    /// (per pair, its contested records by cell or worldspace); null — the default, and what every caller before
    /// 2026-09-12 passes — reproduces the ranking without it exactly.</summary>
    public static SearchReport Run(OrderRuleSet rules, IReadOnlyList<ContentionEdge> edges, int budget,
                                   int enumerationCap = 20000, int minContention = 0, string minContentionSource = "default",
                                   PlaceEvidence? places = null)
    {
        var notes = new List<string>();
        var rejected = new List<string>();
        var baseline = rules.Baseline;
        if (places is null) notes.Add(SearchReport.NoPlaceEvidence);

        var baselineLegality = rules.Check(baseline);
        if (!baselineLegality.Legal)
            notes.Add("the user's own order breaks " + baselineLegality.Violations.Count +
                      " hard rule(s); every candidate below is measured against it anyway, and the breakage is reported per candidate");

        var baselineViolated = rules.ViolatedSoft(baseline).ToList();
        var baselineViolatedKeys = new HashSet<string>(baselineViolated.Select(r => r.Name), StringComparer.Ordinal);

        // "00-baseline" so the file it is written to sorts first beside its alternatives, and so a reader who sees
        // only the directory listing can tell the user's own order from the ones this search invented.
        var baselineCandidate = new OrderCandidate(
            "00-baseline", baseline, Array.Empty<CandidateMove>(), 0, 0, false, 0,
            Array.Empty<OrderRule>(), baselineViolated, baselineLegality, Array.Empty<string>());

        var pos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < baseline.Count; i++) pos[baseline[i]] = i;

        // How contested each pair is, summed over the kinds the caller supplied. This is the only evidence this
        // layer has for the roadmap's "affects a relevant winner": a pair contesting thousands of records is where
        // a reorder changes what the game loads, and a pair contesting two is not. Without it the emitted set is
        // sorted by displacement and then by NAME, which on a real order means the alphabet picks the answer.
        // A pair with no edge weighs 0 — the caller's evidence, never a guess about what two plugins "probably" share.
        var supplied = new Dictionary<string, Pair>(StringComparer.Ordinal);
        foreach (var e in edges)
        {
            if (!pos.ContainsKey(e.A) || !pos.ContainsKey(e.B)) continue;      // not in this order: nothing to reorder
            if (e.A.Equals(e.B, StringComparison.OrdinalIgnoreCase)) continue;
            var k = PairKey(e.A, e.B);
            if (!supplied.TryGetValue(k, out var p))
            {
                bool aFirst = pos[e.A] <= pos[e.B];
                supplied[k] = p = new Pair { Earlier = aFirst ? e.A : e.B, Later = aFirst ? e.B : e.A };
            }
            p.Edges++;
            p.Contention += e.Count;
            p.Named |= e.Kind.Equals("explicit", StringComparison.OrdinalIgnoreCase);
            p.Items.Add(e);
        }
        var suppliedPairs = supplied.Values.OrderBy(p => p.Earlier, StringComparer.OrdinalIgnoreCase)
                                           .ThenBy(p => p.Later, StringComparer.OrdinalIgnoreCase).ToList();

        // The second dimension on the same records (P2): how many of a pair's contested records are filed in a cell
        // or a worldspace, and how many have no place. Only from the caller's evidence — a pair it said nothing about
        // weighs 0 in places, exactly as a pair with no edge weighs 0 in contention.
        if (places is not null)
            foreach (var p in suppliedPairs)
            {
                var pp = places.Of(p.Earlier, p.Later);
                if (pp is null) continue;
                p.PlaceContention = pp.Place;
                p.CellContention = pp.Cell;
                p.NoPlaceContention = pp.NoPlace;
                p.Places = pp;
            }

        ManifestSearch Stats(int fixedPairs, int fixedEdges, long fixedWeight, IReadOnlyList<ManifestPruneRule> byRule,
                             IReadOnlyList<string> heaviest, int belowPairs, int belowEdges, long belowWeight,
                             int keptByRule, int weightedPairs, int enumerated, long noContention, int? reached,
                             int constructedOrders, int weightedWithPlace, int weightedWithoutPlace, int generatedWithoutPlace,
                             int weightedWithCell, int generatedWithoutCell) =>
            new(minContention, minContentionSource, edges.Count, suppliedPairs.Count,
                fixedPairs, fixedEdges, fixedWeight,
                belowPairs, belowEdges, belowWeight, keptByRule,
                weightedPairs, enumerated, noContention, reached, enumerationCap, constructedOrders,
                places?.RecordsWithPlace ?? 0, places?.RecordsInterior ?? 0, places?.RecordsExterior ?? 0,
                places?.RecordsPersistent ?? 0, places?.RecordsWorldspace ?? 0, places?.RecordsWithoutPlace ?? 0,
                places?.PlaceDisputed ?? 0, weightedWithPlace, weightedWithoutPlace, generatedWithoutPlace,
                places?.RecordsWorldwideContainers ?? 0, weightedWithCell, generatedWithoutCell,
                places is not null,
                byRule, heaviest);

        // The mandatory rules contradicting each other is not a search result, it is a refusal: the search would be
        // picking a winner among rules the user said were all required.
        if (!rules.Satisfiable)
            return new SearchReport(
                SearchResult.ConstraintsUnsatisfiable,
                Array.Empty<InteractionGroup>(),
                new[] { baselineCandidate },
                0, 0, false, rejected, notes,

                "REFUSED: the mandatory rules contradict each other, so no order satisfies them. " +
                "This is unsatisfiability of the ENCODED CONSTRAINTS, not a statement about the game. " +
                "Resolve or re-scope one of the rules named below and re-run.",
                Stats(0, 0, 0, Array.Empty<ManifestPruneRule>(), Array.Empty<string>(), 0, 0, 0, 0, 0, 0, 0, null, 0, 0, 0, 0, 0, 0),
                Array.Empty<ManifestPlace>(), places is not null);

        // ---- the prune: pairs no candidate could reverse, then pairs too light to matter -----------------
        // Before grouping, because both kinds of dropped pair would otherwise shape the groups, the weights and the
        // enumeration order. Counted and named, never silently skipped: a reader must be able to see that the pairs
        // missing from the candidate list were removed by a rule or by a threshold, and which.
        int fixedPairs = 0, fixedEdges = 0; long fixedWeight = 0;
        var perRule = new Dictionary<string, (int Pairs, int Edges, long Weight)>(StringComparer.Ordinal);
        var droppedLines = new List<(string Line, int Contention, string Earlier, string Later)>();
        var movable = new List<Pair>();
        foreach (var p in suppliedPairs)
        {
            var fixedBy = rules.OrderFixedBy(p.Earlier, p.Later);
            if (fixedBy is null) { movable.Add(p); continue; }
            fixedPairs++; fixedEdges += p.Edges; fixedWeight += p.Contention;
            (int Pairs, int Edges, long Weight) acc = perRule.TryGetValue(fixedBy, out var cur) ? cur : (0, 0, 0L);
            perRule[fixedBy] = (acc.Pairs + 1, acc.Edges + p.Edges, acc.Weight + p.Contention);
            droppedLines.Add(($"{p.Earlier} | {p.Later} | {p.Contention} | {fixedBy}", p.Contention, p.Earlier, p.Later));
        }
        var byRule = perRule.Select(kv => new ManifestPruneRule(kv.Key, kv.Value.Pairs, kv.Value.Edges, kv.Value.Weight))
                            .OrderBy(r => r.Rule, StringComparer.Ordinal).ToList();
        var heaviestFixed = droppedLines.OrderByDescending(x => x.Contention)
                                        .ThenBy(x => x.Earlier, StringComparer.OrdinalIgnoreCase)
                                        .ThenBy(x => x.Later, StringComparer.OrdinalIgnoreCase)
                                        .Take(10).Select(x => x.Line).ToList();

        int belowPairs = 0, belowEdges = 0, keptByRule = 0; long belowWeight = 0;
        var kept = new List<Pair>();
        foreach (var p in movable)
        {
            bool tooLight = minContention > 0 && p.Contention < minContention;
            if (tooLight && p.Named) { keptByRule++; kept.Add(p); continue; }   // a rule naming a pair outranks a number
            if (tooLight) { belowPairs++; belowEdges += p.Edges; belowWeight += p.Contention; continue; }
            kept.Add(p);
        }

        var weight = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var keptByKey = new Dictionary<string, Pair>(StringComparer.Ordinal);
        foreach (var p in kept) { weight[PairKey(p.Earlier, p.Later)] = p.Contention; keptByKey[PairKey(p.Earlier, p.Later)] = p; }
        int Weight(string a, string b) => weight.TryGetValue(PairKey(a, b), out var w) ? w : 0;
        Pair? KeptPair(string a, string b) => keptByKey.TryGetValue(PairKey(a, b), out var p) ? p : null;
        // The P3 partition, built once per pair the first time a candidate needs it — never for the pairs the walk
        // never reaches, whose block strings nobody would read.
        IReadOnlyList<PlaceBlock> BlocksOf(Pair? p)
        {
            if (p is null || p.Places is null || places is null) return Array.Empty<PlaceBlock>();
            return p.Blocks ??= PlaceBlocksOf(p.Places.ByPlace, places.Describe);
        }

        var groups = BuildGroups(kept.SelectMany(p => p.Items).ToList(), baseline);
        if (groups.Count == 0)
            notes.Add(suppliedPairs.Count == 0
                ? "no interaction group: nothing the caller supplied connects two plugins in this order, so there is nothing to reorder"
                : $"no interaction group: all {suppliedPairs.Count} pair(s) the caller supplied were removed before grouping " +
                  $"({fixedPairs} whose order a hard rule already fixes, {belowPairs} below the contention threshold), so there is nothing left to reorder");

        int constructed = 0;
        bool cutShort = false;
        var byOrder = new Dictionary<string, OrderCandidate>(StringComparer.Ordinal);   // dedupe by the resulting order

        // ---- phase 1: one move per CONTESTED pair, heaviest first, across every group at once ------------
        // One global walk rather than a walk per group in index order. The difference is what the enumeration cap cuts:
        // per-group-in-index-order, the cap cuts the HEAD of what matters (on a 1,696-plugin order the cap was reached
        // inside the first few plugins of one group's million member pairs, so the alphabet chose the candidates); in
        // weight order, the cap cuts the TAIL, and the weight the walk reached is reported so a reader knows where the
        // line fell. Pairs with no surviving edge are not walked at all and are counted instead.
        var memberGroup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups) foreach (var m in g.Members) memberGroup[m] = g.Key;
        var walk = kept.Where(p => memberGroup.ContainsKey(p.Earlier) && memberGroup.ContainsKey(p.Later))
                       .Select(p => new { Pair = p, Group = memberGroup[p.Earlier] })
                       .OrderByDescending(x => x.Pair.Contention)
                       .ThenBy(x => x.Group, StringComparer.OrdinalIgnoreCase)
                       .ThenBy(x => x.Pair.Earlier, StringComparer.OrdinalIgnoreCase)
                       .ThenBy(x => x.Pair.Later, StringComparer.OrdinalIgnoreCase)
                       .ToList();
        long memberPairs = groups.Sum(g => (long)g.Members.Count * (g.Members.Count - 1) / 2);
        long noContentionPairs = memberPairs - walk.Count;
        if (noContentionPairs > 0)
            notes.Add($"{noContentionPairs} pair(s) of plugins inside an interaction group share no contention edge and were {NoContentionNotBuilt}");

        int enumerated = 0;
        int? reached = null;
        var singles = new List<(OrderCandidate Cand, CandidateMove Move)>();
        foreach (var step in walk)
        {
            string earlier = step.Pair.Earlier, later = step.Pair.Later, gkey = step.Group;
            if (constructed + 2 > enumerationCap) { cutShort = true; break; }
            constructed += 2;

            // The two ways to make 'later' load before 'earlier'. Both displace the same span, so the choice
            // is usually the tie-break below — but only one of them may be LEGAL (a master sitting between
            // them can forbid one direction), which is the case this branch exists for.
            var o1 = Move(baseline, later, earlier, before: true);
            var o2 = Move(baseline, earlier, later, before: false);
            var m1 = new CandidateMove(later, "before", earlier, pos[later], IndexOf(o1, later), gkey);
            var m2 = new CandidateMove(earlier, "after", later, pos[earlier], IndexOf(o2, earlier), gkey);

            var l1 = rules.Check(o1); var l2 = rules.Check(o2);
            enumerated++;
            reached = step.Pair.Contention;                 // the walk is heaviest-first, so this is the lightest so far
            int d1 = Displaced(baseline, o1), d2 = Displaced(baseline, o2);

            if (!l1.Legal && !l2.Legal)
            {
                rejected.Add($"[{gkey}] making '{later}' load before '{earlier}' is forbidden both ways: " +
                             $"{m1.Describe} breaks {Names(l1)}; {m2.Describe} breaks {Names(l2)}");
                continue;
            }
            var (order, move, legality, disp) =
                !l2.Legal ? (o1, m1, l1, d1)
              : !l1.Legal ? (o2, m2, l2, d2)
              : d2 < d1 ? (o2, m2, l2, d2) : (o1, m1, l1, d1);
            if (!l1.Legal || !l2.Legal)
                rejected.Add($"[{gkey}] the other way round ({(l1.Legal ? m2.Describe : m1.Describe)}) is forbidden: breaks {Names(l1.Legal ? l2 : l1)}");

            var cand = Make(rules, order, new[] { move }, disp, phase: 1, Weight(earlier, later), baselineViolatedKeys, new[] { gkey }, legality,
                            step.Pair.PlaceContention, step.Pair.NoPlaceContention, BlocksOf(step.Pair), step.Pair.CellContention);
            if (!Permutation(baseline, order))
            { notes.Add($"DROPPED a candidate that was not a permutation of the user's order ({move.Describe}) — one plugin has one position, and a candidate that loses or repeats one is a defect, not an option"); continue; }
            if (byOrder.TryAdd(OrderKey(order), cand)) singles.Add((cand, move));
        }

        // ---- phase 2: pairs of phase-1 moves, only after every single one, only while the budget lasts ----
        // "Only while the budget lasts" is literal: a composed candidate is emitted after every single one, so if
        // the singles already fill the budget no composition can ever be emitted and building them would be work
        // nobody reads. The phase is then SKIPPED — and that is enumeration being cut short, which is why the
        // unexplored count below goes to "unknown" rather than quietly to 0.
        var pairs = new List<OrderCandidate>();
        var singleList = singles.OrderBy(s => s.Cand.SortKey, StringComparer.Ordinal).ToList();
        bool pairPhaseSkipped = singleList.Count >= Math.Max(0, budget);
        if (pairPhaseSkipped)
            notes.Add($"the composition phase was not run: {singleList.Count} single-move candidate(s) already fill a budget of {budget}, " +
                      "so no composed candidate could have been emitted. Composed orders are therefore UNEXPLORED, not absent.");
        for (int i = 0; i < singleList.Count && !cutShort && !pairPhaseSkipped; i++)
            for (int j = i + 1; j < singleList.Count; j++)
            {
                if (constructed + 1 > enumerationCap) { cutShort = true; break; }
                constructed++;
                var mi = singleList[i].Move; var mj = singleList[j].Move;
                if (mi.Moved.Equals(mj.Moved, StringComparison.OrdinalIgnoreCase)) continue;   // the second would just replace the first
                var order = Move(Move(baseline, mi.Moved, mi.Reference, mi.Placement == "before"),
                                 mj.Moved, mj.Reference, mj.Placement == "before");
                if (OrderKey(order) == OrderKey(baseline)) continue;
                var leg = rules.Check(order);
                if (!leg.Legal)
                { rejected.Add($"composing ({mi.Describe}) with ({mj.Describe}) is forbidden: breaks {Names(leg)}"); continue; }
                if (!Permutation(baseline, order))
                { notes.Add($"DROPPED a composed candidate that was not a permutation of the user's order ({mi.Describe} + {mj.Describe})"); continue; }
                var moves = new[]
                {
                    mi with { To = IndexOf(order, mi.Moved) },
                    mj with { To = IndexOf(order, mj.Moved) },
                };
                // A composed candidate's place contention is the sum of its two pairs', and its places are the first
                // pair's blocks followed by the second's — the two pairs' cells are never re-joined across pairs.
                var pi = KeptPair(mi.Moved, mi.Reference); var pj = KeptPair(mj.Moved, mj.Reference);
                var cand = Make(rules, order, moves, Displaced(baseline, order), phase: 2,
                                Weight(mi.Moved, mi.Reference) + Weight(mj.Moved, mj.Reference), baselineViolatedKeys,
                                new[] { mi.Group, mj.Group }.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray(),
                                leg,
                                (pi?.PlaceContention ?? 0) + (pj?.PlaceContention ?? 0),
                                (pi?.NoPlaceContention ?? 0) + (pj?.NoPlaceContention ?? 0),
                                BlocksOf(pi).Concat(BlocksOf(pj)).ToList(),
                                (pi?.CellContention ?? 0) + (pj?.CellContention ?? 0));
                if (byOrder.TryAdd(OrderKey(order), cand)) pairs.Add(cand);
            }
        if (cutShort)
            notes.Add($"enumeration stopped at the cap of {enumerationCap} constructed orders, so the number of legal candidates NOT emitted is unknown, not zero");

        // ---- ordering and the budget ---------------------------------------------------------------------
        // Phase-1 candidates before phase-2; within a phase (P4 as amended by D-1): satisfies advice the baseline
        // violates first, then CELL contention descending, then total contention descending, then the fewest
        // displaced, then the sort key.
        var ordered = Ranked(singleList.Select(s => s.Cand)).Concat(Ranked(pairs)).ToList();

        // The places table (P4, the grouping): after the prune and the threshold, every place any MOVABLE pair
        // contests — how many such pairs meet there, the sum of their counts there, and the union of their plugins.
        var table = PlacesTable(kept.Where(p => p.Places is not null).Select(p => (p.Earlier, p.Later, p.Places!)), places);
        int weightedWithPlace = kept.Count(p => p.PlaceContention > 0);
        int weightedWithCell = kept.Count(p => p.CellContention > 0);
        int generatedWithoutPlace = ordered.Count(c => c.PlaceContention == 0);
        int generatedWithoutCell = ordered.Count(c => c.CellContention == 0);

        var emitted = ordered.Take(Math.Max(0, budget)).ToList();
        int notEmitted = ordered.Count - emitted.Count;
        bool enumerationCutShort = cutShort || pairPhaseSkipped;
        bool budgetBound = emitted.Count >= Math.Max(0, budget) && (notEmitted > 0 || pairPhaseSkipped);

        var named = new List<OrderCandidate> { baselineCandidate };
        for (int i = 0; i < emitted.Count; i++) named.Add(emitted[i] with { Name = Slug(i + 1, emitted[i]) });

        var result = budgetBound ? SearchResult.BudgetExhausted : SearchResult.CandidatesEmitted;
        var outcome = new StringBuilder();
        outcome.Append(result == SearchResult.BudgetExhausted
            ? $"BUDGET EXHAUSTED: {emitted.Count} candidate(s) emitted against a budget of {budget}; "
            : $"{emitted.Count} candidate(s) emitted against a budget of {budget}; ");
        outcome.Append($"{notEmitted} further legal candidate(s) were generated and held back; ");
        outcome.Append(enumerationCutShort
            ? "and enumeration itself stopped early, so the number of legal candidates never built is UNKNOWN, not zero. "
            : "and enumeration ran to completion, so nothing further was left unbuilt. ");
        if (emitted.Count == 0 && ordered.Count == 0)
            outcome.Append("No legal single move exists inside any interaction group — which is a fact about THIS " +
                           "search's moves, not about the game. ");
        outcome.Append(NotProof);

        return new SearchReport(result, groups, named, emitted.Count,
                                notEmitted, enumerationCutShort, rejected, notes, outcome.ToString(),
                                Stats(fixedPairs, fixedEdges, fixedWeight, byRule, heaviestFixed,
                                      belowPairs, belowEdges, belowWeight, keptByRule,
                                      kept.Count, enumerated, noContentionPairs, reached, constructed,
                                      weightedWithPlace, kept.Count - weightedWithPlace, generatedWithoutPlace,
                                      weightedWithCell, generatedWithoutCell),
                                table, places is not null);
    }

    /// <summary>The P4 ranking (D-1), in one place so the singles and the composed candidates cannot drift apart: advice
    /// relevance, then CELL contention (descending), then total contention (descending), then displaced (ascending),
    /// then the sort key.</summary>
    static IEnumerable<OrderCandidate> Ranked(IEnumerable<OrderCandidate> cands) =>
        cands.OrderBy(c => c.Relevant ? 0 : 1)
             .ThenByDescending(c => c.CellContention)
             .ThenByDescending(c => c.Contention)
             .ThenBy(c => c.Displaced)
             .ThenBy(c => c.SortKey, StringComparer.Ordinal);

    /// <summary>The places table over the pairs given (the movable pairs that survived the threshold), sorted by
    /// contention descending, then pairs descending, then name (ordinal), then id. Empty without evidence.
    /// The table is "where movable pairs meet", so it counts CELL contention (D-2): a worldspace place is not a row; a
    /// persistent cell's row counts the placed records inside it, the cell's own CELL record excluded, and a pair counts
    /// toward Pairs there only when it has at least one such record; interior and exterior rows count every record
    /// filed in the cell, the cell's own CELL record included. The per-place counts (P2) and the blocks (P3) are not
    /// changed by this — a block still names the persistent cell with its CELL record and the worldspace.</summary>
    static IReadOnlyList<ManifestPlace> PlacesTable(IEnumerable<(string A, string B, PlacePairCounts Counts)> pairs, PlaceEvidence? places)
    {
        if (places is null) return Array.Empty<ManifestPlace>();
        var rows = new Dictionary<string, (int Pairs, long Contention, HashSet<string> Plugins)>(StringComparer.Ordinal);
        foreach (var (a, b, counts) in pairs)
            foreach (var (id, count) in counts.ByPlace)
            {
                var kind = places.Describe(id)?.Kind;
                if (kind is null || kind == "worldspace") continue;                 // the WRLD record is a container, not a meeting
                int n = count;
                if (kind == "persistent" && counts.ContainerAt is { } at && at.Contains(id)) n--;   // the cell's own CELL record
                if (n <= 0) continue;
                if (!rows.TryGetValue(id, out var r)) rows[id] = r = (0, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                r.Plugins.Add(a); r.Plugins.Add(b);
                rows[id] = (r.Pairs + 1, r.Contention + n, r.Plugins);
            }
        var list = new List<ManifestPlace>();
        foreach (var (id, r) in rows)
        {
            var d = places.Describe(id);
            if (d is null) continue;
            var plugins = r.Plugins.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            var named = plugins.Take(8).ToList();
            if (plugins.Count > 8) named.Add($"and {plugins.Count - 8} more");
            list.Add(new ManifestPlace(d.Id, d.Name, d.Kind, d.WorldspaceName, d.Kind == "exterior" ? d.Grid : null,
                                       r.Pairs, r.Contention, named));
        }
        return list.OrderByDescending(x => x.Contention).ThenByDescending(x => x.Pairs)
                   .ThenBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>The P3 partition of one pair's places into blocks: each interior cell, persistent cell and worldspace
    /// place is a block of its own; exterior cells of one worldspace are joined by EDGE adjacency — the same
    /// worldspace, and grids that differ by exactly one in x and not at all in y, or by exactly one in y and not at
    /// all in x (four neighbours; a diagonal neighbour is NOT adjacent) — over the cells THIS pair contests only.
    /// A block's count is the sum of its cells'; it is named by its heaviest cell (ties: the lowest x, then the lowest
    /// y). Blocks sort by count descending, then by name (ordinal).</summary>
    public static IReadOnlyList<PlaceBlock> PlaceBlocksOf(IReadOnlyDictionary<string, int> byPlace, Func<string, PlaceDescriptor?> describe)
    {
        var cells = byPlace.Where(kv => kv.Value > 0)
                           .Select(kv => (D: describe(kv.Key), Count: kv.Value))
                           .Where(x => x.D is not null)
                           .Select(x => (D: x.D!, x.Count))
                           .OrderBy(x => x.D.Id, StringComparer.Ordinal).ToList();
        int n = cells.Count;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a == b) return; if (a < b) parent[b] = a; else parent[a] = b; }

        var at = new Dictionary<(string Ws, int X, int Y), int>();
        for (int i = 0; i < n; i++)
        {
            var d = cells[i].D;
            if (d.Kind == "exterior" && d.WorldspaceId is { } ws && d.X is { } x && d.Y is { } y) at[(ws, x, y)] = i;
        }
        foreach (var ((ws, x, y), i) in at)
        {
            // exactly one of dx, dy is ±1 and the other 0 — the four edge neighbours, never the diagonal
            if (at.TryGetValue((ws, x + 1, y), out var e)) Union(i, e);
            if (at.TryGetValue((ws, x, y + 1), out var s)) Union(i, s);
        }

        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            var r = Find(i);
            if (!groups.TryGetValue(r, out var l)) groups[r] = l = new List<int>();
            l.Add(i);
        }

        var blocks = new List<PlaceBlock>();
        foreach (var members in groups.Values)
        {
            int count = members.Sum(i => cells[i].Count);
            var heaviest = members.OrderByDescending(i => cells[i].Count)
                                  .ThenBy(i => cells[i].D.X ?? int.MaxValue)
                                  .ThenBy(i => cells[i].D.Y ?? int.MaxValue)
                                  .ThenBy(i => cells[i].D.Name, StringComparer.Ordinal).First();
            var d = cells[heaviest].D;
            string spelling;
            if (d.Kind == "exterior")
            {
                if (members.Count == 1)
                    spelling = $"{d.Name} [{d.WorldspaceName} {d.Grid}] — {count} record(s)";
                else
                {
                    var xs = members.Select(i => cells[i].D.X ?? 0).ToList();
                    var ys = members.Select(i => cells[i].D.Y ?? 0).ToList();
                    spelling = $"{d.Name} + {members.Count - 1} more cell(s) [{d.WorldspaceName} ({xs.Min()}..{xs.Max()}, {ys.Min()}..{ys.Max()})] — {count} record(s)";
                }
            }
            else if (d.Kind == "worldspace")
                spelling = $"{d.Name} (worldspace record) — {count} record(s)";
            else
                spelling = $"{d.Name} — {count} record(s)";
            blocks.Add(new PlaceBlock(d.Name, spelling, count, members.Count));
        }
        return blocks.OrderByDescending(b => b.Count)
                     .ThenBy(b => b.Name, StringComparer.Ordinal)
                     .ThenBy(b => b.Spelling, StringComparer.Ordinal).ToList();
    }

    /// <summary>What a candidate names: its first 12 blocks and, when there are more, one line counting the rest —
    /// <c>and K more block(s), M record(s)</c>.</summary>
    public static IReadOnlyList<string> PlaceListing(IReadOnlyList<PlaceBlock> blocks)
    {
        const int Named = 12;
        var lines = blocks.Take(Named).Select(b => b.Spelling).ToList();
        if (blocks.Count > Named)
            lines.Add($"and {blocks.Count - Named} more block(s), {blocks.Skip(Named).Sum(b => b.Count)} record(s)");
        return lines;
    }

    // -----------------------------------------------------------------------------------------------------

    /// <param name="legality">The legality of <paramref name="order"/>, already computed by the caller that built it.
    /// Passed in rather than recomputed: Check is the search's dominant cost on a real order, and calling it a third
    /// time for an answer the caller is holding buys nothing.</param>
    static OrderCandidate Make(OrderRuleSet rules, IReadOnlyList<string> order, IReadOnlyList<CandidateMove> moves,
                               int displaced, int phase, int contention, HashSet<string> baselineViolated,
                               IReadOnlyList<string> groups, LegalityResult legality,
                               int placeContention, int noPlaceContention, IReadOnlyList<PlaceBlock> blocks, int cellContention)
    {
        var violates = rules.ViolatedSoft(order);
        var stillViolated = new HashSet<string>(violates.Select(r => r.Name), StringComparer.Ordinal);
        var satisfies = rules.Input.Where(r => r.Hardness != RuleHardness.Required)
                                   .Where(r => baselineViolated.Contains(r.Name) && !stillViolated.Contains(r.Name))
                                   .ToList();
        return new OrderCandidate("", order, moves, displaced, phase, satisfies.Count > 0, contention,
                                  satisfies, violates, legality, groups, placeContention, noPlaceContention, blocks, cellContention);
    }

    static string Names(LegalityResult l) => string.Join("; ", l.Violations.Select(v => v.Rule).Distinct());

    /// <summary>Move <paramref name="what"/> to sit immediately before (or after) <paramref name="reference"/>.</summary>
    public static List<string> Move(IReadOnlyList<string> order, string what, string reference, bool before)
    {
        var l = order.ToList();
        int from = l.FindIndex(n => n.Equals(what, StringComparison.OrdinalIgnoreCase));
        if (from < 0) return l;
        l.RemoveAt(from);
        int at = l.FindIndex(n => n.Equals(reference, StringComparison.OrdinalIgnoreCase));
        if (at < 0) return order.ToList();
        l.Insert(before ? at : at + 1, what);
        return l;
    }

    public static int Displaced(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        int n = 0;
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
            if (!a[i].Equals(b[i], StringComparison.OrdinalIgnoreCase)) n++;
        return n + Math.Abs(a.Count - b.Count);
    }

    static int IndexOf(IReadOnlyList<string> order, string name)
    {
        for (int i = 0; i < order.Count; i++) if (order[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>The one key a pair of plugins has, whichever way round it is asked — shared with the place evidence
    /// so a pair's place counts and its edges can never be filed under two spellings of the same pair.</summary>
    public static string PairKey(string a, string b) =>
        string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0
            ? a.ToLowerInvariant() + "|" + b.ToLowerInvariant()
            : b.ToLowerInvariant() + "|" + a.ToLowerInvariant();

    static string OrderKey(IReadOnlyList<string> order) => string.Join("\n", order.Select(s => s.ToLowerInvariant()));

    /// <summary>One plugin, one position: the candidate must be the user's own set, rearranged. Checked before a
    /// candidate is emitted so a search bug becomes a dropped candidate with a note, never a file the consumer runs.</summary>
    static bool Permutation(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        var sa = a.Select(s => s.ToLowerInvariant()).OrderBy(s => s, StringComparer.Ordinal);
        var sb = b.Select(s => s.ToLowerInvariant()).OrderBy(s => s, StringComparer.Ordinal);
        return sa.SequenceEqual(sb, StringComparer.Ordinal);
    }

    static string Slug(int index, OrderCandidate c)
    {
        var m = c.Moves[0];
        var s = $"{m.Moved}-{m.Placement}-{m.Reference}";
        var sb = new StringBuilder();
        foreach (var ch in s.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        var slug = sb.ToString();
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        if (slug.Length > 60) slug = slug[..60].Trim('-');
        return $"{index:00}-{slug}" + (c.Moves.Count > 1 ? "-plus1" : "");
    }
}

// ---------------------------------------------------------------------------------------------------------
//  THE MANIFEST — what orderprobe writes next to the candidate files. Data only, no clock: two runs over the
//  same configuration produce byte-identical manifests, which is what makes "did anything change?" answerable
//  by comparing them. Run timing lives in the run log, where it belongs.
// ---------------------------------------------------------------------------------------------------------

public sealed record ManifestProvenance(string Source, string Checked, string VersionScope, string? Note);

public sealed record ManifestRule(string Before, string After, string Hardness, string Origin, string? Reason,
                                  ManifestProvenance? Provenance, bool Applicable);

public sealed record ManifestContradiction(string Kind, IReadOnlyList<string> Rules, string Explanation);

public sealed record ManifestGroup(string Key, IReadOnlyList<string> Members, int Contention,
                                   IReadOnlyList<string> Edges);

/// <param name="PlaceContention">Contested records between the moved plugins that are filed in a cell or a worldspace;
/// <paramref name="CellContention"/> those less the worldspace-wide containers (the ranking's term, D-1);
/// <paramref name="NoPlaceContention"/> those with no place. <paramref name="PlaceBlocks"/> is the number of P3 blocks
/// and <paramref name="Places"/> their strings — the first 12 and one line counting the rest. Trailing and defaulted:
/// a manifest built without place evidence reads exactly as it did.</param>
public sealed record ManifestCandidate(string Name, string File, IReadOnlyList<string> Changes, int Displaced,
                                       int Contention, int Moves, int Phase, IReadOnlyList<string> Groups, bool Legal,
                                       IReadOnlyList<string> Violations, IReadOnlyList<string> SatisfiesAdvice,
                                       IReadOnlyList<string> ViolatesAdvice,
                                       int PlaceContention = 0, int CellContention = 0, int NoPlaceContention = 0, int PlaceBlocks = 0,
                                       IReadOnlyList<string>? Places = null);

public sealed record ManifestBudget(int Limit, string Result, int Emitted, int GeneratedNotEmitted,
                                    string Unexplored, bool EnumerationCutShort, string Outcome);

public sealed record OrderManifest(
    int SchemaVersion,
    string Probe,
    string Instance,
    string Profile,
    ConfigurationIdentity Baseline,
    IReadOnlyList<string> FixedPlugins,
    IReadOnlyList<ManifestRule> Rules,
    IReadOnlyList<string> RuleNotes,
    IReadOnlyList<ManifestContradiction> Contradictions,
    IReadOnlyList<ManifestContradiction> UnsatisfiableAdvice,
    IReadOnlyList<ManifestGroup> Groups,
    IReadOnlyList<ManifestCandidate> Candidates,
    ManifestBudget Budget,
    // What the search spent and what it pruned: the record the search itself carries (ManifestSearch, declared beside
    // SearchReport), so the manifest cannot report different numbers from the console.
    ManifestSearch Search,
    IReadOnlyList<string> RejectedMoves,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Limits,
    // The places table (P4): the top 50 places any movable pair contests, by contention. Trailing and defaulted, so a
    // manifest built without place evidence carries no table rather than an empty one.
    IReadOnlyList<ManifestPlace>? Places = null)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public void Write(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, Serialize());
    }

    public static ManifestProvenance? Prov(RuleProvenance? p) =>
        p is null ? null : new ManifestProvenance(p.Source, p.Checked, p.VersionScope, p.Note);

    public static ManifestContradiction Of(Contradiction c) =>
        new(c.Kind.ToString(), c.Rules.Select(r => r.Full).ToList(), c.Explanation);
}
