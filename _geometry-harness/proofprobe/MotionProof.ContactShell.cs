/// <summary>Fixture access for the contact-shell known answers (ContactEvidenceProof, CS-D cases): the loose-item builders of a box,
/// compressed-mesh plate and convex item with a stated Havok radius, exposed so a second proof can decode them.</summary>
static partial class MotionProof
{
    internal static byte[] CsSupportBox(float halfXY, float halfZ, float radiusU) => SupportBox(halfXY, halfZ, radiusU);
    internal static byte[] CsSupportMeshPlate(float half, float radiusU) => SupportMeshPlate(half, radiusU);
    internal static byte[] CsFreeItemRadius(float radiusU) => FreeItemRadius(radiusU, radiusU);
    internal static byte[] CsFreeItemBoxRadius(float halfU, float radiusU) => FreeItemBoxRadius(halfU, radiusU);
}
