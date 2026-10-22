using System.Numerics;

namespace Itera.Engine.Assets;

/// <summary>
/// A named place on a mesh asset, such as a muzzle or a grip: where a held item, an effect, or a shot starts. The import
/// reads one from each leaf node of the file that holds nothing (an empty in Blender): no mesh, camera, or light, not a
/// joint, and a name. A point under a joint of the rig is in that joint's space (<see cref="Joint"/>), so it gives a
/// <c>MeshAttachment</c>'s offset and turn as it is. Any other point is in the asset's space, its joint -1. In metres.
/// </summary>
public readonly record struct MeshPoint(string Name, int Joint, Vector3 Position, Quaternion Rotation);
