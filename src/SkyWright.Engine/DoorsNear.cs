using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace SkyWright.Engine;

/// <summary>The doors near a placed reference, nearest first: <paramref name="Listed"/> holds at most the number asked for,
/// <paramref name="Total"/> counts them all. <paramref name="Refusal"/> is one sentence when the question cannot be
/// answered; <paramref name="Unread"/> is one sentence when a plugin could not be read, so the list may be short.</summary>
public sealed record DoorsNearAnswer(IReadOnlyList<FormKey> Listed, int Total, string? Refusal, string? Unread = null);

/// <summary>The context panel's doors near a reference: of what stands near it (<see cref="NearReferences"/>: every
/// plugin's version decides "near"), the load doors, that is the records whose winning version is a placed object, not
/// deleted, with a TeleportDestination.</summary>
public static class DoorsNear
{
    public static DoorsNearAnswer Of(LoadOrderResolver.IndexView view, FormKey seed, double within, int listAtMost,
                                     CancellationToken ct = default)
    {
        var near = NearReferences.Of(view, new[] { seed }, within, new[] { typeof(IPlacedObjectGetter) }, ct);
        if (near.Refusal is { } why) return new DoorsNearAnswer(Array.Empty<FormKey>(), 0, why);
        var doors = near.Keys.Where(k => near.Winners![k] is IPlacedObjectGetter p && !p.IsDeleted && p.TeleportDestination is not null).ToList();
        return new DoorsNearAnswer(doors.Take(listAtMost).ToList(), doors.Count, null, near.UnreadSentence);
    }
}
