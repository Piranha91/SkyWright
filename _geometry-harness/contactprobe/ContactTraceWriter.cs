using System.Security.Cryptography;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

/// <summary>contactprobe's per-object trace file (schema housecarl.probe-trace/1; patch-miss packet, GATE-A §2). Keyed
/// mode (--trace-keys FILE): every requested FormKey with its events per role — a key that is not a placed reference in
/// this order reads <c>absent</c>. Counting mode (no key list): every event counted by (role, outcome, rule), and the
/// coverage consistency check — each counted rule's trace count beside the coverage-block counter it must equal. The
/// trace is a side file: it never changes the emit or the --json dump.</summary>
static class ContactTraceWriter
{
    public const string Schema = "housecarl.probe-trace/1";

    /// <summary>Read a key list: one FormKey per line ('XXXXXX:Plugin.esp'); blank lines and '#' comments ignored.</summary>
    public static List<FormKey> ReadKeys(string path)
    {
        var keys = new List<FormKey>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (!FormKey.TryFactory(line, out var k)) throw new ArgumentException($"--trace-keys: not a FormKey: '{line}'");
            keys.Add(k);
        }
        return keys;
    }

    /// <summary>The coverage-block counters and the trace (role, outcome, rule) each must equal (GATE-A §2.3 invariant).</summary>
    public static IEnumerable<(string Counter, long Coverage, long Trace)> Consistency(ContactSkips s, ProbeTraceCollector t) => new[]
    {
        ("HostNoGeometry", s.HostNoGeometry, t.Count(TraceRole.MovedHost, TraceOutcome.Unjudgeable, "NoGeometry")),
        ("HostEnableUnresolved", s.HostEnableUnresolved, t.Count(TraceRole.MovedHost, TraceOutcome.Unjudgeable, "EnableChainUnresolved")),
        ("HostNoCollision", s.HostNoCollision, t.Count(TraceRole.MovedHost, TraceOutcome.SetAside, "NoSolidCollision")),
        ("HostVolume", s.HostVolume, t.Count(TraceRole.MovedHost, TraceOutcome.SetAside, "VolumeScale")),
        ("HostTooSmall", s.HostTooSmall, t.Count(TraceRole.MovedHost, TraceOutcome.SetAside, "TooSmallToHost")),
        ("HostRuntimeDisabled", s.HostRuntimeDisabled, t.Count(TraceRole.MovedHost, TraceOutcome.SetAside, "RuntimeDisabledInArm")),
        ("DepNoGeometry", s.DepNoGeometry, t.Count(TraceRole.Dependent, TraceOutcome.Unjudgeable, "NoGeometry")),
        ("DepVolume", s.DepVolume, t.Count(TraceRole.Dependent, TraceOutcome.SetAside, "OverDependentSize")),
        ("BoxesApart", s.BoxesApart, t.Count(TraceRole.Dependent, TraceOutcome.SetAside, "BoxesApart")),
        ("DepIntroducedByMover", s.DepIntroducedByMover, t.Count(TraceRole.Dependent, TraceOutcome.SetAside, "DepIntroducedByMover")),
        ("DepOtherSpace", s.DepOtherSpace, t.Count(TraceRole.Dependent, TraceOutcome.SetAside, "DepOtherSpace")),
        ("DepAbsentInArm", s.DepAbsentInArm, t.Count(TraceRole.Dependent, TraceOutcome.SetAside, "DepAbsentInArm")),
        ("DepEnableUnresolved", s.DepEnableUnresolved, t.Count(TraceRole.Dependent, TraceOutcome.Unjudgeable, "DepEnableChainUnresolved")),
        ("WinnerRuntimeDisabled", s.WinnerRuntimeDisabled, t.Count(TraceRole.WinnerArm, TraceOutcome.SetAside, "RuntimeDisabled")),
        ("WinnerEnableUnresolved", s.WinnerEnableUnresolved, t.Count(TraceRole.WinnerArm, TraceOutcome.Unjudgeable, "EnableChainUnresolved")),
    };

    /// <param name="admission">For a judged pair (dependent, host): whether the emit admits it, and why not.</param>
    public static void Write(string path, ProbeTraceCollector trace, string? keysPath, IReadOnlyList<FormKey>? keys,
                             ContactIndex cidx, ContactSkips skips, ConfigurationIdentity identity, string mode,
                             Func<FormKey, string> baseLabel, Func<FormKey, FormKey, (bool Admitted, string Why)?> admission)
    {
        var thresholds = new Dictionary<string, double>
        {
            ["MoveThreshold"] = cidx.MoveThreshold, ["MinHostDim"] = cidx.MinHostDim, ["MaxHostDim"] = cidx.MaxHostDim,
            ["MaxDependentDim"] = cidx.MaxDependentDim, ["Reach"] = cidx.Reach, ["BoxBand"] = cidx.BoxBand,
            ["Epsilon"] = cidx.Epsilon, ["NearBand"] = cidx.NearBand,
        };
        object? objects = null;
        if (keys is not null)
            objects = keys.Select(k =>
            {
                var snaps = cidx.Index.History.Of(k);
                bool present = snaps is { Count: > 0 };
                var events = trace.For(k).ToList();
                var roles = events.Select(e => (object)new
                {
                    role = e.Role, outcome = e.Outcome, rule = e.Rule,
                    other = e.Other.IsNull ? null : e.Other.ToString(),
                    values = e.Values, detail = e.Detail,
                    admitted = e.Outcome == TraceOutcome.JudgedRelation ? admission(e.Key, e.Other)?.Admitted : null,
                    admissionWhy = e.Outcome == TraceOutcome.JudgedRelation ? admission(e.Key, e.Other)?.Why : null,
                }).ToList();
                if (!present)
                    roles.Add(new { role = "*", outcome = TraceOutcome.Absent, rule = "NotAPlacedReference", other = (string?)null,
                                    values = (IReadOnlyDictionary<string, double>?)null, detail = "no placement of this key in the order",
                                    admitted = (bool?)null, admissionWhy = (string?)null });
                var winBase = present ? cidx.WinnerBase(k) : default;
                return new { formKey = k.ToString(), present, @base = winBase.IsNull ? null : winBase.ToString(),
                             baseLabel = winBase.IsNull ? null : baseLabel(winBase), roles };
            }).ToList();
        var doc = new
        {
            schema = Schema, probe = "contactprobe", mode,
            configuration = new { fingerprint = identity.Fingerprint, source = identity.Source },
            population = identity.Population, state = identity.State,
            request = keysPath is null ? null : new { path = keysPath, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(keysPath))).ToLowerInvariant(), formKeys = keys!.Count },
            countingMode = trace.CountOnly,
            thresholds,
            counts = trace.Counts.OrderBy(kv => kv.Key.Role, StringComparer.Ordinal).ThenBy(kv => kv.Key.Outcome, StringComparer.Ordinal)
                                 .ThenBy(kv => kv.Key.Rule, StringComparer.Ordinal)
                                 .Select(kv => new { role = kv.Key.Role, outcome = kv.Key.Outcome, rule = kv.Key.Rule, count = kv.Value }).ToList(),
            consistency = trace.CountOnly
                ? Consistency(skips, trace).Select(c => new { counter = c.Counter, coverage = c.Coverage, trace = c.Trace, equal = c.Coverage == c.Trace }).ToList()
                : null,
            objects,
        };
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        }));
    }
}
