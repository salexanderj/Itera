using System.Numerics;
using Itera.Engine.Meshing;
using Itera.Engine.Rendering.GpuData;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// The world's point lights (docs/rendering-redesign.md, stage 1.5 and phase 3): each block's glowing surfaces (embers,
/// lava, glow caps; a material's <see cref="MeshMaterial.Glow"/>) make one light at their middle, weighted by glow,
/// found when the block's mesh is uploaded, and events make others (<see cref="PointLights"/>: beams, muzzle flashes,
/// blasts). Each frame the blocks the camera's levels draw (in view or not) and the events' lights within
/// <see cref="LightGatherMetres"/> offer theirs, those whose reach meets the view's frustum are kept, and the light
/// budget's brightest at the camera (<c>Engine:Renderer:MostLights</c>, at most <see cref="MeshPass.MostLights"/>) go into
/// the slot's list, brightest first, which the mesh pass sorts into the view's clusters for <c>mesh.frag</c>.
/// </summary>
public sealed unsafe partial class WorldMeshes
{
    /// <summary>Lights farther than this from the camera are not gathered, in metres.</summary>
    private const float LightGatherMetres = 48;

    /// <summary>
    /// A light's power is its glowing area in square metres times its glow, times this: glowing coals light the
    /// floor round them red in a hall (judged by eye in the spike).
    /// </summary>
    private const float LightPowerScale = 400;

    /// <summary>A light reaches where its power over the distance squared falls to this, at most <see cref="MostReachMetres"/>.</summary>
    private const float LightCutoff = 0.02f;

    private const float MostReachMetres = 16;

    /// <summary>Lights offered in one frame at most; more are not gathered.</summary>
    private const int MostOffered = 4096;

    private readonly MeshLight[] _offered = new MeshLight[MostOffered];
    private readonly float[] _offeredRanks = new float[MostOffered];
    private int _offeredCount;

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

    /// <summary>Offers a drawn block's light, if it has one near enough, for the frame's list.</summary>
    private void OfferLight(in MeshBlock block, Vector3 origin, float voxelMetres)
    {
        if (block.LightPower <= 0 || _offeredCount == MostOffered)
        {
            return;
        }

        var power = block.LightPower * voxelMetres * voxelMetres * LightPowerScale;
        Offer(origin + block.LightAt * voxelMetres, block.LightColour, power);
    }

    /// <summary>Offers the events' lights of the frame (<see cref="PointLights"/>), as the blocks offer theirs.</summary>
    private void OfferEventLights(ReadOnlySpan<PointLight> lights)
    {
        _stats.LastEventLights = lights.Length;
        foreach (ref readonly var light in lights)
        {
            if (_offeredCount == MostOffered)
            {
                return;
            }

            Offer(light.Position, light.Colour, light.Power);
        }
    }

    /// <summary>
    /// Offers a light of <paramref name="power"/> at <paramref name="position"/>: kept when it lies within
    /// <see cref="LightGatherMetres"/> and its reach meets the view's frustum.
    /// </summary>
    private void Offer(Vector3 position, Vector3 colour, float power)
    {
        var distanceSquared = Vector3.DistanceSquared(position, _viewPosition);
        if (distanceSquared > LightGatherMetres * LightGatherMetres)
        {
            return;
        }

        var reach = Math.Min(MathF.Sqrt(power / LightCutoff), MostReachMetres);
        if (!InView(position, reach))
        {
            return;
        }

        _offered[_offeredCount] = new MeshLight
        {
            Position = position,
            Power = power,
            Colour = colour,
            Reach = reach,
        };
        // Brightest at the camera first: the sort is ascending.
        _offeredRanks[_offeredCount++] = -power / (distanceSquared + 1);
    }

    /// <summary>
    /// Writes the light budget's brightest lights offered this frame into the slot's list, brightest at the camera first,
    /// and returns how many; each cluster of the view then keeps those that matter most to it (<c>mesh-clusters.comp</c>).
    /// </summary>
    private uint WriteLights(int slot)
    {
        var count = Math.Min(_offeredCount, _lightBudget);
        _stats.LastLights = count;
        Array.Sort(_offeredRanks, _offered, 0, _offeredCount);

        _offered.AsSpan(0, count).CopyTo(_pass.LightsOf(slot));
        _offeredCount = 0;
        return (uint)count;
    }
}
