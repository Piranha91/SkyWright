using System.Runtime.CompilerServices;

// The synthetic proof (proofprobe) pins NifGeometry.WorldTransform's contract (the root node's own transform left out,
// a turn below the root kept) on a hand-built node chain (Q28, 2026-09-05).
[assembly: InternalsVisibleTo("proofprobe")]

// The engine's tests reach internal helpers such as ConvexHull.
[assembly: InternalsVisibleTo("SkyWright.Engine.Tests")]
