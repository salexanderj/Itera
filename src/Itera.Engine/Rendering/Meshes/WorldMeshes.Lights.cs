using System.Numerics;
using Itera.Engine.Meshing;
using Itera.Engine.Rendering.GpuData;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// The world's point lights (docs/rendering-redesign.md, stage 1.5 and phase 3): each block's glowing surfaces (embers,
/// lava, glow caps; a material's <see cref="MeshMaterial.Glow"/>) make one light at their middle, weighted by glow,
/// found when the block's mesh is uploaded. Each frame the blocks the camera's levels draw (in view or not) offer theirs
/// to the frame's view (<see cref="MeshView.Offer"/>), which keeps and ranks them with the events' lights.
/// </summary>
public sealed unsafe partial class WorldMeshes
{
    /// <summary>
    /// A light's power is its glowing area in square metres times its glow, times this: glowing coals light the
    /// floor round them red in a hall (judged by eye in the spike).
    /// </summary>
    private const float LightPowerScale = 400;

    /// <summary>
    /// The light of a block's mesh: the glowing vertices' middle and colour, weighted by glow, and their glow summed (each
    /// vertex stands for about one voxel face of surface).
    /// </summary>
    private void FindLight(MeshBuffers mesh, ref MeshBlock block)
    {
        var at = Vector3.Zero;
        var colour = Vector3.Zero;
        var power = 0f;
        foreach (ref readonly var vertex in mesh.Vertices.AsSpan(0, mesh.VertexCount))
        {
            ref readonly var material = ref _materials[MeshVertex.DecodeMaterial(in vertex)];
            if (material.Glow <= 0)
            {
                continue;
            }

            at += MeshVertex.DecodePosition(in vertex) * material.Glow;
            colour += material.Colour * material.Glow;
            power += material.Glow;
        }

        block.LightPower = power;
        if (power > 0)
        {
            block.LightAt = at / power;
            block.LightColour = colour / power;
        }
    }

    /// <summary>Offers a drawn block's light, if it has one, to the frame's view.</summary>
    private void OfferLight(in MeshBlock block, Vector3 origin, float voxelMetres)
    {
        if (block.LightPower <= 0)
        {
            return;
        }

        var power = block.LightPower * voxelMetres * voxelMetres * LightPowerScale;
        _view.Offer(origin + block.LightAt * voxelMetres, block.LightColour, power);
    }
}
