using SkyRender;
using SkyRender.Character;
using OpenTK.Graphics.OpenGL4;

namespace ViewProbe;

/// <summary>F-G4-1 (Gate B8 review Q2): a GL allocation failure around a texture upload refuses the shot before its first
/// draw, on both paths, instead of drawing a substitute. Two signals, one per path: on the retained path the dependency
/// reads the GL error queue itself after its upload and, on GL_OUT_OF_MEMORY, deletes the upload, halves the resident
/// budget and returns white without recording a missing path — white for a resolved, not-missing path has no other source;
/// on the per-shot path the dependency never reads the queue, so the error is still queued after the load. Both are
/// checked after every load. Never called "missing" or "untextured": the asset exists; the driver could not hold it.</summary>
internal sealed class GlAllocationRefusal : Exception
{
    GlAllocationRefusal(string message) : base(message) { }

    internal static void Check(string category, string gamePath, int loaded, int white, bool missing, bool resolved,
        ResidentTextureCache? resident)
    {
        bool queued = false;
        for (int i = 0; i < 8; i++)
        {
            var error = GL.GetError();
            if (error == ErrorCode.NoError) break;
            if (error == ErrorCode.OutOfMemory) queued = true;
        }
        bool degraded = resident is not null && loaded == white && !missing && resolved;
        if (!queued && !degraded) return;
        string budget = resident is null
            ? "this path keeps no resident texture cache"
            : FormattableString.Invariant($"the resident texture budget is now {resident.BudgetBytes / (1024 * 1024)} MiB");
        throw new GlAllocationRefusal(FormattableString.Invariant(
            $"texture upload refused before draw: the GL driver reported out of memory (GL_OUT_OF_MEMORY) during or before the upload of '{gamePath}' ({category}); {budget}. The shot is refused rather than drawn with a substitute texture."));
    }
}
