using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

// Reflection probe over Nifly 1.1.0 — the version houseCARL pins.
//   niflyprobe             round 2: the TRANSFORM surface (a shape's own T/R/S is local to its parent;
//                          a world-space AABB needs the accumulated chain — what does the library offer?)
//   niflyprobe collision   round 3: the HAVOK SHAPE surface — every bhk shape the collision decoder must read
//                          (bhkCompressedMeshShapeData chunks, the simple convex shapes, the transform wrappers,
//                          the rigid body's own transform), grounded before a line of decoder is written.

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
    Console.WriteLine($"### {t.FullName}  : {Pretty(t.BaseType)}");
    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(x => x.Name))
        if (filter == null || p.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            Console.WriteLine($"    P {Pretty(p.PropertyType),-44} {p.Name}");
    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(x => x.Name))
        if (filter == null || f.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            Console.WriteLine($"    F {Pretty(f.FieldType),-44} {f.Name}");
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                       .Where(x => !x.IsSpecialName && x.DeclaringType != typeof(object)).OrderBy(x => x.Name))
        if (filter == null || m.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            Console.WriteLine($"    M {Pretty(m.ReturnType),-44} {m.Name}({string.Join(", ", m.GetParameters().Select(x => Pretty(x.ParameterType) + " " + x.Name))})");
    Console.WriteLine();
}

if (args.Length > 0 && args[0] == "collision")
{
    Console.WriteLine("=== every bhk* / hk* type in the assembly ===");
    foreach (var t in asm.GetTypes().Where(t => t.IsPublic && (t.Name.StartsWith("bhk") || t.Name.StartsWith("hk"))).OrderBy(t => t.Name))
        Console.WriteLine($"    {t.Name,-40} : {Pretty(t.BaseType)}");
    Console.WriteLine();

    Console.WriteLine("=== collision object / body ===");
    Members("bhkNiCollisionObject");
    Members("bhkCollisionObject");
    Members("bhkPCollisionObject");
    Members("bhkSPCollisionObject");
    Members("bhkBlendCollisionObject");
    Members("bhkWorldObject");
    Members("bhkEntity");
    Members("bhkRigidBody");
    Members("bhkRigidBodyT");

    Console.WriteLine("=== shape hierarchy ===");
    Members("bhkShape");
    Members("bhkSphereRepShape");
    Members("bhkConvexShape");
    Members("bhkBoxShape");
    Members("bhkSphereShape");
    Members("bhkCapsuleShape");
    Members("bhkCylinderShape");
    Members("bhkConvexVerticesShape");
    Members("bhkConvexTransformShape");
    Members("bhkTransformShape");
    Members("bhkListShape");
    Members("bhkConvexListShape");
    Members("bhkMultiSphereShape");
    Members("bhkMoppBvTreeShape");
    Members("bhkBvTreeShape");
    Members("bhkNiTriStripsShape");
    Members("bhkPackedNiTriStripsShape");
    Members("hkPackedNiTriStripsData");
    Members("bhkPlaneShape");
    Members("bhkHeightFieldShape");

    Console.WriteLine("=== compressed mesh ===");
    Members("bhkCompressedMeshShape");
    Members("bhkCompressedMeshShapeData");
    Members("bhkCMSDChunk");
    Members("bhkCMSDBigTris");
    Members("bhkCMSDTransform");
    Members("bhkCMSDMaterial");
    Members("bhkCMSChunk");
    Members("bhkCMSBigTri");

    Console.WriteLine("=== rigid-body info, header ===");
    Members("bhkRigidBodyCInfo2010");
    Members("bhkRigidBodyCInfo2014");
    Members("bhkRigidBodyCInfo550_660");
    Members("bhkEntityCInfo");
    Members("bhkWorldObjectCInfo");
    Members("NiHeader");
    Members("NiFileVersion");
    Members("NiVersion");
    Members("NifFile", "Header");
    Members("NifFile", "Version");

    Console.WriteLine("=== structs the decoder touches ===");
    Members("bhkQsTransform");
    Members("UshortVector3");
    Members("NiBound");
    Members("hkAabb");
    Members("HalfVector3");
    Members("hkSubPartData");
    Members("bhkMeshMaterial");
    Members("bhkWeldInfo");
    Members("Vector3");

    Console.WriteLine("=== value types ===");
    Members("hkQuaternion");
    Members("Matrix44");
    Members("Vector4");
    Members("TriangleData");
    Members("OblivionSubShape");
    Members("HavokFilter");
    Members("NiSkinPartition", "Vert");
    return;
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
