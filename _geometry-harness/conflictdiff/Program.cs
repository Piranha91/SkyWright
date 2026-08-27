using HarnessShared;

// CONFLICT-SET DIFF — the second half of the differential chain (roadmap 2.4).
//
// --verify's CONFIRMED means COVERAGE: a candidate overrides every broken record. It does not mean
// repair — the TGC run proved a CONFIRMED patch can repair 8 records and create 8 others. The only
// way to know what a patch actually DOES to the conflict set is to enable it synthetically and
// diff, and this tool is the diff:
//
//     navownprobe --emit base.json
//     navownprobe --enable "Candidate.esp" --emit with-candidate.json
//     conflictdiff base.json with-candidate.json
//
// The verdict line is DIFFERENTIAL-VERIFIED / DIFFERENTIAL-NEUTRAL / DIFFERENTIAL-HARMFUL, decided
// by net records repaired minus created. `differential-sweep.ps1` drives the whole loop over the
// candidates --verify names.
//
// usage: conflictdiff <base.json> <changed.json>

if (args.Length != 2) { Console.Error.WriteLine("usage: conflictdiff <base.json> <changed.json>"); return 2; }

ConflictFile baseFile, changedFile;
try { baseFile = ConflictIo.Read(args[0]); changedFile = ConflictIo.Read(args[1]); }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }

static Dictionary<(string Kind, string A, string B), HashSet<string>> Index(ConflictFile f)
{
    var d = new Dictionary<(string, string, string), HashSet<string>>();
    foreach (var c in f.Conflicts)
    {
        var k = (c.Kind, c.A, c.B);
        if (!d.TryGetValue(k, out var set)) d[k] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in c.Necessary) set.Add(r.FormKey);
    }
    return d;
}

var before = Index(baseFile);
var after = Index(changedFile);

int repaired = 0, created = 0, groupsGone = 0, groupsNew = 0;

Console.WriteLine($"base    : {args[0]}  ({baseFile.Conflicts.Count:N0} conflicts, {before.Count:N0} groups)");
Console.WriteLine($"changed : {args[1]}  ({changedFile.Conflicts.Count:N0} conflicts, {after.Count:N0} groups)");
Console.WriteLine();

foreach (var (k, recs) in before.OrderBy(kv => kv.Key.Kind).ThenBy(kv => kv.Key.A))
{
    var now = after.GetValueOrDefault(k);
    var gone = now is null ? recs : recs.Where(r => !now.Contains(r)).ToHashSet();
    if (gone.Count == 0) continue;
    repaired += gone.Count;
    if (now is null || now.Count == 0) groupsGone++;
    Console.WriteLine($"   {(now is null ? "GONE  " : "SHRANK"),-7} {k.Kind,-10} {k.A}  x  {k.B}   -{gone.Count} record(s)");
}
foreach (var (k, recs) in after.OrderBy(kv => kv.Key.Kind).ThenBy(kv => kv.Key.A))
{
    var was = before.GetValueOrDefault(k);
    var fresh = was is null ? recs : recs.Where(r => !was.Contains(r)).ToHashSet();
    if (fresh.Count == 0) continue;
    created += fresh.Count;
    if (was is null || was.Count == 0) groupsNew++;
    Console.WriteLine($"   {(was is null ? "NEW   " : "GREW  "),-7} {k.Kind,-10} {k.A}  x  {k.B}   +{fresh.Count} record(s)");
}

if (repaired == 0 && created == 0) Console.WriteLine("   IDENTICAL — the change touched nothing the detector sees.");

Console.WriteLine();
Console.WriteLine($"   repaired: {repaired:N0} record(s), {groupsGone:N0} group(s) fully gone");
Console.WriteLine($"   created : {created:N0} record(s), {groupsNew:N0} new group(s)");
string verdict = repaired == 0 && created == 0 ? "DIFFERENTIAL-INERT"
               : repaired > created ? "DIFFERENTIAL-VERIFIED"
               : repaired == created ? "DIFFERENTIAL-NEUTRAL"
               : "DIFFERENTIAL-HARMFUL";
Console.WriteLine($"   verdict : {verdict}  (net {repaired - created:+#;-#;0} record(s))");
Console.WriteLine();
Console.WriteLine("   A verdict here outranks --verify's CONFIRMED: coverage says a patch touches the broken");
Console.WriteLine("   records; only the differential says what actually happens to the conflict set.");
return 0;
