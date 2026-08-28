using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

// Reflection probe over Mutagen.Bethesda.Skyrim 0.54.4 — the EXACT version houseCARL pins.
// Purpose: ground the placement + navmesh API before any code is written against it.

var asms = new[] {
    typeof(Mutagen.Bethesda.Skyrim.PlacedObject).Assembly,          // Mutagen.Bethesda.Skyrim
    typeof(Mutagen.Bethesda.Plugins.FormKey).Assembly,              // Mutagen.Bethesda.Core
};

string Pretty(Type t)
{
    if (t == null) return "?";
    if (!t.IsGenericType) return t.Name;
    var n = t.Name.Substring(0, t.Name.IndexOf('`'));
    return n + "<" + string.Join(",", t.GetGenericArguments().Select(Pretty)) + ">";
}

// LAND terrain — grounded before viewprobe draws it (heightmap layout, offset semantics).
Console.WriteLine("=== LAND terrain surface ===");
Dump("ILandscapeGetter");
Dump("LandscapeBinaryOverlay");
Dump("ILandscapeVertexHeightMapGetter");
Dump("LandscapeVertexHeightMapBinaryOverlay");
Dump("ICellGetter");

void Dump(string typeName)
{
    Type t = null;
    foreach (var a in asms) { t = a.GetTypes().FirstOrDefault(x => x.Name == typeName); if (t != null) break; }
    if (t == null) { Console.WriteLine($"### {typeName}  -- NOT FOUND"); return; }
    Console.WriteLine($"### {t.FullName}   (interfaces: {string.Join(", ", t.GetInterfaces().Take(8).Select(Pretty))})");
    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name))
        Console.WriteLine($"    P {Pretty(p.PropertyType),-52} {p.Name}{(p.CanWrite ? " {get;set;}" : " {get;}")}");
    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(f => f.Name))
        Console.WriteLine($"    F {Pretty(f.FieldType),-52} {f.Name}");
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                       .Where(m => !m.IsSpecialName && m.DeclaringType == t).OrderBy(m => m.Name).Take(25))
        Console.WriteLine($"    M {Pretty(m.ReturnType),-52} {m.Name}({string.Join(", ", m.GetParameters().Select(x => Pretty(x.ParameterType) + " " + x.Name))})");
    Console.WriteLine();
}

foreach (var n in new[] {
    "IPlacedObjectGetter", "IPlacedNpcGetter", "IPlacedGetter", "IPlacedSimpleGetter",
    "Placement", "IPlacementGetter",
    "ICellGetter", "IWorldspaceGetter",
    "ICellBlockGetter", "ICellSubBlockGetter",
    "IWorldspaceBlockGetter", "IWorldspaceSubBlockGetter",
    "INavigationMeshGetter", "INavigationMeshDataGetter",
    "INavmeshTriangleGetter", "INavmeshGeometryGetter",
    "IModelGetter", "IStaticGetter",
    "ISimpleModelGetter", "IModeledGetter", "ICellGridGetter",
    "IPlacedPrimitiveGetter", "IObjectBoundsGetter",
    "P3Float", "P3Int16", "P2Int16",
    "IANavmeshParentGetter", "IDoorTriangleGetter", "IPlaceableObjectGetter",
})
    Dump(n);

// What placed-record types exist at all? (the reflection-over-Mutagen generality check)
Console.WriteLine("### Types assignable to IPlacedGetter");
var placedIface = asms[0].GetTypes().FirstOrDefault(x => x.Name == "IPlacedGetter");
if (placedIface != null)
    foreach (var t in asms[0].GetTypes().Where(t => placedIface.IsAssignableFrom(t) && t != placedIface).OrderBy(t => t.Name))
        Console.WriteLine($"    {(t.IsInterface ? "I" : "C")} {t.Name}");

// Which record interfaces expose a Model? (Base -> mesh path generality)
Console.WriteLine("\n### Getter interfaces exposing a Model property");
foreach (var t in asms[0].GetTypes().Where(t => t.IsInterface && t.Name.EndsWith("Getter")))
{
    var p = t.GetProperty("Model");
    if (p != null && p.DeclaringType == t) Console.WriteLine($"    {t.Name,-40} Model -> {Pretty(p.PropertyType)}");
}


