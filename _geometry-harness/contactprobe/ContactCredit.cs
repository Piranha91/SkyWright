using HarnessShared;
using Mutagen.Bethesda.Plugins;

// CONTACT, the decisions between the judged rows and the emit: which rows are the class the emit carries, and the class's
// physical predicate, as an emitted record states it and as a --base arm re-measures it. Moved out of Program.cs's
// top-level statements unchanged so a self-test can hold them to known answers (task
// 2026-10-14-probe-toplevel-decisions-testable). proofprobe compiles this file in for the predicate's case on its
// ct-synth ContactIndex.
static class ContactCredit
{
    /// <summary>The rule the default pass's emit was measured under (task 2026-10-20-contactprobe-sweep-emit-policy): the rule's
    /// version and every option that changes the emit's rows or coverage. conflictdiff refuses two emits whose policies differ, and
    /// --reverse-of refuses a baseline measured under another. Bump the version when the sweep's contact rule changes.</summary>
    public const string SweepPolicyVersion = "placement_contact_v1";

    public static string SweepPolicy(float eps, float reach, bool radius, bool bos, bool allRows, IEnumerable<string> forceEnable)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var forced = forceEnable.Select(f => f.ToLowerInvariant()).OrderBy(f => f, StringComparer.Ordinal).ToList();
        return string.Join("|", SweepPolicyVersion, "eps " + eps.ToString("R", inv), "reach " + reach.ToString("R", inv),
                           "radius " + (radius ? "on" : "off"), "bos " + (bos ? "on" : "off"), "all-rows " + (allRows ? "on" : "off"),
                           "force-enable " + (forced.Count == 0 ? "none" : string.Join(",", forced)));
    }

    /// <summary>The emit's class: a Fixed dependent left Stranded or Orphaned, the Bethesda floor withheld.</summary>
    public static bool Emitted(DependentMotion motion, ContactVerdict verdict, DependencyPopulation population)
        => motion == DependentMotion.Fixed && verdict is ContactVerdict.Stranded or ContactVerdict.Orphaned && population != DependencyPopulation.BethesdaFloor;

    // THE PHYSICAL PREDICATE of a CONTACT row (2026-09-03; handoff 2026-09-12): the dependent still touches nothing within ε
    // under the arm being read — the class's own winner-arm test with the provenance half ("the mover's side did not re-place
    // it") taken off. A sham on the mover's side that nudges the dependent 1 u reads MovedTogether and leaves the class; the
    // dependent still floats. A dependent deleted or disabled at runtime under the arm is Gone (nothing floats); one whose
    // geometry or enable chain cannot be read is Unmeasured.
    public static (PhysicalState State, string Detail) Physical(bool measured, float dist, float pen, float eps, string against)
    {
        if (!measured) return (PhysicalState.Unmeasured, "the winner arm was not measured");
        if (pen > eps) return (PhysicalState.Gone, $"sunk {pen:0.#}u into {against}");
        if (dist <= eps) return (PhysicalState.Gone, $"touches {against} at {dist:0.#}u");
        return (PhysicalState.Holds, $"touches nothing within {eps:0.#}u (nearest {dist:0.#}u)");
    }

    /// <summary>The predicate re-measured for one dependent under the order <paramref name="cidx"/> holds.</summary>
    public static (PhysicalState State, string Detail) Predicate(ContactIndex cidx, BoxHash<FormKey> winHash, FormKey dep, float eps,
                                                               Func<FormKey, string> baseLabel)
    {
        var idx = cidx.Index;
        var snaps = idx.History.Of(dep);
        if (snaps is null || snaps.Count == 0) return (PhysicalState.Unmeasured, "the dependent is not placed in this order");
        var win = snaps[^1];
        if (win.Deleted || idx.IsDeletedNow(dep)) return (PhysicalState.Gone, "the dependent is deleted under this order — nothing floats");
        var en = cidx.WinnerEnable(dep);
        if (!en.Resolved) return (PhysicalState.Unmeasured, "the dependent's enable chain cannot be followed in this order");
        if (!en.Enabled) return (PhysicalState.Gone, "the dependent is disabled at runtime under this order — nothing floats");
        var b = cidx.WinnerBase(dep);
        if (cidx.Summary(b) is not { CanDepend: true }) return (PhysicalState.Unmeasured, "the dependent has no solid to test under this order");
        var box = cidx.WorldBox(b, win);
        if (box.IsEmpty) return (PhysicalState.Unmeasured, "the dependent has no geometry under this order");
        var c = cidx.BestContact(dep, snaps.Count - 1, b, win, box, winHash, cidx.WinnerPlacement, (k, _) => cidx.WinnerBase(k), out _);
        if (c is null) return (PhysicalState.Holds, $"touches nothing within {cidx.BoxBand:0.#}u of its box");
        return Physical(true, c.Contact.FacingDistance, c.Contact.EffectivePenetration, eps, baseLabel(cidx.WinnerBase(c.Against)));
    }
}
