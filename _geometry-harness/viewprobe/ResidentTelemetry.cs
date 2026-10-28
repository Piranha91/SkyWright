using SkyRender;
using SkyRender.Character;

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

    internal static bool Failed { get; private set; }

    static string? Resolve(string? value) => string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value.Trim());

    internal static int? ReadCounter(ResidentTextureCache resident) => resident.RendersSinceRepoll;

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
                counterField = resident is null ? null : "RendersSinceRepoll",
                // Process memory at cell end (lane V, 2026-10-10): the managed heap as the last GC left it, the GC's
                // peak-free total, the GC's commit, and the OS's private bytes and peak working set.
                managedHeapBytes = GC.GetGCMemoryInfo().HeapSizeBytes,
                managedCommittedBytes = GC.GetGCMemoryInfo().TotalCommittedBytes,
                gcTotalMemoryBytes = GC.GetTotalMemory(false),
                gcHeapHardLimitBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                privateBytes = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64,
                peakWorkingSetBytes = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64,
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
