namespace RunControl;

/// <summary>An enrichment refused before anything was written; the message is one sentence (bundleenrich.Refused).</summary>
public sealed class EnrichRefused(string sentence) : Exception(sentence);

/// <summary>What every enrichment shares (bundleenrich.py's _load, _write, _row, _guard and _commit): its files'
/// JSON form, its manifest rows, the once-per-kind guard, and the record that binds it to the disposition.</summary>
public static class EnrichCommit
{
    public const int Schema = 1;

    /// <summary>The file as JSON, or null when it is absent or not JSON.</summary>
    public static object? Load(string path) => PyJson.LoadEvalFile(path);

    /// <summary>bundleenrich._write: indent 1, raw non-ASCII, a final newline, the platform's line ends.</summary>
    public static void Write(string path, object? doc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Ev.WriteText(path, PyJson.Dumps(doc, indent: 1, ensureAscii: false) + "\n");
    }

    /// <summary>One manifest row for a file the enrichment wrote; more holds the keys after recordedBy, in order.</summary>
    public static PyDict Row(string rel, string d, string kind, PyDict more)
    {
        string file = PyPath.Join(d, rel);
        var row = new PyDict
        {
            ["id"] = Hashing.Sha256Text(rel)[..16], ["kind"] = "enrichment", ["enrichment"] = kind, ["path"] = rel,
            ["sha256"] = Hashing.Sha256File(file), ["bytes"] = new FileInfo(file).Length, ["strength"] = null,
            ["provenance"] = new PyDict { ["derivedAfterMeasurement"] = true }, ["recordedBy"] = $"enrichment:{kind}",
        };
        return row.Update(more);
    }

    /// <summary>The bundle's manifest, refused when the folder is no bundle or already holds this kind.</summary>
    public static PyDict Guard(string d, string kind)
    {
        if (Load(PyPath.Join(d, "manifest.json")) is not PyDict manifest)
            throw new EnrichRefused($"{PyPath.Join(d, "manifest.json")} is absent or not JSON, so this is not a bundle");
        foreach (var e in Ev.Items(manifest.Get("evidence")))
            if (Ev.Eq(Ev.G(e, "kind"), "enrichment") && Ev.Eq(Ev.G(e, "enrichment"), kind))
                throw new EnrichRefused($"the bundle is already enriched with {kind}; enrich a fresh copy of the original instead");
        return manifest;
    }

    /// <summary>The tool an enrichment record names: this controller's own program file, where Python names
    /// bundleenrich.py. A test replaces it.</summary>
    public static Func<List<object?>> Tool = () => [new PyDict { ["file"] = "runcontrol.dll", ["sha256"] = ToolIdentity.DriverSha256() }];

    /// <summary>Writes the enrichment's record and adds its rows to the manifest's evidence, required and enrichment lists.</summary>
    public static void Commit(string d, PyDict manifest, string kind, List<PyDict> outputs)
    {
        string recRel = $"enrichment/{kind}/enrichment.json";
        Write(PyPath.Join(d, recRel), new PyDict
        {
            ["schemaVersion"] = (long)Schema, ["kind"] = kind, ["tool"] = Tool(),
            ["baseManifestSha256"] = Hashing.Sha256File(PyPath.Join(d, "manifest.json")),
            ["dispositionSha256"] = Hashing.Sha256File(PyPath.Join(d, "disposition.json")),
            ["outputs"] = outputs.Select(o => o["path"]).ToList(),
            ["note"] = "derived after measurement; changes no result, count or claim",
        });
        var rows = new List<PyDict>(outputs) { Row(recRel, d, kind, new PyDict { ["role"] = "record", ["checkId"] = null, ["armId"] = null }) };
        if (!manifest.ContainsKey("evidence")) manifest["evidence"] = new List<object?>();
        var evidence = (List<object?>)manifest["evidence"]!;
        evidence.AddRange(rows);
        if (!manifest.ContainsKey("required")) manifest["required"] = new List<object?>();
        var required = (List<object?>)manifest["required"]!;
        foreach (var r in rows)
            if (!PyValue.In(r["path"], required)) required.Add(r["path"]);
        if (!manifest.ContainsKey("enrichment")) manifest["enrichment"] = new List<object?>();
        ((List<object?>)manifest["enrichment"]!).Add(new PyDict { ["kind"] = kind, ["record"] = recRel });
        Write(PyPath.Join(d, "manifest.json"), manifest);
    }
}
