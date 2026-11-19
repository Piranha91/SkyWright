using System.Text.RegularExpressions;
using HarnessShared;

namespace RunControl;

/// <summary>Every layer's copy of one plugin name as a plan records it: rows {layer, path, sha256} in the probes'
/// precedence, whether each is in the view, and whether the game's Data folder could be walked.</summary>
public sealed record LayerWalk(List<PyDict> Rows, List<bool> InView, bool GameWalked);

/// <summary>One plan's reading of the instance's plugin layers. The walk is the probes' own
/// (HarnessOrderCli.PluginLayerCopies), made once per view.</summary>
public sealed class PlanLayers(string instance, string? profile, string? game)
{
    readonly Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<HarnessOrderCli.PluginLayerCopy>>> _views = new(StringComparer.Ordinal);
    readonly HashMemo _hashes = new();

    public LayerWalk Walk(string plain, IReadOnlyList<string> view, bool hashRows = true)
    {
        string? profileDir = string.IsNullOrEmpty(profile) ? null : Path.Combine(instance, "profiles", profile);
        string key = string.Join("\0", view);
        if (!_views.TryGetValue(key, out var copies))
            _views[key] = copies = HarnessOrderCli.PluginLayerCopies(
                profileDir, view, Path.Combine(instance, "mods"), string.IsNullOrEmpty(game) ? "" : Path.Combine(game, "Data"),
                Path.Combine(instance, "overwrite"));
        var mine = copies.TryGetValue(plain, out var found) ? found : [];
        var rows = mine.Select(c => new PyDict { ["layer"] = c.Layer, ["path"] = c.Path, ["sha256"] = hashRows ? _hashes.Sha256(c.Path) : null }).ToList();
        return new LayerWalk(rows, mine.Select(c => c.InView).ToList(), !string.IsNullOrEmpty(game));
    }
}

/// <summary>An arm that declares a mod list other than the scope's: what the plan needs to say which copy of a
/// scope-listed plugin that list puts first.</summary>
public sealed record OwnView(bool Complete, List<string> ArmView, List<string> ScopeView, PyDict ScopeProvider, Func<string, bool, LayerWalk> Walk);

/// <summary>The frozen request of an arm: its plugin rows, its providers, and which layer provides a supplied file.</summary>
public static class PlanArms
{
    static readonly Regex Sha256Form = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

    /// <summary>A 64-hex sha256 as text.</summary>
    public static bool IsSha256(object? v) => v is string s && Sha256Form.IsMatch(s);

    /// <summary>d[a] when it has the key, else d[b] when it has that, else the fallback.</summary>
    public static object? Either(PyDict d, string a, string b, object? fallback = null) =>
        d.ContainsKey(a) ? d[a] : d.ContainsKey(b) ? d[b] : fallback;

    /// <summary>Which layer provides a declared file: in-view (the scope lists it or a view layer has it), out-of-view
    /// (only a folder outside the view has it) or missing.</summary>
    public static string Provides(string name, PyDict scopePlugins, Func<string, LayerWalk?>? resolve)
    {
        if (scopePlugins.ContainsKey(PyText.Lower(name))) return "in-view";
        var res = resolve?.Invoke(name);
        if (res == null || res.InView.Count == 0) return "missing";
        return res.InView[0] ? "in-view" : "out-of-view";
    }

    // Without a walk: the scope's copy is still read when the arm's mod list adds no folder above the layer that gave it.
    static bool ScopeCopyStillWins(string plain, OwnView own)
    {
        object? provider = own.ScopeProvider.Get(PyText.Lower(plain));
        if (!Py.Truthy(provider)) return false;
        var arm = own.ArmView.Select(PyText.Lower).ToList();
        var scope = own.ScopeView.Select(PyText.Lower).ToList();
        string p = PyText.Lower(PyText.Str(provider));
        if (p == "overwrite") return true;
        if (p == "data") return arm.All(scope.Contains);
        if (!arm.Contains(p) || !scope.Contains(p)) return false;
        var above = scope.Take(scope.IndexOf(p)).ToHashSet(StringComparer.Ordinal);
        return arm.Take(arm.IndexOf(p)).All(above.Contains);
    }

