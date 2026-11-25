using System.Text.Json;

namespace Workbench;

/// <summary>The app's one reading of whether houseCARL refused a call (step 4, review F3): houseCARL refuses in its
/// answer's text, in one of two shapes, a text answer that starts <c>error:</c> or a JSON answer with <c>"ok": false</c>
/// and its reason in <c>error</c>. No public member of houseCARL at the pin decides it (its run log's reading is private
/// and counts the first shape only), so this is the app's own, and every houseCARL call the app makes goes through
/// <see cref="Checked"/> (<see cref="PatchWriter.CallAsync"/>).</summary>
internal static class HousecarlAnswer
{
    /// <summary>houseCARL's own reason when <paramref name="answer"/> is a refusal, on one line; null when it is an answer.</summary>
    internal static string? Refusal(string answer)
    {
        var t = answer.TrimStart();
        if (t.StartsWith("error:", StringComparison.Ordinal)) return OneLine(t["error:".Length..]);
        if (!t.StartsWith('{')) return null;
        try
        {
            using var d = JsonDocument.Parse(t);
            if (d.RootElement.ValueKind != JsonValueKind.Object || !d.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.False) return null;
            return d.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? OneLine(e.GetString()!) : "it gave no reason";
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The answer, or a refusal as one sentence that keeps houseCARL's reason.</summary>
    internal static string Checked(string tool, string answer) =>
        Refusal(answer) is { } why ? throw new InvalidOperationException($"houseCARL refused {What(tool)}: {why}") : answer;

    /// <summary>What the call was, in the modder's words.</summary>
    static string What(string tool) => tool switch
    {
        "housecarl_apply" => "the write",
        "housecarl_remove" => "the undo",
        "housecarl_forward" => "the copy of a record from its winner",
        "housecarl_records" => "the read",
        "housecarl_asset_status" => "the read of a file's winner",
        _ => tool,
    };

    static string OneLine(string s)
    {
        var lines = s.Replace("\r", "").Split('\n').Select(l => l.Trim().TrimStart('-').Trim()).Where(l => l.Length > 0);
        return string.Join("; ", lines).TrimEnd('.') + ".";
    }
}
