using Mutagen.Bethesda.Pex;

using HousecarlCore;

namespace SkyWright.Engine;

/// <summary>What one compiled script says about the classes it needs: its parent, the classes it calls statically,
/// and the other classes its variables, parameters, locals and properties are typed as.</summary>
public sealed record ScriptFacts(string Name, string? Parent, IReadOnlyCollection<string> StaticCalls, IReadOnlyCollection<string> TypeMentions);

/// <summary>Shape 4 — a script's dependency absent. The engine's behaviour, observed in this machine's own VR game logs
/// (Papyrus.0-3.log, Nov 2025, an earlier state of the order; the Papyrus VM is closed source):
///   4a  parent absent  — "Cannot open store for class P", "Unable to link "P" - the parent of "S"", then "Script S
///       cannot be bound … does not exist": the script is not attached at all. A finding.
///   4b  static call to an absent class — at call time "Cannot open store", "Static function F not found on object C.
///       Aborting call and returning None": that call returns None and the script otherwise runs. A look: what the
///       script does with None is not read.
///   4c  another mention of an absent type (a variable, parameter, local or property) — not observed; a count only.
/// A script is reached when a record or quest alias attaches it (VMAD), or a reached script extends or calls it.
/// Fragment scripts (dialogue, quest stage, scene, perk) and scripts a DLL or an MCM starts are not followed: an
/// unreached script is set aside under that name.</summary>
public static class ScriptDependencyLook
{
    public const string Shape = "scriptDependency";

    static readonly HashSet<string> Primitive = new(StringComparer.OrdinalIgnoreCase) { "int", "float", "bool", "string", "var", "none", "" };

    /// <summary>Read one parsed .pex into facts — every object it carries.</summary>
    public static IEnumerable<ScriptFacts> FactsOf(PexFile pex)
    {
        foreach (var obj in pex.Objects)
        {
            if (obj.Name is not { Length: > 0 } name) continue;
            var calls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Type(string? t)
            {
                if (t is null) return;
                t = t.Trim();
                if (t.EndsWith("[]", StringComparison.Ordinal)) t = t[..^2];
                if (!Primitive.Contains(t)) types.Add(t);
            }
            foreach (var v in obj.Variables) Type(v.TypeName);
            foreach (var p in obj.Properties) Type(p.TypeName);
            var bodies = obj.States.SelectMany(st => st.Functions).Select(nf => nf.Function)
                .Concat(obj.Properties.SelectMany(pr => new[] { pr.ReadHandler, pr.WriteHandler }));
            foreach (var body in bodies)
            {
                if (body is null) continue;
                Type(body.ReturnTypeName);
                foreach (var p in body.Parameters) Type(p.TypeName);
                foreach (var l in body.Locals) Type(l.TypeName);
                foreach (var ins in body.Instructions)
                    if (ins.OpCode == InstructionOpcode.CALLSTATIC && ins.Arguments.Count > 0
                        && ins.Arguments[0].VariableType == VariableType.Identifier && ins.Arguments[0].StringValue is { Length: > 0 } cls)
                        calls.Add(cls);
            }
            calls.Remove(name); types.Remove(name);
            yield return new ScriptFacts(name, obj.ParentClassName is { Length: > 0 } par ? par : null, calls, types);
        }
    }

    /// <summary><paramref name="supplied"/>: every class the order supplies → its facts and the mod that wins it
    /// (null for the base game's archives). <paramref name="attached"/>: script names a winning record of an active
    /// plugin attaches. <paramref name="onDisk"/>: a class name → a folder holding its .pex that the order does not load,
    /// or null. Class names compare case-insensitively, as the VM does.</summary>
    public static LookResult Evaluate(IReadOnlyDictionary<string, (ScriptFacts Facts, string? Provider)> supplied,
                                      IReadOnlyCollection<string> attached, Func<string, string?> onDisk)
    {
        var r = new LookResult(Shape);
        // Reach: attachments, then parents and called classes of what is reached.
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var q = new Queue<string>(attached);
        while (q.Count > 0)
        {
            var s = q.Dequeue();
            if (!reached.Add(s)) continue;
            if (!supplied.TryGetValue(s, out var f)) continue;
            if (f.Facts.Parent is { } p) q.Enqueue(p);
            foreach (var c in f.Facts.StaticCalls) q.Enqueue(c);
        }

        var rows = new Dictionary<(string Cls, string Missing, string? Provider), List<string>>();
        foreach (var (name, (facts, provider)) in supplied.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            void Missing(string cls, string missing)
            {
                if (supplied.ContainsKey(missing)) return;
                if (!reached.Contains(name)) { r.SetAside($"{cls}: nothing found attaching or calling the script (fragments and DLL- or MCM-started scripts are not followed)", $"{name} -> {missing}"); return; }
                if (cls == "4c") { r.SetAside("4c: a variable, parameter or property typed as a class no mod supplies (engine behaviour not observed)", $"{name} -> {missing}"); return; }
                var key = (cls, missing, provider);
                if (!rows.TryGetValue(key, out var l)) rows[key] = l = new List<string>();
                l.Add(name);
            }
            if (facts.Parent is { } par) Missing("4a", par);
            foreach (var c in facts.StaticCalls) Missing("4b", c);
            foreach (var t in facts.TypeMentions) if (t != facts.Parent && !facts.StaticCalls.Contains(t)) Missing("4c", t);
        }

        foreach (var ((cls, missing, provider), scripts) in rows.OrderBy(k => k.Key.Cls).ThenBy(k => k.Key.Missing, StringComparer.OrdinalIgnoreCase))
        {
            var where = onDisk(missing);
            string has = where is null ? "no mod on disk has it" : $"'{where}' has it on disk and is not enabled";
            string list = string.Join(", ", scripts.Take(6)) + (scripts.Count > 6 ? $" and {scripts.Count - 6} more" : "");
            var mods = new List<string> { provider ?? "the base game" };
            if (where is not null) mods.Add(where);
            if (cls == "4a")
                r.Rows.Add(new LookRow(Shape, "parent-absent", LookKind.Finding, mods, Array.Empty<string>(), scripts.Select(s => $"scripts\\{s}.pex").ToList(),
                    $"{list} ({provider ?? "base game"}) extend{(scripts.Count == 1 ? "s" : "")} {missing}, whose .pex no active mod supplies; {has}.",
                    $"The game cannot link {(scripts.Count == 1 ? "this script" : "these scripts")}, so {(scripts.Count == 1 ? "it is" : "they are")} not attached to anything.",
                    "game-log: this machine's VR Papyrus logs (Nov 2025, an earlier order state): 'Unable to link … the parent of …' then 'cannot be bound … does not exist'"));
            else
                r.Rows.Add(new LookRow(Shape, "static-call-absent", LookKind.Look, mods, Array.Empty<string>(), scripts.Select(s => $"scripts\\{s}.pex").ToList(),
                    $"{list} ({provider ?? "base game"}) call{(scripts.Count == 1 ? "s" : "")} a function of {missing}, whose .pex no active mod supplies; {has}.",
                    "Each such call returns None in game and the script carries on; whether that breaks what the script does is untested (a guarded optional call is harmless).",
                    "game-log: this machine's VR Papyrus logs (Nov 2025, an earlier order state): 'Static function … not found on object … Aborting call and returning None'; the script's handling of None: unasserted"));
        }
        r.Number();
        return r;
    }
}
