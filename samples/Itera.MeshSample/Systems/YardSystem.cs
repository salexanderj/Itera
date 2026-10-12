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
/// entity that draws its mesh asset where it stands, and but for a piece only drawn the same triangles as a mesh
/// collider of its physics material and layer (by name, the run's: <see cref="PhysicsDemo.Register"/>), the stone's
/// tagged climbable (<see cref="Yard.TagsOf"/>), each noted in <see cref="YardColliders"/> with its piece. Colliders are
/// added once the match's physics exists, as here at the start.
/// </summary>
public sealed class YardSystem(
    World world,
    MeshAssets assets,
    MeshColliders colliders,
    YardColliders yard,
    PhysicsMaterials materials,
    PhysicsLayers layers
) : BaseSystem<World, FixedStepTime>(world), IFixedUpdateSystem
{
    public override void Initialize()
    {
        foreach (var piece in Yard.Pieces)
        {
            var index = assets.Find(piece.Asset);
            // Drawn local to the match's origin; its collider placed from its absolute point.
            var placement = MeshPlacement.At(
                colliders.Origin.ToLocal(piece.Position),
                Quaternion.Identity,
                piece.Scale
            );
            World.Create(placement, DrawnMesh.Of(index));
            if (!piece.Collides)
            {
                continue;
            }

            var scale = Matrix4x4.CreateScale(piece.Scale);
            var settings = new PhysicsBodySettings
            {
                Material = piece.Material is { } material ? Material(material) : PhysicsMaterialId.Default,
                Layer = piece.Layer is { } layer ? Layer(layer) : PhysicsLayerId.Default,
            };
            yard.Add(colliders.Add(assets[index], piece.Position, in scale, Yard.TagsOf, settings), piece);
        }
    }

    private PhysicsMaterialId Material(string name) =>
        materials.TryFind(name, out var id)
            ? id
            : throw new InvalidOperationException($"No physics material is named {name}.");

    private PhysicsLayerId Layer(string name) =>
        layers.TryFind(name, out var id)
            ? id
            : throw new InvalidOperationException($"No physics layer is named {name}.");

    public override void Update(in FixedStepTime step) { }
}
