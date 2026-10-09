using System.Numerics;
using Itera.Engine.Loading;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Loading;

/// <summary>
/// <see cref="ModelsLoadingTask"/>: waits until every listed model is uploaded, reports the share uploaded, is done for
/// an empty list, reads a list that fills later, and allocates nothing when read.
/// </summary>
public sealed class ModelsLoadingTaskTests : IDisposable
{
    private readonly VoxelWorld _world = TestWorlds.Load(new SlopeGenerator(), new Vector3(0, 2.5f, 0));
    private readonly VoxelModels _models;

    public ModelsLoadingTaskTests()
    {
        _models = new VoxelModels(_world);
    }

    public void Dispose() => _world.Dispose();

    [Fact]
    public void It_is_done_when_every_model_is_uploaded()
    {
        var first = AddModel();
        var second = AddModel();
        var task = new ModelsLoadingTask("Models", _models, [first, second]);

        Assert.Equal("Models", task.Name);
        Assert.False(task.Progress.Done);
        Assert.Equal(0, task.Progress.Fraction);

        _models.SetUploaded(first);
        Assert.False(task.Progress.Done);
        Assert.Equal(0.5f, task.Progress.Fraction);

        _models.SetUploaded(second);
        Assert.Equal(LoadingProgress.Finished, task.Progress);
    }

    [Fact]
    public void An_empty_list_is_done()
    {
        var task = new ModelsLoadingTask("Models", _models, Array.Empty<int>());

        Assert.Equal(LoadingProgress.Finished, task.Progress);
    }

    [Fact]
    public void It_reads_its_list_at_each_check()
    {
        var handles = new List<int>();
        var task = new ModelsLoadingTask("Models", _models, () => handles);
        Assert.True(task.Progress.Done);

        handles.Add(AddModel());
        Assert.False(task.Progress.Done);

        _models.SetUploaded(handles[0]);
        Assert.True(task.Progress.Done);
    }

    [Fact]
    public void Reading_it_allocates_nothing()
    {
        var first = AddModel();
        var second = AddModel();
        var task = new ModelsLoadingTask("Models", _models, new[] { first, second });
        _ = task.Progress;
        var before = SystemAllocation.StartMeasurement();
        var done = 0;
        for (var i = 0; i < 100; i++)
        {
            done += task.Progress.Done ? 1 : 0;
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(0, done);
    }

    private int AddModel()
    {
        var voxels = new byte[VoxelConstants.ChunkVoxels];
        voxels[0] = 1;
        return _models.Add([_models.Encode(voxels)], new VoxelCoord(1, 1, 1));
    }
}
