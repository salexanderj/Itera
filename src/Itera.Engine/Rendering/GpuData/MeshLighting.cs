using System.Numerics;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// The light of a frame's world (<c>mesh-data.glsl</c>'s <c>MeshLighting</c>, one per frame slot, read by
/// <c>mesh.frag</c> and <c>mesh-shadow.vert</c>): the sun's shadow cascades, two and with
/// <c>Engine:Renderer:DistantShadowMetres</c> a third for the distance (each one's view-projection from the world,
/// uploaded unchanged and used as <c>M * v</c>, the view depth where each ends, and each texel's width for the normal
/// offset), where the sky field's cells lie (how much of the sky each cell of air round the camera sees, a 3D
/// texture), and how many point lights the slot's list holds (<see cref="MeshLight"/>). Written by <c>MeshView</c>
/// (the cascades and the lights) and <c>WorldMeshes</c> (the sky field's part).
/// </summary>
public struct MeshLighting
{
    /// <summary>The near cascade's view-projection: x and y in [-1, 1] across its map, z in [0, 1] from the sun.</summary>
    public Matrix4x4 Near;

    /// <summary>The far cascade's view-projection.</summary>
    public Matrix4x4 Far;

    /// <summary>The view depth, in metres, where the near cascade ends and the far one begins.</summary>
    public float NearEnd;

    /// <summary>The view depth where the far cascade ends: farther surfaces take no shadow.</summary>
    public float FarEnd;

    /// <summary>The width of one texel of the near cascade's map, in metres.</summary>
    public float NearTexelMetres;

    /// <summary>The width of one texel of the far cascade's map.</summary>
    public float FarTexelMetres;

    /// <summary>1 when the maps hold this frame's shadows; 0 (shadows off) lights every surface the sun faces.</summary>
    public uint Shadows;

    /// <summary>The low corner of the sky field's cell (0, 0, 0), in world metres.</summary>
    public Vector3 SkyCorner;

    /// <summary>The edge of one of the sky field's cells in metres; 0 when there is no field (all open to the sky).</summary>
    public float SkyCellMetres;

    /// <summary>The point lights in the slot's list.</summary>
    public uint LightCount;

    /// <summary>
    /// The view depth where the near cascade starts to blend into the far one, which covers the band from here to
    /// <see cref="NearEnd"/> too: no line where they meet.
    /// </summary>
    public float NearBlendStart;

    /// <summary>
    /// The view depth where the far cascade's shadows start to fade out, gone at <see cref="FarEnd"/>; with the distant
    /// cascade drawn, where they blend into its shadows instead.
    /// </summary>
    public float FarFadeStart;

    /// <summary>The distant cascade's view-projection, from <see cref="FarFadeStart"/> to <see cref="DistantEnd"/>.</summary>
    public Matrix4x4 Distant;

    /// <summary>The view depth where the distant cascade ends: farther surfaces take no shadow.</summary>
    public float DistantEnd;

    /// <summary>The width of one texel of the distant cascade's map.</summary>
    public float DistantTexelMetres;

    /// <summary>The view depth where the distant cascade's shadows start to fade out, gone at <see cref="DistantEnd"/>.</summary>
    public float DistantFadeStart;

    /// <summary>
    /// How much of the distant cascade's shadow is drawn, 0 to 1, by the sun's height (it fades in as the sun sinks); 0
    /// when it is not drawn this frame, and <c>mesh.frag</c> then does not read its map.
    /// </summary>
    public float DistantStrength;
}

/// <summary>
/// A point light of a frame (<c>mesh-data.glsl</c>'s <c>MeshLight</c>, in the slot's list that <c>mesh.frag</c> loops
/// over): glowing surfaces of the world, gathered per block, and events' lights. Written by <c>MeshView</c>.
/// </summary>
public struct MeshLight
{
    /// <summary>Where the light is, in world metres.</summary>
    public Vector3 Position;

    /// <summary>How strong it is: its glowing area in square metres times its glow.</summary>
    public float Power;

    /// <summary>Its colour, linear.</summary>
    public Vector3 Colour;

    /// <summary>The distance in metres past which it lights nothing.</summary>
    public float Reach;
}

/// <summary>
/// The shadow pass's push constants (<c>mesh-shadow.vert</c>): the frame slot and the cascade drawn, 0 near, 1 far, or 2
/// distant (<c>mesh-asset-shadow.vert</c>'s 0 or 1: the mesh assets cast into no distant cascade).
/// </summary>
public struct MeshShadowConstants
{
    public uint Slot;

    public uint Cascade;
}
