using System.Text.Json;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>A guarded argument (into, out_path, to_file) given as anything but text or null is refused in one
/// sentence; null and "" are read as absent, as the server reads them (pinned on the wire in StateToolsTests).</summary>
[Trait("tier", "unit")]
public sealed class ReviewSpeedForwardTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-wb-r31-" + Guid.NewGuid().ToString("N"));
    string Inst => Path.Combine(_root, "instance");
    string Game => Path.Combine(_root, "game");
    string Elsewhere => Path.Combine(_root, "out");

    public ReviewSpeedForwardTests()
    {
        foreach (var d in new[] { "mods/Enabled", "profiles/P", "overwrite" }) Directory.CreateDirectory(Path.Combine(Inst, d));
        Directory.CreateDirectory(Path.Combine(Game, "Data"));
    }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    (Dictionary<string, JsonElement>? Args, string? Refusal) Prepare(string tool, string json)
    {
        var given = JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        return ForwardRule.Prepare(tool, given, Elsewhere, HousecarlCore.Mo2OutputLayout.Of(Inst, "P", Game));
    }

    static void OneSentence(string? why, string arg)
    {
        Assert.NotNull(why);
        Assert.Contains(arg, why);
        Assert.Contains("text", why);
        Assert.DoesNotContain("\n", why);
        Assert.EndsWith(".", why);
    }

    // R31a: a number for into, which the server would bind as text
    [Fact]
    public void A_number_for_into_is_refused()
    {
        var (args, why) = Prepare("housecarl_apply", """{ "ops": [], "into": 123 }""");
        Assert.Null(args);
        OneSentence(why, "into");
    }

    // R31b
    [Theory]
    [InlineData("housecarl_apply", "out_path", "42")]
    [InlineData("housecarl_records", "to_file", "7")]
    [InlineData("housecarl_compile_script", "out_path", "true")]
    [InlineData("housecarl_apply", "into", "[\"a\"]")]
    [InlineData("housecarl_apply", "into", "{ \"path\": \"a\" }")]
    public void A_guarded_argument_that_is_not_text_is_refused(string tool, string arg, string value)
    {
        var (args, why) = Prepare(tool, $$"""{ "ops": [], "{{arg}}": {{value}} }""");
        Assert.Null(args);
        OneSentence(why, arg);
    }

    // R31c: null and "" stay absent on the workbench's side (the wire test pins the server's side)
    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    public void A_null_or_empty_into_is_read_as_absent_and_the_output_folder_is_imposed(string value)
    {
        var (args, why) = Prepare("housecarl_apply", $$"""{ "ops": [], "into": {{value}} }""");
        Assert.True(why is null, "refused: " + why);
        Assert.Equal(HousecarlCore.OutputFolderPolicy.Full(Elsewhere), args!["out_path"].GetString());
    }
}
