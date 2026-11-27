using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using Xunit;

namespace WorkbenchTests;

/// <summary>houseCARL's check tool on the in-process bridge (recordprobe's dangling check): housecarl_check is answered by
/// its own method on a real service, and a tool the bridge does not list is still refused with the same sentence.</summary>
[Trait("tier", "integration")]
public sealed class HousecarlCheckBridgeTests
{
    [Fact]
    public void The_check_tool_is_answered_in_process_and_an_unlisted_tool_is_still_refused_with_the_same_sentence()
    {
        using var w = new TiltedRefWorld();
        using var svc = HousecarlCalls.Open(w.DataDir, Path.Combine(w.Instance, "mods"), Path.Combine(w.Instance, "profiles", "Default"),
                                            Path.Combine(w.Root, "hcdata-bridge"), TestCorpus.Path);
        var text = HousecarlCalls.Invoke(svc, ToolNames.Check, new Dictionary<string, object?> { ["findings"] = new[] { "dangling" }, ["format"] = "json" });
        using var doc = JsonDocument.Parse(text);
        var errors = doc.RootElement.GetProperty("families").GetProperty("errors");
        Assert.Equal(1, errors.GetProperty("scanned_plugins").GetInt32());
        Assert.Equal(0, errors.GetProperty("dangling").GetInt32());

        var ex = Assert.Throws<InvalidOperationException>(() => HousecarlCalls.Invoke(svc, ToolNames.NexusSearch, new Dictionary<string, object?>()));
        Assert.Equal("houseCARL's housecarl_nexus_search is not one this program calls in process; this is a fault in SkyWright.", ex.Message);
    }
}
