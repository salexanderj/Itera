using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Maths;
using Itera.Engine.Physics;
using Itera.Engine.Systems;
using Itera.Modding;

namespace Itera.MeshSample.Systems;

/// <summary>
/// Finds the yard block the local player aims at (frame group, after the camera's systems): the shared ray from where
/// the player's tools aim (<see cref="ToolAim.Look(World, Itera.Engine.Voxels.VoxelWorld?, Vector3, float, float, bool, Itera.Engine.Objects.ObjectRays?, MeshColliders?)"/>:
/// the eye along the look, towards the screen's middle through the yard's colliders in third person, or the fly
/// camera), within <see cref="ReachMetres"/>, meeting a climbable
/// surface of one of the yard's pieces (<see cref="YardColliders"/>), whose box, larger by <see cref="RimMetres"/> each
/// way, <see cref="AimHighlight"/> then shows; else it hides it. Reads the player's command, mode, position, and size,
/// and the active camera's pose; writes no components.
/// </summary>
public sealed class AimHighlightSystem(
    World world,
    CollisionSources collisions,
    YardColliders yard,
    AimHighlight highlight,
    WorldOrigin origin
) : BaseSystem<World, FrameTime>(world), IFrameUpdateSystem
{
    /// <summary>How far from the eye a block is in reach, in metres.</summary>
    public const float ReachMetres = 1.5f;

    /// <summary>How far the highlight's box stands out from the block's faces, in metres.</summary>
    public const float RimMetres = 0.02f;

    public override void Initialize()
    {
        EcsChecks.RequireAll<PlayerCommand, PlayerMode, CharacterPosition, CharacterSize>(in ToolAim.Players);
        EcsChecks.RequireAll<CameraPose>(in ToolAim.Cameras);
    }

    public override void Update(in FrameTime frame)
    {
        var player = ToolAim.FindPlayer(World);
        if (player.Found)
        {
            var (from, forward) = ToolAim.Look(
                World,
                null,
                player.Eye,
                player.Yaw,
                player.Pitch,
                player.Flying,
                meshes: collisions.Meshes
            );
            if (
                collisions.Raycast(from, forward, ReachMetres, out var hit)
                && (hit.Tags & Yard.Climbable) != 0
                && yard.TryGet(hit.Collider, out var piece)
            )
            {
                // A block is a cube from its place to its place plus its scale.
                highlight.Show(
                    Matrix4x4.CreateScale(piece.Scale + 2 * RimMetres)
                        * Matrix4x4.CreateTranslation(origin.ToLocal(piece.Position) - new Vector3(RimMetres)),
                    piece.Position
                );
                return;
            }
        }

        highlight.Hide();
    }

    /// <summary>Hides the highlight, which outlives the match (the pass is the run's).</summary>
    public override void Dispose()
    {
        highlight.Hide();
        base.Dispose();
    }
}
