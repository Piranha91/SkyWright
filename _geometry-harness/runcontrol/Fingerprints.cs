using HarnessShared;

namespace RunControl;

/// <summary>`runcontrol fingerprint`: the configuration fingerprint of a recorded configuration, by the owner's rule
/// (ConfigurationIdentity.Compute), for the Python that must not hold its own copy of it.</summary>
public static class Fingerprints
{
    // (v or "") as text.
    static string Text(object? v) => PyValue.Truthy(v) ? Ev.Str(v) : "";

    /// <summary>The configuration fingerprint of a recorded configuration with another population and state, by the
    /// owner's rule (ConfigurationIdentity.Compute); null for a configuration schema the owner does not compute.</summary>
    public static string? Fingerprint(PyDict cfg, string population, string state)
    {
        if (cfg.Get("schemaVersion") is { } schema && !Ev.Eq(schema, (long)ConfigurationIdentity.CurrentSchema)) return null;
        var plugins = Ev.Items(cfg.Get("plugins")).Select(p => new ConfigPlugin(Text(Ev.G(p, "name")), "", Text(Ev.G(p, "sha256")), 0, "", false, false, [])).ToList();
        var providers = Ev.Items(cfg.Get("providers")).Select(v => new ConfigProvider(Text(Ev.G(v, "name")), Text(Ev.G(v, "kind")))).ToList();
        var overlays = Ev.Items(cfg.Get("overlays")).Select(o => new ConfigOverlay(Text(Ev.G(o, "kind")), "", Text(Ev.G(o, "sha256")))).ToList();
        return ConfigurationIdentity.Compute("", plugins, providers, overlays, population, state).Fingerprint;
    }

    /// <summary>`runcontrol fingerprint`: the fingerprint of each configuration in a JSON list, by the owner's rule. A
    /// configuration names its population and state as text; a schemaVersion other than the owner's, or none of these
    /// shapes, is refused in one sentence.</summary>
    public static List<object?> Of(object? doc)
    {
        if (doc is not List<object?> list)
            throw new Refusal("fingerprint reads a JSON list of configurations on standard input; give it a list, even of one.");
        var found = new List<object?>();
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is not PyDict cfg)
                throw new Refusal($"fingerprint: configuration {i} is not a JSON object; give plugins, providers, overlays, population and state.");
            if (cfg.Get("population") is not string population || cfg.Get("state") is not string state)
                throw new Refusal($"fingerprint: configuration {i} names no population or state as text; give both, as the run recorded them.");
            found.Add(Fingerprint(cfg, population, state) ?? throw new Refusal(
                $"fingerprint: configuration {i} has schemaVersion {PyText.Repr(cfg.Get("schemaVersion"))}, and the owner computes only "
                + $"schema {ConfigurationIdentity.CurrentSchema}; it was written by another build, so measure it again with this one."));
        }
        return found;
    }
}
