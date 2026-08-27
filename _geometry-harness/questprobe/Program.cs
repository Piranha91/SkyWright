using System.Reflection;

// Reflection probe over Mutagen.Bethesda.Skyrim 0.54.4 — the EXACT version houseCARL pins.
//
// Purpose: ground the QUEST DEPENDENCY GRAPH before a line is written against it.
//
// The question this has to answer, from the scenario-consistency design:
//   quest -> objective -> target -> alias -> (unique actor | forced ref | location | runtime match)
// An objective target that resolves to an ACTOR follows that actor wherever a mod moves them.
// One that resolves to a FIXED MARKER does not, and breaks silently when the world around it
// is rearranged. Everything downstream depends on Mutagen surfacing that distinction, so it is
// established here first rather than assumed.
//
// Phase 1 = discovery (what types even exist), phase 2 = full member dump of the ones that matter.

var asms = new[]
{
    typeof(Mutagen.Bethesda.Skyrim.Quest).Assembly,          // Mutagen.Bethesda.Skyrim
    typeof(Mutagen.Bethesda.Plugins.FormKey).Assembly,       // Mutagen.Bethesda.Core
};

string Pretty(Type? t)
{
    if (t == null) return "?";
    if (!t.IsGenericType) return t.Name;
    var n = t.Name[..t.Name.IndexOf('`')];
    return n + "<" + string.Join(",", t.GetGenericArguments().Select(Pretty)) + ">";
}

var all = asms.SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
              .Where(t => t.IsPublic).ToList();

Console.WriteLine($"public types scanned: {all.Count:N0}\n");

// ---------- phase 1: discovery ----------
void Discover(string label, params string[] needles)
{
    var hits = all.Where(t => needles.Any(n => t.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
                  .Select(t => t.Name)
                  .Where(n => !n.EndsWith("BinaryOverlay") && !n.EndsWith("BinaryCreateTranslation")
                           && !n.EndsWith("BinaryWriteTranslation") && !n.EndsWith("SetterCommon")
                           && !n.EndsWith("CommonGetter") && !n.EndsWith("Common") && !n.EndsWith("MixIn")
                           && !n.EndsWith("Mask") && !n.EndsWith("ErrorMask") && !n.EndsWith("TranslationMask"))
                  .Distinct().OrderBy(x => x).ToList();
    Console.WriteLine($"=== {label}  ({hits.Count}) ===");
    Console.WriteLine("   " + string.Join(", ", hits));
    Console.WriteLine();
}

Discover("QUEST", "Quest");
Discover("ALIAS", "Alias");
Discover("OBJECTIVE / TARGET", "Objective", "QuestTarget");
Discover("PACKAGE", "Package");
Discover("LOCATION", "Location");
Discover("SCENE", "Scene");
Discover("NAVMESH", "Navmesh", "NavigationMesh", "NavigationDoor");
Discover("STORY MANAGER", "StoryManager");
Discover("SCRIPT/VMAD", "ScriptEntry", "ScriptProperty", "VirtualMachineAdapter");

// ---------- phase 2: full dump ----------
void Dump(string typeName)
{
    var t = all.FirstOrDefault(x => x.Name == typeName);
    if (t == null) { Console.WriteLine($"### {typeName}  -- NOT FOUND\n"); return; }
    Console.WriteLine($"### {t.FullName}");
    var ifaces = t.GetInterfaces().Where(i => i.IsPublic).Take(10).Select(Pretty).ToList();
    if (ifaces.Count > 0) Console.WriteLine($"    : {string.Join(", ", ifaces)}");
    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name))
        Console.WriteLine($"    P {Pretty(p.PropertyType),-56} {p.Name}");
    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                       .Where(f => f.DeclaringType == t).OrderBy(f => f.Name).Take(40))
        Console.WriteLine($"    F {Pretty(f.FieldType),-56} {f.Name}"
                        + (f.IsLiteral ? $" = {f.GetRawConstantValue()}" : ""));
    Console.WriteLine();
}

