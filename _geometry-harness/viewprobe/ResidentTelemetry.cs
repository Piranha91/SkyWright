using CharacterViewer.Rendering;

namespace ViewProbe;

/// <summary>Opt-in resident texture cache telemetry for memory measurement. Off unless VP_RESIDENT_TELEMETRY_OUT names an
/// output file. With it set, a static worklist appends one JSON line per cell at cell end, on every exit path: the cell's
/// identity, how its shot loop ended, its shot and render-pass counts, and the resident cache's budget and repoll counter
/// at that moment. The record describes how the shot loop ended, not how the cell tore down: it is written before the
/// cell's graphics owners are disposed, so a teardown failure after it shows only in the process exit code, and a reader
/// must check that the two agree. Nothing is written per shot or per texture operation, and nothing is printed unless the
/// write fails, which prints one line and makes the run exit 2.</summary>
internal static class ResidentTelemetry
{
    internal const string Variable = "VP_RESIDENT_TELEMETRY_OUT";
    static readonly string? OutputPath = Resolve(Environment.GetEnvironmentVariable(Variable));
    // Rendering exposes BudgetBytes but not its repoll counter; the private field is read only when telemetry is on.
    static readonly System.Reflection.FieldInfo? CounterField = typeof(ResidentTextureCache).GetField("_rendersSinceRepoll",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    internal static bool Failed { get; private set; }

    static string? Resolve(string? value) => string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value.Trim());

    internal static int? ReadCounter(ResidentTextureCache resident) => CounterField?.GetValue(resident) as int?;

    internal static void WriteCell(int cellIndex, string cell, bool reuseCell, string outcome, int boundaryExit, int shots,
        GraphicsCell? graphics)
    {
        if (OutputPath is null) return;
        try
        {
            ResidentTextureCache? resident = graphics?.ResidentOrNull;
            var record = new
            {
                schema = "viewprobe-resident-telemetry-v1",
                processId = Environment.ProcessId,
                cellIndex,
                cell,
                reuseCell,
                outcome,
                boundaryExit,
                shots,
                passes = graphics?.Passes,
                residentCache = resident is not null,
                budgetBytes = resident?.BudgetBytes,
                rendersSinceRepoll = resident is null ? null : ReadCounter(resident),
                counterField = resident is null ? null : CounterField is null ? "absent" : "_rendersSinceRepoll",
                writtenUtc = DateTimeOffset.UtcNow,
            };
            File.AppendAllText(OutputPath, System.Text.Json.JsonSerializer.Serialize(record) + "\n",
                new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Failed = true;
            Console.Error.WriteLine($"resident telemetry: could not append the cell record to {OutputPath}: {ex.Message}");
        }
    }
}
