using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Assets;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Physics;
using Itera.Modding;

namespace Itera.MeshSample.Systems;

/// <summary>
/// Puts the yard in the match (fixed group, first; it does nothing after): for each of <see cref="Yard.Pieces"/>, an
/// entity that draws its mesh asset where it stands, and the same triangles as a mesh collider, the stone's tagged
/// climbable (<see cref="Yard.TagsOf"/>), each noted in <see cref="YardColliders"/> with its piece. Colliders are added
/// once the match's physics exists, as here at the start.
/// </summary>
public sealed class YardSystem(World world, MeshAssets assets, MeshColliders colliders, YardColliders yard)
    : BaseSystem<World, FixedStepTime>(world),
        IFixedUpdateSystem
{
    public override void Initialize()
    {
        foreach (var piece in Yard.Pieces)
        {
            var index = assets.Find(piece.Asset);
            var placement = MeshPlacement.At(piece.Position, Quaternion.Identity, piece.Scale);
            World.Create(placement, DrawnMesh.Of(index));
            var transform = Matrix4x4.CreateScale(piece.Scale) * Matrix4x4.CreateTranslation(piece.Position);
            yard.Add(colliders.Add(assets[index], in transform, Yard.TagsOf), piece);
        }
    }

    public override void Update(in FixedStepTime step) { }
}