Console.WriteLine("──────────── the quest graph ────────────\n");
foreach (var n in new[]
{
    "IQuestGetter",
    "IQuestObjectiveGetter",
    "IQuestTargetGetter",
    "IQuestObjectiveTargetGetter",
    "IFindMatchingRefNearAliasGetter",
    "IFindMatchingRefFromEventGetter",
    "ILocationAliasReferenceGetter",
    "IExternalAliasReferenceGetter",
    "ICreateReferenceToObjectGetter",
    "IAPackageTargetGetter",
    "IPackageTargetSpecificReferenceGetter",
    "IPackageTargetAliasGetter",
    "IPackageTargetObjectIDGetter",
    "IPackageDataLocationGetter",
    "IPackageDataTargetGetter",
    "IALocationTargetGetter",
    "ILocationTargetGetter",
    "IPatrolGetter",
    "IQuestAliasGetter",
    "IQuestFragmentAliasGetter",
    "QuestAlias",
    "IPackageGetter",
    "IPackageTargetGetter",
    "IPackageLocationGetter",
    "ILocationGetter",
    "IPlacedObjectGetter",
    "ISceneGetter",
    "ISceneActionGetter",
    "ISceneAdapterGetter",
    "ISceneActorGetter",
    "IScenePhaseGetter",
    "IPackageDataObjectListGetter",
    "INavigationDoorLinkGetter",
    "INavigationMeshGetter",
    "INavigationMeshDataGetter",
    "IDoorTriangleGetter",
    "IEdgeLinkGetter",
    "INavmeshTriangleGetter",
    "IStoryManagerBranchNodeGetter",
    "IStoryManagerQuestNodeGetter",
})
    Dump(n);

// Enums decide how an alias resolves; print every value so the taxonomy is grounded, not guessed.
Console.WriteLine("──────────── enums that decide resolution ────────────\n");
foreach (var t in all.Where(t => t.IsEnum &&
        (t.Name.Contains("Alias", StringComparison.OrdinalIgnoreCase)
      || t.Name.Contains("QuestTarget", StringComparison.OrdinalIgnoreCase)
      || t.Name.Contains("PackageTarget", StringComparison.OrdinalIgnoreCase)
      || t.Name.Contains("PackageLocation", StringComparison.OrdinalIgnoreCase)))
        .OrderBy(t => t.Name))
{
    Console.WriteLine($"### enum {t.FullName}");
    foreach (var v in Enum.GetValues(t)) Console.WriteLine($"    {Convert.ToInt64(v),6}  {v}");
    Console.WriteLine();
}

// Nested types on QuestAlias carry the per-arm shapes in Mutagen's generated model.
Console.WriteLine("──────────── nested types under Quest / QuestAlias / PackageTarget ────────────\n");
foreach (var host in new[] { "QuestAlias", "QuestObjective", "APackageTarget", "PackageDataLocation", "ALocationTarget" })
{
    var t = all.FirstOrDefault(x => x.Name == host);
    if (t == null) { Console.WriteLine($"### {host} -- NOT FOUND\n"); continue; }
    var nested = t.GetNestedTypes(BindingFlags.Public).OrderBy(x => x.Name).ToList();
    Console.WriteLine($"### {host}: {nested.Count} nested");
    foreach (var n in nested)
    {
        Console.WriteLine($"    {(n.IsEnum ? "enum " : n.IsInterface ? "iface" : "class")} {n.Name}"
                        + (n.IsEnum ? "  { " + string.Join(", ", Enum.GetNames(n)) + " }" : ""));
        if (!n.IsEnum && n.Name is not ("Mask" or "ErrorMask" or "TranslationMask"))
            foreach (var p in n.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name).Take(20))
                Console.WriteLine($"        P {Pretty(p.PropertyType),-48} {p.Name}");
    }
    Console.WriteLine();
}