    // A scope-listed plugin in an arm with its own mod list: the scope's row when that list's first copy is the scope's
    // file or has its bytes, else a row naming the list's copy; a walk that cannot be made states nothing.
    static PyDict OwnViewRow(object? name, PyDict scoped, OwnView own)
    {
        string plain = PyPath.Name(PyText.Str(name));
        if (!own.Complete)
        {
            if (ScopeCopyStillWins(plain, own)) return scoped;
            return new PyDict
            {
                ["name"] = PyText.Str(name), ["sha256"] = null, ["path"] = null, ["hashSource"] = "unstated", ["scopeSha256"] = scoped.Get("sha256"),
                ["unstatedReason"] = "this arm declares its own mod list, different from the scope's, and the plan was "
                                     + "not given all of --instance, --game and --profile, so it cannot say whether that "
                                     + $"list puts another copy of {plain} first; the scope's hash is not assumed",
            };
        }
        var res = own.Walk(plain, false);
        if (res.Rows.Count == 0)
            return new PyDict
            {
                ["name"] = PyText.Str(name), ["sha256"] = null, ["path"] = null, ["hashSource"] = "unstated", ["scopeSha256"] = scoped.Get("sha256"),
                ["unstatedReason"] = $"no layer of this arm's view, the game's Data folder or the profile provides {plain}",
            };
        var win = res.Rows[0];
        string winPath = Py.S(win["path"]);
        if (Py.Truthy(scoped.Get("path")) && PyOrder.SamePathGiven(winPath, PyText.Str(scoped["path"]))) return scoped;
        string? sha = Hashing.Sha256FileOrNull(winPath);
        if (sha != null && Py.Truthy(scoped.Get("sha256")) && PyText.Lower(sha) == PyText.Lower(PyText.Str(scoped["sha256"]))) return scoped;
        var full = own.Walk(plain, true);
        var row = new PyDict
        {
            ["name"] = PyText.Str(name), ["sha256"] = sha, ["path"] = winPath,
            ["hashSource"] = sha != null ? $"instance:{win["layer"]}" : "unstated", ["scopeSha256"] = scoped.Get("sha256"),
            ["differsFromScope"] = $"this arm's mod list puts the copy of {plain} in '{win["layer"]}' first; it is not the copy the scope measured",
            ["providedBy"] = full.Rows.Select(r => (object?)new PyDict(r)).ToList(),
        };
        if (sha == null) row["unstatedReason"] = $"the copy of {plain} at {winPath} would not hash, so the plan states no content for it";
        return row;
    }

    /// <summary>One row of an arm's requested plugins: the scope's path and hash for a name the scope lists; for any
    /// other name the copy the probes would read, when every layer they walk could be walked; else unstated, saying why.</summary>
    public static PyDict RequestedPlugin(object? name, PyDict scopePlugins, Func<string, LayerWalk?>? resolve, OwnView? own)
    {
        string plain = PyPath.Name(PyText.Str(name));
        PyDict row;
        if (scopePlugins.Get(PyText.Lower(plain)) is PyDict scoped)
        {
            object? sha = IsSha256(scoped.Get("sha256")) ? scoped["sha256"] : null;
            row = new PyDict { ["name"] = PyText.Str(name), ["sha256"] = sha, ["path"] = scoped.Get("path"), ["hashSource"] = sha != null ? "scope" : "unstated" };
            if (sha == null) row["unstatedReason"] = $"the measured scope lists {plain} with no 64-hex sha256, so the plan states no content for it";
            return own != null ? OwnViewRow(name, row, own) : row;
        }
        row = new PyDict { ["name"] = PyText.Str(name), ["sha256"] = null, ["path"] = null, ["hashSource"] = "unstated" };
        var res = resolve?.Invoke(plain);
        if (res == null)
        {
            row["unstatedReason"] = $"no --instance was given, so the plan cannot resolve {plain} the way the probes resolve it";
            return row;
        }
        var win = res.Rows.Count > 0 ? res.Rows[0] : null;
        if (!res.GameWalked)
            row["unstatedReason"] = "no --game was given, so the game's Data folder — a layer the probes always walk, above the "
                                    + "profile's mod folders outside this configuration's view — was not walked, and the copy of "
                                    + $"{plain} the probes would read cannot be named";
        else if (win != null && IsSha256(win.Get("sha256")))
        {
            row["sha256"] = win["sha256"];
            row["path"] = win["path"];
            row["hashSource"] = $"instance:{win["layer"]}";
        }
        else if (win != null)
            row["unstatedReason"] = $"the copy of {plain} at {win["path"]} would not hash, so the plan states no content for it";
        else
            row["unstatedReason"] = $"no layer provides {plain}: not the overwrite folder, not a mod folder "
                                    + "of this configuration's view, not the game's Data folder, and not one "
                                    + "of the profile's other mod folders";
        if (res.Rows.Count > 0) row["providedBy"] = res.Rows.Select(r => (object?)new PyDict(r)).ToList();
        return row;
    }

    /// <summary>The asset providers an arm reads: the scope's as recorded when the candidate declares no mod list;
    /// else the scope's overwrite rows, the declared folders, then the scope's rows that are neither.</summary>
    public static List<object?> RequestedProviders(List<PyDict> scopeProviders, List<string>? viewMods)
    {
        var rows = scopeProviders.Select(v => new PyDict(v)).ToList();
        if (viewMods == null) return rows.Cast<object?>().ToList();
        static string Kind(PyDict v) => PyText.Lower(Py.Truthy(v.Get("kind")) ? PyText.Str(v["kind"]) : "");
        return rows.Where(v => Kind(v) == "overwrite").Cast<object?>()
            .Concat(viewMods.Select(m => (object?)new PyDict { ["name"] = m, ["kind"] = "mod" }))
            .Concat(rows.Where(v => Kind(v) is not ("overwrite" or "mod"))).ToList();
    }
}
