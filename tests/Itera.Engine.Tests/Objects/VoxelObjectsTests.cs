using System.Numerics;
using Itera.Engine.Objects;
using Itera.Engine.Rendering;
using Itera.Engine.Tests.Systems;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Objects;

/// <summary>
/// A match's object kinds as models (<see cref="VoxelObjects"/>): each part a model of its kind's voxels with three
/// levels that keep their densities, its corner placed from the kind's anchor; the kinds sealed for the world; and the
/// loading task done once they are made (with no GPU to wait for).
/// </summary>
public sealed class VoxelObjectsTests : IDisposable
{
    private const byte Bark = 30;
    private const byte Leaves = 31;
    private readonly VoxelWorld _world;
    private readonly VoxelModels _models;

    public VoxelObjectsTests()
    {
        _world = TestWorlds.Load(new FlatTestGenerator(new VoxelGrid(10)), Vector3.Zero, radiusMetres: 3);
        _models = new VoxelModels(_world);
    }

    public void Dispose()
    {
        _models.Dispose();
        _world.Dispose();
    }

    /// <summary>
    /// A tree of 5 cm voxels 300 voxels tall (15 m) is three parts, each a model of 5 cm voxels whose three trees hold the
    /// part's levels with its border, materials and densities, and whose corner lies where the part's border does from
    /// the trunk's foot.
    /// </summary>
    [Fact]
    public void Each_part_is_a_model_of_its_kinds_voxels_with_densities_at_three_levels()
    {
        var shape = new VoxelObjectShape(5, new VoxelCoord(60, 300, 60), new VoxelCoord(150, 0, 150));
        shape.FillCapsule(150, 0, 150, 150, 1200, 150, 25, Bark);
        shape.FillEllipsoid(150, 1300, 150, 140, 190, 140, Leaves);
        var kinds = new VoxelObjectKinds();
        var id = kinds.Register("tree", shape);
        var objects = new VoxelObjects(kinds, _world, _models, NullLogger<VoxelObjects>.Instance);

        objects.Initialize();

        Assert.True(kinds.IsSealed);
        var kind = kinds[id];
        var parts = objects.PartsOf(id);
        Assert.Equal(3, parts.Length);
        var materials = new byte[VoxelConstants.ChunkVoxels];
        var densities = new byte[VoxelConstants.ChunkVoxels];
        var expectedMaterials = new byte[VoxelConstants.ChunkVoxels];
        var expectedDensities = new byte[VoxelConstants.ChunkVoxels];
        for (var p = 0; p < parts.Length; p++)
        {
            var part = kind.Parts[p];
            var model = parts[p].Model;
            Assert.Equal(5, _models.UnitCentimetres(model));
            Assert.True(_models.HasDensities(model));
            Assert.Equal(VoxelModels.MaxLevels, _models.LevelCount(model));
            Assert.Equal(part.Size, _models.Size(model));
            Assert.Equal(0.20f, _models.VoxelMetres(model, 2), 6);
            // Its box starts 8 voxels (40 cm) before the voxels it owns, its border.
            var corner = new Vector3(-1.9f, (part.Offset.Y - 8) * 0.05f, -1.9f);
            Assert.True(_models.IsPart(model));
            Assert.True(Vector3.Distance(corner, parts[p].Corner) < 1e-5f, $"Part {p} is at {parts[p].Corner}.");
            for (var level = 0; level < VoxelModels.MaxLevels; level++)
            {
                var size = part.SizeAt(level);
                var tree = new ModelTree(_models.Tree(model, level));
                Array.Clear(materials);
                Array.Clear(densities);
                _models.Decode(tree, materials, size);
                _models.DecodeDensities(tree, densities, size);
                part.Decode(level, expectedMaterials, expectedDensities);
                for (var z = 0; z < size.Z; z++)
                {
                    for (var y = 0; y < size.Y; y++)
                    {
                        for (var x = 0; x < size.X; x++)
                        {
                            var i = x + VoxelConstants.ChunkSize * (y + VoxelConstants.ChunkSize * z);
                            Assert.Equal(expectedMaterials[i], materials[i]);
                            Assert.Equal(expectedDensities[i], densities[i]);
                        }
                    }
                }
            }
        }
    }

    /// <summary>A kind whose voxels are as coarse as the world's stops the match's start, naming it.</summary>
    [Fact]
    public void A_kind_not_finer_than_the_world_stops_the_start()
    {
        var shape = new VoxelObjectShape(10, new VoxelCoord(8, 8, 8), new VoxelCoord(40, 0, 40));
        shape.FillBox(0, 0, 0, 80, 80, 80, Bark);
        var kinds = new VoxelObjectKinds();
        kinds.Register("crate", shape);

        var error = Assert.Throws<InvalidOperationException>(
            new VoxelObjects(kinds, _world, _models, NullLogger<VoxelObjects>.Instance).Initialize
        );
        Assert.Contains("crate", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The loading task waits for the models to be made; with no GPU to draw them it is done then, and with no kinds too.
    /// </summary>
    [Fact]
    public void The_loading_task_is_done_once_the_models_are_made_where_no_gpu_waits()
    {
        var shape = new VoxelObjectShape(5, new VoxelCoord(10, 10, 10), new VoxelCoord(25, 0, 25));
        shape.FillEllipsoid(25, 25, 25, 20, 20, 20, Leaves);
        var kinds = new VoxelObjectKinds();
        kinds.Register("bush", shape);
        var objects = new VoxelObjects(kinds, _world, _models, NullLogger<VoxelObjects>.Instance);
        var task = new ObjectsLoadingTask(objects, new NoGpuWorld());

        Assert.False(task.Progress.Done);
        objects.Initialize();
        Assert.True(task.Progress.Done);
        Assert.False(objects.AllUploaded());

        var none = new VoxelObjects(new VoxelObjectKinds(), _world, _models, NullLogger<VoxelObjects>.Instance);
        none.Initialize();
        Assert.True(none.AllUploaded());
    }
}
