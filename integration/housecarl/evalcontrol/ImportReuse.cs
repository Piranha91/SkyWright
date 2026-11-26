using RunControl;

namespace EvalControl;

/// <summary>An import declared as a reuse (moved from runcontrol's validator with the record imports): the capture was
/// measured for another setup, so it keeps its own candidate source name. runcontrol's identity checks always apply the
/// candidate-name rule; for an import declared as a reuse, this module takes back that one rejection and records, as a
/// stated limit, what the capture names instead. Only an import declares a reuse, and only this module imports.</summary>
public static class ImportReuse
{
    /// <summary>runcontrol's identity checks of an imported emit, with a declared reuse's candidate source recorded as a
    /// limit in `limits` rather than rejected. `declaredReuse` is the import's origin block when it declares a reuse.</summary>
    public static List<object?> IdentityChecks(Validator validator, PyDict identity, PyDict arm, PyDict doc, PyDict? adapter,
        List<object?> limits, PyDict? declaredReuse)
    {
        var rej = validator.IdentityChecks(identity, arm, doc, adapter, null, limits);
        if (!PyValue.Truthy(declaredReuse)) return rej;
        string source = Validator.TextOrEmpty(identity.Get("source"));
        object? candidateFile = validator.ReferenceArm(arm, null)["candidateFile"];
        string wantSource = $"candidate:{Ev.Str(candidateFile)}";
        // the one rule a declared reuse is not held to: its source names the setup it was captured for
        string mismatch = Validator.SourceMismatch(source, wantSource);
        rej.RemoveAll(x => x is PyDict r && Ev.Eq(r.Get("cause"), "identity-mismatch") && Ev.Eq(r.Get("detail"), mismatch));
        if (PyValue.Truthy(candidateFile) && PyText.Lower(source) != PyText.Lower(wantSource))
        {
            var src = Ev.OrEmpty(declaredReuse!.Get("reusedFrom"));
            limits.Add(new PyDict
            {
                ["capturedAgainst"] = source.Length > 0 ? source : null, ["armCandidate"] = wantSource,
                ["declaredSource"] = new PyDict { ["planId"] = src.Get("planId"), ["armId"] = src.Get("armId") },
                ["detail"] = source.Length > 0
                    ? $"declared reuse: the capture states it was measured against '{source}', "
                      + $"not this arm's '{wantSource}'. It is credited because its stated "
                      + "configuration equals this arm's frozen request, not because the files "
                      + "are the same file; the source name is kept as captured, and which run "
                      + "it names is the importer's caller's declaration, not verified here"
                    : "declared reuse: the capture states NO candidate source, so which file it "
                      + $"was measured against is not stated at all - this arm's is '{wantSource}'. "
                      + "It is credited because its stated configuration equals this arm's frozen "
                      + "request; nothing here says the two are the same file",
            });
        }
        return rej;
    }
}
