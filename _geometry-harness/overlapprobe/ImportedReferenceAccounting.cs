using System.Text.Json;
using Mutagen.Bethesda.Plugins;

/// <summary>
/// ONE DISPOSITION PER IMPORTED IDENTITY. When a capture imports a scope, the scope's references
/// are identities the consumer is entitled to ask about — so the manifest has to say what became of
/// every one of them, not merely list the ones it happened to measure.
///
/// The receipt used to answer with two filters that are not complementary: the record loop skips a
/// reference with no winning pose, while <c>missingImportedReferences</c> names references with no
/// winner PLACEMENT HISTORY. An identity with history and no usable pose satisfies neither, so it was
/// emitted nowhere and a consumer counting rows could only guess. (In the current ingestion path that
/// state does not arise — OverlapIndex.Record writes the history snap and the effective pose together
/// — but "total because a different class happens to keep an invariant" is not an accounting; it is a
/// coincidence, and the consumer contract is written against the manifest, not against that class.)
///
/// So the partition here is total BY CONSTRUCTION: every imported identity is classified exactly
/// once, the counts add up to the imported count, and <c>missingImportedReferences</c> is DERIVED
/// from the same partition rather than recomputed beside it — the one shape in which the two can
/// never disagree again.
///
/// WHAT A DISPOSITION IS NOT: evidence about the game, or about what the order contains. Every
/// input here is about PLACEMENT — whether a placement history and a winning pose resolved — so no
/// disposition says a reference is absent from the selected state, and none says a record for it
/// does or does not exist. No consumer may read one as a deletion, a disable, or a geometric
/// separation: those are earned from an enumerated record row's own fields, never from a
/// reference's failure to appear.
/// </summary>
static class ImportedReferenceAccounting
{
    /// <summary>A record row in this manifest carries the identity.</summary>
    internal const string Emitted = "emitted";
    /// <summary>No plugin in the evaluated order gives the reference a PLACEMENT, so there is no
    /// winner placement history for it. The population <c>missingImportedReferences</c> has always
    /// named — unchanged in meaning.
    ///
    /// NOT "the order does not declare it". A placed record with no Placement subrecord — which a
    /// deletion typically strips — is dropped by PlacementExtract.ToRef before it reaches
    /// OverlapIndex.Record, so it never enters the history at all, while OverlapIndex.Ingest still
    /// notes its deletion. An identity whose only record in this order is placement-less lands
    /// here, and a record for it DOES exist. This disposition says nothing about record existence
    /// and nothing about the game state.</summary>
    internal const string NoPlacementHistory = "no_placement_history";
    /// <summary>It has winner history, but no winning placement resolved, so nothing about its
    /// position or geometry is known. The gap this accounting exists to close.</summary>
    internal const string NoWinningPose = "no_winning_pose";
    /// <summary>It has a winning placement, but fell outside this capture's scope, so no row was
    /// measured for it here.</summary>
    internal const string OutOfScope = "out_of_scope";

    internal sealed record Row(string FormId, string Disposition, string Reason);

    internal sealed record Partitioned(IReadOnlyList<Row> Rows)
    {
        public int ImportedCount => Rows.Count;
        public IReadOnlyList<Row> Unmeasured => Rows.Where(r => r.Disposition != Emitted).ToList();
        public IReadOnlyList<string> WithDisposition(string disposition)
            => Rows.Where(r => r.Disposition == disposition).Select(r => r.FormId).ToList();
        public IReadOnlyDictionary<string, int> Counts => new[] { Emitted, NoPlacementHistory, NoWinningPose, OutOfScope }
            .ToDictionary(d => d, d => Rows.Count(r => r.Disposition == d), StringComparer.Ordinal);
    }

    /// <summary>The single decision. Ordered so the more specific reason wins: a row that was
    /// emitted needs no excuse, and an identity with no placement at all is not "out of scope".</summary>
    internal static Row Classify(string formId, bool emitted, bool hasHistory, bool hasWinningPose)
        => emitted ? new Row(formId, Emitted,
               "a record row in this manifest enumerates the reference")
         : !hasHistory ? new Row(formId, NoPlacementHistory,
               "no plugin in the evaluated order gives the reference a placement, so this configuration resolved no winner "
             + "placement history for it; a placement-less record (a deletion typically strips placement) can still declare "
             + "the identity, so this states nothing about whether a record exists, and nothing about its presence or "
             + "absence in the selected game state")
         : !hasWinningPose ? new Row(formId, NoWinningPose,
               "the reference has winner history but no winning placement resolved, so its position and geometry are unknown here; "
             + "this is unknown placement, not evidence of absence or of geometric separation")
         : new Row(formId, OutOfScope,
               "the reference has a winning placement but lies outside this capture's scope, so no row was measured for it here");

