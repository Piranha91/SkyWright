using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

// Reflection probe over Nifly 1.1.0 — the version houseCARL pins.
// Round 2: the TRANSFORM surface. A shape's own Translation/Rotation/Scale is LOCAL to its
// parent node; a world-space AABB needs the accumulated chain. Find out what the library
// already offers before hand-rolling a tree walk.

var asm = typeof(NiflySharp.NifFile).Assembly;
Type Find(string n) => asm.GetTypes().FirstOrDefault(t => t.Name == n);

string Pretty(Type t)
{
    if (t == null) return "?";
    if (t.IsByRef) return "ref " + Pretty(t.GetElementType());
    if (!t.IsGenericType) return t.Name;
    var n = t.Name.Substring(0, t.Name.IndexOf('`'));
    return n + "<" + string.Join(",", t.GetGenericArguments().Select(Pretty)) + ">";
}

void Members(string typeName, string filter = null)
{
    var t = Find(typeName);
    if (t == null) { Console.WriteLine($"### {typeName} -- NOT FOUND\n"); return; }
    Console.WriteLine($"### {t.FullName}");
    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(x => x.Name))
        if (filter == null || p.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            Console.WriteLine($"    P {Pretty(p.PropertyType),-44} {p.Name}");
    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(x => x.Name))
        if (filter == null || f.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            Console.WriteLine($"    F {Pretty(f.FieldType),-44} {f.Name}");
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                       .Where(x => !x.IsSpecialName).OrderBy(x => x.Name))
        if (filter == null || m.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            Console.WriteLine($"    M {Pretty(m.ReturnType),-44} {m.Name}({string.Join(", ", m.GetParameters().Select(x => Pretty(x.ParameterType) + " " + x.Name))})");
    Console.WriteLine();
}

Console.WriteLine("=== NifFile: transform / shape / node surface ===");
foreach (var f in new[] { "Transform", "Shape", "Node", "Bound", "Parent", "Root", "Vert", "Tri" })
{
    Console.WriteLine($"--- filter '{f}' ---");
    Members("NifFile", f);
}

Console.WriteLine("=== transform value types ===");
Members("MatTransform");
Members("Matrix33");
Members("Vector3");
Members("Vector4");
Members("BoundingSphere");

Console.WriteLine("=== node / AV object ===");
Members("NiAVObject");
Members("NiNode");

Console.WriteLine("=== collision entry points ===");
Members("bhkCollisionObject");
Members("bhkRigidBody");
Members("bhkRigidBodyT");
Members("bhkMoppBvTreeShape");
Members("bhkCompressedMeshShape");
Members("bhkCompressedMeshShapeData");
Members("bhkConvexVerticesShape");
Members("bhkListShape");
Members("bhkTransformShape");
Members("bhkBoxShape");
Members("hkPackedNiTriStripsData");
Members("bhkPackedNiTriStripsShape");
Members("bhkCMSChunk");
Members("bhkCMSBigTri");

Console.WriteLine("=== NiBlockRefArray ===");
Members("NiBlockRefArray`1");
Members("NiBlockRef`1");
Members("NiRefArray");
