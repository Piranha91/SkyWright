namespace RunControl;

/// <summary>An instance with one header-only plugin active and nothing else (bundleenrich.private_instance), so every
/// record read by absolute path is an off-order read of the file at that path.</summary>
public static class PrivateInstance
{
    public const string Anchor = "PrivateInstanceAnchor.esp";

    /// <summary>The anchor plugin's bytes: a TES4 record holding one HEDR (version 1.71, no records, next id 0x800).</summary>
    public static byte[] AnchorBytes()
    {
        using var m = new MemoryStream();
        using var w = new BinaryWriter(m);
        w.Write("TES4"u8);
        w.Write(18u); w.Write(0u); w.Write(0u); w.Write(0u); w.Write((ushort)44); w.Write((ushort)0);
        w.Write("HEDR"u8);
        w.Write((ushort)12); w.Write(1.71f); w.Write(0); w.Write(0x800u);
        w.Flush();
        return m.ToArray();
    }

    /// <summary>Builds the instance under root and returns the reader's environment, in Python's key order.</summary>
    public static PyDict Create(string root)
    {
        foreach (string sub in new[] { "data", "Data", "mods", "profile" }) Directory.CreateDirectory(PyPath.Join(root, sub));
        File.WriteAllBytes(PyPath.Join(root, "Data", Anchor), AnchorBytes());
        Ev.WriteText(PyPath.Join(root, "profile", "plugins.txt"), "*" + Anchor + "\n");
        Ev.WriteText(PyPath.Join(root, "profile", "loadorder.txt"), Anchor + "\n");
        Ev.WriteText(PyPath.Join(root, "profile", "modlist.txt"), "");
        return new PyDict
        {
            ["HOUSECARL_DATA_DIR"] = PyPath.Join(root, "data"), ["HouseCarl__DataDir"] = PyPath.Join(root, "Data"),
            ["HouseCarl__ModsDir"] = PyPath.Join(root, "mods"), ["HouseCarl__ProfileDir"] = PyPath.Join(root, "profile"),
        };
    }
}