// Enum shapes that matter for filtering walkable triangles / disabled refs.
void DumpEnum(string n)
{
    foreach (var a in asms)
    {
        var t = a.GetTypes().FirstOrDefault(x => x.Name == n && x.IsEnum);
        if (t == null) continue;
        Console.WriteLine($"### enum {t.FullName} : {Enum.GetUnderlyingType(t).Name}");
        foreach (var v in Enum.GetValues(t))
            Console.WriteLine($"    {Convert.ToInt64(v),12} 0x{Convert.ToInt64(v):X}  {v}");
        Console.WriteLine();
        return;
    }
    Console.WriteLine($"### enum {n} -- NOT FOUND");
}
Console.WriteLine();
foreach (var nested in new[] { "NavmeshTriangle", "Cell", "PlacedObject", "SkyrimMajorRecord" })
{
    var t = asms[0].GetTypes().FirstOrDefault(x => x.Name == nested && !x.IsInterface);
    if (t == null) continue;
    foreach (var e in t.GetNestedTypes().Where(x => x.IsEnum))
    {
        Console.WriteLine($"### enum {nested}.{e.Name} : {Enum.GetUnderlyingType(e).Name}");
        foreach (var v in Enum.GetValues(e))
            Console.WriteLine($"    0x{Convert.ToInt64(v):X8}  {v}");
        Console.WriteLine();
    }
}


Console.WriteLine();
foreach (var n in new[] { "P3Float", "P3Int16", "P2Int", "AssetLinkGetter`1", "IAssetLinkGetter" })
{
    Type t = null;
    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
    { t = a.GetTypes().FirstOrDefault(x => x.Name == n); if (t != null) break; }
    if (t == null) { Console.WriteLine($"### {n} -- NOT FOUND"); continue; }
    Console.WriteLine($"### {t.FullName}  [{t.Assembly.GetName().Name}]");
    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance)) Console.WriteLine($"    F {Pretty(f.FieldType),-40} {f.Name}");
    foreach (var pr in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)) Console.WriteLine($"    P {Pretty(pr.PropertyType),-40} {pr.Name}");
    Console.WriteLine();
}


Console.WriteLine();
foreach (var n in new[] { "IFormLinkNullableGetter`1", "IFormLinkGetter`1",
                          "ISkyrimMajorRecordGetter", "IMajorRecordGetter" })
{
    Type t = null;
    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
    { t = a.GetTypes().FirstOrDefault(x => x.Name == n); if (t != null) break; }
    if (t == null) { Console.WriteLine($"### {n} -- NOT FOUND"); continue; }
    Console.WriteLine($"### {t.FullName}  [{t.Assembly.GetName().Name}]");
    foreach (var pr in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        Console.WriteLine($"    P {Pretty(pr.PropertyType),-46} {pr.Name}");
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(x => !x.IsSpecialName).Take(12))
        Console.WriteLine($"    M {Pretty(m.ReturnType),-46} {m.Name}({string.Join(", ", m.GetParameters().Select(x => Pretty(x.ParameterType)))})");
    foreach (var i in t.GetInterfaces())
        foreach (var pr in i.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Console.WriteLine($"    p[{i.Name}] {Pretty(pr.PropertyType),-38} {pr.Name}");
    Console.WriteLine();
}


Console.WriteLine("### ISkyrimModGetter : Cells / Worldspaces");
{
    var t = asms[0].GetTypes().First(x => x.Name == "ISkyrimModGetter");
    foreach (var pr in t.GetProperties().Where(x => x.Name is "Cells" or "Worldspaces"))
        Console.WriteLine($"    P {Pretty(pr.PropertyType),-56} {pr.Name}");
    var g = asms[0].GetTypes().FirstOrDefault(x => x.Name == "ISkyrimListGroupGetter`1")
         ?? asms[0].GetTypes().FirstOrDefault(x => x.Name == "ISkyrimGroupGetter`1");
    foreach (var n2 in new[]{"ISkyrimListGroupGetter`1","ISkyrimGroupGetter`1","IListGroupGetter`1","IGroupGetter`1"})
    {
        Type gt = null;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { gt = a.GetTypes().FirstOrDefault(x => x.Name == n2); if (gt != null) break; }
        if (gt == null) continue;
        Console.WriteLine($"    ### {gt.FullName}");
        foreach (var pr in gt.GetProperties()) Console.WriteLine($"        P {Pretty(pr.PropertyType),-46} {pr.Name}");
    }
}
