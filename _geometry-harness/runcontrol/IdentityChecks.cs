namespace RunControl;

/// <summary>One role of a receipt whose bound program identity is not the plan's.</summary>
public sealed record IdentityDefect(string Role, string Program, string Kind, object? Recorded, object? Found);

/// <summary>The offline identity comparison (evalrun.py): each receipt's bound digests against the plan's record,
/// from retained metadata only. No tool is read, so a plan made by either controller is compared the same way.</summary>
public static class IdentityChecks
{
    /// <summary>Why a plan without the tool identity contract cannot run, resume or import; null when it has it.</summary>
    public static string? ContractRefusal(PyDict plan)
    {
        if (plan.Get("toolIdentity") is PyDict ti && Ev.Eq(ti.Get("contract"), (long)EvalStore.ToolIdentityContract)
            && ti.Get("programs") is PyDict) return null;
        return $"plan-identity-outdated: plan.json records no tool identity (toolIdentity contract {EvalStore.ToolIdentityContract}), "
            + "so the programs this would launch cannot be compared with the ones it was planned with. Make a new plan in a new directory.";
    }

    /// <summary>Every role of a receipt whose bound identity is not the plan's. A role with no digest is
    /// tool-identity-missing; two absent digests are never equal. A plan without the contract has nothing to compare.</summary>
    public static List<IdentityDefect> Defects(PyDict plan, PyDict receipt, IReadOnlySet<string>? moreLaunchKinds = null)
    {
        var found = new List<IdentityDefect>();
        if (ContractRefusal(plan) != null) return found;
        var progs = PyValue.Dict(PyValue.Dict(plan["toolIdentity"])["programs"]);
        object? bound = Ev.OrEmpty(receipt.Get("consumed")).Get("programs");
        foreach (var (role, pidRaw) in Receipts.ReceiptRoles(receipt, moreLaunchKinds))
        {
            string pid = (string)pidRaw!;
            var want = progs.Get(pid) as PyDict ?? new PyDict();
            object? wd = want.Get("digest");
            var row = bound is PyDict b ? b.Get(role) as PyDict : null;
            object? got = row?.Get("digest");
            if (got is not string g || g.Length == 0)
                found.Add(new(role, pid, "tool-identity-missing", wd, got));
            else if (wd is not string w || w.Length == 0 || !Ev.Eq(want.Get("state"), "present") || g != w
                     || !Ev.Eq(row!.Get("program"), pid))
                found.Add(new(role, pid, "tool-identity-mismatch", wd, got));
        }
        return found;
    }

    /// <summary>What a disposition says about tool identity: verified under the contract, legacy without it.</summary>
    public static PyDict Guarantee(PyDict plan)
    {
        if (ContractRefusal(plan) != null)
            return new PyDict
            {
                ["guarantee"] = "legacy",
                ["detail"] = "this plan predates tool identity: which build produced each result was not checked",
            };
        return new PyDict
        {
            ["guarantee"] = "verified",
            ["detail"] = "every accepted local result is bound, per role, to the program identity this plan froze; "
                + "evaluation.integrity lists any receipt that is not",
        };
    }
}
