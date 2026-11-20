using HarnessShared;

namespace RunControl;

/// <summary>What the controller reads from a plugin file, through the probes' own header reader (PluginHeader).</summary>
public static class PluginFiles
{
    public static readonly string[] Suffixes = [".esp", ".esm", ".esl"];

    /// <summary>A file whose name ends in a plugin extension.</summary>
    public static bool IsPlugin(string path) => Suffixes.Contains(PyText.Lower(PyPath.Suffix(path)));

    static PluginHeader Read(string path) =>
        PluginHeader.TryRead(path)
        ?? throw new Refusal($"{PyPath.Name(path)} is not a plugin (no TES4 header); check the file at {PyOrder.Fwd(path)}.");

    /// <summary>One plugin's header as the preparation records it: path, sha256, masters, and the header's own ESM and
    /// ESL bits (what the file states; IsMaster is the load-group test).</summary>
    public static PyDict Header(string path)
    {
        string p = PyPath.Str(path);
        var h = Read(p);
        return new PyDict
        {
            ["path"] = PyOrder.Fwd(p), ["sha256"] = Hashing.Sha256File(p), ["masters"] = Py.Objs(h.Masters),
            ["esm"] = h.EsmBitSet, ["esl"] = h.EslBitSet,
        };
    }

    /// <summary>The record count a plugin's header states, or null when it states none.</summary>
    public static long? Records(string path) => Read(PyPath.Str(path)).Records;

    /// <summary>Whether a plugin loads with the masters: its header's ESM bit, or the extension the engine treats as
    /// master-flagged (the header reader's own two halves of MasterFlag).</summary>
    public static bool IsMaster(string name, PyDict headers) =>
        Py.Truthy((headers.Get(name) as PyDict)?.Get("esm")) || PluginHeader.MasterExtension(name);
}
