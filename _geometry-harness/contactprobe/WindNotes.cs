using System.Text.Json;
using System.Text.Json.Nodes;
using HousecarlCore;
using SkyWright.Engine;

/// <summary>The wind label on contactprobe's static results (animated motion follow-ups, item 1, decision E1, option L:
/// on by default). A render geometry entry whose member's mesh has a visible Tree_Anim shape gains <c>windA</c> /
/// <c>windB</c> (<see cref="NifWind.Json"/>); every number stays as it was. An entry without a Tree_Anim member is left
/// exactly as before (it is not even re-serialised), so outputs without one are unchanged byte for byte.</summary>
static class WindNotes
{
    /// <summary>The entry with windA / windB added when either is non-null; otherwise the entry itself.</summary>
    public static object Attach(object entry, WindLabel? windA, WindLabel? windB, JsonSerializerOptions options)
    {
        if (windA is null && windB is null) return entry;
        var node = JsonSerializer.SerializeToNode(entry, entry.GetType(), options)!.AsObject();
        if (windA is not null) node["windA"] = JsonSerializer.SerializeToNode(NifWind.Json(windA), options);
        if (windB is not null) node["windB"] = JsonSerializer.SerializeToNode(NifWind.Json(windB), options);
        return node;
    }
}
