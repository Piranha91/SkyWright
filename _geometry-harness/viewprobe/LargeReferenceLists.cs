using Mutagen.Bethesda.Plugins;

namespace ViewProbe;

// THE LARGE-REFERENCE LISTS of the target worldspace (WRLD RNAM), plugin by plugin in load order (WinningRefs.cs
// ObserveLarge). Each plugin that carries a record of the worldspace is a carrier; its list may be empty (a city or an
// eFPS override copies the WRLD without RNAM). The selection rule is WinningRefs.cs's: a root worldspace takes the
// union of every carrier's list; a child worldspace (its winning record has a Parent) takes its own lists' union, or
// none. "Winner" (the last carrier's list alone) is kept for the sabotage arm and for a reader; the game does not use it
// (lane G measured Tamriel's listed references loaded while the winning Tamriel record lists nothing).
internal sealed class LargeReferenceLists
{
    readonly HashSet<FormKey> _union = new();
    HashSet<FormKey> _winner = new();
    readonly List<string> _carriers = new();

    internal bool IsChild { get; private set; }
    internal IReadOnlyList<string> Carriers => _carriers;

    internal void Observe(string plugin, IEnumerable<FormKey> listed, bool hasParent)
    {
        var mine = listed.ToHashSet();
        _union.UnionWith(mine);
        _winner = mine;
        IsChild = hasParent;   // the last carrier's record is the winning one
        _carriers.Add(plugin);
    }

    internal IReadOnlySet<FormKey> Selected(bool childUnion, bool rootUsesUnion) =>
        IsChild ? (childUnion ? _union : Empty) : (rootUsesUnion ? _union : _winner);

    static readonly HashSet<FormKey> Empty = new();

    internal LargeReferenceLists Clone()
    {
        var c = new LargeReferenceLists { IsChild = IsChild };
        c._union.UnionWith(_union);
        c._winner = _winner.ToHashSet();
        c._carriers.AddRange(_carriers);
        return c;
    }
}
