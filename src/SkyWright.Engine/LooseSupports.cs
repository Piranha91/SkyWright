using HousecarlCore;

namespace SkyWright.Engine;

/// <summary>Rest rule part 3: the loose-support pass, lowest first. Each loose body's role is read once (memoised) by
/// <c>read(key, role)</c>, which may ask for the roles of the loose bodies below it; a body asked for while its own reading is in
/// progress (loose bodies resting on each other) is reported as <see cref="LooseRole.Cycle"/> to the asker.</summary>
public sealed class LooseReadings<TKey> where TKey : notnull
{
    readonly Func<TKey, Func<TKey, LooseRole>, LooseRole> read;
    public LooseReadings(Func<TKey, Func<TKey, LooseRole>, LooseRole> read) => this.read = read;

    /// <summary>The role of one loose body as a support for what is above it.</summary>
    public LooseRole Role(TKey key)
    {
        if (memo.TryGetValue(key, out var r)) return r;
        if (!visiting.Add(key)) return LooseRole.Cycle;
        try { r = read(key, Role); }
        finally { visiting.Remove(key); }
        memo[key] = r;
        return r;
    }
    readonly Dictionary<TKey, LooseRole> memo = new();
    readonly HashSet<TKey> visiting = new();
}
