namespace RunControl;

/// <summary>Station Q's measured primitive table, which a plan freezes a copy of and the state check passes to
/// stateprobe. stateprobe itself takes the table only by flag, so which file to freeze is decided here: the flag, else
/// HOUSECARL_PRIMITIVE_SOLIDITY, else `primitiveSolidity` in the file HOUSECARL_HARNESS_CONFIG names.</summary>
public static class PrimitiveTable
{
    public const string FileName = "inputs/primitive-solidity.json";
    public const string NoneGiven = "no --primitive-solidity, HOUSECARL_PRIMITIVE_SOLIDITY or primitiveSolidity in the harness config "
                                    + "(HOUSECARL_HARNESS_CONFIG) names one";

    /// <summary>(record, bytes, refusal): a named table that is missing or is not a table refuses the plan; none
    /// anywhere is recorded as not given.</summary>
    public static (PyDict? record, byte[]? bytes, string? refusal) Read(string? flag, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        string direct = PyValue.Strip(env("HOUSECARL_PRIMITIVE_SOLIDITY") ?? ""), config = PyValue.Strip(env("HOUSECARL_HARNESS_CONFIG") ?? "");
        string? source = null, path = null;
        if (!string.IsNullOrEmpty(flag)) (source, path) = ("--primitive-solidity", flag);
        else if (direct.Length > 0) (source, path) = ("HOUSECARL_PRIMITIVE_SOLIDITY", direct);
        else if (config.Length > 0)
        {
            string cfg = PyPath.Str(config);
            if (!File.Exists(cfg))
                return (null, null, $"HOUSECARL_HARNESS_CONFIG points at {cfg}, which does not exist; fix the variable or unset it.");
            object? got;
            try { got = PyValue.Dict(PyJson.Loads(PyJson.Decode(File.ReadAllBytes(cfg), bom: false))).Get("primitiveSolidity"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or PyError)
            {
                return (null, null, $"the harness config {cfg} could not be read as JSON ({ex.Message}); fix it before planning.");
            }
            if (got is string named && PyValue.Strip(named).Length > 0) (source, path) = ($"primitiveSolidity in {cfg}", PyValue.Strip(named));
        }
        if (path == null) return (new PyDict { ["given"] = false, ["why"] = NoneGiven }, null, null);
        if (!File.Exists(path))
            return (null, null, $"the primitive table {path} (from {source}) does not exist; point {source} at station Q's "
                                + "table or remove it to evaluate without one.");
        byte[] data = File.ReadAllBytes(path);
        object? rows;
        try { rows = PyJson.Loads(PyJson.Decode(data, bom: false)); }
        catch (FormatException ex)
        {
            return (null, null, $"the primitive table {path} (from {source}) is not JSON ({ex.Message}); fix the table or remove {source}.");
        }
        bool measured = rows is List<object?> { Count: > 0 } list
                        && list.All(r => r is PyDict d && PyValue.Strip(Py.Truthy(d.Get("evidence")) ? PyText.Str(d["evidence"]) : "").Length > 0);
        if (!measured)
            return (null, null, $"the primitive table {path} (from {source}) is not a list of measured entries each naming its "
                                + $"evidence; fix the table or remove {source}.");
        return (new PyDict
        {
            ["given"] = true, ["file"] = FileName, ["sha256"] = Hashing.Sha256Bytes(data), ["source"] = source, ["path"] = PyPath.Str(path),
            ["entries"] = (long)((List<object?>)rows!).Count,
        }, data, null);
    }
}
