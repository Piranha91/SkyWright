using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

/// <summary>Known answers for contactprobe's per-object trace (patch-miss packet, GATE-A §2 and §12; GATE-B §2), on the
/// proof's synthetic contact order (the ct-synth fixture: plugins written with Mutagen, read back through ContactIndex —
/// the same ingest and the same MovedHosts / DependentSnaps / Judge calls contactprobe makes). Each expected outcome is
/// the fixture's documented shape: Table16 moved 10 u (under the 16 u threshold), Table36 never exists at runtime,
/// FX20 is not a dependent type, Wall21 is too big to be a dependent, Bowl22 was introduced by the mover, Bowl1 is
/// Stranded, Bowl3 moved with its table, Bowl8 sits 5 u above its table (a near miss at ε 4), Bowl9 was sunk 10 u
/// (no relation), Bowl31 is disabled at runtime through its parent.</summary>
static class ProbeTraceProof
{
    public static void Run(Action<string, bool, string> check, ContactIndex cidx, IReadOnlyList<HashSet<int>> closure,
                           Func<int, FormKey> H, Func<int, FormKey> D, FormKey bowl22)
    {
        // Untraced reference pass, for T3 and for the coverage counts T2 compares against.
        cidx.Trace = null;
        var skips0 = new ContactSkips();
        var hosts0 = cidx.MovedHosts(skips0, closure);
        var dep0 = cidx.DependentSnaps(skips0);
        var win0 = cidx.PhysicalWinners(null, skips0);
        var rows0 = hosts0.SelectMany(h => cidx.Judge(h, dep0, win0, closure, skips0, keepNoRelation: true)).ToList();

        // Keyed pass.
        var keys = new[] { H(16), H(36), D(20), D(21), bowl22, D(1), D(3), D(8), D(9), D(31) };
        var tr = new ProbeTraceCollector(keys);
        cidx.Trace = tr;
        var skips1 = new ContactSkips();
        var hosts1 = cidx.MovedHosts(skips1, closure);
        var dep1 = cidx.DependentSnaps(skips1);
        var win1 = cidx.PhysicalWinners(null, skips1);
        var rows1 = hosts1.SelectMany(h => cidx.Judge(h, dep1, win1, closure, skips1, keepNoRelation: true)).ToList();
        cidx.Trace = null;

        TraceEvent? One(FormKey k, string role, FormKey other = default) =>
            tr.For(k).FirstOrDefault(e => e.Role == role && (other.IsNull ? true : e.Other == other));
        string Show(FormKey k) => string.Join(" | ", tr.For(k).Select(e => $"{e.Role}:{e.Outcome}:{e.Rule}{(e.Other.IsNull ? "" : "@" + e.Other.ID.ToString("X"))}{(e.Detail is null ? "" : " " + e.Detail)}")) is { Length: > 0 } s ? s : "(no events)";
        double V(TraceEvent? e, string name) => e?.Values is { } v && v.TryGetValue(name, out var x) ? x : double.NaN;

        var t16 = One(H(16), TraceRole.MovedHost);
        check("trace T1a: Table16, moved 10 u -> moved-host NEVER / NotMovedOverThreshold with the move (10) and the cut (16)",
              t16 is { Outcome: TraceOutcome.Never, Rule: "NotMovedOverThreshold" } && Math.Abs(V(t16, "move") - 10) < 0.01 && Math.Abs(V(t16, "limit") - 16) < 0.01, Show(H(16)));
        check("trace T1b: Table36, never enabled without its mover -> moved-host SET ASIDE / RuntimeDisabledInArm",
              One(H(36), TraceRole.MovedHost) is { Outcome: TraceOutcome.SetAside, Rule: "RuntimeDisabledInArm" }, Show(H(36)));
        check("trace T1c: FX20 -> dependent NEVER / NotDependentType",
              One(D(20), TraceRole.Dependent) is { Outcome: TraceOutcome.Never, Rule: "NotDependentType" }, Show(D(20)));
        var t21 = One(D(21), TraceRole.Dependent);
        check("trace T1d: Wall21 -> dependent SET ASIDE / OverDependentSize with its size over the 256 u cut",
              t21 is { Outcome: TraceOutcome.SetAside, Rule: "OverDependentSize" } && V(t21, "maxDim") > 256 && Math.Abs(V(t21, "limit") - 256) < 0.01, Show(D(21)));
        check("trace T1e: Bowl22, introduced by the mover -> dependent SET ASIDE / DepIntroducedByMover",
              tr.For(bowl22).Any(e => e.Role == TraceRole.Dependent && e.Outcome == TraceOutcome.SetAside && e.Rule == "DepIntroducedByMover"), Show(bowl22));
        check("trace T1f: Bowl1 on moved Table1 -> dependent JUDGED RELATION (Stranded) against H1",
              One(D(1), TraceRole.Dependent, H(1)) is { Outcome: TraceOutcome.JudgedRelation } e1 && (e1.Detail ?? "").Contains("Stranded"), Show(D(1)));
        check("trace T1g: Bowl3 moved with its table -> JUDGED RELATION NOT CLASH (MovedTogether), not below threshold",
              One(D(3), TraceRole.Dependent, H(3)) is { Outcome: TraceOutcome.JudgedRelationNotClash } e3 && (e3.Detail ?? "").Contains("MovedTogether"), Show(D(3)));
        var t8 = One(D(8), TraceRole.Dependent, H(8));
        check("trace T1h: Bowl8, 5 u above its table -> JUDGED BELOW THRESHOLD (NearMiss) with distance ≈ 5, ε 4",
              t8 is { Outcome: TraceOutcome.JudgedBelowThreshold, Rule: "NearMiss" } && Math.Abs(V(t8, "distance") - 5) < 1 && Math.Abs(V(t8, "epsilon") - 4) < 0.01, Show(D(8)));
        check("trace T1i: Bowl9, sunk 10 u as authored -> JUDGED NO RELATION",
              One(D(9), TraceRole.Dependent, H(9)) is { Outcome: TraceOutcome.JudgedNoRelation }, Show(D(9)));
        check("trace T1j: Bowl31, disabled at runtime through its parent -> dependent UNJUDGEABLE / RuntimeDisabled",
              One(D(31), TraceRole.Dependent, H(31)) is { Outcome: TraceOutcome.Unjudgeable, Rule: "RuntimeDisabled" }, Show(D(31)));
        check("trace T1k: every requested key has exactly one moved-host event and at least one dependent event (no silent key)",
              keys.All(k => tr.For(k).Count(e => e.Role == TraceRole.MovedHost) == 1 && tr.For(k).Any(e => e.Role == TraceRole.Dependent)),
              string.Join("; ", keys.Select(k => $"{k.ID:X}: host {tr.For(k).Count(e => e.Role == TraceRole.MovedHost)}, dep {tr.For(k).Count(e => e.Role == TraceRole.Dependent)}")));

        // T2: counting mode reproduces every coverage-block counter exactly.
        var all = new ProbeTraceCollector(null);
        cidx.Trace = all;
        var skips2 = new ContactSkips();
        var hosts2 = cidx.MovedHosts(skips2, closure);
        var dep2 = cidx.DependentSnaps(skips2);
        var win2 = cidx.PhysicalWinners(null, skips2);
        foreach (var h in hosts2) cidx.Judge(h, dep2, win2, closure, skips2, keepNoRelation: true);
        cidx.Trace = null;
        var pairs = new (string Name, long Skip, long Trace)[]
        {
            ("HostNoGeometry", skips2.HostNoGeometry, all.Count(TraceRole.MovedHost, TraceOutcome.Unjudgeable, "NoGeometry")),
            ("HostEnableUnresolved", skips2.HostEnableUnresolved, all.Count(TraceRole.MovedHost, TraceOutcome.Unjudgeable, "EnableChainUnresolved")),
            ("HostNoCollision", skips2.HostNoCollision, all.Count(TraceRole.MovedHost, TraceOutcome.SetAside, "NoSolidCollision")),
            ("HostVolume", skips2.HostVolume, all.Count(TraceRole.MovedHost, TraceOutcome.SetAside, "VolumeScale")),
            ("HostTooSmall", skips2.HostTooSmall, all.Count(TraceRole.MovedHost, TraceOutcome.SetAside, "TooSmallToHost")),
            ("HostRuntimeDisabled", skips2.HostRuntimeDisabled, all.Count(TraceRole.MovedHost, TraceOutcome.SetAside, "RuntimeDisabledInArm")),
            ("DepNoGeometry", skips2.DepNoGeometry, all.Count(TraceRole.Dependent, TraceOutcome.Unjudgeable, "NoGeometry")),
            ("DepVolume", skips2.DepVolume, all.Count(TraceRole.Dependent, TraceOutcome.SetAside, "OverDependentSize")),
            ("BoxesApart", skips2.BoxesApart, all.Count(TraceRole.Dependent, TraceOutcome.SetAside, "BoxesApart")),
            ("DepIntroducedByMover", skips2.DepIntroducedByMover, all.Count(TraceRole.Dependent, TraceOutcome.SetAside, "DepIntroducedByMover")),
            ("DepOtherSpace", skips2.DepOtherSpace, all.Count(TraceRole.Dependent, TraceOutcome.SetAside, "DepOtherSpace")),
            ("DepAbsentInArm", skips2.DepAbsentInArm, all.Count(TraceRole.Dependent, TraceOutcome.SetAside, "DepAbsentInArm")),
            ("DepEnableUnresolved", skips2.DepEnableUnresolved, all.Count(TraceRole.Dependent, TraceOutcome.Unjudgeable, "DepEnableChainUnresolved")),
            ("WinnerRuntimeDisabled", skips2.WinnerRuntimeDisabled, all.Count(TraceRole.WinnerArm, TraceOutcome.SetAside, "RuntimeDisabled")),
            ("WinnerEnableUnresolved", skips2.WinnerEnableUnresolved, all.Count(TraceRole.WinnerArm, TraceOutcome.Unjudgeable, "EnableChainUnresolved")),
        };
        bool someNonZero = pairs.Any(p => p.Skip > 0);
        check("trace T2: counting mode reproduces every coverage counter exactly (and the fixture exercises some)",
              someNonZero && pairs.All(p => p.Skip == p.Trace), string.Join(", ", pairs.Select(p => $"{p.Name} {p.Skip}/{p.Trace}")));

        // T3: attaching a sink changes nothing the probe decides.
        string Sig(IEnumerable<ContactRow> rs) => string.Join(";", rs.Select(r => $"{r.Host.Key}>{r.Dependent}:{r.Relation}/{r.Verdict}/{r.Reason}").OrderBy(x => x, StringComparer.Ordinal));
        string SkipSig(ContactSkips s) => $"{s.HostNoGeometry},{s.HostNoCollision},{s.HostVolume},{s.HostTooSmall},{s.HostRuntimeDisabled},{s.HostEnableUnresolved},{s.DepNoGeometry},{s.DepVolume},{s.BoxesApart},{s.DepIntroducedByMover},{s.DepOtherSpace},{s.DepAbsentInArm},{s.DepEnableUnresolved},{s.WinnerRuntimeDisabled},{s.WinnerEnableUnresolved}";
        check("trace T3: with a sink attached the hosts, rows and every counter are identical to the untraced pass",
              hosts0.Count == hosts1.Count && Sig(rows0) == Sig(rows1) && SkipSig(skips0) == SkipSig(skips1),
              $"hosts {hosts0.Count}/{hosts1.Count}, rows {rows0.Count}/{rows1.Count}, counters {SkipSig(skips0)} / {SkipSig(skips1)}");
    }
}
