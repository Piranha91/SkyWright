using Mutagen.Bethesda.Plugins;

namespace HarnessShared;

// NEWCOMER SELECTION, RULE v2 (roadmap 4.26, selection v2). Read NewcomerSelection.cs (rule v1) first: v2 keeps v1's
// five selecting kinds, its containers, rings and removals unchanged, and adds three things.
//
//   formLinks   a selecting kind: N's records point at records B carries, or B's records point at records N carries,
//               outside shared records and placement bases (v1 noted these and never let them select). B is selected
//               by links alone when it has at least LinkMin distinct linked records (both directions together).
//   features    every candidate carries the facts its score reads, all generic (no feature names a plugin): the count
//               of each kind, shared records by group, the noted counters, B's and N's sizes, and SPECIFICITY — for
//               each kind, the sum over the items B shares with N of 1 / (the number of installed plugins sharing that
//               item with N the same way). An item only B shares scores 1; one forty plugins share scores 1/40. The
//               newcomer itself is never counted; plugins that leave the order with it are not either.
//   ranking     R-A, chosen on development data only (4.26 v2 Gate B; run 2b954f2b, dev/fit/fit.json): the number of
//               selecting kinds met (form links one more kind; this number is Score), then shared cells, shared
//               records, shared assets, base links, form links (out + in), then plugin name. The family it was chosen
//               from (a log-count sum, a specificity sum, a fitted logistic score) and the threshold {1, 2, 4} are in the
//               run folder. controlRank is the newcomer-blind control R-0: B's own record count, then its cell count,
//               then its name. It uses nothing about the newcomer, so a ranking that beats it is using the pair, not a
//               prior on large mods.

public sealed record SelEvidenceV2(int Records, IReadOnlyList<string> RecordExamples, int Cells, IReadOnlyList<string> CellExamples,
                                   int Neighbours, IReadOnlyList<string> NeighbourExamples, int Assets, IReadOnlyList<string> AssetExamples,
                                   int NewcomerPlacesTheirBase, int TheyPlaceNewcomerBase, IReadOnlyList<string> BaseLinkExamples,
                                   int FormLinksOut, int FormLinksIn, IReadOnlyList<string> FormLinkExamples);
public sealed record SelCandidateV2(string Plugin, string? Folder, int Rank, int ControlRank, double Score, IReadOnlyList<string> Kinds,
                                    SelEvidenceV2 Evidence, IReadOnlyDictionary<string, double> Features, SelNoted Noted);
public sealed record SelRuleV2(string Version, int Ring, IReadOnlyList<string> Containers, IReadOnlyList<string> Selecting,
                               IReadOnlyList<string> Noted, bool ReverseLinks, int LinkMin, string Ranking, string ControlRanking);
public sealed record SelectionV2(
    string Tool, int Schema, SelNewcomer Newcomer, SelOrder Order, IReadOnlyList<SelNamed> Dependencies,
    IReadOnlyList<SelCandidateV2> Candidates, IReadOnlyList<SelAssetOnly> AssetOnlyFolders, IReadOnlyList<SelExcluded> Excluded,
    IReadOnlyList<SelNamed> Unreadable, bool Incomplete, IReadOnlyList<string> Unmeasured, SelRuleV2 Rule,
    IReadOnlyDictionary<string, object?>? Cost);

/// <summary>The items one installed plugin shares with the newcomer, kind by kind (the sets v1's Judge counts).</summary>
public sealed class PairItems
{
    public readonly List<FormKey> Records = new();
    public readonly List<string> Cells = new();                // "I:<fk>" or "E:<ws> (x,y)"
    public readonly List<string> Neighbours = new();           // "<ws> (x,y)", B's cell
    public readonly List<string> Assets = new();
    public readonly List<string> BaseLinks = new();            // "o:<fk>" N places B's, "i:<fk>" B places N's
    public readonly List<FormKey> LinksOut = new();            // N points at a record B carries
    public readonly List<FormKey> LinksIn = new();             // B points at a record N carries
}

