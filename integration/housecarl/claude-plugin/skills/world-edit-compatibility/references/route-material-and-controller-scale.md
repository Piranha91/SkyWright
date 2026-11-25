# Route materials and controller scale

**Class:** LIVING. The probe implementation is in `DetectorModel.RouteCut`, `RouteConnectivity`,
and `ActorBand`; read their current constants rather than copying threshold values into a report.

The route band is measured above the physical floor. Preserve the collision material at triangle
ordinal through transforms, compound shapes, chunk instancing and body filtering. A NIF can contain
several materials: a stairs material elsewhere on the mesh says nothing about the route contact.
Unknown material stays unknown. Mesh filenames and render shader materials do not identify a
Havok collision material.

The stairs family is the set of nif.xml `SkyrimHavokMaterial` names beginning
`SKY_HAV_MAT_STAIRS_`. The current rule grants the exception only when every triangle contacting
the selected path line within the actor band has a stairs material, the cut rise is below the
exclusive physical step-band top, and the float is known and below the float-wall threshold.
It reports a step (`None`). Other materials retain the existing rule; the float clause is unchanged.
The contact-wide check is conservative for mixed surfaces, so a lower non-stairs contact cannot
be cleared by a higher stairs surface.

The material effect was measured on convex collision, with stairs wood and stairs stone. Applying
it to compressed-mesh chunks, other stairs materials, other actors or paces remains a transfer
assumption. A proof of material decoding is not proof of that engine behaviour. Keep that caveat
with any changed kit-piece row. The float rule on stairs is also untested in game.

For actor height, build a per-race, per-sex `ControllerCustom` census from the winning skeletal
model and winning asset bytes. The discriminator is a `bhkSPCollisionObject` that references a
`bhkSimpleShapePhantom` with a resolvable shape. Ordinary ragdoll rigid bodies do not satisfy it.
An unread skeleton, missing path, unknown block or broken controller link yields unknown.
Keep the skeleton path, content hash and reason with the census; regenerate it for the active
order instead of shipping a fixed race-name table. A replacement skeleton can change the fact
without changing the race record.

`ControllerCustom=true` ignores placement scale. `false` applies placement scale at 3D build.
Unknown on a non-unit placement makes the band's result unjudgeable; at unit scale the identity
multiplier needs no classification, and the unknown is still reported. Name the placed actor that
sets the top and why its scale counted or did not count.

This is a scale correction to the existing height model. It does not measure a creature's absolute
controller dimensions, radius or step height, resolve NPC templates, or model runtime resizing
before the 3D is rebuilt.

Format reference: [nif.xml](https://github.com/niftools/nifxml/blob/develop/nif.xml), specifically
`HavokMaterial`, `SkyrimHavokMaterial`, `bhkCMSChunk`, `bhkMeshMaterial`, and the phantom classes.
