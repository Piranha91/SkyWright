using Mutagen.Bethesda.Plugins;

using HousecarlCore;

namespace SkyWright.Engine;

/// <summary>What one placed reference contributes to the runtime enable chain: its own initially-disabled flag
/// (<see cref="PlacedRef.InitiallyDisabled"/>) and its XESP enable parent, if any, with the opposite-of-parent flag.</summary>
public readonly record struct EnableFacts(bool InitiallyDisabled, FormKey? Parent, bool Opposite);

/// <summary>A reference's runtime enable state as the chain resolves it. <paramref name="ViaParent"/>: the state came
/// from an XESP parent, not the ref's own flag (with a parent the flag is CK bookkeeping). <paramref name="Resolved"/>:
/// every reference on the chain was known to the caller and the chain has no cycle — false means the answer is the
/// fallback (the ref's own flag on a cycle; "enabled" for a reference the caller does not place) and a caller that
/// judges anything by it should count it rather than trust it.</summary>
public readonly record struct EnableResolution(bool Enabled, bool ViaParent, bool Resolved);

/// <summary>The runtime enable state of a placed reference — the one rule for "does this thing exist when the cell
/// loads", shared by viewprobe (what to draw) and the placement-contact index (what can host, rest, or re-support).
///
/// <para>A reference with an XESP enable parent takes the PARENT's runtime state, XOR the opposite-of-parent flag;
/// its own initially-disabled flag is bookkeeping the engine ignores. Chains are followed with memoisation. The player
/// reference (ID 0x14) is always enabled at runtime even though its record carries the disabled flag — which is exactly
/// why "parent = player, opposite" is the standard permanently-disabled idiom. A cycle is an authoring error and falls
/// back to the ref's own flag; a parent the caller does not know resolves as enabled (no flag known) — both are
/// reported through <see cref="EnableResolution.Resolved"/> so the caller can count them. Measured against the
/// running game on the lab's JK's–OCW pair (2026-09-03): every disabled-via-parent call agreed with the engine.</para></summary>
public static class EnableState
{
    public const uint PlayerRefId = 0x14;
    public const uint IsMarkerFlag = 0x00800000;

    /// <param name="key">the reference to resolve.</param>
    /// <param name="facts">the caller's view of any reference's flag and parent — null when the caller does not place
    /// that reference (absent from the arm under test).</param>
    /// <param name="memo">per-arm cache; pass the same dictionary for every call over one arm.</param>
    public static EnableResolution Resolve(FormKey key, Func<FormKey, EnableFacts?> facts, Dictionary<FormKey, EnableResolution>? memo = null)
        => ResolveInner(key, facts, memo, null);

    static EnableResolution ResolveInner(FormKey key, Func<FormKey, EnableFacts?> facts, Dictionary<FormKey, EnableResolution>? memo, HashSet<FormKey>? seen)
    {
        if (key.ID == PlayerRefId) return new EnableResolution(true, false, true);
        if (memo is not null && memo.TryGetValue(key, out var known)) return known;
        EnableResolution r;
        var f = facts(key);
        if (f is null) r = new EnableResolution(true, false, false);                   // not placed here: no flag known
        else if (f.Value.Parent is { } parent)
        {
            seen ??= new HashSet<FormKey>();
            if (!seen.Add(key)) r = new EnableResolution(!f.Value.InitiallyDisabled, false, false);   // cycle
            else
            {
                var p = ResolveInner(parent, facts, memo, seen);
                r = new EnableResolution(p.Enabled ^ f.Value.Opposite, true, p.Resolved);
            }
        }
        else r = new EnableResolution(!f.Value.InitiallyDisabled, false, true);
        if (memo is not null) memo[key] = r;
        return r;
    }
}
