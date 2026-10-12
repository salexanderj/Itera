using System.Numerics;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Physics;
using Itera.Engine.Systems;
using Itera.Engine.Voxels;

namespace Itera.Engine.Objects;

/// <summary>
/// Makes a falling body of each part cut off a voxel object (<see cref="ObjectDamage.Fallen"/>, a
/// <see cref="VoxelEditOperation.RemovePart"/> applied live): a model of the part's voxels for each of its kind's parts it
/// lies in (three levels with densities, as the kind's, by the kind's rule), placed and turned where they lay, made into
/// one cut body (<see cref="LooseBodies.TryCreate(ReadOnlySpan{LoosePart}, float, Vector3, float, out BepuPhysics.BodyHandle)"/>:
/// it touches only the voxels and the objects' statics, is shaded as the world, and goes some seconds after it rests,
/// <c>LooseBodySystem</c>) with its entity (<see cref="LooseBody"/>, <see cref="CutPiece"/>), making room as a thrown
/// ball does. Every game makes its own, from the same edit. Scoped, with voxel terrain; run in the match's fixed step
/// after the fixed group (<see cref="Make"/>). Main thread.
/// </summary>
public sealed class FallenParts(
    ObjectDamage damage,
    VoxelObjects kindModels,
    LooseBodies bodies,
    VoxelModels models,
    EcsWorld ecs,
    PhysicsWorld physics,
    VoxelWorld world
)
{
    /// <summary>The density of a fallen part's voxels, in kilograms a cubic metre: about green wood's.</summary>
    public const float DensityKilogramsPerCubicMetre = 800;

    /// <summary>The fewest solid voxels a fallen part makes a body of; a smaller speck just goes.</summary>
    public const int FewestVoxels = 27;

    private readonly LooseBodyRoom _room = new(bodies);
    private readonly int[] _counts = new int[256];
    private readonly List<LoosePart> _parts = [];
    private bool[] _holds = [];
    private byte[] _materials = [];
    private byte[] _densities = [];
    private byte[] _coarse = [];
    private byte[] _coarseDensities = [];

    /// <summary>The bodies made so far.</summary>
    public int Made { get; private set; }

    /// <summary>Makes the bodies of the parts cut off since the last step.</summary>
    public void Make()
    {
        var fallen = damage.Fallen;
        if (fallen.Count == 0)
        {
            return;
        }

        foreach (var part in fallen)
        {
            MakeBody(in part);
        }

        damage.ReturnFallen();
    }

    private void MakeBody(in FallenPart fallen)
    {
        if (_materials.Length == 0)
        {
            _materials = new byte[VoxelConstants.ChunkVoxels];
            _densities = new byte[VoxelConstants.ChunkVoxels];
            _coarse = new byte[VoxelConstants.ChunkVoxels];
            _coarseDensities = new byte[VoxelConstants.ChunkVoxels];
        }

        var solid = 0;
        foreach (var voxel in fallen.Voxels)
        {
            solid += voxel.Material != VoxelConstants.Air ? 1 : 0;
        }

        if (solid < FewestVoxels)
        {
            return;
        }

        var kind = fallen.Kind;
        var placement = fallen.Placement;
        var turn = ObjectTurns.Rotation(placement.Heading);
        var anchor = world.Origin.ToLocal(placement.Anchor);
        var corners = kindModels.PartsOf(placement.Kind);
        _parts.Clear();
        // The parts the piece holds solid voxels of, each a model owning those; the others' borders only round them.
        var holds = _holds.Length >= kind.Parts.Length ? _holds : _holds = new bool[kind.Parts.Length];
        Array.Clear(holds);
        foreach (var voxel in fallen.Voxels)
        {
            if (voxel.Material != VoxelConstants.Air)
            {
                const int own = ObjectPart.OwnVoxels;
                var at = kind.PartIndexAt(voxel.X / own, voxel.Y / own, voxel.Z / own);
                if (at >= 0)
                {
                    holds[at] = true;
                }
            }
        }

        for (var p = 0; p < kind.Parts.Length; p++)
        {
            if (holds[p] && TryModel(fallen, kind, p, holds, out var model, out var tree))
            {
                var part = kind.Parts[p];
                _parts.Add(
                    new LoosePart(
                        model,
                        tree,
                        part.Size,
                        anchor + Vector3.Transform(corners[p].Corner, turn),
                        turn,
                        Owned: true
                    )
                );
            }
        }

        if (_parts.Count == 0)
        {
            return;
        }

        var parts = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_parts);
        if (
            !_room.MakeRoom(ecs.World, 1, insideQuery: false)
            || !bodies.TryCreate(parts, DensityKilogramsPerCubicMetre, Vector3.Zero, -1, out var body)
        )
        {
            foreach (var part in parts)
            {
                models.RemoveAndFree(part.Model);
            }

            return;
        }

        var pose = physics.Simulation.Bodies[body].Pose;
        bodies.BoxOf(body, out var min, out var max);
        ecs.World.Create(
            new LooseBody { Body = body, Born = bodies.BornOf(body) },
            new BodyPose
            {
                Position = pose.Position,
                PreviousPosition = pose.Position,
                Orientation = pose.Orientation,
                PreviousOrientation = pose.Orientation,
            },
            new BodyBox { Min = min, Max = max },
            new CutPiece()
        );
        Made++;
    }

    /// <summary>
    /// A model of the fallen voxels in part <paramref name="index"/>'s box (border included, as the kind's part's model),
    /// at three levels, owning its own window's faces, and those against a low side where no part of the piece lies
    /// beyond (<paramref name="holds"/>); false when no voxel lies in the box.
    /// </summary>
    private bool TryModel(
        in FallenPart fallen,
        VoxelObjectKind kind,
        int index,
        bool[] holds,
        out int model,
        out ModelTree tree
    )
    {
        model = -1;
        tree = default;
        const int border = ObjectPart.BorderVoxels;
        const int edge = VoxelConstants.ChunkSize;
        var part = kind.Parts[index];
        var own = new VoxelCoord(part.Offset.X - border, part.Offset.Y - border, part.Offset.Z - border);
        var end = new VoxelCoord(own.X + part.Size.X, own.Y + part.Size.Y, own.Z + part.Size.Z);
        var any = false;
        int x0 = int.MaxValue,
            y0 = int.MaxValue,
            z0 = int.MaxValue,
            x1 = int.MinValue,
            y1 = int.MinValue,
            z1 = int.MinValue;
        foreach (var voxel in fallen.Voxels)
        {
            if (
                voxel.X < own.X
                || voxel.Y < own.Y
                || voxel.Z < own.Z
                || voxel.X >= end.X
                || voxel.Y >= end.Y
                || voxel.Z >= end.Z
            )
            {
                continue;
            }

            if (!any)
            {
                Array.Clear(_materials);
                Array.Clear(_densities);
                any = true;
            }

            int x = voxel.X - own.X,
                y = voxel.Y - own.Y,
                z = voxel.Z - own.Z;
            var i = x + edge * (y + edge * z);
            _materials[i] = voxel.Material;
            _densities[i] = voxel.Density;
            (x0, y0, z0) = (Math.Min(x0, x), Math.Min(y0, y), Math.Min(z0, z));
            (x1, y1, z1) = (Math.Max(x1, x), Math.Max(y1, y), Math.Max(z1, z));
        }

        if (!any)
        {
            return false;
        }

        Span<ModelTree> trees = stackalloc ModelTree[VoxelModels.MaxLevels];
        trees[0] = models.Encode(_materials, _densities, part.SizeAt(0));
        var source = new ChunkVoxels(_materials, _densities);
        for (var level = 1; level < VoxelModels.MaxLevels; level++)
        {
            Array.Clear(_coarse);
            Array.Clear(_coarseDensities);
            var step = 1 << level;
            var size = part.SizeAt(level);
            for (var cz = z0 / step; cz <= z1 / step && cz < size.Z; cz++)
            {
                for (var cy = y0 / step; cy <= y1 / step && cy < size.Y; cy++)
                {
                    for (var cx = x0 / step; cx <= x1 / step && cx < size.X; cx++)
                    {
                        ObjectPart.PartLevel.Sample(
                            in source,
                            default,
                            cx,
                            cy,
                            cz,
                            step,
                            _counts,
                            out var material,
                            out var density
                        );
                        var i = cx + edge * (cy + edge * cz);
                        _coarse[i] = material;
                        _coarseDensities[i] = density;
                    }
                }
            }

            trees[level] = models.Encode(_coarse, _coarseDensities, size);
        }

        model = models.Add(trees, part.Size, 0, kind.UnitCentimetres, densities: true);
        // Its own window's faces, and those against a low side whose part beyond holds none of the piece.
        const int ownVoxels = ObjectPart.OwnVoxels;
        int px = part.Offset.X / ownVoxels,
            py = part.Offset.Y / ownVoxels,
            pz = part.Offset.Z / ownVoxels;
        models.SetOwned(
            model,
            new VoxelCoord(border, border, border),
            new VoxelCoord(border + part.OwnSize.X, border + part.OwnSize.Y, border + part.OwnSize.Z),
            new VoxelCoord(
                part.OpenLow.X == 1 || !Holds(kind, holds, px - 1, py, pz) ? 1 : 0,
                part.OpenLow.Y == 1 || !Holds(kind, holds, px, py - 1, pz) ? 1 : 0,
                part.OpenLow.Z == 1 || !Holds(kind, holds, px, py, pz - 1) ? 1 : 0
            )
        );
        tree = trees[0];
        return true;

        static bool Holds(VoxelObjectKind kind, bool[] holds, int x, int y, int z)
        {
            var at = kind.PartIndexAt(x, y, z);
            return at >= 0 && holds[at];
        }
    }
}