public static class SelectionRuleV2
{
    public const string Version = "v2";
    public const int LinkMin = 1;
    public static readonly string[] SelectingKinds = { "records", "cells", "neighbours", "assets", "baseLinks", "formLinks" };
    public static readonly string[] NotedKinds = { "containerRecords", "ring2Cells", "sharedMasters" };
    public const string Ranking = "R-A: selecting kinds met (score; form links count as one kind), then shared cells, shared records, shared assets, base links, form links (out + in), then plugin name";
    public const string ControlRanking = "R-0, newcomer-blind: the installed plugin's own record count (more first), then its cell count, then plugin name";

    /// <summary>The shared items per kind, with the same definitions as <see cref="SelectionRule.Judge"/>.</summary>
    public static PairItems Items(NewcomerFacts n, InstalledFacts b)
    {
        var N = n.Fp; var B = b.Fp; var it = new PairItems();
        var shared = new HashSet<FormKey>(N.Keys); shared.IntersectWith(B.Keys);
        foreach (var k in shared) if (!N.Containers.Contains(k) && !B.Containers.Contains(k)) it.Records.Add(k);
        foreach (var c in N.Interior) if (B.Interior.Contains(c)) it.Cells.Add("I:" + c);
        foreach (var c in N.Exterior) if (B.Exterior.Contains(c)) it.Cells.Add("E:" + Footprint.ExtKey(c.Ws, c.X, c.Y));
        var ring1 = new HashSet<(FormKey, int, int)>();
        foreach (var (ws, x, y) in N.Exterior)
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    var c = (ws, x + dx, y + dy);
                    if (!N.Exterior.Contains(c)) ring1.Add(c);
                }
        foreach (var c in B.Exterior) if (ring1.Contains(c)) it.Neighbours.Add(Footprint.ExtKey(c.Ws, c.X, c.Y));
        foreach (var a in n.Assets) if (b.Assets.Contains(a)) it.Assets.Add(a);
        foreach (var k in N.PlacedBases) if (B.Keys.Contains(k) && !shared.Contains(k)) it.BaseLinks.Add("o:" + k);
        foreach (var k in B.PlacedBases) if (N.Keys.Contains(k) && !shared.Contains(k)) it.BaseLinks.Add("i:" + k);
        if (N.Links is not null) foreach (var k in N.Links) if (B.Keys.Contains(k) && !shared.Contains(k) && !N.PlacedBases.Contains(k)) it.LinksOut.Add(k);
        if (B.Links is not null) foreach (var k in B.Links) if (N.Keys.Contains(k) && !shared.Contains(k) && !B.PlacedBases.Contains(k)) it.LinksIn.Add(k);
        return it;
    }

    /// <summary>How many judged installed plugins share each item with the newcomer, per kind.</summary>
    public sealed class Multiplicity
    {
        public readonly Dictionary<FormKey, int> Records = new(), LinksOut = new(), LinksIn = new();
        public readonly Dictionary<string, int> Cells = new(StringComparer.Ordinal), Neighbours = new(StringComparer.Ordinal),
                                                Assets = new(StringComparer.Ordinal), BaseLinks = new(StringComparer.Ordinal);
        static void Add<T>(Dictionary<T, int> d, IEnumerable<T> xs) where T : notnull { foreach (var x in xs) d[x] = d.TryGetValue(x, out var c) ? c + 1 : 1; }
        public void Add(PairItems it)
        {
            Add(Records, it.Records); Add(Cells, it.Cells); Add(Neighbours, it.Neighbours); Add(Assets, it.Assets);
            Add(BaseLinks, it.BaseLinks); Add(LinksOut, it.LinksOut); Add(LinksIn, it.LinksIn);
        }
    }

    static double Spec<T>(IEnumerable<T> xs, Dictionary<T, int> df) where T : notnull => xs.Sum(x => 1.0 / df[x]);

    static string Group(string? type) => type switch
    {
        null => "recOther",
        _ when type.StartsWith("Placed", StringComparison.Ordinal) || type is "NavigationMesh" or "Landscape" or "Cell" => "recPlaced",
        "Npc" or "Race" => "recActor",
        "ConstructibleObject" or "Armor" or "Weapon" or "Ingestible" or "Ingredient" or "MiscItem" or "LeveledItem" or "Container" => "recItem",
        "Spell" or "MagicEffect" or "Perk" or "ObjectEffect" => "recMagic",
        "Quest" or "DialogTopic" or "DialogResponses" or "Scene" => "recQuest",
        _ => "recOther",
    };

    /// <summary>The features one candidate's score reads. Every value is a fact of N, B and the order.</summary>
    public static Dictionary<string, double> Features(NewcomerFacts n, InstalledFacts b, PairItems it, SelNoted noted, Multiplicity df)
    {
        var f = new Dictionary<string, double>
        {
            ["rec"] = it.Records.Count, ["cell"] = it.Cells.Count, ["nb"] = it.Neighbours.Count, ["ast"] = it.Assets.Count,
            ["bl"] = it.BaseLinks.Count, ["flOut"] = it.LinksOut.Count, ["flIn"] = it.LinksIn.Count,
            ["ring2"] = noted.Ring2Cells, ["containers"] = noted.ContainerRecords, ["sharedMasters"] = noted.SharedMasters.Count,
            ["recPlaced"] = 0, ["recActor"] = 0, ["recItem"] = 0, ["recMagic"] = 0, ["recQuest"] = 0, ["recOther"] = 0,
        };
        foreach (var k in it.Records)
        {
            var type = n.Fp.Info is not null && n.Fp.Info.TryGetValue(k, out var i) ? i.Type : null;
            f[Group(type)] += 1;
        }
        f["specRec"] = Spec(it.Records, df.Records);
        f["specCell"] = Spec(it.Cells, df.Cells);
        f["specNb"] = Spec(it.Neighbours, df.Neighbours);
        f["specAst"] = Spec(it.Assets, df.Assets);
        f["specBl"] = Spec(it.BaseLinks, df.BaseLinks);
        f["specFl"] = Spec(it.LinksOut, df.LinksOut) + Spec(it.LinksIn, df.LinksIn);
        f["bRecords"] = b.Fp.Keys.Count;
        f["bCells"] = b.Fp.Interior.Count + b.Fp.Exterior.Count;
        f["nRecords"] = n.Fp.Keys.Count;
        f["nCells"] = n.Fp.Interior.Count + n.Fp.Exterior.Count;
        f["nAssets"] = n.Assets.Count;
        return f;
    }

    public static bool LinksSelect(Dictionary<string, double> f) => f["flOut"] + f["flIn"] >= LinkMin;

    /// <summary>R-A's first key: the number of selecting kinds met.</summary>
    public static double Score(IReadOnlyList<string> kinds) => kinds.Count;

    /// <summary>Order the candidates by score and assign the control rank R-0; the order never drops anything.</summary>
    public static List<SelCandidateV2> Rank(List<(string Plugin, string? Folder, List<string> Kinds, SelEvidenceV2 Ev, Dictionary<string, double> F, SelNoted Noted)> picked)
    {
        var control = picked.OrderByDescending(p => p.F["bRecords"]).ThenByDescending(p => p.F["bCells"])
                            .ThenBy(p => p.Plugin, StringComparer.OrdinalIgnoreCase)
                            .Select((p, i) => (p.Plugin, i + 1)).ToDictionary(x => x.Plugin, x => x.Item2, StringComparer.OrdinalIgnoreCase);
        return picked.Select(p => (p, s: Score(p.Kinds)))
                     .OrderByDescending(x => x.s).ThenByDescending(x => x.p.F["cell"]).ThenByDescending(x => x.p.F["rec"])
                     .ThenByDescending(x => x.p.F["ast"]).ThenByDescending(x => x.p.F["bl"]).ThenByDescending(x => x.p.F["flOut"] + x.p.F["flIn"])
                     .ThenBy(x => x.p.Plugin, StringComparer.OrdinalIgnoreCase)
                     .Select((x, i) => new SelCandidateV2(x.p.Plugin, x.p.Folder, i + 1, control[x.p.Plugin], Math.Round(x.s, 9), x.p.Kinds, x.p.Ev,
                                                          x.p.F.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 9)), x.p.Noted))
                     .ToList();
    }

    public static string ExcludedReason(SelNoted noted, int links)
    {
        var r = SelectionRule.ExcludedReason(noted);
        return links > 0 && links < LinkMin ? $"{r}; {links} linked record(s), below v2's form-link threshold of {LinkMin}" : r;
    }
}
