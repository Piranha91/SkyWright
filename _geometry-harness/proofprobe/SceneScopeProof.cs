using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

static class SceneScopeProof
{
    public static void Run(Action<string, bool, string> check)
    {
        var space = FormKey.Factory("00003C:Skyrim.esm");
        var seed = new SceneCapture.ImportedSeed(
            FormKey.Factory("000100:Skyrim.esm"), FormKey.Factory("000200:Skyrim.esm"), space,
            0, 0, 0, false, new Aabb(new(-10, -10, -10), new(10, 10, 10), false), null, "proof");
        var persistentFiled = SceneCapture.ExteriorUnknownDisposition(space, 100, 100, 100, [seed]);
        check("scene scope: nearby exterior unknown is admitted despite persistent-cell filing",
            persistentFiled.Membership == SceneCapture.ExteriorMembership.InNeighbourhood
            && persistentFiled.AdmitToScene && !persistentFiled.RetainUnassessed, persistentFiled.ToString());
        var distant = SceneCapture.ExteriorUnknownDisposition(space, 9000, 0, 0, [seed]);
        check("scene scope: distant-origin exterior unknown is excluded from pairs but retained as physically unassessed",
            distant.Membership == SceneCapture.ExteriorMembership.OutsideNeighbourhood
            && !distant.AdmitToScene && distant.RetainUnassessed
            && distant.ScopeReason.Contains("geometry extent is unavailable", StringComparison.Ordinal), distant.ToString());
        var absentSpace = SceneCapture.ExteriorUnknownDisposition(FormKey.Factory("000999:Skyrim.esm"), 0, 0, 0, [seed]);
        check("scene scope: absent-space seed leaves membership unresolved and retained",
            absentSpace.Membership == SceneCapture.ExteriorMembership.Unresolved
            && !absentSpace.AdmitToScene && absentSpace.RetainUnassessed, absentSpace.ToString());
        var unknownSeed = seed with { WorldBounds = null, BoundsReason = "unknown proof footprint" };
        var unknown = SceneCapture.ExteriorUnknownDisposition(space, 20_000, 0, 0, [unknownSeed]);
        check("scene scope: unknown seed footprint remains unresolved and retained without worldspace expansion",
            unknown.Membership == SceneCapture.ExteriorMembership.Unresolved
            && !unknown.AdmitToScene && unknown.RetainUnassessed, unknown.ToString());
        var invalid = SceneCapture.ExteriorUnknownDisposition(space, double.NaN, 0, 0, [seed]);
        check("scene scope: invalid placed position remains unresolved and retained",
            invalid.Membership == SceneCapture.ExteriorMembership.Unresolved
            && !invalid.AdmitToScene && invalid.RetainUnassessed, invalid.ToString());

        string root = Path.Combine(Path.GetTempPath(), "housecarl-scene-preflight-" + Guid.NewGuid().ToString("N"));
        try
        {
            string profile = Path.Combine(root, "profiles", "Proof");
            string mods = Path.Combine(root, "mods");
            Directory.CreateDirectory(profile);
            Directory.CreateDirectory(Path.Combine(mods, "Highest"));
            Directory.CreateDirectory(Path.Combine(mods, "Middle"));
            Directory.CreateDirectory(Path.Combine(mods, "Lowest"));
            File.WriteAllText(Path.Combine(profile, "modlist.txt"), "+Highest\n-Middle\n+Lowest\n");

            string candidate = Path.Combine(root, "candidate.json");
            new CandidateFile(ConfigurationIdentity.CurrentSchema, "proof", "Proof", ["Skyrim.esm"],
                ["Highest", "Lowest"], ["proof"], null).Write(candidate);
            var result = SceneCapture.PreflightCandidateAssets(candidate, "Proof", profile, mods);
            check("scene preflight: an ordered provider subset is accepted",
                result.Providers.Count == 2 && result.OmittedProviderCount == 1,
                $"selected {result.Providers.Count}; omitted {result.OmittedProviderCount}");

            new CandidateFile(ConfigurationIdentity.CurrentSchema, "proof", "Proof", ["Skyrim.esm"],
                [], ["proof"], null).Write(candidate);
            result = SceneCapture.PreflightCandidateAssets(candidate, "Proof", profile, mods);
            check("scene preflight: an explicit empty provider list is accepted",
                result.Providers.Count == 0 && result.OmittedProviderCount == 3,
                $"selected {result.Providers.Count}; omitted {result.OmittedProviderCount}");

            new CandidateFile(ConfigurationIdentity.CurrentSchema, "proof", "Proof", ["Skyrim.esm"],
                ["Lowest", "Highest"], ["proof"], null).Write(candidate);
            check("scene preflight: reversed provider priority is refused",
                Refused(candidate, "does not retain MO2 strongest-first priority", profile, mods), "priority refusal");

            new CandidateFile(ConfigurationIdentity.CurrentSchema, "proof", "Proof", ["Skyrim.esm"],
                ["Higest"], ["proof"], null).Write(candidate);
            check("scene preflight: provider-name typo is refused",
                Refused(candidate, "possible typo", profile, mods), "typo refusal");

            new CandidateFile(ConfigurationIdentity.CurrentSchema, "proof", "Proof", ["Skyrim.esm"],
                null, ["proof"], null).Write(candidate);
            check("scene preflight: missing explicit Mods is refused",
                Refused(candidate, "explicit Mods list", profile, mods), "Mods refusal");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static bool Refused(string candidate, string expected, string profile, string mods)
    {
        try { _ = SceneCapture.PreflightCandidateAssets(candidate, "Proof", profile, mods); return false; }
        catch (SceneCapture.PreflightRefusal ex)
        {
            return ex.Message.Contains(expected, StringComparison.Ordinal) && ex.Code.Length > 0 && ex.Remedy.Length > 0;
        }
    }
}