    /// <summary>Classify every imported identity. <paramref name="emitted"/> is the set of keys the
    /// record loop actually wrote a row for — read from the loop's own output, so the accounting
    /// cannot claim a row the manifest does not contain.</summary>
    internal static Partitioned Partition(IEnumerable<FormKey> imported, IReadOnlySet<FormKey> emitted,
                                          Func<FormKey, bool> hasHistory, Func<FormKey, bool> hasWinningPose)
        => new(imported.Distinct()
                       .Select(k => Classify(k.ToString(), emitted.Contains(k), hasHistory(k), hasWinningPose(k)))
                       .OrderBy(r => r.FormId, StringComparer.Ordinal)
                       .ToList());

    /// <summary>The additive <c>scene.discovery.importedReferenceAccounting</c> block. SCHEMA
    /// ADDITION: nothing existing changes shape or meaning, and a consumer that does not know the
    /// member is unaffected. <c>unaccounted</c> is zero by construction and is reported anyway, so
    /// a reader can see the partition is total instead of having to re-derive it.</summary>
    internal static object Receipt(Partitioned p) => new
    {
        importedCount = p.ImportedCount,
        dispositions = p.Counts,
        unaccounted = p.ImportedCount - p.Counts.Values.Sum(),
        rule = "every imported scene reference receives exactly one disposition; a disposition states what this "
             + "manifest knows about the identity and never asserts its presence, absence or separation in the game state",
        unmeasured = p.Unmeasured.Select(r => new { formid = r.FormId, disposition = r.Disposition, reason = r.Reason }).ToArray()
    };

    /// <summary>A deterministic exercise of the production partition above, for a test that must
    /// not need a load order. It reads a fixture naming the four inputs the real capture supplies —
    /// the imported identities and which of them have a record row, winner history and a winning
    /// pose — and prints the receipt the real capture would emit for them. Same Classify, same
    /// Partition, same Receipt: there is no second implementation to drift.</summary>
    internal static int SelfCheck(string fixturePath)
    {
        HashSet<FormKey> Read(JsonElement root, string member)
        {
            var set = new HashSet<FormKey>();
            if (!root.TryGetProperty(member, out var rows) || rows.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"the fixture needs an array member '{member}'");
            foreach (var row in rows.EnumerateArray())
                set.Add(FormKey.Factory(row.GetString() ?? throw new InvalidDataException($"'{member}' holds a non-string")));
            return set;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(fixturePath));
            var imported = Read(doc.RootElement, "imported");
            var emitted = Read(doc.RootElement, "emitted");
            var history = Read(doc.RootElement, "history");
            var pose = Read(doc.RootElement, "pose");
            // A fixture that contradicts the producer would test nothing: the record loop only ever
            // emits a row for a reference it resolved a pose for, and only ever resolves a pose for
            // one it has history for. Refused rather than silently classified.
            var strays = emitted.Except(imported).ToList();
            if (strays.Count > 0)
                throw new InvalidDataException($"'emitted' names {strays.Count} identity(ies) that are not imported, e.g. {strays[0]}");
            var emittedWithoutPose = emitted.Except(pose).ToList();
            if (emittedWithoutPose.Count > 0)
                throw new InvalidDataException($"'emitted' names {emittedWithoutPose.Count} identity(ies) with no winning pose, e.g. {emittedWithoutPose[0]}");
            var poseWithoutHistory = pose.Except(history).ToList();
            if (poseWithoutHistory.Count > 0)
                throw new InvalidDataException($"'pose' names {poseWithoutHistory.Count} identity(ies) with no history, e.g. {poseWithoutHistory[0]}");

            var partition = Partition(imported, emitted, history.Contains, pose.Contains);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                importedReferenceAccounting = Receipt(partition),
                missingImportedReferences = partition.WithDisposition(NoPlacementHistory).OrderBy(k => k, StringComparer.Ordinal).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"--self-check-imported-accounting '{fixturePath}': {ex.Message}");
            return 2;
        }
    }
}
